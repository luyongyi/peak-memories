using System;
using System.Collections.Generic;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

internal static class SpawnedReplayResources
{
    internal sealed class Resource
    {
        public string Path = "", Kind = "", Name = "";
        public GameObject? Source;
        public double RetryAfter;
    }
    // Verified in local globalgamemanagers ResourceManager + resources.assets.
    // HealingPuffShroomSpawn has particles/AOE only and belongs to the VFX track.
    private static readonly Resource[] allowed =
    {
        new() { Path = "0_items/bounceshroomspawn", Name = "BounceShroomSpawn", Kind = "bounce-shroom" },
        new() { Path = "0_items/shelfshroomspawn", Name = "ShelfShroomSpawn", Kind = "shelf-shroom" },
        new() { Path = "scoutcannon_placed", Name = "ScoutCannon_Placed", Kind = "scout-cannon" },
    };
    internal static Resource? Find(string path)
    {
        foreach (var value in allowed)
            if (value.Path == path)
            {
                if (!value.Source && Time.realtimeSinceStartupAsDouble >= value.RetryAfter)
                { value.RetryAfter = Time.realtimeSinceStartupAsDouble + 1; value.Source = Resources.Load<GameObject>(value.Path); }
                return value;
            }
        return NativePlacementResources.Find(path);
    }
    internal static Resource? Match(GameObject source)
    {
        if (!source || source.GetComponent<Item>()) return null;
        string name = source.name.Replace("(Clone)", "").Trim();
        foreach (var value in allowed) if (value.Name == name) return value;
        return NativePlacementResources.Match(source);
    }
}

// Passive observers of already completed native actions, not calls to spawn,
// collide or use an item. OnPhotonInstantiate runs for remote objects too.
internal sealed class SpawnedReplayObserver : IDisposable
{
    private static readonly HashSet<SpawnedReplayObserver> listeners = new();
    private static Harmony? harmony;
    private readonly Action<GameObject, bool> birth;
    private readonly Action<GameObject> destruction;
    private readonly Action<Transform> bounce;
    private readonly Action<GameObject>? changed;
    private bool disposed;
    public SpawnedReplayObserver(Action<GameObject, bool> birth, Action<GameObject> destruction, Action<Transform> bounce,
        Action<GameObject>? changed = null)
    {
        this.birth = birth; this.destruction = destruction; this.bounce = bounce; this.changed = changed;
        if (listeners.Count == 0)
        {
            var install = new Harmony("cn.mylus.peakreplaylab.spawned." + Guid.NewGuid().ToString("N"));
            try
            {
                install.Patch(AccessTools.DeclaredMethod(typeof(Peak.PhotonCleanupHelper), "OnPhotonInstantiate"),
                    postfix: new HarmonyMethod(typeof(SpawnedReplayObserver), nameof(Born)));
                install.Patch(AccessTools.DeclaredMethod(typeof(Peak.PhotonCleanupHelper), "OnDestroy"),
                    prefix: new HarmonyMethod(typeof(SpawnedReplayObserver), nameof(Destroyed)));
                install.Patch(AccessTools.DeclaredMethod(typeof(CollisionModifier), "TriggerCharacterBouncedEvents"),
                    postfix: new HarmonyMethod(typeof(SpawnedReplayObserver), nameof(Bounced)));
                install.Patch(AccessTools.DeclaredMethod(typeof(ScoutCannon), "Awake"),
                    postfix: new HarmonyMethod(typeof(SpawnedReplayObserver), nameof(CannonBorn)));
                install.Patch(AccessTools.DeclaredMethod(typeof(ScoutCannon), "RPCA_Light"),
                    postfix: new HarmonyMethod(typeof(SpawnedReplayObserver), nameof(CannonChanged)));
                install.Patch(AccessTools.DeclaredMethod(typeof(Constructable), "AngleIt"),
                    postfix: new HarmonyMethod(typeof(SpawnedReplayObserver), nameof(CannonAngled)));
                harmony = install;
            }
            catch { install.UnpatchSelf(); throw; }
        }
        listeners.Add(this);
    }
    private static void Born(Peak.PhotonCleanupHelper __instance)
    {
        if (!__instance) return;
        if (!ReplaySafety.Active) EffectReplayCapture.ObserveNativeSpawn(__instance.gameObject);
        foreach (var listener in listeners) try { listener.birth(__instance.gameObject, true); } catch { }
    }
    private static void Destroyed(Peak.PhotonCleanupHelper __instance)
    { if (__instance) foreach (var listener in listeners) try { listener.destruction(__instance.gameObject); } catch { } }
    private static void Bounced(CollisionModifier __instance)
    { if (__instance && !ReplaySafety.Active) foreach (var listener in listeners) try { listener.bounce(__instance.transform); } catch { } }
    private static void CannonBorn(ScoutCannon __instance)
    { if (__instance) foreach (var listener in listeners) try { listener.birth(__instance.gameObject, true); } catch { } }
    private static void CannonChanged(ScoutCannon __instance) { if (__instance) Changed(__instance.gameObject); }
    private static void CannonAngled(PhotonView view) { if (view && view.GetComponent<ScoutCannon>()) Changed(view.gameObject); }
    private static void Changed(GameObject source)
    { if (!ReplaySafety.Active) foreach (var listener in listeners) try { listener.changed?.Invoke(source); } catch { } }
    public void Dispose()
    {
        if (disposed) return; disposed = true; listeners.Remove(this);
        if (listeners.Count == 0) { harmony?.UnpatchSelf(); harmony = null; }
    }
}

