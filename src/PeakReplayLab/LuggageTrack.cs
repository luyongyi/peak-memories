using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakReplayLab;

internal sealed class LuggageCapture : IDisposable
{
    private sealed class Entry
    {
        public Luggage Source = null!;
        public int ObjectId;
        public Animator? Animator;
        public string Key = "", Kind = "";
        public CrateFrame? Previous;
        public LuggageEvent? Pending;
        public bool OpenEffect, Skeleton, Initialized;
        public bool AppearanceDirty = true;
        public bool InitialUrgent, Ambiguous;
        public bool EventOnly;
        public IDisposable? RootLifecycle;
        public IDisposable? OpenEffectLifecycle;
        public CrateAppearanceCapturePolicy AppearanceSchedule = null!;
        public double NextProbe, WatchFrom = double.NegativeInfinity, WatchUntil = double.NegativeInfinity;
        public readonly List<AnimatorClipInfo> Clips = new(4);
    }
    private readonly int sceneHandle = SceneManager.GetActiveScene().handle;
    private readonly Action<string>? warning;
    private readonly HashSet<string> reported = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Entry> entries = new();
    private readonly Dictionary<int, int> segmentEntries = new();
    private readonly SegmentCaptureClient? segments;
    private readonly Queue<LuggageEvent> signals = new();
    private readonly List<CrateFrame> frames = new();
    private readonly LuggageEventObserver observer;
    private Entry[] ordered = Array.Empty<Entry>();
    private CrateFrame[] previous = Array.Empty<CrateFrame>();
    private bool directoryDirty, disposed;
    internal bool InitialBaselineReady => !directoryDirty && entries.Values.All(entry =>
        !entry.Source || entry.Ambiguous || entry.Previous != null);

