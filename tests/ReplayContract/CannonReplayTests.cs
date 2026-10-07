using Newtonsoft.Json;
using PeakReplayLab;

internal static class CannonReplayTests
{
    public static void Run(Action<string, Action> test)
    {
        test("placed cannon records one fixed root and authored tube orientation", () =>
        {
            var value = Cannon(); SpawnedReplayRules.Validate(value);
            Check(value.Pose.Nodes.Length == 1 && value.Pose.Nodes[0].Path == "0:Cannon");
            string text = JsonConvert.SerializeObject(value, ReplayFiles.Json);
            var copy = JsonConvert.DeserializeObject<SpawnedReplayFrame>(text, ReplayFiles.Json)!;
            Check(copy.Pose.Nodes[0].Rotation[0] == value.Pose.Nodes[0].Rotation[0]);
            Check(!text.Contains("Photon") && !text.Contains("Rigidbody") && !text.Contains("FireTargets"));
        });
        test("cannon native clip anchor advances without capture pose updates", () =>
        {
            var value = Cannon(); value.Animation = Light(10); SpawnedReplayRules.Validate(value);
            Check(Math.Abs(CrateAnimationTimeline.Sample(value.Animation, 11.5, 3, false) - 1.5) < 1e-6);
            Check(CrateAnimationTimeline.Sample(value.Animation, 999, 3, false) == 3);
        });
        test("cannon crop preserves a light that began before the rolling window", () =>
        {
            var source = Cannon(); source.Animation = Light(118);
            var cropped = SpawnedReplayRules.Rebase(source, 120);
            Check(cropped.Animation!.AnchorTime == -2 && source.Animation.AnchorTime == 118);
            Check(ReferenceEquals(source.Pose, cropped.Pose));
            Check(CrateAnimationTimeline.Sample(cropped.Animation, 0, 3, false) == 2);
            SpawnedReplayRules.Validate(cropped);
        });
        test("cannon seeking is independent of playback history", () =>
        {
            var a = Light(4);
            double before = CrateAnimationTimeline.Sample(a, 5, 3, false);
            _ = CrateAnimationTimeline.Sample(a, 100, 3, false);
            Check(CrateAnimationTimeline.Sample(a, 5, 3, false) == before);
        });
        test("settled cannon and legacy mushrooms retain shared immutable state", () =>
        {
            var c = Cannon(); Check(ReferenceEquals(c, SpawnedReplayRules.Rebase(c, 120)));
            var m = new SpawnedReplayFrame { Key = "legacy", Kind = "bounce-shroom", Resource = "0_items/bounceshroomspawn" };
            SpawnedReplayRules.Validate(m); Check(ReferenceEquals(m, SpawnedReplayRules.Rebase(m, 120)));
        });
        test("cannon payload validates exact resource and supported native clips", () =>
        {
            var value = Cannon(); value.Resource = "0_items/scoutcannonitem"; Reject(value);
            value = Cannon(); value.Animation = Light(0); value.Animation.Clip = "arbitrary-gameplay"; Reject(value);
            value = Cannon(); value.Animation = Light(0); value.Animation.Anchored = false; Reject(value);
            value = Cannon(); value.Animation = Light(0); value.Animation.Loop = true; Reject(value);
        });
        test("cannon animation validation rejects invalid clocks and quaternions", () =>
        {
            var value = Cannon(); value.Animation = Light(double.NaN); Reject(value);
            value = Cannon(); value.Animation = Light(0); value.Animation.Rate = float.PositiveInfinity; Reject(value);
            value = Cannon(); value.Pose.Nodes[0].Rotation = new float[4]; Reject(value);
        });
        test("cannon animations cannot be smuggled into schema-seven mushroom kinds", () =>
        {
            var value = Cannon(); value.Kind = "bounce-shroom"; value.Resource = "0_items/bounceshroomspawn";
            value.Animation = Light(0); Reject(value);
        });
        test("cannon animation storage budget counts anchor and clip identity", () =>
        {
            var value = Cannon(); long idle = SpawnedReplayRules.Estimate(value);
            value.Animation = Light(0); Check(SpawnedReplayRules.Estimate(value) > idle);
            var array = new[] { value }; var budget = new ReplayTrackBudget<SpawnedReplayFrame>(SpawnedReplayRules.Estimate);
            budget.Acquire(array); long bytes = budget.Bytes;
            for (int i = 0; i < 7200; i++) budget.Acquire(array);
            Check(budget.Bytes == bytes);
            for (int i = 0; i < 7201; i++) budget.Release(array);
            Check(budget.Bytes == 0);
        });
        test("cannon fire and despawn are separate discrete states", () =>
        {
            var a = Cannon(); a.Animation = new CrateAnimationFrame { Clip = "CannonFire", Anchored = true, AnchorTime = 3, Duration = 1f / 3 };
            SpawnedReplayRules.Validate(a); var b = Cannon(); b.Pose.Active = false;
            Check(!SpawnedReplayRules.CanInterpolate(a, b));
            Check(CrateAnimationTimeline.Sample(a.Animation, 4, 1d / 3, false) == a.Animation.Duration);
        });
    }
    private static SpawnedReplayFrame Cannon() => new()
    {
        Key = "test-cannon-lifetime", Kind = "scout-cannon", Resource = "scoutcannon_placed",
        Pose = new ObjectPose { Position = new[] { 2f, 3f, 4f }, Nodes = new[] { new NodePose { Path = "0:Cannon",
            Position = new[] { 0f, 1.221f, 0f }, Rotation = new[] { -.38268343f, 0f, 0f, .9238795f } } } },
    };
    private static CrateAnimationFrame Light(double time) => new() { Clip = "CannonLight", Time = 0, Anchored = true, AnchorTime = time, Duration = 3 };
    private static void Check(bool yes) { if (!yes) throw new Exception("Cannon replay assertion failed."); }
    private static void Reject(SpawnedReplayFrame value)
    { try { SpawnedReplayRules.Validate(value); } catch (InvalidDataException) { return; } throw new Exception("Invalid cannon data was accepted."); }
}
