namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class NotNullWhenAttribute : Attribute
    { public bool ReturnValue { get; } public NotNullWhenAttribute(bool returnValue) => ReturnValue = returnValue; }
}
namespace UnityEngine
{
    public class Object
    {
        public string name = "";
        public static implicit operator bool([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Object? value) => value != null;
        public static T[] FindObjectsByType<T>(FindObjectsInactive inactive, FindObjectsSortMode order) => Array.Empty<T>();
    }
    public enum FindObjectsInactive { Include }
    public enum FindObjectsSortMode { None }
    public class GameObject : Object
    {
        public bool activeInHierarchy = true;
        public readonly Transform transform;
        public int ComponentReads, DirectoryReads;
        private readonly Dictionary<Type, Component> components = new();
        public GameObject() { transform = new Transform(this); }
        public void Put<T>(T component) where T : Component { component.gameObject = this; components[typeof(T)] = component; }
        public T? GetComponent<T>() where T : Component { ComponentReads++; return components.TryGetValue(typeof(T),out var value) ? (T)value : null; }
        public T[] GetComponentsInChildren<T>(bool includeInactive) where T : Component
        { DirectoryReads++; return components.Values.OfType<T>().ToArray(); }
    }
    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform => gameObject.transform;
        public Component() { gameObject = new GameObject(); }
        protected Component(GameObject source) { gameObject = source; }
    }
    public class Transform : Component
    {
        private Vector3 scale;
        public int ScaleReads;
        internal Transform(GameObject source) : base(source) { }
        public Vector3 localScale { get { ScaleReads++; return scale; } set => scale = value; }
    }
    public readonly struct Vector3
    {
        public readonly float x,y,z;
        public Vector3(float x,float y,float z) { this.x=x;this.y=y;this.z=z; }
    }
    public class RuntimeAnimatorController : Object { }
    public struct AnimatorStateInfo { public int fullPathHash; public float normalizedTime,speed,speedMultiplier; public bool loop; }
    public class Animator : Component
    {
        public bool enabled = true, Transition;
        public bool isActiveAndEnabled => enabled && gameObject.activeInHierarchy;
        public RuntimeAnimatorController? runtimeAnimatorController = new();
        public int layerCount = 1, StateReads, TransitionReads;
        public float speed = 1;
        public AnimatorStateInfo State = new() {fullPathHash=1,loop=true,speed=1,speedMultiplier=1};
        public AnimatorStateInfo GetCurrentAnimatorStateInfo(int layer) { StateReads++;return State; }
        public bool IsInTransition(int layer) { TransitionReads++;return Transition; }
        public int GetCurrentAnimatorClipInfoCount(int layer) => 1;
    }
    public static class Time { public static double realtimeSinceStartupAsDouble; }
    public static class Resources { public static T? Load<T>(string path) where T : Object => null; }
}
public class Item : UnityEngine.Component { }
public class ClimbHandle : UnityEngine.Component { }
public class Campfire : UnityEngine.Component { }
public class AddScreenshake : UnityEngine.Component { }
public class CheckpointFlag : UnityEngine.Component { public UnityEngine.Animator? anim; }
public class ShittyPiton : UnityEngine.Component
{
    private bool isBreaking, disabled, isHung;
    private float crackScale;
    public UnityEngine.GameObject? crack;
    public bool Breaking {get=>isBreaking;set=>isBreaking=value;}
    public bool Disabled {get=>disabled;set=>disabled=value;}
    public bool Hung {get=>isHung;set=>isHung=value;}
    public float CrackScale {get=>crackScale;set=>crackScale=value;}
}
public class MagicBeanVine : UnityEngine.Component
{
    private bool _isGrowing;
    public bool Growing {get=>_isGrowing;set=>_isGrowing=value;}
}
namespace Peak
{
    public class AntiSphere : UnityEngine.Component
    {
        private bool _dying;
        public bool Dying {get=>_dying;set=>_dying=value;}
    }
    public class GrowOverTime : UnityEngine.Component { public bool isActiveAndEnabled; }
}
namespace PeakReplayLab
{
    internal static class SpawnedReplayResources
    {
        internal sealed class Resource
        {
            public string Path="",Name="",Kind="";
            public UnityEngine.GameObject? Source;
            public double RetryAfter;
        }
    }
    internal static class VisualReplica { public static void InvalidateCapture(UnityEngine.Transform source,bool hierarchy) { } }
}
