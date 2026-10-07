using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

internal sealed class RopeReplayCapture : IDisposable
{
    private sealed class Identity { public readonly string Key = "rope:" + Guid.NewGuid().ToString("N"); }
    private sealed class Entry
    {
        public Component Source = null!;
        public string Key = "", Kind = "", Resource = "", SourcePath = "";
        public RopeReplayFrame? Last;
        public RopeBoneVisualizer? Visual;
        public Renderer? Renderer;
        public Transform[] Bones = Array.Empty<Transform>();
        public Vector3[] LineBuffer = Array.Empty<Vector3>();
        public double NextProbe, HotUntil, FailureUntil;
        public bool Dirty = true, HierarchyDirty;
    }
    private static readonly ConditionalWeakTable<Component, Identity> identities = new();
    private readonly Dictionary<int, Entry> entries = new();
    private readonly List<RopeReplayFrame> sampled = new();
    private readonly HashSet<string> reported = new(StringComparer.Ordinal);
    private readonly Action<string>? warning;
    private readonly RopeReplayObserver observer;
    private readonly RopeSceneDiscovery discovery;
    private readonly int scene = SceneManager.GetActiveScene().handle;
    private RopeReplayFrame[] previous = Array.Empty<RopeReplayFrame>();
    private Entry[] ordered = Array.Empty<Entry>();
    private bool disposed, directoryDirty;
    public long ProbeCount { get; private set; }
    public long ColdSkipCount { get; private set; }
    public long ChangedFrameCount { get; private set; }
    public int TrackedCount => entries.Count;

