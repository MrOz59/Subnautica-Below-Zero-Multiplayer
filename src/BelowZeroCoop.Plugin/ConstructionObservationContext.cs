using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace BelowZeroCoop.Plugin
{
    /// <summary>
    /// Explicit diagnostic suppression for future trusted save/replay adapters.
    /// No automatic "local player" inference: an unsuppressed event still has
    /// unknown origin and can NEVER authorize a gameplay transaction.
    ///
    /// Scopes apply to the thread executing the vanilla method. Async/coroutine
    /// continuations require a fresh scope at each execution point.
    /// </summary>
    internal static class ConstructionObservationContext
    {
        internal enum SuppressionReason
        {
            SaveRestore,
            RemoteReplay,
            ScriptedMutation
        }

        [ThreadStatic] private static int suppressionDepth;

        internal static bool IsSuppressed
        {
            get { return suppressionDepth > 0; }
        }

        internal static IDisposable Suppress(SuppressionReason reason)
        {
            if (!Enum.IsDefined(typeof(SuppressionReason), reason))
                throw new ArgumentOutOfRangeException("reason");
            if (suppressionDepth == int.MaxValue)
                throw new InvalidOperationException("Observation suppression depth exceeded");
            suppressionDepth++;
            return new Scope(Thread.CurrentThread.ManagedThreadId);
        }

        private sealed class Scope : IDisposable
        {
            private readonly int ownerThreadId;
            private bool released;

            internal Scope(int ownerThreadId)
            {
                this.ownerThreadId = ownerThreadId;
            }

            public void Dispose()
            {
                if (released)
                    return;
                // Never silently unsuppress an unrelated thread. A future
                // replay adapter must dispose on the same execution thread.
                if (Thread.CurrentThread.ManagedThreadId != ownerThreadId)
                    throw new InvalidOperationException(
                        "Observation scope must be disposed on its creating thread");
                if (suppressionDepth <= 0)
                    throw new InvalidOperationException("Observation suppression underflow");
                suppressionDepth--;
                released = true;
            }
        }
    }

    /// <summary>
    /// Bounded, short-lived coalescing of identical diagnostic snapshots.
    /// Never used to accept/reject game-state changes or networking events.
    /// An incomplete or changed progress/transform snapshot is not merged.
    /// </summary>
    internal sealed class ConstructionObservationDeduplicator
    {
        private readonly Dictionary<string, DateTime> seen =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly Queue<Entry> insertionOrder = new Queue<Entry>();
        private const int MaxSeen = 128;
        private const long WindowTicks = TimeSpan.TicksPerMillisecond * 250;

        private sealed class Entry
        {
            internal string Key;
            internal DateTime AtUtc;
        }

        internal bool ShouldRecord(BuildingHookObserver.Observation observation)
        {
            if (observation == null || observation.Game == null)
                return false;
            DateTime now = observation.ObservedUtc;
            string key = Key(observation);
            DateTime prior;
            if (seen.TryGetValue(key, out prior))
            {
                long delta = now.Ticks - prior.Ticks;
                if (delta >= 0 && delta <= WindowTicks)
                    return false;
            }

            seen[key] = now;
            insertionOrder.Enqueue(new Entry { Key = key, AtUtc = now });
            while (insertionOrder.Count > MaxSeen)
            {
                Entry oldest = insertionOrder.Dequeue();
                DateTime current;
                if (seen.TryGetValue(oldest.Key, out current) &&
                    current == oldest.AtUtc)
                    seen.Remove(oldest.Key);
            }
            return true;
        }

        internal void Clear()
        {
            seen.Clear();
            insertionOrder.Clear();
        }

        private static string Key(BuildingHookObserver.Observation o)
        {
            // Delimiters are not accepted in game IDs or tech labels.
            // Volatile token is only an in-process filter key, not an ID.
            var g = o.Game;
            var b = new StringBuilder(256);
            b.Append(o.EventName).Append('|').Append(o.RuntimeType)
                .Append('|').Append(o.VolatileInstanceToken)
                .Append('|').Append(g.ObservedObjectId)
                .Append('|').Append(g.ObjectIdSource)
                .Append('|').Append(g.TechType)
                .Append('|').Append(g.CompletionEvidence)
                .Append('|').Append(g.ConstructedAmount.HasValue
                    ? g.ConstructedAmount.Value.ToString("R", CultureInfo.InvariantCulture)
                    : "unknown")
                .Append('|').Append(g.TargetBaseId);
            AppendTransform(b, g.WorldTransform);
            AppendTransform(b, g.LocalTransform);
            return b.ToString();
        }

        private static void AppendTransform(StringBuilder buffer,
            ConstructionGameSnapshotProbe.TransformData t)
        {
            if (t == null)
            {
                buffer.Append("|null");
                return;
            }
            buffer.Append('|').Append(t.X.ToString("R", CultureInfo.InvariantCulture));
            buffer.Append('|').Append(t.Y.ToString("R", CultureInfo.InvariantCulture));
            buffer.Append('|').Append(t.Z.ToString("R", CultureInfo.InvariantCulture));
            buffer.Append('|').Append(t.RotationX.ToString("R", CultureInfo.InvariantCulture));
            buffer.Append('|').Append(t.RotationY.ToString("R", CultureInfo.InvariantCulture));
            buffer.Append('|').Append(t.RotationZ.ToString("R", CultureInfo.InvariantCulture));
            buffer.Append('|').Append(t.RotationW.ToString("R", CultureInfo.InvariantCulture));
        }
    }
}
