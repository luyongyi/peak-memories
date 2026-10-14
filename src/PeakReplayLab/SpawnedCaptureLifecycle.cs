using System;

namespace PeakReplayLab;

// One real event owns a finite observation window. Actual growth, shrink, and
// authored animation have an explicit sampling cadence and one final read.
// Appearance/light polling cannot renew or dirty this geometry lifecycle.
internal sealed class SpawnedCaptureLifecycle
{
    private double eventFrom = double.NegativeInfinity, eventUntil = double.NegativeInfinity;
    private double next = double.NegativeInfinity;
    private readonly double interval;
    private readonly bool followCapture;
    private bool wasObserving;
    public bool Dirty { get; private set; } = true;
    public double EventUntil => eventUntil;
    public SpawnedCaptureLifecycle(double sampleHz = 30, bool followSceneClock = false)
    {
        if (!Finite(sampleHz) || sampleHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleHz));
        interval = 1d / sampleHz;
        // The main recorder already caps capture at 60 Hz. Its timestamps can
        // jitter around 1/60; a second 60 Hz deadline would drop native steps.
        // The production 30 Hz scene caller has already selected real frames.
        // Retain legacy independent 60 Hz policy behavior for offline fixtures.
        followCapture = sampleHz >= 60 || followSceneClock && sampleHz == 30;
    }
    public void NativeEvent(double now, double duration = 1.25)
    {
        if (!Finite(now) || !Finite(duration) || duration < 0) throw new ArgumentOutOfRangeException(nameof(duration));
        eventFrom = now; eventUntil = now + duration; Dirty = true;
    }
    public void NotifyStateChange() => Dirty = true;
    public bool IsObserving(double now, bool actualAction) => actualAction || now >= eventFrom && now <= eventUntil;
    public bool ShouldRead(double now, bool hasBaseline, bool visibilityChanged, bool actualAction)
    {
        if (!hasBaseline || Dirty || visibilityChanged) return true;
        bool observing = IsObserving(now, actualAction);
        return observing ? followCapture || !wasObserving || now + 1e-8 >= next : wasObserving;
    }
    public void Observed(double now, bool actualAction)
    {
        Dirty = false; wasObserving = IsObserving(now, actualAction); next = now + interval;
    }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

// Native ShittyPiton keeps isBreaking true after an OnHangStop. Its crack scale
// uses an asymptotic float Lerp, which may stop just short of crackScale. Observe
// actual cached scale advancement rather than an eternal target inequality.
internal sealed class NativePitonCrackActivity
{
    private bool known, wasAdvancing;
    private float target, x, y, z;
    public bool NeedsScale(float crackTarget) => !known || wasAdvancing || crackTarget != target;
    public bool Observe(bool breaking, bool disabled, bool hung, float crackTarget, float sx, float sy, float sz)
    {
        if (!breaking || disabled) { known = wasAdvancing = false; return false; }
        bool advancing = !known || crackTarget != target || sx != x || sy != y || sz != z;
        known = true; wasAdvancing = advancing; target = crackTarget; x = sx; y = sy; z = sz;
        // Hanging permits the next native timed crack even between Lerp steps.
        // A released piton sleeps as soon as the float scale stops advancing.
        return hung || advancing;
    }
}
