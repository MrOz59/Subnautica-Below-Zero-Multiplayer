using System;
using System.Collections.Generic;

namespace BelowZeroCoop.Plugin
{
    /// <summary>
    /// Tracks *diagnostic evidence* of removal, not authoritative game state.
    /// Requests and coroutine entry are not proof of deletion. Never emits
    /// network operations or removes a world entity.
    /// </summary>
    internal sealed class DeconstructionEvidenceTracker
    {
        internal enum Phase { Unknown, Requested, Progressing, VerifiedAbsent }
        internal sealed class Entry
        {
            internal string Identity;
            internal Phase State;
            internal DateTime UpdatedUtc;
            internal int Generation;
        }

        private readonly Dictionary<string, Entry> entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly TimeSpan maxAge;
        private readonly int capacity;

        internal DeconstructionEvidenceTracker(int capacity = 256, TimeSpan? maxAge = null)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException("capacity");
            this.capacity = capacity;
            this.maxAge = maxAge ?? TimeSpan.FromMinutes(2);
            if (this.maxAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("maxAge");
        }

        // No identity means no durable evidence. IDs must come from a
        // separately validated game UID rather than a ghost or object hash.
        internal bool Request(string identity, int generation, DateTime utc)
        {
            if (!Valid(identity, generation)) return false;
            Prune(utc);
            Entry entry;
            if (entries.TryGetValue(identity, out entry))
            {
                // Out-of-order observations may never rewind a current
                // generation's activity time or change its phase.
                if (generation < entry.Generation || utc < entry.UpdatedUtc)
                    return false;
                if (generation == entry.Generation &&
                    entry.State == Phase.VerifiedAbsent) return false;
                if (generation > entry.Generation)
                {
                    entry.Generation = generation;
                    entry.State = Phase.Requested;
                }
                entry.UpdatedUtc = utc;
                return true;
            }
            if (entries.Count >= capacity) EvictOldest();
            entries.Add(identity, new Entry
            {
                Identity = identity, Generation = generation,
                State = Phase.Requested, UpdatedUtc = utc
            });
            return true;
        }

        internal bool Progress(string identity, int generation, DateTime utc)
        {
            Entry entry;
            if (!TryCurrent(identity, generation, utc, out entry) ||
                entry.State == Phase.VerifiedAbsent) return false;
            entry.State = Phase.Progressing;
            entry.UpdatedUtc = utc;
            return true;
        }

        // Must only be called after independently verifying that the
        // persistent game object no longer exists, not on a Harmony prefix,
        // postfix, or coroutine return alone.
        internal bool ConfirmAbsent(string identity, int generation, DateTime utc)
        {
            Entry entry;
            if (!TryCurrent(identity, generation, utc, out entry) ||
                entry.State != Phase.Progressing) return false;
            entry.State = Phase.VerifiedAbsent;
            entry.UpdatedUtc = utc;
            return true;
        }

        internal bool Cancel(string identity, int generation)
        {
            Entry entry;
            if (!Valid(identity, generation) ||
                !entries.TryGetValue(identity, out entry) ||
                entry.Generation != generation ||
                entry.State == Phase.VerifiedAbsent) return false;
            entries.Remove(identity);
            return true;
        }

        internal Phase GetPhase(string identity, int generation, DateTime utc)
        {
            Entry entry;
            return TryCurrent(identity, generation, utc, out entry)
                ? entry.State : Phase.Unknown;
        }

        internal void Clear() { entries.Clear(); }

        private bool TryCurrent(string identity, int generation, DateTime utc,
            out Entry entry)
        {
            entry = null;
            if (!Valid(identity, generation)) return false;
            Prune(utc);
            return entries.TryGetValue(identity, out entry) &&
                entry.Generation == generation && utc >= entry.UpdatedUtc;
        }

        private void Prune(DateTime utc)
        {
            var expired = new List<string>();
            foreach (var pair in entries)
                if (utc >= pair.Value.UpdatedUtc &&
                    utc - pair.Value.UpdatedUtc > maxAge)
                    expired.Add(pair.Key);
            foreach (string key in expired) entries.Remove(key);
        }

        private void EvictOldest()
        {
            string oldest = null;
            DateTime when = DateTime.MaxValue;
            foreach (var pair in entries)
                if (pair.Value.UpdatedUtc < when)
                {
                    when = pair.Value.UpdatedUtc;
                    oldest = pair.Key;
                }
            if (oldest != null) entries.Remove(oldest);
        }

        private static bool Valid(string identity, int generation)
        {
            if (string.IsNullOrWhiteSpace(identity) ||
                identity.Length > 128 || generation <= 0 ||
                identity != identity.Trim())
                return false;
            foreach (char ch in identity)
                if (char.IsControl(ch))
                    return false;
            return true;
        }
    }
}