    public RopeReplayCapture(Action<string>? warning = null)
    {
        this.warning = warning;
        discovery = new RopeSceneDiscovery(source => Register(source));
        observer = new RopeReplayObserver(Changed, Synchronized, message => Warn("observer", message));
        try { Scan(); } catch { observer.Dispose(); throw; }
    }
    private void Changed(Component source)
    {
        if (disposed || !source || source.gameObject.scene.handle != scene) return;
        var entry = Register(source);
        if (entry == null) return;
        entry.Dirty = true; entry.HotUntil = Time.timeAsDouble + 1;
        if (source is JungleVine) entry.HierarchyDirty = true;
    }
    private void Synchronized(Rope source, RopeSyncData data)
    {
        // Native packets may resend an already stationary rope. Compare the packet's
        // managed data first; an unchanged packet must not keep all 81 transforms hot.
        if (!source || !entries.TryGetValue(source.GetInstanceID(), out var entry) || entry.Last == null || data.segments == null || entry.Last.Points.Length != data.segments.Length)
        { Changed(source!); return; }
        for (int i = 0; i < data.segments.Length; i++)
        {
            var old = entry.Last.Points[i]; var value = data.segments[i];
            if (!Near(old.Position, new Vector3(value.position.x, value.position.y, value.position.z), .001f) ||
                Math.Abs(Quaternion.Dot(RopeReplayResources.Quat(old.Rotation), value.rotation)) <= .999999f)
            { Changed(source); return; }
        }
    }
    private Entry? Register(Component source)
    {
        if (!source || source.gameObject.scene.handle != scene) return null;
        int id = source.GetInstanceID();
        if (entries.TryGetValue(id, out var entry))
        {
            if (entry.Source) return entry;
            entries.Remove(id); directoryDirty = true; // Unity may reuse a destroyed instance ID.
        }
        string kind = RopeReplayResources.Kind(source);
        if (!RopeReplayAdmission.Record(kind, source.gameObject.activeInHierarchy, source is JungleVine && source.GetComponent<SpawnedVine>())) return null;
        if (entries.Count >= RopeReplayRules.MaximumEntities)
        {
            int? victim = null;
            foreach (var pair in entries)
                if (RopeReplayAdmission.CanReplace(pair.Value.Source, pair.Value.Source && pair.Value.Source.gameObject.activeInHierarchy, pair.Value.Last?.Visible ?? false))
                { victim = pair.Key; break; }
            if (victim.HasValue) { entries.Remove(victim.Value); directoryDirty = true; }
            else { Warn("limit", "同时活动的动态绳索实体达到 256 个上限，超出部分未记录。"); return null; }
        }
        entry = new Entry { Source = source, Key = identities.GetValue(source, _ => new Identity()).Key,
            Kind = kind, Resource = RopeReplayResources.Name(source), SourcePath = WorldTrack.Path(source.transform), HotUntil = Time.timeAsDouble + 1 };
        if (source is Rope)
        {
            entry.Visual = source.GetComponentInChildren<RopeBoneVisualizer>(true);
            if (entry.Visual && entry.Visual.boneRoot)
                entry.Bones = RopeReplayResources.Bones(entry.Visual.boneRoot.transform);
            entry.Renderer = source.GetComponentInChildren<SkinnedMeshRenderer>(true);
        }
        else if (source is RescueHook hook) entry.Renderer = hook.line;
        else entry.Renderer = source.GetComponentInChildren<Renderer>(true);
        entries.Add(id, entry); directoryDirty = true;
        return entry;
    }
    private void Scan()
    {
        // Once at recorder initialization only. Subsequent births are observed; the
        // sparse fallback walks a bounded number of hierarchy edges on each frame.
        foreach (int key in entries.Where(pair => !pair.Value.Source).Select(pair => pair.Key).ToArray())
        { entries.Remove(key); directoryDirty = true; }
        foreach (var source in Object.FindObjectsByType<Rope>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Register(source);
        foreach (var source in Object.FindObjectsByType<JungleVine>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Register(source);
        foreach (var source in Object.FindObjectsByType<RescueHook>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Register(source);
        foreach (var source in Object.FindObjectsByType<RopeAnchor>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Register(source);
    }
    public RopeReplayFrame[] Capture(double now)
    {
        if (disposed) throw new ObjectDisposedException(nameof(RopeReplayCapture));
        discovery.Tick(now);
        // These are at most 256 managed entries, not a scene-wide native search.
        foreach (var entry in ordered) if (!entry.Source) { directoryDirty = true; break; }
        if (directoryDirty)
            foreach (int id in entries.Where(pair => !pair.Value.Source).Select(pair => pair.Key).ToArray()) entries.Remove(id);
        if (directoryDirty) { ordered = entries.Values.OrderBy(e => e.Key, StringComparer.Ordinal).ToArray(); directoryDirty = false; }
        sampled.Clear();
        foreach (var entry in ordered)
        {
            if (!entry.Source) continue;
            if (now < entry.FailureUntil)
            { if (entry.Last != null) sampled.Add(entry.Last); ColdSkipCount++; continue; }
            if (entry.Last != null && !entry.Dirty && now > entry.HotUntil && now < entry.NextProbe)
            { sampled.Add(entry.Last); ColdSkipCount++; continue; }
            try
            {
                ProbeCount++;
                var frame = Read(entry);
                entry.FailureUntil = 0;
                entry.Dirty = false;
                entry.NextProbe = now + .5 + (entry.Source.GetInstanceID() & 7) * .017;
                if (!ReferenceEquals(frame, entry.Last)) { ChangedFrameCount++; entry.HotUntil = Math.Max(entry.HotUntil, now + .5); }
                entry.Last = frame; sampled.Add(frame);
            }
            catch (Exception e)
            {
                entry.Dirty = false; entry.NextProbe = now + 1; entry.HotUntil = 0; entry.FailureUntil = now + 1;
                if (entry.Last != null) sampled.Add(entry.Last);
                Warn("capture:" + entry.Resource, "绳索尚未就绪，保留上一有效状态：" + entry.Resource + " · " + e.Message);
            }
        }
        bool same = sampled.Count == previous.Length;
        if (same) for (int i = 0; i < sampled.Count; i++) if (!ReferenceEquals(sampled[i], previous[i])) { same = false; break; }
        if (!same) previous = sampled.ToArray();
        return previous;
    }

    private static RopeReplayFrame Read(Entry entry)
    {
        var source = entry.Source; var old = entry.Last;
        if (entry.HierarchyDirty) { VisualReplica.InvalidateCapture(source.transform, true); entry.HierarchyDirty = false; }
        var visualRoot = source is RescueHook hookRoot && hookRoot.line ? hookRoot.line.transform : source.transform;
        var pose = entry.Kind == "anchor" || entry.Kind == "vine" ? VisualReplica.Capture(source.transform) : Root(visualRoot, old?.Pose);
        bool visible = source.gameObject.activeInHierarchy;
        string collision = "none"; int attachment = 0;
        float width = 0, cutoff = 1, hang = 0, lengthScale = 1, jitter = 0;
        RopeReplayPoint[] points = Array.Empty<RopeReplayPoint>(), bones = Array.Empty<RopeReplayPoint>();
        if (source is Rope rope)
        {
            attachment = (int)rope.attachmenState; collision = "capsule";
            points = SampleTransforms(rope.GetRopeSegments(), old?.Points, true);
            if (entry.Bones.Length == 0 && entry.Visual && entry.Visual.boneRoot)
                entry.Bones = RopeReplayResources.Bones(entry.Visual.boneRoot.transform);
            bones = SampleTransforms(entry.Bones, old?.Bones, false);
            visible &= entry.Renderer && entry.Renderer.enabled && points.Length > 0;
            if (entry.Renderer && entry.Renderer.sharedMaterial) cutoff = Float(entry.Renderer.sharedMaterial, "_RopeCutoff", 1);
            width = points.Length > 0 ? points[0].Radius * 2 : 0;
        }
        else if (source is JungleVine vine)
        {
            var transforms = vine.colliderRoot;
            int count = transforms ? transforms.childCount : 0;
            if (count > 50) throw new InvalidOperationException("藤索超过原生 50 段。");
            var changed = new List<RopeReplayPoint>(count);
            bool same = old != null && old.Points.Length == count;
            for (int i = 0; i < count; i++)
            {
                var point = Sample(transforms!.GetChild(i), same ? old!.Points[i] : null, true);
                changed.Add(point);
                if (same && !ReferenceEquals(point, old!.Points[i])) same = false;
            }
            points = same ? old!.Points : changed.ToArray();
            collision = vine.colliderType == JungleVine.ColliderType.Box ? "box" : "capsule"; attachment = 2;
            visible &= entry.Renderer && entry.Renderer.enabled && count > 1;
            if (entry.Renderer && entry.Renderer.sharedMaterial)
            {
                var material = entry.Renderer.sharedMaterial;
                hang = Float(material, "_Hang", 0); lengthScale = Float(material, "_LengthScale", 1); jitter = Float(material, "_JitterAmount", 0);
            }
            width = count > 0 ? points[0].Radius * 2 : 0;
        }
        else if (source is RescueHook hook)
        {
            var line = hook.line;
            visible &= line && line.gameObject.activeInHierarchy && line.enabled;
            int count = visible ? line!.positionCount : 0;
            if (count > RopeReplayRules.MaximumPoints) throw new InvalidOperationException("钩爪折线超过 128 点。");
            if (entry.LineBuffer.Length != count) entry.LineBuffer = new Vector3[count];
            if (count > 0) line!.GetPositions(entry.LineBuffer);
            var widthCurve = line ? line.widthCurve : null;
            float widthMultiplier = line ? line.widthMultiplier : 0;
            bool same = old != null && old.Points.Length == count;
            RopeReplayPoint[]? changed = null;
            for (int i = 0; i < count; i++)
            {
                Vector3 p = line!.useWorldSpace ? entry.LineBuffer[i] : line.transform.TransformPoint(entry.LineBuffer[i]);
                float radius = Math.Abs(widthMultiplier * widthCurve!.Evaluate(count < 2 ? 0 : (float)i / (count - 1))) * .5f;
                var prior = old != null && i < old.Points.Length ? old.Points[i] : null;
                var point = Point(p, Quaternion.identity, Vector3.one, radius, 0, prior);
                if (!ReferenceEquals(point, prior) || !same)
                {
                    if (changed == null) { changed = new RopeReplayPoint[count]; for (int j = 0; j < i; j++) changed[j] = old!.Points[j]; }
                    changed[i] = point; same = false;
                }
                else if (changed != null) changed[i] = point;
            }
            points = same ? old!.Points : changed ?? Array.Empty<RopeReplayPoint>();
            width = count > 0 ? points[0].Radius * 2 : 0; attachment = visible ? 2 : 0;
        }
        else if (source is RopeAnchor anchor) { attachment = anchor.Ghost ? 1 : 2; }
        if (old != null && ReferenceEquals(pose, old.Pose) && ReferenceEquals(points, old.Points) && ReferenceEquals(bones, old.Bones) &&
            old.Visible == visible && old.Attachment == attachment && old.CollisionKind == collision && Same(old.Width, width) &&
            Same(old.Cutoff, cutoff) && Same(old.Hang, hang) && Same(old.LengthScale, lengthScale) && Same(old.Jitter, jitter)) return old;
        return new RopeReplayFrame { Key = entry.Key, Kind = entry.Kind, Resource = entry.Resource, SourcePath = entry.SourcePath,
            Pose = pose, Visible = visible, Attachment = attachment, CollisionKind = collision, Points = points, Bones = bones,
            Width = width, Cutoff = cutoff, Hang = hang, LengthScale = lengthScale, Jitter = jitter };
    }
    private static RopeReplayPoint[] SampleTransforms(IList<Transform> sources, RopeReplayPoint[]? old, bool collision)
    {
        int max = collision ? 40 : RopeReplayRules.MaximumBones;
        if (sources.Count > max) throw new InvalidOperationException("绳索节点超过原生上限。");
        bool same = old != null && old.Length == sources.Count;
        RopeReplayPoint[]? changed = null;
        for (int i = 0; i < sources.Count; i++)
        {
            var prior = old != null && i < old.Length ? old[i] : null;
            var point = Sample(sources[i], prior, collision);
            if (!ReferenceEquals(point, prior) || !same)
            {
                if (changed == null) { changed = new RopeReplayPoint[sources.Count]; for (int j = 0; j < i; j++) changed[j] = old![j]; }
                changed[i] = point; same = false;
            }
            else if (changed != null) changed[i] = point;
        }
        return same ? old! : changed ?? Array.Empty<RopeReplayPoint>();
    }
    private static RopeReplayPoint Sample(Transform transform, RopeReplayPoint? old, bool collider)
    {
        var p = transform.position; var q = transform.rotation; var scale = transform.lossyScale;
        float radius = 0, height = 0;
        if (collider && transform.TryGetComponent<CapsuleCollider>(out var capsule))
        {
            p = transform.TransformPoint(capsule.center);
            float along = capsule.direction == 0 ? scale.x : capsule.direction == 1 ? scale.y : scale.z;
            float across = capsule.direction == 0 ? Math.Max(Math.Abs(scale.y), Math.Abs(scale.z)) : capsule.direction == 1 ? Math.Max(Math.Abs(scale.x), Math.Abs(scale.z)) : Math.Max(Math.Abs(scale.x), Math.Abs(scale.y));
            radius = capsule.radius * across; height = Math.Max(capsule.height * Math.Abs(along), radius * 2);
        }
        else if (collider && transform.TryGetComponent<BoxCollider>(out var box))
        { p = transform.TransformPoint(box.center); scale = Vector3.Scale(scale, box.size); height = Math.Abs(scale.y); }
        return Point(p, q, scale, radius, height, old);
    }
    private static RopeReplayPoint Point(Vector3 p, Quaternion q, Vector3 scale, float radius, float height, RopeReplayPoint? old)
    {
        if (old != null && Near(old.Position, p, .001f) && Math.Abs(Quaternion.Dot(RopeReplayResources.Quat(old.Rotation), q)) > .999999f &&
            Near(old.Scale, scale, .00001f) && Same(old.Radius, radius) && Same(old.Height, height)) return old;
        return new RopeReplayPoint(V(p), Q(q), V(scale)) { Radius = radius, Height = height };
    }
    private static ObjectPose Root(Transform source, ObjectPose? old)
    {
        var p = source.position; var q = source.rotation; var s = source.lossyScale; bool active = source.gameObject.activeInHierarchy;
        if (old != null && old.Active == active && Near(old.Position, p, .001f) && Math.Abs(Quaternion.Dot(RopeReplayResources.Quat(old.Rotation), q)) > .999999f && Near(old.Scale, s, .00001f)) return old;
        return new ObjectPose { Position = V(p), Rotation = Q(q), Scale = V(s), Active = active };
    }
    private static bool Near(float[] p, Vector3 v, float tolerance) => Math.Abs(p[0] - v.x) <= tolerance && Math.Abs(p[1] - v.y) <= tolerance && Math.Abs(p[2] - v.z) <= tolerance;
    private static bool Same(float a, float b) => Math.Abs(a - b) < .00001f;
    private static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };
    private static float[] Q(Quaternion q) => new[] { q.x, q.y, q.z, q.w };
    private static float Float(Material source, string property, float fallback) => source.HasProperty(property) ? source.GetFloat(property) : fallback;
    private void Warn(string key, string message) { if (reported.Count < 128 && reported.Add(key)) try { warning?.Invoke(message); } catch { } }
    public void Dispose()
    { if (disposed) return; disposed = true; observer.Dispose(); discovery.Clear(); entries.Clear(); sampled.Clear(); previous = Array.Empty<RopeReplayFrame>(); ordered = Array.Empty<Entry>(); }
}

// A sparse safety net for third-party spawning paths. No Resources/FindObjects
// scan in Tick: each rendered frame visits at most 64 nodes/child edges, and a
// completed pass sleeps 30 seconds. Hidden biome subtrees are not traversed.
internal sealed class RopeSceneDiscovery
{
    private readonly Action<Component> found;
    private readonly Stack<(Transform Node, int Child)> pending = new();
    private readonly int scene = SceneManager.GetActiveScene().handle;
    private double nextPass = Time.timeAsDouble + 30;
    public RopeSceneDiscovery(Action<Component> found) => this.found = found;
    public void Tick(double now)
    {
        if (pending.Count == 0)
        {
            if (now < nextPass || SceneManager.GetActiveScene().handle != scene) return;
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects()) if (root.activeInHierarchy) pending.Push((root.transform, -1));
            nextPass = now + 30;
        }
        for (int work = 0; work < 64 && pending.Count > 0; work++)
        {
            var visit = pending.Pop(); var node = visit.Node;
            if (!node || !node.gameObject.activeInHierarchy) continue;
            int child = visit.Child;
            if (child < 0)
            {
                if (node.TryGetComponent<Rope>(out var rope)) found(rope);
                if (node.TryGetComponent<RescueHook>(out var hook)) found(hook);
                if (node.TryGetComponent<RopeAnchor>(out var anchor)) found(anchor);
                if (node.TryGetComponent<JungleVine>(out var vine)) found(vine);
                child = 0;
            }
            if (child < node.childCount)
            { pending.Push((node, child + 1)); pending.Push((node.GetChild(child), -1)); }
        }
        if (pending.Count == 0) nextPass = now + 30;
    }
    public void Clear() => pending.Clear();
}

internal static class RopeReplayResources
{
    public static string Kind(Component source) => source is Rope ? "rope" : source is JungleVine ? "vine" : source is RescueHook ? "hook" : source is RopeAnchor ? "anchor" : "";
    public static string Name(Component source) => source.name.Replace("(Clone)", "").Trim();
    public static Vector3 Point(float[] value) => new(value[0], value[1], value[2]);
    public static Quaternion Quat(float[] value) => new Quaternion(value[0], value[1], value[2], value[3]).normalized;
    public static Transform[] Bones(Transform root) => root.GetComponentsInChildren<Transform>(true).Where(t => t != root).ToArray();
    public static Transform Match(Transform source, Transform sourceRoot, Transform clone)
    {
        var path = new Stack<(int Index, string Name)>();
        for (var t = source; t != sourceRoot; t = t.parent)
        { if (!t || !t.parent) throw new InvalidOperationException("Visual bone lies outside resource root."); path.Push((t.GetSiblingIndex(), t.name)); }
        foreach (var part in path)
        {
            if (part.Index >= clone.childCount) throw new InvalidOperationException("Visual bone hierarchy differs from the game resource.");
            clone = clone.GetChild(part.Index);
            if (clone.name != part.Name) throw new InvalidOperationException("Visual bone name differs from the game resource.");
        }
        return clone;
    }
}

// Observers enqueue dirtiness only. No RPC, Instantiate, physics, interaction or item use.
internal sealed class RopeReplayObserver : IDisposable
{
    private static readonly List<RopeReplayObserver> observers = new();
    private readonly Action<Component> callback;
    private readonly Action<Rope, RopeSyncData>? synchronized;
    private readonly bool presentation;
    private static readonly Harmony harmony = new("cn.mylus.peakreplaylab.rope-observer");
    private readonly List<string> failedHooks = new();
    private bool disposed;
    public RopeReplayObserver(Action<Component> callback, Action<Rope, RopeSyncData>? synchronized, Action<string>? warning = null, bool presentation = false)
    {
        this.callback = callback; this.synchronized = synchronized; this.presentation = presentation;
        observers.Add(this);
        if (observers.Count > 1) return;
        Patch(typeof(Rope), "Awake"); Patch(typeof(Rope), "AttachToSpool_Rpc"); Patch(typeof(Rope), "AttachToAnchor_Rpc");
        Patch(typeof(Rope), "OnEnable");
        Patch(typeof(Rope), "Detach_Rpc"); Patch(typeof(Rope), "AddSegment"); Patch(typeof(Rope), "RemoveSegment");
        Patch(typeof(Rope), "AddCharacterClimbing"); Patch(typeof(Rope), "RemoveCharacterClimbing"); Patch(typeof(Rope), "SetSyncData", nameof(Synchronized));
        Patch(typeof(JungleVine), "Awake"); Patch(typeof(JungleVine), "ForceBuildVine");
        Patch(typeof(RopeAnchor), "Awake"); Patch(typeof(RopeAnchorProjectile), "GetShot", nameof(Anchor));
        Patch(typeof(RescueHook), "Awake"); Patch(typeof(RescueHook), "RPCA_RescueWall");
        Patch(typeof(RescueHook), "RPCA_RescueCharacter"); Patch(typeof(RescueHook), "RPCA_LetGo");
        Patch(typeof(Item), "OnEnable", nameof(ItemEnabled));
        if (failedHooks.Count > 0)
            try { warning?.Invoke("部分绳索事件观察不可用，保留已生效观察器并使用稀疏分帧发现兜底；短时变化可能遗漏：" + string.Join("、", failedHooks)); } catch { }
    }
    private void Patch(Type type, string name, string observer = nameof(Changed))
    {
        try
        {
            var method = AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.Name, name);
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(RopeReplayObserver), observer));
        }
        catch (Exception e) { failedHooks.Add(type.Name + "." + name + " (" + e.GetType().Name + ")"); }
    }
    private static void Changed(Component __instance)
    {
        if (!__instance) return;
        foreach (var observer in observers)
            try { if (!observer.disposed && observer.presentation == ReplaySafety.Active) observer.callback(__instance); } catch { }
    }
    private static void ItemEnabled(Item __instance)
    { if (__instance && __instance.TryGetComponent<RescueHook>(out var hook)) Changed(hook); }
    private static void Anchor(RopeAnchorProjectile __instance)
    { if (__instance) Changed(__instance.GetComponent<RopeAnchor>()); }
    private static void Synchronized(Rope __instance, RopeSyncData data)
    {
        if (!__instance) return;
        foreach (var observer in observers)
            try { if (!observer.disposed && observer.presentation == ReplaySafety.Active) observer.synchronized?.Invoke(__instance, data); } catch { }
    }
    public void Dispose()
    { if (disposed) return; disposed = true; observers.Remove(this); if (observers.Count == 0) harmony.UnpatchSelf(); }
}

