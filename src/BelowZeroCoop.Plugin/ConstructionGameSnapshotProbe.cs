using System;
using System.Reflection;

namespace BelowZeroCoop.Plugin
{
    /// <summary>
    /// Independent, best-effort reflection of known Below Zero game object
    /// metadata. This is diagnostic-only: it does not create UUIDs, modify
    /// game state, or certify the source as a local player construction.
    /// No game assembly is redistributed or referenced by this code.
    /// </summary>
    internal static class ConstructionGameSnapshotProbe
    {
        internal sealed class TransformData
        {
            internal readonly double X;
            internal readonly double Y;
            internal readonly double Z;
            internal readonly double RotationX;
            internal readonly double RotationY;
            internal readonly double RotationZ;
            internal readonly double RotationW;

            internal TransformData(double x, double y, double z,
                double rx, double ry, double rz, double rw)
            {
                X = x; Y = y; Z = z;
                RotationX = rx; RotationY = ry;
                RotationZ = rz; RotationW = rw;
            }
        }

        internal sealed class Snapshot
        {
            // The game's original identifier is a string, not necessarily a
            // System.Guid. Never turn a missing ID into a random durable ID.
            internal readonly string ObservedObjectId;
            internal readonly string TargetBaseId;
            internal readonly string TechType;
            internal readonly double? ConstructedAmount;
            internal readonly TransformData WorldTransform;
            internal readonly TransformData LocalTransform;
            internal readonly string CompletionEvidence;
            internal readonly string Origin;

            internal Snapshot(string objectId, string targetBaseId,
                string techType, double? amount, TransformData world,
                TransformData local, string completion)
            {
                ObservedObjectId = objectId;
                TargetBaseId = targetBaseId;
                TechType = techType;
                ConstructedAmount = amount;
                WorldTransform = world;
                LocalTransform = local;
                CompletionEvidence = completion;
                // A method callback cannot distinguish player actions from
                // save loading, scripts or replayed remote objects.
                Origin = "unknown";
            }
        }

        internal static Snapshot Capture(string eventName, object gameComponent)
        {
            if (gameComponent == null)
                return new Snapshot(null, null, null, null, null, null, "unknown");

            try
            {
                object gameObject = Read(gameComponent, "gameObject");
                object owner = gameObject ?? gameComponent;
                object transform = Read(owner, "transform");
                string uniqueId = GetUniqueId(gameObject, gameComponent.GetType().Assembly);
                string baseId = null;

                // TargetBase identifies the parent base only. It is NEVER
                // used as the ID of a newly constructed piece.
                object targetBase = Read(gameComponent, "TargetBase");
                if (targetBase != null)
                    baseId = GetUniqueId(Read(targetBase, "gameObject"),
                        gameComponent.GetType().Assembly);

                string tech = SafeLabel(Read(gameComponent, "techType"), 64);
                double? amount = ReadFinite(Read(gameComponent, "constructedAmount"));

                string stage = "unverified_method_return";
                if (eventName == "construct_method_returned")
                    stage = amount.HasValue
                        ? (amount.Value >= 0.999 ? "complete_candidate" : "partial_progress")
                        : "progress_unknown";
                else if (eventName == "base_ghost_finish_returned")
                    stage = "finish_returned_candidate";

                return new Snapshot(uniqueId, baseId, tech, amount,
                    ReadTransform(transform, false), ReadTransform(transform, true),
                    stage);
            }
            catch
            {
                // Unity can destroy a component while it is being inspected,
                // and some builds have different assembly APIs. Never throw
                // from a Harmony postfix.
                return new Snapshot(null, null, null, null, null, null, "unknown");
            }
        }

        private static string GetUniqueId(object gameObject, Assembly assembly)
        {
            if (gameObject == null || assembly == null)
                return null;
            Type identifierType = assembly.GetType("UniqueIdentifier", false);
            if (identifierType == null)
                return null;

            object identifier = null;
            try
            {
                var method = gameObject.GetType().GetMethod("GetComponent",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(Type) }, null);
                if (method != null)
                    identifier = method.Invoke(gameObject, new object[] { identifierType });
            }
            catch
            {
                return null;
            }

            return SafeLabel(Read(identifier, "Id"), 128);
        }

        private static TransformData ReadTransform(object transform, bool local)
        {
            if (transform == null)
                return null;

            object point = Read(transform, local ? "localPosition" : "position");
            object rotation = Read(transform, local ? "localRotation" : "rotation");
            double? x = ReadFinite(Read(point, "x"));
            double? y = ReadFinite(Read(point, "y"));
            double? z = ReadFinite(Read(point, "z"));
            double? rx = ReadFinite(Read(rotation, "x"));
            double? ry = ReadFinite(Read(rotation, "y"));
            double? rz = ReadFinite(Read(rotation, "z"));
            double? rw = ReadFinite(Read(rotation, "w"));

            if (!x.HasValue || !y.HasValue || !z.HasValue ||
                !rx.HasValue || !ry.HasValue || !rz.HasValue || !rw.HasValue ||
                Math.Abs(x.Value) > 100000 || Math.Abs(y.Value) > 100000 ||
                Math.Abs(z.Value) > 100000 ||
                Math.Abs(rx.Value) > 2 || Math.Abs(ry.Value) > 2 ||
                Math.Abs(rz.Value) > 2 || Math.Abs(rw.Value) > 2)
                return null;

            return new TransformData(x.Value, y.Value, z.Value,
                rx.Value, ry.Value, rz.Value, rw.Value);
        }

        private static object Read(object source, string member)
        {
            if (source == null)
                return null;
            try
            {
                Type t = source.GetType();
                const BindingFlags flags = BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.Instance;
                PropertyInfo p = t.GetProperty(member, flags);
                if (p != null && p.GetIndexParameters().Length == 0)
                    return p.GetValue(source, null);
                FieldInfo f = t.GetField(member, flags);
                return f == null ? null : f.GetValue(source);
            }
            catch
            {
                return null;
            }
        }

        private static double? ReadFinite(object value)
        {
            if (value == null)
                return null;
            try
            {
                double n = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                return double.IsNaN(n) || double.IsInfinity(n) ? (double?)null : n;
            }
            catch
            {
                return null;
            }
        }

        private static string SafeLabel(object raw, int max)
        {
            if (raw == null)
                return null;
            string text;
            try { text = raw.ToString(); }
            catch { return null; }

            if (string.IsNullOrWhiteSpace(text) || text.Length > max)
                return null;
            foreach (char c in text)
            {
                if (!char.IsLetterOrDigit(c) && c != '.' && c != ':' &&
                    c != '_' && c != '-' && c != '/')
                    return null;
            }
            return text;
        }
    }
}
