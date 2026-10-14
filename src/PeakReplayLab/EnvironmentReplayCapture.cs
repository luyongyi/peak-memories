using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakReplayLab;

internal static class EnvironmentReplayCapture
{
    private sealed class Wind
    {
        public WindChillZone Source = null!;
        public int ObjectId;
        public string Key = "";
        public (StormVisual Source, string Key, int SourceId)[] Storms = Array.Empty<(StormVisual, string, int)>();
        public EnvironmentWindFrame? Previous;
    }
    private sealed class Lava
    {
        public MonoBehaviour Source = null!;
        public Transform Target = null!;
        public int SourceId, TargetId;
        public string Key = "";
        public int Kind;
        public EnvironmentLavaFrame? Previous;
    }
    private sealed class Fog
    {
        public OrbFogHandler Source = null!;
        public int ObjectId;
        public FogSphere? Sphere;
        public string Key = "";
    }
    private sealed class LavaCandidate
    {
        public MonoBehaviour Source = null!;
        public Transform Target = null!;
        public int SourceId, TargetId, Kind;
    }
    private sealed class StormCandidate
    {
        public StormVisual Source = null!;
        public WindChillZone Owner = null!;
        public int SourceId, ObjectId;
        public string Key = "";
        public bool Admitted;
    }
    private static readonly AccessTools.FieldRef<WindChillZone, Vector3> direction = AccessTools.FieldRefAccess<WindChillZone, Vector3>("currentWindDirection");
    private static readonly AccessTools.FieldRef<WindChillZone, float> until = AccessTools.FieldRefAccess<WindChillZone, float>("untilSwitch");
    private static readonly AccessTools.FieldRef<WindChillZone, float> duration = AccessTools.FieldRefAccess<WindChillZone, float>("timeUntilNextWind");
    private static readonly AccessTools.FieldRef<WindChillZone, float> activeFor = AccessTools.FieldRefAccess<WindChillZone, float>("hasBeenActiveFor");
    private static readonly AccessTools.FieldRef<StormVisual, float> stormIntensity = AccessTools.FieldRefAccess<StormVisual, float>("windIntensity");
    private static readonly AccessTools.FieldRef<MovingLava, bool> moving = AccessTools.FieldRefAccess<MovingLava, bool>("timeToMove");
    private static readonly List<Wind> winds = new();
    private static readonly List<Lava> lava = new();
    private static readonly List<Fog> fogs = new();
    private static readonly Queue<(int Id, Component Source)> pending = new();
    private static readonly HashSet<int> pendingIds = new();
    private static EnvironmentReplayObserver? observer;
    private static SegmentCaptureClient? windSegments, lavaSegments, fogSegments, stormSegments;
    private static readonly Dictionary<int, LavaCandidate> lavaCandidates = new();
    private static readonly Dictionary<int, StormCandidate> stormCandidates = new();
    private static readonly List<LavaCandidate> lavaAdmission = new();
    private static readonly List<StormCandidate> stormAdmission = new();
    private static readonly List<int> deadCandidates = new();
    private static bool overflow;
    private static OrbFogHandler? orb;
    private static FogSphere? sphere;
    private static string sphereKey = "";
    private static EnvironmentReplayFrame? previous;
    private static int scene = -1;
    private static readonly EnvironmentDiscoverySchedule discovery = new();
    private static readonly EnvironmentSampleClock sampling = new();
    private static readonly int weatherId = Shader.PropertyToID("_WeatherBlend"), windId = Shader.PropertyToID("GlobalWind"),
        heightId = Shader.PropertyToID("HeightFogAmount"), spiritId = Shader.PropertyToID("HeightFogSpirit");
    // Immutable pure data only. Failure reporting can retain the rejected native
    // observation before Reset; no serialization or logging runs in Sample.
    internal static EnvironmentReplayFrame? LastObservation => previous;
    internal static bool InitialBaselineReady
    {
        get
        {
            if (scene != SceneManager.GetActiveScene().handle || previous == null || overflow || pending.Count != 0) return false;
            foreach (var value in winds) if (value.Source && value.Previous == null) return false;
            foreach (var value in lava) if (value.Source && value.Target && value.Previous == null) return false;
            return previous.Winds.Length == winds.Count && previous.Lava.Length == lava.Count;
        }
    }

