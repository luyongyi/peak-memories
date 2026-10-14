using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

internal static class WebWrapReplayCapture
{
    private static readonly ConditionalWeakTable<Character, Cache> caches = new();
    private sealed class Cache { public WebWrapReplayFrame? Last; }
    public static WebWrapReplayFrame? Sample(Character character)
    {
        var wrappers = character.refs?.afflictions?.webWraps;
        if (wrappers == null || wrappers.Count > WebWrapReplayRules.MaximumParts) return null;
        var cache = caches.GetValue(character, _ => new Cache());
        var previous = cache.Last;
        bool changed = previous == null || previous.Parts.Length != wrappers.Count;
        WebWrapReplayPart[]? parts = changed ? new WebWrapReplayPart[wrappers.Count] : null;
        for (int i = 0; i < wrappers.Count; i++)
        {
            var source = wrappers[i];
            if (!source || !source.transform.IsChildOf(character.transform)) return null;
            var material = source.sharedMaterial;
            if (!material || !material.HasProperty("_Clip")) return null;
            float clip = material.GetFloat("_Clip");
            var rotation = source.transform.localRotation;
            var scale = source.transform.parent.localScale;
            var old = previous != null && i < previous.Parts.Length ? previous.Parts[i] : null;
            bool active = source.gameObject.activeSelf && source.enabled;
            if (old != null && old.Active == active && old.Clip == clip && Quat(old.Rotation, rotation) && Vec(old.ParentScale, scale))
            { if (parts != null) parts[i] = old; continue; }
            if (parts == null) { parts = (WebWrapReplayPart[])previous!.Parts.Clone(); changed = true; }
            parts[i] = new WebWrapReplayPart { Path = ActorJointPaths.Path(source.transform, character.transform), Active = active,
                Clip = clip, Rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w }, ParentScale = new[] { scale.x, scale.y, scale.z } };
        }
        return changed ? cache.Last = new WebWrapReplayFrame { Parts = parts! } : previous;
    }
    private static bool Vec(float[] a, Vector3 p) => a[0] == p.x && a[1] == p.y && a[2] == p.z;
    private static bool Quat(float[] a, Quaternion p) => a[0] == p.x && a[1] == p.y && a[2] == p.z && a[3] == p.w;
}

