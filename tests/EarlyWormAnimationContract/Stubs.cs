namespace UnityEngine
{
    public class Object
    {
        public string name = "";
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
        public T AddComponent<T>() where T : Component,new() {var result=new T();Put(result);return result;}
    }
    public class Component : Object
    {
        public GameObject gameObject = new(); public Transform transform => gameObject.transform;
        public T? GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
    }
    public class Transform : Object
    {
        public readonly GameObject gameObject; public Transform(GameObject source) { gameObject = source; }
        public Quaternion Rotation = new(0,0,0,1); public int RotationReads;
        public Quaternion localRotation { get { RotationReads++; return Rotation; } set { Rotation = value; } }
        public T? GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
    }
    public struct Vector3
    {
        public float x,y,z;
        public Vector3(float x,float y,float z) { this.x=x;this.y=y;this.z=z; }
        public static Vector3 up => new(0,1,0);
    }
    public struct Quaternion
    {
        public float x,y,z,w;
        public Quaternion(float x,float y,float z,float w) { this.x=x;this.y=y;this.z=z;this.w=w; }
        public static Quaternion AngleAxis(float angle,Vector3 axis)
        {
            double radians = angle * Math.PI / 360;
            return new((float)(axis.x*Math.Sin(radians)),(float)(axis.y*Math.Sin(radians)),(float)(axis.z*Math.Sin(radians)),(float)Math.Cos(radians));
        }
        public static Quaternion operator *(Quaternion a,Quaternion b) => new(
            a.w*b.x+a.x*b.w+a.y*b.z-a.z*b.y,
            a.w*b.y-a.x*b.z+a.y*b.w+a.z*b.x,
            a.w*b.z+a.x*b.y-a.y*b.x+a.z*b.w,
            a.w*b.w-a.x*b.x-a.y*b.y-a.z*b.z);
    }
    public class Animator : Component
    {
        public bool enabled = true, Transition, applyRootMotion, isHuman, Ground = true, Cooked,fireEvents=true,isInitialized=true;
        public AnimatorUpdateMode updateMode; public AnimatorCullingMode cullingMode;
        public RuntimeAnimatorController? runtimeAnimatorController; public int layerCount=1;public float speed=1;
        public AnimatorStateInfo State = new() { loop=true,normalizedTime=.25f,speed=1,speedMultiplier=1,fullPathHash=unchecked((int)2261229391u) };
        public readonly List<AnimatorClipInfo> Current = new();
        public int StateQueries,ClipQueries,TransitionQueries,CountQueries,BoolQueries;
        public StateMachineBehaviour[] Behaviours=Array.Empty<StateMachineBehaviour>();
        public int Rebinds,ExplicitUpdates,ExplicitPlays;
        private float manualTime;private int manualState;
        public bool IsInTransition(int layer) {TransitionQueries++;return Transition;}
        public AnimatorStateInfo GetCurrentAnimatorStateInfo(int layer) {StateQueries++;return State;}
        public int GetCurrentAnimatorClipInfoCount(int layer) {CountQueries++;return Current.Count;}
        public void GetCurrentAnimatorClipInfo(int layer,List<AnimatorClipInfo> target) {ClipQueries++;target.AddRange(Current);}
        public bool GetBool(int key) {BoolQueries++;return key==1?Ground:Cooked;}
        public static int StringToHash(string key) => key=="OnGround"?1:2;
        public T[] GetBehaviours<T>() where T:StateMachineBehaviour => (Behaviours.Length!=0?Behaviours:runtimeAnimatorController?.Behaviours??Array.Empty<StateMachineBehaviour>()).OfType<T>().ToArray();
        public void Rebind() {Rebinds++;isInitialized=true;}
        public void SetBool(string key,bool value) {if(key=="OnGround")Ground=value;else Cooked=value;}
        public void Play(int state,int layer,float phase) {ExplicitPlays++;manualState=state;manualTime=phase;}
        public void Update(float delta)
        {
            if(delta!=0)throw new Exception("Native evaluator advanced automatically");ExplicitUpdates++;
            string name=manualState==824124038?"EarlyWormCooked":manualState==unchecked((int)2261229391u)?"EarlyWormWalk":"EarlyWormInHand";
            runtimeAnimatorController!.Clips.Single(clip=>clip.name==name).SampleAnimation(gameObject,manualTime);
        }
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
        public bool legacy,isLooping=true;public float length=1;
        public AnimationEvent[] Events=Array.Empty<AnimationEvent>();public int EventReads,Samples;
        public GameObject? SampleTarget;public float SampleTime;
        public AnimationEvent[] events {get {EventReads++;return Events;}}
        public void SampleAnimation(GameObject target,float time) {SampleTarget=target;SampleTime=time;Samples++;}
    }
    public class AnimationEvent { }
    public class StateMachineBehaviour : Object { }
}
public enum ItemState {Ground,Held,InBackpack}
public class Item : UnityEngine.Component {public ushort itemID=184;public ItemState itemState;public ItemCooking? cooking=new();}
public class ItemCooking : UnityEngine.Object {public int timesCookedLocal;}
public class Rotate : UnityEngine.Component {public bool enabled=true;public UnityEngine.Transform tf=null!;public UnityEngine.Vector3 rotation=new(0,-50,0);}
namespace Peak {public class EarlyWorm : UnityEngine.Component {public UnityEngine.Animator anim=new();public Rotate rotate=new();}}
namespace PeakReplayLab
{
    internal sealed class VisualReplica
    {
        public bool IsReady=true,MappingExists=true,ReturnNative;
        public readonly UnityEngine.GameObject Clone=new();public int MappingReads;
        public bool TryCloneTransform(UnityEngine.Transform source,out UnityEngine.Transform target)
        {MappingReads++;target=ReturnNative?source:Clone.transform;return MappingExists;}
    }
}
