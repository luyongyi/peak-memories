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
        public Animator? Animator;
        public string Key = "", Kind = "";
        public CrateFrame? Previous;
        public LuggageEvent? Pending;
        public bool OpenEffect, Skeleton, Initialized;
        public double NextProbe, WatchFrom, WatchUntil;
        public readonly List<AnimatorClipInfo> Clips = new(4);
    }
    private readonly int sceneHandle = SceneManager.GetActiveScene().handle;
    private readonly Action<string>? warning;
    private readonly HashSet<string> reported = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Entry> entries = new();
    private readonly Queue<LuggageEvent> signals = new();
    private readonly List<CrateFrame> frames = new();
    private readonly LuggageEventObserver observer;
    private Entry[] ordered = Array.Empty<Entry>();
    private CrateFrame[] previous = Array.Empty<CrateFrame>();
    private bool directoryDirty, disposed;

    public LuggageCapture(Action<string>? warning = null)
    {
        this.warning = warning;
        observer = new LuggageEventObserver(signal => { if (signals.Count < 4096) signals.Enqueue(signal); });
        try
        {
            // One catalogue pass. Awake/OnDestroy maintain it thereafter: no periodic
            // whole-scene search and no per-tick child-transform traversal.
            foreach (var source in UnityEngine.Object.FindObjectsByType<Luggage>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Register(source);
        }
        catch { observer.Dispose(); throw; }
    }
    public CrateFrame[] Capture() => Capture(Time.timeAsDouble);
    public CrateFrame[] Capture(double captureTime)
    {
        if (disposed) throw new ObjectDisposedException(nameof(LuggageCapture));
        while (signals.Count > 0)
        {
            var signal = signals.Dequeue();
            if (signal.Kind == LuggageEventKind.Destroyed) { if (entries.Remove(signal.Id)) directoryDirty = true; continue; }
            if (!signal.Source || signal.Source.gameObject.scene.handle != sceneHandle) continue;
            Register(signal.Source);
            entries[signal.Id].NextProbe = 0; // Includes an inactive biome's first Awake.
            if (signal.Kind != LuggageEventKind.Discovered)
            {
                var entry = entries[signal.Id]; entry.Pending = signal; entry.NextProbe = 0;
                entry.WatchFrom = signal.Time; entry.WatchUntil = signal.Time + .5;
            }
        }
        if (directoryDirty)
        {
            var duplicates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in entries.Values.GroupBy(e => e.Key))
                if (group.Count() > 1) { duplicates.Add(group.Key); Warn("duplicate:" + group.Key, "箱子层级不唯一，已跳过：" + group.Key); }
            ordered = entries.Values.Where(e => !duplicates.Contains(e.Key)).OrderBy(e => e.Key, StringComparer.Ordinal).ToArray();
            directoryDirty = false;
        }
        frames.Clear();
        foreach (var entry in ordered)
        {
            if (!entry.Source) continue;
            try { frames.Add(Sample(entry, captureTime)); }
            catch (Exception e) { Warn(entry.Key, "箱子状态采集失败：" + entry.Key + " · " + e.Message); }
        }
        bool same = frames.Count == previous.Length;
        if (same) for (int i = 0; i < frames.Count; i++) if (!ReferenceEquals(frames[i], previous[i])) { same = false; break; }
        if (!same) previous = frames.ToArray();
        return previous;
    }
    private void Register(Luggage source)
    {
        if (!source || source.gameObject.scene.handle != sceneHandle || entries.ContainsKey(source.GetInstanceID())) return;
        entries.Add(source.GetInstanceID(), new Entry { Source = source, Animator = source.GetComponent<Animator>(),
            Key = WorldTrack.Path(source.transform), Kind = source.GetType().FullName ?? source.GetType().Name });
        directoryDirty = true;
    }
    private CrateFrame Sample(Entry entry, double time)
    {
        var source = entry.Source; var old = entry.Previous;
        bool pendingDue = entry.Pending.HasValue && time >= entry.Pending.Value.Time;
        bool watching = time >= entry.WatchFrom && time <= entry.WatchUntil;
        if (old != null && !pendingDue && !watching && time < entry.NextProbe) return old;
        // Static roots/visibility are an inexpensive 2 Hz fallback, staggered per chest.
        // Only a notified interaction watches briefly at capture rate for its delayed effect.
        entry.NextProbe = time + .5 + (source.GetInstanceID() & 3) * .03;
        bool open = source.IsOpen;
        bool active = source.gameObject.activeInHierarchy;
        bool newlyVisible = old != null && !old.Pose.Active && active;
        bool effect = source.activateOnOpen && source.activateOnOpen.activeSelf;
        bool skeleton = source is RespawnChest respawn && respawn.skeleton && respawn.skeleton.activeSelf;
        bool appearanceChanged = !entry.Initialized || newlyVisible || effect != entry.OpenEffect || skeleton != entry.Skeleton;
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
        entry.Initialized = true; entry.OpenEffect = effect; entry.Skeleton = skeleton;
        if (old != null && old.Open == open && ReferenceEquals(old.Pose, pose) && ReferenceEquals(old.Animation, animation)) return old;
        return entry.Previous = new CrateFrame { Key = entry.Key, Kind = entry.Kind, Open = open, Pose = pose, Animation = animation };
    }
    private static ObjectPose RootPose(Transform root, ObjectPose old)
    {
        var p = root.position; var q = root.rotation; var s = root.lossyScale; bool active = root.gameObject.activeInHierarchy;
        if (old.Active == active && old.Position[0] == p.x && old.Position[1] == p.y && old.Position[2] == p.z &&
            old.Rotation[0] == q.x && old.Rotation[1] == q.y && old.Rotation[2] == q.z && old.Rotation[3] == q.w &&
            old.Scale[0] == s.x && old.Scale[1] == s.y && old.Scale[2] == s.z) return old;
        return new ObjectPose { Position = new[] { p.x, p.y, p.z }, Rotation = new[] { q.x, q.y, q.z, q.w },
            Scale = new[] { s.x, s.y, s.z }, Active = active, Nodes = old.Nodes };
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
        observer.Dispose(); entries.Clear(); signals.Clear(); frames.Clear(); ordered = Array.Empty<Entry>(); previous = Array.Empty<CrateFrame>(); reported.Clear();
    }
}

