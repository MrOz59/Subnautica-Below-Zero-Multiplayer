namespace Subnautica.Client.MonoBehaviours.General
{
    using System.Collections.Generic;
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
