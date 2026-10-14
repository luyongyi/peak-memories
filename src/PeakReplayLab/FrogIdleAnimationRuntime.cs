using System;
using System.Collections.Generic;
using UnityEngine;

namespace PeakReplayLab;

// Native generic animation bindings require an Animator. It is attached only
// to the inert visual, stays disabled, cannot fire events, and is evaluated with
// zero delta at an absolute phase. Native AI and physics remain absent.
internal sealed class FrogIdleAnimationRuntime : IDisposable
{
    private const int IdleState = unchecked((int)2499778558u);
    private static readonly int IdleOffset = Animator.StringToHash("IdleOffset");
    private sealed class AuditedClip { internal AnimationClip? Clip; }
    private static readonly Dictionary<RuntimeAnimatorController, AuditedClip> clips = new();
    private static readonly Queue<RuntimeAnimatorController> clipOrder = new();
    private const int MaximumControllers = 16;
    private readonly AnimationClip clip;
    private readonly GameObject target;
    private readonly Animator animator;
    private bool sampled, disposed, bound;
    private double sampleTime;

    private FrogIdleAnimationRuntime(AnimationClip clip, GameObject target, Animator animator)
    { this.clip = clip; this.target = target; this.animator = animator; }

    internal sealed class Capture
    {
        private readonly Animator? animator;
        private readonly RuntimeAnimatorController? controller;
        private readonly AnimationClip? clip;
        private readonly bool behaviourFree;
        private readonly List<AnimatorClipInfo> current = new(1);
        internal bool Supported => clip && behaviourFree;
        internal long ValidationReads { get; private set; }
        internal Capture(FrogTongue frog)
        {
            if (!frog || frog.GetType() != typeof(FrogTongue) || !frog.anim) return;
            animator = frog.anim; controller = animator.runtimeAnimatorController;
            clip = FindClip(controller);
            behaviourFree = HasNoBehaviours(animator);
        }
        internal bool TryCapture(double time, out CreatureIdleAnimation? animation)
        {
            animation = null; ValidationReads++;
            if (!animator || !controller || !Supported || !Finite(time) || Math.Abs(time) > 1_000_000 ||
                !animator!.enabled || !animator.gameObject.activeInHierarchy ||
                animator.updateMode != AnimatorUpdateMode.Normal || animator.cullingMode != AnimatorCullingMode.AlwaysAnimate ||
                animator.applyRootMotion || animator.isHuman || animator.runtimeAnimatorController != controller ||
                animator.layerCount != 1 || animator.IsInTransition(0)) return false;
            var state = animator.GetCurrentAnimatorStateInfo(0);
            if (!state.loop || state.fullPathHash != IdleState || !Finite(state.normalizedTime) || animator.GetCurrentAnimatorClipInfoCount(0) != 1) return false;
            current.Clear(); animator.GetCurrentAnimatorClipInfo(0, current);
            if (current.Count != 1 || current[0].clip != clip || Math.Abs(current[0].weight - 1) > .00001f) return false;
            double speed = (double)state.speed * state.speedMultiplier * animator.speed;
            if (!Finite(speed) || speed < 0 || speed > 8) return false;
            double offset = animator.GetFloat(IdleOffset);
            if (!Finite(offset) || Math.Abs(offset) > 1_000_000) return false;
            double phase = state.normalizedTime + offset;
            phase -= Math.Floor(phase);
            animation = new CreatureIdleAnimation
            {
                Clip = CreatureIdleAnimationRules.FrogIdle, AnchorTime = time,
                ClipTime = phase * CreatureIdleAnimationRules.FrogIdleLength,
                Speed = (float)speed, RopePhase = 0,
            };
            return true;
        }
    }

