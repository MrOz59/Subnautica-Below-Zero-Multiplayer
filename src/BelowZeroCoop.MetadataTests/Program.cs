using System.Text.Json;
using System.Text.Json.Nodes;
using BelowZeroCoop.Server;

static void Check(bool condition, string label)
{
    if (!condition)
        throw new Exception("Metadata/energy regression failed: " + label);
}

static EquipmentCommand Equip(uint player, long rev, EquipmentAction kind, Guid item,
                              int slot = -1, Guid operation = default)
    => new(operation == Guid.Empty ? Guid.NewGuid() : operation,
           player, rev, kind, item, slot);

static PowerCommand Device(long revision, PowerAction action,
                           Guid id, int generation, PowerRole role,
                           long rate, Guid operation = default)
    => new(operation == Guid.Empty ? Guid.NewGuid() : operation,
           revision, action, id, generation, role, rate);

// #78: basic metadata and quickslot transitions.
{
    var equipment = new EquipmentState();
    uint actor = 4;
    Guid scanner = Guid.NewGuid(), seaglide = Guid.NewGuid();
    Guid addOp = Guid.NewGuid();
    EquipmentCommand add = Equip(actor, 0, EquipmentAction.AddItem, scanner,
                                 operation: addOp);
    EquipmentResult added = equipment.Apply(add, actor);
    Check(added.Applied && added.Revision == 1, "unique item added");

    EquipmentResult duplicate = equipment.Apply(add, actor);
    Check(duplicate.Applied && duplicate.Duplicate && duplicate.Revision == 1,
          "replayed item acquisition is idempotent");
    Check(equipment.SnapshotPlayer(actor)!.OwnedItems.Count == 1,
          "replay does not duplicate inventory");
    Check(!equipment.Apply(Equip(actor, 1, EquipmentAction.BindSlot,
                                 seaglide, 0), actor).Applied,
          "cannot place unowned item into a slot");
    Check(!equipment.Apply(Equip(actor, 1, EquipmentAction.BindSlot,
                                 scanner, 10), actor).Applied,
          "cannot access out-of-range slot");
    Check(!equipment.Apply(Equip(actor, 1, EquipmentAction.BindSlot,
                                 scanner, 0), actor + 1).Applied,
          "a different actor cannot access another player's quickslots");

    var bound = equipment.Apply(
        Equip(actor, 1, EquipmentAction.BindSlot, scanner, 0), actor);
    Check(bound.Applied && equipment.SnapshotPlayer(actor)!.Slots[0] == scanner,
          "binding references exact owned instance");

    var moved = equipment.Apply(
        Equip(actor, 2, EquipmentAction.BindSlot, scanner, 3), actor);
    Check(moved.Applied && equipment.SnapshotPlayer(actor)!.Slots[0] == null &&
          equipment.SnapshotPlayer(actor)!.Slots[3] == scanner,
          "moving same instance between slots atomically unbinds the old slot");
    Check(!equipment.Apply(Equip(actor, 2, EquipmentAction.RemoveItem, scanner),
                            actor).Applied, "old equipment revision rejected");

    var removed = equipment.Apply(
        Equip(actor, 3, EquipmentAction.RemoveItem, scanner), actor);
    Check(removed.Applied && equipment.SnapshotPlayer(actor)!.Slots[3] == null,
          "removing an item clears any dependent quickslot");
    Check(!equipment.Apply(Equip(actor, 4, EquipmentAction.BindSlot, scanner, 0),
                            actor).Applied, "removed item cannot be re-equipped");

    Guid collision = addOp;
    Check(!equipment.Apply(
        Equip(actor, 4, EquipmentAction.AddItem, seaglide,
              operation: collision), actor).Applied,
        "operation ID collision with a different payload is rejected");

    var snapshot = equipment.Snapshot();
    var restored = EquipmentState.Restore(
        JsonSerializer.Deserialize<EquipmentDocument>(
            JsonSerializer.Serialize(snapshot))!);
    Check(restored.SnapshotPlayer(actor)!.Revision == 4,
          "revisions survive version 2 metadata roundtrip");
    Check(restored.Apply(add, actor).Duplicate,
          "idempotent receipt persists in a save");
    snapshot.Players.Single().Slots[0] = seaglide;
    Check(restored.SnapshotPlayer(actor)!.Slots[0] == null,
          "snapshot objects cannot mutate a restored profile");

    var corrupted = restored.Snapshot();
    corrupted.Players[0].Slots[2] = Guid.NewGuid();
    bool rejected = false;
    try { EquipmentState.Restore(corrupted); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected, "dangling metadata item reference hard fails");

    string dir = Path.Combine(Path.GetTempPath(), "belowzerocoop-slots-" +
                              Guid.NewGuid().ToString("N"));
    string path = Path.Combine(dir, "equipment.json");
    try
    {
        GameplayStore.SaveEquipment(path, equipment);
        var recovered = GameplayStore.LoadEquipment(path);
        Check(recovered.SnapshotPlayer(actor)!.Revision == 4,
              "quickslot schema survives disk save and restart");
        Check(recovered.Apply(add, actor).Duplicate,
              "receipt preserved across equipment persistence");
        GameplayStore.SaveEquipment(path, recovered);
        Check(File.Exists(path + ".bak"),
              "previous equipment metadata file retained as backup");
        File.WriteAllText(path, "{}");
        bool corrupt = false;
        try { GameplayStore.LoadEquipment(path); }
        catch (InvalidDataException) { corrupt = true; }
        Check(corrupt, "incomplete equipment snapshot cannot erase slots");
        Check(GameplayStore.LoadEquipment(path + ".bak").SnapshotPlayer(actor) != null,
              "explicit equipment backup remains loadable");

        // A syntactically valid JSON file with a missing nested slot field
        // must never look like a player simply unequipped everything.
        var incomplete = JsonNode.Parse(File.ReadAllText(path + ".bak"))!;
        incomplete["Players"]!.AsArray()[0]!.AsObject().Remove("Slots");
        File.WriteAllText(path, incomplete.ToJsonString());
        bool missingSlotsRejected = false;
        try { GameplayStore.LoadEquipment(path); }
        catch (InvalidDataException) { missingSlotsRejected = true; }
        Check(missingSlotsRejected, "missing quickslot field is never silently cleared");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
    }
}

