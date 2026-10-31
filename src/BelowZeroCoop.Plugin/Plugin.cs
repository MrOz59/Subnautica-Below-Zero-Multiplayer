using System;
using System.Net;
using System.Net.Sockets;
using BepInEx;
using BepInEx.Configuration;
using BelowZeroCoop.Protocol;
using UnityEngine;

namespace BelowZeroCoop.Plugin
{
    [BepInPlugin("dev.belowzerocoop.plugin", "BelowZeroCoop", "0.1.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private ConfigEntry<bool> enableProbe;
        private ConfigEntry<string> serverHost;
        private ConfigEntry<int> serverPort;
        private UdpClient socket;
        private float nextPingAt;
        private uint sequence;
        private uint pending;
        private float sentAt;
        private bool wasConnected;
        private bool initializationFailed;

        private void Awake()
        {
            enableProbe = Config.Bind("Network", "EnableProbe", false,
                "Enables a development-only network ping to a trusted server.");
            serverHost = Config.Bind("Network", "Host", "127.0.0.1",
                "IP address of the BelowZeroCoop development server.");
            serverPort = Config.Bind("Network", "Port", 27021,
                "UDP port of the BelowZeroCoop development server.");

            Logger.LogInfo("BelowZeroCoop plugin loaded. Multiplayer gameplay is not implemented yet.");
        }

        private void Update()
        {
            if (!enableProbe.Value || initializationFailed)
                return;

            if (socket == null && !TryInitialize())
                return;

            float now = Time.realtimeSinceStartup;
            if (now >= nextPingAt)
            {
                nextPingAt = now + 2f;
                pending = ++sequence;
                if (pending == 0)
                    pending = ++sequence;

                byte[] data = PacketCodec.Encode(new Packet(PacketType.Ping, pending));
                try
                {
                    sentAt = now;
                    socket.Send(data, data.Length);
                }
                catch (SocketException ex)
                {
                    Logger.LogWarning("Ping failed: " + ex.SocketErrorCode);
                    CloseSocket();
                    return;
                }
            }

            // Poll only datagrams already buffered by the OS: never block a Unity frame.
            int processed = 0;
            try
            {
                while (socket.Available > 0 && processed++ < 8)
                {
                    IPEndPoint remote = null;
                    byte[] data = socket.Receive(ref remote);
                    Packet reply;
                    if (!PacketCodec.TryDecode(data, out reply) ||
                        reply.Type != PacketType.Pong || reply.Sequence != pending)
                        continue;

                    if (!wasConnected)
                    {
                        Logger.LogInfo("Development server replied. RTT approx " +
                                       Math.Round((now - sentAt) * 1000f) + " ms.");
                        wasConnected = true;
                    }
                }
            }
            catch (SocketException ex)
            {
                Logger.LogWarning("Receive failed: " + ex.SocketErrorCode);
                CloseSocket();
            }
        }

        private bool TryInitialize()
        {
            IPAddress address;
            if (!IPAddress.TryParse(serverHost.Value, out address) ||
                serverPort.Value <= 0 || serverPort.Value > 65535)
            {
                Logger.LogError("Invalid server IP or port. Disable EnableProbe or correct the config.");
                initializationFailed = true;
                return false;
            }

            try
            {
                socket = new UdpClient(address.AddressFamily);
                socket.Connect(address, serverPort.Value);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("Could not initialize UDP probe: " + ex.Message);
                initializationFailed = true;
                CloseSocket();
                return false;
            }
        }

        private void OnDestroy()
        {
            CloseSocket();
        }

        private void CloseSocket()
        {
            if (socket != null)
            {
                socket.Close();
                socket = null;
            }

            wasConnected = false;
        }
    }
}
