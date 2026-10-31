using System.Net;
using System.Net.Sockets;
using BelowZeroCoop.Protocol;

namespace BelowZeroCoop.Server;

internal static class Program
{
    private const int DefaultPort = 27021;

    public static async Task<int> Main(string[] args)
    {
        IPAddress bindAddress = IPAddress.Loopback;
        int port = DefaultPort;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--help" || args[i] == "-h")
            {
                ShowHelp();
                return 0;
            }

            if (args[i] == "--bind" && ++i < args.Length && IPAddress.TryParse(args[i], out IPAddress? parsed))
            {
                bindAddress = parsed;
                continue;
            }

            if (args[i] == "--port" && ++i < args.Length &&
                int.TryParse(args[i], out int parsedPort) && parsedPort >= 1 && parsedPort <= 65535)
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
        Console.WriteLine("BelowZeroCoop network probe listening on " + bindAddress + ":" + port);
        Console.WriteLine("Protocol v" + PacketCodec.WireVersion + ". CTRL+C to stop.");
        Console.WriteLine("This is not yet a playable game server.");
        if (!IPAddress.IsLoopback(bindAddress))
            Console.WriteLine("WARNING: no authentication yet. Use only on a trusted LAN; never expose this port to the internet.");

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
                Console.Error.WriteLine("UDP error: " + ex.SocketErrorCode);
                continue;
            }

            if (!PacketCodec.TryDecode(message.Buffer, out Packet packet))
                continue;

            if (packet.Type != PacketType.Ping)
                continue;

            byte[] reply = PacketCodec.Encode(new Packet(PacketType.Pong, packet.Sequence));
            try
            {
                await server.SendAsync(reply, message.RemoteEndPoint, shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                Console.Error.WriteLine("UDP send failed: " + ex.SocketErrorCode);
            }
        }

        return 0;
    }

    private static void ShowHelp()
    {
        Console.WriteLine("Usage: dotnet BelowZeroCoop.Server.dll [--bind 127.0.0.1] [--port 27021]");
        Console.WriteLine("Use --bind 0.0.0.0 only on a trusted private LAN.");
    }
}
