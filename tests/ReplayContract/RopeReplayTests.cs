using System;
using System.IO;
using PeakReplayLab;

internal static class RopeReplayTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Check(bool value) { if (!value) throw new Exception("Rope replay assertion failed."); }
        static RopeReplayPoint Point(float x = 0) => new() { Position = new[] { x, 2f, 3f }, Radius = .125f, Height = .7f };
        static RopeReplayFrame Rope() => new() { Key = "rope:1", Kind = "rope", Resource = "RopeDynamic", Visible = true,
            Attachment = 2, CollisionKind = "capsule", Points = new[] { Point(), Point(1) }, Bones = new[] { Point(), Point(1) } };
        static void Reject(Action body) { try { body(); } catch (InvalidDataException) { return; } throw new Exception("Invalid rope was accepted."); }
        test("rope real segment poses and native bones validate", () => RopeReplayRules.Validate(Rope()));
        test("rope snapshot crop shares timeless immutable shape", () => { var frame = Rope(); Check(ReferenceEquals(frame, RopeReplayRules.Rebase(frame, 800))); });
        test("rope stable state can interpolate without prior playback history", () =>
        { var a = Rope(); var b = Rope(); b.Points[1].Position[1] += 1; Check(RopeReplayRules.CanInterpolate(a, b)); Check(RopeReplayRules.CanInterpolate(b, a)); });
        test("rope topology and attachment changes are discrete", () =>
        {
            var a = Rope(); var b = Rope(); b.Points = new[] { Point() }; Check(!RopeReplayRules.CanInterpolate(a, b));
            b = Rope(); b.Attachment = 0; Check(!RopeReplayRules.CanInterpolate(a, b));
            b = Rope(); b.Visible = false; Check(!RopeReplayRules.CanInterpolate(a, b));
            b = Rope(); b.CollisionKind = "box"; Check(!RopeReplayRules.CanInterpolate(a, b));
        });
        test("rope 40 segment and 64 bone caps are enforced", () =>
        {
            var a = Rope(); a.Points = new RopeReplayPoint[41]; Reject(() => RopeReplayRules.Validate(a));
            a = Rope(); a.Bones = new RopeReplayPoint[65]; Reject(() => RopeReplayRules.Validate(a));
        });
        test("hook actual sampled line supports 40 points without rope bones", () =>
        {
            var a = Rope(); a.Kind = "hook"; a.Bones = Array.Empty<RopeReplayPoint>(); a.Points = new RopeReplayPoint[40];
            for (int i = 0; i < 40; i++) a.Points[i] = Point(i); RopeReplayRules.Validate(a);
            a.Bones = new[] { Point() }; Reject(() => RopeReplayRules.Validate(a));
        });
        test("vine 50 real collision points and anchor empty path validate", () =>
        {
            var a = Rope(); a.Kind = "vine"; a.Bones = Array.Empty<RopeReplayPoint>(); a.Points = new RopeReplayPoint[50];
            for (int i = 0; i < 50; i++) a.Points[i] = Point(i); RopeReplayRules.Validate(a);
            a.Kind = "anchor"; a.Points = Array.Empty<RopeReplayPoint>(); RopeReplayRules.Validate(a);
        });
        test("rope rejects nonfinite positions invalid rotations and negative thickness", () =>
        {
            var a = Rope(); a.Points[0].Position[0] = float.NaN; Reject(() => RopeReplayRules.Validate(a));
            a = Rope(); a.Bones[0].Rotation = new float[4]; Reject(() => RopeReplayRules.Validate(a));
            a = Rope(); a.Points[0].Radius = -1; Reject(() => RopeReplayRules.Validate(a));
        });
        test("rope resource kind identity and node paths are bounded", () =>
        {
            var a = Rope(); a.Kind = "arbitrary-prefab"; Reject(() => RopeReplayRules.Validate(a));
            a = Rope(); a.Resource = " "; Reject(() => RopeReplayRules.Validate(a));
            a = Rope(); a.Pose.Nodes = new[] { new NodePose { Path = "." }, new NodePose { Path = "." } }; Reject(() => RopeReplayRules.Validate(a));
        });
        test("rope estimate accounts for real points and visual bone samples", () =>
        { var a = Rope(); long before = RopeReplayRules.Estimate(a); a.Bones = new[] { Point() }; Check(before - RopeReplayRules.Estimate(a) == 192); });
        test("authored map vines cannot fill the dynamic rope budget", () =>
        {
            int tracked = 0;
            // Regression: the real 0.4.0 recording contained 249 hidden map vines
            // plus 7 native bridges, leaving no room for the subsequent cannon shot.
            for (int i = 0; i < 256; i++) if (RopeReplayAdmission.Record("vine", i < 7, false)) tracked++;
            Check(tracked == 0);
            if (RopeReplayAdmission.Record("rope", true, false)) tracked++;
            if (RopeReplayAdmission.Record("anchor", true, false)) tracked++;
            Check(tracked == 2 && tracked < RopeReplayRules.MaximumEntities);
        });
        test("spawned chain and rescue hook retain their own tracks", () =>
        {
            Check(RopeReplayAdmission.Record("vine", true, true));
            Check(RopeReplayAdmission.Record("hook", true, false));
            Check(!RopeReplayAdmission.Record("vine", false, true));
            Check(!RopeReplayAdmission.Record("rope", false, false));
            Check(!RopeReplayAdmission.Record("arbitrary", true, true));
        });
        test("new rope admission can evict only dead inactive or invisible entries", () =>
        {
            Check(RopeReplayAdmission.CanReplace(false, false, false));
            Check(RopeReplayAdmission.CanReplace(true, false, true));
            Check(RopeReplayAdmission.CanReplace(true, true, false));
            Check(!RopeReplayAdmission.CanReplace(true, true, true));
        });
    }
}
