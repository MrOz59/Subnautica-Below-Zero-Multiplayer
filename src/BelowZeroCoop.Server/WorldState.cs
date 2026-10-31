using System.Text.Json;

namespace BelowZeroCoop.Server;

/// <summary>
/// Development-only authoritative state model. It deliberately does not modify
/// Subnautica saves or accept world-changing UDP commands without authentication.
/// Historical regressions: upstream issues #77, #82 and #85.
/// </summary>
internal sealed class WorldState
{
    public const int SchemaVersion = 1;
    private const int MaxEntities = 20000;
    private const int MaxReceipts = 4096;
    private const int MaxItemQuantity = 100000;
    private static readonly TimeSpan MaxLeaseDuration = TimeSpan.FromSeconds(60);

    private readonly object gate = new();
    private readonly Dictionary<Guid, WorldEntity> entities = new();
    private readonly Dictionary<Guid, OperationReceipt> receipts = new();
    private readonly Queue<Guid> receiptOrder = new();
    // Volatile ownership must NEVER be restored from disk.
    private readonly Dictionary<Guid, WorldLease> leases = new();
    private long revision;

    public WorldResult Apply(WorldCommand command, DateTime now)
    {
        lock (gate)
        {
            if (!Valid(command))
                return WorldResult.Rejected("invalid_command", revision);

            string signature = JsonSerializer.Serialize(command);
            if (receipts.TryGetValue(command.CommandId, out OperationReceipt? previous))
            {
                return previous.Signature == signature
                    ? new WorldResult(true, true, "duplicate", previous.Revision)
                    : WorldResult.Rejected("operation_id_collision", revision);
            }

            if (!entities.TryGetValue(command.EntityId, out WorldEntity? current))
            {
                if (command.Action != WorldAction.Build ||
                    command.Generation != 1 || command.ExpectedRevision != 0)
                    return WorldResult.Rejected("missing_entity", revision);

                if (entities.Count >= MaxEntities)
                    return WorldResult.Rejected("entity_capacity", revision);

                current = new WorldEntity
                {
                    Id = command.EntityId,
                    Generation = 1,
                    Kind = command.Kind!,
                    Deleted = false,
                    Revision = ++revision,
                    Items = new Dictionary<string, int>(StringComparer.Ordinal)
                };
                entities.Add(command.EntityId, current);
            }
            else if (command.Action == WorldAction.Build)
            {
                // A rebuild MUST refer to the exact tombstone and advance its
                // generation. Its inventory is always empty: upstream #82.
                if (!current.Deleted || current.Generation == int.MaxValue ||
                    command.Generation != current.Generation + 1 ||
                    command.ExpectedRevision != current.Revision)
                    return WorldResult.Rejected("generation_conflict", revision);

                current.Generation++;
                current.Revision = ++revision;
                current.Deleted = false;
                current.Kind = command.Kind!;
                current.Items.Clear();
                leases.Remove(current.Id);
            }
            else
            {
                if (current.Deleted || command.Generation != current.Generation ||
                    command.ExpectedRevision != current.Revision)
                    return WorldResult.Rejected("revision_conflict", revision);

                ExpireLeaseFor(current.Id, now);
                if (!leases.TryGetValue(current.Id, out WorldLease? lease) ||
                    lease.ActorId != command.ActorId)
                    return WorldResult.Rejected("lease_required", revision);

                if (command.Action == WorldAction.ChangeItems)
                {
                    int oldCount = current.Items.TryGetValue(command.Item!, out int present)
                        ? present : 0;
                    long newCount = (long)oldCount + command.QuantityDelta;
                    if (newCount < 0 || newCount > MaxItemQuantity)
                        return WorldResult.Rejected("invalid_quantity", revision);
                    if (newCount > 0 && oldCount == 0 && current.Items.Count >= 128)
                        return WorldResult.Rejected("inventory_capacity", revision);

                    if (newCount == 0)
                        current.Items.Remove(command.Item!);
                    else
                        current.Items[command.Item!] = (int)newCount;
                }
                else if (command.Action == WorldAction.Destroy)
                {
                    current.Deleted = true;
                    current.Items.Clear();
                    leases.Remove(current.Id);
                }
                else
                {
                    return WorldResult.Rejected("invalid_action", revision);
                }

                current.Revision = ++revision;
            }

            var receipt = new OperationReceipt(command.CommandId, signature, current.Revision);
            receipts.Add(command.CommandId, receipt);
            receiptOrder.Enqueue(command.CommandId);
            while (receipts.Count > MaxReceipts)
                receipts.Remove(receiptOrder.Dequeue());

            return new WorldResult(true, false, "ok", current.Revision);
        }
    }

    public bool TryAcquireLease(Guid entityId, uint actorId, DateTime now, TimeSpan duration)
    {
        if (entityId == Guid.Empty || actorId == 0 ||
            duration <= TimeSpan.Zero || duration > MaxLeaseDuration)
            return false;

        lock (gate)
        {
            if (!entities.TryGetValue(entityId, out WorldEntity? entity) || entity.Deleted)
                return false;

            ExpireLeaseFor(entityId, now);
            if (leases.TryGetValue(entityId, out WorldLease? existing) &&
                existing.ActorId != actorId)
                return false;

            leases[entityId] = new WorldLease(actorId, now + duration);
            return true;
        }
    }

