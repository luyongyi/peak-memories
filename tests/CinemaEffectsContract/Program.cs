using PeakReplayLab.Cinema;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UObject = UnityEngine.Object;

int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action action)
{
    UObject.Registry.Clear(); SystemInfo.graphicsShaderLevel = 50; SystemInfo.supportedRenderTargetCount = 4;
    action(); passed++; Console.WriteLine("PASS " + name);
}
Volume Volume(string name) => UObject.Registry.OfType<Volume>().Single(value => value && value.gameObject.name == name);
T Effect<T>(string name) where T : VolumeComponent => Volume(name).profile!.components.OfType<T>().Single();

Test("original zero settings preserve native volume assets and native post flags", () =>
{
    var f = new Fixture(); using var effects = new CinemaEffects(f.Camera);
    effects.Apply(new(), 10);
    Check(effects.Warning.Length == 0, effects.Warning);
    Check(Volume("Cinema Color").weight == 0 && Volume("Cinema Focus").weight == 0, "Neutral settings still override native effects.");
    Check(!f.Data.renderPostProcessing && f.Data.requiresDepthOption == CameraOverrideOption.UsePipelineSettings, "Neutral mode forced native post/depth.");
    Check(ReferenceEquals(f.Native.sharedProfile, f.NativeProfile) && f.NativeColor.contrast.value == 37 && f.NativeColor.contrast.overrideState, "Native shared profile was changed.");
    Check((int)f.Data.volumeLayerMask == (1 | int.MinValue) && ReferenceEquals(f.Data.volumeTrigger, f.Camera.transform), "Camera did not preserve environment layers and follow the cinematic camera.");
    Check(f.Data.UpdateMode == VolumeFrameworkUpdateMode.EveryFrame, "Paused cinematic volumes would not update.");
});

Test("preset strength blends only the dedicated color volume and resets overrides", () =>
{
    var f = new Fixture(); using var effects = new CinemaEffects(f.Camera);
    effects.Apply(new() { Preset = CinemaEffectPreset.Warm, Intensity = .25f }, 10);
    var color = Effect<ColorAdjustments>("Cinema Color"); var balance = Effect<WhiteBalance>("Cinema Color");
    Check(Volume("Cinema Color").weight == .25f && color.contrast.value == 8 && balance.temperature.value == 22, "Preset intensity does not blend from the native profile.");
    Check(!color.postExposure.overrideState && !color.hueShift.overrideState && !balance.tint.overrideState, "Preset overrides unrelated parameters.");
    effects.Apply(new() { Exposure = 1 }, 10);
    Check(color.postExposure.overrideState && !color.contrast.overrideState && !color.saturation.overrideState && !balance.temperature.overrideState, "Changing presets left stale parameter overrides.");
    effects.Apply(new(), 10);
    Check(Volume("Cinema Color").weight == 0 && !color.active && !balance.active, "Original did not fully remove cinematic grading.");
});

Test("all presets and sliders are bounded without contaminating native data", () =>
{
    var f = new Fixture(); using var effects = new CinemaEffects(f.Camera);
    effects.Apply(new() { Preset = CinemaEffectPreset.Cool, Contrast = 100, Saturation = -100, Temperature = -100 }, 10);
    Check(Effect<ColorAdjustments>("Cinema Color").contrast.value == 100 && Effect<WhiteBalance>("Cinema Color").temperature.value == -100, "Preset offsets exceeded supported bounds.");
    effects.Apply(new() { Preset = CinemaEffectPreset.Mono }, 10);
    Check(Effect<ColorAdjustments>("Cinema Color").saturation.value == -100, "Mono preset is not monochrome.");
    effects.Apply(new() { Exposure = float.NaN, Intensity = float.PositiveInfinity }, 10);
    Check(Volume("Cinema Color").weight == 0, "Invalid input poisoned grading.");
    Check(f.NativeColor.contrast.value == 37, "Native profile was edited.");
});

Test("DOF has an independent weight and off restores the native depth option", () =>
{
    var f = new Fixture(); using var effects = new CinemaEffects(f.Camera);
    effects.Apply(new() { Intensity = 0, DofMode = CinemaEffectFocusMode.Manual, DofQuality = CinemaEffectQuality.High, BlurStrength = 1 }, 3);
    var focus = Effect<DepthOfField>("Cinema Focus");
    Check(focus.active && focus.mode.value == DepthOfFieldMode.Bokeh && focus.focusDistance.value == 3, "High DOF was not configured.");
    Check(Volume("Cinema Focus").weight == 1 && Volume("Cinema Color").weight == 0 && f.Data.requiresDepthOption == CameraOverrideOption.On, "Grading intensity affected DOF or depth was not requested.");
    effects.Apply(new() { DofMode = CinemaEffectFocusMode.Actor }, float.NaN);
    Check(focus.mode.value == DepthOfFieldMode.Gaussian && focus.gaussianStart.value == 10 && focus.gaussianEnd.value > 10, "Low quality or invalid focus fallback failed.");
    effects.Apply(new(), 5);
    Check(!focus.active && Volume("Cinema Focus").weight == 0 && f.Data.requiresDepthOption == CameraOverrideOption.UsePipelineSettings, "Turning DOF off left a forced depth texture.");
});

