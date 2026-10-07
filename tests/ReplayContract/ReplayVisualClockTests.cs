using PeakReplayLab;

internal static class ReplayVisualClockTests
{
    public static void Run(Action<string, Action> test)
    {
        test("native water shader advances by playback time while Unity scaled time remains frozen", () =>
        {
            var clock = new ReplayVisualClock(40); clock.SetTime(0);
            Check(Render(clock) == 40);
            clock.SetTime(1d / 60); Near(Render(clock), 40 + 1f / 60);
            clock.SetTime(10); Check(Render(clock) == 50);
        });
        test("paused and buffered replay water keeps exactly the same phase across renders and cameras", () =>
        {
            var clock = new ReplayVisualClock(40); clock.SetTime(12.5);
            float phase = Render(clock);
            for (int frame = 0; frame < 10000; frame++) Check(Render(clock) == phase);
            clock.SetTime(12.5); Check(Render(clock) == phase);
        });
        test("water shader speed follows the supplied playback clock without adding a second integrator", () =>
        {
            var clock = new ReplayVisualClock(40); clock.SetTime(1);
            float before = Render(clock);
            clock.SetTime(1 + .5 / 60); Near(Render(clock) - before, .5f / 60);
            clock.SetTime(1 + 2d / 60); Near(Render(clock) - before, 2f / 60);
        });
        test("seeking water forward backward and repeatedly is independent of seek history", () =>
        {
            var clock = new ReplayVisualClock(40); clock.SetTime(2.25); float expected = Render(clock);
            clock.SetTime(100); Check(Render(clock) == 140);
            clock.SetTime(2.25); Check(Render(clock) == expected);
            clock.SetTime(0); Check(Render(clock) == 40);
            clock.SetTime(2.25); Check(Render(clock) == expected);
        });
        test("native shader time is preserved before a replay sample outside presentation and after close", () =>
        {
            var clock = new ReplayVisualClock(40); float native = 999;
            Check(!clock.Override(true, true, ref native) && native == 999);
            clock.SetTime(10);
            Check(!clock.Override(false, true, ref native) && native == 999);
            Check(!clock.Override(true, false, ref native) && native == 999);
            Check(clock.Override(true, true, ref native) && native == 50);
            clock.Close(); clock.SetTime(20); native = 999;
            Check(!clock.Override(true, true, ref native) && native == 999);
        });
        test("invalid replay shader time cannot publish non-finite GPU animation values", () =>
        {
            var clock = new ReplayVisualClock(40); clock.SetTime(10);
            foreach (double bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, double.MaxValue })
            {
                bool rejected = false;
                try { clock.SetTime(bad); } catch (ArgumentOutOfRangeException) { rejected = true; }
                Check(rejected && Render(clock) == 50);
            }
        });
    }
    private static float Render(ReplayVisualClock clock)
    {
        float frozenUnityTime = 40;
        Check(clock.Override(true, true, ref frozenUnityTime)); return frozenUnityTime;
    }
    private static void Near(float actual, float expected) => Check(Math.Abs(actual - expected) < .00001f);
    private static void Check(bool value) { if (!value) throw new Exception("Replay visual clock assertion failed."); }
}
