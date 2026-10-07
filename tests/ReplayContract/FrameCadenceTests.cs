using PeakReplayLab;

internal static class FrameCadenceTests
{
    public static void Run(Action<string, Action> test)
    {
        test("render cadence excludes explicit two-second warmup and computes stable FPS", () =>
        {
            var meter = Warm();
            for (int i = 0; i < 1200; i++) meter.Observe(.02, true, true);
            var value = meter.Recording;
            Check(value.Frames == 1200 && Near(value.AverageFps, 50) && Near(value.P99FrameMs, 20));
            Check(Near(value.OnePercentLowFps!.Value, 50) && Near(value.PointOnePercentLowFps!.Value, 50));
        });
        test("low FPS uses mean slowest tail rather than percentile reciprocal", () =>
        {
            var meter = Warm();
            for (int i = 0; i < 990; i++) meter.Observe(.01, true, true);
            for (int i = 0; i < 9; i++) meter.Observe(.02, true, true);
            meter.Observe(.1, true, true);
            var value = meter.Recording;
            Check(Near(value.P99FrameMs, 10) && Near(value.MaximumFrameMs, 100));
            Check(Near(value.OnePercentLowFps!.Value, 10000d / 280) && Near(value.PointOnePercentLowFps!.Value, 10));
        });
        test("low FPS stays unknown until enough frames have been measured", () =>
        {
            var meter = Warm();
            for (int i = 0; i < 99; i++) meter.Observe(.02, true, true);
            Check(meter.Recording.OnePercentLowFps == null && meter.Recording.PointOnePercentLowFps == null);
            meter.Observe(.02, true, true); Check(meter.Recording.OnePercentLowFps != null && meter.Recording.PointOnePercentLowFps == null);
        });
        test("mode switches charge preceding frame and warm the new comparison window", () =>
        {
            var meter = Warm(); meter.Observe(.02, true, true); meter.Observe(.03, false, true);
            Check(meter.Recording.Frames == 2 && meter.Stopped.Frames == 0);
            for (int i = 0; i < 4; i++) meter.Observe(.5, false, true);
            meter.Observe(.01, false, true);
            Check(meter.Stopped.Frames == 1 && Near(meter.Stopped.AverageFps, 100));
        });
        test("render cadence ignores sealing invalid intervals and bounds its history", () =>
        {
            var meter = Warm();
            for (int i = 0; i < 10000; i++) meter.Observe(.02, true, true);
            Check(meter.Recording.Frames == 8192 && meter.Recording.ObservedFrames == 10000);
            meter.Observe(.03, false, false); meter.Observe(double.NaN, false, true); meter.Observe(6, false, true);
            Check(meter.Stopped.Frames == 0); meter.Reset(); Check(meter.Recording.Frames == 0 && meter.Recording.ObservedFrames == 0);
        });
        test("performance snapshots report enqueue P99 allocation without creating fake samples", () =>
        {
            ReplayPerformance.Reset(); ReplayPerformance.Enabled = true;
            for (int i = 0; i < 300; i++) using (ReplayPerformance.Measure(ReplayStage.Enqueue)) { }
            var snapshot = ReplayPerformance.Snapshot(); var row = snapshot.Single(r => r.Stage == "Enqueue");
            Check(row.Samples == 300 && row.RecentP99Ms >= row.RecentP95Ms && ReplayPerformance.Report().Contains("p99="));
            Check(snapshot.Single(r => r.Stage == "Environment").Samples == 0); ReplayPerformance.Reset();
        });
    }
    private static ReplayFrameCadence Warm()
    { var result = new ReplayFrameCadence(); for (int i = 0; i < 5; i++) result.Observe(.5, true, true); return result; }
    private static bool Near(double a, double b) => Math.Abs(a - b) < .000001;
    private static void Check(bool value) { if (!value) throw new Exception("Assertion failed."); }
}
