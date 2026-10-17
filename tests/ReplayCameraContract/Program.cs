using PeakReplayLab;

int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Near(float actual, float expected, string message, float tolerance = .0001f)
{
    Check(float.IsFinite(actual) && MathF.Abs(actual - expected) <= tolerance,
        $"{message}: expected {expected}, got {actual}.");
}
void VectorNear((float X, float Y, float Z) actual, (float X, float Y, float Z) expected, string message)
{
    Near(actual.X, expected.X, message + " (X)");
    Near(actual.Y, expected.Y, message + " (Y)");
    Near(actual.Z, expected.Z, message + " (Z)");
}
float Length((float X, float Y, float Z) value) => MathF.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z);
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); Environment.Exit(1); }
}

Test("one wheel detent has identical zoom and fly-speed effects for 1-unit and 120-unit backends", () =>
{
    foreach (float direction in new[] { -1f, 1f })
    {
        float uniform = ReplayCameraMotion.WheelNotches(direction, 1);
        float nativeWindows = ReplayCameraMotion.WheelNotches(direction * 120, 120);
        Near(uniform, direction, "Uniform wheel detent");
        Near(nativeWindows, direction, "Native Windows wheel detent");
        Near(ReplayCameraMotion.ZoomDistance(8, uniform), ReplayCameraMotion.ZoomDistance(8, nativeWindows), "Backend zoom parity");
        Near(ReplayCameraMotion.FlySpeed(6, uniform), ReplayCameraMotion.FlySpeed(6, nativeWindows), "Backend speed parity");
    }
});

Test("smooth wheels retain sub-detent input instead of a dead zone or rounding", () =>
{
    Near(ReplayCameraMotion.WheelNotches(.1f, 1), .1f, "Uniform fine scroll");
    Near(ReplayCameraMotion.WheelNotches(12, 120), .1f, "Native Windows fine scroll");
    Near(ReplayCameraMotion.ZoomDistance(8, .1f), 7.75f, "Fine zoom");
    float distance = 8;
    for (int i = 0; i < 4; i++) distance = ReplayCameraMotion.ZoomDistance(distance, .25f);
    Near(distance, ReplayCameraMotion.ZoomDistance(8, 1), "Four quarter-detents equal one detent");
});

Test("third-person zoom responds by 2.5 metres per detent in both directions", () =>
{
    Near(ReplayCameraMotion.ZoomDistance(8, 1), 5.5f, "Zoom in");
    Near(ReplayCameraMotion.ZoomDistance(8, -1), 10.5f, "Zoom out");
    Near(ReplayCameraMotion.ZoomDistance(ReplayCameraMotion.ZoomDistance(8, 1), -1), 8, "Opposite scroll restores distance");
    Near(ReplayCameraMotion.ZoomDistance(8, 0), 8, "No wheel input");
});

Test("zoom stops at usable near and far limits even for rapid wheel input", () =>
{
    Near(ReplayCameraMotion.ZoomDistance(8, 1000), 1, "Near limit");
    Near(ReplayCameraMotion.ZoomDistance(8, -1000), 35, "Far limit");
    Near(ReplayCameraMotion.ZoomDistance(1, 1), 1, "Already at near limit");
    Near(ReplayCameraMotion.ZoomDistance(35, -1), 35, "Already at far limit");
    Near(ReplayCameraMotion.ZoomDistance(-100, 0), 1, "Below-range starting distance");
    Near(ReplayCameraMotion.ZoomDistance(100, 0), 35, "Above-range starting distance");
});

Test("fly-speed wheel changes are proportional and preserve fine input", () =>
{
    Near(ReplayCameraMotion.FlySpeed(6, 1), 7.5f, "Speed up");
    Near(ReplayCameraMotion.FlySpeed(6, -1), 4.8f, "Slow down");
    Near(ReplayCameraMotion.FlySpeed(ReplayCameraMotion.FlySpeed(6, 1), -1), 6, "Opposite scroll restores speed");
    float fine = ReplayCameraMotion.FlySpeed(6, .1f);
    Check(fine > 6 && fine < 7.5f, "Fine speed input was discarded or rounded to a whole detent.");
    float speed = 6;
    for (int i = 0; i < 4; i++) speed = ReplayCameraMotion.FlySpeed(speed, .25f);
    Near(speed, 7.5f, "Four quarter-detents equal one speed step");
});

