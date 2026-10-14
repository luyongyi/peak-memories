using PeakReplayLab;

internal static class RopeObservationTests
{
    internal static void Run(Action<string, Action> test)
    {
        static void Check(bool value) { if (!value) throw new Exception("Rope observation assertion failed."); }
        static RopeReplayFrame State(double t, float x = 0) => new()
        {
            Key = "rope:timed", Kind = "rope", Resource = "RopeDynamic", Visible = true, Attachment = 2,
            SampleTimeKnown = true, SampleTime = t,
            Bones = new[] { new RopeReplayPoint(new[] { x, 0f, 0f }, new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f }) },
        };
        static ReplayFrame Frame(double t, RopeReplayFrame rope) => new() { T = t, Ropes = new[] { rope } };
        static RopeObservationTimeline Index(bool end, params ReplayFrame[] frames)
        { var builder = new RopeObservationTimeline.Builder(); foreach (var frame in frames) builder.Observe(frame); return builder.Build(end); }
        test("rope low cadence interpolates across repeated global frames", () =>
        {
            var a = State(0); var b = State(.1, 1);
            var index = Index(true, Frame(0, a), Frame(.02, a), Frame(.04, a), Frame(.06, a), Frame(.08, a), Frame(.1, b));
            Check(index.TrySample(a.Key, .05, out var left, out var right, out float mix));
            Check(ReferenceEquals(a, left) && ReferenceEquals(b, right) && Math.Abs(mix - .5) < .00001);
        });
        test("rope seek page baseline preserves actual predecessor observation clock", () =>
        {
            var a = State(.94); var b = State(1.04, 1);
            var index = Index(true, Frame(1, a), Frame(1.02, a), Frame(1.04, b));
            Check(index.TrySample(a.Key, 1.01, out var left, out _, out float mix));
            Check(ReferenceEquals(a, left) && Math.Abs(mix - .7) < .00001);
        });
        test("rope long sleep holds checkpoint and new event does not animate backwards", () =>
        {
            var a = State(0); var b = State(90, 1); var index = Index(true, Frame(89, a), Frame(90, b));
            Check(index.TrySample(a.Key, 89.99, out var left, out var right, out float mix));
            Check(ReferenceEquals(a, left) && ReferenceEquals(a, right) && mix == 0);
            Check(index.TrySample(a.Key, 90, out left, out right, out mix) && ReferenceEquals(b, left));
        });
        test("rope idle page needs no birth history or future page", () =>
        {
            var a = State(-300); var index = Index(false, Frame(50, a), Frame(51, a));
            Check(index.CanSample(new[] { a }, 50.99)); Check(index.TrySample(a.Key, 50.99, out var left, out _, out _)); Check(ReferenceEquals(left, a));
        });
        test("rope active page requests only its bounded missing successor", () =>
        {
            var a = State(.95); var index = Index(false, Frame(.95, a), Frame(1, a));
            Check(!index.CanSample(new[] { a }, .99));
            var b = State(1.05, 1); var joined = Index(false, Frame(.95, a), Frame(1, a), Frame(1.05, b), Frame(1.2, b));
            Check(joined.CanSample(new[] { a }, .99));
        });
        test("rope visibility and topology events stay discrete", () =>
        {
            var a = State(0); var b = State(.1, 1); b.Visible = false;
            var index = Index(true, Frame(0, a), Frame(.1, b));
            Check(index.TrySample(a.Key, .05, out var left, out var right, out _) && ReferenceEquals(left, right));
        });
        test("rope timed crop rebases clock while sharing geometry", () =>
        {
            var a = State(10); var b = RopeReplayRules.Rebase(a, 100);
            Check(b.SampleTimeKnown && b.SampleTime == -90 && a.SampleTime == 10 && ReferenceEquals(a.Bones, b.Bones));
        });
        test("rope schema 18 remains timeless and schema 19 preserves clock", () =>
        {
            var a = State(0); a.SampleTimeKnown = false;
            var old = new ReplayDeltaCodec.Writer(18).Encode(Frame(0, a));
            var reader = new ReplayDeltaCodec.Reader(18); var legacy = reader.Decode(Newtonsoft.Json.Linq.JObject.FromObject(old));
            Check(!legacy.Ropes[0].SampleTimeKnown);
            var b = State(.1); var encoded = new ReplayDeltaCodec.Writer().Encode(Frame(.1, b));
            var current = new ReplayDeltaCodec.Reader().Decode(Newtonsoft.Json.Linq.JObject.FromObject(encoded));
            Check(current.Ropes[0].SampleTimeKnown && current.Ropes[0].SampleTime == .1);
        });
    }
}
