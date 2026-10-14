using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class EarlyWormAnimationStorageTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("audited worm native clocks sample loops backwards and zero length cooked pose deterministically", () =>
        {
            foreach (string clip in new[] { CreatureIdleAnimationRules.EarlyWormWalk, CreatureIdleAnimationRules.EarlyWormInHand })
            {
                var animation = Animation(clip);
                Check(Math.Abs(CreatureIdleAnimationRules.SampleTime(animation, 10.5) - .75) < .00001);
                Check(Math.Abs(CreatureIdleAnimationRules.SampleTime(animation, 9.5) - .75) < .00001);
                Check(Math.Abs(CreatureIdleAnimationRules.SampleTime(animation, 10.5) - .75) < .00001);
            }
            var cooked = Animation(CreatureIdleAnimationRules.EarlyWormCooked);
            Check(CreatureIdleAnimationRules.SampleTime(cooked, -100) == 0 && CreatureIdleAnimationRules.SampleTime(cooked, 100) == 0);
        });
        test("schema twenty five worm asset and spin anchors preserve tree stream retained snapshots", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(25); var tree = new ReplayDeltaCodec.Reader(25); var stream = new ReplayDeltaCodec.StreamReader(25);
            var retained = new List<(ItemFrame Item, string Json)>();
            int number = 0;
            foreach (string clip in new[] { CreatureIdleAnimationRules.EarlyWormWalk, CreatureIdleAnimationRules.EarlyWormInHand, CreatureIdleAnimationRules.EarlyWormCooked })
            {
                var animation = Animation(clip); animation.AnchorTime = number;
                var item = new ItemFrame { Key = "worm", ItemId = 184, IdleAnimation = animation, SampleTimeKnown = true,
                    SampleTime = number, ClockMode = CreatureReplayRules.IdleClock };
                var frame = new ReplayFrame { T = number++, Items = new[] { item } };
                ReplayRules.Validate(frame, -1);
                string encoded = Json(writer.Encode(frame)); var decoded = tree.Decode(JObject.Parse(encoded)).Items[0]; var streamed = stream.DecodeRecord(encoded)!.Items[0];
                Check(Json(decoded) == Json(item) && Json(streamed) == Json(item)); retained.Add((streamed, Json(streamed)));
            }
            foreach (var saved in retained) Check(Json(saved.Item) == saved.Json);
        });
        test("worm references and spin fields cannot be smuggled into schema twenty four baselines", () =>
        {
            var frame = new ReplayFrame { Items = new[] { new ItemFrame { Key = "worm", ItemId = 184,
                IdleAnimation = Animation(CreatureIdleAnimationRules.EarlyWormWalk) } } };
            Reject(() => new ReplayDeltaCodec.Writer(24).Encode(frame));
            var frog = new ItemFrame { Key = "frog", ItemId = 172, IdleAnimation = new CreatureIdleAnimation { Clip = CreatureIdleAnimationRules.FrogIdle, Speed = 1 } };
            var token = JObject.FromObject(new ReplayDeltaCodec.Writer(24).Encode(new ReplayFrame { Items = new[] { frog } }));
            var descriptor = (JObject)token["Items"]![0]!["IdleAnimation"]!;
            Check(descriptor["SpinRotation"] == null && descriptor["SpinSpeed"] == null);
            descriptor["SpinSpeed"] = 0;
            Reject(() => new ReplayDeltaCodec.Reader(24).Decode(token));
            Reject(() => new ReplayDeltaCodec.StreamReader(24).DecodeRecord(Json(token)));
        });
        test("worm clocks reject unaudited item resources rotation axes and invalid cooked time", () =>
        {
            void Bad(Action<CreatureIdleAnimation> mutate)
            { var animation = Animation(CreatureIdleAnimationRules.EarlyWormWalk); mutate(animation); Reject(() => CreatureIdleAnimationRules.Validate(animation)); }
            Bad(a => a.SpinSpeed = 3601); Bad(a => a.SpinSpeed = float.NaN); Bad(a => a.SpinRotation = null);
            Bad(a => a.SpinRotation = new float[4]); Bad(a => a.SpinRotation = new[] { 0f, 0f, 1f }); Bad(a => a.RopePhase = .1f);
            var wrong = new ItemFrame { Key = "wrong", ItemId = 172, IdleAnimation = Animation(CreatureIdleAnimationRules.EarlyWormWalk) };
            Reject(() => ReplayRules.Validate(new ReplayFrame { Items = new[] { wrong } }, -1));
            var cooked = Animation(CreatureIdleAnimationRules.EarlyWormCooked); cooked.Speed = 1; Reject(() => CreatureIdleAnimationRules.Validate(cooked));
            cooked = Animation(CreatureIdleAnimationRules.EarlyWormCooked); cooked.ClipTime = .1; Reject(() => CreatureIdleAnimationRules.Validate(cooked));
            var raven = new CreatureIdleAnimation { Clip = CreatureIdleAnimationRules.RavenIdle, Speed = 1, SpinRotation = new[] { 0f, 0f, 0f, 1f } };
            Reject(() => CreatureIdleAnimationRules.Validate(raven));
        });
        test("native worm root motion interpolates independently while action clip changes stay discrete", () =>
        {
            ItemFrame Worm(double time, string clip, float x) => new() { Key = "worm", ItemId = 184, SampleTimeKnown = true, SampleTime = time,
                IdleAnimation = Animation(clip), Pose = new ObjectPose { Position = new[] { x, 0f, 0f } } };
            var a = Worm(0, CreatureIdleAnimationRules.EarlyWormWalk, 0); var b = Worm(.2, CreatureIdleAnimationRules.EarlyWormWalk, 2);
            var builder = new ItemObservationTimeline.Builder(); builder.Observe(new ReplayFrame { Items = new[] { a } }); builder.Observe(new ReplayFrame { T = .2, Items = new[] { b } });
            Check(builder.Build(true).TrySample(a.Key, .1, out var left, out var right, out float mix) && ReferenceEquals(a, left) && ReferenceEquals(b, right) && Math.Abs(mix - .5) < .00001);
            b.IdleAnimation = Animation(CreatureIdleAnimationRules.EarlyWormInHand);
            Check(!ItemObservationTimeline.CanInterpolateObservation(a, b));
        });
        test("worm rebase shifts only clocks and shares immutable local spin anchors", () =>
        {
            var animation = Animation(CreatureIdleAnimationRules.EarlyWormWalk);
            var item = new ItemFrame { Key = "worm", ItemId = 184, SampleTimeKnown = true, SampleTime = 10,
                ClockMode = CreatureReplayRules.IdleClock, IdleAnimation = animation };
            var shifted = ItemAnimationTimeline.Rebase(item, 100);
            Check(shifted.SampleTime == -90 && shifted.IdleAnimation!.AnchorTime == -90 && item.SampleTime == 10 && animation.AnchorTime == 10);
            Check(ReferenceEquals(shifted.IdleAnimation!.SpinRotation, animation.SpinRotation) && ReferenceEquals(shifted.Pose, item.Pose));
            Check(Math.Abs(CreatureIdleAnimationRules.SampleTime(shifted.IdleAnimation, -89.5) - CreatureIdleAnimationRules.SampleTime(animation, 10.5)) < .00001);
            var packet = item.WithObservation(20, CreatureReplayRules.ActiveClock);
            Check(packet.SampleTime == 20 && packet.ClockMode == 0 && item.SampleTime == 10 && ReferenceEquals(packet.IdleAnimation, item.IdleAnimation) && ReferenceEquals(packet.Pose, item.Pose));
        });
        test("worm reference actions survive independently decoded reverse pages and negative opening anchors", () => InRoot(root =>
        {
            const double origin = 1000.125;
            var walk = Animation(CreatureIdleAnimationRules.EarlyWormWalk); walk.AnchorTime = origin - .75;
            var hand = Animation(CreatureIdleAnimationRules.EarlyWormInHand); hand.AnchorTime = origin + 1;
            var cooked = Animation(CreatureIdleAnimationRules.EarlyWormCooked); cooked.AnchorTime = origin + 3;
            var frames = new List<ReplayFrame>(); ItemFrame? item = null;
            for (int i = 0; i <= 80; i++)
            {
                double time = origin + i / 20d;
                if (i % 4 == 0)
                    item = new ItemFrame { Key = "worm", ItemId = 184, SampleTimeKnown = true, SampleTime = time,
                        SampleDiscontinuity = i == 20 || i == 60, ClockMode = i < 60 ? CreatureReplayRules.ActiveClock : CreatureReplayRules.IdleClock,
                        IdleAnimation = i < 20 ? walk : i < 60 ? hand : cooked,
                        Pose = new ObjectPose { Position = new[] { i / 20f, 0f, 0f } } };
                frames.Add(new ReplayFrame { T = time, Items = new[] { item! } });
            }
            var writer = new FullReplayWriter(root, new ReplayHeader { Scene = "Level_WormReference" },
                new ContinuousReplayOptions { SegmentSeconds = .4, SegmentFrameLimit = 9 });
            foreach (var frame in frames) Check(writer.TryEnqueue(frame));
            var result = writer.CompleteAsync().GetAwaiter().GetResult(); Check(result.Status == "completed");
            var info = FullReplayArchive.ReadInfo(result.FilePath); Check(info.Header.Schema == ReplayRules.CurrentSchema && info.Version == 4 && info.Pages.Length >= 8);
            foreach (var page in info.Pages.AsEnumerable().Reverse())
            {
                var decoded = FullReplayArchive.ReadPage(result.FilePath, info, page.Index);
                foreach (var frame in decoded.Frames)
                {
                    int index = (int)Math.Round(frame.T * 20); var expected = frames[index].Items[0]; var actual = frame.Items[0];
                    Check(actual.SampleTime == expected.SampleTime - origin && actual.SampleDiscontinuity == expected.SampleDiscontinuity);
                    Check(actual.IdleAnimation!.Clip == expected.IdleAnimation!.Clip && actual.IdleAnimation.SpinSpeed == expected.IdleAnimation.SpinSpeed);
                    Check(actual.IdleAnimation.SpinRotation!.SequenceEqual(expected.IdleAnimation.SpinRotation!));
                    double phase = CreatureIdleAnimationRules.SampleTime(actual.IdleAnimation, frame.T);
                    double original = CreatureIdleAnimationRules.SampleTime(expected.IdleAnimation, frames[index].T);
                    Check(Math.Abs(phase - original) < .0000001);
                }
            }
            Check(walk.AnchorTime == origin - .75 && hand.AnchorTime == origin + 1 && cooked.AnchorTime == origin + 3);
        }));
    }
    private static CreatureIdleAnimation Animation(string clip) => new()
    { Clip = clip, AnchorTime = 10, ClipTime = clip == CreatureIdleAnimationRules.EarlyWormCooked ? 0 : .25,
        Speed = clip == CreatureIdleAnimationRules.EarlyWormCooked ? 0 : 1, SpinRotation = new[] { 0f, 0f, 0f, 1f },
        SpinSpeed = clip == CreatureIdleAnimationRules.EarlyWormCooked ? 0 : -50 };
    private static string Json(object value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static void Check(bool value) { if (!value) throw new Exception("Worm native animation storage assertion failed."); }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } catch (JsonException) { return; } throw new Exception("Invalid worm reference data was accepted."); }
    private static void InRoot(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "PeakWormReference-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "PeakWormReference-"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected worm reference test directory.");
            Directory.Delete(root, true);
        }
    }
}
