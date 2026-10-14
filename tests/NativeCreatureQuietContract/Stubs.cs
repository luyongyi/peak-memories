using UnityEngine;

namespace UnityEngine
{
    public class Object { private static int sequence; private readonly int id = ++sequence; public int GetInstanceID() => id; public string name = ""; public bool Destroyed; public static implicit operator bool(Object? value) => value != null && !value.Destroyed; }
    public class Component : Object { public GameObject gameObject = null!; public Transform transform => gameObject.transform; }
    public class Behaviour : Component { public bool enabled = true; }
    public class MonoBehaviour : Behaviour { }
    public sealed class GameObject : Object
    {
        private readonly List<Component> components = new();
        public readonly Transform transform;
        public bool activeSelf = true;
        public bool activeInHierarchy => activeSelf && (transform.parent == null || transform.parent.gameObject.activeInHierarchy);
        public int ComponentSearches;
        public int PoseReads;
        public GameObject(string name = "") { this.name = name; transform = new Transform(); Add(transform); }
        public T Add<T>(T value) where T : Component { value.gameObject = this; components.Add(value); return value; }
        public T? GetComponent<T>() where T : Component { ComponentSearches++; return components.OfType<T>().FirstOrDefault(); }
        public T[] GetComponentsInChildren<T>(bool includeInactive) where T : Component
        {
            ComponentSearches++; var result = new List<T>(components.OfType<T>());
            foreach (var child in transform.Children) if (includeInactive || child.gameObject.activeInHierarchy) result.AddRange(child.gameObject.GetComponentsInChildren<T>(includeInactive));
            return result.ToArray();
        }
    }
    public sealed class Transform : Component
    {
        private Transform? father;
        public readonly List<Transform> Children = new();
        public Transform? parent { get => father; set { father?.Children.Remove(this); father = value; father?.Children.Add(this); } }
        private Vector3 p,s = new(1,1,1); private Quaternion q = new(0,0,0,1);
        public Vector3 position { get { gameObject.PoseReads++; return p; } set => p = value; }
        public Vector3 lossyScale { get { gameObject.PoseReads++; return s; } set => s = value; }
        public Quaternion rotation { get { gameObject.PoseReads++; return q; } set => q = value; }
        public bool IsChildOf(Transform root) { for (Transform? p = this; p != null; p = p.parent) if (ReferenceEquals(p,root)) return true; return false; }
    }
    public struct Vector3 { public float x,y,z; public Vector3(float x,float y,float z) { this.x=x;this.y=y;this.z=z; } }
    public struct Quaternion { public float x,y,z,w; public Quaternion(float x,float y,float z,float w) { this.x=x;this.y=y;this.z=z;this.w=w; } }
    public class RuntimeAnimatorController : Object { public AnimationClip[] animationClips = Array.Empty<AnimationClip>(); }
    public sealed class AnimationClip : Object { public float length; public object[] events = Array.Empty<object>(); }
    public class StateMachineBehaviour : Object { }
    public struct AnimatorStateInfo { public int fullPathHash; }
    public sealed class Animator : Behaviour
    {
        public RuntimeAnimatorController? runtimeAnimatorController;
        public bool isInitialized = true, Transition, Biting;
        public int layerCount = 1, State = 1432961145;
        public StateMachineBehaviour[] Behaviours = Array.Empty<StateMachineBehaviour>();
        public int StateReads, BoolReads;
        public static int StringToHash(string value) => value.GetHashCode();
        public bool IsInTransition(int layer) => Transition;
        public bool GetBool(int name) { BoolReads++; return Biting; }
        public AnimatorStateInfo GetCurrentAnimatorStateInfo(int layer) { StateReads++; return new() { fullPathHash = State }; }
        public T[] GetBehaviours<T>() where T : StateMachineBehaviour => Behaviours.OfType<T>().ToArray();
    }
    public sealed class Animation : Behaviour { }
    public sealed class Rigidbody : Component { }
    public sealed class Renderer : Behaviour { }
}
public class Character : Component { }
public sealed class VenusFlyTrap : MonoBehaviour
{
    public Animator anim = null!;
    public Transform trapRoot = null!;
    private Character? _targetPlayer;
    private bool _isMunching;
    public bool HiddenState => _targetPlayer != null || _isMunching;
    public void NativeState(Character? value, bool chewing) { _targetPlayer = value; _isMunching = chewing; }
}
public sealed class Capybara : MonoBehaviour { }
public sealed class MirageLuggage : MonoBehaviour { }
public sealed class WobbleSpinBounce : MonoBehaviour { }
public sealed class CollisionModifier : MonoBehaviour { }
public sealed class DisableBasedOnRunSettings : MonoBehaviour { }
public sealed class UnknownVisualDriver : MonoBehaviour { }
namespace Photon.Pun { public sealed class PhotonView : MonoBehaviour { } }
namespace Peak { public sealed class GenericOptimizer : MonoBehaviour { } }
namespace PeakReplayLab
{
    public sealed class ObjectPose
    {
        public bool Active = true;
        public float[] Position = new float[3], Rotation = new float[] { 0,0,0,1 }, Scale = new float[] { 1,1,1 };
    }
    public sealed class CreatureReplayFrame { public string Kind = "", Resource = ""; public ObjectPose Pose = new(); }
    public static class CreatureReplayRules { public const int ActiveClock = 0, SleepingClock = 2; }
}
