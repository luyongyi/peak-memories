using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PeakReplayLab;

// Schema 13 preserves real observation timestamps, including stationary samples
// and full page baselines whose clock predates their opening frame. Legacy
// formats keep every original main-frame key instead of inventing low-rate time.
internal sealed class EnvironmentReplayTimeline
{
    internal readonly struct Key
    {
        public readonly double Time;
        public readonly EnvironmentReplayFrame? State;
        public readonly bool Cut;
        public Key(double time, EnvironmentReplayFrame? state, bool cut) { Time = time; State = state; Cut = cut; }
    }

    internal sealed class Builder
    {
        private readonly List<Key> keys = new();
        private double previousTime = double.NegativeInfinity;
        private int previousSegment;
        private EnvironmentReplayFrame? previous;

        public void Observe(ReplayFrame frame)
        {
            if (!ReplayRules.Finite(frame.T) || frame.T < previousTime) throw new InvalidDataException("环境时间轴没有排序。");
            if (frame.T == previousTime) return;
            bool cut = keys.Count == 0 || frame.T - previousTime > .5 || previousSegment != frame.World.Segment ||
                !EnvironmentSnapshotEquality.SameEvent(previous, frame.World.Environment);
            var state = frame.World.Environment;
            bool stamped = state != null && state.SampleTimeKnown;
            double time = stamped ? state!.SampleTime : frame.T;
            if (!ReplayRules.Finite(time) || time > frame.T + .000001) throw new InvalidDataException("环境采样时间无效。");
            if (time > frame.T) time = frame.T; // Accepted floating-point tolerance must not delay an exact event.
            // The opening baseline may predate this window. A real cut cannot
            // carry a prior interval's observation clock into a new segment.
            if (cut && keys.Count != 0) time = Math.Max(time, frame.T);
            bool sameObservation = stamped && previous != null && previous.SampleTimeKnown && previous.SampleTime == state!.SampleTime;
            if (!cut && sameObservation && !EnvironmentSnapshotEquality.Same(previous, state))
                throw new InvalidDataException("同一环境采样时间包含不同状态。");
            if (cut || !stamped || !sameObservation)
            {
                if (keys.Count != 0 && time <= keys[keys.Count - 1].Time) throw new InvalidDataException("环境采样时间没有递增。");
                keys.Add(new Key(time, state, cut));
            }
            previous = state; previousTime = frame.T; previousSegment = frame.World.Segment;
        }

        public EnvironmentReplayTimeline Build(bool knownEnd) => new(keys.ToArray(), previousTime, knownEnd);
    }

    private readonly Key[] keys;
    private readonly double observedEnd;
    private readonly bool knownEnd;
    public int KeyCount => keys.Length;
    private EnvironmentReplayTimeline(Key[] keys, double observedEnd, bool knownEnd)
    { this.keys = keys; this.observedEnd = observedEnd; this.knownEnd = knownEnd; }

    public EnvironmentReplayTimeline(IEnumerable<ReplayFrame> frames, bool knownEnd, CancellationToken cancellation = default)
    {
        var builder = new Builder();
        foreach (var frame in frames) { cancellation.ThrowIfCancellationRequested(); builder.Observe(frame); }
        var built = builder.Build(knownEnd); keys = built.keys; observedEnd = built.observedEnd; this.knownEnd = knownEnd;
    }

    public bool TrySample(double time, out EnvironmentReplayBracket sample)
    {
        sample = default;
        if (!ReplayRules.Finite(time) || keys.Length == 0 || time < keys[0].Time) return false;
        int lo = 0, hi = keys.Length - 1;
        while (lo < hi)
        {
            int middle = (lo + hi + 1) / 2;
            if (keys[middle].Time <= time) lo = middle; else hi = middle - 1;
        }
        var left = keys[lo];
        if (lo == keys.Length - 1)
        {
            // A future real observation is required even for a stationary
            // sample: the next interval might contain the start of motion.
            // Unknown legacy states and true recording endpoints may hold.
            if (!knownEnd && time > left.Time + .000001 && left.State != null && left.State.SampleTimeKnown) return false;
            if (!knownEnd && time > observedEnd + .000001) return false;
            sample = new EnvironmentReplayBracket(left.State, left.State, left.Time, left.Time, 0); return true;
        }
        var right = keys[lo + 1];
        double gap = right.Time - left.Time;
        bool blend = !right.Cut && left.State != null && right.State != null && gap > 0 && gap <= .5;
        float mix = blend ? (float)Math.Max(0, Math.Min(1, (time - left.Time) / gap)) : 0;
        sample = new EnvironmentReplayBracket(left.State, blend ? right.State : left.State, left.Time, blend ? right.Time : left.Time, mix);
        return true;
    }

}

