using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Reflection;
using Photon.Pun;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakReplayLab;

// The recording side only reads live objects. Never call GetData<T>: the game's getter
// creates missing entries, which would change the very inventory being observed.
internal sealed class ItemCapture : IDisposable
{
    private sealed class Identity
    {
        public Guid Guid;
        public string Key = "";
        public string Prefab = "";
        public string Name = "";
        public int ItemId = -1;
        public long Revision;
        public long MotionBoundary;
        public double MotionUntil;
        public PhotonView? BackpackView;
        public Character? BackpackOwner;
        public Item? Source;
        public bool Pending, Urgent, VisualDirty, HierarchyDirty, ContinuousMotionHint, MetadataPending;
        public bool Destroyed;
        public int SourceInstanceId;
    }
    private sealed class Tracked
    {
        public Item Source = null!;
        public Transform Transform = null!;
        public Identity Identity = null!;
        public ItemSamplingPolicy Schedule = null!;
        public ItemDistancePolicy Distance = null!;
        public readonly ItemRestPolicy Rest = new();
        public readonly ItemActivityPolicy Activity = new();
        public readonly ItemSparseSamplingPolicy Sparse = new();
        public NativeCaptureAnimationActivity? Animation;
        public CompassPointer[] Compasses = Array.Empty<CompassPointer>();
        public StickyItemComponent[] StickyItems = Array.Empty<StickyItemComponent>();
        public long SeenAnimationRevision;
        public bool AnimationDirty;
        public double NextLightRetry, NextAnimationRetry;
        public bool ExemptDistance, PausedByDistance;
        public ItemFrame? Frame;
        public bool Attached;
        public bool DynamicChildren;
        public bool Mob;
        public FrogCaptureActivity? Frog;
        public EarlyWormAnimationCapture? Worm;
        public long SeenMotionBoundary;
        public long SeenRevision = -1;
        public BackpackPlacement? Backpack;
        public int InstanceId;
        public int ObjectId;
        public bool KnownStatic, EventOnly;
        public bool HasMoved;
        public bool FallbackPending;
        public bool InitialUrgent;
        public double NextFallback;
        public bool ContinuousMotionPending, MetadataQueued;
        public double NextMetadataRead = double.NegativeInfinity;
        public float LastMetadataUiFuel = float.NaN;
        public double LastContinuousHint = double.NegativeInfinity;
    }
    private sealed class BackpackPlacement
    {
        public Character Owner = null!;
        public PhotonView View = null!;
        public Transform SlotTransform = null!;
        public int Slot, ItemId, BackpackType;
        public Guid Instance, BackpackInstance;
        public InventoryFrame[] Inventory = Array.Empty<InventoryFrame>();
    }
    private sealed class InventoryCache
    {
        public InventoryFrame[] Frames = Array.Empty<InventoryFrame>();
        public bool Known;
        public long Revision = -1;
        public double NextRead;
        public int Selected = -2;
    }
    private static readonly ConditionalWeakTable<Item, Identity> identities = new();
    private static readonly ConditionalWeakTable<Character, InventoryCache> inventoryCache = new();
    private static readonly Dictionary<Type, bool> updatingTypes = new();
    private static long inventoryRevision;
    private static ItemCapture? liveCapture;
    internal static bool EventHooksHealthy { get; set; }
    internal const int MaximumItems = 1024;
    private readonly Action<string>? warning;
    private readonly Func<float>? captureDistance;
    private readonly int captureScene;
    private readonly HashSet<string> warned = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Tracked> tracked = new();
    private readonly HashSet<Tracked> initialPending = new();
    private readonly HashSet<Item> registrationRetry = new();
    private readonly List<Tracked> metadataEntries = new();
    private readonly Dictionary<int, int> segmentItems = new();
    private readonly SegmentCaptureClient? segments;
    private Tracked[] ordered = Array.Empty<Tracked>();
    private readonly ItemCaptureWorkSet<Tracked> work = new();
    private readonly Queue<Identity> changed = new();
    private readonly List<Tracked> animatedEntries = new(), lightEntries = new();
    private readonly List<ItemFrame> frames = new();
    private ItemFrame[] previousArray = Array.Empty<ItemFrame>();
    private bool started, directoryDirty, framesDirty;
    private double nextRegistryAudit;
    private int registryCursor, knownNativeCount = -1;
    internal long ActiveVisits { get; private set; }
    internal long SleepingDistanceChecks { get; private set; }
    internal long FallbackReads { get; private set; }
    internal long NativeReads { get; private set; }
    internal long RegistryVisits { get; private set; }
    internal long RootReads { get; private set; }
    internal long ChildReads { get; private set; }
    internal long LightReads { get; private set; }
    internal long AnimationProbes { get; private set; }
    internal long SleepTransitions { get; private set; }
    internal long WakeTransitions { get; private set; }
    internal long FrogRootChecks { get; private set; }
    internal long FrogIdleSkips { get; private set; }
    internal long FrogIdleTransitions { get; private set; }
    internal long SparseSkips { get; private set; }
    internal long WormClockChecks { get; private set; }
    internal long WormGeometrySkips { get; private set; }
    internal long WormDescriptorChanges { get; private set; }
    internal long ContinuousRootProbes { get; private set; }
    internal long MetadataReads { get; private set; }
    internal bool InitialBaselineReady => started && initialPending.Count == 0 && registrationRetry.Count == 0;

    public ItemCapture(Action<string>? warning = null, Func<float>? captureDistance = null)
    {
        this.warning = warning; this.captureDistance = captureDistance; liveCapture = this;
        captureScene = SceneManager.GetActiveScene().handle;
        segments = CaptureSegmentScope.Active?.CreateClient(AdmitSegmentItem, RetireSegmentItem, PreserveCarriedItem);
    }

    private void AdmitSegmentItem(GameObject source)
    { var item = source.GetComponent<Item>(); if (item) Register(item); }

    private bool PreserveCarriedItem(GameObject source)
    {
        var item = source.GetComponent<Item>();
        if (!item) return false;
        var holder = item.trueHolderCharacter;
        if (holder && holder.IsPlayerControlled && holder.gameObject.activeInHierarchy)
        { NativeSpawnOriginCapture.Detach(source); return true; }
        // Native backpack storage clears trueHolderCharacter. An equipped pack
        // is independently carried, whereas a pack left in a closed map is not.
        if (item.itemState == ItemState.InBackpack && item.backpackReference.IsSome)
        {
            var reference = item.backpackReference.Value.Item2;
            if (reference.type == BackpackReference.BackpackType.Equipped && reference.view)
            { NativeSpawnOriginCapture.Detach(source); return true; }
        }
        bool attached = tracked.TryGetValue(item.GetInstanceID(), out var entry) && StickyAttached(entry, playerOnly: true);
        if (attached) NativeSpawnOriginCapture.Detach(source);
        return attached;
    }

    private void RetireSegmentItem(int objectId)
    {
        if (!segmentItems.TryGetValue(objectId, out int id)) return;
        segmentItems.Remove(objectId);
        if (!tracked.TryGetValue(id, out var entry)) return;
        tracked.Remove(id); initialPending.Remove(entry); work.Forget(entry);
        animatedEntries.Remove(entry); lightEntries.Remove(entry);
        VisualReplica.ReleaseCapture(entry.Transform);
        entry.Frog?.Release();
        entry.Frame = null; entry.Backpack = null; entry.Animation = null;
        directoryDirty = framesDirty = true;
    }

