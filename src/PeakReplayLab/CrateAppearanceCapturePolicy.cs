using System;

namespace PeakReplayLab;

// Native chest scripts change appearance at interactions/activation. Only that
// event or an advancing recorded animation warrants fast material/light reads.
// Audited stationary roots retain one immutable baseline and wake from exact
// lifecycle/native events. Unproven scripts keep a staggered integrity fallback.
internal sealed class CrateAppearanceCapturePolicy
{
    private readonly double spread;
    private double nextFull = double.NegativeInfinity, nextAnimated = double.NegativeInfinity, lastAnimated = double.NegativeInfinity;
    private bool wasObserving;
    public CrateAppearanceCapturePolicy(int identity) => spread = Math.Abs(identity % 67) / 67d * .25;
    public double ProbeInterval => .5 + spread;
    public bool Due(double now, bool hasBaseline, bool dirty, bool active, bool eventWindow, CrateAnimationFrame? animation,
        bool followSceneClock = false, bool eventOnly = false)
    {
        if (!hasBaseline || dirty) return true;
        if (!active) return false;
        bool observing = eventWindow || Advancing(animation, now);
        return (!eventOnly && now >= nextFull) || wasObserving && !observing || observing &&
            (followSceneClock ? now > lastAnimated : now + 1e-8 >= nextAnimated);
    }
    public void Observed(double now, bool observing = false)
    {
        // Keep the original phase when an integrity poll is a frame late.
        // Repeatedly adding a rounded per-object interval makes initially
        // staggered objects resonate on common capture frames after a while.
        if (double.IsNegativeInfinity(nextFull) || now < nextFull) nextFull = now + ProbeInterval;
        else nextFull += (Math.Floor((now - nextFull) / .5) + 1) * .5;
        lastAnimated = now; nextAnimated = now + 1d / 30; wasObserving = observing;
    }
    internal static bool Advancing(CrateAnimationFrame? value, double now)
    {
        if (value == null || !value.Anchored || value.Rate == 0 || now < value.AnchorTime) return false;
        if (value.Loop) return true;
        double end = value.AnchorTime + (value.Rate > 0 ? value.Duration - value.Time : value.Time) / Math.Abs(value.Rate);
        // Include the settling edge so the final curve value becomes a checkpoint.
        return now <= end + 1d / 30;
    }
}