    public static void Reset()
    {
        observer?.Dispose(); observer = null; scene = -1; winds.Clear(); lava.Clear(); fogs.Clear();
        windSegments?.Dispose(); lavaSegments?.Dispose(); fogSegments?.Dispose(); stormSegments?.Dispose();
        windSegments = lavaSegments = fogSegments = stormSegments = null; lavaCandidates.Clear(); stormCandidates.Clear();
        lavaAdmission.Clear(); stormAdmission.Clear(); deadCandidates.Clear();
        pending.Clear(); pendingIds.Clear(); overflow = false;
        orb = null; sphere = null; sphereKey = ""; previous = null; discovery.Reset(); sampling.Reset();
    }
    // Component type disambiguates a zone and its visual sharing one native
    // GameObject; runtime instance IDs are deliberately never persisted.
    internal static string Key(Component source) => source.GetType().FullName + ":" + WorldTrack.Path(source.transform);

    private static void Changed(Component source)
    {
        if (!source || source.gameObject.scene.handle != scene) return;
        int id = source.GetInstanceID(); if (pendingIds.Contains(id)) return;
        // More lifecycle objects than all supported environment families is a
        // visible recording failure, not a silently dropped birth.
        if (pendingIds.Count >= 256) { overflow = true; return; }
        pendingIds.Add(id); pending.Enqueue((id, source));
    }
    private static bool Discover(double now)
    {
        int current = SceneManager.GetActiveScene().handle;
        bool initial = scene != current;
        if (initial)
        {
            Reset(); scene = current;
            var scope = CaptureSegmentScope.Active;
            windSegments = scope?.CreateClient(AdmitWind, RetireWind);
            lavaSegments = scope?.CreateClient(AdmitLava, RetireLava);
            fogSegments = scope?.CreateClient(AdmitFog, RetireFog);
            stormSegments = scope?.CreateClient(AdmitStorm, RetireStorm);
            observer = new EnvironmentReplayObserver(Changed);
            using var timing = ReplayPerformance.Measure(ReplayStage.Discovery);
            foreach (var source in UnityEngine.Object.FindObjectsByType<WindChillZone>(FindObjectsInactive.Include, FindObjectsSortMode.None)) RegisterWind(source);
            foreach (var source in UnityEngine.Object.FindObjectsByType<StormVisual>(FindObjectsInactive.Include, FindObjectsSortMode.None)) RegisterStorm(source);
            foreach (var source in UnityEngine.Object.FindObjectsByType<LavaRising>(FindObjectsInactive.Include, FindObjectsSortMode.None)) RegisterRising(source);
            foreach (var source in UnityEngine.Object.FindObjectsByType<MovingLava>(FindObjectsInactive.Include, FindObjectsSortMode.None)) RegisterLava(source, source.transform, 1);
            foreach (var source in UnityEngine.Object.FindObjectsByType<OrbFogHandler>(FindObjectsInactive.Include, FindObjectsSortMode.None)) RegisterFog(source);
        }
        if (overflow) throw new InvalidOperationException("Native environment lifecycle directory exceeds its limit.");
        PruneDestroyed();
        if (pending.Count != 0)
        {
            using var timing = ReplayPerformance.Measure(ReplayStage.Discovery);
            while (pending.Count != 0)
            {
                var signal = pending.Dequeue(); pendingIds.Remove(signal.Id);
                var source = signal.Source; if (!source || source.gameObject.scene.handle != scene) continue;
                // These callbacks are Awake/Start discoveries, not gameplay
                // actions. Map activation must not bypass the baseline budget.
                if (source is WindChillZone wind) RegisterWind(wind);
                else if (source is StormVisual storm) RegisterStorm(storm);
                else if (source is LavaRising rising) RegisterRising(rising);
                else if (source is MovingLava movingLava) RegisterLava(movingLava, movingLava.transform, 1);
                else if (source is OrbFogHandler fog) RegisterFog(fog);
                else if (source is FogSphere fogSphere) { var owner = fogSphere.GetComponentInParent<OrbFogHandler>(true); if (owner) RegisterFog(owner); }
            }
        }
        Fog? selected = null;
        foreach (var candidate in fogs)
        {
            if (selected == null) selected = candidate;
            if (candidate.Source.gameObject.activeInHierarchy) { selected = candidate; break; }
        }
        if (orb != selected?.Source || sphere != selected?.Sphere) discovery.Changed(EnvironmentDirectory.Fog);
        orb = selected?.Source; sphere = selected?.Sphere; sphereKey = sphere ? selected!.Key : "";
        return discovery.Take(current, now, EnvironmentDirectory.None) != EnvironmentDirectory.None;
    }
    private static void PruneDestroyed()
    {
        // Unexpected destroys have no reliable callback on several native
        // types. Inspect only the bounded, already cached directory each tick;
        // removing one dead entry never launches a whole-scene search.
        for (int i = winds.Count - 1; i >= 0; i--)
        {
            if (i >= winds.Count) continue;
            var wind = winds[i];
            if (!wind.Source)
            { RemoveStormsOwnedBy(wind.Source); windSegments?.Forget(wind.ObjectId); RetireWind(wind.ObjectId); continue; }
            bool changed = false;
            foreach (var storm in wind.Storms)
                if (!storm.Source) { RemoveStorm(storm.SourceId); changed = true; }
            if (changed) RebuildStorms(wind);
        }
        for (int i = lava.Count - 1; i >= 0; i--)
            if (!lava[i].Source || !lava[i].Target) RemoveLavaSource(lava[i].SourceId, forgetCandidate: true);
        for (int i = fogs.Count - 1; i >= 0; i--)
            if (i < fogs.Count && !fogs[i].Source) { fogSegments?.Forget(fogs[i].ObjectId); RetireFog(fogs[i].ObjectId); }
    }

