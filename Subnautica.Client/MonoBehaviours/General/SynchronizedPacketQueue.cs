namespace Subnautica.Client.MonoBehaviours.General
{
    using System.Collections.Generic;
    using Subnautica.Network.Models.Server;
    using Subnautica.Network.Models.Core;

    /// <summary>
    /// LiteNetLib may invoke receive callbacks off the Unity main thread.
    /// Every operation shares one lock, including initial-sync cleanup.
    /// </summary>
    public sealed class SynchronizedPacketQueue
    {
        private readonly Queue<NetworkPacket> packets = new Queue<NetworkPacket>();
        private readonly object gate = new object();

        public int Count
        {
            get
            {
                lock (gate)
                {
                    return packets.Count;
                }
            }
        }

        public void Enqueue(NetworkPacket packet)
        {
            lock (gate)
            {
                packets.Enqueue(packet);

                // Do not ever discard construction, inventory, animations, or
                // reliable events. If the main thread stalls long enough to queue
                // 64+ player snapshots, keep just the newest two per player.
                if (packets.Count > 64 && packet is PlayerUpdatedArgs)
                {
                    CompactOldPlayerSnapshots();
                }
            }
        }

        private void CompactOldPlayerSnapshots()
        {
            NetworkPacket[] pending = packets.ToArray();
            var countByPlayer = new Dictionary<byte, int>();
            bool[] keep = new bool[pending.Length];
            int removed = 0;

            for (int i = pending.Length - 1; i >= 0; i--)
            {
                var movement = pending[i] as PlayerUpdatedArgs;
                if (movement == null || movement.PacketOwnerId == 0)
                {
                    keep[i] = true;
                    continue;
                }

                int count;
                countByPlayer.TryGetValue(movement.PacketOwnerId, out count);
                keep[i] = count < 2;
                countByPlayer[movement.PacketOwnerId] = count + 1;
                if (!keep[i])
                {
                    removed++;
                }
            }

            if (removed == 0)
            {
                return;
            }

            packets.Clear();
            for (int i = 0; i < pending.Length; i++)
            {
                if (keep[i])
                {
                    packets.Enqueue(pending[i]);
                }
            }
        }

        public bool TryDequeue(out NetworkPacket packet)
        {
            lock (gate)
            {
                if (packets.Count == 0)
                {
                    packet = null;
                    return false;
                }

                packet = packets.Dequeue();
                return true;
            }
        }

        public NetworkPacket Dequeue()
        {
            lock (gate)
            {
                return packets.Dequeue();
            }
        }

        public void Clear()
        {
            lock (gate)
            {
                packets.Clear();
            }
        }
    }
}
