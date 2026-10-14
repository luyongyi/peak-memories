// Model capture test doubles only. Native Animator/IK execution is not simulated.
namespace Photon.Pun
{
    public static class PhotonNetwork { public static int SerializationRate { get; set; } = 20; }
}
namespace UnityEngine
{
    public class Object
    {
        private static int nextId;
        private readonly int id = ++nextId;
        public string name = "";
        public bool Destroyed;
        public int GetInstanceID() => id;
        public static implicit operator bool(Object? value) => value is not null && !value.Destroyed;
    }
    public sealed class GameObject : Object { public bool activeSelf = true; }
    public class Component : Object
    {
        public Transform transform = null!;
        public GameObject gameObject = new();
        public T? GetComponentInParent<T>() where T : Component => null;
    }
    public sealed class Transform : Component
    {
        public static int PoseReads;
        private readonly List<Transform> children = new();
        private Vector3 storedPosition, storedLocalPosition, storedScale = new(1, 1, 1);
        private Quaternion storedRotation = new(0, 0, 0, 1), storedLocalRotation = new(0, 0, 0, 1);
        public Transform parent = null!;
        public Vector3 position { get { PoseReads++; return storedPosition; } set => storedPosition = value; }
        public Vector3 localPosition { get { PoseReads++; return storedLocalPosition; } set => storedLocalPosition = value; }
        public Vector3 localScale { get { PoseReads++; return storedScale; } set => storedScale = value; }
        public Quaternion rotation { get { PoseReads++; return storedRotation; } set => storedRotation = value; }
        public Quaternion localRotation { get { PoseReads++; return storedLocalRotation; } set => storedLocalRotation = value; }
        public Transform(string nodeName, Transform? ancestor = null)
        {
            name = nodeName; transform = this; parent = ancestor!; ancestor?.children.Add(this);
        }
        public bool IsChildOf(Transform ancestor)
        { for (var node = parent; node != null; node = node.parent) if (node == ancestor) return true; return false; }
        public int GetSiblingIndex() => parent?.children.IndexOf(this) ?? 0;
        public Transform GetChild(int index) => children[index];
    }
    public readonly struct Vector3
    {
        public readonly float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public float sqrMagnitude => x * x + y * y + z * z;
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
    }
    public readonly struct Quaternion
    {
        public readonly float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
    }
    public sealed class SkinnedMeshRenderer : Component
    { public Transform[] bones = Array.Empty<Transform>(); public Transform? rootBone; }
    public static class Time { public static double unscaledTimeAsDouble; }
}
public sealed class Item : UnityEngine.Component { }
public sealed class Bodypart : UnityEngine.Component { }
public sealed class RigCreator : UnityEngine.Component { public Bodypart[] parts = Array.Empty<Bodypart>(); }
public sealed class CharacterRefs { public Bodypart hip = null!; public RigCreator rigCreator = null!; }
public sealed class CharacterData
{
    public bool dead, passedOut, fullyPassedOut, isClimbingAnything, isGrounded = true, isSkeleton,
        isRopeClimbing, isVineClimbing, isCrouching, isReaching;
    public float currentRagdollControll = 1;
    public Item currentItem = null!;
    public Character carrier = null!;
}
public sealed class Character : UnityEngine.Component
{
    public bool IsLocal, warping;
    public int CatalogueReads;
    public CharacterRefs refs = new();
    public CharacterData data = new();
    public T[] GetComponentsInChildren<T>(bool inactive) where T : UnityEngine.Component
    { CatalogueReads++; return Array.Empty<T>(); }
    public Character(bool local)
    {
        IsLocal = local; transform = new UnityEngine.Transform("Character");
        refs.hip = new Bodypart { transform = new UnityEngine.Transform("Hip", transform) };
        refs.rigCreator = new RigCreator { transform = new UnityEngine.Transform("Rig", transform) };
        refs.rigCreator.parts = new[] { refs.hip };
    }
}
namespace PeakReplayLab
{
    public sealed class NodePose
    {
        public string Path;
        public float[] Position, Rotation, Scale;
        public bool Active;
        public double SampleTime;
        public NodePose(string path, float[] position, float[] rotation, float[] scale, bool active, double sampleTime)
        { Path = path; Position = position; Rotation = rotation; Scale = scale; Active = active; SampleTime = sampleTime; }
    }
    internal static class ActorJointReplayRules
    { public const int MaximumJoints = 1000, MaximumPathLength = 4096; }
}