    public ItemFrame[] Capture(ActorFrame[] actors)
    {
        double now = Time.timeAsDouble;
        float radius = captureDistance?.Invoke() ?? 40f;
        if (float.IsNaN(radius) || float.IsInfinity(radius)) radius = 40f;
        DiscoverBaseline();
        DrainChanges();
        AuditRegistry(now);
        RetryRegistrations();
        CaptureAnimationSignals(now);
        CaptureLights(now);
        int sleepChecks = ItemSleepPolicy.DistanceProbeBudget(work.SleepingCount);
        int fallbackBudget = 8;
        for (int i = 0; i < sleepChecks; i++)
        {
            var entry = work.NextSleeping();
            if (entry == null) break;
            SleepingDistanceChecks++;
            entry.Distance.Include(actors, radius, entry.Frame?.HolderId, out bool enteredRange);
            // This is a managed checkpoint-distance check. Event-only sleepers
            // do not touch Transform, Rigidbody, components or visual materials.
            bool fallback = fallbackBudget > 0 && (!entry.EventOnly || !EventHooksHealthy || entry.ContinuousMotionPending) && now >= entry.NextFallback;
            if (fallback && entry.ContinuousMotionPending && entry.KnownStatic && !enteredRange)
            {
                entry.ContinuousMotionPending = false;
                ContinuousRootProbes++;
                if (!ContinuousRootChanged(entry))
                {
                    // A zero-force wind/zone hint does not wake a sleeping rig.
                    // Reuse the existing staggered safety deadline and retain
                    // the complete checkpoint without metadata/bone snapshots.
                    fallbackBudget--; entry.NextFallback = now + ItemSleepPolicy.ProbeDelay(entry.InstanceId);
                    continue;
                }
                entry.Rest.Wake();
            }
            if (enteredRange || fallback)
            {
                if (fallback) { FallbackReads++; fallbackBudget--; }
                entry.PausedByDistance = enteredRange;
                entry.FallbackPending = fallback;
                Wake(entry);
            }
            else if (entry.Frog?.Holding == true) FrogIdleSkips++;
        }
        // Reverse iteration permits O(1) retirement into the sleeping set.
        for (int i = work.Active.Count - 1; i >= 0; i--)
        {
            var entry = work.Active[i];
            ActiveVisits++;
            try { CaptureEntry(entry, actors, radius, now); }
            catch (Exception e)
            {
                entry.Schedule.Observed(now, entry.Identity.Revision, false);
                // Retain the last valid immutable checkpoint on a transient read
                // failure. A later event or fallback retries the source.
                Warn("read", "有一个物品尚未准备好，保留上一有效状态：" + e.Message);
            }
        }
        CaptureMetadata(now);
        if (directoryDirty)
        {
            ordered = tracked.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
            directoryDirty = false; framesDirty = true;
        }
        if (framesDirty)
        {
            frames.Clear();
            foreach (var entry in ordered) if (entry.Frame != null) frames.Add(entry.Frame);
            bool same = frames.Count == previousArray.Length;
            if (same) for (int i = 0; i < frames.Count; i++)
                if (!ReferenceEquals(frames[i], previousArray[i])) { same = false; break; }
            if (!same) previousArray = frames.ToArray();
            framesDirty = false;
        }
        return previousArray;
    }

    private static bool ContinuousRootChanged(Tracked entry)
    {
        try
        {
            var source = entry.Source; var pose = entry.Frame?.Pose;
            if (!source || !source.gameObject.activeInHierarchy || pose == null) return true;
            var p = entry.Transform.position; var r = entry.Transform.rotation; var s = entry.Transform.lossyScale;
            if (Math.Abs(p.x - pose.Position[0]) > .0001f || Math.Abs(p.y - pose.Position[1]) > .0001f || Math.Abs(p.z - pose.Position[2]) > .0001f ||
                Math.Abs(s.x - pose.Scale[0]) > .0001f || Math.Abs(s.y - pose.Scale[1]) > .0001f || Math.Abs(s.z - pose.Scale[2]) > .0001f) return true;
            double dot = (double)r.x * pose.Rotation[0] + (double)r.y * pose.Rotation[1] + (double)r.z * pose.Rotation[2] + (double)r.w * pose.Rotation[3];
            if (double.IsNaN(dot) || double.IsInfinity(dot) || Math.Abs(dot) < .999999) return true;
            var body = source.rig;
            return body && !body.isKinematic && (body.linearVelocity.sqrMagnitude > .0001f || body.angularVelocity.sqrMagnitude > .0001f);
        }
        catch { return true; } // Let the ordinary read/backoff handle an unfamiliar source.
    }

    private void CaptureMetadata(double now)
    {
        for (int i = metadataEntries.Count - 1; i >= 0; i--)
        {
            var entry = metadataEntries[i]; var identity = entry.Identity;
            if (!entry.Source || !tracked.TryGetValue(entry.InstanceId, out var current) || !ReferenceEquals(entry, current) || !identity.MetadataPending)
            { entry.MetadataQueued = false; metadataEntries.RemoveAt(i); continue; }
            if (entry.Frame == null || now + 1e-8 < entry.NextMetadataRead) continue;
            if (CaptureBootstrapWorkBudget.InitialOnly && !BootstrapExplicitRead(entry)) continue;
            // Remaining fuel is data, not a geometry or gameplay boundary.
            // Preserve the root clock, appearance references and quiet lease.
            var old = entry.Frame;
            float fuel = Number(entry.Source.data, DataEntryKey.Fuel, -1);
            float uiFuel = Number(entry.Source.data, DataEntryKey.UseRemainingPercentage, -1);
            MetadataReads++;
            if (old.Fuel != fuel)
            {
                var updated = WithAppearance(old, old.Pose, old.Visuals, old.Lights);
                updated.Fuel = fuel; entry.Frame = updated; framesDirty = true;
            }
            if (entry.LastMetadataUiFuel != uiFuel) { entry.LastMetadataUiFuel = uiFuel; MarkInventoryDirty(); }
            entry.NextMetadataRead = now + (entry.Source.itemState == ItemState.Ground ? ItemSparseSamplingPolicy.Interval : 0);
            identity.MetadataPending = false; entry.MetadataQueued = false; metadataEntries.RemoveAt(i);
        }
    }

    internal void DiscoverBaseline()
    {
        if (started) return;
        started = true;
        // Scope admission queues unseen map sources before any hierarchy read.
        // Repeated preparation never discovers/rebuilds the same directory.
        foreach (var item in Item.ALL_ITEMS) Register(item);
        knownNativeCount = Item.ALL_ITEMS.Count;
        nextRegistryAudit = EventHooksHealthy ? double.PositiveInfinity : Time.timeAsDouble + 5;
    }

