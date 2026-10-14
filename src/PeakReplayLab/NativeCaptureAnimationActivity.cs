using System;
using UnityEngine;

namespace PeakReplayLab;

// Component/layer directories are captured once. Read has no hierarchy searches,
// reflection, clip allocations or per-frame arrays. Scene callers use their
// shared 30 Hz clock; standalone callers retain the local 30 Hz deadline.
internal sealed class NativeCaptureAnimationActivity
{
    private sealed class Binding
    {
        internal readonly Animator Source;
        internal readonly NativeAnimationClock[] Layers;
        internal Binding(Animator source)
        {
            Source = source; Layers = new NativeAnimationClock[Math.Min(32, source.layerCount)];
            for (int i = 0; i < Layers.Length; i++) Layers[i] = new NativeAnimationClock();
        }
    }
    private readonly Binding[] bindings;
    private double next = double.NegativeInfinity;
    private double last = double.NegativeInfinity;
    private readonly bool eventDriven;
    private double probeUntil = double.NegativeInfinity;
    public bool Advancing { get; private set; }
    public long Revision { get; private set; }
    public long ProbeCount { get; private set; }
    public NativeCaptureAnimationActivity(Animator[] sources, bool eventDriven = false)
    {
        this.eventDriven = eventDriven;
        bindings = new Binding[sources.Length];
        for (int i = 0; i < sources.Length; i++) bindings[i] = new Binding(sources[i]);
    }
    public void Invalidate() => last = next = double.NegativeInfinity;
    // Only audited controller setters/activation call Wake. Actual advancing
    // clocks remain watched beyond this grace; a stopped controller releases
    // its native queries after the finite action/initialization lease expires.
    public void Wake(double now, double seconds = .25)
    {
        if (double.IsNaN(now) || double.IsInfinity(now) || double.IsNaN(seconds) || double.IsInfinity(seconds))
            throw new ArgumentOutOfRangeException(nameof(now));
        probeUntil = Math.Max(probeUntil, now + Math.Max(0, seconds));
        Invalidate();
    }
    public bool Read(double now, bool force = false, bool followSceneClock = false)
    {
        if (eventDriven && !force && !Advancing && now > probeUntil) return false;
        if (!force && (followSceneClock ? now <= last + 1e-8 : now + 1e-8 < next)) return Advancing;
        last = now; next = now + 1d / 30; Advancing = false; ProbeCount++;
        foreach (var binding in bindings)
        {
            var source = binding.Source;
            bool enabled = source && source.isActiveAndEnabled && source.runtimeAnimatorController;
            int layerCount = enabled ? source!.layerCount : 0;
            for (int layer = 0; layer < binding.Layers.Length; layer++)
            {
                var clock = binding.Layers[layer]; long revision = clock.Revision;
                bool layerEnabled = enabled && layer < layerCount;
                var state = layerEnabled ? source!.GetCurrentAnimatorStateInfo(layer) : default;
                bool transitioning = layerEnabled && source!.IsInTransition(layer);
                // Grave's default state is a looping state with no Motion.
                // Its normalizedTime alone must not turn an idle bell tower
                // into permanent geometry/material work. This count API needs
                // no clip array, and is released with the same finite lease.
                bool evaluates = layerEnabled && (!eventDriven || transitioning || source!.GetCurrentAnimatorClipInfoCount(layer) != 0);
                Advancing |= clock.Observe(evaluates, state.fullPathHash, state.normalizedTime, state.loop,
                    transitioning, evaluates ? source!.speed * state.speed * state.speedMultiplier : 0);
                if (revision != clock.Revision) Revision++;
            }
        }
        return Advancing;
    }
}
