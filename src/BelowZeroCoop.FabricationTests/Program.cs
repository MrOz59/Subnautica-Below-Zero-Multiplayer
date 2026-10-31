using BelowZeroCoop.Server;
using System.Text.Json;
using System.Text.Json.Nodes;

static void Check(bool ok, string message)
{
    if (!ok)
        throw new Exception("Fabricator regression failed: " + message);
}

var recipe = new FabricationRecipe("sample_battery", "battery_instance", TimeSpan.FromSeconds(2));
var recipes = new[] { recipe };
DateTime start = new(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);
TimeSpan lease = TimeSpan.FromSeconds(5);

// #3/#76: a completed craft and its item collection cannot leave the UI stuck.
{
    var machine = new FabricationState(recipes);
    Guid station = Guid.NewGuid();
    Guid firstJobId = Guid.NewGuid();
    Check(machine.RegisterStation(station, 1), "station registered");
    Check(machine.RegisterStation(station, 1), "re-register is idempotent");
    Check(machine.TryOpen(station, 1, 10, start, lease), "first user opens fabricator");
    Check(!machine.TryOpen(station, 1, 20, start, lease),
          "fabricator cannot have two UI owners at once");

    FabricationBeginResult first = machine.Begin(
        firstJobId, station, 1, 10, recipe.Id, start);
    Check(first.Started && first.OutputItemId != Guid.Empty, "craft started");
    FabricationBeginResult retry = machine.Begin(
        firstJobId, station, 1, 10, recipe.Id, start.AddMilliseconds(100));
    Check(retry.Started && retry.Duplicate &&
          retry.OutputItemId == first.OutputItemId,
          "repeated start returns original output identity");
    Check(!machine.Begin(firstJobId, station, 1, 20, recipe.Id, start).Started,
          "a different actor cannot reuse the job id");
    Check(!machine.TryOpen(station, 1, 20, start.AddSeconds(1), lease),
          "machine remains occupied while crafting");
    Check(!machine.Claim(firstJobId, 10, start.AddSeconds(1)).Allowed,
          "item cannot be retrieved before completion");

    Check(machine.Advance(start.AddSeconds(2)) == 1, "craft completes exactly once");
    Check(machine.Advance(start.AddSeconds(2)) == 0,
          "repeated completion callback is idempotent");
    // Critical #76 regression: job Ready is not an exclusive UI lease.
    Check(machine.TryOpen(station, 1, 20, start.AddSeconds(2), lease),
          "second user can reopen before the first claims the output");

    FabricationClaimResult output = machine.Claim(
        firstJobId, 10, start.AddSeconds(2));
    Check(output.Allowed && output.NewlyClaimed &&
          output.OutputItemId == first.OutputItemId &&
          output.OutputType == recipe.OutputType, "owner claims unique output once");
    FabricationClaimResult duplicate = machine.Claim(
        firstJobId, 10, start.AddSeconds(2));
    Check(duplicate.Allowed && !duplicate.NewlyClaimed &&
          duplicate.OutputItemId == first.OutputItemId &&
          duplicate.Reason == "pending_delivery",
          "repeated claim returns the same pending delivery ticket");
    Check(!machine.ConfirmDelivery(firstJobId, 20, first.OutputItemId),
          "another actor cannot acknowledge someone else's item");
    Check(!machine.ConfirmDelivery(firstJobId, 10, Guid.NewGuid()),
          "an unrelated inventory item cannot complete the delivery");
    Check(machine.ConfirmDelivery(firstJobId, 10, first.OutputItemId),
          "acknowledge the durable item delivery");
    Check(machine.ConfirmDelivery(firstJobId, 10, first.OutputItemId),
          "duplicate delivery acknowledgements are idempotent");
    Check(machine.Claim(firstJobId, 10, start.AddSeconds(2)).Reason ==
          "already_claimed", "confirmed delivery can never be prepared again");
    Check(!machine.Claim(firstJobId, 20, start.AddSeconds(2)).Allowed,
          "other player cannot claim someone else's completed work");

    // Critical #3 regression: receiving output cannot block future interaction.
    Guid secondJobId = Guid.NewGuid();
    FabricationBeginResult second = machine.Begin(
        secondJobId, station, 1, 20, recipe.Id, start.AddSeconds(2));
    Check(second.Started && second.OutputItemId != first.OutputItemId,
          "fabrication restarts after previous output delivery");
    Check(machine.Cancel(secondJobId, 20, start.AddSeconds(2)),
          "active job can be cancelled");
    Check(machine.Cancel(secondJobId, 20, start.AddSeconds(2)),
          "duplicate cancellation harmless");
    Check(!machine.Claim(secondJobId, 20, start.AddSeconds(10)).Allowed,
          "cancelled work never releases an item");
    Check(machine.TryOpen(station, 1, 10, start.AddSeconds(3), lease),
          "cancelled job releases station for next interaction");
}