Test("medium Gaussian quality requests improved sampling independently of blur strength", () =>
{
    var f = new Fixture(); using var effects = new CinemaEffects(f.Camera);
    effects.Apply(new() { DofMode = CinemaEffectFocusMode.Manual, DofQuality = CinemaEffectQuality.Low, BlurStrength = .3f }, 8);
    var focus = Effect<DepthOfField>("Cinema Focus");
    float radius = focus.gaussianMaxRadius.value;
    Check(!focus.highQualitySampling.value, "Low quality unexpectedly used improved sampling.");
    effects.Apply(new() { DofMode = CinemaEffectFocusMode.Manual, DofQuality = CinemaEffectQuality.Medium, BlurStrength = .3f }, 8);
    Check(focus.mode.value == DepthOfFieldMode.Gaussian && focus.highQualitySampling.value && focus.gaussianMaxRadius.value == radius,
        "Medium quality did not improve sampling or changed the requested blur strength.");
});

Test("missing Bokeh shader degrades to supported Gaussian with a warning", () =>
{
    var f = new Fixture(); f.Resources.bokehDepthOfFieldPS = null;
    using var effects = new CinemaEffects(f.Camera);
    effects.Apply(new() { DofMode = CinemaEffectFocusMode.Actor, DofQuality = CinemaEffectQuality.High }, 8);
    Check(effects.Warning.Contains("低质量") && Effect<DepthOfField>("Cinema Focus").mode.value == DepthOfFieldMode.Gaussian, "Missing Bokeh silently failed.");
});

Test("invalid focus mode does not accidentally enable a depth effect", () =>
{
    var f = new Fixture(); using var effects = new CinemaEffects(f.Camera);
    effects.Apply(new() { DofMode = (CinemaEffectFocusMode)99 }, 10);
    Check(Volume("Cinema Focus").weight == 0 && f.Data.requiresDepthOption == CameraOverrideOption.UsePipelineSettings, "An invalid focus enum enabled DOF.");
});

Test("unsupported GPU disables DOF while retaining supported color", () =>
{
    var f = new Fixture(); SystemInfo.graphicsShaderLevel = 30;
    using var effects = new CinemaEffects(f.Camera);
    effects.Apply(new() { Preset = CinemaEffectPreset.Warm, DofMode = CinemaEffectFocusMode.Manual }, 8);
    Check(effects.Warning.Contains("不支持景深") && Volume("Cinema Focus").weight == 0 && Volume("Cinema Color").weight == 1, "Unsupported DOF disabled independent grading.");
});

Test("selected renderer shaders are checked instead of another renderer's resources", () =>
{
    var f = new Fixture(); var otherResources = ScriptableObject.CreateInstance<PostProcessData>();
    f.Asset.m_RendererDataList = new ScriptableRendererData[] { f.Renderer, new UniversalRendererData { postProcessData = otherResources } };
    f.Data.m_RendererIndex = 1;
    using var effects = new CinemaEffects(f.Camera);
    effects.Apply(new() { Preset = CinemaEffectPreset.Warm, DofMode = CinemaEffectFocusMode.Manual }, 8);
    Check(effects.Warning.Contains("不支持调色") && effects.Warning.Contains("不支持景深"), "Resources from a different renderer hid missing shaders.");
    Check(!f.Data.renderPostProcessing, "Missing shader effects were still enabled.");
});

Test("isolation avoids other camera masks and original volume layers", () =>
{
    var f = new Fixture(); var other = new GameObject("Reflection camera").AddComponent<Camera>();
    var otherData = other.gameObject.AddComponent<UniversalAdditionalCameraData>(); otherData.volumeLayerMask = int.MinValue | 1;
    using var effects = new CinemaEffects(f.Camera);
    Check(Volume("Cinema Color").gameObject.layer == 30 && (int)otherData.volumeLayerMask == (int.MinValue | 1), "Runtime effect can leak to another camera, or its mask was edited.");
});

Test("no isolation layer and no URP degrade without adding components or editing the camera", () =>
{
    var f = new Fixture(); var other = new GameObject("All layer camera").AddComponent<Camera>(); other.gameObject.AddComponent<UniversalAdditionalCameraData>().volumeLayerMask = -1;
    int count = UObject.Registry.Count;
    using var blocked = new CinemaEffects(f.Camera); blocked.Apply(new() { Preset = CinemaEffectPreset.Warm }, 10);
    Check(blocked.Warning.Contains("后处理层") && UObject.Registry.Count == count && (int)f.Data.volumeLayerMask == 1, "Layer allocation failure mutated the scene.");
    var bare = new GameObject("Bare camera").AddComponent<Camera>();
    using var unavailable = new CinemaEffects(bare);
    Check(unavailable.Warning.Contains("URP") && bare.GetComponent<UniversalAdditionalCameraData>() == null, "Missing URP added an unconfigured component.");
});