    private static void AdmitWind(GameObject source)
    { foreach (var wind in source.GetComponents<WindChillZone>()) if (wind) RegisterWind(wind); }
    private static void RetireWind(int objectId)
    {
        for (int i = winds.Count - 1; i >= 0; i--)
            if (winds[i].ObjectId == objectId)
            { winds[i].Previous = null; winds[i].Storms = Array.Empty<(StormVisual, string, int)>(); winds.RemoveAt(i); discovery.Changed(EnvironmentDirectory.Winds); }
    }
    private static void AdmitLava(GameObject source)
    {
        int id = source.GetInstanceID();
        lavaAdmission.Clear();
        foreach (var candidate in lavaCandidates.Values) if (candidate.TargetId == id) lavaAdmission.Add(candidate);
        // Register may retarget/remove a descriptor. Never enumerate the live
        // dictionary while callbacks change it, or replay an old queued target.
        foreach (var candidate in lavaAdmission)
        {
            if (!candidate.Source) { RemoveLavaSource(candidate.SourceId, true); continue; }
            if (candidate.Source is LavaRising rising) RegisterRising(rising);
            else RegisterLava(candidate.Source, candidate.Source.transform, candidate.Kind);
        }
        lavaAdmission.Clear();
    }
    private static void RetireLava(int objectId)
    {
        for (int i = lava.Count - 1; i >= 0; i--)
            if (lava[i].TargetId == objectId)
            { discovery.Changed(lava[i].Kind == 0 ? EnvironmentDirectory.RisingLava : EnvironmentDirectory.MovingLava); lava[i].Previous = null; lava.RemoveAt(i); }
        deadCandidates.Clear();
        foreach (var candidate in lavaCandidates.Values)
            if (candidate.TargetId == objectId && (!candidate.Source || !candidate.Target)) deadCandidates.Add(candidate.SourceId);
        foreach (int id in deadCandidates) lavaCandidates.Remove(id);
        deadCandidates.Clear();
    }
    private static void AdmitFog(GameObject source)
    { foreach (var fog in source.GetComponents<OrbFogHandler>()) if (fog) RegisterFog(fog); }
    private static void RetireFog(int objectId)
    {
        for (int i = fogs.Count - 1; i >= 0; i--)
            if (fogs[i].ObjectId == objectId)
            { fogs.RemoveAt(i); discovery.Changed(EnvironmentDirectory.Fog); }
    }

