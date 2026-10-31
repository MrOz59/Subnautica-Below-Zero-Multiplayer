using System;
using System.Collections.Generic;
using System.IO;
using BelowZeroCoop.Server;

internal static class DurableSessionTests
{
    private static readonly FabricationRecipe Recipe = new(
        "sample_battery", "battery", TimeSpan.FromSeconds(2),
        new Dictionary<string, int> { ["copper"] = 2 });

    private static readonly DateTime Start =
        new(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);

    private static void Check(bool ok, string description)
    {
        if (!ok)
            throw new Exception("Durable crafting regression failed: " + description);
    }

    public static void Run()
    {
        StableOutputAcrossRestart();
        RejectOverlappingHostWriters();
        RollBackWhenSaveFails();
        Console.WriteLine("Durable crafting session regressions passed.");
    }

    private static void StableOutputAcrossRestart()
    {
        string folder = Path.Combine(Path.GetTempPath(), "bzc-session-" +
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "session.json");
        Guid jobId = Guid.NewGuid(), stationId = Guid.NewGuid(),
            grantId = Guid.NewGuid();
        Guid outputId;
        try
        {
            using (var session = new DurableFabricationSession(path, new[] { Recipe }))
            {
                Check(session.RegisterStation(stationId, 1), "register");
                Check(File.Exists(path), "register must persist before returning");
                Check(session.GrantMaterials(grantId, 10, "copper", 2), "credit");
                Check(session.TryOpen(stationId, 1, 10, Start, TimeSpan.FromSeconds(10)),
                      "open");
                var job = session.Begin(jobId, stationId, 1, 10, Recipe.Id, Start);
                Check(job.Started, "start craft");
                outputId = job.OutputItemId;

                var disk = GameplayStore.LoadFabrication(path, new[] { Recipe });
                Check(disk.SnapshotInventory(10)!.Materials.Count == 0,
                      "material reservation is durable before Begin returns");
                Check(disk.Snapshot().Jobs.Count == 1,
                      "pending job and reserved ingredients share a single file");
            }

            using (var resumed = new DurableFabricationSession(path, new[] { Recipe }))
            {
                Check(resumed.GrantMaterials(grantId, 10, "copper", 2),
                      "previous material grant replay is idempotent after restart");
                Check(resumed.SnapshotInventory(10)!.Materials.Count == 0,
                      "grant replay did not create resources");
                var ticket = resumed.Claim(jobId, 10, Start.AddSeconds(2));
                Check(ticket.Allowed && ticket.OutputItemId == outputId,
                      "pending craft remains claimable");
                var delivered = resumed.DeliverToInventory(jobId, 10, Start.AddSeconds(2));
                Check(delivered.Delivered && !delivered.Duplicate &&
                      delivered.OutputItemId == outputId, "unique item delivered");
                Check(resumed.SnapshotInventory(10)!.OwnedItems.Count == 1,
                      "one item credited");
            }

            using (var again = new DurableFabricationSession(path, new[] { Recipe }))
            {
                Check(again.DeliverToInventory(jobId, 10, Start.AddSeconds(3)).Duplicate,
                      "replayed delivery after process restart cannot duplicate output");
                Check(again.SnapshotInventory(10)!.OwnedItems.Count == 1 &&
                      again.SnapshotInventory(10)!.OwnedItems.ContainsKey(outputId),
                      "durable inventory has same UUID");
                Check(!again.DeliverToInventory(jobId, 11, Start.AddSeconds(3)).Delivered,
                      "unrelated actor cannot claim the durable item");
            }
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private static void RejectOverlappingHostWriters()
    {
        string folder = Path.Combine(Path.GetTempPath(), "bzc-lock-" +
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "save.json");
        try
        {
            using (var owner = new DurableFabricationSession(path, new[] { Recipe }))
            {
                bool blocked = false;
                try
                {
                    using var second = new DurableFabricationSession(path, new[] { Recipe });
                }
                catch (IOException)
                {
                    blocked = true;
                }
                Check(blocked, "a second process must not acquire the same save file");
            }
            using var afterRelease = new DurableFabricationSession(path, new[] { Recipe });
            Check(afterRelease.Snapshot().Jobs.Count == 0,
                  "released lock allows another session");
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private static void RollBackWhenSaveFails()
    {
        string folder = Path.Combine(Path.GetTempPath(), "bzc-fail-" +
            Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(folder, "occupied");
        Directory.CreateDirectory(destination);
        try
        {
            using var session = new DurableFabricationSession(
                destination, new[] { Recipe });
            bool failed = false;
            try
            {
                session.RegisterStation(Guid.NewGuid(), 1);
            }
            catch (IOException)
            {
                failed = true;
            }

            Check(failed, "storage error propagates to the caller");
            Check(session.Snapshot().Stations.Count == 0,
                  "failed save rolled back the uncommitted new station");
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }
}