// A client disconnect must not permanently lock the Fabricator (#76).
{
    var machine = new FabricationState(recipes);
    Guid station = Guid.NewGuid();
    machine.RegisterStation(station, 1);
    Check(machine.TryOpen(station, 1, 7, start, lease), "actor opens");
    Check(machine.ReleaseActor(7) == 1, "disconnect releases UI lease");
    Check(machine.ReleaseActor(7) == 0, "repeat disconnect harmless");
    Check(machine.TryOpen(station, 1, 8, start, lease),
          "new player can enter after owner disconnect");

    Guid task = Guid.NewGuid();
    var job = machine.Begin(task, station, 1, 8, recipe.Id, start);
    Check(job.Started, "craft started before disconnect");
    machine.ReleaseActor(8);
    Check(machine.Advance(start.AddSeconds(2)) == 1,
          "craft still finishes with no connected actor");
    Check(machine.TryOpen(station, 1, 9, start.AddSeconds(2), lease),
          "new user can open after disconnected owner's work completes");
    var pending = machine.Claim(task, 8, start.AddSeconds(2));
    Check(pending.NewlyClaimed, "owner retains output after reconnect");
    Check(machine.ConfirmDelivery(task, 8, pending.OutputItemId),
          "returned owner's inventory may acknowledge the delivery");
    Check(!machine.Claim(task, 9, start.AddSeconds(2)).Allowed,
          "other actor never receives reserved result");
}

// A timed-out abandoned UI lease must not block future users.
{
    var machine = new FabricationState(recipes);
    Guid station = Guid.NewGuid();
    machine.RegisterStation(station, 1);
    Check(machine.TryOpen(station, 1, 1, start, TimeSpan.FromSeconds(2)),
          "lease acquired");
    Check(machine.TryOpen(station, 1, 2, start.AddSeconds(3), lease),
          "new owner can open after timeout");
    Check(!machine.TryClose(station, 1), "old actor cannot close new owner's UI");
    Check(machine.TryClose(station, 2), "active actor can close UI");
    Check(machine.TryOpen(station, 1, 1, start.AddSeconds(3), lease),
          "closing UI releases station");
}

// Station destruction cancels unfinished work, increments generations on rebuild,
// and rejects stale old-generation interactions.
{
    var machine = new FabricationState(recipes);
    Guid station = Guid.NewGuid();
    machine.RegisterStation(station, 1);
    machine.TryOpen(station, 1, 1, start, lease);
    Guid jobId = Guid.NewGuid();
    Check(machine.Begin(jobId, station, 1, 1, recipe.Id, start).Started,
          "old-generation craft started");
    Check(machine.DestroyStation(station, 1), "fabricator destroyed");
    Check(!machine.Claim(jobId, 1, start.AddSeconds(5)).Allowed,
          "destroyed machine cannot produce cancelled work");
    Check(!machine.RegisterStation(station, 1),
          "stale generation cannot revive a destroyed fabricator");
    Check(machine.RegisterStation(station, 2),
          "reconstruction increases station generation");
    Check(!machine.TryOpen(station, 1, 1, start.AddSeconds(5), lease),
          "stale generation cannot receive interaction");
    Check(machine.TryOpen(station, 2, 1, start.AddSeconds(5), lease),
          "reconstructed fabricator is usable");

    Guid replacementJob = Guid.NewGuid();
    Check(machine.Begin(replacementJob, station, 2, 1, recipe.Id,
                        start.AddSeconds(5)).Started,
          "new generation can start independent job");
    Check(machine.Advance(start.AddSeconds(7)) == 1, "reconstructed craft completed");
    Check(machine.Claim(replacementJob, 1, start.AddSeconds(7)).NewlyClaimed,
          "replacement craft grants its new item");
}

