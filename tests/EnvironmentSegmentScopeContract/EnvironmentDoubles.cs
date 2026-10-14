using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityEngine
{
    public partial class GameObject
    {
        public T[] GetComponents<T>() where T : Object { ComponentReads++; return Components.Values.OfType<T>().ToArray(); }
    }
    public class MonoBehaviour : Component
    {
        public bool enabled = true;
        public MonoBehaviour(GameObject source) : base(source) { source.Components[GetType()] = this; Object.NativeRegistry.Add(this); }
    }
    public class Rigidbody : Component { public Rigidbody(GameObject source) : base(source) { } }
    public static class EnvironmentComponents
    {
        public static int ParentQueries, TreeQueries;
        public static T? GetComponentInParent<T>(this Component source, bool includeInactive) where T : Object
        {
            ParentQueries++;
            for (var node = source.transform; node != null; node = node.RawParent)
                if (node.gameObject.Components.Values.OfType<T>().FirstOrDefault() is T found) return found;
            return null;
        }
        public static T? GetComponentInChildren<T>(this Component source, bool includeInactive) where T : Component
        { TreeQueries++; return Object.NativeRegistry.OfType<T>().FirstOrDefault(value => Under(value.transform, source.transform)); }
        private static bool Under(Transform child, Transform root)
        { for (var node = child; node != null; node = node.RawParent) if (ReferenceEquals(node, root)) return true; return false; }
    }
    public static class Shader
    {
        private static readonly Dictionary<string, int> names = new();
        public static int PropertyToID(string value) { if (!names.TryGetValue(value, out int id)) names[value] = id = names.Count + 1; return id; }
        public static float GetGlobalFloat(int id) => 0;
    }
}
public class WindChillZone : UnityEngine.MonoBehaviour
{
    public UnityEngine.Vector3 currentWindDirection = new(1, 0, 0);
    public float untilSwitch = 1, timeUntilNextWind = 1, hasBeenActiveFor;
    public bool windActive;
    public int StateReads;
    public float windIntensity { get { StateReads++; return 1; } }
    public float StormProgress => 0;
    public float timeUntilStorm => 0;
    public WindChillZone(UnityEngine.GameObject source) : base(source) { }
}
public class StormVisual : UnityEngine.MonoBehaviour
{
    public float windIntensity = 1;
    public int StateReads;
    public int stormType => 1;
    public float windFactor { get { StateReads++; return 1; } }
    public StormVisual(UnityEngine.GameObject source) : base(source) { }
}
public class LavaRising : UnityEngine.MonoBehaviour
{
    public UnityEngine.Rigidbody? lava;
    public UnityEngine.GameObject? fogPlane;
    public bool started, ended;
    public float timeTraveled;
    public LavaRising(UnityEngine.GameObject source, UnityEngine.GameObject target) : base(source) => lava = new(target);
}
public class MovingLava : UnityEngine.MonoBehaviour
{
    public bool timeToMove;
    public MovingLava(UnityEngine.GameObject source) : base(source) { }
}
public class OrbFogHandler : UnityEngine.MonoBehaviour
{
    public float currentCloseFog;
    public int currentID;
    public bool isMoving, hasArrived;
    public OrbFogHandler(UnityEngine.GameObject source) : base(source) { }
}
public class FogSphere : UnityEngine.MonoBehaviour
{
    public UnityEngine.Vector3 fogPoint;
    public float currentSize = 1, PADDING, ENABLE, REVEAL_AMOUNT;
    public FogSphere(UnityEngine.GameObject source) : base(source) { }
}
public class DayNightManager : UnityEngine.Object
{
    public static DayNightManager? instance;
    public float rainstormWindFactor, snowstormWindFactor;
}
namespace PeakReplayLab
{
    internal sealed class EnvironmentReplayObserver : IDisposable
    {
        private static Action<UnityEngine.Component>? listener;
        private readonly Action<UnityEngine.Component> changed;
        public EnvironmentReplayObserver(Action<UnityEngine.Component> changed) { this.changed = changed; listener += changed; }
        public static void Emit(UnityEngine.Component source) => listener?.Invoke(source);
        public void Dispose() => listener -= changed;
    }
    internal enum ReplayStage { Discovery, Environment }
    internal static class ReplayPerformance
    {
        public readonly struct Scope : IDisposable { public void Dispose() { } }
        public static Scope Measure(ReplayStage stage) => default;
    }
    public sealed class EnvironmentReplayFrame
    {
        public bool SampleTimeKnown; public double SampleTime;
        public EnvironmentWindFrame[] Winds = Array.Empty<EnvironmentWindFrame>();
        public EnvironmentLavaFrame[] Lava = Array.Empty<EnvironmentLavaFrame>();
        public EnvironmentFogFrame? Fog;
        public float WeatherBlend, GlobalWind, RainFactor, SnowFactor, HeightFogAmount, HeightFogSpirit;
    }
    public sealed class EnvironmentWindFrame
    {
        public string Key = ""; public bool Enabled, Active; public float[] Direction = new float[3];
        public float Intensity, StormProgress, TimeUntilStorm, SecondsUntilSwitch, Duration, ActiveFor;
        public EnvironmentStormFrame[] Storms = Array.Empty<EnvironmentStormFrame>();
    }
    public sealed class EnvironmentStormFrame { public string Key = ""; public int Kind; public float Factor, Intensity; }
    public sealed class EnvironmentLavaFrame
    { public string Key = ""; public int Kind; public bool Active, Started, Ended, FogPlaneActive; public float[] Position = new float[3]; public float ProgressTime; }
    public sealed class EnvironmentFogFrame
    { public string Key = ""; public float[] Point = new float[3]; public float Size, Padding, Enable, Reveal, CloseFog; public bool Active, Moving, Arrived; public int Origin; }
    internal static class EnvironmentReplayRules { public const int MaximumWinds = 16, MaximumLava = 32, MaximumStorms = 8; }
}
