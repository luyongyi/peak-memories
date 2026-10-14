using System;

namespace PeakReplayLab;

// Native semantic state is read from cached managed fields. A sampled pose must
// never wake itself or renew a high-rate lease merely because idle bones moved.
public sealed class CreatureCaptureLifecycle
{
    public const double ActionInterval = 1d / 30;
    public const double InitialSettleSeconds = .25;
    private readonly bool spider, beetle, followSceneClock;
    private bool observed, lastActive, lastSleeping, lastStunned, pending;
    private int lastState;
    private double nextSample = double.NegativeInfinity, settleUntil = double.NegativeInfinity;
    public int ClockMode { get; private set; }
    public double NextSampleAt => nextSample;
    public bool LastWasBoundary { get; private set; }
    public bool IdleCandidate { get; private set; }

    public CreatureCaptureLifecycle(bool spider, bool beetle, bool followSceneClock = false)
    { this.spider = spider; this.beetle = beetle; this.followSceneClock = followSceneClock; }

    public void NotifyNativeEvent() => pending = true;

    public bool Due(double now, bool active, bool sleeping, int spiderState, bool stunned, bool idleVisualReady = true)
    {
        if (double.IsNaN(now) || double.IsInfinity(now)) return false;
        sleeping &= beetle;
        stunned &= spider;
        int state = spider ? spiderState : 0;
        bool first = !observed;
        bool activation = observed && active != lastActive;
        bool boundary = first || pending || activation || sleeping != lastSleeping || state != lastState || stunned != lastStunned;
        // The first native LateUpdate initializes the line endpoints and rig.
        // This short window belongs only to admission/reactivation/real events.
        // Observing idle animation is deliberately unable to extend it.
        if (spider && active && (first || activation || pending)) settleUntil = now + InitialSettleSeconds;
        IdleCandidate = spider && active && state == 0 && !stunned && now >= settleUntil;
        int mode = !active || sleeping ? CreatureReplayRules.SleepingClock :
            IdleCandidate && idleVisualReady ? CreatureReplayRules.IdleClock : CreatureReplayRules.ActiveClock;
        boundary |= observed && ClockMode != mode;
        ClockMode = mode;
        lastActive = active; lastSleeping = sleeping; lastState = state; lastStunned = stunned;
        observed = true;
        LastWasBoundary = boundary;
        // A settled web spider is an event-driven checkpoint, just like a
        // sleeping beetle. Tiny idle leg/silk motion does not wake the rig.
        // Native action, stun and visibility edges still bypass this deadline.
        bool dormant = mode != CreatureReplayRules.ActiveClock;
        // Production has already admitted this frame through the single scene
        // 30 Hz clock. A second relative deadline would drop jittered ticks.
        if (!boundary && (dormant || !followSceneClock && now + 1e-8 < nextSample)) return false;
        pending = false;
        nextSample = dormant ? double.PositiveInfinity : now + ActionInterval;
        return true;
    }

    // Only a completed boundary read with a supported native visual clock may
    // stop geometry. Unsupported assets/transitions retain action sampling.
    public void CompleteIdleVisual(double now, bool ready)
    {
        if (!IdleCandidate) return;
        ClockMode = ready ? CreatureReplayRules.IdleClock : CreatureReplayRules.ActiveClock;
        nextSample = ready ? double.PositiveInfinity : now + ActionInterval;
        LastWasBoundary = true;
    }
}
