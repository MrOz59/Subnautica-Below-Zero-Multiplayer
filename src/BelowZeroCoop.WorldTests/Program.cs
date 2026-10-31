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
        Check(Get(reloaded, chestId).Items["Battery"] == 3,
              "reloaded world retains construction inventory");
        Check(!reloaded.Apply(inventory, now).Duplicate == false,
              "operation receipt preserved"); // duplicate is expected
        Check(Get(reloaded, chestId).Items["Battery"] == 3,
              "replay after restart never duplicates inventory");

        // No actor's transient lock is persisted, even if the server died.
        Check(reloaded.TryAcquireLease(chestId, 31, now, TimeSpan.FromSeconds(10)),
              "crash recovery starts with no stale ownership");

        // Save again so there is an explicit recovery backup.
        WorldStore.Save(path, reloaded);
        Check(File.Exists(path + ".bak"), "previous complete snapshot retained");
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

Console.WriteLine("World-state regressions (#77/#82/#85) passed.");
