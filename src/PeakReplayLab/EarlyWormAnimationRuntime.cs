using System;
using System.Collections.Generic;
using UnityEngine;

namespace PeakReplayLab;

// The generic SkinnedMeshRenderer bindings of this native controller are not
// evaluated by AnimationClip.SampleAnimation on the inert clone. A disabled,
// explicitly evaluated visual Animator supplies Unity's native property binding
// without automatic time advancement, gameplay components or animation events.
internal sealed class EarlyWormAnimationRuntime : IDisposable
{
    internal sealed class Directory
    {
        internal AnimationClip? Walk, InHand, Cooked;
        internal RuntimeAnimatorController? Controller;
        internal bool Supported => Walk && InHand && Cooked;
        internal AnimationClip? Find(string key) => key switch
        {
            CreatureIdleAnimationRules.EarlyWormWalk => Walk,
            CreatureIdleAnimationRules.EarlyWormInHand => InHand,
            CreatureIdleAnimationRules.EarlyWormCooked => Cooked,
            _ => null,
        };
    }
    // Main-thread-only, bounded resource directory. No weak-table lookup is
    // introduced into replay page workers or the full recording writer.
    private static readonly Dictionary<RuntimeAnimatorController, Directory> directories = new();
    private static readonly Queue<RuntimeAnimatorController> directoryOrder = new();
    private const int MaximumDirectories = 16;
    private readonly Directory directory;
    private readonly Transform target;
    private readonly Animator animator;
    private CreatureIdleAnimation? previous;
    private double previousTime;
    private bool sampled, disposed, bound;

    private EarlyWormAnimationRuntime(Directory directory, Transform target, Animator animator)
    { this.directory = directory; this.target = target; this.animator = animator; }

    internal static bool TryCreate(VisualReplica visual, Item source, out EarlyWormAnimationRuntime? runtime)
    {
        runtime = null;
        if (!source || source.itemID != 184 || !visual.IsReady) return false;
        var worm = source.GetComponent<Peak.EarlyWorm>();
        if (!worm || worm!.GetType() != typeof(Peak.EarlyWorm) || !worm.anim || !worm.rotate ||
            worm.rotate.tf != worm.anim.transform || worm.anim.GetBehaviours<StateMachineBehaviour>().Length != 0) return false;
        var directory = FindDirectory(worm.anim.runtimeAnimatorController);
        if (!directory.Supported || !visual.TryCloneTransform(worm.anim.transform, out var target) || target == worm.anim.transform) return false;
        // VisualReplica never copies native Animators. Reuse only our disabled
        // controller-free evaluator when a pooled visual returns to this state.
        var existing = target.GetComponent<Animator>();
        if (existing && (existing!.enabled || existing.runtimeAnimatorController)) return false;
        var evaluator = existing ? existing! : target.gameObject.AddComponent<Animator>();
        evaluator.enabled = false; evaluator.fireEvents = false; evaluator.applyRootMotion = false;
        evaluator.cullingMode = AnimatorCullingMode.AlwaysAnimate; evaluator.updateMode = AnimatorUpdateMode.Normal;
        evaluator.speed = 0; evaluator.runtimeAnimatorController = directory.Controller;
        // Native assets are trusted but a controller replaced by another mod
        // must not execute StateMachineBehaviours on a replay visual.
        if (evaluator.GetBehaviours<StateMachineBehaviour>().Length != 0)
        { evaluator.runtimeAnimatorController = null; return false; }
        runtime = new EarlyWormAnimationRuntime(directory, target, evaluator);
        return true;
    }

    internal void Apply(CreatureIdleAnimation animation, double replayTime)
    {
        if (disposed || !target || !target.gameObject.activeInHierarchy || !animator) return;
        var clip = directory.Find(animation.Clip);
        if (!clip) return;
        bool needsBinding = !bound || !animator.isInitialized;
        if (!needsBinding && sampled && ReferenceEquals(previous, animation) && previousTime == replayTime) return;
        double sample = CreatureIdleAnimationRules.SampleTime(animation, replayTime);
        // The visual is hidden while its resource preparation runs. Binding is
        // deferred until Replica.Apply has activated the complete hierarchy.
        if (needsBinding) { animator.Rebind(); bound = true; }
        bool walk = animation.Clip == CreatureIdleAnimationRules.EarlyWormWalk;
        bool cooked = animation.Clip == CreatureIdleAnimationRules.EarlyWormCooked;
        animator.SetBool("OnGround", walk);
        animator.SetBool("Cooked", cooked);
        int state = cooked ? 824124038 : walk ? unchecked((int)2261229391u) : 2120410058;
        animator.Play(state, 0, (float)sample);
        animator.Update(0);
        // Native Rotate modifies the Animator Pivot itself; none of these clips
        // writes that transform. Use the recorded local quaternion at the same
        // anchor as the clip, never the template's starting rotation.
        if (animation.SpinRotation is { Length: 4 } rotation)
        {
            var anchor = new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]);
            double degrees = (replayTime - animation.AnchorTime) * animation.SpinSpeed;
            target.localRotation = anchor * Quaternion.AngleAxis((float)(degrees % 360), Vector3.up);
        }
        sampled = true; previous = animation; previousTime = replayTime;
    }
    internal void Invalidate() { sampled = false; previous = null; }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Invalidate();
        // Keep the inert disabled component with its pooled clone. Deferred
        // Object.Destroy would race a same-frame template reuse.
        if (animator) { animator.enabled = false; animator.runtimeAnimatorController = null; }
    }

    internal static Directory FindDirectory(RuntimeAnimatorController? controller)
    {
        if (!controller) return new Directory();
        if (directories.TryGetValue(controller!, out var found)) return found;
        var directory = ValidateDirectory(controller!);
        while (directories.Count >= MaximumDirectories && directoryOrder.Count != 0) directories.Remove(directoryOrder.Dequeue());
        directories.Add(controller!, directory); directoryOrder.Enqueue(controller!);
        return directory;
    }

    private static Directory ValidateDirectory(RuntimeAnimatorController controller)
    {
        var result = new Directory();
        if (controller is AnimatorOverrideController || controller.name != "EarlyWorm") return result;
        var clips = controller.animationClips;
        if (clips == null || clips.Length != 3) return result;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var clip in clips)
        {
            if (!clip || clip.legacy || clip.events.Length != 0 || !names.Add(clip.name)) return new Directory();
            switch (clip.name)
            {
                case "EarlyWormWalk":
                    if (!clip.isLooping || Math.Abs(clip.length - 1) > .00001) return new Directory();
                    result.Walk = clip; break;
                case "EarlyWormInHand":
                    if (!clip.isLooping || Math.Abs(clip.length - 1) > .00001) return new Directory();
                    result.InHand = clip; break;
                case "EarlyWormCooked":
                    if (Math.Abs(clip.length) > .00001) return new Directory();
                    result.Cooked = clip; break;
                default: return new Directory();
            }
        }
        result.Controller = controller;
        return result;
    }
}
