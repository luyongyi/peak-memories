using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakReplayLab;

internal sealed class LuggagePlayback : IDisposable
{
    private sealed class Entry
    {
        public Luggage Source = null!;
        public string Kind = "";
        public VisualReplica? Replica;
        public CrateVisualAnimation? Animation;
        public NativeVisualAppearance.Playback? Appearance;
        public NativeLightAppearance.Playback? Lights;
        public ObjectPose? LastPose;
        public CrateAnimationFrame? LastAnimation;
        public bool OriginalsHidden, Failed, AnimationFailed, Presented;
        public bool RestartAppearance, RestartLights;
    }
    private readonly Dictionary<string, Entry> allowed = new(StringComparer.Ordinal);
    private readonly Dictionary<Renderer, bool> originals = new();
    private readonly NativeLightAppearance.Originals originalLights = new();
    private readonly Dictionary<string, CrateFrame> next = new(StringComparer.Ordinal);
    private readonly HashSet<string> present = new(StringComparer.Ordinal);
    private readonly HashSet<string> reported = new(StringComparer.Ordinal);
    private readonly Action<string>? warning;
    private readonly PresentationWorkBudget workBudget;
    private CrateFrame[]? previousRight;
    private CrateFrame[]? requestedLeft, requestedRight;
    private float requestedMix;
    private double requestedTime;
    private int appliedFrame = -1;
    private bool pending;
    private readonly HashSet<string> wanted = new(StringComparer.Ordinal);
    private bool disposed;
    private int enforcedFrame = -1;
    public LuggagePlayback(Action<string>? warning = null, PresentationWorkBudget? workBudget = null)
    {
        this.warning = warning; this.workBudget = workBudget ?? new();
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
    public void Apply(CrateFrame[] left, CrateFrame[] right, float mix, double replayTime, bool discontinuity = false)
    {
        if (disposed) throw new ObjectDisposedException(nameof(LuggagePlayback));
        workBudget.BeginFrame(Time.frameCount); VisualReplica.DrainRetired(workBudget);
        requestedLeft = left; requestedRight = right; requestedMix = mix; requestedTime = replayTime;
        appliedFrame = Time.frameCount; pending = false;
        if (!ReferenceEquals(previousRight, right))
        { next.Clear(); foreach (var frame in right) if (!next.ContainsKey(frame.Key)) next.Add(frame.Key, frame); previousRight = right; }
        wanted.Clear();
        foreach (var frame in left)
        {
            var current = mix >= 1 && next.TryGetValue(frame.Key, out var later) && later.Kind == frame.Kind ? later : frame;
            if (current.Pose.Active) wanted.Add(frame.Key);
        }
        // A newer cursor replaces unfinished work. Retaining a half-built old
        // scene would consume the shared budget after the user has left it.
        foreach (var pair in allowed)
            if (!wanted.Contains(pair.Key) && pair.Value.Replica != null &&
                (!pair.Value.Replica.IsReady || pair.Value.Animation != null && !pair.Value.Animation.IsReady ||
                    pair.Value.Appearance != null && !pair.Value.Appearance.IsReady || pair.Value.Lights != null && !pair.Value.Lights.IsReady))
                CancelBuild(pair.Value);
        present.Clear();
        foreach (var frame in left)
        {
            if (!allowed.TryGetValue(frame.Key, out var entry) || !entry.Source || entry.Kind != frame.Kind)
            { Warn("missing:" + frame.Key, "录像箱子与当前关卡不匹配，已跳过：" + frame.Key); continue; }
            if (!present.Add(frame.Key)) continue;
            if (entry.Failed) continue;
            try
            {
                next.TryGetValue(frame.Key, out var following);
                if (following?.Kind != frame.Kind) following = null;
                bool boundary = mix >= 1 && following != null;
                var current = boundary ? following! : frame;
                if (discontinuity) { Hide(entry); entry.RestartAppearance = true; entry.RestartLights = true; }
                if (!current.Pose.Active) { Hide(entry); continue; }
                if (entry.Replica == null)
                {
                    if (!VisualReplica.CanStartBuild || !workBudget.Create()) { pending = true; continue; }
                    entry.Replica = VisualReplica.BeginBuild(entry.Source.gameObject);
                }
                if (!entry.OriginalsHidden)
                {
                    foreach (var renderer in entry.Source.GetComponentsInChildren<Renderer>(true))
                    {
                        if (!renderer || originals.ContainsKey(renderer)) continue;
                        originals.Add(renderer, renderer.forceRenderingOff); renderer.forceRenderingOff = true;
                    }
                    originalLights.Hide(entry.Source.gameObject);
                    entry.OriginalsHidden = true;
                }
                if (!entry.Replica.ContinueBuild(workBudget)) { pending = true; continue; }
                if (entry.Appearance == null)
                {
                    if (!workBudget.Build()) { Defer(entry); pending = true; continue; }
                    entry.Appearance = new NativeVisualAppearance.Playback(entry.Replica, entry.Source.gameObject, true);
                }
                if (!entry.Appearance.ContinueBuild(workBudget)) { Defer(entry); pending = true; continue; }
                if (entry.Lights == null)
                {
                    if (!workBudget.Build()) { Defer(entry); pending = true; continue; }
                    entry.Lights = new NativeLightAppearance.Playback(entry.Replica, entry.Source.gameObject, true);
                }
                if (!entry.Lights.ContinueBuild(workBudget)) { Defer(entry); pending = true; continue; }
                // Native graph creation and first evaluation are also admitted
                // by the shared budget, while the completed mesh stays hidden.
                if (current.Animation != null && !entry.AnimationFailed)
                {
                    try
                    {
                        if (entry.Animation == null)
                        {
                            if (!workBudget.Build()) { Defer(entry); pending = true; continue; }
                            entry.Animation = new CrateVisualAnimation(entry.Source, entry.Replica.Root);
                        }
                        if (!entry.Animation.ContinueBuild(workBudget)) { Defer(entry); pending = true; continue; }
                    }
                    catch (Exception e)
                    {
                        entry.AnimationFailed = true;
                        try { entry.Animation?.Dispose(); } catch { }
                        Warn("animation:" + frame.Key, "箱子动画已隔离，保留记录的视觉状态：" + frame.Key + " · " + e.Message);
                    }
                }
                bool checkpointChanged = !ReferenceEquals(entry.LastPose, current.Pose) || !ReferenceEquals(entry.LastAnimation, current.Animation);
                bool dynamicRoot = following != null && !ReferenceEquals(frame.Pose, following.Pose);
                if (current.Animation != null && !entry.AnimationFailed)
                {
                    try
                    {
                        if (!entry.Animation!.TryPrepareSample(current.Animation, boundary ? null : following?.Animation, boundary ? 0 : mix,
                            replayTime, checkpointChanged || dynamicRoot, workBudget))
                        { Defer(entry); pending = true; continue; }
                    }
                    catch (Exception e)
                    {
                        entry.AnimationFailed = true;
                        try { entry.Animation?.Dispose(); } catch { }
                        Warn("animation:" + frame.Key, "箱子动画已隔离，保留记录的视觉状态：" + frame.Key + " · " + e.Message);
                    }
                }
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
                        entry.Animation!.ApplyPreparedSample();
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
                bool appearanceReady = entry.Appearance.TryApply(current.Visuals, workBudget, restart: entry.RestartAppearance);
                entry.RestartAppearance = false;
                if (!appearanceReady) { Defer(entry); pending = true; continue; }
                bool lightsReady = entry.Lights.TryApply(current.Lights, workBudget, restart: entry.RestartLights);
                entry.RestartLights = false;
                if (!lightsReady) { Defer(entry); pending = true; continue; }
                entry.Presented = true;
            }
            catch (Exception e)
            {
                entry.Failed = true; Hide(entry);
                CancelBuild(entry);
                Warn("apply:" + frame.Key, "箱子视觉回放已隔离：" + frame.Key + " · " + e.Message);
            }
        }
        foreach (var pair in allowed) if (!present.Contains(pair.Key)) Hide(pair.Value);
        // PlaybackSession.Tick alone owns original-renderer enforcement.
    }
    // Sequential playback can carry an already complete presentation across a
    // busy frame. Hiding it would turn every budget miss into a visible flash.
    // New builds and seeks still stay hidden until their full state is restored.
    private static void Defer(Entry entry) { if (!entry.Presented) Hide(entry); }
    private static void Hide(Entry entry)
    {
        if (entry.Replica != null && entry.Replica.Root && entry.Replica.Root.activeSelf) entry.Replica.Hide();
        entry.LastPose = null; entry.LastAnimation = null; entry.Presented = false;
    }
    private static void CancelBuild(Entry entry)
    {
        try { entry.Animation?.Dispose(); } catch { }
        try { entry.Appearance?.Dispose(); } catch { }
        try { entry.Lights?.Dispose(); } catch { }
        entry.Appearance = null; entry.Lights = null;
        entry.Animation = null; entry.Replica?.Retire(); entry.Replica = null;
        entry.LastPose = null; entry.LastAnimation = null; entry.Presented = false;
    }
    public void Enforce(bool completePending = false)
    {
        if (disposed) return;
        // The early maintenance pass runs before the session's rotating work
        // admission. It must not start or consume that frame's build deadline.
        if (completePending)
        { workBudget.BeginFrame(Time.frameCount); VisualReplica.DrainRetired(workBudget); }
        if (enforcedFrame != Time.frameCount)
        {
            enforcedFrame = Time.frameCount;
            foreach (var pair in originals)
                try { if (pair.Key && !pair.Key.forceRenderingOff) pair.Key.forceRenderingOff = true; }
                catch (Exception e) { Warn("renderer-hide", "无法隐藏原生箱子渲染器：" + e.Message); }
            originalLights.Enforce();
        }
        if (completePending && pending && appliedFrame != Time.frameCount && requestedLeft != null && requestedRight != null)
            Apply(requestedLeft, requestedRight, requestedMix, requestedTime);
    }
    private void Warn(string key, string message)
    { if (reported.Count < 128 && reported.Add(key)) try { warning?.Invoke(message); } catch { } }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var pair in allowed)
        {
            try { pair.Value.Animation?.Dispose(); } catch (Exception e) { Warn("animation-dispose:" + pair.Key, e.Message); }
            try { pair.Value.Appearance?.Dispose(); } catch (Exception e) { Warn("appearance-dispose:" + pair.Key, e.Message); }
            try { pair.Value.Lights?.Dispose(); } catch (Exception e) { Warn("lights-dispose:" + pair.Key, e.Message); }
            try { pair.Value.Replica?.Dispose(); } catch (Exception e) { Warn("dispose:" + pair.Key, e.Message); }
        }
        foreach (var pair in originals)
            try { if (pair.Key) pair.Key.forceRenderingOff = pair.Value; } catch (Exception e) { Warn("restore", e.Message); }
        originalLights.Dispose();
        originals.Clear(); allowed.Clear(); next.Clear(); present.Clear(); reported.Clear(); previousRight = null;
        requestedLeft = requestedRight = null; wanted.Clear(); VisualReplica.FlushRetired();
    }
}