    private void CaptureEntry(Tracked entry, ActorFrame[] actors, float radius, double now)
    {
        var identity = entry.Identity;
        var item = entry.Source;
        if (!item)
        {
            segments?.Forget(entry.ObjectId);
            RetireSegmentItem(entry.ObjectId);
            return;
        }
        if (!item.gameObject.activeInHierarchy)
        {
            entry.Frog?.Release();
            initialPending.Remove(entry); // An observed inactive source has no visible initial state.
            if (entry.Frame != null) { entry.Frame = null; framesDirty = true; }
            entry.Attached = false; entry.Backpack = null;
            entry.Distance.Inactive(now, identity.Revision); entry.Rest.Wake();
            // OnEnable reintroduces the object. Hook degradation gets a bounded
            // fallback so a missed activation cannot hide it indefinitely.
            entry.EventOnly = EventHooksHealthy && entry.KnownStatic;
            entry.NextFallback = now + ItemSleepPolicy.ProbeDelay(entry.InstanceId);
            Sleep(entry); return;
        }
        if (CaptureBootstrapWorkBudget.InitialOnly && entry.Frame != null && !BootstrapExplicitRead(entry)) return;
        bool semanticBoundary = identity.Revision != entry.SeenRevision || identity.MotionBoundary != entry.SeenMotionBoundary ||
            identity.VisualDirty || identity.HierarchyDirty || entry.AnimationDirty || entry.InitialUrgent || entry.PausedByDistance;
        if (!entry.Sparse.Due(now, item.itemState == ItemState.Ground, entry.Frame == null, semanticBoundary))
        { SparseSkips++; return; }
        if (entry.Frame == null)
        {
            // No quiet/event state is consumed when preparation runs out of
            // its shared work budget. A real action gets one immediate attempt.
            if (!entry.InitialUrgent && !CaptureBootstrapWorkBudget.TryBeginRead()) return;
        }
        entry.InitialUrgent = false;
        bool refresh = entry.PausedByDistance;
        entry.PausedByDistance = false;
        bool fallbackRead = entry.FallbackPending;
        entry.FallbackPending = false;
        bool wasSettled = entry.Rest.Settled;
        bool notified = identity.Revision != entry.SeenRevision;
        CreatureIdleAnimation? wormAnimation = null;
        bool wormChanged = false;
        bool wormReady = false;
        if (entry.Worm != null)
        {
            long checks = entry.Worm.ClockChecks, skips = entry.Worm.GeometrySkips, changes = entry.Worm.DescriptorChanges;
            wormReady = entry.Worm.TryObserve(now, semanticBoundary, out wormAnimation, out wormChanged);
            WormClockChecks += entry.Worm.ClockChecks - checks;
            WormGeometrySkips += entry.Worm.GeometrySkips - skips;
            WormDescriptorChanges += entry.Worm.DescriptorChanges - changes;
        }
        bool frogHeld = entry.Frog?.Holding == true || entry.Frame?.IdleAnimation != null;
        if (entry.Frog != null && entry.Frame != null)
        {
            long checks = entry.Frog.RootChecks;
            bool held = entry.Frog.TryHold(now, entry.Frame,
                notified || identity.VisualDirty || identity.HierarchyDirty || entry.AnimationDirty || now < identity.MotionUntil,
                fallbackRead || refresh);
            FrogRootChecks += entry.Frog.RootChecks - checks;
            if (held)
            {
                FrogIdleSkips++;
                entry.NextFallback = now + ItemSleepPolicy.ProbeDelay(entry.InstanceId);
                entry.EventOnly = false; Sleep(entry); return;
            }
            if (frogHeld) { entry.Animation?.Invalidate(); entry.AnimationDirty = true; }
        }
        // Sync delivery and body observation do not reset the quiet clock.
        // Only a real data/use event or observed movement can do that.
        if (notified) entry.Rest.Wake();
        bool fixedPlacement = entry.Backpack != null && SameBackpackPlacement(item, entry.Backpack);
        bool attachmentChanged = entry.Backpack != null && !fixedPlacement;
        bool animationMoving = entry.Animation?.Advancing == true || ProceduralChildrenMoving(entry);
        bool continuous = entry.Mob || entry.ExemptDistance || entry.Frog != null || animationMoving;
        bool hot = entry.Attached || continuous || now < identity.MotionUntil || !entry.Rest.Settled;
        bool appearanceDue = !wormReady && entry.Frame != null && NativeVisualAppearance.NeedsCaptureAtTime(entry.Transform,
            entry.Frame.Visuals, now, entry.Frame.Pose.Active, animationMoving || entry.Mob, followSceneClock: true);
        if (animationMoving && entry.Rest.Settled && !entry.Attached && !entry.Mob && !entry.ExemptDistance &&
            !refresh && !fallbackRead && !notified && !entry.AnimationDirty && !entry.Activity.AnimatedRootDue(now, followSceneClock: true)) return;
        if (!refresh && !fallbackRead && !appearanceDue && !entry.AnimationDirty && !wormChanged &&
            !entry.Schedule.ShouldRead(now, identity.Revision, hot, fixedPlacement, attachmentChanged)) return;
        bool dirty = refresh || notified || attachmentChanged || identity.VisualDirty || entry.AnimationDirty || wormChanged;
        entry.AnimationDirty = false;
        if (dirty)
        {
            VisualReplica.InvalidateCapture(entry.Transform, identity.HierarchyDirty || refresh);
            identity.VisualDirty = identity.HierarchyDirty = false;
        }
        string key = Key(item);
        var old = entry.Frame;
        if (old != null && old.Key != key) { old = null; dirty = true; }
        var backpack = KnownBackpackPlacement(item, key, fixedPlacement ? entry.Backpack : null);
        // The safety check of an ordinary sleeping physical item does not
        // invalidate/rebuild its full visual hierarchy. Unknown components keep
        // the more conservative bounded full-tree fallback.
        bool unknownFallback = fallbackRead && !entry.KnownStatic && !wormReady;
        if (unknownFallback) VisualReplica.InvalidateCapture(entry.Transform, hierarchyChanged: true);
        bool fastChildren = (entry.Mob || entry.ExemptDistance || entry.Frog != null || animationMoving || backpack == null && entry.Attached && entry.DynamicChildren) && entry.Activity.FastChildrenDue(now, followSceneClock: true);
        bool children = ItemActivityPolicy.ChildrenDue(dirty, old == null, fastChildren && !wormReady, unknownFallback);
        if (children) { ChildReads++; entry.Activity.ChildrenObserved(now); }
        NativeReads++;
        RootReads++;
        entry.Activity.RootObserved(now);
        var sample = Read(item, key, old, children, fixedPlacement && backpack != null ? old?.Pose : null,
            now, dirty, !wormReady && (animationMoving || entry.Mob), old == null || dirty || entry.Activity.LightDue(now, followSceneClock: true),
            wormReady ? wormAnimation : null, suppressStableAppearance: wormReady);
        if (old == null || dirty || entry.Activity.LightDue(now, followSceneClock: true)) { LightReads++; entry.Activity.LightObserved(now); }
        bool attached = item.itemState != ItemState.Ground || StickyAttached(entry);
        bool kinematic = item.rig && item.rig.isKinematic;
        entry.Distance.Observed(now, identity.Revision, sample.Pose.Position[0], sample.Pose.Position[1], sample.Pose.Position[2]);
        bool rootMoved = old != null && backpack == null && RootMoved(old.Pose, sample.Pose);
        entry.HasMoved |= attached || !kinematic || rootMoved;
        float linearVelocitySquared = 0, angularVelocitySquared = 0;
        if (backpack == null && item.rig && !item.rig.isKinematic)
        {
            linearVelocitySquared = item.rig.linearVelocity.sqrMagnitude;
            angularVelocitySquared = item.rig.angularVelocity.sqrMagnitude;
        }
        bool moving = ItemActivityPolicy.ObservedMotion(old != null, rootMoved, linearVelocitySquared, angularVelocitySquared);
        // A delayed bounded safety poll need not replay a one-second hot phase
        // for an already settled unchanged checkpoint. New events clear Settled
        // above; a real pose/body movement still wakes immediately here.
        if (!fallbackRead || attached || moving || !entry.Rest.Settled)
            entry.Rest.Observed(now, attached || moving, sample.Pose);
        bool retainQuietObservation = old != null && old.SampleTimeKnown && old.ClockMode != CreatureReplayRules.ActiveClock &&
            wasSettled && entry.Rest.Settled && !moving && !attached && !semanticBoundary && !wormChanged && !dirty && !animationMoving &&
            SameQuietGeometryAndState(old, sample);
        int observedMode = retainQuietObservation ? old!.ClockMode :
            wormReady && !moving && entry.Rest.Settled ? CreatureReplayRules.IdleClock : CreatureReplayRules.ActiveClock;
        if (retainQuietObservation && !ReferenceEquals(sample, old))
        {
            // Fuel/continuous metadata may change while a sleeping root is
            // physically identical. Keep that independent geometry packet and
            // clock; a safety poll is not another animation observation.
            sample = sample.WithObservation(old!.SampleTime, old.ClockMode, old.SampleDiscontinuity);
            sample.Pose = old.Pose;
        }
        else if (!(ReferenceEquals(sample, old) && old?.ClockMode == observedMode && observedMode != CreatureReplayRules.ActiveClock))
            sample = sample.WithObservation(now, observedMode, semanticBoundary || wormChanged);
        if (!ReferenceEquals(entry.Frame, sample)) { entry.Frame = sample; framesDirty = true; }
        initialPending.Remove(entry);
        entry.Attached = attached; entry.Backpack = backpack; entry.SeenRevision = identity.Revision;
        entry.SeenMotionBoundary = identity.MotionBoundary; entry.Sparse.Observed(now);
        entry.Schedule.Observed(now, identity.Revision, moving, backpack != null);
        entry.Distance.Include(actors, radius, sample.HolderId, out _);
        if (entry.Frog != null && entry.Frog.TryEnter(now, sample, entry.Rest.Settled, out var idleAnimation))
        {
            var idleFrame = sample.WithIdleAnimation(idleAnimation).WithObservation(now,
                idleAnimation == null ? CreatureReplayRules.SleepingClock : CreatureReplayRules.IdleClock,
                semanticBoundary || wormChanged);
            if (!ReferenceEquals(entry.Frame, idleFrame)) { entry.Frame = idleFrame; framesDirty = true; }
            FrogIdleTransitions++;
            entry.EventOnly = false;
            entry.NextFallback = now + ItemSleepPolicy.ProbeDelay(entry.InstanceId);
            Sleep(entry); return;
        }
        // Light and Animator clocks have independent small work sets. Component
        // presence and an enabled bulb cannot keep the hierarchy hot. An actual
        // advancing native loop remains in the active work set at the ground
        // 5 Hz cadence (held items retain their original cadence), because
        // a still interval within one clip does not prove its later keys are idle.
        animationMoving = entry.Animation?.Advancing == true || ProceduralChildrenMoving(entry);
        continuous = entry.Mob || entry.ExemptDistance || entry.Frog != null || animationMoving;
        bool untouchedStatic = ItemSleepPolicy.InitialStatic(EventHooksHealthy, entry.KnownStatic, kinematic, entry.HasMoved, attached, continuous, now < identity.MotionUntil);
        if (untouchedStatic || ItemActivityPolicy.CanSleep(entry.Rest.Settled, attached, entry.Mob || entry.ExemptDistance || entry.Frog != null, animationMoving, now < identity.MotionUntil))
        {
            // A final child snapshot is a phase boundary, not deletion from the
            // logical frame. Every seek page still sees the last complete state.
            if (!retainQuietObservation && ItemActivityPolicy.FinalChildrenDue(children, fallbackRead, wasSettled, dirty, moving, attached))
            {
                var final = Read(item, key, sample, true, backpack != null ? sample.Pose : null, now, false, false, false);
                final = final.WithObservation(now, CreatureReplayRules.SleepingClock, semanticBoundary || wormChanged);
                RootReads++; ChildReads++; NativeReads++;
                if (!ReferenceEquals(entry.Frame, final)) { entry.Frame = final; framesDirty = true; }
            }
            else if (!retainQuietObservation && entry.Frame?.ClockMode != CreatureReplayRules.SleepingClock)
            { entry.Frame = entry.Frame!.WithObservation(now, CreatureReplayRules.SleepingClock, semanticBoundary || wormChanged); framesDirty = true; }
            entry.EventOnly = ItemSleepPolicy.EventOnly(EventHooksHealthy, entry.KnownStatic, kinematic);
            entry.NextFallback = now + ItemSleepPolicy.ProbeDelay(entry.InstanceId);
            Sleep(entry);
        }
    }

