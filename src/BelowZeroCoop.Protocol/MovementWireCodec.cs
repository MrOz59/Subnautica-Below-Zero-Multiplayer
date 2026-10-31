using System;

namespace BelowZeroCoop.Protocol
{
    /// <summary>
    /// Dedicated fixed-length movement datagram for the LAN development prototype.
    /// The server owns PeerId allocation. Clients MUST use PeerId=0 on upload.
    /// No credentials or game binaries are part of the wire format.
    /// </summary>
    public struct MovementDatagram
    {
        public uint Sequence { get; }
        public uint PeerId { get; }
        public double SentAt { get; }
        public float X { get; }
        public float Y { get; }
        public float Z { get; }

        public MovementDatagram(uint sequence, uint peerId, double sentAt,
                                float x, float y, float z)
        {
            Sequence = sequence;
            PeerId = peerId;
            SentAt = sentAt;
            X = x;
            Y = y;
            Z = z;
        }

        public MovementSnapshot ToSnapshot()
        {
            return new MovementSnapshot(Sequence, SentAt, X, Y, Z);
        }
    }

    public static class MovementWireCodec
    {
        public const byte MovementType = 5;
        public const int Size = 34;
        public const float CoordinateLimit = 100000f;

        public static byte[] Encode(MovementDatagram sample)
        {
            if (!IsValid(sample))
                throw new ArgumentOutOfRangeException(nameof(sample), "Invalid movement datagram");

            byte[] data = new byte[Size];
            data[0] = (byte)'B';
            data[1] = (byte)'Z';
            data[2] = (byte)'C';
            data[3] = (byte)'0';
            data[4] = PacketCodec.WireVersion;
            data[5] = MovementType;
            WriteU32(data, 6, sample.Sequence);
            WriteU32(data, 10, sample.PeerId);
            WriteU64(data, 14, unchecked((ulong)BitConverter.DoubleToInt64Bits(sample.SentAt)));
            WriteU32(data, 22, FloatBits(sample.X));
            WriteU32(data, 26, FloatBits(sample.Y));
            WriteU32(data, 30, FloatBits(sample.Z));
            return data;
        }

        public static bool TryDecode(byte[] data, out MovementDatagram sample)
        {
            sample = default(MovementDatagram);
            if (data == null || data.Length != Size ||
                data[0] != (byte)'B' || data[1] != (byte)'Z' ||
                data[2] != (byte)'C' || data[3] != (byte)'0' ||
                data[4] != PacketCodec.WireVersion || data[5] != MovementType)
                return false;

            var parsed = new MovementDatagram(
                ReadU32(data, 6),
                ReadU32(data, 10),
                BitConverter.Int64BitsToDouble(unchecked((long)ReadU64(data, 14))),
                BitsToFloat(ReadU32(data, 22)),
                BitsToFloat(ReadU32(data, 26)),
                BitsToFloat(ReadU32(data, 30)));
            if (!IsValid(parsed))
                return false;

            sample = parsed;
            return true;
        }

        private static bool IsValid(MovementDatagram sample)
        {
            return sample.Sequence != 0 &&
                   !double.IsNaN(sample.SentAt) && !double.IsInfinity(sample.SentAt) &&
                   sample.SentAt >= 0 &&
                   ValidCoordinate(sample.X) && ValidCoordinate(sample.Y) &&
                   ValidCoordinate(sample.Z);
        }

        private static bool ValidCoordinate(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) &&
                   Math.Abs(value) <= CoordinateLimit;
        }

        private static uint FloatBits(float value)
        {
            return unchecked((uint)BitConverter.ToInt32(BitConverter.GetBytes(value), 0));
        }

        private static float BitsToFloat(uint bits)
        {
            return BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)bits)), 0);
        }

        private static void WriteU32(byte[] bytes, int offset, uint value)
        {
            for (int i = 0; i < 4; ++i)
                bytes[offset + i] = (byte)(value >> (i * 8));
        }

        private static uint ReadU32(byte[] bytes, int offset)
        {
            uint value = 0;
            for (int i = 0; i < 4; ++i)
                value |= (uint)bytes[offset + i] << (i * 8);
            return value;
        }

        private static void WriteU64(byte[] bytes, int offset, ulong value)
        {
            for (int i = 0; i < 8; ++i)
                bytes[offset + i] = (byte)(value >> (i * 8));
        }

        private static ulong ReadU64(byte[] bytes, int offset)
        {
            ulong value = 0;
            for (int i = 0; i < 8; ++i)
                value |= (ulong)bytes[offset + i] << (i * 8);
            return value;
        }
    }
}
