using System.Text.Json;

namespace BelowZeroCoop.Server;

/// <summary>
/// Standalone authoritative inventory/quickslot metadata model.
/// Regression guard for upstream #78. Not connected to Unity or native saves.
/// </summary>
internal sealed class EquipmentState
{
    public const int SchemaVersion = 2;
    public const int QuickslotCount = 5;
    private const int MaxOwnedItems = 128;
    private const int MaxPlayers = 16;
    private const int MaxReceipts = 2048;

    private readonly object gate = new();
    private readonly Dictionary<uint, EquipmentProfile> profiles = new();
    private readonly Dictionary<Guid, EquipmentReceipt> receipts = new();
    private readonly Queue<Guid> receiptOrder = new();

    /// <summary>
    /// verifiedPlayerId must be bound to an authenticated network session by
    /// the future game adapter. Never trust a caller-supplied player ID alone.
    /// </summary>
    public EquipmentResult Apply(EquipmentCommand command, uint verifiedPlayerId)
    {
        lock (gate)
        {
            if (command == null || command.OperationId == Guid.Empty ||
                verifiedPlayerId == 0 || command.PlayerId != verifiedPlayerId ||
                command.ItemId == Guid.Empty ||
                !Enum.IsDefined(typeof(EquipmentAction), command.Action))
                return EquipmentResult.Reject("invalid_command");

            string signature = JsonSerializer.Serialize(command);
            if (receipts.TryGetValue(command.OperationId, out EquipmentReceipt? receipt))
            {
                return receipt.Signature == signature
                    ? new EquipmentResult(true, true, "duplicate", receipt.Revision)
                    : EquipmentResult.Reject("operation_id_collision");
            }

            if (!profiles.TryGetValue(verifiedPlayerId, out EquipmentProfile? profile))
            {
                if (command.ExpectedRevision != 0 || profiles.Count >= MaxPlayers)
                    return EquipmentResult.Reject("unknown_player");
                profile = new EquipmentProfile
                {
                    PlayerId = verifiedPlayerId,
                    Revision = 0,
                    OwnedItems = new List<Guid>(),
                    Slots = new Guid?[QuickslotCount]
                };
            }

            if (command.ExpectedRevision != profile.Revision)
                return EquipmentResult.Reject("revision_conflict");

            bool isSlotAction = command.Action is EquipmentAction.BindSlot or EquipmentAction.ClearSlot;
            if (isSlotAction && (command.Slot < 0 || command.Slot >= QuickslotCount))
                return EquipmentResult.Reject("invalid_slot");
            if (!isSlotAction && command.Slot != -1)
                return EquipmentResult.Reject("unexpected_slot");

            // Mutate a detached copy and publish once. Rejected commands have
            // no partial side effects, even when they alter multiple slots.
            EquipmentProfile next = profile.Copy();
            switch (command.Action)
            {
                case EquipmentAction.AddItem:
                    if (next.OwnedItems.Contains(command.ItemId) ||
                        next.OwnedItems.Count >= MaxOwnedItems)
                        return EquipmentResult.Reject("invalid_inventory_add");
                    next.OwnedItems.Add(command.ItemId);
                    break;

                case EquipmentAction.RemoveItem:
                    if (!next.OwnedItems.Remove(command.ItemId))
                        return EquipmentResult.Reject("missing_item");
                    for (int i = 0; i < QuickslotCount; ++i)
                    {
                        if (next.Slots[i] == command.ItemId)
                            next.Slots[i] = null;
                    }
                    break;

                case EquipmentAction.BindSlot:
                    if (!next.OwnedItems.Contains(command.ItemId))
                        return EquipmentResult.Reject("slot_item_not_owned");
                    // One physical item instance cannot inhabit two slots.
                    for (int i = 0; i < QuickslotCount; ++i)
                    {
                        if (next.Slots[i] == command.ItemId)
                            next.Slots[i] = null;
                    }
                    next.Slots[command.Slot] = command.ItemId;
                    break;

                case EquipmentAction.ClearSlot:
                    if (next.Slots[command.Slot] != command.ItemId)
                        return EquipmentResult.Reject("slot_mismatch");
                    next.Slots[command.Slot] = null;
                    break;

                default:
                    return EquipmentResult.Reject("invalid_action");
            }

            next.Revision++;
            profiles[verifiedPlayerId] = next;
            receipts.Add(command.OperationId,
                new EquipmentReceipt(command.OperationId, signature, next.Revision));
            receiptOrder.Enqueue(command.OperationId);
            while (receipts.Count > MaxReceipts)
                receipts.Remove(receiptOrder.Dequeue());

            return new EquipmentResult(true, false, "ok", next.Revision);
        }
    }

    public EquipmentProfile? SnapshotPlayer(uint id)
    {
        lock (gate)
            return profiles.TryGetValue(id, out EquipmentProfile? profile)
                ? profile.Copy() : null;
    }

    public EquipmentDocument Snapshot()
    {
        lock (gate)
        {
            return new EquipmentDocument
            {
                Version = SchemaVersion,
                Players = profiles.Values.OrderBy(x => x.PlayerId).Select(x => x.Copy()).ToList(),
                Receipts = receiptOrder.Select(id => receipts[id]).ToList()
            };
        }
    }

