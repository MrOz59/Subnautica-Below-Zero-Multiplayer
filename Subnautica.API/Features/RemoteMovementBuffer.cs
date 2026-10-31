namespace Subnautica.API.Features
{
    using System.Collections.Generic;
    using UnityEngine;

    /// <summary>
    /// Jitter-adaptive interpolation for remote visual transforms. Authoritative
    /// inventory, interactions and physics state remain packet-driven.
    /// </summary>
    public sealed class RemoteMovementBuffer
    {
        private const int MaxSnapshots = 32;
        private const float TeleportDistanceSquared = 100f * 100f;
        private const float MaxExtrapolationSeconds = 0.10f;

        // Dead reckoning must not magnify a short jitter spike into a visual launch.
        private const float MaxExtrapolationSpeed = 35f;
        private readonly List<Snapshot> snapshots = new List<Snapshot>(MaxSnapshots);

        private float offset;
        private float jitter;
        private float bufferSeconds = 0.12f;
        private float lastArrival;
        private float persistentDelaySeconds;
        private float lastSentTime;
        private uint lastSequence;
        private bool hasSequence;

        /// <summary>
        /// Last genuine sender timestamp, used to reject late cross-mode packets.
        /// Zero means that the sender has not supplied time metadata.
        /// </summary>
        public float LatestTimestamp { get; private set; }

        private struct Snapshot
        {
            public float Time;
            public Vector3 Position;
            public Quaternion Rotation;

            public Snapshot(float time, Vector3 position, Quaternion rotation)
            {
                Time = time;
                Position = position;
                Rotation = rotation;
            }
        }

        public void Reset()
        {
            snapshots.Clear();
            offset = 0f;
            jitter = 0f;
            bufferSeconds = 0.12f;
            lastArrival = 0f;
            persistentDelaySeconds = 0f;
            lastSentTime = 0f;
            lastSequence = 0;
            hasSequence = false;
            LatestTimestamp = 0f;
        }

        public bool AddSnapshot(uint sequence, float sentTime, Vector3 position, Quaternion rotation)
        {
            float arrival = Time.realtimeSinceStartup;
            bool validSenderTime = sentTime > 0f && !float.IsNaN(sentTime) && !float.IsInfinity(sentTime);
            if (!validSenderTime)
            {
                // Support packets without the new timestamp metadata.
                sentTime = arrival;
            }

            if (hasSequence && sequence != 0 && unchecked((int)(sequence - lastSequence)) <= 0)
            {
                return false;
            }

            if (snapshots.Count > 0)
            {
                Snapshot previous = snapshots[snapshots.Count - 1];
                if (validSenderTime && sentTime <= previous.Time)
                {
                    return false;
                }

                if ((position - previous.Position).sqrMagnitude > TeleportDistanceSquared)
                {
                    Reset();
                }
            }

            float sampleOffset = arrival - sentTime;
            if (snapshots.Count == 0)
            {
                offset = sampleOffset;
            }
            else
            {
                // Follow better observed delivery times, not transient slow packets.
                if (sampleOffset < offset)
                {
                    offset = Mathf.Lerp(offset, sampleOffset, 0.25f);
                    persistentDelaySeconds = 0f;
                }
                else if (sampleOffset > offset + 0.06f)
                {
                    // A higher route latency sustained for over a second needs a
                    // gradually higher baseline; isolated late packets do not.
                    persistentDelaySeconds += Mathf.Clamp(arrival - lastArrival, 0f, 0.2f);
                    if (persistentDelaySeconds > 1.5f)
                    {
                        offset = Mathf.MoveTowards(offset, sampleOffset, 0.01f);
                    }
                }
                else
                {
                    persistentDelaySeconds = 0f;
                }

                float sendDelta = sentTime - lastSentTime;
                if (sendDelta > 0f && sendDelta < 0.5f)
                {
                    float receiveDelta = arrival - lastArrival;
                    jitter = Mathf.Lerp(jitter, Mathf.Min(Mathf.Abs(receiveDelta - sendDelta), 0.5f), 0.2f);
                }
            }

            lastArrival = arrival;
            lastSentTime = sentTime;
            if (validSenderTime)
            {
                LatestTimestamp = sentTime;
            }

            if (sequence != 0)
            {
                lastSequence = sequence;
                hasSequence = true;
            }

            snapshots.Add(new Snapshot(sentTime, position, rotation));
            if (snapshots.Count > MaxSnapshots)
            {
                snapshots.RemoveAt(0);
            }

            return true;
        }

        public bool TrySample(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (snapshots.Count == 0)
            {
                return false;
            }

            float desiredDelay = Mathf.Clamp(0.10f + jitter * 3f, 0.10f, 0.30f);
            bufferSeconds = Mathf.MoveTowards(bufferSeconds, desiredDelay, Time.unscaledDeltaTime * 0.20f);
            float renderTime = Time.realtimeSinceStartup - offset - bufferSeconds;

            while (snapshots.Count > 2 && snapshots[1].Time <= renderTime)
            {
                snapshots.RemoveAt(0);
            }

            Snapshot first = snapshots[0];
            if (snapshots.Count == 1 || renderTime <= first.Time)
            {
                position = first.Position;
                rotation = first.Rotation;
                return true;
            }

            Snapshot second = snapshots[1];
            if (renderTime < second.Time)
            {
                float span = second.Time - first.Time;
                float alpha = span > 0.0001f ? Mathf.Clamp01((renderTime - first.Time) / span) : 1f;
                position = Vector3.LerpUnclamped(first.Position, second.Position, alpha);
                rotation = Quaternion.SlerpUnclamped(first.Rotation, second.Rotation, alpha);
                return true;
            }

            float interval = second.Time - first.Time;
            float extrapolation = Mathf.Clamp(renderTime - second.Time, 0f, MaxExtrapolationSeconds);
            Vector3 velocity = interval > 0.0001f
                ? (second.Position - first.Position) / interval
                : Vector3.zero;
            velocity = Vector3.ClampMagnitude(velocity, MaxExtrapolationSpeed);
            position = second.Position + velocity * extrapolation;
            rotation = second.Rotation;
            return true;
        }
    }
}
