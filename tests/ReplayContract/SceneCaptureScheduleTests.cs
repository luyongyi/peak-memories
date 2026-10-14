using PeakReplayLab;

internal static class SceneCaptureScheduleTests
{
    public static void Run(Action<string, Action> test)
    {
        test("scene periodic capture uses 30 real frames per second while local player retains 60", () =>
        {
            var main = new SampleClock(60); var scene = new SceneCaptureSchedule(); int actors = 0;
            for (int i = 0; i < 7200; i++)
            {
                double now = i / 120d;
                if (!main.ShouldCapture(now)) continue;
                var frame = new ReplayFrame { T = now }; actors++;
                if (scene.Due(now)) scene.Observed(frame); else scene.Reuse(frame);
            }
            Check(actors == 3600 && scene.CaptureTicks == 1800 && scene.ReusedTicks == 1800);
            Check(Math.Abs(scene.ObservedHz - 30) < .001);
        });
        test("scene cadence stays bounded by rendered frames and slower configured player recording", () =>
        {
            foreach (int hz in new[] { 5, 10, 20, 30, 45, 60 })
            {
                var main = new SampleClock(hz); var scene = new SceneCaptureSchedule(); int actors = 0;
                for (int i = 0; i < 6000; i++)
                {
                    double now = 123456 + i / 100d;
                    if (!main.ShouldCapture(now)) continue;
                    var frame = new ReplayFrame { T = now }; actors++;
                    if (scene.Due(now)) scene.Observed(frame); else scene.Reuse(frame);
                }
                Check(scene.CaptureTicks <= 1800 && scene.CaptureTicks <= actors);
                if (hz <= 30) Check(scene.CaptureTicks == actors);
            }
        });
        test("scene repeated player frames retain every scene reference and true observation time", () =>
        {
            var scene = new SceneCaptureSchedule();
            var first = new ReplayFrame { T = 4, World = new WorldFrame(), Items = new[] { new ItemFrame() }, Crates = new[] { new CrateFrame() },
                Ropes = new[] { new RopeReplayFrame() }, Effects = new[] { new EffectReplayFrame() }, Audio = new[] { new AudioReplayFrame() },
                Spawned = new[] { new SpawnedReplayFrame() }, Balloons = new[] { new BalloonReplayFrame() },
                Creatures = new[] { new CreatureReplayFrame() }, Interactions = new[] { new InteractionReplayFrame() } };
            Check(scene.Due(first.T)); scene.Observed(first);
            var second = new ReplayFrame { T = 4.016, Actors = new[] { new ActorFrame { Id = "current-player" } },
                Events = new[] { new ItemEvent { T = 4.009, Sequence = 17 } }, CoverPeakExtraction = true };
            var actors = second.Actors; var events = second.Events;
            Check(!scene.Due(second.T)); scene.Reuse(second);
            Check(second.SceneSampleTimeKnown && second.SceneSampleTime == 4 && first.SceneSampleTime == 4);
            Check(ReferenceEquals(first.World, second.World) && ReferenceEquals(first.Items, second.Items) && ReferenceEquals(first.Crates, second.Crates) &&
                ReferenceEquals(first.Ropes, second.Ropes) && ReferenceEquals(first.Effects, second.Effects) && ReferenceEquals(first.Audio, second.Audio) &&
                ReferenceEquals(first.Spawned, second.Spawned) && ReferenceEquals(first.Balloons, second.Balloons) &&
                ReferenceEquals(first.Creatures, second.Creatures) && ReferenceEquals(first.Interactions, second.Interactions));
            Check(ReferenceEquals(actors, second.Actors) && ReferenceEquals(events, second.Events) && second.Events[0].T == 4.009 && second.CoverPeakExtraction);
        });
        test("scene pause and stalls never create duplicate or catch up observations", () =>
        {
            var scene = new SceneCaptureSchedule(); Check(scene.Due(0)); scene.Observed(new ReplayFrame { T = 0 });
            for (int i = 0; i < 100; i++) Check(!scene.Due(0));
            Check(scene.Due(100)); scene.Observed(new ReplayFrame { T = 100 });
            Check(scene.CaptureTicks == 2 && scene.SkippedSlots == 2999);
            Check(!scene.Due(100) && !scene.Due(100.001));
        });
        test("scene scheduling remains approximately 30 Hz under supported frame jitter", () =>
        {
            var main = new SampleClock(60); var scene = new SceneCaptureSchedule(); double time = 0;
            for (int i = 0; i < 6000; i++)
            {
                time += i % 2 == 0 ? .015 : .019;
                if (!main.ShouldCapture(time)) continue;
                var frame = new ReplayFrame { T = time };
                if (scene.Due(time)) scene.Observed(frame); else scene.Reuse(frame);
            }
            Check(scene.ObservedHz > 29.9 && scene.ObservedHz < 30.1 && scene.CaptureTicks < 3100 && scene.ReusedTicks > 2000);
        });
        test("scene held snapshot reuse allocates no hot path data", () =>
        {
            var scene = new SceneCaptureSchedule(); scene.Observed(new ReplayFrame()); var held = new ReplayFrame { T = .01 };
            for (int i = 0; i < 100; i++) scene.Reuse(held);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 6000; i++) scene.Reuse(held);
            Check(GC.GetAllocatedBytesForCurrentThread() == before);
        });
        test("rope and spawned production actions follow the admitted scene clock once under jitter", () =>
        {
            var main = new SampleClock(60); var scene = new SceneCaptureSchedule();
            var rope = new RopeCaptureLifecycle(); var spawned = new SpawnedCaptureLifecycle(30, followSceneClock: true);
            int ropeReads = 0, spawnedReads = 0; double time = 0;
            for (int i = 0; i < 6000; i++)
            {
                time += i % 2 == 0 ? .015 : .019;
                if (!main.ShouldCapture(time) || !scene.Due(time)) continue;
                scene.Observed(new ReplayFrame { T = time });
                if (rope.ShouldRead(time, true, true, 30, sceneTick: true))
                { ropeReads++; rope.Observed(time, true, true, true, 30); }
                if (spawned.ShouldRead(time, true, false, true))
                { spawnedReads++; spawned.Observed(time, true); }
            }
            Check(ropeReads == scene.CaptureTicks && spawnedReads == scene.CaptureTicks && rope.LastRateHz == 30);
        });
    }
    private static void Check(bool value) { if (!value) throw new Exception("Scene capture schedule assertion failed."); }
}
