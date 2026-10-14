using System;
using UnityEngine;

namespace PeakReplayLab;

// A shader property or Light alone does not prove that anything writes it.
// Build this conservative capability once at admission, never in Capture.
// Unknown scripts/controllers retain the existing observation fallback.
internal static class NativeStaticVisualCapability
{
    public static bool CrateEventOnly(Luggage source)
    {
        if (source.GetType() != typeof(Luggage) && source.GetType() != typeof(RespawnChest)) return false;
        return AuditedTree(source.gameObject, crate: true, "");
    }
    public static bool SpawnedEventOnly(GameObject source, string kind)
    {
        // Growth, cracks, sphere death, flags, stove output and cannon cycles
        // retain their independent native activity readers.
        if (kind is not ("piton" or "shelf-shroom" or "bounce-shroom" or "cloud-fungus")) return false;
        return AuditedTree(source, crate: false, kind);
    }
    private static bool AuditedTree(GameObject root, bool crate, string kind)
    {
        foreach (var component in root.GetComponentsInChildren<Component>(true))
        {
            if (!component) return false; // Missing native script is unproven.
            if (component is Animator animator)
            {
                // Verified installed controllers contain no state behaviours.
                // Spawn/bounce/push clips are finite and have explicit event
                // windows; an arbitrary replacement controller stays observed.
                var controller = animator.runtimeAnimatorController;
                if (!controller) continue;
                if (controller is AnimatorOverrideController || animator.GetBehaviours<StateMachineBehaviour>().Length != 0) return false;
                if (crate)
                {
                    if (component.gameObject != root || controller.name is not ("Luggage" or "LuggageClown" or "RespawnChest")) return false;
                }
                else if (!((kind is "shelf-shroom" or "bounce-shroom" or "cloud-fungus") && controller.name == "ShelfShroomSpawn" ||
                    kind == "piton" && controller.name == "ClimbingSpike")) return false;
                continue;
            }
            if (component is Animation) return false;
            if (component is not MonoBehaviour) continue;
            var type = component.GetType();
            if (type == typeof(CaptureRootLifecycleObserver) || type == typeof(Photon.Pun.PhotonView)) continue;
            if (type.Assembly != typeof(Luggage).Assembly) return false;
            string name = type.FullName ?? "";
            if (name is "Peak.PhotonCleanupHelper" or "AddScreenshake" or
                    "SFX_PlayOneShot" or "AnimationJuice" or "LazyGizmo") continue;
            if (crate && name is "Luggage" or "RespawnChest" or "MirageLuggage") continue;
            // SpineCheck only runs during native placement validation. Its
            // completed spawn precedes the recorder's admitted initial read.
            if (crate && name == "SpineCheck") continue;
            if (!crate && name is "ClimbHandle" or "CollisionModifier" or "MushroomBounceBadgeTracker") continue;
            // Includes LanternLight, Lantern, Action_Torch, LightCuller,
            // DOTweenAnimation and every unproven/modded script.
            return false;
        }
        return true;
    }
    internal static double BindAnimationWindow(GameObject source, out bool advancing)
    {
        // Cold admission only: cache a finite envelope from the verified native
        // clips. Idle roots never read these Animator clocks again.
        double duration = SpawnedReplayRules.AnimationWatchSeconds;
        advancing = false;
        foreach (var animator in source.GetComponentsInChildren<Animator>(true))
        {
            if (!animator || !animator.runtimeAnimatorController) continue;
            double speed = Math.Abs(animator.speed);
            if (double.IsNaN(speed) || double.IsInfinity(speed) || speed == 0) continue;
            foreach (var clip in animator.runtimeAnimatorController.animationClips)
                if (clip && !clip.isLooping && clip.length > 0)
                    duration = Math.Max(duration, Math.Min(60, clip.length / speed + .25));
            if (!animator.isActiveAndEnabled || animator.layerCount < 1) continue;
            var state = animator.GetCurrentAnimatorStateInfo(0);
            advancing |= Moving(state, animator.speed);
            if (animator.IsInTransition(0)) advancing |= Moving(animator.GetNextAnimatorStateInfo(0), animator.speed);
        }
        return duration;
    }
    private static bool Moving(AnimatorStateInfo state, float animatorSpeed)
    {
        float rate = animatorSpeed * state.speed * state.speedMultiplier;
        return !state.loop && state.length > 0 && !float.IsNaN(rate) && !float.IsInfinity(rate) &&
            (rate > 0 && state.normalizedTime < 1 || rate < 0 && state.normalizedTime > 0);
    }
}
