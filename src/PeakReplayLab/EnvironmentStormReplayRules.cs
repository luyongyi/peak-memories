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
