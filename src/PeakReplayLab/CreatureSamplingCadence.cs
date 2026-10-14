using System;

namespace PeakReplayLab;

// Expensive native animation/geometry observation has a single 5 Hz deadline.
// Cached semantic fields still run on the scene clock so visibility, state and
// real action edges are never delayed until the next regular observation.
public sealed class CreatureSamplingCadence
{
    public const double Interval = 1d / 5;
    private bool observed, lastActive, pending;
    private int lastState, followUps;
    private double nextRegular = double.NegativeInfinity;
    private double detailUntil = double.NegativeInfinity;
    public bool LastWasBoundary { get; private set; }
    internal bool NeedsConfirmation => pending || followUps > 0;
    public long RegularReads { get; private set; }
    public long BoundaryReads { get; private set; }

    public void NotifyNativeEvent(double now = double.NaN, double detailSeconds = 0)
    {
        pending = true;
        // Native coroutines and Animator triggers can finish one scene tick
        // after their RPC. This bounded confirmation cannot renew itself from
        // changing bones, and it never creates a continuing high-rate lease.
        followUps = 2;
        ConfirmShortAction(now, detailSeconds);
    }

    public void ConfirmShortAction(double now, double seconds = .3)
    {
        if (!double.IsNaN(now) && !double.IsInfinity(now) && seconds > 0 && seconds <= 1)
            detailUntil = now + seconds;
    }

    public bool Due(double now, bool active, int state, bool semanticBoundary = false)
    {
        if (double.IsNaN(now) || double.IsInfinity(now)) return false;
        bool first = !observed;
        bool boundary = first || pending || semanticBoundary || active != lastActive || state != lastState;
        observed = true; lastActive = active; lastState = state;
        LastWasBoundary = boundary;
        bool confirm = !pending && followUps > 0;
        bool detail = now < detailUntil;
        if (!boundary && !confirm && !detail && now + 1e-8 < nextRegular) return false;
        if (confirm) followUps--;
        pending = false;
        if (first || now + 1e-8 >= nextRegular)
        {
            // Anchor the cadence to actual observations, with no catch-up loop
            // after a stalled frame and no work proportional to elapsed time.
            nextRegular = now + Interval;
            RegularReads++;
        }
        if (boundary || confirm) BoundaryReads++;
        return true;
    }
}
