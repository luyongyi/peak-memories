using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace PeakReplayLab;

// A disabled, manually evaluated Animator binds these clips on the inert tree.
// AnimationClip.SampleAnimation does not bind this native controller reliably.
// The original Raven
// controller contains SFX component-enable and feather GameObject curves; no
// gameplay, SFX or particle component is copied by VisualReplica. Their actual
// births/audio remain owned by the independent effect and audio tracks.
internal sealed class RavenAnimationRuntime : IDisposable
{
    internal const int IdleState = unchecked((int)4155861363u);
    internal const int FlyState = unchecked((int)4189856725u);
    internal const int MouthState = unchecked((int)3023314922u);
    internal const int RandomParameter = unchecked((int)2516931489u);
    private sealed class AuditedClips
    {
        internal AnimationClip? Idle, Fly, Mouth;
        internal bool Valid;
    }
    private static readonly ConditionalWeakTable<RuntimeAnimatorController, AuditedClips> assets = new();
    internal static readonly AccessTools.FieldRef<Peak.Island.VoiceThief, Animator>? NativeAnimator = AnimatorField();
    private readonly AnimationClip idle, fly;
    private readonly GameObject target;
    private readonly Animator animator;
    // Complete audited difference of all controller Transform bindings minus
    // FlyAway bindings: six rotations, with no missing position/scale binding.
    private static readonly string[] flightHeldPaths =
    {
        "Armature/Base/L_LegConnect", "Armature/Base/R_LegConnect",
        "Armature/Base/Torso/L_Shoulder", "Armature/Base/Torso/R_Shoulder",
        "Armature/Base/Torso/Neck/Head/Bottom-Beak", "Armature/Base/Torso/Neck/Head/Top-Beak",
    };
    private readonly Transform[] flightHeldTransforms;
    private readonly Quaternion[] flightHeldRotations;
    private AnimationClip? sampledClip;
    private double sampledTime;
    private bool disposed, bound;

    private RavenAnimationRuntime(AnimationClip idle, AnimationClip fly, GameObject target, Animator animator, Transform[] flightHeldTransforms)
    {
        this.idle = idle; this.fly = fly; this.target = target; this.animator = animator;
        this.flightHeldTransforms = flightHeldTransforms;
        flightHeldRotations = new Quaternion[flightHeldTransforms.Length];
    }

    internal static bool CanReference(Animator source)
    {
        try { return source && source.GetBehaviours<StateMachineBehaviour>().Length == 0; }
        catch { return false; }
    }

    internal static bool TryClips(RuntimeAnimatorController? controller, out AnimationClip? idle, out AnimationClip? fly, out AnimationClip? mouth)
    {
        idle = fly = mouth = null;
        if (!controller) return false;
        var found = assets.GetValue(controller!, source =>
        {
            var result = new AuditedClips();
            if (source is AnimatorOverrideController || source.name != "Raven") return result;
            var clips = source.animationClips;
            if (clips == null || clips.Length != 3) return result;
            foreach (var clip in clips)
            {
                if (!clip || clip.legacy || Math.Abs(clip.frameRate - 60) > .001 || clip.events.Length != 0) return result;
                if (clip.name == "RavenIdle" && !result.Idle && clip.isLooping && Math.Abs(clip.length - 3) < .00001) result.Idle = clip;
                else if (clip.name == "RavenFlyAway" && !result.Fly && !clip.isLooping && Math.Abs(clip.length - 3) < .00001) result.Fly = clip;
                else if (clip.name == "RavenMouthOpen" && !result.Mouth && clip.isLooping && Math.Abs(clip.length - .5) < .00001) result.Mouth = clip;
                else return result;
            }
            result.Valid = result.Idle && result.Fly && result.Mouth;
            return result;
        });
        if (!found.Valid) return false;
        idle = found.Idle; fly = found.Fly; mouth = found.Mouth; return true;
    }

