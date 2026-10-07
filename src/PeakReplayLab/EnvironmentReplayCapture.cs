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
        public string Key = "";
        public (StormVisual Source, string Key)[] Storms = Array.Empty<(StormVisual, string)>();
        public EnvironmentWindFrame? Previous;
    }
    private sealed class Lava
    {
        public MonoBehaviour Source = null!;
        public Transform Target = null!;
        public string Key = "";
        public int Kind;
        public EnvironmentLavaFrame? Previous;
    }
    private static readonly AccessTools.FieldRef<WindChillZone, Vector3> direction = AccessTools.FieldRefAccess<WindChillZone, Vector3>("currentWindDirection");
    private static readonly AccessTools.FieldRef<WindChillZone, float> until = AccessTools.FieldRefAccess<WindChillZone, float>("untilSwitch");
    private static readonly AccessTools.FieldRef<WindChillZone, float> duration = AccessTools.FieldRefAccess<WindChillZone, float>("timeUntilNextWind");
    private static readonly AccessTools.FieldRef<WindChillZone, float> activeFor = AccessTools.FieldRefAccess<WindChillZone, float>("hasBeenActiveFor");
    private static readonly AccessTools.FieldRef<StormVisual, float> stormIntensity = AccessTools.FieldRefAccess<StormVisual, float>("windIntensity");
    private static readonly AccessTools.FieldRef<MovingLava, bool> moving = AccessTools.FieldRefAccess<MovingLava, bool>("timeToMove");
    private static readonly List<Wind> winds = new();
    private static readonly List<Lava> lava = new();
    private static OrbFogHandler? orb;
    private static FogSphere? sphere;
    private static string sphereKey = "";
    private static EnvironmentReplayFrame? previous;
    private static int scene = -1;
    private static readonly EnvironmentDiscoverySchedule discovery = new();
    private static readonly EnvironmentSampleClock sampling = new();
    private static bool knownOrb, knownSphere;
    private static readonly int weatherId = Shader.PropertyToID("_WeatherBlend"), windId = Shader.PropertyToID("GlobalWind"),
        heightId = Shader.PropertyToID("HeightFogAmount"), spiritId = Shader.PropertyToID("HeightFogSpirit");
    // Immutable pure data only. Failure reporting can retain the rejected native
    // observation before Reset; no serialization or logging runs in Sample.
    internal static EnvironmentReplayFrame? LastObservation => previous;

    public static void Reset()
    { scene = -1; winds.Clear(); lava.Clear(); orb = null; sphere = null; sphereKey = ""; previous = null; knownOrb = knownSphere = false; discovery.Reset(); sampling.Reset(); }
    // Component type disambiguates a zone and its visual sharing one native
    // GameObject; runtime instance IDs are deliberately never persisted.
    internal static string Key(Component source) => source.GetType().FullName + ":" + WorldTrack.Path(source.transform);

    private static void Discover(double now)
    {
        int current = SceneManager.GetActiveScene().handle;
        if (scene != current) { Reset(); scene = current; }
        var invalid = EnvironmentDirectory.None;
        foreach (var value in winds)
        {
            if (!value.Source) { invalid |= EnvironmentDirectory.Winds; continue; }
            foreach (var storm in value.Storms) if (!storm.Source) invalid |= EnvironmentDirectory.Winds;
        }
        foreach (var value in lava)
            if (!value.Source || !value.Target) invalid |= value.Kind == 0 ? EnvironmentDirectory.RisingLava : EnvironmentDirectory.MovingLava;
        if (knownOrb && !orb || knownSphere && !sphere) invalid |= EnvironmentDirectory.Fog;
        var due = discovery.Take(current, now, invalid);
        if (due == EnvironmentDirectory.None) return;
        using var timing = ReplayPerformance.Measure(ReplayStage.Discovery);
        if ((due & EnvironmentDirectory.Winds) != 0) DiscoverWinds();
        if ((due & EnvironmentDirectory.RisingLava) != 0) DiscoverLava(0);
        if ((due & EnvironmentDirectory.MovingLava) != 0) DiscoverLava(1);
        if ((due & EnvironmentDirectory.Fog) != 0)
        {
            orb = UnityEngine.Object.FindFirstObjectByType<OrbFogHandler>();
            sphere = orb ? orb!.GetComponentInChildren<FogSphere>(true) : null;
            sphereKey = sphere ? Key(sphere!) : "";
            knownOrb = orb; knownSphere = sphere;
        }
        if (winds.Count > EnvironmentReplayRules.MaximumWinds || lava.Count > EnvironmentReplayRules.MaximumLava)
            throw new InvalidOperationException("Native environment exceeds the recorded entity limit.");
    }

    private static void DiscoverWinds()
    {
        bool changed = false;
        for (int i = winds.Count - 1; i >= 0; i--) if (!winds[i].Source) { winds.RemoveAt(i); changed = true; }
        foreach (var source in UnityEngine.Object.FindObjectsByType<WindChillZone>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (source.gameObject.scene.handle != scene) continue;
            Wind? value = null;
            foreach (var existing in winds) if (existing.Source == source) { value = existing; break; }
            if (value == null) { value = new Wind { Source = source, Key = Key(source) }; winds.Add(value); changed = true; }
            var native = source.GetComponentsInChildren<StormVisual>(true);
            var builder = new List<(StormVisual Source, string Key)>();
            foreach (var storm in native)
            {
                if (storm.GetComponentInParent<WindChillZone>() != source) continue;
                string? key = null;
                foreach (var existing in value.Storms) if (existing.Source == storm) { key = existing.Key; break; }
                builder.Add((storm, key ?? Key(storm)));
            }
            bool same = builder.Count == value.Storms.Length;
            for (int i = 0; same && i < builder.Count; i++) same = builder[i].Source == value.Storms[i].Source;
            if (!same) value.Storms = builder.ToArray();
        }
        if (changed) winds.Sort((a, b) => StringComparer.Ordinal.Compare(a.Key, b.Key));
    }

    private static void DiscoverLava(int kind)
    {
        bool changed = false;
        for (int i = lava.Count - 1; i >= 0; i--)
            if (lava[i].Kind == kind && (!lava[i].Source || !lava[i].Target)) { lava.RemoveAt(i); changed = true; }
        void Add(MonoBehaviour source, Transform target)
        {
            if (!source || !target || source.gameObject.scene.handle != scene) return;
            foreach (var existing in lava)
                if (existing.Source == source) { existing.Target = target; return; }
            lava.Add(new Lava { Source = source, Target = target, Key = Key(source), Kind = kind }); changed = true;
        }
        if (kind == 0)
            foreach (var source in UnityEngine.Object.FindObjectsByType<LavaRising>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (source.lava) Add(source, source.lava.transform);
        if (kind == 1)
            foreach (var source in UnityEngine.Object.FindObjectsByType<MovingLava>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Add(source, source.transform);
        if (changed) lava.Sort((a, b) => StringComparer.Ordinal.Compare(a.Key, b.Key));
    }

    public static EnvironmentReplayFrame Sample(bool force = false)
    {
        using var timing = ReplayPerformance.Measure(ReplayStage.Environment);
        double now = Time.timeAsDouble;
        Discover(now);
        if (!sampling.Take(now, force || HasDiscreteEdge()) && previous != null) return previous;
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
