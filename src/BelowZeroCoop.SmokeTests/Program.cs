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


var hello = PacketCodec.Encode(new Packet(PacketType.Hello, 42));
Check(PacketCodec.TryDecode(hello, out Packet decodedHello), "hello decoded");
Check(decodedHello.Type == PacketType.Hello && decodedHello.Sequence == 42, "hello roundtrip");
var ack = PacketCodec.Encode(new Packet(PacketType.HelloAck, 42));
Check(PacketCodec.TryDecode(ack, out Packet decodedAck), "ack decoded");
Check(decodedAck.Type == PacketType.HelloAck && decodedAck.Sequence == 42, "ack roundtrip");
foreach (byte invalid in new byte[] { 0, 5, 254, 255 })
{
    var malformed = (byte[])hello.Clone();
    malformed[5] = invalid;
    Check(!PacketCodec.TryDecode(malformed, out _), "unknown type rejected");
}
Check(!PacketCodec.TryDecode(new byte[PacketCodec.PacketLength + 1], out _),
      "oversized packet rejected");
// Simulate two clients with unrelated monotonic clocks and approximately
// 300 ms RTT (150 ms one-way propagation in this test stream).
var smoother = new MovementInterpolator();
Check(!smoother.TrySample(0, out _), "no sample before first snapshot");
Check(smoother.Add(new MovementSnapshot(1, 10.0, 0, 0, 0), 10.15),
      "first position accepted");
Check(smoother.Add(new MovementSnapshot(2, 10.05, 0.5f, 0, 0), 10.20),
      "second position accepted");
Check(!smoother.Add(new MovementSnapshot(1, 10.02, 300, 0, 0), 10.24),
      "out-of-order frame rejected");
Check(smoother.TrySample(10.40, out MovementSample position), "position sampled");
Check(position.X >= 0.5f && position.X <= 1.51f,
      "bounded interpolation/extrapolation");
Check(smoother.DelaySeconds >= 0.10 && smoother.DelaySeconds <= 0.35,
      "adaptive delay within bounds");

Check(!smoother.Add(new MovementSnapshot(3, double.NaN, 1, 0, 0), 10.30),
      "invalid sender timestamp rejected");
Check(!smoother.Add(new MovementSnapshot(3, 10.10, float.PositiveInfinity, 0, 0), 10.30),
      "invalid spatial coordinates rejected");
Check(smoother.Add(new MovementSnapshot(3, 10.10, 1000, 0, 0), 10.30),
      "teleport resets interpolation");
Check(smoother.TrySample(10.31, out position) && position.X == 1000,
      "teleport does not smear across the world");
Check(smoother.Count == 1, "teleport clears old position history");
Check(!smoother.Add(new MovementSnapshot(2, 10.05, 2, 0, 0), 10.35),
      "late pre-teleport snapshot cannot undo teleport");
Check(smoother.TrySample(10.36, out position) && position.X == 1000,
      "remote remains at teleport destination");

var wrapping = new MovementInterpolator();
Check(wrapping.Add(new MovementSnapshot(uint.MaxValue, 1, 0, 0, 0), 1.15),
      "sequence near wrap accepted");
Check(wrapping.Add(new MovementSnapshot(1, 1.05, 1, 0, 0), 1.20),
      "uint sequence wrap accepted");
for (uint i = 2; i < 200; i++)
{
    wrapping.Add(new MovementSnapshot(i, 1.05 + i * 0.05,
                                      i * 0.1f, 0, 0), 1.20 + i * 0.05);
}
Check(wrapping.Count <= 32, "snapshot memory bounded");
// Movement is a separate, strictly length-delimited packet family.
var movement = new MovementDatagram(0x12345678u, 7u, 12.345, 1.5f, -22.25f, 30f);
var binary = MovementWireCodec.Encode(movement);
Check(binary.Length == MovementWireCodec.Size, "movement datagram length");
Check(binary[6] == 0x78 && binary[9] == 0x12, "movement sequence little-endian");
Check(binary[10] == 7 && binary[11] == 0, "server peer id little-endian");
Check(MovementWireCodec.TryDecode(binary, out MovementDatagram decodedMovement),
      "movement decode");
Check(decodedMovement.Sequence == movement.Sequence &&
      decodedMovement.PeerId == movement.PeerId &&
      decodedMovement.SentAt == movement.SentAt &&
      decodedMovement.X == movement.X &&
      decodedMovement.Y == movement.Y &&
      decodedMovement.Z == movement.Z, "movement roundtrip");
Check(!PacketCodec.TryDecode(binary, out _), "control decoder rejects movement frame");
Check(!MovementWireCodec.TryDecode(new byte[10], out _), "movement rejects short payload");
Check(!MovementWireCodec.TryDecode(new byte[400], out _), "movement rejects oversized payload");
var badMagic = (byte[])binary.Clone();
badMagic[0] = 0;
Check(!MovementWireCodec.TryDecode(badMagic, out _), "movement rejects invalid magic");
var badVersion = (byte[])binary.Clone();
badVersion[4] = 200;
Check(!MovementWireCodec.TryDecode(badVersion, out _), "movement rejects future protocol");
var badType = (byte[])binary.Clone();
badType[5] = 50;
Check(!MovementWireCodec.TryDecode(badType, out _), "movement rejects invalid type");
Check(!MovementWireCodec.TryDecode(null!, out _), "movement rejects null");
bool invalidRejected = false;
try
{
    MovementWireCodec.Encode(new MovementDatagram(1, 0, 0, float.NaN, 0, 0));
}
catch (ArgumentOutOfRangeException)
{
    invalidRejected = true;
}
Check(invalidRejected, "movement encode rejects NaN");
invalidRejected = false;
try
{
    MovementWireCodec.Encode(new MovementDatagram(1, 0, 0, 100001f, 0, 0));
}
catch (ArgumentOutOfRangeException)
{
    invalidRejected = true;
}
Check(invalidRejected, "movement encode enforces world bounds");
Console.WriteLine("Protocol, movement and interpolation smoke tests passed.");
