using BelowZeroCoop.Server;
using System.Text.Json.Nodes;

internal static class IntegratedWorldTests
{
    private static readonly IReadOnlyCollection<ConstructionBounds> Nobody =
        Array.Empty<ConstructionBounds>();

    private static readonly DateTime NowUtc =
        new(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);
    private static readonly FabricationRecipe Recipe =
        new("battery", "battery", TimeSpan.FromSeconds(2),
            new Dictionary<string, int> { ["copper"] = 2 });

    private static ConstructionBounds At(double x) =>
        new(x, 0, 0, 1, 1, 1);

    private static void Check(bool yes, string description)
    {
        if (!yes)
            throw new Exception("Integrated construction regression failed: " + description);
    }

    public static void Run()
    {
        OneSaveForCascadeEnergyAndCraftRefund();
        RejectInvalidOrOccupiedWithoutSideEffects();
        FailedStorageWriteDoesNotPublish();
        RejectCorruptCrossDomainSnapshots();
        ConcurrentMutationsAreSerialized();
        Console.WriteLine("Integrated construction, power and crafting regressions passed.");
    }

    private static void OneSaveForCascadeEnergyAndCraftRefund()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "bzc-world-aggregate-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "world.json");
        Guid gridId = Guid.NewGuid();
        Guid room = Guid.NewGuid(), solar = Guid.NewGuid(), fab = Guid.NewGuid();
        Guid jobId = Guid.NewGuid(), credit = Guid.NewGuid();
        Guid removalId = Guid.NewGuid();
        long committedRevision;
        try
        {
            using (var session = new IntegratedWorldSession(
                path, gridId, 10_000, new[] { Recipe }))
            {
                Check(session.Place(Guid.NewGuid(), 0, room, 1, "Room",
                    At(0), Nobody).Applied, "place parent room");
                Check(session.Place(Guid.NewGuid(), 1, solar, 1, "SolarPanel",
                    At(10), Nobody).Applied, "place generator");
                Check(session.Place(Guid.NewGuid(), 2, fab, 1, "Fabricator",
                    At(20), Nobody).Applied, "place fabricator and register station");

                Check(session.Connect(Guid.NewGuid(), 3, solar, 1, room, 1,
                    ConstructionLinkKind.Structural).Applied,
                    "generator depends on room");
                Check(session.Connect(Guid.NewGuid(), 4, fab, 1, room, 1,
                    ConstructionLinkKind.Structural).Applied,
                    "fabricator depends on room");
                Check(session.Connect(Guid.NewGuid(), 5, solar, 1, fab, 1,
                    ConstructionLinkKind.Power).Applied,
                    "power cable connects the two devices");

                Check(session.InstallPowerDevice(Guid.NewGuid(), 0, solar, 1,
                    PowerRole.Generator, 5000).Applied,
                    "live generator registered with matching construction");
                Check(session.InstallPowerDevice(Guid.NewGuid(), 1, fab, 1,
                    PowerRole.Consumer, 3000).Applied,
                    "live fabricator registered as power consumer");
                Check(session.GrantMaterials(credit, 10, "copper", 2),
                    "trusted actor granted copper");
                var begin = session.BeginCraft(jobId, fab, 1, 10, "battery", NowUtc);
                Check(begin.Started, "craft started using registered station");
                Check(session.Snapshot().Fabrication.Inventories.Single()
                    .Materials.Count == 0, "ingredients reserved");

                long beforeRejected = session.Snapshot().Revision;
                var rejected = session.Remove(Guid.NewGuid(), 6,
                    room, 1, false, Nobody, 1000);
                Check(!rejected.Applied, "noncascade removal rejects dependents");
                Check(session.Snapshot().Revision == beforeRejected,
                    "rejection leaves all three domains untouched");

                var deleted = session.Remove(removalId, 6, room, 1,
                    true, Nobody, 1000);
                Check(deleted.Applied && deleted.RemovedCount == 3,
                    "whole base removed with one atomic cascade");
                committedRevision = session.Snapshot().Revision;
                var state = session.Snapshot();
                Check(state.Construction.Nodes.All(n => n.Deleted) &&
                      state.Construction.Links.Count == 0,
                      "cascade deleted all structures and their power wires");
                Check(state.Power.Devices.Count == 2 &&
                      state.Power.Devices.All(d => d.Deleted),
                      "both power devices were tombstoned with their constructions");
                Check(state.Power.EnergyMilliUnits == 2000 &&
                      state.Power.LastTickMs == 1000,
                      "energy accounted up to host removal time before deletion");
                Check(state.Fabrication.Stations.Single().Deleted &&
                      state.Fabrication.Jobs.Single().Status == FabricationStatus.Cancelled,
                      "fabricator deleted and its unfinished work cancelled");
                Check(state.Fabrication.Inventories.Single()
                    .Materials["copper"] == 2,
                    "reserved crafting materials refunded once");

                var duplicate = session.Remove(removalId, 6, room, 1,
                    true, Nobody, 1000);
                Check(duplicate.Applied && duplicate.Duplicate &&
                      session.Snapshot().Revision == committedRevision,
                      "replayed removal never refunds or deletes twice");
            }

            using (var reopened = new IntegratedWorldSession(
                path, gridId, 10_000, new[] { Recipe }))
            {
                var state = reopened.Snapshot();
                Check(state.Revision == committedRevision &&
                      state.Construction.Nodes.All(n => n.Deleted) &&
                      state.Power.Devices.All(d => d.Deleted) &&
                      state.Fabrication.Jobs.Single().Status == FabricationStatus.Cancelled &&
                      state.Fabrication.Inventories.Single().Materials["copper"] == 2,
                      "single recovered snapshot contains all three committed effects");

                Check(!reopened.InstallPowerDevice(Guid.NewGuid(),
                    state.Power.ConfigRevision, solar, 1,
                    PowerRole.Generator, 9000).Applied,
                    "removed solar panel cannot be resurrected by delayed power event");
                Check(reopened.Place(Guid.NewGuid(),
                    state.Construction.Revision, fab, 2, "Fabricator",
                    At(20), Nobody).Applied,
                    "Fabricator can be reconstructed using next generation");
                var craft = reopened.BeginCraft(Guid.NewGuid(), fab, 2, 10,
                    "battery", NowUtc.AddSeconds(10));
                Check(craft.Started, "reconstructed Fabricator can use refunded inputs");
                var delivered = reopened.DeliverCraft(craft.JobId, 10,
                    NowUtc.AddSeconds(12));
                Check(delivered.Delivered && !delivered.Duplicate &&
                      delivered.OutputItemId == craft.OutputItemId,
                      "craft item delivered exactly once");
                Check(reopened.DeliverCraft(craft.JobId, 10,
                    NowUtc.AddSeconds(13)).Duplicate,
                    "replayed delivery cannot duplicate item");
            }

            Check(File.Exists(path + ".bak"), "previous composite snapshot kept");
            using var verified = new IntegratedWorldSession(
                path, gridId, 10_000, new[] { Recipe });
            Check(verified.Snapshot().Fabrication.Inventories.Single()
                .OwnedItems.Count == 1, "post-rebuild item persists on disk");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private static void RejectInvalidOrOccupiedWithoutSideEffects()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "bzc-integrated-check-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "world.json");
        try
        {
            using var session = new IntegratedWorldSession(
                path, Guid.NewGuid(), 10000, new[] { Recipe });
            Guid generator = Guid.NewGuid(), room = Guid.NewGuid();
            Check(session.Place(Guid.NewGuid(), 0, room, 1, "Room",
                At(0), Nobody).Applied, "room exists");
            Check(session.Place(Guid.NewGuid(), 1, generator, 1, "SolarPanel",
                At(10), Nobody).Applied, "generator exists");
            Check(session.InstallPowerDevice(Guid.NewGuid(), 0,
                generator, 1, PowerRole.Generator, 5000).Applied,
                "generator registered");
            long revision = session.Snapshot().Revision;
            var occupied = new[] { At(10) };

            var blocked = session.Remove(Guid.NewGuid(), 2, generator, 1,
                false, occupied, 1000);
            Check(!blocked.Applied && session.Snapshot().Revision == revision,
                "occupant prevents removal without partial power tombstone");

            blocked = session.Remove(Guid.NewGuid(), 2, generator, 1,
                false, Nobody, 10000);
            Check(!blocked.Applied && blocked.Reason == "invalid_power_clock" &&
                  session.Snapshot().Revision == revision,
                  "time budget violation rolls back construction tombstone");
            Check(session.Snapshot().Power.Devices.Single().Deleted == false,
                "invalid clock left generator alive");

            Check(session.Remove(Guid.NewGuid(), 2, generator, 1,
                false, Nobody, 1000).Applied,
                "valid follow-up removal commits");
            Check(session.Snapshot().Power.Devices.Single().Deleted,
                "follow-up removed source cleanly");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private static void FailedStorageWriteDoesNotPublish()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "bzc-integrated-io-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "blocked");
        Directory.CreateDirectory(path);
        try
        {
            using var session = new IntegratedWorldSession(
                path, Guid.NewGuid(), 10000, new[] { Recipe });
            bool failed = false;
            try
            {
                session.Place(Guid.NewGuid(), 0, Guid.NewGuid(), 1,
                    "Room", At(0), Nobody);
            }
            catch (IOException)
            {
                failed = true;
            }
            Check(failed, "save failure propagated instead of reporting success");
            Check(session.Snapshot().Revision == 0 &&
                  session.Snapshot().Construction.Nodes.Count == 0,
                  "failed commit never published tentative construction");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private static void RejectCorruptCrossDomainSnapshots()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "bzc-integrated-damage-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "world.json");
        Guid gridId = Guid.NewGuid(), source = Guid.NewGuid();
        try
        {
            using (var session = new IntegratedWorldSession(
                path, gridId, 10000, new[] { Recipe }))
            {
                session.Place(Guid.NewGuid(), 0, source, 1,
                    "SolarPanel", At(0), Nobody);
                session.InstallPowerDevice(Guid.NewGuid(), 0, source, 1,
                    PowerRole.Generator, 1000);
            }
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            json["Power"]!["Devices"]!.AsArray()[0]!["Id"] = Guid.NewGuid().ToString();
            File.WriteAllText(path, json.ToJsonString());

            bool refused = false;
            try
            {
                using var invalid = new IntegratedWorldSession(
                    path, gridId, 10000, new[] { Recipe });
            }
            catch (InvalidDataException)
            {
                refused = true;
            }
            Check(refused, "orphaned power source detected during integrated restore");

            json = JsonNode.Parse(File.ReadAllText(path + ".bak"))!;
            File.WriteAllText(path, json.ToJsonString());
            using var restored = new IntegratedWorldSession(
                path, gridId, 10000, new[] { Recipe });
            Check(restored.Snapshot().Construction.Nodes.Count == 1,
                "manual backup recovery restores an entire valid aggregate");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private static void ConcurrentMutationsAreSerialized()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "bzc-integrated-race-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "world.json");
        try
        {
            using var session = new IntegratedWorldSession(
                path, Guid.NewGuid(), 10000, new[] { Recipe });
            Guid a = Guid.NewGuid(), b = Guid.NewGuid();
            session.Place(Guid.NewGuid(), 0, a, 1, "Room", At(0), Nobody);
            session.Place(Guid.NewGuid(), 1, b, 1, "SolarPanel", At(10), Nobody);
            var start = new System.Threading.ManualResetEventSlim(false);
            long revision = session.Snapshot().Construction.Revision;
            var tasks = new[]
            {
                Task.Run(() =>
                {
                    start.Wait();
                    return session.Connect(Guid.NewGuid(), revision,
                        b, 1, a, 1, ConstructionLinkKind.Structural).Applied;
                }),
                Task.Run(() =>
                {
                    start.Wait();
                    return session.Remove(Guid.NewGuid(), revision, a,
                        1, false, Nobody, 1000).Applied;
                })
            };
            start.Set();
            Task.WaitAll(tasks);
            Check(tasks.Count(x => x.Result) == 1,
                "only one conflicting topology operation wins");
            Check(session.Snapshot().Revision == 3,
                "one authoritative aggregate revision for one committed change");
            using var second = new IntegratedWorldSession(
                Path.Combine(dir, "unrelated.json"), Guid.NewGuid(),
                10000, new[] { Recipe });
            Check(second.Snapshot().Revision == 0,
                "separate world files remain independent");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
