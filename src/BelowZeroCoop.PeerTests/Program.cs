using System.Net;
using BelowZeroCoop.Protocol;
using BelowZeroCoop.Server;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("Peer regression failed: " + name);
}

DateTime start = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
var sessions = new PeerRegistry();
var endpoint = new IPEndPoint(IPAddress.Loopback, 45123);
Check(sessions.TryHello(endpoint, start, out Peer peer), "join");
Check(peer.LastSeenUtc == start, "initial activity timestamp");

// Rejected client-supplied peer IDs must not renew the session.
Check(!sessions.TryAcceptMovement(endpoint,
    new MovementDatagram(1, peer.Id, 1.0, 0, 0, 0),
    start.AddSeconds(10), out _), "spoofed movement rejected");
Check(peer.LastSeenUtc == start, "spoofed movement leaves liveness unchanged");

// A valid movement extends the session, but a replay does not.
DateTime acceptedAt = start.AddSeconds(11);
Check(sessions.TryAcceptMovement(endpoint,
    new MovementDatagram(1, 0, 1.0, 0, 0, 0),
    acceptedAt, out _), "valid movement accepted");
Check(peer.LastSeenUtc == acceptedAt, "valid movement refreshes liveness");
Check(!sessions.TryAcceptMovement(endpoint,
    new MovementDatagram(1, 0, 1.0, 0, 0, 0),
    start.AddSeconds(25), out _), "replay rejected");
Check(peer.LastSeenUtc == acceptedAt, "replay does not extend liveness");

// Invalid traffic cannot occupy a slot past the normal idle expiry.
Check(sessions.Prune(acceptedAt + PeerRegistry.Expiry + TimeSpan.FromMilliseconds(1))
    .Count == 1, "inactive peer expires despite rejected packets");
Check(!sessions.TryGet(endpoint, start.AddMinutes(2), out _),
    "expired endpoint removed");

// Legitimate pings still count as activity.
Check(sessions.TryHello(endpoint, start.AddMinutes(3), out peer), "rejoin");
DateTime pingAt = start.AddMinutes(3).AddSeconds(15);
Check(sessions.TryGet(endpoint, pingAt, out _), "ping accepted");
Check(peer.LastSeenUtc == pingAt, "ping refreshes liveness");
Check(sessions.Prune(pingAt.AddSeconds(20)).Count == 0,
    "recently active peer remains connected");
Console.WriteLine("Peer liveness regression tests passed.");
