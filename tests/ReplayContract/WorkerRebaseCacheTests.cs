using System;
using Newtonsoft.Json;
using PeakReplayLab;

internal static class WorkerRebaseCacheTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("bounded continuous scene rebase retains hot sharing and recomputes evicted clocks exactly", () =>
        {
            var mapper = new ContinuousReplayRebaser(100);
            var source = RavenFrame(100); string original = Json(source);
            var first = mapper.Apply(source); var same = mapper.Apply(source);
            Check(ReferenceEquals(first.Creatures, same.Creatures));
            for (int i = 1; i < 2400; i++) mapper.Apply(RavenFrame(100 + i / 30d));
            var later = mapper.Apply(source);
            Check(!ReferenceEquals(first.Creatures, later.Creatures) && Json(first) == Json(later));
            Check(ReferenceEquals(source.Creatures[0].Pose, later.Creatures[0].Pose));
            Check(source.Creatures[0].SampleTime == 100 && later.Creatures[0].SampleTime == 0 &&
                later.Creatures[0].IdleAnimation!.AnchorTime == 0 && Json(source) == original);
            Check(ReferenceEquals(later.Creatures, mapper.Apply(source).Creatures));
        });
        test("bounded highlight track rebase evicts old translations without copying immutable pose arrays", () =>
        {
            var mapper = new ReplayTrackRebaser<CreatureReplayFrame>(CreatureReplayRules.Rebase, 100);
            var source = RavenFrame(100).Creatures;
            var first = mapper.Apply(source);
            for (int i = 1; i < 2400; i++) mapper.Apply(RavenFrame(100 + i).Creatures);
            var later = mapper.Apply(source);
            Check(!ReferenceEquals(first, later) && Json(first) == Json(later));
            Check(ReferenceEquals(source[0].Pose.Nodes, later[0].Pose.Nodes) && source[0].IdleAnimation!.AnchorTime == 100);
            Check(ReferenceEquals(later, mapper.Apply(source)));
        });
        test("bounded environment rebase preserves frozen geometry and recreates evicted timestamps once", () =>
        {
            var source = World(80); var mapper = new EnvironmentTimeRebaser(50);
            var first = mapper.Apply(source); Check(ReferenceEquals(first, mapper.Apply(source)));
            for (int i = 0; i < 800; i++) mapper.Apply(World(81 + i));
            var later = mapper.Apply(source);
            Check(!ReferenceEquals(first, later) && Json(first) == Json(later));
            Check(later.Environment!.SampleTime == 30 && source.Environment!.SampleTime == 80 &&
                ReferenceEquals(source.ActiveMapObjects, later.ActiveMapObjects) &&
                ReferenceEquals(source.Environment.Winds, later.Environment.Winds));
            Check(ReferenceEquals(later, mapper.Apply(source)));
        });
        test("bounded effect birth rebase retains exact early end and immutable evidence after eviction", () =>
        {
            var source = Birth(12); var mapper = new EffectReplayInitialStateRebaser(10);
            var first = mapper.Apply(source)!; Check(ReferenceEquals(first, mapper.Apply(source)));
            for (int i = 0; i < 600; i++) mapper.Apply(Birth(20 + i));
            var later = mapper.Apply(source)!;
            Check(!ReferenceEquals(first, later) && Json(first) == Json(later));
            Check(later.ObservedAt == 2 && later.UntilTime == 2.5 && later.EndTime == 2.25 && later.AnchorTime == 2);
            Check(source.ObservedAt == 12 && ReferenceEquals(source.Seeds, later.Seeds) && ReferenceEquals(source.Particles, later.Particles));
            Check(ReferenceEquals(later, mapper.Apply(source)));
        });
        test("bounded legacy checkpoint rebase never copies payload or changes digest on cache eviction", () =>
        {
            var source = Particles(12); var mapper = new EffectParticleCheckpointRebaser(10);
            var first = mapper.Apply(source)!;
            for (int i = 0; i < 80; i++) mapper.Apply(Particles(20 + i));
            var later = mapper.Apply(source)!;
            Check(!ReferenceEquals(first, later) && Json(first) == Json(later));
            var old = source[0].Checkpoint!; var translated = later[0].Checkpoint!;
            Check(translated.ObservedAt == 2 && old.ObservedAt == 12 && translated.RootPhase == old.RootPhase);
            Check(ReferenceEquals(old.Data, translated.Data) && ReferenceEquals(old.State, translated.State) && translated.Digest == old.Digest);
            EffectParticleCheckpointRules.Validate(translated);
            Check(ReferenceEquals(later, mapper.Apply(source)));
        });
        test("unchanged hot scene descriptors remain shared while unrelated observations churn past cache capacity", () =>
        {
            var hot = RavenFrame(100).Creatures[0]; var mapper = new ContinuousReplayRebaser(100);
            CreatureReplayFrame? translated = null;
            for (int i = 0; i < 2400; i++)
            {
                var frame = RavenFrame(100 + i / 30d); frame.Creatures = new[] { hot, frame.Creatures[0] };
                var current = mapper.Apply(frame).Creatures[0];
                if (translated == null) translated = current;
                else Check(ReferenceEquals(translated, current));
            }
            Check(hot.IdleAnimation!.AnchorTime == 100 && translated!.IdleAnimation!.AnchorTime == 0);
        });
    }
    private static ReplayFrame RavenFrame(double time) => new()
    {
        T = time, Creatures = new[] { new CreatureReplayFrame
        {
            Key = "raven:" + time, Kind = "voice-thief", Resource = "voice-thief", SampleTimeKnown = true, SampleTime = time,
            ClockMode = CreatureReplayRules.IdleClock, Pose = new ObjectPose(),
            IdleAnimation = new CreatureIdleAnimation { Clip = CreatureIdleAnimationRules.RavenIdle, AnchorTime = time, ClipTime = .75, Speed = 1 },
        } },
    };
    private static WorldFrame World(double time) => new()
    {
        ActiveMapObjects = new[] { true, false }, Environment = new EnvironmentReplayFrame
        { SampleTimeKnown = true, SampleTime = time, Winds = new[] { new EnvironmentWindFrame { Key = "wind", Direction = new[] { 1f, 0f, 0f } } } },
    };
    private static EffectReplayInitialState Birth(double time) => new()
    { ObservedAt = time, UntilTime = time + .5, AnchorTime = time, Ended = true, EndTime = time + .25, Seeds = new[] { 917L } };
    private static EffectParticleFrame[] Particles(double time)
    {
        var checkpoint = new EffectParticleCheckpoint { ObservedAt = time, RootPhase = 12, Layout = "test-layout", State = new byte[] { 1, 2, 3 }, Data = new byte[] { 4, 5, 6, 7 }, Count = 1 };
        checkpoint.Digest = EffectParticleCheckpointRules.Digest(checkpoint);
        return new[] { new EffectParticleFrame(new NodePose(), Array.Empty<float>(), Array.Empty<float>()) { Checkpoint = checkpoint } };
    }
    private static string Json(object value) => JsonConvert.SerializeObject(value);
    private static void Check(bool value) { if (!value) throw new Exception("Bounded worker rebase regression failed."); }
}