    public LuggageCapture(Action<string>? warning = null)
    {
        this.warning = warning;
        segments = CaptureSegmentScope.Active?.CreateClient(AdmitSegmentCrate, RetireSegmentCrate);
        observer = new LuggageEventObserver(signal => { if (signals.Count < 4096) signals.Enqueue(signal); });
        try
        {
            // One catalogue pass. Awake/OnDestroy maintain it thereafter: no periodic
            // whole-scene search and no per-tick child-transform traversal.
            foreach (var source in UnityEngine.Object.FindObjectsByType<Luggage>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Register(source);
        }
        catch { Dispose(); throw; }
    }
    private void AdmitSegmentCrate(GameObject source)
    { var crate = source.GetComponent<Luggage>(); if (crate) Register(crate); }
    private void RetireSegmentCrate(int objectId)
    {
        if (!segmentEntries.TryGetValue(objectId, out int id)) return;
        segmentEntries.Remove(objectId);
        if (!entries.TryGetValue(id, out var entry)) return;
        entries.Remove(id); directoryDirty = true;
        entry.RootLifecycle?.Dispose();
        entry.OpenEffectLifecycle?.Dispose();
        if (entry.Source) VisualReplica.ReleaseCapture(entry.Source.transform);
        entry.Previous = null; entry.Pending = null; entry.Clips.Clear();
    }
    public CrateFrame[] Capture() => Capture(Time.timeAsDouble);
    public CrateFrame[] Capture(double captureTime)
    {
        if (disposed) throw new ObjectDisposedException(nameof(LuggageCapture));
        while (signals.Count > 0)
        {
            var signal = signals.Dequeue();
            if (signal.Kind == LuggageEventKind.Destroyed)
            {
                if (entries.TryGetValue(signal.Id, out var destroyed))
                { segments?.Forget(destroyed.ObjectId); RetireSegmentCrate(destroyed.ObjectId); }
                continue;
            }
            if (!signal.Source || signal.Source.gameObject.scene.handle != sceneHandle) continue;
            bool action = signal.Kind != LuggageEventKind.Discovered && signal.Kind != LuggageEventKind.Appearance;
            Register(signal.Source, urgent: action);
            if (!entries.TryGetValue(signal.Id, out var target)) continue;
            target.NextProbe = 0; target.AppearanceDirty = true; // Includes an inactive biome's first Awake.
            if (signal.Kind != LuggageEventKind.Discovered && signal.Kind != LuggageEventKind.Appearance)
            {
                var entry = entries[signal.Id]; entry.Pending = signal; entry.NextProbe = 0;
                entry.InitialUrgent = true;
                entry.WatchFrom = signal.Time; entry.WatchUntil = signal.Time + .5;
            }
        }
        if (directoryDirty)
        {
            var duplicates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in entries.Values.GroupBy(e => e.Key))
                if (group.Count() > 1) { duplicates.Add(group.Key); Warn("duplicate:" + group.Key, "箱子层级不唯一，已跳过：" + group.Key); }
            foreach (var entry in entries.Values) entry.Ambiguous = duplicates.Contains(entry.Key);
            ordered = entries.Values.Where(e => !duplicates.Contains(e.Key)).OrderBy(e => e.Key, StringComparer.Ordinal).ToArray();
            directoryDirty = false;
        }
        frames.Clear();
        foreach (var entry in ordered)
        {
            if (!entry.Source) continue;
            if (CaptureBootstrapWorkBudget.InitialOnly && entry.Previous != null && !entry.InitialUrgent &&
                !entry.AppearanceDirty && !entry.Pending.HasValue && captureTime > entry.WatchUntil)
            { frames.Add(entry.Previous); continue; }
            if (entry.Previous == null && !entry.InitialUrgent && !CaptureBootstrapWorkBudget.TryBeginRead()) continue;
            entry.InitialUrgent = false;
            try { frames.Add(Sample(entry, captureTime)); }
            catch (Exception e)
            {
                if (entry.Previous != null) frames.Add(entry.Previous);
                Warn(entry.Key, "箱子状态采集失败：" + entry.Key + " · " + e.Message);
            }
        }
        bool same = frames.Count == previous.Length;
        if (same) for (int i = 0; i < frames.Count; i++) if (!ReferenceEquals(frames[i], previous[i])) { same = false; break; }
        if (!same) previous = frames.ToArray();
        return previous;
    }
    private void Register(Luggage source, bool urgent = false)
    {
        if (!source || source.gameObject.scene.handle != sceneHandle) return;
        if (segments != null && !segments.Admit(source.gameObject, urgent)) return;
        if (entries.ContainsKey(source.GetInstanceID())) return;
        var entry = new Entry { Source = source, Animator = source.GetComponent<Animator>(),
            ObjectId = source.gameObject.GetInstanceID(),
            InitialUrgent = urgent,
            AppearanceSchedule = new CrateAppearanceCapturePolicy(source.GetInstanceID()),
            EventOnly = NativeStaticVisualCapability.CrateEventOnly(source),
            Key = WorldTrack.Path(source.transform), Kind = source.GetType().FullName ?? source.GetType().Name };
        entries.Add(source.GetInstanceID(), entry);
        entry.RootLifecycle = CaptureRootLifecycleObserver.Bind(source.gameObject, () =>
        {
            if (!disposed && source && signals.Count < 4096)
                signals.Enqueue(new LuggageEvent(source, LuggageEventKind.Appearance, Time.timeAsDouble));
        });
        // The host's SpawnItemRoutine can wait for registration longer than an
        // animation window. Observe its exact delayed activation independently.
        if (source.activateOnOpen && source.activateOnOpen != source.gameObject)
            entry.OpenEffectLifecycle = CaptureRootLifecycleObserver.Bind(source.activateOnOpen!, () =>
            {
                if (!disposed && source && signals.Count < 4096)
                    signals.Enqueue(new LuggageEvent(source, LuggageEventKind.Appearance, Time.timeAsDouble));
            });
        segmentEntries[source.gameObject.GetInstanceID()] = source.GetInstanceID();
        directoryDirty = true;
    }
    private CrateFrame Sample(Entry entry, double time)
    {
        var source = entry.Source; var old = entry.Previous;
        bool pendingDue = entry.Pending.HasValue && time >= entry.Pending.Value.Time;
        bool watching = time >= entry.WatchFrom && time <= entry.WatchUntil;
        bool appearanceDue = entry.AppearanceSchedule.Due(time, old != null, entry.AppearanceDirty,
            old?.Pose.Active ?? false, watching, old?.Animation, followSceneClock: true, eventOnly: entry.EventOnly);
        if (old != null && !pendingDue && !watching && !appearanceDue && (entry.EventOnly || time < entry.NextProbe)) return old;
        // Unproven scripts retain the staggered fallback; audited roots wake
        // only through exact-root lifecycle/native events and finite clips.
        // Only a notified interaction watches briefly at capture rate for its delayed effect.
        entry.NextProbe = time + entry.AppearanceSchedule.ProbeInterval;
        bool open = source.IsOpen;
        bool active = source.gameObject.activeInHierarchy;
        bool newlyVisible = old != null && !old.Pose.Active && active;
        bool effect = source.activateOnOpen && source.activateOnOpen.activeSelf;
        bool skeleton = source is RespawnChest respawn && respawn.skeleton && respawn.skeleton.activeSelf;
        bool appearanceChanged = !entry.Initialized || newlyVisible || entry.AppearanceDirty || effect != entry.OpenEffect || skeleton != entry.Skeleton;
        bool animationChanged = false;
        CrateAnimationFrame? animation = old?.Animation;
        if (old == null || newlyVisible && animation == null) animation = ReadAnimation(entry, time, null);
        else if (old.Open != open && !entry.Pending.HasValue)
            entry.Pending = new LuggageEvent(source, LuggageEventKind.Open, time);
        if (entry.Pending is LuggageEvent signal && Time.frameCount > signal.Frame && time >= signal.Time)
        {
            var observed = ReadAnimation(entry, time, signal);
            if (observed != null) { animation = observed; entry.Pending = null; animationChanged = true; }
            else if (time - signal.Time > .75)
            {
                entry.Pending = null; animation = null; animationChanged = true;
                Warn("animation:" + entry.Key, "箱子原生动画未就绪，仅保留该阶段的视觉状态：" + entry.Key);
            }
        }
        // Initial checkpoint and discrete events only; never sample every lid/bone at 60 Hz.
        ObjectPose pose = old == null || appearanceChanged || animationChanged
            ? VisualReplica.Capture(source.transform) : RootPose(source.transform, old.Pose);
        var visuals = old?.Visuals; var lights = old?.Lights;
        if (appearanceChanged || entry.AppearanceSchedule.Due(time, old != null, false, active, watching, animation, followSceneClock: true, eventOnly: entry.EventOnly))
        {
            // The cached component/property directories never traverse the scene
            // here. Full values are read at 30 Hz only for an interacting chest.
            visuals = NativeVisualAppearance.Capture(source.transform, visuals);
            lights = NativeLightAppearance.Capture(source.transform, lights);
            entry.AppearanceSchedule.Observed(time, watching || CrateAppearanceCapturePolicy.Advancing(animation, time)); entry.AppearanceDirty = false;
        }
        entry.Initialized = true; entry.OpenEffect = effect; entry.Skeleton = skeleton;
        if (old != null && old.Open == open && ReferenceEquals(old.Pose, pose) && ReferenceEquals(old.Animation, animation) &&
            ReferenceEquals(old.Visuals, visuals) && ReferenceEquals(old.Lights, lights)) return old;
        return entry.Previous = new CrateFrame { Key = entry.Key, Kind = entry.Kind, Open = open, Pose = pose, Animation = animation,
            Visuals = visuals, Lights = lights };
    }
    private static ObjectPose RootPose(Transform root, ObjectPose old)
    {
        var p = root.position; var q = root.rotation; var s = root.lossyScale; bool active = root.gameObject.activeInHierarchy;
        if (old.Active == active && old.Position[0] == p.x && old.Position[1] == p.y && old.Position[2] == p.z &&
            old.Rotation[0] == q.x && old.Rotation[1] == q.y && old.Rotation[2] == q.z && old.Rotation[3] == q.w &&
            old.Scale[0] == s.x && old.Scale[1] == s.y && old.Scale[2] == s.z) return old;
        return new ObjectPose(new[] { p.x, p.y, p.z }, new[] { q.x, q.y, q.z, q.w }, new[] { s.x, s.y, s.z }, active, old.Nodes);
    }
    private static CrateAnimationFrame? ReadAnimation(Entry entry, double time, LuggageEvent? signal)
    {
        var animator = entry.Animator;
        if (!animator || !animator.runtimeAnimatorController || animator.layerCount < 1 || !animator.gameObject.activeInHierarchy) return null;
        var state = animator.GetCurrentAnimatorStateInfo(0);
        entry.Clips.Clear(); animator.GetCurrentAnimatorClipInfo(0, entry.Clips);
        bool transitioning = animator.IsInTransition(0);
        if (transitioning)
        {
            state = animator.GetNextAnimatorStateInfo(0);
            entry.Clips.Clear(); animator.GetNextAnimatorClipInfo(0, entry.Clips);
        }
        if (signal?.Kind == LuggageEventKind.Open && !state.IsName("Luggage_Open")) return null;
        if (signal?.Kind == LuggageEventKind.Unclasp && !state.IsName("Luggage_Unclasp")) return null;
        AnimationClip? clip = null; float weight = -1;
        foreach (var info in entry.Clips) if (info.clip && info.weight > weight) { clip = info.clip; weight = info.weight; }
        if (!clip || clip.humanMotion || clip.length <= 0) return null;
        float cursor = state.normalizedTime * clip.length;
        float rate = animator.speed * state.speed * state.speedMultiplier;
        if (float.IsNaN(cursor) || float.IsInfinity(cursor) || float.IsNaN(rate) || float.IsInfinity(rate)) return null;
        // SetTrigger is evaluated later by Unity. Do not mistake the old, still-forward
        // unclasp state for the new reclasp phase before that transition has happened.
        if (signal?.Kind == LuggageEventKind.Reclasp && !transitioning && state.IsName("Luggage_Unclasp") && rate >= 0) return null;
        cursor = clip.isLooping ? Mathf.Repeat(cursor, clip.length) : Mathf.Clamp(cursor, 0, clip.length);
        double anchor = signal?.Time ?? time;
        cursor = Mathf.Clamp(cursor - rate * (float)Math.Max(0, time - anchor), 0, clip.length);
        if (!signal.HasValue && clip.isLooping) rate = 0;
        return new CrateAnimationFrame { Clip = PeakReplayLab.Capture.ClipKey(clip, animator.runtimeAnimatorController),
            Time = cursor, Anchored = true, AnchorTime = anchor, Rate = Mathf.Clamp(rate, -32, 32), Duration = clip.length, Loop = clip.isLooping };
    }
    private void Warn(string key, string message)
    { if (reported.Count < 128 && reported.Add(key)) try { warning?.Invoke(message); } catch { } }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var entry in entries.Values)
        { entry.RootLifecycle?.Dispose(); entry.OpenEffectLifecycle?.Dispose(); if (entry.Source) VisualReplica.ReleaseCapture(entry.Source.transform); }
        observer.Dispose(); segments?.Dispose(); entries.Clear(); segmentEntries.Clear(); signals.Clear(); frames.Clear(); ordered = Array.Empty<Entry>(); previous = Array.Empty<CrateFrame>(); reported.Clear();
    }
}
