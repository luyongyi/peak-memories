using PeakReplayLab;

internal static class InteractionLifecycleTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("interaction static appearance has no periodic read merely because a shader exposes animated controls", () =>
        {
            for (int i = 0; i < 6000; i++) Check(!InteractionAppearanceReadPolicy.Periodic(true, false, false, false, false));
            Check(InteractionAppearanceReadPolicy.Periodic(true, true, false, false, false));
            Check(InteractionAppearanceReadPolicy.Periodic(true, false, true, false, false));
            Check(InteractionAppearanceReadPolicy.Periodic(true, false, false, true, false));
            Check(!InteractionAppearanceReadPolicy.Periodic(false, true, true, true, true));
        });
        test("native Gloom fade reads once at admission and sleeps until a real native action", () =>
        {
            var reads = new InteractionNativeFadeReadWindow();
            Check(reads.ShouldRead(0)); reads.Observed(0);
            for (int i = 1; i < 6000; i++) Check(!reads.ShouldRead(i / 30d));
            reads.NativeEvent(200); Check(reads.ShouldRead(200)); reads.Observed(200);
            Check(reads.ShouldRead(200.5)); reads.Observed(200.5);
            Check(reads.ShouldRead(205)); reads.Observed(205); // A stall still captures the native final value.
            Check(!reads.ShouldRead(205.1) && reads.Until == 201);
            reads.StateChanged(); Check(reads.ShouldRead(300)); reads.Observed(300);
            Check(!reads.ShouldRead(300.1) && reads.Until == 201);
        });
        test("constant and zero-rate native light curves do not claim perpetual flicker activity", () =>
        {
            Check(!InteractionLightDriverPolicy.Varying(false, 1, 10));
            Check(!InteractionLightDriverPolicy.Varying(true, 0, 10));
            Check(!InteractionLightDriverPolicy.Varying(true, 1, 0));
            Check(!InteractionLightDriverPolicy.Varying(true, float.NaN, 10));
            Check(!InteractionLightDriverPolicy.Varying(true, 1, float.PositiveInfinity));
            Check(InteractionLightDriverPolicy.Varying(true, 1, 10));
            Check(InteractionLightDriverPolicy.Varying(true, -1, 10));
        });
        test("warp post weights sleep when stable and retain actual cast and terminal fade observations", () =>
        {
            var reads = new InteractionNativePostReadWindow();
            Check(reads.ShouldRead(0, false)); reads.Observed(0, false);
            for (int i = 1; i < 6000; i++) Check(!reads.ShouldRead(i / 30d, false));
            reads.StateChanged(200);
            for (int i = 0; i <= 600; i++)
            {
                double now = 200 + i / 30d;
                Check(reads.ShouldRead(now, true)); reads.Observed(now, true);
            }
            reads.StateChanged(220);
            for (int i = 1; i <= 60; i++)
            {
                double now = 220 + i / 30d;
                Check(reads.ShouldRead(now, false)); reads.Observed(now, i < 60);
            }
            for (int i = 1; i < 6000; i++)
            {
                double now = 222 + i / 30d;
                if (reads.ShouldRead(now, false)) reads.Observed(now, false);
                if (i > 4) Check(!reads.ShouldRead(now, false));
            }
            reads.StateChanged(500); Check(reads.ShouldRead(500, false));
        });
        test("soul pillar identical network setters admit once and then leave geometry asleep", () =>
        {
            var p = new SoulPillarCaptureEventPolicy();
            Check(p.ObserveBroken(false));
            for (int i = 0; i < 6000; i++) Check(!p.ObserveBroken(false));
            Check(p.ObserveBroken(true));
            for (int i = 0; i < 6000; i++) Check(!p.ObserveBroken(true));
        });
        test("soul pillar native break occurs once even after its nested setter admits the state", () =>
        {
            var p = new SoulPillarCaptureEventPolicy();
            Check(p.ObserveBroken(true));
            Check(SoulPillarCaptureEventPolicy.ActionChanged(0, false, true, false, 0));
            Check(!p.ObserveBroken(true));
            Check(!SoulPillarCaptureEventPolicy.ActionChanged(0, true, true, false, 0));
            Check(!SoulPillarCaptureEventPolicy.ActionChanged(0, false, false, false, 0));
        });
        test("soul pillar charge joins and partial cancellations cannot renew the event window", () =>
        {
            Check(SoulPillarCaptureEventPolicy.ActionChanged(1, false, false, false, 1));
            Check(!SoulPillarCaptureEventPolicy.ActionChanged(1, false, false, true, 2));
            Check(!SoulPillarCaptureEventPolicy.ActionChanged(2, false, false, true, 1));
            Check(SoulPillarCaptureEventPolicy.ActionChanged(2, false, false, true, 0));
            Check(!SoulPillarCaptureEventPolicy.ActionChanged(2, false, false, false, -1));
            Check(!SoulPillarCaptureEventPolicy.ActionChanged(99, false, true, false, 0));
            // Native type 1 sets Interacting true even after an extra cancel
            // made the counter negative. The actual Animator state owns the edge.
            Check(SoulPillarCaptureEventPolicy.ActionChanged(1, false, false, false, 0));
        });
        test("soul pillar repeated no-op callbacks cannot bypass a failed snapshot retry", () =>
        {
            var gate = new SoulPillarCaptureEventPolicy(); var life = new InteractionCaptureLifecycle(0);
            Check(gate.ObserveBroken(false)); life.StateChanged();
            life.Failed(0);
            for (int i = 1; i < 15; i++)
            {
                if (gate.ObserveBroken(false)) life.StateChanged();
                Check(!life.ShouldProbe(i / 60d, false, true, InteractionContinuousState.None));
            }
            if (gate.ObserveBroken(true)) life.StateChanged();
            Check(life.ShouldProbe(.24, false, true, InteractionContinuousState.None));
        });
        test("interaction changing snapshots never extend the finite native event window", () =>
        {
            var p = new InteractionCaptureLifecycle(0); p.NativeEvent(0);
            int after = 0;
            for (int i = 0; i <= 6000; i++)
            {
                double t = i / 60d;
                if (!p.ShouldProbe(t, i != 0, true, InteractionContinuousState.None)) continue;
                if (t > 9) after++;
                // Every sampled value is different; this must not renew an event.
                p.Observed(t, true, InteractionContinuousState.None);
            }
            Check(p.EventUntil == 8 && p.EventWakeCount == 1 && after == 0);
            Check(p.SleepingSkipCount > 5000);
        });
        test("interaction true physical behavior outlives an event without extending it and closes with a final snapshot", () =>
        {
            var p = new InteractionCaptureLifecycle(0); p.NativeEvent(0, .5);
            p.Observed(0, true, InteractionContinuousState.Physics);
            Check(p.ShouldProbe(2, true, true, InteractionContinuousState.Physics));
            p.Observed(2, true, InteractionContinuousState.Physics);
            Check(p.NextProbe < 2.04 && p.EventUntil == .5);
            Check(p.ShouldProbe(2.01, true, true, InteractionContinuousState.None));
            p.Observed(2.01, true, InteractionContinuousState.None);
            Check(!p.ShouldProbe(2.1, true, true, InteractionContinuousState.None));
            Check(double.IsPositiveInfinity(p.NextProbe));
        });
        test("interaction state notifications capture a value without reopening high frequency capture", () =>
        {
            var p = new InteractionCaptureLifecycle(31); p.Observed(0, true, InteractionContinuousState.None);
            p.StateChanged(); Check(p.ShouldProbe(.1, true, true, InteractionContinuousState.None));
            p.Observed(.1, true, InteractionContinuousState.None);
            Check(!p.EventActive(.1) && double.IsPositiveInfinity(p.NextProbe) && p.EventWakeCount == 0);
        });
        test("interaction native action wakes a sleeping object and uses a hard maximum event duration", () =>
        {
            var p = new InteractionCaptureLifecycle(0); p.Observed(0, true, InteractionContinuousState.None);
            p.NativeEvent(.2, 100); Check(p.ShouldProbe(.2, true, true, InteractionContinuousState.None));
            p.Observed(.2, true, InteractionContinuousState.None);
            Check(p.EventUntil == 8.2 && p.NextProbe < .24);
            p.NativeEvent(9, .6); Check(p.EventUntil == 9.6 && p.EventWakeCount == 2);
        });
        test("interaction hidden and reactivated source records each visibility edge and retains its sleeping state", () =>
        {
            var p = new InteractionCaptureLifecycle(0); p.NativeEvent(0);
            p.Observed(0, true, InteractionContinuousState.AdvancingAnimation);
            Check(p.ShouldProbe(.01, true, false, InteractionContinuousState.AdvancingAnimation));
            p.Observed(.01, false, InteractionContinuousState.AdvancingAnimation);
            Check(p.NextProbe > 1 && !p.ShouldProbe(.02, true, false, InteractionContinuousState.AdvancingAnimation));
            Check(p.ShouldProbe(.03, true, true, InteractionContinuousState.None));
            p.Observed(.03, true, InteractionContinuousState.None);
            Check(p.EventUntil == 8);
        });
        test("interaction progressing animation is an independent behavior and stopped animation does not keep it alive", () =>
        {
            var p = new InteractionCaptureLifecycle(0); p.Observed(0, true, InteractionContinuousState.None);
            Check(p.ShouldProbe(.1, true, true, InteractionContinuousState.AdvancingAnimation));
            p.Observed(.1, true, InteractionContinuousState.AdvancingAnimation);
            Check(p.BehaviourWakeCount == 1 && !p.EventActive(.1));
            Check(p.ShouldProbe(.12, true, true, InteractionContinuousState.None));
            p.Observed(.12, true, InteractionContinuousState.None);
            Check(!p.ShouldProbe(.2, true, true, InteractionContinuousState.None));
        });
        test("interaction destroyed entries cannot be revived by a late callback", () =>
        {
            var p = new InteractionCaptureLifecycle(0); p.Observed(0, true, InteractionContinuousState.None);
            p.Retire(); p.NativeEvent(1); p.StateChanged();
            Check(!p.ShouldProbe(100, false, true, InteractionContinuousState.Physics));
            Check(double.IsPositiveInfinity(p.NextProbe) && p.EventWakeCount == 0);
        });
        test("interaction missing initial state retries with bounded work and requires an event after three retries", () =>
        {
            var p = new InteractionCaptureLifecycle(0);
            Check(p.ShouldProbe(0, false, true, InteractionContinuousState.None)); p.Failed(0);
            Check(!p.ShouldProbe(.01, false, true, InteractionContinuousState.None));
            Check(p.ShouldProbe(.25, false, true, InteractionContinuousState.None)); p.Failed(.25);
            Check(!p.ShouldProbe(1, false, true, InteractionContinuousState.None));
            Check(p.ShouldProbe(1.25, false, true, InteractionContinuousState.None)); p.Failed(1.25);
            Check(p.ShouldProbe(5.25, false, true, InteractionContinuousState.None)); p.Failed(5.25);
            Check(!p.ShouldProbe(100, false, true, InteractionContinuousState.None));
            p.NativeEvent(100); Check(p.ShouldProbe(100, false, true, InteractionContinuousState.None));
        });
        test("interaction unknown capability fallback is staggered and repeated sleeping scheduling allocates no managed data", () =>
        {
            var a = new InteractionCaptureLifecycle(0, true); var b = new InteractionCaptureLifecycle(31, true);
            a.Observed(0, true, InteractionContinuousState.None); b.Observed(0, true, InteractionContinuousState.None);
            Check(b.NextProbe > a.NextProbe);
            // Warm the JIT before measuring the pure managed policy.
            for (int i = 0; i < 1000; i++) a.ShouldProbe(.1, true, true, InteractionContinuousState.None);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 6000; i++) a.ShouldProbe(.1, true, true, InteractionContinuousState.None);
            Check(GC.GetAllocatedBytesForCurrentThread() == before);
        });
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("Interaction lifecycle assertion failed."); }
}
