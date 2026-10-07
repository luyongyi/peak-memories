using System;

namespace PeakReplayLab;

// Separate expensive metadata polling from motion and native shader animation.
// Object-specific deadlines spread steady-state fallback work across frames;
// observed native changes and first snapshots always bypass the deadline.
internal sealed class NativeCapturePolicy
{
    private readonly double spread;
    private double nextFull = double.NegativeInfinity, nextAnimated = double.NegativeInfinity;
    public NativeCapturePolicy(int identity) => spread = Math.Abs(identity % 29) / 29d * .25;
    public bool FullDue(double now, bool hasBaseline, bool dirty) => !hasBaseline || dirty || now >= nextFull;
    public bool AnimatedDue(double now) => now + 1e-8 >= nextAnimated;
    public bool NeedsRead(double now, bool active, bool animated) => FullDue(now, true, false) || active && animated && AnimatedDue(now);
    public void FullObserved(double now) { nextFull = now + .5 + spread; nextAnimated = now + 1d / 30; }
    public void AnimatedObserved(double now) => nextAnimated = now + 1d / 30;
}

internal sealed class StaticCreatureCapturePolicy
{
    private readonly double spread;
    private bool dirty = true;
    private double next = double.NegativeInfinity;
    public StaticCreatureCapturePolicy(int identity) => spread = Math.Abs(identity % 67) / 67d * .5;
    public void Changed() => dirty = true;
    public bool Due(double now, bool hasBaseline) => !hasBaseline || dirty || now >= next;
    public void Observed(double now) { dirty = false; next = now + 1 + spread; }
    public static bool DynamicDue(bool hasBaseline, bool active, bool wasActive, double now, double nextSample) =>
        !hasBaseline || active != wasActive || active && now + 1e-8 >= nextSample;
}
