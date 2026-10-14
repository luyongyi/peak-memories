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
        public float EvaluatedPose;
        private readonly Dictionary<System.Type, Object> components = new();
        public GameObject() { transform = new Transform(this); }
        public void Put<T>(T component) where T : Object
        { components[typeof(T)] = component; if (component is Animator animator) animator.gameObject = this; }
        public T? GetComponent<T>() where T : Object { ComponentReads++; return components.TryGetValue(typeof(T), out var value) ? (T)value : null; }
        public T AddComponent<T>() where T : Object, new() { var component = new T(); Put(component); return component; }
    }
    public class Transform : Object
    {
        public readonly GameObject gameObject;
        public Transform(GameObject gameObject) { this.gameObject = gameObject; }
        public T? GetComponent<T>() where T : Object => gameObject.GetComponent<T>();
    }
    public class Animator : Object
    {
        public GameObject gameObject = new();
        public Transform transform => gameObject.transform;
        public bool enabled = true, Transition;
        public bool applyRootMotion, isHuman;
        public AnimatorUpdateMode updateMode;
        public AnimatorCullingMode cullingMode;
        public RuntimeAnimatorController? runtimeAnimatorController;
        public int layerCount = 1;
        public float speed = 1;
        public bool fireEvents = true, Weaving, AttackTrigger, BonkTrigger;
        public bool isInitialized;
        public StateMachineBehaviour[] Behaviours = System.Array.Empty<StateMachineBehaviour>();
        public int BehaviourQueries, ParameterQueries, Rebinds, ExplicitPlays, ExplicitUpdates, BoolSets, TriggerResets;
        public float PlayedPhase;
        public int PlayedState;
        public AnimatorStateInfo State = new() { fullPathHash = 1074470604, loop = true, normalizedTime = 12.25f, speed = .33f, speedMultiplier = 1 };
        public readonly List<AnimatorClipInfo> Current = new();
        public int TransitionQueries, StateQueries, ClipCountQueries, ClipQueries;
        public bool IsInTransition(int layer) { TransitionQueries++; return Transition; }
        public AnimatorStateInfo GetCurrentAnimatorStateInfo(int layer) { StateQueries++; return State; }
        public int GetCurrentAnimatorClipInfoCount(int layer) { ClipCountQueries++; return Current.Count; }
        public void GetCurrentAnimatorClipInfo(int layer, List<AnimatorClipInfo> clips) { ClipQueries++; clips.AddRange(Current); }
        public static int StringToHash(string value) => value == "Weaving" ? unchecked((int)2794419566u) : value == "Attack" ? 1080829965 : 1925987662;
        public AnimatorControllerParameter[] parameters
        { get { ParameterQueries++; return isInitialized ? runtimeAnimatorController?.Parameters ?? System.Array.Empty<AnimatorControllerParameter>() : System.Array.Empty<AnimatorControllerParameter>(); } }
        public bool GetBool(int key) => key == StringToHash("Weaving") && Weaving;
        public void SetBool(int key, bool value) { BoolSets++; if (key != StringToHash("Weaving")) throw new System.Exception("Unexpected parameter"); Weaving = value; }
        public void ResetTrigger(int key)
        {
            TriggerResets++;
            if (key == StringToHash("Attack")) AttackTrigger = false;
            else if (key == StringToHash("Bonk")) BonkTrigger = false;
            else throw new System.Exception("Unexpected trigger");
        }
        public T[] GetBehaviours<T>() where T : StateMachineBehaviour
        { BehaviourQueries++; return System.Linq.Enumerable.ToArray(System.Linq.Enumerable.OfType<T>(Behaviours.Length > 0 ? Behaviours : runtimeAnimatorController?.Behaviours ?? System.Array.Empty<StateMachineBehaviour>())); }
        public bool HasState(int layer, int state) => isInitialized && layer == 0 && state == 1074470604 && runtimeAnimatorController?.HasIdleState == true;
        public void Rebind()
        {
            if (!gameObject.activeInHierarchy) throw new System.Exception("Rebound hidden visual hierarchy.");
            Rebinds++; isInitialized = true; Weaving = true; AttackTrigger = BonkTrigger = true;
        }
        public void Play(int state, int layer, float phase)
        { if (layer != 0) throw new System.Exception("Unexpected layer"); ExplicitPlays++; PlayedState = state; PlayedPhase = phase; }
        public void Update(float delta)
        {
            if (delta != 0 || !isInitialized || enabled || fireEvents || applyRootMotion || speed != 0 || Weaving || AttackTrigger || BonkTrigger || PlayedState != 1074470604)
                throw new System.Exception("Visual evaluator advanced time, emitted events, or entered a native action transition.");
            ExplicitUpdates++; gameObject.EvaluatedPose = (float)System.Math.Sin(PlayedPhase * System.Math.PI * 2);
        }
    }
    public class RuntimeAnimatorController : Object
    {
        public AnimationClip[] Clips = System.Array.Empty<AnimationClip>();
        public int DirectoryReads;
        public bool HasIdleState = true;
        public StateMachineBehaviour[] Behaviours = System.Array.Empty<StateMachineBehaviour>();
        public AnimatorControllerParameter[] Parameters = new[]
        {
            new AnimatorControllerParameter { nameHash = unchecked((int)2794419566u), type = AnimatorControllerParameterType.Bool },
            new AnimatorControllerParameter { nameHash = 1080829965, type = AnimatorControllerParameterType.Trigger },
            new AnimatorControllerParameter { nameHash = 1925987662, type = AnimatorControllerParameterType.Trigger },
        };
        public AnimationClip[] animationClips { get { DirectoryReads++; return Clips; } }
    }
    public sealed class AnimatorOverrideController : RuntimeAnimatorController { }
    public sealed class AnimationClip : Object
    {
        public bool legacy, isLooping = true;
        public float length = 1, frameRate = 60;
        public AnimationEvent[] Events = System.Array.Empty<AnimationEvent>();
        public int EventReads, Samples;
        public GameObject? SampleTarget;
        public float SampleTime;
        public AnimationEvent[] events { get { EventReads++; return Events; } }
        // Match the discovered Unity failure: direct SampleAnimation on the
        // inert no-Animator hierarchy does not drive the visual pose.
        public void SampleAnimation(GameObject target, float time) { SampleTarget = target; SampleTime = time; Samples++; }
    }
    public class StateMachineBehaviour : Object { }
    public sealed class AnimatorControllerParameter { public int nameHash; public AnimatorControllerParameterType type; }
    public enum AnimatorControllerParameterType { Float = 1, Int = 3, Bool = 4, Trigger = 9 }
    public sealed class AnimationEvent { }
    public sealed class LineRenderer : Object { public bool useWorldSpace = true; public int positionCount = 40; }
    public struct AnimatorStateInfo { public int fullPathHash; public bool loop; public float normalizedTime, speed, speedMultiplier; }
    public struct AnimatorClipInfo { public AnimationClip? clip; public float weight; }
    public enum AnimatorUpdateMode { Normal, Fixed, UnscaledTime }
    public enum AnimatorCullingMode { AlwaysAnimate, CullUpdateTransforms, CullCompletely }
    public static class Mathf { public static float Clamp01(float value) => value < 0 ? 0 : value > 1 ? 1 : value; }
}

public sealed class Spider : UnityEngine.Object
{
    public enum SpiderState { Idle, Dropped, Grabbing, LetGo }
    public SpiderState spiderState;
    public UnityEngine.Animator anim = new();
    public UnityEngine.Transform? startPoint = new UnityEngine.GameObject().transform;
    public UnityEngine.Transform? spiderButt = new UnityEngine.GameObject().transform;
    public RopeRender? ropeRender = new();
    public UnityEngine.LineRenderer? line = new();
    private float sinceGrab;
    public void SetSinceGrab(float value) => sinceGrab = value;
    public float SinceGrabForTest => sinceGrab;
}
public sealed class RopeRender { public object? wobbleCurve = new(), wobbleOverLineCurve = new(); }

namespace PeakReplayLab
{
    internal sealed class VisualReplica
    {
        public bool IsReady = true;
        public bool MappingExists = true, ReturnNative;
        public readonly UnityEngine.GameObject Clone = new();
        public int MappingReads;
        public bool TryCloneTransform(UnityEngine.Transform source, out UnityEngine.Transform target)
        { MappingReads++; target = ReturnNative ? source : Clone.transform; return MappingExists; }
    }
}
