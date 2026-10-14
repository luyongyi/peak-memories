using System;
using System.Collections.Generic;

namespace PeakReplayLab;

// Ground movement and unmodelled visual motion use a cheap 5 Hz observation.
// Native semantic events bypass this deadline; repeated physics/wind packets
// are observations, not new events, and cannot renew a fast capture lease.
internal sealed class ItemSparseSamplingPolicy
{
    internal const double Interval = .2;
    private double next = double.NegativeInfinity;
    private double nextAnimation = double.NegativeInfinity;
    internal bool Due(double now, bool ground, bool initial, bool boundary) =>
        !ground || initial || boundary || now + 1e-8 >= next;
    internal void Observed(double now) => next = now + Interval;
    internal bool AnimationDue(double now, bool ground, bool force) =>
        !ground || force || now + 1e-8 >= nextAnimation;
    internal void AnimationObserved(double now) => nextAnimation = now + Interval;
}

// The capture loop visits only Active. Sleeping entries keep their immutable
// checkpoint and are visited separately, in bounded round-robin batches, for
// cached-distance checks / explicitly required fallback probes.
internal sealed class ItemCaptureWorkSet<T> where T : class
{
    private readonly List<T> active = new(), sleeping = new();
    private readonly Dictionary<T, int> activeIndices = new(), sleepingIndices = new();
    private int cursor;
    public IReadOnlyList<T> Active => active;
    public int SleepingCount => sleeping.Count;
    public bool IsActive(T value) => activeIndices.ContainsKey(value);
    public void Wake(T value)
    {
        Remove(sleeping, sleepingIndices, value);
        if (!activeIndices.ContainsKey(value)) { activeIndices.Add(value, active.Count); active.Add(value); }
    }
    public void Sleep(T value)
    {
        Remove(active, activeIndices, value);
        if (!sleepingIndices.ContainsKey(value)) { sleepingIndices.Add(value, sleeping.Count); sleeping.Add(value); }
    }
    public void Forget(T value)
    { Remove(active, activeIndices, value); Remove(sleeping, sleepingIndices, value); }
    public T? NextSleeping()
    {
        if (sleeping.Count == 0) return null;
        if (cursor >= sleeping.Count) cursor = 0;
        return sleeping[cursor++];
    }
    private static void Remove(List<T> values, Dictionary<T, int> indices, T value)
    {
        if (!indices.TryGetValue(value, out int index)) return;
        int last = values.Count - 1;
        if (index != last) { values[index] = values[last]; indices[values[index]] = index; }
        values.RemoveAt(last); indices.Remove(value);
    }
    public void Clear() { active.Clear(); sleeping.Clear(); activeIndices.Clear(); sleepingIndices.Clear(); cursor = 0; }
}

internal static class ItemSleepPolicy
{
    public static bool InitialStatic(bool hooksHealthy, bool knownStatic, bool kinematic, bool hasMoved, bool attached, bool continuous, bool motionPending = false) =>
        EventOnly(hooksHealthy, knownStatic, kinematic) && !hasMoved && !attached && !continuous && !motionPending;
    // Missing event hooks or an unclassified component can never authorize a
    // permanent sleep. Physics bodies also retain a low-frequency root safety
    // probe because a third-party script can apply force without a native event.
    public static bool EventOnly(bool hooksHealthy, bool knownStatic, bool kinematic) =>
        hooksHealthy && knownStatic && kinematic;
    public static bool CanSleep(bool inRange, bool settled, bool attached, bool continuous, bool motionPending) =>
        !inRange && settled && !attached && !continuous && !motionPending;
    public static int DistanceProbeBudget(int sleepingCount) => Math.Min(32, sleepingCount);
    public static double ProbeDelay(int identity) => .5 + Math.Abs(identity % 17) * .011;
}

// Pure scheduling policy, independently tested without loading Unity or the game.
internal sealed class ItemSamplingPolicy
{
    private readonly double spread;
    private long revision = long.MinValue;
    private double nextProbe = double.NegativeInfinity;
    private double nextChildProbe = double.NegativeInfinity;
    private double hotUntil = double.NegativeInfinity;
    private bool wasMoving;
    public ItemSamplingPolicy(int identity) => spread = Math.Abs(identity % 17) * .011;
    public bool ShouldRead(double now, long currentRevision, bool attachedOrAnimated,
        bool fixedPlacement = false, bool attachmentChanged = false) =>
        attachmentChanged || currentRevision != revision || now >= nextProbe ||
        !fixedPlacement && (attachedOrAnimated || now < hotUntil);
    public void Observed(double now, long currentRevision, bool moving, bool fixedPlacement = false)
    {
        revision = currentRevision;
        if (fixedPlacement) hotUntil = double.NegativeInfinity;
        // An observation may begin one short convergence window, but cannot
        // extend that same window forever. Real motion is a separate input to
        // ShouldRead; a changing value is not another semantic event.
        else if (moving && !wasMoving) hotUntil = now + .25;
        wasMoving = moving;
        nextProbe = now + .45 + spread;
    }
    public bool SampleChildren(double now, bool dirty, bool attachedOrMob, bool dynamicChildren)
    {
        // Ground objects can animate or extinguish without a lifecycle event. Probe
        // only those known to contain dynamic visuals, at a separate slow deadline;
        // root motion/sync must not turn this into a 60 Hz full-tree scan.
        bool sample = dirty || dynamicChildren && (attachedOrMob || now >= nextChildProbe);
        if (sample) nextChildProbe = now + .45 + spread;
        return sample;
    }
    public static bool NeedsInventory(double now, double next, long revision, long seen, int selected, int previousSelected) =>
        revision != seen || selected != previousSelected || now >= next;
}

// A known equipped backpack slot supplies the world placement in playback. Its
// recorded baseline must not change merely because the wearer moved. Visual
// child/visibility changes remain independently recordable and immutable.
internal static class BackpackCapturePolicy
{
    public static ObjectPose KeepPlacement(ObjectPose baseline, ObjectPose sampled)
    {
        if (baseline.Active == sampled.Active && ReferenceEquals(baseline.Nodes, sampled.Nodes)) return baseline;
        return new ObjectPose
        {
            Position = baseline.Position, Rotation = baseline.Rotation, Scale = baseline.Scale,
            Active = sampled.Active, Nodes = sampled.Nodes,
        };
    }
}

internal static class PoseVersionPolicy
{
    public static bool CanSkip(ObjectPose left, ObjectPose right, float mix, ObjectPose? previousLeft,
        ObjectPose? previousRight, float previousMix, bool invalidated, bool reveal, bool previousReveal) =>
        !invalidated && ReferenceEquals(left, previousLeft) && ReferenceEquals(right, previousRight) &&
        reveal == previousReveal && (mix == previousMix || ReferenceEquals(left, right));
}
