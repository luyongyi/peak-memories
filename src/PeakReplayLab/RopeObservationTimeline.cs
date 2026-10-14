using System;
using System.Collections.Generic;
using System.IO;

namespace PeakReplayLab;

// Built with the existing page index, off the game thread. Every page's complete
// baseline carries its observation time, so sleeping ropes never require a read
// from their birth. A bounded next page supplies the bracket for active ropes.
internal sealed class RopeObservationTimeline
{
    internal const double MaximumInterpolationGap = .15;
    internal readonly struct Observation
    {
        internal readonly RopeReplayFrame Frame;
        internal Observation(RopeReplayFrame frame) { Frame = frame; }
        internal double Time => Frame.SampleTime;
    }
    private readonly Dictionary<string, List<Observation>> observations;
    private readonly double coveredUntil;
    private readonly bool knownEnd;
    internal long EstimatedIndexBytes { get; }
    private RopeObservationTimeline(Dictionary<string, List<Observation>> observations, double coveredUntil, bool knownEnd)
    {
        this.observations = observations; this.coveredUntil = coveredUntil; this.knownEnd = knownEnd;
        long bytes = 256;
        foreach (var pair in observations) bytes += 160 + pair.Key.Length * 2L + pair.Value.Capacity * 8L;
        EstimatedIndexBytes = bytes;
    }
    internal sealed class Builder
    {
        private readonly Dictionary<string, List<Observation>> observations = new(StringComparer.Ordinal);
        private double lastTime = double.NegativeInfinity;
        internal void Observe(ReplayFrame frame)
        {
            lastTime = frame.T;
            foreach (var rope in frame.Ropes)
            {
                if (!rope.SampleTimeKnown) continue;
                if (rope.SampleTime > frame.T + .000001) throw new InvalidDataException("Rope observation is in the future.");
                if (!observations.TryGetValue(rope.Key, out var values)) observations.Add(rope.Key, values = new());
                if (values.Count > 0)
                {
                    var previous = values[values.Count - 1];
                    if (rope.SampleTime < previous.Time) throw new InvalidDataException("Rope observation clock moved backwards.");
                    if (rope.SampleTime == previous.Time) continue;
                }
                values.Add(new Observation(rope));
            }
        }
        internal RopeObservationTimeline Build(bool knownEnd) => new(observations, lastTime, knownEnd);
    }
    private static int Before(List<Observation> values, double time)
    {
        int lo = 0, hi = values.Count - 1;
        while (lo < hi) { int middle = (lo + hi + 1) / 2; if (values[middle].Time <= time) lo = middle; else hi = middle - 1; }
        return lo;
    }
    internal bool CanSample(RopeReplayFrame[] visible, double time)
    {
        if (knownEnd || coveredUntil >= time + MaximumInterpolationGap) return true;
        foreach (var rope in visible)
        {
            if (!rope.SampleTimeKnown || !observations.TryGetValue(rope.Key, out var values) || values.Count == 0) continue;
            int index = Before(values, time);
            if (index + 1 < values.Count || values[index].Time <= time - MaximumInterpolationGap) continue;
            return false;
        }
        return true;
    }
    internal bool TrySample(string key, double time, out RopeReplayFrame left, out RopeReplayFrame right, out float mix)
    {
        left = right = null!; mix = 0;
        if (!observations.TryGetValue(key, out var values) || values.Count == 0 || values[0].Time > time) return false;
        int index = Before(values, time);
        left = right = values[index].Frame;
        if (index + 1 >= values.Count) return true;
        var after = values[index + 1]; double gap = after.Time - left.SampleTime;
        // Long silence, birth/hide/topology changes are discrete boundaries.
        if (gap <= 0 || gap > MaximumInterpolationGap || !RopeReplayRules.CanInterpolate(left, after.Frame)) return true;
        right = after.Frame; mix = (float)Math.Max(0, Math.Min(1, (time - left.SampleTime) / gap));
        return true;
    }
}
