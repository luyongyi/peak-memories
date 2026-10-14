using System.Collections.Generic;

namespace UnityEngine
{
    public class Object
    {
        public string name = "";
        public static implicit operator bool(Object? value) => value != null;
    }
    public class GameObject : Object
    {
        public bool activeInHierarchy = true;
        public readonly Transform transform;
        public int ComponentReads;
        private readonly Dictionary<System.Type, Object> components = new();
        public GameObject() { transform = new Transform(this); }
        public void Put<T>(T component) where T : Object => components[typeof(T)] = component;
        public T? GetComponent<T>() where T : Object { ComponentReads++; return components.TryGetValue(typeof(T), out var value) ? (T)value : null; }
        public T AddComponent<T>() where T : Object,new() {var result=new T();if(result is Animator animator)animator.gameObject=this;Put(result);return result;}
    }
    public class Transform : Object
    {
        public readonly GameObject gameObject;
        public Quaternion localRotation=new(){w=1};
        public bool FindAvailable=true;
        private readonly Dictionary<string,Transform> children=new();
        public Transform(GameObject gameObject) { this.gameObject = gameObject; }
        public T? GetComponent<T>() where T:Object=>gameObject.GetComponent<T>();
        public Transform? Find(string path){if(!FindAvailable)return null;if(!children.TryGetValue(path,out var child))children.Add(path,child=new GameObject().transform);return child;}
    }
    public struct Quaternion {public float x,y,z,w;}
    public class Animator : Object
    {
        public static readonly string[] DefaultedFlightPaths={"Armature/Base/L_LegConnect","Armature/Base/R_LegConnect","Armature/Base/Torso/L_Shoulder","Armature/Base/Torso/R_Shoulder","Armature/Base/Torso/Neck/Head/Bottom-Beak","Armature/Base/Torso/Neck/Head/Top-Beak"};
        public GameObject gameObject = new();
        public Transform transform => gameObject.transform;
        public bool enabled = true, Transition;
        public bool applyRootMotion, isHuman,fireEvents=true;
        public bool isInitialized;
        public AnimatorUpdateMode updateMode;
        public AnimatorCullingMode cullingMode;
        public RuntimeAnimatorController? runtimeAnimatorController;
        public int layerCount = 2;
        public float speed = 1, MouthWeight, CycleOffset;
        public AnimatorStateInfo State = new() { fullPathHash = unchecked((int)4155861363u), loop = true, normalizedTime = 12.25f, speed = 1, speedMultiplier = 1 };
        public readonly List<AnimatorClipInfo> Current = new();
        public int StateQueries, ClipQueries;
        public int BehaviourQueries,Rebinds,Plays,Updates,FloatWrites,LayerWrites,PlayedState,PlayedLayer;
        public float PlayedNormalized,UpdatedDelta,BaseLayerWeight=1;
        public bool IsInTransition(int layer) => Transition;
        public AnimatorStateInfo GetCurrentAnimatorStateInfo(int layer) { StateQueries++; return State; }
        public int GetCurrentAnimatorClipInfoCount(int layer) => Current.Count;
        public void GetCurrentAnimatorClipInfo(int layer, List<AnimatorClipInfo> clips) { ClipQueries++; clips.AddRange(Current); }
        public float GetLayerWeight(int layer) => MouthWeight;
        public float GetFloat(int id) => CycleOffset;
        public T[] GetBehaviours<T>() where T:StateMachineBehaviour {BehaviourQueries++;return runtimeAnimatorController?.Behaviours.OfType<T>().ToArray()??System.Array.Empty<T>();}
        public void Rebind() {if(!gameObject.activeInHierarchy)throw new System.Exception("Rebound inactive hierarchy");Rebinds++;isInitialized=true;CycleOffset=.333f;MouthWeight=.5f;
            foreach(var path in DefaultedFlightPaths)gameObject.transform.Find(path)!.localRotation=new(){x=8,w=1};}
        public void SetLayerWeight(int layer,float weight){LayerWrites++;if(layer==0)BaseLayerWeight=weight;else MouthWeight=weight;}
        public void SetFloat(int hash,float value){FloatWrites++;CycleOffset=value;}
        public void Play(int state,int layer,float normalized){Plays++;PlayedState=state;PlayedLayer=layer;PlayedNormalized=normalized;}
        public void Update(float delta)
        {
            if(enabled||fireEvents||applyRootMotion||speed!=0||MouthWeight!=0||BaseLayerWeight!=1||CycleOffset!=0)
                throw new System.Exception("Unsafe or incorrectly offset visual Animator evaluation");
            Updates++;UpdatedDelta=delta;
            if(PlayedState==PeakReplayLab.RavenAnimationRuntime.FlyState)
                foreach(var path in DefaultedFlightPaths)gameObject.transform.Find(path)!.localRotation=new(){x=9,w=1};
            gameObject.transform.Find("Armature/Base/Torso/Neck/Head/Bottom-Beak")!.localRotation=new(){x=PlayedState==PeakReplayLab.RavenAnimationRuntime.FlyState?9:PlayedNormalized,w=1};
            gameObject.transform.Find("Armature/Base/Torso/Neck/Head/Top-Beak")!.localRotation=new(){x=PlayedState==PeakReplayLab.RavenAnimationRuntime.FlyState?9:PlayedNormalized+1,w=1};
        }
    }
    public class RuntimeAnimatorController : Object
    {
        public AnimationClip[] Clips = System.Array.Empty<AnimationClip>();
        public int DirectoryReads;
        public StateMachineBehaviour[] Behaviours=System.Array.Empty<StateMachineBehaviour>();
        public AnimationClip[] animationClips { get { DirectoryReads++; return Clips; } }
    }
    public sealed class AnimatorOverrideController : RuntimeAnimatorController { }
    public sealed class AnimationClip : Object
    {
        public bool legacy, isLooping = true;
        public float length = 3, frameRate = 60;
        public AnimationEvent[] Events = System.Array.Empty<AnimationEvent>();
        public int EventReads, Samples;
        public GameObject? SampleTarget;
        public float SampleTime;
        public AnimationEvent[] events { get { EventReads++; return Events; } }
        public void SampleAnimation(GameObject target, float time) { SampleTarget = target; SampleTime = time; Samples++; }
    }
    public sealed class AnimationEvent { }
    public class StateMachineBehaviour:Object { }
    public struct AnimatorStateInfo { public int fullPathHash; public bool loop; public float normalizedTime, speed, speedMultiplier; }
    public struct AnimatorClipInfo { public AnimationClip? clip; public float weight; }
    public enum AnimatorUpdateMode { Normal, Fixed, UnscaledTime }
    public enum AnimatorCullingMode { AlwaysAnimate, CullUpdateTransforms, CullCompletely }
}
namespace Peak.Island
{
    public sealed class VoiceThief : UnityEngine.Object
    {
        private UnityEngine.Animator anim = new();
        public UnityEngine.Animator Animator => anim;
        public bool IsLeaving;
    }
}
namespace PeakReplayLab
{
    internal sealed class VisualReplica
    {
        public bool IsReady = true, MappingExists = true, ReturnNative;
        public readonly UnityEngine.GameObject Clone = new();
        public int MappingReads;
        public bool TryCloneTransform(UnityEngine.Transform source, out UnityEngine.Transform target)
        { MappingReads++; target = ReturnNative ? source : Clone.transform; return MappingExists; }
    }
}
