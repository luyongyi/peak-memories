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
    private sealed class ColliderProbe
    {
        public readonly CapsuleCollider? Capsule;
        public readonly BoxCollider? Box;
        public ColliderProbe(Transform source)
        { Capsule = source.GetComponent<CapsuleCollider>(); if (!Capsule) Box = source.GetComponent<BoxCollider>(); }
    }
    private static readonly ConditionalWeakTable<Transform, ColliderProbe> colliderProbes = new();
    private sealed class Entry
    {
        public Component Source = null!;
        public GameObject ScopeRoot = null!;
        public int InstanceId;
        public int ScopeId;
        public string Key = "", Kind = "", Resource = "", SourcePath = "";
        public RopeReplayFrame? Last;
        public RopeBoneVisualizer? Visual;
        public Renderer? Renderer;
        public BreakableBridge? Bridge;
        public Transform[] Bones = Array.Empty<Transform>();
        public Vector3[] LineBuffer = Array.Empty<Vector3>();
        public RopeCaptureLifecycle Lifecycle = new();
        public IList<RopeSyncData.SegmentData>? LatestNativeSegments;
        public int LatestNativeCount = -1;
        public int KnownAttachment, KnownClimbers, KnownTopology;
        public RopeSyncData.SegmentData[] MotionCheckpoint = Array.Empty<RopeSyncData.SegmentData>();
        public double NextMotionCheck, NextVisibilityProbe;
        public float LastRequestedSegments;
        public bool NativeActive, HookActive, HierarchyDirty;
        public bool SceneVine;
        public bool InitialUrgent;
    }
    private static readonly ConditionalWeakTable<Component, Identity> identities = new();
    private readonly Dictionary<int, Entry> entries = new();
    private readonly Dictionary<int, List<int>> scopeEntries = new();
    private readonly SegmentCaptureClient? segments;
    private readonly ConditionalWeakTable<Rope, Entry> ropeEntries = new();
    private readonly ConditionalWeakTable<RopeBoneVisualizer, Entry> visualEntries = new();
    private readonly List<RopeReplayFrame> sampled = new();
    private readonly HashSet<string> reported = new(StringComparer.Ordinal);
    private readonly Action<string>? warning;
    private readonly RopeReplayObserver observer;
    private readonly RopeSceneDiscovery discovery;
    private readonly int scene = SceneManager.GetActiveScene().handle;
    private readonly AccessTools.FieldRef<RescueHook, bool>? hookPulling;
    private readonly AccessTools.FieldRef<Rope, List<Transform>>? simulationSegments;
    private RopeReplayFrame[] previous = Array.Empty<RopeReplayFrame>();
    private Entry[] ordered = Array.Empty<Entry>();
    private bool disposed, directoryDirty;
    private double captureNow;
    public long ProbeCount { get; private set; }
    public long ColdSkipCount { get; private set; }
    public long ChangedFrameCount { get; private set; }
    public long SleepCount { get; private set; }
    public long WakeCount { get; private set; }
    public long ManagedMotionChecks { get; private set; }
    public long VisibilityProbeCount { get; private set; }
    public long PointReadCount { get; private set; }
    public long BoneReadCount { get; private set; }
    public long DiscoveryFallbackTicks { get; private set; }
    public long DegradedSafetyReads { get; private set; }
    public bool DiscoveryFallbackEnabled => !observer.BirthHooksHealthy;
    public int SleepingCount { get; private set; }
    public int TrackedCount => entries.Count;
    internal bool InitialBaselineReady
    { get { foreach (var entry in entries.Values) if (entry.Source && entry.Last == null) return false; return true; } }

    public RopeReplayCapture(Action<string>? warning = null)
    {
        this.warning = warning;
        segments = CaptureSegmentScope.Active?.CreateClient(RegisterObject, Retire, go => go && go.activeInHierarchy);
        captureNow = Time.timeAsDouble;
        try { hookPulling = AccessTools.FieldRefAccess<RescueHook, bool>("isPulling"); }
        catch { Warn("hook-state", "钩爪持续状态不可用，沿用原生开始/结束事件与可见性检查。"); }
        try { simulationSegments = AccessTools.FieldRefAccess<Rope, List<Transform>>("simulationSegments"); }
        catch { Warn("rope-topology", "绳索分段缓存不可用，拓扑事件将读取原生目录计数。"); }
        discovery = new RopeSceneDiscovery(source => Register(source));
        observer = new RopeReplayObserver(Changed, Synchronized, message => Warn("observer", message), localSegments: VisualSynchronized);
        try { Scan(); } catch { observer.Dispose(); segments?.Dispose(); throw; }
    }
    private void Changed(Component source, RopeCaptureEvent kind, double duration)
    {
        if (disposed || !source || source.gameObject.scene.handle != scene) return;
        var entry = Register(source, urgent: kind != RopeCaptureEvent.Baseline);
        if (entry == null) return;
        if (kind == RopeCaptureEvent.Visibility)
        {
            bool active = source.gameObject.activeInHierarchy;
            if (entry.NativeActive == active) return;
            entry.NativeActive = active;
        }
        if (source is Rope rope)
        {
            if (kind == RopeCaptureEvent.Climbing)
            {
                int members = rope.charactersClimbing.Count;
                if (entry.KnownClimbers == members) return;
                entry.KnownClimbers = members;
            }
            if (kind == RopeCaptureEvent.Topology)
            {
                int count = simulationSegments != null ? simulationSegments(rope).Count : rope.GetRopeSegments().Count;
                if (entry.KnownTopology == count) return;
                entry.KnownTopology = count;
            }
            if (kind == RopeCaptureEvent.Attachment)
            {
                int attachment = (int)rope.attachmenState;
                if (entry.KnownAttachment == attachment) return;
                entry.KnownAttachment = attachment;
            }
        }
        else if (source is RopeAnchor anchor && kind == RopeCaptureEvent.Attachment)
        {
            int attachment = anchor.Ghost ? 1 : 2;
            if (entry.KnownAttachment == attachment) return;
            entry.KnownAttachment = attachment;
        }
        if (kind == RopeCaptureEvent.HookStart && entry.HookActive || kind == RopeCaptureEvent.HookEnd && !entry.HookActive) return;
        Notify(entry, Time.timeAsDouble, kind, duration);
        if (source is JungleVine && (kind == RopeCaptureEvent.Topology || kind == RopeCaptureEvent.BridgeFall || kind == RopeCaptureEvent.Visibility))
            entry.HierarchyDirty = true;
        if (kind == RopeCaptureEvent.HookStart) entry.HookActive = true;
        if (kind == RopeCaptureEvent.HookEnd) entry.HookActive = false;
    }
    private void Notify(Entry entry, double now, RopeCaptureEvent kind, double duration = 0)
    {
        bool sleeping = entry.Lifecycle.Sleeping;
        entry.Lifecycle.Notify(now, kind, duration);
        if (kind != RopeCaptureEvent.Baseline) entry.InitialUrgent = true;
        if (sleeping && !entry.Lifecycle.Sleeping) WakeCount++;
    }
    private void Synchronized(Rope source, RopeSyncData data)
    {
        if (disposed || ReferenceEquals(source, null) || data.segments == null) return;
        if (!ropeEntries.TryGetValue(source, out var entry)) entry = Register(source, urgent: true);
        if (entry != null) ObserveNativeSegments(entry, data.segments);
    }
    private void VisualSynchronized(RopeBoneVisualizer source, IList<RopeSyncData.SegmentData> segments)
    {
        // RefreshLocalSegmentData already read these native transforms for the
        // game. Its managed output is a sleeping-rope wake sensor, not a second
        // Transform traversal or an invitation to reset the high-rate window.
        if (!disposed && visualEntries.TryGetValue(source, out var entry)) ObserveNativeSegments(entry, segments);
    }
    private void ObserveNativeSegments(Entry entry, IList<RopeSyncData.SegmentData> segments)
    {
        bool topologyChanged = entry.LatestNativeCount < 0 ? entry.Last != null && entry.Last.Points.Length != segments.Count :
            entry.LatestNativeCount != segments.Count;
        entry.LatestNativeCount = segments.Count;
        entry.LatestNativeSegments = segments;
        // Count received native topologies, not their distance from the last
        // successful capture. Repeated packets during a read failure must not
        // reopen the burst or erase its bounded retry deadline every frame.
        if (topologyChanged)
        { Notify(entry, captureNow, RopeCaptureEvent.Topology); return; }
        if (!entry.Lifecycle.Sleeping || captureNow < entry.NextMotionCheck) return;
        entry.NextMotionCheck = captureNow + .1;
        ManagedMotionChecks++;
        if (entry.MotionCheckpoint.Length != segments.Count)
        { Notify(entry, captureNow, RopeCaptureEvent.Motion); return; }
        for (int i = 0; i < segments.Count; i++)
        {
            var old = entry.MotionCheckpoint[i]; var value = segments[i];
            if (Math.Abs(old.position.x - value.position.x) > .001f || Math.Abs(old.position.y - value.position.y) > .001f ||
                Math.Abs(old.position.z - value.position.z) > .001f || Math.Abs(Quaternion.Dot(old.rotation, value.rotation)) <= .999999f)
            { Notify(entry, captureNow, RopeCaptureEvent.Motion); return; }
        }
    }
    private static void CheckpointNativeSegments(Entry entry)
    {
        var native = entry.LatestNativeSegments;
        if (native == null) return;
        if (entry.MotionCheckpoint.Length != native.Count) entry.MotionCheckpoint = new RopeSyncData.SegmentData[native.Count];
        for (int i = 0; i < native.Count; i++) entry.MotionCheckpoint[i] = native[i];
    }
    private void RegisterObject(GameObject source)
    {
        if (disposed || !source || !(segments?.Admit(source) ?? true)) return;
        foreach (var component in source.GetComponents<Component>())
            if (component is Rope or JungleVine or RescueHook or RopeAnchor) Register(component);
    }
    private void RemoveEntry(int id)
    {
        if (!entries.TryGetValue(id, out var entry)) return;
        entries.Remove(id); directoryDirty = true;
        if (scopeEntries.TryGetValue(entry.ScopeId, out var ids))
        { ids.Remove(id); if (ids.Count == 0) scopeEntries.Remove(entry.ScopeId); }
        if (entry.Source is Rope rope) ropeEntries.Remove(rope);
        if (!ReferenceEquals(entry.Visual, null)) visualEntries.Remove(entry.Visual!);
        if (entry.Source) VisualReplica.ReleaseCapture(entry.Source.transform);
        entry.Last = null; entry.Bones = Array.Empty<Transform>(); entry.LineBuffer = Array.Empty<Vector3>();
        entry.LatestNativeSegments = null; entry.MotionCheckpoint = Array.Empty<RopeSyncData.SegmentData>();
        entry.Source = null!; entry.ScopeRoot = null!; entry.Visual = null; entry.Renderer = null; entry.Bridge = null;
    }
    private void Retire(int scopeId)
    {
        if (!scopeEntries.TryGetValue(scopeId, out var ids)) return;
        while (ids.Count != 0) RemoveEntry(ids[ids.Count - 1]);
    }
    private Entry? Register(Component source, bool urgent = false)
    {
        if (disposed || !source || source.gameObject.scene.handle != scene || !(segments?.Admit(source.gameObject, urgent) ?? true)) return null;
        int id = source.GetInstanceID();
        if (entries.TryGetValue(id, out var entry))
        {
            if (entry.Source) return entry;
            RemoveEntry(id); // Unity may reuse a destroyed instance ID.
        }
        string kind = RopeReplayResources.Kind(source);
        bool sceneVine = source is JungleVine && !source.GetComponent<SpawnedVine>() && source.GetComponent<BreakableBridge>();
        if (!sceneVine && !RopeReplayAdmission.Record(kind, source.gameObject.activeInHierarchy, source is JungleVine && source.GetComponent<SpawnedVine>())) return null;
        int familyCount = 0; foreach (var existing in entries.Values) if (existing.SceneVine == sceneVine) familyCount++;
        if (familyCount >= (sceneVine ? RopeReplayRules.MaximumSceneVines : RopeReplayRules.MaximumEntities))
        {
            int? victim = null;
            foreach (var pair in entries)
                if (pair.Value.SceneVine == sceneVine && RopeReplayAdmission.CanReplace(pair.Value.Source, pair.Value.Source && pair.Value.Source.gameObject.activeInHierarchy, pair.Value.Last?.Visible ?? false))
                { victim = pair.Key; break; }
            if (victim.HasValue) RemoveEntry(victim.Value);
            else { Warn("limit:" + sceneVine, "绳索或场景断桥目录达到各自上限，超出部分未记录。"); return null; }
        }
        double now = Time.timeAsDouble;
        entry = new Entry { Source = source, ScopeRoot = source.gameObject, InstanceId = id, ScopeId = source.gameObject.GetInstanceID(), Key = identities.GetValue(source, _ => new Identity()).Key,
            Kind = kind, Resource = RopeReplayResources.Name(source), SourcePath = WorldTrack.Path(source.transform), SceneVine = sceneVine,
            NativeActive = source.gameObject.activeInHierarchy, Lifecycle = new RopeCaptureLifecycle(kind == "anchor" || kind == "vine"),
            InitialUrgent = urgent,
            NextVisibilityProbe = now + .5 + (id & 31) * .017 };
        entry.Lifecycle.Notify(now, RopeCaptureEvent.Baseline);
        if (source is Rope nativeRope)
        {
            entry.LastRequestedSegments = nativeRope.Segments;
            entry.KnownAttachment = (int)nativeRope.attachmenState;
            entry.KnownClimbers = nativeRope.charactersClimbing.Count;
            entry.KnownTopology = simulationSegments != null ? simulationSegments(nativeRope).Count : nativeRope.GetRopeSegments().Count;
            entry.Visual = source.GetComponentInChildren<RopeBoneVisualizer>(true);
            if (entry.Visual && entry.Visual.boneRoot)
                entry.Bones = RopeReplayResources.Bones(entry.Visual.boneRoot.transform);
            entry.Renderer = source.GetComponentInChildren<SkinnedMeshRenderer>(true);
            ropeEntries.Remove(nativeRope); ropeEntries.Add(nativeRope, entry);
            if (entry.Visual) { visualEntries.Remove(entry.Visual); visualEntries.Add(entry.Visual, entry); }
        }
        else if (source is RescueHook hook) entry.Renderer = hook.line;
        else entry.Renderer = source.GetComponentInChildren<Renderer>(true);
        if (source is RopeAnchor anchor) entry.KnownAttachment = anchor.Ghost ? 1 : 2;
        if (source is JungleVine) entry.Bridge = source.GetComponent<BreakableBridge>();
        entries.Add(id, entry); directoryDirty = true;
        if (!scopeEntries.TryGetValue(entry.ScopeId, out var scoped)) scopeEntries.Add(entry.ScopeId, scoped = new List<int>(1));
        scoped.Add(id);
        return entry;
    }
    private void Scan()
    {
        // Once at recorder initialization only. Subsequent births are observed; the
        // sparse fallback walks a bounded number of hierarchy edges on each frame.
        foreach (int key in entries.Where(pair => !pair.Value.Source).Select(pair => pair.Key).ToArray())
            if (entries.TryGetValue(key, out var entry)) { if (!entry.ScopeRoot) segments?.Forget(entry.ScopeId); RemoveEntry(key); }
        foreach (var source in Object.FindObjectsByType<Rope>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Register(source);
        foreach (var source in Object.FindObjectsByType<JungleVine>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Register(source);
        foreach (var source in Object.FindObjectsByType<RescueHook>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Register(source);
        foreach (var source in Object.FindObjectsByType<RopeAnchor>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Register(source);
    }
    public RopeReplayFrame[] Capture(double now, ActorFrame[] actors, float captureDistance = 40)
    {
        if (disposed) throw new ObjectDisposedException(nameof(RopeReplayCapture));
        captureNow = now; SleepingCount = 0;
        if (DiscoveryFallbackEnabled) { DiscoveryFallbackTicks++; discovery.Tick(now); }
        // These are at most 256 managed entries, not a scene-wide native search.
        foreach (var entry in ordered) if (!entry.Source) { directoryDirty = true; break; }
        if (directoryDirty)
            foreach (int id in entries.Where(pair => !pair.Value.Source).Select(pair => pair.Key).ToArray())
                if (entries.TryGetValue(id, out var entry)) { if (!entry.ScopeRoot) segments?.Forget(entry.ScopeId); RemoveEntry(id); }
        if (directoryDirty) { ordered = entries.Values.OrderBy(e => e.Key, StringComparer.Ordinal).ToArray(); directoryDirty = false; }
        sampled.Clear();
        foreach (var entry in ordered)
        {
            if (!entry.Source) continue;
            if (entry.Last == null && !entry.InitialUrgent && !CaptureBootstrapWorkBudget.TryBeginRead()) continue;
            bool action = SemanticAction(entry);
            if (CaptureBootstrapWorkBudget.InitialOnly && entry.Last != null && !entry.InitialUrgent && !entry.HierarchyDirty &&
                !action && now >= entry.Lifecycle.ActionUntil)
            { sampled.Add(entry.Last); if (entry.Lifecycle.Sleeping) SleepingCount++; continue; }
            bool safetyProbe = false;
            // Parent biome activation has no JungleVine.OnEnable callback.
            // A staggered scalar visibility check retains that boundary without
            // traversing the static vine's 50 points or visual hierarchy.
            if (now >= entry.NextVisibilityProbe)
            {
                VisibilityProbeCount++;
                entry.NextVisibilityProbe = now + .5 + (entry.InstanceId & 31) * .017;
                bool active = entry.Source.gameObject.activeInHierarchy;
                if (entry.NativeActive != active)
                { entry.NativeActive = active; Notify(entry, now, RopeCaptureEvent.Visibility); }
                safetyProbe = entry.Lifecycle.Sleeping && (!observer.CaptureHooksHealthy || entry.Source is Rope && !observer.LocalSegmentsHealthy);
            }
            bool wasSleeping = entry.Lifecycle.Sleeping;
            bool scheduled = entry.Lifecycle.ShouldRead(now, action, true, 30, sceneTick: true);
            safetyProbe &= !scheduled;
            if (!scheduled && !safetyProbe)
            {
                if (entry.Last != null) sampled.Add(entry.Last);
                if (entry.Lifecycle.Sleeping) SleepingCount++;
                ColdSkipCount++; continue;
            }
            if (wasSleeping && !entry.Lifecycle.Sleeping) WakeCount++;
            bool inRange = InRange(entry.Last, actors, captureDistance);
            try
            {
                entry.InitialUrgent = false;
                ProbeCount++;
                var frame = Read(entry, now);
                bool changed = !ReferenceEquals(frame, entry.Last);
                if (safetyProbe) DegradedSafetyReads++;
                if (safetyProbe && changed) Notify(entry, now, RopeCaptureEvent.Motion);
                // A degraded-hook safety read must not turn an unchanged
                // sleeping object back into a 30 Hz settling session.
                if (!safetyProbe || changed || !entry.Lifecycle.Sleeping)
                    entry.Lifecycle.Observed(now, changed, action, inRange, 30);
                if (changed) ChangedFrameCount++;
                if (entry.Lifecycle.Sleeping) { if (!safetyProbe) SleepCount++; SleepingCount++; }
                CheckpointNativeSegments(entry);
                entry.Last = frame; sampled.Add(frame);
            }
            catch (Exception e)
            {
                entry.Lifecycle.Failed(now);
                if (entry.Last != null) sampled.Add(entry.Last);
                Warn("capture:" + entry.Resource, "绳索尚未就绪，保留上一有效状态：" + entry.Resource + " · " + e.Message);
            }
        }
        bool same = sampled.Count == previous.Length;
        if (same) for (int i = 0; i < sampled.Count; i++) if (!ReferenceEquals(sampled[i], previous[i])) { same = false; break; }
        if (!same) previous = sampled.ToArray();
        return previous;
    }

    private bool SemanticAction(Entry entry)
    {
        if (!entry.NativeActive) return false;
        if (entry.Source is Rope rope)
        {
            // These are existing managed game fields, not per-point Unity reads.
            bool lengthChanging = rope.Segments != entry.LastRequestedSegments;
            entry.LastRequestedSegments = rope.Segments;
            return lengthChanging || rope.charactersClimbing.Count != 0 || rope.antigrav ||
                rope.attachmenState == Rope.ATTACHMENT.inSpool && rope.IsActive();
        }
        if (entry.Source is RescueHook hook) return hookPulling != null ? hookPulling(hook) : entry.HookActive;
        if (entry.Source is RopeAnchor anchor) return anchor.Ghost;
        return entry.Bridge is { } bridge && bridge.isShaking;
    }
    private static bool InRange(RopeReplayFrame? frame, ActorFrame[] actors, float radius)
    {
        if (frame == null || radius <= 0 || float.IsNaN(radius) || float.IsInfinity(radius)) return true;
        double squared = (double)radius * radius;
        foreach (var actor in actors)
        {
            var actorPosition = actor.Position;
            if (actorPosition.Length != 3) continue;
            // Cached endpoints/positions only; no scene/player Transform reads.
            if (Within(frame.Pose.Position, actorPosition, squared)) return true;
            foreach (var point in frame.Points) if (Within(point.Position, actorPosition, squared)) return true;
        }
        return false;
    }
    private static bool Within(float[] p, float[] other, double squared)
    {
        double x = (double)p[0] - other[0], y = (double)p[1] - other[1], z = (double)p[2] - other[2];
        return x * x + y * y + z * z <= squared;
    }

    private RopeReplayFrame Read(Entry entry, double now)
    {
        var source = entry.Source; var old = entry.Last;
        if (entry.HierarchyDirty) { VisualReplica.InvalidateCapture(source.transform, true); entry.HierarchyDirty = false; }
        var visualRoot = source is RescueHook hookRoot && hookRoot.line ? hookRoot.line.transform : source.transform;
        var pose = entry.Kind == "anchor" || entry.Kind == "vine" ? VisualReplica.Capture(source.transform) : Root(visualRoot, old?.Pose);
        bool visible = source.gameObject.activeInHierarchy;
        string collision = "none"; int attachment = 0;
        float width = 0, cutoff = 1, hang = 0, lengthScale = 1, jitter = 0, breakAmount = 0;
        RopeReplayPoint[] points = Array.Empty<RopeReplayPoint>(), bones = Array.Empty<RopeReplayPoint>();
        if (source is Rope rope)
        {
            attachment = (int)rope.attachmenState; collision = "capsule";
            var transforms = rope.GetRopeSegments(); PointReadCount += transforms.Count;
            points = SampleTransforms(transforms, old?.Points, true);
            if (entry.Bones.Length == 0 && entry.Visual && entry.Visual.boneRoot)
                entry.Bones = RopeReplayResources.Bones(entry.Visual.boneRoot.transform);
            BoneReadCount += entry.Bones.Length; bones = SampleTransforms(entry.Bones, old?.Bones, false);
            visible &= entry.Renderer && entry.Renderer.enabled && points.Length > 0;
            if (entry.Renderer && entry.Renderer.sharedMaterial) cutoff = Float(entry.Renderer.sharedMaterial, "_RopeCutoff", 1);
            width = points.Length > 0 ? points[0].Radius * 2 : 0;
        }
        else if (source is JungleVine vine)
        {
            var transforms = vine.colliderRoot;
            int count = transforms ? transforms.childCount : 0;
            PointReadCount += count;
            if (count > 50) throw new InvalidOperationException("藤索超过原生 50 段。");
            bool same = old != null && old.Points.Length == count;
            RopeReplayPoint[]? changed = null;
            for (int i = 0; i < count; i++)
            {
                var prior = old != null && i < old.Points.Length ? old.Points[i] : null;
                var point = Sample(transforms!.GetChild(i), prior, true);
                if (!ReferenceEquals(point, prior) || !same)
                {
                    if (changed == null) { changed = new RopeReplayPoint[count]; for (int j = 0; j < i; j++) changed[j] = old!.Points[j]; }
                    changed[i] = point; same = false;
                }
                else if (changed != null) changed[i] = point;
            }
            points = same ? old!.Points : changed ?? Array.Empty<RopeReplayPoint>();
            collision = vine.colliderType == JungleVine.ColliderType.Box ? "box" : "capsule"; attachment = 2;
            // Native Fall_Rpc destroys colliderRoot BEFORE its 1.43-second
            // visible shader collapse. Geometry lifetime is not collider lifetime.
            visible &= entry.Renderer && entry.Renderer.enabled && entry.Renderer.gameObject.activeInHierarchy;
            if (entry.Renderer && entry.Renderer.sharedMaterial)
            {
                var material = entry.Renderer.sharedMaterial;
                hang = Float(material, "_Hang", 0); lengthScale = Float(material, "_LengthScale", 1); jitter = Float(material, "_JitterAmount", 0);
                breakAmount = Float(material, "_BreakAmount", 0);
            }
            width = count > 0 ? points[0].Radius * 2 : 0;
        }
        else if (source is RescueHook hook)
        {
            var line = hook.line;
            visible &= line && line.gameObject.activeInHierarchy && line.enabled;
            int count = visible ? line!.positionCount : 0;
            PointReadCount += count;
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
            Same(old.Cutoff, cutoff) && Same(old.Hang, hang) && Same(old.LengthScale, lengthScale) && Same(old.Jitter, jitter) && Same(old.BreakAmount, breakAmount)) return old;
        return new RopeReplayFrame { Key = entry.Key, Kind = entry.Kind, Resource = entry.Resource, SourcePath = entry.SourcePath,
            SampleTimeKnown = true, SampleTime = now,
            Pose = pose, Visible = visible, Attachment = attachment, CollisionKind = collision, Points = points, Bones = bones,
            Width = width, Cutoff = cutoff, Hang = hang, LengthScale = lengthScale, Jitter = jitter, BreakAmount = breakAmount, SceneVine = entry.SceneVine };
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
        var shape = collider ? colliderProbes.GetValue(transform, static source => new ColliderProbe(source)) : null;
        var capsule = shape?.Capsule; var box = shape?.Box;
        if (capsule)
        {
            p = transform.TransformPoint(capsule.center);
            float along = capsule.direction == 0 ? scale.x : capsule.direction == 1 ? scale.y : scale.z;
            float across = capsule.direction == 0 ? Math.Max(Math.Abs(scale.y), Math.Abs(scale.z)) : capsule.direction == 1 ? Math.Max(Math.Abs(scale.x), Math.Abs(scale.z)) : Math.Max(Math.Abs(scale.x), Math.Abs(scale.y));
            radius = capsule.radius * across; height = Math.Max(capsule.height * Math.Abs(along), radius * 2);
        }
        else if (box)
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
    {
        if (disposed) return;
        disposed = true; observer.Dispose(); segments?.Dispose(); discovery.Clear();
        foreach (int id in entries.Keys.ToArray()) RemoveEntry(id);
        scopeEntries.Clear(); sampled.Clear(); previous = Array.Empty<RopeReplayFrame>(); ordered = Array.Empty<Entry>();
    }
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
    private readonly Action<Component, RopeCaptureEvent, double> callback;
    private readonly Action<Rope, RopeSyncData>? synchronized;
    private readonly Action<RopeBoneVisualizer, IList<RopeSyncData.SegmentData>>? localSegments;
    private readonly bool presentation;
    private static readonly Harmony harmony = new("cn.mylus.peakreplaylab.rope-observer");
    private static AccessTools.FieldRef<RopeBoneVisualizer, List<RopeSyncData.SegmentData>>? localSegmentField;
    private static bool localSegmentsHealthy;
    private static bool synchronizedHealthy;
    private static bool birthHooksHealthy, captureHooksHealthy;
    private readonly List<string> failedHooks = new();
    private bool disposed;
    public bool LocalSegmentsHealthy => localSegmentsHealthy && synchronizedHealthy;
    public bool BirthHooksHealthy => birthHooksHealthy;
    public bool CaptureHooksHealthy => captureHooksHealthy;
    public RopeReplayObserver(Action<Component, RopeCaptureEvent, double> callback, Action<Rope, RopeSyncData>? synchronized,
        Action<string>? warning = null, bool presentation = false, Action<RopeBoneVisualizer, IList<RopeSyncData.SegmentData>>? localSegments = null)
    {
        this.callback = callback; this.synchronized = synchronized; this.presentation = presentation; this.localSegments = localSegments;
        observers.Add(this);
        if (observers.Count > 1) return;
        birthHooksHealthy = Patch(typeof(Rope), "Awake", nameof(Baseline));
        Patch(typeof(Rope), "AttachToSpool_Rpc", nameof(Attachment)); Patch(typeof(Rope), "AttachToAnchor_Rpc", nameof(Attachment));
        Patch(typeof(Rope), "OnRejoinSyncRPC", nameof(Attachment));
        birthHooksHealthy &= Patch(typeof(Rope), "OnEnable", nameof(Visibility)); Patch(typeof(Rope), "OnDisable", nameof(Visibility));
        Patch(typeof(Rope), "Detach_Rpc", nameof(Attachment)); Patch(typeof(Rope), "AddSegment", nameof(Topology)); Patch(typeof(Rope), "RemoveSegment", nameof(Topology));
        Patch(typeof(Rope), "AddCharacterClimbing", nameof(Climbing)); Patch(typeof(Rope), "RemoveCharacterClimbing", nameof(Climbing));
        synchronizedHealthy = Patch(typeof(Rope), "SetSyncData", nameof(Synchronized));
        try
        {
            localSegmentField = AccessTools.FieldRefAccess<RopeBoneVisualizer, List<RopeSyncData.SegmentData>>("localSegmentData");
            localSegmentsHealthy = Patch(typeof(RopeBoneVisualizer), "RefreshLocalSegmentData", nameof(LocalSegments));
        }
        catch (Exception e) { localSegmentsHealthy = false; failedHooks.Add("RopeBoneVisualizer.localSegmentData (" + e.GetType().Name + ")"); }
        birthHooksHealthy &= Patch(typeof(JungleVine), "Awake", nameof(Baseline)); Patch(typeof(JungleVine), "ForceBuildVine", nameof(Topology));
        Patch(typeof(BreakableBridge), "ShakeBridge_Rpc", nameof(BridgeShake));
        Patch(typeof(BreakableBridge), "Fall_Rpc", nameof(BridgeFall));
        birthHooksHealthy &= Patch(typeof(RopeAnchor), "Awake", nameof(Baseline)); Patch(typeof(RopeAnchor), "set_Ghost", nameof(Attachment));
        Patch(typeof(RopeAnchorProjectile), "GetShot", nameof(Anchor));
        birthHooksHealthy &= Patch(typeof(RescueHook), "Awake", nameof(Baseline)); Patch(typeof(RescueHook), "RPCA_RescueWall", nameof(HookStart));
        Patch(typeof(RescueHook), "RPCA_RescueCharacter", nameof(HookStart)); Patch(typeof(RescueHook), "RPCA_LetGo", nameof(HookEnd));
        birthHooksHealthy &= Patch(typeof(Item), "OnEnable", nameof(ItemEnabled));
        Patch(typeof(Item), "OnDisable", nameof(ItemEnabled));
        captureHooksHealthy = failedHooks.Count == 0;
        if (failedHooks.Count > 0)
            try { warning?.Invoke("部分绳索事件观察不可用，保留已生效观察器并使用稀疏分帧发现兜底；短时变化可能遗漏：" + string.Join("、", failedHooks)); } catch { }
    }
    private bool Patch(Type type, string name, string observer)
    {
        try
        {
            var method = AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.Name, name);
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(RopeReplayObserver), observer));
            return true;
        }
        catch (Exception e) { failedHooks.Add(type.Name + "." + name + " (" + e.GetType().Name + ")"); return false; }
    }
    private static void Changed(Component source, RopeCaptureEvent kind, double duration = 0)
    {
        if (!source) return;
        foreach (var observer in observers)
            try { if (!observer.disposed && observer.presentation == ReplaySafety.Active) observer.callback(source, kind, duration); } catch { }
    }
    private static void Baseline(Component __instance) => Changed(__instance, RopeCaptureEvent.Baseline);
    private static void Visibility(Component __instance) => Changed(__instance, RopeCaptureEvent.Visibility);
    private static void Attachment(Component __instance) => Changed(__instance, RopeCaptureEvent.Attachment);
    private static void Topology(Component __instance) => Changed(__instance, RopeCaptureEvent.Topology);
    private static void Climbing(Component __instance) => Changed(__instance, RopeCaptureEvent.Climbing);
    private static void HookStart(Component __instance) => Changed(__instance, RopeCaptureEvent.HookStart);
    private static void HookEnd(Component __instance) => Changed(__instance, RopeCaptureEvent.HookEnd);
    private static void ItemEnabled(Item __instance)
    { if (__instance && __instance.TryGetComponent<RescueHook>(out var hook)) Changed(hook, RopeCaptureEvent.Visibility); }
    private static void BridgeShake(BreakableBridge __instance)
    { if (__instance && __instance.TryGetComponent<JungleVine>(out var vine)) Changed(vine, RopeCaptureEvent.BridgeShake, __instance.fallTime); }
    private static void BridgeFall(BreakableBridge __instance)
    { if (__instance && __instance.TryGetComponent<JungleVine>(out var vine)) Changed(vine, RopeCaptureEvent.BridgeFall, 1.0 / .7 + .05); }
    private static void Anchor(RopeAnchorProjectile __instance, float travelTime)
    { if (__instance) Changed(__instance.GetComponent<RopeAnchor>(), RopeCaptureEvent.AnchorFlight, travelTime + .05); }
    private static void LocalSegments(RopeBoneVisualizer __instance)
    {
        if (localSegmentField == null || ReplaySafety.Active) return;
        var segments = localSegmentField(__instance);
        foreach (var observer in observers)
            try { if (!observer.disposed && !observer.presentation) observer.localSegments?.Invoke(__instance, segments); } catch { }
    }
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
        public readonly List<Material> MaterialSlots = new();
        public Transform[] Bones = Array.Empty<Transform>();
        public readonly List<Material> Materials = new();
        public Material? Normal, Ghost;
        public RopeReplayFrame? Left, Right;
        public long LastUsed;
        public float Mix;
        public Vector3[] Positions = Array.Empty<Vector3>();
        public Component Source = null!;
        public RopeBoneVisualizer? BoneSource;
        public Transform[] SourceBones = Array.Empty<Transform>();
        public int BuildStage, BuildIndex;
        public bool Failed, Ready;
        public void Hide() { Replica?.Hide(); if (LineRoot && LineRoot.activeSelf) LineRoot.SetActive(false); Left = Right = null; }
        public void Retire()
        { Replica?.Retire(); Replica = null; Dispose(); }
        public void Dispose()
        { Replica?.Dispose(); if (LineRoot) { LineRoot.SetActive(false); Object.Destroy(LineRoot); } foreach (var m in Materials) if (m) Object.Destroy(m); Materials.Clear(); }
    }
    private readonly Action<string>? warning;
    private readonly PresentationWorkBudget workBudget;
    private readonly Dictionary<string, Component> resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Component Source, string Resource)> sceneSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> retryAfter = new(StringComparer.Ordinal);
    private readonly Dictionary<Renderer, bool> originals = new();
    private readonly Dictionary<string, RopeReplayFrame> next = new(StringComparer.Ordinal);
    private readonly HashSet<string> present = new(StringComparer.Ordinal), wanted = new(StringComparer.Ordinal), reported = new(StringComparer.Ordinal);
    private RopeReplayFrame[]? previousRight;
    private RopeReplayFrame[]? requestedLeft, requestedRight;
    private RopeObservationTimeline? requestedObservations;
    private float requestedMix;
    private double requestedTime;
    private bool pending;
    private int appliedFrame = -1;
    private readonly List<string> removals = new();
    private readonly RopeReplayObserver observer;
    private readonly RopeSceneDiscovery discovery;
    private double nextRetryCleanup;
    private int enforcedFrame = -1;
    private long useSerial;
    private bool disposed, started;
    public int VisualCount => entries.Count;
    public long ApplyCount { get; private set; }
    public long SharedPoseSkipCount { get; private set; }
    public RopeReplayPlayback(Action<string>? warning = null, PresentationWorkBudget? workBudget = null)
    {
        this.warning = warning; this.workBudget = workBudget ?? new();
        discovery = new RopeSceneDiscovery(AddResource);
        observer = new RopeReplayObserver((source, _, _) => AddResource(source), null, message => Warn("observer", message), presentation: true);
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
        string resource = RopeReplayResources.Name(source);
        string key = RopeReplayResources.Kind(source) + ":" + resource;
        bool sceneObject = source.gameObject.scene.IsValid() && source.gameObject.scene.isLoaded;
        if (!resources.TryGetValue(key, out var old) || !old || !sceneObject && old.gameObject.scene.IsValid()) resources[key] = source;
        if (!sceneObject) return;
        // Resource names are stable after registration. Keep the normalized name
        // with the scene source so playback never does Replace/Trim per frame.
        sceneSources[WorldTrack.Path(source.transform)] = (source, resource);
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
    public void Apply(RopeReplayFrame[] left, RopeReplayFrame[] right, float mix, double time, bool discontinuity, RopeObservationTimeline? observations = null)
    {
        if (disposed) throw new ObjectDisposedException(nameof(RopeReplayPlayback));
        workBudget.BeginFrame(Time.frameCount); VisualReplica.DrainRetired(workBudget);
        requestedLeft = left; requestedRight = right; requestedMix = mix; requestedTime = time;
        requestedObservations = observations;
        appliedFrame = Time.frameCount; pending = false;
        started |= left.Length > 0 || right.Length > 0;
        if (!ReferenceEquals(previousRight, right))
        { next.Clear(); foreach (var frame in right) if (!next.ContainsKey(frame.Key)) next.Add(frame.Key, frame); previousRight = right; }
        wanted.Clear(); foreach (var frame in left)
        {
            var state = mix >= 1 && next.TryGetValue(frame.Key, out var later) ? later : frame;
            if (state.Visible && state.Pose.Active) wanted.Add(frame.Key);
        }
        removals.Clear();
        foreach (var pair in entries) if (!wanted.Contains(pair.Key) && !pair.Value.Ready)
        { pair.Value.Retire(); removals.Add(pair.Key); }
        foreach (string key in removals) entries.Remove(key);
        present.Clear();
        double now = Time.unscaledTimeAsDouble;
        if (now >= nextRetryCleanup)
        {
            removals.Clear();
            foreach (var pair in retryAfter) if (pair.Value <= now && !wanted.Contains(pair.Key)) removals.Add(pair.Key);
            foreach (string key in removals) retryAfter.Remove(key);
            nextRetryCleanup = now + 1;
        }
        foreach (var frame in left)
        {
            if (!present.Add(frame.Key)) continue;
            next.TryGetValue(frame.Key, out var later);
            var state = mix >= 1 && later != null ? later : frame;
            if (state.Kind == "vine" && sceneSources.TryGetValue(state.SourcePath, out var native) && native.Source &&
                native.Resource == state.Resource) HideSource(native.Source);
            if (!state.Visible || !state.Pose.Active) { if (entries.TryGetValue(frame.Key, out var hidden)) hidden.Hide(); continue; }
            if (!entries.TryGetValue(frame.Key, out var entry))
            {
                if (retryAfter.TryGetValue(frame.Key, out double retry) && now < retry) continue;
                Component? source = null;
                if (state.SceneVine && sceneSources.TryGetValue(state.SourcePath, out var sceneVine) && sceneVine.Source &&
                    sceneVine.Source is JungleVine && sceneVine.Source.GetComponent<BreakableBridge>() && sceneVine.Resource == state.Resource)
                    source = sceneVine.Source;
                if (!source && !state.SceneVine) resources.TryGetValue(state.Kind + ":" + state.Resource, out source);
                if (!source)
                { Warn("resource:" + state.Kind + ":" + state.Resource, "未找到匹配的绳索原生资源，将重试：" + state.Resource); continue; }
                if (!VisualReplica.CanStartBuild || !workBudget.Create()) { pending = true; continue; }
                if (!MakeRoom()) { Warn("cache-limit", "绳索视觉实体达到 256 个上限，超出部分暂不显示。"); continue; }
                try { entry = Create(source, state); entries.Add(frame.Key, entry); retryAfter.Remove(frame.Key); }
                catch (Exception e)
                {
                    // Resource readiness may improve, but repeated failed cloning must not
                    // allocate a GameObject/material hierarchy on every playback frame.
                    if (retryAfter.Count >= RopeReplayRules.MaximumTotalEntities && !retryAfter.ContainsKey(frame.Key))
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
            try { if (!ContinueBuild(entry, state)) { pending = true; continue; } }
            catch (Exception e)
            { entry.Failed = true; entry.Retire(); Warn("create:" + state.Key, "绳索视觉创建失败，已隔离：" + state.Resource + " · " + e.Message); continue; }
            var following = later != null && RopeReplayRules.CanInterpolate(frame, later) ? later : frame;
            float blend = ReferenceEquals(following, frame) ? 0 : Mathf.Clamp01(mix);
            if (mix >= 1 && later != null) { following = later; blend = 0; }
            var beginning = mix >= 1 && later != null ? later : frame;
            if (frame.SampleTimeKnown && observations != null && observations.TrySample(frame.Key, time, out var observedLeft, out var observedRight, out float observedMix))
            { beginning = observedLeft; following = observedRight; blend = observedMix; }
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
        if (entries.Count < RopeReplayRules.MaximumTotalEntities) return true;
        string? victim = null; long oldest = long.MaxValue;
        foreach (var pair in entries)
            if (!wanted.Contains(pair.Key) && pair.Value.LastUsed < oldest) { victim = pair.Key; oldest = pair.Value.LastUsed; }
        if (victim == null) return false;
        var removed = entries[victim]; entries.Remove(victim);
        try { removed.Retire(); } catch (Exception e) { Warn("evict:" + victim, "旧绳索视觉释放失败：" + e.Message); }
        return true;
    }
    private static Entry Create(Component source, RopeReplayFrame frame)
    {
        var entry = new Entry { Source = source };
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
                entry.Ready = true;
            }
            else
            {
                entry.Replica = VisualReplica.BeginBuild(source.gameObject);
            }
            return entry;
        }
        catch { entry.Retire(); throw; }
    }
    private bool ContinueBuild(Entry entry, RopeReplayFrame frame)
    {
        if (entry.Ready) return true;
        if (entry.Replica == null || !entry.Replica.ContinueBuild(workBudget)) return false;
        while (!entry.Ready && workBudget.Build())
        {
            if (entry.BuildStage == 0)
            {
                entry.Renderers = entry.Replica.Renderers.ToArray();
                if (!(entry.Source is Rope)) { entry.Ready = true; return true; }
                entry.BoneSource = entry.Source.GetComponentInChildren<RopeBoneVisualizer>(true);
                if (!entry.BoneSource || !entry.BoneSource.boneRoot) throw new InvalidOperationException("Native rope bones are missing.");
                entry.SourceBones = RopeReplayResources.Bones(entry.BoneSource.boneRoot.transform);
                if (entry.SourceBones.Length != frame.Bones.Length || entry.SourceBones.Length > RopeReplayRules.MaximumBones)
                    throw new InvalidOperationException("Recorded/native rope bone counts differ.");
                entry.Bones = new Transform[entry.SourceBones.Length]; entry.BuildStage = 1;
            }
            else if (entry.BuildStage == 1)
            {
                if (entry.BuildIndex < entry.SourceBones.Length)
                {
                    entry.Bones[entry.BuildIndex] = RopeReplayResources.Match(entry.SourceBones[entry.BuildIndex], entry.Source.transform, entry.Replica.Root.transform);
                    entry.BuildIndex++;
                }
                else { entry.BuildStage = 2; entry.BuildIndex = 0; }
            }
            else if (entry.BuildStage == 2)
            {
                if (entry.BuildIndex < entry.Renderers.Length)
                { if (entry.Renderers[entry.BuildIndex++] is SkinnedMeshRenderer skin) skin.updateWhenOffscreen = true; }
                else entry.BuildStage = 3;
            }
            else if (entry.BuildStage == 3)
            { entry.Normal = CopyMaterial(entry.BoneSource!.ropeMaterial, entry); entry.BuildStage = 4; }
            else
            {
                entry.Ghost = CopyMaterial(entry.BoneSource!.ghostMaterial, entry);
                entry.SourceBones = Array.Empty<Transform>(); entry.Ready = true;
            }
        }
        return entry.Ready;
    }
    private static Material CopyMaterial(Material source, Entry entry)
    { if (!source) throw new InvalidOperationException("Native rope material is missing."); var copy = new Material(source); NativeVisualAppearance.MarkReplayMaterial(copy); entry.Materials.Add(copy); return copy; }
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
            // Renderer.sharedMaterials creates a managed array on every access.
            // Reuse one scratch list per rope and refresh it after a ghost swap.
            entry.MaterialSlots.Clear();
            renderer.GetSharedMaterials(entry.MaterialSlots);
            foreach (var material in entry.MaterialSlots)
            {
                if (!material) continue;
                Set(material, "_RopeCutoff", state.Cutoff); Set(material, "_Hang", state.Hang);
                Set(material, "_LengthScale", state.LengthScale); Set(material, "_JitterAmount", state.Jitter);
                Set(material, "_BreakAmount", state.BreakAmount);
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
    public void Enforce(bool completePending = false)
    {
        if (disposed) return;
        // Keep the early hiding/discovery pass independent of the rotating
        // presentation budget. Paused sessions still drain in the later pass.
        if (completePending)
        { workBudget.BeginFrame(Time.frameCount); VisualReplica.DrainRetired(workBudget); }
        if (started && enforcedFrame != Time.frameCount)
        {
            enforcedFrame = Time.frameCount;
            discovery.Tick(Time.unscaledTimeAsDouble);
            foreach (var pair in originals) if (pair.Key && !pair.Key.forceRenderingOff) pair.Key.forceRenderingOff = true;
        }
        if (completePending && pending && appliedFrame != Time.frameCount && requestedLeft != null && requestedRight != null)
            Apply(requestedLeft, requestedRight, requestedMix, requestedTime, false, requestedObservations);
    }
    private void Warn(string key, string message) { if (reported.Count < 128 && reported.Add(key)) try { warning?.Invoke(message); } catch { } }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        observer.Dispose(); discovery.Clear();
        foreach (var pair in entries) try { pair.Value.Dispose(); } catch (Exception e) { Warn("dispose:" + pair.Key, e.Message); }
        foreach (var pair in originals) try { if (pair.Key) pair.Key.forceRenderingOff = pair.Value; } catch (Exception e) { Warn("restore", e.Message); }
        originals.Clear(); resources.Clear(); sceneSources.Clear(); entries.Clear(); retryAfter.Clear(); next.Clear(); present.Clear(); wanted.Clear(); previousRight = null;
        requestedLeft = requestedRight = null; removals.Clear(); VisualReplica.FlushRetired();
    }
}
