using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakReplayLab;

// Only audited native environment sources can be addressed. Replaying never
// invokes ToggleWind, SetFogOrigin, lava RPCs, status application, or forces.
internal sealed class EnvironmentReplayPlayer : IDisposable
{
    private sealed class Wind
    {
        public WindChillZone Source = null!;
        public readonly Dictionary<string, Storm> Storms = new(StringComparer.Ordinal);
    }
    private sealed class Storm
    {
        public StormVisual Source = null!;
        public WindChillZone Zone = null!;
        public FogConfig? Fog;
        public EnvironmentStormFrame? State;
        public Vector3 Position;
        public Quaternion Rotation;
        public bool QuadEnabled;
        public readonly MaterialPropertyBlock Block = new();
        public readonly MaterialPropertyBlock InitialBlock = new();
        public ParticleSystem.MinMaxCurve ForceX, ForceZ;
        public double LastTime = double.NaN;
        public bool Visible, Emitting, EmissionStarted, WasWindActive;
        public float LastActiveFor;
    }
    private sealed class Lava
    {
        public MonoBehaviour Source = null!;
        public Transform Target = null!;
    }
    private sealed class LavaSurface
    {
        public LavaPost Source = null!;
        public Renderer? Renderer;
        public bool Enabled;
    }
    private static readonly AccessTools.FieldRef<WindChillZone, Vector3> direction = AccessTools.FieldRefAccess<WindChillZone, Vector3>("currentWindDirection");
    private static readonly AccessTools.FieldRef<WindChillZone, float> until = AccessTools.FieldRefAccess<WindChillZone, float>("untilSwitch");
    private static readonly AccessTools.FieldRef<WindChillZone, float> duration = AccessTools.FieldRefAccess<WindChillZone, float>("timeUntilNextWind");
    private static readonly AccessTools.FieldRef<WindChillZone, float> activeFor = AccessTools.FieldRefAccess<WindChillZone, float>("hasBeenActiveFor");
    private static readonly AccessTools.FieldRef<StormVisual, float> stormIntensity = AccessTools.FieldRefAccess<StormVisual, float>("windIntensity");
    private static readonly AccessTools.FieldRef<OrbFogHandler, FogSphereOrigin[]> fogOrigins = AccessTools.FieldRefAccess<OrbFogHandler, FogSphereOrigin[]>("origins");
    private readonly Dictionary<string, Wind> winds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Lava> lava = new(StringComparer.Ordinal);
    private readonly List<(Behaviour Source, bool Enabled)> suspended = new();
    private readonly List<LavaSurface> lavaSurfaces = new();
    private readonly Dictionary<ParticleSystem, (bool Playing, bool Paused, bool Emission)> particleStates = new();
    private readonly Action<string>? warning;
    private readonly ReplayShaderClock? shaderClock;
    private bool warnedMissing;
    private readonly EnvironmentReplayFrame initial;
    private readonly Dictionary<string, float> floats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vector4> vectors = new(StringComparer.Ordinal);
    private readonly Texture? windTexture;
    private readonly Color windTint;
    private OrbFogHandler? orb;
    private FogSphere? sphere;
    private Renderer? fogRenderer;
    private string sphereKey = "";
    private readonly MaterialPropertyBlock fogBlock = new(), initialFogBlock = new();
    private FogSphereOrigin[] origins = Array.Empty<FogSphereOrigin>();
    private Vector3 initialFogScale;
    private double time;
    private bool disposed, warnedUnknown;
    public string Warning { get; private set; } = "";

