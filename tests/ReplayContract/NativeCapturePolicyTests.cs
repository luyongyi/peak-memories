using PeakReplayLab;

internal static class NativeCapturePolicyTests
{
    public static void Run(Action<string, Action> test)
    {
        CrateAppearanceCapturePolicyTests.Run(test);
        StaticCaptureDirectoryTests.Run(test);
        test("native material metadata is cold while motion continues and dirty revisions bypass deadline", () =>
        {
            var schedule = new NativeCapturePolicy(0);
            Check(schedule.FullDue(0, false, false)); schedule.FullObserved(0);
            for (int frame = 1; frame < 30; frame++) Check(!schedule.FullDue(frame / 60d, true, false));
            Check(schedule.FullDue(.01, true, true));
            Check(schedule.FullDue(.5, true, false));
            schedule.FullObserved(.5); Check(!schedule.FullDue(.51, true, false));
        });
        test("native animated shader values retain independent thirty hertz deadline", () =>
        {
            var schedule = new NativeCapturePolicy(0); schedule.FullObserved(0);
            Check(!schedule.AnimatedDue(1d / 60)); Check(schedule.AnimatedDue(1d / 30));
            schedule.AnimatedObserved(1d / 30);
            Check(!schedule.AnimatedDue(3d / 60)); Check(schedule.AnimatedDue(2d / 30));
            Check(!schedule.FullDue(2d / 30, true, false));
        });
        test("native material fallback work uses distinct bounded object deadlines", () =>
        {
            var fast = new NativeCapturePolicy(0); var slow = new NativeCapturePolicy(28);
            fast.FullObserved(0); slow.FullObserved(0);
            Check(fast.FullDue(.5, true, false) && !slow.FullDue(.5, true, false));
            Check(slow.FullDue(.75, true, false));
            var negative = new NativeCapturePolicy(int.MinValue); negative.FullObserved(0);
            Check(negative.FullDue(.75, true, false));
        });
        test("dormant future biome creature keeps baseline until activity changes immediately", () =>
        {
            Check(StaticCreatureCapturePolicy.DynamicDue(false, false, false, 0, 10));
            for (int frame = 0; frame < 3600; frame++)
                Check(!StaticCreatureCapturePolicy.DynamicDue(true, false, false, frame / 60d, .2));
            Check(StaticCreatureCapturePolicy.DynamicDue(true, true, false, .01, 10));
            Check(StaticCreatureCapturePolicy.DynamicDue(true, false, true, .01, 10));
            Check(!StaticCreatureCapturePolicy.DynamicDue(true, true, true, .01, 1d / 30));
            Check(StaticCreatureCapturePolicy.DynamicDue(true, true, true, 1d / 30, 1d / 30));
        });
        test("cold item material deadline wakes independently of its renewed motion deadline", () =>
        {
            var motion = new ItemSamplingPolicy(0); motion.Observed(0, 1, false);
            var appearance = new NativeCapturePolicy(0); appearance.FullObserved(0);
            Check(motion.ShouldRead(.45, 1, false)); motion.Observed(.45, 1, false);
            Check(!motion.ShouldRead(.5, 1, false));
            Check(appearance.NeedsRead(.5, true, false)); appearance.FullObserved(.5);
            Check(!appearance.NeedsRead(.51, true, false) && !motion.ShouldRead(.51, 1, false));
        });
        test("shader declaration alone never promotes a static item to animated polling", () =>
        {
            var appearance = new NativeCapturePolicy(0); appearance.FullObserved(0);
            for (int frame = 1; frame < 30; frame++) Check(!appearance.NeedsRead(frame / 60d, true, false));
            Check(appearance.NeedsRead(1d / 30, true, true));
            Check(!appearance.NeedsRead(1d / 30, false, true));
            Check(appearance.NeedsRead(.5, false, false));
        });
    }
    private static void Check(bool value) { if (!value) throw new Exception("Native capture scheduling assertion failed."); }
}
