using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class CreatureNativeIdleStorageTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("schema21 native idle clocks survive tree and streaming deltas and action removal", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(21); var reader = new ReplayDeltaCodec.Reader(21);
            var stream = new ReplayDeltaCodec.StreamReader(21); var idle = Idle(0);
            foreach (var frame in new[] { Frame(0, idle), Frame(100, idle), Frame(101, Active(101)), Frame(102, Idle(102)) })
            {
                var token = JObject.FromObject(writer.Encode(frame)); string json = JsonConvert.SerializeObject(token, ReplayFiles.Json);
                var decoded = reader.Decode(token).Creatures[0]; var streamed = stream.DecodeRecord(json)!.Creatures[0];
                Check(decoded.ClockMode == frame.Creatures[0].ClockMode && streamed.ClockMode == decoded.ClockMode);
                if (frame.Creatures[0].IdleAnimation == null) Check(decoded.IdleAnimation == null && streamed.IdleAnimation == null);
                else foreach (var value in new[] { decoded, streamed })
                {
                    Check(value.IdleAnimation!.Clip == CreatureIdleAnimationRules.SpiderIdle);
                    Check(value.IdleAnimation.AnchorTime == value.SampleTime && value.IdleAnimation.RopePhase == .7f);
                    Check(Math.Abs(CreatureIdleAnimationRules.SampleTime(value.IdleAnimation, frame.T) -
                        CreatureIdleAnimationRules.SampleTime(frame.Creatures[0].IdleAnimation!, frame.T)) < 1e-9);
                }
            }
        });
        test("schema21 idle descriptors are optional without expanding older static baselines", () =>
        {
            foreach (int schema in new[] { 15, 19, 20, 21 })
            {
                var creature = Active(0); creature.SampleTimeKnown = schema >= 20;
                var token = JObject.FromObject(new ReplayDeltaCodec.Writer(schema).Encode(Frame(0, creature)));
                Check(token["Creatures"]![0]!["IdleAnimation"] == null);
                Check(new ReplayDeltaCodec.Reader(schema).Decode(token).Creatures[0].IdleAnimation == null);
                Check(new ReplayDeltaCodec.StreamReader(schema).DecodeRecord(token.ToString(Formatting.None))!.Creatures[0].IdleAnimation == null);
            }
        });
        test("native idle clocks and new ghost bodies cannot be written to older schemas", () =>
        {
            Reject(() => new ReplayDeltaCodec.Writer(20).Encode(Frame(0, Idle(0))));
            foreach (string kind in new[] { "ghost-ball", "player-ghost" })
            { var body = Active(0); body.Kind = body.Resource = kind; Reject(() => new ReplayDeltaCodec.Writer(20).Encode(Frame(0, body))); }
            var token = JObject.FromObject(new ReplayDeltaCodec.Writer(21).Encode(Frame(0, Idle(0))));
            Reject(() => new ReplayDeltaCodec.Reader(20).Decode(token));
            Reject(() => new ReplayDeltaCodec.StreamReader(20).DecodeRecord(token.ToString(Formatting.None)));
        });
        test("native idle descriptor baselines reject partial and inconsistent clocks", () =>
        {
            foreach (string field in new[] { "Clip", "AnchorTime", "ClipTime", "Speed", "RopePhase" })
            {
                var token = JObject.FromObject(new ReplayDeltaCodec.Writer(21).Encode(Frame(0, Idle(0))));
                ((JObject)token["Creatures"]![0]!["IdleAnimation"]!).Remove(field);
                Reject(() => new ReplayDeltaCodec.Reader(21).Decode(token));
                Reject(() => new ReplayDeltaCodec.StreamReader(21).DecodeRecord(token.ToString(Formatting.None)));
            }
            foreach (Action<CreatureReplayFrame> bad in new Action<CreatureReplayFrame>[]
            { x => x.State = 1, x => x.ClockMode = 0, x => x.IdleAnimation!.AnchorTime = 1,
              x => x.SampleTimeKnown = false, x => x.Line = null, x => x.Resource = "spider", x => x.Line!.WorldSpace = false,
              x => x.Line!.Points = null! })
            { var frame = Idle(0); bad(frame); Reject(() => CreatureReplayRules.Validate(frame)); }
        });
        test("native idle checkpoint cold seek computes phase without any animation history", () =>
        {
            var frame = Idle(-100); var builder = new CreatureObservationTimeline.Builder();
            builder.Observe(Frame(500, frame)); builder.Observe(Frame(501, frame)); var index = builder.Build(false);
            Check(index.CanSample(new[] { frame }, 500.99));
            Check(index.TrySample(frame.Key, 500.99, out var held, out var right, out var mix) && ReferenceEquals(held, right) && mix == 0);
            Check(Math.Abs(CreatureIdleAnimationRules.SampleTime(held.IdleAnimation!, 500.99) -
                CreatureIdleAnimationRules.SampleTime(frame.IdleAnimation!, 500.99)) < 1e-9);
        });
        test("native idle clocks rebase with creature snapshots and retain immutable geometry", () =>
        {
            var original = Idle(10); var rebased = CreatureReplayRules.Rebase(original, 20);
            Check(rebased.SampleTime == -10 && rebased.IdleAnimation!.AnchorTime == -10);
            Check(ReferenceEquals(original.Pose, rebased.Pose) && ReferenceEquals(original.Line, rebased.Line));
            Check(original.IdleAnimation!.AnchorTime == 10 && original.IdleAnimation.RopePhase == rebased.IdleAnimation!.RopePhase);
            Check(Math.Abs(CreatureIdleAnimationRules.SampleTime(original.IdleAnimation, 100) -
                CreatureIdleAnimationRules.SampleTime(rebased.IdleAnimation!, 80)) < 1e-9);
        });
        test("schema21 idle archive pages retain direct native phase through reverse cold seeks", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "PeakNativeIdle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var idle = Idle(-5); var writer = new FullReplayWriter(root, new ReplayHeader { Schema = ReplayRules.CurrentSchema, Scene = "Level_NativeIdle" });
                for (int i = 0; i <= 180; i++) Check(writer.TryEnqueue(Frame(i / 60d, idle)));
                var result = writer.CompleteAsync().GetAwaiter().GetResult(); Check(result.Status == "completed");
                var info = FullReplayArchive.ReadInfo(result.FilePath); Check(info.Header.Schema == ReplayRules.CurrentSchema && info.Pages.Length >= 2);
                foreach (var page in info.Pages.AsEnumerable().Reverse())
                {
                    var clip = FullReplayArchive.ReadPage(result.FilePath, info, page.Index);
                    var index = new ActorJointTimeline(clip.Frames, false).Creatures;
                    foreach (var frame in clip.Frames)
                    {
                        Check(index.CanSample(frame.Creatures, frame.T));
                        var decoded = frame.Creatures[0]; Check(decoded.IdleAnimation!.AnchorTime == -5);
                        Check(CreatureIdleAnimationRules.SampleTime(decoded.IdleAnimation, frame.T) == CreatureIdleAnimationRules.SampleTime(idle.IdleAnimation!, frame.T));
                    }
                }
            }
            finally
            {
                if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()) + "PeakNativeIdle-", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unexpected native idle test directory.");
                Directory.Delete(root, true);
            }
        });
        test("native idle unavailable visual gate keeps geometry active until a validated boundary", () =>
        {
            var policy = new CreatureCaptureLifecycle(true, false);
            Check(policy.Due(0, true, false, 0, false, false));
            Check(policy.Due(.25, true, false, 0, false, false) && policy.IdleCandidate && policy.ClockMode == 0);
            policy.CompleteIdleVisual(.25, false);
            Check(policy.Due(.29, true, false, 0, false, false) && policy.ClockMode == 0);
            policy.CompleteIdleVisual(.29, true);
            Check(!policy.Due(50, true, false, 0, false, true));
            Check(policy.Due(50.001, true, false, 1, false, true) && policy.ClockMode == 0);
        });
    }
    private static CreatureReplayFrame Idle(double time)
    {
        var frame = Active(time); frame.ClockMode = CreatureReplayRules.IdleClock;
        frame.IdleAnimation = new CreatureIdleAnimation { Clip = CreatureIdleAnimationRules.SpiderIdle,
            AnchorTime = time, ClipTime = .2, Speed = .33f, RopePhase = .7f };
        return frame;
    }
    private static CreatureReplayFrame Active(double time) => new()
    {
        Key = "native-idle", Kind = "spider", Resource = "spider-web", SampleTimeKnown = true, SampleTime = time,
        Pose = new ObjectPose { Active = true }, Line = new CreatureReplayLine { Enabled = true, WorldSpace = true, Points = new float[120], Width = .02f },
    };
    private static ReplayFrame Frame(double time, CreatureReplayFrame creature) => new() { T = time, Creatures = new[] { creature } };
    private static void Check(bool value) { if (!value) throw new Exception("Native idle storage assertion failed."); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception e) when (e is InvalidDataException || e is JsonException) { return; } throw new Exception("Invalid native idle data was accepted."); }
}
