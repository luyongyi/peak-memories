using System;
using PeakReplayLab;

public static class SampleClockTests
{
    public static void Run(Action<string, Action> test)
    {
        test("sample clock captures all exact 60 FPS frames", () =>
        {
            var clock = new SampleClock(60);
            for (int i = 0; i <= 7200; i++) Check(clock.ShouldCapture(i / 60d));
            Check(clock.Captured == 7201 && clock.Skipped == 0);
            Near(clock.ObservedRate, 60);
        });
        test("sample clock preserves 59.94 FPS instead of halving to 30 Hz", () =>
        {
            var clock = new SampleClock(60);
            for (int i = 0; i <= 7192; i++) Check(clock.ShouldCapture(i / 59.94d));
            Check(clock.Captured == 7193 && clock.Skipped is >= 6 and <= 8);
            Near(clock.ObservedRate, 59.94);
        });
        test("sample clock selects 60 Hz from 120 FPS", () => CheckHighRate(120));
        test("sample clock selects 60 Hz from 144 FPS", () => CheckHighRate(144));
        test("sample clock captures 30 FPS only once per real frame", () =>
        {
            var clock = new SampleClock(60);
            for (int i = 0; i <= 3600; i++) Check(clock.ShouldCapture(i / 30d));
            Check(clock.Captured == 3601 && clock.Skipped == 3600);
            Near(clock.ObservedRate, 30);
        });
        test("sample clock does not sample paused game time", () =>
        {
            var clock = new SampleClock(60);
            Check(clock.ShouldCapture(9));
            for (int i = 0; i < 500; i++) Check(!clock.ShouldCapture(9));
            Check(clock.Captured == 1 && clock.Skipped == 0 && clock.ObservedRate == 0);
            Check(clock.ShouldCapture(9 + 1 / 60d));
            Check(clock.Captured == 2 && clock.Skipped == 0);
        });
        test("sample clock skips stalled frames without catch-up duplicates", () =>
        {
            var clock = new SampleClock(60);
            Check(clock.ShouldCapture(0));
            Check(clock.ShouldCapture(1 / 60d));
            Check(clock.ShouldCapture(10));
            Check(clock.Captured == 3 && clock.Skipped == 598);
            Check(!clock.ShouldCapture(10));
            Check(!clock.ShouldCapture(10.001));
            Check(clock.ShouldCapture(10 + 1 / 60d));
            Check(clock.Captured == 4 && clock.Skipped == 598);
        });
        test("sample clock rejects non-monotonic time without moving its phase", () =>
        {
            var clock = new SampleClock(60);
            Check(clock.ShouldCapture(20));
            Check(!clock.ShouldCapture(19));
            Check(!clock.ShouldCapture(20.005));
            Check(!clock.ShouldCapture(20.003));
            Check(clock.ShouldCapture(20 + 1 / 60d));
            Check(clock.Captured == 2 && clock.Skipped == 0);
        });
        test("sample clock reset permits a new scene's clock", () =>
        {
            var clock = new SampleClock(60);
            clock.ShouldCapture(1000); clock.ShouldCapture(1001);
            Check(clock.Skipped == 59);
            clock.Reset();
            Check(clock.Captured == 0 && clock.Skipped == 0 && clock.ObservedRate == 0);
            Check(clock.ShouldCapture(0) && clock.ShouldCapture(1 / 60d));
            Check(clock.Captured == 2 && clock.Skipped == 0);
        });
        test("sample clock handles accumulated floating-point frame time", () =>
        {
            var clock = new SampleClock(60);
            double time = 12345.6789;
            for (int i = 0; i <= 7200; i++)
            {
                Check(clock.ShouldCapture(time));
                time += 1 / 60d;
            }
            Check(clock.Captured == 7201 && clock.Skipped == 0);
        });
        test("sample clock phase survives a multi-day game clock", () =>
        {
            var clock = new SampleClock(60);
            for (int i = 0; i <= 7200; i++) Check(clock.ShouldCapture(86400 * 30d + i / 60d));
            Check(clock.Captured == 7201 && clock.Skipped == 0);
        });
        test("sample clock tolerates boundary roundoff but not early rendered frames", () =>
        {
            var clock = new SampleClock(60);
            Check(clock.ShouldCapture(0));
            Check(!clock.ShouldCapture(1 / 60d - 0.00001));
            Check(clock.ShouldCapture(1 / 60d - 1e-10));
            Check(!clock.ShouldCapture(1 / 60d));
            Check(clock.Captured == 2 && clock.Skipped == 0);
        });
        test("sample clock rejects invalid rates and timestamps", () =>
        {
            Reject(() => new SampleClock(0));
            Reject(() => new SampleClock(-1));
            Reject(() => new SampleClock(1001));
            var clock = new SampleClock(60);
            Reject(() => clock.ShouldCapture(double.NaN));
            Reject(() => clock.ShouldCapture(double.PositiveInfinity));
            Reject(() => clock.ShouldCapture(-1));
            Check(clock.ShouldCapture(0));
            Reject(() => clock.ShouldCapture(double.MaxValue));
            Check(clock.ShouldCapture(1 / 60d));
        });
    }

    private static void CheckHighRate(int renderHz)
    {
        var clock = new SampleClock(60);
        int captures = 0;
        double previousCapture = -1;
        for (int i = 0; i <= 120 * renderHz; i++)
        {
            double now = i / (double)renderHz;
            if (!clock.ShouldCapture(now)) continue;
            Check(now > previousCapture);
            previousCapture = now;
            captures++;
            Check(!clock.ShouldCapture(now));
        }
        Check(captures == 7201 && clock.Captured == captures && clock.Skipped == 0);
        Near(clock.ObservedRate, 60);
    }

    private static void Check(bool value)
    {
        if (!value) throw new Exception("Sample clock assertion failed.");
    }

    private static void Near(double actual, double expected)
    {
        Check(Math.Abs(actual - expected) < 0.00001);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Invalid sample clock input was accepted.");
    }
}
