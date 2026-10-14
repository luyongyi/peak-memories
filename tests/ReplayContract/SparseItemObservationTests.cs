using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class SparseItemObservationTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("five hertz item movement interpolates true observations through thirty and sixty hertz held wrappers", () =>
        {
            var a = Item(0, 0); var b = Item(.2, 2);
            var frames = Enumerable.Range(0, 13).Select(i => Frame(i / 60d, i == 12 ? b : a)).ToArray();
            var index = Index(true, frames);
            for (int i = 1; i < 12; i++)
            {
                double time = i / 60d;
                Check(index.TrySample(a.Key, time, out var left, out var right, out float mix));
                Check(ReferenceEquals(a, left) && ReferenceEquals(b, right) && Math.Abs(mix - time / .2) < .00001);
            }
            Check(index.TrySample(a.Key, .05, out _, out _, out float backwards) && Math.Abs(backwards - .25) < .00001);
        });
        test("five hertz creature movement now has a bounded interpolating future bracket", () =>
        {
            CreatureReplayFrame Creature(double time, float position) => new()
            { Key = "raven", Kind = "voice-thief", Resource = "voice-thief", SampleTimeKnown = true, SampleTime = time,
                Pose = new ObjectPose { Position = new[] { position, 0f, 0f } } };
            var a = Creature(0, 0); var b = Creature(.2333, 2);
            var builder = new CreatureObservationTimeline.Builder();
            builder.Observe(new ReplayFrame { T = 0, Creatures = new[] { a } });
            builder.Observe(new ReplayFrame { T = .2333, Creatures = new[] { b } });
            Check(builder.Build(true).TrySample(a.Key, .11665, out var left, out var right, out float mix));
            Check(ReferenceEquals(a, left) && ReferenceEquals(b, right) && Math.Abs(mix - .5) < .00001);
        });
        test("real scene index keeps current thirty hertz light metadata separate from five hertz item movement", () =>
        {
            var a = Item(0, 0); var b = Item(.2, 2); var sceneItems = new ItemFrame[7][];
            for (int scene = 0; scene <= 6; scene++)
            {
                var motion = scene == 6 ? b : a;
                sceneItems[scene] = new[] { new ItemFrame { Key = motion.Key, ItemId = motion.ItemId, Pose = motion.Pose,
                    SampleTimeKnown = true, SampleTime = motion.SampleTime, Lights = new[] { new NativeLightFrame { Intensity = scene + 1 } } } };
            }
            var clip = new ReplayClip { Complete = true };
            for (int global = 0; global <= 12; global++)
                clip.Frames.Add(new ReplayFrame { T = global / 60d, SceneSampleTimeKnown = true,
                    SceneSampleTime = global / 2 / 30d, Items = sceneItems[global / 2] });
            using var timeline = new MemoryReplayTimeline(clip);
            Check(timeline.TrySample(.05, out var sample));
            Check(Math.Abs(sample.Scene.Mix - .5) < .00001 && sample.Scene.Left.Items[0].Lights![0].Intensity == 2);
            Check(sample.Joints!.Items.TrySample(a.Key, .05, out var left, out var right, out float itemMix));
            Check(ReferenceEquals(left.Pose, a.Pose) && ReferenceEquals(right.Pose, b.Pose) && Math.Abs(itemMix - .25) < .00001);
            Check(left.Lights![0].Intensity == 1 && sample.Scene.Left.Items[0].SampleTime == 0);
            // Reverse selection must restore the earlier metadata independently
            // of the sparse motion bracket's shared first packet.
            Check(timeline.TrySample(.025, out sample) && sample.Scene.Left.Items[0].Lights![0].Intensity == 1);
        });
        test("sparse item motion respects event holder visibility topology teleport and world cuts", () =>
        {
            for (int boundary = 0; boundary < 13; boundary++)
            {
                var a = Item(0, 0); var b = Item(.2, 2); var first = Frame(0, a); var second = Frame(.2, b);
                switch (boundary)
                {
                    case 0: b.State = 1; break;
                    case 1: b.HolderId = "new-holder"; break;
                    case 2: b.Primary = true; break;
                    case 3: b.Secondary = true; break;
                    case 4: b.Used = true; break;
                    case 5: b.Lit = true; break;
                    case 6: b.RevealBackpackContents = true; break;
                    case 7: b.Pose.Active = false; break;
                    case 8: b.Pose.Nodes[0].Visible = false; break;
                    case 9: b.Pose.Nodes[0].Path = "new-node"; break;
                    case 10: b.Pose.Position[0] = 21; break;
                    case 11: second.World.Segment = 1; break;
                    case 12: second.World.ActiveMapObjects = new[] { true }; break;
                }
                var index = Index(true, first, second);
                Check(index.TrySample(a.Key, .1, out var left, out var right, out float mix));
                Check(ReferenceEquals(a, left) && ReferenceEquals(left, right) && mix == 0);
                Check(index.TrySample(a.Key, .2, out left, out _, out _) && ReferenceEquals(b, left));
            }
        });
        test("sparse item disappearance and held world boundary survive until next actual read", () =>
        {
            var a = Item(0, 0); var b = Item(.2, 2);
            var absent = Index(true, Frame(0, a), Frame(.05), Frame(.1, a), Frame(.2, b));
            Check(absent.TrySample(a.Key, .15, out var left, out var right, out float mix) && ReferenceEquals(left, right) && mix == 0);
            var boundary = Frame(.1, a); boundary.World.Segment = 1; var after = Frame(.2, b); after.World.Segment = 1;
            var world = Index(true, Frame(0, a), boundary, after);
            Check(world.TrySample(a.Key, .15, out left, out right, out mix) && ReferenceEquals(left, right) && mix == 0);
        });
        test("same state native item event cuts its exact observation then permits smooth later action samples", () =>
        {
            var a = Item(0, 0); var edge = Item(.2, 2); edge.SampleDiscontinuity = true; var after = Item(.4, 3);
            var index = Index(true, Frame(0, a), Frame(.2, edge), Frame(.4, after));
            Check(index.TrySample(a.Key, .1, out var left, out var right, out float mix) && ReferenceEquals(left, right) && mix == 0);
            Check(index.TrySample(a.Key, .3, out left, out right, out mix) && ReferenceEquals(edge, left) && ReferenceEquals(after, right) && Math.Abs(mix - .5) < .00001);
            var writer = new ReplayDeltaCodec.Writer(); var tree = new ReplayDeltaCodec.Reader(); var stream = new ReplayDeltaCodec.StreamReader(25);
            foreach (var packet in new[] { a, edge, after })
            {
                string encoded = Json(writer.Encode(Frame(packet.SampleTime, packet)));
                Check(tree.Decode(JObject.Parse(encoded)).Items[0].SampleDiscontinuity == packet.SampleDiscontinuity);
                Check(stream.DecodeRecord(encoded)!.Items[0].SampleDiscontinuity == packet.SampleDiscontinuity);
            }
            var shifted = ItemAnimationTimeline.Rebase(edge, 10); Check(shifted.SampleDiscontinuity && shifted.SampleTime == -9.8);
        });
        test("same state native creature events cannot be blended early or inherited into later deltas", () =>
        {
            CreatureReplayFrame Creature(double time, float position, bool boundary = false) => new()
            { Key = "raven", Kind = "voice-thief", Resource = "voice-thief", SampleTimeKnown = true, SampleTime = time,
                SampleDiscontinuity = boundary, Pose = new ObjectPose { Position = new[] { position, 0f, 0f } } };
            var a = Creature(0, 0); var edge = Creature(.2, 2, true); var after = Creature(.4, 3);
            var builder = new CreatureObservationTimeline.Builder();
            var writer = new ReplayDeltaCodec.Writer(); var stream = new ReplayDeltaCodec.StreamReader(25);
            foreach (var packet in new[] { a, edge, after })
            {
                var frame = new ReplayFrame { T = packet.SampleTime, Creatures = new[] { packet } }; builder.Observe(frame);
                Check(stream.DecodeRecord(Json(writer.Encode(frame)))!.Creatures[0].SampleDiscontinuity == packet.SampleDiscontinuity);
            }
            var index = builder.Build(true);
            Check(index.TrySample(a.Key, .1, out var left, out var right, out float mix) && ReferenceEquals(left, right) && mix == 0);
            Check(index.TrySample(a.Key, .3, out left, out right, out mix) && ReferenceEquals(edge, left) && ReferenceEquals(after, right) && Math.Abs(mix - .5) < .00001);
            Check(CreatureReplayRules.Rebase(edge, 10).SampleDiscontinuity);
            Reject(() => new ReplayDeltaCodec.Writer(24).Encode(new ReplayFrame { T = .2, Creatures = new[] { edge } }));
        });
        test("standalone event flags cannot bypass complete observation clocks in either decoder", () =>
        {
            var a = Item(0, 0); a.SampleTimeKnown = false;
            var token = JObject.FromObject(new ReplayDeltaCodec.Writer().Encode(Frame(0, a)));
            token["Items"]![0]!["SampleDiscontinuity"] = true;
            Reject(() => new ReplayDeltaCodec.Reader().Decode(token)); Reject(() => new ReplayDeltaCodec.StreamReader(25).DecodeRecord(Json(token)));
        });
        test("dormant and native clip item checkpoints never request speculative future samples", () =>
        {
            foreach (int mode in new[] { CreatureReplayRules.SleepingClock, CreatureReplayRules.IdleClock })
            {
                var a = Item(-300, 0); a.ClockMode = mode;
                var index = Index(false, Frame(50, a), Frame(51, a));
                Check(index.CanSample(new[] { a }, 50.99));
                Check(index.TrySample(a.Key, 50.99, out var left, out var right, out float mix) && ReferenceEquals(a, left) && ReferenceEquals(a, right) && mix == 0);
            }
        });
        test("active item future readiness is bounded and long silence does not invent movement", () =>
        {
            var a = Item(.9, 0); var index = Index(false, Frame(.9, a), Frame(1, a));
            Check(!index.CanSample(new[] { a }, .99));
            var b = Item(1.1, 2); var joined = Index(false, Frame(.9, a), Frame(1, a), Frame(1.1, b));
            Check(joined.CanSample(new[] { a }, .99));
            var late = Item(90, 3); var silence = Index(true, Frame(89, a), Frame(90, late));
            Check(silence.TrySample(a.Key, 89.99, out var left, out var right, out float mix) && ReferenceEquals(a, left) && ReferenceEquals(left, right) && mix == 0);
        });
        test("five hertz item stopping interpolates its measured final movement then holds the terminal checkpoint", () =>
        {
            var a = Item(0, 0); var stopped = Item(.2, 2); stopped.ClockMode = CreatureReplayRules.SleepingClock;
            var index = Index(true, Frame(0, a), Frame(.2, stopped), Frame(1, stopped));
            Check(index.TrySample(a.Key, .1, out var left, out var right, out float mix) && ReferenceEquals(a, left) && ReferenceEquals(stopped, right) && Math.Abs(mix - .5) < .00001);
            Check(index.TrySample(a.Key, .3, out left, out right, out mix) && ReferenceEquals(stopped, left) && ReferenceEquals(stopped, right) && mix == 0);
            stopped.SampleDiscontinuity = true;
            Check(!ItemObservationTimeline.CanInterpolateObservation(a, stopped));
        });
        test("moving native clip ends smoothly at the same idle reference while clip and holder changes cut", () =>
        {
            var clock = new CreatureIdleAnimation { Clip = CreatureIdleAnimationRules.EarlyWormWalk, Speed = 1 };
            var a = Item(0, 0); a.ItemId = 184; a.IdleAnimation = clock;
            var stopped = Item(.2, 2); stopped.ItemId = 184; stopped.IdleAnimation = clock; stopped.ClockMode = CreatureReplayRules.IdleClock;
            var index = Index(true, Frame(0, a), Frame(.2, stopped), Frame(1, stopped));
            Check(index.TrySample(a.Key, .1, out var left, out var right, out float mix) && ReferenceEquals(a, left) && ReferenceEquals(stopped, right) && Math.Abs(mix - .5) < .00001);
            stopped.IdleAnimation = new CreatureIdleAnimation { Clip = CreatureIdleAnimationRules.EarlyWormInHand, Speed = 1 };
            Check(!ItemObservationTimeline.CanInterpolateObservation(a, stopped));
            stopped.IdleAnimation = clock; stopped.HolderId = "new-holder";
            Check(!ItemObservationTimeline.CanInterpolateObservation(a, stopped));
        });
        test("terminal item intervals do not interpolate long gaps or anticipate a later wake event", () =>
        {
            var a = Item(0, 0); var stopped = Item(.4, 2); stopped.ClockMode = CreatureReplayRules.SleepingClock;
            var longGap = Index(true, Frame(0, a), Frame(.4, stopped));
            Check(longGap.TrySample(a.Key, .2, out var left, out var right, out float mix) && ReferenceEquals(left, right) && mix == 0);
            var awake = Item(.6, 3); awake.SampleDiscontinuity = true;
            var wake = Index(true, Frame(.4, stopped), Frame(.6, awake));
            Check(wake.TrySample(a.Key, .5, out left, out right, out mix) && ReferenceEquals(stopped, left) && ReferenceEquals(left, right) && mix == 0);
            Check(wake.TrySample(a.Key, .6, out left, out _, out _) && ReferenceEquals(awake, left));
        });
        test("schema twenty five item clocks roundtrip both readers and leave retained packets immutable", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(); var tree = new ReplayDeltaCodec.Reader(); var stream = new ReplayDeltaCodec.StreamReader(ReplayRules.CurrentSchema);
            var a = Item(0, 0); var b = Item(.2, 2); var rest = Item(.4, 3); rest.ClockMode = CreatureReplayRules.SleepingClock;
            var retained = new List<(ItemFrame Frame, string Json)>();
            foreach (var frame in new[] { Frame(0, a), Frame(.1, a), Frame(.2, b), Frame(.4, rest), Frame(60, rest) })
            {
                string encoded = Json(writer.Encode(frame)); var decoded = tree.Decode(JObject.Parse(encoded)); var streamed = stream.DecodeRecord(encoded)!;
                Check(Json(decoded.Items) == Json(frame.Items) && Json(streamed.Items) == Json(frame.Items));
                retained.Add((streamed.Items[0], Json(streamed.Items[0])));
            }
            foreach (var saved in retained) Check(Json(saved.Frame) == saved.Json);
        });
        test("schema ten through twenty four preserve timeless item bytes and original reservation", () =>
        {
            var a = Item(0, 0); a.SampleTimeKnown = false; var frame = Frame(0, a); long reservation = RollingBuffer.Estimate(a);
            foreach (int schema in Enumerable.Range(10, 15))
            {
                string encoded = Json(new ReplayDeltaCodec.Writer(schema).Encode(frame)); var token = JObject.Parse(encoded);
                Check(token["Items"]![0]!["SampleTimeKnown"] == null && token["Items"]![0]!["SampleTime"] == null && token["Items"]![0]!["ClockMode"] == null);
                var tree = new ReplayDeltaCodec.Reader(schema).Decode(token).Items[0];
                var stream = new ReplayDeltaCodec.StreamReader(schema).DecodeRecord(encoded)!.Items[0];
                Check(!tree.SampleTimeKnown && !stream.SampleTimeKnown && RollingBuffer.Estimate(tree) == reservation);
            }
            a.SampleTimeKnown = true; Check(RollingBuffer.Estimate(a) == reservation + 24);
            Reject(() => new ReplayDeltaCodec.Writer(24).Encode(frame));
        });
        test("item baseline clocks are all absent or have complete known and time fields", () =>
        {
            var a = Item(0, 0); var token = JObject.FromObject(new ReplayDeltaCodec.Writer().Encode(Frame(0, a)));
            Check(token["Items"]![0]!["ClockMode"] == null);
            ((JObject)token["Items"]![0]!).Remove("SampleTimeKnown");
            Reject(() => new ReplayDeltaCodec.Reader().Decode(token));
            Reject(() => new ReplayDeltaCodec.StreamReader(ReplayRules.CurrentSchema).DecodeRecord(Json(token)));
            a.SampleTimeKnown = false;
            token = JObject.FromObject(new ReplayDeltaCodec.Writer().Encode(Frame(0, a)));
            Check(token["Items"]![0]!["SampleTimeKnown"] == null && token["Items"]![0]!["SampleTime"] == null);
            Check(!new ReplayDeltaCodec.Reader().Decode(token).Items[0].SampleTimeKnown);
        });
        test("item observation validation rejects corrupt clocks and indices", () =>
        {
            var a = Item(0, 0); a.SampleTime = double.NaN; Reject(() => ReplayRules.Validate(Frame(0, a), -1));
            a = Item(0, 0); a.ClockMode = 3; Reject(() => ReplayRules.Validate(Frame(0, a), -1));
            a = Item(1, 0); Reject(() => ReplayRules.Validate(Frame(.5, a), -1));
            a = Item(.1, 0); a.SampleTimeKnown = false; Reject(() => ReplayRules.Validate(Frame(.5, a), -1));
            var builder = new ItemObservationTimeline.Builder(); builder.Observe(Frame(1, Item(1, 0)));
            Reject(() => builder.Observe(Frame(2, Item(.9, 1))));
            Reject(() => Index(true, Frame(.1, Item(.2, 0))));
        });
        test("item observation rebase retains shared pose and negative opening clocks with bounded tables", () =>
        {
            var a = Item(10, 1); var source = Frame(100, a); var rebaser = new ContinuousReplayRebaser(100);
            var first = rebaser.Apply(source); var second = rebaser.Apply(Frame(101, a));
            Check(first.Items[0].SampleTime == -90 && a.SampleTime == 10 && ReferenceEquals(first.Items[0].Pose, a.Pose));
            Check(ReferenceEquals(first.Items[0], second.Items[0]));
            var buffer = new RollingBuffer(10000000, 100); buffer.Add(source); buffer.Add(Frame(101, a));
            var clip = buffer.Snapshot(new ReplayHeader { Scene = "Level_Sparse" }, DateTime.UtcNow);
            Check(clip.Frames[0].T == 0 && clip.Frames[0].Items[0].SampleTime <= 0 && ReferenceEquals(clip.Frames[0].Items[0], clip.Frames[1].Items[0]));
        });
        test("cold sparse item seek reads only target and bounded successor with smooth reverse seeks", () =>
        {
            var reads = new List<int>();
            ReplayClip Page(int page)
            {
                lock (reads) reads.Add(page);
                var clip = new ReplayClip { Complete = true };
                for (int i = 0; i < (page == 4 ? 11 : 10); i++)
                {
                    double time = page + i / 10d; double observed = Math.Round(Math.Floor((time + .00001) * 5) / 5, 6);
                    clip.Frames.Add(Frame(time, Item(observed, (float)observed)));
                }
                return clip;
            }
            using var timeline = new PagedReplayTimeline(new ReplayHeader(), 5, 55, Array.Empty<string>(),
                Enumerable.Range(0, 5).Select(p => new ReplayPageRange(p, p == 4 ? 5 : p + .9)).ToArray(), (p, _) => Page(p));
            Ready(timeline, .5); lock (reads) reads.Clear();
            var sample = Ready(timeline, 3.95);
            Check(sample.Joints!.Items.TrySample("item:sparse", 3.95, out var left, out var right, out float mix));
            Check(Math.Abs(left.SampleTime - 3.8) < .00001 && Math.Abs(right.SampleTime - 4) < .00001 && Math.Abs(mix - .75) < .00001);
            lock (reads) Check(reads.Contains(3) && reads.Contains(4) && !reads.Any(p => p < 3));
            sample = Ready(timeline, 3.85);
            Check(sample.Joints!.Items.TrySample("item:sparse", 3.85, out left, out right, out mix) && Math.Abs(mix - .25) < .00001);
        });
    }
    private static ItemFrame Item(double time, float x) => new()
    { Key = "item:sparse", ItemId = 1, SampleTimeKnown = true, SampleTime = time,
        Pose = new ObjectPose { Position = new[] { x, 0f, 0f }, Nodes = new[] { new NodePose("joint", new float[3], new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f }) } } };
    private static ReplayFrame Frame(double time, params ItemFrame[] items) => new() { T = time, Items = items };
    private static ItemObservationTimeline Index(bool end, params ReplayFrame[] frames)
    { var builder = new ItemObservationTimeline.Builder(); foreach (var frame in frames) builder.Observe(frame); return builder.Build(end); }
    private static ReplayTimelineSample Ready(IReplayTimeline timeline, double time)
    {
        var until = Stopwatch.StartNew();
        while (until.Elapsed.TotalSeconds < 10) { if (timeline.TrySample(time, out var sample)) return sample; Thread.Sleep(1); }
        throw new Exception("Bounded sparse seek timed out: " + timeline.LoadingDiagnostic);
    }
    private static string Json(object value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static void Check(bool value) { if (!value) throw new Exception("Sparse item observation assertion failed."); }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } catch (JsonException) { return; } throw new Exception("Invalid sparse item data was accepted."); }
}