    private static void AdmitStorm(GameObject source)
    {
        int id = source.GetInstanceID(); stormAdmission.Clear();
        foreach (var candidate in stormCandidates.Values) if (candidate.ObjectId == id) stormAdmission.Add(candidate);
        foreach (var candidate in stormAdmission)
        {
            if (!candidate.Source || !candidate.Owner) { RemoveStorm(candidate.SourceId); continue; }
            RegisterStorm(candidate.Source, candidate.Owner);
        }
        stormAdmission.Clear();
    }
    private static void RetireStorm(int objectId)
    {
        bool changed = false; deadCandidates.Clear();
        foreach (var candidate in stormCandidates.Values)
        {
            if (candidate.ObjectId != objectId) continue;
            changed |= candidate.Admitted; candidate.Admitted = false; candidate.Key = "";
            if (!candidate.Source || !candidate.Owner) deadCandidates.Add(candidate.SourceId);
        }
        foreach (int id in deadCandidates) stormCandidates.Remove(id);
        deadCandidates.Clear();
        if (changed) foreach (var wind in winds) RebuildStorms(wind);
    }
    private static void RemoveStorm(int sourceId)
    {
        if (!stormCandidates.TryGetValue(sourceId, out var candidate)) return;
        stormCandidates.Remove(sourceId);
        bool remaining = false;
        foreach (var other in stormCandidates.Values) if (other.ObjectId == candidate.ObjectId) { remaining = true; break; }
        if (!remaining) stormSegments?.Forget(candidate.ObjectId);
    }
    private static void RemoveStormsOwnedBy(WindChillZone? owner)
    {
        deadCandidates.Clear();
        foreach (var candidate in stormCandidates.Values)
            if (ReferenceEquals(candidate.Owner, owner)) deadCandidates.Add(candidate.SourceId);
        foreach (int id in deadCandidates) RemoveStorm(id);
        deadCandidates.Clear();
    }
    private static void RegisterStorm(StormVisual source, WindChillZone? knownOwner = null)
    {
        if (!source || source.gameObject.scene.handle != scene) return;
        var owner = knownOwner ? knownOwner : source.GetComponentInParent<WindChillZone>(true);
        if (!owner) return;
        int id = source.GetInstanceID();
        if (stormCandidates.TryGetValue(id, out var replaced) && !ReferenceEquals(replaced.Source, source)) RemoveStorm(id);
        if (!stormCandidates.TryGetValue(id, out var candidate))
        {
            candidate = new StormCandidate { Source = source, SourceId = id, ObjectId = source.gameObject.GetInstanceID(), Owner = owner };
            stormCandidates.Add(id, candidate);
        }
        else if (candidate.Owner != owner)
        {
            candidate.Owner = owner; candidate.Admitted = false; candidate.Key = "";
            foreach (var wind in winds) RebuildStorms(wind);
        }
        if (stormSegments != null && !stormSegments.Admit(source.gameObject)) return;
        candidate.Admitted = true;
        if (candidate.Key.Length == 0) candidate.Key = Key(source);
        // The owner has its own budgeted admission. A globally parked wind must
        // never let a storm under a future map bypass that storm's actual root.
        RegisterWind(owner);
        foreach (var wind in winds) RebuildStorms(wind);
    }
    private static void RebuildStorms(Wind wind)
    {
        var builder = new List<(StormVisual Source, string Key, int SourceId)>();
        foreach (var candidate in stormCandidates.Values)
            if (candidate.Admitted && candidate.Owner == wind.Source && candidate.Source) builder.Add((candidate.Source, candidate.Key, candidate.SourceId));
        if (builder.Count > EnvironmentReplayRules.MaximumStorms) throw new InvalidOperationException("Native storm directory exceeds its limit.");
        builder.Sort((a, b) => StringComparer.Ordinal.Compare(a.Key, b.Key));
        bool same = builder.Count == wind.Storms.Length;
        for (int i = 0; same && i < builder.Count; i++) same = builder[i].Source == wind.Storms[i].Source;
        if (same) return;
        wind.Storms = builder.ToArray(); discovery.Changed(EnvironmentDirectory.Winds);
    }

    private static void RegisterWind(WindChillZone source, bool urgent = false)
    {
        if (!source || source.gameObject.scene.handle != scene) return;
        if (windSegments != null && !windSegments.Admit(source.gameObject, urgent)) return;
        Wind? value = null;
        foreach (var existing in winds) if (existing.Source == source) { value = existing; break; }
        if (value == null)
        {
            if (winds.Count >= EnvironmentReplayRules.MaximumWinds) throw new InvalidOperationException("Native wind directory exceeds its limit.");
            value = new Wind { Source = source, ObjectId = source.gameObject.GetInstanceID(), Key = Key(source) }; winds.Add(value);
            winds.Sort((a, b) => StringComparer.Ordinal.Compare(a.Key, b.Key));
        }
        RebuildStorms(value);
        discovery.Changed(EnvironmentDirectory.Winds);
    }