    public static EquipmentState Restore(EquipmentDocument document)
    {
        if (document == null || document.Version != SchemaVersion ||
            document.Players == null || document.Players.Count > MaxPlayers ||
            document.Receipts == null || document.Receipts.Count > MaxReceipts)
            throw new InvalidDataException("Unsupported or invalid equipment schema.");

        var state = new EquipmentState();
        foreach (EquipmentProfile profile in document.Players)
        {
            ValidateProfile(profile);
            if (!state.profiles.TryAdd(profile.PlayerId, profile.Copy()))
                throw new InvalidDataException("Duplicate equipment player ID.");
        }

        foreach (EquipmentReceipt receipt in document.Receipts)
        {
            if (receipt == null || receipt.OperationId == Guid.Empty ||
                string.IsNullOrWhiteSpace(receipt.Signature) ||
                receipt.Signature.Length > 1024 || receipt.Revision <= 0 ||
                !state.receipts.TryAdd(receipt.OperationId, receipt))
                throw new InvalidDataException("Invalid equipment operation receipt.");
            state.receiptOrder.Enqueue(receipt.OperationId);
        }

        return state;
    }

    /// <summary>
    /// Explicit one-way migration for an earlier *BelowZeroCoop* prototype
    /// layout. This does not claim support for BOT Benson or game save formats.
    /// Bad references cause a hard error; they are never silently discarded.
    /// </summary>
    public static EquipmentState MigrateLegacy(EquipmentLegacyDocument legacy)
    {
        if (legacy == null || legacy.Version != 1 || legacy.Players == null ||
            legacy.Players.Count > MaxPlayers)
            throw new InvalidDataException("Unknown legacy equipment format.");

        var migrated = new EquipmentDocument { Version = SchemaVersion };
        foreach (LegacyEquipmentProfile player in legacy.Players)
        {
            if (player == null || player.Revision < 0 ||
                player.Quickslots == null ||
                player.Quickslots.Length != QuickslotCount || player.OwnedItems == null)
                throw new InvalidDataException("Invalid legacy quickslot array.");

            migrated.Players.Add(new EquipmentProfile
            {
                PlayerId = player.PlayerId,
                Revision = player.Revision,
                OwnedItems = player.OwnedItems.ToList(),
                Slots = player.Quickslots.ToArray()
            });
        }
        return Restore(migrated);
    }

    private static void ValidateProfile(EquipmentProfile profile)
    {
        if (profile == null || profile.PlayerId == 0 || profile.Revision < 0 ||
            profile.Slots == null || profile.Slots.Length != QuickslotCount ||
            profile.OwnedItems == null || profile.OwnedItems.Count > MaxOwnedItems ||
            profile.OwnedItems.Any(id => id == Guid.Empty) ||
            profile.OwnedItems.Distinct().Count() != profile.OwnedItems.Count)
            throw new InvalidDataException("Invalid player equipment profile.");

        var bound = new HashSet<Guid>();
        foreach (Guid? item in profile.Slots)
        {
            if (!item.HasValue)
                continue;
            if (!profile.OwnedItems.Contains(item.Value) || !bound.Add(item.Value))
                throw new InvalidDataException(
                    "Quickslot references an unavailable or duplicated item instance.");
        }
    }
}

internal enum EquipmentAction { AddItem = 1, RemoveItem = 2, BindSlot = 3, ClearSlot = 4 }

internal sealed record EquipmentCommand(Guid OperationId, uint PlayerId,
    long ExpectedRevision, EquipmentAction Action, Guid ItemId, int Slot = -1);

internal sealed record EquipmentResult(bool Applied, bool Duplicate, string Reason, long Revision)
{
    internal static EquipmentResult Reject(string message) => new(false, false, message, 0);
}

internal sealed class EquipmentProfile
{
    public uint PlayerId { get; set; }
    public long Revision { get; set; }
    public List<Guid> OwnedItems { get; set; } = new();
    public Guid?[] Slots { get; set; } = new Guid?[EquipmentState.QuickslotCount];

    public EquipmentProfile Copy() => new()
    {
        PlayerId = PlayerId,
        Revision = Revision,
        OwnedItems = OwnedItems.ToList(),
        Slots = Slots.ToArray()
    };
}

internal sealed class EquipmentDocument
{
    public int Version { get; set; } = EquipmentState.SchemaVersion;
    public List<EquipmentProfile> Players { get; set; } = new();
    public List<EquipmentReceipt> Receipts { get; set; } = new();
}

internal sealed record EquipmentReceipt(Guid OperationId, string Signature, long Revision);

internal sealed class EquipmentLegacyDocument
{
    public int Version { get; set; } = 1;
    public List<LegacyEquipmentProfile> Players { get; set; } = new();
}

internal sealed class LegacyEquipmentProfile
{
    public uint PlayerId { get; set; }
    public long Revision { get; set; }
    public List<Guid> OwnedItems { get; set; } = new();
    public Guid?[] Quickslots { get; set; } = new Guid?[EquipmentState.QuickslotCount];
}
