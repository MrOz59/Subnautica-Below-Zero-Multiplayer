using BelowZeroCoop.Server;

static void Check(bool success, string name)
{
    if (!success)
        throw new Exception("World-state regression failed: " + name);
}

static WorldCommand Build(uint actor, Guid entity, int generation = 1, long expected = 0,
                          Guid operation = default) =>
    new(operation == Guid.Empty ? Guid.NewGuid() : operation,
        actor, entity, generation, expected, WorldAction.Build, Kind: "Bioreactor");

static WorldCommand Change(uint actor, Guid entity, int generation, long expected,
                           string item, int quantity, Guid operation = default) =>
    new(operation == Guid.Empty ? Guid.NewGuid() : operation,
        actor, entity, generation, expected, WorldAction.ChangeItems,
        Item: item, QuantityDelta: quantity);

static WorldCommand Destroy(uint actor, Guid entity, int generation, long expected) =>
    new(Guid.NewGuid(), actor, entity, generation, expected, WorldAction.Destroy);

static WorldEntity Get(WorldState state, Guid entity)
    => state.Snapshot().Entities.Single(x => x.Id == entity);

DateTime now = new(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);

// Upstream #77: an abandoned interaction must never lock an object forever.
{
    var state = new WorldState();
    Guid entity = Guid.NewGuid();
    WorldResult built = state.Apply(Build(1, entity), now);
    Check(built.Applied && built.Revision == 1, "create entity");

    Check(state.TryAcquireLease(entity, 1, now, TimeSpan.FromSeconds(5)),
          "actor A acquires lock");
    Check(!state.TryAcquireLease(entity, 2, now, TimeSpan.FromSeconds(5)),
          "actor B cannot steal active lock");
    Check(state.ExpireLeases(now.AddSeconds(6)) == 1, "expired lease purged");
    Check(state.TryAcquireLease(entity, 2, now.AddSeconds(6), TimeSpan.FromSeconds(15)),
          "actor B can use object after owner timeout");
    Check(!state.Apply(Change(1, entity, 1, 1, "Fish", 1), now.AddSeconds(6)).Applied,
          "old actor may no longer mutate object");
    Check(state.ReleaseActor(2) == 1, "logout releases actor locks");
    Check(state.ReleaseActor(2) == 0, "repeated logout idempotent");
    Check(state.TryAcquireLease(entity, 3, now.AddSeconds(7), TimeSpan.FromSeconds(10)),
          "next player can interact after logout");
}

// Upstream #82: destroyed and rebuilt bioreactor must never retain old fish.
{
    var world = new WorldState();
    Guid id = Guid.NewGuid();
    WorldResult built = world.Apply(Build(1, id), now);
    Check(world.TryAcquireLease(id, 1, now, TimeSpan.FromSeconds(20)), "lease bioreactor");

    Guid addOp = Guid.NewGuid();
    WorldCommand add = Change(1, id, 1, built.Revision, "Fish", 1, addOp);
    WorldResult added = world.Apply(add, now);
    Check(added.Applied && Get(world, id).Items["Fish"] == 1, "add item");
    WorldResult repeated = world.Apply(add, now);
    Check(repeated.Applied && repeated.Duplicate && repeated.Revision == added.Revision,
          "repeat operation cannot duplicate item");
    Check(Get(world, id).Items["Fish"] == 1, "replayed add has no side effect");

    WorldResult destroyed = world.Apply(Destroy(1, id, 1, added.Revision), now);
    Check(destroyed.Applied, "destroy entity");
    Check(Get(world, id).Deleted && Get(world, id).Items.Count == 0,
          "tombstone clears contents");
    Check(!world.Apply(Change(1, id, 1, added.Revision, "Fish", 5), now).Applied,
          "late inventory update cannot modify tombstone");

    WorldResult rebuilt = world.Apply(Build(2, id, 2, destroyed.Revision), now);
    Check(rebuilt.Applied && Get(world, id).Generation == 2, "new generation");
    Check(Get(world, id).Items.Count == 0, "rebuild must begin empty");
    Check(!world.Apply(Build(1, id, 1, 0), now).Applied,
          "old build cannot resurrect stale generation");
    Check(!world.Apply(Destroy(1, id, 1, added.Revision), now).Applied,
          "old generation cannot destroy rebuilt object");
    Check(!world.Apply(Change(1, id, 1, 1, "Fish", 1, addOp), now).Applied ||
          Get(world, id).Items.Count == 0, "old repeated update has no new effect");
    Check(!world.TryAcquireLease(Guid.NewGuid(), 2, now, TimeSpan.FromSeconds(1)),
          "cannot claim nonexistent object");
}