    private static void RegisterRising(LavaRising source, bool urgent = false)
    {
        if (!source) return;
        if (source.lava) RegisterLava(source, source.lava.transform, 0, urgent);
        else RemoveLavaSource(source.GetInstanceID(), forgetCandidate: true);
    }
    private static void ForgetLavaTargetIfUnused(int targetId)
    {
        foreach (var candidate in lavaCandidates.Values) if (candidate.TargetId == targetId) return;
        lavaSegments?.Forget(targetId);
    }
    private static void RemoveLavaSource(int sourceId, bool forgetCandidate)
    {
        for (int i = lava.Count - 1; i >= 0; i--)
            if (lava[i].SourceId == sourceId)
            { discovery.Changed(lava[i].Kind == 0 ? EnvironmentDirectory.RisingLava : EnvironmentDirectory.MovingLava); lava.RemoveAt(i); }
        if (forgetCandidate && lavaCandidates.TryGetValue(sourceId, out var candidate))
        { lavaCandidates.Remove(sourceId); ForgetLavaTargetIfUnused(candidate.TargetId); }
    }
    private static void RegisterLava(MonoBehaviour source, Transform target, int kind, bool urgent = false)
    {
        if (!source || !target || source.gameObject.scene.handle != scene || target.gameObject.scene.handle != scene) return;
        int sourceId = source.GetInstanceID(), targetId = target.gameObject.GetInstanceID();
        if (lavaCandidates.TryGetValue(sourceId, out var candidate))
        {
            int oldTargetId = candidate.TargetId;
            if (oldTargetId != targetId || !ReferenceEquals(candidate.Source, source)) RemoveLavaSource(sourceId, forgetCandidate: false);
            candidate.Source = source; candidate.Target = target; candidate.TargetId = targetId; candidate.Kind = kind;
            if (oldTargetId != targetId) ForgetLavaTargetIfUnused(oldTargetId);
        }
        else lavaCandidates.Add(sourceId, new LavaCandidate { Source = source, Target = target, SourceId = sourceId, TargetId = targetId, Kind = kind });
        if (lavaSegments != null && !lavaSegments.Admit(target.gameObject, urgent)) return;
        foreach (var existing in lava)
        {
            if (existing.Source != source) continue;
            if (existing.Target != target) existing.Previous = null;
            existing.Target = target; existing.TargetId = targetId; existing.Kind = kind;
            discovery.Changed(kind == 0 ? EnvironmentDirectory.RisingLava : EnvironmentDirectory.MovingLava); return;
        }
        if (lava.Count >= EnvironmentReplayRules.MaximumLava) throw new InvalidOperationException("Native lava directory exceeds its limit.");
        lava.Add(new Lava { Source = source, Target = target, SourceId = sourceId, TargetId = targetId, Key = Key(source), Kind = kind });
        lava.Sort((a, b) => StringComparer.Ordinal.Compare(a.Key, b.Key));
        discovery.Changed(kind == 0 ? EnvironmentDirectory.RisingLava : EnvironmentDirectory.MovingLava);
    }
    private static void RegisterFog(OrbFogHandler source, bool urgent = false)
    {
        if (!source || source.gameObject.scene.handle != scene) return;
        // Fog is the live transition itself. It remains independently admitted
        // when global; a fog owned by a future map still obeys that map's gate.
        if (fogSegments != null && !fogSegments.Admit(source.gameObject, urgent, urgentUnknown: true)) return;
        Fog? entry = null; foreach (var existing in fogs) if (existing.Source == source) { entry = existing; break; }
        if (entry == null)
        {
            if (fogs.Count >= EnvironmentReplayRules.MaximumWinds) throw new InvalidOperationException("Native fog directory exceeds its limit.");
            entry = new Fog { Source = source, ObjectId = source.gameObject.GetInstanceID() }; fogs.Add(entry);
        }
        entry.Sphere = source.GetComponentInChildren<FogSphere>(true);
        entry.Key = entry.Sphere ? Key(entry.Sphere!) : "";
        discovery.Changed(EnvironmentDirectory.Fog);
    }

