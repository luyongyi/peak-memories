using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace PeakReplayLab;

// Borrow the game's complete render setup instead of making a bare Camera.CopyFrom.
// Keep Camera.main/MainCamera.instance, URP data, camera stacks and reflection cameras intact.
internal sealed class ReplayView : IDisposable
{
    private readonly List<(Behaviour Component, bool Enabled)> behaviours = new();
    private readonly Dictionary<Material, float> eyeMaterials = new();
    private readonly Dictionary<ScriptableRendererFeature, bool> eyeFeatures = new();
    private readonly Action<Exception> report;
    private readonly Vector3 position;
    private readonly Quaternion rotation;
    private readonly int cullingMask;
    private readonly float nearClip, fieldOfView;
    private readonly bool enabled;
    private bool disposed;
    public Camera Camera { get; }

    public ReplayView(Action<Exception> report)
    {
        this.report = report;
        Camera = MainCamera.instance ? MainCamera.instance.GetComponent<Camera>() : UnityEngine.Camera.main;
        if (!Camera || !Camera.gameObject.activeInHierarchy) throw new InvalidOperationException("原生主摄像机尚未就绪，无法打开回放画面。");
        position = Camera.transform.position; rotation = Camera.transform.rotation;
        cullingMask = Camera.cullingMask; nearClip = Camera.nearClipPlane;
        fieldOfView = Camera.fieldOfView; enabled = Camera.enabled;
        try
        {
            var movement = Camera.GetComponent<MainCameraMovement>();
            if (movement) Suspend(movement);
            var character = Character.localCharacter;
            if (!character) throw new InvalidOperationException("本地场景角色尚未准备完成。");
            foreach (var eye in character.GetComponentsInChildren<EyeBlinkController>(true))
            {
                // The fresh scene starts the hidden live scout passed out on the beach.
                // At timeScale=0 EyeBlink.Update would hold a GLOBAL full-screen black pass
                // forever, even though we render entirely different recorded characters.
                if (eye.eyeBlinkMaterial && eye.eyeBlinkMaterial.HasProperty("_EyeOpen") && !eyeMaterials.ContainsKey(eye.eyeBlinkMaterial))
                    eyeMaterials.Add(eye.eyeBlinkMaterial, eye.eyeBlinkMaterial.GetFloat("_EyeOpen"));
                if (eye.rend)
                    foreach (var feature in eye.rend.rendererFeatures)
                        if (feature && feature.name == "Eye Blink" && !eyeFeatures.ContainsKey(feature)) eyeFeatures.Add(feature, feature.isActive);
                Suspend(eye); // Original OnDisable also opens the eye and disables the pass.
            }
            // These are the hidden live scout's status effects, not the recorded subject's
            // environment. Keep environmental volumes, fog and lighting fully enabled.
            foreach (var post in UnityEngine.Object.FindObjectsByType<PassOutPost>(FindObjectsSortMode.None)) SuspendStatus(post);
            foreach (var post in UnityEngine.Object.FindObjectsByType<FallPost>(FindObjectsSortMode.None)) SuspendStatus(post);
            Camera.cullingMask |= 1; // Visual-only actors are on Default.
            Camera.nearClipPlane = .05f;
            Enforce();
        }
        catch { Dispose(); throw; }
    }

    private void Suspend(Behaviour component)
    {
        if (behaviours.Any(s => s.Component == component)) return;
        behaviours.Add((component, component.enabled));
        component.enabled = false;
    }

    private void SuspendStatus(Behaviour controller)
    {
        Suspend(controller);
        var volume = controller.GetComponent<UnityEngine.Rendering.Volume>();
        if (volume) Suspend(volume);
    }

    public void Enforce()
    {
        if (disposed) return;
        if (!Camera) throw new InvalidOperationException("原生回放摄像机已被移除。");
        foreach (var saved in behaviours) if (saved.Component) saved.Component.enabled = false;
        foreach (var pair in eyeMaterials) if (pair.Key) pair.Key.SetFloat("_EyeOpen", 1);
        foreach (var pair in eyeFeatures) if (pair.Key) pair.Key.SetActive(false);
        Camera.enabled = true;
    }

    public string Describe()
    {
        var urp = Camera.GetComponent<UniversalAdditionalCameraData>();
        return $"nativeCamera={Camera.name}; main={UnityEngine.Camera.main == Camera}; enabled={Camera.enabled}; " +
            $"urp={(urp ? urp.renderType.ToString() : "none")}; target={(Camera.targetTexture ? Camera.targetTexture.name : "screen")}; " +
            $"mask={Camera.cullingMask}; position={Camera.transform.position}; eyePassesSuppressed={eyeFeatures.Count}; " +
            "eyeOpen=" + string.Join(",", eyeMaterials.Keys.Where(m => m).Select(m => m.GetFloat("_EyeOpen").ToString("F1")));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        void Restore(Action action) { try { action(); } catch (Exception e) { try { report(e); } catch { } } }
        Restore(() =>
        {
            if (!Camera) return;
            Camera.transform.SetPositionAndRotation(position, rotation);
            Camera.cullingMask = cullingMask; Camera.nearClipPlane = nearClip;
            Camera.fieldOfView = fieldOfView; Camera.enabled = enabled;
        });
        foreach (var saved in behaviours) Restore(() => { if (saved.Component) saved.Component.enabled = saved.Enabled; });
        foreach (var pair in eyeMaterials) Restore(() => { if (pair.Key) pair.Key.SetFloat("_EyeOpen", pair.Value); });
        foreach (var pair in eyeFeatures) Restore(() => { if (pair.Key) pair.Key.SetActive(pair.Value); });
    }
}
