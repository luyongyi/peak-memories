using System.Reflection;
using UnityEngine;

// Controlled native adapters, not a Unity simulation. Tests execute the real
// directory, capture, lifecycle observer, playback lease, data rules and budgets.
namespace UnityEngine
{
    public class Object
    {
        private static int sequence; private readonly int id = ++sequence;
        public bool Destroyed; public string name = ""; public static long TruthChecks;
        public int GetInstanceID() => id;
        public static implicit operator bool(Object? value) { TruthChecks++; return value != null && !value.Destroyed; }
    }
    public class Component : Object { public GameObject gameObject = null!; public Transform transform => gameObject.transform; }
    public class MonoBehaviour : Component { }
    public sealed class GameObject : Object
    {
        private readonly List<Component> components = new(); private bool active = true;
        public readonly Transform transform;
        public int ActiveReads, ActivationWrites, HierarchyReads, ComponentReads, PoseReads, PoseWrites;
        public bool IsReplica;
        public GameObject(string name = "") { this.name = name; transform = Add(new Transform()); }
        public bool activeSelf { get { ActiveReads++; return active; } }
        public bool activeInHierarchy => active && (!transform.parent || transform.parent!.gameObject.activeInHierarchy);
        public T Add<T>(T value) where T : Component { value.gameObject = this; components.Add(value); return value; }
        public T AddComponent<T>() where T : Component, new()
        { var value = Add(new T()); if (activeInHierarchy) Callback(value, "OnEnable"); return value; }
        public T? GetComponent<T>() where T : Component { ComponentReads++; return components.OfType<T>().FirstOrDefault(); }
        public T[] GetComponentsInChildren<T>(bool includeInactive) where T : Component
        {
            HierarchyReads++; var values = new List<T>(components.OfType<T>());
            foreach (var child in transform.Children)
                if (includeInactive || child.gameObject.activeInHierarchy) values.AddRange(child.gameObject.GetComponentsInChildren<T>(includeInactive));
            return values.ToArray();
        }
        private IEnumerable<GameObject> Tree()
        { yield return this; foreach (var child in transform.Children) foreach (var value in child.gameObject.Tree()) yield return value; }
        public void SetActive(bool value)
        {
            ActivationWrites++; if (active == value) return;
            var before = Tree().ToDictionary(root => root, root => root.activeInHierarchy); active = value;
            foreach (var root in Tree()) if (before[root] != root.activeInHierarchy)
                foreach (var component in root.components.ToArray()) Callback(component, root.activeInHierarchy ? "OnEnable" : "OnDisable");
        }
        private static void Callback(Component component, string method) => component.GetType().GetMethod(method,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(component, null);
        public void NotifyLifecycle() { foreach (var value in components.ToArray()) Callback(value, activeInHierarchy ? "OnEnable" : "OnDisable"); }
    }
    public sealed class Transform : Component
    {
        private Transform? father; private Vector3 p, s = Vector3.one; private Quaternion q = Quaternion.identity;
        public readonly List<Transform> Children = new(); public int FailReads;
        // Unity's Transform.parent is an oblivious reference that uses Unity
        // truthiness for a missing/destroyed parent, rather than nullable flow.
        public Transform parent => father!;
        public Vector3 localPosition { get { gameObject.PoseReads++; return p; } set { p = value; gameObject.PoseWrites++; } }
        public Vector3 localScale { get { gameObject.PoseReads++; return s; } set { s = value; gameObject.PoseWrites++; } }
        public Quaternion localRotation { get { gameObject.PoseReads++; return q; } set { q = value; gameObject.PoseWrites++; } }
        public Vector3 position { get { gameObject.PoseReads++; return father ? father!.position + p * father.lossyScale : p; } }
        public Quaternion rotation { get { gameObject.PoseReads++; return q; } }
        public Vector3 lossyScale { get { gameObject.PoseReads++; return father ? s * father!.lossyScale : s; } }
        public void GetPositionAndRotation(out Vector3 value, out Quaternion orientation)
        { if (FailReads-- > 0) throw new InvalidOperationException("controlled native TRS read failure"); value = position; orientation = rotation; }
        public void SetPositionAndRotation(Vector3 value, Quaternion orientation)
        { p = father ? (value - father!.position) / father.lossyScale : value; q = orientation; gameObject.PoseWrites++; }
        public void SetParent(Transform? value, bool worldPositionStays)
        {
            var worldP = position; var worldS = lossyScale; father?.Children.Remove(this); father = value; father?.Children.Add(this);
            if (worldPositionStays) { SetPositionAndRotation(worldP, q); localScale = father ? worldS / father!.lossyScale : worldS; }
        }
    }
    public struct Vector3 : IEquatable<Vector3>
    {
        public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 one => new(1, 1, 1);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, Vector3 b) => new(a.x * b.x, a.y * b.y, a.z * b.z);
        public static Vector3 operator /(Vector3 a, Vector3 b) => new(a.x / b.x, a.y / b.y, a.z / b.z);
        public bool Equals(Vector3 b) => x == b.x && y == b.y && z == b.z;
        public override bool Equals(object? value) => value is Vector3 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(x, y, z);
        public static bool operator ==(Vector3 a, Vector3 b) => a.Equals(b);
        public static bool operator !=(Vector3 a, Vector3 b) => !a.Equals(b);
    }
    public struct Quaternion : IEquatable<Quaternion>
    {
        public float x, y, z, w; public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new(0, 0, 0, 1);
        public bool Equals(Quaternion b) => x == b.x && y == b.y && z == b.z && w == b.w;
        public override bool Equals(object? value) => value is Quaternion other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(x, y, z, w);
        public static bool operator ==(Quaternion a, Quaternion b) => a.Equals(b);
        public static bool operator !=(Quaternion a, Quaternion b) => !a.Equals(b);
    }
    public class Renderer : Component
    {
        private bool off; public int FlagWrites; public bool enabled = true;
        public bool forceRenderingOff { get => off; set { off = value; FlagWrites++; } }
    }
    public sealed class ParticleSystemRenderer : Renderer { }
}
public sealed class FakeItem : MonoBehaviour
{
    public int NativeActivations, Pickups, NetworkCalls; public bool pickedUp;
    private void OnEnable() => NativeActivations++;
    private void OnDisable() => NativeActivations++;
}
namespace PeakReplayLab
{
    public static class ReplaySafety { public static bool Active; }
    public sealed class NodePose { public string Path = ""; public float[] Position = new float[3], Rotation = new[] { 0f, 0f, 0f, 1f }, Scale = new[] { 1f, 1f, 1f }; }
    public sealed class ObjectPose
    {
        public float[] Position, Rotation, Scale; public bool Active; public NodePose[] Nodes;
        public ObjectPose() : this(new float[3], new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f }) { }
        public ObjectPose(float[] p, float[] r, float[] s, bool active = true) { Position = p; Rotation = r; Scale = s; Active = active; Nodes = Array.Empty<NodePose>(); }
    }
    public sealed class NativeRendererFrame { }
    public sealed class NativeLightFrame { }
    public static class NativeVisualAppearanceRules { public static bool Validate(NativeRendererFrame[]? value) => true; public static long Estimate(NativeRendererFrame[]? value) => 0; }
    public static class NativeLightRules { public static bool Validate(NativeLightFrame[]? value) => true; public static long Estimate(NativeLightFrame[]? value) => 0; }
    public static class CreatureDirectoryRules
    {
        public static ObjectPose StaticActive(ObjectPose old, bool value) => new(old.Position, old.Rotation, old.Scale, value) { Nodes = old.Nodes };
    }
    internal sealed class VisualReplica
    {
        internal static readonly List<VisualReplica> Instances = new();
        internal static bool CanStartBuild = true;
        internal static int Created, Retired, BuildCalls, ApplyCalls, FailBuilds, StepsForNextBuild = 1;
        internal readonly GameObject Root;
        private readonly Dictionary<Renderer, Renderer> clones = new(); private int steps; private bool retired;
        internal bool IsReady => steps == 0;
        private VisualReplica(GameObject source)
        {
            Created++; steps = StepsForNextBuild; StepsForNextBuild = 1;
            Root = new GameObject("controlled inert replica") { IsReplica = true }; Root.SetActive(false);
            foreach (var renderer in source.GetComponentsInChildren<Renderer>(true))
                if (renderer is not ParticleSystemRenderer) clones.Add(renderer, Root.Add(new Renderer { enabled = renderer.enabled, forceRenderingOff = renderer.forceRenderingOff }));
            Instances.Add(this);
        }
        internal static VisualReplica BeginBuild(GameObject source, bool share, int maximumNodes) => new(source);
        internal bool ContinueBuild(PresentationWorkBudget budget)
        {
            BuildCalls++; if (FailBuilds-- > 0) throw new InvalidOperationException("controlled replica build failure");
            while (steps > 0 && budget.Build()) steps--; return IsReady;
        }
        internal bool TryCloneRenderer(Renderer source, out Renderer copy) => clones.TryGetValue(source, out copy!);
        internal void Apply(ObjectPose pose, ObjectPose? right, float mix)
        { ApplyCalls++; Root.transform.SetPositionAndRotation(new(pose.Position[0], pose.Position[1], pose.Position[2]), new(pose.Rotation[0], pose.Rotation[1], pose.Rotation[2], pose.Rotation[3])); Root.transform.localScale = new(pose.Scale[0], pose.Scale[1], pose.Scale[2]); Root.SetActive(pose.Active); }
        internal void Hide() { if (Root.activeSelf) Root.SetActive(false); }
        internal void Retire() { if (retired) return; retired = true; Retired++; Root.Destroyed = true; }
        internal static void Reset() { Instances.Clear(); CanStartBuild = true; Created = Retired = BuildCalls = ApplyCalls = FailBuilds = 0; StepsForNextBuild = 1; }
    }
}