internal readonly struct EnvironmentReplayBracket
{
    public readonly EnvironmentReplayFrame? Left, Right;
    public readonly double LeftTime, RightTime;
    public readonly float Mix;
    public EnvironmentReplayBracket(EnvironmentReplayFrame? left, EnvironmentReplayFrame? right, double leftTime, double rightTime, float mix)
    { Left = left; Right = right; LeftTime = leftTime; RightTime = rightTime; Mix = mix; }
}

internal static class EnvironmentSnapshotEquality
{
    public static bool SameEvent(EnvironmentReplayFrame? a, EnvironmentReplayFrame? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.SampleTimeKnown != b.SampleTimeKnown || a.Winds.Length != b.Winds.Length || a.Lava.Length != b.Lava.Length) return false;
        for (int i = 0; i < a.Winds.Length; i++)
        {
            var x = a.Winds[i]; var y = b.Winds[i];
            if (!EnvironmentReplayMath.SameWindEvent(x, y) || x.Duration != y.Duration || x.Storms.Length != y.Storms.Length) return false;
            for (int j = 0; j < x.Storms.Length; j++) if (x.Storms[j].Key != y.Storms[j].Key || x.Storms[j].Kind != y.Storms[j].Kind) return false;
        }
        for (int i = 0; i < a.Lava.Length; i++)
            if (!EnvironmentReplayMath.SameLavaEvent(a.Lava[i], b.Lava[i]) || a.Lava[i].FogPlaneActive != b.Lava[i].FogPlaneActive) return false;
        var fog = a.Fog; var next = b.Fog;
        return ReferenceEquals(fog, next) || fog != null && next != null && fog.Key == next.Key && fog.Active == next.Active &&
            fog.Origin == next.Origin && fog.Moving == next.Moving && fog.Arrived == next.Arrived && fog.Enable == next.Enable;
    }

    public static bool Same(EnvironmentReplayFrame? a, EnvironmentReplayFrame? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.SampleTimeKnown != b.SampleTimeKnown || a.SampleTime != b.SampleTime || a.WeatherBlend != b.WeatherBlend || a.GlobalWind != b.GlobalWind || a.RainFactor != b.RainFactor ||
            a.SnowFactor != b.SnowFactor || a.HeightFogAmount != b.HeightFogAmount || a.HeightFogSpirit != b.HeightFogSpirit ||
            a.Winds.Length != b.Winds.Length || a.Lava.Length != b.Lava.Length || !Same(a.Fog, b.Fog)) return false;
        if (!ReferenceEquals(a.Winds, b.Winds))
            for (int i = 0; i < a.Winds.Length; i++)
            {
                var x = a.Winds[i]; var y = b.Winds[i];
                if (ReferenceEquals(x, y)) continue;
                if (x.Key != y.Key || x.Enabled != y.Enabled || x.Active != y.Active || !Same(x.Direction, y.Direction) ||
                    x.Intensity != y.Intensity || x.StormProgress != y.StormProgress || x.TimeUntilStorm != y.TimeUntilStorm ||
                    x.SecondsUntilSwitch != y.SecondsUntilSwitch || x.Duration != y.Duration || x.ActiveFor != y.ActiveFor || x.Storms.Length != y.Storms.Length) return false;
                if (!ReferenceEquals(x.Storms, y.Storms))
                    for (int j = 0; j < x.Storms.Length; j++)
                    {
                        var u = x.Storms[j]; var v = y.Storms[j];
                        if (!ReferenceEquals(u, v) && (u.Key != v.Key || u.Kind != v.Kind || u.Factor != v.Factor || u.Intensity != v.Intensity)) return false;
                    }
            }
        if (!ReferenceEquals(a.Lava, b.Lava))
            for (int i = 0; i < a.Lava.Length; i++)
            {
                var x = a.Lava[i]; var y = b.Lava[i];
                if (!ReferenceEquals(x, y) && (x.Key != y.Key || x.Kind != y.Kind || x.Active != y.Active || !Same(x.Position, y.Position) ||
                    x.Started != y.Started || x.Ended != y.Ended || x.ProgressTime != y.ProgressTime || x.FogPlaneActive != y.FogPlaneActive)) return false;
            }
        return true;
    }

    private static bool Same(float[] a, float[] b) => ReferenceEquals(a, b) || a.Length == b.Length && a[0] == b[0] && a[1] == b[1] && a[2] == b[2];
    private static bool Same(EnvironmentFogFrame? a, EnvironmentFogFrame? b) => ReferenceEquals(a, b) || a != null && b != null &&
        a.Key == b.Key && Same(a.Point, b.Point) && a.Size == b.Size && a.Padding == b.Padding && a.Enable == b.Enable && a.Reveal == b.Reveal &&
        a.CloseFog == b.CloseFog && a.Active == b.Active && a.Origin == b.Origin && a.Moving == b.Moving && a.Arrived == b.Arrived;
}
