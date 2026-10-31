// Compile-only API surface. NEVER include this assembly or these stubs
// in production builds; validate the real plugin with game/BepInEx DLLs.
using System;

// Minimal stand-in for Subnautica Below Zero's Player.main in Assembly-CSharp.
public sealed class Player
{
    public static Player main { get; set; }
    public UnityEngine.Transform transform { get; } = new UnityEngine.Transform();
}

namespace UnityEngine
{
    public class Object
    {
        public static int DestroyedCount { get; private set; }
        public static void Destroy(Object value)
        {
            if (value != null)
                DestroyedCount++;
        }
    }

    public enum PrimitiveType { Capsule }

    public sealed class Vector3
    {
        public float x;
        public float y;
        public float z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }

    public sealed class Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w)
        {
            this.x = x; this.y = y; this.z = z; this.w = w;
        }
    }

    public sealed class Transform
    {
        public Vector3 position { get; set; } = new Vector3(0, 0, 0);
        public Vector3 localPosition { get; set; } = new Vector3(0, 0, 0);
        public Quaternion rotation { get; set; } = new Quaternion(0, 0, 0, 1);
        public Quaternion localRotation { get; set; } = new Quaternion(0, 0, 0, 1);
        public Vector3 localScale { get; set; } = new Vector3(1, 1, 1);
    }

    public sealed class Collider : Object { }

    public sealed class GameObject : Object
    {
        private readonly System.Collections.Generic.Dictionary<System.Type, object> components =
            new System.Collections.Generic.Dictionary<System.Type, object>();
        public static int CreatedCount { get; private set; }
        public string name { get; set; }
        public Transform transform { get; } = new Transform();

        public void SetComponent<T>(T component) where T : class
        {
            components[typeof(T)] = component;
        }

        public object GetComponent(System.Type type)
        {
            object component;
            return type != null && components.TryGetValue(type, out component)
                ? component : null;
        }

        public static GameObject CreatePrimitive(PrimitiveType type)
        {
            CreatedCount++;
            return new GameObject();
        }

        public T GetComponent<T>() where T : class
        {
            return null;
        }
    }

    public class MonoBehaviour { }

    public static class Time
    {
        public static float realtimeSinceStartup { get { return 0f; } }
    }

    public static class Mathf
    {
        public static float Max(float a, float b) { return Math.Max(a, b); }
        public static float Abs(float value) { return Math.Abs(value); }
        public static float Lerp(float a, float b, float t) { return a + (b - a) * t; }
        public static int RoundToInt(float value) { return (int)Math.Round(value); }
    }
}

namespace BepInEx.Configuration
{
    public sealed class ConfigEntry<T>
    {
        public T Value { get; set; }
        public ConfigEntry(T value) { Value = value; }
    }

    public sealed class ConfigFile
    {
        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description)
        {
            return new ConfigEntry<T>(defaultValue);
        }
    }
}

namespace BepInEx
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BepInPlugin : Attribute
    {
        public BepInPlugin(string guid, string name, string version) { }
    }

    public sealed class ManualLogSource
    {
        public void LogInfo(object data) { }
        public void LogError(object data) { }
        public void LogWarning(object data) { }
    }

    public class BaseUnityPlugin : UnityEngine.MonoBehaviour
    {
        protected BepInEx.Configuration.ConfigFile Config { get; } =
            new BepInEx.Configuration.ConfigFile();

        protected ManualLogSource Logger { get; } = new ManualLogSource();
    }
}

// Reflection-only hook scanner fixture; absent from real production game.
public sealed class GameHookProbeFixture
{
    public static int InvokedCount { get; private set; }
    public static int ForeignPostfixCount { get; private set; }
    public void Present() { InvokedCount++; }
    public static void ForeignPostfix(object __instance)
    {
        ForeignPostfixCount++;
    }
    public void Overloaded(int value) { }
    public void Overloaded(string value) { }
}

// Fake Build Zero game targets for compile-check only. They are not
// actual Below Zero game classes or method bodies.
public sealed class UniqueIdentifier
{
    // Game API fixture name, not a copy of the original implementation.
    public string Id { get; set; }
}

public sealed class Constructable
{
    public readonly UnityEngine.GameObject gameObject = new UnityEngine.GameObject();
    public float constructedAmount;
    public string techType;
    public static int Executions { get; private set; }
    public bool Construct()
    {
        Executions++;
        HarmonyLib.HarmonyFakeDispatch.After(
            System.Reflection.MethodBase.GetCurrentMethod(), this);
        return true;
    }
}
public sealed class FakeBaseOwner
{
    public UnityEngine.GameObject gameObject { get; } = new UnityEngine.GameObject();
}

public sealed class BaseGhost
{
    public UnityEngine.GameObject gameObject { get; } = new UnityEngine.GameObject();
    public FakeBaseOwner TargetBase { get; set; }
    public static int Executions { get; private set; }
    public void Finish()
    {
        Executions++;
        HarmonyLib.HarmonyFakeDispatch.After(
            System.Reflection.MethodBase.GetCurrentMethod(), this);
    }
}
public sealed class AmbiguousConstructable
{
    public void Construct() { }
    public void Construct(int value) { }
}
