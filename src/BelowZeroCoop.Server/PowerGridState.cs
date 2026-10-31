using System.Text.Json;

namespace BelowZeroCoop.Server;

/// <summary>
/// Standalone, deterministic base energy accounting. All energy and rates
/// use integer milli-units, never float. Game integration is deliberately
/// absent pending an authenticated host-authoritative event channel.
/// Historical upstream regression: #81.
/// </summary>
internal sealed class PowerGridState
{
    public const int SchemaVersion = 1;
    public const int MaxDevices = 128;
    public const int MaxReceipts = 2048;
    public const long MaxRatePerDevice = 1_000_000;
    public const long MaxCapacity = 1_000_000_000;
    public const long MaxTickMs = 5000;

    private readonly object gate = new();
    private readonly Dictionary<Guid, PowerDevice> devices = new();
    private readonly Dictionary<Guid, PowerReceipt> receipts = new();
    private readonly Queue<Guid> receiptOrder = new();

    private readonly Guid gridId;
    private readonly long capacity;
    private long energy;
    private long lastTickMs;
    private long configRevision;
    private long generatorRemainder;
    private long consumerRemainder;
    private long totalUnserved;
    private long totalSpilled;

    public PowerGridState(Guid gridId, long capacityMilliUnits, long initialMilliUnits = 0,
                          long initialTickMs = 0)
    {
        if (gridId == Guid.Empty || capacityMilliUnits < 0 ||
            capacityMilliUnits > MaxCapacity || initialMilliUnits < 0 ||
            initialMilliUnits > capacityMilliUnits || initialTickMs < 0)
            throw new ArgumentOutOfRangeException(nameof(capacityMilliUnits),
                "Invalid grid, capacity, initial stored energy or time.");

        this.gridId = gridId;
        capacity = capacityMilliUnits;
        energy = initialMilliUnits;
        lastTickMs = initialTickMs;
    }

    public PowerResult Apply(PowerCommand command)
    {
        lock (gate)
        {
            if (command == null || command.OperationId == Guid.Empty ||
                command.DeviceId == Guid.Empty || command.Generation < 1 ||
                !Enum.IsDefined(typeof(PowerAction), command.Action))
                return PowerResult.Reject("invalid_command", configRevision);

            string signature = JsonSerializer.Serialize(command);
            if (receipts.TryGetValue(command.OperationId, out PowerReceipt? prior))
            {
                return prior.Signature == signature
                    ? new PowerResult(true, true, "duplicate", prior.Revision)
                    : PowerResult.Reject("operation_id_collision", configRevision);
            }

            if (command.ExpectedRevision != configRevision)
                return PowerResult.Reject("revision_conflict", configRevision);

            // Only an authenticated host should be allowed to invoke this API
            // once it is connected to the real game. It is not exposed on UDP.
            PowerDevice candidate;
            if (command.Action == PowerAction.Upsert)
            {
                if (command.Role is not (PowerRole.Generator or PowerRole.Consumer) ||
                    command.RateMilliUnitsPerSecond < 0 ||
                    command.RateMilliUnitsPerSecond > MaxRatePerDevice)
                    return PowerResult.Reject("invalid_device", configRevision);

                if (devices.TryGetValue(command.DeviceId, out PowerDevice? existing))
                {
                    if (existing.Deleted)
                    {
                        if (existing.Generation == int.MaxValue ||
                            command.Generation != existing.Generation + 1)
                            return PowerResult.Reject("generation_conflict", configRevision);
                    }
                    else if (command.Generation != existing.Generation)
                    {
                        return PowerResult.Reject("generation_conflict", configRevision);
                    }
                }
                else if (command.Generation != 1 || devices.Count >= MaxDevices)
                {
                    return PowerResult.Reject("missing_or_full", configRevision);
                }

                candidate = new PowerDevice
                {
                    Id = command.DeviceId,
                    Generation = command.Generation,
                    Role = command.Role,
                    RateMilliUnitsPerSecond = command.RateMilliUnitsPerSecond,
                    Deleted = false
                };
            }
            else
            {
                if (command.Role != PowerRole.None ||
                    command.RateMilliUnitsPerSecond != 0 ||
                    !devices.TryGetValue(command.DeviceId, out PowerDevice? existing) ||
                    existing.Deleted || command.Generation != existing.Generation)
                    return PowerResult.Reject("missing_or_stale_device", configRevision);

                candidate = new PowerDevice
                {
                    Id = command.DeviceId,
                    Generation = existing.Generation,
                    Role = PowerRole.None,
                    RateMilliUnitsPerSecond = 0,
                    Deleted = true
                };
            }

            devices[command.DeviceId] = candidate;
            configRevision++;
            var receipt = new PowerReceipt(command.OperationId, signature, configRevision);
            receipts[command.OperationId] = receipt;
            receiptOrder.Enqueue(command.OperationId);
            while (receipts.Count > MaxReceipts)
                receipts.Remove(receiptOrder.Dequeue());

            return new PowerResult(true, false, "ok", configRevision);
        }
    }