    public static EnvironmentReplayFrame Sample(bool force = false)
    {
        using var timing = ReplayPerformance.Measure(ReplayStage.Environment);
        double now = Time.timeAsDouble;
        bool directoryChanged = Discover(now);
        if (CaptureBootstrapWorkBudget.InitialOnly && !force && !directoryChanged && InitialBaselineReady && !HasDiscreteEdge()) return previous!;
        if (!sampling.Take(now, force || directoryChanged || HasDiscreteEdge()) && previous != null) return previous;
        var windBuilder = new EnvironmentSnapshotArray<EnvironmentWindFrame>(previous?.Winds, winds.Count);
        for (int i = 0; i < winds.Count; i++)
        {
            var value = winds[i]; var source = value.Source; var old = value.Previous;
            if (!source) throw new InvalidOperationException("A native wind source disappeared during environment capture.");
            var stormBuilder = new EnvironmentSnapshotArray<EnvironmentStormFrame>(old?.Storms, value.Storms.Length);
            for (int j = 0; j < value.Storms.Length; j++)
            {
                var visual = value.Storms[j]; var prior = old != null && old.Storms.Length == value.Storms.Length ? old.Storms[j] : null;
                float intensity = stormIntensity(visual.Source);
                stormBuilder.Set(j, prior != null && prior.Key == visual.Key && prior.Kind == (int)visual.Source.stormType && prior.Factor == visual.Source.windFactor && prior.Intensity == intensity
                    ? prior : new EnvironmentStormFrame { Key = visual.Key, Kind = (int)visual.Source.stormType, Factor = visual.Source.windFactor, Intensity = intensity });
            }
            var stormFrames = stormBuilder.Build();
            Vector3 vector = direction(source);
            bool enabled = source.enabled && source.gameObject.activeInHierarchy;
            float seconds = until(source), length = duration(source), elapsed = activeFor(source);
            bool same = old != null && old.Enabled == enabled && old.Active == source.windActive && Eq(old.Direction, vector) &&
                old.Intensity == source.windIntensity && old.StormProgress == source.StormProgress && old.TimeUntilStorm == source.timeUntilStorm &&
                old.SecondsUntilSwitch == seconds && old.Duration == length && old.ActiveFor == elapsed && ReferenceEquals(stormFrames, old.Storms);
            var frame = value.Previous = same ? old! : new EnvironmentWindFrame
            {
                Key = value.Key, Enabled = enabled, Active = source.windActive, Direction = old != null && Eq(old.Direction, vector) ? old.Direction : Vec(vector),
                Intensity = source.windIntensity, StormProgress = source.StormProgress, TimeUntilStorm = source.timeUntilStorm,
                SecondsUntilSwitch = seconds, Duration = length, ActiveFor = elapsed, Storms = stormFrames,
            };
            windBuilder.Set(i, frame);
        }
        var windFrames = windBuilder.Build();
        var lavaBuilder = new EnvironmentSnapshotArray<EnvironmentLavaFrame>(previous?.Lava, lava.Count);
        for (int i = 0; i < lava.Count; i++)
        {
            var value = lava[i]; if (!value.Source || !value.Target) throw new InvalidOperationException("A native lava source disappeared during environment capture.");
            var rising = value.Source as LavaRising; var movingLava = value.Source as MovingLava;
            var position = value.Target.position; var old = value.Previous;
            bool started = rising ? rising!.started : moving(movingLava!);
            bool ended = rising && rising!.ended;
            float progress = rising ? rising!.timeTraveled : 0;
            bool fogPlane = rising && rising!.fogPlane && rising.fogPlane.activeSelf;
            bool active = value.Target.gameObject.activeSelf;
            bool same = old != null && old.Active == active && Eq(old.Position, position) && old.Started == started && old.Ended == ended && old.ProgressTime == progress && old.FogPlaneActive == fogPlane;
            var frame = value.Previous = same ? old! : new EnvironmentLavaFrame
            {
                Key = value.Key, Kind = rising ? 0 : 1, Active = active, Position = old != null && Eq(old.Position, position) ? old.Position : Vec(position),
                Started = started, Ended = ended, ProgressTime = progress, FogPlaneActive = fogPlane,
            };
            lavaBuilder.Set(i, frame);
        }
        var lavaFrames = lavaBuilder.Build();
        EnvironmentFogFrame? fog = null;
        if (sphere && orb)
        {
            var old = previous?.Fog; float close = orb!.currentCloseFog;
            bool same = old != null && old.Key == sphereKey && Eq(old.Point, sphere!.fogPoint) && old.Size == sphere.currentSize && old.Padding == sphere.PADDING &&
                old.Enable == sphere.ENABLE && old.Reveal == sphere.REVEAL_AMOUNT && old.CloseFog == close && old.Active == sphere.gameObject.activeSelf &&
                old.Origin == orb.currentID && old.Moving == orb.isMoving && old.Arrived == orb.hasArrived;
            fog = same ? old : new EnvironmentFogFrame { Key = sphereKey, Point = old != null && Eq(old.Point, sphere!.fogPoint) ? old.Point : Vec(sphere!.fogPoint), Size = sphere.currentSize, Padding = sphere.PADDING,
                Enable = sphere.ENABLE, Reveal = sphere.REVEAL_AMOUNT, CloseFog = close, Active = sphere.gameObject.activeSelf,
                Origin = orb.currentID, Moving = orb.isMoving, Arrived = orb.hasArrived };
        }
        var light = DayNightManager.instance;
        float weather = Shader.GetGlobalFloat(weatherId), global = Shader.GetGlobalFloat(windId),
            rain = light ? light.rainstormWindFactor : 0, snow = light ? light.snowstormWindFactor : 0,
            height = Shader.GetGlobalFloat(heightId), spirit = Shader.GetGlobalFloat(spiritId);
        if (previous != null && ReferenceEquals(windFrames, previous.Winds) && ReferenceEquals(lavaFrames, previous.Lava) && ReferenceEquals(fog, previous.Fog) && previous.WeatherBlend == weather && previous.GlobalWind == global &&
            previous.RainFactor == rain && previous.SnowFactor == snow && previous.HeightFogAmount == height && previous.HeightFogSpirit == spirit &&
            previous.SampleTimeKnown && previous.SampleTime == now) return previous;
        return previous = new EnvironmentReplayFrame { SampleTimeKnown = true, SampleTime = now, Winds = windFrames, Lava = lavaFrames, Fog = fog, WeatherBlend = weather, GlobalWind = global,
            RainFactor = rain, SnowFactor = snow, HeightFogAmount = height, HeightFogSpirit = spirit };
    }

