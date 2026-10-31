using System.Net;
using System.Net.Sockets;
using BelowZeroCoop.Protocol;

namespace BelowZeroCoop.Server;

/// <summary>
/// Local development handshake/Ping responder. Not yet an authenticated
/// multiplayer server and should not be exposed to untrusted networks.
/// </summary>
internal static class Program
{
    private const int DefaultPort = 27021;

    public static async Task<int> Main(string[] args)
    {
        IPAddress bindAddress = IPAddress.Loopback;
        int port = DefaultPort;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--help" or "-h")
            {
                ShowHelp();
                return 0;
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

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        using var server = new UdpClient(new IPEndPoint(bindAddress, port));
        var sessions = new PeerRegistry();
        Console.WriteLine("BelowZeroCoop development server at " + bindAddress + ":" + port);
        Console.WriteLine("Protocol v" + PacketCodec.WireVersion + ". CTRL+C to stop.");
        Console.WriteLine("LAN-only prototype: versioned probes and relay of debug position snapshots.");
        Console.WriteLine("No authentication or actual co-op gameplay yet.");
        if (!IPAddress.IsLoopback(bindAddress))
            Console.WriteLine("WARNING: untrusted network exposure is NOT supported.");

        while (!shutdown.IsCancellationRequested)
        {
            UdpReceiveResult message;
            try
            {
                message = await server.ReceiveAsync(shutdown.Token);
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

            // Protocol probes are exactly ten bytes. Keep the movement codec
            // independent so future state packets can use their own layouts.
            if (PacketCodec.TryDecode(message.Buffer, out Packet packet))
            {
                Packet? reply = null;
                if (packet.Type == PacketType.Hello &&
                    sessions.TryHello(message.RemoteEndPoint, now, out _))
                {
                    reply = new Packet(PacketType.HelloAck, packet.Sequence);
                }
                else if (packet.Type == PacketType.Ping &&
                         sessions.TryGet(message.RemoteEndPoint, now, out _))
                {
                    reply = new Packet(PacketType.Pong, packet.Sequence);
                }

                if (reply.HasValue)
                {
                    try
                    {
                        byte[] response = PacketCodec.Encode(reply.Value);
                        await server.SendAsync(response, message.RemoteEndPoint, shutdown.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (SocketException ex)
                    {
                        Console.Error.WriteLine("UDP send error: " + ex.SocketErrorCode);
                    }
                }

                continue;
            }

            if (!MovementWireCodec.TryDecode(message.Buffer, out MovementDatagram movement) ||
                !sessions.TryAcceptMovement(message.RemoteEndPoint, movement, now, out Peer sender))
                continue;

            // A client never chooses its own public peer ID. The server stamps
            // the assigned ID and fans out only to other registered endpoints.
            byte[] relay = MovementWireCodec.Encode(new MovementDatagram(
                movement.Sequence, sender.Id, movement.SentAt,
                movement.X, movement.Y, movement.Z));

            foreach (Peer recipient in sessions.Members)
            {
                if (recipient.Id == sender.Id)
                    continue;

                try
                {
                    await server.SendAsync(relay, recipient.Endpoint, shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                    return 0;
                }
                catch (SocketException ex)
                {
                    Console.Error.WriteLine("UDP relay error: " + ex.SocketErrorCode);
                }
            }
        }

        return 0;
    }

    private static void ShowHelp()
    {
        Console.WriteLine("Usage: dotnet BelowZeroCoop.Server.dll [--bind 127.0.0.1] [--port 27021]");
        Console.WriteLine("This is an unauthenticated localhost development probe, not a public multiplayer server.");
    }
}