    /// <summary>
    /// Caller provides monotonically increasing elapsed milliseconds. A long
    /// pause cannot be silently processed as unlimited free generation.
    /// Consumption is always deducted when devices are active; a removed
    /// device makes no contribution starting with the NEXT tick.
    /// </summary>
    public bool TryAdvance(long nowMs, out PowerBalance balance)
    {
        lock (gate)
        {
            balance = new PowerBalance(energy, 0, 0, 0, 0);
            if (nowMs < lastTickMs || nowMs - lastTickMs > MaxTickMs)
                return false;

            long delta = nowMs - lastTickMs;
            if (delta == 0)
                return true;

            long generatorRate = 0, consumerRate = 0;
            foreach (PowerDevice device in devices.Values)
            {
                if (device.Deleted)
                    continue;
                if (device.Role == PowerRole.Generator)
                    generatorRate += device.RateMilliUnitsPerSecond;
                else
                    consumerRate += device.RateMilliUnitsPerSecond;
            }

            long production = (generatorRate * delta + generatorRemainder) / 1000;
            long demand = (consumerRate * delta + consumerRemainder) / 1000;
            generatorRemainder = (generatorRate * delta + generatorRemainder) % 1000;
            consumerRemainder = (consumerRate * delta + consumerRemainder) % 1000;

            long available = energy + production;
            long served = Math.Min(available, demand);
            long unserved = demand - served;
            long remaining = available - served;
            long spilled = Math.Max(0, remaining - capacity);

            energy = Math.Min(capacity, remaining);
            totalUnserved = checked(totalUnserved + unserved);
            totalSpilled = checked(totalSpilled + spilled);
            lastTickMs = nowMs;
            balance = new PowerBalance(energy, production, demand, unserved, spilled);
            return true;
        }
    }

    public PowerDocument Snapshot()
    {
        lock (gate)
        {
            return new PowerDocument
            {
                Version = SchemaVersion,
                GridId = gridId,
                ConfigRevision = configRevision,
                CapacityMilliUnits = capacity,
                EnergyMilliUnits = energy,
                LastTickMs = lastTickMs,
                GeneratorRemainder = generatorRemainder,
                ConsumerRemainder = consumerRemainder,
                TotalUnservedMilliUnits = totalUnserved,
                TotalSpilledMilliUnits = totalSpilled,
                Devices = devices.Values.OrderBy(x => x.Id).Select(x => x.Copy()).ToList(),
                Receipts = receiptOrder.Select(x => receipts[x]).ToList()
            };
        }
    }

