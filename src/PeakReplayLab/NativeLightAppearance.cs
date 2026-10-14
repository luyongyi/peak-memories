using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakReplayLab;

internal static class NativeLightAppearance
{
    private sealed class Entry { public Light Source = null!; public string Path = ""; public int Index; }
    private sealed class Probe
    {
        public readonly List<Entry> Lights = new();
        public int MaximumNodes;
        public Probe(Transform root, int maximumNodes)
        {
            MaximumNodes = maximumNodes; int count = 0;
            Visit(root, ".", 0);
            void Visit(Transform node, string path, int depth)
            {
                if (node != root && node.GetComponent<Item>()) return;
                if (depth > 64 || ++count > maximumNodes) throw new InvalidOperationException("Native light hierarchy exceeds its recording limit.");
                int index = 0;
                foreach (var light in node.GetComponents<Light>())
                {
                    if (Lights.Count >= NativeLightRules.MaximumLights) throw new InvalidOperationException("Native light count exceeds its recording limit.");
                    Lights.Add(new Entry { Source = light, Path = path, Index = index++ });
                }
                for (int i = 0; i < node.childCount; i++)
                { var child = node.GetChild(i); Visit(child, (path == "." ? "" : path + "/") + i.ToString(CultureInfo.InvariantCulture) + ":" + Uri.EscapeDataString(child.name), depth + 1); }
            }
        }
    }
    private static readonly ConditionalWeakTable<Transform, Probe> probes = new();
    private sealed class HiddenLight { public bool Enabled; public int Owners; }
    private static readonly ConditionalWeakTable<Light, HiddenLight> hiddenLights = new();
    private static bool AuthoredEnabled(Light light) => hiddenLights.TryGetValue(light, out var hidden) ? hidden.Enabled : light.enabled;
    public static void InvalidateCapture(Transform root) { if (root) probes.Remove(root); }
    private sealed class CookieIdentity { public string Name = ""; }
    private static readonly ConditionalWeakTable<Texture, CookieIdentity> cookieNames = new();
    private static string CookieName(Texture? cookie) => cookie ? cookieNames.GetValue(cookie!, source =>
        new CookieIdentity { Name = source.name.Replace(" (Instance)", "").Replace("(Instance)", "").Trim() }).Name : "";
    private static bool RelativeActive(Transform source, Transform root)
    { for (var node = source; node && node != root; node = node.parent) if (!node.gameObject.activeSelf) return false; return true; }
    public static NativeLightFrame[] Capture(Transform root, NativeLightFrame[]? previous = null, int maximumNodes = 256, bool includeHierarchyActivity = true)
    {
        if (!root) throw new ArgumentNullException(nameof(root));
        if (maximumNodes < 1 || maximumNodes > 1024) throw new ArgumentOutOfRangeException(nameof(maximumNodes));
        if (!probes.TryGetValue(root, out var probe))
        { probe = new Probe(root, maximumNodes); probes.Add(root, probe); }
        else if (maximumNodes > probe.MaximumNodes)
        { probes.Remove(root); probe = new Probe(root, maximumNodes); probes.Add(root, probe); }
        // Most placed/items have no native lights. Their cached directory returns
        // a shared known-empty snapshot before any light values/texture reads.
        if (probe.Lights.Count == 0) return Array.Empty<NativeLightFrame>();
        using var timing = ReplayPerformance.Measure(ReplayStage.Lights);
        NativeLightFrame[]? changed = previous == null || previous.Length != probe.Lights.Count ? new NativeLightFrame[probe.Lights.Count] : null;
        for (int i = 0; i < probe.Lights.Count; i++)
        {
            var entry = probe.Lights[i]; var light = entry.Source;
            if (!light) { probes.Remove(root); return previous ?? Array.Empty<NativeLightFrame>(); }
            var old = previous != null && i < previous.Length ? previous[i] : null;
            var color = light.color; string cookie = CookieName(light.cookie); var cookieSize = light.cookieSize2D;
            bool enabled = light.enabled && (!includeHierarchyActivity || RelativeActive(light.transform, root));
            var frame = old != null && old.Path == entry.Path && old.Index == entry.Index && old.Enabled == enabled && old.Type == (int)light.type &&
                old.Intensity == light.intensity && old.Range == light.range && old.SpotAngle == light.spotAngle && old.InnerSpotAngle == light.innerSpotAngle &&
                old.BounceIntensity == light.bounceIntensity && old.Shadows == (int)light.shadows && old.ShadowStrength == light.shadowStrength &&
                old.ShadowBias == light.shadowBias && old.ShadowNormalBias == light.shadowNormalBias && old.ShadowNearPlane == light.shadowNearPlane &&
                old.CullingMask == light.cullingMask && old.RenderingLayerMask == unchecked((uint)light.renderingLayerMask) && old.Cookie == cookie &&
                old.CookieSize == cookieSize.x && old.CookieSize2D.Length == 2 && old.CookieSize2D[0] == cookieSize.x && old.CookieSize2D[1] == cookieSize.y &&
                old.RenderMode == (int)light.renderMode && old.UseColorTemperature == light.useColorTemperature && old.ColorTemperature == light.colorTemperature &&
                old.Color.Length == 4 && old.Color[0] == color.r && old.Color[1] == color.g && old.Color[2] == color.b && old.Color[3] == color.a ? old :
                new NativeLightFrame { Path = entry.Path, Index = entry.Index, Enabled = enabled, Type = (int)light.type,
                    Color = new[] { color.r, color.g, color.b, color.a }, Intensity = light.intensity, Range = light.range, SpotAngle = light.spotAngle,
                    InnerSpotAngle = light.innerSpotAngle, BounceIntensity = light.bounceIntensity, Shadows = (int)light.shadows, ShadowStrength = light.shadowStrength,
                    ShadowBias = light.shadowBias, ShadowNormalBias = light.shadowNormalBias, ShadowNearPlane = light.shadowNearPlane, CullingMask = light.cullingMask,
                    RenderingLayerMask = unchecked((uint)light.renderingLayerMask), Cookie = cookie, CookieSize = cookieSize.x, CookieSize2D = new[] { cookieSize.x, cookieSize.y }, RenderMode = (int)light.renderMode,
                    UseColorTemperature = light.useColorTemperature, ColorTemperature = light.colorTemperature };
            if (changed != null) changed[i] = frame;
            else if (!ReferenceEquals(frame, old)) { changed = (NativeLightFrame[])previous!.Clone(); changed[i] = frame; }
        }
        return changed ?? previous!;
    }
    internal static void Copy(Light source, Light target)
    {
        target.type = source.type; target.color = source.color; target.intensity = source.intensity;
        target.range = source.range; target.spotAngle = source.spotAngle; target.innerSpotAngle = source.innerSpotAngle; target.bounceIntensity = source.bounceIntensity;
        target.shadows = source.shadows; target.shadowStrength = source.shadowStrength; target.shadowBias = source.shadowBias;
        target.shadowNormalBias = source.shadowNormalBias; target.shadowNearPlane = source.shadowNearPlane; target.shadowResolution = source.shadowResolution;
        target.cullingMask = source.cullingMask; target.renderingLayerMask = source.renderingLayerMask; target.cookie = source.cookie; target.cookieSize2D = source.cookieSize2D;
        target.renderMode = source.renderMode; target.useColorTemperature = source.useColorTemperature; target.colorTemperature = source.colorTemperature;
        target.enabled = AuthoredEnabled(source);
    }
    internal sealed class Playback : IDisposable
    {
        private readonly VisualReplica replica;
        private readonly Dictionary<string, Texture> cookies = new(StringComparer.Ordinal);
        private readonly NativeRendererVisibilityPolicy<Light> visibility = new(
            static light => light.enabled, static (light, enabled) => light.enabled = enabled);
        private readonly NativeLightFrame[] baseline;
        private NativeLightFrame[]? applied;
        private readonly NativeVisualResourceCatalog<Texture>.Lease sharedTextures;
        private NativeLightFrame[]? preparing, requested;
        private bool preparingActive, stateDirty;
        private int prepareLight, prepareAbsent;
        private int initializeLight;
        public bool IsReady { get; private set; }
        public Playback(VisualReplica replica, GameObject nativeSource, bool incremental = false)
        {
            if (!replica.IsReady) throw new InvalidOperationException("Light baselines require a completed visual hierarchy.");
            this.replica = replica;
            sharedTextures = NativeVisualAppearance.AcquireTextureCatalog(SceneManager.GetActiveScene().handle);
            baseline = new NativeLightFrame[replica.LightDirectory.Count];
            if (!incremental) while (!IsReady) InitializeNext();
        }
        public bool ContinueBuild(PresentationWorkBudget budget)
        { while (!IsReady && budget.Build()) InitializeNext(); return IsReady; }
        private void InitializeNext()
        {
            if (initializeLight >= replica.LightDirectory.Count) { IsReady = true; return; }
            var entry = replica.LightDirectory[initializeLight]; var light = entry.Clone; Register(entry.Source.cookie);
            var color = light.color; var size = light.cookieSize2D;
            baseline[initializeLight++] = new NativeLightFrame { Path = entry.Path, Index = entry.Index, Enabled = light.enabled, Type = (int)light.type,
                Color = new[] { color.r, color.g, color.b, color.a }, Intensity = light.intensity, Range = light.range, SpotAngle = light.spotAngle,
                InnerSpotAngle = light.innerSpotAngle, BounceIntensity = light.bounceIntensity, Shadows = (int)light.shadows, ShadowStrength = light.shadowStrength,
                ShadowBias = light.shadowBias, ShadowNormalBias = light.shadowNormalBias, ShadowNearPlane = light.shadowNearPlane, CullingMask = light.cullingMask,
                RenderingLayerMask = unchecked((uint)light.renderingLayerMask), Cookie = CookieName(light.cookie), CookieSize = size.x, CookieSize2D = new[] { size.x, size.y },
                RenderMode = (int)light.renderMode, UseColorTemperature = light.useColorTemperature, ColorTemperature = light.colorTemperature };
        }
        private void Register(Texture? texture)
        {
            if (!texture) return;
            string key = CookieName(texture); if (!cookies.ContainsKey(key)) cookies.Add(key, texture!);
            sharedTextures.Register(key, texture!);
        }
        private Texture? FindCookie(string key)
        {
            if (cookies.TryGetValue(key, out var local) && local) return local;
            return sharedTextures.TryGet(key, out var shared) ? shared : null;
        }
        public void CancelPending() { preparingActive = false; preparing = requested = null; }
        public bool TryApply(NativeLightFrame[]? frames, PresentationWorkBudget budget, bool restart = false)
        {
            if (!IsReady) throw new InvalidOperationException("An unfinished light baseline cannot be applied.");
            if (restart) CancelPending();
            if (!preparingActive && !stateDirty && ReferenceEquals(applied, frames)) return true;
            if (!preparingActive)
            { requested = frames; preparing = frames ?? baseline; preparingActive = true; stateDirty = true; prepareLight = prepareAbsent = 0; visibility.Begin(); }
            while (prepareLight < preparing!.Length)
            {
                var frame = preparing[prepareLight];
                if (frame.Cookie.Length != 0 && !FindCookie(frame.Cookie) &&
                    !sharedTextures.ExpandIncremental(() => Resources.FindObjectsOfTypeAll<Texture>(), NativeVisualAppearance.TextureIdentity, budget.Build)) return false;
                if (!budget.Build()) return false;
                ApplyLight(frame); prepareLight++;
            }
            // A shared work budget can yield between any two lights. Keep the
            // previous valid illumination until its replacement is ready; an
            // off-then-on reset would leave retained visuals dark while yielding.
            while (prepareAbsent < baseline.Length)
            {
                if (!budget.Build()) return false;
                var prior = baseline[prepareAbsent++];
                if (replica.TryCloneLight(prior.Path, prior.Index, out var previous)) visibility.HideAbsent(previous);
            }
            applied = requested; preparing = requested = null; preparingActive = false; stateDirty = false; return true;
        }
        public void Apply(NativeLightFrame[]? frames)
        {
            if (!IsReady) throw new InvalidOperationException("An unfinished light baseline cannot be applied.");
            if (!stateDirty && ReferenceEquals(applied, frames)) return;
            if (frames == null) { if (applied != null || stateDirty) Apply(baseline); applied = null; stateDirty = false; return; }
            // A known empty/partial snapshot clears lights that existed at another
            // seek point. Null alone means use authored lighting from an old recording.
            visibility.Begin();
            foreach (var frame in frames) ApplyLight(frame);
            foreach (var authored in baseline)
                if (replica.TryCloneLight(authored.Path, authored.Index, out var previous)) visibility.HideAbsent(previous);
            applied = frames; stateDirty = false;
        }
        private void ApplyLight(NativeLightFrame frame)
        {
            if (!replica.TryCloneLight(frame.Path, frame.Index, out var light)) return;
            light.type = (LightType)frame.Type; light.color = new Color(frame.Color[0], frame.Color[1], frame.Color[2], frame.Color[3]);
            light.intensity = frame.Intensity; light.range = frame.Range; light.spotAngle = frame.SpotAngle; light.innerSpotAngle = frame.InnerSpotAngle;
            light.bounceIntensity = frame.BounceIntensity; light.shadows = (LightShadows)frame.Shadows; light.shadowStrength = frame.ShadowStrength;
            light.shadowBias = frame.ShadowBias; light.shadowNormalBias = frame.ShadowNormalBias; light.shadowNearPlane = frame.ShadowNearPlane;
            light.cullingMask = frame.CullingMask; light.renderingLayerMask = unchecked((int)frame.RenderingLayerMask); light.cookieSize2D = new Vector2(frame.CookieSize2D[0], frame.CookieSize2D[1]);
            light.renderMode = (LightRenderMode)frame.RenderMode; light.useColorTemperature = frame.UseColorTemperature; light.colorTemperature = frame.ColorTemperature;
            if (frame.Cookie.Length == 0) light.cookie = null;
            else
            {
                var cookie = FindCookie(frame.Cookie); light.cookie = cookie ? cookie : null;
            }
            visibility.SetDesired(light, frame.Enabled);
        }
        public void Dispose() { cookies.Clear(); visibility.Clear(); applied = null; preparing = requested = null; preparingActive = false; sharedTextures.Dispose(); }
    }
    internal sealed class Originals : IDisposable
    {
        private readonly HashSet<Light> owned = new();
        public void Hide(GameObject root)
        {
            foreach (var light in root.GetComponentsInChildren<Light>(true))
            {
                bool nestedItem = false;
                for (var node = light.transform; node && node != root.transform; node = node.parent)
                    if (node.GetComponent<Item>()) { nestedItem = true; break; }
                if (nestedItem || !owned.Add(light)) continue;
                var hidden = hiddenLights.GetValue(light, source => new HiddenLight { Enabled = source.enabled });
                hidden.Owners++; light.enabled = false;
            }
        }
        public void Enforce() { foreach (var source in owned) if (source && source.enabled) source.enabled = false; }
        public void Dispose()
        {
            foreach (var source in owned)
                if (hiddenLights.TryGetValue(source, out var hidden) && --hidden.Owners == 0)
                { if (source) source.enabled = hidden.Enabled; hiddenLights.Remove(source!); }
            owned.Clear();
        }
    }
}
