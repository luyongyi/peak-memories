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
        public double WatchUntil, NextProbe, RetryAfter;
        public bool Dirty = true;
        public CannonReplayCapture? Cannon;
    }
    private readonly Dictionary<int, Entry> entries = new();
    private readonly List<int> destroyed = new();
    private readonly List<SpawnedReplayFrame> frames = new();
    private SpawnedReplayFrame[] previous = Array.Empty<SpawnedReplayFrame>();
    private readonly HashSet<string> warned = new(StringComparer.Ordinal);
    private readonly Action<string>? warning;
    private readonly SpawnedReplayObserver observer;
    private readonly NativePlacementObserver placementObserver;
    private readonly int scene = SceneManager.GetActiveScene().handle;
    private long serial;
    private bool disposed;
    public SpawnedReplayCapture(Action<string>? warning = null)
    {
        this.warning = warning;
        observer = new SpawnedReplayObserver(Register, Remove, Bounce, Changed);
        try { placementObserver = new NativePlacementObserver(Register, Remove, Changed); }
        catch { observer.Dispose(); throw; }
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
        catch { placementObserver.Dispose(); observer.Dispose(); throw; }
    }
    private void Register(GameObject source, bool born)
    {
        if (disposed || ReplaySafety.Active || !source || source.scene.handle != scene) return;
        if (entries.TryGetValue(source.GetInstanceID(), out var existing))
        {
            if (existing.Source == source) return;
            entries.Remove(source.GetInstanceID()); // Unity may reuse a destroyed instance ID.
        }
        var resource = SpawnedReplayResources.Match(source); if (resource == null) return;
        if (entries.Count >= SpawnedReplayRules.MaximumEntities) { Warn("limit", "生成地形超过256个，超出部分未录制。"); return; }
        entries.Add(source.GetInstanceID(), new Entry { Source = source, Resource = resource,
            Key = "spawn:" + source.GetInstanceID() + ":" + (++serial),
            WatchUntil = born ? Time.timeAsDouble + SpawnedReplayRules.AnimationWatchSeconds : double.NegativeInfinity,
            Cannon = resource.Kind == "scout-cannon" ? new CannonReplayCapture(source.GetComponent<ScoutCannon>()) : null });
    }
    private void Remove(GameObject source) { if (source) entries.Remove(source.GetInstanceID()); }
    private void Changed(GameObject source)
    {
        NativePlacementResources.InvalidateCapture(source);
        Register(source, false);
        if (entries.TryGetValue(source.GetInstanceID(), out var entry))
        { entry.Dirty = true; entry.RetryAfter = 0; entry.Cannon?.Changed(); }
    }
    private void Bounce(Transform source)
    {
        for (var node = source; node; node = node.parent)
            if (entries.TryGetValue(node.gameObject.GetInstanceID(), out var entry))
            { entry.Dirty = true; entry.RetryAfter = 0; entry.WatchUntil = Time.timeAsDouble + SpawnedReplayRules.AnimationWatchSeconds; return; }
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
            try { frames.Add(Sample(entry, now)); }
            catch (Exception e)
            {
                // An observer failure is not a native despawn. Keep the last known
                // state until recovery or an actual destruction notification.
                if (entry.Last != null) frames.Add(entry.Last);
                entry.RetryAfter = now + 1; Warn(entry.Resource.Path, "生成地形采集暂不可用：" + e.Message);
            }
        }
        foreach (int id in destroyed) entries.Remove(id);
        bool same = frames.Count == previous.Length;
        if (same) for (int i = 0; i < frames.Count; i++) if (!ReferenceEquals(frames[i], previous[i])) { same = false; break; }
        return same ? previous : previous = frames.ToArray();
    }
    private static SpawnedReplayFrame Sample(Entry entry, double now)
    {
        // Light controllers may flicker while an otherwise static model is
        // sleeping. Sample light values at recorder cadence independently of
        // the cheaper geometry probe and retain the immutable array when equal.
        var lights = NativeLightAppearance.Capture(entry.Source.transform, entry.Last?.Lights);
        if (entry.Cannon != null)
        {
            var value = entry.Cannon.Sample(entry.Key, entry.Resource.Path, now, entry.Dirty);
            var visuals = NativeVisualAppearance.CaptureAtTime(entry.Source.transform, entry.Last?.Visuals, now, entry.Dirty, allowAnimated: true);
            entry.Dirty = false;
            if (entry.Last != null && ReferenceEquals(entry.Last.Pose, value.Pose) && ReferenceEquals(entry.Last.Animation, value.Animation) && ReferenceEquals(entry.Last.Visuals, visuals) && ReferenceEquals(entry.Last.Lights, lights)) return entry.Last;
            return entry.Last = new SpawnedReplayFrame { Key = value.Key, Resource = value.Resource, Kind = value.Kind, Pose = value.Pose, Animation = value.Animation, Visuals = visuals, Lights = lights };
        }
        bool visibilityChanged = entry.Last != null && entry.Last.Pose.Active != entry.Source.activeInHierarchy;
        bool animating = NativePlacementResources.IsAnimating(entry.Source);
        bool animatedAppearance = animating || now <= entry.WatchUntil;
        bool appearanceDue = entry.Last != null && NativeVisualAppearance.NeedsCaptureAtTime(entry.Source.transform, entry.Last.Visuals, now, entry.Last.Pose.Active, animatedAppearance);
        if (entry.Last != null && !visibilityChanged && !animating && !appearanceDue && !SpawnedReplayRules.ShouldProbe(now, entry.NextProbe, entry.WatchUntil, entry.Dirty))
        {
            if (ReferenceEquals(entry.Last.Lights, lights)) return entry.Last;
            return entry.Last = new SpawnedReplayFrame { Key = entry.Last.Key, Resource = entry.Last.Resource, Kind = entry.Last.Kind,
                Pose = entry.Last.Pose, Animation = entry.Last.Animation, Visuals = entry.Last.Visuals, Lights = lights };
        }
        // The real short spawn/bounce result includes Unity's native transitions.
        // No controller, collision handler or rerandomized spawn is replayed.
        var pose = VisualReplica.Capture(entry.Source.transform, entry.Last == null || entry.Dirty || visibilityChanged || animating || now <= entry.WatchUntil);
        var appearance = NativeVisualAppearance.CaptureAtTime(entry.Source.transform, entry.Last?.Visuals, now, entry.Dirty || visibilityChanged, allowAnimated: animatedAppearance);
        entry.NextProbe = now + .5 + (entry.Source.GetInstanceID() & 3) * .03;
        entry.Dirty = false;
        if (entry.Last != null && ReferenceEquals(entry.Last.Pose, pose) && ReferenceEquals(entry.Last.Visuals, appearance) && ReferenceEquals(entry.Last.Lights, lights)) return entry.Last;
        return entry.Last = new SpawnedReplayFrame { Key = entry.Key, Resource = entry.Resource.Path, Kind = entry.Resource.Kind, Pose = pose, Visuals = appearance, Lights = lights };
    }
    private void Warn(string key, string message) { if (warned.Count < 128 && warned.Add(key)) try { warning?.Invoke(message); } catch { } }
    public void Dispose()
    { if (disposed) return; disposed = true; placementObserver.Dispose(); observer.Dispose(); entries.Clear(); frames.Clear(); destroyed.Clear(); previous = Array.Empty<SpawnedReplayFrame>(); }
}

