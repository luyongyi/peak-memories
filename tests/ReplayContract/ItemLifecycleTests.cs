using PeakReplayLab;

internal static class ItemLifecycleTests
{
    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("item movement observations cannot perpetually renew convergence heat", () =>
        {
            var schedule = new ItemSamplingPolicy(0);
            schedule.Observed(0, 1, true);
            for (int i = 1; i <= 300; i++) schedule.Observed(i / 60d, 1, true);
            check(!schedule.ShouldRead(5.01, 1, false));
            check(schedule.ShouldRead(5.01, 1, true)); // Actual attached/body/animation reason is independent.
            schedule.Observed(5.02, 1, false); schedule.Observed(5.03, 1, true);
            check(schedule.ShouldRead(5.1, 1, false)); // A new motion episode still opens once.
        });
        test("animator enabled speed alone cannot claim a stopped clock is moving", () =>
        {
            var clock = new NativeAnimationClock();
            clock.Observe(true, 10, .2f, false, false, 1);
            for (int i = 0; i < 600; i++) check(!clock.Observe(true, 10, .2f, false, false, 1));
        });
        test("nonloop forward terminal clock settles despite increasing native normalized time", () =>
        {
            var clock = new NativeAnimationClock();
            check(clock.Observe(true, 10, .7f, false, false, 1));
            check(clock.Observe(true, 10, 1.1f, false, false, 1)); // Final pose edge.
            check(!clock.Observe(true, 10, 12, false, false, 1));
        });
        test("reverse animation preserves its motion and settles at zero", () =>
        {
            var clock = new NativeAnimationClock();
            check(clock.Observe(true, 10, .9f, false, false, -1));
            check(clock.Observe(true, 10, .3f, false, false, -1));
            check(clock.Observe(true, 10, -.1f, false, false, -1));
            check(!clock.Observe(true, 10, -5, false, false, -1));
        });
        test("real looping animation remains an independent long action without event revisions", () =>
        {
            var clock = new NativeAnimationClock();
            clock.Observe(true, 10, 0, true, false, 1); long revision = clock.Revision;
            for (int i = 1; i < 1000; i++) check(clock.Observe(true, 10, i / 30f, true, false, 1));
            check(clock.Revision == revision);
        });
        test("automatic state transition and pause are separate wake boundaries", () =>
        {
            var clock = new NativeAnimationClock();
            clock.Observe(true, 10, 1, false, false, 1); long revision = clock.Revision;
            check(clock.Observe(true, 20, 0, false, true, 1) && clock.Revision > revision);
            revision = clock.Revision;
            check(!clock.Observe(true, 20, .1f, false, false, 0) && clock.Revision > revision);
        });
        test("stopped animation clock wakes once when native progression resumes without a new state", () =>
        {
            var clock = new NativeAnimationClock(); clock.Observe(true, 10, .2f, false, false, 1);
            check(!clock.Observe(true, 10, .2f, false, false, 1)); long stopped = clock.Revision;
            check(!clock.Observe(true, 10, .2f, false, false, 1) && clock.Revision == stopped);
            check(clock.Observe(true, 10, .3f, false, false, 1) && clock.Revision > stopped);
            long resumed = clock.Revision;
            check(clock.Observe(true, 10, .4f, false, false, 1) && clock.Revision == resumed);
            check(!clock.Observe(true, 10, .4f, false, false, float.NaN)); long invalid = clock.Revision;
            check(!clock.Observe(true, 10, .4f, false, false, float.NaN) && clock.Revision == invalid);
        });
        test("light cadence and unknown fallback never authorize every frame hierarchy reads", () =>
        {
            var policy = new ItemActivityPolicy(); int lights = 0, children = 0;
            for (int i = 0; i < 600; i++)
            {
                double now = i / 60d;
                if (policy.LightDue(now)) { lights++; policy.LightObserved(now); }
                bool fallback = policy.UnknownDue(now);
                if (fallback) policy.UnknownObserved(now, 0);
                if (ItemActivityPolicy.ChildrenDue(false, false, false, fallback)) children++;
            }
            check(lights == 300 && children == 20);
            check(!ItemActivityPolicy.ChildrenDue(false, false, false, false));
            check(ItemActivityPolicy.ChildrenDue(true, false, false, false));
            check(ItemActivityPolicy.ChildrenDue(false, true, false, false));
        });
        test("stationary ground item sleeps near players while true ownership and actions stay active", () =>
        {
            check(ItemActivityPolicy.CanSleep(true, false, false, false, false));
            check(!ItemActivityPolicy.CanSleep(true, true, false, false, false));
            check(!ItemActivityPolicy.CanSleep(true, false, true, false, false));
            check(!ItemActivityPolicy.CanSleep(true, false, false, true, false));
            check(!ItemActivityPolicy.CanSleep(true, false, false, false, true));
            var work = new ItemCaptureWorkSet<object>(); var item = new object(); work.Wake(item); work.Sleep(item);
            check(work.Active.Count == 0 && work.SleepingCount == 1);
            work.Wake(item); check(work.Active.Count == 1 && work.SleepingCount == 0);
        });
        test("awake zero velocity physical body reaches quiet sleep with its final checkpoint and bounded fallback", () =>
        {
            var rest = new ItemRestPolicy(); var work = new ItemCaptureWorkSet<object>(); var item = new object(); work.Wake(item);
            var pose = new ObjectPose { Position = new[] { 0f, 0f, 0f }, Rotation = new[] { 0f, 0f, 0f, 1f } };
            var awakeBody = (Awake: true, LinearSquared: 0f, AngularSquared: 0f);
            double final = -1;
            for (int i = 0; i < 600; i++)
            {
                double now = i / 60d;
                bool moving = ItemActivityPolicy.ObservedMotion(i != 0, false, awakeBody.LinearSquared, awakeBody.AngularSquared);
                rest.Observed(now, moving, pose);
                if (!ItemActivityPolicy.CanSleep(rest.Settled, false, false, false, false)) continue;
                final = now; work.Sleep(item); break;
            }
            check(awakeBody.Awake && final >= 1 && final < 1.05 && rest.Settled);
            check(work.Active.Count == 0 && work.SleepingCount == 1);
            check(!ItemSleepPolicy.EventOnly(true, true, false) && ItemSleepPolicy.ProbeDelay(0) == .5);
        });
        test("force waking a quiet body retains real velocity and root movement before another quiet sleep", () =>
        {
            var rest = new ItemRestPolicy(); var work = new ItemCaptureWorkSet<object>(); var item = new object(); work.Wake(item);
            var pose = new ObjectPose { Position = new[] { 0f, 0f, 0f }, Rotation = new[] { 0f, 0f, 0f, 1f } };
            rest.Observed(0, false, pose); rest.Observed(.5, false, pose); rest.Observed(1, false, pose);
            work.Sleep(item); check(rest.Settled && work.SleepingCount == 1);
            // Item.ApplyForceRPC's real Harmony postfix calls MarkMotion, whose
            // queue wakes the work set before the next sampling decision.
            work.Wake(item); check(work.IsActive(item));
            bool forceMotion = ItemActivityPolicy.ObservedMotion(true, false, .25f, 0);
            rest.Observed(5, forceMotion, pose); check(forceMotion && !rest.Settled);
            check(ItemActivityPolicy.ObservedMotion(true, true, 0, 0));
            check(ItemActivityPolicy.ObservedMotion(true, false, 0, .25f));
            rest.Observed(5.1, false, pose); rest.Observed(5.6, false, pose); rest.Observed(6.1, false, pose);
            check(rest.Settled && ItemActivityPolicy.CanSleep(rest.Settled, false, false, false, false));
        });
        test("settled physical safety probes retain final children while real events and motion completion still record a final tree", () =>
        {
            var rest = new ItemRestPolicy();
            var pose = new ObjectPose { Position = new[] { 0f, 0f, 0f }, Rotation = new[] { 0f, 0f, 0f, 1f } };
            rest.Observed(0, false, pose); rest.Observed(.5, false, pose); bool wasSettled = rest.Settled;
            rest.Observed(1, false, pose);
            int finalTrees = ItemActivityPolicy.FinalChildrenDue(false, false, wasSettled, false, false, false) ? 1 : 0;
            check(rest.Settled && finalTrees == 1);
            // Existing quiet physical fallback retains the already complete
            // final checkpoint, instead of repeating its full children read.
            for (int i = 0; i < 100; i++)
                if (ItemActivityPolicy.FinalChildrenDue(false, true, rest.Settled, false, false, false)) finalTrees++;
            check(finalTrees == 1);
            // A native event is not just a safety probe, even if it does not
            // move the root; final child-only action state must still be read.
            check(ItemActivityPolicy.FinalChildrenDue(false, true, true, true, false, false));
            rest.Wake(); rest.Observed(5, true, pose); rest.Observed(5.1, false, pose); rest.Observed(5.6, false, pose);
            wasSettled = rest.Settled; rest.Observed(6.1, false, pose);
            check(rest.Settled && ItemActivityPolicy.FinalChildrenDue(false, false, wasSettled, false, false, false));
            // Unknown fallback already sampled its children once, so no duplicate.
            check(!ItemActivityPolicy.FinalChildrenDue(true, true, true, false, false, false));
        });
        test("unchanged valid physics packets and empty packets do not become repeated events", () =>
        {
            var packets = new ItemMotionPacketPolicy();
            check(!Packet(packets, 0, valid: false));
            check(Packet(packets, 0));
            for (int i = 0; i < 6000; i++) check(!Packet(packets, 0));
            check(Packet(packets, 1));
            check(!Packet(packets, 1));
        });
        test("packet dedupe retains accumulated slow drift and velocity change", () =>
        {
            var packets = new ItemMotionPacketPolicy(); check(Packet(packets, 0));
            check(!Packet(packets, .00004f)); check(!Packet(packets, .00008f)); check(Packet(packets, .00012f));
            check(Packet(packets, .00012f, velocity: .5f)); check(!Packet(packets, .00012f, velocity: .5f));
            check(Packet(packets, .00012f, velocity: 0)); // Last motion-to-rest packet is retained.
        });
        test("sleeping activity policy and packet comparison allocate no hot-path memory", () =>
        {
            var packets = new ItemMotionPacketPolicy(); var activity = new ItemActivityPolicy(); Packet(packets, 0);
            for (int i = 0; i < 100; i++) { Packet(packets, 0); activity.LightDue(i); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 6000; i++) { Packet(packets, 0); activity.LightDue(i); ItemActivityPolicy.CanSleep(true, false, false, false, false); }
            check(GC.GetAllocatedBytesForCurrentThread() == before);
        });
    }
    private static bool Packet(ItemMotionPacketPolicy policy, float x, bool valid = true, float velocity = 0) =>
        policy.Observe(x, 0, 0, 0, 0, 0, valid ? 1 : 0, velocity, 0, 0, 0, 0, 0);
}