// Jobs and outputs survive a local restart. UI locks must not survive.
{
    var machine = new FabricationState(recipes);
    Guid station = Guid.NewGuid();
    Guid jobId = Guid.NewGuid();
    machine.RegisterStation(station, 1);
    machine.TryOpen(station, 1, 123, start, lease);
    var started = machine.Begin(jobId, station, 1, 123, recipe.Id, start);
    string dir = Path.Combine(Path.GetTempPath(),
        "bzc-crafting-" + Guid.NewGuid().ToString("N"));
    string file = Path.Combine(dir, "crafting.json");
    try
    {
        GameplayStore.SaveFabrication(file, machine);
        var recovered = GameplayStore.LoadFabrication(file, recipes);
        Check(!recovered.TryOpen(station, 1, 456, start.AddSeconds(1), lease),
              "running work still keeps the station busy after restart");
        Check(recovered.Advance(start.AddSeconds(2)) == 1,
              "recovered incomplete work eventually becomes ready");
        Check(recovered.TryOpen(station, 1, 456, start.AddSeconds(2), lease),
              "recovered station reopens once work completes");
        var claim = recovered.Claim(jobId, 123, start.AddSeconds(2));
        Check(claim.NewlyClaimed && claim.OutputItemId == started.OutputItemId,
              "pending output remains claimable with original identity");

        // Simulate crash after preparing an outbox ticket but before
        // inventory acknowledgement: same stable item ID remains recoverable.
        GameplayStore.SaveFabrication(file, recovered);
        var second = GameplayStore.LoadFabrication(file, recipes);
        var pending = second.Claim(jobId, 123, start.AddSeconds(3));
        Check(pending.Allowed && !pending.NewlyClaimed &&
              pending.Reason == "pending_delivery" &&
              pending.OutputItemId == started.OutputItemId,
              "unacknowledged result is recoverable without a new item identity");

        Check(second.ConfirmDelivery(jobId, 123, pending.OutputItemId),
              "acknowledgement finalizes pending delivery");
        GameplayStore.SaveFabrication(file, second);
        var third = GameplayStore.LoadFabrication(file, recipes);
        Check(third.Claim(jobId, 123, start.AddSeconds(4)).Reason ==
              "already_claimed", "completed delivery stays final across restarts");
        Check(File.Exists(file + ".bak"), "previous crafting snapshot is backed up");

        File.WriteAllText(file, "{}");
        bool invalid = false;
        try { GameplayStore.LoadFabrication(file, recipes); }
        catch (InvalidDataException) { invalid = true; }
        Check(invalid, "incomplete crafting save rejected rather than reset");

        var legacy = JsonNode.Parse(File.ReadAllText(file + ".bak"))!;
        legacy["Jobs"]!.AsArray()[0]!.AsObject().Remove("Status");
        File.WriteAllText(file, legacy.ToJsonString());
        bool missingStatus = false;
        try { GameplayStore.LoadFabrication(file, recipes); }
        catch (InvalidDataException) { missingStatus = true; }
        Check(missingStatus, "missing job lifecycle state rejected");

        var backup = GameplayStore.LoadFabrication(file + ".bak", recipes);
        Check(backup.Claim(jobId, 123, start.AddSeconds(3)).Allowed,
              "explicit backup can recover a prepared delivery ticket");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

// Two threads cannot claim two UI leases or create parallel jobs on one station.
{
    var machine = new FabricationState(recipes);
    Guid station = Guid.NewGuid();
    machine.RegisterStation(station, 1);
    bool[] opened = await Task.WhenAll(Enumerable.Range(1, 12).Select(i =>
        Task.Run(() => machine.TryOpen(station, 1, (uint)i, start, lease))));
    Check(opened.Count(x => x) == 1, "only one concurrent UI owner");
}

// Repeated or invalid snapshot identities must be rejected before any state is used.
{
    var machine = new FabricationState(recipes);
    Guid station = Guid.NewGuid();
    machine.RegisterStation(station, 1);
    var document = machine.Snapshot();
    document.Stations.Add(document.Stations[0].Copy());
    bool duplicateRejected = false;
    try { FabricationState.Restore(document, recipes); }
    catch (InvalidDataException) { duplicateRejected = true; }
    Check(duplicateRejected, "duplicate station identities rejected");

    bool recipeRejected = false;
    try { new FabricationState(new[] { recipe, recipe }); }
    catch (ArgumentException) { recipeRejected = true; }
    Check(recipeRejected, "duplicate trusted recipe definitions rejected");
}

Console.WriteLine("Fabricator lifecycle regressions (#3/#76) passed.");
