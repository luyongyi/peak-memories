using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class CreatureObservationClockTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("creature active observations interpolate across repeated global capture frames", () =>
        {
            var a = State(0); var b = State(.1, 1);
            var index = Index(true, Frame(0, a), Frame(.02, a), Frame(.04, a), Frame(.08, a), Frame(.1, b));
            Check(index.TrySample(a.Key, .05, out var left, out var right, out float mix));
            Check(ReferenceEquals(a, left) && ReferenceEquals(b, right) && Math.Abs(mix - .5) < .00001);
        });
        test("creature active clock never interpolates across long capture gaps", () =>
        {
            var a = State(0, mode: CreatureReplayRules.ActiveClock); var b = State(.4, 1, CreatureReplayRules.ActiveClock);
            var index = Index(true, Frame(0, a), Frame(.4, b));
            Check(index.TrySample(a.Key, .1, out var left, out var right, out float mix) && ReferenceEquals(left, right) && mix == 0);
        });
        test("creature idle clocks hold checkpoints even when supplied dense different geometry", () =>
        {
            var a = State(0, mode: CreatureReplayRules.IdleClock); var b = State(.1, 1, CreatureReplayRules.IdleClock);
            var index = Index(true, Frame(0, a), Frame(.1, b));
            Check(index.TrySample(a.Key, .05, out var left, out var right, out float mix));
            Check(ReferenceEquals(a, left) && ReferenceEquals(a, right) && mix == 0);
        });
        test("creature unchanged successful observations retain clocks before first motion", () =>
        {
            var a = State(0, mode: CreatureReplayRules.ActiveClock);
            var finalStill = State(10, mode: CreatureReplayRules.ActiveClock); finalStill.Pose = a.Pose; finalStill.Line = a.Line;
            var moved = State(10.04, 1, CreatureReplayRules.ActiveClock);
            var index = Index(true, Frame(0, a), Frame(10, finalStill), Frame(10.04, moved));
            Check(index.TrySample(a.Key, 10.02, out var left, out var right, out float mix));
            Check(ReferenceEquals(finalStill, left) && ReferenceEquals(moved, right) && Math.Abs(mix-.5) < .00001);
            var writer = new ReplayDeltaCodec.Writer(20); writer.Encode(Frame(0, a));
            var delta = JObject.FromObject(writer.Encode(Frame(10, finalStill)))["Creatures"]![0]!;
            Check(delta["SampleTime"] != null && delta["Pose"] == null && delta["Line"] == null && delta["Visuals"] == null);
        });
        test("creature independent page baseline retains actual observation before page start", () =>
        {
            var a = State(.95); var b = State(1.05, 1); var index = Index(true, Frame(1, a), Frame(1.05, b));
            Check(index.TrySample(a.Key, 1, out var left, out var right, out float mix));
            Check(ReferenceEquals(a, left) && ReferenceEquals(b, right) && Math.Abs(mix - .5) < .00001);
        });
        test("creature sleeping checkpoint seeks without birth history or speculative successor", () =>
        {
            var a = State(-300, mode: CreatureReplayRules.SleepingClock);
            var index = Index(false, Frame(50, a), Frame(51, a));
            Check(index.CanSample(new[] { a }, 50.99));
            Check(index.TrySample(a.Key, 50.99, out var left, out var right, out float mix) && ReferenceEquals(a, left) && ReferenceEquals(a, right) && mix == 0);
        });
        test("creature long silence does not turn wake into seconds of invented interpolation", () =>
        {
            var a = State(0); var b = State(90, 1); var index = Index(true, Frame(89, a), Frame(90, b));
            Check(index.TrySample(a.Key, 89.99, out var left, out var right, out _) && ReferenceEquals(a, left) && ReferenceEquals(a, right));
            Check(index.TrySample(a.Key, 90, out left, out _, out _) && ReferenceEquals(b, left));
        });
        test("creature mode state root child renderer and silk visibility changes are discrete cuts", () =>
        {
            for (int boundary = 0; boundary < 7; boundary++)
            {
                var a = State(0); var b = State(.1, 1);
                switch (boundary)
                {
                    case 0: b.ClockMode = CreatureReplayRules.IdleClock; break;
                    case 1: b.State = 1; break;
                    case 2: b.Pose.Active = false; break;
                    case 3: b.Pose.Nodes[0].Active = false; break;
                    case 4: b.Pose.Nodes[0].Visible = false; break;
                    case 5: b.Line!.Enabled = false; break;
                    case 6: b.Pose.Nodes[0].Path = "0:NewTopology"; break;
                }
                var index = Index(true, Frame(0, a), Frame(.1, b));
                Check(index.TrySample(a.Key, .05, out var left, out var right, out float mix) && ReferenceEquals(a, left) && ReferenceEquals(left, right) && mix == 0);
                Check(index.TrySample(a.Key, .1, out left, out _, out _) && ReferenceEquals(b, left));
            }
        });
        test("creature same key reappearance cannot interpolate across a short absent lifetime", () =>
        {
            var a = State(0); var b = State(.1, 1);
            var index = Index(true, Frame(0, a), Frame(.05), Frame(.1, b));
            Check(index.TrySample(a.Key, .025, out var left, out var right, out float mix));
            Check(ReferenceEquals(a, left) && ReferenceEquals(a, right) && mix == 0);
            Check(index.TrySample(a.Key, .1, out left, out _, out _) && ReferenceEquals(b, left));
        });
        test("creature same-clock reappearance preserves its absence cut until the next new observation", () =>
        {
            var a = State(0); var b = State(.04, 1);
            var index = Index(true, Frame(0, a), Frame(.02), Frame(.03, a), Frame(.04, b));
            Check(index.TrySample(a.Key, .01, out var left, out var right, out float mix));
            Check(ReferenceEquals(a, left) && ReferenceEquals(a, right) && mix == 0);
            Check(index.TrySample(a.Key, .035, out left, out right, out mix) && ReferenceEquals(a, left) && ReferenceEquals(a, right) && mix == 0);
        });
        test("creature active brackets request only a bounded missing successor", () =>
        {
            var a = State(.9, mode: CreatureReplayRules.ActiveClock); var index = Index(false, Frame(.9, a), Frame(1, a));
            Check(!index.CanSample(new[] { a }, .99));
            var b = State(1.1, 1, CreatureReplayRules.ActiveClock); var joined = Index(false, Frame(.9, a), Frame(1, a), Frame(1.1, b));
            Check(joined.CanSample(new[] { a }, .99));
            var sleeping = State(.99, mode: CreatureReplayRules.SleepingClock);
            Check(index.CanSample(new[] { sleeping }, .99));
        });
        test("creature hanging idle checkpoint at fresh page boundary never requires a later observation", () =>
        {
            var idle = State(.98, mode: CreatureReplayRules.IdleClock); var index = Index(false, Frame(.98, idle), Frame(1, idle));
            Check(index.CanSample(new[] { idle }, .99));
            Check(index.TrySample(idle.Key, .99, out var left, out var right, out float mix) && ReferenceEquals(idle, left) && ReferenceEquals(idle, right) && mix == 0);
        });
        test("creature schema twenty tree and stream preserve observation clocks modes and immutable deltas", () =>
        {
            var a = State(0); var b = State(.2, 1); var c = State(.23, 2, CreatureReplayRules.ActiveClock); c.State = 1;
            var writer = new ReplayDeltaCodec.Writer(20); var tree = new ReplayDeltaCodec.Reader(20); var stream = new ReplayDeltaCodec.StreamReader(20);
            var retained = new List<(CreatureReplayFrame Frame, string Json)>();
            foreach (var frame in new[] { Frame(0, a), Frame(.1, a), Frame(.2, b), Frame(.23, c) })
            {
                string encoded = Json(writer.Encode(frame)); var decoded = tree.Decode(JObject.Parse(encoded)); var streamed = stream.DecodeRecord(encoded)!;
                Check(Json(decoded.Creatures) == Json(frame.Creatures) && Json(streamed.Creatures) == Json(frame.Creatures));
                retained.Add((streamed.Creatures[0], Json(streamed.Creatures[0])));
            }
            foreach (var saved in retained) Check(Json(saved.Frame) == saved.Json);
        });
        test("creature schema fifteen through nineteen remain timeless with original decoded reservation", () =>
        {
            var a = State(0); a.SampleTimeKnown = false; a.ClockMode = CreatureReplayRules.ActiveClock;
            var frame = Frame(0, a); long oldBytes = CreatureReplayRules.Estimate(a);
            foreach (int schema in Enumerable.Range(15, 5))
            {
                string encoded = Json(new ReplayDeltaCodec.Writer(schema).Encode(frame)); var token = JObject.Parse(encoded);
                Check(token["Creatures"]![0]!["SampleTime"] == null && token["Creatures"]![0]!["SampleTimeKnown"] == null && token["Creatures"]![0]!["ClockMode"] == null);
                var tree = new ReplayDeltaCodec.Reader(schema).Decode(token).Creatures[0];
                var stream = new ReplayDeltaCodec.StreamReader(schema).DecodeRecord(encoded)!.Creatures[0];
                Check(!tree.SampleTimeKnown && !stream.SampleTimeKnown && tree.ClockMode == CreatureReplayRules.ActiveClock);
                Check(CreatureReplayRules.Estimate(tree) == oldBytes);
                Check(!Index(true, Frame(0, tree)).TrySample(tree.Key, 0, out _, out _, out _));
            }
            a.SampleTimeKnown = true;
            Check(CreatureReplayRules.Estimate(a) == oldBytes + 24);
            Reject(() => new ReplayDeltaCodec.Writer(19).Encode(frame));
        });
        test("creature optional zero active clock mode remains readable while clock fields stay complete", () =>
        {
            var a = State(0, mode: CreatureReplayRules.ActiveClock);
            var token = JObject.FromObject(new ReplayDeltaCodec.Writer(20).Encode(Frame(0, a)));
            ((JObject)token["Creatures"]![0]!).Remove("ClockMode"); string encoded = Json(token);
            Check(new ReplayDeltaCodec.Reader(20).Decode(token).Creatures[0].ClockMode == CreatureReplayRules.ActiveClock);
            Check(new ReplayDeltaCodec.StreamReader(20).DecodeRecord(encoded)!.Creatures[0].ClockMode == CreatureReplayRules.ActiveClock);
            ((JObject)token["Creatures"]![0]!).Remove("SampleTimeKnown");
            Reject(() => new ReplayDeltaCodec.Reader(20).Decode(token));
            Reject(() => new ReplayDeltaCodec.StreamReader(20).DecodeRecord(Json(token)));
        });
        test("creature timeless schema twenty baselines omit optional clocks without expanding static checkpoints", () =>
        {
            var a = State(0, mode: CreatureReplayRules.ActiveClock); a.SampleTimeKnown = false;
            var token = JObject.FromObject(new ReplayDeltaCodec.Writer(20).Encode(Frame(0, a)));
            Check(token["Creatures"]![0]!["SampleTimeKnown"] == null && token["Creatures"]![0]!["SampleTime"] == null && token["Creatures"]![0]!["ClockMode"] == null);
            var decoded = new ReplayDeltaCodec.Reader(20).Decode(token).Creatures[0];
            var streamed = new ReplayDeltaCodec.StreamReader(20).DecodeRecord(Json(token))!.Creatures[0];
            Check(!decoded.SampleTimeKnown && !streamed.SampleTimeKnown && CreatureReplayRules.Estimate(decoded) == CreatureReplayRules.Estimate(a));
        });
        test("creature clock validation rejects nonfinite future backwards and unknown mode metadata", () =>
        {
            var a = State(0); a.SampleTime = double.NaN; Reject(() => CreatureReplayRules.Validate(a));
            a = State(0); a.ClockMode = 3; Reject(() => CreatureReplayRules.Validate(a));
            a = State(.1); a.SampleTimeKnown = false; Reject(() => CreatureReplayRules.Validate(a));
            a = State(1); Reject(() => ReplayRules.Validate(Frame(.5, a), -1));
            var builder = new CreatureObservationTimeline.Builder(); builder.Observe(Frame(1, State(1)));
            Reject(() => builder.Observe(Frame(2, State(.9))));
        });
        test("creature clip rebase preserves negative opening clocks and shared rig identity", () =>
        {
            var a = State(10); var b = CreatureReplayRules.Rebase(a, 100);
            Check(b.SampleTimeKnown && b.SampleTime == -90 && b.ClockMode == a.ClockMode && a.SampleTime == 10);
            Check(ReferenceEquals(a.Pose, b.Pose) && ReferenceEquals(a.Line, b.Line) && ReferenceEquals(a.Visuals, b.Visuals));
            var rebaser = new ContinuousReplayRebaser(100); var first = rebaser.Apply(Frame(100, a)); var second = rebaser.Apply(Frame(101, a));
            Check(first.Creatures[0].SampleTime == -90 && ReferenceEquals(first.Creatures[0], second.Creatures[0]));
            var source = State(100); var buffer = new RollingBuffer(10000000, 100);
            buffer.Add(Frame(100, source)); buffer.Add(Frame(101, source)); buffer.Add(Frame(102, source));
            var clip = buffer.Snapshot(new ReplayHeader { Scene = "Level_CreatureClock" }, DateTime.UtcNow);
            Check(clip.Frames[0].T == 0 && clip.Frames[0].Creatures[0].SampleTime <= 0);
            Check(ReferenceEquals(clip.Frames[0].Creatures[0], clip.Frames[clip.Frames.Count-1].Creatures[0]));
        });
        test("creature cold paged seek reads destination and one successor without birth pages", () =>
        {
            var reads = new List<int>();
            ReplayClip Page(int page)
            {
                lock (reads) reads.Add(page);
                var clip = new ReplayClip { Complete = true };
                for (int i = 0; i <= 10; i++)
                {
                    double t = page + i / 10d; double observed = i == 0 ? page - .1 : page + (i-1)/10d;
                    clip.Frames.Add(Frame(t, State(observed, (float)observed, CreatureReplayRules.ActiveClock)));
                }
                return clip;
            }
            using var timeline = new PagedReplayTimeline(new ReplayHeader(), 5, 55, Array.Empty<string>(),
                Enumerable.Range(0, 5).Select(p => new ReplayPageRange(p, p+1)).ToArray(), (p, _) => Page(p));
            Ready(timeline, .5); lock (reads) reads.Clear();
            var sample = Ready(timeline, 3.95);
            Check(sample.Joints!.Creatures.TrySample("creature:clock", 3.95, out var left, out var right, out float mix));
            Check(left.SampleTime <= 3.95 && right.SampleTime >= 3.95 && mix > 0);
            lock (reads) Check(!reads.Any(p => p == 0 || p == 1 || p == 2) && reads.Contains(3) && reads.Contains(4));
        });
        test("creature current schema shared archive pages decode independently in reverse order", () => InRoot(root =>
        {
            var frames = new List<ReplayFrame>(); CreatureReplayFrame? previous = null;
            for (int i = 0; i <= 180; i++)
            {
                if (i % 2 == 0) previous = State(i / 60d, i / 60f);
                frames.Add(Frame(i / 60d, previous!));
            }
            var writer = new FullReplayWriter(root, new ReplayHeader { Scene = "Level_CreatureClock" });
            foreach (var frame in frames) Check(writer.TryEnqueue(frame));
            var result = writer.CompleteAsync().GetAwaiter().GetResult(); Check(result.Status == "completed", result.Fault ?? result.Status);
            var info = FullReplayArchive.ReadInfo(result.FilePath); Check(info.Header.Schema == ReplayRules.CurrentSchema && info.Pages.Length >= 2);
            foreach (var page in info.Pages.AsEnumerable().Reverse())
            {
                var clip = FullReplayArchive.ReadPage(result.FilePath, info, page.Index);
                foreach (var frame in clip.Frames) Check(Json(frame.Creatures) == Json(frames[(int)Math.Round(frame.T*60)].Creatures));
                var index = new ActorJointTimeline(clip.Frames, page.Index == info.Pages.Length-1).Creatures;
                Check(index.TrySample("creature:clock", clip.Frames[0].T, out var left, out _, out _));
                Check(left.SampleTime <= clip.Frames[0].T);
            }
        }));
    }

    private static CreatureReplayFrame State(double time, float x = 0, int mode = CreatureReplayRules.ActiveClock) => new()
    {
        Key = "creature:clock", Kind = "spider", Resource = "spider-normal", SampleTimeKnown = true, SampleTime = time, ClockMode = mode,
        Pose = new ObjectPose { Active = true, Position = new[] { x, 0f, 0f },
            Nodes = new[] { new NodePose { Path = "0:Body", Active = true, Visible = true, Position = new[] { 0f, x, 0f } } } },
        Line = new CreatureReplayLine { Enabled = true, WorldSpace = true, Width = .08f, Points = new[] { 0f, 0f, 0f, x, 1f, 0f } },
    };
    private static ReplayFrame Frame(double time, params CreatureReplayFrame[] creatures) => new() { T = time, Creatures = creatures };
    private static CreatureObservationTimeline Index(bool end, params ReplayFrame[] frames)
    { var builder = new CreatureObservationTimeline.Builder(); foreach (var frame in frames) builder.Observe(frame); return builder.Build(end); }
    private static ReplayTimelineSample Ready(IReplayTimeline timeline, double time)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        { if (timeline.TrySample(time, out var sample)) return sample; Thread.Sleep(1); }
        throw new Exception("Creature clock seek timed out: " + timeline.LoadingDiagnostic);
    }
    private static void InRoot(Action<string> action)
    {
        string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "peak-creature-clock-"));
        string root = Path.GetFullPath(prefix + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); }
        finally { if (!root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected test root."); Directory.Delete(root, true); }
    }
    private static string Json(object value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static void Check(bool value, string reason = "") { if (!value) throw new Exception("Creature observation clock assertion failed: " + reason); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException || error is JsonException || error is ArgumentOutOfRangeException) { return; }
        throw new Exception("Invalid creature clock was accepted.");
    }
}
