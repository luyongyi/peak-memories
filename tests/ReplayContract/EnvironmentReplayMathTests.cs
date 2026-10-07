using PeakReplayLab;

internal static class EnvironmentReplayMathTests
{
    public static void Run(Action<string, Action> test)
    {
        test("wind transitions retain their direction and do not interpolate a new gust early", () =>
        {
            var left = Wind(true, 1, 8); var right = Wind(true, -1, 9);
            Check(!EnvironmentReplayMath.SameWindEvent(left, right));
            Check(EnvironmentReplayMath.Mix(false, .99f) == 0);
            right = Wind(false, 1, 0); Check(!EnvironmentReplayMath.SameWindEvent(left, right));
        });
        test("wind event clocks blend only while the same gust advances", () =>
        {
            var left = Wind(true, 1, 8); var right = Wind(true, 1, 8.25f);
            Check(EnvironmentReplayMath.SameWindEvent(left, right));
            Near(EnvironmentReplayMath.Lerp(left.ActiveFor, right.ActiveFor, .4f), 8.1f);
            right.ActiveFor = .1f; Check(!EnvironmentReplayMath.SameWindEvent(left, right));
            right.Key = "other"; Check(!EnvironmentReplayMath.SameWindEvent(left, right));
        });
        test("lava start end and restart remain discrete across seeks", () =>
        {
            var left = Lava(true, false, 4); var right = Lava(true, false, 4.25f);
            Check(EnvironmentReplayMath.SameLavaEvent(left, right));
            Near(EnvironmentReplayMath.Lerp(101, 103, .5f), 102);
            right.Ended = true; Check(!EnvironmentReplayMath.SameLavaEvent(left, right));
            right.Ended = false; right.ProgressTime = 0; Check(!EnvironmentReplayMath.SameLavaEvent(left, right));
            right = Lava(false, false, 0); Check(!EnvironmentReplayMath.SameLavaEvent(left, right));
        });
        test("unknown environment stays distinct from a known calm empty scene", () =>
        {
            EnvironmentReplayRules.Validate(null); Check(EnvironmentReplayRules.Estimate(null) == 0);
            var calm = new EnvironmentReplayFrame(); EnvironmentReplayRules.Validate(calm);
            Check(calm.Winds.Length == 0 && calm.Lava.Length == 0 && calm.Fog == null && EnvironmentReplayRules.Estimate(calm) > 0);
        });
        test("environment rejects corrupt vectors event clocks duplicate source paths and invalid fog", () =>
        {
            var frame = new EnvironmentReplayFrame { Winds = new[] { Wind(true, 1, 8) }, Lava = new[] { Lava(true, false, 1) } };
            EnvironmentReplayRules.Validate(frame);
            frame.Winds[0].Direction = new float[2]; Reject(() => EnvironmentReplayRules.Validate(frame));
            frame.Winds[0].Direction = new[] { 1f, 0f, 0f }; frame.Winds[0].SecondsUntilSwitch = float.NaN; Reject(() => EnvironmentReplayRules.Validate(frame));
            frame.Winds[0].SecondsUntilSwitch = 2; frame.Lava[0].Key = frame.Winds[0].Key; Reject(() => EnvironmentReplayRules.Validate(frame));
            frame.Lava[0].Key = "lava";
            frame.Fog = new EnvironmentFogFrame { Key = "fog", Point = new[] { 100f, 200f, 300f }, Size = 600, Padding = 300, Enable = 1, Origin = 3, CloseFog = 1 };
            EnvironmentReplayRules.Validate(frame);
            frame.Fog.Enable = 2.01f; Reject(() => EnvironmentReplayRules.Validate(frame));
            frame.Fog.Enable = 1; frame.Fog.Point[1] = float.PositiveInfinity; Reject(() => EnvironmentReplayRules.Validate(frame));
        });
        test("native storm emission preserves the active gust latch and off threshold", () =>
        {
            // Native active code starts above .1 and does not stop an existing
            // system as the curve falls. Its off code stops only below .1.
            Check(!EnvironmentStormReplayRules.ShouldEmit(true, true, .1f, false));
            Check(EnvironmentStormReplayRules.ShouldEmit(true, true, .1001f, false));
            Check(EnvironmentStormReplayRules.ShouldEmit(true, true, 0, true));
            Check(EnvironmentStormReplayRules.ShouldEmit(true, false, .1f, true));
            Check(!EnvironmentStormReplayRules.ShouldEmit(true, false, .0999f, true));
            Check(!EnvironmentStormReplayRules.ShouldEmit(true, false, 1, false));
            Check(EnvironmentStormReplayRules.ShouldEmit(false, true, 0, false));
            Check(!EnvironmentStormReplayRules.ShouldEmit(false, false, 1, true));
        });
        test("storm retirement uses off countdown even though native ActiveFor resets", () =>
        {
            var tail = EnvironmentStormReplayRules.Rebuild(false, true, false, .5f, .3f, 0, 30, 29.25f, 4);
            Check(!tail.Clear); Near(tail.Coast, .75f); Near(tail.Warmup, 3.25f);
            // A backwards seek to an earlier off-phase reconstructs that phase,
            // independent of the later phase's particle presentation/history.
            var later = EnvironmentStormReplayRules.Rebuild(false, true, false, .2f, .1f, 0, 30, 28, 4);
            var earlier = EnvironmentStormReplayRules.Rebuild(false, true, false, .5f, .3f, 0, 30, 29.25f, 4);
            Near(later.Coast, 2); Check(earlier == tail);
            Check(EnvironmentStormReplayRules.Rebuild(false, true, false, .1f, .1f, 0, 30, 25, 4).Clear);
            Check(EnvironmentStormReplayRules.Rebuild(false, true, false, 0, 0, 0, 30, 29.25f, 4).Clear);
        });
        test("storm seek warmup plus retirement remains within the fixed rebuild budget", () =>
        {
            foreach (float lifetime in new[] { .5f, 4f, 30f, 600f })
            foreach (float retired in new[] { 0f, .25f, 2f, 7.75f, 9f, 500f })
            {
                var phase = EnvironmentStormReplayRules.Rebuild(false, true, false, .5f, .3f, 0, 600, 600 - retired, lifetime);
                Check(phase.Warmup >= 0 && phase.Coast >= 0 && phase.Warmup + phase.Coast <= 8);
                Check(phase.Warmup + phase.Coast <= lifetime);
                // Identical recorded clocks while paused must not age the tail.
                Check(phase == EnvironmentStormReplayRules.Rebuild(false, true, false, .5f, .3f, 0, 600, 600 - retired, lifetime));
            }
            var active = EnvironmentStormReplayRules.Rebuild(true, true, true, 1, 1, 100, 120, 20, 600);
            Near(active.Warmup, 8); Check(active.Coast == 0 && !active.Clear);
        });
    }
    private static EnvironmentWindFrame Wind(bool active, float direction, float elapsed) => new()
    { Key = "wind", Enabled = true, Active = active, Direction = new[] { direction, 0f, .2f }, ActiveFor = elapsed, Duration = 20, SecondsUntilSwitch = 20 - elapsed };
    private static EnvironmentLavaFrame Lava(bool started, bool ended, float progress) => new()
    { Key = "lava", Active = true, Started = started, Ended = ended, ProgressTime = progress, Position = new[] { 0f, 100f, 0f } };
    private static void Check(bool value) { if (!value) throw new Exception("Environment event boundary mismatch."); }
    private static void Near(float actual, float expected) => Check(Math.Abs(actual - expected) < .00001f);
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid environment was accepted."); }
}
