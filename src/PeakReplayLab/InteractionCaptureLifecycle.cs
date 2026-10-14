using System;

namespace PeakReplayLab;

[Flags]
internal enum InteractionContinuousState
{
    None = 0,
    Physics = 1,
    AdvancingAnimation = 2,
    AttachedMotion = 4,
    NativeTween = 8,
}

// An authored shader property existing on a material is not evidence that a
// native script is changing it. Only a live driver, a proven animation or an
// observed native action buys periodic appearance reads.
internal static class InteractionAppearanceReadPolicy
{
    internal static bool Periodic(bool active, bool nativeDriver, bool advancingAnimation, bool nativeEvent, bool fallbackDue) =>
        active && (nativeDriver || advancingAnimation || nativeEvent || fallbackDue);
}

internal static class InteractionLightDriverPolicy
{
    internal static bool Varying(bool varyingCurve, float speed, float amount) =>
        varyingCurve && speed != 0 && amount != 0 && !float.IsNaN(speed) && !float.IsInfinity(speed) && !float.IsNaN(amount) && !float.IsInfinity(amount);
}

// GhostFire.RPC_SetLit starts one native one-second shader coroutine. Include
// the first observation after its deadline: a low frame rate must not strand
// the recorded fade short of the native final value.
internal sealed class InteractionNativeFadeReadWindow
{
    private bool dirty = true, finalPending;
    internal double Until { get; private set; } = double.NegativeInfinity;
    internal void StateChanged() => dirty = true;
    internal void NativeEvent(double now)
    { Until = Math.Max(Until, now + 1); dirty = true; finalPending = true; }
    internal bool ShouldRead(double now) => dirty || now <= Until || finalPending;
    internal void Observed(double now)
    { dirty = false; if (now > Until) finalPending = false; }
}

// Both native warp scripts lerp their volume weights after cast cancellation.
// A cast/feed edge starts observation, then actual output changes retain a
// short settling grace. Stable weights release all Volume reads. This does not
// renew the owning object's semantic event or geometry capture window.
internal sealed class InteractionNativePostReadWindow
{
    private bool dirty = true;
    private double until = double.NegativeInfinity;
    internal void StateChanged(double now) { dirty = true; until = Math.Max(until, now + .1); }
    internal bool ShouldRead(double now, bool nativeCasting) => dirty || nativeCasting || now <= until;
    internal void Observed(double now, bool changed)
    { dirty = false; if (changed) until = Math.Max(until, now + .1); }
}

// SetBroken is also a Photon synchronization setter, called even for identical
// packets. Admission is retained once per capture; only native visual boundaries
// buy an action window. Joining an already charging pillar does not restart it.
internal sealed class SoulPillarCaptureEventPolicy
{
    private bool known, broken;
    internal bool ObserveBroken(bool value)
    {
        bool changed = !known || broken != value;
        known = true; broken = value;
        return changed;
    }
    internal static bool ActionChanged(int type, bool brokenBefore, bool brokenAfter, bool interactingBefore, int charactersAfter) =>
        type == 0 ? !brokenBefore && brokenAfter :
        type == 1 ? !interactingBefore :
        type == 2 && interactingBefore && charactersAfter <= 0;
}

// An observed snapshot changing is not a native action. Only NativeEvent opens
// a finite action window. Independently proven physical/animation activity has
// its own state and never pushes that deadline forward.
internal sealed class InteractionCaptureLifecycle
{
    internal const double ActiveInterval = 1d / 30;
    internal const double MaximumEventSeconds = 8;
    private readonly double coldInterval;
    private readonly bool allowColdFallback, followSceneClock;
    private bool dirty = true, hasObservation, observedActive, retired;
    private int failures;
    private double retryAfter = double.NegativeInfinity;
    private InteractionContinuousState continuous;
    internal double EventUntil { get; private set; } = double.NegativeInfinity;
    internal double NextProbe { get; private set; } = double.NegativeInfinity;
    internal long EventWakeCount { get; private set; }
    internal long BehaviourWakeCount { get; private set; }
    internal long SleepingSkipCount { get; private set; }
    internal bool LastProbeWasFallback { get; private set; }
    internal InteractionCaptureLifecycle(int identity, bool allowColdFallback = false, bool followSceneClock = false)
    { coldInterval = 1 + (identity & 31) * .017; this.allowColdFallback = allowColdFallback; this.followSceneClock = followSceneClock; }
    internal void NativeEvent(double now, double seconds = MaximumEventSeconds)
    {
        if (retired) return;
        if (double.IsNaN(now) || double.IsInfinity(now) || double.IsNaN(seconds) || double.IsInfinity(seconds))
            throw new ArgumentOutOfRangeException(nameof(now));
        EventUntil = Math.Max(EventUntil, now + Math.Max(0, Math.Min(MaximumEventSeconds, seconds)));
        dirty = true; failures = 0; retryAfter = double.NegativeInfinity; EventWakeCount++;
    }
    // A state-only hook still captures its final value, but cannot buy another
    // period of high-frequency polling (e.g. repeated network ammo setters).
    internal void StateChanged() { if (!retired) { dirty = true; failures = 0; retryAfter = double.NegativeInfinity; } }
    internal bool EventActive(double now) => !retired && now <= EventUntil;
    internal bool ShouldProbe(double now, bool hasBaseline, bool active, InteractionContinuousState state)
    {
        if (retired) return false;
        if (!dirty && now + 1e-8 < retryAfter) return false;
        if (!active) state = InteractionContinuousState.None;
        if (hasObservation && active != observedActive) dirty = true;
        if (state != continuous)
        {
            if (continuous == InteractionContinuousState.None && state != InteractionContinuousState.None) BehaviourWakeCount++;
            // Both start and stop require an immediate snapshot. The stop
            // observation is then retained while the object sleeps.
            continuous = state; dirty = true;
        }
        // Only real active behavior/events follow the admitted scene tick.
        // Dormancy, cold fallback and failed-read backoff keep their deadlines.
        bool sceneAction = followSceneClock && active && (EventActive(now) || continuous != InteractionContinuousState.None);
        bool result = !hasBaseline || dirty || sceneAction || now + 1e-8 >= NextProbe;
        LastProbeWasFallback = result && hasObservation && !dirty && allowColdFallback && !EventActive(now) &&
            continuous == InteractionContinuousState.None && !double.IsPositiveInfinity(NextProbe);
        if (!result && !EventActive(now) && continuous == InteractionContinuousState.None) SleepingSkipCount++;
        return result;
    }
    internal void Observed(double now, bool active, InteractionContinuousState state)
    {
        hasObservation = true; observedActive = active; dirty = false; failures = 0; retryAfter = double.NegativeInfinity;
        continuous = active ? state : InteractionContinuousState.None;
        NextProbe = active && (EventActive(now) || continuous != InteractionContinuousState.None) ? now + ActiveInterval :
            allowColdFallback ? now + coldInterval : double.PositiveInfinity;
    }
    internal void Failed(double now)
    {
        dirty = false; failures++;
        retryAfter = failures > 3 ? double.PositiveInfinity : now + (failures == 1 ? .25 : failures == 2 ? 1 : 4);
        NextProbe = retryAfter;
    }
    internal void Retire() { retired = true; dirty = false; continuous = InteractionContinuousState.None; NextProbe = double.PositiveInfinity; }
}
