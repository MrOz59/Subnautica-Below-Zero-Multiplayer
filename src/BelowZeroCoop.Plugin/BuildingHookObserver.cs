using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BelowZeroCoop.Plugin
{
    /// <summary>
    /// Own Harmony implementation: two optional passive observers of game
    /// construction entry points. Does not alter game return values, game
    /// state, inventory, world saves, or network traffic.
    /// </summary>
    internal sealed class BuildingHookObserver : IDisposable
    {
        internal const int MaxPending = 128;
        private const string HarmonyOwner = "dev.belowzerocoop.build-observers";
        private static readonly object ActiveGate = new object();
        private static BuildingHookObserver active;

        internal sealed class Candidate
        {
            internal readonly string ClassName;
            internal readonly string MethodName;
            internal readonly string EventName;
            internal readonly MethodInfo Callback;

            internal Candidate(string className, string methodName,
                string eventName, MethodInfo callback)
            {
                ClassName = className;
                MethodName = methodName;
                EventName = eventName;
                Callback = callback;
            }
        }

        internal sealed class Observation
        {
            internal readonly string EventName;
            internal readonly string RuntimeType;
            internal readonly int VolatileInstanceToken;
            internal readonly DateTime ObservedUtc;
            internal readonly ConstructionGameSnapshotProbe.Snapshot Game;

            internal Observation(string eventName, string runtimeType,
                int volatileInstanceToken, DateTime observedUtc,
                ConstructionGameSnapshotProbe.Snapshot game)
            {
                EventName = eventName;
                RuntimeType = runtimeType;
                VolatileInstanceToken = volatileInstanceToken;
                ObservedUtc = observedUtc;
                Game = game;
            }
        }

        private readonly object gate = new object();
        private readonly Queue<Observation> pending = new Queue<Observation>();
        private readonly List<MethodBase> installed = new List<MethodBase>();
        private readonly Harmony harmony = new Harmony(HarmonyOwner);
        private readonly ConstructionObservationDeduplicator deduplicator =
            new ConstructionObservationDeduplicator();
        [ThreadStatic] private static int callbackDepth;
        private int dropped;
        private int duplicateObservations;
        private bool disposed;

        internal int InstalledCount
        {
            get { lock (gate) return installed.Count; }
        }

        internal int DroppedCount
        {
            get { lock (gate) return dropped; }
        }

        internal int SuppressedDuplicateCount
        {
            get { lock (gate) return duplicateObservations; }
        }

        private static readonly MethodInfo ConstructCallback =
            typeof(BuildingHookObserver).GetMethod("AfterConstruct",
                BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo BaseCallback =
            typeof(BuildingHookObserver).GetMethod("AfterBaseFinish",
                BindingFlags.Static | BindingFlags.NonPublic);

        internal static Candidate[] DefaultTargets()
        {
            return new[]
            {
                new Candidate("Constructable", "Construct",
                    "construct_method_returned", ConstructCallback),
                new Candidate("BaseGhost", "Finish",
                    "base_ghost_finish_returned", BaseCallback)
            };
        }

        /// <summary>
        /// The caller (Plugin) must pass only the actual loaded Assembly-CSharp.
        /// Installed targets must be unique declared instance methods.
        /// Overloads and unavailable game methods are deliberately skipped.
        /// </summary>
        internal int Install(Assembly gameAssembly, IEnumerable<Candidate> candidates,
            Action<string> report)
        {
            if (gameAssembly == null)
                throw new ArgumentNullException("gameAssembly");
            if (candidates == null)
                throw new ArgumentNullException("candidates");

            lock (ActiveGate)
            {
                if (active != null || disposed)
                    return 0;

                active = this;
                try
                {
                    foreach (Candidate candidate in candidates)
                    {
                        if (candidate == null || candidate.Callback == null)
                            continue;

                        Type type = gameAssembly.GetType(candidate.ClassName, false);
                        if (type == null)
                        {
                            SafeReport(report, "Skipping absent game type: " +
                                candidate.ClassName);
                            continue;
                        }

                        MethodInfo[] matches = type.GetMethods(
                            BindingFlags.Instance | BindingFlags.Public |
                            BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                            .Where(method => method.Name == candidate.MethodName &&
                                !method.IsAbstract && !method.ContainsGenericParameters)
                            .ToArray();

                        if (matches.Length != 1)
                        {
                            SafeReport(report, "Skipping " + candidate.ClassName +
                                "." + candidate.MethodName + ": " +
                                matches.Length + " suitable methods (requires exactly one).");
                            continue;
                        }

                        try
                        {
                            // Harmony Postfix has no __result and never returns
                            // bool. It cannot suppress or alter the vanilla call.
                            harmony.Patch(matches[0], postfix:
                                new HarmonyMethod(candidate.Callback));
                            installed.Add(matches[0]);
                            SafeReport(report, "Installed read-only postfix: " +
                                candidate.ClassName + "." + candidate.MethodName);
                        }
                        catch (Exception ex)
                        {
                            SafeReport(report, "Failed installing " +
                                candidate.ClassName + "." + candidate.MethodName +
                                " (" + ex.GetType().Name + ")");
                            // Keep successfully installed independent observers.
                        }
                    }

                    if (installed.Count == 0)
                        active = null;
                    return installed.Count;
                }
                catch
                {
                    StopUnderActiveLock();
                    throw;
                }
            }
        }

        private static void AfterConstruct(object __instance)
        {
            ReportFromPatch("construct_method_returned", __instance);
        }

        private static void AfterBaseFinish(object __instance)
        {
            ReportFromPatch("base_ghost_finish_returned", __instance);
        }

        private static void ReportFromPatch(string eventName, object instance)
        {
            try
            {
                BuildingHookObserver receiver;
                lock (ActiveGate)
                    receiver = active;
                if (receiver == null || instance == null ||
                    ConstructionObservationContext.IsSuppressed ||
                    callbackDepth != 0)
                    return;

                // Never allow reflective Unity access to recursively create
                // another observation on the same callback thread.
                callbackDepth++;
                try
                {

                // RuntimeHelpers token is process-local and NOT an entity UUID.
                // Never use it as a persistent or network identity.
                // Game object metadata is captured on the invoking game
                // thread, not dereferenced later from a stale Unity object.
                // This is a diagnostic candidate: ID/source/finish must be
                // validated before building any host-authoritative event.
                var snapshot = ConstructionGameSnapshotProbe.Capture(eventName, instance);
                var observation = new Observation(eventName,
                    instance.GetType().FullName ?? "<unknown>",
                    RuntimeHelpers.GetHashCode(instance), DateTime.UtcNow,
                    snapshot);
                lock (receiver.gate)
                {
                    if (receiver.disposed)
                        return;
                    if (!receiver.deduplicator.ShouldRecord(observation))
                    {
                        receiver.duplicateObservations++;
                        return;
                    }
                    if (receiver.pending.Count >= MaxPending)
                    {
                        receiver.pending.Dequeue();
                        receiver.dropped++;
                    }
                    receiver.pending.Enqueue(observation);
                }
                }
                finally
                {
                    callbackDepth--;
                }
            }
            catch
            {
                // Fail closed: a diagnostic observer must never cause a
                // game construction method to throw or become cancelled.
            }
        }

        internal List<Observation> Drain(int maxItems)
        {
            lock (gate)
            {
                var result = new List<Observation>();
                int budget = Math.Max(0, Math.Min(32, maxItems));
                while (budget-- > 0 && pending.Count > 0)
                    result.Add(pending.Dequeue());
                return result;
            }
        }

        internal void Stop()
        {
            lock (ActiveGate)
                StopUnderActiveLock();
        }

        private void StopUnderActiveLock()
        {
            if (active == this)
                active = null;

            foreach (MethodBase method in installed)
            {
                try
                {
                    // Only remove patches from our own Harmony ID. Never
                    // unpatch other mods' owners or vanilla game callbacks.
                    harmony.Unpatch(method, HarmonyPatchType.Postfix, HarmonyOwner);
                }
                catch
                {
                    // Plugin shutdown should not interrupt Unity teardown.
                }
            }
            installed.Clear();
            lock (gate)
            {
                pending.Clear();
                deduplicator.Clear();
                disposed = true;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private static void SafeReport(Action<string> report, string line)
        {
            if (report == null)
                return;
            try { report(line); }
            catch { /* logging failure cannot affect the game */ }
        }
    }
}
