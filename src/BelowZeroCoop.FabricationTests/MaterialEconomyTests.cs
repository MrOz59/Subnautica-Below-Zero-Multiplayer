using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BelowZeroCoop.Server;

internal static class MaterialEconomyTests
{
    private static void Require(bool condition, string detail)
    {
        if (!condition)
            throw new Exception("Craft material transaction failed: " + detail);
    }

    private static readonly DateTime Started =
        new(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);

    private static readonly FabricationRecipe Recipe =
        new("battery", "battery", TimeSpan.FromSeconds(2),
            new Dictionary<string, int> { ["copper"] = 2, ["acid"] = 1 });

    private static FabricationState NewMachine() => new(new[] { Recipe });

    private static int Count(FabricationState state, uint actor, string name)
        => state.SnapshotInventory(actor)?.Materials.GetValueOrDefault(name) ?? 0;

    public static void Run()
    {
        NoMaterialsOrUnauthorizedOwner();
        IdempotentReserveAndRefund();
        DestroyRefunds();
        DeliveryAfterCrashUsesSameItemId();
        SimultaneousCraftsCannotOverspend();
        CorruptionIsRejected();
        Console.WriteLine("Crafting material and transactional inventory regressions passed.");
    }

    private static void NoMaterialsOrUnauthorizedOwner()
    {
        var server = NewMachine();
        Guid station = Guid.NewGuid(), job = Guid.NewGuid();
        Require(server.RegisterStation(station, 1), "register");
        Require(server.TryOpen(station, 1, 1, Started, TimeSpan.FromSeconds(10)), "open");
        Require(!server.Begin(job, station, 1, 1, "battery", Started).Started,
            "cannot craft without ingredients");
        Guid credit = Guid.NewGuid();
        Require(server.GrantMaterials(credit, 1, "copper", 2), "grant copper");
        Require(server.GrantMaterials(credit, 1, "copper", 2), "replayed grant accepted");
        Require(Count(server, 1, "copper") == 2, "grant exactly once");
        Require(!server.GrantMaterials(credit, 2, "copper", 2), "credit identity collision");
        Require(!server.GrantMaterials(credit, 1, "copper", 3), "changed credit rejected");
        Require(!server.Begin(job, station, 1, 1, "battery", Started).Started,
            "missing acid cannot start");
        Require(server.GrantMaterials(Guid.NewGuid(), 2, "acid", 2), "grant second player");
        Require(!server.Begin(job, station, 1, 1, "battery", Started).Started,
            "second player ingredients cannot be spent");
        Require(server.GrantMaterials(Guid.NewGuid(), 1, "acid", 1), "grant acid");
        Require(server.Begin(job, station, 1, 1, "battery", Started).Started,
            "valid inventory enables crafting");
        Require(Count(server, 1, "copper") == 0 && Count(server, 1, "acid") == 0,
            "ingredients reserved exactly once");
    }

    private static void IdempotentReserveAndRefund()
    {
        var server = NewMachine();
        Guid station = Guid.NewGuid(), job = Guid.NewGuid();
        Require(server.RegisterStation(station, 1), "register station");
        Require(server.GrantMaterials(Guid.NewGuid(), 17, "copper", 3), "stock copper");
        Require(server.GrantMaterials(Guid.NewGuid(), 17, "acid", 2), "stock acid");
        Require(server.TryOpen(station, 1, 17, Started, TimeSpan.FromSeconds(10)),
            "open station");
        var first = server.Begin(job, station, 1, 17, "battery", Started);
        Require(first.Started, "create job");
        Require(server.Begin(job, station, 1, 17, "battery",
            Started.AddMilliseconds(200)).Duplicate, "repeat begin is idempotent");
        Require(Count(server, 17, "copper") == 1 && Count(server, 17, "acid") == 1,
            "reserve exactly one recipe");

        Require(!server.GrantMaterials(Guid.NewGuid(), 17, "copper", 100000),
            "outstanding reservations bound future refund capacity");
        Require(server.Cancel(job, 17, Started.AddSeconds(1)), "cancel before ready");
        Require(server.Cancel(job, 17, Started.AddSeconds(1)), "duplicate cancel");
        Require(Count(server, 17, "copper") == 3 && Count(server, 17, "acid") == 2,
            "cancel returns all original ingredients once");
        Require(!server.DeliverToInventory(job, 17, Started.AddSeconds(9)).Delivered,
            "cancel cannot grant an item");
    }

    private static void DestroyRefunds()
    {
        var server = NewMachine();
        Guid station = Guid.NewGuid(), job = Guid.NewGuid();
        server.RegisterStation(station, 1);
        server.GrantMaterials(Guid.NewGuid(), 5, "copper", 2);
        server.GrantMaterials(Guid.NewGuid(), 5, "acid", 1);
        server.TryOpen(station, 1, 5, Started, TimeSpan.FromSeconds(10));
        Require(server.Begin(job, station, 1, 5, "battery", Started).Started, "start");
        Require(server.DestroyStation(station, 1), "destroy running station");
        Require(Count(server, 5, "copper") == 2 && Count(server, 5, "acid") == 1,
            "destroy refunds unfinished job");
        Require(!server.DestroyStation(station, 1), "second destroy not applied");
        Require(Count(server, 5, "copper") == 2, "refund not replayed");
        Require(!server.DeliverToInventory(job, 5, Started.AddSeconds(20)).Delivered,
            "destroyed incomplete work yields nothing");
    }