// #78: explicitly documented migration for OUR prior quickslot schema,
// never a claim of compatibility with third-party game data.
{
    Guid knife = Guid.NewGuid();
    var v1 = new EquipmentLegacyDocument
    {
        Version = 1,
        Players = new List<LegacyEquipmentProfile>
        {
            new()
            {
                PlayerId = 8,
                Revision = 7,
                OwnedItems = new List<Guid> { knife },
                Quickslots = new Guid?[] { null, knife, null, null, null }
            }
        }
    };

    EquipmentState migrated = EquipmentState.MigrateLegacy(v1);
    Check(migrated.SnapshotPlayer(8)!.Slots[1] == knife,
          "quickslot survives explicit legacy migration");
    Check(migrated.SnapshotPlayer(8)!.Revision == 7,
          "migration preserves revision for conflict detection");

    v1.Players[0].Quickslots[1] = Guid.NewGuid();
    bool refused = false;
    try { EquipmentState.MigrateLegacy(v1); }
    catch (InvalidDataException) { refused = true; }
    Check(refused, "invalid old metadata is rejected, not silently erased");
}

// #81: removal of a generator must immediately remove its future output.
{
    Guid grid = Guid.NewGuid();
    Guid solar = Guid.NewGuid();
    Guid fridge = Guid.NewGuid();
    var power = new PowerGridState(grid, capacityMilliUnits: 10000);

    var create = Device(0, PowerAction.Upsert, solar, 1,
                        PowerRole.Generator, 5000);
    var add = power.Apply(create);
    Check(add.Applied && add.Revision == 1, "solar generator configured");

    var load = power.Apply(Device(1, PowerAction.Upsert, fridge, 1,
                                  PowerRole.Consumer, 3000));
    Check(load.Applied && load.Revision == 2, "energy consumer configured");

    Check(power.TryAdvance(1000, out PowerBalance first),
          "first energy tick succeeds");
    Check(first.ProducedMilliUnits == 5000 &&
          first.DemandedMilliUnits == 3000 &&
          first.StoredMilliUnits == 2000,
          "integer energy production and consumption accounted");

    Guid deleteId = Guid.NewGuid();
    PowerCommand stopSolar = Device(2, PowerAction.Remove, solar, 1,
                                    PowerRole.None, 0, deleteId);
    PowerResult stopped = power.Apply(stopSolar);
    Check(stopped.Applied && stopped.Revision == 3, "generator removed");
    Check(power.Apply(stopSolar).Duplicate,
          "duplicate removal has no effect");

    Check(power.TryAdvance(2000, out PowerBalance afterRemove),
          "second energy tick succeeds");
    Check(afterRemove.ProducedMilliUnits == 0 &&
          afterRemove.DemandedMilliUnits == 3000 &&
          afterRemove.StoredMilliUnits == 0 &&
          afterRemove.UnservedMilliUnits == 1000,
          "removed generator cannot keep producing stale energy");

    Check(!power.Apply(Device(3, PowerAction.Upsert, solar, 1,
                              PowerRole.Generator, 5000)).Applied,
          "deleted solar entity cannot be resurrected as old generation");

    Check(power.Apply(Device(3, PowerAction.Upsert, solar, 2,
                             PowerRole.Generator, 7000)).Applied,
          "new generation may rebuild a previously removed source");
    Check(power.TryAdvance(3000, out PowerBalance resumed) &&
          resumed.ProducedMilliUnits == 7000 &&
          resumed.DemandedMilliUnits == 3000 &&
          resumed.StoredMilliUnits == 4000,
          "recreated generator re-enters the energy graph exactly once");

    var restored = PowerGridState.Restore(JsonSerializer.Deserialize<PowerDocument>(
        JsonSerializer.Serialize(power.Snapshot()))!);
    Check(restored.TryAdvance(4000, out PowerBalance next) &&
          next.StoredMilliUnits == 8000,
          "power graph and stored energy survive snapshot reload");
    Check(restored.Apply(stopSolar).Duplicate,
          "replayed remove cannot delete replacement generation");
    Check(restored.TryRebaseClock(0),
          "restored grid can explicitly adopt a new process clock");
    Check(!restored.TryRebaseClock(0),
          "clock rebasing is a one-shot recovery operation");
    Check(restored.Snapshot().EnergyMilliUnits == 8000,
          "clock rebasing never grants offline generator energy");
    Check(restored.Snapshot().Devices.Single(x => x.Id == solar).Generation == 2,
          "rebuild generation remains intact after old event replay");

    var corrupt = restored.Snapshot();
    corrupt.Devices[0].RateMilliUnitsPerSecond = -999;
    bool rejected = false;
    try { PowerGridState.Restore(corrupt); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected, "invalid power devices cannot be restored");

    string dir = Path.Combine(Path.GetTempPath(), "belowzerocoop-power-" +
                              Guid.NewGuid().ToString("N"));
    string path = Path.Combine(dir, "power.json");
    try
    {
        GameplayStore.SavePower(path, restored);
        var recovered = GameplayStore.LoadPower(path);
        Check(recovered.Snapshot().EnergyMilliUnits == 8000,
              "stored energy survives disk persistence");
        Check(recovered.TryRebaseClock(0),
              "fresh process can rebase an energy grid without stale ticks");
        Check(recovered.TryAdvance(1000, out PowerBalance afterRebase) &&
              afterRebase.StoredMilliUnits == 10000 &&
              afterRebase.SpilledMilliUnits == 2000,
              "power accounting resumes after restart without retroactive catch-up");
        Check(recovered.Apply(stopSolar).Duplicate,
              "old power removal stays idempotent after restart");
        Check(recovered.Snapshot().Devices.Single(x => x.Id == solar).Generation == 2,
              "generator generation persists across disk restart");
        GameplayStore.SavePower(path, recovered);
        Check(File.Exists(path + ".bak"),
              "previous complete power metadata saved as a backup");
        File.WriteAllText(path, "{ bad json");
        bool powerFileCorrupt = false;
        try { GameplayStore.LoadPower(path); }
        catch (InvalidDataException) { powerFileCorrupt = true; }
        Check(powerFileCorrupt, "invalid power JSON cannot silently reset grid");
        Check(GameplayStore.LoadPower(path + ".bak").Snapshot().EnergyMilliUnits == 10000,
              "explicit power backup remains recoverable");

        var incomplete = JsonNode.Parse(File.ReadAllText(path + ".bak"))!;
        incomplete["Devices"]!.AsArray()[0]!.AsObject().Remove("RateMilliUnitsPerSecond");
        File.WriteAllText(path, incomplete.ToJsonString());
        bool missingRateRejected = false;
        try { GameplayStore.LoadPower(path); }
        catch (InvalidDataException) { missingRateRejected = true; }
        Check(missingRateRejected,
              "missing generator rate never silently disables production");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
    }
}