Test("fly speed remains between precise and long-distance navigation limits", () =>
{
    Near(ReplayCameraMotion.FlySpeed(6, 1000), 80, "Maximum fly speed");
    Near(ReplayCameraMotion.FlySpeed(6, -1000), .5f, "Minimum fly speed");
    Near(ReplayCameraMotion.FlySpeed(.5f, -1), .5f, "Already at minimum");
    Near(ReplayCameraMotion.FlySpeed(80, 1), 80, "Already at maximum");
});

Test("camera motion has the same travel distance at 30, 60 and 120 update FPS", () =>
{
    foreach (int frames in new[] { 30, 60, 120 })
    {
        float distance = 0;
        for (int i = 0; i < frames; i++)
            distance += ReplayCameraMotion.Movement(0, 0, 1, 6, 1f / frames, false, false).Z;
        Near(distance, 6, $"One second at {frames} FPS");
    }
});

Test("diagonal and three-axis flight do not travel faster than straight flight", () =>
{
    var straight = ReplayCameraMotion.Movement(0, 0, 1, 6, .1f, false, false);
    var diagonal = ReplayCameraMotion.Movement(1, 0, 1, 6, .1f, false, false);
    var allAxes = ReplayCameraMotion.Movement(1, 1, 1, 6, .1f, false, false);
    Near(Length(straight), .6f, "Straight speed");
    Near(Length(diagonal), .6f, "Diagonal speed");
    Near(Length(allAxes), .6f, "Three-axis speed");
    Near(diagonal.X, diagonal.Z, "Diagonal preserves direction");
    Near(allAxes.X, allAxes.Y, "Three-axis preserves X/Y direction");
    Near(allAxes.Y, allAxes.Z, "Three-axis preserves Y/Z direction");
});

Test("pitched forward flight combined with vertical input is bounded in world space", () =>
{
    // Looking up 45 degrees while holding forward and ascend produces this
    // non-orthogonal world vector; normalizing local key axes is insufficient.
    var motion = ReplayCameraMotion.Movement(0, 1.7071068f, .7071068f, 6, .1f, false, false);
    Near(Length(motion), .6f, "Pitched ascend speed");
    Check(motion.Y > motion.Z && motion.Z > 0, "World direction was not preserved.");
    Near(motion.Y / motion.Z, 2.4142136f, "World direction ratio");
    var large = ReplayCameraMotion.Movement(float.MaxValue, float.MaxValue, float.MaxValue, 6, .1f, false, false);
    Near(Length(large), .6f, "Large finite input is normalized without float overflow");
});

Test("small analogue motion stays slow and signed axes retain their direction", () =>
{
    VectorNear(ReplayCameraMotion.Movement(.25f, 0, 0, 6, .1f, false, false), (.15f, 0, 0), "Quarter-stick movement");
    VectorNear(ReplayCameraMotion.Movement(0, -.5f, 0, 6, .1f, false, false), (0, -.3f, 0), "Downward movement");
    VectorNear(ReplayCameraMotion.Movement(0, 0, -1, 6, .1f, false, false), (0, 0, -.6f), "Backward movement");
    VectorNear(ReplayCameraMotion.Movement(0, 0, 0, 6, .1f, false, false), (0, 0, 0), "Idle camera");
});

Test("boost and precision apply to motion without changing the stored fly speed", () =>
{
    Near(ReplayCameraMotion.Movement(0, 0, 1, 6, .1f, true, false).Z, 1.8f, "Boost");
    Near(ReplayCameraMotion.Movement(0, 0, 1, 6, .1f, false, true).Z, .15f, "Precision");
    Near(ReplayCameraMotion.Movement(0, 0, 1, 6, .1f, true, true).Z, .45f, "Combined modifiers");
    Near(ReplayCameraMotion.Movement(0, 0, 1, 6, .1f, false, false).Z, .6f, "Released modifiers");
});

