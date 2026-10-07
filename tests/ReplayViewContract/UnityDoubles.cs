// Deliberately small, in-memory API doubles. These exercise ReplayView ownership and
// restoration contracts, NOT Unity lifecycle timing, URP execution, or actual rendering.
namespace UnityEngine
{
    public class Object
    {
        internal static readonly List<Object> Registry = new();
        public string name = "";
        public bool Destroyed { get; private set; }
        protected Object() => Registry.Add(this);
        public static implicit operator bool(Object? value) => value is not null && !value.Destroyed;
        public static bool operator !(Object? value) => !(bool)value;
        public static void Destroy(Object value) => value.Destroyed = true;
        public static T[] FindObjectsByType<T>(FindObjectsSortMode _) where T : Object => Registry.OfType<T>()
            .Where(o => o && (o is not Component c || c.gameObject.activeInHierarchy)).ToArray();
        internal static void Reset() => Registry.Clear();
    }

    public enum FindObjectsSortMode { None }
    public readonly record struct Vector3(float X, float Y, float Z);
    public readonly record struct Quaternion(float X, float Y, float Z, float W);

    public sealed class Transform
    {
        public Vector3 position;
        public Quaternion rotation;
        public void SetPositionAndRotation(Vector3 p, Quaternion r) { position = p; rotation = r; }
    }

    public sealed class GameObject : Object
    {
        private readonly List<Component> components = new();
        private readonly List<GameObject> children = new();
        private GameObject? parent;
        public bool activeSelf = true;
        public bool activeInHierarchy => activeSelf && (parent?.activeInHierarchy ?? true);
        public Transform transform { get; } = new();
        public GameObject(string name) => this.name = name;
        public T AddComponent<T>() where T : Component, new()
        {
            var component = new T { gameObject = this, name = name };
            components.Add(component);
            return component;
        }
        public T GetComponent<T>() where T : Component => components.OfType<T>().FirstOrDefault()!;
        public void AddChild(GameObject child) { child.parent = this; children.Add(child); }
        public T[] GetComponentsInChildren<T>(bool includeInactive) where T : Component
        {
            var result = new List<T>();
            if (includeInactive || activeInHierarchy) result.AddRange(components.OfType<T>());
            foreach (var child in children) result.AddRange(child.GetComponentsInChildren<T>(includeInactive));
            return result.ToArray();
        }
    }

    public class Component : Object
    {
        public GameObject gameObject = null!;
        public Transform transform => gameObject.transform;
        public T GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
        public T[] GetComponentsInChildren<T>(bool includeInactive) where T : Component => gameObject.GetComponentsInChildren<T>(includeInactive);
    }

    public class Behaviour : Component
    {
        private bool isEnabled = true;
        public bool ThrowOnNextEnabledSet;
        public bool enabled
        {
            get => isEnabled;
            set
            {
                if (ThrowOnNextEnabledSet) { ThrowOnNextEnabledSet = false; throw new InvalidOperationException("Synthetic restore failure"); }
                if (value == isEnabled) return;
                isEnabled = value;
                OnEnabledChanged(value);
            }
        }
        protected virtual void OnEnabledChanged(bool value) { }
    }

    public class MonoBehaviour : Behaviour { }
    public class ScriptableObject : Object { }
    public sealed class CanvasGroup : Behaviour
    {
        public float alpha = 1;
        public bool interactable = true, blocksRaycasts = true, ignoreParentGroups;
    }

    // Match the installed Unity player's managed rejection rule. This is a
    // serialization contract double; it does not execute native UI rendering.
    public static class JsonUtility
    {
        public static int ToJsonCalls, OverwriteCalls;
        public static void ResetCalls() { ToJsonCalls = 0; OverwriteCalls = 0; }
        public static string ToJson(object obj)
        {
            ToJsonCalls++;
            if (obj is Object && obj is not MonoBehaviour && obj is not ScriptableObject)
                throw new ArgumentException("JsonUtility.ToJson does not support engine types.");
            var fields = obj.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly).ToDictionary(field => field.Name, field => field.GetValue(obj));
            return System.Text.Json.JsonSerializer.Serialize(fields);
        }
        public static void FromJsonOverwrite(string json, object target)
        {
            OverwriteCalls++;
            if (target is Object && target is not MonoBehaviour && target is not ScriptableObject)
                throw new ArgumentException("Engine types cannot be overwritten from JSON outside of the Editor.");
            var fields = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json)!;
            foreach (var field in target.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly))
                if (fields.TryGetValue(field.Name, out var value))
                    field.SetValue(target, System.Text.Json.JsonSerializer.Deserialize(value.GetRawText(), field.FieldType));
        }
    }

    public sealed class Camera : Behaviour
    {
        public static Camera main = null!;
        public int cullingMask;
        public float nearClipPlane;
        public float fieldOfView;
        public RenderTexture targetTexture = null!;
    }
    public sealed class RenderTexture : Object { }
    public sealed class Material : Object
    {
        private readonly Dictionary<string, float> floats = new();
        public bool HasProperty(string key) => floats.ContainsKey(key);
        public float GetFloat(string key) => floats[key];
        public void SetFloat(string key, float value) => floats[key] = value;
    }
}

namespace UnityEngine.Rendering
{
    public sealed class Volume : UnityEngine.Behaviour { }
}

namespace UnityEngine.Rendering.Universal
{
    public enum CameraRenderType { Base, Overlay }
    public sealed class UniversalAdditionalCameraData : UnityEngine.Behaviour
    {
        public CameraRenderType renderType;
        public List<UnityEngine.Camera> cameraStack { get; } = new();
    }
    public sealed class ScriptableRendererFeature : UnityEngine.Object
    {
        public bool isActive { get; private set; } = true;
        public void SetActive(bool value) => isActive = value;
    }
    public sealed class ScriptableRendererData : UnityEngine.Object
    {
        public List<ScriptableRendererFeature> rendererFeatures { get; } = new();
    }
}

public sealed class MainCamera : UnityEngine.Behaviour { public static MainCamera instance = null!; }
public sealed class MainCameraMovement : UnityEngine.Behaviour { }
public sealed class Character : UnityEngine.Behaviour { public static Character localCharacter = null!; }
public sealed class PassOutPost : UnityEngine.Behaviour { }
public sealed class FallPost : UnityEngine.Behaviour { }
public sealed class EyeBlinkController : UnityEngine.Behaviour
{
    public UnityEngine.Material eyeBlinkMaterial = null!;
    public UnityEngine.Rendering.Universal.ScriptableRendererData rend = null!;
    protected override void OnEnabledChanged(bool value)
    {
        // Model the known game OnDisable side effects so the tests detect snapshots
        // accidentally being taken after the controller has already been suspended.
        if (value) return;
        if (eyeBlinkMaterial && eyeBlinkMaterial.HasProperty("_EyeOpen")) eyeBlinkMaterial.SetFloat("_EyeOpen", 1);
        if (rend is not null)
            foreach (var feature in rend.rendererFeatures)
                if (feature && feature.name == "Eye Blink") feature.SetActive(false);
    }
}
