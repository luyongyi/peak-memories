// Small in-memory API doubles for ownership, head/hat anchoring and projection.
// They do not simulate Unity callback order, TMP rendering or native camera optics.
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
        public static T FindFirstObjectByType<T>(FindObjectsInactive inactive) where T : Object => Registry.OfType<T>()
            .FirstOrDefault(value => value && (inactive == FindObjectsInactive.Include || value is not Component c || c.gameObject.activeInHierarchy))!;
        public static void Destroy(Object value)
        {
            value.Destroyed = true;
            if (value is GameObject go)
            {
                foreach (var component in go.Components) component.Destroyed = true;
                foreach (var child in go.Children.ToArray()) Destroy(child);
            }
        }
        internal static void Reset() => Registry.Clear();
    }
    public enum FindObjectsInactive { Exclude, Include }
    public class Component : Object
    {
        public GameObject gameObject = null!;
        public Transform transform => gameObject.transform;
        public T GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
    }
    public class Behaviour : Component { public bool enabled = true; }
    public class MonoBehaviour : Behaviour { }
    public sealed class GameObject : Object
    {
        internal readonly List<Component> Components = new();
        internal readonly List<GameObject> Children = new();
        internal GameObject? Parent;
        public bool activeSelf { get; private set; } = true;
        public bool activeInHierarchy => activeSelf && (Parent?.activeInHierarchy ?? true);
        public bool? ThrowOnNextSetActive;
        public Transform transform { get; }
        public GameObject(string name, params Type[] types)
        {
            this.name = name;
            transform = types.Contains(typeof(RectTransform)) ? new RectTransform() : new Transform();
            transform.gameObject = this; Components.Add(transform);
            foreach (var type in types) if (!typeof(Transform).IsAssignableFrom(type)) AddComponent(type);
        }
        private Component AddComponent(Type type)
        {
            var component = (Component)Activator.CreateInstance(type)!;
            component.gameObject = this; Components.Add(component); return component;
        }
        public T AddComponent<T>() where T : Component, new() => (T)AddComponent(typeof(T));
        public T GetComponent<T>() where T : Component => Components.OfType<T>().FirstOrDefault()!;
        public void SetActive(bool active)
        {
            if (ThrowOnNextSetActive == active)
            { ThrowOnNextSetActive = null; throw new InvalidOperationException("Synthetic native label visibility failure."); }
            activeSelf = active;
        }
    }
    public class Transform : Component
    {
        public Vector3 position;
        public Transform? parent => gameObject.Parent?.transform;
        public void SetParent(Transform parent, bool _)
        {
            gameObject.Parent?.Children.Remove(gameObject);
            gameObject.Parent = parent.gameObject;
            parent.gameObject.Children.Add(gameObject);
        }
    }
    public sealed class RectTransform : Transform { public Vector2 sizeDelta, pivot; }
    public struct Vector3(float x, float y, float z)
    {
        public float x = x, y = y, z = z;
        public static Vector3 up => new(0, 1, 0);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator *(Vector3 value, float scale) => new(value.x * scale, value.y * scale, value.z * scale);
    }
    public readonly record struct Vector2(float x, float y);
    public readonly record struct Color(float r, float g, float b, float a)
    { public static Color white => new(1, 1, 1, 1); }
    public readonly struct Bounds(Vector3 center, Vector3 size)
    { public Vector3 max => center + size * .5f; }
    public class Renderer : Component { public bool enabled = true; public Bounds bounds; }
    public sealed class Material : Object { }
    public static class Mathf { public static float Max(float a, float b) => Math.Max(a, b); }
    public static class Screen { public static int width = 1920, height = 1080; }
    public static class Time { public static float timeScale = 1; }
    public enum RenderMode { ScreenSpaceOverlay }
    public sealed class Canvas : Behaviour
    { public RenderMode renderMode; public int sortingOrder; public float scaleFactor = 1; }
    public sealed class CanvasRenderer : Component { }
    public sealed class Camera : Behaviour
    {
        public int ProjectionCalls;
        public Vector3 WorldToScreenPoint(Vector3 point)
        {
            ProjectionCalls++;
            return new Vector3(Screen.width * .5f + (point.x - transform.position.x) * 40,
                Screen.height * .5f + (point.y - transform.position.y) * 40, point.z - transform.position.z);
        }
    }
}
namespace UnityEngine.UI
{
    public sealed class CanvasScaler : UnityEngine.Behaviour
    {
        public enum ScaleMode { ScaleWithScreenSize }
        public ScaleMode uiScaleMode;
        public UnityEngine.Vector2 referenceResolution;
        public float matchWidthOrHeight;
    }
}
namespace TMPro
{
    public sealed class TMP_FontAsset : UnityEngine.Object { }
    public static class TMP_Settings { public static TMP_FontAsset? defaultFontAsset; }
    public enum TextAlignmentOptions { Bottom }
    public enum TextWrappingModes { NoWrap }
    public enum TextOverflowModes { Ellipsis }
    public sealed class TextMeshProUGUI : UnityEngine.Behaviour
    {
        public TMP_FontAsset font = null!;
        public UnityEngine.Material fontSharedMaterial = null!;
        public float fontSize;
        public TextAlignmentOptions alignment;
        public bool richText, raycastTarget;
        public TextWrappingModes textWrappingMode;
        public TextOverflowModes overflowMode;
        public string text = "";
        public UnityEngine.Color color = UnityEngine.Color.white;
        public UnityEngine.RectTransform rectTransform => (UnityEngine.RectTransform)transform;
    }
}
public sealed class UIPlayerNames : UnityEngine.MonoBehaviour { public PlayerName[] playerNameText = Array.Empty<PlayerName>(); }
public sealed class PlayerName : UnityEngine.MonoBehaviour { public TMPro.TextMeshProUGUI text = null!; }
namespace PeakReplayLab
{
    internal sealed class ActorFrame { public string Name = ""; }
    // A posed actor test double calls the actual production anchor helper.
    internal sealed class VisualActor
    {
        public bool Visible = true;
        public ActorFrame? State;
        public UnityEngine.Vector3 HeadPosition;
        public UnityEngine.Renderer[] Hats = Array.Empty<UnityEngine.Renderer>();
        public UnityEngine.Vector3 NamePosition => ReplayNameplateAnchor.Position(HeadPosition, Hats);
    }
}
