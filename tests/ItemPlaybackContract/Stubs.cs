namespace UnityEngine
{
    public class Object
    {
        private static int nextId;
        private readonly int id = ++nextId;
        public string name = "berry";
        public int GetInstanceID() => id;
        public static implicit operator bool([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Object? value) => value != null;
    }
    public readonly record struct Vector3(float x, float y, float z)
    {
        public static Vector3 Scale(Vector3 a, Vector3 b) => new(a.x * b.x, a.y * b.y, a.z * b.z);
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => new(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
    }
    public struct Quaternion { public static Quaternion identity => new(); }
    public readonly record struct Color(float Value)
    { public static Color operator *(Color a, Color b) => new(a.Value * b.Value); }
    public sealed class MaterialPropertyBlock { public void SetColor(string key, Color color) { } }
    public sealed class Material : Object
    { public bool HasProperty(string key) => true; public Color GetColor(string key) => new(1); }
    public sealed class Renderer : Object
    {
        public bool forceRenderingOff;
        public Material[] sharedMaterials = { new() };
        public void GetPropertyBlock(MaterialPropertyBlock block, int slot) { }
        public void SetPropertyBlock(MaterialPropertyBlock block, int slot) { }
    }
    public sealed class Transform : Object
    {
        public Transform? parent;
        public Vector3 localScale = new(1, 1, 1), localPosition, position;
        public Vector3 lossyScale => parent == null ? localScale : Vector3.Scale(parent.lossyScale, localScale);
        public void SetParent(Transform? value, bool worldPositionStays) { parent = value; }
        public void SetLocalPositionAndRotation(Vector3 position, Quaternion rotation) { localPosition = position; }
    }
    public readonly struct Scene { public bool IsValid() => true; }
    public sealed class GameObject : Object
    {
        public bool activeSelf;
        public int Hides;
        public readonly Transform transform = new();
        public readonly Scene scene = new();
        public void SetActive(bool value) { if (activeSelf && !value) Hides++; activeSelf = value; }
    }
    public sealed class Rigidbody : Object { public Vector3 centerOfMass; }
    public static class Time { public static int frameCount; public static float unscaledTime; }
}
public sealed class ItemCooking : UnityEngine.Object
{ public static UnityEngine.Color GetCookColor(int value) => new(value); }
public sealed class Item : UnityEngine.Object
{
    public static readonly List<Item> ALL_ITEMS = new();
    public readonly UnityEngine.GameObject gameObject = new();
    public readonly UnityEngine.Renderer Renderer = new();
    public ushort itemID;
    public UnityEngine.Vector3 centerOfMass;
    public T? GetComponent<T>() where T : class => null;
    public T[] GetComponentsInChildren<T>(bool inactive) => new object[] { Renderer }.OfType<T>().ToArray();
}
public static class ItemDatabase
{
    public static Item? Prefab;
    public static bool TryGetItem(ushort id, out Item? item) { item = Prefab; return item != null; }
    public static bool TryGetItem(string name, out Item? item) { item = Prefab; return item?.name == name; }
}
namespace PeakReplayLab
{
    public static class ItemCapture { public const int MaximumItems = 8192; }
    public sealed class ObjectPose
    {
        public bool Active = true;
        public int Revision;
        public float[] Position = { 0, 0, 0 }, Scale = { 1, 1, 1 };
    }
    public sealed class NativeRendererFrame { }
    public sealed class NativeLightFrame { }
    public sealed class ItemFrame
    {
        public string Key = "berry", Prefab = "berry", HolderId = "";
        public int ItemId, State;
        public float Cooked;
        public bool RevealBackpackContents;
        public ObjectPose Pose = new();
        public NativeRendererFrame[]? Visuals = { new() };
        public NativeLightFrame[]? Lights = { new() };
        public CreatureIdleAnimation? IdleAnimation;
        public bool SampleTimeKnown,SampleDiscontinuity;
        public double SampleTime;
        public int ClockMode;
    }
    public sealed class InventoryFrame
    { public bool Empty, Backpack; public int Slot, ItemId; public string Instance = ""; }
    public sealed class ActorFrame
    { public bool InventoryKnown, BackpackWorn; public int BackpackType; public string Id = ""; public InventoryFrame[] Inventory = Array.Empty<InventoryFrame>(); }
    public sealed class VisualActor
    {
        public bool Visible = true;
        public ActorFrame? State;
        public readonly UnityEngine.Transform Anchor = new();
        public UnityEngine.Transform BackpackSlot(int slot) => Anchor;
    }
    public sealed class VisualReplica : IDisposable
    {
        public static readonly List<VisualReplica> Created = new();
        public static bool CanStartBuild = true, DeferBuild, FailApply;
        public readonly UnityEngine.GameObject Root = new();
        public readonly List<UnityEngine.Renderer> Renderers = new() { new() };
        public bool IsReady, IsResetting, Retired;
        private bool invalidated = true;
        public int RetainedCost => 8;
        public ObjectPose? LastPose;
        public ObjectPose? LastNextPose;
        public float LastMix;
        public int PoseApplies, Resets;
        public static VisualReplica BeginBuild(UnityEngine.GameObject source, bool shareImmutableMaterials)
        { var copy = new VisualReplica(); Created.Add(copy); return copy; }
        public bool ContinueBuild(PresentationWorkBudget budget)
        { if (DeferBuild) return false; if (!IsReady) IsReady = budget.Build(); return IsReady; }
        public void BeginReuse() { Resets++; IsResetting = true; Hide(); InvalidatePose(); }
        public bool ContinueReuse(PresentationWorkBudget budget)
        { if (budget.Build()) IsResetting = false; return !IsResetting; }
        public void InvalidatePose() { invalidated = true; }
        public void Apply(ObjectPose left, ObjectPose? right, float mix, bool revealAuthoredRenderers)
        {
            if (FailApply) throw new InvalidOperationException("bad pose");
            // Match the real stationary-pose memo: hiding/waking must invalidate it.
            if (!invalidated && ReferenceEquals(LastPose, left) && ReferenceEquals(LastNextPose,right) &&
                (LastMix==mix || right==null || ReferenceEquals(left,right))) return;
            invalidated = false; LastPose = left; LastNextPose=right;LastMix=mix;PoseApplies++; Root.SetActive(left.Active);
            var next=right??left;
            Root.transform.position=UnityEngine.Vector3.Lerp(new(left.Position[0],left.Position[1],left.Position[2]),new(next.Position[0],next.Position[1],next.Position[2]),mix);
        }
        public void Hide() { if (Root.activeSelf) { Root.SetActive(false); invalidated = true; } }
        public void Sleep() { Hide(); if (Root.transform.parent) { Root.transform.SetParent(null, true); invalidated = true; } }
        public void Retire() { Retired = true; Hide(); }
        public void Dispose() => Retire();
        public static void DrainRetired(PresentationWorkBudget budget) { }
        public static void FlushRetired() { }
    }
    // This boundary deliberately matches native TryApply's immutable-request
    // contract: a pending A can complete and return true to a call supplying B.
    public sealed class NativePass<T>
    {
        private T[]? requested;
        private int remaining;
        public bool Preparing;
        public T[]? Applied;
        public int Calls, Restarts;
        public bool Apply(T[]? frames, PresentationWorkBudget budget, bool restart, bool defer)
        {
            Calls++;
            if (restart) { Preparing = false; Restarts++; }
            if (!Preparing && ReferenceEquals(Applied, frames)) return true;
            if (!Preparing) { requested = frames; remaining = Math.Max(1, frames?.Length ?? 0); Preparing = true; }
            if (defer) return false;
            while (remaining > 0) { if (!budget.Build()) return false; remaining--; }
            Applied = requested; Preparing = false; return true;
        }
    }
    public static class NativeVisualAppearance
    {
        public sealed class Playback : IDisposable
        {
            public static bool Defer;
            public static readonly List<Playback> Created = new();
            public readonly NativePass<NativeRendererFrame> Pass = new();
            public bool IsReady;
            public Playback(VisualReplica replica, UnityEngine.GameObject source, bool deferred) { Created.Add(this); }
            public bool ContinueBuild(PresentationWorkBudget budget) { if (!IsReady) IsReady = budget.Build(); return IsReady; }
            public bool TryApply(NativeRendererFrame[]? frames, PresentationWorkBudget budget, bool revealAuthoredRenderers = false, bool restart = false)
                => Pass.Apply(frames, budget, restart, Defer);
            public void ReapplyVisibility(bool revealAuthoredRenderers)
            { if (Pass.Preparing) throw new InvalidOperationException("visibility disturbed an unfinished pass"); }
            public void Dispose() { }
        }
    }
    public static class NativeLightAppearance
    {
        public sealed class Originals : IDisposable
        { public void Hide(UnityEngine.GameObject source) { } public void Enforce() { } public void Dispose() { } }
        public sealed class Playback : IDisposable
        {
            public static bool Defer;
            public static readonly List<Playback> Created = new();
            public readonly NativePass<NativeLightFrame> Pass = new();
            public bool IsReady;
            public Playback(VisualReplica replica, UnityEngine.GameObject source, bool deferred) { Created.Add(this); }
            public bool ContinueBuild(PresentationWorkBudget budget) { if (!IsReady) IsReady = budget.Build(); return IsReady; }
            public bool TryApply(NativeLightFrame[]? frames, PresentationWorkBudget budget, bool restart = false)
                => Pass.Apply(frames, budget, restart, Defer);
            public void Dispose() { }
        }
    }
}
