using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UObject = UnityEngine.Object;

namespace PeakReplayLab.Cinema;

public enum CinemaEffectPreset { Original, Warm, Cool, Mono }
public enum CinemaEffectFocusMode { Off, Manual, Actor }
public enum CinemaEffectQuality { Low, Medium, High }

// This DTO deliberately has no dependency on the cinematic timeline or its UI.
public sealed class CinemaEffectSettings
{
    public CinemaEffectPreset Preset { get; set; }
    public float Intensity { get; set; } = 1;
    public float Exposure { get; set; }
    public float Contrast { get; set; }
    public float Saturation { get; set; }
    public float Temperature { get; set; }
    public CinemaEffectFocusMode DofMode { get; set; }
    public float BlurStrength { get; set; } = .5f;
    public CinemaEffectQuality DofQuality { get; set; } = CinemaEffectQuality.Low;
}

// Owns only runtime objects. Native profiles, materials, environment volumes and
// other cameras are never edited. Construct this only while cinematic mode owns
// the camera, and dispose it before ReplayView restores the native camera.
public sealed class CinemaEffects : IDisposable
{
    private readonly Camera camera;
    private UniversalAdditionalCameraData? data;
    private bool saved, disposed, faulted, cleanupFailed;
    private bool oldPost;
    private CameraOverrideOption oldDepth;
    private LayerMask oldMask;
    private Transform? oldTrigger;
    private VolumeFrameworkUpdateMode oldUpdateMode;
    private GameObject? root;
    private Volume? colorVolume, focusVolume;
    private VolumeProfile? colorProfile, focusProfile;
    private ColorAdjustments? color;
    private WhiteBalance? balance;
    private DepthOfField? focus;
    private bool colorSupported, gaussianSupported, bokehSupported;
    private string unavailable = "", capabilityWarning = "";
    public string Warning { get; private set; } = "";
    public string Diagnostic { get; private set; } = "cinema-effects=inactive";

    public CinemaEffects(Camera camera)
    {
        this.camera = camera;
        try
        {
            if (!camera) { Unavailable("电影效果未启用：原生摄像机不可用。"); return; }
            data = camera.GetComponent<UniversalAdditionalCameraData>();
            if (!data || !(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset asset))
            { Unavailable("电影效果未启用：当前摄像机没有可用的 URP 后处理。"); return; }
            var resources = RendererResources(asset, data!);
            if (resources == null)
            { Unavailable("电影效果未启用：无法核实当前 URP 渲染器的后处理资源。"); return; }
            colorSupported = Supported(resources.uberPostPS) && Supported(asset.colorGradingMode == ColorGradingMode.HighDynamicRange ? resources.lutBuilderHdrPS : resources.lutBuilderLdrPS);
            gaussianSupported = SystemInfo.graphicsShaderLevel >= 35 && SystemInfo.supportedRenderTargetCount > 1 && Supported(resources.gaussianDepthOfFieldPS);
            bokehSupported = SystemInfo.graphicsShaderLevel >= 35 && Supported(resources.bokehDepthOfFieldPS);
            int layer = IsolationLayer(camera);
            if (layer < 0) { Unavailable("电影效果未启用：没有可隔离于其他摄像机的后处理层。"); return; }

            oldPost = data!.renderPostProcessing; oldDepth = data.requiresDepthOption;
            oldMask = data.volumeLayerMask; oldTrigger = data.volumeTrigger;
            oldUpdateMode = camera.GetVolumeFrameworkUpdateMode(); saved = true;
            root = new GameObject("PEAK Memories - cinematic effects") { hideFlags = HideFlags.HideAndDontSave, layer = layer };
            root.SetActive(false);
            colorProfile = ScriptableObject.CreateInstance<VolumeProfile>(); colorProfile.name = "Cinema runtime color";
            focusProfile = ScriptableObject.CreateInstance<VolumeProfile>(); focusProfile.name = "Cinema runtime focus";
            colorProfile.hideFlags = focusProfile.hideFlags = HideFlags.HideAndDontSave;
            color = colorProfile.Add<ColorAdjustments>(false); balance = colorProfile.Add<WhiteBalance>(false);
            focus = focusProfile.Add<DepthOfField>(false);
            colorVolume = MakeVolume("Color", root.transform, layer, colorProfile);
            focusVolume = MakeVolume("Focus", root.transform, layer, focusProfile);
            // Separate weights preserve the native grading beneath an intensity
            // blend without also blending the DOF focus distance toward it.
            colorVolume.weight = focusVolume.weight = 0;
            data.volumeLayerMask = (int)oldMask | (1 << layer);
            data.volumeTrigger = camera.transform;
            camera.SetVolumeFrameworkUpdateMode(VolumeFrameworkUpdateMode.EveryFrame);
            root.SetActive(true);
            Diagnostic = $"cinema-effects=ready; layer={layer}; color={colorSupported}; gaussian={gaussianSupported}; bokeh={bokehSupported}";
        }
        catch (Exception error)
        {
            faulted = true; ReleaseResources(); RestoreCamera();
            Unavailable("电影效果初始化失败，已保留普通回放：" + error.Message);
        }
    }