// Upstream #82 across a restart: a deleted entity's tombstone must survive
// disk persistence and must prevent old item packets from reviving its content.
{
    var world = new WorldState();
    Guid id = Guid.NewGuid();
    WorldResult built = world.Apply(Build(10, id), now);
    Check(world.TryAcquireLease(id, 10, now, TimeSpan.FromSeconds(10)),
          "claim bioreactor before crash");
    WorldResult stored = world.Apply(
        Change(10, id, 1, built.Revision, "Fish", 4), now);
    WorldResult deleted = world.Apply(
        Destroy(10, id, 1, stored.Revision), now);
    Check(deleted.Applied, "tombstone committed before restart");

    string dir = Path.Combine(Path.GetTempPath(),
        "belowzerocoop-rebuild-" + Guid.NewGuid().ToString("N"));
    string path = Path.Combine(dir, "world.json");
    try
    {
        WorldStore.Save(path, world);
        var restored = WorldStore.Load(path);
        Check(Get(restored, id).Deleted, "tombstone persisted across restart");
        Check(Get(restored, id).Items.Count == 0,
              "destroyed inventory not retained");
        Check(!restored.Apply(Change(10, id, 1, stored.Revision,
                                    "Fish", 1), now).Applied,
              "stale fish packet rejected after restart");

        WorldResult rebuilt = restored.Apply(
            Build(20, id, 2, deleted.Revision), now);
        Check(rebuilt.Applied, "rebuild after restart accepted");
        Check(Get(restored, id).Items.Count == 0,
              "rebuild after restart starts with empty inventory");

        WorldStore.Save(path, restored);
        var secondRestart = WorldStore.Load(path);
        Check(Get(secondRestart, id).Generation == 2 &&
              Get(secondRestart, id).Items.Count == 0,
              "second restart cannot resurrect the old generation");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

// Upstream #85: deterministic snapshots must survive saving and late joins.
{
    var state = new WorldState();
    Guid baseId = Guid.NewGuid();
    Guid chestId = Guid.NewGuid();
    WorldResult first = state.Apply(Build(11, baseId), now);
    WorldResult second = state.Apply(Build(22, chestId), now);
    Check(first.Applied && second.Applied, "multiple independent constructions");
    Check(state.TryAcquireLease(chestId, 22, now, TimeSpan.FromSeconds(20)),
          "lease storage");
    Guid inventoryOp = Guid.NewGuid();
    WorldCommand inventory = Change(22, chestId, 1, second.Revision,
                                    "Battery", 3, inventoryOp);
    WorldResult filled = state.Apply(inventory, now);
    Check(filled.Applied, "store inventory");

    var snapshot = state.Snapshot();
    Check(state.TryChangesSince(first.Revision, out List<WorldChange> deltas),
          "changes after a baseline revision can be replayed");
    Check(deltas.Count == 2 &&
          deltas[0].Revision == second.Revision &&
          deltas[1].Revision == filled.Revision,
          "late join receives contiguous change history");
    deltas[1].Entity.Items["Battery"] = 999;
    Check(Get(state, chestId).Items["Battery"] == 3,
          "delta results cannot mutate authoritative entities");
    Check(!state.TryChangesSince(999, out _),
          "future revisions are rejected");
    Check(snapshot.Entities.Count == 2 && snapshot.Revision == 3,
          "snapshot sees both builders' changes");
    snapshot.Entities.Single(x => x.Id == chestId).Items["Battery"] = 200;
    Check(Get(state, chestId).Items["Battery"] == 3,
          "snapshot cannot mutate live world by reference");

    string dir = Path.Combine(Path.GetTempPath(), "belowzerocoop-world-" +
                              Guid.NewGuid().ToString("N"));
    string path = Path.Combine(dir, "world.json");
    try
    {
        WorldStore.Save(path, state);
        var reloaded = WorldStore.Load(path);
        Check(reloaded.Snapshot().Revision == state.Snapshot().Revision,
              "reloaded world retains global revision");
        Check(reloaded.TryChangesSince(first.Revision, out List<WorldChange> recovered) &&
              recovered.Count == 2,
              "change history survives persistence round-trip");
        Check(Get(reloaded, chestId).Items["Battery"] == 3,
              "reloaded world retains construction inventory");
        Check(reloaded.Apply(inventory, now).Duplicate,
              "operation receipt preserved across process restarts");
        Check(Get(reloaded, chestId).Items["Battery"] == 3,
              "replay after restart never duplicates inventory");

        // No actor's transient lock is persisted, even if the server died.
        Check(reloaded.TryAcquireLease(chestId, 31, now, TimeSpan.FromSeconds(10)),
              "crash recovery starts with no stale ownership");

        // Save again so there is an explicit recovery backup.
        WorldStore.Save(path, reloaded);
        Check(File.Exists(path + ".bak"), "previous complete snapshot retained");
        File.WriteAllText(path, "{}");
        bool incomplete = false;
        try { WorldStore.Load(path); }
        catch (InvalidDataException) { incomplete = true; }
        Check(incomplete, "empty or truncated world JSON cannot erase constructions");

        File.WriteAllText(path, "{ broken json");
        bool corrupted = false;
        try { WorldStore.Load(path); }
        catch (InvalidDataException) { corrupted = true; }
        Check(corrupted, "corrupted primary cannot silently reset the world");
        Check(Get(WorldStore.Load(path + ".bak"), chestId).Items["Battery"] == 3,
              "explicit backup remains recoverable");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

// Correct revision and operation IDs are required; a second player must not
// silently overwrite stale state (historical metadata/crafter issues).
{
    var world = new WorldState();
    Guid id = Guid.NewGuid();
    WorldResult built = world.Apply(Build(1, id), now);
    Check(world.TryAcquireLease(id, 1, now, TimeSpan.FromSeconds(5)), "take lease");
    Check(!world.Apply(Change(2, id, 1, built.Revision, "Fish", 1), now).Applied,
          "actor without lease cannot modify object");

    Guid op = Guid.NewGuid();
    var valid = Change(1, id, 1, built.Revision, "Fish", 1, op);
    Check(world.Apply(valid, now).Applied, "legitimate mutation");
    Check(!world.Apply(Change(1, id, 1, built.Revision, "Fish", 2, op), now).Applied,
          "reused operation ID with changed payload rejected");
    Check(!world.Apply(Change(1, id, 1, built.Revision, "Fish", 1), now).Applied,
          "stale revision rejected");
    Check(!world.Apply(Change(1, id, 1, 2, "Fish", -1000), now).Applied,
          "cannot consume inventory below zero");
    Check(Get(world, id).Items["Fish"] == 1, "conflicts leave state intact");
}

// Thread-safe simultaneous commands against the same revision: at most one wins.
{
    var world = new WorldState();
    Guid id = Guid.NewGuid();
    var ops = Enumerable.Range(1, 16).Select(index =>
        Build((uint)index, id)).ToArray();
    WorldResult[] results = await Task.WhenAll(
        ops.Select(op => Task.Run(() => world.Apply(op, now))));
    Check(results.Count(result => result.Applied && !result.Duplicate) == 1,
          "only one concurrent creation commits");
    Check(world.Snapshot().Revision == 1, "global revision is serializable");
}

// If the limited delta window is exhausted, fetch a full snapshot instead.
{
    var world = new WorldState();
    Guid id = Guid.NewGuid();
    WorldResult built = world.Apply(Build(5, id), now);
    Check(world.TryAcquireLease(id, 5, now, TimeSpan.FromSeconds(30)),
          "lease entity for change history pressure");
    long revision = built.Revision;
    for (int i = 0; i < WorldState.MaxChanges + 10; ++i)
    {
        WorldResult result = world.Apply(
            Change(5, id, 1, revision, "Battery", 1), now);
        Check(result.Applied, "new change is accepted under load");
        revision = result.Revision;
    }

    Check(!world.TryChangesSince(0, out _),
          "too-old clients must request a fresh snapshot");
    Check(world.TryChangesSince(revision - 2, out List<WorldChange> recent) &&
          recent.Count == 2, "recent change history is available");
    Check(recent[0].Revision == revision - 1 && recent[1].Revision == revision,
          "recent deltas are contiguous and ordered");
    Check(world.Snapshot().Changes.Count == WorldState.MaxChanges,
          "change history stays bounded");
}

ConstructionTopologyTests.Run();
Console.WriteLine("World-state regressions (#77/#82/#85) passed.");
