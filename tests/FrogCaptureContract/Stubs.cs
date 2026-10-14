namespace UnityEngine
{
    public class Object
    {
        private static int sequence; private readonly int id = ++sequence;
        public string name = "";
        public int GetInstanceID() => id;
        public static implicit operator bool(Object? value) => value != null;
    }
    public class GameObject : Object
    {
        public bool activeSelf = true, activeInHierarchy = true;
        public readonly Transform transform;
        public int ComponentReads;
        private readonly Dictionary<Type, Component> components = new();
        public GameObject() { transform = new(this); }
        public void Put(Component component) { components[component.GetType()] = component; component.gameObject = this; }
        public T? GetComponent<T>() where T : Component { ComponentReads++; return components.Values.OfType<T>().FirstOrDefault(); }
        public T AddComponent<T>() where T : Component,new() {var value=new T();Put(value);return value;}
    }
    public class Component : Object
    {
        public GameObject gameObject = new(); public Transform transform => gameObject.transform;
        public T? GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
    }
    public class Transform : Object
    {
        public readonly GameObject gameObject; public Transform(GameObject source) { gameObject = source; }
        public Vector3 Position, Scale = new(1,1,1); public Quaternion Rotation = new(0,0,0,1); public int PositionReads,RotationReads,ScaleReads;
        public Vector3 position { get { PositionReads++;return Position; } set { Position=value; } }
        public Quaternion rotation { get { RotationReads++;return Rotation; } set { Rotation=value; } }
        public Vector3 lossyScale { get { ScaleReads++;return Scale; } }
        public T? GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
    }
    public struct Vector3 { public float x,y,z; public Vector3(float x,float y,float z) { this.x=x;this.y=y;this.z=z; } public float sqrMagnitude => x*x+y*y+z*z; }
    public struct Quaternion { public float x,y,z,w; public Quaternion(float x,float y,float z,float w) { this.x=x;this.y=y;this.z=z;this.w=w; } }
    public class Rigidbody : Component { public bool isKinematic; public Vector3 linearVelocity,angularVelocity; }
    public class Animator : Component
    {
        public bool enabled = true, Transition, applyRootMotion, isHuman,fireEvents=true,Airborne,isInitialized=true;
        public AnimatorUpdateMode updateMode; public AnimatorCullingMode cullingMode;
        public RuntimeAnimatorController? runtimeAnimatorController; public int layerCount=1;public float speed=1;
        public AnimatorStateInfo State = new() { loop=true,normalizedTime=.25f,speed=1,speedMultiplier=1,fullPathHash=unchecked((int)2499778558u) };
        public readonly List<AnimatorClipInfo> Current = new();
        public int StateQueries,ClipQueries,TransitionQueries,CountQueries;
        public float IdleOffset; public StateMachineBehaviour[] Behaviours=Array.Empty<StateMachineBehaviour>();
        public int Rebinds,ExplicitUpdates,ExplicitPlays,ResetTriggers;private float manualTime;
        public bool IsInTransition(int layer) {TransitionQueries++;return Transition;}
        public AnimatorStateInfo GetCurrentAnimatorStateInfo(int layer) {StateQueries++;return State;}
        public int GetCurrentAnimatorClipInfoCount(int layer) {CountQueries++;return Current.Count;}
        public void GetCurrentAnimatorClipInfo(int layer,List<AnimatorClipInfo> target) {ClipQueries++;target.AddRange(Current);}
        public T[] GetBehaviours<T>() where T:StateMachineBehaviour => (Behaviours.Length!=0?Behaviours:runtimeAnimatorController?.Behaviours??Array.Empty<StateMachineBehaviour>()).OfType<T>().ToArray();
        public static int StringToHash(string key) => key=="IdleOffset"?822357063:0;
        public float GetFloat(int key) => IdleOffset;
        public void SetFloat(int key,float value) {IdleOffset=value;}
        public void ResetTrigger(string key) {ResetTriggers++;}
        public void SetBool(string key,bool value) {Airborne=value;}
        public void Rebind() {Rebinds++;isInitialized=true;}
        public void Play(int state,int layer,float phase)
        {if(state!=unchecked((int)2499778558u))throw new Exception("Unknown native frog state");ExplicitPlays++;manualTime=phase;}
        public void Update(float delta)
        {if(delta!=0)throw new Exception("Frog evaluator advanced automatically");ExplicitUpdates++;runtimeAnimatorController!.Clips.Single(clip=>clip.name=="FrogIdle").SampleAnimation(gameObject,manualTime*2);}
    }
    public struct AnimatorStateInfo { public bool loop; public float normalizedTime,speed,speedMultiplier;public int fullPathHash; }
    public struct AnimatorClipInfo { public AnimationClip? clip;public float weight; }
    public enum AnimatorUpdateMode { Normal,Fixed,UnscaledTime }
    public enum AnimatorCullingMode { AlwaysAnimate,CullUpdateTransforms,CullCompletely }
    public class RuntimeAnimatorController : Object
    {
        public AnimationClip[] Clips=Array.Empty<AnimationClip>();public int DirectoryReads;
        public StateMachineBehaviour[] Behaviours=Array.Empty<StateMachineBehaviour>();
        public AnimationClip[] animationClips {get {DirectoryReads++;return Clips;}}
    }
    public class AnimatorOverrideController : RuntimeAnimatorController { }
    public class AnimationClip : Object
    {
        public bool legacy,isLooping=true;public float length=2;
        public AnimationEvent[] Events=Array.Empty<AnimationEvent>();public int EventReads,Samples;
        public GameObject? SampleTarget;public float SampleTime;
        public AnimationEvent[] events {get {EventReads++;return Events;}}
        public void SampleAnimation(GameObject target,float time) {SampleTarget=target;SampleTime=time;Samples++;}
    }
    public class AnimationEvent { }
    public class StateMachineBehaviour : Object { }
    public static class Time {public static double timeAsDouble;}
}
public enum ItemState {Ground,Held,InBackpack}
public class Item : UnityEngine.Component
{
    public ushort itemID=172;public ItemState itemState;public UnityEngine.Rigidbody? rig=new();public ItemCooking? cooking=new();
}
public class MobItem : Item { }
public class ItemCooking : UnityEngine.Object {public int timesCookedLocal;}
public class Character : UnityEngine.Component { }
public class FrogTongue : UnityEngine.Component
{
    public MobItem mobItem=null!;public UnityEngine.Animator anim=new();public UnityEngine.Transform visuals=new UnityEngine.GameObject().transform;
    public bool sleeping;
    private bool _isPulling,_thrown,inWater;
    private float _tongueLength;
    private Character? _targetCharacter;
    private UnityEngine.Rigidbody? _targetRig;
    public void SetAction(bool pulling=false,bool thrown=false,bool water=false,float tongue=0,Character? character=null,UnityEngine.Rigidbody? target=null)
    {_isPulling=pulling;_thrown=thrown;inWater=water;_tongueLength=tongue;_targetCharacter=character;_targetRig=target;}
    public bool FieldsInitialized => _isPulling||_thrown||inWater||_tongueLength!=0||_targetCharacter!=null||_targetRig!=null;
}
namespace PeakReplayLab
{
    internal static class ReplaySafety {public static bool Active;}
    internal static class ItemCapture {internal static bool EventHooksHealthy;internal static int Dirty;internal static void MarkDirty(Item source) {Dirty++;}}
    internal class ObjectPose
    {
        public bool Active=true;public float[] Position={0,0,0},Rotation={0,0,0,1},Scale={1,1,1};public object[] Nodes=Array.Empty<object>();
    }
    internal class ItemFrame {public ObjectPose Pose=new();public CreatureIdleAnimation? IdleAnimation;}
    internal class InventoryFrame { }
    internal sealed class VisualReplica
    {
        public bool IsReady=true,MappingExists=true,ReturnNative;
        public readonly UnityEngine.GameObject Clone=new();public int MappingReads;
        public bool TryCloneTransform(UnityEngine.Transform source,out UnityEngine.Transform target)
        {MappingReads++;target=ReturnNative?source:Clone.transform;return MappingExists;}
    }
}
