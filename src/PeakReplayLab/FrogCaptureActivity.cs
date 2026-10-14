using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace PeakReplayLab;

// A Frog's inherited sleeping flag does not stop its overridden FixedUpdate or
// existing coroutines. This gate combines native action fields, an audited clip
// and the existing staggered item root safety poll; no hierarchy is searched here.
internal sealed class FrogCaptureActivity
{
    private static readonly ConditionalWeakTable<FrogTongue, FrogCaptureActivity> activities = new();
    private static readonly AccessTools.FieldRef<FrogTongue, bool>? pulling = Resolve<bool>("_isPulling");
    private static readonly AccessTools.FieldRef<FrogTongue, bool>? thrown = Resolve<bool>("_thrown");
    private static readonly AccessTools.FieldRef<FrogTongue, bool>? inWater = Resolve<bool>("inWater");
    private static readonly AccessTools.FieldRef<FrogTongue, float>? tongueLength = Resolve<float>("_tongueLength");
    private static readonly AccessTools.FieldRef<FrogTongue, Character>? targetCharacter = Resolve<Character>("_targetCharacter");
    private static readonly AccessTools.FieldRef<FrogTongue, Rigidbody>? targetBody = Resolve<Rigidbody>("_targetRig");
    internal static bool EventHooksHealthy { get; set; }
    private readonly FrogTongue frog;
    private readonly Item item;
    private readonly Transform root;
    private readonly Rigidbody? body;
    private readonly Animator? animator;
    private readonly Transform? visuals;
    private readonly FrogIdleAnimationRuntime.Capture idle;
    private readonly double probeDelay;
    private double eventUntil = double.NegativeInfinity;
    private double nextIdleProbe = double.NegativeInfinity;
    private bool hidden;
    internal bool Holding { get; private set; }
    internal long RootChecks { get; private set; }
    internal long IdleSkips { get; private set; }
    internal long IdleTransitions { get; private set; }
    internal long AnimationValidationReads => idle.ValidationReads;

    private FrogCaptureActivity(FrogTongue frog, Item item)
    {
        this.frog = frog; this.item = item; root = item.transform; body = item.rig;
        animator = frog.anim; visuals = frog.visuals;
        probeDelay = ItemSleepPolicy.ProbeDelay(item.GetInstanceID());
        idle = new FrogIdleAnimationRuntime.Capture(frog);
    }
    internal static FrogCaptureActivity? Create(Item item)
    {
        if (item.itemID != 172 || item.GetType() != typeof(MobItem)) return null;
        var frog = item.GetComponent<FrogTongue>();
        if (!frog || frog!.GetType() != typeof(FrogTongue)) return null;
        var activity = new FrogCaptureActivity(frog, item);
        activities.Remove(frog); activities.Add(frog, activity);
        return activity;
    }
    internal static void NotifyNativeEvent(FrogTongue frog)
    {
        if (!frog || ReplaySafety.Active) return;
        if (activities.TryGetValue(frog, out var activity))
        { activity.eventUntil = Time.timeAsDouble + .8; activity.nextIdleProbe = double.NegativeInfinity; activity.Holding = false; }
        if (frog.mobItem) ItemCapture.MarkDirty(frog.mobItem);
    }
    internal static bool IsDormant(FrogTongue frog) =>
        activities.TryGetValue(frog, out var activity) && activity.Holding && activity.SemanticallyQuiet(Time.timeAsDouble);

    internal bool TryEnter(double now, ItemFrame checkpoint, bool settled, out CreatureIdleAnimation? animation)
    {
        animation = null;
        if (!settled || now < nextIdleProbe || !SemanticallyQuiet(now) || !idle.Supported) return false;
        // ItemRestPolicy has just confirmed the sampled root and velocities.
        // Retry an unsupported current animation at a bounded rate, rather than
        // adding another set of Animator/Transform getters to every action tick.
        nextIdleProbe = now + probeDelay;
        bool sleeping = frog.sleeping;
        if (sleeping)
        {
            if (!animator || animator!.enabled || !visuals || visuals!.gameObject.activeSelf) return false;
        }
        else if (!idle.TryCapture(now, out animation)) return false;
        Holding = true; hidden = sleeping; IdleTransitions++;
        return true;
    }
    internal bool TryHold(double now, ItemFrame checkpoint, bool interrupted, bool safetyProbe)
    {
        if (!Holding) return false;
        if (interrupted || !SemanticallyQuiet(now) || hidden != frog.sleeping)
        { Holding = false; return false; }
        if (safetyProbe)
        {
            RootChecks++;
            if (!RootStable(checkpoint.Pose)) { Holding = false; return false; }
            if (hidden)
            {
                if (!animator || animator!.enabled || !visuals || visuals!.gameObject.activeSelf)
                { Holding = false; return false; }
            }
            else if (!idle.TryCapture(now, out var observed) || !SameClock(checkpoint.IdleAnimation, observed!, now))
            { Holding = false; return false; }
        }
        IdleSkips++; return true;
    }
    internal void Release() => Holding = false;

    private bool SemanticallyQuiet(double now) =>
        EventHooksHealthy && ItemCapture.EventHooksHealthy && pulling != null && thrown != null && inWater != null &&
        tongueLength != null && targetCharacter != null && targetBody != null &&
        now >= eventUntil && item.itemState == ItemState.Ground &&
        !(item.cooking && item.cooking!.timesCookedLocal > 0) &&
        !pulling(frog) && !thrown(frog) && !inWater(frog) &&
        Finite(tongueLength(frog)) && tongueLength(frog) <= .00001f && tongueLength(frog) >= -1 &&
        ReferenceEquals(targetCharacter(frog), null) && ReferenceEquals(targetBody(frog), null);

    private bool RootStable(ObjectPose pose)
    {
        if (!body || pose.Position.Length < 3 || pose.Rotation.Length < 4 || pose.Scale.Length < 3) return false;
        var p = root.position; var r = root.rotation; var s = root.lossyScale;
        if (!Near(p.x, pose.Position[0]) || !Near(p.y, pose.Position[1]) || !Near(p.z, pose.Position[2]) ||
            !Near(s.x, pose.Scale[0]) || !Near(s.y, pose.Scale[1]) || !Near(s.z, pose.Scale[2])) return false;
        double dot = (double)r.x * pose.Rotation[0] + (double)r.y * pose.Rotation[1] +
            (double)r.z * pose.Rotation[2] + (double)r.w * pose.Rotation[3];
        return Finite(dot) && Math.Abs(dot) > .999999 &&
            (body!.isKinematic || body.linearVelocity.sqrMagnitude <= .0001f && body.angularVelocity.sqrMagnitude <= .0001f);
    }
    private static bool SameClock(CreatureIdleAnimation? previous, CreatureIdleAnimation observed, double now)
    {
        if (previous == null || previous.Clip != observed.Clip || Math.Abs(previous.Speed - observed.Speed) > .00001f) return false;
        double difference = Math.Abs(CreatureIdleAnimationRules.SampleTime(previous, now) - observed.ClipTime);
        difference = Math.Min(difference, CreatureIdleAnimationRules.FrogIdleLength - difference);
        // Small native frame timing jitter must not create a new checkpoint every
        // safety poll; a pause, culling reset or a changed clock still wakes it.
        return difference <= .075;
    }
    private static bool Near(float a, float b) => Finite(a) && Finite(b) && Math.Abs(a - b) <= .0001f;
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static AccessTools.FieldRef<FrogTongue, T>? Resolve<T>(string name)
    { try { return AccessTools.FieldRefAccess<FrogTongue, T>(name); } catch { return null; } }
}
