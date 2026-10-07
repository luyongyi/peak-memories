using Newtonsoft.Json;
using PeakReplayLab;

internal static class SpawnedReplayTests
{
    public static void Run(Action<string, Action> test)
    {
        test("spawned bounce and shelf mushrooms retain distinct native resources", () =>
        {
            var value = Sample(); SpawnedReplayRules.Validate(value);
            value.Kind = "shelf-shroom"; value.Resource = "0_items/shelfshroomspawn"; SpawnedReplayRules.Validate(value);
        });
        test("spawned terrain stores observed native nodes not gameplay commands", () =>
        {
            var source = Sample(); source.Pose.Nodes[0].Scale = new[] { .4f, .2f, .4f };
            string json = JsonConvert.SerializeObject(source, ReplayFiles.Json);
            var copy = JsonConvert.DeserializeObject<SpawnedReplayFrame>(json, ReplayFiles.Json)!;
            SpawnedReplayRules.Validate(copy);
            Check(copy.Pose.Nodes[0].Scale[1] == .2f && !json.Contains("Collider") && !json.Contains("AnimatorController"));
        });
        test("spawned terrain crop shares timeless immutable pose", () =>
        { var source = Sample(); Check(ReferenceEquals(source, SpawnedReplayRules.Rebase(source, 99999))); });
        test("static spawned terrain avoids frame-rate tree sampling", () =>
        {
            for (int i = 1; i < 30; i++) Check(!SpawnedReplayRules.ShouldProbe(i / 60d, .5, 0, false));
            Check(SpawnedReplayRules.ShouldProbe(.5, .5, 0, false));
        });
        test("native spawn bounce windows and explicit changes bypass the cold gate", () =>
        {
            Check(SpawnedReplayRules.ShouldProbe(.1, 1, SpawnedReplayRules.AnimationWatchSeconds, false));
            Check(SpawnedReplayRules.ShouldProbe(.1, 1, 0, true));
            Check(!SpawnedReplayRules.ShouldProbe(1.3, 1.5, SpawnedReplayRules.AnimationWatchSeconds, false));
        });
        test("spawned interpolation never bridges a despawn or resource change", () =>
        {
            var a = Sample(); var b = Sample(); Check(SpawnedReplayRules.CanInterpolate(a, b));
            b.Pose.Active = false; Check(!SpawnedReplayRules.CanInterpolate(a, b)); b.Pose.Active = true;
            b.Key = "replacement-lifetime"; Check(!SpawnedReplayRules.CanInterpolate(a, b)); b.Key = a.Key;
            b.Resource = "0_items/shelfshroomspawn"; Check(!SpawnedReplayRules.CanInterpolate(a, b));
        });
        test("spawned validation rejects unbounded and unknown kinds", () =>
        {
            var value = Sample(); value.Key = new string('x', 257); Bad(value);
            value = Sample(); value.Kind = "arbitrary-gameplay-prefab"; Bad(value);
            value = Sample(); value.Pose.Nodes = new NodePose[257]; Bad(value);
        });
        test("spawned node identities and quaternions are strictly validated", () =>
        {
            var value = Sample(); value.Pose.Nodes = new[] { value.Pose.Nodes[0], value.Pose.Nodes[0] }; Bad(value);
            value = Sample(); value.Pose.Rotation = new float[4]; Bad(value);
            value = Sample(); value.Pose.Nodes[0].Position[0] = float.NaN; Bad(value);
            value = Sample(); value.Pose.Nodes[0].Rotation[3] = float.PositiveInfinity; Bad(value);
        });
        test("static spawned state is counted once and released after final frame", () =>
        {
            var value = Sample(); var array = new[] { value }; var budget = new ReplayTrackBudget<SpawnedReplayFrame>(SpawnedReplayRules.Estimate);
            budget.Acquire(array); long initial = budget.Bytes;
            for (int i = 0; i < 120; i++) budget.Acquire(array);
            Check(initial == budget.Bytes);
            for (int i = 0; i < 120; i++) budget.Release(array);
            Check(initial == budget.Bytes); budget.Release(array); Check(budget.Bytes == 0);
        });
        test("spawned validation caches do not trust a fresh modified file state", () =>
        {
            var first = new ReplayTrackValidation<SpawnedReplayFrame>(f => f.Key, SpawnedReplayRules.MaximumEntities, SpawnedReplayRules.Validate);
            var value = Sample(); first.Validate(new[] { value }); value.Pose.Position[0] = float.NaN;
            var fresh = new ReplayTrackValidation<SpawnedReplayFrame>(f => f.Key, SpawnedReplayRules.MaximumEntities, SpawnedReplayRules.Validate);
            try { fresh.Validate(new[] { value }); } catch (InvalidDataException) { return; }
            throw new Exception("A fresh replay validation trusted the capture cache.");
        });
        test("spawned collection validates duplicate identities and count cap", () =>
        {
            var check = new ReplayTrackValidation<SpawnedReplayFrame>(f => f.Key, SpawnedReplayRules.MaximumEntities, SpawnedReplayRules.Validate);
            Reject(() => check.Validate(new[] { Sample(), Sample() }));
            Reject(() => check.Validate(new SpawnedReplayFrame[257]));
        });
    }
    private static SpawnedReplayFrame Sample() => new()
    {
        Key = "spawned-terrain-synthetic", Resource = "0_items/bounceshroomspawn", Kind = "bounce-shroom",
        Pose = new ObjectPose { Nodes = new[] { new NodePose { Path = "0:Bounce/0:Mesh" } } },
    };
    private static void Check(bool condition) { if (!condition) throw new Exception("Spawned terrain assertion failed."); }
    private static void Bad(SpawnedReplayFrame value) => Reject(() => SpawnedReplayRules.Validate(value));
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid spawned terrain data was accepted."); }
}