    // This intentionally allows only continuous fuel/progress metadata to vary.
    // Visibility, node motion, ownership, native resources and discrete state
    // still require a fresh observation even when the physical root is quiet.
    private static bool SameQuietGeometryAndState(ItemFrame a, ItemFrame b) =>
        a.Key == b.Key && a.ItemId == b.ItemId && a.Prefab == b.Prefab && a.Name == b.Name &&
        a.HolderId == b.HolderId && a.State == b.State && a.Primary == b.Primary && a.Secondary == b.Secondary &&
        a.ProgressKnown == b.ProgressKnown && a.Uses == b.Uses && a.Cooked == b.Cooked && a.Used == b.Used && a.Lit == b.Lit &&
        a.RevealBackpackContents == b.RevealBackpackContents && ReferenceEquals(a.IdleAnimation, b.IdleAnimation) &&
        ReferenceEquals(a.Visuals, b.Visuals) && ReferenceEquals(a.Lights, b.Lights) &&
        a.Color[0] == b.Color[0] && a.Color[1] == b.Color[1] && a.Color[2] == b.Color[2] && a.Color[3] == b.Color[3] &&
        VisualReplica.Same(a.Pose, b.Pose);

    private void Wake(Tracked entry)
    { if (!work.IsActive(entry)) WakeTransitions++; work.Wake(entry); }
    private void Sleep(Tracked entry)
    { if (work.IsActive(entry)) SleepTransitions++; work.Sleep(entry); }

    private static bool BootstrapExplicitRead(Tracked entry) => entry.InitialUrgent || entry.Identity.Urgent ||
        entry.Identity.VisualDirty || entry.Identity.HierarchyDirty || entry.Identity.Revision != entry.SeenRevision;

    private void CaptureAnimationSignals(double now)
    {
        for (int i = animatedEntries.Count - 1; i >= 0; i--)
        {
            var entry = animatedEntries[i];
            if (!entry.Source || !tracked.TryGetValue(entry.InstanceId, out var current) || !ReferenceEquals(current, entry))
            { animatedEntries.RemoveAt(i); continue; }
            if (CaptureBootstrapWorkBudget.InitialOnly && entry.Frame != null && !BootstrapExplicitRead(entry)) continue;
            if (now < entry.NextAnimationRetry) continue;
            if (entry.Frog?.Holding == true) continue;
            if (entry.Worm != null) continue; // The audited clip clock owns this small work set.
            bool force = entry.Identity.Revision != entry.SeenRevision || entry.Identity.VisualDirty || entry.Identity.HierarchyDirty;
            if (!entry.Sparse.AnimationDue(now, entry.Source.itemState == ItemState.Ground, force)) continue;
            try
            {
                var animation = entry.Animation!; long probes = animation.ProbeCount;
                animation.Read(now, followSceneClock: true); AnimationProbes += animation.ProbeCount - probes;
                entry.Sparse.AnimationObserved(now);
                if (entry.SeenAnimationRevision == animation.Revision) continue;
                entry.SeenAnimationRevision = animation.Revision;
                entry.AnimationDirty = true;
                // State boundaries (including automatic transitions) wake once.
                // Actual animation keeps its own cadence, outside event-window heat.
                Wake(entry);
            }
            catch (Exception error) { entry.NextAnimationRetry = now + 1; Warn("animation-clock", "物品动画时钟暂不可用，保留状态：" + error.Message); }
        }
    }

    private void CaptureLights(double now)
    {
        for (int i = lightEntries.Count - 1; i >= 0; i--)
        {
            var entry = lightEntries[i];
            if (!entry.Source || !tracked.TryGetValue(entry.InstanceId, out var current) || !ReferenceEquals(current, entry))
            { lightEntries.RemoveAt(i); continue; }
            var old = entry.Frame;
            if (CaptureBootstrapWorkBudget.InitialOnly && old != null && !BootstrapExplicitRead(entry)) continue;
            if (old == null || now < entry.NextLightRetry || !entry.Activity.LightDue(now, followSceneClock: true)) continue;
            try
            {
                entry.Activity.LightObserved(now); LightReads++;
                var lights = NativeLightAppearance.Capture(entry.Transform, old.Lights);
                var visuals = NativeVisualAppearance.CaptureAtTime(entry.Transform, old.Visuals, now,
                    allowAnimated: old.Lit || HasEnabledLight(lights), followSceneClock: true);
                var pose = old.Pose;
                // A visibility edge may also hide/reveal the native flame subtree.
                // Intensity/colour pulses never trigger a geometry traversal.
                if (LightVisibilityChanged(old.Lights, lights))
                { pose = VisualReplica.Capture(entry.Transform, true); RootReads++; ChildReads++; }
                if (ReferenceEquals(old.Lights, lights) && ReferenceEquals(old.Visuals, visuals) && ReferenceEquals(old.Pose, pose)) continue;
                var updated = WithAppearance(old, pose, visuals, lights);
                if (!ReferenceEquals(old.Pose, pose)) updated = updated.WithObservation(now, old.ClockMode, discontinuity: true);
                entry.Frame = updated; framesDirty = true;
            }
            catch (Exception error) { entry.NextLightRetry = now + 1; Warn("light-clock", "物品灯光采样暂不可用，保留状态：" + error.Message); }
        }
    }

