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
        RemoveExpired(utcNow);
        if (peers.TryGetValue(endpoint, out peer!))
        {
            peer.LastSeenUtc = utcNow;
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
        RemoveExpired(utcNow);
        if (!peers.TryGetValue(endpoint, out peer!))
            return false;
        peer.LastSeenUtc = utcNow;
        return true;
    }

    public bool TryAcceptMovement(IPEndPoint endpoint, MovementDatagram packet,
                                  DateTime utcNow, out Peer peer)
    {
        peer = null!;
        if (packet.PeerId != 0 || !TryGet(endpoint, utcNow, out Peer sender))
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
            double maxDistance = MaxMovementSpeed * Math.Max(senderElapsed, elapsed) + 2.0;
            if (distanceSquared > maxDistance * maxDistance)
                return false;
        }

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

    private void RemoveExpired(DateTime utcNow)
    {
        foreach (IPEndPoint endpoint in peers.Where(item => utcNow - item.Value.LastSeenUtc > Expiry)
                                             .Select(item => item.Key).ToArray())
            peers.Remove(endpoint);
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