    private static bool HasDiscreteEdge()
    {
        if (previous == null || previous.Winds.Length != winds.Count || previous.Lava.Length != lava.Count) return true;
        foreach (var value in winds)
        {
            var old = value.Previous; var source = value.Source;
            if (old == null || old.Enabled != (source.enabled && source.gameObject.activeInHierarchy) || old.Active != source.windActive ||
                !Eq(old.Direction, direction(source)) || old.Duration != duration(source) || activeFor(source) + .001f < old.ActiveFor ||
                old.Storms.Length != value.Storms.Length) return true;
        }
        foreach (var value in lava)
        {
            var old = value.Previous; var rising = value.Source as LavaRising;
            bool started = rising ? rising!.started : moving((MovingLava)value.Source);
            bool ended = rising && rising!.ended;
            bool fogPlane = rising && rising!.fogPlane && rising.fogPlane.activeSelf;
            if (old == null || old.Active != value.Target.gameObject.activeSelf || old.Started != started || old.Ended != ended ||
                old.FogPlaneActive != fogPlane || rising && rising!.timeTraveled + .001f < old.ProgressTime) return true;
        }
        var oldFog = previous.Fog;
        if (!sphere || !orb) return oldFog != null;
        return oldFog == null || oldFog.Key != sphereKey || oldFog.Active != sphere!.gameObject.activeSelf || oldFog.Origin != orb!.currentID ||
            oldFog.Moving != orb.isMoving || oldFog.Arrived != orb.hasArrived || oldFog.Enable != sphere.ENABLE;
    }
    private static bool Eq(float[] values, Vector3 value) => values[0] == value.x && values[1] == value.y && values[2] == value.z;
    private static float[] Vec(Vector3 value) => new[] { value.x, value.y, value.z };
}