Test("motion follows the supplied real frame clock and bounds a long frame hitch", () =>
{
    // A paused replay still supplies real frame time to this pure helper.
    // Unity input and Time.unscaledDeltaTime wiring require the in-game check.
    VectorNear(ReplayCameraMotion.Movement(0, 0, 1, 6, .016f, false, false), (0, 0, .096f), "Paused replay camera frame");
    VectorNear(ReplayCameraMotion.Movement(0, 0, 1, 6, 0, false, false), (0, 0, 0), "Zero frame time");
    VectorNear(ReplayCameraMotion.Movement(0, 0, 1, 6, -1, false, false), (0, 0, 0), "Negative frame time");
    Near(ReplayCameraMotion.Movement(0, 0, 1, 6, 5, false, false).Z, .6f, "Hitch is capped at 100 ms");
});

Test("invalid wheel values and invalid backend units cannot poison camera state", () =>
{
    foreach (float invalid in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity })
    {
        Near(ReplayCameraMotion.WheelNotches(invalid, 1), 0, "Invalid wheel input");
        Near(ReplayCameraMotion.WheelNotches(1, invalid), 0, "Invalid backend scale");
    }
    Near(ReplayCameraMotion.WheelNotches(1, 0), 0, "Zero backend scale");
    Near(ReplayCameraMotion.WheelNotches(1, -120), 0, "Negative backend scale");
});

Test("invalid camera state recovers defaults and invalid scroll leaves a valid state unchanged", () =>
{
    foreach (float invalid in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity })
    {
        Near(ReplayCameraMotion.ZoomDistance(invalid, 0), 5, "Default follow distance");
        Near(ReplayCameraMotion.FlySpeed(invalid, 0), 6, "Default fly speed");
        Near(ReplayCameraMotion.ZoomDistance(8, invalid), 8, "Invalid zoom scroll");
        Near(ReplayCameraMotion.FlySpeed(6, invalid), 6, "Invalid speed scroll");
        Near(ReplayCameraMotion.FlySpeed(100, invalid), 80, "Invalid scroll still repairs out-of-range speed");
    }
    Near(ReplayCameraMotion.FlySpeed(-1, 0), .5f, "Negative starting speed is bounded");
    Near(ReplayCameraMotion.FlySpeed(100, 0), 80, "Excessive starting speed is bounded");
});

Test("NaN and infinite motion inputs cannot move or corrupt the camera", () =>
{
    foreach (float invalid in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity })
    {
        VectorNear(ReplayCameraMotion.Movement(invalid, 0, 1, 6, .1f, false, false), (0, 0, 0), "Invalid X");
        VectorNear(ReplayCameraMotion.Movement(0, invalid, 1, 6, .1f, false, false), (0, 0, 0), "Invalid Y");
        VectorNear(ReplayCameraMotion.Movement(0, 0, invalid, 6, .1f, false, false), (0, 0, 0), "Invalid Z");
        VectorNear(ReplayCameraMotion.Movement(0, 0, 1, invalid, .1f, false, false), (0, 0, 0), "Invalid speed");
        VectorNear(ReplayCameraMotion.Movement(0, 0, 1, 6, invalid, false, false), (0, 0, 0), "Invalid time");
    }
    VectorNear(ReplayCameraMotion.Movement(0, 0, 1, 0, .1f, false, false), (0, 0, 0), "Zero speed");
    VectorNear(ReplayCameraMotion.Movement(0, 0, 1, -1, .1f, false, false), (0, 0, 0), "Negative speed");
});

Console.WriteLine($"TOTAL: {passed} replay camera input and motion contracts passed. Unity controls and rendering require in-game acceptance.");