// #81: overflow, time jump, demand shortage, and fractional accumulation.
{
    var power = new PowerGridState(Guid.NewGuid(), 10000, 0, 0);
    Guid microGenerator = Guid.NewGuid();
    Check(power.Apply(Device(0, PowerAction.Upsert, microGenerator, 1,
                             PowerRole.Generator, 1)).Applied,
          "fractional rate device accepted");
    Check(!power.TryAdvance(-1, out _), "negative time rejected");
    Check(!power.TryAdvance(10000, out _), "unbounded host sleep rejected");
    Check(power.TryAdvance(500, out PowerBalance half) &&
          half.ProducedMilliUnits == 0,
          "fractional production is retained as a remainder");
    Check(power.TryAdvance(1000, out PowerBalance full) &&
          full.ProducedMilliUnits == 1 &&
          full.StoredMilliUnits == 1,
          "two half-second ticks exactly match one second");

    Guid consumer = Guid.NewGuid();
    Check(power.Apply(Device(1, PowerAction.Upsert, consumer, 1,
                             PowerRole.Consumer, 4000)).Applied,
          "consumer configured");
    Check(power.TryAdvance(2000, out PowerBalance underpowered) &&
          underpowered.StoredMilliUnits == 0 &&
          underpowered.UnservedMilliUnits == 3998,
          "unsatisfied demand is recorded instead of creating negative power");
    Check(power.Snapshot().TotalUnservedMilliUnits == 3998,
          "unserved demand persists in metadata");

    bool hugeRejected = false;
    try { new PowerGridState(Guid.NewGuid(), PowerGridState.MaxCapacity + 1); }
    catch (ArgumentOutOfRangeException) { hugeRejected = true; }
    Check(hugeRejected, "unbounded energy storage rejected");
}

Console.WriteLine("Equipment schema and power balance regressions (#78/#81) passed.");
