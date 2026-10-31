using BelowZeroCoop.Protocol;

internal static class NetworkImpairmentTests
{
    /// <summary>
    /// Regression for upstream #83: deterministic 300ms RTT / jitter / loss /
    /// duplicate / reorder simulation with no Unity or real game required.
    /// Receive-time ordering is deliberately different from send ordering.
    /// </summary>
    public static void Run()
    {
        var random = new Random(83);
        var datagrams = new List<(MovementSnapshot Sample, double Arrival)>();
        const int frameCount = 250;
        const double tick = 0.05;
        const double oneWayDelay = 0.15; // ~300 ms RTT before jitter

        for (int i = 1; i <= frameCount; ++i)
        {
            if (i % 10 == 0) // deterministic 10% packet loss
                continue;

            double sentAt = i * tick;
            double jitter = (random.NextDouble() - 0.5) * 0.10; // +/-50ms
            if (i % 23 == 0)
                jitter += 0.12; // occasionally much later than the next packet

            var snapshot = new MovementSnapshot(
                (uint)i, sentAt, (float)(i * 0.30), 0f, 0f);
            datagrams.Add((snapshot, sentAt + oneWayDelay + jitter));
            if (i % 11 == 0)
                datagrams.Add((snapshot, sentAt + oneWayDelay + jitter + 0.002));
        }

        datagrams.Sort((left, right) => left.Arrival.CompareTo(right.Arrival));
        var buffer = new MovementInterpolator();
        int accepted = 0, rejected = 0;
        double lastArrival = 0;

        foreach (var packet in datagrams)
        {
            bool used = buffer.Add(packet.Sample, packet.Arrival);
            if (used) accepted++;
            else rejected++;
            lastArrival = Math.Max(lastArrival, packet.Arrival);

            if (buffer.TrySample(packet.Arrival, out MovementSample position))
            {
                Check(float.IsFinite(position.X), "No nonfinite preview position");
                Check(position.X >= -1f && position.X <= 80f,
                    "Jitter and duplicates keep movement bounded");
            }
            Check(buffer.Count <= 32, "Network impairment cannot grow the history");
        }

        Check(accepted > 140, "Network stream remains usable despite packet loss");
        Check(rejected > 10, "Duplicates and reordering rejected");
        Check(buffer.TrySample(lastArrival + 0.4, out MovementSample final),
            "Final buffered position can be sampled");
        Check(final.X <= 80f && final.X >= 50f,
            "Extrapolation remains near the latest valid movement");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception("Network impairment regression failed: " + message);
    }
}