    public void Apply(CinemaEffectSettings settings, float focusDistance)
    {
        if (disposed || faulted || unavailable.Length != 0) return;
        try
        {
            if (!camera || !data || !root || settings == null)
                throw new InvalidOperationException("电影效果的摄像机或设置已失效。");
            capabilityWarning = "";
            float intensity = Safe(settings.Intensity, 0, 1, 1);
            float exposure = Safe(settings.Exposure, -5, 5, 0);
            float contrast = Safe(settings.Contrast, -100, 100, 0);
            float saturation = Safe(settings.Saturation, -100, 100, 0);
            float temperature = Safe(settings.Temperature, -100, 100, 0);
            switch (settings.Preset)
            {
                case CinemaEffectPreset.Warm: temperature += 22; contrast += 8; saturation += 6; break;
                case CinemaEffectPreset.Cool: temperature -= 22; contrast += 10; saturation -= 8; break;
                case CinemaEffectPreset.Mono: saturation = -100; contrast += 12; break;
            }
            bool wantsColor = intensity > 0 && (exposure != 0 || contrast != 0 || saturation != 0 || temperature != 0);
            bool useColor = wantsColor && colorSupported;
            if (wantsColor && !useColor) Warn("当前渲染器不支持调色，已保留原生颜色。");
            Set(color!.postExposure, exposure, useColor && exposure != 0);
            Set(color.contrast, Safe(contrast, -100, 100, 0), useColor && contrast != 0);
            Set(color.saturation, Safe(saturation, -100, 100, 0), useColor && saturation != 0);
            Set(balance!.temperature, Safe(temperature, -100, 100, 0), useColor && temperature != 0);
            color.active = useColor; balance.active = useColor;
            colorVolume!.weight = useColor ? intensity : 0;

            float blur = Safe(settings.BlurStrength, 0, 1, .5f);
            bool wantsFocus = (settings.DofMode == CinemaEffectFocusMode.Manual || settings.DofMode == CinemaEffectFocusMode.Actor) && blur > 0;
            bool high = settings.DofQuality == CinemaEffectQuality.High && bokehSupported;
            bool useFocus = wantsFocus && (high || gaussianSupported);
            if (wantsFocus && !useFocus) Warn("当前渲染器或设备不支持景深，已关闭电影景深。");
            else if (wantsFocus && settings.DofQuality == CinemaEffectQuality.High && !high)
                Warn("高质量景深不可用，已降为低质量背景景深。");
            focus!.active = useFocus;
            focusVolume!.weight = useFocus ? 1 : 0;
            // Off means no cinematic DOF override, so the native environment is
            // retained. Low uses Gaussian background blur; High uses Bokeh.
            focus.mode.Override(high ? DepthOfFieldMode.Bokeh : DepthOfFieldMode.Gaussian);
            float distance = Safe(focusDistance, .5f, 5000, 10);
            focus.focusDistance.Override(distance);
            focus.focalLength.Override(50);
            focus.aperture.Override(16 - blur * 14.6f);
            focus.gaussianStart.Override(distance);
            focus.gaussianEnd.Override(distance + Math.Max(1, distance * (1.5f - blur)));
            focus.gaussianMaxRadius.Override(.5f + blur);
            focus.highQualitySampling.Override(settings.DofQuality != CinemaEffectQuality.Low);
            data!.renderPostProcessing = oldPost || useColor || useFocus;
            data.requiresDepthOption = useFocus ? CameraOverrideOption.On : oldDepth;
            Warning = capabilityWarning;
            Diagnostic = $"cinema-effects=active; color={useColor}; weight={colorVolume.weight:F2}; dof={(useFocus ? high ? "bokeh" : "gaussian" : "off")}; focus={distance:F2}; native-volumes=preserved";
        }
        catch (Exception error)
        {
            faulted = true; ReleaseResources(); RestoreCamera();
            Unavailable("电影效果已降级为普通回放：" + error.Message);
        }
    }

