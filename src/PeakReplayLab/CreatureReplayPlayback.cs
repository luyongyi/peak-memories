using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

internal sealed class CreatureReplayPlayback : IDisposable
{
    private sealed class Entry : IDisposable
    {
        public string Kind = "", Resource = "", Path = "";
        public VisualReplica? Visual;
        public GameObject? LineRoot;
        public LineRenderer? Line;
        public NativeVisualAppearance.Playback? Appearance;
        public CreatureIdleAnimationRuntime? IdleRuntime;
        public RavenAnimationRuntime? RavenRuntime;
        public SpiderIdleLinePlayback? IdleLine;
        public bool IdlePresented;
        public ObjectPose? IdlePose;
        public CreatureIdleAnimation? IdleFrame;
        public GameObject Native = null!;
        public string Key = "";
        private bool lineInitialized;
        public bool Presented, RestartAppearance = true, NeedsRefresh;
        private bool appearancePending;
        private NativeRendererFrame[]? appearanceTarget;
        public string Template => Kind + "\n" + Native.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture);
        public bool Ready => LineRoot || Visual != null && Visual.IsReady && !Visual.IsResetting && Appearance?.IsReady == true && lineInitialized;
        public int Seen;
        public void Hide()
        {
            Visual?.Hide(); Visual?.InvalidatePose(); if (LineRoot && LineRoot!.activeSelf) LineRoot.SetActive(false);
            IdleRuntime?.Invalidate(); IdlePresented = false; IdlePose = null; IdleFrame = null;
            RavenRuntime?.Invalidate();
            Presented = false; RestartAppearance = true; NeedsRefresh = false; appearancePending = false; appearanceTarget = null;
        }
        public void Defer()
        {
            if (Presented) return;
            Visual?.Hide(); if (LineRoot && LineRoot!.activeSelf) LineRoot.SetActive(false);
        }
        public bool ApplyAppearance(NativeRendererFrame[]? frames, PresentationWorkBudget budget)
        {
            if (!appearancePending || RestartAppearance) appearanceTarget = frames;
            bool ready = Appearance!.TryApply(frames, budget, restart: RestartAppearance);
            appearancePending = !ready; RestartAppearance = false;
            // A completed pass can advance the pose even while a newer animated
            // appearance is waiting. Retain pending work without starving motion.
            NeedsRefresh = ready && !ReferenceEquals(appearanceTarget, frames);
            return ready;
        }
        public void Dispose() { IdleRuntime?.Dispose(); RavenRuntime?.Dispose(); Appearance?.Dispose(); Visual?.Dispose(); if (LineRoot) { LineRoot!.SetActive(false); Object.Destroy(LineRoot); } }
        public void Retire() { IdleRuntime?.Dispose(); RavenRuntime?.Dispose(); Appearance?.Dispose(); Visual?.Retire(); if (LineRoot) { LineRoot!.SetActive(false); Object.Destroy(LineRoot); } }
        public bool PrepareIdle(CreatureIdleAnimation animation, PresentationWorkBudget budget)
        {
            if (RavenAnimationRuntime.IsRaven(animation.Clip))
            {
                if (RavenRuntime != null) return true;
                if (!budget.Build()) return false;
                if (Visual == null || !RavenAnimationRuntime.TryCreate(Visual, Native, animation, out RavenRuntime))
                    throw new InvalidOperationException("原生乌鸦动画不可用，无法复现已停采片段。");
                return true;
            }
            if (IdleRuntime != null) return true;
            if (!budget.Build()) return false;
            if (Visual == null || !CreatureIdleAnimationRuntime.TryCreate(Visual, Native, animation, out IdleRuntime))
                throw new InvalidOperationException("原生蜘蛛待机动画不可用，无法复现已停采片段。");
            IdleLine = SpiderIdleLinePlayback.TryCreate(Visual, Native, Line);
            if (IdleLine == null) throw new InvalidOperationException("原生蜘蛛待机蛛丝模板不可用。");
            return true;
        }
        public bool ContinueBuild(PresentationWorkBudget budget)
        {
            if (LineRoot) return true;
            if (Visual!.IsResetting && !Visual.ContinueReuse(budget)) return false;
            if (!Visual.ContinueBuild(budget)) return false;
            if (Appearance == null) { if (!budget.Build()) return false; Appearance = new NativeVisualAppearance.Playback(Visual, Native, true); }
            if (!Appearance.ContinueBuild(budget)) return false;
            if (!lineInitialized)
            {
                if (!budget.Build()) return false;
                var spider = Native.GetComponent<Spider>();
                if (spider && spider.line && Visual.TryCloneTransform(spider.line.transform, out var target)) Line = CloneLine(spider.line, target);
                lineInitialized = true;
            }
            return true;
        }
        public void BeginReuse()
        {
            Hide(); if (Line) { Line!.enabled = false; Line.positionCount = 0; }
            if (Visual == null) return;
            Appearance?.Dispose(); Appearance = null; Visual.BeginReuse();
        }
    }
    private sealed class StaticEntry
    {
        public Transform Root = null!;
        public Renderer[] Renderers = Array.Empty<Renderer>();
        public string Path = "";
        public Vector3 OriginalPosition, OriginalScale;
        public Quaternion OriginalRotation;
        public bool Visible, FallbackVisible, FallbackRequested;
        public ObjectPose? AppliedPose, AppliedNext;
        public float AppliedMix;
        public int Seen;
        public VisualReplica? Fallback;
        public void Restore()
        {
            Fallback?.Dispose(); Fallback = null;
            if (!Root) return;
            Root.localPosition = OriginalPosition; Root.localRotation = OriginalRotation; Root.localScale = OriginalScale;
        }
    }
    private readonly struct RenderState
    {
        public readonly bool ForceOff, Enabled;
        public RenderState(Renderer source) { ForceOff = source.forceRenderingOff; Enabled = source.enabled; }
    }
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StaticEntry> statics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CreatureReplayFrame> following = new(StringComparer.Ordinal);
    private readonly Dictionary<Renderer, RenderState> hidden = new();
    private readonly Dictionary<WobbleSpinBounce, bool> suspendedCapyMotion = new();
    private readonly Dictionary<Renderer, StaticEntry> staticOwners = new();
    private readonly Dictionary<string, double> retry = new(StringComparer.Ordinal);
    private readonly HashSet<string> current = new(StringComparer.Ordinal), warned = new(StringComparer.Ordinal);
    private readonly List<KeyValuePair<string, Entry>> sleeping = new();
    private readonly List<string> removed = new();
    private readonly List<StaticEntry> fallbackSleeping = new();
    private readonly Action<string>? warning;
    private readonly CreatureReplayObserver observer;
    private readonly PresentationWorkBudget workBudget;
    private readonly Func<Vector3, float>? priorityScore;
    private readonly bool includeExtended;
    private readonly bool includeNativeBodies;
    private readonly List<CreatureReplayFrame> ordered = new();
    private readonly PresentationTemplatePool<Entry> pool = new(16, 8192, static entry => entry.Retire());
    private CreatureReplayFrame[] pendingLeft = Array.Empty<CreatureReplayFrame>(), pendingRight = Array.Empty<CreatureReplayFrame>();
    private float pendingMix;
    private double pendingTime;
    private CreatureObservationTimeline? pendingObservations;
    private bool pending;
    private int lastApplyFrame = -1;
    private int serial, enforcedFrame = -1, fallbackCount, fallbackUsed;
    private bool disposed;
    public int MissingCount { get; private set; }
    public int VisibleCount { get; private set; }
    public int PendingCount { get; private set; }
    public CreatureReplayPlayback(Action<string>? warning = null, PresentationWorkBudget? workBudget = null, Func<Vector3, float>? priorityScore = null, bool includeExtended = false, bool includeNativeBodies = false)
    {
        this.warning = warning;
        this.workBudget = workBudget ?? new PresentationWorkBudget();
        this.priorityScore = priorityScore;
        this.includeExtended = includeExtended;
        this.includeNativeBodies = includeNativeBodies;
        observer = new CreatureReplayObserver(Hide);
        try
        {
            CreatureReplayResources.Refresh();
            CreatureReplayResources.VisitScene(Hide);
        }
        catch { Dispose(); throw; }
    }
    private void Hide(GameObject source)
    {
        if (!source || CreatureReplayResources.Ours(source.transform)) return;
        string kind = CreatureReplayResources.Kind(source); if (kind.Length == 0) return;
        // Old files never owned these native bodies. Only schema 15 has a full
        // extended directory, including the meaningful absence of a new kind.
        if (!includeExtended && !CreatureDirectoryRules.IsLegacyKind(kind)) return;
        if (!includeNativeBodies && CreatureDirectoryRules.BodyRequiresSchema21(kind)) return;
        CreatureReplayResources.Register(source, kind);
        bool frog = kind == "frog-tongue";
        if (kind == "capybara")
            foreach (var motion in source.GetComponentsInChildren<WobbleSpinBounce>(true))
                if (motion && !suspendedCapyMotion.ContainsKey(motion))
                { suspendedCapyMotion.Add(motion, motion.enabled); motion.enabled = false; }
        foreach (var renderer in source.GetComponentsInChildren<Renderer>(true))
        {
            if (frog && !(renderer is LineRenderer)) continue; // ItemPlayback owns the body.
            if (!(renderer is MeshRenderer || renderer is SkinnedMeshRenderer || renderer is LineRenderer) || hidden.ContainsKey(renderer)) continue;
            hidden.Add(renderer, new RenderState(renderer)); renderer.forceRenderingOff = true;
        }
    }
    public void Enforce(bool completePending = true)
    {
        if (disposed || enforcedFrame == Time.frameCount) return;
        workBudget.BeginFrame(Time.frameCount); VisualReplica.DrainRetired(workBudget);
        if (completePending && pending && lastApplyFrame != Time.frameCount) Apply(pendingLeft, pendingRight, pendingMix, pendingTime, false, pendingObservations);
        enforcedFrame = Time.frameCount;
        foreach (var pair in hidden)
            if (pair.Key)
            {
                bool desired = pair.Value.ForceOff || !staticOwners.TryGetValue(pair.Key, out var owner) || !owner.Visible;
                if (pair.Key.forceRenderingOff != desired) pair.Key.forceRenderingOff = desired;
            }
    }
    public void Apply(CreatureReplayFrame[] left, CreatureReplayFrame[] right, float mix, double time, bool discontinuity, CreatureObservationTimeline? observations = null)
    {
        if (disposed) throw new ObjectDisposedException(nameof(CreatureReplayPlayback));
        lastApplyFrame = Time.frameCount; workBudget.BeginFrame(Time.frameCount);
        VisualReplica.DrainRetired(workBudget);
        pendingLeft = left; pendingRight = right; pendingMix = mix; pendingTime = time; pendingObservations = observations; pending = false;
        serial++; MissingCount = VisibleCount = PendingCount = fallbackUsed = 0; following.Clear(); current.Clear();
        foreach (var entry in statics.Values) entry.Visible = entry.FallbackVisible = entry.FallbackRequested = false;
        foreach (var frame in right) following[frame.Key] = frame;
        bool prioritize = false;
        foreach (var frame in left) if (frame.Pose.Active)
        {
            current.Add(frame.Key);
            if (CreatureReplayRules.CompactStaticTrap(frame))
            {
                if (!statics.TryGetValue(frame.Key, out var trap) || trap.Root && !trap.Root.gameObject.activeSelf && (trap.Fallback == null || !trap.Fallback.IsReady)) prioritize = true;
            }
            else if (!entries.TryGetValue(frame.Key, out var existing) || !existing.Ready) prioritize = true;
        }
        Reclaim();
        ordered.Clear(); ordered.AddRange(left);
        if (prioritize && priorityScore != null) ordered.Sort((a, b) => { int score = priorityScore(V(a.Pose.Position)).CompareTo(priorityScore(V(b.Pose.Position))); return score != 0 ? score : string.CompareOrdinal(a.Key, b.Key); });
        double now = Time.realtimeSinceStartupAsDouble;
        foreach (var recorded in ordered)
        {
            var frame = recorded;
            following.TryGetValue(frame.Key, out var next);
            float frameMix = mix;
            bool interpolate;
            if (frame.SampleTimeKnown && observations != null && observations.TrySample(frame.Key, time, out var observedLeft, out var observedRight, out float observedMix))
            {
                frame = observedLeft; next = observedRight; frameMix = observedMix;
                // Visibility/topology cuts were checked once by the worker,
                // avoiding a full node-tree comparison in the display loop.
                interpolate = !ReferenceEquals(frame, next);
            }
            else interpolate = next != null && CreatureReplayRules.CanInterpolate(frame, next);
            if (retry.TryGetValue(frame.Key, out double nextRetry) && now < nextRetry) { MissingCount++; continue; }
            entries.TryGetValue(frame.Key, out var entry);
            try
            {
                if (CreatureReplayRules.CompactStaticTrap(frame))
                {
                    if (entry != null) { entry.Retire(); entries.Remove(frame.Key); entry = null; }
                    ApplyStatic(frame, mix); retry.Remove(frame.Key); continue;
                }
                if (entry != null && (entry.Kind != frame.Kind || entry.Resource != frame.Resource || entry.Path != frame.SourcePath))
                { entry.Retire(); entries.Remove(frame.Key); entry = null; }
                if (entry == null)
                {
                    if (!frame.Pose.Active) continue;
                    var native = CreatureReplayResources.Find(frame.Kind, frame.Resource, frame.SourcePath);
                    if (!native) throw new InvalidOperationException("原版生物模板不可用：" + frame.Kind);
                    if (entries.Count >= CreatureReplayRules.MaximumDynamicEntities * 2 + 16) throw new InvalidOperationException("生物视觉池已满");
                    string template = frame.Kind + "\n" + native!.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    entry = TakePooledEntry(template, frame.Key);
                    if (entry != null) { if (entry.Key != frame.Key) entry.BeginReuse(); }
                    else
                    {
                    if (!VisualReplica.CanStartBuild || !workBudget.Create()) { pending = true; PendingCount++; continue; }
                    entry = new Entry { Kind = frame.Kind, Resource = frame.Resource, Path = frame.SourcePath, Native = native! };
                    try
                    {
                        if (frame.Kind == "frog-tongue")
                        {
                            var line = native!.GetComponentInChildren<LineRenderer>(true);
                            if (!line) throw new InvalidOperationException("原版青蛙舌头模板不可用");
                            entry.LineRoot = new GameObject("PEAK Replay Creature - frog-tongue") { layer = line!.gameObject.layer };
                            entry.LineRoot.SetActive(false);
                            entry.Line = CloneLine(line, entry.LineRoot.transform);
                        }
                        else
                        {
                            entry.Visual = VisualReplica.BeginBuild(native!, false, CreatureReplayRules.MaximumNodes);
                            entry.Visual.Root.name = "PEAK Replay Creature - " + frame.Kind;
                        }
                    }
                    catch { entry.Retire(); entry = null; throw; }
                    }
                    entry.Key = frame.Key; entry.Resource = frame.Resource; entry.Path = frame.SourcePath; entries.Add(frame.Key, entry);
                }
                if (discontinuity) entry.Hide();
                if (!entry.ContinueBuild(workBudget)) { entry.Defer(); pending = true; PendingCount++; continue; }
                if (frame.IdleAnimation != null && !entry.PrepareIdle(frame.IdleAnimation, workBudget))
                { entry.Defer(); pending = true; PendingCount++; continue; }
                if (entry.Appearance != null)
                {
                    bool appearanceReady = entry.ApplyAppearance(frame.Visuals, workBudget);
                    if (!appearanceReady)
                    {
                        entry.Defer(); pending = true; PendingCount++;
                        if (entry.Presented) VisibleCount++;
                        continue;
                    }
                    if (entry.NeedsRefresh) { pending = true; PendingCount++; }
                }
                if (discontinuity) entry.Visual?.InvalidatePose();
                bool nativeIdle = frame.IdleAnimation != null;
                bool resetIdle = discontinuity || !entry.IdlePresented || !ReferenceEquals(entry.IdlePose, frame.Pose) || !ReferenceEquals(entry.IdleFrame, frame.IdleAnimation);
                if (entry.IdlePresented && (!nativeIdle || resetIdle)) entry.Visual?.InvalidatePose();
                if (resetIdle) { entry.IdleRuntime?.Invalidate(); entry.RavenRuntime?.Invalidate(); }
                entry.Visual?.Apply(frame.Pose, interpolate ? next!.Pose : null, frameMix);
                if (entry.LineRoot)
                {
                    // Frog line points are sampled in world space, independent
                    // of the ItemPlayback body's pool and lifetime.
                    entry.LineRoot!.SetActive(frame.Pose.Active);
                }
                entry.Appearance?.ReapplyVisibility();
                if (nativeIdle)
                {
                    if (RavenAnimationRuntime.IsRaven(frame.IdleAnimation!.Clip))
                        entry.RavenRuntime!.Apply(frame.IdleAnimation, time);
                    else
                    {
                        entry.IdleRuntime!.Apply(frame.IdleAnimation!, time);
                        // The native idle clip moves the abdomen. Rebuild the silk
                        // once from those cloned endpoints rather than writing the
                        // held 40 points and then overwriting all 40 a second time.
                        ApplyLineState(entry.Line, frame.Line, null, frameMix);
                        if (!entry.IdleLine!.TryApply(frame.IdleAnimation!.RopePhase))
                            ApplyLine(entry.Line, frame.Line, null, frameMix);
                    }
                }
                else ApplyLine(entry.Line, frame.Line, interpolate ? next!.Line : null, frameMix);
                entry.IdlePresented = nativeIdle; entry.IdlePose = frame.Pose; entry.IdleFrame = frame.IdleAnimation;
                entry.Presented = frame.Pose.Active;
                entry.Seen = serial; retry.Remove(frame.Key);
                if (frame.Pose.Active) VisibleCount++;
            }
            catch (Exception e)
            {
                entry?.Retire(); entries.Remove(frame.Key); MissingCount++; retry[frame.Key] = now + 1;
                if (warned.Count < 128 && warned.Add(frame.Kind)) warning?.Invoke("生物视觉缺失：" + e.Message);
            }
        }
        ReclaimStaticFallbacks();
        foreach (var entry in statics.Values) ApplyStaticVisibility(entry);
        Reclaim();
        removed.Clear(); foreach (var pair in retry) if (!current.Contains(pair.Key)) removed.Add(pair.Key);
        foreach (var key in removed) retry.Remove(key);
    }
    // Capturing a foreach frame here would allocate even for ready creatures and
    // static traps. Only a missing visual needs the pool predicates.
    private Entry? TakePooledEntry(string template, string key) =>
        pool.Take(template, candidate => candidate.Key == key, candidate => !current.Contains(candidate.Key));

    private void ApplyStatic(CreatureReplayFrame frame, float mix)
    {
        if (!statics.TryGetValue(frame.Key, out var entry) || !entry.Root || entry.Path != frame.SourcePath)
        {
            if (entry != null)
            {
                foreach (var renderer in entry.Renderers) if (renderer) staticOwners.Remove(renderer);
                if (entry.Fallback != null) fallbackCount--; entry.Restore(); statics.Remove(frame.Key);
            }
            var native = CreatureReplayResources.Find("spore-trap", frame.SourcePath);
            if (!native) throw new InvalidOperationException("原版静态孢子陷阱不可用：" + frame.SourcePath);
            if (statics.Count >= CreatureReplayRules.MaximumStaticTraps) throw new InvalidOperationException("静态孢子陷阱目录已满");
            Hide(native!);
            var root = native!.transform;
            entry = new StaticEntry { Root = root, Path = frame.SourcePath,
                OriginalPosition = root.localPosition, OriginalRotation = root.localRotation, OriginalScale = root.localScale,
                Renderers = native.GetComponentsInChildren<Renderer>(true) };
            statics.Add(frame.Key, entry);
            foreach (var renderer in entry.Renderers) if (renderer && hidden.ContainsKey(renderer)) staticOwners[renderer] = entry;
        }
        following.TryGetValue(frame.Key, out var next);
        bool blend = next != null && CreatureReplayRules.CompactStaticTrap(next) && CreatureReplayRules.CanInterpolate(frame, next);
        var pose = frame.Pose;
        var nextPose = blend ? next!.Pose : pose;
        // The static model has no animated children. Most samples retain the
        // exact same root snapshot, so avoid rewriting thousands of native TRS.
        if (!ReferenceEquals(entry.AppliedPose, pose) || !ReferenceEquals(entry.AppliedNext, nextPose) ||
            !ReferenceEquals(pose, nextPose) && entry.AppliedMix != mix)
        {
            var position = V(pose.Position); var rotation = Q(pose.Rotation); var scale = V(pose.Scale);
            if (blend)
            {
                position = Vector3.LerpUnclamped(position, V(nextPose.Position), mix);
                rotation = Quaternion.SlerpUnclamped(rotation, Q(nextPose.Rotation), mix);
                scale = Vector3.LerpUnclamped(scale, V(nextPose.Scale), mix);
            }
            // Preserve the exact authored local scale when the scene already
            // has this pose (lossyScale can include a rotated nonuniform parent).
            if (entry.Root.position != position || entry.Root.rotation != rotation || entry.Root.lossyScale != scale)
            {
                entry.Root.SetPositionAndRotation(position, rotation);
                var parentScale = entry.Root.parent ? entry.Root.parent.lossyScale : Vector3.one;
                entry.Root.localScale = new Vector3(Divide(scale.x, parentScale.x), Divide(scale.y, parentScale.y), Divide(scale.z, parentScale.z));
            }
            entry.AppliedPose = pose; entry.AppliedNext = nextPose; entry.AppliedMix = mix;
        }
        entry.Seen = serial;
        // Never activate the native gameplay root. Usually fresh scene traps
        // are activeSelf=true and their original meshes can be reused directly.
        bool parentActive = !entry.Root.parent || entry.Root.parent.gameObject.activeInHierarchy;
        entry.Visible = pose.Active && entry.Root.gameObject.activeSelf;
        entry.FallbackRequested = pose.Active && parentActive && !entry.Root.gameObject.activeSelf;
        if (entry.FallbackRequested)
        {
            if (fallbackUsed >= CreatureReplayRules.MaximumDynamicEntities)
                throw new InvalidOperationException("已消耗陷阱的回拖视觉超过256个");
            if (entry.Fallback == null)
            {
                if (!VisualReplica.CanStartBuild || !workBudget.Create()) { pending = true; PendingCount++; return; }
                MakeFallbackRoom();
                entry.Fallback = VisualReplica.BeginBuild(entry.Root.gameObject, true, CreatureReplayRules.MaximumNodes);
                entry.Fallback.Root.name = "PEAK Replay Creature - static trap";
                fallbackCount++;
            }
            if (!entry.Fallback.ContinueBuild(workBudget)) { pending = true; PendingCount++; return; }
            if (!entry.Fallback.Root.transform.parent)
            {
                foreach (var renderer in entry.Renderers)
                    if (renderer && hidden.TryGetValue(renderer, out var original) && entry.Fallback.TryCloneRenderer(renderer, out var copy))
                        copy.forceRenderingOff = original.ForceOff;
                // The parent's recorded biome activation still governs the inert model.
                entry.Fallback.Root.transform.SetParent(entry.Root.parent, true);
            }
            entry.Fallback.Apply(pose, blend ? next!.Pose : null, mix);
            // VisualReplica normally has no parent and interprets Scale as a
            // world scale. This inert fallback follows the recorded biome.
            entry.Fallback.Root.transform.localScale = entry.Root.localScale;
            entry.FallbackVisible = true; fallbackUsed++;
        }
        ApplyStaticVisibility(entry);
        if (pose.Active && parentActive) VisibleCount++;
    }
    private void ApplyStaticVisibility(StaticEntry entry)
    {
        foreach (var renderer in entry.Renderers)
            if (renderer && hidden.TryGetValue(renderer, out var original))
            {
                bool desired = original.ForceOff || !entry.Visible;
                if (renderer.forceRenderingOff != desired) renderer.forceRenderingOff = desired;
            }
        if (!entry.FallbackVisible) entry.Fallback?.Hide();
    }
    private void MakeFallbackRoom()
    {
        if (fallbackCount < CreatureReplayRules.MaximumDynamicEntities + 16) return;
        StaticEntry? oldest = null;
        foreach (var pair in statics)
        {
            var candidate = pair.Value;
            if (candidate.Fallback != null && !current.Contains(pair.Key) && !candidate.FallbackVisible && (oldest == null || candidate.Seen < oldest.Seen)) oldest = candidate;
        }
        if (oldest == null) throw new InvalidOperationException("静态陷阱回拖视觉池已满");
        oldest.Fallback!.Retire(); oldest.Fallback = null; fallbackCount--;
    }
    private void ReclaimStaticFallbacks()
    {
        fallbackSleeping.Clear();
        foreach (var entry in statics.Values) if (entry.Fallback != null && !entry.FallbackVisible)
        {
            if (entry.FallbackRequested) continue;
            if (!entry.Fallback.IsReady) { entry.Fallback.Retire(); entry.Fallback = null; fallbackCount--; }
            else fallbackSleeping.Add(entry);
        }
        fallbackSleeping.Sort((a, b) => a.Seen.CompareTo(b.Seen));
        for (int i = 0; i < fallbackSleeping.Count - 16; i++)
        { fallbackSleeping[i].Fallback!.Retire(); fallbackSleeping[i].Fallback = null; fallbackCount--; }
    }
    private static Vector3 V(float[] values) => new(values[0], values[1], values[2]);
    private static Quaternion Q(float[] values) => new(values[0], values[1], values[2], values[3]);
    private static float Divide(float value, float denominator) => Mathf.Abs(denominator) > .000001f ? value / denominator : value;
    private void Reclaim()
    {
        removed.Clear();
        foreach (var pair in entries) if (!current.Contains(pair.Key))
        {
            pair.Value.Hide(); removed.Add(pair.Key);
            if (pair.Value.Ready) pool.Return(pair.Value.Template, pair.Value, pair.Value.Visual?.RetainedCost ?? 4);
            else pair.Value.Retire();
        }
        foreach (var key in removed) entries.Remove(key);
    }
    private static LineRenderer CloneLine(LineRenderer source, Transform target)
    {
        var line = target.gameObject.AddComponent<LineRenderer>();
        line.enabled = false; line.sharedMaterials = source.sharedMaterials;
        line.useWorldSpace = source.useWorldSpace; line.widthCurve = source.widthCurve; line.widthMultiplier = source.widthMultiplier;
        line.colorGradient = source.colorGradient; line.numCapVertices = source.numCapVertices; line.numCornerVertices = source.numCornerVertices;
        line.textureMode = source.textureMode; line.alignment = source.alignment; line.loop = source.loop;
        line.generateLightingData = source.generateLightingData; line.shadowCastingMode = source.shadowCastingMode; line.receiveShadows = source.receiveShadows;
        return line;
    }
    private static void ApplyLine(LineRenderer? target, CreatureReplayLine? left, CreatureReplayLine? right, float mix)
    {
        ApplyLineState(target, left, right, mix);
        if (!target) return;
        if (left == null) { target!.enabled = false; return; }
        bool blend = right != null && left.WorldSpace == right.WorldSpace && left.Points.Length == right.Points.Length && left.Enabled && right.Enabled;
        for (int i = 0; i < target!.positionCount; i++)
        {
            int at = i * 3;
            var a = new Vector3(left.Points[at], left.Points[at + 1], left.Points[at + 2]);
            var b = blend ? new Vector3(right!.Points[at], right.Points[at + 1], right.Points[at + 2]) : a;
            target.SetPosition(i, Vector3.LerpUnclamped(a, b, mix));
        }
    }
    private static void ApplyLineState(LineRenderer? target, CreatureReplayLine? left, CreatureReplayLine? right, float mix)
    {
        if (!target) return;
        if (left == null) { target!.enabled = false; return; }
        bool blend = right != null && left.WorldSpace == right.WorldSpace && left.Points.Length == right.Points.Length && left.Enabled && right.Enabled;
        target!.useWorldSpace = left.WorldSpace; target.enabled = left.Enabled;
        target.widthMultiplier = blend ? Mathf.LerpUnclamped(left.Width, right!.Width, mix) : left.Width;
        target.positionCount = left.Points.Length / 3;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; observer.Dispose();
        foreach (var entry in entries.Values) entry.Dispose(); entries.Clear();
        foreach (var entry in statics.Values) entry.Restore(); statics.Clear();
        foreach (var pair in hidden) if (pair.Key) { pair.Key.forceRenderingOff = pair.Value.ForceOff; pair.Key.enabled = pair.Value.Enabled; }
        foreach (var pair in suspendedCapyMotion) if (pair.Key) pair.Key.enabled = pair.Value;
        suspendedCapyMotion.Clear();
        hidden.Clear(); staticOwners.Clear(); retry.Clear();
        pool.Clear(); VisualReplica.FlushRetired();
    }
}
