using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using PeakReplayLab;
using Photon.Pun;
using UnityEngine;
using Zorro.Core;

namespace LocalRouteAudit;

// Deliberate, opt-in native stimuli. No recorder callback, scale, item data,
// collider, FakeItem active flag, physics material or Animator is modified.
internal sealed class SnowReplayAudit
{
    private enum Step { Unstarted, WarpFake, FakeAcceptance, InitialHeld, GroundBirth, FirstPush, Quiet, SecondPush, WarpPickup, PickupAcceptance, FinalHeld, Done, Failed }
    private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly MethodInfo ItemKey = typeof(Plugin).Assembly.GetType("PeakReplayLab.ItemCapture", true)!
        .GetMethod("Key", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
    private static readonly FieldInfo LiveItemCapture = typeof(Plugin).Assembly.GetType("PeakReplayLab.ItemCapture", true)!
        .GetField("liveCapture", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly string file;
    private readonly List<object> events = new(), samples = new();
    private readonly List<object> playbackChecks = new();
    private readonly List<object> pushStimuli = new();
    private readonly Dictionary<string, (string Mesh, string Collider)> recordedPaths = new(StringComparer.Ordinal);
    private bool replayVisibleFake, replayHiddenFake, replayBallShape;
    private readonly List<double> interestingTimes = new();
    private Step step;
    private FakeItem? fake;
    private Item? source;
    private ItemScaleSyncer? syncer;
    private Snowball? ball;
    private Transform? mesh, collider;
    private string fakePath = "", key = "", meshPath = "", colliderPath = "", error = "", material = "";
    private int fakeIndex, nativeSegmentIndex = -1, originalHeldId, groundId, finalHeldId, groundMatches, firstGrowthMatches, secondGrowthMatches, idleFrames;
    private ushort itemId;
    private Vector3 floor, direction, normal;
    private Vector3 quietStand;
    private double started, entered, nextForce, lastFrame = double.NaN, lastRecordedScale = double.NaN, quietSince = double.NaN, quietSample = double.NaN;
    private float initialGroundScale, beforeSecondScale, beforePickupScale;
    private float nativeMinimumSpeedToGrow;
    private int firstPushCount, secondPushCount;
    private double nextCaptureDiagnostic;
    private object? lastCaptureDiagnostic;
    private bool fakeBefore, fakeAfter, birthRecorded, firstGrowth, secondGrowth, quietVerified, heldMatched;
    private bool quietNativeUnsettled, quietAnchorKnown;
    private int quietMovingWitnesses;
    private Vector3 quietAnchor;
    private Quaternion quietRotation;
    private float quietScale;
    private long quietRevision, quietBoundary;
    private ItemFrame? latestItem;
    internal SnowReplayAudit(string output) => file = Path.Combine(output, "snow-visual.json");
    internal bool Started => step != Step.Unstarted;
    internal bool Done => step is Step.Done or Step.Failed;
    internal bool Success => step == Step.Done && fakeBefore && fakeAfter && birthRecorded && firstGrowth && secondGrowth && (quietVerified || quietNativeUnsettled) && heldMatched;
    internal bool ReplayVerified => replayVisibleFake && replayHiddenFake && replayBallShape;
    internal IEnumerable<double> InterestingTimes => interestingTimes;
    internal object Summary => new
    {
        Requested = true, Started, Completed = Done, Success, Stage = step.ToString(), Error = error,
        FakePath = fakePath, FakeIndex = fakeIndex, NativeSegmentIndex = nativeSegmentIndex, ItemId = itemId, OriginalHeldId = originalHeldId,
        GroundId = groundId, FinalHeldId = finalHeldId, FakeVisibleBefore = fakeBefore, FakeHiddenAfter = fakeAfter,
        NativeAcceptedBirthRecorded = birthRecorded, FirstGroundGrowth = firstGrowth, SecondGroundGrowth = secondGrowth,
        QuietObservationStopped = quietVerified, HeldShapeMatched = heldMatched, FirstGrowthMatches = firstGrowthMatches,
        TrueStaticSleepCovered = quietVerified, QuietNativeUnsettled = quietNativeUnsettled, QuietMovingWitnesses = quietMovingWitnesses,
        QuietScenario = quietVerified ? "NativeTrueRest" : quietNativeUnsettled ? "NativeStillMoving_NoStaticSleepClaim" : "Unverified",
        SecondGrowthMatches = secondGrowthMatches, GroundShapeMatches = groundMatches, QuietAcceptedFrames = idleFrames,
        NativeMinimumSpeedToGrow = nativeMinimumSpeedToGrow, FirstPushCount = firstPushCount, SecondPushCount = secondPushCount,
        MaximumHorizontalStimulusSpeed = 1.3f, MaximumStimuliPerPeriod = 4,
        QuietPlayerStand = V(quietStand),
        NativeSnowMaterial = material, SnowFloor = V(floor), PushDirection = V(direction), ComparisonTolerance = .0005f,
        ReplayVerified, ReplayVisibleFake = replayVisibleFake, ReplayHiddenFake = replayHiddenFake, ReplayBallShape = replayBallShape, ReplayVisualChecks = playbackChecks.Count,
        PerformanceMeasurementValid = false,
        Limitation = "Only this exercised native Snowball and FakeItem are coverage. A still-moving native ice ball is required to retain motion capture and does not establish true-static sleep coverage. TrueStaticSleepCovered reports that separate condition explicitly. Accepted-frame data are compared to native applied mesh/collider scale; offline reverse-page verification is separate. No screenshot, CPU, every-item or all-ground claim.",
    };

    internal void Start(double now, NativeSegmentInfo segment, ReplayFrame? frame)
    {
        if (Started) return;
        started = now;
        try
        {
            GuardLocal();
            Require(string.Equals(segment.Biome, "Alpine", StringComparison.OrdinalIgnoreCase),
                "Snow audit requires the genuine native Alpine biome.");
            nativeSegmentIndex = segment.Index;
            var candidates = NativeRouteActions.FindCurrentCandidates<FakeItem>(segment,
                x => !x.pickedUp && x.realItemPrefab && x.realItemPrefab.GetComponent<Snowball>()).OrderBy(x => Vector3.Distance(Character.localCharacter.Center, x.Center()));
            foreach (var candidate in candidates)
            {
                if (!FakeItemManager.Instance.TryGetFakeItem(candidate.index, out var managed) || managed != candidate ||
                    !Character.localCharacter.player.HasEmptySlot(candidate.realItemPrefab.itemID)) continue;
                if (!TrySnowLane(candidate.Center(), out floor, out direction, out normal, out material)) continue;
                fake = candidate; break;
            }
            Require(fake, "No active native Snowball FakeItem with accepted manager identity and a bounded flat snow/ice lane was found.");
            fakePath = NativeRouteActions.PathOf(fake!.transform); fakeIndex = fake.index; itemId = fake.realItemPrefab.itemID;
            fakeBefore = FindFake(frame)?.Pose.Active == true;
            Require(fakeBefore, "The real FakeItem lacks its visible recorded initial baseline.");
            Mark("fake-visible-baseline", now, frame?.T ?? now);
            NativeRouteActions.TeleportTo(floor + Vector3.up * 1.2f);
            Enter(Step.WarpFake, now);
        }
        catch (Exception e) { Fail(e, now); }
    }

    internal void Tick(double now, ReplayFrame? frame)
    {
        if (!Started || Done) return;
        try
        {
            Require(now - started <= 110, "Snow audit exceeded its 110-second bound.");
            var local = GuardLocal();
            switch (step)
            {
                case Step.WarpFake:
                    if (local.warping || now - entered < 1) { Limit(now, 15); break; }
                    Require(fake && fake!.gameObject.activeInHierarchy && fake.IsInteractible(local) && Vector3.Distance(local.Center, fake.Center()) < 5,
                        "Native snow pile is not safely interactible from the verified nearby floor.");
                    fake!.Interact(local); Mark("fake-native-interact", now, frame?.T ?? now); Enter(Step.FakeAcceptance, now); break;
                case Step.FakeAcceptance:
                    var held = local.data.currentItem;
                    if (!fake || !fake!.pickedUp || fake.gameObject.activeInHierarchy || !held || held.itemID != itemId || held.itemState != ItemState.Held)
                    { Limit(now, 12); break; }
                    fakeAfter = FindFake(frame)?.Pose.Active == false;
                    if (!fakeAfter) { Limit(now, 12); break; }
                    Bind(held); originalHeldId = held.GetInstanceID(); Mark("fake-hidden-real-held", now, frame?.T ?? now); Enter(Step.InitialHeld, now); break;
                case Step.InitialHeld:
                    if (!ShapeMatches(latestItem)) { Limit(now, 8); break; }
                    birthRecorded = true;
                    Require(local.refs.items.currentSelectedSlot.IsSome, "Native accepted ball has no equipped slot.");
                    byte slotId = local.refs.items.currentSelectedSlot.Value;
                    var slot = local.player.GetItemSlot(slotId);
                    Require(slot != null, "Native drop slot is absent.");
                    float height = source!.colliders.Where(x => x && x.enabled).Select(x => x.bounds.extents.y).DefaultIfEmpty(.25f).Max();
                    Require(height > 0 && height < 1.2f, "Unexpected native starting Snowball collider size.");
                    local.refs.items.photonView.RPC("DropItemRpc", RpcTarget.All, 0f, slotId,
                        floor + Vector3.up * (height + .12f), Vector3.zero, source.transform.rotation, slot!.data, false);
                    local.refs.items.EquipSlot(Optionable<byte>.None);
                    Mark("native-drop", now, frame?.T ?? now); source = null; latestItem = null; Enter(Step.GroundBirth, now); break;
                case Step.GroundBirth:
                    if (!source)
                    {
                        var ground = UnityEngine.Object.FindObjectsByType<Snowball>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                            .Select(x => x.GetComponent<Item>()).Where(x => x && x.itemID == itemId && x.itemState == ItemState.Ground &&
                                x.GetInstanceID() != originalHeldId && x.gameObject.scene == local.gameObject.scene && Vector3.Distance(x.transform.position, floor) < 4)
                            .OrderBy(x => Vector3.Distance(x.transform.position, floor)).FirstOrDefault();
                        if (!ground) { Limit(now, 10); break; }
                        Bind(ground!); groundId = ground!.GetInstanceID(); initialGroundScale = mesh!.localScale.x;
                    }
                    if (now - entered < 1 || !ShapeMatches(latestItem)) { Limit(now, 10); break; }
                    Require(source!.rig && !source.rig.isKinematic, "Native dropped Snowball is not a dynamic rigidbody.");
                    Mark("ground-before-push", now, frame?.T ?? now); Enter(Step.FirstPush, now); Push(now); break;
                case Step.FirstPush:
                    Push(now);
                    if (firstGrowthMatches >= 2 && mesh!.localScale.x > initialGroundScale + .003f)
                    {
                        firstGrowth = true; Mark("ground-grown-before-pickup", now, frame?.T ?? now);
                        Require(TryQuietStand(source!.transform.position, out quietStand), "No verified safe snow floor at least four metres away for native quiet observation.");
                        NativeRouteActions.TeleportTo(quietStand);
                        Mark("leave-ball-for-native-quiet", now, frame?.T ?? now);
                        Enter(Step.Quiet, now); break;
                    }
                    Limit(now, 12); break;
                case Step.Quiet:
                    Require(source && source!.itemState == ItemState.Ground, "Ball disappeared or was held before ground quiet evidence.");
                    var f = latestItem;
                    bool quiet = f != null && f.SampleTimeKnown && f.ClockMode == CreatureReplayRules.SleepingClock &&
                        source!.rig.linearVelocity.magnitude < .04f && ShapeMatches(f);
                    if (!quiet) { quietSince = double.NaN; idleFrames = 0; }
                    else if (double.IsNaN(quietSince) || f!.SampleTime != quietSample)
                    { quietSince = now; quietSample = f!.SampleTime; idleFrames = 0; }
                    if (quiet && now - quietSince >= 3 && idleFrames >= 30)
                    {
                        quietVerified = true; beforeSecondScale = mesh!.localScale.x;
                        Mark("ground-quiet-observation-held", now, frame?.T ?? now); Enter(Step.SecondPush, now); Push(now); break;
                    }
                    if (now - entered >= 10 && quietMovingWitnesses >= 6 && ShapeMatches(f))
                    {
                        quietNativeUnsettled = true; beforeSecondScale = mesh!.localScale.x;
                        quietSample = f!.SampleTime;
                        Mark("native-still-moving-no-static-sleep-claim", now, frame?.T ?? now);
                        Enter(Step.SecondPush, now); Push(now); break;
                    }
                    Limit(now, 35); break;
                case Step.SecondPush:
                    Push(now);
                    if (secondGrowthMatches >= 2 && mesh!.localScale.x > beforeSecondScale + .003f && latestItem!.SampleTime > quietSample)
                    {
                        secondGrowth = true; beforePickupScale = mesh.localScale.x; Mark("ground-repush-woke-growth", now, frame?.T ?? now);
                        Require(TrySnowFloor(source!.transform.position, out var pickupFloor), "No safe snow floor beside naturally moved ball.");
                        NativeRouteActions.TeleportTo(pickupFloor.point + Vector3.up * 1.2f);
                        Enter(Step.WarpPickup, now); break;
                    }
                    Limit(now, 12); break;
                case Step.WarpPickup:
                    if (local.warping || now - entered < 1) { Limit(now, 12); break; }
                    Require(source && source!.itemState == ItemState.Ground && source.IsInteractible(local) &&
                        local.player.HasEmptySlot(itemId) && Vector3.Distance(local.Center, source.Center()) < 5,
                        "Naturally grown ball cannot be picked up through native interaction.");
                    beforePickupScale = mesh!.localScale.x;
                    source!.Interact(local); Mark("grown-native-pickup", now, frame?.T ?? now); Enter(Step.PickupAcceptance, now); break;
                case Step.PickupAcceptance:
                    var picked = local.data.currentItem;
                    if (!picked || picked.itemID != itemId || picked.itemState != ItemState.Held || picked.GetInstanceID() == groundId)
                    { Limit(now, 12); break; }
                    Bind(picked); finalHeldId = picked.GetInstanceID(); Enter(Step.FinalHeld, now); break;
                case Step.FinalHeld:
                    if (!ShapeMatches(latestItem)) { Limit(now, 10); break; }
                    // Native RPC_SyncScale persists its prior scalar before
                    // assigning the incoming value, so legitimate pickup can
                    // carry one .01 packet of native lag. Do not permit a reset
                    // to the original, ungrown shape to pass this assertion.
                    Require(Math.Abs(mesh!.localScale.x - beforePickupScale) <= .025f,
                        "Native pickup failed to preserve the grown size within its scalar RPC tolerance.");
                    heldMatched = true; Mark("grown-held-shape-matched", now, frame?.T ?? now); Enter(Step.Done, now); Flush(); break;
            }
        }
        catch (Exception e) { Fail(e, now); }
    }

    // Called synchronously by the real recorder's accepted-frame postfix. This
    // observes what the recorder wrote, rather than fabricating a recorder frame.
    internal void ObserveAccepted(ReplayFrame frame)
    {
        if (!Started || Done || frame.T == lastFrame) return;
        lastFrame = frame.T;
        try
        {
            var fakeFrame = FindFake(frame);
            if (fake && fake!.pickedUp && !fake.gameObject.activeInHierarchy && fakeFrame?.Pose.Active == false) fakeAfter = true;
            if (!source) return;
            latestItem = frame.Items.FirstOrDefault(x => x.Key == key);
            if (latestItem == null) return;
            var meshNode = latestItem.Pose.Nodes.FirstOrDefault(x => x.Path == meshPath);
            float recorded = meshNode?.Scale[0] ?? float.NaN;
            bool changed = recorded != lastRecordedScale;
            bool matches = ShapeMatches(latestItem);
            if (step == Step.Quiet && latestItem.SampleTimeKnown && latestItem.SampleTime == quietSample) idleFrames++;
            if (matches && source!.itemState == ItemState.Ground && changed)
            {
                groundMatches++;
                if (recorded > lastRecordedScale + .0002)
                {
                    if ((step == Step.GroundBirth || step == Step.FirstPush) && recorded > initialGroundScale + .0005f) firstGrowthMatches++;
                    if (step == Step.SecondPush && recorded > beforeSecondScale + .0005f) secondGrowthMatches++;
                }
            }
            if (samples.Count < 2048 && (changed || matches && frame.T >= lastSample + .25))
            {
                lastSample = frame.T;
                var diagnostic = CaptureDiagnostic(frame.T);
                if (step == Step.Quiet && matches && source!.itemState == ItemState.Ground)
                {
                    long revision = Convert.ToInt64(Read(diagnostic, "Revision"));
                    long boundary = Convert.ToInt64(Read(diagnostic, "MotionBoundary"));
                    var position = source.transform.position; var rotation = source.transform.rotation;
                    bool clean = Read(diagnostic, "Found") is true && Read(diagnostic, "ChildScaleDirty") is false &&
                        Read(diagnostic, "VisualDirty") is false && Read(diagnostic, "HierarchyDirty") is false &&
                        Read(diagnostic, "Attached") is false && Read(diagnostic, "AnimationAdvancing") is not true &&
                        Read(diagnostic, "MotionUntil") is double until && until < frame.T &&
                        latestItem.ClockMode == CreatureReplayRules.ActiveClock;
                    if (!quietAnchorKnown || !clean || revision != quietRevision || boundary != quietBoundary || Math.Abs(recorded - quietScale) > .0005f)
                    { quietMovingWitnesses = 0; quietAnchorKnown = clean; }
                    else if ((position - quietAnchor).sqrMagnitude > .00000001f || Quaternion.Angle(rotation, quietRotation) > .01f)
                        quietMovingWitnesses++;
                    quietAnchor = position; quietRotation = rotation; quietScale = recorded;
                    quietRevision = revision; quietBoundary = boundary;
                }
                samples.Add(new
                {
                    NativeTime = frame.T, Stage = step.ToString(), Key = key, NativeRootId = source!.GetInstanceID(),
                    NativeState = source.itemState.ToString(), NativeStateCode = (int)source.itemState, RecordedState = latestItem.State,
                    latestItem.SampleTimeKnown, latestItem.SampleTime, latestItem.ClockMode,
                    NativeAppliedMeshScale = V(mesh!.localScale), NativeAppliedColliderScale = V(collider!.localScale),
                    RecordedMeshScale = meshNode?.Scale.ToArray(), RecordedColliderScale = latestItem.Pose.Nodes.FirstOrDefault(x => x.Path == colliderPath)?.Scale.ToArray(),
                    RecordedToNativeMeshRatio = Ratio(meshNode?.Scale, mesh.localScale),
                    RecordedToNativeColliderRatio = Ratio(latestItem.Pose.Nodes.FirstOrDefault(x => x.Path == colliderPath)?.Scale, collider.localScale),
                    RootScale = latestItem.Pose.Scale.ToArray(), NativeRootScale = V(source.transform.lossyScale),
                    NativeRootPosition = V(source.transform.position), NativeRootRotation = Q(source.transform.rotation),
                    MeshPath = meshPath, ColliderPath = colliderPath, Matches = matches,
                    NativeSpeed = source.rig ? source.rig.linearVelocity.magnitude : 0,
                    NativeAngularSpeed = source.rig ? source.rig.angularVelocity.magnitude : 0,
                    NativeRigPosition = source.rig ? V(source.rig.position) : null,
                    NativeRigRotation = source.rig ? Q(source.rig.rotation) : null,
                    NativeMinimumSpeedToGrow = ball!.minimumSpeedToGrow,
                    NativeContactType = Read(ball, "_lastContactType")?.ToString(),
                    CaptureDiagnostics = diagnostic,
                    FakeRecordedActive = fakeFrame?.Pose.Active,
                });
            }
            lastRecordedScale = recorded;
        }
        catch (Exception e) { Fail(e, frame.T); }
    }
    private double lastSample;

    private void Bind(Item item)
    {
        source = item; ball = item.GetComponent<Snowball>(); syncer = item.GetComponent<ItemScaleSyncer>();
        Require(ball && syncer && item.mainRenderer && item.colliders != null && item.colliders.Length > 0 && item.colliders[0],
            "Native Snowball references are incomplete.");
        Require(Read(syncer, "_applyDirectlyToMeshAndCollider") is true, "Unexpected native Snowball scale mode; refusing a guessed comparison.");
        nativeMinimumSpeedToGrow = ball!.minimumSpeedToGrow;
        mesh = item.mainRenderer!.transform; collider = item.colliders![0].transform;
        meshPath = NodePath(mesh, item.transform); colliderPath = NodePath(collider, item.transform);
        Require(meshPath != "." && colliderPath != ".", "Snowball child-scale audit requires actual visual/collider child nodes.");
        key = (string)ItemKey.Invoke(null, new object[] { item })!;
        recordedPaths[key] = (meshPath, colliderPath);
        latestItem = null; lastRecordedScale = double.NaN;
    }
    private bool ShapeMatches(ItemFrame? frame)
    {
        if (frame == null || !source || !mesh || !collider || frame.Key != key || frame.State != (int)source!.itemState) return false;
        var a = frame.Pose.Nodes.FirstOrDefault(x => x.Path == meshPath);
        var b = frame.Pose.Nodes.FirstOrDefault(x => x.Path == colliderPath);
        return a != null && b != null && Near(a.Scale, mesh!.localScale) && Near(b.Scale, collider!.localScale) && Near(frame.Pose.Scale, source.transform.lossyScale);
    }
    private void Push(double now)
    {
        if (now < nextForce || now - entered > 4 || (step == Step.SecondPush ? secondPushCount : firstPushCount) >= 4) return;
        Require(source && source!.itemState == ItemState.Ground && source.rig && !source.rig.isKinematic, "Native ball cannot receive a dynamic test push.");
        Require(Vector3.Distance(source!.transform.position, floor) < 8 && TrySnowFloor(source.transform.position, out _), "Naturally rolling ball left the verified bounded snow surface.");
        var rig = source!.rig!;
        float threshold = ball!.minimumSpeedToGrow;
        float target = Math.Min(1.3f, threshold + .2f);
        Require(!float.IsNaN(threshold) && !float.IsInfinity(threshold) && threshold >= 0 && target > threshold,
            "Native grow speed cannot be exceeded within the bounded 1.3 m/s horizontal fixture limit.");
        var before = rig.linearVelocity;
        var horizontal = new Vector3(direction.x, 0, direction.z).normalized;
        Require(horizontal.sqrMagnitude > .9f, "Verified snow lane has no safe horizontal stimulus direction.");
        // Native velocity stimulus only. Preserve gravity/fall velocity; all
        // friction, braking, contacts and size changes remain native gameplay.
        rig.linearVelocity = new Vector3(horizontal.x * target, before.y, horizontal.z * target);
        if (step == Step.SecondPush) secondPushCount++; else firstPushCount++;
        pushStimuli.Add(new { NativeTime = lastFrame, StimulusTime = now, Stage = step.ToString(), Key = key,
            NativeMinimumSpeedToGrow = threshold, HorizontalTarget = target, BeforeVelocity = V(before), AfterVelocity = V(rig.linearVelocity),
            NativePosition = V(source.transform.position), BoundMetres = 8, MaximumHorizontalSpeed = 1.3f });
        nextForce = now + .8;
        Mark("bounded-native-rigidbody-velocity", now, lastFrame);
    }
    private InteractionReplayFrame? FindFake(ReplayFrame? frame) => frame?.Interactions.FirstOrDefault(x => x.Kind == "fake-item" && x.SourcePath == fakePath);
    private void Enter(Step next, double now) { step = next; entered = now; nextForce = now; }
    private void Limit(double now, double seconds) => Require(now - entered <= seconds, step + " did not satisfy its native/recorded assertions within " + seconds + " seconds.");
    private void Mark(string kind, double now, double time)
    { events.Add(new { Kind = kind, NativeTime = time, StimulusTime = now, Stage = step.ToString(), Key = key, FakePath = fakePath }); if (kind != "bounded-native-rigidbody-velocity") interestingTimes.Add(time); }
    private void Fail(Exception e, double now)
    { error = e.GetType().Name + ": " + e.Message; Mark("snow-audit-failed", now, lastFrame); step = Step.Failed; Flush(); }
    internal void Flush() => File.WriteAllText(file, JsonConvert.SerializeObject(new { Summary, Events = events, Samples = samples, PushStimuli = pushStimuli, PlaybackChecks = playbackChecks,
        Method = "Normal FakeItem.Interact -> native manager accepted -> real held Snowball; native DropItemRpc/EquipSlot; bounded direct Rigidbody horizontal velocity stimuli only on verified snow/ice floor, target native minimumSpeedToGrow+.2 capped at 1.3 m/s, vertical velocity preserved, at most four stimuli per period; player native WarpPlayerRPC to verified snow floor at least four metres away during quiet observation, never moving the ball; native Item.Interact to pick up after two pre-held growth periods and quiet/re-wake. First natural ground growth may occur during GroundBirth or FirstPush and is counted only as observed, never backfilled. No scale, mesh, collision material, brake, ball kinematic/pose, FakeItem visibility, item-data or recorder state writes. All frame samples originate in AcceptFullRunFrame; private capture diagnostics are read only on emitted samples with a .25-second independent bound." }, Formatting.Indented));

    private object? CaptureDiagnostic(double now)
    {
        if (now < nextCaptureDiagnostic) return lastCaptureDiagnostic;
        nextCaptureDiagnostic = now + .25;
        var tracked = Read(LiveItemCapture.GetValue(null), "tracked") as IDictionary;
        var entry = source ? tracked?[source!.GetInstanceID()] : null;
        var identity = Read(entry, "Identity"); var rest = Read(entry, "Rest");
        lastCaptureDiagnostic = new
        {
            ObservedAt = now, NativeInstanceId = source ? source!.GetInstanceID() : 0, Found = entry != null,
            RestSettled = Read(rest, "Settled"), RestLastObserved = Read(rest, "lastObserved"),
            Revision = Read(identity, "Revision"), MotionBoundary = Read(identity, "MotionBoundary"), MotionUntil = Read(identity, "MotionUntil"),
            ChildScaleDirty = Read(identity, "ChildScaleDirty"), Pending = Read(identity, "Pending"), Urgent = Read(identity, "Urgent"),
            VisualDirty = Read(identity, "VisualDirty"), HierarchyDirty = Read(identity, "HierarchyDirty"),
            LastSeen = Read(entry, "LastSeen"), SeenRevision = Read(entry, "SeenRevision"), SeenMotionBoundary = Read(entry, "SeenMotionBoundary"),
            LastCompletedObservation = Read(Read(entry, "Frame"), "SampleTime"),
            Attached = Read(entry, "Attached"), AnimationAdvancing = Read(Read(entry, "Animation"), "Advancing"),
            Mob = Read(entry, "Mob"), ExemptDistance = Read(entry, "ExemptDistance"), KnownStatic = Read(entry, "KnownStatic"),
            EventOnly = Read(entry, "EventOnly"), NextFallback = Read(entry, "NextFallback"),
        };
        return lastCaptureDiagnostic;
    }

    // One read-only inspection after each paused, fully ready seek. Expected
    // scales come from the production visual's actual recorded pose brackets;
    // comparing the real clone transforms catches binding/apply errors as well
    // as a fake pickup hidden only in the data. Offline checks validate that
    // these recorded packets themselves equal the native capture witnesses.
    internal void ObservePlayback(Plugin plugin, double target, double origin)
    {
        if (!Success || interestingTimes.Count == 0 || target < interestingTimes.Min() - origin - .001 || target > interestingTimes.Max() - origin + .001) return;
        var session = Read(plugin, "uiPlayback");
        Require(session != null, "Ready snow seek has no actual playback session.");
        var fakeEntries = Read(Read(Read(session, "interactions"), "fakeItems"), "entries") as IDictionary;
        var entry = fakeEntries?[fakePath];
        var expectedFake = Read(entry, "Last") as InteractionReplayFrame;
        Require(expectedFake != null, "Ready snow seek has no actual fake-item visibility lease.");
        var nativeRenderers = Read(entry, "Renderers") as Renderer[] ?? Array.Empty<Renderer>();
        var fallback = Read(Read(entry, "Fallback"), "Root") as GameObject;
        var fallbackRenderers = fallback ? fallback!.GetComponentsInChildren<Renderer>(true) : Array.Empty<Renderer>();
        bool nativeVisible = nativeRenderers.Any(Visible), fallbackVisible = fallbackRenderers.Any(Visible);
        bool visible = nativeVisible || fallbackVisible;
        Require(visible == expectedFake!.Pose.Active, "Actual native/fallback fake snow visibility disagrees with the recorded seek state.");
        if (visible) replayVisibleFake = true; else replayHiddenFake = true;
        var ballChecks = new List<object>();
        var visuals = Read(Read(session, "items"), "visuals") as IDictionary;
        foreach (var pair in recordedPaths)
        {
            var visual = visuals?[pair.Key];
            if (visual == null) continue;
            var replica = Read(visual, "Replica");
            var root = Read(replica, "Root") as GameObject;
            var left = Read(replica, "appliedLeft") as ObjectPose;
            var right = Read(replica, "appliedRight") as ObjectPose;
            Require(root && left != null && Read(visual, "Visible") is true, "Present Snowball replica is not actually visible after a ready seek.");
            float mix = Read(replica, "appliedMix") is float value ? value : 0;
            var nodes = Read(replica, "nodes") as IDictionary;
            var actualMesh = Read(nodes?[pair.Value.Mesh], "Transform") as Transform;
            var actualCollider = Read(nodes?[pair.Value.Collider], "Transform") as Transform;
            var a = left!.Nodes.FirstOrDefault(x => x.Path == pair.Value.Mesh);
            var b = left.Nodes.FirstOrDefault(x => x.Path == pair.Value.Collider);
            Require(a != null && b != null && actualMesh && actualCollider, "Snowball playback mesh/collider paths did not bind.");
            var nextA = right?.Nodes.FirstOrDefault(x => x.Path == pair.Value.Mesh) ?? a;
            var nextB = right?.Nodes.FirstOrDefault(x => x.Path == pair.Value.Collider) ?? b;
            var meshExpected = Vector3.Lerp(Vec(a!.Scale), Vec(nextA!.Scale), mix);
            var colliderExpected = Vector3.Lerp(Vec(b!.Scale), Vec(nextB!.Scale), mix);
            var rootExpected = Vector3.Lerp(Vec(left.Scale), Vec(right?.Scale ?? left.Scale), mix);
            Require(Near(V(actualMesh!.localScale), meshExpected) && Near(V(actualCollider!.localScale), colliderExpected) && Near(V(root!.transform.lossyScale), rootExpected),
                "Actual Snowball replica child/root scale differs from its recorded pose interpolation.");
            replayBallShape = true;
            ballChecks.Add(new { Key = pair.Key, MeshPath = pair.Value.Mesh, ColliderPath = pair.Value.Collider, Mix = mix,
                ExpectedMesh = V(meshExpected), ActualMesh = V(actualMesh!.localScale), ExpectedCollider = V(colliderExpected), ActualCollider = V(actualCollider!.localScale),
                ExpectedRoot = V(rootExpected), ActualRoot = V(root!.transform.lossyScale), Visible = true, Matches = true });
        }
        playbackChecks.Add(new { Target = target, NativeCaptureTime = target + origin, FakeExpectedActive = expectedFake.Pose.Active,
            NativeFakeVisible = nativeVisible, InertFallbackVisible = fallbackVisible,
            NativeRendererFlags = nativeRenderers.Where(x => x).Select(x => new { x.enabled, x.forceRenderingOff, Active = x.gameObject.activeInHierarchy }).ToArray(), BallChecks = ballChecks, Success = true });
        Flush();
    }
    private static bool Visible(Renderer value) => value && value.enabled && !value.forceRenderingOff && value.gameObject.activeInHierarchy;
    private static Vector3 Vec(float[] value) => new(value[0], value[1], value[2]);

    private static bool TrySnowLane(Vector3 center, out Vector3 floor, out Vector3 direction, out Vector3 normal, out string material)
    {
        floor = direction = normal = Vector3.zero; material = "";
        for (int radius = 0; radius <= 3; radius++)
            for (int angle = 0; angle < 8; angle++)
            {
                var p = center + new Vector3(Mathf.Cos(angle * Mathf.PI / 4), 0, Mathf.Sin(angle * Mathf.PI / 4)) * radius;
                if (!TrySnowFloor(p, out var first) || Vector3.Distance(first.point, center) > 3.8f) continue;
                for (int d = 0; d < 8; d++)
                {
                    var flat = new Vector3(Mathf.Cos(d * Mathf.PI / 4), 0, Mathf.Sin(d * Mathf.PI / 4));
                    bool safe = true;
                    for (int j = 1; j <= 8; j++)
                        if (!TrySnowFloor(first.point + flat * (j * .6f), out var next) || Math.Abs(next.point.y - first.point.y) > .18f)
                        { safe = false; break; }
                    if (!safe || Physics.CheckSphere(first.point + Vector3.up * 1.2f, .3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) continue;
                    floor = first.point; normal = first.normal; direction = Vector3.ProjectOnPlane(flat, normal).normalized;
                    material = first.collider.GetComponent<Renderer>().sharedMaterial.name; return true;
                }
            }
        return false;
    }
    private static bool TryQuietStand(Vector3 ballPosition, out Vector3 stand)
    {
        for (int ring = 0; ring < 3; ring++)
            for (int angle = 0; angle < 16; angle++)
            {
                float a = angle * Mathf.PI / 8;
                var offset = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * (4.6f + ring);
                if (!TrySnowFloor(ballPosition + offset, out var hit) || Math.Abs(hit.point.y - ballPosition.y) > .6f) continue;
                var target = hit.point + Vector3.up * 1.2f;
                if (Vector3.Distance(hit.point, ballPosition) < 4 || Physics.CheckSphere(target, .35f,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) continue;
                stand = target; return true;
            }
        stand = default; return false;
    }
    private static bool TrySnowFloor(Vector3 around, out RaycastHit hit)
    {
        // Skip the actual test ball/player instead of changing their layer or
        // temporarily disabling collision. Native Snowball classifies this same
        // collider GameObject, Terrain/Stone1 and material name on collision.
        foreach (var candidate in Physics.RaycastAll(around + Vector3.up * 3, Vector3.down, 7,
                     Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore).OrderBy(value => value.distance))
        {
            if (candidate.normal.y < .97f || Math.Abs(candidate.point.y - around.y) > 3 || !candidate.collider) continue;
            var go = candidate.collider.gameObject;
            if (go.GetComponentInParent<Item>() || go.GetComponentInParent<Character>()) continue;
            if (go.layer != LayerMask.NameToLayer("Terrain") && !go.CompareTag("Stone1")) break;
            var renderer = go.GetComponent<Renderer>();
            if (!renderer || !renderer.sharedMaterial) break;
            string name = renderer.sharedMaterial.name.ToLowerInvariant();
            if (name.Contains("snow") || name.Contains("ice")) { hit = candidate; return true; }
            break;
        }
        hit = default; return false;
    }
    private static Character GuardLocal()
    {
        Require(PhotonNetwork.OfflineMode && PhotonNetwork.InRoom && PhotonNetwork.IsMasterClient && !RunSettings.isMiniRun,
            "Snow audit requires an isolated native offline full run.");
        var local = Character.localCharacter;
        Require(local && local.IsLocal && local.refs != null && local.refs.hip && !local.data.dead && !local.data.fullyPassedOut && !local.gameOver &&
            PlayerHandler.GetAllPlayerCharacters().Count == 1, "Native single local player is unavailable for safe snow stimuli.");
        return local!;
    }
    private static object? Read(object? source, string name)
    { if (source == null) return null; for (var type = source.GetType(); type != null; type = type.BaseType)
        { var field = type.GetField(name, Flags | BindingFlags.DeclaredOnly); if (field != null) return field.GetValue(source);
          var property = type.GetProperty(name, Flags | BindingFlags.DeclaredOnly); if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(source); } return null; }
    private static bool Near(float[] value, Vector3 native) => value.Length == 3 && Math.Abs(value[0] - native.x) <= .0005f && Math.Abs(value[1] - native.y) <= .0005f && Math.Abs(value[2] - native.z) <= .0005f;
    private static float[]? Ratio(float[]? value, Vector3 native) => value == null || value.Length != 3 || native.x == 0 || native.y == 0 || native.z == 0
        ? null : new[] { value[0] / native.x, value[1] / native.y, value[2] / native.z };
    private static float[] V(Vector3 value) => new[] { value.x, value.y, value.z };
    private static float[] Q(Quaternion value) => new[] { value.x, value.y, value.z, value.w };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string NodePath(Transform node, Transform root)
    {
        var parts = new Stack<string>();
        while (node != root)
        {
            var parent = node.parent; Require(parent, "Snowball node is outside the captured root.");
            // ObjectPose uses VisualReplica's sibling-index paths, not the
            // equal-name ordinals used by player ActorJointPaths.
            parts.Push(node.GetSiblingIndex().ToString(CultureInfo.InvariantCulture) + ":" + Uri.EscapeDataString(node.name)); node = parent!;
        }
        return parts.Count == 0 ? "." : string.Join("/", parts);
    }
}