internal sealed class LuggagePlayback : IDisposable
{
    private sealed class Entry
    {
        public Luggage Source = null!;
        public string Kind = "";
        public VisualReplica? Replica;
        public CrateVisualAnimation? Animation;
        public ObjectPose? LastPose;
        public CrateAnimationFrame? LastAnimation;
        public bool OriginalsHidden, Failed, AnimationFailed;
    }
    private readonly Dictionary<string, Entry> allowed = new(StringComparer.Ordinal);
    private readonly Dictionary<Renderer, bool> originals = new();
    private readonly Dictionary<string, CrateFrame> next = new(StringComparer.Ordinal);
    private readonly HashSet<string> present = new(StringComparer.Ordinal);
    private readonly HashSet<string> reported = new(StringComparer.Ordinal);
    private readonly Action<string>? warning;
    private CrateFrame[]? previousRight;
    private bool disposed;
    private int enforcedFrame = -1;
    public LuggagePlayback(Action<string>? warning = null)
    {
        this.warning = warning;
        int sceneHandle = SceneManager.GetActiveScene().handle;
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in UnityEngine.Object.FindObjectsByType<Luggage>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!source || source.gameObject.scene.handle != sceneHandle) continue;
            string key = WorldTrack.Path(source.transform);
            if (allowed.ContainsKey(key)) { ambiguous.Add(key); continue; }
            allowed.Add(key, new Entry { Source = source, Kind = source.GetType().FullName ?? source.GetType().Name });
        }
        foreach (string key in ambiguous) { allowed.Remove(key); Warn("duplicate:" + key, "箱子层级不唯一，无法安全回放：" + key); }
    }
    public void Apply(CrateFrame[] left, CrateFrame[] right, float mix) => Apply(left, right, mix, 0);
    public void Apply(CrateFrame[] left, CrateFrame[] right, float mix, double replayTime)
    {
        if (disposed) throw new ObjectDisposedException(nameof(LuggagePlayback));
        if (!ReferenceEquals(previousRight, right))
        { next.Clear(); foreach (var frame in right) if (!next.ContainsKey(frame.Key)) next.Add(frame.Key, frame); previousRight = right; }
        present.Clear();
        foreach (var frame in left)
        {
            if (!allowed.TryGetValue(frame.Key, out var entry) || !entry.Source || entry.Kind != frame.Kind)
            { Warn("missing:" + frame.Key, "录像箱子与当前关卡不匹配，已跳过：" + frame.Key); continue; }
            if (!present.Add(frame.Key)) continue;
            if (entry.Failed) continue;
            try
            {
                if (!entry.OriginalsHidden)
                {
                    foreach (var renderer in entry.Source.GetComponentsInChildren<Renderer>(true))
                    {
                        if (!renderer || originals.ContainsKey(renderer)) continue;
                        originals.Add(renderer, renderer.forceRenderingOff); renderer.forceRenderingOff = true;
                    }
                    entry.OriginalsHidden = true;
                }
                next.TryGetValue(frame.Key, out var following);
                if (following?.Kind != frame.Kind) following = null;
                bool boundary = mix >= 1 && following != null;
                var current = boundary ? following! : frame;
                if (!current.Pose.Active) { Hide(entry); continue; }
                entry.Replica ??= new VisualReplica(entry.Source.gameObject);
                bool checkpointChanged = !ReferenceEquals(entry.LastPose, current.Pose) || !ReferenceEquals(entry.LastAnimation, current.Animation);
                bool dynamicRoot = following != null && !ReferenceEquals(frame.Pose, following.Pose);
                if (checkpointChanged || dynamicRoot)
                {
                    entry.Replica.InvalidatePose();
                    entry.Replica.Apply(frame.Pose, following?.Pose, mix);
                    entry.LastPose = current.Pose; entry.LastAnimation = current.Animation;
                }
                if (current.Animation != null && !entry.AnimationFailed)
                {
                    try
                    {
                        entry.Animation ??= new CrateVisualAnimation(entry.Source, entry.Replica.Root);
                        entry.Animation.Sample(current.Animation, boundary ? null : following?.Animation, boundary ? 0 : mix, replayTime, checkpointChanged || dynamicRoot);
                    }
                    catch (Exception e)
                    {
                        // One bad asset must neither spam native errors every tick nor
                        // erase this chest. Retain recorded event-checkpoint geometry.
                        entry.AnimationFailed = true;
                        try { entry.Animation?.Dispose(); } catch { }
                        entry.Replica.InvalidatePose(); entry.Replica.Apply(frame.Pose, following?.Pose, mix);
                        Warn("animation:" + frame.Key, "箱子动画已隔离，保留记录的视觉状态：" + frame.Key + " · " + e.Message);
                    }
                }
            }
            catch (Exception e)
            {
                entry.Failed = true; Hide(entry);
                try { entry.Animation?.Dispose(); } catch { }
                Warn("apply:" + frame.Key, "箱子视觉回放已隔离：" + frame.Key + " · " + e.Message);
            }
        }
        foreach (var pair in allowed) if (!present.Contains(pair.Key)) Hide(pair.Value);
        // PlaybackSession.Tick alone owns original-renderer enforcement.
    }
    private static void Hide(Entry entry)
    {
        if (entry.Replica?.Root && entry.Replica.Root.activeSelf) entry.Replica.Hide();
        entry.LastPose = null; entry.LastAnimation = null;
    }
    public void Enforce()
    {
        if (disposed || enforcedFrame == Time.frameCount) return;
        enforcedFrame = Time.frameCount;
        foreach (var pair in originals)
            try { if (pair.Key && !pair.Key.forceRenderingOff) pair.Key.forceRenderingOff = true; }
            catch (Exception e) { Warn("renderer-hide", "无法隐藏原生箱子渲染器：" + e.Message); }
    }
    private void Warn(string key, string message)
    { if (reported.Count < 128 && reported.Add(key)) try { warning?.Invoke(message); } catch { } }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var pair in allowed)
        {
            try { pair.Value.Animation?.Dispose(); } catch (Exception e) { Warn("animation-dispose:" + pair.Key, e.Message); }
            try { pair.Value.Replica?.Dispose(); } catch (Exception e) { Warn("dispose:" + pair.Key, e.Message); }
        }
        foreach (var pair in originals)
            try { if (pair.Key) pair.Key.forceRenderingOff = pair.Value; } catch (Exception e) { Warn("restore", e.Message); }
        originals.Clear(); allowed.Clear(); next.Clear(); present.Clear(); reported.Clear(); previousRight = null;
    }
}
