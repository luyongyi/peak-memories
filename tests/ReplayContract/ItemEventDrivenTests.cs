using PeakReplayLab;

internal static class ItemEventDrivenTests
{
    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("event-driven item workset coalesces repeated wake and preserves sleeping membership", () =>
        {
            var work = new ItemCaptureWorkSet<object>(); var item = new object();
            work.Wake(item); work.Wake(item); check(work.Active.Count == 1);
            work.Sleep(item); work.Sleep(item); check(work.Active.Count == 0 && work.SleepingCount == 1);
            for (int i = 0; i < 100; i++) work.Wake(item);
            check(work.Active.Count == 1 && work.SleepingCount == 0 && work.IsActive(item));
            work.Forget(item); check(work.Active.Count == 0 && work.SleepingCount == 0);
            check(work.NextSleeping() == null);
        });
        test("event-driven reverse iteration can retire every active item without skipping", () =>
        {
            var work = new ItemCaptureWorkSet<object>(); var seen = new HashSet<object>();
            for (int i = 0; i < 1000; i++) work.Wake(new object());
            for (int i = work.Active.Count - 1; i >= 0; i--)
            { var item = work.Active[i]; check(seen.Add(item)); work.Sleep(item); }
            check(seen.Count == 1000 && work.Active.Count == 0 && work.SleepingCount == 1000);
        });
        test("sleeping round robin covers every object despite wake removal and new registrations", () =>
        {
            var work = new ItemCaptureWorkSet<object>(); var items = new List<object>();
            for (int i = 0; i < 127; i++) { var item = new object(); items.Add(item); work.Sleep(item); }
            for (int i = 0; i < 63; i++) { var item = work.NextSleeping()!; work.Wake(item); }
            check(work.Active.Count == 63 && work.SleepingCount == 64);
            foreach (var item in items) work.Sleep(item);
            var seen = new HashSet<object>();
            for (int i = 0; i < 254; i++) seen.Add(work.NextSleeping()!);
            check(seen.Count == 127);
            work.Clear(); check(work.Active.Count == 0 && work.SleepingCount == 0);
        });
        test("sleeping source replacement keeps old checkpoint identity out of the workset", () =>
        {
            var work = new ItemCaptureWorkSet<object>(); var oldEntity = new object(); var newEntity = new object();
            work.Sleep(oldEntity); work.Forget(oldEntity); work.Wake(newEntity);
            check(work.SleepingCount == 0 && work.Active.Count == 1);
            check(ReferenceEquals(work.Active[0], newEntity));
        });
        test("pure event sleep requires healthy hooks known semantic behavior and a kinematic body", () =>
        {
            check(ItemSleepPolicy.EventOnly(true, true, true));
            check(!ItemSleepPolicy.EventOnly(false, true, true));
            check(!ItemSleepPolicy.EventOnly(true, false, true));
            check(!ItemSleepPolicy.EventOnly(true, true, false));
        });
        test("untouched known kinematic object needs only its initial baseline even near a player", () =>
        {
            check(ItemSleepPolicy.InitialStatic(true, true, true, false, false, false));
            check(!ItemSleepPolicy.InitialStatic(true, true, true, true, false, false));
            check(!ItemSleepPolicy.InitialStatic(true, true, true, false, true, false));
            check(!ItemSleepPolicy.InitialStatic(true, true, true, false, false, true));
            check(!ItemSleepPolicy.InitialStatic(false, true, true, false, false, false));
            check(!ItemSleepPolicy.InitialStatic(true, true, true, false, false, false, motionPending: true));
        });
        test("queued kinematic movement stays active until the next fixed-step pose can be captured", () =>
        {
            var work = new ItemCaptureWorkSet<object>(); var item = new object();
            work.Sleep(item); work.Wake(item);
            var schedule = new ItemSamplingPolicy(0); schedule.Observed(0, 0, false);
            const double motionUntil = .25;
            var old = new ObjectPose(); var final = new ObjectPose { Position = new[] { 3f, 0f, 0f } };
            for (int frame = 1; frame <= 14; frame++)
            {
                double now = frame / 60.0;
                check(!ItemSleepPolicy.InitialStatic(true, true, true, false, false, false, now < motionUntil));
                check(schedule.ShouldRead(now, 0, now < motionUntil));
            }
            check(ItemRestPolicy.RootMoved(old, final));
            // After a real moved pose, initial-static eligibility never bypasses
            // the ordinary continuous-rest and distance requirements again.
            check(!ItemSleepPolicy.InitialStatic(true, true, true, true, false, false));
            check(work.IsActive(item));
        });
        test("a sleeping initial static object validates on approach once without repeated in-range wakes", () =>
        {
            var distance = new ItemDistancePolicy(1); distance.Observed(0, 1, 0, 0, 0);
            var actor = new ActorFrame { Id = "player", Position = new[] { 0f, 0f, 0f } };
            var actors = new[] { actor };
            check(distance.Include(actors, 40, "", out bool first) && first); // baseline
            for (int i = 0; i < 100; i++) check(distance.Include(actors, 40, "", out bool entered) && !entered);
            actor.Position = new[] { 100f, 0f, 0f }; check(!distance.Include(actors, 40, "", out _));
            actor.Position = new[] { 0f, 0f, 0f }; check(distance.Include(actors, 40, "", out bool returned) && returned);
            check(distance.Include(actors, 40, "", out bool repeated) && !repeated);
        });
        test("held creatures animated lights and pending physics never sleep due to distance", () =>
        {
            check(ItemSleepPolicy.CanSleep(false, true, false, false, false));
            check(!ItemSleepPolicy.CanSleep(true, true, false, false, false));
            check(!ItemSleepPolicy.CanSleep(false, false, false, false, false));
            check(!ItemSleepPolicy.CanSleep(false, true, true, false, false));
            check(!ItemSleepPolicy.CanSleep(false, true, false, true, false));
            check(!ItemSleepPolicy.CanSleep(false, true, false, false, true));
        });
        test("distant resting item wakes for a different recorded player approaching", () =>
        {
            var distance = new ItemDistancePolicy(10);
            distance.Observed(0, 1, 500, 0, 0);
            var actors = new[] { new ActorFrame { Id = "local", Position = new[] { 0f, 0f, 0f } },
                new ActorFrame { Id = "remote", Position = new[] { 100f, 0f, 0f } } };
            check(!distance.Include(actors, 40, "", out _));
            actors[1].Position = new[] { 480f, 0f, 0f };
            check(distance.Include(actors, 40, "", out bool entered) && entered);
        });
        test("unowned thrown item must finish a full quiet second before leaving active capture", () =>
        {
            var work = new ItemCaptureWorkSet<object>(); var item = new object(); var rest = new ItemRestPolicy();
            work.Wake(item);
            for (int i = 0; i <= 119; i++)
            {
                bool moving = i < 60; rest.Observed(i / 60.0, moving, new ObjectPose());
                if (ItemSleepPolicy.CanSleep(false, rest.Settled, false, false, false)) work.Sleep(item);
                check(work.IsActive(item));
            }
            rest.Observed(2, false, new ObjectPose());
            if (ItemSleepPolicy.CanSleep(false, rest.Settled, false, false, false)) work.Sleep(item);
            check(work.Active.Count == 0 && work.SleepingCount == 1);
            // The next physical notification reintroduces it before sampling.
            work.Wake(item); rest.Wake(); check(work.IsActive(item) && !rest.Settled);
        });
        test("stationary capture scales with active items while distance batches stay bounded", () =>
        {
            var work = new ItemCaptureWorkSet<object>();
            for (int i = 0; i < 1000; i++) work.Sleep(new object());
            for (int i = 0; i < 4; i++) work.Wake(new object());
            int activeVisits = 0, distanceChecks = 0, sleeperNativeReads = 0;
            for (int frame = 0; frame < 600; frame++)
            {
                activeVisits += work.Active.Count;
                int budget = ItemSleepPolicy.DistanceProbeBudget(work.SleepingCount);
                check(budget <= 32);
                for (int i = 0; i < budget; i++)
                { check(work.NextSleeping() != null); distanceChecks++; }
                // All sleepers are known static and kinematic with healthy hooks.
                if (!ItemSleepPolicy.EventOnly(true, true, true)) sleeperNativeReads++;
            }
            check(activeVisits == 2400 && distanceChecks == 19200 && sleeperNativeReads == 0);
            Console.WriteLine($"  item-work-counts: frames=600 total=1004 active-visits={activeVisits} (previous full-table=602400), cached-distance={distanceChecks}, sleeping-native-reads={sleeperNativeReads}");
        });
        test("fallback probes are staggered below one second and hook failure disables event-only sleep", () =>
        {
            var deadlines = new HashSet<double>();
            for (int identity = -32; identity <= 32; identity++)
            {
                double delay = ItemSleepPolicy.ProbeDelay(identity);
                check(delay >= .5 && delay < .75); deadlines.Add(delay);
                check(!ItemSleepPolicy.EventOnly(false, true, true));
            }
            check(deadlines.Count == 17);
        });
    }
}
