using System;

namespace PeakReplayLab;

// Remote models continue evaluating Animator, IK and ragdoll interpolation
// between movement packets. Observe that completed model at the native network
// rate, rather than freezing it when no new packet has arrived.
internal sealed class ActorNetworkSamplingPolicy
{
    private double origin, lastObserved = double.NaN, lastCaptured = double.NaN;
    private int rate;

    public bool ShouldCapture(double time, int nativeHz, bool boundary = false)
    {
        if (double.IsNaN(time) || double.IsInfinity(time) || time < 0)
            throw new ArgumentOutOfRangeException(nameof(time));
        // Invalid native configuration must not leave a teammate frozen. The
        // caller's recording clock still bounds this fallback to real frames.
        int effectiveRate = nativeHz > 0 ? Math.Min(nativeHz, 1000) : ActorJointSamplingPolicy.CoreHz;
        if (time == lastObserved) return false;
        bool reset = double.IsNaN(lastObserved) || time < lastObserved || rate != effectiveRate;
        lastObserved = time;
        if (reset)
        {
            rate = effectiveRate; origin = lastCaptured = time;
            return true;
        }
        if (!ActorJointSamplingPolicy.Due(rate, time, lastCaptured, origin, boundary)) return false;
        // One real LateUpdate snapshot consumes every elapsed slot. Neither a
        // stall nor a discrete wake can produce catch-up poses or a high-rate tail.
        lastCaptured = time;
        return true;
    }

    public void Reset()
    {
        origin = 0; rate = 0;
        lastObserved = lastCaptured = double.NaN;
    }
}
