using System.Net;
using System.Net.Sockets;
using BelowZeroCoop.Protocol;

namespace BelowZeroCoop.Server;

/// <summary>
/// Trusted-LAN movement development service, NOT an authenticated multiplayer
/// server. The real Unity game/Steam login is never replaced by this process.
/// </summary>
internal static class Program
{
    private const int DefaultPort = 27021;
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMilliseconds(500);

    public static async Task<int> Main(string[] args)
    {
        IPAddress bindAddress = IPAddress.Loopback;
        int port = DefaultPort;
        bool allowLan = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--help" or "-h")
            {
                ShowHelp();
                return 0;
            }

            if (args[i] == "--allow-lan")
            {
                allowLan = true;
                continue;
            }

            if (args[i] == "--bind" && i + 1 < args.Length &&
                IPAddress.TryParse(args[++i], out IPAddress? parsed))
            {
                bindAddress = parsed;
                continue;
            }

            if (args[i] == "--port" && i + 1 < args.Length &&
                int.TryParse(args[++i], out int parsedPort) &&
                parsedPort is >= 1 and <= 65535)
            {
                port = parsedPort;
                continue;
            }

            Console.Error.WriteLine("Invalid argument. Use --help for options.");
            return 2;
        }

        if (!IPAddress.IsLoopback(bindAddress) && !allowLan)
        {
            Console.Error.WriteLine("Refusing non-loopback listener without explicit --allow-lan.");
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        using var server = new UdpClient(new IPEndPoint(bindAddress, port));
        var sessions = new PeerRegistry();
        Console.WriteLine($"BelowZeroCoop LAN dev server at {bindAddress}:{port}");
        Console.WriteLine($"Wire protocol {PacketCodec.WireVersion}; maximum {PeerRegistry.MaxPeers} peers.");
        Console.WriteLine("UNAUTHENTICATED development-only service. Do not expose to the internet.");

        while (!shutdown.IsCancellationRequested)
        {
            // Wake even without UDP traffic: disconnected players must be
            // removed and announced instead of lingering forever.
            UdpReceiveResult message;
            using var receiveWindow = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            receiveWindow.CancelAfter(MaintenanceInterval);
            try
            {
                message = await server.ReceiveAsync(receiveWindow.Token);
            }
            catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
            {
                await AnnounceDepartures(server, sessions,
                    sessions.Prune(DateTime.UtcNow), shutdown.Token);
                continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                Console.Error.WriteLine("UDP receive error: " + ex.SocketErrorCode);
                continue;
            }

            DateTime now = DateTime.UtcNow;
            await AnnounceDepartures(server, sessions, sessions.Prune(now), shutdown.Token);

            if (PacketCodec.TryDecode(message.Buffer, out Packet packet))
            {
                if (packet.Type == PacketType.Hello &&
                    sessions.TryHello(message.RemoteEndPoint, now, out Peer joining))
                {
                    // The request sequence is echoed for retry matching. Welcome
                    // carries the server-assigned ID, not client-supplied data.
                    if (!await SendControl(server, message.RemoteEndPoint,
                            new Packet(PacketType.HelloAck, packet.Sequence), shutdown.Token))
                        continue;

                    if (!await SendControl(server, message.RemoteEndPoint,
                            new Packet(PacketType.Welcome, joining.Id), shutdown.Token))
                        continue;

                    // A Hello retry still gets ACK and Welcome, but cannot
                    // amplify into an unbounded stream of full snapshots.
                    // Retrying after the cooldown permits baseline recovery
                    // if the first datagrams were lost.
                    if (!sessions.TryBeginBaseline(joining, now))
                        continue;

                    // A late joiner gets an immediate one-frame baseline for
                    // existing peers. Normal streaming continues at ~20 Hz.
                    foreach (Peer peer in sessions.Members)
                    {
                        if (peer.Id == joining.Id || !peer.HasMovement)
                            continue;

                        var snapshot = new MovementDatagram(
                            peer.LastSequence, peer.Id, peer.LastSentTime,
                            peer.LastX, peer.LastY, peer.LastZ);
                        await SendPayload(server, MovementWireCodec.Encode(snapshot),
                            joining.Endpoint, shutdown.Token);
                    }
                }
                else if (packet.Type == PacketType.Goodbye &&
                         sessions.TryRemove(message.RemoteEndPoint, out Peer departed))
                {
                    await AnnounceDepartures(server, sessions,
                        new List<Peer> { departed }, shutdown.Token);
                }
                else if (packet.Type == PacketType.Ping &&
                         sessions.TryGet(message.RemoteEndPoint, now, out _))
                {
                    await SendControl(server, message.RemoteEndPoint,
                        new Packet(PacketType.Pong, packet.Sequence), shutdown.Token);
                }

                continue;
            }

            if (!MovementWireCodec.TryDecode(message.Buffer, out MovementDatagram movement) ||
                !sessions.TryAcceptMovement(message.RemoteEndPoint, movement, now, out Peer sender))
                continue;

            // Server-assigned identity prevents a client from selecting whose
            // debug avatar it controls. This is NOT cryptographic identity.
            byte[] relay = MovementWireCodec.Encode(new MovementDatagram(
                movement.Sequence, sender.Id, movement.SentAt,
                movement.X, movement.Y, movement.Z));

            foreach (Peer peer in sessions.Members)
            {
                if (peer.Id != sender.Id)
                    await SendPayload(server, relay, peer.Endpoint, shutdown.Token);
            }
        }

        return 0;
    }

    private static async Task AnnounceDepartures(
        UdpClient server, PeerRegistry sessions, List<Peer> departed,
        CancellationToken cancellationToken)
    {
        foreach (Peer leaving in departed)
        {
            byte[] message = PacketCodec.Encode(new Packet(PacketType.PeerLeft, leaving.Id));
            foreach (Peer recipient in sessions.Members)
                await SendPayload(server, message, recipient.Endpoint, cancellationToken);
        }
    }

    private static Task<bool> SendControl(
        UdpClient server, IPEndPoint endpoint, Packet packet,
        CancellationToken cancellationToken)
    {
        return SendPayload(server, PacketCodec.Encode(packet), endpoint, cancellationToken);
    }

    private static async Task<bool> SendPayload(
        UdpClient server, byte[] data, IPEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            await server.SendAsync(data, endpoint, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine("UDP send error to " + endpoint + ": " + ex.SocketErrorCode);
            return false;
        }
    }

    private static void ShowHelp()
    {
        Console.WriteLine("Usage: BelowZeroCoop.Server [--bind 127.0.0.1] [--port 27021] [--allow-lan]");
        Console.WriteLine("Only use --allow-lan on a trusted network. Never forward the port online.");
    }
}
