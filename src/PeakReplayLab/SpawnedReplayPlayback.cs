using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

internal sealed class SpawnedReplayPlayback : IDisposable
{
    private sealed class Entry
    {
        public string Resource = "", Kind = "";
        public VisualReplica Visual = null!;
        public CannonReplayVisual? Cannon;
        public NativeVisualAppearance.Playback? Appearance;
        public NativeLightAppearance.Playback? Lights;
        public GameObject Native = null!;
        public string Key = "";
        private bool cannonInitialized;
        public bool Presented, RestartPose = true, RestartAppearance = true, RestartLights = true;
        private bool appearancePending, lightsPending;
        public bool AppearanceNeedsRefresh, LightsNeedRefresh;
        private NativeRendererFrame[]? appearanceTarget;
        private NativeLightFrame[]? lightsTarget;
        public string Template => Kind + "\n" + Native.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture);
        public bool Ready => Visual.IsReady && !Visual.IsResetting && Appearance?.IsReady == true && Lights?.IsReady == true && cannonInitialized;
        public void Dispose() { Cannon?.Dispose(); Lights?.Dispose(); Appearance?.Dispose(); Visual.Dispose(); }
        public void Retire() { Cannon?.Dispose(); Lights?.Dispose(); Appearance?.Dispose(); Visual.Retire(); }
        public bool ContinueBuild(PresentationWorkBudget budget)
        {
            if (Visual.IsResetting && !Visual.ContinueReuse(budget)) return false;
            if (!Visual.ContinueBuild(budget)) return false;
            if (Appearance == null) { if (!budget.Build()) return false; Appearance = new NativeVisualAppearance.Playback(Visual, Native, true); }
            if (!Appearance.ContinueBuild(budget)) return false;
            if (Lights == null) { if (!budget.Build()) return false; Lights = new NativeLightAppearance.Playback(Visual, Native, true); }
            if (!Lights.ContinueBuild(budget)) return false;
            if (!cannonInitialized)
            { if (!budget.Build()) return false; if (Kind == "scout-cannon") Cannon = new CannonReplayVisual(Native, Visual); cannonInitialized = true; }
            return true;
        }
        public void BeginReuse()
        { Hide(); Cannon?.Dispose(); Cannon = null; cannonInitialized = false; Lights?.Dispose(); Lights = null; Appearance?.Dispose(); Appearance = null; Visual.BeginReuse(); }
        public void Hide()
        { Presented = false; RestartPose = RestartAppearance = RestartLights = true; Visual.Hide(); Visual.InvalidatePose(); }
        public void Defer() { if (!Presented) Visual.Hide(); }
        public bool ApplyAppearance(NativeRendererFrame[]? frames, PresentationWorkBudget budget)
        {
            if (!appearancePending || RestartAppearance) appearanceTarget = frames;
            appearancePending = !Appearance!.TryApply(frames, budget, restart: RestartAppearance);
            RestartAppearance = false;
            // Commit the finished pass even if newer samples arrived while it
            // was building, then schedule the latest one without starving it.
            AppearanceNeedsRefresh = !appearancePending && !ReferenceEquals(appearanceTarget, frames);
            return !appearancePending;
        }
        public bool ApplyLights(NativeLightFrame[]? frames, PresentationWorkBudget budget)
        {
            if (!lightsPending || RestartLights) lightsTarget = frames;
            lightsPending = !Lights!.TryApply(frames, budget, restart: RestartLights);
            RestartLights = false;
            LightsNeedRefresh = !lightsPending && !ReferenceEquals(lightsTarget, frames);
            return !lightsPending;
        }
        public int Seen;
    }
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<Renderer, bool> hidden = new();
    private readonly NativeLightAppearance.Originals originalLights = new();
    private readonly Dictionary<string, SpawnedReplayFrame> following = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> retry = new(StringComparer.Ordinal);
    private readonly HashSet<string> current = new(StringComparer.Ordinal), warned = new(StringComparer.Ordinal);
    private readonly List<string> removed = new();
    private readonly List<KeyValuePair<string, Entry>> sleeping = new();
    private readonly Action<string>? warning;
    private readonly SpawnedReplayObserver observer;
    private readonly NativePlacementObserver placementObserver;
    private int serial, enforcedFrame = -1;
    private bool disposed;
    public int MissingCount { get; private set; }
    public int VisibleCount { get; private set; }
    private readonly PresentationWorkBudget workBudget;
    private readonly Func<Vector3, float>? priorityScore;
    private readonly List<SpawnedReplayFrame> ordered = new();
    private readonly PresentationTemplatePool<Entry> pool = new(24, 8192, static entry => entry.Retire());
    private bool pending;
    private int appliedFrame = -1;
    private SpawnedReplayFrame[] lastLeft = Array.Empty<SpawnedReplayFrame>(), lastRight = Array.Empty<SpawnedReplayFrame>();
    private float lastMix;
    private double lastTime;
    public SpawnedReplayPlayback(Action<string>? warning = null, PresentationWorkBudget? workBudget = null, Func<Vector3, float>? priorityScore = null)
    {
        this.warning = warning;
        this.workBudget = workBudget ?? new();
        this.priorityScore = priorityScore;
        observer = new SpawnedReplayObserver((go, _) => Hide(go), _ => { }, _ => { });
        try { placementObserver = new NativePlacementObserver((go, _) => Hide(go), _ => { }, Hide); }
        catch { observer.Dispose(); throw; }
        try
        {
            foreach (var source in Object.FindObjectsByType<Peak.PhotonCleanupHelper>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (source) Hide(source.gameObject);
            foreach (var source in Object.FindObjectsByType<ScoutCannon>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (source) Hide(source.gameObject);
            foreach (var source in NativePlacementResources.InitialSources()) if (source) Hide(source);
        }
        catch { Dispose(); throw; }
    }
    private void Hide(GameObject source)
    {
        if (SpawnedReplayResources.Match(source) == null) return;
        foreach (var renderer in source.GetComponentsInChildren<Renderer>(true))
        {
            // Particles are separately hidden/replayed by the effect track.
            if (!(renderer is MeshRenderer || renderer is SkinnedMeshRenderer) || hidden.ContainsKey(renderer)) continue;
            hidden.Add(renderer, renderer.forceRenderingOff); renderer.forceRenderingOff = true;
        }
        originalLights.Hide(source);
    }
    public void Enforce(bool completePending = true)
    {
        if (disposed || enforcedFrame == Time.frameCount) return; enforcedFrame = Time.frameCount;
        workBudget.BeginFrame(Time.frameCount); VisualReplica.DrainRetired(workBudget);
        foreach (var pair in hidden) if (pair.Key && !pair.Key.forceRenderingOff) pair.Key.forceRenderingOff = true;
        originalLights.Enforce();
        if (completePending && pending && appliedFrame != Time.frameCount) Apply(lastLeft, lastRight, lastMix, lastTime, false);
    }
    public void Apply(SpawnedReplayFrame[] left, SpawnedReplayFrame[] right, float mix, double time, bool discontinuity)
    {
        if (disposed) throw new ObjectDisposedException(nameof(SpawnedReplayPlayback));
        workBudget.BeginFrame(Time.frameCount);
        VisualReplica.DrainRetired(workBudget);
        appliedFrame = Time.frameCount;
        lastLeft = left; lastRight = right; lastMix = mix; lastTime = time; pending = false;
        serial++; MissingCount = 0; VisibleCount = 0; current.Clear(); following.Clear();
        foreach (var frame in right) following[frame.Key] = frame;
        bool prioritize = false;
        foreach (var frame in left) if (frame.Pose.Active)
        { current.Add(frame.Key); if (!entries.TryGetValue(frame.Key, out var existing) || !existing.Ready) prioritize = true; }
        // Reclaim obsolete seek checkpoints before creating this cursor's set.
        // Otherwise 256 old + 24 sleeping entries could block valid new objects.
        removed.Clear();
        foreach (var pair in entries) if (!current.Contains(pair.Key))
        {
            pair.Value.Hide(); pair.Value.Visual.Sleep(); removed.Add(pair.Key);
            if (pair.Value.Ready) pool.Return(pair.Value.Template, pair.Value, pair.Value.Visual.RetainedCost);
            else pair.Value.Retire();
        }
        foreach (var key in removed) entries.Remove(key);
        ordered.Clear(); ordered.AddRange(left);
        if (prioritize && priorityScore != null) ordered.Sort((a, b) => { int score = priorityScore(new Vector3(a.Pose.Position[0], a.Pose.Position[1], a.Pose.Position[2])).CompareTo(priorityScore(new Vector3(b.Pose.Position[0], b.Pose.Position[1], b.Pose.Position[2]))); return score != 0 ? score : string.CompareOrdinal(a.Key, b.Key); });
        double now = Time.realtimeSinceStartupAsDouble;
        foreach (var frame in ordered)
        {
            if (!frame.Pose.Active) continue;
            if (retry.TryGetValue(frame.Key, out double after) && now < after) { MissingCount++; continue; }
            entries.TryGetValue(frame.Key, out var entry);
            try
            {
                if (entry != null && (entry.Resource != frame.Resource || entry.Kind != frame.Kind))
                { entry.Retire(); entries.Remove(frame.Key); entry = null; }
                if (entry == null)
                {
                    var source = SpawnedReplayResources.Find(frame.Resource);
                    if (source == null || source.Kind != frame.Kind || !source.Source) throw new InvalidOperationException("Native spawned terrain resource unavailable: " + frame.Resource);
                    if (entries.Count >= SpawnedReplayRules.MaximumEntities + 24) throw new InvalidOperationException("Spawned terrain visual pool is full.");
                    string template = frame.Kind + "\n" + source.Source!.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    entry = TakePooledEntry(template, frame.Key);
                    if (entry != null) { if (entry.Key != frame.Key) entry.BeginReuse(); }
                    else
                    {
                    if (!VisualReplica.CanStartBuild || !workBudget.Create()) { pending = true; MissingCount++; continue; }
                    entry = new Entry { Resource = frame.Resource, Kind = frame.Kind, Native = source.Source!, Visual = VisualReplica.BeginBuild(source.Source!, true) };
                    try
                    {
                        entry.Visual.Root.name = "PEAK Replay Spawned - " + source.Name;
                    }
                    catch { entry.Retire(); entry = null; throw; }
                    }
                    entry.Resource = frame.Resource; entry.Key = frame.Key; entries.Add(frame.Key, entry);
                }
                if (discontinuity) entry.Hide();
                if (!entry.ContinueBuild(workBudget)) { entry.Defer(); pending = true; MissingCount++; continue; }
                if (!entry.ApplyAppearance(frame.Visuals, workBudget) || !entry.ApplyLights(frame.Lights, workBudget))
                {
                    entry.Defer(); pending = true; MissingCount++;
                    if (entry.Presented) VisibleCount++;
                    continue;
                }
                following.TryGetValue(frame.Key, out var next);
                if (entry.RestartPose) entry.Visual.InvalidatePose();
                if (entry.Cannon != null) entry.Cannon.Apply(frame, next, mix, time, entry.RestartPose);
                else entry.Visual.Apply(frame.Pose, next != null && SpawnedReplayRules.CanInterpolate(frame, next) ? next.Pose : null, mix);
                entry.Appearance!.ReapplyVisibility();
                entry.Presented = true; entry.RestartPose = false;
                pending |= entry.AppearanceNeedsRefresh || entry.LightsNeedRefresh;
                entry.Seen = serial; retry.Remove(frame.Key);
                if (frame.Pose.Active) VisibleCount++;
            }
            catch (Exception e)
            {
                entry?.Retire(); entries.Remove(frame.Key); MissingCount++; retry[frame.Key] = now + 1;
                if (warned.Count < 128 && warned.Add(frame.Resource)) try { warning?.Invoke("生成地形缺失：" + e.Message); } catch { }
            }
        }
        removed.Clear(); foreach (var pair in retry) if (!current.Contains(pair.Key)) removed.Add(pair.Key);
        foreach (string key in removed) retry.Remove(key);
    }
    // Allocate captured predicates only when a visual is missing, never once per
    // existing object in the playback loop.
    private Entry? TakePooledEntry(string template, string key) =>
        pool.Take(template, candidate => candidate.Key == key, candidate => !current.Contains(candidate.Key));

    public void Dispose()
    {
        if (disposed) return; disposed = true; placementObserver.Dispose(); observer.Dispose();
        foreach (var pair in hidden) try { if (pair.Key) pair.Key.forceRenderingOff = pair.Value; } catch { }
        originalLights.Dispose();
        foreach (var entry in entries.Values) try { entry.Dispose(); } catch { }
        hidden.Clear(); entries.Clear(); retry.Clear();
        pool.Clear(); VisualReplica.FlushRetired();
    }
}