    internal static bool TryCreate(VisualReplica visual, GameObject source, CreatureIdleAnimation animation, out RavenAnimationRuntime? runtime)
    {
        runtime = null;
        if (!IsRaven(animation.Clip) || !source || !visual.IsReady || NativeAnimator == null) return false;
        var raven = source.GetComponent<Peak.Island.VoiceThief>();
        if (!raven) return false;
        var animator = NativeAnimator(raven!);
        if (!animator || !CanReference(animator!) || !TryClips(animator.runtimeAnimatorController, out var idle, out var fly, out _) ||
            !visual.TryCloneTransform(animator.transform, out var clone) || clone == animator.transform) return false;
        var flightHeld = new Transform[flightHeldPaths.Length];
        for (int i = 0; i < flightHeld.Length; i++)
        {
            var node = clone.Find(flightHeldPaths[i]);
            if (!node) return false;
            flightHeld[i] = node!;
        }
        var existing = clone.GetComponent<Animator>();
        // Only our controller-free disabled evaluator can survive pool reuse.
        if (existing && (existing!.enabled || existing.runtimeAnimatorController)) return false;
        var evaluator = existing ? existing! : clone.gameObject.AddComponent<Animator>();
        evaluator.enabled = false; evaluator.fireEvents = false; evaluator.applyRootMotion = false;
        evaluator.cullingMode = AnimatorCullingMode.AlwaysAnimate; evaluator.updateMode = AnimatorUpdateMode.Normal;
        evaluator.speed = 0; evaluator.runtimeAnimatorController = animator.runtimeAnimatorController;
        if (evaluator.GetBehaviours<StateMachineBehaviour>().Length != 0)
        { evaluator.runtimeAnimatorController = null; return false; }
        runtime = new RavenAnimationRuntime(idle!, fly!, clone.gameObject, evaluator, flightHeld); return true;
    }

    internal static bool IsRaven(string clip) => clip == CreatureIdleAnimationRules.RavenIdle || clip == CreatureIdleAnimationRules.RavenFlyAway;
    internal void Apply(CreatureIdleAnimation animation, double time)
    {
        if (disposed || !target || !animator) return;
        var clip = animation.Clip == CreatureIdleAnimationRules.RavenIdle ? idle :
            animation.Clip == CreatureIdleAnimationRules.RavenFlyAway ? fly : null;
        if (!clip) throw new InvalidOperationException("Unsupported native raven animation.");
        double phase = CreatureIdleAnimationRules.SampleTime(animation, time);
        // Preparation runs while the replica is hidden. Rebind only after the
        // complete hierarchy is activated by the normal pose presentation.
        if (!target.activeInHierarchy) return;
        bool bindingLost = !bound || !animator.isInitialized;
        if (!bindingLost && sampledClip == clip && sampledTime == phase) return;
        // FlyAway does not write these six rotations. The source Animator's
        // authored defaults differ from defaults captured when the clone first
        // binds an already animated idle checkpoint. Keep the complete audited
        // unanimated rotation set from this flight's recorded checkpoint.
        // This also protects a first seek that starts directly inside flight.
        bool flight = animation.Clip == CreatureIdleAnimationRules.RavenFlyAway;
        if (flight) for (int i = 0; i < flightHeldTransforms.Length; i++)
            flightHeldRotations[i] = flightHeldTransforms[i].localRotation;
        if (bindingLost) { animator.Rebind(); bound = true; }
        animator.SetLayerWeight(0, 1); animator.SetLayerWeight(1, 0);
        // Capture already folded the native randomized cycle offset into phase.
        // Leaving the parameter live would apply that offset a second time.
        animator.SetFloat(RandomParameter, 0);
        animator.Play(animation.Clip == CreatureIdleAnimationRules.RavenIdle ? IdleState : FlyState, 0, (float)(phase / 3));
        animator.Update(0);
        if (flight) for (int i = 0; i < flightHeldTransforms.Length; i++)
            flightHeldTransforms[i].localRotation = flightHeldRotations[i];
        sampledClip = clip; sampledTime = phase;
    }
    internal void Invalidate() => sampledClip = null;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; sampledClip = null;
        if (animator) { animator.enabled = false; animator.runtimeAnimatorController = null; }
    }
    private static AccessTools.FieldRef<Peak.Island.VoiceThief, Animator>? AnimatorField()
    { try { return AccessTools.FieldRefAccess<Peak.Island.VoiceThief, Animator>("anim"); } catch { return null; } }
}
