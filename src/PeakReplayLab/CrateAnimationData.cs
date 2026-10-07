using System;

namespace PeakReplayLab;

// A native clip sampled on a render-only chest replica. This is not a gameplay event:
// no open RPC, random loot generation or interaction handler is replayed.
public sealed class CrateAnimationFrame
{
    public string Clip { get; set; } = "";
    public float Time { get; set; }
    // Schema 4: one event anchor advances a native clip without new per-tick chest poses.
    // False is the legacy schema-3 per-frame cursor representation.
    public bool Anchored { get; set; }
    public double AnchorTime { get; set; }
    public float Rate { get; set; } = 1;
    public float Duration { get; set; }
    public bool Loop { get; set; }
}

public static class CrateAnimationTimeline
{
    public static bool Valid(CrateAnimationFrame value) => !string.IsNullOrWhiteSpace(value.Clip) && value.Clip.Length <= 1024 &&
        Finite(value.Time) && value.Time >= 0 && value.Time <= 86400 && (!value.Anchored ||
        Finite(value.AnchorTime) && Math.Abs(value.AnchorTime) <= 604800 && Finite(value.Rate) && Math.Abs(value.Rate) <= 32 &&
        Finite(value.Duration) && value.Duration > 0 && value.Duration <= 86400 && value.Time <= value.Duration + .001f);

    public static double Sample(CrateAnimationFrame value, double replayTime, double assetDuration, bool assetLoops,
        CrateAnimationFrame? next = null, float mix = 0)
    {
        double duration = value.Anchored ? value.Duration : assetDuration;
        bool loop = value.Anchored ? value.Loop : assetLoops;
        if (!Finite(duration) || duration <= 0 || !Finite(replayTime)) return 0;
        double cursor = value.Time;
        if (value.Anchored) cursor += Math.Max(0, replayTime - value.AnchorTime) * value.Rate;
        else if (next != null && !next.Anchored && next.Clip == value.Clip)
        {
            double end = next.Time;
            if (loop && end < cursor && cursor - end > duration * .5) end += duration;
            cursor += (end - cursor) * Math.Max(0, Math.Min(1, mix));
        }
        if (!loop) return Math.Max(0, Math.Min(duration, cursor));
        return ((cursor % duration) + duration) % duration;
    }

    // Snapshot cropping must rebase the event too. A negative anchor is intentional: the
    // opening began before this 120-second window and must already be partly/fully open.
    public static CrateFrame Rebase(CrateFrame source, double origin)
    {
        var animation = source.Animation;
        if (animation == null || !animation.Anchored || origin == 0) return source;
        return new CrateFrame { Key = source.Key, Kind = source.Kind, Open = source.Open, Pose = source.Pose,
            Animation = new CrateAnimationFrame { Clip = animation.Clip, Time = animation.Time, Anchored = true,
                AnchorTime = animation.AnchorTime - origin, Rate = animation.Rate, Duration = animation.Duration, Loop = animation.Loop } };
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
