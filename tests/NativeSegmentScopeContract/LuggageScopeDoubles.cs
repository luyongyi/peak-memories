using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityEngine.SceneManagement
{
    public readonly struct Scene { public readonly int handle; public Scene(int handle) => this.handle = handle; }
    public static class SceneManager { public static Scene GetActiveScene() => new(1); }
}
namespace UnityEngine
{
    public enum FindObjectsInactive { Include }
    public enum FindObjectsSortMode { None }
    public partial class Object
    {
        public static readonly List<Object> NativeRegistry = new();
        public static T[] FindObjectsByType<T>(FindObjectsInactive inactive, FindObjectsSortMode sort) where T : Object => NativeRegistry.OfType<T>().ToArray();
    }
    public partial class GameObject
    {
        public SceneManagement.Scene scene = new(1);
        public int ComponentReads;
        public readonly Dictionary<Type, Object> Components = new();
        public T? GetComponent<T>() where T : Object
        { ComponentReads++; return Components.TryGetValue(typeof(T), out var value) ? (T)value : null; }
        public T? GetComponentInParent<T>(bool includeInactive = false) where T : Object
        {
            for (var node = transform; node != null; node = node.parent)
                if (node.gameObject.GetComponent<T>() is { } value) return value;
            return null;
        }
    }
    public class Component : Object
    {
        public readonly GameObject gameObject;
        public Transform transform => gameObject.transform;
        public Component(GameObject source) => gameObject = source;
        public T? GetComponent<T>() where T : Object => gameObject.GetComponent<T>();
    }
    public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x=x; this.y=y; this.z=z; } }
    public struct Quaternion { public float x, y, z, w; public Quaternion(float x, float y, float z, float w) { this.x=x; this.y=y; this.z=z; this.w=w; } }
    public partial class Transform
    {
        public Vector3 position;
        public Quaternion rotation = new(0,0,0,1);
        public Vector3 lossyScale = new(1,1,1);
    }
    public static class Time { public static double timeAsDouble; public static int frameCount; }
    public static class Mathf
    {
        public static float Clamp(float x, float min, float max) => Math.Max(min, Math.Min(max,x));
        public static float Repeat(float x, float length) => x - (float)Math.Floor(x / length) * length;
    }
    public class AnimationClip : Object { public bool humanMotion, isLooping; public float length = 1; }
    public struct AnimatorClipInfo { public AnimationClip clip; public float weight; }
    public struct AnimatorStateInfo
    {
        public string Name;
        public float normalizedTime, speed, speedMultiplier;
        public bool IsName(string name) => Name == name;
    }
    public class Animator : Component
    {
        public Object runtimeAnimatorController = new();
        public int layerCount = 1;
        public float speed = 1;
        public AnimationClip Clip = new();
        public AnimatorStateInfo State = new() { Name = "Luggage_Open", normalizedTime=.3f, speed=1, speedMultiplier=1 };
        public Animator(GameObject source) : base(source) { }
        public AnimatorStateInfo GetCurrentAnimatorStateInfo(int layer) => State;
        public AnimatorStateInfo GetNextAnimatorStateInfo(int layer) => State;
        public void GetCurrentAnimatorClipInfo(int layer, List<AnimatorClipInfo> result) => result.Add(new() { clip=Clip, weight=1 });
        public void GetNextAnimatorClipInfo(int layer, List<AnimatorClipInfo> result) => GetCurrentAnimatorClipInfo(layer,result);
        public bool IsInTransition(int layer) => false;
    }
}
public class Biome : UnityEngine.Component
{
    public Biome(UnityEngine.GameObject source) : base(source) => source.Components[typeof(Biome)] = this;
}
public class Luggage : UnityEngine.Component
{
    public bool IsOpen;
    public UnityEngine.GameObject? activateOnOpen;
    public Luggage(UnityEngine.GameObject source) : base(source)
    { source.Components[typeof(Luggage)] = this; UnityEngine.Object.NativeRegistry.Add(this); }
}
public class RespawnChest : Luggage
{
    public UnityEngine.GameObject? skeleton;
    public RespawnChest(UnityEngine.GameObject source) : base(source) { }
}
namespace PeakReplayLab
{
    internal static class NativeStaticVisualCapability
    { public static bool CrateEventOnly(Luggage source) => false; }
    internal static class CaptureRootLifecycleObserver
    {
        private sealed class Subscription : IDisposable { public void Dispose() { } }
        public static IDisposable Bind(UnityEngine.GameObject root, Action callback) => new Subscription();
    }
    internal enum LuggageEventKind { Discovered, Destroyed, Unclasp, Open, Reclasp, Appearance }
    internal readonly struct LuggageEvent
    {
        public readonly Luggage Source; public readonly int Id, Frame; public readonly LuggageEventKind Kind; public readonly double Time;
        public LuggageEvent(Luggage source, LuggageEventKind kind, double time)
        { Source=source; Id=source.GetInstanceID(); Kind=kind; Time=time; Frame=UnityEngine.Time.frameCount; }
    }
    internal sealed class LuggageEventObserver : IDisposable
    {
        private static Action<LuggageEvent>? listener;
        private readonly Action<LuggageEvent> callback;
        public LuggageEventObserver(Action<LuggageEvent> callback) { this.callback=callback; listener += callback; }
        public static void Emit(Luggage source, LuggageEventKind kind, double time) => listener?.Invoke(new(source,kind,time));
        public void Dispose() => listener -= callback;
    }
    public sealed class ObjectPose
    {
        public float[] Position, Rotation, Scale; public bool Active; public object[] Nodes;
        public ObjectPose(float[] p, float[] r, float[] s, bool active, object[] nodes)
        { Position=p; Rotation=r; Scale=s; Active=active; Nodes=nodes; }
    }
    public sealed class NativeVisualFrame { }
    public sealed class NativeLightFrame { }
    public sealed class CrateFrame
    {
        public string Key="",Kind=""; public bool Open; public ObjectPose Pose=null!;
        public CrateAnimationFrame? Animation; public NativeVisualFrame[]? Visuals; public NativeLightFrame[]? Lights;
    }
    internal static class WorldTrack { public static string Path(UnityEngine.Transform root) => root.gameObject.name; }
    internal static class Capture { public static string ClipKey(UnityEngine.AnimationClip clip, UnityEngine.Object controller) => "clip-open"; }
    internal static class VisualReplica
    {
        public static int Reads, Releases;
        public static bool ThrowNextRead;
        public static ObjectPose Capture(UnityEngine.Transform root)
        {
            Reads++; if (ThrowNextRead) { ThrowNextRead=false; throw new InvalidOperationException("native initial pose not ready"); }
            var p=root.position; var q=root.rotation; var s=root.lossyScale;
            return new(new[]{p.x,p.y,p.z},new[]{q.x,q.y,q.z,q.w},new[]{s.x,s.y,s.z},root.gameObject.activeInHierarchy,Array.Empty<object>());
        }
        public static void ReleaseCapture(UnityEngine.Transform source) => Releases++;
    }
    internal static class NativeVisualAppearance
    { public static NativeVisualFrame[] Capture(UnityEngine.Transform root, NativeVisualFrame[]? previous) => previous ?? Array.Empty<NativeVisualFrame>(); }
    internal static class NativeLightAppearance
    { public static NativeLightFrame[] Capture(UnityEngine.Transform root, NativeLightFrame[]? previous) => previous ?? Array.Empty<NativeLightFrame>(); }
}
