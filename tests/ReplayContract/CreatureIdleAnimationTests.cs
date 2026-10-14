using System;
using System.IO;
using PeakReplayLab;

internal static class CreatureIdleAnimationTests
{
    public static void Run(Action<string, Action> test)
    {
        test("native idle phase is driven by absolute recording time including reverse seeks", () =>
        {
            var animation = Clock(10, .25, .5f);
            Equal(CreatureIdleAnimationRules.SampleTime(animation, 10), .25);
            Equal(CreatureIdleAnimationRules.SampleTime(animation, 10.5), .5);
            Equal(CreatureIdleAnimationRules.SampleTime(animation, 12), .25);
            Equal(CreatureIdleAnimationRules.SampleTime(animation, 9), .75);
            foreach (double time in new[] { 200d, 10.1, 25, 8.2, 10.5, 10.5 })
                Equal(CreatureIdleAnimationRules.SampleTime(animation, time), ((.25 + (time - 10) * .5) % 1 + 1) % 1);
        });
        test("native idle pause and playback speeds require no advancing animation state", () =>
        {
            var animation = Clock(5, .125, .33f);
            double paused = CreatureIdleAnimationRules.SampleTime(animation, 6);
            for (int i = 0; i < 1000; i++) Equal(CreatureIdleAnimationRules.SampleTime(animation, 6), paused);
            Equal(CreatureIdleAnimationRules.SampleTime(animation, 5.5), .125 + .5 * animation.Speed);
            Equal(CreatureIdleAnimationRules.SampleTime(animation, 7), .125 + 2 * animation.Speed);
            animation.Speed = 0;
            Equal(CreatureIdleAnimationRules.SampleTime(animation, 9999), .125);
        });
        test("idle clip clocks rebase without changing clip phase speed or native silk phase", () =>
        {
            var original = Clock(5, .75, .33f); original.RopePhase = .4f;
            var rebased = CreatureIdleAnimationRules.Rebase(original, 12);
            Check(rebased.AnchorTime == -7 && original.AnchorTime == 5);
            Check(rebased.Clip == original.Clip && rebased.ClipTime == original.ClipTime &&
                rebased.Speed == original.Speed && rebased.RopePhase == original.RopePhase);
            Check(ReferenceEquals(original, CreatureIdleAnimationRules.Rebase(original, 0)));
            foreach (double time in new[] { 12d, 12.001, 50, 9, 4 })
                Equal(CreatureIdleAnimationRules.SampleTime(original, time), CreatureIdleAnimationRules.SampleTime(rebased, time - 12));
            CreatureIdleAnimationRules.Validate(rebased);
        });
        test("native idle animation rejects unsupported clips and malformed clocks", () =>
        {
            foreach (Action<CreatureIdleAnimation> corrupt in new Action<CreatureIdleAnimation>[]
            {
                x => x.Clip = "../scene", x => x.Clip = "SpiderAttack", x => x.Clip = null!,
                x => x.AnchorTime = double.NaN, x => x.AnchorTime = double.PositiveInfinity, x => x.AnchorTime = 1_000_001,
                x => x.ClipTime = double.NaN, x => x.ClipTime = -double.Epsilon, x => x.ClipTime = 1,
                x => x.Speed = float.NaN, x => x.Speed = float.PositiveInfinity, x => x.Speed = -1, x => x.Speed = 9,
                x => x.RopePhase = float.NaN, x => x.RopePhase = -1, x => x.RopePhase = 1.001f,
            })
            {
                var bad = Clock(0, 0, .33f); corrupt(bad);
                Reject(() => CreatureIdleAnimationRules.Validate(bad));
            }
            Reject(() => CreatureIdleAnimationRules.Validate(null!));
            Reject(() => CreatureIdleAnimationRules.SampleTime(Clock(0, 0, 1), double.NaN));
            Reject(() => CreatureIdleAnimationRules.SampleTime(Clock(0, 0, 1), double.PositiveInfinity));
            Reject(() => CreatureIdleAnimationRules.SampleTime(Clock(0, 0, 1), double.MaxValue));
            var valid = Clock(-1_000_000, .999999, 8); valid.RopePhase = 1;
            CreatureIdleAnimationRules.Validate(valid);
        });
    }
    private static CreatureIdleAnimation Clock(double time, double phase, float speed) => new()
    { Clip = CreatureIdleAnimationRules.SpiderIdle, AnchorTime = time, ClipTime = phase, Speed = speed };
    private static void Equal(double actual, double expected) => Check(Math.Abs(actual - expected) < 1e-9);
    private static void Check(bool value) { if (!value) throw new Exception("Native idle animation clock mismatch."); }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Malformed native idle clock was accepted."); }
}