internal sealed class SpawnedReplayCapture : IDisposable
{
    private sealed class Entry
    {
        public GameObject Source = null!;
        public SpawnedReplayResources.Resource Resource = null!;
        public string Key = "";
        public SpawnedReplayFrame? Last;
        public double RetryAfter;
        public bool Dirty = true;
        public bool InitialUrgent;
        public bool EventOnlyAppearance;
        public double AnimationWatchSeconds = SpawnedReplayRules.AnimationWatchSeconds;
        public IDisposable? RootLifecycle;
        public CannonReplayCapture? Cannon;
        public Transform Root = null!;
        public readonly ItemActivityPolicy Activity = new();
        // All non-player periodic work is capped by the shared scene clock.
        // Idle geometry still has no poll; events retain their final checkpoint.
        public readonly SpawnedCaptureLifecycle Lifecycle = new(30, followSceneClock: true);
    }
    private readonly Dictionary<int, Entry> entries = new();
    private readonly List<int> destroyed = new();
    private readonly List<SpawnedReplayFrame> frames = new();
    private SpawnedReplayFrame[] previous = Array.Empty<SpawnedReplayFrame>();
    private readonly HashSet<string> warned = new(StringComparer.Ordinal);
    private readonly Action<string>? warning;
    private readonly SpawnedReplayObserver observer;
    private readonly NativePlacementObserver placementObserver;
    private readonly SegmentCaptureClient? segments;
    private readonly int scene = SceneManager.GetActiveScene().handle;
    private long serial;
    private bool disposed;
    public long GeometryReads { get; private set; }
    public long LightReads { get; private set; }
    public long AppearanceReads { get; private set; }
    public long SleepingGeometrySkips { get; private set; }
    internal bool InitialBaselineReady
    { get { foreach (var entry in entries.Values) if (entry.Source && entry.Last == null) return false; return true; } }
    public SpawnedReplayCapture(Action<string>? warning = null)
    {
        this.warning = warning;
        segments = CaptureSegmentScope.Active?.CreateClient(source => Register(source, false), Retire);
        try { observer = new SpawnedReplayObserver(ObservedBirth, Remove, Bounce, Changed); }
        catch { segments?.Dispose(); throw; }
        // Placement Awake/Start is also a region activation discovery. Actual
        // initialization, growth, breaking and interaction call Changed next.
        try { placementObserver = new NativePlacementObserver((source, _) => Register(source, false), Remove, Changed); }
        catch { observer.Dispose(); segments?.Dispose(); throw; }
        try
        {
            // One initialization pass only. Afterwards native birth/destruction
            // notifications maintain the directory: no periodic scene search.
            foreach (var source in Object.FindObjectsByType<Peak.PhotonCleanupHelper>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (source) Register(source.gameObject, false);
            foreach (var source in Object.FindObjectsByType<ScoutCannon>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (source) Register(source.gameObject, false);
            foreach (var source in NativePlacementResources.InitialSources()) if (source) Register(source, false);
        }
        catch { Dispose(); throw; }
    }
    private void ObservedBirth(GameObject source, bool born)
    {
        if (!source) return;
        // Cannon Awake can come from map activation; Photon instantiations of
        // short-lived player-created terrain keep their immediate admission.
        bool cannon = source.name.StartsWith("ScoutCannon_Placed", StringComparison.Ordinal);
        Register(source, born && !cannon);
    }
    private void Register(GameObject source, bool born)
    {
        if (disposed || ReplaySafety.Active || !source || source.scene.handle != scene) return;
        if (!(segments?.Admit(source, born) ?? true)) return;
        if (entries.TryGetValue(source.GetInstanceID(), out var existing))
        {
            if (existing.Source == source) return;
            entries.Remove(source.GetInstanceID()); // Unity may reuse a destroyed instance ID.
        }
        var resource = SpawnedReplayResources.Match(source); if (resource == null) return;
        if (entries.Count >= SpawnedReplayRules.MaximumEntities) { Warn("limit", "生成地形超过256个，超出部分未录制。"); return; }
        var entry = new Entry { Source = source, Root = source.transform, Resource = resource,
            Key = "spawn:" + source.GetInstanceID() + ":" + (++serial),
            InitialUrgent = born,
            EventOnlyAppearance = NativeStaticVisualCapability.SpawnedEventOnly(source, resource.Kind),
            Cannon = resource.Kind == "scout-cannon" ? new CannonReplayCapture(source.GetComponent<ScoutCannon>()) : null };
        entries.Add(source.GetInstanceID(), entry);
        if (entry.EventOnlyAppearance)
        {
            entry.AnimationWatchSeconds = NativeStaticVisualCapability.BindAnimationWindow(source, out bool advancing);
            if (advancing) entry.Lifecycle.NativeEvent(Time.timeAsDouble, entry.AnimationWatchSeconds);
        }
        entry.RootLifecycle = CaptureRootLifecycleObserver.Bind(source, () => Changed(source));
        if (born) entry.Lifecycle.NativeEvent(Time.timeAsDouble, entry.AnimationWatchSeconds);
    }
    private void Retire(int id)
    {
        if (!entries.TryGetValue(id, out var entry)) return;
        entry.RootLifecycle?.Dispose();
        VisualReplica.ReleaseCapture(entry.Root);
        NativePlacementResources.InvalidateCapture(entry.Source);
        entries.Remove(id); previous = Array.Empty<SpawnedReplayFrame>();
    }
    private void Remove(GameObject source)
    { if (source) { int id = source.GetInstanceID(); segments?.Forget(id); Retire(id); } }
    private void Changed(GameObject source)
    {
        if (!source || !(segments?.Admit(source, true) ?? true)) return;
        NativePlacementResources.InvalidateCapture(source);
        Register(source, true);
        if (entries.TryGetValue(source.GetInstanceID(), out var entry))
        { entry.Dirty = true; entry.InitialUrgent = true; entry.RetryAfter = 0; entry.Lifecycle.NativeEvent(Time.timeAsDouble, entry.AnimationWatchSeconds); entry.Cannon?.Changed(); }
    }
    private void Bounce(Transform source)
    {
        for (var node = source; node; node = node.parent)
        {
            int id = node.gameObject.GetInstanceID();
            if (!entries.TryGetValue(id, out var entry))
            {
                // A real bounce may precede its budgeted region baseline. The
                // cheap name/type match avoids registering every ancestor as a
                // candidate; Register still gates all rig/appearance binding.
                if (SpawnedReplayResources.Match(node.gameObject) == null) continue;
                Register(node.gameObject, true);
                if (!entries.TryGetValue(id, out entry)) return;
            }
            if (entry != null)
            { entry.Dirty = true; entry.InitialUrgent = true; entry.RetryAfter = 0; entry.Lifecycle.NativeEvent(Time.timeAsDouble, entry.AnimationWatchSeconds); return; }
        }
    }
    public SpawnedReplayFrame[] Capture(double now)
    {
        if (disposed) throw new ObjectDisposedException(nameof(SpawnedReplayCapture));
        frames.Clear(); destroyed.Clear();
        foreach (var pair in entries)
        {
            var entry = pair.Value;
            if (!entry.Source) { destroyed.Add(pair.Key); continue; }
            if (now < entry.RetryAfter) { if (entry.Last != null) frames.Add(entry.Last); continue; }
            if (CaptureBootstrapWorkBudget.InitialOnly && entry.Last != null && !entry.InitialUrgent && !entry.Dirty &&
                !entry.Lifecycle.IsObserving(now, false))
            { frames.Add(entry.Last); continue; }
            if (entry.Last == null && !entry.InitialUrgent && !CaptureBootstrapWorkBudget.TryBeginRead()) continue;
            entry.InitialUrgent = false;
            try { frames.Add(Sample(entry, now)); }
            catch (Exception e)
            {
                // An observer failure is not a native despawn. Keep the last known
                // state until recovery or an actual destruction notification.
                if (entry.Last != null) frames.Add(entry.Last);
                entry.RetryAfter = now + 1; Warn(entry.Resource.Path, "生成地形采集暂不可用：" + e.Message);
            }
        }
        foreach (int id in destroyed) { segments?.Forget(id); Retire(id); }
        bool same = frames.Count == previous.Length;
        if (same) for (int i = 0; i < frames.Count; i++) if (!ReferenceEquals(frames[i], previous[i])) { same = false; break; }
        return same ? previous : previous = frames.ToArray();
    }
    private SpawnedReplayFrame Sample(Entry entry, double now)
    {
        // Capability was bound once at admission. A constant Light/material is
        // event-only; unknown controllers keep their independent 30 Hz reader.
        bool visibilityChanged = !entry.EventOnlyAppearance && entry.Last != null && entry.Last.Pose.Active != entry.Source.activeInHierarchy;
        bool animating = !entry.EventOnlyAppearance && NativePlacementResources.IsAnimating(entry.Source, now, entry.Dirty, followSceneClock: true);
        bool animatedAppearance = entry.Lifecycle.IsObserving(now, animating);
        bool geometryDue = entry.Dirty || entry.Lifecycle.ShouldRead(now, entry.Last != null, visibilityChanged, animating);
        var lights = entry.Last?.Lights;
        bool lightDue = lights == null || entry.Dirty || (entry.EventOnlyAppearance ? geometryDue : lights.Length > 0);
        if (lightDue && entry.Activity.LightDue(now, entry.Dirty, followSceneClock: true))
        {
            LightReads++;
            lights = NativeLightAppearance.Capture(entry.Root, lights);
            entry.Activity.LightObserved(now);
        }
        if (entry.Cannon != null)
        {
            var value = entry.Cannon.Sample(entry.Key, entry.Resource.Path, now, entry.Dirty);
            var visuals = NativeVisualAppearance.CaptureAtTime(entry.Source.transform, entry.Last?.Visuals, now, entry.Dirty, allowAnimated: true, followSceneClock: true);
            entry.Dirty = false;
            if (entry.Last != null && ReferenceEquals(entry.Last.Pose, value.Pose) && ReferenceEquals(entry.Last.Animation, value.Animation) && ReferenceEquals(entry.Last.Visuals, visuals) && ReferenceEquals(entry.Last.Lights, lights)) return entry.Last;
            return entry.Last = new SpawnedReplayFrame { Key = value.Key, Resource = value.Resource, Kind = value.Kind, Pose = value.Pose, Animation = value.Animation, Visuals = visuals, Lights = lights };
        }
        bool appearanceDue = !entry.EventOnlyAppearance && entry.Last != null && NativeVisualAppearance.NeedsCaptureAtTime(entry.Root, entry.Last.Visuals, now, entry.Last.Pose.Active, animatedAppearance, followSceneClock: true);
        if (!geometryDue && !appearanceDue && entry.Last != null)
        {
            SleepingGeometrySkips++;
            if (ReferenceEquals(entry.Last.Lights, lights)) return entry.Last;
            return entry.Last = new SpawnedReplayFrame { Key = entry.Last.Key, Resource = entry.Last.Resource, Kind = entry.Last.Kind,
                Pose = entry.Last.Pose, Animation = entry.Last.Animation, Visuals = entry.Last.Visuals, Lights = lights };
        }
        // The real short spawn/bounce result includes Unity's native transitions.
        // No controller, collision handler or rerandomized spawn is replayed.
        var pose = entry.Last?.Pose;
        if (geometryDue)
        { GeometryReads++; pose = VisualReplica.Capture(entry.Root, true); entry.Lifecycle.Observed(now, animating); }
        var appearance = entry.Last?.Visuals;
        if (entry.Last == null || entry.Dirty || visibilityChanged || appearanceDue || animatedAppearance || entry.EventOnlyAppearance && geometryDue)
        {
            AppearanceReads++;
            appearance = entry.EventOnlyAppearance ? NativeVisualAppearance.Capture(entry.Root, appearance) :
                NativeVisualAppearance.CaptureAtTime(entry.Root, appearance, now, entry.Dirty || visibilityChanged, allowAnimated: animatedAppearance, followSceneClock: true);
        }
        entry.Dirty = false;
        if (entry.Last != null && ReferenceEquals(entry.Last.Pose, pose) && ReferenceEquals(entry.Last.Visuals, appearance) && ReferenceEquals(entry.Last.Lights, lights)) return entry.Last;
        return entry.Last = new SpawnedReplayFrame { Key = entry.Key, Resource = entry.Resource.Path, Kind = entry.Resource.Kind, Pose = pose!, Visuals = appearance, Lights = lights };
    }
    private void Warn(string key, string message) { if (warned.Count < 128 && warned.Add(key)) try { warning?.Invoke(message); } catch { } }
    public void Dispose()
    {
        if (disposed) return; disposed = true; placementObserver.Dispose(); observer.Dispose(); segments?.Dispose();
        foreach (var entry in entries.Values) { entry.RootLifecycle?.Dispose(); VisualReplica.ReleaseCapture(entry.Root); NativePlacementResources.InvalidateCapture(entry.Source); }
        entries.Clear(); frames.Clear(); destroyed.Clear(); previous = Array.Empty<SpawnedReplayFrame>();
    }
}