    private static bool LightVisibilityChanged(NativeLightFrame[]? before, NativeLightFrame[] after)
    {
        if (before == null || before.Length != after.Length) return true;
        for (int i = 0; i < after.Length; i++) if (before[i].Enabled != after[i].Enabled) return true;
        return false;
    }

    private static ItemFrame WithAppearance(ItemFrame old, ObjectPose pose, NativeRendererFrame[]? visuals, NativeLightFrame[]? lights) => new()
    {
        Key = old.Key, ItemId = old.ItemId, Prefab = old.Prefab, Name = old.Name, HolderId = old.HolderId, State = old.State,
        Pose = pose, ProgressKnown = old.ProgressKnown, Primary = old.Primary, Secondary = old.Secondary,
        Progress = old.Progress, Uses = old.Uses, Fuel = old.Fuel, Cooked = old.Cooked, Used = old.Used, Lit = old.Lit,
        RevealBackpackContents = old.RevealBackpackContents, Color = old.Color, Visuals = visuals, Lights = lights, IdleAnimation = old.IdleAnimation,
        SampleTimeKnown = old.SampleTimeKnown, SampleTime = old.SampleTime, ClockMode = old.ClockMode,
        SampleDiscontinuity = old.SampleDiscontinuity,
    };

    private static bool HasEnabledLight(NativeLightFrame[]? lights)
    {
        if (lights != null) foreach (var light in lights) if (light.Enabled) return true;
        return false;
    }

    private static bool ProceduralChildrenMoving(Tracked entry)
    {
        // Audited native Warp mode spins even on a motionless ground compass.
        // Normal/pirate heading falls under held motion or the bounded unknown
        // fallback; Update-method presence alone does not authorize full-rate.
        foreach (var compass in entry.Compasses)
            if (compass && compass.isActiveAndEnabled && compass.compassType == CompassPointer.CompassType.Warp &&
                compass.warpSpeed != 0 && compass.speedMultiplier != 0) return true;
        return false;
    }

    private static bool StickyAttached(Tracked entry, bool playerOnly = false)
    {
        foreach (var sticky in entry.StickyItems)
            if (sticky && sticky.stuckToCharacter && (!playerOnly || sticky.stuckToCharacter.IsPlayerControlled)) return true;
        return false;
    }

    private static bool WornBackpack(Character owner, out BackpackSlot? backpack)
    {
        backpack = null;
        if (!owner || !owner.IsPlayerControlled || !owner.gameObject.activeInHierarchy || !owner.refs?.hip ||
            !owner.refs.animator || !owner.refs.items || !owner.player) return false;
        backpack = owner.player.backpackSlot;
        int type = backpack == null ? 0 : (int)backpack.backpackType;
        return backpack != null && !backpack.IsEmpty() && type >= 1 && type <= 4 &&
            (!owner.refs.items.currentSelectedSlot.IsSome || owner.refs.items.currentSelectedSlot.Value != 3);
    }

    private static bool SameBackpackPlacement(Item item, BackpackPlacement placement)
    {
        if (!item || !item.gameObject.activeInHierarchy || item.itemState != ItemState.InBackpack ||
            !item.backpackReference.IsSome || !item.backpackSlotTransform ||
            item.backpackSlotTransform != placement.SlotTransform || item.itemID != placement.ItemId ||
            item.data?.guid != placement.Instance) return false;
        var (slot, reference) = item.backpackReference.Value;
        if (slot != placement.Slot || reference.type != BackpackReference.BackpackType.Equipped ||
            !reference.view || reference.view != placement.View || !WornBackpack(placement.Owner, out var backpack) ||
            (int)backpack!.backpackType != placement.BackpackType || backpack.data?.guid != placement.BackpackInstance) return false;
        // Actor capture already refreshes this cache before item capture. A slot
        // inventory change is an immediate boundary, not a half-second delay.
        return inventoryCache.TryGetValue(placement.Owner, out var inventory) && inventory.Known &&
            ReferenceEquals(inventory.Frames, placement.Inventory);
    }

    private static BackpackPlacement? KnownBackpackPlacement(Item item, string key, BackpackPlacement? previous)
    {
        if (previous != null) return previous; // SameBackpackPlacement checked it this tick.
        if (item.itemState != ItemState.InBackpack || !item.backpackReference.IsSome || !item.backpackSlotTransform) return null;
        var (slot, reference) = item.backpackReference.Value;
        if (reference.type != BackpackReference.BackpackType.Equipped || !reference.view) return null;
        var identity = identities.GetValue(item, _ => new Identity());
        if (identity.BackpackView != reference.view || !identity.BackpackOwner)
        { identity.BackpackView = reference.view; identity.BackpackOwner = reference.view.GetComponent<Character>(); }
        var owner = identity.BackpackOwner;
        if (!owner || !WornBackpack(owner!, out var backpack) ||
            !inventoryCache.TryGetValue(owner!, out var inventory) ||
            !BackpackAttachmentPolicy.IsKnownEquippedSlot(key, item.itemID, slot, inventory.Known, inventory.Frames)) return null;
        return new BackpackPlacement
        {
            Owner = owner!, View = reference.view, Slot = slot, SlotTransform = item.backpackSlotTransform,
            ItemId = item.itemID, Instance = item.data.guid, BackpackType = (int)backpack!.backpackType,
            BackpackInstance = backpack.data?.guid ?? Guid.Empty, Inventory = inventory.Frames,
        };
    }

    private void Register(Item item, bool urgent = false)
    {
        if (!item) { registrationRetry.Remove(item!); return; }
        var scene = item.gameObject.scene;
        if (!scene.IsValid() || !scene.isLoaded || scene.handle != captureScene) { registrationRetry.Remove(item); return; }
        int id = item.GetInstanceID();
        if (segments != null && !segments.Admit(item.gameObject, urgent)) { registrationRetry.Remove(item); return; }
        try { RegisterAdmitted(item, id, urgent); registrationRetry.Remove(item); }
        catch (Exception error)
        {
            registrationRetry.Add(item);
            Warn("register", "物品目录尚未准备好，将在准备预算内重试：" + error.Message);
        }
    }

    private void RetryRegistrations()
    {
        if (registrationRetry.Count == 0) return;
        foreach (var item in registrationRetry.ToArray())
        {
            if (!item || !item.gameObject.activeInHierarchy) { registrationRetry.Remove(item!); continue; }
            if (!CaptureBootstrapWorkBudget.TryBeginRead()) break;
            Register(item);
        }
    }

    private void RegisterAdmitted(Item item, int id, bool urgent)
    {
        if (tracked.TryGetValue(id, out var existing))
        {
            if (existing.Source == item)
            {
                existing.InitialUrgent |= urgent;
                if (existing.Frame == null && item.gameObject.activeInHierarchy) initialPending.Add(existing);
                return;
            }
            RetireSegmentItem(existing.ObjectId);
        }
        if (tracked.Count >= MaximumItems) { Warn("count", "物品轨道达到 1024 个实体上限；超出的物品未记录。"); return; }
        var identity = identities.GetValue(item, _ => new Identity());
        if (identity.Destroyed) return;
        identity.Source = item;
        identity.SourceInstanceId = id;
        Describe(item, out bool animated, out bool knownStatic);
        var entry = new Tracked { Source = item, Transform = item.transform, InstanceId = id, ObjectId = item.gameObject.GetInstanceID(), Identity = identity,
            Schedule = new ItemSamplingPolicy(id), Distance = new ItemDistancePolicy(id), DynamicChildren = animated,
            InitialUrgent = urgent,
            KnownStatic = knownStatic, Mob = item is MobItem || item.GetComponentInChildren<Mandrake>(true) || item.GetComponentInChildren<Peak.EarlyWorm>(true),
            ExemptDistance = DistanceExempt(item) };
        entry.Frog = FrogCaptureActivity.Create(item);
        EarlyWormAnimationCapture.TryCreate(item, out entry.Worm);
        if (entry.Frog != null) { entry.Mob = false; entry.ExemptDistance = false; }
        entry.Compasses = item.GetComponentsInChildren<CompassPointer>(true);
        entry.StickyItems = item.GetComponentsInChildren<StickyItemComponent>(true);
        var animators = item.GetComponentsInChildren<Animator>(true);
        if (animators.Length != 0) entry.Animation = new NativeCaptureAnimationActivity(animators);
        bool hasLight = item.GetComponentsInChildren<Light>(true).Length != 0;
        if (entry.Animation != null) animatedEntries.Add(entry);
        if (hasLight) lightEntries.Add(entry);
        tracked.Add(id, entry); initialPending.Add(entry); segmentItems[entry.ObjectId] = id; Wake(entry); directoryDirty = true;
    }

