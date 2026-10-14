using System;
using System.Collections.Generic;
using UnityEngine;

namespace PeakReplayLab;

// Only a known single native clip with a known procedural Y spin is replaced
// by a resource clock. Item root motion, ownership and gameplay fields remain
// the item track's job; transitions and unknown controllers retain capture.
internal sealed class EarlyWormAnimationCapture
{
    private const int WalkState = unchecked((int)2261229391u), InHandState = 2120410058, CookedState = 824124038;
    private static readonly int OnGround = Animator.StringToHash("OnGround"), Cooked = Animator.StringToHash("Cooked");
    internal const double ProbeInterval = .2;
    private readonly Item item;
    private readonly Peak.EarlyWorm worm = null!;
    private readonly Animator animator = null!;
    private readonly Rotate rotate = null!;
    private readonly Transform pivot = null!;
    private readonly RuntimeAnimatorController? controller;
    private readonly EarlyWormAnimationRuntime.Directory directory;
    private readonly bool behaviourFree;
    private readonly List<AnimatorClipInfo> current = new(1);
    private CreatureIdleAnimation? previous;
    private double nextCheck = double.NegativeInfinity;
    internal long ClockChecks { get; private set; }
    internal long GeometrySkips { get; private set; }
    internal long DescriptorChanges { get; private set; }
    internal bool Supported => directory.Supported && behaviourFree;

    internal EarlyWormAnimationCapture(Item item)
    {
        this.item = item;
        worm = item.GetComponent<Peak.EarlyWorm>()!;
        animator = worm ? worm.anim : null!;
        rotate = worm ? worm.rotate : null!;
        pivot = animator ? animator.transform : null!;
        controller = animator ? animator.runtimeAnimatorController : null!;
        directory = EarlyWormAnimationRuntime.FindDirectory(controller);
        behaviourFree = animator && animator.GetBehaviours<StateMachineBehaviour>().Length == 0;
    }
    internal static bool TryCreate(Item item, out EarlyWormAnimationCapture? capture)
    {
        capture = null;
        if (!item || item.itemID != 184) return false;
        var candidate = new EarlyWormAnimationCapture(item);
        if (!candidate.worm || candidate.worm.GetType() != typeof(Peak.EarlyWorm) || !candidate.Supported) return false;
        capture = candidate; return true;
    }
    internal void Invalidate() => nextCheck = double.NegativeInfinity;

    internal bool TryObserve(double now, bool force, out CreatureIdleAnimation? animation, out bool changed)
    {
        animation = previous; changed = false;
        if (!Finite(now) || Math.Abs(now) > 1_000_000) return Unsupported(out animation, out changed);
        if (!force && now + 1e-8 < nextCheck)
        { if (previous != null) GeometrySkips++; return previous != null; }
        nextCheck = now + ProbeInterval; ClockChecks++;
        if (!item || !worm || !animator || !rotate || !pivot || !controller || !Supported ||
            !animator.enabled || !animator.gameObject.activeInHierarchy ||
            animator.runtimeAnimatorController != controller || animator.layerCount != 1 ||
            animator.updateMode != AnimatorUpdateMode.Normal || animator.cullingMode != AnimatorCullingMode.AlwaysAnimate ||
            animator.applyRootMotion || animator.isHuman || animator.IsInTransition(0) || rotate.tf != pivot)
            return Unsupported(out animation, out changed);
        bool ground = item.itemState == ItemState.Ground;
        bool cooked = item.cooking && item.cooking!.timesCookedLocal > 0;
        var state = animator.GetCurrentAnimatorStateInfo(0);
        if ((!cooked && !state.loop) || !Finite(state.normalizedTime) || animator.GetCurrentAnimatorClipInfoCount(0) != 1)
            return Unsupported(out animation, out changed);
        current.Clear(); animator.GetCurrentAnimatorClipInfo(0, current);
        if (current.Count != 1 || Math.Abs(current[0].weight - 1) > .00001f) return Unsupported(out animation, out changed);
        if (animator.GetBool(OnGround) != ground || animator.GetBool(Cooked) != cooked) return Unsupported(out animation, out changed);
        string key;
        AnimationClip? expected;
        if (cooked)
        { key = CreatureIdleAnimationRules.EarlyWormCooked; expected = directory.Cooked; if (state.fullPathHash != CookedState || rotate.enabled) return Unsupported(out animation, out changed); }
        else if (ground)
        { key = CreatureIdleAnimationRules.EarlyWormWalk; expected = directory.Walk; if (state.fullPathHash != WalkState || !rotate.enabled) return Unsupported(out animation, out changed); }
        else
        { key = CreatureIdleAnimationRules.EarlyWormInHand; expected = directory.InHand; if (state.fullPathHash != InHandState || rotate.enabled) return Unsupported(out animation, out changed); }
        if (current[0].clip != expected) return Unsupported(out animation, out changed);
        // A mod changing Rotate's axis/rate keeps the captured-pose fallback.
        if (rotate.rotation.x != 0 || rotate.rotation.y != -50 || rotate.rotation.z != 0)
            return Unsupported(out animation, out changed);
        double speed = cooked ? 0 : (double)state.speed * state.speedMultiplier * animator.speed;
        if (!Finite(speed) || speed < 0 || speed > 8) return Unsupported(out animation, out changed);
        double phase = cooked ? 0 : state.normalizedTime - Math.Floor(state.normalizedTime);
        var rotation = pivot.localRotation;
        if (!Valid(rotation)) return Unsupported(out animation, out changed);
        float spin = rotate.enabled ? -50 : 0;
        if (SameClock(previous, key, phase, (float)speed, spin, rotation, now))
        { animation = previous; GeometrySkips++; return true; }
        animation = previous = new CreatureIdleAnimation
        {
            Clip = key, AnchorTime = now, ClipTime = phase, Speed = (float)speed,
            SpinRotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w }, SpinSpeed = spin,
        };
        changed = true; DescriptorChanges++; return true;
    }

    private bool Unsupported(out CreatureIdleAnimation? animation, out bool changed)
    { changed = previous != null; previous = animation = null; return false; }
    private static bool SameClock(CreatureIdleAnimation? old, string clip, double phase, float speed, float spin, Quaternion rotation, double now)
    {
        if (old == null || old.Clip != clip || Math.Abs(old.Speed - speed) > .00001f || old.SpinSpeed != spin || old.SpinRotation is not { Length: 4 } anchor) return false;
        double delta = Math.Abs(CreatureIdleAnimationRules.SampleTime(old, now) - phase);
        if (clip != CreatureIdleAnimationRules.EarlyWormCooked) delta = Math.Min(delta, 1 - delta);
        if (delta > .075) return false;
        double angle = (now - old.AnchorTime) * spin;
        var predicted = new Quaternion(anchor[0], anchor[1], anchor[2], anchor[3]) * Quaternion.AngleAxis((float)(angle % 360), Vector3.up);
        double dot = (double)predicted.x * rotation.x + (double)predicted.y * rotation.y + (double)predicted.z * rotation.z + (double)predicted.w * rotation.w;
        return Finite(dot) && Math.Abs(dot) >= .999999;
    }
    private static bool Valid(Quaternion value)
    {
        if (!Finite(value.x) || !Finite(value.y) || !Finite(value.z) || !Finite(value.w)) return false;
        double norm = (double)value.x * value.x + (double)value.y * value.y + (double)value.z * value.z + (double)value.w * value.w;
        return norm >= .999 && norm <= 1.001;
    }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
