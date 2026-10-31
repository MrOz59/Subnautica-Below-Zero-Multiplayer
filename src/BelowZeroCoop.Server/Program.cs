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
    private const int MaxSessions = 32;
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(30);

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
        var sessions = new Dictionary<IPEndPoint, DateTime>();
        Console.WriteLine("BelowZeroCoop development server at " + bindAddress + ":" + port);
        Console.WriteLine("Protocol v" + PacketCodec.WireVersion + ". CTRL+C to stop.");
        Console.WriteLine("Development probe only: no player data and no authentication.");
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

            if (!PacketCodec.TryDecode(message.Buffer, out Packet packet))
                continue;

            DateTime now = DateTime.UtcNow;
            Packet? reply = null;

            if (packet.Type == PacketType.Hello)
            {
                foreach (var old in sessions.Where(x => now - x.Value > SessionTimeout)
                                            .Select(x => x.Key).ToArray())
                    sessions.Remove(old);

                if (sessions.Count < MaxSessions || sessions.ContainsKey(message.RemoteEndPoint))
                {
                    sessions[message.RemoteEndPoint] = now;
                    reply = new Packet(PacketType.HelloAck, packet.Sequence);
                }
            }
            else if (packet.Type == PacketType.Ping &&
                     sessions.TryGetValue(message.RemoteEndPoint, out DateTime lastSeen) &&
                     now - lastSeen <= SessionTimeout)
            {
                sessions[message.RemoteEndPoint] = now;
                reply = new Packet(PacketType.Pong, packet.Sequence);
            }

            if (!reply.HasValue)
                continue;

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

        return 0;
    }

    private static void ShowHelp()
    {
        Console.WriteLine("Usage: dotnet BelowZeroCoop.Server.dll [--bind 127.0.0.1] [--port 27021]");
        Console.WriteLine("This is an unauthenticated localhost development probe, not a public multiplayer server.");
    }
}