    private void DrainChanges()
    {
        while (changed.Count > 0)
        {
            var identity = changed.Dequeue(); identity.Pending = false;
            bool urgent = identity.Urgent; identity.Urgent = false;
            var item = identity.Source;
            // A queued OnDestroy retains the managed instance until drained;
            // Unity's overloaded null does not erase its registration identity.
            if (ReferenceEquals(item, null)) continue;
            if (identity.Destroyed || !item)
            {
                registrationRetry.Remove(item!);
                if (tracked.TryGetValue(identity.SourceInstanceId, out var dead) && ReferenceEquals(dead.Identity, identity))
                { segments?.Forget(dead.ObjectId); RetireSegmentItem(dead.ObjectId); }
                identity.Source = null;
                continue;
            }
            Register(item, urgent);
            if (!tracked.TryGetValue(item.GetInstanceID(), out var entryValue)) continue;
            if (identity.MetadataPending && !entryValue.MetadataQueued)
            { entryValue.MetadataQueued = true; metadataEntries.Add(entryValue); }
            bool continuousHint = identity.ContinuousMotionHint;
            identity.ContinuousMotionHint = false;
            bool semantic = urgent || identity.VisualDirty || identity.HierarchyDirty || identity.Revision != entryValue.SeenRevision ||
                identity.MotionBoundary != entryValue.SeenMotionBoundary;
            if (!semantic && entryValue.Frame != null && (entryValue.Rest.Settled || entryValue.EventOnly) && !entryValue.Attached)
            {
                if (continuousHint)
                {
                    // First hint after a quiet session gets a prompt root
                    // bracket. Repeated zero-force hints cannot keep bringing
                    // the staggered fallback deadline forward.
                    if (Time.timeAsDouble - entryValue.LastContinuousHint > .75)
                        entryValue.NextFallback = Math.Min(entryValue.NextFallback, Time.timeAsDouble + ItemSparseSamplingPolicy.Interval);
                    entryValue.LastContinuousHint = Time.timeAsDouble;
                }
                entryValue.ContinuousMotionPending |= continuousHint;
                // Metadata has its own tiny work set. Motion hints only request
                // a bounded safety observation; neither renews the quiet lease.
                continue;
            }
            if (identity.HierarchyDirty)
            {
                Describe(item, out entryValue.DynamicChildren, out entryValue.KnownStatic);
                entryValue.Mob = item is MobItem || item.GetComponentInChildren<Mandrake>(true) || item.GetComponentInChildren<Peak.EarlyWorm>(true);
                entryValue.ExemptDistance = DistanceExempt(item);
                entryValue.Frog?.Release(); entryValue.Frog = FrogCaptureActivity.Create(item);
                EarlyWormAnimationCapture.TryCreate(item, out entryValue.Worm);
                if (entryValue.Frog != null) { entryValue.Mob = false; entryValue.ExemptDistance = false; }
                entryValue.Compasses = item.GetComponentsInChildren<CompassPointer>(true);
                entryValue.StickyItems = item.GetComponentsInChildren<StickyItemComponent>(true);
                var animators = item.GetComponentsInChildren<Animator>(true);
                if (animators.Length != 0)
                {
                    if (entryValue.Animation == null) animatedEntries.Add(entryValue);
                    entryValue.Animation = new NativeCaptureAnimationActivity(animators); entryValue.SeenAnimationRevision = -1;
                }
                if (item.GetComponentsInChildren<Light>(true).Length != 0 && !lightEntries.Contains(entryValue)) lightEntries.Add(entryValue);
            }
            Wake(entryValue);
        }
    }

    private void AuditRegistry(double now)
    {
        // Normally a constant-time count guard is sufficient: Awake/enable/data
        // assignment/destroy notifications incrementally maintain the directory.
        // If a game update broke a hook, scan at most 16 native entries per frame
        // and repeat every five seconds, without rebuilding existing entries.
        int count = Item.ALL_ITEMS.Count;
        if (count != knownNativeCount) { knownNativeCount = count; registryCursor = 0; nextRegistryAudit = now; }
        if (now < nextRegistryAudit) return;
        int through = Math.Min(count, registryCursor + 16);
        for (; registryCursor < through; registryCursor++) { RegistryVisits++; Register(Item.ALL_ITEMS[registryCursor]); }
        if (registryCursor >= count)
        {
            registryCursor = 0; nextRegistryAudit = EventHooksHealthy ? double.PositiveInfinity : now + 5;
        }
    }

