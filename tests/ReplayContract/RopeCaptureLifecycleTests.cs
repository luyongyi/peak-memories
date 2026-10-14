using System;
using PeakReplayLab;

internal static class RopeCaptureLifecycleTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Check(bool value) { if (!value) throw new Exception("Rope capture lifecycle assertion failed."); }
        static int Run(RopeCaptureLifecycle policy, double from, double to, bool changed, bool action = false, bool nearby = true, int hz = 60)
        {
            int reads = 0;
            for (int i = (int)Math.Round(from * 60); i < (int)Math.Round(to * 60); i++)
            {
                double now = i / 60.0;
                if (!policy.ShouldRead(now, action, nearby, hz)) continue;
                policy.Observed(now, changed, action, nearby, hz); reads++;
            }
            return reads;
        }
        test("rope arbitrary shape changes cannot renew high-rate lifetime", () =>
        {
            var value = new RopeCaptureLifecycle(); value.Notify(0, RopeCaptureEvent.Attachment);
            int reads = Run(value, 0, 60, true);
            Check(reads > 900 && reads < 1000 && value.LastRateHz == 15 && value.BurstUntil == .25 && !value.Sleeping);
        });
        test("rope real long-running actions remain distinct from settling", () =>
        {
            var value = new RopeCaptureLifecycle();
            Check(Run(value, 0, 12, true, true) == 720 && value.LastRateHz == 60);
            Check(value.ShouldRead(12, false, true)); value.Observed(12, true, false, true);
            Check(value.TailStartedAt == 12 && value.BurstUntil == 12.25);
            Check(Run(value, 12 + 1.0 / 60, 30, true) < 430 && !value.Sleeping);
        });
        test("rope full-rate native actions retain every irregular global capture tick", () =>
        {
            var value = new RopeCaptureLifecycle();
            double now = 0;
            for (int i = 0; i < 600; i++)
            {
                // Average 60 Hz with the jitter seen in actual recorder ticks.
                // No second 60 Hz limiter may discard the shorter intervals.
                Check(value.ShouldRead(now, true, true, 60));
                value.Observed(now, true, true, true, 60);
                now += i % 2 == 0 ? .015 : 1d / 30 - .015;
            }
            Check(value.LastRateHz == 60 && value.BurstUntil == double.NegativeInfinity);
        });
        test("rope full-rate bursts follow irregular ticks then residual settling reduces cadence", () =>
        {
            var value = new RopeCaptureLifecycle(); value.Notify(0, RopeCaptureEvent.Topology);
            double now = 0; int burst = 0, tail = 0, tailTicks = 0;
            for (int i = 0; i < 120; i++)
            {
                bool due = value.ShouldRead(now, false, true, 60);
                if (now < RopeCaptureLifecycle.BurstSeconds) { Check(due); burst++; }
                else { tailTicks++; if (due) tail++; }
                if (due) value.Observed(now, true, false, true, 60);
                now += i % 2 == 0 ? .015 : 1d / 30 - .015;
            }
            Check(burst >= 15 && tail > 0 && tail < tailTicks * .6 && value.LastRateHz == 30);
            Check(value.BurstUntil == .25);
        });
        test("rope scene-vine actions keep their explicit lower cadence on irregular ticks", () =>
        {
            var value = new RopeCaptureLifecycle(); double now = 0; int reads = 0;
            for (int i = 0; i < 600; i++)
            {
                if (value.ShouldRead(now, true, true, 30))
                { value.Observed(now, true, true, true, 30); reads++; }
                now += i % 2 == 0 ? .015 : 1d / 30 - .015;
            }
            Check(reads > 200 && reads <= 300 && value.LastRateHz == 30);
        });
        test("rope following the global action clock cannot defeat capture failure backoff", () =>
        {
            var value = new RopeCaptureLifecycle(); value.Notify(0, RopeCaptureEvent.Topology);
            Check(value.ShouldRead(0, true, true, 60)); value.Failed(0);
            Check(!value.ShouldRead(.015, true, true, 60));
            Check(!value.ShouldRead(.1, true, true, 60));
            Check(value.ShouldRead(1, true, true, 60));
        });
        test("rope post-action protection preserves every recoil frame and arbitrary changes cannot renew it", () =>
        {
            var value = new RopeCaptureLifecycle(); Run(value, 0, 1, true, true);
            Check(value.ShouldRead(1, false, true)); value.Observed(1, true, false, true);
            Check(value.PostActionUntil == 3 && value.TailStartedAt == 1);
            Check(Run(value, 1 + 1d / 60, 3, true) == 119 && value.LastRateHz == 60);
            Check(value.ShouldRead(3, false, true)); value.Observed(3, true, false, true);
            Check(value.LastRateHz == 30);
            int residual = Run(value, 3 + 1d / 60, 7, true);
            Check(residual > 70 && residual < 120 && value.LastRateHz == 15);
            Check(value.PostActionUntil == 3 && value.BurstUntil == 1.25 && !value.Sleeping);
        });
        test("rope stable post-action state sleeps before its protection deadline", () =>
        {
            var value = new RopeCaptureLifecycle(); Run(value, 0, 1, false, true);
            Check(value.ShouldRead(1, false, true)); value.Observed(1, false, false, true);
            Run(value, 1 + 1d / 60, 2.5, false);
            Check(value.Sleeping && value.PostActionUntil == 3);
            Check(!value.ShouldRead(2.75, false, true));
        });
        test("rope genuine events do not extend protection but a new completed action does", () =>
        {
            var value = new RopeCaptureLifecycle(); Run(value, 0, 1, true, true);
            Check(value.ShouldRead(1, false, true)); value.Observed(1, true, false, true);
            value.Notify(1.5, RopeCaptureEvent.Visibility);
            Check(value.PostActionUntil == 3);
            value.Notify(1.6, RopeCaptureEvent.Topology);
            Check(value.PostActionUntil == 3);
            Check(value.ShouldRead(2, true, true)); value.Observed(2, true, true, true);
            Check(value.ShouldRead(2.2, false, true)); value.Observed(2.2, true, false, true);
            Check(Math.Abs(value.PostActionUntil - 4.2) < 1e-9);
        });
        test("rope scene-vine post-action protection retains its lower native cadence", () =>
        {
            var value = new RopeCaptureLifecycle(); Run(value, 0, 1, true, true, true, 30);
            Check(value.ShouldRead(1, false, true, 30)); value.Observed(1, true, false, true, 30);
            int reads = Run(value, 1 + 1d / 60, 3, true, false, true, 30);
            Check(reads >= 58 && reads <= 60 && value.LastRateHz == 30 && value.PostActionUntil == 3);
            Check(value.ShouldRead(3, false, true, 30)); value.Observed(3, true, false, true, 30);
            Check(value.LastRateHz == 30);
        });
        test("rope a genuine topology sequence retains deployment frames", () =>
        {
            var value = new RopeCaptureLifecycle(); int reads = 0;
            for (int i = 0; i < 600; i++)
            {
                double now = i / 60.0;
                if (i < 300 && i % 10 == 0) value.Notify(now, RopeCaptureEvent.Topology);
                if (!value.ShouldRead(now, false, true)) continue;
                value.Observed(now, true, false, true); if (i < 300) reads++;
            }
            Check(reads == 300 && value.LastRateHz == 15 && value.BurstUntil < 5.1);
        });
        test("rope unchanged final checkpoint sleeps and stops geometry reads", () =>
        {
            var value = new RopeCaptureLifecycle(); value.Notify(0, RopeCaptureEvent.Attachment);
            Run(value, 0, 3, true); int quiet = Run(value, 3, 5, false);
            Check(quiet > 1 && value.Sleeping && value.NextReadAt == double.PositiveInfinity);
            Check(Run(value, 5, 600, false) == 0);
        });
        test("rope stationary vine and anchor baselines sleep after one read", () =>
        {
            var value = new RopeCaptureLifecycle(true); value.Notify(0, RopeCaptureEvent.Baseline);
            Check(Run(value, 0, 600, true) == 1 && value.Sleeping);
        });
        test("rope semantic flight overrides passive initial state until final pose", () =>
        {
            var value = new RopeCaptureLifecycle(true); value.Notify(0, RopeCaptureEvent.AnchorFlight, 3.5);
            Check(Run(value, 0, 3.5, true) == 210 && !value.Sleeping);
            Check(value.ShouldRead(3.5, false, true)); value.Observed(3.5, true, false, true);
            Check(!value.Sleeping && value.TailStartedAt == 3.5);
            Run(value, 3.5 + 1.0 / 60, 6, false); Check(value.Sleeping);
        });
        test("rope native collapse timeline retains final shader state", () =>
        {
            var value = new RopeCaptureLifecycle(true); value.Notify(0, RopeCaptureEvent.BridgeFall, 1.0 / .7 + .05);
            int reads = Run(value, 0, 1.5, true, false, true, 30);
            Check(reads >= 44 && reads <= 46 && !value.Sleeping);
            Run(value, 1.5, 4, false, false, true, 30); Check(value.Sleeping);
        });
        test("rope new events immediately wake a sleeping checkpoint", () =>
        {
            var value = new RopeCaptureLifecycle(true); Run(value, 0, 2, false); Check(value.Sleeping);
            value.Notify(2, RopeCaptureEvent.Topology); Check(value.ShouldRead(2, false, true));
            value.Observed(2, true, false, true); Check(!value.Sleeping && value.LastRateHz == 60);
        });
        test("rope repeated network poses wake only once without renewing burst", () =>
        {
            var value = new RopeCaptureLifecycle(true); Run(value, 0, 1, false); Check(value.Sleeping);
            value.Notify(1, RopeCaptureEvent.Motion); Check(value.ShouldRead(1, false, true));
            value.Observed(1, true, false, true); int reads = 1;
            for (int i = 61; i < 3600; i++)
            {
                double now = i / 60.0; value.Notify(now, RopeCaptureEvent.Motion);
                if (!value.ShouldRead(now, false, true)) continue;
                value.Observed(now, true, false, true); reads++;
            }
            Check(reads < 950 && value.BurstUntil == double.NegativeInfinity && value.LastRateHz == 15);
        });
        test("rope distance reduces only residual cadence and never active motion", () =>
        {
            var near = new RopeCaptureLifecycle(); var far = new RopeCaptureLifecycle();
            int nearReads = Run(near, 0, 60, true); int farReads = Run(far, 0, 60, true, false, false);
            Check(farReads < nearReads * .75 && far.LastRateHz == 10 && !far.Sleeping);
            Check(Run(far, 60, 61, true, true, false) == 60 && far.LastRateHz == 60);
        });
        test("rope failure preserves retries without a per-frame exception loop", () =>
        {
            var value = new RopeCaptureLifecycle(); Check(value.ShouldRead(0, false, true)); value.Failed(0);
            for (int i = 1; i < 60; i++)
            { value.Notify(i / 60.0, RopeCaptureEvent.Motion); Check(!value.ShouldRead(i / 60.0, false, true)); }
            Check(value.ShouldRead(1, false, true)); value.Failed(1);
            value.Notify(1.1, RopeCaptureEvent.Attachment); Check(value.ShouldRead(1.1, false, true));
            value.Observed(1.1, true, false, true); Check(!value.Sleeping);
        });
        test("rope subthreshold frames require a full quiet window before sleeping", () =>
        {
            var value = new RopeCaptureLifecycle(); value.Notify(0, RopeCaptureEvent.Attachment);
            Run(value, 0, 2, false); Check(value.Sleeping);
            value.Notify(2, RopeCaptureEvent.Climbing); Run(value, 2, 3.5, false, true); Check(!value.Sleeping);
            Check(value.ShouldRead(3.5, false, true)); value.Observed(3.5, false, false, true);
            Check(!value.Sleeping); Run(value, 3.5 + 1.0 / 60, 4.8, false); Check(value.Sleeping);
        });
        test("rope invalid clocks cannot trigger an unbounded capture", () =>
        {
            var value = new RopeCaptureLifecycle();
            value.Notify(double.NaN, RopeCaptureEvent.Topology, double.PositiveInfinity);
            Check(!value.ShouldRead(double.NaN, true, true) && !value.ShouldRead(double.PositiveInfinity, true, true));
        });
    }
}
