using BelowZeroCoop.Plugin;

static void Check(bool ok, string what)
{
    if (!ok) throw new Exception("Deconstruction regression: " + what);
}
var t = new DeconstructionEvidenceTracker(capacity: 2, maxAge: TimeSpan.FromSeconds(30));
var now = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
Check(!t.Request("", 1, now), "missing identity");
Check(!t.Request("room-a", 0, now), "invalid generation");
Check(!t.Request("room\nb", 1, now), "reject control characters in UID");
Check(!t.Request(" room-b ", 1, now), "reject padded UID");
Check(!t.ConfirmAbsent("room-a", 1, now), "no confirmation without progress");
Check(t.Request("room-a", 1, now), "request");
Check(t.GetPhase("room-a", 1, now) == DeconstructionEvidenceTracker.Phase.Requested,
    "request is not completion");
Check(!t.ConfirmAbsent("room-a", 1, now), "request is not deletion");
Check(t.Progress("room-a", 1, now.AddSeconds(1)), "progress");
Check(t.Cancel("room-a", 1), "cancelled work removed");
Check(t.GetPhase("room-a", 1, now.AddSeconds(2)) ==
    DeconstructionEvidenceTracker.Phase.Unknown, "cancel does not leave tombstone");
Check(t.Request("room-a", 1, now.AddSeconds(3)), "retry");
Check(t.Progress("room-a", 1, now.AddSeconds(4)), "retry progress");
Check(!t.Request("room-a", 1, now.AddSeconds(3)), "stale request rejected");
Check(!t.Request("room-a", 2, now.AddSeconds(3)), "stale newer generation rejected");
Check(t.GetPhase("room-a", 1, now.AddSeconds(4)) ==
    DeconstructionEvidenceTracker.Phase.Progressing,
    "stale observations cannot rewind state or generation");
Check(t.ConfirmAbsent("room-a", 1, now.AddSeconds(5)), "externally verified absence");
Check(!t.ConfirmAbsent("room-a", 1, now.AddSeconds(6)), "duplicate confirmation rejected");
Check(!t.Request("room-a", 1, now.AddSeconds(7)), "same generation cannot revive");
Check(!t.Progress("room-a", 1, now.AddSeconds(8)), "late progress cannot undo absence");
Check(!t.Cancel("room-a", 1), "confirmed absence not cancellable");
Check(t.Request("room-a", 2, now.AddSeconds(9)), "new generation allowed");
Check(!t.Progress("room-a", 1, now.AddSeconds(10)), "old generation cannot affect rebuild");
Check(t.GetPhase("room-a", 2, now.AddSeconds(10)) ==
    DeconstructionEvidenceTracker.Phase.Requested, "rebuilt object retains new state");
Check(t.GetPhase("room-a", 2, now.AddSeconds(41)) ==
    DeconstructionEvidenceTracker.Phase.Unknown, "stale evidence expires");
Check(t.Request("room-b", 1, now.AddSeconds(42)), "bounded entry one");
Check(t.Request("room-c", 1, now.AddSeconds(43)), "bounded entry two");
Check(t.Request("room-d", 1, now.AddSeconds(44)), "bounded entry three");
Check(t.GetPhase("room-b", 1, now.AddSeconds(44)) ==
    DeconstructionEvidenceTracker.Phase.Unknown, "oldest evidence evicted");
t.Clear();
Check(t.GetPhase("room-d", 1, now.AddSeconds(44)) ==
    DeconstructionEvidenceTracker.Phase.Unknown, "cleanup releases evidence");
Console.WriteLine("Deconstruction evidence safety tests passed.");