internal sealed class RopeReplayPlayback : IDisposable
{
    private sealed class Entry : IDisposable
    {
        public VisualReplica? Replica;
        public GameObject? LineRoot;
        public LineRenderer? Line;
        public Renderer[] Renderers = Array.Empty<Renderer>();
        public Transform[] Bones = Array.Empty<Transform>();
        public readonly List<Material> Materials = new();
        public Material? Normal, Ghost;
        public RopeReplayFrame? Left, Right;
        public long LastUsed;
        public float Mix;
        public Vector3[] Positions = Array.Empty<Vector3>();
        public bool Failed;
        public void Hide() { Replica?.Hide(); if (LineRoot && LineRoot.activeSelf) LineRoot.SetActive(false); Left = Right = null; }
        public void Dispose()
        { Replica?.Dispose(); if (LineRoot) { LineRoot.SetActive(false); Object.Destroy(LineRoot); } foreach (var m in Materials) if (m) Object.Destroy(m); Materials.Clear(); }
    }
    private readonly Action<string>? warning;
    private readonly Dictionary<string, Component> resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Component> sceneSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> retryAfter = new(StringComparer.Ordinal);
    private readonly Dictionary<Renderer, bool> originals = new();
    private readonly Dictionary<string, RopeReplayFrame> next = new(StringComparer.Ordinal);
    private readonly HashSet<string> present = new(StringComparer.Ordinal), wanted = new(StringComparer.Ordinal), reported = new(StringComparer.Ordinal);
    private RopeReplayFrame[]? previousRight;
    private readonly RopeReplayObserver observer;
    private readonly RopeSceneDiscovery discovery;
    private double nextRetryCleanup;
    private int enforcedFrame = -1;
    private long useSerial;
    private bool disposed, started;
    public int VisualCount => entries.Count;
    public long ApplyCount { get; private set; }
    public long SharedPoseSkipCount { get; private set; }
    public RopeReplayPlayback(Action<string>? warning = null)
    {
        this.warning = warning;
        discovery = new RopeSceneDiscovery(AddResource);
        observer = new RopeReplayObserver(AddResource, null, message => Warn("observer", message), presentation: true);
        // Native Photon spawn names verified in this build, never a file-provided path.
        // Loading a prefab asset does not instantiate it or run its MonoBehaviours.
        try
        {
            // Exact keys from this build's ResourceManager.m_Container. These assets
            // must exist even on a fresh process before the user has fired a launcher.
            foreach (string name in new[] { "ropedynamic", "ropedynamic2 variant", "ropedynamicantigrav", "ropedynamicbreakable", "ropedynamichelicopter",
                "ropeanchor", "ropeanchoranti", "ropeanchorforropeshooter", "ropeanchorforropeshooteranti", "ropeanchorhelicopter",
                "ropeanchorwithantirope", "ropeanchorwithrope", "ropeanchorwithropebreakable", "chainshootable",
                "0_items/rescuehook", "0_items/rescuehook_infinite" })
            {
                var asset = Resources.Load<GameObject>(name);
                if (!asset) continue;
                if (asset.TryGetComponent<Rope>(out var rope)) AddResource(rope);
                if (asset.TryGetComponent<RopeAnchor>(out var anchor)) AddResource(anchor);
                if (asset.TryGetComponent<JungleVine>(out var vine)) AddResource(vine);
                if (asset.TryGetComponent<RescueHook>(out var hook)) AddResource(hook);
            }
            RefreshResources();
        }
        catch { Dispose(); throw; }
    }

