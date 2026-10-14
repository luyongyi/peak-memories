using System;
using System.Collections.Generic;
using System.IO;

namespace PeakReplayLab;

// Constructed with the existing page/joint index on the decoder worker. A page
// baseline is sufficient for dormant creatures; active brackets may request the
// existing bounded next-page window, never the creature's birth history.
internal sealed class CreatureObservationTimeline
{
    // 5 Hz has a .2 s nominal interval; allow one 30 Hz scene tick of jitter
    // while keeping the existing half-second bounded page look-ahead contract.
    internal const double MaximumInterpolationGap = .3;
    private readonly struct Observation
    {
        internal readonly CreatureReplayFrame Frame;
        internal readonly bool InterpolateFromPrevious;
        internal Observation(CreatureReplayFrame frame, bool interpolateFromPrevious)
        { Frame = frame; InterpolateFromPrevious = interpolateFromPrevious; }
        internal double Time => Frame.SampleTime;
    }
    private readonly Dictionary<string, List<Observation>> observations;
    private readonly double coveredUntil;
    private readonly bool knownEnd;
    internal long EstimatedIndexBytes { get; }
    private CreatureObservationTimeline(Dictionary<string, List<Observation>> observations, double coveredUntil, bool knownEnd)
    {
        this.observations = observations; this.coveredUntil = coveredUntil; this.knownEnd = knownEnd;
        long bytes = 256;
        foreach (var pair in observations) bytes += 160 + pair.Key.Length * 2L + pair.Value.Capacity * 16L;
        EstimatedIndexBytes = bytes;
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
            if (!ReplayRules.Finite(frame.T) || frame.T < lastTime) throw new InvalidDataException("Creature frame clock moved backwards.");
            bool worldCut = previousWorld != null &&
                (frame.T - lastTime > .5 || SceneObservationTimeline.Builder.WorldCut(previousWorld, frame.World));
            lastTime = frame.T; previousWorld = frame.World;
            currentKeys.Clear();
            foreach (var creature in frame.Creatures)
            {
                if (!creature.SampleTimeKnown) continue;
                currentKeys.Add(creature.Key);
                if (!ReplayRules.Finite(creature.SampleTime) || creature.SampleTime > frame.T + .000001)
                    throw new InvalidDataException("Creature observation is in the future.");
                if (!observations.TryGetValue(creature.Key, out var values)) observations.Add(creature.Key, values = new());
                bool blend = false;
                if (values.Count > 0)
                {
                    var previous = values[values.Count - 1];
                    if (creature.SampleTime < previous.Time) throw new InvalidDataException("Creature observation clock moved backwards.");
                    bool cut = worldCut || !previousKeys.Contains(creature.Key) || pendingCuts.Contains(creature.Key);
                    if (creature.SampleTime == previous.Time)
                    {
                        if (cut) pendingCuts.Add(creature.Key);
                        continue;
                    }
                    double gap = creature.SampleTime - previous.Time;
                    blend = !cut && gap <= MaximumInterpolationGap &&
                        CreatureReplayRules.CanInterpolateObservation(previous.Frame, creature);
                    pendingCuts.Remove(creature.Key);
                }
                values.Add(new Observation(creature, blend));
            }
            var oldKeys = previousKeys; previousKeys = currentKeys; currentKeys = oldKeys;
        }
        internal CreatureObservationTimeline Build(bool knownEnd) => new(observations, lastTime, knownEnd);
    }
    private static int Before(List<Observation> values, double time)
    {
        int lo = 0, hi = values.Count - 1;
        while (lo < hi) { int middle = (lo + hi + 1) / 2; if (values[middle].Time <= time) lo = middle; else hi = middle - 1; }
        return lo;
    }
    internal bool CanSample(CreatureReplayFrame[] visible, double time)
    {
        if (knownEnd || coveredUntil >= time + MaximumInterpolationGap) return true;
        foreach (var creature in visible)
        {
            if (!creature.SampleTimeKnown || !creature.Pose.Active || creature.ClockMode == CreatureReplayRules.SleepingClock ||
                creature.ClockMode == CreatureReplayRules.IdleClock ||
                !observations.TryGetValue(creature.Key, out var values) || values.Count == 0) continue;
            int index = Before(values, time);
            if (index + 1 < values.Count || values[index].Time <= time - MaximumInterpolationGap) continue;
            return false;
        }
        return true;
    }
    internal bool TrySample(string key, double time, out CreatureReplayFrame left, out CreatureReplayFrame right, out float mix)
    {
        left = right = null!; mix = 0;
        if (!observations.TryGetValue(key, out var values) || values.Count == 0 || values[0].Time > time) return false;
        int index = Before(values, time);
        left = right = values[index].Frame;
        if (left.ClockMode != CreatureReplayRules.ActiveClock || index + 1 >= values.Count || !values[index + 1].InterpolateFromPrevious) return true;
        var after = values[index + 1]; double gap = after.Time - left.SampleTime;
        right = after.Frame; mix = (float)Math.Max(0, Math.Min(1, (time - left.SampleTime) / gap));
        return true;
    }
}
