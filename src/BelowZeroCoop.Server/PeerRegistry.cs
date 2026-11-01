using System.Net;
using BelowZeroCoop.Protocol;

namespace BelowZeroCoop.Server;

/// <summary>
/// Ephemeral LAN development sessions. This is NOT authentication.
/// A session is associated with an observed UDP endpoint, never a client-supplied ID.
/// </summary>
internal sealed class PeerRegistry
{
    public const int MaxPeers = 8;
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(30);

    private const double MinMovementIntervalSeconds = 0.025; // max ~40 accepted Hz
    private const double MaxMovementSpeed = 60.0; // hard cap until game-aware validation exists

    private readonly Dictionary<IPEndPoint, Peer> peers = new();
    private uint nextId = 1;

    public IEnumerable<Peer> Members => peers.Values;

    public bool TryHello(IPEndPoint endpoint, DateTime utcNow, out Peer peer)
    {
        if (peers.TryGetValue(endpoint, out peer!))
        {
            peer.LastSeenUtc = utcNow;
            // A retry of Hello must not invalidate a successfully established
            // movement sequence or allow old snapshots to be replayed.
            return true;
        }

        if (peers.Count >= MaxPeers)
        {
            peer = null!;
            return false;
        }

        peer = new Peer(nextId++, endpoint, utcNow);
        if (nextId == 0)
            nextId = 1;
        peers.Add(endpoint, peer);
        return true;
    }

    public bool TryGet(IPEndPoint endpoint, DateTime utcNow, out Peer peer)
    {
        if (!peers.TryGetValue(endpoint, out peer!))
            return false;
        peer.LastSeenUtc = utcNow;
        return true;
    }

    public bool TryAcceptMovement(IPEndPoint endpoint, MovementDatagram packet,
                                  DateTime utcNow, out Peer peer)
    {
        peer = null!;
        // A rejected or replayed movement packet must not extend the session.
        // Otherwise an inactive peer can keep an occupied slot alive forever
        // by sending malformed movement with a valid UDP endpoint.
        if (packet.PeerId != 0 || !peers.TryGetValue(endpoint, out var sender))
            return false;

        if (sender.HasMovement)
        {
            if (unchecked((int)(packet.Sequence - sender.LastSequence)) <= 0 ||
                packet.SentAt <= sender.LastSentTime)
                return false;

            double elapsed = (utcNow - sender.LastMovementUtc).TotalSeconds;
            if (elapsed < MinMovementIntervalSeconds)
                return false;

            double senderElapsed = packet.SentAt - sender.LastSentTime;
            // Large unexplained jumps are discarded; legit teleports require
            // an authenticated gameplay event in a future protocol revision.
            double dx = packet.X - sender.LastX;
            double dy = packet.Y - sender.LastY;
            double dz = packet.Z - sender.LastZ;
            double distanceSquared = dx * dx + dy * dy + dz * dz;
            // Do not let a malicious or broken sender claim hours of elapsed
            // time to bypass the per-snapshot maximum travel distance.
            double boundedInterval = Math.Min(1.0, Math.Max(senderElapsed, elapsed));
            double maxDistance = MaxMovementSpeed * boundedInterval + 2.0;
            if (distanceSquared > maxDistance * maxDistance)
                return false;
        }

        // Refresh liveness only after *all* movement validation succeeds.
        sender.LastSeenUtc = utcNow;
        sender.HasMovement = true;
        sender.LastSequence = packet.Sequence;
        sender.LastSentTime = packet.SentAt;
        sender.LastMovementUtc = utcNow;
        sender.LastX = packet.X;
        sender.LastY = packet.Y;
        sender.LastZ = packet.Z;
        peer = sender;
        return true;
    }

    public bool TryRemove(IPEndPoint endpoint, out Peer peer)
    {
        if (!peers.TryGetValue(endpoint, out peer!))
            return false;

        return peers.Remove(endpoint);
    }

    /// <summary>
    /// Called from the server receive loop AND its idle tick. Returns exact
    /// peer identities so remote debug objects can be despawned immediately.
    /// </summary>
    public List<Peer> Prune(DateTime utcNow)
    {
        var removed = new List<Peer>();
        foreach (Peer peer in peers.Values)
        {
            if (utcNow - peer.LastSeenUtc > Expiry)
                removed.Add(peer);
        }

        foreach (Peer peer in removed)
            peers.Remove(peer.Endpoint);

        return removed;
    }

}

internal sealed class Peer
{
    public uint Id { get; }
    public IPEndPoint Endpoint { get; }
    public DateTime LastSeenUtc { get; set; }
    public bool HasMovement { get; set; }
    public uint LastSequence { get; set; }
    public double LastSentTime { get; set; }
    public DateTime LastMovementUtc { get; set; }
    public float LastX { get; set; }
    public float LastY { get; set; }
    public float LastZ { get; set; }

    public Peer(uint id, IPEndPoint endpoint, DateTime utcNow)
    {
        Id = id;
        Endpoint = endpoint;
        LastSeenUtc = utcNow;
    }
}
