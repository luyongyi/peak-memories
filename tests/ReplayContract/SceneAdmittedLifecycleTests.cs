using PeakReplayLab;

internal static class SceneAdmittedLifecycleTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("active creature actions retain every admitted scene observation under render jitter", () =>
        {
            foreach (var times in Cases())
            foreach (var policy in new[] { new CreatureCaptureLifecycle(false, false, true),
                new CreatureCaptureLifecycle(false, true, true), new CreatureCaptureLifecycle(true, false, true) })
            {
                int reads = 0;
                foreach (double now in times) if (policy.Due(now, true, false, 2, false)) reads++;
                Check(reads == times.Length && policy.ClockMode == CreatureReplayRules.ActiveClock);
            }
        });
        test("continuous interaction motion retains admitted scene ticks without renewing an event", () =>
        {
            foreach (var times in Cases())
            {
                var policy = new InteractionCaptureLifecycle(0, followSceneClock: true);
                policy.NativeEvent(times[0], .5); int reads = 0;
                foreach (double now in times)
                    if (policy.ShouldProbe(now, reads != 0, true, InteractionContinuousState.Physics))
                    { reads++; policy.Observed(now, true, InteractionContinuousState.Physics); }
                Check(reads == times.Length && policy.EventUntil == times[0] + .5 && policy.EventWakeCount == 1);
                double stopped = times[^1] + .015;
                Check(policy.ShouldProbe(stopped, true, true, InteractionContinuousState.None));
                policy.Observed(stopped, true, InteractionContinuousState.None);
                Check(!policy.ShouldProbe(stopped + .015, true, true, InteractionContinuousState.None) && double.IsPositiveInfinity(policy.NextProbe));
            }
        });
        test("scene-admitted creature sampling never wakes sleeping beetles inactive rigs or settled web spiders", () =>
        {
            var sleeping = new CreatureCaptureLifecycle(false, true, true);
            var inactive = new CreatureCaptureLifecycle(false, false, true);
            var spider = new CreatureCaptureLifecycle(true, false, true);
            int sleepReads = 0, inactiveReads = 0, idleLate = 0;
            foreach (double now in Cases()[0])
            {
                if (sleeping.Due(now, true, true, 0, false)) sleepReads++;
                if (inactive.Due(now, false, false, 0, false)) inactiveReads++;
                if (spider.Due(now, true, false, 0, false) && now > 1001) idleLate++;
            }
            Check(sleepReads == 1 && inactiveReads == 1 && idleLate == 0);
            Check(sleeping.ClockMode == CreatureReplayRules.SleepingClock && spider.ClockMode == CreatureReplayRules.IdleClock);
            sleeping.NotifyNativeEvent(); Check(sleeping.Due(1101, true, false, 0, false));
            Check(inactive.Due(1101, true, false, 0, false));
            spider.NotifyNativeEvent(); Check(spider.Due(1101, true, false, 0, false));
        });
        test("scene-admitted finite interaction events stop and wake only from a new native event", () =>
        {
            var times = Cases()[0]; var policy = new InteractionCaptureLifecycle(0, followSceneClock: true);
            policy.NativeEvent(times[0], .5); int activeTicks = 0, activeReads = 0, lateReads = 0;
            foreach (double now in times)
            {
                if (policy.EventActive(now)) activeTicks++;
                if (!policy.ShouldProbe(now, true, true, InteractionContinuousState.None)) continue;
                if (policy.EventActive(now)) activeReads++;
                if (now > times[0] + 1) lateReads++;
                policy.Observed(now, true, InteractionContinuousState.None);
            }
            Check(activeTicks == activeReads && lateReads == 0 && policy.EventWakeCount == 1 && double.IsPositiveInfinity(policy.NextProbe));
            policy.NativeEvent(times[^1] + .001, .1);
            Check(policy.ShouldProbe(times[^1] + .001, true, true, InteractionContinuousState.None));
        });
        test("scene-admitted interaction active ticks preserve failure backoff cold fallback and retirement", () =>
        {
            var policy = new InteractionCaptureLifecycle(31, allowColdFallback: true, followSceneClock: true);
            policy.Observed(0, true, InteractionContinuousState.None);
            Check(!policy.ShouldProbe(.02, true, true, InteractionContinuousState.None));
            Check(policy.ShouldProbe(1.527, true, true, InteractionContinuousState.None) && policy.LastProbeWasFallback);
            policy.Observed(1.527, true, InteractionContinuousState.None);
            policy.Failed(2);
            Check(!policy.ShouldProbe(2.02, true, true, InteractionContinuousState.Physics));
            Check(policy.ShouldProbe(2.25, true, true, InteractionContinuousState.Physics)); policy.Failed(2.25);
            Check(!policy.ShouldProbe(3, true, true, InteractionContinuousState.Physics));
            policy.NativeEvent(3, .1); Check(policy.ShouldProbe(3, true, true, InteractionContinuousState.Physics));
            policy.Retire(); Check(!policy.ShouldProbe(4, false, true, InteractionContinuousState.Physics));
        });
    }
    private static double[][] Cases() => new[] { Times(60, .0015), Times(58, 0) };
    private static double[] Times(int renderHz, double jitter)
    {
        var main = new SampleClock(60); var scene = new SceneCaptureSchedule(); var result = new List<double>();
        for (int i = 0; i < renderHz * 100; i++)
        {
            double now = 1000 + i / (double)renderHz + (i % 4 < 2 ? jitter : -jitter);
            if (!main.ShouldCapture(now) || !scene.Due(now)) continue;
            scene.Observed(new ReplayFrame { T = now }); result.Add(now);
        }
        Check(result.Count >= 2990 && result.Count <= 3001);
        return result.ToArray();
    }
    private static void Check(bool value) { if (!value) throw new Exception("Admitted scene lifecycle assertion failed."); }
}
