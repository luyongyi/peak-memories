using System;

namespace PeakReplayLab;

// Notifications describe actual native operations. Observed shape changes only
// affect settling; they must never manufacture another high-rate event window.
internal enum RopeCaptureEvent
{
    Baseline, Visibility, Attachment, Topology, Climbing, BridgeShake, BridgeFall,
    AnchorFlight, HookStart, HookEnd, Motion,
}

internal sealed class RopeCaptureLifecycle
{
    internal const double QuietSeconds = 1;
    internal const double BurstSeconds = .25;
    internal const double PostActionSeconds = 2;
    internal const double FastTailSeconds = 2;
    private readonly bool passiveBaseline;
    private bool pending = true, sampled, lastAction;
    private double burstUntil = double.NegativeInfinity, actionUntil = double.NegativeInfinity;
    private double postActionUntil = double.NegativeInfinity;
    private double tailStarted = double.NegativeInfinity, quietSince = double.NaN;
    private double nextRead = double.NegativeInfinity, failureUntil = double.NegativeInfinity;
    public bool Sleeping { get; private set; }
    public double NextReadAt => nextRead;
    public double BurstUntil => burstUntil;
    public double ActionUntil => actionUntil;
    public double PostActionUntil => postActionUntil;
    public double TailStartedAt => tailStarted;
    public int LastRateHz { get; private set; }

    public RopeCaptureLifecycle(bool passiveBaseline = false) => this.passiveBaseline = passiveBaseline;

    public void Notify(double now, RopeCaptureEvent kind, double duration = 0)
    {
        if (!Finite(now)) return;
        if (kind == RopeCaptureEvent.Motion)
        {
            // A stream of remote poses cannot defeat the settling cadence. Only
            // a sleeping checkpoint needs an immediate first observation.
            if (Sleeping)
            { Sleeping = false; pending = true; tailStarted = now; quietSince = double.NaN; }
            return;
        }
        Sleeping = false; pending = true; failureUntil = double.NegativeInfinity;
        quietSince = double.NaN; tailStarted = now;
        burstUntil = now + BurstSeconds;
        if (Finite(duration) && duration > 0) actionUntil = Math.Max(actionUntil, now + duration);
    }

    public bool ShouldRead(double now, bool semanticAction, bool inRange, int actionHz = 60, bool sceneTick = false)
    {
        if (!Finite(now)) return false;
        bool action = semanticAction || now < actionUntil;
        if (action)
        {
            if (!lastAction) pending = true;
            Sleeping = false; quietSince = double.NaN;
        }
        else if (lastAction)
        {
            // Observe the first post-action pose, including a hidden line or a
            // collapsed mesh, before considering the immutable final state idle.
            pending = true; tailStarted = now; burstUntil = now + BurstSeconds;
            // Deployment has ended, but its first physical recoil can still be
            // large. Only this genuine action edge owns the finite protection;
            // ordinary shape changes and event notifications cannot renew it.
            postActionUntil = now + PostActionSeconds;
        }
        lastAction = action;
        if (now < failureUntil) return false;
        if (Sleeping) return false;
        if (!sampled || pending) return true;
        // Production callers enter only on the shared <=30 Hz scene clock.
        // Do not quantize a 30 Hz action a second time under rendered-frame
        // jitter. Residual lower-frequency checks retain their own deadlines.
        if (sceneTick && actionHz == 30 && (action || now < burstUntil || now < postActionUntil || LastRateHz >= 30)) return true;
        // The recorder already caps its global clock at 60 Hz. An additional
        // now + 1/60 deadline drops genuine action frames when that clock has
        // ordinary jitter (e.g. successive intervals of 15 ms and 18 ms).
        // Follow every admitted capture tick for full-rate native actions and
        // bursts; slower scene-vine actions and residual settling retain clocks.
        if (actionHz >= 60 && (action || now < burstUntil || now < postActionUntil)) return true;
        // Action starting while the tail was slow must not wait for its old slot.
        return now + 1e-9 >= nextRead;
    }

    public void Observed(double now, bool changed, bool semanticAction, bool inRange, int actionHz = 60)
    {
        bool first = !sampled;
        sampled = true; pending = false; failureUntil = double.NegativeInfinity;
        bool action = semanticAction || now < actionUntil;
        lastAction = action;
        if (action)
        {
            quietSince = double.NaN; Sleeping = false;
            LastRateHz = Math.Max(1, actionHz); nextRead = LastRateHz >= 60 ? now : now + 1.0 / LastRateHz;
            return;
        }
        if (first && passiveBaseline)
        { Sleep(); return; }
        if (changed)
        {
            quietSince = double.NaN;
            if (double.IsNegativeInfinity(tailStarted)) tailStarted = now;
        }
        else
        {
            if (double.IsNaN(quietSince)) quietSince = now;
            if (now - quietSince + 1e-9 >= QuietSeconds && now >= burstUntil)
            { Sleep(); return; }
        }
        Sleeping = false;
        // Tail age begins after protection expires, preserving the existing
        // first-two-seconds residual cadence. Quiet sleep above can still end
        // all reads earlier; protection is never a mandatory two-second lease.
        LastRateHz = now < burstUntil || now < postActionUntil ? Math.Max(1, actionHz) :
            now - Math.Max(tailStarted, postActionUntil) < FastTailSeconds ? (inRange ? 30 : 15) : (inRange ? 15 : 10);
        nextRead = LastRateHz >= 60 ? now : now + 1.0 / LastRateHz;
    }

    public void Failed(double now)
    {
        // Preserve the last checkpoint and retry with bounded work. Ordinary
        // motion packets cannot clear this backoff; a genuine event can.
        pending = true; Sleeping = false; quietSince = double.NaN;
        LastRateHz = 1; failureUntil = nextRead = now + 1;
    }

    private void Sleep()
    { Sleeping = true; LastRateHz = 0; nextRead = double.PositiveInfinity; }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
