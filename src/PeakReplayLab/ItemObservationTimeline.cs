using System;
using System.Collections.Generic;
using System.IO;

namespace PeakReplayLab;

// Independent low-rate item observations, indexed once on the decoder worker.
// A dormant page baseline is sufficient; active movement needs only the same
// bounded successor window already used by joints, ropes and creatures.
internal sealed class ItemObservationTimeline
{
    internal const double MaximumInterpolationGap = .3;
    private readonly struct Observation
    {
        internal readonly ItemFrame Frame;
        internal readonly bool InterpolateFromPrevious;
        internal Observation(ItemFrame frame, bool interpolate)
        { Frame = frame; InterpolateFromPrevious = interpolate; }
        internal double Time => Frame.SampleTime;
    }
    private readonly Dictionary<string, List<Observation>> observations;
    private readonly double coveredUntil;
    private readonly bool knownEnd;
    internal long EstimatedIndexBytes { get; }
    private ItemObservationTimeline(Dictionary<string, List<Observation>> observations, double coveredUntil, bool knownEnd)
    {
        this.observations = observations; this.coveredUntil = coveredUntil; this.knownEnd = knownEnd;
        long bytes = 128;
        foreach (var pair in observations) bytes += 160 + pair.Key.Length * 2L + pair.Value.Capacity * 16L;
        EstimatedIndexBytes = observations.Count == 0 ? 0 : bytes;
    }
    internal sealed class Builder
    {
        private readonly Dictionary<string, List<Observation>> observations = new(StringComparer.Ordinal);
        private HashSet<string> previousKeys = new(StringComparer.Ordinal), currentKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> pendingCuts = new(StringComparer.Ordinal);
        private double lastTime = double.NegativeInfinity;
        private WorldFrame? previousWorld;
        internal void Observe(ReplayFrame frame)
        {
            if (!ReplayRules.Finite(frame.T) || frame.T < lastTime) throw new InvalidDataException("Item frame clock moved backwards.");
            bool worldCut = previousWorld != null &&
                (frame.T - lastTime > .5 || SceneObservationTimeline.Builder.WorldCut(previousWorld, frame.World));
            lastTime = frame.T; previousWorld = frame.World; currentKeys.Clear();
            foreach (var item in frame.Items)
            {
                if (!item.SampleTimeKnown) continue;
                currentKeys.Add(item.Key);
                if (!ReplayRules.Finite(item.SampleTime) || item.SampleTime > frame.T + .000001)
                    throw new InvalidDataException("Item observation is in the future.");
                if (!observations.TryGetValue(item.Key, out var values)) observations.Add(item.Key, values = new());
                bool blend = false;
                if (values.Count > 0)
                {
                    var previous = values[values.Count - 1];
                    if (item.SampleTime < previous.Time) throw new InvalidDataException("Item observation clock moved backwards.");
                    bool cut = worldCut || !previousKeys.Contains(item.Key) || pendingCuts.Contains(item.Key);
                    if (item.SampleTime == previous.Time)
                    {
                        if (cut) pendingCuts.Add(item.Key);
                        continue;
                    }
                    blend = !cut && item.SampleTime - previous.Time <= MaximumInterpolationGap &&
                        CanInterpolateObservation(previous.Frame, item);
                    pendingCuts.Remove(item.Key);
                }
                values.Add(new Observation(item, blend));
            }
            var oldKeys = previousKeys; previousKeys = currentKeys; currentKeys = oldKeys;
        }
        internal ItemObservationTimeline Build(bool knownEnd) => new(observations, lastTime, knownEnd);
    }
    private static int Before(List<Observation> values, double time)
    {
        int lo = 0, hi = values.Count - 1;
        while (lo < hi) { int middle = (lo + hi + 1) / 2; if (values[middle].Time <= time) lo = middle; else hi = middle - 1; }
        return lo;
    }
    internal bool CanSample(ItemFrame[] visible, double time)
    {
        if (knownEnd || coveredUntil >= time + MaximumInterpolationGap) return true;
        foreach (var item in visible)
        {
            if (!item.SampleTimeKnown || !item.Pose.Active || item.ClockMode != CreatureReplayRules.ActiveClock ||
                !observations.TryGetValue(item.Key, out var values) || values.Count == 0) continue;
            int index = Before(values, time);
            if (index + 1 < values.Count || values[index].Time <= time - MaximumInterpolationGap) continue;
            return false;
        }
        return true;
    }
    internal bool TrySample(string key, double time, out ItemFrame left, out ItemFrame right, out float mix)
    {
        left = right = null!; mix = 0;
        if (!ReplayRules.Finite(time) || !observations.TryGetValue(key, out var values) || values.Count == 0 || values[0].Time > time) return false;
        int index = Before(values, time); left = right = values[index].Frame;
        if (left.ClockMode != CreatureReplayRules.ActiveClock || index + 1 >= values.Count || !values[index + 1].InterpolateFromPrevious) return true;
        var after = values[index + 1]; right = after.Frame;
        mix = (float)Math.Max(0, Math.Min(1, (time - left.SampleTime) / (after.Time - left.SampleTime)));
        return true;
    }
    internal static bool CanInterpolateObservation(ItemFrame a, ItemFrame b)
    {
        // The final observed position belongs to the preceding movement too.
        // Blend into a compatible terminal checkpoint; later dormant time still
        // holds because TrySample requires its left endpoint to be active.
        if (b.SampleDiscontinuity || a.Key != b.Key || a.ItemId != b.ItemId || a.Prefab != b.Prefab || a.HolderId != b.HolderId || a.State != b.State ||
            a.ClockMode != CreatureReplayRules.ActiveClock ||
            (b.ClockMode != CreatureReplayRules.ActiveClock && b.ClockMode != CreatureReplayRules.SleepingClock && b.ClockMode != CreatureReplayRules.IdleClock) ||
            !SameNativeClock(a.IdleAnimation, b.IdleAnimation) || !a.Pose.Active || !b.Pose.Active ||
            a.Primary != b.Primary || a.Secondary != b.Secondary || a.Used != b.Used || a.Lit != b.Lit ||
            a.RevealBackpackContents != b.RevealBackpackContents || a.Pose.Nodes.Length != b.Pose.Nodes.Length) return false;
        double distance = 0;
        for (int i = 0; i < 3; i++) { double delta = a.Pose.Position[i] - b.Pose.Position[i]; distance += delta * delta; }
        if (distance > 400) return false;
        for (int i = 0; i < a.Pose.Nodes.Length; i++)
        {
            var left = a.Pose.Nodes[i]; var right = b.Pose.Nodes[i];
            if (left.Path != right.Path || left.Active != right.Active || left.Visible != right.Visible) return false;
        }
        if ((a.Visuals == null) != (b.Visuals == null) || a.Visuals != null && b.Visuals != null && a.Visuals.Length != b.Visuals.Length) return false;
        if (a.Visuals != null && b.Visuals != null) for (int i = 0; i < a.Visuals.Length; i++)
            if (a.Visuals[i].Path != b.Visuals[i].Path || a.Visuals[i].Index != b.Visuals[i].Index ||
                a.Visuals[i].Mesh != b.Visuals[i].Mesh || a.Visuals[i].Enabled != b.Visuals[i].Enabled) return false;
        return true;
    }
    private static bool SameNativeClock(CreatureIdleAnimation? a, CreatureIdleAnimation? b) =>
        a == null && b == null || a != null && b != null && a.Clip == b.Clip && a.Speed == b.Speed &&
        a.SpinSpeed == b.SpinSpeed && a.RopePhase == b.RopePhase;
}
