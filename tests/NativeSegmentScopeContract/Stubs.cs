namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class NotNullWhenAttribute : Attribute
    { public bool ReturnValue { get; } public NotNullWhenAttribute(bool returnValue) => ReturnValue = returnValue; }
}
namespace UnityEngine
{
    public partial class Object
    {
        private static int sequence;
        private readonly int identity = ++sequence;
        public int? ReusedId;
        public bool Destroyed;
        public int GetInstanceID() => ReusedId ?? identity;
        public static implicit operator bool([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Object? value) => value is not null && !value.Destroyed;
    }
    public partial class GameObject : Object
    {
        public string name;
        public bool activeSelf = true;
        public readonly Transform transform;
        public static int ActiveReads;
        public bool activeInHierarchy
        { get { ActiveReads++; return !Destroyed && activeSelf && (transform.RawParent == null || transform.RawParent.gameObject.activeInHierarchy); } }
        public GameObject(string name = "source") { this.name = name; transform = new Transform(this); }
        public void SetActive(bool value) => activeSelf = value;
    }
    public partial class Transform : Object
    {
        public readonly GameObject gameObject;
        internal Transform? RawParent;
        public static int ParentReads;
        public Transform? parent { get { ParentReads++; return RawParent; } set => RawParent = value; }
        public Transform(GameObject source) => gameObject = source;
    }
}
public class MapHandler : UnityEngine.Object
{
    public static MapHandler? Instance;
    public static bool Exists => Instance;
    public MapSegment[] segments = Array.Empty<MapSegment>();
    public MapSegment[] variantSegments = Array.Empty<MapSegment>();
    public int currentSegment;
    public int GetCurrentSegment() => throw new Exception("capture must use actual hierarchy activation, never the early segment index");
    public class MapSegment
    {
        private UnityEngine.GameObject? _segmentParent;
        private UnityEngine.GameObject? _segmentCampfire;
        public MapSegment? SelectedVariant;
        public bool ThrowResolved;
        public UnityEngine.GameObject? segmentParent
        { get { if (ThrowResolved) throw new InvalidOperationException("native variant selection unavailable"); return SelectedVariant?.segmentParent ?? _segmentParent; } }
        public UnityEngine.GameObject? segmentCampfire
        { get { if (ThrowResolved) throw new InvalidOperationException("native variant selection unavailable"); return SelectedVariant?.segmentCampfire ?? _segmentCampfire; } }
        public MapSegment(UnityEngine.GameObject? root, UnityEngine.GameObject? campfire = null)
        { _segmentParent = root; _segmentCampfire = campfire; }
        public void SetRawRoots(UnityEngine.GameObject? root, UnityEngine.GameObject? campfire = null)
        { _segmentParent = root; _segmentCampfire = campfire; }
    }
}
