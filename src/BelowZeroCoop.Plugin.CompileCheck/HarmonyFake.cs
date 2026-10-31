
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HarmonyLib
{
    public enum HarmonyPatchType { Postfix }
    public sealed class HarmonyMethod
    {
        public MethodInfo method;
        public HarmonyMethod(MethodInfo value) { method = value; }
    }

    public sealed class Harmony
    {
        private readonly string owner;
        public Harmony(string id) { owner = id; }
        public MethodInfo Patch(MethodBase original, HarmonyMethod prefix = null,
            HarmonyMethod postfix = null, HarmonyMethod transpiler = null,
            HarmonyMethod finalizer = null)
        {
            if (prefix != null || postfix == null)
                throw new InvalidOperationException("fake observer expects postfix only");
            HarmonyFakeDispatch.Add(original, owner, postfix.method);
            return original as MethodInfo;
        }
        public void Unpatch(MethodBase original, HarmonyPatchType type, string harmonyID)
        {
            HarmonyFakeDispatch.Remove(original, harmonyID);
        }
    }
    public static class HarmonyFakeDispatch
    {
        private sealed class Entry
        {
            internal string Owner;
            internal MethodInfo Method;
        }
        private static readonly Dictionary<MethodBase, List<Entry>> Hooks = new();
        public static int PatchCount => Hooks.Values.Sum(x => x.Count);
        internal static void Add(MethodBase target, string owner, MethodInfo callback)
        {
            if (!Hooks.TryGetValue(target, out var handlers))
                Hooks.Add(target, handlers = new());
            handlers.Add(new Entry { Owner = owner, Method = callback });
        }
        internal static void Remove(MethodBase target, string owner)
        {
            if (Hooks.TryGetValue(target, out var handlers))
            {
                handlers.RemoveAll(x => x.Owner == owner);
                if (handlers.Count == 0)
                    Hooks.Remove(target);
            }
        }
        public static void After(MethodBase target, object instance)
        {
            if (!Hooks.TryGetValue(target, out var handlers))
                return;
            foreach (var patch in handlers.ToArray())
                patch.Method.Invoke(null, new[] { instance });
        }
    }
}
