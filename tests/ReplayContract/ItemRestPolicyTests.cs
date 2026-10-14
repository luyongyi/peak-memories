using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class ItemRestPolicyTests
{
    private static ObjectPose Pose(float x = 0, float rotationX = 0) => new()
    {
        Position = new[] { x, 0f, 0f },
        Rotation = new[] { rotationX, 0f, 0f, 1f }
    };

    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("distant newly discovered items remain captured until rest is observed", () =>
        {
            var policy = new ItemRestPolicy();
            check(!policy.Settled && policy.ShouldCapture(false, false));
            policy.Observed(0, false, Pose());
            check(!policy.Settled && policy.ShouldCapture(false, false));
            policy.Observed(.5, false, Pose());
            policy.Observed(.99, false, Pose());
            check(!policy.Settled && policy.ShouldCapture(false, false));
            policy.Observed(1, false, Pose());
            check(policy.Settled && !policy.ShouldCapture(false, false));
        });
        test("distant moving items remain captured even after several seconds", () =>
        {
            var policy = new ItemRestPolicy();
            for (int sample = 0; sample <= 20; sample++)
            {
                policy.Observed(sample * .5, true, Pose(sample));
                check(!policy.Settled && policy.ShouldCapture(false, false));
            }
        });
        test("a short pause between rolling movements cannot suspend distant capture", () =>
        {
            var policy = new ItemRestPolicy();
            policy.Observed(0, true, Pose());
            policy.Observed(.1, false, Pose());
            policy.Observed(.6, false, Pose());
            policy.Observed(.9, true, Pose(1));
            check(!policy.Settled && policy.ShouldCapture(false, false));
            policy.Observed(1, false, Pose(1));
            policy.Observed(1.5, false, Pose(1));
            policy.Observed(1.99, false, Pose(1));
            check(!policy.Settled && policy.ShouldCapture(false, false));
            policy.Observed(2, false, Pose(1));
            check(policy.Settled && !policy.ShouldCapture(false, false));
        });
        test("motion immediately wakes an item which was already settled", () =>
        {
            var policy = new ItemRestPolicy();
            Quiet(policy, 0);
            check(policy.Settled);
            policy.Observed(1.1, true, Pose(2));
            check(!policy.Settled && policy.ShouldCapture(false, false));
            policy.Observed(1.2, false, Pose(2));
            policy.Observed(1.7, false, Pose(2));
            policy.Observed(2.19, false, Pose(2));
            check(!policy.Settled);
            policy.Observed(2.2, false, Pose(2));
            check(policy.Settled);
        });
        test("accumulated slow root drift resets the quiet period", () =>
        {
            var policy = new ItemRestPolicy();
            policy.Observed(0, false, Pose());
            policy.Observed(.3, false, Pose(.00004f));
            policy.Observed(.6, false, Pose(.00008f));
            // Each step is below the movement tolerance, but cumulative movement is not.
            policy.Observed(.9, false, Pose(.00012f));
            policy.Observed(1.2, false, Pose(.00012f));
            policy.Observed(1.8, false, Pose(.00012f));
            check(!policy.Settled && policy.ShouldCapture(false, false));
            policy.Observed(1.91, false, Pose(.00012f));
            check(policy.Settled);
        });
        test("root rotation changes prevent resting even without translation", () =>
        {
            var policy = new ItemRestPolicy();
            policy.Observed(0, false, Pose());
            policy.Observed(.5, false, Pose());
            policy.Observed(1, false, Pose(rotationX: .001f));
            check(!policy.Settled && policy.ShouldCapture(false, false));
            policy.Observed(1.5, false, Pose(rotationX: .001f));
            policy.Observed(2, false, Pose(rotationX: .001f));
            check(policy.Settled);
        });
        test("root movement compares position and quaternion components but ignores scale", () =>
        {
            var origin = Pose();
            check(!ItemRestPolicy.RootMoved(origin, Pose(.00009f, .00009f)));
            check(ItemRestPolicy.RootMoved(origin, Pose(.00011f)));
            check(ItemRestPolicy.RootMoved(origin, Pose(-.00011f)));
            check(ItemRestPolicy.RootMoved(origin, Pose(rotationX: .00011f)));
            foreach (int axis in new[] { 1, 2 })
            {
                var moved = Pose(); moved.Position[axis] = .00011f;
                check(ItemRestPolicy.RootMoved(origin, moved));
            }
            foreach (int component in new[] { 1, 2, 3 })
            {
                var rotated = Pose(); rotated.Rotation[component] += .001f;
                check(ItemRestPolicy.RootMoved(origin, rotated));
            }
            var scaled = Pose(); scaled.Scale = new[] { 2f, 3f, 4f };
            check(!ItemRestPolicy.RootMoved(origin, scaled));
        });
        test("explicit item events wake resting capture and require a fresh quiet period", () =>
        {
            var policy = new ItemRestPolicy();
            Quiet(policy, 0);
            check(policy.Settled);
            policy.Wake();
            check(!policy.Settled && policy.ShouldCapture(false, false));
            policy.Observed(10, false, Pose());
            policy.Observed(10.5, false, Pose());
            policy.Observed(10.99, false, Pose());
            check(!policy.Settled);
            policy.Observed(11, false, Pose());
            check(policy.Settled);
        });
        test("nearby items and exempt living items remain captured after settling", () =>
        {
            var policy = new ItemRestPolicy();
            Quiet(policy, 0);
            check(policy.Settled);
            check(policy.ShouldCapture(true, false));
            check(policy.ShouldCapture(false, true));
            check(policy.ShouldCapture(true, true));
            check(!policy.ShouldCapture(false, false));
        });
        test("long gaps between observations cannot count as continuous rest", () =>
        {
            var policy = new ItemRestPolicy();
            policy.Observed(0, false, Pose());
            policy.Observed(.5, false, Pose());
            policy.Observed(1.26, false, Pose());
            check(!policy.Settled && policy.ShouldCapture(false, false));
            policy.Observed(1.76, false, Pose());
            policy.Observed(2.25, false, Pose());
            check(!policy.Settled);
            policy.Observed(2.27, false, Pose());
            check(policy.Settled);
            policy.Observed(3.1, false, Pose());
            check(!policy.Settled && policy.ShouldCapture(false, false));
        });
        test("an observation exactly at the gap limit preserves the quiet period", () =>
        {
            var policy = new ItemRestPolicy();
            policy.Observed(0, false, Pose());
            policy.Observed(.75, false, Pose());
            policy.Observed(1, false, Pose());
            check(policy.Settled);
        });
        test("a backwards capture clock starts a new quiet period", () =>
        {
            var policy = new ItemRestPolicy();
            Quiet(policy, 10);
            check(policy.Settled);
            policy.Observed(2, false, Pose());
            check(!policy.Settled && policy.ShouldCapture(false, false));
            policy.Observed(2.5, false, Pose());
            policy.Observed(2.99, false, Pose());
            check(!policy.Settled);
            policy.Observed(3, false, Pose());
            check(policy.Settled);
        });
        test("suspending a settled distant item retains its last recorded snapshot", () =>
        {
            var policy = new ItemRestPolicy();
            var baseline = new ItemFrame { Key = "settled-item", ItemId = 1,
                Prefab = "rolling-item", Pose = Pose(50) };
            var first = new ReplayFrame { T = 0, Items = new[] { baseline } };
            check(policy.ShouldCapture(false, false));
            Quiet(policy, 0);
            check(!policy.ShouldCapture(false, false));
            var suspended = new ReplayFrame { T = 1, Items = new[] { baseline } };
            var writer = new ReplayDeltaCodec.Writer(); var reader = new ReplayDeltaCodec.Reader();
            reader.Decode(Token(writer.Encode(first)));
            var delta = Token(writer.Encode(suspended));
            check(delta["RemovedItems"] == null);
            check(delta["Items"] == null);
            var replayed = reader.Decode(delta);
            check(replayed.Items.Length == 1 && replayed.Items[0].Pose.Position[0] == 50);
            check(baseline.Pose.Position[0] == 50);
        });
    }

    private static void Quiet(ItemRestPolicy policy, double start)
    {
        policy.Observed(start, false, Pose());
        policy.Observed(start + .5, false, Pose());
        policy.Observed(start + 1, false, Pose());
    }

    private static JObject Token(object value) => JObject.FromObject(value, JsonSerializer.Create(ReplayFiles.Json));
}