// Birth observations update a bounded directory. Sampling reads completed native
// animation/physics results; it never calls Scan, Spawn, RPC, Trigger or damage.
internal sealed class CreatureReplayCapture : IDisposable
{
    private sealed class Entry
    {
        public GameObject Source = null!;
        public Transform Root = null!;
        public Spider? Spider;
        public MushroomZombie? Zombie;
        public Condor? Condor;
        public ScoutmasterCaptureActivity? ScoutmasterActivity;
        public CreatureCaptureActivity Activity = null!;
        public CreatureActionState ActionState = null!;
        public readonly CreatureSamplingCadence Cadence = new();
        public CreatureIdleAnimationRuntime.Capture? IdleCapture;
        public RavenAnimationCapture? RavenCapture;
        public NativeCreatureQuietCapture? QuietCapture;
        public Func<ObjectPose>? RavenRoot;
        public FrogTongueCaptureActivity? TongueActivity;
        public LineRenderer? Line;
        public string Key = "", Kind = "", Resource = "", Path = "";
        public CreatureReplayFrame? Last;
        public double RetryAfter, AppearanceRetryAfter, IdleRetryAfter;
        public bool AppearanceDirty = true, InitialUrgent, InitialAppearanceObserved;
        public Vector3[] LineScratch = Array.Empty<Vector3>();
    }
    private readonly CreatureReplayObserver observer;
    private readonly SegmentCaptureClient? segments;
    private readonly Dictionary<int, Entry> entries = new();
    private readonly StaticCaptureDirectory<Entry, CreatureReplayFrame> staticEntries = new();
    private readonly Func<Entry, CreatureReplayFrame?, CreatureReplayFrame?> readStatic;
    private readonly Action<Entry, Exception> staticError;
    private readonly List<int> removed = new();
    private readonly List<CreatureReplayFrame> frames = new();
    private readonly HashSet<string> warned = new(StringComparer.Ordinal);
    private readonly Action<string>? warning;
    private readonly int scene = SceneManager.GetActiveScene().handle;
    private CreatureReplayFrame[] previous = Array.Empty<CreatureReplayFrame>();
    private CreatureReplayFrame[] previousStatic = Array.Empty<CreatureReplayFrame>(), previousDynamic = Array.Empty<CreatureReplayFrame>();
    private long serial;
    private int dynamicCount;
    private bool disposed;
    internal bool InitialBaselineReady
    {
        get
        {
            if (!staticEntries.InitialBaselineReady) return false;
            foreach (var entry in entries.Values)
                if (entry.Source && (entry.Last == null || !entry.InitialAppearanceObserved) &&
                    (entry.ScoutmasterActivity == null || entry.ScoutmasterActivity.Active)) return false;
            return true;
        }
    }
    private readonly Func<bool> beginInitialRead = CaptureBootstrapWorkBudget.TryBeginRead;
    private long geometryReads, nodeVisits, lineReads, linePointVisits, managedStateChecks, boundaryReads;
    private long spiderIdleReads, spiderIdleSkips, spiderActionReads, beetleAwakeReads, beetleSleepingSkips;
    private long ravenClockChecks, ravenGeometryReads, ravenGeometrySkips, ravenMouthFallbacks;
    private long ravenPhaseCorrections, ravenPhaseRootReads;
    private long ravenScheduleSkips, ravenSemanticWakes;
    private long venusQuietSkips, capybaraQuietSkips, quietGeometryReads, quietSafetyChecks;
    private long frogTongueIdleSkips, frogTongueActiveReads;
    private long lowRateSkips, lowRateReads, actionConfirmations;
    internal object Diagnostics => new
    {
        GeometryReads = geometryReads, NodeVisits = nodeVisits, LineReads = lineReads, LinePointVisits = linePointVisits,
        ManagedStateChecks = managedStateChecks, BoundaryReads = boundaryReads,
        SpiderIdleReads = spiderIdleReads, SpiderIdleSkips = spiderIdleSkips, SpiderActionReads = spiderActionReads,
        BeetleAwakeReads = beetleAwakeReads, BeetleSleepingSkips = beetleSleepingSkips,
        RavenClockChecks = ravenClockChecks, RavenGeometryReads = ravenGeometryReads, RavenGeometrySkips = ravenGeometrySkips, RavenMouthFallbacks = ravenMouthFallbacks,
        RavenPhaseCorrections = ravenPhaseCorrections, RavenPhaseRootReads = ravenPhaseRootReads,
        RavenScheduleSkips = ravenScheduleSkips, RavenSemanticWakes = ravenSemanticWakes,
        VenusQuietSkips = venusQuietSkips, CapybaraQuietSkips = capybaraQuietSkips, QuietGeometryReads = quietGeometryReads, QuietSafetyChecks = quietSafetyChecks,
        FrogTongueIdleSkips = frogTongueIdleSkips, FrogTongueActiveReads = frogTongueActiveReads,
        LowRateSkips = lowRateSkips, LowRateReads = lowRateReads, ActionConfirmations = actionConfirmations,
        Measurement = "Active creature geometry uses a 5 Hz cadence; cached semantic edges remain on the scene clock. Unassigned audited Raven clips use staggered 0.75-1 second Animator/root safety audits; assignment and unknown inputs retain pose fallback. Native events receive bounded follow-up confirmations. Sleeping beetles, closed tongues, validated idle spiders, static Capybaras and audited already-disabled Venus idle poses retain their checkpoint."
    };
    private static readonly AccessTools.FieldRef<Condor, Condor.CondorState> condorState =
        AccessTools.FieldRefAccess<Condor, Condor.CondorState>("_state");
    public CreatureReplayCapture(Action<string>? warning = null)
    {
        this.warning = warning;
        readStatic = SampleStatic;
        staticError = (entry, error) => Warn(entry.Kind, "静态陷阱事件采样暂不可用，已安排有限重试：" + error.Message);
        segments = CaptureSegmentScope.Active?.CreateClient(source => Register(source), Retire);
        // Awake/Start/OnEnable also run in bulk when a new biome is activated.
        // Only completed native actions, handled by Changed, bypass the budget.
        try { observer = new CreatureReplayObserver(source => Register(source), Destroyed, Changed, AppearanceChanged); }
        catch { segments?.Dispose(); throw; }
        try
        {
            CreatureReplayResources.Refresh();
            CreatureReplayResources.VisitScene(source => Register(source));
        }
        catch { Dispose(); throw; }
    }
    private void Register(GameObject source, bool urgent = false)
    {
        if (disposed || ReplaySafety.Active || !source || source.scene.handle != scene || CreatureReplayResources.Ours(source.transform)) return;
        if (!(segments?.Admit(source, urgent) ?? true)) return;
        if (entries.TryGetValue(source.GetInstanceID(), out var existing)) { existing.InitialUrgent |= urgent; return; }
        if (staticEntries.Contains(source.GetInstanceID())) { if (urgent) staticEntries.Changed(source.GetInstanceID(), true); return; }
        string kind = CreatureReplayResources.Kind(source);
        if (kind.Length == 0) return;
        var spider = source.GetComponent<Spider>(); var zombie = source.GetComponent<MushroomZombie>();
        var condor = source.GetComponent<Condor>(); var frog = source.GetComponent<FrogTongue>();
        bool isStatic = kind == "spore-trap";
        if (isStatic ? staticEntries.Count >= CreatureReplayRules.MaximumStaticTraps : dynamicCount >= CreatureReplayRules.MaximumDynamicEntities)
        { Warn(isStatic ? "static-limit" : "dynamic-limit", isStatic ? "静态孢子陷阱超过8192个，超出部分未录制。" : "动态生物超过256个，超出部分未录制。"); return; }
        string resource = CreatureReplayResources.Resource(source, kind, zombie);
        CreatureReplayResources.Register(source, kind, resource);
        var entry = new Entry { Source = source, Root = source.transform, Spider = spider, Zombie = zombie, Condor = condor, Kind = kind, Resource = resource,
            Key = "creature:" + source.GetInstanceID() + ":" + ++serial, Path = CreatureReplayResources.SourcePath(source, kind),
            Line = spider ? spider!.line : frog ? source.GetComponentInChildren<LineRenderer>(true) : null,
            LineScratch = spider ? new Vector3[CreatureReplayRules.MaximumLinePoints] : frog ? new Vector3[CreatureReplayRules.MaximumFrogLinePoints] : Array.Empty<Vector3>(),
            InitialUrgent = urgent, InitialAppearanceObserved = kind == "frog-tongue" };
        entry.Activity = new CreatureCaptureActivity(spider, kind == "beetle" ? source.GetComponent<Mob>() : null, followSceneClock: true);
        entry.ActionState = new CreatureActionState(source, kind);
        if (kind == "venus-flytrap" || kind == "capybara")
            entry.QuietCapture = new NativeCreatureQuietCapture(source, kind, resource);
        if (spider) entry.IdleCapture = new CreatureIdleAnimationRuntime.Capture(spider!, resource);
        if (kind == "voice-thief")
        {
            entry.RavenCapture = new RavenAnimationCapture(source.GetComponent<Peak.Island.VoiceThief>());
            entry.RavenRoot = () => ReadRavenRoot(entry);
        }
        if (frog) entry.TongueActivity = new FrogTongueCaptureActivity(frog!, entry.Line);
        if (kind == "scoutmaster")
            entry.ScoutmasterActivity = new ScoutmasterCaptureActivity(source.GetComponent<Scoutmaster>());
        // Publish only the fully initialized entry: a failed birth observation
        // must not leave a Scoutmaster that bypasses the activity gate forever.
        if (isStatic) staticEntries.Register(source.GetInstanceID(), entry, urgent);
        else entries[source.GetInstanceID()] = entry;
        if (!isStatic) dynamicCount++;
    }
    private void Retire(int id)
    {
        bool removedStatic = staticEntries.Remove(id);
        if (entries.TryGetValue(id, out var entry))
        {
            entry.ScoutmasterActivity?.Dispose();
            VisualReplica.ReleaseCapture(entry.Root);
            entries.Remove(id); dynamicCount--;
        }
        else if (!removedStatic) return;
        previous = previousStatic = previousDynamic = Array.Empty<CreatureReplayFrame>();
    }
    private void Destroyed(GameObject source)
    { if (source) { int id = source.GetInstanceID(); segments?.Forget(id); Retire(id); } }
    private void Changed(GameObject source)
    {
        Register(source, true);
        if (source && staticEntries.Changed(source.GetInstanceID(), true)) return;
        if (source && entries.TryGetValue(source.GetInstanceID(), out var entry))
        {
            entry.RetryAfter = entry.AppearanceRetryAfter = 0;
            entry.IdleRetryAfter = 0;
            entry.Activity.NotifyNativeEvent();
            entry.Cadence.NotifyNativeEvent(Time.timeAsDouble, entry.TongueActivity != null ? .3 : entry.Kind == "venus-flytrap" ? 1 : 0);
            entry.RavenCapture?.NotifyNativeEvent();
            entry.QuietCapture?.NotifyNativeEvent(Time.timeAsDouble);
            entry.TongueActivity?.NotifyNativeEvent();
            // Arise converts a player zombie after Awake. Its canonical visual
            // must follow the final native variant, not the temporary birth flag.
            if (entry.Zombie)
            {
                string resource = CreatureReplayResources.Resource(source, entry.Kind, entry.Zombie);
                if (entry.Resource != resource) { entry.Resource = resource; entry.AppearanceDirty = true; }
            }
        }
    }
    private void AppearanceChanged(GameObject source)
    {
        Changed(source);
        if (source && entries.TryGetValue(source.GetInstanceID(), out var entry)) entry.AppearanceDirty = true;
    }
    public CreatureReplayFrame[] Capture(double now)
    {
        if (disposed) throw new ObjectDisposedException(nameof(CreatureReplayCapture));
        var staticFrames = staticEntries.Capture(now, readStatic, staticError, beginInitialRead);
        frames.Clear(); removed.Clear();
        foreach (var pair in entries)
        {
            var entry = pair.Value;
            if (!entry.Source) { removed.Add(pair.Key); continue; }
            try
            {
                // Native Scoutmaster stays enabled at its spawn/parking location.
                // Do not walk that idle rig or write it into every page. Check
                // before retry suppression so retirement cannot leak stale
                // frames. A later appearance starts with a complete snapshot.
                if (entry.ScoutmasterActivity != null && !entry.ScoutmasterActivity.Active)
                {
                    entry.Last = null;
                    entry.RetryAfter = entry.AppearanceRetryAfter = 0;
                    entry.AppearanceDirty = true;
                    entry.Activity.NotifyNativeEvent();
                    continue;
                }
                if (now < entry.RetryAfter) { if (entry.Last != null) frames.Add(entry.Last); continue; }
                if (CaptureBootstrapWorkBudget.InitialOnly && entry.Last != null && !entry.InitialUrgent &&
                    (entry.InitialAppearanceObserved || now < entry.AppearanceRetryAfter))
                { frames.Add(entry.Last); continue; }
                if ((entry.Last == null || !entry.InitialAppearanceObserved) && !entry.InitialUrgent && !CaptureBootstrapWorkBudget.TryBeginRead()) continue;
                entry.InitialUrgent = false;
                if (!entry.InitialAppearanceObserved && now >= entry.AppearanceRetryAfter) entry.Activity.NotifyNativeEvent();
                if (entry.RetryAfter != 0) { entry.Activity.NotifyNativeEvent(); entry.RetryAfter = 0; }
                // Only admitted region entries reach this loop. Native activity
                // edges bypass the shared animation clock within that region.
                bool active = entry.Source.activeInHierarchy;
                bool activeChanged = entry.Last != null && entry.Last.Pose.Active != active;
                managedStateChecks++;
                if (entry.TongueActivity != null && !entry.TongueActivity.Due(now, active))
                { frogTongueIdleSkips++; if (entry.Last != null) frames.Add(entry.Last); continue; }
                if (!entry.Activity.Due(now, active))
                {
                    if (entry.Kind == "beetle" && entry.Activity.ClockMode == CreatureReplayRules.SleepingClock) beetleSleepingSkips++;
                    if (entry.Kind == "spider" && entry.Activity.ClockMode == CreatureReplayRules.IdleClock) spiderIdleSkips++;
                    frames.Add(entry.Last!); continue;
                }
                int nativeState = entry.Spider ? (int)entry.Spider!.spiderState : entry.Zombie ? (int)entry.Zombie!.currentState :
                    entry.Condor ? (int)condorState(entry.Condor!) : entry.ActionState.State;
                bool quietBoundary = false;
                if (entry.QuietCapture != null)
                {
                    long oldSafetyChecks = entry.QuietCapture.QuietSafetyChecks;
                    bool due = entry.QuietCapture.Due(now, entry.Last,
                        entry.AppearanceDirty || !entry.InitialAppearanceObserved || entry.Cadence.NeedsConfirmation);
                    quietSafetyChecks += entry.QuietCapture.QuietSafetyChecks - oldSafetyChecks;
                    if (!due)
                    {
                        if (entry.Kind == "venus-flytrap") venusQuietSkips++; else capybaraQuietSkips++;
                        if (entry.Last != null) frames.Add(entry.Last);
                        continue;
                    }
                    quietBoundary = entry.QuietCapture.LastWasBoundary;
                    if (entry.QuietCapture.RequiresAppearanceRead) entry.AppearanceDirty = true;
                }
                if (entry.RavenCapture != null)
                {
                    long oldWakes = entry.RavenCapture.SemanticWakes;
                    bool due = entry.RavenCapture.Due(now, active,
                        entry.AppearanceDirty || !entry.InitialAppearanceObserved || entry.Cadence.NeedsConfirmation);
                    ravenSemanticWakes += entry.RavenCapture.SemanticWakes - oldWakes;
                    if (!due) { lowRateSkips++; ravenScheduleSkips++; if (entry.Last != null) frames.Add(entry.Last); continue; }
                }
                // The native frog can reveal its tongue after an attack delay.
                // One visibility/pulling edge gets a fixed detail window; the
                // continually changing 200 points cannot renew that window.
                if (entry.TongueActivity?.LastWasBoundary == true && !entry.TongueActivity.Quiet)
                    entry.Cadence.ConfirmShortAction(now);
                bool scheduled = entry.Cadence.Due(now, active, nativeState,
                    entry.Activity.LastWasBoundary || entry.TongueActivity?.LastWasBoundary == true || quietBoundary);
                if (entry.ActionState.Reliable && (entry.RavenCapture?.LowRateSafe ?? true) && !scheduled)
                { lowRateSkips++; if (entry.Last != null) frames.Add(entry.Last); continue; }
                lowRateReads++;
                if (entry.Cadence.LastWasBoundary) actionConfirmations++;
                CreatureIdleAnimation? ravenAnimation = null;
                bool ravenReady = false;
                if (entry.RavenCapture != null)
                {
                    long oldMouth = entry.RavenCapture.MouthFallbacks;
                    ravenReady = entry.RavenCapture.TryObserve(now, active, out ravenAnimation, out bool ravenChanged);
                    ravenClockChecks++; ravenMouthFallbacks += entry.RavenCapture.MouthFallbacks - oldMouth;
                    if (ravenReady && !ravenChanged && entry.Last != null && entry.Last.IdleAnimation == ravenAnimation &&
                        !entry.AppearanceDirty && entry.InitialAppearanceObserved && !activeChanged &&
                        RavenPhaseCheckpointCapture.SameRoot(entry.Last.Pose, entry.RavenRoot!()))
                    { ravenGeometrySkips++; frames.Add(entry.Last!); continue; }
                    if (ravenReady && ravenChanged && RavenPhaseCheckpointCapture.TryCorrect(entry.Last, ravenAnimation!, entry.RavenCapture.State,
                        entry.InitialAppearanceObserved, entry.AppearanceDirty, activeChanged, entry.Cadence.LastWasBoundary,
                        entry.RavenRoot!, out var phaseCorrected))
                    {
                        entry.Last = phaseCorrected; ravenPhaseCorrections++; ravenGeometrySkips++;
                        frames.Add(phaseCorrected); continue;
                    }
                    if (ravenReady)
                    {
                        if (!ravenChanged) ravenAnimation = entry.RavenCapture.Reanchor(now);
                    }
                }
                using var timing = ReplayPerformance.Measure(ReplayStage.DynamicCreatures);
                CreatureIdleAnimation? idleAnimation = ravenAnimation;
                if (entry.IdleCapture != null && entry.Activity.IdleCandidate && now >= entry.IdleRetryAfter)
                {
                    bool ready;
                    try { ready = entry.IdleCapture.TryCapture(now, out idleAnimation); }
                    catch { ready = false; }
                    entry.Activity.CompleteIdleVisual(now, ready);
                    entry.IdleRetryAfter = ready ? 0 : now + .25;
                }
                else if (entry.Activity.ClockMode == CreatureReplayRules.IdleClock)
                    entry.Activity.CompleteIdleVisual(now, false);
                // ItemCapture owns the frog's body/rig; this track supplies only
                // its native tongue line, without a duplicate creature mesh.
                var pose = entry.Kind == "frog-tongue" ? FrogPose(entry) : VisualReplica.Capture(entry.Root, true, CreatureReplayRules.MaximumNodes);
                geometryReads++; nodeVisits += pose.Nodes.Length;
                if (entry.QuietCapture != null) quietGeometryReads++;
                if (entry.RavenCapture != null) ravenGeometryReads++;
                if (entry.TongueActivity != null && !entry.TongueActivity.Quiet) frogTongueActiveReads++;
                if (entry.TongueActivity?.LastWasBoundary ?? entry.Activity.LastWasBoundary) boundaryReads++;
                if (entry.Kind == "spider")
                {
                    if (entry.Activity.ClockMode == CreatureReplayRules.IdleClock) spiderIdleReads++;
                    else if (active) spiderActionReads++;
                }
                if (entry.Kind == "beetle" && entry.Activity.ClockMode == CreatureReplayRules.ActiveClock) beetleAwakeReads++;
                var line = entry.Line ? SampleLine(entry, entry.Line!) : null;
                if (line != null) { lineReads++; linePointVisits += line.Points.Length / 3; }
                var visuals = entry.Last?.Visuals;
                bool appearanceReadFailed = false;
                if (entry.Kind != "frog-tongue" && now >= entry.AppearanceRetryAfter)
                    try
                    {
                        visuals = NativeVisualAppearance.CaptureAtTime(entry.Root, visuals, now, activeChanged || entry.AppearanceDirty, CreatureReplayRules.MaximumNodes, allowAnimated: active, animateMainTexture: entry.Kind == "ghost-ball", followSceneClock: true);
                        entry.AppearanceDirty = false;
                        entry.InitialAppearanceObserved = true;
                    }
                    catch (Exception e)
                    {
                        appearanceReadFailed = true;
                        // Appearance metadata must not discard valid observed
                        // bone motion (the native zombie has 295 renderers).
                        Warn(entry.Kind + "-appearance", "生物外观采样暂不可用，继续记录骨骼动作：" + e.Message);
                        entry.AppearanceRetryAfter = now + 1;
                    }
                int state = entry.Spider || entry.Zombie || entry.Condor ? nativeState :
                    ravenReady ? entry.RavenCapture!.State : entry.Kind == "venus-flytrap" || entry.Kind == "antlion" ? nativeState : pose.Active ? 0 : 1;
                // Record the actual observation even if the native pose did
                // not change. It supplies the near-side bracket when motion
                // starts after a quiet spell; geometry arrays remain shared.
                // Sleeping/inactive entries never enter this read path.
                entry.Last = new CreatureReplayFrame { Key = entry.Key, Kind = entry.Kind, Resource = entry.Resource,
                    SourcePath = entry.Path, State = state, Pose = pose, Line = line, Visuals = visuals,
                    SampleTimeKnown = true, SampleTime = now, SampleDiscontinuity = entry.Cadence.LastWasBoundary || quietBoundary,
                    ClockMode = ravenReady ? CreatureReplayRules.IdleClock :
                        entry.QuietCapture?.ClockMode ?? entry.TongueActivity?.ClockMode ?? entry.Activity.ClockMode, IdleAnimation = idleAnimation };
                entry.QuietCapture?.CompleteRead(entry.Last, !appearanceReadFailed && entry.InitialAppearanceObserved && !entry.AppearanceDirty);
                frames.Add(entry.Last!);
            }
            catch (Exception e)
            {
                if (entry.Last != null) frames.Add(entry.Last);
                entry.RetryAfter = now + 1; Warn(entry.Kind, "生物视觉采样暂不可用：" + e.Message);
            }
        }
        foreach (int id in removed) { segments?.Forget(id); Retire(id); }
        bool same = frames.Count == previousDynamic.Length;
        if (same) for (int i = 0; i < frames.Count; i++) if (!ReferenceEquals(frames[i], previousDynamic[i])) { same = false; break; }
        if (same && ReferenceEquals(staticFrames, previousStatic)) return previous;
        if (!same) previousDynamic = frames.ToArray();
        previousStatic = staticFrames;
        previous = new CreatureReplayFrame[staticFrames.Length + previousDynamic.Length];
        Array.Copy(staticFrames, previous, staticFrames.Length);
        Array.Copy(previousDynamic, 0, previous, staticFrames.Length, previousDynamic.Length);
        return previous;
    }
    private static CreatureReplayFrame? SampleStatic(Entry entry, CreatureReplayFrame? previous)
    {
        // Only initial registration and the completed native trigger enqueue a
        // trap. Keep its own armed/consumed state; the world track already owns
        // parent biome activation, including areas reached by a later seek.
        if (!entry.Source) return null;
        using var timing = ReplayPerformance.Measure(ReplayStage.StaticTraps);
        bool active = entry.Source.activeSelf;
        var root = entry.Root;
        var p = root.position; var r = root.rotation; var s = root.lossyScale;
        Span<float> trs = stackalloc float[] { p.x, p.y, p.z, r.x, r.y, r.z, r.w, s.x, s.y, s.z };
        var pose = CreatureDirectoryRules.StaticPose(previous?.Pose, active, trs);
        if (previous != null && ReferenceEquals(pose, previous.Pose)) return previous;
        return new CreatureReplayFrame { Key = entry.Key, Kind = entry.Kind, Resource = entry.Kind,
            SourcePath = entry.Path, State = active ? 0 : 1, Pose = pose };
    }
    private ObjectPose ReadRavenRoot(Entry entry)
    {
        ravenPhaseRootReads++;
        // Read only the root, without entering VisualReplica's hierarchy probe
        // (an invalidated hierarchy is allowed to force children in that API).
        var previous = entry.Last!.Pose;
        entry.Root.GetPositionAndRotation(out var p, out var r); var s = entry.Root.lossyScale;
        bool active = entry.Source.activeInHierarchy;
        if (previous.Active == active && previous.Position[0] == p.x && previous.Position[1] == p.y && previous.Position[2] == p.z &&
            previous.Rotation[0] == r.x && previous.Rotation[1] == r.y && previous.Rotation[2] == r.z && previous.Rotation[3] == r.w &&
            previous.Scale[0] == s.x && previous.Scale[1] == s.y && previous.Scale[2] == s.z) return previous;
        return new ObjectPose(new[] { p.x, p.y, p.z }, new[] { r.x, r.y, r.z, r.w }, new[] { s.x, s.y, s.z }, active, previous.Nodes);
    }
    private static CreatureReplayLine SampleLine(Entry entry, LineRenderer source)
    {
        int count = source.positionCount;
        if (count > entry.LineScratch.Length) throw new InvalidOperationException("生物线条超过控制点录制上限");
        source.GetPositions(entry.LineScratch);
        bool worldSpace = source.useWorldSpace;
        if (entry.Kind == "frog-tongue" && !worldSpace)
        {
            for (int i = 0; i < count; i++) entry.LineScratch[i] = source.transform.TransformPoint(entry.LineScratch[i]);
            worldSpace = true;
        }
        var old = entry.Last?.Line;
        bool enabled = source.enabled && source.gameObject.activeInHierarchy;
        bool same = old != null && old.Enabled == enabled && old.WorldSpace == worldSpace && old.Width == source.widthMultiplier && old.Points.Length == count * 3;
        for (int i = 0; same && i < count; i++)
        { var p = entry.LineScratch[i]; same = old!.Points[i * 3] == p.x && old.Points[i * 3 + 1] == p.y && old.Points[i * 3 + 2] == p.z; }
        if (same) return old!;
        var points = new float[count * 3];
        for (int i = 0; i < count; i++) { var p = entry.LineScratch[i]; points[i * 3] = p.x; points[i * 3 + 1] = p.y; points[i * 3 + 2] = p.z; }
        return new CreatureReplayLine { Enabled = enabled, WorldSpace = worldSpace, Width = source.widthMultiplier, Points = points };
    }
    private static ObjectPose FrogPose(Entry entry)
    {
        // World-space line points need no body hierarchy or placement. Keeping
        // this separate also avoids sharing ItemCapture's animated probe cache.
        Span<float> identity = stackalloc float[] { 0, 0, 0, 0, 0, 0, 1, 1, 1, 1 };
        return CreatureDirectoryRules.StaticPose(entry.Last?.Pose, entry.Source.activeInHierarchy, identity);
    }
    private void Warn(string key, string message) { if (warned.Count < 128 && warned.Add(key)) warning?.Invoke(message); }
    public void Dispose()
    {
        if (disposed) return; disposed = true; observer.Dispose(); segments?.Dispose();
        foreach (var entry in entries.Values) { entry.ScoutmasterActivity?.Dispose(); VisualReplica.ReleaseCapture(entry.Root); }
        entries.Clear(); staticEntries.Clear();
        frames.Clear(); removed.Clear();
        previous = previousStatic = previousDynamic = Array.Empty<CreatureReplayFrame>();
    }
}