    private void RefreshResources()
    {
        // Only known native component families enter this allowlist. A recording cannot
        // ask Resources.Load to instantiate an arbitrary path or active game prefab.
        foreach (var source in Resources.FindObjectsOfTypeAll<Rope>()) AddResource(source);
        foreach (var source in Resources.FindObjectsOfTypeAll<JungleVine>()) AddResource(source);
        foreach (var source in Resources.FindObjectsOfTypeAll<RescueHook>()) AddResource(source);
        foreach (var source in Resources.FindObjectsOfTypeAll<RopeAnchor>()) AddResource(source);
    }
    private void AddResource(Component source)
    {
        if (!source) return;
        string key = RopeReplayResources.Kind(source) + ":" + RopeReplayResources.Name(source);
        bool sceneObject = source.gameObject.scene.IsValid() && source.gameObject.scene.isLoaded;
        if (!resources.TryGetValue(key, out var old) || !old || !sceneObject && old.gameObject.scene.IsValid()) resources[key] = source;
        if (!sceneObject) return;
        sceneSources[WorldTrack.Path(source.transform)] = source;
        // Native map bridges/chains are scenery, not playback objects. Version 0.4.0
        // incorrectly hid all of them; only a legacy frame addressing one may hide it.
        if (source is JungleVine && !source.GetComponent<SpawnedVine>()) return;
        HideSource(source);
    }
    private void HideSource(Component source)
    {
        if (!source) return;
        if (source is RescueHook hook)
        { if (hook.line && !originals.ContainsKey(hook.line)) originals.Add(hook.line, hook.line.forceRenderingOff); }
        else foreach (var renderer in source.GetComponentsInChildren<Renderer>(true))
            if (renderer && !originals.ContainsKey(renderer)) originals.Add(renderer, renderer.forceRenderingOff);
    }
    public void Apply(RopeReplayFrame[] left, RopeReplayFrame[] right, float mix, double time, bool discontinuity)
    {
        if (disposed) throw new ObjectDisposedException(nameof(RopeReplayPlayback));
        started |= left.Length > 0 || right.Length > 0;
        if (!ReferenceEquals(previousRight, right))
        { next.Clear(); foreach (var frame in right) if (!next.ContainsKey(frame.Key)) next.Add(frame.Key, frame); previousRight = right; }
        wanted.Clear(); foreach (var frame in left) wanted.Add(frame.Key);
        present.Clear();
        double now = Time.unscaledTimeAsDouble;
        if (now >= nextRetryCleanup)
        {
            foreach (string key in retryAfter.Where(pair => pair.Value <= now && !wanted.Contains(pair.Key)).Select(pair => pair.Key).ToArray()) retryAfter.Remove(key);
            nextRetryCleanup = now + 1;
        }
        foreach (var frame in left)
        {
            if (!present.Add(frame.Key)) continue;
            next.TryGetValue(frame.Key, out var later);
            var state = mix >= 1 && later != null ? later : frame;
            if (state.Kind == "vine" && sceneSources.TryGetValue(state.SourcePath, out var native) && native &&
                RopeReplayResources.Name(native) == state.Resource) HideSource(native);
            if (!state.Visible || !state.Pose.Active) { if (entries.TryGetValue(frame.Key, out var hidden)) hidden.Hide(); continue; }
            if (!entries.TryGetValue(frame.Key, out var entry))
            {
                if (retryAfter.TryGetValue(frame.Key, out double retry) && now < retry) continue;
                if (!resources.TryGetValue(state.Kind + ":" + state.Resource, out var source) || !source)
                { Warn("resource:" + state.Kind + ":" + state.Resource, "未找到匹配的绳索原生资源，将重试：" + state.Resource); continue; }
                if (!MakeRoom()) { Warn("cache-limit", "绳索视觉实体达到 256 个上限，超出部分暂不显示。"); continue; }
                try { entry = Create(source, state); entries.Add(frame.Key, entry); retryAfter.Remove(frame.Key); }
                catch (Exception e)
                {
                    // Resource readiness may improve, but repeated failed cloning must not
                    // allocate a GameObject/material hierarchy on every playback frame.
                    if (retryAfter.Count >= RopeReplayRules.MaximumEntities && !retryAfter.ContainsKey(frame.Key))
                    {
                        string oldest = retryAfter.OrderBy(pair => pair.Value).First().Key;
                        retryAfter.Remove(oldest);
                    }
                    retryAfter[frame.Key] = now + 2;
                    Warn("create:" + state.Key, "绳索视觉创建失败，稍后重试：" + state.Resource + " · " + e.Message); continue;
                }
            }
            entry.LastUsed = ++useSerial;
            if (entry.Failed) continue;
            var following = later != null && RopeReplayRules.CanInterpolate(frame, later) ? later : frame;
            float blend = ReferenceEquals(following, frame) ? 0 : Mathf.Clamp01(mix);
            if (mix >= 1 && later != null) { following = later; blend = 0; }
            var beginning = mix >= 1 && later != null ? later : frame;
            if (!discontinuity && ReferenceEquals(entry.Left, beginning) && ReferenceEquals(entry.Right, following) &&
                (ReferenceEquals(beginning, following) || entry.Mix == blend)) { SharedPoseSkipCount++; continue; }
            try
            {
                ApplyCount++;
                if (entry.Replica != null)
                {
                    if (discontinuity) entry.Replica.InvalidatePose();
                    entry.Replica.Apply(beginning.Pose, following.Pose, blend);
                    if (state.Kind == "rope") ApplyBones(entry, beginning, following, blend);
                    if (state.Kind == "vine") ApplyVineBounds(entry, beginning, following, blend);
                    ApplyMaterials(entry, state);
                }
                else ApplyLine(entry, beginning, following, blend);
                entry.Left = beginning; entry.Right = following; entry.Mix = blend;
            }
            catch (Exception e) { entry.Failed = true; entry.Hide(); Warn("apply:" + frame.Key, "绳索视觉已隔离：" + frame.Resource + " · " + e.Message); }
        }
        foreach (var pair in entries) if (!present.Contains(pair.Key)) pair.Value.Hide();
    }
    private bool MakeRoom()
    {
        if (entries.Count < RopeReplayRules.MaximumEntities) return true;
        string? victim = null; long oldest = long.MaxValue;
        foreach (var pair in entries)
            if (!wanted.Contains(pair.Key) && pair.Value.LastUsed < oldest) { victim = pair.Key; oldest = pair.Value.LastUsed; }
        if (victim == null) return false;
        var removed = entries[victim]; entries.Remove(victim);
        try { removed.Dispose(); } catch (Exception e) { Warn("evict:" + victim, "旧绳索视觉释放失败：" + e.Message); }
        return true;
    }
    private static Entry Create(Component source, RopeReplayFrame frame)
    {
        var entry = new Entry();
        try
        {
            if (source is RescueHook hook)
            {
                if (!hook.line) throw new InvalidOperationException("Native RescueHook line is missing.");
                var original = hook.line;
                entry.LineRoot = new GameObject("PEAK Replay hook line"); entry.LineRoot.SetActive(false);
                var line = entry.LineRoot.AddComponent<LineRenderer>(); entry.Line = line;
                line.useWorldSpace = true; line.loop = original.loop; line.alignment = original.alignment;
                line.textureMode = original.textureMode; line.numCapVertices = original.numCapVertices; line.numCornerVertices = original.numCornerVertices;
                line.generateLightingData = original.generateLightingData; line.shadowCastingMode = original.shadowCastingMode; line.receiveShadows = original.receiveShadows;
                line.colorGradient = original.colorGradient; line.widthMultiplier = 1;
                line.sharedMaterials = original.sharedMaterials.Select(m => CopyMaterial(m, entry)).ToArray();
                entry.Renderers = new Renderer[] { line };
            }
            else
            {
                entry.Replica = new VisualReplica(source.gameObject);
                entry.Renderers = entry.Replica.Root.GetComponentsInChildren<Renderer>(true);
                if (source is Rope)
                {
                    var visual = source.GetComponentInChildren<RopeBoneVisualizer>(true);
                    if (!visual || !visual.boneRoot) throw new InvalidOperationException("Native rope bones are missing.");
                    entry.Bones = RopeReplayResources.Bones(visual.boneRoot.transform)
                        .Select(bone => RopeReplayResources.Match(bone, source.transform, entry.Replica.Root.transform)).ToArray();
                    if (entry.Bones.Length != frame.Bones.Length) throw new InvalidOperationException("Recorded/native rope bone counts differ.");
                    entry.Normal = CopyMaterial(visual.ropeMaterial, entry); entry.Ghost = CopyMaterial(visual.ghostMaterial, entry);
                    foreach (var renderer in entry.Renderers) if (renderer is SkinnedMeshRenderer skin) skin.updateWhenOffscreen = true;
                }
            }
            return entry;
        }
        catch { entry.Dispose(); throw; }
    }
    private static Material CopyMaterial(Material source, Entry entry)
    { if (!source) throw new InvalidOperationException("Native rope material is missing."); var copy = new Material(source); entry.Materials.Add(copy); return copy; }
    private static void ApplyBones(Entry entry, RopeReplayFrame left, RopeReplayFrame right, float mix)
    {
        if (entry.Bones.Length != left.Bones.Length || right.Bones.Length != left.Bones.Length) throw new InvalidOperationException("Rope bone topology changed.");
        // Resource traversal is parent-first; setting children first would move them again
        // when the parent is updated. All data is actual observed world pose.
        for (int i = 0; i < entry.Bones.Length; i++)
        {
            var a = left.Bones[i]; var b = right.Bones[i]; var bone = entry.Bones[i];
            bone.SetPositionAndRotation(Vector3.Lerp(RopeReplayResources.Point(a.Position), RopeReplayResources.Point(b.Position), mix),
                Quaternion.Slerp(RopeReplayResources.Quat(a.Rotation), RopeReplayResources.Quat(b.Rotation), mix));
            Vector3 scale = Vector3.Lerp(RopeReplayResources.Point(a.Scale), RopeReplayResources.Point(b.Scale), mix);
            var parent = bone.parent ? bone.parent.lossyScale : Vector3.one;
            bone.localScale = new Vector3(Divide(scale.x, parent.x), Divide(scale.y, parent.y), Divide(scale.z, parent.z));
        }
    }
    private static float Divide(float value, float divisor) => Math.Abs(divisor) > .000001f ? value / divisor : 0;
    private static void ApplyVineBounds(Entry entry, RopeReplayFrame left, RopeReplayFrame right, float mix)
    {
        if (left.Points.Length == 0) return;
        // JungleVine.SetRendererBounds expands the native renderer after its shader
        // bends the mesh. A prefab clone has not run that method: reuse the observed
        // real path for culling bounds, without re-running its generation/physics.
        Vector3 first = RopeReplayResources.Point(left.Points[0].Position);
        var bounds = new Bounds(first, Vector3.zero);
        for (int i = 0; i < left.Points.Length; i++)
            bounds.Encapsulate(Vector3.Lerp(RopeReplayResources.Point(left.Points[i].Position), RopeReplayResources.Point(right.Points[i].Position), mix));
        bounds.Expand(Math.Max(1, left.Width * 2));
        foreach (var renderer in entry.Renderers) if (renderer) renderer.bounds = bounds;
    }
    private static void ApplyMaterials(Entry entry, RopeReplayFrame state)
    {
        foreach (var renderer in entry.Renderers)
        {
            if (state.Kind == "rope" && renderer is SkinnedMeshRenderer)
            {
                var material = state.Attachment == 1 ? entry.Ghost : entry.Normal;
                if (renderer.sharedMaterial != material) renderer.sharedMaterial = material;
            }
            foreach (var material in renderer.sharedMaterials)
            {
                if (!material) continue;
                Set(material, "_RopeCutoff", state.Cutoff); Set(material, "_Hang", state.Hang);
                Set(material, "_LengthScale", state.LengthScale); Set(material, "_JitterAmount", state.Jitter);
            }
        }
    }
    private static void Set(Material material, string name, float value) { if (material.HasProperty(name)) material.SetFloat(name, value); }
    private static void ApplyLine(Entry entry, RopeReplayFrame left, RopeReplayFrame right, float mix)
    {
        int count = left.Points.Length;
        if (!entry.Line || !entry.LineRoot || count < 2) { entry.Hide(); return; }
        entry.LineRoot.transform.SetPositionAndRotation(Vector3.Lerp(RopeReplayResources.Point(left.Pose.Position), RopeReplayResources.Point(right.Pose.Position), mix),
            Quaternion.Slerp(RopeReplayResources.Quat(left.Pose.Rotation), RopeReplayResources.Quat(right.Pose.Rotation), mix));
        entry.LineRoot.transform.localScale = Vector3.Lerp(RopeReplayResources.Point(left.Pose.Scale), RopeReplayResources.Point(right.Pose.Scale), mix);
        if (entry.Positions.Length != count) entry.Positions = new Vector3[count];
        bool widthsChanged = entry.Left == null || entry.Left.Points.Length != count;
        for (int i = 0; i < count; i++)
        {
            entry.Positions[i] = Vector3.Lerp(RopeReplayResources.Point(left.Points[i].Position), RopeReplayResources.Point(right.Points[i].Position), mix);
            widthsChanged |= entry.Left != null && (entry.Left.Points.Length != count || entry.Left.Points[i].Radius != left.Points[i].Radius);
        }
        if (widthsChanged)
        {
            var keys = new Keyframe[count];
            for (int i = 0; i < count; i++) keys[i] = new Keyframe((float)i / (count - 1), left.Points[i].Radius * 2);
            entry.Line.widthCurve = new AnimationCurve(keys);
        }
        entry.Line.positionCount = count; entry.Line.SetPositions(entry.Positions); entry.Line.enabled = true;
        if (!entry.LineRoot.activeSelf) entry.LineRoot.SetActive(true);
    }
    public void Enforce()
    {
        if (disposed || !started || enforcedFrame == Time.frameCount) return;
        enforcedFrame = Time.frameCount;
        discovery.Tick(Time.unscaledTimeAsDouble);
        foreach (var pair in originals) if (pair.Key && !pair.Key.forceRenderingOff) pair.Key.forceRenderingOff = true;
    }
    private void Warn(string key, string message) { if (reported.Count < 128 && reported.Add(key)) try { warning?.Invoke(message); } catch { } }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        observer.Dispose(); discovery.Clear();
        foreach (var pair in entries) try { pair.Value.Dispose(); } catch (Exception e) { Warn("dispose:" + pair.Key, e.Message); }
        foreach (var pair in originals) try { if (pair.Key) pair.Key.forceRenderingOff = pair.Value; } catch (Exception e) { Warn("restore", e.Message); }
        originals.Clear(); resources.Clear(); sceneSources.Clear(); entries.Clear(); retryAfter.Clear(); next.Clear(); present.Clear(); wanted.Clear(); previousRight = null;
    }
}
