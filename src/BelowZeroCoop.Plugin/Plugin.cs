using System;
using System.Net;
using System.Net.Sockets;
using BepInEx;
using BepInEx.Configuration;
using BelowZeroCoop.Protocol;
using UnityEngine;

namespace BelowZeroCoop.Plugin
{
    /// <summary>
    /// Minimal BepInEx entry point and non-blocking network connectivity probe.
    /// Game state replication is intentionally not implemented yet.
    /// </summary>
    [BepInPlugin("dev.belowzerocoop.plugin", "BelowZeroCoop", "0.2.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const float HelloRetrySeconds = 2f;
        private const float PingIntervalSeconds = 1f;
        private const float PingTimeoutSeconds = 3f;
        private const float SessionTimeoutSeconds = 8f;

        private ConfigEntry<bool> enableProbe;
        private ConfigEntry<bool> enableMovementPreview;
        private readonly MovementPreview movementPreview = new MovementPreview();
        private bool previewActive;
        private ConfigEntry<string> serverHost;
        private ConfigEntry<int> serverPort;

        private UdpClient socket;
        private string activeHost;
        private int activePort;
        private bool handshakeComplete;
        private bool awaitingPong;
        private uint sequence;
        private uint lastHelloSequence;
        private uint lastPingSequence;
        private int missedPongs;
        private int successfulPongs;
        private float helloRetryAt;
        private float nextPingAt;
        private float pingSentAt;
        private float lastResponseAt;
        private float nextInitAttemptAt;
        private float smoothedRttMs = -1f;
        private float jitterMs;

        private void Awake()
        {
            enableProbe = Config.Bind("Network", "EnableProbe", false,
                "Enable a development-only UDP connectivity probe. Not multiplayer.");
            enableMovementPreview = Config.Bind("Development", "EnableMovementPreview", false,
                "Enable sending local position and rendering temporary peer capsules. LAN debug only.");
            serverHost = Config.Bind("Network", "Host", "127.0.0.1",
                "IP address of the trusted BelowZeroCoop development server.");
            serverPort = Config.Bind("Network", "Port", 27021,
                "UDP port of the development server.");

            Logger.LogInfo("BelowZeroCoop loaded. No playable multiplayer features yet.");
        }

        private void Update()
        {
            if (!enableProbe.Value)
            {
                if (socket != null)
                    CloseSocket();
                if (previewActive)
                {
                    movementPreview.Reset();
                    previewActive = false;
                }
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (socket != null &&
                (activeHost != serverHost.Value || activePort != serverPort.Value))
            {
                CloseSocket();
                nextInitAttemptAt = now;
            }

            if (socket == null && now >= nextInitAttemptAt && !TryInitialize(now))
                return;
            if (socket == null)
                return;

            // All socket operations are performed on Unity's main thread.
            // Available prevents receiving from blocking the frame indefinitely.
            ReadBufferedPackets();

            now = Time.realtimeSinceStartup;
            if (!handshakeComplete)
            {
                if (now >= helloRetryAt)
                {
                    lastHelloSequence = NextSequence();
                    if (Send(PacketType.Hello, lastHelloSequence))
                        helloRetryAt = now + HelloRetrySeconds;
                }
                return;
            }

            if (now - lastResponseAt >= SessionTimeoutSeconds)
            {
                Logger.LogWarning("Development server timed out; restarting hello.");
                handshakeComplete = false;
                awaitingPong = false;
                movementPreview.Reset();
                helloRetryAt = now;
                return;
            }

            if (awaitingPong && now - pingSentAt >= PingTimeoutSeconds)
            {
                missedPongs++;
                awaitingPong = false;
                nextPingAt = now;

                if (missedPongs >= 3)
                {
                    Logger.LogWarning("Three network probe timeouts; renegotiating.");
                    handshakeComplete = false;
                    movementPreview.Reset();
                    helloRetryAt = now;
                    return;
                }
            }

            if (!awaitingPong && now >= nextPingAt)
            {
                uint seq = NextSequence();
                if (Send(PacketType.Ping, seq))
                {
                    lastPingSequence = seq;
                    pingSentAt = Time.realtimeSinceStartup;
                    nextPingAt = pingSentAt + PingIntervalSeconds;
                    awaitingPong = true;
                }
            }

            if (enableMovementPreview.Value)
            {
                previewActive = true;
                movementPreview.Tick(now, SendMovement);
            }
            else if (previewActive)
            {
                movementPreview.Reset();
                previewActive = false;
            }
        }

        private void SendMovement(MovementDatagram movement)
        {
            if (socket == null || !handshakeComplete)
                return;

            try
            {
                byte[] bytes = MovementWireCodec.Encode(movement);
                socket.Send(bytes, bytes.Length);
            }
            catch (SocketException ex)
            {
                Logger.LogWarning("Movement preview send error: " + ex.SocketErrorCode);
                CloseSocket();
                nextInitAttemptAt = Time.realtimeSinceStartup + 5f;
            }
        }

        private uint NextSequence()
        {
            sequence++;
            if (sequence == 0)
                sequence++;
            return sequence;
        }

        private bool Send(PacketType type, uint number)
        {
            try
            {
                byte[] payload = PacketCodec.Encode(new Packet(type, number));
                socket.Send(payload, payload.Length);
                return true;
            }
            catch (SocketException ex)
            {
                Logger.LogWarning("Development probe send error: " + ex.SocketErrorCode);
                CloseSocket();
                nextInitAttemptAt = Time.realtimeSinceStartup + 5f;
                return false;
            }
        }

        private void ReadBufferedPackets()
        {
            try
            {
                int budget = 8;
                while (socket != null && budget-- > 0 && socket.Available > 0)
                {
                    IPEndPoint remote = null;
                    byte[] bytes = socket.Receive(ref remote);
                    float receivedAt = Time.realtimeSinceStartup;
                    MovementDatagram movement;
                    if (MovementWireCodec.TryDecode(bytes, out movement))
                    {
                        if (handshakeComplete && enableMovementPreview.Value)
                            movementPreview.OnPosition(movement, receivedAt);
                        continue;
                    }

                    Packet packet;
                    if (!PacketCodec.TryDecode(bytes, out packet))
                        continue;
                    if (packet.Type == PacketType.HelloAck &&
                        !handshakeComplete && packet.Sequence == lastHelloSequence)
                    {
                        handshakeComplete = true;
                        awaitingPong = false;
                        missedPongs = 0;
                        nextPingAt = receivedAt;
                        lastResponseAt = receivedAt;
                        movementPreview.Reset();
                        Logger.LogInfo("BelowZeroCoop development server handshake complete.");
                    }
                    else if (packet.Type == PacketType.Pong &&
                             handshakeComplete && awaitingPong &&
                             packet.Sequence == lastPingSequence)
                    {
                        awaitingPong = false;
                        missedPongs = 0;
                        lastResponseAt = receivedAt;

                        float rttMs = Mathf.Max(0f, (receivedAt - pingSentAt) * 1000f);
                        if (smoothedRttMs < 0f)
                        {
                            smoothedRttMs = rttMs;
                        }
                        else
                        {
                            float variation = Mathf.Abs(rttMs - smoothedRttMs);
                            jitterMs = Mathf.Lerp(jitterMs, variation, 0.125f);
                            smoothedRttMs = Mathf.Lerp(smoothedRttMs, rttMs, 0.125f);
                        }

                        successfulPongs++;
                        if (successfulPongs == 1 || successfulPongs % 10 == 0)
                        {
                            Logger.LogInfo("UDP RTT " + Mathf.RoundToInt(rttMs) +
                                           " ms; smoothed " + Mathf.RoundToInt(smoothedRttMs) +
                                           " ms; jitter " + Mathf.RoundToInt(jitterMs) + " ms.");
                        }
                    }
                }
            }
            catch (SocketException ex)
            {
                Logger.LogWarning("Development probe receive error: " + ex.SocketErrorCode);
                CloseSocket();
                nextInitAttemptAt = Time.realtimeSinceStartup + 5f;
            }
        }

        private bool TryInitialize(float now)
        {
            IPAddress address;
            if (!IPAddress.TryParse(serverHost.Value, out address) ||
                serverPort.Value <= 0 || serverPort.Value > 65535)
            {
                Logger.LogError("Invalid development server IP or UDP port.");
                nextInitAttemptAt = now + 5f;
                return false;
            }

            try
            {
                socket = new UdpClient(address.AddressFamily);
                socket.Connect(address, serverPort.Value);
                activeHost = serverHost.Value;
                activePort = serverPort.Value;
                helloRetryAt = now;
                handshakeComplete = false;
                awaitingPong = false;
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Development probe startup failed: " + ex.Message);
                CloseSocket();
                nextInitAttemptAt = now + 5f;
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

            activeHost = null;
            activePort = 0;
            handshakeComplete = false;
            awaitingPong = false;
            successfulPongs = 0;
            missedPongs = 0;
            smoothedRttMs = -1f;
            jitterMs = 0f;
            movementPreview.Reset();
        }
    }
}
