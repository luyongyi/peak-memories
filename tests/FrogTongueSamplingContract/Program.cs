using PeakReplayLab;

int passed = 0;
void Check(bool value) { if (!value) throw new Exception("Frog tongue sampling boundary failed."); }
void Test(string name, Action body) { body(); passed++; Console.WriteLine("PASS " + name); }
Test("closed tongues retain one checkpoint across unlimited quiet scene ticks", () =>
{
    var policy = new FrogTongueSamplingPolicy();
    Check(policy.Due(true, false, 0, 0, false));
    for (int i = 0; i < 10000; i++) Check(!policy.Due(true, false, 0, 0, false));
    Check(policy.Quiet && policy.ClockMode == CreatureReplayRules.SleepingClock);
});
Test("native visibility and late point initialization independently wake geometry", () =>
{
    var policy = new FrogTongueSamplingPolicy(); policy.Due(true, false, 0, 0, false);
    Check(policy.Due(true, true, 0, 0, false));
    Check(!policy.Due(true, true, 0, 0, false));
    Check(policy.Due(true, true, 200, 0, false));
    for (int i = 0; i < 300; i++) Check(policy.Due(true, true, 200, .5f, true));
    Check(policy.ClockMode == CreatureReplayRules.ActiveClock);
});
Test("retraction records a final closed checkpoint and releases continuous reads", () =>
{
    var policy = new FrogTongueSamplingPolicy(); policy.Due(true, true, 200, 1, true);
    Check(policy.Due(true, false, 0, -.01f, false));
    Check(policy.LastWasBoundary);
    for (int i = 0; i < 1000; i++) Check(!policy.Due(true, false, 0, -.01f, false));
});
Test("native notifications wake once without indefinitely renewing a hot lease", () =>
{
    var policy = new FrogTongueSamplingPolicy(); policy.Due(true, false, 0, 0, false);
    policy.NotifyNativeEvent(); Check(policy.Due(true, false, 0, 0, false));
    Check(!policy.Due(true, false, 0, 0, false));
    policy.NotifyNativeEvent(); Check(policy.Due(true, false, 0, 0, false));
    Check(!policy.Due(true, false, 0, 0, false));
});
Test("source activation and pulling boundaries cannot inherit stale quiet state", () =>
{
    var policy = new FrogTongueSamplingPolicy(); policy.Due(true, false, 0, 0, false);
    Check(policy.Due(false, false, 0, 0, false)); Check(!policy.Due(false, false, 0, 0, false));
    Check(policy.Due(true, false, 0, 0, false));
    Check(policy.Due(true, true, 0, 0, true)); Check(!policy.Quiet);
});
Test("unrecognized or invalid native fields retain conservative scene sampling", () =>
{
    var policy = new FrogTongueSamplingPolicy();
    for (int i = 0; i < 100; i++) Check(policy.Due(true, false, 0, 0, false, reliable: false));
    for (int i = 0; i < 100; i++) Check(policy.Due(true, false, 0, float.NaN, false));
    for (int i = 0; i < 100; i++) Check(policy.Due(true, false, -1, 0, false));
});
Test("steady quiet checks allocate no per-tick managed data", () =>
{
    var policy = new FrogTongueSamplingPolicy();
    for (int i = 0; i < 1000; i++) policy.Due(true, false, 0, 0, false);
    long before = GC.GetAllocatedBytesForCurrentThread(); bool unexpected = false;
    for (int i = 0; i < 20000; i++) unexpected |= policy.Due(true, false, 0, 0, false);
    long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
    Check(!unexpected && bytes == 0);
});
Console.WriteLine($"TOTAL: {passed} frog tongue boundary checks passed. Native rendering is not simulated.");

namespace PeakReplayLab
{
    public static class CreatureReplayRules { public const int ActiveClock = 0, SleepingClock = 2; }
}