    private static void DeliveryAfterCrashUsesSameItemId()
    {
        var server = NewMachine();
        Guid station = Guid.NewGuid(), job = Guid.NewGuid();
        server.RegisterStation(station, 1);
        server.GrantMaterials(Guid.NewGuid(), 9, "copper", 2);
        server.GrantMaterials(Guid.NewGuid(), 9, "acid", 1);
        server.TryOpen(station, 1, 9, Started, TimeSpan.FromSeconds(10));
        var original = server.Begin(job, station, 1, 9, "battery", Started);
        Require(original.Started, "start job");
        string folder = Path.Combine(Path.GetTempPath(), "bzc-economy-" +
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "fab.json");
        try
        {
            GameplayStore.SaveFabrication(path, server);
            var restored = GameplayStore.LoadFabrication(path, new[] { Recipe });
            Require(Count(restored, 9, "copper") == 0, "reservation survives restart");
            var pending = restored.Claim(job, 9, Started.AddSeconds(2));
            Require(pending.Allowed && pending.OutputItemId == original.OutputItemId,
                "stable output receipt after restart");
            GameplayStore.SaveFabrication(path, restored);
            var afterTicket = GameplayStore.LoadFabrication(path, new[] { Recipe });

            var delivered = afterTicket.DeliverToInventory(job, 9, Started.AddSeconds(3));
            Require(delivered.Delivered && !delivered.Duplicate &&
                    delivered.OutputItemId == original.OutputItemId,
                "pending ticket atomically credits exact item");
            var duplicate = afterTicket.DeliverToInventory(job, 9, Started.AddSeconds(3));
            Require(duplicate.Delivered && duplicate.Duplicate &&
                    duplicate.OutputItemId == original.OutputItemId,
                "repeated claim cannot credit another item");
            Require(afterTicket.SnapshotInventory(9)!.OwnedItems.Count == 1,
                "inventory only receives one output instance");
            GameplayStore.SaveFabrication(path, afterTicket);
            var final = GameplayStore.LoadFabrication(path, new[] { Recipe });
            Require(final.SnapshotInventory(9)!.OwnedItems.Count == 1 &&
                    final.SnapshotInventory(9)!.OwnedItems.ContainsKey(original.OutputItemId),
                "exact delivered item persists after multiple restarts");
            Require(final.DeliverToInventory(job, 9, Started.AddSeconds(5)).Duplicate,
                "replay after crash cannot mint another output");
            Require(!final.DeliverToInventory(job, 8, Started.AddSeconds(5)).Delivered,
                "other player cannot withdraw item");
            Require(!final.Begin(Guid.NewGuid(), station, 1, 9, "battery",
                    Started.AddSeconds(5)).Started, "ingredients cannot be spent twice");
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private static void SimultaneousCraftsCannotOverspend()
    {
        var server = NewMachine();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        server.RegisterStation(a, 1);
        server.RegisterStation(b, 1);
        server.GrantMaterials(Guid.NewGuid(), 13, "copper", 2);
        server.GrantMaterials(Guid.NewGuid(), 13, "acid", 1);
        server.TryOpen(a, 1, 13, Started, TimeSpan.FromSeconds(10));
        server.TryOpen(b, 1, 13, Started, TimeSpan.FromSeconds(10));
        var tasks = new[]
        {
            Task.Run(() => server.Begin(Guid.NewGuid(), a, 1, 13, "battery", Started)),
            Task.Run(() => server.Begin(Guid.NewGuid(), b, 1, 13, "battery", Started))
        };
        Task.WaitAll(tasks);
        Require(tasks.Count(x => x.Result.Started) == 1,
            "only one job can reserve a single set of materials");
    }

    private static void CorruptionIsRejected()
    {
        var server = NewMachine();
        Guid station = Guid.NewGuid();
        server.RegisterStation(station, 1);
        server.GrantMaterials(Guid.NewGuid(), 1, "copper", 2);
        server.GrantMaterials(Guid.NewGuid(), 1, "acid", 1);
        server.TryOpen(station, 1, 1, Started, TimeSpan.FromSeconds(10));
        server.Begin(Guid.NewGuid(), station, 1, 1, "battery", Started);
        var document = server.Snapshot();
        document.Inventories[0].Materials["copper"] = -1;
        bool rejected = false;
        try { FabricationState.Restore(document, new[] { Recipe }); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected, "negative material count rejected");

        document = server.Snapshot();
        document.Jobs[0].ReservedIngredients["copper"] = 999;
        rejected = false;
        try { FabricationState.Restore(document, new[] { Recipe }); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected, "tampered recipe reservation rejected");
    }
}
