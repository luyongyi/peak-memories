using System;

namespace PeakReplayLab;

// Pure scheduling policy, independently tested without loading Unity or the game.
internal sealed class ItemSamplingPolicy
{
    private readonly double spread;
    private long revision = long.MinValue;
    private double nextProbe = double.NegativeInfinity;
    private double nextChildProbe = double.NegativeInfinity;
    private double hotUntil = double.NegativeInfinity;
    public ItemSamplingPolicy(int identity) => spread = Math.Abs(identity % 17) * .011;
    public bool ShouldRead(double now, long currentRevision, bool attachedOrAnimated,
        bool fixedPlacement = false, bool attachmentChanged = false) =>
        attachmentChanged || currentRevision != revision || now >= nextProbe ||
        !fixedPlacement && (attachedOrAnimated || now < hotUntil);
    public void Observed(double now, long currentRevision, bool moving, bool fixedPlacement = false)
    {
        revision = currentRevision;
        if (fixedPlacement) hotUntil = double.NegativeInfinity;
        else if (moving) hotUntil = now + .25;
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