    public EnvironmentReplayPlayer(Action<string>? warning = null)
    {
        this.warning = warning;
        initial = EnvironmentReplayCapture.Sample(force: true);
        foreach (string name in new[] { "_WeatherBlend", "GlobalWind", "HeightFogAmount", "HeightFogSpirit", "FogEnabled", "_FogSphereSize", "CloseDistanceMod", "FakeMountainEnabled",
            "WindSkyBrightnessValue", "WindTextureInfluence", "WindFogDensity", "WindFogTextureDensity", "WindMixInfluence", "WindRotationAngle", "WindSphereScale", "LavaAlpha", "LavaHeight", "LavaStart" }) floats[name] = Shader.GetGlobalFloat(name);
        foreach (string name in new[] { "FogCenter", "WindSpeed", "WindRotationAxis" }) vectors[name] = Shader.GetGlobalVector(name);
        windTexture = Shader.GetGlobalTexture("_WindTexture"); windTint = Shader.GetGlobalColor("WindTint");
        try
        {
            int scene = SceneManager.GetActiveScene().handle;
            foreach (var source in UnityEngine.Object.FindObjectsByType<WindChillZone>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (source.gameObject.scene.handle != scene) continue;
                var wind = new Wind { Source = source };
                winds.Add(EnvironmentReplayCapture.Key(source), wind);
                Suspend(source);
                foreach (var height in source.GetComponents<WindHeightEffect>()) Suspend(height);
                foreach (var visual in source.GetComponentsInChildren<StormVisual>(true))
                {
                    if (visual.GetComponentInParent<WindChillZone>() != source) continue;
                    var storm = new Storm { Source = visual, Zone = source, Fog = visual.GetComponentInParent<FogConfig>(), Position = visual.transform.position,
                        Rotation = visual.transform.rotation, QuadEnabled = visual.quadRend && visual.quadRend.enabled };
                    if (visual.quadRend) visual.quadRend.GetPropertyBlock(storm.InitialBlock);
                    if (visual.particleForceField) { storm.ForceX = visual.particleForceField.directionX; storm.ForceZ = visual.particleForceField.directionZ; }
                    foreach (var particle in visual.part ?? Array.Empty<ParticleSystem>())
                        if (particle && !particleStates.ContainsKey(particle)) particleStates.Add(particle, (particle.isPlaying, particle.isPaused, particle.emission.enabled));
                    wind.Storms.Add(EnvironmentReplayCapture.Key(visual), storm); Suspend(visual);
                    if (storm.Fog) Suspend(storm.Fog);
                }
            }
            foreach (var source in UnityEngine.Object.FindObjectsByType<LavaRising>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (source.gameObject.scene.handle == scene && source.lava) { lava.Add(EnvironmentReplayCapture.Key(source), new Lava { Source = source, Target = source.lava.transform }); Suspend(source); }
            foreach (var source in UnityEngine.Object.FindObjectsByType<MovingLava>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (source.gameObject.scene.handle == scene) { lava.Add(EnvironmentReplayCapture.Key(source), new Lava { Source = source, Target = source.transform }); Suspend(source); }
            foreach (var source in UnityEngine.Object.FindObjectsByType<LavaPost>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (source.gameObject.scene.handle == scene)
                {
                    var renderer = source.GetComponent<Renderer>(); lavaSurfaces.Add(new LavaSurface { Source = source, Renderer = renderer, Enabled = renderer && renderer.enabled });
                    Suspend(source);
                }
            orb = UnityEngine.Object.FindFirstObjectByType<OrbFogHandler>();
            sphere = orb ? orb!.GetComponentInChildren<FogSphere>(true) : null;
            if (orb) { origins = fogOrigins(orb!) ?? orb.transform.root.GetComponentsInChildren<FogSphereOrigin>(); Suspend(orb); }
            if (sphere)
            {
                sphereKey = EnvironmentReplayCapture.Key(sphere!); fogRenderer = sphere.GetComponent<Renderer>(); initialFogScale = sphere.transform.localScale;
                if (fogRenderer) fogRenderer!.GetPropertyBlock(initialFogBlock);
                Suspend(sphere);
            }
            shaderClock = new ReplayShaderClock(warning);
        }
        catch { Dispose(); throw; }
    }

    private void Suspend(Behaviour source)
    {
        foreach (var item in suspended) if (item.Source == source) return;
        suspended.Add((source, source.enabled)); source.enabled = false;
    }

    public void Enforce() { if (!disposed) foreach (var item in suspended) if (item.Source) item.Source.enabled = false; }

    public void UpdateVisualClock(double recordingTime) { if (!disposed) shaderClock?.SetTime(recordingTime); }

    public void Apply(EnvironmentReplayFrame? left, EnvironmentReplayFrame? right, float mix, double recordingTime, Vector3 cameraPosition)
    {
        if (disposed) return;
        Enforce(); time = recordingTime;
        UpdateVisualClock(recordingTime);
        if (left == null)
        {
            if (!warnedUnknown)
            {
                warnedUnknown = true; Warning = "此旧录像未记录风向、风暴时间、岩浆及雾推进；无法补出历史天气。"; warning?.Invoke(Warning);
            }
            // Clear values from a previously viewed known sample. An unknown
            // old page must render identically regardless of seek history.
            foreach (var state in initial.Lava)
                if (lava.TryGetValue(state.Key, out var target) && target.Target)
                {
                    target.Target.position = Vec(state.Position);
                    if (target.Target.gameObject.activeSelf != state.Active) target.Target.gameObject.SetActive(state.Active);
                    if (target.Source is LavaRising rising)
                    {
                        rising.started = state.Started; rising.ended = state.Ended; rising.timeTraveled = state.ProgressTime;
                        if (rising.fogPlane && rising.fogPlane.activeSelf != state.FogPlaneActive) rising.fogPlane.SetActive(state.FogPlaneActive);
                    }
                }
            Shader.SetGlobalFloat("_WeatherBlend", 0); Shader.SetGlobalFloat("GlobalWind", 0);
            Shader.SetGlobalFloat("HeightFogAmount", initial.HeightFogAmount); Shader.SetGlobalFloat("HeightFogSpirit", initial.HeightFogSpirit);
            var staticLighting = DayNightManager.instance;
            if (staticLighting) { staticLighting.rainstormWindFactor = 0; staticLighting.snowstormWindFactor = 0; staticLighting.UpdateCycle(); }
            ApplyLegacyFog();
            foreach (var wind in winds.Values)
            {
                wind.Source.windActive = false; wind.Source.windIntensity = 0;
                foreach (var storm in wind.Storms.Values) { storm.State = null; storm.Source.windFactor = 0; stormIntensity(storm.Source) = 0; SetStormVisible(storm, false); }
            }
            return;
        }
        foreach (var wind in winds.Values)
        {
            wind.Source.windActive = false; wind.Source.windIntensity = 0;
            foreach (var storm in wind.Storms.Values) storm.State = null;
        }
        foreach (var state in left.Winds)
        {
            if (!winds.TryGetValue(state.Key, out var wind) || !wind.Source) { Missing(state.Key); continue; }
            var next = FindWind(right, state.Key);
            float blend = next == null ? 0 : EnvironmentReplayMath.Mix(EnvironmentReplayMath.SameWindEvent(state, next), mix);
            var source = wind.Source;
            source.windActive = state.Enabled && state.Active;
            direction(source) = Vec(state.Direction);
            source.windIntensity = Blend(state.Intensity, next?.Intensity, blend);
            source.StormProgress = Blend(state.StormProgress, next?.StormProgress, blend);
            source.timeUntilStorm = Blend(state.TimeUntilStorm, next?.TimeUntilStorm, blend);
            until(source) = Blend(state.SecondsUntilSwitch, next?.SecondsUntilSwitch, blend);
            duration(source) = state.Duration; activeFor(source) = Blend(state.ActiveFor, next?.ActiveFor, blend);
            foreach (var visual in state.Storms)
            {
                if (!wind.Storms.TryGetValue(visual.Key, out var storm)) { Missing(visual.Key); continue; }
                var nextVisual = FindStorm(next, visual.Key);
                storm.State = visual; storm.Source.windFactor = Blend(visual.Factor, nextVisual?.Factor, blend);
                stormIntensity(storm.Source) = Blend(visual.Intensity, nextVisual?.Intensity, blend);
            }
        }
        foreach (var state in left.Lava)
        {
            if (!lava.TryGetValue(state.Key, out var target) || !target.Target) { Missing(state.Key); continue; }
            var next = FindLava(right, state.Key);
            float blend = next == null ? 0 : EnvironmentReplayMath.Mix(EnvironmentReplayMath.SameLavaEvent(state, next), mix);
            target.Target.position = next == null ? Vec(state.Position) : Vector3.Lerp(Vec(state.Position), Vec(next.Position), blend);
            if (target.Target.gameObject.activeSelf != state.Active) target.Target.gameObject.SetActive(state.Active);
            if (target.Source is LavaRising rising)
            {
                rising.started = state.Started; rising.ended = state.Ended; rising.timeTraveled = Blend(state.ProgressTime, next?.ProgressTime, blend);
                if (rising.fogPlane && rising.fogPlane.activeSelf != state.FogPlaneActive) rising.fogPlane.SetActive(state.FogPlaneActive);
            }
        }
        ApplyFog(left.Fog, right?.Fog, mix);
        var light = DayNightManager.instance;
        if (light)
        {
            light.rainstormWindFactor = Blend(left.RainFactor, right?.RainFactor, mix);
            light.snowstormWindFactor = Blend(left.SnowFactor, right?.SnowFactor, mix);
            light.UpdateCycle();
        }
        UpdateCamera(cameraPosition);
        Shader.SetGlobalFloat("_WeatherBlend", Blend(left.WeatherBlend, right?.WeatherBlend, mix));
        Shader.SetGlobalFloat("GlobalWind", Blend(left.GlobalWind, right?.GlobalWind, mix));
        Shader.SetGlobalFloat("HeightFogAmount", Blend(left.HeightFogAmount, right?.HeightFogAmount, mix));
        Shader.SetGlobalFloat("HeightFogSpirit", Blend(left.HeightFogSpirit, right?.HeightFogSpirit, mix));
    }

    public void UpdateCamera(Vector3 position)
    {
        if (disposed) return;
        foreach (var wind in winds.Values)
        {
            if (!wind.Source) continue;
            bool inside = wind.Source.windZoneBounds.Contains(position);
            wind.Source.observedCharacterInsideBounds = inside;
            foreach (var storm in wind.Storms.Values)
            {
                if (!storm.Source) continue;
                // The original quad remains enabled inside the zone while its
                // recorded windFactor fades after windActive becomes false.
                bool visible = inside && storm.State != null && storm.Source.gameObject.activeInHierarchy;
                storm.Source.observedPlayerInWindZone = visible && wind.Source.windActive;
                if (inside)
                {
                    var vector = direction(wind.Source);
                    storm.Source.transform.SetPositionAndRotation(position, vector.sqrMagnitude > .000001f ? Quaternion.LookRotation(vector) : storm.Source.transform.rotation);
                }
                if (storm.Source.particleForceField)
                {
                    var vector = direction(wind.Source); float intensity = stormIntensity(storm.Source) * storm.Source.windParticleMult;
                    storm.Source.particleForceField.directionX = vector.x * intensity; storm.Source.particleForceField.directionZ = vector.z * intensity;
                }
                SetStormVisible(storm, visible);
                if (visible && wind.Source.windActive && storm.Fog) storm.Fog!.SetFog();
            }
        }
        foreach (var surface in lavaSurfaces)
        {
            var source = surface.Source;
            if (!source || !source.lava1 || !source.lava2 || !source.thresholdTransform || !source.lavaStart || !source.lavaFadeIn) continue;
            // LavaPost caches initial heights and launches a scaled-time tween.
            // Use the captured moving surface's actual height after any seek.
            Shader.SetGlobalFloat("LavaHeight", position.z < source.thresholdTransform.position.z ? source.lava1.position.y : source.lava2.position.y);
            Shader.SetGlobalFloat("LavaStart", source.lavaStart.position.z);
            if (surface.Renderer) surface.Renderer!.enabled = position.z >= source.lavaFadeIn.position.z;
        }
    }

    private void SetStormVisible(Storm storm, bool visible)
    {
        if (storm.Source.quadRend)
        {
            storm.Source.quadRend.enabled = visible;
            storm.Source.quadRend.GetPropertyBlock(storm.Block); storm.Block.SetFloat("_Alpha", visible ? storm.Source.windFactor : 0);
            storm.Source.quadRend.SetPropertyBlock(storm.Block);
        }
        bool active = storm.Zone.windActive;
        float elapsed = activeFor(storm.Zone);
        if (!visible)
        {
            foreach (var particle in storm.Source.part ?? Array.Empty<ParticleSystem>())
                if (particle && (particle.isPlaying || particle.isPaused || particle.particleCount > 0))
                    particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            storm.Visible = false; storm.LastTime = time; storm.Emitting = false;
            storm.WasWindActive = active; storm.LastActiveFor = elapsed;
            return;
        }
        bool newGust = active && (!storm.WasWindActive || elapsed + .001f < storm.LastActiveFor);
        bool discontinuity = !storm.Visible || newGust || double.IsNaN(storm.LastTime) || time < storm.LastTime || time - storm.LastTime > .5;
        if (discontinuity) storm.EmissionStarted = EmissionStarted(storm);
        if (active && storm.Zone.windIntensity > .1f) storm.EmissionStarted = true;
        bool emitting = EnvironmentStormReplayRules.ShouldEmit(storm.Source.useWindChillZoneIntensity,
            active, storm.Zone.windIntensity, storm.EmissionStarted);
        if (!active && storm.Source.windFactor <= 0 && stormIntensity(storm.Source) <= 0) emitting = false;
        foreach (var particle in storm.Source.part ?? Array.Empty<ParticleSystem>())
        {
            if (!particle) continue;
            bool authoredEmission = !particleStates.TryGetValue(particle, out var saved) || saved.Emission;
            var emission = particle.emission;
            if (discontinuity)
            {
                var phase = EnvironmentStormReplayRules.Rebuild(emitting, storm.EmissionStarted, active,
                    storm.Source.windFactor, stormIntensity(storm.Source), elapsed, duration(storm.Zone), until(storm.Zone), particle.main.startLifetime.constantMax);
                if (phase.Clear) { emission.enabled = false; particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear); continue; }
                emission.enabled = authoredEmission;
                particle.Simulate(phase.Warmup, false, true, false);
                emission.enabled = authoredEmission && emitting;
                if (!emitting) particle.Stop(false, ParticleSystemStopBehavior.StopEmitting);
                if (phase.Coast > 0) particle.Simulate(phase.Coast, false, false, false);
            }
            else
            {
                emission.enabled = authoredEmission && emitting;
                if (emitting && !storm.Emitting) particle.Play(false);
                else if (!emitting && storm.Emitting) particle.Stop(false, ParticleSystemStopBehavior.StopEmitting);
                if (!emitting && storm.Source.windFactor <= 0 && stormIntensity(storm.Source) <= 0)
                { particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear); continue; }
                if (time > storm.LastTime) particle.Simulate((float)(time - storm.LastTime), false, false, false);
            }
            particle.Pause(false);
        }
        storm.Visible = visible; storm.LastTime = time; storm.Emitting = visible && emitting;
        storm.WasWindActive = active; storm.LastActiveFor = elapsed;
    }

    private static bool EmissionStarted(Storm storm)
    {
        if (!storm.Source.useWindChillZoneIntensity) return true;
        if (storm.Zone.windIntensity > .1f) return true;
        var curve = storm.Zone.windIntensityCurve;
        if (curve == null) return false;
        // Native curve assets provide the threshold history on an isolated seek;
        // sampling is bounded and occurs only on a discontinuity/new gust.
        float progress = storm.Zone.windActive ? Mathf.Clamp01(storm.Zone.StormProgress) : 1;
        for (int i = 0; i <= 64; i++) if (curve.Evaluate(progress * i / 64f) > .1f) return true;
        return false;
    }

    private void ApplyFog(EnvironmentFogFrame? left, EnvironmentFogFrame? right, float mix)
    {
        if (left == null)
        {
            // A known environment with no native fog source is not a historical
            // fog guess. Remove an earlier known sphere when seeking here.
            if (sphere) { sphere!.ENABLE = 0; if (sphere.gameObject.activeSelf) sphere.gameObject.SetActive(false); }
            Shader.SetGlobalFloat("FogEnabled", 0);
            return;
        }
        if (!sphere || left.Key != sphereKey) { Missing(left.Key); ApplyLegacyFog(); return; }
        float blend = right != null && right.Key == left.Key && right.Origin == left.Origin && right.Active == left.Active ? mix : 0;
        sphere!.fogPoint = right == null ? Vec(left.Point) : Vector3.Lerp(Vec(left.Point), Vec(right.Point), blend);
        sphere.currentSize = Blend(left.Size, right?.Size, blend); sphere.PADDING = left.Padding;
        sphere.ENABLE = Blend(left.Enable, right?.Enable, blend); sphere.REVEAL_AMOUNT = Blend(left.Reveal, right?.Reveal, blend);
        if (sphere.gameObject.activeSelf != left.Active) sphere.gameObject.SetActive(left.Active);
        WriteFog(Blend(left.CloseFog, right?.CloseFog, blend));
    }

    // Older recordings establish the segment but have no historical fog timer.
    // Align the native static origin, avoiding the fresh scene's beach sphere
    // protruding through an Alpine replay. Do not start a fog catch-up coroutine.
    private void ApplyLegacyFog()
    {
        if (!sphere) return;
        var map = MapHandler.Instance;
        int segment = map ? WorldSegmentIndex.Resolve((int)map.GetCurrentSegment(), map.segments.Length) : -1;
        if (segment < 0 || segment >= origins.Length)
        {
            ApplyFog(initial.Fog, initial.Fog, 0);
            return;
        }
        var origin = origins[segment];
        bool active = initial.Fog?.Active ?? true;
        if (sphere!.gameObject.activeSelf != active) sphere.gameObject.SetActive(active);
        sphere!.PADDING = initial.Fog?.Padding ?? sphere.PADDING;
        sphere!.fogPoint = origin.transform.position; sphere.currentSize = origin.size; sphere.REVEAL_AMOUNT = 0;
        sphere.ENABLE = origin.disableFog ? 0 : 1;
        WriteFog(initial.Fog?.CloseFog ?? 1);
    }

    private void WriteFog(float close)
    {
        if (!sphere) return;
        sphere!.transform.localScale = Vector3.one * ((sphere.currentSize + sphere.PADDING) * 2);
        if (fogRenderer)
        {
            fogRenderer!.GetPropertyBlock(fogBlock);
            fogBlock.SetFloat("_PADDING", sphere.PADDING); fogBlock.SetFloat("_FogDepth", sphere.currentSize);
            fogBlock.SetFloat("_RevealAmount", sphere.REVEAL_AMOUNT); fogBlock.SetVector("_FogCenter", sphere.fogPoint);
            fogRenderer.SetPropertyBlock(fogBlock);
        }
        Shader.SetGlobalFloat("_FogSphereSize", sphere.currentSize); Shader.SetGlobalVector("FogCenter", sphere.fogPoint);
        Shader.SetGlobalFloat("FogEnabled", sphere.gameObject.activeInHierarchy ? sphere.ENABLE : 0); Shader.SetGlobalFloat("CloseDistanceMod", close);
    }

    public void Dispose()
    {
        if (disposed) return;
        // Restore the passive recorded fields while controllers remain stopped.
        try { Apply(initial, initial, 0, 0, Vector3.zero); } catch { }
        disposed = true;
        shaderClock?.Dispose();
        foreach (var wind in winds.Values) foreach (var storm in wind.Storms.Values)
        {
            if (!storm.Source) continue;
            storm.Source.transform.SetPositionAndRotation(storm.Position, storm.Rotation);
            if (storm.Source.quadRend) { storm.Source.quadRend.enabled = storm.QuadEnabled; storm.Source.quadRend.SetPropertyBlock(storm.InitialBlock); }
            if (storm.Source.particleForceField) { storm.Source.particleForceField.directionX = storm.ForceX; storm.Source.particleForceField.directionZ = storm.ForceZ; }
        }
        if (sphere) sphere!.transform.localScale = initialFogScale;
        if (fogRenderer) fogRenderer!.SetPropertyBlock(initialFogBlock);
        foreach (var surface in lavaSurfaces) if (surface.Renderer) surface.Renderer!.enabled = surface.Enabled;
        foreach (var pair in particleStates)
        {
            if (!pair.Key) continue;
            pair.Key.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            var emission = pair.Key.emission; emission.enabled = pair.Value.Emission;
            if (pair.Value.Playing || pair.Value.Paused) pair.Key.Play(false);
            if (pair.Value.Paused) pair.Key.Pause(false);
        }
        foreach (var item in suspended) if (item.Source) item.Source.enabled = item.Enabled;
        foreach (var value in floats) Shader.SetGlobalFloat(value.Key, value.Value);
        foreach (var value in vectors) Shader.SetGlobalVector(value.Key, value.Value);
        Shader.SetGlobalTexture("_WindTexture", windTexture); Shader.SetGlobalColor("WindTint", windTint);
    }

    private static float Blend(float left, float? right, float mix) => right.HasValue ? EnvironmentReplayMath.Lerp(left, right.Value, mix) : left;
    private void Missing(string key)
    {
        if (warnedMissing) return;
        warnedMissing = true;
        Warning = "部分原生环境资源与录像不匹配，已保留其余可回放环境。"; warning?.Invoke(Warning);
    }
    private static Vector3 Vec(float[] value) => new(value[0], value[1], value[2]);
    private static EnvironmentWindFrame? FindWind(EnvironmentReplayFrame? frame, string key) { if (frame != null) foreach (var value in frame.Winds) if (value.Key == key) return value; return null; }
    private static EnvironmentLavaFrame? FindLava(EnvironmentReplayFrame? frame, string key) { if (frame != null) foreach (var value in frame.Lava) if (value.Key == key) return value; return null; }
    private static EnvironmentStormFrame? FindStorm(EnvironmentWindFrame? frame, string key) { if (frame != null) foreach (var value in frame.Storms) if (value.Key == key) return value; return null; }
}
