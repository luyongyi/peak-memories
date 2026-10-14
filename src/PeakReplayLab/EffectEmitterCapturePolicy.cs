using System;

namespace PeakReplayLab;

// Events own immutable native module baselines. Clock progression and emitter
// motion are not reasons to reread size/colour/emission curves on a static VFX.
internal sealed class EffectEmitterCapturePolicy
{
    internal const bool RecordsNativeParticles = false;
    private bool dirty = true;
    internal bool EventPending => dirty;
    private double animateUntil = double.NegativeInfinity;
    private readonly EffectSlowProbeClock culling = new(), unknownAnimation = new();
    internal void Invalidate(double now = 0, double animationSeconds = 0)
    { dirty = true; if (animationSeconds > 0) animateUntil = Math.Max(animateUntil, now + animationSeconds); }
    internal bool ProbeCulling(double now) => culling.Take(now);
    internal bool Due(double now, bool known, bool continuousNativeAnimation, bool changedDistanceCulling,
        bool unknownNativeAnimation = false) =>
        !known || dirty || now < animateUntil || continuousNativeAnimation || changedDistanceCulling ||
        unknownNativeAnimation && unknownAnimation.Take(now);
    internal void Complete(double now) { dirty = false; }
}

// Probe the culler's managed attenuation at 4 Hz before touching native modules.
// Keep the original phase on jittered scene ticks and never catch up missed work.
internal sealed class EffectSlowProbeClock
{
    private double next = double.NegativeInfinity, last = double.NegativeInfinity;
    internal bool Take(double now)
    {
        if (now <= last + 1e-8 && now >= last) return false;
        if (now < last) next = double.NegativeInfinity;
        if (now + 1e-8 < next) return false;
        next = double.IsNegativeInfinity(next) ? now + .25 : next + (Math.Floor((now + 1e-8 - next) / .25) + 1) * .25;
        last = now; return true;
    }
}

// An enabled controller is only a capability. Empty Motion states, paused
// clocks and completed one-shots release module work. A stop retains one final
// observation, while unknown native queries use the separate 4 Hz safety path.
internal sealed class EffectNativeModuleClock
{
    private bool known, wasEvaluating, wasActive, wasTransitioning;
    private int previousState;
    private float previousPosition, previousRate;
    internal bool Observe(bool enabled, bool hasClips, int state, float normalizedTime, bool looping, bool transitioning, float rate)
    {
        bool evaluates = enabled && (hasClips || transitioning) && !float.IsNaN(normalizedTime) &&
            !float.IsInfinity(normalizedTime) && !float.IsNaN(rate) && !float.IsInfinity(rate);
        float position = evaluates ? looping ? normalizedTime : Math.Max(0, Math.Min(1, normalizedTime)) : 0;
        bool changed = known && (evaluates != wasEvaluating || state != previousState ||
            rate != previousRate || transitioning != wasTransitioning);
        bool active = evaluates && rate != 0 && (transitioning ||
            (looping || rate > 0 && position < 1 || rate < 0 && position > 0) &&
            (!known || changed || position != previousPosition));
        bool read = active || wasActive || changed && evaluates;
        known = true; wasEvaluating = evaluates; wasActive = active; wasTransitioning = transitioning;
        previousState = state; previousPosition = position; previousRate = rate;
        return read;
    }
}

// This is a visual reconstruction window, not a claim that old particles or
// hidden RNG are bit-exact. It remains bounded even in hour-old looping smoke.
internal static class EffectNativeWarmupWindow
{
    internal const double MaximumPhase = 60;
    internal static double Start(double phase, double lifetime, double duration, double delay)
    {
        phase = double.IsNaN(phase) || double.IsInfinity(phase) || phase < 0 ? 0 : phase;
        double extent = Safe(lifetime) + Safe(duration) + Safe(delay);
        double window = Math.Min(MaximumPhase, Math.Max(.1, extent));
        return Math.Max(0, phase - window);
    }
    private static double Safe(double value) => double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? MaximumPhase : value;
    internal static float LoopTime(double phase, float relativeRate, float duration) =>
        duration > 0 && !float.IsNaN(duration) && !float.IsInfinity(duration) &&
            relativeRate >= 0 && !float.IsNaN(relativeRate) && !float.IsInfinity(relativeRate) &&
            !double.IsNaN(phase) && !double.IsInfinity(phase)
            ? (float)(Math.Max(0, phase * relativeRate) % duration) : 0;
}
