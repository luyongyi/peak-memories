using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using PeakReplayLab;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace LocalRouteAudit;

// Opt-in visual correctness oracle, deliberately excluded from performance runs.
// Uses the production inert clone and animation code; never Animator.Update,
// SampleAnimation on the original, native AI mutations, or synthetic poses.
internal sealed class FaunaVisualAudit : IDisposable
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const int MaximumEntries = 32, MaximumPerKind = 4, SamplesPerState = 24;
    private sealed class Node
    {
        internal Transform Source = null!, Clone = null!;
        internal string Path = "";
    }
    private sealed class NativeEffect
    {
        internal Component Source = null!;
        internal string Kind = "", Path = "", Name = "";
        internal int Id;
        internal bool Seen, LastExists, LastActive, LastEnabled, LastPlaying;
        internal int LastCount;
    }
    private sealed class Skin
    {
        internal SkinnedMeshRenderer Source = null!, Clone = null!;
        internal string Path = "";
        internal int Count;
    }
    private sealed class MaterialValue
    {
        internal int Id;
        internal ShaderPropertyType Kind;
        internal float Number;
        internal int Integer;
        internal Vector4 Vector;
        internal Texture? Texture;
        internal Vector2 TextureScale, TextureOffset;
    }
    private sealed class MaterialState
    {
        internal Material Source = null!;
        internal Shader? Shader;
        internal int Queue;
        internal MaterialValue[] Values = Array.Empty<MaterialValue>();
    }
    private sealed class RendererState
    {
        internal Renderer Source = null!;
        internal Material[] Materials = Array.Empty<Material>();
    }
    private sealed class NativeAppearanceState
    {
        internal RendererState[] Renderers = Array.Empty<RendererState>();
        internal MaterialState[] Materials = Array.Empty<MaterialState>();
    }
    private sealed class Entry
    {
        internal int Id;
        internal string Kind = "", Path = "";
        internal GameObject Source = null!, Root = null!;
        internal object Capture = null!, Visual = null!;
        internal Animator? Animator;
        internal object? Runtime, LastClock;
        internal Node[] Nodes = Array.Empty<Node>();
        internal Renderer[] Renderers = Array.Empty<Renderer>();
        internal Renderer[] NativeRenderers = Array.Empty<Renderer>();
        internal Skin[] Skins = Array.Empty<Skin>();
        internal NativeEffect[] Effects = Array.Empty<NativeEffect>();
        internal NodePose[] FrozenNodes = Array.Empty<NodePose>();
        internal Pose[] FrozenNativeNodes = Array.Empty<Pose>();
        internal float[][] FrozenShapes = Array.Empty<float[]>();
        internal string FrozenClip = "";
        internal double BaselineTime;
        internal readonly Dictionary<string, int> Samples = new(StringComparer.Ordinal);
        internal double NextSample;
        internal int Unsupported;
        internal string LastClip = "";
        internal bool NativeSupported;
        internal int UnsupportedDetails;
        internal bool Ready, Disposed;
    }
    private sealed class Stimulus
    {
        public int RavenId;
        public double RequestedAt, WarpCompletedAt = -1, FlightObservedAt = -1;
        public float[] Position = Array.Empty<float>();
        public float DistanceAfterWarp = -1;
        public bool ReturnedToBaseCamp;
        public int FlightSamples;
        public string Error = "";
    }
    private sealed class WormStimulus
    {
        public int OriginalRootId, OriginalItemId, HeldRootId, HeldItemId;
        public string Stage = "warp-to-worm", Error = "";
        public double RequestedAt, StageSince, WarpCompletedAt = -1, PickupRequestedAt = -1, InHandObservedAt = -1,
            SmokeRequestedAt = -1, FinishCookingRequestedAt = -1, CookedObservedAt = -1;
        public bool ReturnedToBaseCamp, Completed;
        public Item? Original;
    }
    private sealed class SpiderStimulus
    {
        public int RootId;
        public double RequestedAt, WarpCompletedAt = -1;
        public bool ReturnedToBaseCamp;
        public string Error = "";
    }
    private readonly struct Pose
    {
        internal readonly Vector3 Position, Scale;
        internal readonly Quaternion Rotation;
        internal readonly bool Active;
        internal Pose(Transform t, bool world = false)
        { Position = world ? t.position : t.localPosition; Rotation = world ? t.rotation : t.localRotation; Scale = world ? t.lossyScale : t.localScale; Active = world ? t.gameObject.activeInHierarchy : t.gameObject.activeSelf; }
    }
    private readonly string file;
    private readonly Type visualType, ravenCaptureType, ravenRuntimeType, frogCaptureType, frogRuntimeType, wormCaptureType, wormRuntimeType, spiderCaptureType, spiderRuntimeType;
    private readonly MethodInfo beginBuild, continueBuild, capturePose, applyPose, mapTransform, mapRenderer;
    private readonly Dictionary<int, Entry> entries = new();
    private readonly List<object> comparisons = new(), errors = new(), unsupportedDetails = new(), nativeEffects = new();
    private readonly Dictionary<string, int> stateCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> stateFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> motionWitnesses = new(StringComparer.Ordinal);
    private readonly PresentationWorkBudget budget = new();
    private double nextDiscover, nextWrite;
    private int compared, failed, baselines, baselineFailures, sourceMutations, unsupported, mappingFailures, blendShapeComparisons, sourceBlendShapeMutations, sourceMaterialMutations;
    private bool disposed;
    private Stimulus? stimulus;
    private WormStimulus? wormStimulus;
    private SpiderStimulus? spiderStimulus;
    private bool spiderRequired;
    private double maximumRavenClockDrift;
    // The watchdog can cross the 2ms re-anchor threshold before its next audit.
    // Keep the exact-phase geometry oracle separate and impose an explicit
    // 10ms sub-frame budget on the recorded production clock between audits.
    private const double RavenClockDriftBudgetSeconds = .01;

    internal FaunaVisualAudit(string output)
    {
        file = Path.Combine(output, "fauna-visual.json"); var assembly = typeof(Plugin).Assembly;
        visualType = assembly.GetType("PeakReplayLab.VisualReplica", true)!;
        ravenCaptureType = assembly.GetType("PeakReplayLab.RavenAnimationCapture", true)!;
        ravenRuntimeType = assembly.GetType("PeakReplayLab.RavenAnimationRuntime", true)!;
        frogCaptureType = assembly.GetType("PeakReplayLab.FrogIdleAnimationRuntime+Capture", true)!;
        frogRuntimeType = assembly.GetType("PeakReplayLab.FrogIdleAnimationRuntime", true)!;
        wormCaptureType = assembly.GetType("PeakReplayLab.EarlyWormAnimationCapture", true)!;
        wormRuntimeType = assembly.GetType("PeakReplayLab.EarlyWormAnimationRuntime", true)!;
        spiderCaptureType = assembly.GetType("PeakReplayLab.CreatureIdleAnimationRuntime+Capture", true)!;
        spiderRuntimeType = assembly.GetType("PeakReplayLab.CreatureIdleAnimationRuntime", true)!;
        beginBuild = visualType.GetMethod("BeginBuild", Static, null, new[] { typeof(GameObject), typeof(bool), typeof(int) }, null)!;
        continueBuild = visualType.GetMethod("ContinueBuild", Instance)!;
        capturePose = visualType.GetMethod("Capture", Static, null, new[] { typeof(Transform), typeof(bool), typeof(int) }, null)!;
        applyPose = visualType.GetMethod("Apply", Instance)!;
        mapTransform = visualType.GetMethod("TryCloneTransform", Instance, null, new[] { typeof(Transform), typeof(Transform).MakeByRefType() }, null)!;
        mapRenderer = visualType.GetMethod("TryCloneRenderer", Instance, null, new[] { typeof(Renderer), typeof(Renderer).MakeByRefType() }, null)!;
        Flush();
    }

    internal object Summary => new
    {
        Enabled = true, File = "fauna-visual.json", PerformanceMeasurementValid = false,
        Compared = compared, Failed = failed, SourceMutations = sourceMutations, Unsupported = unsupported, MappingFailures = mappingFailures,
        Baselines = baselines, BaselineFailures = baselineFailures, MotionWitnesses = new Dictionary<string, int>(motionWitnesses),
        BlendShapeComparisons = blendShapeComparisons, SourceBlendShapeMutations = sourceBlendShapeMutations, SourceMaterialMutations = sourceMaterialMutations,
        States = new Dictionary<string, int>(stateCounts), ErrorCount = errors.Count,
        FrogIdleVerified = Verified("frog-idle-v1"), RavenIdleVerified = Verified("raven-idle-v1"), RavenFlightVerified = Verified("raven-fly-away-v1"),
        WormWalkVerified = Verified("early-worm-walk-v1"), WormInHandVerified = Verified("early-worm-in-hand-v1"), WormCookedVerified = Verified("early-worm-cooked-v1"),
        SpiderIdleRequired = spiderRequired, SpiderIdleVerified = Verified("spider-idle-v1"),
        MaximumRavenClockDriftSeconds = maximumRavenClockDrift,
        RavenClockDriftBudgetSeconds, RavenClockUsesProductionSchedule = true,
        RavenClockWithinLegacyTwoMillisecondBudget = maximumRavenClockDrift <= .00200001,
        RavenClockDriftWithinBound = maximumRavenClockDrift <= RavenClockDriftBudgetSeconds,
        Passed = Verified("frog-idle-v1") && Verified("raven-idle-v1") && Verified("raven-fly-away-v1") && Verified("early-worm-walk-v1") && Verified("early-worm-in-hand-v1") &&
            Verified("early-worm-cooked-v1") && wormStimulus?.Completed == true && failed == 0 && sourceMutations == 0 &&
            baselineFailures == 0 && sourceBlendShapeMutations == 0 && sourceMaterialMutations == 0 && errors.Count == 0 && mappingFailures == 0 &&
            (!spiderRequired || Verified("spider-idle-v1") && spiderStimulus?.ReturnedToBaseCamp == true && spiderStimulus.Error.Length == 0) && maximumRavenClockDrift <= RavenClockDriftBudgetSeconds,
        CoverageComplete = false,
    };
    private bool Verified(string clip) => stateCounts.TryGetValue(clip, out int count) && count >= 2 && !stateFailures.ContainsKey(clip) &&
        (clip == "early-worm-cooked-v1" || motionWitnesses.ContainsKey(clip));

    internal void Tick(double nativeTime, string phase, NativeSegmentInfo? segment)
    {
        if (disposed) return;
        try
        {
            budget.BeginFrame(Time.frameCount);
            if (stimulus != null && entries.TryGetValue(stimulus.RavenId, out var stimulated)) ObserveNativeEffects(stimulated, nativeTime);
            foreach (var entry in entries.Values)
                if (!entry.Disposed && (!entry.Source || !entry.Source.activeInHierarchy || entry.Source.scene != SceneManager.GetActiveScene())) Retire(entry);
            if (segment != null && segment.RootActive && Time.realtimeSinceStartupAsDouble >= nextDiscover)
            { nextDiscover = Time.realtimeSinceStartupAsDouble + 1; Discover(segment); }
            StimulateSpider(nativeTime, segment);
            if (spiderStimulus == null || spiderStimulus.ReturnedToBaseCamp || segment?.Index != 1) StimulateFlight(nativeTime, phase);
            StimulateWorm(nativeTime, phase, segment);
            foreach (var entry in entries.Values)
            {
                if (entry.Disposed) continue;
                if (!entry.Source) { Retire(entry); continue; }
                if (!entry.Source.activeInHierarchy || entry.Source.scene != SceneManager.GetActiveScene()) continue;
                try
                {
                    if (!entry.Ready)
                    {
                        if (!(bool)continueBuild.Invoke(entry.Visual, new object[] { budget })!) continue;
                        entry.Root = (GameObject)visualType.GetProperty("Root", Instance)!.GetValue(entry.Visual)!;
                        entry.Renderers = entry.Root.GetComponentsInChildren<Renderer>(true);
                        HideRenderers(entry);
                        var nodes = new List<Node>();
                        foreach (var source in entry.Source.GetComponentsInChildren<Transform>(true))
                        {
                            object?[] arguments = { source, null };
                            if (!(bool)mapTransform.Invoke(entry.Visual, arguments)! || arguments[1] is not Transform clone || clone == source)
                            { mappingFailures++; throw new InvalidOperationException("Native source transform has no distinct inert mapping."); }
                            nodes.Add(new Node { Source = source, Clone = clone, Path = RelativePath(source, entry.Source.transform) });
                        }
                        entry.Nodes = nodes.ToArray();
                        entry.NativeRenderers = entry.Source.GetComponentsInChildren<Renderer>(true)
                            .Where(value => value is MeshRenderer || value is SkinnedMeshRenderer).ToArray();
                        var skins = new List<Skin>();
                        foreach (var source in entry.NativeRenderers.OfType<SkinnedMeshRenderer>())
                        {
                            object?[] arguments = { source, null };
                            if (!(bool)mapRenderer.Invoke(entry.Visual, arguments)! || arguments[1] is not SkinnedMeshRenderer clone || clone == source)
                            { mappingFailures++; throw new InvalidOperationException("Native skinned renderer has no distinct inert mapping."); }
                            int count = source.sharedMesh ? source.sharedMesh.blendShapeCount : 0;
                            if (count > NativeVisualAppearanceRules.MaximumBlendShapes ||
                                (clone.sharedMesh ? clone.sharedMesh.blendShapeCount : 0) != count)
                            { mappingFailures++; throw new InvalidOperationException("Native and inert blend-shape directories differ."); }
                            skins.Add(new Skin { Source = source, Clone = clone, Count = count, Path = RelativePath(source.transform, entry.Source.transform) });
                        }
                        entry.Skins = skins.ToArray(); entry.Ready = true;
                        if (entry.Kind == "spider") entry.NextSample = nativeTime + 1;
                    }
                    if (nativeTime < entry.NextSample) continue;
                    entry.NextSample = nativeTime + .125;
                    object? clock = Observe(entry, nativeTime);
                    entry.NativeSupported = clock != null;
                    if (clock == null) { entry.Unsupported++; unsupported++; ObserveUnsupported(entry); continue; }
                    string clip = (string)clock.GetType().GetProperty("Clip")!.GetValue(clock)!;
                    object productionClock = clock;
                    double clockDrift = 0;
                    if (entry.Kind == "raven" && entry.Animator)
                    {
                        var recorded = (CreatureIdleAnimation)clock;
                        var state = entry.Animator!.GetCurrentAnimatorStateInfo(0);
                        double normalized = state.normalizedTime + (clip == "raven-idle-v1" ? entry.Animator.GetFloat(unchecked((int)2516931489u)) : 0);
                        double actual = clip == "raven-idle-v1" ? (normalized - Math.Floor(normalized)) * 3 : Math.Max(0, Math.Min(3, normalized * 3));
                        clockDrift = Math.Abs(CreatureIdleAnimationRules.SampleTime(recorded, nativeTime) - actual);
                        if (clip == "raven-idle-v1") clockDrift = Math.Min(clockDrift, Math.Abs(3 - clockDrift));
                        maximumRavenClockDrift = Math.Max(maximumRavenClockDrift, clockDrift);
                        // Separate float-clock approximation from the native
                        // evaluator oracle. No current child pose is copied.
                        clock = new CreatureIdleAnimation { Clip = recorded.Clip, AnchorTime = nativeTime, ClipTime = actual, Speed = recorded.Speed };
                    }
                    entry.LastClip = clip;
                    if (entry.Samples.TryGetValue(clip, out int sampled) && sampled >= SamplesPerState) continue;
                    var before = entry.Nodes.Select(node => new Pose(node.Source)).ToArray();
                    var rootBefore = new Pose(entry.Source.transform, true);
                    var shapesBefore = entry.Skins.Select(skin => Enumerable.Range(0, skin.Count).Select(skin.Source.GetBlendShapeWeight).ToArray()).ToArray();
                    var appearanceBefore = SnapshotAppearance(entry);
                    bool baseline = entry.FrozenClip != clip;
                    if (baseline)
                    {
                        // Copy native child geometry exactly once per admitted
                        // state. Later samples cannot pass by copying the answer
                        // from the native bones at their current animation phase.
                        var frozen = (ObjectPose)capturePose.Invoke(null, new object[] { entry.Source.transform, true, 1024 })!;
                        entry.FrozenNodes = frozen.Nodes; entry.FrozenNativeNodes = before; entry.FrozenShapes = shapesBefore;
                        entry.FrozenClip = clip; entry.BaselineTime = nativeTime;
                    }
                    EnsureRuntime(entry, clock);
                    var pose = new ObjectPose(
                        new[] { rootBefore.Position.x, rootBefore.Position.y, rootBefore.Position.z },
                        new[] { rootBefore.Rotation.x, rootBefore.Rotation.y, rootBefore.Rotation.z, rootBefore.Rotation.w },
                        new[] { rootBefore.Scale.x, rootBefore.Scale.y, rootBefore.Scale.z }, rootBefore.Active, entry.FrozenNodes);
                    visualType.GetMethod("InvalidatePose", Instance)!.Invoke(entry.Visual, null);
                    applyPose.Invoke(entry.Visual, new[] { pose, null, (object)0f, false });
                    for (int skin = 0; skin < entry.Skins.Length; skin++)
                        for (int index = 0; index < entry.Skins[skin].Count; index++)
                            entry.Skins[skin].Clone.SetBlendShapeWeight(index, entry.FrozenShapes[skin][index]);
                    var runtimeType = entry.Runtime!.GetType();
                    runtimeType.GetMethod("Invalidate", Instance)!.Invoke(entry.Runtime, null);
                    runtimeType.GetMethod("Apply", Instance)!.Invoke(entry.Runtime, new[] { clock, (object)nativeTime });
                    HideRenderers(entry);
                    Compare(entry, before, rootBefore, shapesBefore, appearanceBefore, nativeTime, phase, clip, clock, productionClock, clockDrift, baseline);
                    if (stimulus != null && stimulus.RavenId == entry.Id && clip == "raven-fly-away-v1")
                    { if (stimulus.FlightObservedAt < 0) stimulus.FlightObservedAt = nativeTime; stimulus.FlightSamples++; }
                    entry.Samples[clip] = sampled + (baseline ? 0 : 1);
                }
                catch (Exception error) { Error(entry.Kind, error); Retire(entry); }
            }
            if (Time.realtimeSinceStartupAsDouble >= nextWrite) { nextWrite = Time.realtimeSinceStartupAsDouble + 5; Flush(); }
        }
        catch (Exception error) { Error("oracle", error); }
    }

    private void Discover(NativeSegmentInfo segment)
    {
        if (entries.Count >= MaximumEntries) return;
        var center = Character.localCharacter ? Character.localCharacter.Center : Vector3.zero;
        var ravens = UnityEngine.Object.FindObjectsByType<Peak.Island.VoiceThief>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            // Photon birds are detached scene-root births. Parent-only scope
            // checks would silently exclude every real raven from this oracle.
            .Where(value => value && value.gameObject.scene == SceneManager.GetActiveScene())
            .OrderBy(value => Vector3.SqrMagnitude(value.transform.position - center));
        foreach (var raven in ravens) Add(raven.gameObject, raven, "raven");
        var frogs = UnityEngine.Object.FindObjectsByType<FrogTongue>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .Where(value => value && value.gameObject.scene == SceneManager.GetActiveScene() && value.GetComponent<Item>() && value.GetComponent<Item>().itemID == 172)
            .OrderBy(value => Vector3.SqrMagnitude(value.transform.position - center));
        foreach (var frog in frogs) Add(frog.gameObject, frog, "frog");
        // Independent native item registry, rather than the recorder's admitted
        // directory. Detached Photon item roots still belong to the active scene.
        var worms = Item.ALL_ITEMS.Where(value => value && value.itemID == 184 && value.gameObject.activeInHierarchy &&
                value.gameObject.scene == SceneManager.GetActiveScene())
            .OrderBy(value => Vector3.SqrMagnitude(value.transform.position - center));
        foreach (var worm in worms) Add(worm.gameObject, worm, "worm");
        foreach (var spider in UnityEngine.Object.FindObjectsByType<Spider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .Where(value => value && value.gameObject.scene == SceneManager.GetActiveScene())) Add(spider.gameObject, spider, "spider");
    }
    private void Add(GameObject source, Component component, string kind)
    {
        int id = source.GetInstanceID();
        if (entries.ContainsKey(id) || entries.Count >= MaximumEntries || entries.Values.Count(entry => entry.Kind == kind && !entry.Disposed) >= MaximumPerKind) return;
        try
        {
            var captureType = kind == "raven" ? ravenCaptureType : kind == "worm" ? wormCaptureType : kind == "spider" ? spiderCaptureType : frogCaptureType;
            object[] captureArguments = kind == "spider" ? new object[] { component, CreatureDirectoryRules.IsWebSpiderName(source.name) ? "spider-web" : "spider-normal" } : new object[] { component };
            object capture = Activator.CreateInstance(captureType, Instance, null, captureArguments, null)!;
            object visual = beginBuild.Invoke(null, new object[] { source, true, 1024 })!;
            var effects = kind == "raven" ? source.GetComponentsInChildren<ParticleSystem>(true).Take(8)
                .Select(value => new NativeEffect { Source = value, Id = value.GetInstanceID(), Kind = "particle-system", Name = value.name, Path = NativeRouteActions.PathOf(value.transform) })
                .Concat(source.GetComponentsInChildren<MonoBehaviour>(true).Where(value => value && value.GetType().Name == "SFX_PlayOneShot").Take(8)
                    .Select(value => new NativeEffect { Source = value, Id = value.GetInstanceID(), Kind = "native-sfx-enable", Name = value.name, Path = NativeRouteActions.PathOf(value.transform) })).ToArray() : Array.Empty<NativeEffect>();
            entries.Add(id, new Entry { Id = id, Kind = kind, Path = NativeRouteActions.PathOf(source.transform), Source = source, Capture = capture, Visual = visual, Effects = effects,
                Animator = captureType.GetField("animator", Instance)?.GetValue(capture) as Animator });
        }
        catch (Exception error) { Error(kind + "-discover", error); }
    }
    private object? Observe(Entry entry, double nativeTime)
    {
        if (entry.Kind == "raven")
        {
            // Exercise the production event/watchdog gate as well as its clip
            // evaluator; repeatedly calling TryObserve would hide drift between
            // the new staggered safety audits.
            bool due = (bool)ravenCaptureType.GetMethod("Due", Instance)!.Invoke(entry.Capture, new object[] { nativeTime, true, false })!;
            if (!due) return entry.LastClock;
            object?[] arguments = { nativeTime, true, null, false };
            return entry.LastClock = (bool)ravenCaptureType.GetMethod("TryObserve", Instance)!.Invoke(entry.Capture, arguments)! ? arguments[2] : null;
        }
        if (entry.Kind == "worm")
        {
            object?[] arguments = { nativeTime, true, null, false };
            return (bool)wormCaptureType.GetMethod("TryObserve", Instance)!.Invoke(entry.Capture, arguments)! ? arguments[2] : null;
        }
        object?[] frogArguments = { nativeTime, null };
        var captureType = entry.Kind == "spider" ? spiderCaptureType : frogCaptureType;
        return (bool)captureType.GetMethod("TryCapture", Instance)!.Invoke(entry.Capture, frogArguments)! ? frogArguments[1] : null;
    }
    private void ObserveUnsupported(Entry entry)
    {
        if (entry.UnsupportedDetails >= 2 || unsupportedDetails.Count >= 32) return;
        // A first rejection may be the native entry blend; retain a later
        // rejection as well so a permanent asset mismatch is diagnosable.
        if (entry.UnsupportedDetails == 1 && entry.Unsupported < 24) return;
        entry.UnsupportedDetails++;
        var animator = entry.Animator; var state = animator ? animator!.GetCurrentAnimatorStateInfo(0) : default;
        var controller = animator ? animator!.runtimeAnimatorController : null;
        unsupportedDetails.Add(new
        {
            entry.Kind, entry.Id, NativeTime = Time.timeAsDouble, entry.Unsupported,
            AnimatorExists = (bool)animator, Enabled = animator && animator!.enabled, Active = animator && animator!.gameObject.activeInHierarchy,
            Controller = controller ? controller!.name : "", Layers = animator ? animator!.layerCount : 0,
            UpdateMode = animator ? animator!.updateMode.ToString() : "", CullingMode = animator ? animator!.cullingMode.ToString() : "",
            RootMotion = animator && animator!.applyRootMotion, Human = animator && animator!.isHuman,
            Transition = animator && animator!.IsInTransition(0), state.fullPathHash, state.normalizedTime, state.loop,
            Clips = animator ? animator!.GetCurrentAnimatorClipInfo(0).Select(info => new { Name = info.clip ? info.clip.name : "", info.weight }).ToArray() : null,
            MouthWeight = animator && animator!.layerCount > 1 ? animator.GetLayerWeight(1) : 0,
            Directory = controller ? controller!.animationClips.Select(clip => new { clip.name, clip.length, clip.isLooping, clip.frameRate }).ToArray() : null,
            Worm = entry.Kind == "worm" ? WormUnsupportedState(entry) : null,
        });
    }
    private object WormUnsupportedState(Entry entry)
    {
        var item = entry.Source.GetComponent<Item>();
        var worm = entry.Source.GetComponent<Peak.EarlyWorm>();
        var rotate = worm ? worm!.rotate : null;
        return new
        {
            ItemExists = (bool)item, ItemId = item ? item!.itemID : 0,
            ItemState = item ? item!.itemState.ToString() : "", TimesCookedLocal = item && item!.cooking ? item.cooking.timesCookedLocal : -1,
            WormExists = (bool)worm, RotateExists = (bool)rotate, RotateEnabled = rotate && rotate!.enabled,
            RotateTargetsAnimator = worm && worm!.anim && rotate && rotate!.tf == worm.anim.transform,
            RotateVector = rotate ? new[] { rotate!.rotation.x, rotate.rotation.y, rotate.rotation.z } : null,
            NativeOnGround = worm && worm!.anim && worm.anim.GetBool("OnGround"), NativeCooked = worm && worm!.anim && worm.anim.GetBool("Cooked"),
            SupportedDirectory = wormCaptureType.GetProperty("Supported", Instance)?.GetValue(entry.Capture),
        };
    }
    private void StimulateSpider(double nativeTime, NativeSegmentInfo? segment)
    {
        if (segment?.Index != 1 || segment.Biome != "Roots" || !segment.RootActive || !Character.localCharacter) return;
        spiderRequired = true;
        var local = Character.localCharacter;
        if (spiderStimulus == null)
        {
            if (local.warping || local.data.dead || local.gameOver) return;
            // The native Scan method enables culled spiders based on distance.
            // Approach beside the capture cylinder through the normal warp;
            // never directly enable the source, alter AI or invoke its animator.
            var candidates = segment.Root!.GetComponentsInChildren<Spider>(true).AsEnumerable();
            if (!candidates.Any() && SpiderManager.instance)
                candidates = SpiderManager.instance.spiders.Where(value => value && value.gameObject.scene == SceneManager.GetActiveScene());
            var candidate = candidates
                .Where(value => value && value.startPoint && value.spiderState == Spider.SpiderState.Idle)
                .OrderBy(value => Vector3.SqrMagnitude(value.startPoint.position - local.Center)).FirstOrDefault();
            if (!candidate) return;
            spiderStimulus = new SpiderStimulus { RootId = candidate!.gameObject.GetInstanceID(), RequestedAt = nativeTime };
            try
            {
                float distance = Mathf.Max(10, candidate.castRadius + 4);
                Vector3 origin = candidate.startPoint.position + Vector3.right * distance + Vector3.up * 20;
                if (!Physics.Raycast(origin, Vector3.down, out var hit, 200, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    throw new InvalidOperationException("No native ground beside the spider capture cylinder.");
                NativeRouteActions.TeleportTo(hit.point + Vector3.up * 1.2f);
            }
            catch (Exception error) { spiderStimulus.Error = error.GetType().Name + ": " + error.Message; }
            return;
        }
        var action = spiderStimulus;
        if (action.ReturnedToBaseCamp || action.Error.Length != 0 || local.warping) return;
        if (action.WarpCompletedAt < 0) action.WarpCompletedAt = nativeTime;
        if (nativeTime - action.WarpCompletedAt < 6) return;
        try { NativeRouteActions.TeleportToBaseCamp(); action.ReturnedToBaseCamp = true; }
        catch (Exception error) { action.Error = error.GetType().Name + ": " + error.Message; }
    }
    private void StimulateFlight(double nativeTime, string phase)
    {
        if (phase != "FarIdle" || !Character.localCharacter) return;
        var local = Character.localCharacter;
        if (stimulus == null)
        {
            if (local.warping || local.data.dead || local.gameOver) return;
            var candidate = entries.Values.Where(entry => entry.Kind == "raven" && !entry.Disposed && entry.Source && entry.Source.activeInHierarchy &&
                entry.NativeSupported && entry.LastClip == "raven-idle-v1" && entry.Samples.TryGetValue("raven-idle-v1", out int samples) && samples >= SamplesPerState)
                .OrderBy(entry => Vector3.SqrMagnitude(entry.Source.transform.position - local.Center)).FirstOrDefault();
            if (candidate == null) return;
            Vector3 position = candidate.Source.transform.position + Vector3.up + Vector3.right * 1.5f;
            stimulus = new Stimulus { RavenId = candidate.Id, RequestedAt = nativeTime, Position = new[] { position.x, position.y, position.z } };
            ObserveNativeEffects(candidate, nativeTime);
            try { NativeRouteActions.TeleportTo(position); }
            catch (Exception error) { stimulus.Error = error.GetType().Name + ": " + error.Message; }
            return;
        }
        if (stimulus.Error.Length != 0 || stimulus.ReturnedToBaseCamp || local.warping) return;
        if (stimulus.WarpCompletedAt < 0)
        {
            stimulus.WarpCompletedAt = nativeTime;
            if (entries.TryGetValue(stimulus.RavenId, out var entry) && entry.Source)
                stimulus.DistanceAfterWarp = Vector3.Distance(local.Center, entry.Source.transform.position);
        }
        // Permit normal native AI and the complete observed flight, then return
        // to the same protected base camp to avoid a prolonged fall from a perch.
        if (nativeTime - stimulus.WarpCompletedAt < 3) return;
        try { NativeRouteActions.TeleportToBaseCamp(); stimulus.ReturnedToBaseCamp = true; }
        catch (Exception error) { stimulus.Error = error.GetType().Name + ": " + error.Message; }
    }
    private void ObserveNativeEffects(Entry entry, double nativeTime)
    {
        foreach (var effect in entry.Effects)
        {
            bool exists = effect.Source, active = exists && effect.Source!.gameObject.activeInHierarchy;
            bool enabled = exists && effect.Source is Behaviour behaviour && behaviour.enabled;
            bool playing = false; int count = 0;
            if (exists && effect.Source is ParticleSystem particles) { playing = particles.isPlaying; count = particles.particleCount; }
            if (effect.Seen && effect.LastExists == exists && effect.LastActive == active && effect.LastEnabled == enabled && effect.LastPlaying == playing && effect.LastCount == count) continue;
            if (nativeEffects.Count < 512) nativeEffects.Add(new
            {
                RavenId = entry.Id, effect.Kind, effect.Id, effect.Name, effect.Path, NativeTime = nativeTime, RenderFrame = Time.frameCount,
                Exists = exists, Active = active, Enabled = enabled, IsPlaying = playing, ParticleCount = count,
                BirthObserved = effect.Seen && (playing && !effect.LastPlaying || count > 0 && effect.LastCount == 0 || active && !effect.LastActive),
                EnableObserved = effect.Seen && enabled && !effect.LastEnabled,
            });
            effect.Seen = true; effect.LastExists = exists; effect.LastActive = active; effect.LastEnabled = enabled; effect.LastPlaying = playing; effect.LastCount = count;
        }
    }
    private void StimulateWorm(double nativeTime, string phase, NativeSegmentInfo? segment)
    {
        if (phase != "FarIdle" || segment == null || segment.Index < 3 || !segment.RootActive || !Character.localCharacter) return;
        var local = Character.localCharacter;
        if (wormStimulus == null)
        {
            // The one player cannot perform the bird and item interactions at
            // once. Let the normal raven spook finish and return first.
            if (stimulus == null || !stimulus.ReturnedToBaseCamp || local.warping || local.data.dead || local.gameOver) return;
            var candidate = entries.Values.Where(entry => entry.Kind == "worm" && !entry.Disposed && entry.Source && entry.Source.activeInHierarchy &&
                    entry.Samples.TryGetValue("early-worm-walk-v1", out int count) && count >= SamplesPerState)
                .OrderBy(entry => Vector3.SqrMagnitude(entry.Source.transform.position - local.Center)).FirstOrDefault();
            if (candidate == null) return;
            var item = candidate.Source.GetComponent<Item>();
            if (!item || item.itemState != ItemState.Ground || !item.IsInteractible(local) || !local.player.HasEmptySlot(item.itemID)) return;
            wormStimulus = new WormStimulus
            {
                Original = item, OriginalRootId = candidate.Id, OriginalItemId = item.GetInstanceID(), RequestedAt = nativeTime, StageSince = nativeTime,
            };
            try { NativeRouteActions.TeleportTo(item.transform.position + Vector3.up * 1.2f); }
            catch (Exception error) { wormStimulus.Error = error.GetType().Name + ": " + error.Message; }
            return;
        }
        var action = wormStimulus;
        if (action.Completed || action.Error.Length != 0) return;
        if (nativeTime - action.RequestedAt > 25)
        { action.Error = "Native pickup/cooking did not provide verified InHand and Cooked states within 25 seconds."; return; }
        if (local.warping) return;
        try
        {
            if (action.Stage == "warp-to-worm")
            {
                var item = action.Original;
                if (!item || !item!.gameObject.activeInHierarchy || !item.IsInteractible(local) || !local.player.HasEmptySlot(item.itemID))
                    throw new InvalidOperationException("Native worm pickup no longer meets interactibility and inventory requirements.");
                action.WarpCompletedAt = nativeTime;
                // The real native interaction sends RequestPickup, destroys the
                // ground Photon item and equips the newly spawned held entity.
                item.Interact(local);
                action.PickupRequestedAt = nativeTime; action.StageSince = nativeTime; action.Stage = "observe-in-hand";
                return;
            }
            var held = local.data.currentItem;
            if (!held || held.itemID != 184 || held.itemState != ItemState.Held) return;
            action.HeldRootId = held.gameObject.GetInstanceID(); action.HeldItemId = held.GetInstanceID();
            Add(held.gameObject, held, "worm");
            if (!entries.TryGetValue(action.HeldRootId, out var entry) || entry.Disposed) return;
            if (action.Stage == "observe-in-hand")
            {
                if (!entry.Samples.TryGetValue("early-worm-in-hand-v1", out int count) || count < SamplesPerState || !Verified("early-worm-in-hand-v1")) return;
                action.InHandObservedAt = nativeTime;
                NativeRouteActions.TeleportToBaseCamp(); action.ReturnedToBaseCamp = true;
                action.StageSince = nativeTime; action.Stage = "start-smoking"; return;
            }
            if (!held.cooking || !held.cooking.canBeCooked) throw new InvalidOperationException("Held native worm does not expose its normal cooking API.");
            if (action.Stage == "start-smoking")
            {
                held.cooking.StartCookingVisuals(); action.SmokeRequestedAt = nativeTime;
                action.StageSince = nativeTime; action.Stage = "finish-cooking"; return;
            }
            if (action.Stage == "finish-cooking")
            {
                if (nativeTime - action.StageSince < 1) return;
                // Same API and Photon RPC used by the native cooking flow; no
                // direct item-state, animator, cooked-data or GO activation edits.
                held.cooking.FinishCooking(); action.FinishCookingRequestedAt = nativeTime;
                action.StageSince = nativeTime; action.Stage = "observe-cooked"; return;
            }
            if (action.Stage == "observe-cooked" && held.cooking.timesCookedLocal > 0 &&
                entry.Samples.TryGetValue("early-worm-cooked-v1", out int cookedCount) && cookedCount >= SamplesPerState && Verified("early-worm-cooked-v1"))
            { action.CookedObservedAt = nativeTime; action.Completed = true; action.Stage = "complete"; }
        }
        catch (Exception error) { action.Error = error.GetType().Name + ": " + error.Message; }
    }
    private void EnsureRuntime(Entry entry, object clock)
    {
        if (entry.Runtime != null) return;
        var type = entry.Kind == "raven" ? ravenRuntimeType : entry.Kind == "worm" ? wormRuntimeType : entry.Kind == "spider" ? spiderRuntimeType : frogRuntimeType;
        object?[] arguments = entry.Kind == "raven" || entry.Kind == "spider" ? new[] { entry.Visual, (object)entry.Source, clock, null } :
            new[] { entry.Visual, (object)entry.Source.GetComponent<Item>(), null };
        if (!(bool)type.GetMethod("TryCreate", Static)!.Invoke(null, arguments)!) throw new InvalidOperationException("Production native animation runtime rejected its admitted clip.");
        entry.Runtime = arguments[arguments.Length - 1];
    }
    private void Compare(Entry entry, Pose[] before, Pose rootBefore, float[][] shapesBefore, NativeAppearanceState appearanceBefore,
        double nativeTime, string phase, string clip, object clock, object productionClock, double clockDrift, bool baseline)
    {
        double maximumPosition = 0, maximumAngle = 0, maximumScale = 0;
        string worstPosition = "", worstAngle = "", worstScale = "";
        int activeDifferences = 0, mutated = 0, nativeNodesChanged = 0;
        for (int i = 0; i < entry.Nodes.Length; i++)
        {
            var node = entry.Nodes[i]; var after = new Pose(node.Source);
            if (!Same(before[i], after)) mutated++;
            if (node.Source == entry.Source.transform) continue;
            if (!Same(before[i], entry.FrozenNativeNodes[i])) nativeNodesChanged++;
            var actual = new Pose(node.Clone);
            double position = Vector3.Distance(before[i].Position, actual.Position), angle = Quaternion.Angle(before[i].Rotation, actual.Rotation), scale = Vector3.Distance(before[i].Scale, actual.Scale);
            if (position > maximumPosition) { maximumPosition = position; worstPosition = node.Path; }
            if (angle > maximumAngle) { maximumAngle = angle; worstAngle = node.Path; }
            if (scale > maximumScale) { maximumScale = scale; worstScale = node.Path; }
            if (before[i].Active != actual.Active) activeDifferences++;
        }
        double maximumBlendError = 0, maximumNativeBlendChange = 0;
        string worstBlendPath = "";
        int worstBlendIndex = -1, shapeCount = 0, shapeMutations = 0;
        var shapeDetails = new List<object>();
        for (int skinIndex = 0; skinIndex < entry.Skins.Length; skinIndex++)
        {
            var skin = entry.Skins[skinIndex];
            for (int index = 0; index < skin.Count; index++)
            {
                float expected = shapesBefore[skinIndex][index], nativeAfter = skin.Source.GetBlendShapeWeight(index), actual = skin.Clone.GetBlendShapeWeight(index);
                if (!expected.Equals(nativeAfter)) shapeMutations++;
                double error = Math.Abs(expected - actual);
                if (double.IsNaN(error) || double.IsInfinity(error)) error = double.PositiveInfinity;
                if (error > maximumBlendError) { maximumBlendError = error; worstBlendPath = skin.Path; worstBlendIndex = index; }
                maximumNativeBlendChange = Math.Max(maximumNativeBlendChange, Math.Abs(expected - entry.FrozenShapes[skinIndex][index]));
                if (entry.Kind == "worm") shapeDetails.Add(new
                {
                    skin.Path, Index = index, Native = expected, Clone = actual, Frozen = entry.FrozenShapes[skinIndex][index],
                    NativeMesh = skin.Source.sharedMesh ? skin.Source.sharedMesh.name : "", CloneMesh = skin.Clone.sharedMesh ? skin.Clone.sharedMesh.name : "",
                    SharedMeshMatches = skin.Source.sharedMesh == skin.Clone.sharedMesh,
                    Shape = skin.Source.sharedMesh ? skin.Source.sharedMesh.GetBlendShapeName(index) : "",
                });
                shapeCount++;
            }
        }
        int materialMutations = AppearanceMutations(appearanceBefore);
        blendShapeComparisons += shapeCount; sourceBlendShapeMutations += shapeMutations; sourceMaterialMutations += materialMutations;
        if (!Same(rootBefore, new Pose(entry.Source.transform, true))) mutated++;
        var rootActual = new Pose(entry.Root.transform, true);
        double rootPosition = Vector3.Distance(rootBefore.Position, rootActual.Position), rootAngle = Quaternion.Angle(rootBefore.Rotation, rootActual.Rotation), rootScale = Vector3.Distance(rootBefore.Scale, rootActual.Scale);
        bool pass = maximumPosition <= .0005 && maximumAngle <= .15 && maximumScale <= .0002 && activeDifferences == 0 &&
            rootPosition <= .0005 && rootAngle <= .15 && rootScale <= .0002 && rootBefore.Active == rootActual.Active && mutated == 0 &&
            maximumBlendError <= .01 && shapeMutations == 0 && materialMutations == 0;
        if (baseline) { baselines++; if (!pass) baselineFailures++; }
        else
        {
            compared++; if (!pass) failed++;
            stateCounts[clip] = stateCounts.TryGetValue(clip, out int count) ? count + 1 : 1;
            if (nativeNodesChanged != 0 || maximumNativeBlendChange > .01)
                motionWitnesses[clip] = motionWitnesses.TryGetValue(clip, out int motionCount) ? motionCount + 1 : 1;
        }
        sourceMutations += mutated;
        if (!pass) stateFailures[clip] = stateFailures.TryGetValue(clip, out int badCount) ? badCount + 1 : 1;
        var nativeState = entry.Animator ? entry.Animator!.GetCurrentAnimatorStateInfo(0) : default;
        comparisons.Add(new
        {
            entry.Kind, entry.Id, entry.Path, NativeTime = nativeTime, RenderFrame = Time.frameCount, Phase = phase, Clip = clip,
            Clock = productionClock, EvaluationClock = clock, ProductionClockDriftSeconds = clockDrift,
            Baseline = baseline, FrozenBaselineTime = entry.BaselineTime, NativeNodesChangedFromBaseline = nativeNodesChanged,
            MaximumNativeBlendShapeChangeFromBaseline = maximumNativeBlendChange,
            NodeCount = entry.Nodes.Length, Passed = pass, MaximumLocalPositionErrorMetres = maximumPosition, WorstPositionPath = worstPosition,
            MaximumLocalAngleErrorDegrees = maximumAngle, WorstAnglePath = worstAngle, MaximumLocalScaleError = maximumScale, WorstScalePath = worstScale,
            ActiveDifferences = activeDifferences, RootWorldPositionErrorMetres = rootPosition, RootWorldAngleErrorDegrees = rootAngle, RootWorldScaleError = rootScale,
            RootActiveMatches = rootBefore.Active == rootActual.Active, SourceMutations = mutated,
            BlendShapeCount = shapeCount, MaximumBlendShapeWeightError = maximumBlendError, WorstBlendShapePath = worstBlendPath, WorstBlendShapeIndex = worstBlendIndex,
            SourceBlendShapeMutations = shapeMutations, SourceMaterialMutations = materialMutations,
            BlendShapes = shapeDetails,
            RootNativePosition = new[] { rootBefore.Position.x, rootBefore.Position.y, rootBefore.Position.z },
            RootClonePosition = new[] { rootActual.Position.x, rootActual.Position.y, rootActual.Position.z },
            NativeAnimator = entry.Animator ? new { nativeState.fullPathHash, nativeState.normalizedTime, nativeState.speed, nativeState.speedMultiplier,
                GlobalSpeed = entry.Animator!.speed, CycleOffset = entry.Kind == "raven" ? entry.Animator.GetFloat(unchecked((int)2516931489u)) :
                    entry.Kind == "frog" ? entry.Animator.GetFloat(822357063) : 0,
                Avatar = entry.Animator.avatar ? entry.Animator.avatar.name : "", AvatarValid = entry.Animator.avatar && entry.Animator.avatar.isValid,
                Human = entry.Animator.isHuman } : null,
        });
    }
    private static NativeAppearanceState SnapshotAppearance(Entry entry)
    {
        var renderers = new List<RendererState>();
        var materials = new List<MaterialState>();
        var seen = new HashSet<Material>();
        foreach (var renderer in entry.NativeRenderers)
        {
            if (!renderer) continue;
            var shared = renderer.sharedMaterials;
            renderers.Add(new RendererState { Source = renderer, Materials = shared });
            foreach (var material in shared)
            {
                if (!material || !seen.Add(material)) continue;
                var shader = material.shader;
                var values = new List<MaterialValue>();
                if (shader)
                    for (int index = 0; index < shader.GetPropertyCount(); index++)
                    {
                        int id = shader.GetPropertyNameId(index);
                        var value = new MaterialValue { Id = id, Kind = shader.GetPropertyType(index) };
                        switch (value.Kind)
                        {
                            case ShaderPropertyType.Color: value.Vector = material.GetColor(id); break;
                            case ShaderPropertyType.Vector: value.Vector = material.GetVector(id); break;
                            case ShaderPropertyType.Int: value.Integer = material.GetInteger(id); break;
                            case ShaderPropertyType.Texture:
                                value.Texture = material.GetTexture(id); value.TextureScale = material.GetTextureScale(id); value.TextureOffset = material.GetTextureOffset(id); break;
                            default: value.Number = material.GetFloat(id); break;
                        }
                        values.Add(value);
                    }
                materials.Add(new MaterialState { Source = material, Shader = shader, Queue = material.renderQueue, Values = values.ToArray() });
            }
        }
        return new NativeAppearanceState { Renderers = renderers.ToArray(), Materials = materials.ToArray() };
    }
    private static int AppearanceMutations(NativeAppearanceState before)
    {
        int changed = 0;
        foreach (var renderer in before.Renderers)
        {
            if (!renderer.Source) { changed++; continue; }
            var after = renderer.Source.sharedMaterials;
            if (after.Length != renderer.Materials.Length) { changed++; continue; }
            for (int slot = 0; slot < after.Length; slot++) if (after[slot] != renderer.Materials[slot]) changed++;
        }
        foreach (var snapshot in before.Materials)
        {
            var material = snapshot.Source;
            if (!material || material.shader != snapshot.Shader || material.renderQueue != snapshot.Queue)
            { changed++; continue; }
            foreach (var value in snapshot.Values)
            {
                bool same;
                switch (value.Kind)
                {
                    case ShaderPropertyType.Color: same = ((Vector4)material.GetColor(value.Id)).Equals(value.Vector); break;
                    case ShaderPropertyType.Vector: same = material.GetVector(value.Id).Equals(value.Vector); break;
                    case ShaderPropertyType.Int: same = material.GetInteger(value.Id) == value.Integer; break;
                    case ShaderPropertyType.Texture:
                        same = material.GetTexture(value.Id) == value.Texture && material.GetTextureScale(value.Id).Equals(value.TextureScale) && material.GetTextureOffset(value.Id).Equals(value.TextureOffset); break;
                    default: same = material.GetFloat(value.Id).Equals(value.Number); break;
                }
                if (!same) changed++;
            }
        }
        return changed;
    }
    private static bool Same(Pose a, Pose b) => a.Position.Equals(b.Position) && a.Rotation.Equals(b.Rotation) && a.Scale.Equals(b.Scale) && a.Active == b.Active;
    private static void HideRenderers(Entry entry) { foreach (var renderer in entry.Renderers) if (renderer) renderer.forceRenderingOff = true; }
    private static string RelativePath(Transform source, Transform root)
    { if (source == root) return "."; var parts = new List<string>(); while (source && source != root) { parts.Add(source.name); source = source.parent; } parts.Reverse(); return string.Join("/", parts); }
    private void Error(string kind, Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null) error = invocation.InnerException;
        if (errors.Count < 64) errors.Add(new { Kind = kind, Type = error.GetType().Name, error.Message });
    }
    private void Retire(Entry entry)
    {
        if (entry.Disposed) return; entry.Disposed = true;
        (entry.Runtime as IDisposable)?.Dispose(); (entry.Visual as IDisposable)?.Dispose();
    }
    internal void Flush()
    {
        File.WriteAllText(file, JsonConvert.SerializeObject(new
        {
            Summary, Tolerances = new { PositionMetres = .0005, AngleDegrees = .15, Scale = .0002, BlendShapeWeight = .01 }, Comparisons = comparisons, Errors = errors,
            UnsupportedDetails = unsupportedDetails, FlightStimulus = stimulus,
            SpiderIdleStimulus = spiderStimulus,
            WormInteractionStimulus = wormStimulus == null ? null : new
            {
                wormStimulus.OriginalRootId, wormStimulus.OriginalItemId, wormStimulus.HeldRootId, wormStimulus.HeldItemId,
                wormStimulus.Stage, wormStimulus.Error, wormStimulus.RequestedAt, wormStimulus.WarpCompletedAt, wormStimulus.PickupRequestedAt,
                wormStimulus.InHandObservedAt, wormStimulus.SmokeRequestedAt, wormStimulus.FinishCookingRequestedAt, wormStimulus.CookedObservedAt,
                wormStimulus.ReturnedToBaseCamp, wormStimulus.Completed,
                Method = "Protected offline native WarpPlayerRPC, Item.Interact -> RequestPickup/OnPickupAccepted/EquipSlot, ItemCooking.StartCookingVisuals and FinishCooking native Photon RPC. No direct state, animation, cooking-data or GameObject activation writes. These deliberate native stimuli are separate from source-immutability checks around replay-only evaluation.",
            },
            NativeFlightEffects = nativeEffects,
            Entities = entries.Values.Select(entry => new { entry.Kind, entry.Id, entry.Path, entry.Ready, entry.Disposed, entry.Unsupported, States = entry.Samples }).ToArray(),
            Method = "Opt-in production inert VisualReplica and production clip runtime versus native local transforms, root world pose and every mapped skinned renderer blend-shape weight at the same LateUpdate Time.timeAsDouble, after native Rotate Update and Animator evaluation. Child poses and blend-shape weights are frozen once per state; each later comparison resets the clone to that frozen checkpoint, supplies only the current world root, and evaluates the production runtime at current native time. The baseline is reported separately and never counted as animation verification; verification requires at least two later samples and observed native child/shape change for animated clips. Native transforms, blend shapes, renderer material references and all declared material shader values are copied before clone writes and verified unchanged immediately afterwards. No native source animation sampling or AI state forcing. Performance numbers from this run are invalid. Only observed zero-mouth-layer raven idle/fly, admitted FrogIdle and admitted EarlyWorm states are covered; unsupported states and transitions are counted, never called passed. No unobserved InHand/Cooked state is inferred from Walk success.",
        }, Formatting.Indented));
    }
    public void Dispose()
    { if (disposed) return; disposed = true; foreach (var entry in entries.Values) Retire(entry); Flush(); }
}
