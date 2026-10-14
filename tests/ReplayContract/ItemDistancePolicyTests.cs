using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class ItemDistancePolicyTests
{
    private static ActorFrame Actor(string id, float x, float y = 0, float z = 0) =>
        new() { Id = id, Position = new[] { x, y, z } };

    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("distance capture probes a newly discovered item before using its position", () =>
        {
            var policy = new ItemDistancePolicy(0);
            check(policy.ShouldProbe(0, 0));
            check(!policy.Include(new[] { Actor("local", 0) }, 50, null, out bool entered) && !entered);
            policy.Observed(0, 0, 0, 0, 0);
            check(policy.Include(new[] { Actor("local", 0) }, 50, null, out entered) && entered);
        });
        test("distance capture includes the exact spherical boundary and excludes beyond it", () =>
        {
            var policy = new ItemDistancePolicy(0); policy.Observed(0, 0, 0, 0, 0);
            check(policy.Include(new[] { Actor("local", 3, 4) }, 5, null, out _));
            check(!policy.Include(new[] { Actor("local", 3, 4.001f) }, 5, null, out _));
            check(policy.Include(new[] { Actor("local", 0, 0, -5) }, 5, null, out _));
            check(!policy.Include(new[] { Actor("local", 0, 0, -5.001f) }, 5, null, out _));
        });
        test("distance capture considers every recorded teammate rather than only the first", () =>
        {
            var policy = new ItemDistancePolicy(0); policy.Observed(0, 0, 100, 0, 0);
            var actors = new[] { Actor("local", 0), Actor("far-teammate", -100), Actor("near-teammate", 99) };
            check(policy.Include(actors, 5, null, out bool entered) && entered);
            actors[2] = Actor("near-teammate", 90);
            check(!policy.Include(actors, 5, null, out entered) && !entered);
        });
        test("distance capture with no recorded players excludes unowned items", () =>
        {
            var policy = new ItemDistancePolicy(0); policy.Observed(0, 0, 0, 0, 0);
            check(!policy.Include(Array.Empty<ActorFrame>(), 50, null, out bool entered) && !entered);
            check(!policy.Include(Array.Empty<ActorFrame>(), 50, "missing-owner", out entered) && !entered);
        });
        test("distance capture uses the recorded holder for fixed backpack placement", () =>
        {
            // A fixed backpack pose can retain the location where it was stowed.
            // Its wearer must keep the item sampled after moving far from that pose.
            var policy = new ItemDistancePolicy(0); policy.Observed(0, 0, -1000, 0, 0);
            var actors = new[] { Actor("local", 0), Actor("wearer", 1000) };
            check(policy.Include(actors, 50, "wearer", out bool entered) && entered);
            check(!policy.Include(actors, 50, "other-wearer", out entered) && !entered);
            check(!policy.Include(actors, 50, null, out entered) && !entered);
            check(policy.Include(actors, 50, "wearer", out entered) && entered);
        });
        test("distance capture signals refresh once on entry and again after leaving", () =>
        {
            var policy = new ItemDistancePolicy(0); policy.Observed(10, 7, 100, 0, 0);
            check(!policy.Include(new[] { Actor("local", 0) }, 10, null, out bool entered) && !entered);
            // Player movement uses the cached item position and cannot wait for a probe.
            check(!policy.ShouldProbe(10.01, 7));
            check(policy.Include(new[] { Actor("local", 95) }, 10, null, out entered) && entered);
            check(policy.Include(new[] { Actor("local", 96) }, 10, null, out entered) && !entered);
            check(!policy.Include(new[] { Actor("local", 80) }, 10, null, out entered) && !entered);
            check(policy.Include(new[] { Actor("local", 90) }, 10, null, out entered) && entered);
        });
        test("distance capture uses observed item movement when a distant item approaches", () =>
        {
            var policy = new ItemDistancePolicy(0); policy.Observed(0, 0, 100, 0, 0);
            var actors = new[] { Actor("local", 0) };
            check(!policy.Include(actors, 10, null, out _));
            policy.Observed(1, 0, 5, 0, 0);
            check(policy.Include(actors, 10, null, out bool entered) && entered);
        });
        test("distance capture cold position probes stay below the recording frame cadence", () =>
        {
            var policy = new ItemDistancePolicy(0); policy.Observed(0, 10, 1000, 0, 0);
            for (int frame = 1; frame < 60; frame++) check(!policy.ShouldProbe(frame / 60d, 10));
            check(policy.ShouldProbe(1, 10));
            policy.Observed(1, 10, 1000, 0, 0);
            check(!policy.ShouldProbe(1.001, 10)); check(policy.ShouldProbe(2, 10));
        });
        test("distance capture spreads cold position probes across object deadlines", () =>
        {
            var first = new ItemDistancePolicy(0); var last = new ItemDistancePolicy(16);
            first.Observed(0, 0, 1000, 0, 0); last.Observed(0, 0, 1000, 0, 0);
            check(first.ShouldProbe(1, 0) && !last.ShouldProbe(1, 0));
            check(!last.ShouldProbe(1.175, 0)); check(last.ShouldProbe(1.177, 0));
        });
        test("distance capture events bypass the probe deadline and observation consumes revision", () =>
        {
            var policy = new ItemDistancePolicy(16); policy.Observed(10, 40, 1000, 0, 0);
            check(!policy.ShouldProbe(10.001, 40)); check(policy.ShouldProbe(10.001, 41));
            policy.Observed(10.001, 41, 20, 0, 0);
            check(!policy.ShouldProbe(10.002, 41)); check(policy.ShouldProbe(10.002, 42));
        });
        test("distance capture radius zero and negative restore unlimited active item capture", () =>
        {
            var policy = new ItemDistancePolicy(0); policy.Observed(0, 0, 100000, 0, 0);
            check(policy.Include(Array.Empty<ActorFrame>(), 0, null, out bool entered) && entered);
            check(policy.Include(Array.Empty<ActorFrame>(), -1, null, out entered) && !entered);
            check(!policy.Include(new[] { Actor("local", 0) }, 50, null, out entered) && !entered);
        });
        test("distance capture excludes inactive items even when held or radius is unlimited", () =>
        {
            var policy = new ItemDistancePolicy(0); var actors = new[] { Actor("owner", 0) };
            policy.Observed(0, 1, 0, 0, 0); check(policy.Include(actors, 50, "owner", out _));
            policy.Inactive(.1, 2);
            check(!policy.Include(actors, 50, "owner", out bool entered) && !entered);
            check(!policy.Include(actors, 0, "owner", out entered) && !entered);
            check(!policy.ShouldProbe(.101, 2)); check(policy.ShouldProbe(1.1, 2));
            check(policy.ShouldProbe(.101, 3));
            policy.Observed(.101, 3, 0, 0, 0);
            check(policy.Include(actors, 50, "owner", out entered) && entered);
        });
        test("distance capture rejects nonfinite item and player distances", () =>
        {
            var policy = new ItemDistancePolicy(0); var actors = new[] { Actor("local", 0) };
            foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                policy.Observed(0, 0, invalid, 0, 0);
                check(!policy.Include(actors, 50, null, out _));
                policy.Observed(0, 0, 0, invalid, 0);
                check(!policy.Include(actors, 50, null, out _));
                policy.Observed(0, 0, 0, 0, invalid);
                check(!policy.Include(actors, 50, null, out _));
                policy.Observed(0, 0, 0, 0, 0);
                check(!policy.Include(new[] { Actor("local", invalid) }, 50, null, out _));
            }
        });
        test("distance capture retains the last snapshot through codec deltas and refreshes on return", () =>
        {
            var policy = new ItemDistancePolicy(0); policy.Observed(0, 0, 0, 0, 0);
            var baseline = new ItemFrame { Key = "distance-item", ItemId = 1, Prefab = "test-item" };
            var first = new ReplayFrame { T = 0, Items = new[] { baseline } };
            check(policy.Include(new[] { Actor("local", 0) }, 10, null, out _));
            check(!policy.Include(new[] { Actor("local", 100) }, 10, null, out _));
            var distant = new ReplayFrame { T = 1, Items = new[] { baseline } };
            policy.Observed(2, 1, 2, 0, 0);
            check(policy.Include(new[] { Actor("local", 0) }, 10, null, out bool entered) && entered);
            var refreshed = new ItemFrame { Key = baseline.Key, ItemId = baseline.ItemId, Prefab = baseline.Prefab,
                Pose = new ObjectPose { Position = new[] { 2f, 0f, 0f } } };
            var returned = new ReplayFrame { T = 2, Items = new[] { refreshed } };
            var writer = new ReplayDeltaCodec.Writer(); var reader = new ReplayDeltaCodec.Reader();
            var initialRead = reader.Decode(Token(writer.Encode(first)));
            var distantDelta = Token(writer.Encode(distant));
            check(distantDelta["RemovedItems"] == null);
            check(distantDelta["Items"] == null);
            var distantRead = reader.Decode(distantDelta);
            var returnedRead = reader.Decode(Token(writer.Encode(returned)));
            check(initialRead.Items.Length == 1 && distantRead.Items.Length == 1 && returnedRead.Items.Length == 1);
            check(initialRead.Items[0].Pose.Position[0] == 0 && distantRead.Items[0].Pose.Position[0] == 0);
            check(returnedRead.Items[0].Pose.Position[0] == 2 && baseline.Pose.Position[0] == 0);
        });
    }

    private static JObject Token(object value) => JObject.FromObject(value, JsonSerializer.Create(ReplayFiles.Json));
}
