using System;
using System.Collections.Generic;
using UnityEngine;

namespace PeakReplayLab;

// One cached native Animator, no bone traversal. Stable perches and the authored
// fly-away clip share a clock/checkpoint until a real native edge, changed phase,
// or unsupported visual input. Native events wake immediately; an unassigned
// supported bird gets a staggered 0.75–1 second safety audit. Voice assignment
// and unknown inputs retain the existing five Hertz pose fallback.
internal sealed class RavenAnimationCapture
{
    internal static bool EventHooksHealthy;
    private readonly Peak.Island.VoiceThief raven;
    private readonly Animator? animator;
    private readonly RuntimeAnimatorController? controller;
    private readonly AnimationClip? idle, fly, mouth;
    private readonly bool safeController;
    private readonly List<AnimatorClipInfo> current = new(1);
    private CreatureIdleAnimation? last;
    private bool dirty = true;
    private bool scheduleObserved, scheduledActive, scheduledAssigned, scheduledStealing, scheduledLeaving;
    private int confirmations;
    private double nextAudit = double.NegativeInfinity;
    private readonly double auditInterval;
    internal long Checks { get; private set; }
    internal long ScheduleSkips { get; private set; }
    internal long SemanticWakes { get; private set; }
    internal long MouthFallbacks { get; private set; }
    internal bool LowRateSafe { get; private set; }
    internal int State => last?.Clip == CreatureIdleAnimationRules.RavenFlyAway ? 1 : 0;
    internal RavenAnimationCapture(Peak.Island.VoiceThief raven)
    {
        this.raven = raven;
        // One native ID query at admission; entries audit at different phases.
        auditInterval = .75 + (raven.GetInstanceID() & 1023) / 4096d;
        if (!raven || RavenAnimationRuntime.NativeAnimator == null) return;
        animator = RavenAnimationRuntime.NativeAnimator(raven);
        if (!animator) return;
        controller = animator!.runtimeAnimatorController;
        if (!RavenAnimationRuntime.TryClips(controller, out idle, out fly, out mouth)) return;
        safeController = RavenAnimationRuntime.CanReference(animator!);
        LowRateSafe = EventHooksHealthy && safeController;
    }
    internal void NotifyNativeEvent() { dirty = true; confirmations = 2; }
    internal bool Due(double now, bool active, bool force = false)
    {
        if (!raven || !CreatureIdleAnimationRules.Finite(now)) return true;
        bool assigned = raven.IsAssigned, stealing = raven.CurrentlyStealing, leaving = raven.IsLeaving;
        bool edge = !scheduleObserved || scheduledActive != active || scheduledAssigned != assigned ||
            scheduledStealing != stealing || scheduledLeaving != leaving;
        scheduleObserved = true; scheduledActive = active; scheduledAssigned = assigned;
        scheduledStealing = stealing; scheduledLeaving = leaving;
        if (edge) { NotifyNativeEvent(); SemanticWakes++; }
        // Assigned local victims drive the mouth even while not stealing.
        // Until that path is separately resource-driven, retain its 5 Hz poses.
        if (force || dirty || confirmations != 0 || assigned || stealing || !EventHooksHealthy ||
            !safeController || !LowRateSafe || last == null || now + 1e-8 >= nextAudit) return true;
        ScheduleSkips++; return false;
    }
    internal CreatureIdleAnimation Reanchor(double now)
    {
        if (last == null) throw new InvalidOperationException("No supported raven animation to re-anchor.");
        return last = new CreatureIdleAnimation { Clip = last.Clip, AnchorTime = now,
            ClipTime = CreatureIdleAnimationRules.SampleTime(last, now), Speed = last.Speed };
    }