Test("dispose destroys all runtime resources and restores exact native values once", () =>
{
    var f = new Fixture(); f.Data.renderPostProcessing = true; f.Data.requiresDepthOption = CameraOverrideOption.Off;
    int start = UObject.Registry.Count; var effects = new CinemaEffects(f.Camera);
    var owned = UObject.Registry.Skip(start).ToArray();
    effects.Apply(new() { Preset = CinemaEffectPreset.Warm, DofMode = CinemaEffectFocusMode.Manual }, 4);
    effects.Dispose(); effects.Dispose(); effects.Apply(new() { Preset = CinemaEffectPreset.Cool }, 8);
    Check(owned.All(value => value.Destroyed), "A runtime volume/profile/component survived disposal.");
    Check(f.Data.renderPostProcessing && f.Data.requiresDepthOption == CameraOverrideOption.Off && (int)f.Data.volumeLayerMask == 1 && ReferenceEquals(f.Data.volumeTrigger, f.OriginalTrigger), "Native camera properties were not restored exactly.");
    Check(f.Data.UpdateMode == VolumeFrameworkUpdateMode.ViaScripting && f.Data.ModeWrites == 2, "Update mode was not restored or disposal repeated writes.");
    Check(!f.Native.Destroyed && !f.NativeProfile.Destroyed && !f.NativeColor.Destroyed, "Cleanup destroyed a native asset.");
});

Test("one failed restore setter cannot skip the other restores or resource release", () =>
{
    var f = new Fixture(); int start = UObject.Registry.Count;
    var effects = new CinemaEffects(f.Camera); var owned = UObject.Registry.Skip(start).ToArray(); f.Data.FailPostWrites = true;
    effects.Dispose();
    Check(owned.All(value => value.Destroyed) && (int)f.Data.volumeLayerMask == 1 && ReferenceEquals(f.Data.volumeTrigger, f.OriginalTrigger) && f.Data.UpdateMode == VolumeFrameworkUpdateMode.ViaScripting, "One setter failure skipped independent cleanup.");
    Check(effects.Warning.Contains("清理") && effects.Diagnostic.Contains("restore-incomplete"), "Cleanup failure was not reported accurately.");
});

Test("scene camera destruction does not prevent runtime resource release", () =>
{
    var f = new Fixture(); int start = UObject.Registry.Count;
    var effects = new CinemaEffects(f.Camera); var owned = UObject.Registry.Skip(start).ToArray();
    UObject.Destroy(f.Camera.gameObject); effects.Dispose();
    Check(owned.All(value => value.Destroyed) && !f.NativeProfile.Destroyed, "Destroyed camera prevented cleanup or destroyed native data.");
});

Test("runtime apply failure degrades to ordinary replay and releases owned state", () =>
{
    var f = new Fixture(); int start = UObject.Registry.Count;
    using var effects = new CinemaEffects(f.Camera); var owned = UObject.Registry.Skip(start).ToArray();
    effects.Apply(null!, 4);
    Check(effects.Warning.Contains("普通回放") && owned.All(value => value.Destroyed) && (int)f.Data.volumeLayerMask == 1 && f.Data.UpdateMode == VolumeFrameworkUpdateMode.ViaScripting, "Apply failure escaped or left cinematic state behind.");
});

Console.WriteLine($"TOTAL: {passed} cinematic effects lifecycle and isolation contracts passed. Unity rendering and performance require in-game acceptance.");

sealed class Fixture
{
    public readonly Camera Camera;
    public readonly UniversalAdditionalCameraData Data;
    public readonly UniversalRenderPipelineAsset Asset;
    public readonly UniversalRendererData Renderer;
    public readonly PostProcessData.ShaderResources Resources;
    public readonly Transform OriginalTrigger;
    public readonly Volume Native;
    public readonly VolumeProfile NativeProfile;
    public readonly ColorAdjustments NativeColor;
    public Fixture()
    {
        Camera = new GameObject("Native camera").AddComponent<Camera>(); Data = Camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
        OriginalTrigger = new GameObject("Native character trigger").transform; Data.volumeTrigger = OriginalTrigger;
        Data.requiresDepthOption = CameraOverrideOption.UsePipelineSettings; Data.UpdateMode = VolumeFrameworkUpdateMode.ViaScripting;
        var post = ScriptableObject.CreateInstance<PostProcessData>(); Resources = post.shaders;
        Resources.uberPostPS = new Shader(); Resources.lutBuilderHdrPS = new Shader(); Resources.lutBuilderLdrPS = new Shader(); Resources.gaussianDepthOfFieldPS = new Shader(); Resources.bokehDepthOfFieldPS = new Shader();
        Renderer = new UniversalRendererData { postProcessData = post }; Asset = new UniversalRenderPipelineAsset { m_RendererDataList = new ScriptableRendererData[] { Renderer } }; GraphicsSettings.currentRenderPipeline = Asset;
        Native = new GameObject("Native environment volume").AddComponent<Volume>(); Native.gameObject.layer = 0;
        NativeProfile = ScriptableObject.CreateInstance<VolumeProfile>(); Native.sharedProfile = NativeProfile;
        NativeColor = NativeProfile.Add<ColorAdjustments>(true); NativeColor.contrast.Override(37);
    }
}
