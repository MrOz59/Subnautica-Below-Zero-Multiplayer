using System.Text.Json;
using BelowZeroCoop.Server;

internal static class GameEntityIdentityTests
{
    private static readonly IReadOnlyCollection<ConstructionBounds> Nobody =
        Array.Empty<ConstructionBounds>();

    private static ConstructionBounds At(double x) =>
        new(x, 0, 0, 1, 1, 1);

    private static void Check(bool condition, string reason)
    {
        if (!condition)
            throw new Exception("Game identity regression: " + reason);
    }

    public static void Run()
    {
        DeterministicAndScoped();
        CorruptionAndCapacity();
        AtomicObservedConstructionAndRestart();
        LegacySchemaMigration();
        CorruptCompositeIdentityIsRejected();
        SaveFailureCannotPublishMapping();
        Console.WriteLine("Game-origin identity and provenance regressions passed.");
    }

    private static void DeterministicAndScoped()
    {
        Guid scope = Guid.NewGuid(), otherScope = Guid.NewGuid();
        var ids = new GameEntityIdentityMap(scope);
        Check(!ids.TryFind("submarine:piece-1", out _),
            "unknown source lookup is read-only");
        Check(ids.TryBind("submarine:piece-1", out Guid first),
            "first verified UID gets a mapping");
        Check(first != Guid.Empty, "mapped GUID is nonempty");
        Check(ids.TryBind("submarine:piece-1", out Guid again) &&
              again == first && ids.Snapshot().Revision == 1,
              "repeated bind is idempotent");
        Check(ids.TryBind("Submarine:piece-1", out Guid upper) && upper != first,
              "game ID case remains significant, not normalized");
        Check(ids.TryFind("submarine:piece-1", out Guid lookup) &&
              lookup == first, "read lookup returns original binding");

        var restored = GameEntityIdentityMap.Restore(ids.Snapshot(), scope);
        Check(restored.TryFind("submarine:piece-1", out Guid persisted) &&
              persisted == first, "mapping survives JSON domain restore");
        Check(GameEntityIdentityMap.Derive(scope, "submarine:piece-1") == first &&
              GameEntityIdentityMap.Derive(otherScope, "submarine:piece-1") != first,
              "same world maps reproducibly, different worlds are isolated");
        Check(!ids.TryBind("", out _) &&
              !ids.TryBind("bad\nvalue", out _) &&
              !ids.TryBind(new string('x', 129), out _),
              "empty, control-character and oversized identifiers fail closed");
        Check(ids.Snapshot().Entries.Count == 2,
              "invalid IDs never add entries");
    }

    private static void CorruptionAndCapacity()
    {
        Guid scope = Guid.NewGuid();
        var state = new GameEntityIdentityMap(scope);
        for (int i = 0; i < GameEntityIdentityMap.MaxEntries; ++i)
            Check(state.TryBind("world:id-" + i, out _), "capacity-fill mapping");
        Check(!state.TryBind("world:extra", out _),
            "full registry rejects new ID rather than evicting old mapping");
        Check(state.TryBind("world:id-0", out _),
            "full registry still handles replay of an existing UID");
        Check(state.Snapshot().Entries.Count == GameEntityIdentityMap.MaxEntries,
            "registry remains bounded");
        var snap = state.Snapshot();
        snap.Entries[0].EntityId = Guid.NewGuid();
        bool rejected = false;
        try { GameEntityIdentityMap.Restore(snap, scope); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "tampered mapping fails deterministic validation");

        snap = state.Snapshot();
        snap.Entries.Add(snap.Entries[0].Copy());
        rejected = false;
        try { GameEntityIdentityMap.Restore(snap, scope); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "duplicated mapping fails restore");

        rejected = false;
        try { GameEntityIdentityMap.Restore(state.Snapshot(), Guid.NewGuid()); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "wrong world scope cannot reuse save mappings");
    }

