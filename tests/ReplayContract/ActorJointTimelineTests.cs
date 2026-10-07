using System.Diagnostics;
using System.Numerics;
using PeakReplayLab;

internal static class ActorJointTimelineTests
{
    public static void Run(Action<string, Action> test)
    {
        test("low-rate joints interpolate on observation times instead of expanded frame times", () =>
        {
            var first = Node("./cloth#0", 0, 0); var last = Node("./cloth#0", .1, 10);
            var frames = new[] { Frame(0, first), Frame(.016, first), Frame(.033, first), Frame(.05, first), Frame(.083, first), Frame(.1, last) };
            var timeline = new ActorJointTimeline(frames, true);
            Check(timeline.TryActor("actor", .025, out var actor)); var cloth = actor.At(1);
            Close(cloth.Mix, .25); Close(Value(cloth), 2.5); Close(cloth.LeftTime, 0); Close(cloth.RightTime, .1);
            Check(timeline.TryActor("actor", .075, out actor)); Close(Value(actor.At(1)), 7.5);
        });
        test("unchanged real observations preserve the start of subsequent motion", () =>
        {
            var timeline = new ActorJointTimeline(new[] { Frame(0, Node("./cloth#0", 0, 0)),
                Frame(.1, Node("./cloth#0", .1, 0)), Frame(.2, Node("./cloth#0", .2, 10)) }, true);
            Check(timeline.TryActor("actor", .05, out var actor)); Close(Value(actor.At(1)), 0);
            Check(timeline.TryActor("actor", .15, out actor)); Close(Value(actor.At(1)), 5); Close(actor.At(1).LeftTime, .1);
        });
        test("fast root translation and rotation carry a static low-rate local sleeve every frame", () =>
        {
            var sleeve = Node("./cloth#0", 0, 1);
            var frames = new[] { RotatedFrame(0, 0, sleeve), RotatedFrame(.05, MathF.PI / 2, sleeve),
                RotatedFrame(.1, MathF.PI, Node("./cloth#0", .1, 1)) };
            var timeline = new ActorJointTimeline(frames, true);
            Check(timeline.TryActor("actor", .025, out var actor)); var root = actor.At(0); var cloth = actor.At(1);
            Vector3 actual = Point(root) + Vector3.Transform(Point(cloth), Rotation(root));
            Vector3 expected = new Vector3(.5f, 0, 0) + Vector3.Transform(Vector3.UnitX, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4));
            Close(Vector3.Distance(actual, expected), 0, .00001);
            Close(cloth.Mix, .25); Close(root.Mix, .5);
        });
        test("missing future low-rate observation buffers while a true recording endpoint holds", () =>
        {
            var frames = new[] { Frame(0, Node("./cloth#0", 0, 1)), Frame(.05, Node("./cloth#0", 0, 1)) };
            Check(!new ActorJointTimeline(frames, false).TryActor("actor", .025, out _));
            Check(new ActorJointTimeline(frames, true).TryActor("actor", .025, out var actor)); Close(Value(actor.At(1)), 1);
        });
        test("page opening baseline can predate frame time without shifting its held sample clock", () =>
        {
            var held = Node("./cloth#0", .95, 0);
            var timeline = new ActorJointTimeline(new[] { Frame(1, held), Frame(1.01, held), Frame(1.05, Node("./cloth#0", 1.05, 10)) }, true);
            Check(timeline.TryActor("actor", 1, out var actor)); Close(Value(actor.At(1)), 5);
            Close(actor.At(1).LeftTime, .95); Close(actor.At(1).RightTime, 1.05);
        });
        test("duplicate overlapping page frames do not change joint interpolation", () =>
        {
            var held = Node("./cloth#0", 0, 0);
            var timeline = new ActorJointTimeline(new[] { Frame(0, held), Frame(.05, held), Frame(.05, held), Frame(.1, Node("./cloth#0", .1, 10)) }, true);
            Check(timeline.TryActor("actor", .075, out var actor)); Close(Value(actor.At(1)), 7.5);
        });
        test("joint seek and reverse playback are deterministic", () =>
        {
            var timeline = new ActorJointTimeline(new[] { Frame(0, Node("./cloth#0", 0, 0)), Frame(.1, Node("./cloth#0", .1, 10)) }, true);
            Check(timeline.TryActor("actor", .025, out var early)); double value = Value(early.At(1));
            Check(timeline.TryActor("actor", .09, out _)); Check(timeline.TryActor("actor", .025, out var again));
            Close(Value(again.At(1)), value);
        });
        test("capture stall keeps the previous pose until the real post-stall sample", () =>
        {
            var timeline = new ActorJointTimeline(new[] { Frame(0, Node("./cloth#0", 0, 0)), Frame(1, Node("./cloth#0", 1, 10)) }, true);
            Check(timeline.TryActor("actor", .5, out var actor)); Close(Value(actor.At(1)), 0);
            Check(timeline.TryActor("actor", 1, out actor)); Close(Value(actor.At(1)), 10);
        });
        test("teleports cut joint tracks rather than drawing a flight across the map", () =>
        {
            var next = Frame(.1, Node("./cloth#0", .1, 10)); next.Actors[0].Position[0] = 21;
            var timeline = new ActorJointTimeline(new[] { Frame(0, Node("./cloth#0", 0, 0)), next }, true);
            Check(timeline.TryActor("actor", .05, out var actor)); Close(Value(actor.At(1)), 0);
            Check(timeline.TryActor("actor", .1, out actor)); Close(Value(actor.At(1)), 10);
        });
        test("topology switches cut even when a surviving path has future samples", () =>
        {
            var next = Frame(.1, Node("./different#0", .1, 10));
            var timeline = new ActorJointTimeline(new[] { Frame(0, Node("./cloth#0", 0, 0)), next }, true);
            Check(timeline.TryActor("actor", .05, out var actor)); Check(actor.Topology[1].Path == "./cloth#0"); Close(Value(actor.At(1)), 0);
            Check(timeline.TryActor("actor", .1, out actor)); Check(actor.Topology[1].Path == "./different#0");
        });
        test("disappearance and reappearance never connect across the absent interval", () =>
        {
            var timeline = new ActorJointTimeline(new[] { Frame(0, Node("./cloth#0", 0, 0)), new ReplayFrame { T = .03 },
                Frame(.07, Node("./cloth#0", .07, 10)), Frame(.1, Node("./cloth#0", .1, 20)) }, true);
            Check(!timeline.TryActor("actor", .05, out _));
            Check(timeline.TryActor("actor", .02, out var actor)); Close(Value(actor.At(1)), 0);
            Check(timeline.TryActor("actor", .085, out actor)); Close(Value(actor.At(1)), 15);
        });
        test("new actor intervals clamp stale baseline clocks to their real appearance time", () =>
        {
            var timeline = new ActorJointTimeline(new[] { new ReplayFrame { T = 0 }, Frame(.05, Node("./cloth#0", 0, 2)),
                Frame(.1, Node("./cloth#0", .1, 4)) }, true);
            Check(timeline.TryActor("actor", .075, out var actor)); Close(actor.At(1).LeftTime, .05); Close(Value(actor.At(1)), 3);
        });
        test("active visibility transitions stay discrete until their sampled timestamp", () =>
        {
            var first = Node("./cloth#0", 0, 0); first.Active = false;
            var timeline = new ActorJointTimeline(new[] { Frame(0, first), Frame(.1, Node("./cloth#0", .1, 10)) }, true);
            Check(timeline.TryActor("actor", .05, out var actor)); Check(!actor.At(1).Left.Active); Close(Value(actor.At(1)), 0);
            Check(timeline.TryActor("actor", .1, out actor)); Check(actor.At(1).Left.Active);
        });
        test("invalid future or nonfinite joint clocks are rejected", () =>
        {
            foreach (double stamp in new[] { .1, double.NaN, double.PositiveInfinity })
            {
                bool rejected = false;
                try { _ = new ActorJointTimeline(new[] { Frame(0, Node("./cloth#0", stamp, 1)) }, true); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected);
            }
        });
        test("paged playback waits for and interpolates a low-rate sample beyond the current page", () =>
        {
            using var releaseNext = new ManualResetEventSlim();
            var held = Node("./cloth#0", 0, 0);
            using var timeline = new PagedReplayTimeline(new ReplayHeader(), .2, 5, new[] { "actor" },
                new[] { new ReplayPageRange(0, .1), new ReplayPageRange(.1, .2) }, (index, token) =>
                {
                    if (index == 1) releaseNext.Wait(token);
                    var clip = new ReplayClip { Complete = true };
                    if (index == 0) clip.Frames.AddRange(new[] { Frame(0, held), Frame(.05, held), Frame(.1, held) });
                    else clip.Frames.AddRange(new[] { Frame(.1, held), Frame(.12, Node("./cloth#0", .12, 12)), Frame(.2, Node("./cloth#0", .2, 20)) });
                    return clip;
                });
            var watch = Stopwatch.StartNew();
            while (timeline.CachedPageCount == 0 && watch.ElapsedMilliseconds < 3000) { Check(!timeline.TrySample(.095, out _)); Thread.Sleep(1); }
            Check(!timeline.TrySample(.095, out _)); releaseNext.Set();
            var sample = Ready(timeline, .095);
            Check(sample.Joints!.TryActor("actor", sample.Time, out var actor)); Close(Value(actor.At(1)), 9.5);
            sample = Ready(timeline, .15); Check(sample.Joints!.TryActor("actor", sample.Time, out actor)); Close(Value(actor.At(1)), 15);
            sample = Ready(timeline, .095); Check(sample.Joints!.TryActor("actor", sample.Time, out actor)); Close(Value(actor.At(1)), 9.5);
        });
        test("single-frame twenty-millisecond pages look ahead until the real ten-Hz cloth sample", () =>
        {
            var ranges = Enumerable.Range(0, 7).Select(i => new ReplayPageRange(i * .02, i * .02)).ToArray();
            using var timeline = new PagedReplayTimeline(new ReplayHeader(), .12, 7, new[] { "actor" }, ranges, (index, token) =>
            {
                double time = index * .02;
                var clip = new ReplayClip { Complete = true };
                clip.Frames.Add(Frame(time, Node("./cloth#0", time < .1 ? 0 : .1, time < .1 ? 0 : 10)));
                return clip;
            });
            var sample = Ready(timeline, .01);
            Check(sample.Joints!.TryActor("actor", sample.Time, out var actor)); Close(Value(actor.At(1)), 1);
            Check(timeline.CachedPageCount >= 6 && timeline.CachedPageCount <= 64);
            Check(timeline.CachedDecodedBytes <= PagedReplayTimeline.MaximumCachedDecodedBytes);
            sample = Ready(timeline, .09); Check(sample.Joints!.TryActor("actor", sample.Time, out actor)); Close(Value(actor.At(1)), 9);
            sample = Ready(timeline, .01); Check(sample.Joints!.TryActor("actor", sample.Time, out actor)); Close(Value(actor.At(1)), 1);
        });
        test("failure several tiny pages ahead is surfaced instead of permanent joint buffering", () =>
        {
            var ranges = Enumerable.Range(0, 7).Select(i => new ReplayPageRange(i * .02, i * .02)).ToArray();
            using var timeline = new PagedReplayTimeline(new ReplayHeader(), .12, 7, new[] { "actor" }, ranges, (index, token) =>
            {
                if (index == 4) throw new IOException("simulated future-page failure");
                double time = index * .02; var clip = new ReplayClip { Complete = true };
                clip.Frames.Add(Frame(time, Node("./cloth#0", time < .1 ? 0 : .1, time < .1 ? 0 : 10))); return clip;
            });
            var watch = Stopwatch.StartNew(); bool rejected = false;
            while (watch.ElapsedMilliseconds < 5000)
            {
                try { Check(!timeline.TrySample(.01, out _)); }
                catch (InvalidDataException) { rejected = true; break; }
                Thread.Sleep(1);
            }
            Check(rejected, "Failed low-rate lookahead left playback buffering forever.");
        });
    }

    private static ReplayFrame Frame(double t, NodePose child) => new()
    { T = t, Actors = new[] { new ActorFrame { Id = "actor", JointPose = new[] { Node(".", t, (float)(t * 20)), child } } } };
    private static ReplayFrame RotatedFrame(double t, float angle, NodePose child)
    {
        var frame = Frame(t, child); var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle);
        frame.Actors[0].JointPose[0].Rotation = new[] { q.X, q.Y, q.Z, q.W }; return frame;
    }
    private static NodePose Node(string path, double time, float x) => new() { Path = path, SampleTime = time, Position = new[] { x, 0f, 0f } };
    private static double Value(ActorJointBracket sample) => sample.Left.Position[0] + (sample.Right.Position[0] - sample.Left.Position[0]) * sample.Mix;
    private static Vector3 Point(ActorJointBracket sample) => Vector3.Lerp(new Vector3(sample.Left.Position[0], sample.Left.Position[1], sample.Left.Position[2]),
        new Vector3(sample.Right.Position[0], sample.Right.Position[1], sample.Right.Position[2]), sample.Mix);
    private static Quaternion Rotation(ActorJointBracket sample) => Quaternion.Slerp(new Quaternion(sample.Left.Rotation[0], sample.Left.Rotation[1], sample.Left.Rotation[2], sample.Left.Rotation[3]),
        new Quaternion(sample.Right.Rotation[0], sample.Right.Rotation[1], sample.Right.Rotation[2], sample.Right.Rotation[3]), sample.Mix);
    private static ReplayTimelineSample Ready(PagedReplayTimeline timeline, double time)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 5000) { if (timeline.TrySample(time, out var sample)) return sample; Thread.Sleep(1); }
        throw new Exception("Timed out waiting for joint playback window.");
    }
    private static void Close(double actual, double expected, double error = .00001) => Check(Math.Abs(actual - expected) <= error, $"Expected {expected}; actual {actual}.");
    private static void Check(bool value, string message = "Joint timeline assertion failed.") { if (!value) throw new Exception(message); }
}