    public static PowerGridState Restore(PowerDocument document)
    {
        if (document == null || document.Version != SchemaVersion ||
            document.GridId == Guid.Empty || document.CapacityMilliUnits < 0 ||
            document.CapacityMilliUnits > MaxCapacity ||
            document.EnergyMilliUnits < 0 ||
            document.EnergyMilliUnits > document.CapacityMilliUnits ||
            document.LastTickMs < 0 || document.ConfigRevision < 0 ||
            document.GeneratorRemainder < 0 || document.GeneratorRemainder >= 1000 ||
            document.ConsumerRemainder < 0 || document.ConsumerRemainder >= 1000 ||
            document.TotalUnservedMilliUnits < 0 || document.TotalSpilledMilliUnits < 0 ||
            document.Devices == null || document.Devices.Count > MaxDevices ||
            document.Receipts == null || document.Receipts.Count > MaxReceipts)
            throw new InvalidDataException("Unsupported or invalid power snapshot.");

        var restored = new PowerGridState(document.GridId,
            document.CapacityMilliUnits, document.EnergyMilliUnits, document.LastTickMs)
        {
            configRevision = document.ConfigRevision,
            generatorRemainder = document.GeneratorRemainder,
            consumerRemainder = document.ConsumerRemainder,
            totalUnserved = document.TotalUnservedMilliUnits,
            totalSpilled = document.TotalSpilledMilliUnits
        };

        foreach (PowerDevice device in document.Devices)
        {
            if (device == null || device.Id == Guid.Empty ||
                device.Generation < 1 ||
                (device.Deleted &&
                 (device.Role != PowerRole.None || device.RateMilliUnitsPerSecond != 0)) ||
                (!device.Deleted &&
                 (device.Role is not (PowerRole.Generator or PowerRole.Consumer) ||
                  device.RateMilliUnitsPerSecond < 0 ||
                  device.RateMilliUnitsPerSecond > MaxRatePerDevice)) ||
                !restored.devices.TryAdd(device.Id, device.Copy()))
                throw new InvalidDataException("Invalid or duplicate power device.");
        }

        foreach (PowerReceipt receipt in document.Receipts)
        {
            if (receipt == null || receipt.OperationId == Guid.Empty ||
                string.IsNullOrWhiteSpace(receipt.Signature) ||
                receipt.Signature.Length > 1024 ||
                receipt.Revision < 1 || receipt.Revision > document.ConfigRevision ||
                !restored.receipts.TryAdd(receipt.OperationId, receipt))
                throw new InvalidDataException("Invalid power operation receipt.");
            restored.receiptOrder.Enqueue(receipt.OperationId);
        }

        return restored;
    }
}

internal enum PowerRole { None = 0, Generator = 1, Consumer = 2 }
internal enum PowerAction { Upsert = 1, Remove = 2 }
internal sealed record PowerCommand(Guid OperationId, long ExpectedRevision,
    PowerAction Action, Guid DeviceId, int Generation, PowerRole Role,
    long RateMilliUnitsPerSecond);

internal sealed record PowerResult(bool Applied, bool Duplicate, string Reason, long Revision)
{
    public static PowerResult Reject(string reason, long revision)
        => new(false, false, reason, revision);
}

internal sealed record PowerBalance(long StoredMilliUnits,
    long ProducedMilliUnits, long DemandedMilliUnits,
    long UnservedMilliUnits, long SpilledMilliUnits);

internal sealed class PowerDevice
{
    public Guid Id { get; set; }
    public int Generation { get; set; }
    public PowerRole Role { get; set; }
    public long RateMilliUnitsPerSecond { get; set; }
    public bool Deleted { get; set; }

    public PowerDevice Copy() => new()
    {
        Id = Id,
        Generation = Generation,
        Role = Role,
        RateMilliUnitsPerSecond = RateMilliUnitsPerSecond,
        Deleted = Deleted
    };
}

internal sealed class PowerDocument
{
    public int Version { get; set; } = PowerGridState.SchemaVersion;
    public Guid GridId { get; set; }
    public long ConfigRevision { get; set; }
    public long CapacityMilliUnits { get; set; }
    public long EnergyMilliUnits { get; set; }
    public long LastTickMs { get; set; }
    public long GeneratorRemainder { get; set; }
    public long ConsumerRemainder { get; set; }
    public long TotalUnservedMilliUnits { get; set; }
    public long TotalSpilledMilliUnits { get; set; }
    public List<PowerDevice> Devices { get; set; } = new();
    public List<PowerReceipt> Receipts { get; set; } = new();
}

internal sealed record PowerReceipt(Guid OperationId, string Signature, long Revision);