    internal static bool TryCreate(VisualReplica visual, Item source, out FrogIdleAnimationRuntime? runtime)
    {
        runtime = null;
        if (!source || source.itemID != 172 || !visual.IsReady) return false;
        var frog = source.GetComponent<FrogTongue>();
        if (!frog || frog!.GetType() != typeof(FrogTongue) || !frog.anim || !HasNoBehaviours(frog.anim)) return false;
        var clip = FindClip(frog.anim.runtimeAnimatorController);
        if (!clip || !visual.TryCloneTransform(frog.anim.transform, out var target) || target == frog.anim.transform) return false;
        var existing = target.GetComponent<Animator>();
        if (existing && (existing!.enabled || existing.runtimeAnimatorController)) return false;
        var evaluator = existing ? existing! : target.gameObject.AddComponent<Animator>();
        evaluator.enabled = false; evaluator.fireEvents = false; evaluator.applyRootMotion = false;
        evaluator.cullingMode = AnimatorCullingMode.AlwaysAnimate; evaluator.updateMode = AnimatorUpdateMode.Normal;
        evaluator.speed = 0; evaluator.runtimeAnimatorController = frog.anim.runtimeAnimatorController;
        if (!HasNoBehaviours(evaluator)) { evaluator.runtimeAnimatorController = null; return false; }
        runtime = new FrogIdleAnimationRuntime(clip!, target.gameObject, evaluator);
        return true;
    }
    internal void Apply(CreatureIdleAnimation animation, double replayTime)
    {
        if (disposed || !clip || !target || !target.activeInHierarchy || !animator || animation.Clip != CreatureIdleAnimationRules.FrogIdle) return;
        double time = CreatureIdleAnimationRules.SampleTime(animation, replayTime);
        bool needsBinding = !bound || !animator.isInitialized;
        if (!needsBinding && sampled && time == sampleTime) return;
        if (needsBinding) { animator.Rebind(); bound = true; }
        animator.SetFloat(IdleOffset, 0);
        animator.ResetTrigger("Jump"); animator.ResetTrigger("Lick"); animator.SetBool("Airborne", false);
        animator.Play(IdleState, 0, (float)(time / CreatureIdleAnimationRules.FrogIdleLength));
        animator.Update(0);
        sampled = true; sampleTime = time;
    }
    internal void Invalidate() => sampled = false;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; sampled = false;
        if (animator) { animator.enabled = false; animator.runtimeAnimatorController = null; }
    }

    private static AnimationClip? FindClip(RuntimeAnimatorController? controller)
    {
        if (!controller) return null;
        if (clips.TryGetValue(controller!, out var known)) return known.Clip;
        var audited = Validate(controller!);
        while (clips.Count >= MaximumControllers && clipOrder.Count != 0) clips.Remove(clipOrder.Dequeue());
        clips.Add(controller!, audited); clipOrder.Enqueue(controller!);
        return audited.Clip;
        static AuditedClip Validate(RuntimeAnimatorController source)
        {
            var result = new AuditedClip();
            if (source is AnimatorOverrideController || source.name != "FatFrog (1)") return result;
            var directory = source.animationClips;
            if (directory == null || directory.Length != 6) return result;
            // Validate the complete known directory once. A replaced or modified
            // controller keeps the existing captured-pose fallback.
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in directory)
            {
                if (!candidate || candidate.legacy || !names.Add(candidate.name)) return new AuditedClip();
                switch (candidate.name)
                {
                    case "FrogIdle":
                        if (!candidate.isLooping || Math.Abs(candidate.length - CreatureIdleAnimationRules.FrogIdleLength) > .00001 ||
                            candidate.events.Length != 0) return new AuditedClip();
                        result.Clip = candidate; break;
                    case "FrogJump": case "FrogLick": case "FrogLickEnd": case "FrogAirborneIdle": case "HelloMyBaby": break;
                    default: return new AuditedClip();
                }
            }
            return result;
        }
    }
    private static bool HasNoBehaviours(Animator animator)
    { try { return animator.GetBehaviours<StateMachineBehaviour>().Length == 0; } catch { return false; } }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
