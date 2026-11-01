using System;
using System.Collections.Generic;
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
    [BepInPlugin("dev.belowzerocoop.plugin", "BelowZeroCoop", "0.3.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const float HelloRetrySeconds = 2f;
        private const float PingIntervalSeconds = 1f;
        private const float PingTimeoutSeconds = 3f;
        private const float SessionTimeoutSeconds = 8f;

        private ConfigEntry<bool> enableProbe;
        private ConfigEntry<bool> scanGameHooksOnStartup;
        private ConfigEntry<bool> observeBuildingHooks;
        private ConfigEntry<bool> observeDeconstructionHooks;
        private BuildingHookObserver buildingObserver;
        private int lastDroppedBuildingEvents;
        private bool inGameSceneObserved;
        private ConfigEntry<bool> enableMovementPreview;
        private readonly MovementPreview movementPreview = new MovementPreview();
        private bool previewActive;
        private ConfigEntry<string> serverHost;
        private ConfigEntry<int> serverPort;

        private UdpClient socket;
        private string activeHost;
        private int activePort;
        private bool handshakeComplete;
        private uint assignedPeerId;
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
            scanGameHooksOnStartup = Config.Bind("Development", "ScanGameHooksOnStartup", false,
                "Read-only inspection of game method names. Logs status; never patches gameplay.");
            observeBuildingHooks = Config.Bind("Development", "ObserveBuildingHooks", false,
                "Install passive Harmony construction callbacks. Local logs only; never changes game/world/network.");
            observeDeconstructionHooks = Config.Bind("Development", "ObserveDeconstructionHooks", false,
                "Observe deconstruction method returns only. No proof of removal, persistence or networking.");
            enableMovementPreview = Config.Bind("Development", "EnableMovementPreview", false,
                "Enable sending local position and rendering temporary peer capsules. LAN debug only.");
            serverHost = Config.Bind("Network", "Host", "127.0.0.1",
                "IP address of the trusted BelowZeroCoop development server.");
            serverPort = Config.Bind("Network", "Port", 27021,
                "UDP port of the development server.");

            Logger.LogInfo("BelowZeroCoop loaded. No playable multiplayer features yet.");
            Logger.LogInfo("[GameTest] Startup: hookScan=" +
                scanGameHooksOnStartup.Value +
                " builds=" + observeBuildingHooks.Value +
                " deconstruction=" + observeDeconstructionHooks.Value +
                " udp=" + enableProbe.Value +
                " movementPreview=" + enableMovementPreview.Value);
            if (!enableProbe.Value)
                Logger.LogInfo("[GameTest] Offline-only diagnostics: no network session requested.");
            if (scanGameHooksOnStartup.Value)
            {
                // Only the actual game's Assembly-CSharp may satisfy a hook
                // target. Another mod defining a same-named type is irrelevant.
                var gameAssemblies = Array.FindAll(
                    AppDomain.CurrentDomain.GetAssemblies(),
                    assembly => assembly.GetName().Name == "Assembly-CSharp");
                var results = GameHookCatalog.Probe(
                    gameAssemblies, GameHookCatalog.Targets);
                int found = 0, missing = 0, ambiguous = 0;
                foreach (GameHookCatalog.Result item in results)
                {
                    if (item.Status == GameHookCatalog.ProbeStatus.Found)
                        found++;
                    else if (item.Status == GameHookCatalog.ProbeStatus.Ambiguous)
                        ambiguous++;
                    else
                        missing++;

                    Logger.LogInfo("[HookScan] " + item.Target.Area + " " +
                        item.Target.TypeName + "." + item.Target.MethodName + ": " +
                        item.Status + " — " + item.Detail);
                }
                Logger.LogInfo("[HookScan] Read-only scan: " + found +
                    " found, " + ambiguous + " ambiguous, " + missing +
                    " unavailable. No game methods were patched.");
            }

            if (observeBuildingHooks.Value || observeDeconstructionHooks.Value)
            {
                // Do not install anything unless the real game assembly is
                // present. The catalog may still show missing/ambiguous hooks.
                var gameAssembly = Array.Find(
                    AppDomain.CurrentDomain.GetAssemblies(),
                    assembly => assembly.GetName().Name == "Assembly-CSharp");
                if (gameAssembly == null)
                {
                    Logger.LogWarning("[BuildObserve] Assembly-CSharp unavailable; no patches installed.");
                }
                else
                {
                    try
                    {
                        buildingObserver = new BuildingHookObserver();
                        var targets = new List<BuildingHookObserver.Candidate>();
                        if (observeBuildingHooks.Value)
                            targets.AddRange(BuildingHookObserver.DefaultTargets());
                        if (observeDeconstructionHooks.Value)
                            targets.AddRange(BuildingHookObserver.DeconstructionTargets());
                        int installed = buildingObserver.Install(gameAssembly,
                            targets, line => Logger.LogInfo("[BuildObserve] " + line));
                        if (installed == 0)
                        {
                            buildingObserver.Dispose();
                            buildingObserver = null;
                        }
                        Logger.LogInfo("[BuildObserve] " + installed +
                            " passive construction/deconstruction callbacks active. No gameplay replication.");
                    }
                    catch (Exception ex)
                    {
                        if (buildingObserver != null)
                        {
                            buildingObserver.Dispose();
                            buildingObserver = null;
                        }
                        Logger.LogWarning("[BuildObserve] Disabled after installation failure: " +
                            ex.GetType().Name);
                    }
                }
            }
        }

        private void Update()
        {
            // Log first playable-scene detection once. This makes it possible
            // to distinguish BepInEx plugin loading from reaching the game
            // itself, without mutating a native save or creating any objects.
            if (!inGameSceneObserved && global::Player.main != null)
            {
                inGameSceneObserved = true;
                Logger.LogInfo("[GameTest] Player.main available; in-game lifecycle reached.");
            }

            // Handle diagnostic events on Unity's main thread and keep a
            // strict per-frame budget, completely separate from the UDP probe.
            if (buildingObserver != null)
            {
                foreach (var observed in buildingObserver.Drain(8))
                {
                    var game = observed.Game;
                    var pos = game.WorldTransform;
                    string location = pos == null ? "unknown" :
                        pos.X.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," +
                        pos.Y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," +
                        pos.Z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    Logger.LogInfo("[BuildObserve] " + observed.EventName +
                        " type=" + observed.RuntimeType +
                        " rawGameId=" + (game.ObservedObjectId ?? "unavailable") +
                        " idSource=" + game.ObjectIdSource +
                        " parentBaseId=" + (game.TargetBaseId ?? "unavailable") +
                        " tech=" + (game.TechType ?? "unknown") +
                        " stage=" + game.CompletionEvidence +
                        " origin=" + game.Origin +
                        " xyz=" + location +
                        " volatileInstance=" + observed.VolatileInstanceToken +
                        " utc=" + observed.ObservedUtc.ToString("o"));
                }
                int dropped = buildingObserver.DroppedCount;
                if (dropped != lastDroppedBuildingEvents)
                {
                    Logger.LogWarning("[BuildObserve] Bounded queue overflow: " +
                        dropped + " total old observations dropped.");
                    lastDroppedBuildingEvents = dropped;
                }
            }

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

        private bool SendMovement(MovementDatagram movement)
        {
            if (socket == null || !handshakeComplete)
                return false;

            try
            {
                byte[] bytes = MovementWireCodec.Encode(movement);
                socket.Send(bytes, bytes.Length);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                Logger.LogWarning("Movement preview skipped invalid local coordinates.");
                return false;
            }
            catch (SocketException ex)
            {
                Logger.LogWarning("Movement preview send error: " + ex.SocketErrorCode);
                CloseSocket();
                nextInitAttemptAt = Time.realtimeSinceStartup + 5f;
                return false;
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
                        if (handshakeComplete && enableMovementPreview.Value &&
                            movement.PeerId != assignedPeerId)
                            movementPreview.OnPosition(movement, receivedAt);
                        continue;
                    }

                    Packet packet;
                    if (!PacketCodec.TryDecode(bytes, out packet))
                        continue;
                    // UDP may deliver Welcome before HelloAck: remember the
                    // server identity regardless of their receive order.
                    if (packet.Type == PacketType.Welcome)
                    {
                        assignedPeerId = packet.Sequence;
                        continue;
                    }

                    if (packet.Type == PacketType.PeerLeft && handshakeComplete)
                    {
                        movementPreview.OnPeerLeft(packet.Sequence);
                        continue;
                    }

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
            if (buildingObserver != null)
            {
                buildingObserver.Dispose();
                buildingObserver = null;
            }
            CloseSocket();
        }

        private void CloseSocket()
        {
            if (socket != null)
            {
                // Best-effort clean departure. In case of abrupt termination,
                // the server still expires the session automatically.
                if (handshakeComplete)
                {
                    try
                    {
                        byte[] goodbye = PacketCodec.Encode(
                            new Packet(PacketType.Goodbye, NextSequence()));
                        socket.Send(goodbye, goodbye.Length);
                    }
                    catch (Exception)
                    {
                        // Shutdown must never throw from Unity callbacks.
                    }
                }

                socket.Close();
                socket = null;
            }

            assignedPeerId = 0;
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
