using System;
using System.Collections.Generic;
using BelowZeroCoop.Protocol;
using UnityEngine;

namespace BelowZeroCoop.Plugin
{
    /// <summary>
    /// Opt-in scene probe: publishes the real local player's position and
    /// renders peer capsules. This is NOT a player avatar or gameplay authority.
    /// Runs only on Unity's main thread and owns its temporary scene objects.
    /// </summary>
    internal sealed class MovementPreview
    {
        private const float SendInterval = 0.05f; // 20 Hz while moving
        private const float StationaryHeartbeat = 1f;
        private const float MovementDeadzoneSquared = 0.0025f;
        private const float PeerExpiry = 10f;
        private const int MaxVisualPeers = 8;

        private sealed class RemotePeer
        {
            internal GameObject Visual;
            internal readonly MovementInterpolator Buffer = new MovementInterpolator();
            internal float LastReceivedAt;
        }

        private readonly Dictionary<uint, RemotePeer> remotePeers =
            new Dictionary<uint, RemotePeer>();

        private uint sequence;
        private float nextSendAt;
        private float lastSentAt;
        private Vector3 lastPosition;
        private bool hasLastPosition;

        internal void Reset()
        {
            foreach (RemotePeer peer in remotePeers.Values)
            {
                if (peer.Visual != null)
                    UnityEngine.Object.Destroy(peer.Visual);
            }

            remotePeers.Clear();
            // Keep our outgoing sequence monotonic across network reconnects.
            // The same server-side endpoint may still have recent snapshots.
            nextSendAt = 0f;
            hasLastPosition = false;
        }

        internal void OnPosition(MovementDatagram packet, float receivedAt)
        {
            // Never instantiate debug objects in the main menu.
            if (global::Player.main == null || packet.PeerId == 0)
                return;

            RemotePeer peer;
            if (!remotePeers.TryGetValue(packet.PeerId, out peer))
            {
                if (remotePeers.Count >= MaxVisualPeers)
                    return;

                peer = new RemotePeer();
                remotePeers.Add(packet.PeerId, peer);
            }

            if (!peer.Buffer.Add(packet.ToSnapshot(), receivedAt))
                return;

            peer.LastReceivedAt = receivedAt;
            if (peer.Visual == null)
                peer.Visual = CreateVisual(packet.PeerId);
        }

        internal void OnPeerLeft(uint peerId)
        {
            RemotePeer peer;
            if (!remotePeers.TryGetValue(peerId, out peer))
                return;

            if (peer.Visual != null)
                UnityEngine.Object.Destroy(peer.Visual);
            remotePeers.Remove(peerId);
        }

        internal void Tick(float now, Func<MovementDatagram, bool> transmit)
        {
            var player = global::Player.main;
            if (player == null)
            {
                if (remotePeers.Count > 0)
                    Reset();
                return;
            }

            if (now >= nextSendAt)
            {
                nextSendAt = now + SendInterval;
                Vector3 position = player.transform.position;
                float dx = hasLastPosition ? position.x - lastPosition.x : 0f;
                float dy = hasLastPosition ? position.y - lastPosition.y : 0f;
                float dz = hasLastPosition ? position.z - lastPosition.z : 0f;
                bool moved = !hasLastPosition ||
                             dx * dx + dy * dy + dz * dz > MovementDeadzoneSquared;
                bool heartbeat = !hasLastPosition || now - lastSentAt >= StationaryHeartbeat;

                if (moved || heartbeat)
                {
                    sequence++;
                    if (sequence == 0)
                        sequence++;

                    // A failed send must be retried on a later tick instead
                    // of silently updating the last-sent baseline.
                    bool accepted = transmit(new MovementDatagram(sequence, 0, now,
                                                   position.x, position.y, position.z));
                    if (accepted)
                    {
                        lastPosition = position;
                        lastSentAt = now;
                        hasLastPosition = true;
                    }
                }
            }

            List<uint> expired = null;
            foreach (KeyValuePair<uint, RemotePeer> item in remotePeers)
            {
                RemotePeer peer = item.Value;
                if (now - peer.LastReceivedAt >= PeerExpiry)
                {
                    if (expired == null)
                        expired = new List<uint>();
                    expired.Add(item.Key);
                    continue;
                }

                MovementSample sample;
                if (peer.Buffer.TrySample(now, out sample) && peer.Visual != null)
                {
                    peer.Visual.transform.position = new Vector3(sample.X, sample.Y, sample.Z);
                }
            }

            if (expired != null)
            {
                foreach (uint id in expired)
                {
                    RemotePeer peer = remotePeers[id];
                    if (peer.Visual != null)
                        UnityEngine.Object.Destroy(peer.Visual);
                    remotePeers.Remove(id);
                }
            }
        }

        private static GameObject CreateVisual(uint id)
        {
            GameObject capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            capsule.name = "BelowZeroCoop peer " + id;
            // Prevent physics interactions. A real remote avatar requires
            // game-specific prefabs, animations and vehicle state handling.
            Collider collider = capsule.GetComponent<Collider>();
            if (collider != null)
                UnityEngine.Object.Destroy(collider);
            capsule.transform.localScale = new Vector3(0.55f, 0.95f, 0.55f);
            return capsule;
        }
    }
}
