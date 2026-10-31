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

    public sealed class Transform
    {
        public Vector3 position { get; set; } = new Vector3(0, 0, 0);
        public Vector3 localScale { get; set; } = new Vector3(1, 1, 1);
    }

    public sealed class Collider : Object { }

    public sealed class GameObject : Object
    {
        public static int CreatedCount { get; private set; }
        public string name { get; set; }
        public Transform transform { get; } = new Transform();

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