    internal bool TryObserve(double now, bool active, out CreatureIdleAnimation? animation, out bool changed)
    {
        Checks++; animation = null; changed = false;
        nextAudit = now + auditInterval;
        if (confirmations != 0) confirmations--;
        if (!EventHooksHealthy || !safeController || !active || !raven || !animator || !controller || !idle || !fly || !mouth ||
            !CreatureIdleAnimationRules.Finite(now) || Math.Abs(now) > 1_000_000 ||
            !animator!.enabled || !animator.gameObject.activeInHierarchy || animator.runtimeAnimatorController != controller ||
            animator.layerCount != 2 || animator.updateMode != AnimatorUpdateMode.Normal ||
            animator.cullingMode != AnimatorCullingMode.AlwaysAnimate || animator.isHuman || animator.applyRootMotion ||
            animator.IsInTransition(0) || animator.IsInTransition(1)) return Unsupported();
        // Keep observed geometry for real voice-driven layer blending; the
        // outer 5 Hz clock records sparse poses and playback interpolates them.
        // CurrentlyStealing=false alone is insufficient: a local assigned
        // victim can still drive this layer in the native Update.
        float weight = animator.GetLayerWeight(1);
        if (weight != 0)
        {
            MouthFallbacks++;
            return Unsupported(lowRateSafe: CreatureIdleAnimationRules.Finite(weight) && weight > 0 && weight <= 1);
        }
        var state = animator.GetCurrentAnimatorStateInfo(0);
        bool isIdle = state.fullPathHash == RavenAnimationRuntime.IdleState;
        bool isFly = state.fullPathHash == RavenAnimationRuntime.FlyState;
        // RPCA_FlyAway yields one frame before Animator.Play. Remain on real
        // poses during that gap; do not start an authored flight early.
        if (!isIdle && !isFly || isIdle && raven.IsLeaving || isFly && !raven.IsLeaving || state.loop != isIdle ||
            !CreatureIdleAnimationRules.Finite(state.normalizedTime) || Math.Abs(state.speed - 1) > .000001f ||
            animator.GetCurrentAnimatorClipInfoCount(0) != 1) return Unsupported();
        current.Clear(); animator.GetCurrentAnimatorClipInfo(0, current);
        if (current.Count != 1 || current[0].clip != (isIdle ? idle : fly) || Math.Abs(current[0].weight - 1) > .00001) return Unsupported();
        double speed = (double)state.speed * state.speedMultiplier * animator.speed;
        float offset = isIdle ? animator.GetFloat(RavenAnimationRuntime.RandomParameter) : 0;
        if (!CreatureIdleAnimationRules.Finite(speed) || speed < 0 || speed > 8 ||
            !CreatureIdleAnimationRules.Finite(offset) || offset < 0 || offset > 1) return Unsupported();
        double normalized = state.normalizedTime + (double)offset;
        double phase = isIdle ? (normalized - Math.Floor(normalized)) * 3 : Math.Max(0, Math.Min(3, normalized * 3));
        string clip = isIdle ? CreatureIdleAnimationRules.RavenIdle : CreatureIdleAnimationRules.RavenFlyAway;
        if (!dirty && last != null && last.Clip == clip && last.Speed == (float)speed &&
            SamePhase(CreatureIdleAnimationRules.SampleTime(last, now), phase, isIdle))
        { LowRateSafe = true; animation = last; return true; }
        // Only an observed boundary allocates a new descriptor. Initial phase
        // includes the native cycle offset rather than synchronizing all birds.
        animation = last = new CreatureIdleAnimation { Clip = clip, AnchorTime = now, ClipTime = phase, Speed = (float)speed };
        LowRateSafe = true; dirty = false; changed = true; return true;
    }
    private bool Unsupported(bool lowRateSafe = false) { LowRateSafe = lowRateSafe; last = null; dirty = true; return false; }
    private static bool SamePhase(double predicted, double observed, bool loop)
    {
        double delta = Math.Abs(predicted - observed);
        if (loop) delta = Math.Min(delta, Math.Abs(3 - delta));
        // Unity state clocks are floats. A sub-frame discrepancy may accumulate
        // on long sessions; re-anchor before a visible drift develops.
        return delta <= .002;
    }
}
