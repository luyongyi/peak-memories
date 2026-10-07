using Newtonsoft.Json;
using PeakReplayLab;

internal static class BalloonReplayTests
{
    public static void Run(Action<string, Action> test)
    {
        test("tied balloons store independent identities not an equipped count", () =>
        {
            var a = Sample(); var b = Sample(); b.Key = "second-instance";
            var check = Validator(); check.Validate(new[] { a, b });
            Check(!BalloonReplayRules.SameAttachment(a, b));
        });
        test("native balloon bunch preserves individual red blue purple instances", () =>
        {
            var balloons = new[] { Sample(), Sample(), Sample() };
            for (int i = 0; i < balloons.Length; i++) { balloons[i].Key = "bunch:" + i; balloons[i].ColorIndex = i * 2; }
            Validator().Validate(balloons); Check(balloons[2].ColorIndex == 4);
        });
        test("already attached balloon is a complete standalone baseline", () =>
        {
            var state = Sample(); state.Started = -80;
            string json = JsonConvert.SerializeObject(state, ReplayFiles.Json);
            var copy = JsonConvert.DeserializeObject<BalloonReplayFrame>(json, ReplayFiles.Json)!;
            BalloonReplayRules.Validate(copy); Check(BalloonReplayRules.SameAttachment(state, copy));
            Check(!json.Contains("Position") && !json.Contains("Rotation") && !json.Contains("Rigidbody"));
        });
        test("cropped balloon keeps original age without a new tie event", () =>
        {
            var source = Sample(); source.Started = 100;
            var crop = BalloonReplayRules.Rebase(source, 160);
            BalloonReplayRules.Validate(crop); Check(crop.Started == -60 && source.Started == 100);
            Check(BalloonReplayRules.Sway(source.Key, 180 - source.Started) == BalloonReplayRules.Sway(crop.Key, 20 - crop.Started));
        });
        test("balloon sway is seek invariant and never integrates future playback", () =>
        {
            var state = Sample(); var wanted = BalloonReplayRules.Sway(state.Key, 5);
            for (int i = 0; i < 7200; i++) BalloonReplayRules.Sway(state.Key, i / 60d);
            Check(wanted == BalloonReplayRules.Sway(state.Key, 5));
            Check(wanted == BalloonReplayRules.Sway(state.Key, 5));
        });
        test("multiple head balloons have distinct stable presentation slots", () =>
        {
            var a = BalloonReplayRules.Sway("a", 40, 0, 2);
            var b = BalloonReplayRules.Sway("b", 40, 1, 2);
            Check(Math.Abs(a.X - b.X) > .49);
        });
        test("balloon removal or reattachment is not an interpolated count", () =>
        {
            var state = Sample(); var inactive = Sample(); inactive.Active = false;
            Check(!BalloonReplayRules.SameAttachment(state, inactive));
            inactive = Sample(); inactive.OwnerId = "other-owner";
            Check(!BalloonReplayRules.SameAttachment(state, inactive));
            inactive = Sample(); inactive.Started++;
            Check(!BalloonReplayRules.SameAttachment(state, inactive));
        });
        test("balloon color and attachment offsets are validated", () =>
        {
            var state = Sample(); state.ColorIndex = -1; Bad(state);
            state = Sample(); state.ColorIndex = 6; Bad(state);
            state = Sample(); state.HeadOffset = float.NaN; Bad(state);
            state = Sample(); state.HeadOffset = 100; Bad(state);
        });
        test("balloon timestamps and owner identities cannot be malformed", () =>
        {
            var state = Sample(); state.Started = double.NaN; Bad(state);
            state = Sample(); state.Started = double.PositiveInfinity; Bad(state);
            state = Sample(); state.OwnerId = ""; Bad(state);
            state = Sample(); state.Key = new string('x', 257); Bad(state);
        });
        test("static attachments share budget across all 7200 samples", () =>
        {
            var frames = new[] { Sample() }; var budget = new ReplayTrackBudget<BalloonReplayFrame>(BalloonReplayRules.Estimate);
            budget.Acquire(frames); long bytes = budget.Bytes;
            for (int i = 1; i < 7200; i++) budget.Acquire(frames);
            Check(bytes == budget.Bytes);
            for (int i = 0; i < 7200; i++) budget.Release(frames);
            Check(budget.Bytes == 0);
        });
        test("balloon collection rejects duplicate lifetimes and capacity overflow", () =>
        {
            Reject(() => Validator().Validate(new[] { Sample(), Sample() }));
            Reject(() => Validator().Validate(new BalloonReplayFrame[BalloonReplayRules.MaximumEntities + 1]));
        });
    }
    private static BalloonReplayFrame Sample() => new()
    { Key = "balloon:unique-instance", OwnerId = "observed-player", ColorIndex = 2, HeadOffset = .5f, Started = 4 };
    private static ReplayTrackValidation<BalloonReplayFrame> Validator() =>
        new(f => f.Key, BalloonReplayRules.MaximumEntities, BalloonReplayRules.Validate);
    private static void Bad(BalloonReplayFrame value) => Reject(() => BalloonReplayRules.Validate(value));
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Invalid tied balloon data was accepted.");
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("Tied balloon contract failed."); }
}