    private static void Describe(Item item, out bool animated, out bool knownStatic)
    {
        animated = item.GetComponentsInChildren<Animator>(true).Length > 0 || item.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length > 0;
        knownStatic = true;
        foreach (var behaviour in item.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (!behaviour) continue;
            Type type = behaviour.GetType();
            // Exact native companions do not independently change the visual
            // root/tree. Their changes use observed item/cooking events. A
            // subclass is intentionally outside this semantic allowlist.
            if (type == typeof(Item) || type == typeof(ItemCooking) || type == typeof(ItemParticles) ||
                type == typeof(ItemUseFeedback) || type == typeof(ItemImpactSFX) || type == typeof(ItemVFX) ||
                type == typeof(ItemTorch) || type == typeof(ItemPhysicsSyncer) || type == typeof(PhotonView) ||
                type == typeof(Peak.ItemOptimizer) || type == typeof(Peak.PhotonCleanupHelper) || type == typeof(LootData) ||
                type == typeof(ColorblindVariant) || type == typeof(CenterOfMass) || type == typeof(ChessStabilizer) ||
                type == typeof(EventOnItemCollision) || type == typeof(HandVisual) || type == typeof(Bonkable) ||
                type == typeof(Breakable)) continue;
            if (!updatingTypes.TryGetValue(type, out bool updates))
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                updates = type.GetMethod("Update", flags) != null || type.GetMethod("LateUpdate", flags) != null ||
                    type.GetMethod("FixedUpdate", flags) != null;
                updatingTypes[type] = updates;
            }
            // Unknown timed behavior must not lose a short animation, fuse or
            // sticky attachment merely because the item's physical root rests.
            if (updates) { animated = true; knownStatic = false; }
            // Native synchronous ItemAction delegates run from observed use or
            // state-change events. Third-party subclasses and coroutine actions
            // are intentionally excluded from event-only eligibility.
            else if (!NativeSynchronousAction(type)) knownStatic = false;
        }
    }

    private static bool NativeSynchronousAction(Type type)
    {
        if (type.Assembly != typeof(Item).Assembly || !typeof(ItemActionBase).IsAssignableFrom(type)) return false;
        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            if (typeof(System.Collections.IEnumerator).IsAssignableFrom(method.ReturnType)) return false;
        return true;
    }

    private static bool DistanceExempt(Item item) =>
        item is MobItem || (item.itemTags & Item.ItemTags.Bird) != 0 ||
        item.GetComponentInChildren<Mob>(true) || item.GetComponentInChildren<TumbleWeed>(true) ||
        item.GetComponentInChildren<Mandrake>(true) || item.GetComponentInChildren<Peak.EarlyWorm>(true) || item.GetComponentInChildren<BeeSwarm>(true) ||
        item.GetComponentInChildren<SlipperyJellyfish>(true) || item.GetComponentInChildren<VenusFlyTrap>(true) ||
        item.GetComponentInChildren<Spider>(true) || item.GetComponentInChildren<MushroomZombie>(true);

    private static bool RootMoved(ObjectPose a, ObjectPose b) => ItemRestPolicy.RootMoved(a, b);

    internal static void MarkDirty(Item item, bool hierarchyChanged = false)
    {
        if (!item) return;
        var identity = identities.GetValue(item, _ => new Identity());
        identity.VisualDirty = true; identity.HierarchyDirty |= hierarchyChanged;
        if (!identity.Pending) identity.Revision++;
        identity.MotionUntil = Time.timeAsDouble + .25;
        Notify(item, identity); MarkInventoryDirty();
    }
    internal static void MarkMotion(Item item)
        => MarkMotionObservation(item, continuousObservation: false);
    internal static void MarkMotionObservation(Item item, bool continuousObservation)
    {
        if (!item) return;
        var identity = identities.GetValue(item, _ => new Identity());
        if (continuousObservation) identity.ContinuousMotionHint = true;
        else identity.MotionUntil = Time.timeAsDouble + .25;
        if (!continuousObservation && !identity.Pending) identity.MotionBoundary++;
        Notify(item, identity, urgent: !continuousObservation);
    }
    internal static void MarkMetadataObservation(Item item)
    {
        if (!item) return;
        var identity = identities.GetValue(item, _ => new Identity());
        identity.MetadataPending = true;
        Notify(item, identity, urgent: false);
    }
    private static void Notify(Item item, Identity identity, bool urgent = true)
    {
        identity.Source = item;
        if (liveCapture == null) return;
        // Coalesce lifecycle discoveries without downgrading a real action
        // already queued for the same source during map activation.
        identity.Urgent |= urgent;
        if (identity.Pending) return;
        identity.Pending = true; liveCapture.changed.Enqueue(identity);
    }
    internal static void RegistryChanged(Item item)
    {
        if (!item) return;
        var identity = identities.GetValue(item, _ => new Identity());
        identity.VisualDirty = true;
        if (!identity.Pending) identity.Revision++;
        Notify(item, identity, urgent: false);
        MarkInventoryDirty();
    }
    internal static void Destroying(Item item)
    {
        // Unity fake-null may already be set when a destruction callback runs.
        // Use the cached identity; never call a native getter during teardown.
        if (ReferenceEquals(item, null) || !identities.TryGetValue(item, out var identity)) return;
        identity.Destroyed = true; Notify(item, identity);
    }
    internal static void MarkInventoryDirty() => inventoryRevision++;

    internal static void WakeArea(Vector3 center, float radius)
    {
        var capture = liveCapture;
        if (capture == null || radius <= 0) return;
        double limit = (double)radius * radius;
        // Called once per explosion, using saved coordinates; no extra physics
        // overlap query or hierarchy lookup is added to the native AOE loop.
        foreach (var entry in capture.tracked.Values)
        {
            var pose = entry.Frame?.Pose;
            if (pose == null) continue;
            double dx = pose.Position[0] - center.x, dy = pose.Position[1] - center.y, dz = pose.Position[2] - center.z;
            if (dx * dx + dy * dy + dz * dz <= limit) MarkDirty(entry.Source);
        }
    }

    internal static string Key(Item item)
    {
        var identity = identities.GetValue(item, _ => new Identity());
        Guid guid = item.data?.guid ?? Guid.Empty;
        // Equipping/drop replaces a Photon entity; old/new can briefly coexist with the same
        // data GUID. The instance suffix is a recording identity, never a prefab lookup key.
        if (identity.Key.Length == 0 || identity.Guid != guid)
        {
            identity.Guid = guid;
            identity.Key = (guid == Guid.Empty ? "anonymous" : guid.ToString("N")) + "/" + item.GetInstanceID().ToString(CultureInfo.InvariantCulture);
        }
        return identity.Key;
    }

    internal static string Name(Item item) => item.UIData?.itemName ?? item.name ?? "";

    private static ItemFrame Read(Item item, string key, ItemFrame? old, bool children, ObjectPose? fixedPlacement, double now, bool dirty, bool animatedAppearance, bool sampleLights = true,
        CreatureIdleAnimation? nativeAnimation = null, bool suppressStableAppearance = false)
    {
        var holder = item.trueHolderCharacter;
        var identity = identities.GetValue(item, _ => new Identity());
        if (identity.ItemId != item.itemID)
        {
            identity.Prefab = item.name.Replace("(Clone)", "").Trim();
            if (ItemDatabase.TryGetItem(item.itemID, out var canonical) && canonical) identity.Prefab = canonical.name;
            identity.Name = Name(item); identity.ItemId = item.itemID;
        }
        // Native InBackpack explicitly clears trueHolderCharacter. Preserve the
        // equipped pack owner, not a stale hand owner, without invoking GetData.
        if (item.itemState == ItemState.InBackpack && item.backpackReference.IsSome)
        {
            var reference = item.backpackReference.Value.Item2;
            if (reference.type == BackpackReference.BackpackType.Equipped && reference.view)
            {
                if (identity.BackpackView != reference.view || !identity.BackpackOwner)
                { identity.BackpackView = reference.view; identity.BackpackOwner = reference.view.GetComponent<Character>(); }
                holder = identity.BackpackOwner;
            }
            else holder = null;
        }
        string holderId = holder ? global::PeakReplayLab.Capture.Id(holder) : "";
        var lights = sampleLights ? NativeLightAppearance.Capture(item.transform, old?.Lights) : old?.Lights;
        // Stable equipped contents need no Transform reads on their cold path.
        // A notified/slow child-visual update keeps the original placement while
        // preserving actual visibility and child changes for playback.
        var pose = fixedPlacement == null ? VisualReplica.Capture(item.transform, children) :
            children ? BackpackCapturePolicy.KeepPlacement(fixedPlacement, VisualReplica.Capture(item.transform, true)) : fixedPlacement;
        bool progressKnown = item.itemState == ItemState.Held && holder && holder.IsLocal;
        // Remote CharacterSyncData does not contain use-input/progress. A remote zero
        // must not masquerade as a known idle state; network events are recorded separately.
        bool primary = progressKnown && item.isUsingPrimary, secondary = progressKnown && item.isUsingSecondary;
        float progress = progressKnown ? Mathf.Clamp01(Finite(item.progress, 0)) : 0;
        int uses = Int(item.data, DataEntryKey.ItemUses, -1), cooked = Int(item.data, DataEntryKey.CookedAmount, 0);
        float fuel = Number(item.data, DataEntryKey.Fuel, -1);
        bool used = Flag(item.data, DataEntryKey.Used), lit = Flag(item.data, DataEntryKey.FlareActive) || Flag(item.data, DataEntryKey.PowerEnabled);
        bool revealBackpack = item.itemState == ItemState.InBackpack && item.backpackReference.IsSome &&
            item.backpackReference.Value.Item2.view && item.backpackReference.Value.Item2.IsOnMyBack();
        var color = Color.white;
        if (Entry(item.data, DataEntryKey.Color) is ColorItemData tint)
        {
            var c = tint.Value;
            color = new Color(Finite(c.r, 1), Finite(c.g, 1), Finite(c.b, 1), Finite(c.a, 1));
        }
        bool appearanceChanged = dirty || old == null || old.Cooked != cooked || old.Lit != lit || old.Used != used ||
            old.Color[0] != color.r || old.Color[1] != color.g || old.Color[2] != color.b || old.Color[3] != color.a;
        var visuals = suppressStableAppearance && !appearanceChanged ? old!.Visuals :
            NativeVisualAppearance.CaptureAtTime(item.transform, old?.Visuals, now, appearanceChanged, allowAnimated: animatedAppearance, followSceneClock: true);
        if (old != null && ReferenceEquals(old.IdleAnimation, nativeAnimation) && old.Key == key && old.ItemId == item.itemID && old.Prefab == identity.Prefab && old.Name == identity.Name &&
            old.HolderId == holderId && old.State == (int)item.itemState && old.Primary == primary && old.Secondary == secondary &&
            old.Progress == progress && old.ProgressKnown == progressKnown && old.Uses == uses && old.Fuel == fuel &&
            old.Cooked == cooked && old.Used == used && old.Lit == lit && old.RevealBackpackContents == revealBackpack && old.Color[0] == color.r && old.Color[1] == color.g &&
            old.Color[2] == color.b && old.Color[3] == color.a && ReferenceEquals(old.Visuals, visuals) && ReferenceEquals(old.Lights, lights) && VisualReplica.Same(old.Pose, pose)) return old;
        return new ItemFrame { Key = key, ItemId = item.itemID, Prefab = identity.Prefab, Name = identity.Name, HolderId = holderId,
            State = (int)item.itemState, Pose = pose, ProgressKnown = progressKnown, Primary = primary, Secondary = secondary,
            Progress = progress, Uses = uses, Fuel = fuel, Cooked = cooked, Used = used, Lit = lit, RevealBackpackContents = revealBackpack,
            Color = new[] { color.r, color.g, color.b, color.a }, Visuals = visuals, Lights = lights, IdleAnimation = nativeAnimation };
    }

    public static InventoryFrame[] Inventory(Character c, out bool known)
    {
        if (c == null) { known = false; return Array.Empty<InventoryFrame>(); }
        var cached = inventoryCache.GetValue(c, _ => new InventoryCache());
        var items = c.refs?.items;
        int selected = items != null && items.currentSelectedSlot.IsSome ? items.currentSelectedSlot.Value : -1;
        double now = Time.timeAsDouble;
        if (!ItemSamplingPolicy.NeedsInventory(now, cached.NextRead, inventoryRevision, cached.Revision, selected, cached.Selected))
        { known = cached.Known; return cached.Frames; }
        var values = ReadInventory(c, out known);
        bool same = values.Length == cached.Frames.Length;
        if (same) for (int i = 0; i < values.Length; i++) if (!SameSlot(values[i], cached.Frames[i])) { same = false; break; }
        if (!same) cached.Frames = values;
        cached.Known = known; cached.Revision = inventoryRevision; cached.Selected = selected; cached.NextRead = now + .5;
        return cached.Frames;
    }

    private static bool SameSlot(InventoryFrame a, InventoryFrame b) => a.Empty == b.Empty && a.Slot == b.Slot && a.ItemId == b.ItemId &&
        a.Instance == b.Instance && a.Equipped == b.Equipped && a.Backpack == b.Backpack && a.Uses == b.Uses && a.Fuel == b.Fuel &&
        a.UiFuel == b.UiFuel && a.Cooked == b.Cooked;

    private static InventoryFrame[] ReadInventory(Character c, out bool known)
    {
        known = false;
        try
        {
            if (c == null) return Array.Empty<InventoryFrame>();
            var player = c.player;
            if (player == null || player.itemSlots == null || player.itemSlots.Length < 3 || player.backpackSlot == null || player.tempFullSlot == null)
                return Array.Empty<InventoryFrame>();
            known = PhotonNetwork.IsMasterClient || ItemEventCapture.InventoryKnown(player);
            if (!known) return Array.Empty<InventoryFrame>();
            var items = c.refs?.items;
            int selected = items != null && items.currentSelectedSlot.IsSome ? items.currentSelectedSlot.Value : -1;
            var values = new List<InventoryFrame>(9);
            for (int i = 0; i < 3; i++) values.Add(Slot(player.itemSlots[i], i, false, selected));
            values.Add(Slot(player.backpackSlot, 3, false, selected));
            values.Add(Slot(player.tempFullSlot, 250, false, selected));
            if (!player.backpackSlot.IsEmpty())
            {
                var backpackVisuals = c.refs?.backpackHandler?.activeBackpackVisuals;
                int capacity = backpackVisuals != null ? Math.Max(0, Math.Min(4, backpackVisuals.slotCount)) :
                    player.backpackSlot.prefab is Backpack prefabBackpack ? Math.Max(0, Math.Min(4, prefabBackpack.slotCount)) : -1;
                // Some wearable variants have no storage. Their absent BackpackData is
                // not an unknown inventory; use the actual game's slot count, not names.
                if (capacity == 0) return values.ToArray();
                if (!(Entry(player.backpackSlot.data, DataEntryKey.BackpackData) is BackpackData bag) || bag.itemSlots == null)
                { known = false; return values.ToArray(); }
                int count = Math.Min(capacity < 0 ? 4 : capacity, bag.itemSlots.Length);
                for (int i = 0; i < count; i++) values.Add(Slot(bag.itemSlots[i], i, true, -1));
            }
            return values.ToArray();
        }
        catch { known = false; return Array.Empty<InventoryFrame>(); }
    }

    private static InventoryFrame Slot(ItemSlot? slot, int id, bool backpack, int selected)
    {
        var result = new InventoryFrame { Slot = id, Backpack = backpack, Equipped = !backpack && selected == id };
        if (slot == null || slot.IsEmpty()) return result;
        Item? prefab = slot.prefab;
        if (!prefab && slot is BackpackSlot bag) ItemDatabase.TryGetItem(bag.GetPrefabName(), out prefab);
        if (prefab == null) throw new InvalidOperationException("Nonempty inventory slot has no known prefab.");
        result.Empty = false;
        result.ItemId = prefab.itemID;
        result.Instance = slot.data != null && slot.data.guid != Guid.Empty ? slot.data.guid.ToString("N") : "";
        result.Uses = Int(slot.data, DataEntryKey.ItemUses, -1);
        result.Fuel = Number(slot.data, DataEntryKey.Fuel, -1);
        result.UiFuel = Entry(slot.data, DataEntryKey.UseRemainingPercentage) is FloatItemData uiFuel ? Finite(uiFuel.Value, -1) : -1;
        result.Cooked = Entry(slot.data, DataEntryKey.CookedAmount) is IntItemData cooked ? cooked.Value : -1;
        return result;
    }

    private static DataEntryValue? Entry(ItemInstanceData? data, DataEntryKey key) =>
        data?.data != null && data.data.TryGetValue(key, out var value) ? value : null;
    private static int Int(ItemInstanceData? data, DataEntryKey key, int fallback) => Entry(data, key) switch
    {
        IntItemData value => value.Value,
        OptionableIntItemData value when value.HasData => value.Value,
        _ => fallback,
    };
    private static float Number(ItemInstanceData? data, DataEntryKey key, float fallback) => Entry(data, key) switch
    {
        FloatItemData value => Finite(value.Value, fallback),
        IntItemData value => value.Value,
        _ => fallback,
    };
    private static bool Flag(ItemInstanceData? data, DataEntryKey key) => Entry(data, key) switch
    {
        BoolItemData value => value.Value,
        OptionableBoolItemData value => value.HasData && value.Value,
        _ => false,
    };
    private static float Finite(float value, float fallback) => float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
    private void Warn(string key, string message) { if (warned.Add(key)) warning?.Invoke(message); }
    public void Dispose()
    {
        if (ReferenceEquals(liveCapture, this)) liveCapture = null;
        foreach (var entry in tracked.Values) { entry.Frog?.Release(); VisualReplica.ReleaseCapture(entry.Transform); }
        segments?.Dispose();
        while (changed.Count > 0)
        { var identity = changed.Dequeue(); identity.Pending = identity.Urgent = false; }
        tracked.Clear(); initialPending.Clear(); registrationRetry.Clear(); segmentItems.Clear(); work.Clear(); animatedEntries.Clear(); lightEntries.Clear(); metadataEntries.Clear(); ordered = Array.Empty<Tracked>(); frames.Clear(); previousArray = Array.Empty<ItemFrame>(); warned.Clear();
    }
}