    public int ReleaseActor(uint actorId)
    {
        lock (gate)
        {
            Guid[] owned = leases.Where(item => item.Value.ActorId == actorId)
                                 .Select(item => item.Key).ToArray();
            foreach (Guid entityId in owned)
                leases.Remove(entityId);
            return owned.Length;
        }
    }

    public int ExpireLeases(DateTime now)
    {
        lock (gate)
        {
            Guid[] expired = leases.Where(item => item.Value.ExpiresUtc <= now)
                                   .Select(item => item.Key).ToArray();
            foreach (Guid id in expired)
                leases.Remove(id);
            return expired.Length;
        }
    }

    private void ExpireLeaseFor(Guid id, DateTime now)
    {
        if (leases.TryGetValue(id, out WorldLease? lease) && lease.ExpiresUtc <= now)
            leases.Remove(id);
    }

    public WorldDocument Snapshot()
    {
        lock (gate)
        {
            return new WorldDocument
            {
                Version = SchemaVersion,
                Revision = revision,
                Entities = entities.Values.OrderBy(x => x.Id).Select(x => x.Copy()).ToList(),
                Receipts = receiptOrder.Select(id => receipts[id]).ToList()
            };
        }
    }

    public static WorldState Restore(WorldDocument document)
    {
        if (document == null || document.Version != SchemaVersion ||
            document.Revision < 0 || document.Entities == null ||
            document.Receipts == null || document.Entities.Count > MaxEntities ||
            document.Receipts.Count > MaxReceipts)
            throw new InvalidDataException("Unsupported or invalid world snapshot.");

        var restored = new WorldState { revision = document.Revision };
        foreach (WorldEntity entity in document.Entities)
        {
            if (entity == null || entity.Id == Guid.Empty ||
                entity.Generation < 1 || entity.Revision <= 0 ||
                entity.Revision > document.Revision ||
                !ValidKey(entity.Kind) ||
                entity.Items == null || entity.Items.Count > 128 ||
                entity.Items.Any(pair => !ValidKey(pair.Key) ||
                    pair.Value <= 0 || pair.Value > MaxItemQuantity) ||
                (entity.Deleted && entity.Items.Count != 0) ||
                !restored.entities.TryAdd(entity.Id, entity.Copy()))
                throw new InvalidDataException("Snapshot has invalid or duplicated entity.");
        }

        foreach (OperationReceipt receipt in document.Receipts)
        {
            if (receipt == null || receipt.CommandId == Guid.Empty ||
                string.IsNullOrEmpty(receipt.Signature) ||
                receipt.Signature.Length > 1024 ||
                receipt.Revision <= 0 || receipt.Revision > document.Revision ||
                !restored.receipts.TryAdd(receipt.CommandId, receipt))
                throw new InvalidDataException("Snapshot has invalid operation receipt.");
            restored.receiptOrder.Enqueue(receipt.CommandId);
        }

        // Critically, actor leases are not serialized or reinstated after crash.
        return restored;
    }

    private static bool Valid(WorldCommand command)
    {
        if (command == null || command.CommandId == Guid.Empty ||
            command.ActorId == 0 || command.EntityId == Guid.Empty ||
            command.Generation < 1 || command.ExpectedRevision < 0)
            return false;

        if (command.Action == WorldAction.Build)
            return !string.IsNullOrWhiteSpace(command.Kind) &&
                   command.Kind!.Length <= 64 && ValidKey(command.Kind) &&
                   command.Item == null && command.QuantityDelta == 0;

        if (command.Action == WorldAction.ChangeItems)
            return command.Kind == null && ValidKey(command.Item) &&
                   command.QuantityDelta != 0 &&
                   Math.Abs((long)command.QuantityDelta) <= MaxItemQuantity;

        return command.Action == WorldAction.Destroy && command.Kind == null &&
               command.Item == null && command.QuantityDelta == 0;
    }

    private static bool ValidKey(string? key)
    {
        return !string.IsNullOrWhiteSpace(key) && key.Length <= 64 &&
               key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' ||
                            c == '-' || c == '.');
    }

    private sealed record WorldLease(uint ActorId, DateTime ExpiresUtc);
}

internal enum WorldAction { Build = 1, ChangeItems = 2, Destroy = 3 }

internal sealed record WorldCommand(
    Guid CommandId, uint ActorId, Guid EntityId, int Generation,
    long ExpectedRevision, WorldAction Action, string? Kind = null,
    string? Item = null, int QuantityDelta = 0);

internal sealed record WorldResult(bool Applied, bool Duplicate, string Reason, long Revision)
{
    public static WorldResult Rejected(string reason, long revision)
        => new(false, false, reason, revision);
}

internal sealed class WorldEntity
{
    public Guid Id { get; set; }
    public int Generation { get; set; }
    public long Revision { get; set; }
    public string Kind { get; set; } = "";
    public bool Deleted { get; set; }
    public Dictionary<string, int> Items { get; set; } = new(StringComparer.Ordinal);

    public WorldEntity Copy()
    {
        return new WorldEntity
        {
            Id = Id,
            Generation = Generation,
            Revision = Revision,
            Kind = Kind,
            Deleted = Deleted,
            Items = new Dictionary<string, int>(Items, StringComparer.Ordinal)
        };
    }
}

internal sealed class WorldDocument
{
    public int Version { get; set; } = WorldState.SchemaVersion;
    public long Revision { get; set; }
    public List<WorldEntity> Entities { get; set; } = new();
    public List<OperationReceipt> Receipts { get; set; } = new();
}

internal sealed record OperationReceipt(Guid CommandId, string Signature, long Revision);
