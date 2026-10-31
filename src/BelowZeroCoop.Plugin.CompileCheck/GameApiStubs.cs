// Compile-only API surface. NEVER include this assembly or these stubs
// in production builds; validate the real plugin with game/BepInEx DLLs.
using System;

namespace UnityEngine
{
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