internal sealed class SpawnedReplayPlayback : IDisposable
{
    private sealed class Entry
    {
        public string Resource = "", Kind = "";
        public VisualReplica Visual = null!;
        public CannonReplayVisual? Cannon;
        public NativeVisualAppearance.Playback? Appearance;
        public NativeLightAppearance.Playback? Lights;
        public void Dispose() { Cannon?.Dispose(); Lights?.Dispose(); Appearance?.Dispose(); Visual.Dispose(); }
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
    public SpawnedReplayPlayback(Action<string>? warning = null)
    {
        this.warning = warning;
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
    public void Enforce()
    {
        if (disposed || enforcedFrame == Time.frameCount) return; enforcedFrame = Time.frameCount;
        foreach (var pair in hidden) if (pair.Key && !pair.Key.forceRenderingOff) pair.Key.forceRenderingOff = true;
        originalLights.Enforce();
    }
    public void Apply(SpawnedReplayFrame[] left, SpawnedReplayFrame[] right, float mix, double time, bool discontinuity)
    {
        if (disposed) throw new ObjectDisposedException(nameof(SpawnedReplayPlayback));
        serial++; MissingCount = 0; VisibleCount = 0; current.Clear(); following.Clear();
        foreach (var frame in right) following[frame.Key] = frame;
        foreach (var frame in left) current.Add(frame.Key);
        // Reclaim obsolete seek checkpoints before creating this cursor's set.
        // Otherwise 256 old + 24 sleeping entries could block valid new objects.
        sleeping.Clear();
        foreach (var pair in entries) if (!current.Contains(pair.Key)) { pair.Value.Visual.Hide(); sleeping.Add(pair); }
        sleeping.Sort((a, b) => a.Value.Seen.CompareTo(b.Value.Seen));
        for (int i = 0; i < sleeping.Count - 24; i++) { sleeping[i].Value.Dispose(); entries.Remove(sleeping[i].Key); }
        double now = Time.realtimeSinceStartupAsDouble;
        foreach (var frame in left)
        {
            if (retry.TryGetValue(frame.Key, out double after) && now < after) { MissingCount++; continue; }
            entries.TryGetValue(frame.Key, out var entry);
            try
            {
                if (entry != null && (entry.Resource != frame.Resource || entry.Kind != frame.Kind))
                { entry.Dispose(); entries.Remove(frame.Key); entry = null; }
                if (entry == null)
                {
                    var source = SpawnedReplayResources.Find(frame.Resource);
                    if (source == null || source.Kind != frame.Kind || !source.Source) throw new InvalidOperationException("Native spawned terrain resource unavailable: " + frame.Resource);
                    if (entries.Count >= SpawnedReplayRules.MaximumEntities + 24) throw new InvalidOperationException("Spawned terrain visual pool is full.");
                    entry = new Entry { Resource = frame.Resource, Kind = frame.Kind, Visual = new VisualReplica(source.Source!, true) };
                    try
                    {
                        if (frame.Kind == "scout-cannon") entry.Cannon = new CannonReplayVisual(source.Source!, entry.Visual);
                        entry.Appearance = new NativeVisualAppearance.Playback(entry.Visual, source.Source!);
                        entry.Lights = new NativeLightAppearance.Playback(entry.Visual, source.Source!);
                        entry.Visual.Root.name = "PEAK Replay Spawned - " + source.Name; entries.Add(frame.Key, entry);
                    }
                    catch { entry.Dispose(); entry = null; throw; }
                }
                following.TryGetValue(frame.Key, out var next);
                if (discontinuity) entry.Visual.InvalidatePose();
                if (entry.Cannon != null) entry.Cannon.Apply(frame, next, mix, time, discontinuity);
                else entry.Visual.Apply(frame.Pose, next != null && SpawnedReplayRules.CanInterpolate(frame, next) ? next.Pose : null, mix);
                entry.Appearance?.Apply(frame.Visuals);
                entry.Lights?.Apply(frame.Lights);
                entry.Seen = serial; retry.Remove(frame.Key);
                if (frame.Pose.Active) VisibleCount++;
            }
            catch (Exception e)
            {
                entry?.Visual.Hide(); MissingCount++; retry[frame.Key] = now + 1;
                if (warned.Count < 128 && warned.Add(frame.Resource)) try { warning?.Invoke("生成地形缺失：" + e.Message); } catch { }
            }
        }
        sleeping.Clear();
        foreach (var pair in entries) if (pair.Value.Seen != serial) { pair.Value.Visual.Hide(); sleeping.Add(pair); }
        sleeping.Sort((a, b) => a.Value.Seen.CompareTo(b.Value.Seen));
        for (int i = 0; i < sleeping.Count - 24; i++) { sleeping[i].Value.Dispose(); entries.Remove(sleeping[i].Key); }
        removed.Clear(); foreach (var pair in retry) if (!current.Contains(pair.Key)) removed.Add(pair.Key);
        foreach (string key in removed) retry.Remove(key);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; placementObserver.Dispose(); observer.Dispose();
        foreach (var pair in hidden) try { if (pair.Key) pair.Key.forceRenderingOff = pair.Value; } catch { }
        originalLights.Dispose();
        foreach (var entry in entries.Values) try { entry.Dispose(); } catch { }
        hidden.Clear(); entries.Clear(); retry.Clear();
    }
}
