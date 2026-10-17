using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityEngine
{
    public enum HideFlags { None, HideAndDontSave }
    public enum FindObjectsInactive { Exclude, Include }
    public enum FindObjectsSortMode { None }
    public class Object
    {
        public static readonly List<Object> Registry = new();
        public bool Destroyed;
        public string name = "";
        public HideFlags hideFlags;
        public Object() => Registry.Add(this);
        public static implicit operator bool(Object? value) => value is not null && !value.Destroyed;
        public static T[] FindObjectsByType<T>(FindObjectsInactive inactive, FindObjectsSortMode sort) where T : Object => Registry.OfType<T>().Where(value => value).ToArray();
        public static void Destroy(Object? value)
        {
            if (!value) return;
            value!.Destroyed = true;
            if (value is GameObject go)
            {
                foreach (var child in go.transform.Children.ToArray()) Destroy(child.gameObject);
                foreach (var component in go.Components) Destroy(component);
            }
        }
    }
    public class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject, new() => new();
    }
    public class Component : Object
    {
        public GameObject gameObject = null!;
        public Transform transform => gameObject.transform;
        public T? GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
    }
    public class Behaviour : Component { public bool enabled = true; }
    public class MonoBehaviour : Behaviour { }
    public class Transform : Component
    {
        public readonly List<Transform> Children = new();
        public void SetParent(Transform parent, bool preserve) => parent.Children.Add(this);
    }
    public class GameObject : Object
    {
        public int layer;
        public bool activeSelf = true;
        public readonly Transform transform;
        public readonly List<Component> Components = new();
        public GameObject(string name)
        {
            this.name = name; transform = new Transform { gameObject = this }; Components.Add(transform);
        }
        public void SetActive(bool active) => activeSelf = active;
        public T AddComponent<T>() where T : Component, new()
        {
            var component = new T { gameObject = this }; Components.Add(component); return component;
        }
        public T? GetComponent<T>() where T : Component => Components.OfType<T>().FirstOrDefault(value => value);
    }
    public class Camera : Behaviour { }
    public class Shader : Object { public bool isSupported = true; }
    public struct LayerMask
    {
        public int value;
        public static implicit operator int(LayerMask mask) => mask.value;
        public static implicit operator LayerMask(int value) => new() { value = value };
    }
    public static class SystemInfo
    {
        public static int graphicsShaderLevel = 50;
        public static int supportedRenderTargetCount = 4;
    }
}

namespace UnityEngine.Rendering
{
    using UnityEngine;
    public static class GraphicsSettings { public static Object? currentRenderPipeline; }
    public class VolumeParameter<T>
    {
        public T value;
        public bool overrideState;
        public VolumeParameter(T value) => this.value = value;
        public void Override(T next) { value = next; overrideState = true; }
    }
    public class FloatParameter : VolumeParameter<float> { public FloatParameter(float value = 0) : base(value) { } }
    public class ClampedFloatParameter : FloatParameter { public ClampedFloatParameter(float value = 0) : base(value) { } }
    public class BoolParameter : VolumeParameter<bool> { public BoolParameter(bool value = false) : base(value) { } }
    public class VolumeComponent : ScriptableObject { public bool active = true; }
    public class VolumeProfile : ScriptableObject
    {
        public readonly List<VolumeComponent> components = new();
        public T Add<T>(bool overrides) where T : VolumeComponent, new()
        { var result = new T(); components.Add(result); return result; }
    }
    public class Volume : MonoBehaviour
    {
        public bool isGlobal;
        public float priority, weight;
        public VolumeProfile? profile, sharedProfile;
    }
}

namespace UnityEngine.Rendering.Universal
{
    using UnityEngine;
    using UnityEngine.Rendering;
    public enum CameraOverrideOption { Off, On, UsePipelineSettings }
    public enum VolumeFrameworkUpdateMode { UsePipelineSettings, EveryFrame, ViaScripting }
    public enum ColorGradingMode { LowDynamicRange, HighDynamicRange }
    public enum DepthOfFieldMode { Off, Gaussian, Bokeh }
    public class UniversalAdditionalCameraData : MonoBehaviour
    {
        public int m_RendererIndex = -1;
        public bool FailPostWrites;
        private bool post;
        public bool renderPostProcessing { get => post; set { if (FailPostWrites) throw new InvalidOperationException("post setter failed"); post = value; } }
        public CameraOverrideOption requiresDepthOption;
        public LayerMask volumeLayerMask = 1;
        public Transform? volumeTrigger;
        public VolumeFrameworkUpdateMode UpdateMode;
        public int ModeWrites;
    }
    public static class CameraExtensions
    {
        public static VolumeFrameworkUpdateMode GetVolumeFrameworkUpdateMode(this Camera camera) => camera.GetComponent<UniversalAdditionalCameraData>()!.UpdateMode;
        public static void SetVolumeFrameworkUpdateMode(this Camera camera, VolumeFrameworkUpdateMode mode)
        { var data = camera.GetComponent<UniversalAdditionalCameraData>()!; data.UpdateMode = mode; data.ModeWrites++; }
    }
    public class ScriptableRendererData : ScriptableObject { }
    public class UniversalRendererData : ScriptableRendererData { public PostProcessData? postProcessData; }
    public class UniversalRenderPipelineAsset : ScriptableObject
    {
        public ScriptableRendererData[] m_RendererDataList = Array.Empty<ScriptableRendererData>();
        public int m_DefaultRendererIndex;
        public ColorGradingMode colorGradingMode;
    }
    public class PostProcessData : ScriptableObject
    {
        public ShaderResources shaders = new();
        public class ShaderResources
        {
            public Shader? uberPostPS, lutBuilderHdrPS, lutBuilderLdrPS, gaussianDepthOfFieldPS, bokehDepthOfFieldPS;
        }
    }
    public class ColorAdjustments : VolumeComponent
    {
        public FloatParameter postExposure = new();
        public ClampedFloatParameter contrast = new(), saturation = new(), hueShift = new();
    }
    public class WhiteBalance : VolumeComponent { public ClampedFloatParameter temperature = new(), tint = new(); }
    public class DepthOfField : VolumeComponent
    {
        public VolumeParameter<DepthOfFieldMode> mode = new(DepthOfFieldMode.Off);
        public FloatParameter focusDistance = new(), focalLength = new(), aperture = new(), gaussianStart = new(), gaussianEnd = new(), gaussianMaxRadius = new();
        public BoolParameter highQualitySampling = new();
    }
}
