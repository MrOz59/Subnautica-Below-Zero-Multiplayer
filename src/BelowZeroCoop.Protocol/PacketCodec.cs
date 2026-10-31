using System;

namespace BelowZeroCoop.Protocol
{
    /// <summary>
    /// Tiny versioned wire format used for the initial connection probe.
    /// This is original code; no third-party multiplayer packet layouts are reused.
    /// </summary>
    public enum PacketType : byte
    {
        Ping = 1,
        Pong = 2,
        Hello = 3,
        HelloAck = 4
    }

    public struct Packet
    {
        public PacketType Type { get; private set; }
        public uint Sequence { get; private set; }

        public Packet(PacketType type, uint sequence)
        {
            Type = type;
            Sequence = sequence;
        }
    }

    public static class PacketCodec
    {
        public const int WireVersion = 1;
        public const int PacketLength = 10;

        private static bool IsSupported(PacketType type)
        {
            return type == PacketType.Ping || type == PacketType.Pong ||
                   type == PacketType.Hello || type == PacketType.HelloAck;
        }

        public static byte[] Encode(Packet packet)
        {
            if (!IsSupported(packet.Type))
                throw new ArgumentOutOfRangeException(nameof(packet), "Unsupported packet type");

            uint sequence = packet.Sequence;
            return new byte[]
            {
                (byte)'B', (byte)'Z', (byte)'C', (byte)'0',
                WireVersion, (byte)packet.Type,
                (byte)sequence,
                (byte)(sequence >> 8),
                (byte)(sequence >> 16),
                (byte)(sequence >> 24)
            };
        }

        public static bool TryDecode(byte[] data, out Packet packet)
        {
            packet = default(Packet);
            if (data == null || data.Length != PacketLength)
                return false;

            if (data[0] != (byte)'B' || data[1] != (byte)'Z' ||
                data[2] != (byte)'C' || data[3] != (byte)'0' ||
                data[4] != WireVersion)
                return false;

            PacketType type = (PacketType)data[5];
            if (!IsSupported(type))
                return false;

            uint sequence = (uint)(data[6] |
                                   (data[7] << 8) |
                                   (data[8] << 16) |
                                   (data[9] << 24));
            packet = new Packet(type, sequence);
            return true;
        }
    }
}
