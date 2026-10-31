using System;
using System.Collections.Generic;

namespace BelowZeroCoop.Protocol
{
    /// <summary>
    /// Immutable network sample with a sender-local monotonic timestamp.
    /// Position only for the initial proof-of-concept; animation, vehicle and
    /// orientation replication are separate milestones.
    /// </summary>
    public struct MovementSnapshot
    {
        public uint Sequence { get; }
        public double SentAt { get; }
        public float X { get; }
        public float Y { get; }
        public float Z { get; }

        public MovementSnapshot(uint sequence, double sentAt, float x, float y, float z)
        {
            Sequence = sequence;
            SentAt = sentAt;
            X = x;
            Y = y;
            Z = z;
        }
    }

    public struct MovementSample
    {
        public float X { get; }
        public float Y { get; }
        public float Z { get; }

        public MovementSample(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    /// <summary>
    /// Pure .NET movement smoothing; no Unity or game code dependencies.
    /// Sender and receiver clocks do not need to be synchronized.
    /// The rendering delay is measured in *addition* to network propagation.
    /// </summary>
    public sealed class MovementInterpolator
    {
        private const int Capacity = 32;
        private const double TeleportDistanceSquared = 75.0 * 75.0;
        private const double MaxExtrapolationSeconds = 0.10;
        private const double MaxExtrapolationSpeed = 35.0;

        private readonly List<MovementSnapshot> snapshots = new List<MovementSnapshot>(Capacity);
        private bool hasSequence;
        private uint lastSequence;
        private double clockOffset;
        private double jitter;
        private double lastArrival;
        private double highLatencySeconds;
        private double lastSamplingTime = double.NaN;

        public double DelaySeconds { get; private set; } = 0.12;
        public int Count => snapshots.Count;

        public void Reset()
        {
            snapshots.Clear();
            hasSequence = false;
            lastSequence = 0;
            clockOffset = 0;
            jitter = 0;
            lastArrival = 0;
            highLatencySeconds = 0;
            lastSamplingTime = double.NaN;
            DelaySeconds = 0.12;
        }

        public bool Add(MovementSnapshot sample, double arrivedAt)
        {
            if (sample.Sequence == 0 || !Finite(sample.SentAt) || !Finite(arrivedAt) ||
                !Finite(sample.X) || !Finite(sample.Y) || !Finite(sample.Z))
                return false;

            // Signed difference handles normal uint wrap-around, rejecting stale
            // unreliable UDP frames as long as gaps stay under 2^31 packets.
            if (hasSequence && unchecked((int)(sample.Sequence - lastSequence)) <= 0)
                return false;

            if (snapshots.Count != 0)
            {
                MovementSnapshot previous = snapshots[snapshots.Count - 1];
                if (sample.SentAt <= previous.SentAt)
                    return false;

                if (DistanceSquared(previous, sample) >= TeleportDistanceSquared)
                    Reset();
            }

            double observedOffset = arrivedAt - sample.SentAt;
            if (snapshots.Count == 0)
            {
                clockOffset = observedOffset;
            }
            else
            {
                if (observedOffset < clockOffset)
                {
                    clockOffset += (observedOffset - clockOffset) * 0.25;
                    highLatencySeconds = 0;
                }
                else if (observedOffset > clockOffset + 0.06)
                {
                    highLatencySeconds += Clamp(arrivedAt - lastArrival, 0, 0.2);
                    if (highLatencySeconds >= 1.5)
                        clockOffset += Math.Min(0.01, observedOffset - clockOffset);
                }
                else
                {
                    highLatencySeconds = 0;
                }

                MovementSnapshot previous = snapshots[snapshots.Count - 1];
                double sendGap = sample.SentAt - previous.SentAt;
                if (sendGap > 0 && sendGap < 0.5)
                {
                    double arrivalGap = arrivedAt - lastArrival;
                    double deviation = Math.Min(Math.Abs(arrivalGap - sendGap), 0.5);
                    jitter += (deviation - jitter) * 0.2;
                }
            }

            lastArrival = arrivedAt;
            lastSequence = sample.Sequence;
            hasSequence = true;
            snapshots.Add(sample);
            if (snapshots.Count > Capacity)
                snapshots.RemoveAt(0);

            return true;
        }

        public bool TrySample(double now, out MovementSample position)
        {
            position = default(MovementSample);
            if (!Finite(now) || snapshots.Count == 0)
                return false;

            double targetDelay = Clamp(0.10 + jitter * 3.0, 0.10, 0.35);
            double step = Finite(lastSamplingTime) ? Clamp(now - lastSamplingTime, 0, 0.1) : 0;
            DelaySeconds = MoveTowards(DelaySeconds, targetDelay, step * 0.20);
            lastSamplingTime = now;

            double renderAt = now - clockOffset - DelaySeconds;
            while (snapshots.Count > 2 && snapshots[1].SentAt <= renderAt)
                snapshots.RemoveAt(0);

            MovementSnapshot first = snapshots[0];
            if (snapshots.Count == 1 || renderAt <= first.SentAt)
            {
                position = new MovementSample(first.X, first.Y, first.Z);
                return true;
            }

            MovementSnapshot second = snapshots[1];
            double duration = second.SentAt - first.SentAt;
            if (renderAt <= second.SentAt || duration <= 0)
            {
                double alpha = duration > 0 ? Clamp((renderAt - first.SentAt) / duration, 0, 1) : 1;
                position = Interpolate(first, second, alpha);
                return true;
            }

            // Extrapolation has both a time and speed limit, so a dropped
            // packet cannot launch a remote avatar across the map.
            double seconds = Math.Min(renderAt - second.SentAt, MaxExtrapolationSeconds);
            double dx = (second.X - first.X) / duration;
            double dy = (second.Y - first.Y) / duration;
            double dz = (second.Z - first.Z) / duration;
            double speed = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (speed > MaxExtrapolationSpeed)
            {
                double factor = MaxExtrapolationSpeed / speed;
                dx *= factor;
                dy *= factor;
                dz *= factor;
            }

            position = new MovementSample(
                (float)(second.X + dx * seconds),
                (float)(second.Y + dy * seconds),
                (float)(second.Z + dz * seconds));
            return true;
        }

        private static MovementSample Interpolate(MovementSnapshot a, MovementSnapshot b, double t)
        {
            return new MovementSample(
                (float)(a.X + (b.X - a.X) * t),
                (float)(a.Y + (b.Y - a.Y) * t),
                (float)(a.Z + (b.Z - a.Z) * t));
        }

        private static double DistanceSquared(MovementSnapshot a, MovementSnapshot b)
        {
            double x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
            return x * x + y * y + z * z;
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static double MoveTowards(double current, double target, double step)
        {
            if (Math.Abs(target - current) <= step)
                return target;
            return current + Math.Sign(target - current) * step;
        }
    }
}
