using PeakReplayLab;

internal static class CrateAppearanceCapturePolicyTests
{
    public static void Run(Action<string, Action> test)
    {
        test("active chest appearance follows admitted scene observations without a second jittered deadline", () =>
        {
            foreach (double fps in new[] { 58d, 60d })
            {
                var scene = new SampleClock(30); var schedule = new CrateAppearanceCapturePolicy(7);
                var clip = new CrateAnimationFrame { Anchored = true, AnchorTime = 0, Time = 0, Duration = 200, Rate = 1 };
                int observations = 0, reads = 0;
                for (int frame = 0; frame < 6000; frame++)
                {
                    double time = frame / fps + (frame == 0 ? 0 : (frame % 3 - 1) * .0015);
                    if (!scene.ShouldCapture(time)) continue;
                    observations++;
                    if (schedule.Due(time, frame != 0, false, true, false, clip, followSceneClock: true))
                    { reads++; schedule.Observed(time); }
                    Check(!schedule.Due(time, true, false, true, false, clip, followSceneClock: true));
                }
                Check(reads == observations);
            }
        });
        test("scene admission does not wake dormant chests or sustain expired opening appearance", () =>
        {
            var schedule = new CrateAppearanceCapturePolicy(5); schedule.Observed(0);
            var clip = new CrateAnimationFrame { Anchored = true, AnchorTime = 0, Time = 0, Duration = .2f, Rate = 1 };
            int reads = 0;
            for (int i = 1; i <= 300; i++)
            {
                double time = i / 30d;
                Check(!schedule.Due(time, true, false, false, false, clip, followSceneClock: true));
                if (schedule.Due(time, true, false, true, false, clip, followSceneClock: true))
                { reads++; schedule.Observed(time); }
            }
            Check(reads < 30);
        });
        test("194 dormant chests retain appearance baseline across 20 seconds at sixty hertz", () =>
        {
            var schedules = Enumerable.Range(0, 194).Select(id => new CrateAppearanceCapturePolicy(id)).ToArray();
            foreach (var schedule in schedules) { Check(schedule.Due(0, false, false, false, false, null)); schedule.Observed(0); }
            for (int frame = 1; frame <= 1200; frame++) foreach (var schedule in schedules)
                Check(!schedule.Due(frame / 60d, true, false, false, false, null));
        });
        test("194 stationary visible chest material polls are bounded and staggered", () =>
        {
            var schedules = Enumerable.Range(0, 194).Select(id => new CrateAppearanceCapturePolicy(id)).ToArray();
            foreach (var schedule in schedules) schedule.Observed(0);
            int[] reads = new int[194]; int maximumPerTick = 0;
            for (int frame = 1; frame <= 1200; frame++)
            {
                int tick = 0;
                for (int i = 0; i < schedules.Length; i++)
                    if (schedules[i].Due(frame / 60d, true, false, true, false, null))
                    { reads[i]++; tick++; schedules[i].Observed(frame / 60d); }
                maximumPerTick = Math.Max(maximumPerTick, tick);
            }
            Check(reads.All(count => count >= 26 && count <= 40)); Check(maximumPerTick < 40);
        });
        test("appearance event bypasses deadline even for a now inactive mirage chest", () =>
        {
            var schedule = new CrateAppearanceCapturePolicy(int.MinValue); schedule.Observed(10);
            Check(!schedule.Due(10.01, true, false, false, false, null));
            Check(schedule.Due(10.01, true, true, false, false, null)); schedule.Observed(10.01);
            Check(!schedule.Due(10.02, true, false, false, false, null));
        });
        test("a notified opening samples appearance at thirty hertz without polling idle neighbours", () =>
        {
            var acting = new CrateAppearanceCapturePolicy(0); var idle = new CrateAppearanceCapturePolicy(0);
            acting.Observed(0); idle.Observed(0); int samples = 0;
            for (int frame = 1; frame < 30; frame++)
            {
                double time = frame / 60d;
                if (acting.Due(time, true, false, true, true, null)) { acting.Observed(time); samples++; }
                Check(!idle.Due(time, true, false, true, false, null));
            }
            Check(samples == 14);
        });
        test("an opening keeps dynamic appearance reads through its real clip duration and settles", () =>
        {
            var clip = new CrateAnimationFrame { Anchored = true, AnchorTime = 10, Time = .5f, Duration = 3, Rate = 1 };
            Check(!CrateAppearanceCapturePolicy.Advancing(clip, 9.9));
            Check(CrateAppearanceCapturePolicy.Advancing(clip, 12.5));
            Check(CrateAppearanceCapturePolicy.Advancing(clip, 12.53));
            Check(!CrateAppearanceCapturePolicy.Advancing(clip, 12.6));
            clip.Rate = -1; clip.Time = 2;
            Check(CrateAppearanceCapturePolicy.Advancing(clip, 12)); Check(!CrateAppearanceCapturePolicy.Advancing(clip, 12.1));
        });
        test("paused and legacy unanchored chest poses never cause continuous appearance reads", () =>
        {
            var clip = new CrateAnimationFrame { Anchored = true, AnchorTime = 0, Duration = 3, Rate = 0, Loop = true };
            Check(!CrateAppearanceCapturePolicy.Advancing(clip, 1));
            clip.Anchored = false; clip.Rate = 1; Check(!CrateAppearanceCapturePolicy.Advancing(clip, 1));
            clip.Anchored = true; Check(CrateAppearanceCapturePolicy.Advancing(clip, 999));
            Check(!new CrateAppearanceCapturePolicy(0).Due(999, true, false, false, true, clip));
        });
    }
    private static void Check(bool value) { if (!value) throw new Exception("Crate appearance scheduling assertion failed."); }
}
