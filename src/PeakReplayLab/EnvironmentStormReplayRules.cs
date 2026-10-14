using System;

namespace PeakReplayLab;

// Native StormVisual starts curve-driven emission above .1, retains it while
// the gust is active, and stops below .1 after the gust. Stopping emission does
// not remove already living particles. Rebuilds use the recorded phase clocks.
public static class EnvironmentStormReplayRules
{
    public const float MaximumRebuildSeconds = 8;
    public static bool ShouldEmit(bool useIntensity, bool windActive, float zoneIntensity, bool startedThisGust) =>
        !useIntensity ? windActive : windActive
            ? startedThisGust || zoneIntensity > .1f
            : startedThisGust && zoneIntensity >= .1f;

    public static (bool Clear, float Warmup, float Coast) Rebuild(bool emitting, bool startedThisGust, bool windActive,
        float factor, float intensity, float activeFor, float phaseDuration, float remaining, float maximumLifetime)
    {
        float horizon = Math.Min(MaximumRebuildSeconds, Math.Max(0, maximumLifetime));
        if (horizon == 0) return (true, 0, 0);
        if (emitting) return (false, windActive ? Math.Min(horizon, Math.Max(0, activeFor)) : horizon, 0);
        if (!startedThisGust || factor <= 0 && intensity <= 0) return (true, 0, 0);
        // ActiveFor resets to zero in WindChillZone.Update when the gust stops.
        // Its off-phase duration/countdown supplies the actual retiring age.
        float coast = windActive ? 0 : Math.Max(0, phaseDuration - remaining);
        if (coast >= horizon) return (true, 0, 0);
        float warmup = windActive ? Math.Min(horizon, Math.Max(0, activeFor)) : horizon - coast;
        return (false, warmup, coast);
    }
}

// A rebuild is a replaceable plan. Advancing it never performs Unity work;
// callers admit one operation through their shared presentation budget first.
public enum EnvironmentStormPresentationStep { None, Reset, Clear, Warm, Coast, Advance, Finish }

public sealed class EnvironmentStormPresentationCursor
{
    public const double MaximumSliceSeconds = 1.0 / 30;
    private bool reset, clear;
    private double warm, coast, advance;
    public bool Pending { get; private set; }
    public bool Emitting { get; private set; }
    public void Rebuild(bool clear, double warm, double coast, bool emitting)
    {
        this.clear = clear; reset = !clear; this.warm = Math.Max(0, warm); this.coast = Math.Max(0, coast);
        advance = 0; Emitting = emitting; Pending = true;
    }
    public void Advance(double seconds, bool emitting)
    {
        advance += Math.Max(0, seconds); Emitting = emitting;
        if (seconds > 0) Pending = true;
    }
    public void Cancel() { Pending = reset = clear = false; warm = coast = advance = 0; }
    public EnvironmentStormPresentationStep Take(out float seconds)
    {
        seconds = 0;
        if (!Pending) return EnvironmentStormPresentationStep.None;
        if (clear) { Cancel(); return EnvironmentStormPresentationStep.Clear; }
        if (reset) { reset = false; return EnvironmentStormPresentationStep.Reset; }
        if (warm > .0000001) { seconds = Slice(ref warm); return EnvironmentStormPresentationStep.Warm; }
        if (coast > .0000001) { seconds = Slice(ref coast); return EnvironmentStormPresentationStep.Coast; }
        if (advance > .0000001) { seconds = Slice(ref advance); return EnvironmentStormPresentationStep.Advance; }
        Pending = false; return EnvironmentStormPresentationStep.Finish;
    }
    private static float Slice(ref double remaining)
    {
        double amount = Math.Min(MaximumSliceSeconds, remaining); remaining = Math.Max(0, remaining - amount);
        return (float)amount;
    }
}
