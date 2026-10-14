using PeakReplayLab;
using UnityEngine;

internal static class RavenPlaybackTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("raven native clips never construct spider silk and mouth fallback overrides sampled bones", () =>
        {
            RavenAnimationRuntime.Created.Clear();
            var idle = Frame(CreatureIdleAnimationRules.RavenIdle, 0);
            using var playback = new CreatureReplayPlayback(workBudget: new(() => 0));
            Present(playback, idle, 10.5);
            Check(RavenAnimationRuntime.Created.Count == 1 && SpiderIdleLinePlayback.Created.Count == 0 && CreatureIdleAnimationRuntime.Created.Count == 0);
            var visual = VisualReplica.Created.Single(); var runtime = RavenAnimationRuntime.Created.Single();
            int samples = runtime.Samples;
            var mouth = Frame("", 99);
            Present(playback, mouth, 10.5);
            Check(visual.BoneMarker == 99 && runtime.Samples == samples);
            Present(playback, idle, 10.5);
            Check(visual.BoneMarker > 2000 && runtime.Samples == samples + 1);
        });
        test("raven flight-to-idle reverse seeks restore held pose before the selected absolute clip", () =>
        {
            RavenAnimationRuntime.Created.Clear();
            var idle = Frame(CreatureIdleAnimationRules.RavenIdle, 1);
            var fly = Frame(CreatureIdleAnimationRules.RavenFlyAway, 2);
            using var playback = new CreatureReplayPlayback(workBudget: new(() => 0));
            Present(playback, fly, 10.5); var visual = VisualReplica.Created.Single();
            Present(playback, idle, 10.5, true);
            Check(ReferenceEquals(visual.LastPose, idle.Pose) && Math.Abs(visual.BoneMarker - 2000.75) < .000001);
            int samples = RavenAnimationRuntime.Created.Single().Samples;
            Present(playback, idle, 10.5);
            Check(RavenAnimationRuntime.Created.Single().Samples == samples && visual.BoneMarker > 2000);
            playback.Dispose(); Check(RavenAnimationRuntime.Created.All(x => x.Disposed));
        });
    }
    private static CreatureReplayFrame Frame(string clip, int revision) => new()
    {
        Key = "raven", Kind = "voice-thief", Resource = "voice-thief", SampleTimeKnown = true,
        ClockMode = clip.Length == 0 ? CreatureReplayRules.ActiveClock : CreatureReplayRules.IdleClock,
        State = clip == CreatureIdleAnimationRules.RavenFlyAway ? 1 : 0,
        Pose = new ObjectPose { Revision = revision },
        IdleAnimation = clip.Length == 0 ? null : new CreatureIdleAnimation { Clip = clip, AnchorTime = 10, ClipTime = .25, Speed = 1 },
    };
    private static void Present(CreatureReplayPlayback playback, CreatureReplayFrame frame, double time, bool seek = false)
    { Time.frameCount++; playback.Apply(new[] { frame }, new[] { frame }, 0, time, seek); }
    private static void Check(bool value) { if (!value) throw new Exception("Raven playback orchestration failed."); }
}
