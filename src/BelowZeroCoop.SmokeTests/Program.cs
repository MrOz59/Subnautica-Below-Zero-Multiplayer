using BelowZeroCoop.Protocol;

static void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception("FAIL: " + message);
}

var encoded = PacketCodec.Encode(new Packet(PacketType.Ping, 0x12345678u));
Check(encoded.Length == PacketCodec.PacketLength, "packet size");
Check(encoded[6] == 0x78 && encoded[9] == 0x12, "little endian sequence");
Check(PacketCodec.TryDecode(encoded, out Packet decoded), "decode valid packet");
Check(decoded.Type == PacketType.Ping && decoded.Sequence == 0x12345678u, "roundtrip");

var unknown = (byte[])encoded.Clone();
unknown[5] = 255;
Check(!PacketCodec.TryDecode(unknown, out _), "unknown type rejected");

var invalidVersion = (byte[])encoded.Clone();
invalidVersion[4] = 99;
Check(!PacketCodec.TryDecode(invalidVersion, out _), "version rejected");

var truncated = new byte[5];
Check(!PacketCodec.TryDecode(truncated, out _), "truncated packet rejected");
Check(!PacketCodec.TryDecode(null!, out _), "null packet rejected");

var pong = PacketCodec.Encode(new Packet(PacketType.Pong, uint.MaxValue));
Check(PacketCodec.TryDecode(pong, out Packet decodedPong), "pong parsed");
Check(decodedPong.Type == PacketType.Pong && decodedPong.Sequence == uint.MaxValue,
      "sequence max supported");

Console.WriteLine("Protocol smoke tests passed.");
