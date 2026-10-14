using PeakReplayLab;

int passed = 0;
void Test(string name, Action body) { body(); passed++; Console.WriteLine("PASS " + name); }
void Check(bool ok) { if (!ok) throw new Exception("Creature sparse cadence contract failed."); }
Test("stable creatures perform exactly five heavy reads per second on a 30 Hz semantic clock", () =>
{
    var clock = new CreatureSamplingCadence(); int reads = 0;
    for (int i = 0; i < 18_000; i++) if (clock.Due(i / 30d, true, 0)) reads++;
    Check(reads == 3_000 && clock.RegularReads == 3_000);
});
Test("cached state and activation edges bypass a pending sparse deadline immediately", () =>
{
    var clock = new CreatureSamplingCadence(); Check(clock.Due(0, true, 0));
    Check(!clock.Due(.001, true, 0)); Check(clock.Due(.002, true, 1));
    Check(clock.LastWasBoundary && clock.Due(.003, false, 1));
    Check(clock.Due(.004, true, 1) && !clock.Due(.005, true, 1));
});
Test("native events get exactly two finite coroutine confirmations and cannot become a perpetual lease", () =>
{
    var clock = new CreatureSamplingCadence(); Check(clock.Due(0, true, 0));
    clock.NotifyNativeEvent(); Check(clock.Due(.01, true, 0));
    Check(clock.Due(.02, true, 0) && clock.Due(.03, true, 0));
    for (int i = 4; i < 20; i++) Check(!clock.Due(i / 100d, true, 0));
    Check(clock.Due(.2, true, 0) && clock.BoundaryReads == 4);
});
Test("short tongue detail stops after a fixed .3 seconds even while geometry continues moving", () =>
{
    var clock = new CreatureSamplingCadence(); Check(clock.Due(0, true, 0));
    clock.NotifyNativeEvent(.01, .3);
    for (int i = 1; i <= 9; i++) Check(clock.Due(.01 + i / 30d - 1e-10, true, 0));
    Check(!clock.Due(.32, true, 0) && !clock.Due(.35, true, 0));
    Check(clock.Due(.42, true, 0));
});
Test("a delayed line reveal can request one bounded detail window without an ongoing motion renewal", () =>
{
    var clock = new CreatureSamplingCadence(); Check(clock.Due(0, true, 0));
    clock.ConfirmShortAction(.7); Check(clock.Due(.7, true, 0, true));
    Check(clock.Due(.8, true, 0) && clock.Due(.99, true, 0));
    Check(!clock.Due(1.01, true, 0));
});
Test("stalled frames do one read without a catch-up loop and invalid clocks cannot corrupt deadlines", () =>
{
    var clock = new CreatureSamplingCadence(); Check(clock.Due(0, true, 0));
    Check(!clock.Due(double.NaN, true, 0) && !clock.Due(double.PositiveInfinity, true, 0));
    Check(clock.Due(90_000, true, 0) && !clock.Due(90_000.01, true, 0));
    Check(clock.RegularReads == 2);
});
Test("native Venus release retains a finite one second tail and rejects longer leases", () =>
{
    var clock = new CreatureSamplingCadence(); Check(clock.Due(0, true, 0));
    clock.NotifyNativeEvent(.01, 1);
    for (int i = 1; i < 30; i++) Check(clock.Due(.01 + i / 30d, true, 0));
    Check(clock.Due(1.02, true, 0) && !clock.Due(1.03, true, 0) && !clock.Due(1.05, true, 0));
    var invalid = new CreatureSamplingCadence(); Check(invalid.Due(0, true, 0));
    invalid.ConfirmShortAction(.01, 1.00001); Check(!invalid.Due(.02, true, 0));
});
Test("sparse scheduling allocates no managed data during 30 Hz polling", () =>
{
    var clock = new CreatureSamplingCadence(); clock.Due(0, true, 0);
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 1; i <= 100_000; i++) clock.Due(i / 30d, true, 0);
    Check(GC.GetAllocatedBytesForCurrentThread() == before);
});
Console.WriteLine($"{passed} creature sparse cadence contract checks passed.");
