using PeakReplayLab;

internal static class SpawnedLifecycleTests
{
    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("spawned baseline is retained while independent light clock advances", () =>
        {
            var life = new SpawnedCaptureLifecycle(); var lights = new ItemActivityPolicy();
            check(life.ShouldRead(0, false, false, false)); life.Observed(0, false);
            int reads = 0, lightReads = 0;
            for (int i = 1; i <= 6000; i++)
            {
                double now = i / 60d;
                if (lights.LightDue(now)) { lightReads++; lights.LightObserved(now); }
                if (life.ShouldRead(now, true, false, false)) reads++;
            }
            check(reads == 0 && lightReads == 3000);
        });
        test("spawned event observes at 30 Hz then keeps one complete final checkpoint", () =>
        {
            var life = new SpawnedCaptureLifecycle(); life.NativeEvent(0);
            int reads = 0, readsAfterWindow = 0; double final = -1;
            for (int i = 0; i <= 6000; i++)
            {
                double now = i / 60d;
                if (!life.ShouldRead(now, reads != 0, false, false)) continue;
                reads++; if (now > 1.25) { readsAfterWindow++; final = now; }
                life.Observed(now, false);
            }
            check(reads >= 38 && reads <= 40 && readsAfterWindow == 1 && final <= 1.25 + 1d / 30);
            check(life.EventUntil == 1.25);
        });
        test("observed geometry changes cannot renew a spawned native event", () =>
        {
            var life = new SpawnedCaptureLifecycle(); life.NativeEvent(10, .5);
            for (int i = 0; i < 300; i++)
            {
                double now = 10 + i / 60d;
                if (life.ShouldRead(now, true, false, false)) life.Observed(now, false);
                check(life.EventUntil == 10.5);
            }
            check(!life.ShouldRead(100, true, false, false));
        });
        test("spawned real long growth outlives event and records its exact completion edge", () =>
        {
            var life = new SpawnedCaptureLifecycle(); life.NativeEvent(0); int reads = 0;
            for (int i = 0; i < 600; i++)
            {
                double now = i / 60d;
                if (life.ShouldRead(now, true, false, true)) { life.Observed(now, true); reads++; }
            }
            check(reads == 300 && life.EventUntil == 1.25);
            check(life.ShouldRead(10, true, false, false)); life.Observed(10, false);
            for (int i = 1; i < 600; i++) check(!life.ShouldRead(10 + i / 60d, true, false, false));
        });
        test("spawned native event wakes cold state immediately without corrupting final phase", () =>
        {
            var life = new SpawnedCaptureLifecycle(); life.Observed(0, false);
            check(!life.ShouldRead(50, true, false, false));
            life.NativeEvent(50); check(life.Dirty && life.ShouldRead(50, true, false, false)); life.Observed(50, false);
            check(!life.ShouldRead(50.001, true, false, false));
            life.NativeEvent(50.002); check(life.ShouldRead(50.002, true, false, false));
        });
        test("spawned visibility and explicit dirtiness are one-shot boundaries without new heat", () =>
        {
            var life = new SpawnedCaptureLifecycle(); life.Observed(0, false);
            check(life.ShouldRead(20, true, true, false)); life.Observed(20, false);
            check(!life.ShouldRead(20.001, true, false, false));
            life.NotifyStateChange(); check(life.ShouldRead(20.002, true, false, false)); life.Observed(20.002, false);
            check(!life.IsObserving(20.003, false) && !life.ShouldRead(20.003, true, false, false));
        });
        test("spawned true action starts immediately between inactive deadlines", () =>
        {
            var life = new SpawnedCaptureLifecycle(); life.Observed(0, false);
            check(life.ShouldRead(.001, true, false, true)); life.Observed(.001, true);
            check(!life.ShouldRead(.002, true, false, true));
            check(life.ShouldRead(.003, true, false, false)); life.Observed(.003, false);
            check(!life.ShouldRead(.004, true, false, false));
        });
        test("spawned lifecycle bookkeeping allocates no hot-path memory", () =>
        {
            var life = new SpawnedCaptureLifecycle(); life.NativeEvent(0); life.Observed(0, true);
            for (int i = 0; i < 100; i++) life.ShouldRead(i / 60d, true, false, true);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 6000; i++)
            { double now = i / 60d; if (life.ShouldRead(now, true, false, true)) life.Observed(now, true); }
            check(GC.GetAllocatedBytesForCurrentThread() == before);
        });
        test("spawned legacy independent policy can retain full 60 Hz growth without static polling", () =>
        {
            var life = new SpawnedCaptureLifecycle(60); int reads = 0;
            for (int i = 0; i < 600; i++)
            {
                double now = i / 60d;
                if (life.ShouldRead(now, true, false, true)) { reads++; life.Observed(now, true); }
            }
            check(reads == 600 && life.ShouldRead(10, true, false, false)); life.Observed(10, false);
            check(!life.ShouldRead(10.01, true, false, false));
        });
        test("spawned 60 Hz mode follows irregular supported main capture timestamps without dropping native steps", () =>
        {
            var life = new SpawnedCaptureLifecycle(60); life.NativeEvent(0); int reads = 0;
            // Average is below 60 Hz; real caller intervals still vary around
            // 1/60, so there must not be another quantizing timer underneath.
            double[] times = { 0, .018, .034, .052, .068, .086, .102, .120, .136 };
            foreach (double now in times)
            {
                check(life.ShouldRead(now, reads != 0, false, true)); life.Observed(now, true); reads++;
            }
            check(reads == times.Length);
            check(life.ShouldRead(2, true, false, false)); life.Observed(2, false);
            check(!life.ShouldRead(2.018, true, false, false));
        });
        test("released breaking piton keeps native lerp final float state and stops all geometry reads", () =>
        {
            var life = new SpawnedCaptureLifecycle(60); var crack = new NativePitonCrackActivity(); life.NativeEvent(0);
            float scale = 0, recorded = -1; int readsAfterTail = 0, scaleReadsAfterTail = 0; bool action = false;
            for (int i = 0; i < 6000; i++)
            {
                // Native Update: Vector3.Lerp(current, Vector3.one * crackScale, deltaTime * 15).
                scale += (.75f - scale) * .25f;
                double now = i / 60d;
                if ((i & 1) == 0)
                {
                    bool scaleDue = crack.NeedsScale(.75f);
                    if (scaleDue && now > 2) scaleReadsAfterTail++;
                    action = scaleDue && crack.Observe(true, false, false, .75f, scale, scale, scale);
                }
                if (!life.ShouldRead(now, recorded >= 0, false, action)) continue;
                if (now > 2) readsAfterTail++;
                recorded = scale; life.Observed(now, action);
            }
            check(recorded == scale && recorded != .75f && readsAfterTail == 0 && scaleReadsAfterTail == 0);
            check(!crack.Observe(true, false, false, .75f, scale, scale, scale));
        });
        test("piton hanging and later timed cracks wake from idle without extending the old event", () =>
        {
            var life = new SpawnedCaptureLifecycle(60); var crack = new NativePitonCrackActivity();
            crack.Observe(true, false, false, .75f, .75f, .75f, .75f);
            check(!crack.Observe(true, false, false, .75f, .75f, .75f, .75f)); life.Observed(0, false);
            check(!crack.NeedsScale(.75f));
            for (int i = 0; i < 600; i++)
            {
                double now = 10 + i / 60d;
                bool action = crack.Observe(true, false, true, .75f, .75f, .75f, .75f);
                check(action && life.ShouldRead(now, true, false, action)); life.Observed(now, action);
            }
            bool released = crack.Observe(true, false, false, .75f, .75f, .75f, .75f);
            check(!released && life.ShouldRead(20, true, false, released)); life.Observed(20, released);
            check(!life.ShouldRead(20.001, true, false, false));
            bool newTarget = crack.Observe(true, false, false, 1.5f, .75f, .75f, .75f);
            check(newTarget && life.ShouldRead(25, true, false, newTarget));
            check(double.IsNegativeInfinity(life.EventUntil));
        });
        test("broken disabled piton finishes the native hide boundary even if hung and breaking flags stay true", () =>
        {
            var life = new SpawnedCaptureLifecycle(60); var crack = new NativePitonCrackActivity();
            bool active = crack.Observe(true, false, true, 3, 2, 2, 2); life.Observed(5, active);
            bool broken = crack.Observe(true, true, true, 3, 2.5f, 2.5f, 2.5f);
            check(!broken && life.ShouldRead(5.001, true, false, broken)); life.Observed(5.001, broken);
            check(!life.ShouldRead(100, true, false, false));
        });
    }
}