    private void Unavailable(string warning) { unavailable = warning; Warning = warning; Diagnostic = "cinema-effects=unavailable; " + warning; }
    private void Warn(string warning) { capabilityWarning = capabilityWarning.Length == 0 ? warning : capabilityWarning + " " + warning; }
    private static float Safe(float value, float min, float max, float fallback) => float.IsNaN(value) || float.IsInfinity(value) ? fallback : Math.Max(min, Math.Min(max, value));
    private static bool Supported(Shader? shader) => shader && shader!.isSupported;
    private static void Set(FloatParameter parameter, float value, bool enabled) { parameter.value = value; parameter.overrideState = enabled; }

    private static Volume MakeVolume(string name, Transform parent, int layer, VolumeProfile profile)
    {
        var child = new GameObject("Cinema " + name) { hideFlags = HideFlags.HideAndDontSave, layer = layer };
        child.transform.SetParent(parent, false);
        var volume = child.AddComponent<Volume>(); volume.isGlobal = true;
        volume.priority = 100000; volume.weight = 0; volume.profile = profile;
        return volume;
    }

    private static int IsolationLayer(Camera target)
    {
        int used = 0;
        foreach (var volume in UObject.FindObjectsByType<Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None)) used |= 1 << volume.gameObject.layer;
        foreach (var other in UObject.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (other == target) continue;
            var otherData = other.GetComponent<UniversalAdditionalCameraData>();
            used |= otherData ? (int)otherData!.volumeLayerMask : 1;
        }
        for (int layer = 31; layer >= 0; layer--) if ((used & (1 << layer)) == 0) return layer;
        return -1;
    }

    // Read only the selected renderer's actual shader references, rather than
    // assuming a Shader.Find hit means that this renderer has the required pass.
    private static PostProcessData.ShaderResources? RendererResources(UniversalRenderPipelineAsset asset, UniversalAdditionalCameraData cameraData)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var list = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", fields)?.GetValue(asset) as ScriptableRendererData[];
        var cameraIndex = typeof(UniversalAdditionalCameraData).GetField("m_RendererIndex", fields)?.GetValue(cameraData);
        var defaultIndex = typeof(UniversalRenderPipelineAsset).GetField("m_DefaultRendererIndex", fields)?.GetValue(asset);
        if (list == null || !(cameraIndex is int index) || !(defaultIndex is int fallback)) return null;
        if (index < 0 || index >= list.Length || !list[index]) index = fallback;
        if (index < 0 || index >= list.Length || !(list[index] is UniversalRendererData renderer)) return null;
        return renderer.postProcessData ? renderer.postProcessData!.shaders : null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; ReleaseResources(); RestoreCamera();
        Diagnostic = "cinema-effects=disposed; native-camera=" + (cleanupFailed ? "restore-incomplete" : "restored");
    }

    private void Attempt(Action action)
    {
        try { action(); }
        catch (Exception error) { cleanupFailed = true; Warning = "电影效果清理未完全成功：" + error.Message; }
    }

    private void RestoreCamera()
    {
        if (!saved || !data) return;
        Attempt(() => data!.renderPostProcessing = oldPost);
        Attempt(() => data!.requiresDepthOption = oldDepth);
        Attempt(() => data!.volumeLayerMask = oldMask);
        Attempt(() => data!.volumeTrigger = oldTrigger);
        Attempt(() => { if (camera) camera.SetVolumeFrameworkUpdateMode(oldUpdateMode); });
        saved = false;
    }

    private void ReleaseResources()
    {
        Attempt(() => { if (root) root!.SetActive(false); }); // Unregister before deferred Unity destruction.
        Attempt(() => { if (colorVolume) colorVolume!.profile = null!; });
        Attempt(() => { if (focusVolume) focusVolume!.profile = null!; });
        DestroyProfile(colorProfile); DestroyProfile(focusProfile);
        Attempt(() => { if (root) UObject.Destroy(root); });
        colorProfile = focusProfile = null; root = null; colorVolume = focusVolume = null;
        color = null; balance = null; focus = null;
    }

    private void DestroyProfile(VolumeProfile? profile)
    {
        if (!profile) return;
        // VolumeProfile.OnDisable also clears components; explicitly own each
        // runtime component so player builds do not leave ScriptableObjects alive.
        foreach (var component in profile!.components) Attempt(() => { if (component) UObject.Destroy(component); });
        profile.components.Clear(); Attempt(() => UObject.Destroy(profile));
    }
}
