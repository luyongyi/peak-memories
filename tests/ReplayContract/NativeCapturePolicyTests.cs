using PeakReplayLab;

internal static class NativeCapturePolicyTests
{
    public static void Run(Action<string, Action> test)
    {
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
        test("registered static trap explosion dirties immediate snapshot without waiting for integrity poll", () =>
        {
            var schedule = new StaticCreatureCapturePolicy(0);
            Check(schedule.Due(0, false)); schedule.Observed(0);
            for (int i = 1; i < 60; i++) Check(!schedule.Due(i / 60d, true));
            schedule.Changed(); Check(schedule.Due(.2, true)); schedule.Observed(.2);
            Check(!schedule.Due(.21, true)); Check(schedule.Due(1.2, true));
        });
        test("static trap integrity probes are staggered and retain immutable explosion seek states", () =>
        {
            var first = new StaticCreatureCapturePolicy(0); var last = new StaticCreatureCapturePolicy(66);
            first.Observed(0); last.Observed(0);
            Check(first.Due(1, true) && !last.Due(1, true)); Check(last.Due(1.5, true));
            float[] trs = { 1, 2, 3, 0, 0, 0, 1, 1, 1, 1 };
            var alive = CreatureDirectoryRules.StaticPose(null, true, trs);
            last.Changed(); Check(last.Due(.1, true));
            var exploded = CreatureDirectoryRules.StaticPose(alive, false, trs); last.Observed(.1);
            Check(alive.Active && !exploded.Active && ReferenceEquals(alive.Position, exploded.Position));
            Check(!last.Due(.2, true)); Check(CreatureDirectoryRules.StaticPose(exploded, true, trs).Active);
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
