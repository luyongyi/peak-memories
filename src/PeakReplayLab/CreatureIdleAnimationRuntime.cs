using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace PeakReplayLab;

// A disabled, explicitly evaluated visual Animator supplies Unity's generic
// property binding on the inert clone. It has no automatic time advancement,
// animation events, StateMachineBehaviours or native gameplay components.
internal sealed class CreatureIdleAnimationRuntime : IDisposable
{
    private const int SpiderIdleState = 1074470604;
    private const float NativeStateSpeed = .33000001311302185f;
    private static readonly int Weaving = Animator.StringToHash("Weaving"), Attack = Animator.StringToHash("Attack"), Bonk = Animator.StringToHash("Bonk");
    private sealed class AuditedClip { public AnimationClip? Clip; }
    private static readonly ConditionalWeakTable<RuntimeAnimatorController, AuditedClip> clips = new();
    private static readonly AccessTools.FieldRef<Spider, float>? sinceGrab = ResolveSinceGrab();
    private readonly AnimationClip clip;
    private readonly GameObject target;
    private readonly Animator animator;
    private readonly RuntimeAnimatorController controller;
    internal static string LastRejection { get; private set; } = "";
    private bool disposed;
    private bool sampled;
    private double sampleTime;
    private bool bound;

    private CreatureIdleAnimationRuntime(AnimationClip clip, GameObject target, Animator animator)
    { this.clip = clip; this.target = target; this.animator = animator; controller = animator.runtimeAnimatorController!; }

    internal sealed class Capture
    {
        private readonly Spider spider;
        private readonly Animator? animator;
        private readonly RuntimeAnimatorController? controller;
        private readonly AnimationClip? clip;
        private readonly bool supported;
        private readonly List<AnimatorClipInfo> current = new(1);
        public Capture(Spider spider, string resource)
        {
            this.spider = spider;
            if (!spider || resource != "spider-normal" && resource != "spider-web" || !spider.anim) return;
            animator = spider.anim; controller = animator.runtimeAnimatorController;
            clip = FindClip(controller);
            supported = clip && AuditedAnimator(animator);
        }
        public bool TryCapture(double time, out CreatureIdleAnimation? animation)
        {
            animation = null;
            if (!supported || !spider || !animator || !controller || !clip || sinceGrab == null ||
                !CreatureIdleAnimationRules.Finite(time) || Math.Abs(time) > 1_000_000 ||
                spider.spiderState != Spider.SpiderState.Idle ||
                !spider.startPoint || !spider.spiderButt || spider.ropeRender == null || !spider.line ||
                spider.ropeRender.wobbleCurve == null || spider.ropeRender.wobbleOverLineCurve == null ||
                !spider.line!.useWorldSpace || spider.line.positionCount != 40 ||
                !animator!.enabled || !animator.gameObject.activeInHierarchy ||
                animator.updateMode != AnimatorUpdateMode.Normal || animator.cullingMode != AnimatorCullingMode.AlwaysAnimate ||
                animator.applyRootMotion || animator.isHuman ||
                animator.runtimeAnimatorController != controller || animator.layerCount != 1 || animator.IsInTransition(0) || animator.GetBool(Weaving)) return false;
            var state = animator.GetCurrentAnimatorStateInfo(0);
            if (state.fullPathHash != SpiderIdleState || !state.loop ||
                !CreatureIdleAnimationRules.Finite(state.normalizedTime) ||
                Math.Abs(state.speed - NativeStateSpeed) > .000001f ||
                animator.GetCurrentAnimatorClipInfoCount(0) != 1) return false;
            current.Clear(); animator.GetCurrentAnimatorClipInfo(0, current);
            if (current.Count != 1 || current[0].clip != clip || Math.Abs(current[0].weight - 1) > .00001f) return false;
            double speed = (double)state.speed * state.speedMultiplier * animator.speed;
            float rope = sinceGrab(spider);
            if (!CreatureIdleAnimationRules.Finite(speed) || speed < 0 || speed > 8 || !CreatureIdleAnimationRules.Finite(rope)) return false;
            double phase = state.normalizedTime - Math.Floor(state.normalizedTime);
            animation = new CreatureIdleAnimation
            {
                Clip = CreatureIdleAnimationRules.SpiderIdle, AnchorTime = time,
                ClipTime = phase * CreatureIdleAnimationRules.SpiderIdleLength,
                Speed = (float)speed, RopePhase = Mathf.Clamp01(rope),
            };
            return true;
        }
    }