    private static void AtomicObservedConstructionAndRestart()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "bzc-game-identities-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "world.json");
        Guid scope = Guid.NewGuid(), op = Guid.NewGuid();
        Guid entityId = Guid.Empty;
        try
        {
            using (var host = new IntegratedWorldSession(
                file, scope, 10000, Array.Empty<FabricationRecipe>()))
            {
                foreach (ConstructionEventProvenance provenance in new[]
                {
                    ConstructionEventProvenance.Unknown,
                    ConstructionEventProvenance.SaveLoading,
                    ConstructionEventProvenance.RemoteReplay
                })
                {
                    var rejected = host.PlaceObserved(op, 0, "piece:101", 1,
                        "Room", At(2), Nobody, provenance);
                    Check(!rejected.Applied && rejected.Reason == "origin_unverified",
                        "unverified source never becomes construction");
                }
                Check(host.Snapshot().Revision == 0 &&
                      host.Snapshot().GameIds.Entries.Count == 0,
                    "loading or replay does not allocate a binding");

                var invalid = host.PlaceObserved(op, 0, "bad\nuid", 1,
                    "Room", At(2), Nobody,
                    ConstructionEventProvenance.VerifiedLocalBuild);
                Check(!invalid.Applied && host.Snapshot().GameIds.Entries.Count == 0,
                    "malformed game UID never inserts a registry entry");

                var placed = host.PlaceObserved(op, 0, "piece:101", 1,
                    "Room", At(2), Nobody,
                    ConstructionEventProvenance.VerifiedLocalBuild);
                Check(placed.Applied && host.TryFindGameEntity("piece:101", out entityId),
                    "verified source binds a persistent GUID");
                Check(host.Snapshot().Construction.Nodes.Single().Id == entityId &&
                      host.Snapshot().World.Entities.Single().Id == entityId &&
                      host.Snapshot().GameIds.Entries.Single().EntityId == entityId,
                    "source mapping, construction and world index committed together");

                long revision = host.Snapshot().Revision;
                var operationCollision = host.PlaceObserved(op, 0,
                    "different:piece", 1, "Room", At(2), Nobody,
                    ConstructionEventProvenance.VerifiedLocalBuild);
                Check(!operationCollision.Applied &&
                      operationCollision.Reason == "operation_id_collision" &&
                      host.Snapshot().GameIds.Entries.Count == 1,
                    "reused operation UUID cannot assign a second game object ID");
                Check(host.PlaceObserved(op, 0, "piece:101", 1,
                    "Room", At(2), Nobody,
                    ConstructionEventProvenance.VerifiedLocalBuild).Duplicate &&
                    host.Snapshot().Revision == revision,
                    "repeated placement operation does not increment any state");
                var stale = host.PlaceObserved(Guid.NewGuid(), 0, "new:unseen", 1,
                    "Room", At(4), Nobody,
                    ConstructionEventProvenance.VerifiedLocalBuild);
                Check(!stale.Applied && host.Snapshot().GameIds.Entries.Count == 1,
                    "rejected placement never leaves a dangling mapping");
            }
            using (var reopened = new IntegratedWorldSession(
                file, scope, 10000, Array.Empty<FabricationRecipe>()))
            {
                Check(reopened.TryFindGameEntity("piece:101", out Guid same) &&
                      same == entityId, "exact identity survives process restart");
                Check(!reopened.TryFindGameEntity("new:unseen", out _),
                    "rejected identity remains absent after restart");
                long before = reopened.Snapshot().Construction.Revision;
                var removed = reopened.Remove(Guid.NewGuid(), before, entityId,
                    1, false, Nobody, 1000);
                Check(removed.Applied, "original construction can be deleted");
                var rebuilt = reopened.PlaceObserved(Guid.NewGuid(),
                    reopened.Snapshot().Construction.Revision, "piece:101",
                    2, "Room", At(2), Nobody,
                    ConstructionEventProvenance.VerifiedLocalBuild);
                Check(rebuilt.Applied &&
                      reopened.TryFindGameEntity("piece:101", out Guid stable) &&
                      stable == entityId &&
                      reopened.Snapshot().Construction.Nodes.Single().Generation == 2,
                      "tombstone generation changes, never the game ID binding");
            }

            using var final = new IntegratedWorldSession(
                file, scope, 10000, Array.Empty<FabricationRecipe>());
            Check(final.Snapshot().GameIds.Entries.Single().EntityId == entityId &&
                  final.Snapshot().World.Entities.Single().Generation == 2,
                "generation and identity persisted across multiple restarts");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private static void LegacySchemaMigration()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "bzc-identity-migration-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "world.json");
        Guid scope = Guid.NewGuid();
        try
        {
            using (var state = new IntegratedWorldSession(
                file, scope, 10000, Array.Empty<FabricationRecipe>()))
            {
                Check(state.Place(Guid.NewGuid(), 0, Guid.NewGuid(), 1,
                    "Room", At(2), Nobody).Applied, "legacy world created");
            }
            var json = JsonSerializer.Deserialize<IntegratedWorldDocument>(
                File.ReadAllText(file))!;
            json.Version = 2;
            json.GameIds = null!;
            File.WriteAllText(file, JsonSerializer.Serialize(json));
            using (var restored = new IntegratedWorldSession(
                file, scope, 10000, Array.Empty<FabricationRecipe>()))
            {
                Check(restored.Snapshot().Version == 3 &&
                      restored.Snapshot().GameIds.Entries.Count == 0 &&
                      restored.Snapshot().Construction.Nodes.Count == 1,
                      "v2 snapshot upgraded in memory without inventing IDs");
                Check(restored.Place(Guid.NewGuid(), 1, Guid.NewGuid(), 1,
                    "Room", At(5), Nobody).Applied,
                    "next successful write upgrades saved schema to v3");
            }
            var onDisk = JsonSerializer.Deserialize<IntegratedWorldDocument>(
                File.ReadAllText(file))!;
            Check(onDisk.Version == 3 && onDisk.GameIds != null,
                "durable migration preserves all existing construction data");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private static void CorruptCompositeIdentityIsRejected()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "bzc-identity-tamper-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "world.json");
        Guid scope = Guid.NewGuid();
        try
        {
            using (var host = new IntegratedWorldSession(
                path, scope, 10000, Array.Empty<FabricationRecipe>()))
            {
                Check(host.PlaceObserved(Guid.NewGuid(), 0,
                    "piece:bound", 1, "Room", At(1), Nobody,
                    ConstructionEventProvenance.VerifiedLocalBuild).Applied,
                    "source bound for composite corruption test");
            }

            var document = JsonSerializer.Deserialize<IntegratedWorldDocument>(
                File.ReadAllText(path))!;
            document.GameIds.Entries[0].EntityId = Guid.NewGuid();
            File.WriteAllText(path, JsonSerializer.Serialize(document));

            bool refused = false;
            try
            {
                using var invalid = new IntegratedWorldSession(
                    path, scope, 10000, Array.Empty<FabricationRecipe>());
            }
            catch (InvalidDataException) { refused = true; }
            Check(refused, "corrupted nested identity cannot pass integrated restore");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private static void SaveFailureCannotPublishMapping()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "bzc-identity-fail-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "occupied");
        Directory.CreateDirectory(file);
        try
        {
            using var host = new IntegratedWorldSession(
                file, Guid.NewGuid(), 10000, Array.Empty<FabricationRecipe>());
            bool failed = false;
            try
            {
                host.PlaceObserved(Guid.NewGuid(), 0, "piece:pending", 1,
                    "Room", At(0), Nobody,
                    ConstructionEventProvenance.VerifiedLocalBuild);
            }
            catch (IOException) { failed = true; }
            Check(failed && !host.TryFindGameEntity("piece:pending", out _) &&
                  host.Snapshot().GameIds.Entries.Count == 0 &&
                  host.Snapshot().Construction.Nodes.Count == 0,
                "failed disk write cannot publish either identity or construction");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
