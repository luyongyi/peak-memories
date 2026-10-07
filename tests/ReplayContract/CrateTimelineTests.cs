using System;
using PeakReplayLab;

internal static class CrateTimelineTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Check(bool value) { if (!value) throw new Exception("Crate timeline assertion failed."); }
        static CrateAnimationFrame Anchor(double start = 10, float initial = 0, float rate = 1, float duration = 2) => new()
        { Clip = "Luggage/Open|2|60", Anchored = true, AnchorTime = start, Time = initial, Rate = rate, Duration = duration };
        test("crate anchor advances without per-frame samples", () =>
        { var a = Anchor(); Check(CrateAnimationTimeline.Sample(a, 10.5, 2, false) == .5); Check(a.Time == 0); });
        test("crate animation clamps and settles at open endpoint", () =>
        { var a = Anchor(); Check(CrateAnimationTimeline.Sample(a, 12, 2, false) == 2); Check(CrateAnimationTimeline.Sample(a, 500, 2, false) == 2); });
        test("crate backward seek is independent of previous playback", () =>
        { var a = Anchor(); CrateAnimationTimeline.Sample(a, 100, 2, false); Check(CrateAnimationTimeline.Sample(a, 10.25, 2, false) == .25); });
        test("crate paused/repeated time produces identical cursor", () =>
        { var a = Anchor(); Check(CrateAnimationTimeline.Sample(a, 11, 2, false) == CrateAnimationTimeline.Sample(a, 11, 2, false)); });
        test("crate negative rate handles native reverse clip", () =>
        { var a = Anchor(initial: 2, rate: -1); Check(CrateAnimationTimeline.Sample(a, 10.5, 2, false) == 1.5); Check(CrateAnimationTimeline.Sample(a, 30, 2, false) == 0); });
        test("crate looped native clip wraps", () =>
        { var a = Anchor(); a.Loop = true; Check(CrateAnimationTimeline.Sample(a, 14.5, 2, true) == .5); });
        test("crate future delayed opening holds initial cursor", () =>
        { var a = Anchor(12, .5f); Check(CrateAnimationTimeline.Sample(a, 11, 2, false) == .5); });
        test("crate snapshot rebases pre-window event without mutating source", () =>
        {
            var a = Anchor(99, .25f); var original = new CrateFrame { Key = "crate", Kind = "Luggage", Open = true, Animation = a };
            var clipped = CrateAnimationTimeline.Rebase(original, 100);
            Check(clipped.Animation!.AnchorTime == -1 && original.Animation.AnchorTime == 99);
            Check(ReferenceEquals(clipped.Pose, original.Pose));
            Check(CrateAnimationTimeline.Sample(clipped.Animation, 0, 2, false) == 1.25);
        });
        test("crate legacy schema3 cursor interpolation remains compatible", () =>
        {
            var a = new CrateAnimationFrame { Clip = "old", Time = .25f };
            var b = new CrateAnimationFrame { Clip = "old", Time = .75f };
            Check(CrateAnimationTimeline.Sample(a, 100, 1, false, b, .5f) == .5);
            var crate = new CrateFrame { Animation = a };
            Check(ReferenceEquals(crate, CrateAnimationTimeline.Rebase(crate, 70)));
        });
        test("crate anchor is valid before capture window", () => Check(CrateAnimationTimeline.Valid(Anchor(-120))));
        test("crate invalid anchor rate duration and whitespace are rejected", () =>
        {
            var a = Anchor(); a.AnchorTime = double.NaN; Check(!CrateAnimationTimeline.Valid(a));
            a = Anchor(); a.Rate = float.PositiveInfinity; Check(!CrateAnimationTimeline.Valid(a));
            a = Anchor(); a.Duration = 0; Check(!CrateAnimationTimeline.Valid(a));
            a = Anchor(); a.Clip = "  "; Check(!CrateAnimationTimeline.Valid(a));
            a = Anchor(); a.Time = 3; Check(!CrateAnimationTimeline.Valid(a));
        });
        test("crate animation no dependence on playback render frequency", () =>
        {
            var a = Anchor(0); for (int i = 0; i < 30; i++) CrateAnimationTimeline.Sample(a, i / 30d, 2, false);
            double thirty = CrateAnimationTimeline.Sample(a, 1, 2, false);
            for (int i = 0; i < 144; i++) CrateAnimationTimeline.Sample(a, i / 144d, 2, false);
            Check(thirty == CrateAnimationTimeline.Sample(a, 1, 2, false));
        });
    }
}