    public static bool TryCreate(VisualReplica visual, GameObject source, CreatureIdleAnimation animation, out CreatureIdleAnimationRuntime? runtime)
    {
        runtime = null;
        LastRejection = "";
        if (animation.Clip != CreatureIdleAnimationRules.SpiderIdle) return Reject("unsupported-descriptor");
        if (!source) return Reject("missing-native-template");
        if (!visual.IsReady) return Reject("visual-not-ready");
        var spider = source.GetComponent<Spider>();
        if (!spider || !spider!.anim) return Reject("missing-native-spider-animator");
        var clip = FindClip(spider.anim.runtimeAnimatorController);
        if (!clip) return Reject("native-controller-or-four-clip-signature");
        if (!NoStateBehaviours(spider.anim)) return Reject("native-state-machine-behaviour");
        // A freshly loaded replay prefab need never have run its Animator.
        // Its empty parameter directory is not an asset mismatch, and reading
        // it must not force initialization or any writes to the native source.
        // Capture keeps its strict initialized-source gate; playback validates
        // this same known controller on the inert clone after active binding.
        if (spider.anim.isInitialized && !AuditedAnimator(spider.anim)) return Reject("native-parameter-signature");
        if (!visual.TryCloneTransform(spider.anim.transform, out var target)) return Reject("missing-clone-animation-root");
        // The clone mapping, rather than a recorded path/name, selects the
        // animation root. No recording can redirect sampling into native scene.
        if (target == spider.anim.transform) return Reject("animation-root-is-native");
        var existing = target.GetComponent<Animator>();
        if (existing && (existing!.enabled || existing.runtimeAnimatorController)) return Reject("clone-has-live-animator");
        var evaluator = existing ? existing! : target.gameObject.AddComponent<Animator>();
        evaluator.enabled = false; evaluator.fireEvents = false; evaluator.applyRootMotion = false;
        evaluator.cullingMode = AnimatorCullingMode.AlwaysAnimate; evaluator.updateMode = AnimatorUpdateMode.Normal;
        evaluator.speed = 0; evaluator.runtimeAnimatorController = spider.anim.runtimeAnimatorController;
        // A hidden/uninitialized Animator does not expose its controller's
        // parameter directory yet. Its trusted four-clip controller is fixed;
        // complete parameter validation waits for the first active bind.
        if (!NoStateBehaviours(evaluator))
        { evaluator.runtimeAnimatorController = null; return Reject("clone-state-machine-behaviour"); }
        runtime = new CreatureIdleAnimationRuntime(clip!, target.gameObject, evaluator);
        return true;
    }
    public void Apply(CreatureIdleAnimation animation, double replayTime)
    {
        if (disposed || !clip || !target || !animator) return;
        double time = CreatureIdleAnimationRules.SampleTime(animation, replayTime);
        if (!target.activeInHierarchy) return;
        if (animator.runtimeAnimatorController != controller)
            throw new InvalidOperationException("原生蜘蛛待机动画拒绝：clone-controller-replaced。");
        bool bindingLost = !bound || !animator.isInitialized;
        if (!bindingLost && sampled && time == sampleTime) return;
        // Bind only once the completed visual hierarchy has been activated by
        // Replica.Apply. The descriptor already carries effective clip time;
        // the native state's .33 speed must not be applied a second time.
        if (bindingLost)
        {
            // Check serialized behaviour metadata before Rebind can enter any
            // controller state, including a controller replaced on a pooled GO.
            if (animator.runtimeAnimatorController != controller || !NoStateBehaviours(animator))
                throw new InvalidOperationException("原生蜘蛛待机动画拒绝：clone-controller-or-behaviour。");
            animator.Rebind();
            if (!AuditedAnimator(animator) || !animator.HasState(0, SpiderIdleState))
                throw new InvalidOperationException("原生蜘蛛待机动画拒绝：bound-parameter-or-idle-state。");
            bound = true;
        }
        // The audited controller has AnyState weaving, attack and bonk edges.
        // Clear all three before explicit evaluation, including pooled reuse.
        animator.SetBool(Weaving, false); animator.ResetTrigger(Attack); animator.ResetTrigger(Bonk);
        animator.Play(SpiderIdleState, 0, (float)(time / CreatureIdleAnimationRules.SpiderIdleLength));
        animator.Update(0);
        if (animator.IsInTransition(0) || animator.GetCurrentAnimatorStateInfo(0).fullPathHash != SpiderIdleState)
            throw new InvalidOperationException("原生蜘蛛待机动画拒绝：unexpected-idle-transition。");
        sampleTime = time; sampled = true;
    }
    // Recorded poses overwrite sampled bones when an action begins. Call this
    // when presentation reapplies the held checkpoint or reuses the clone.
    public void Invalidate() => sampled = false;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; sampled = false;
        // Keep this inert component with its clone. Deferred Destroy would race
        // a same-frame visual-pool reuse after the old runtime has retired.
        if (animator) { animator.enabled = false; animator.runtimeAnimatorController = null; }
    }

    private static bool AuditedAnimator(Animator source)
    {
        if (!source || !NoStateBehaviours(source)) return false;
        var parameters = source.parameters;
        if (parameters == null || parameters.Length != 3) return false;
        bool weaving = false, attack = false, bonk = false;
        foreach (var parameter in parameters)
        {
            if (parameter.nameHash == Weaving && parameter.type == AnimatorControllerParameterType.Bool && !weaving) weaving = true;
            else if (parameter.nameHash == Attack && parameter.type == AnimatorControllerParameterType.Trigger && !attack) attack = true;
            else if (parameter.nameHash == Bonk && parameter.type == AnimatorControllerParameterType.Trigger && !bonk) bonk = true;
            else return false;
        }
        return weaving && attack && bonk;
    }
    private static bool NoStateBehaviours(Animator source) => source.GetBehaviours<StateMachineBehaviour>().Length == 0;
    private static bool Reject(string reason) { LastRejection = reason; return false; }

    private static AnimationClip? FindClip(RuntimeAnimatorController? controller)
    {
        if (!controller) return null;
        return clips.GetValue(controller!, source =>
        {
            var result = new AuditedClip();
            // A fixed native asset signature. An override controller or a
            // changed/ambiguous asset remains on captured geometry playback.
            if (source is AnimatorOverrideController || source.name != "Spider (1)") return result;
            var directory = source.animationClips;
            if (directory == null || directory.Length != 4) return result;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in directory)
            {
                if (!candidate || !names.Add(candidate.name) || candidate.legacy ||
                    Math.Abs(candidate.frameRate - 60) > .001 || candidate.events.Length != 0)
                { result.Clip = null; return result; }
                double length; bool loop;
                switch (candidate.name)
                {
                    case "SpiderIdle": length = CreatureIdleAnimationRules.SpiderIdleLength; loop = true; result.Clip = candidate; break;
                    case "SpiderAttack": length = .75; loop = false; break;
                    case "SpiderBonk": case "SpiderWeave": length = .5833333134651184; loop = true; break;
                    default: result.Clip = null; return result;
                }
                if (candidate.isLooping != loop || Math.Abs(candidate.length - length) > .00001)
                { result.Clip = null; return result; }
            }
            return result;
        }).Clip;
    }
    private static AccessTools.FieldRef<Spider, float>? ResolveSinceGrab()
    { try { return AccessTools.FieldRefAccess<Spider, float>("sinceGrab"); } catch { return null; } }
}
