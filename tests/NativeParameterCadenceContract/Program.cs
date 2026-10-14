using PeakReplayLab;

int passed = 0;
void Check(bool condition, string message = "Native 20Hz parameter assertion failed") { if (!condition) throw new Exception(message); }
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); Environment.Exit(1); }
}
double[] SceneTimes(Func<int, double> step, int count = 6000)
{
    var global = new SampleClock(60); var scene = new SampleClock(30); double now = 0;
    var times = new List<double>();
    for (int i = 0; i < count; i++)
    {
        now += step(i);
        if (global.ShouldCapture(now) && scene.ShouldCapture(now)) times.Add(now);
    }
    return times.ToArray();
}

Test("30Hz Scene input preserves 20Hz native parameter reads instead of the relative deadline's 15Hz", () =>
{
    var environment = new EnvironmentSampleClock(); var audio = new ParameterReadProbe();
    int environmentReads = 0, oldReads = 0; double oldNext = double.NegativeInfinity;
    for (int i = 0; i <= 3000; i++)
    {
        double now = i / 30d;
        if (environment.Take(now, false)) environmentReads++;
        audio.Observe(now);
        if (now + .000001 >= oldNext) { oldReads++; oldNext = now + .05; }
    }
    Check(environmentReads == 2001 && audio.ParameterReads == 2001 && oldReads == 1501,
        $"environment={environmentReads}, audio={audio.ParameterReads}, relative={oldReads}");
});
Test("58fps and two real global60-to-Scene30 jitter patterns preserve the native 20Hz target", () =>
{
    foreach (Func<int, double> step in new Func<int, double>[]
        { _ => 1d / 58, i => 1d / 60 + (i % 3 - 1) * .0015, i => i % 2 == 0 ? .015 : .019 })
    {
        var times = SceneTimes(step); var environment = new EnvironmentSampleClock(); var audio = new ParameterReadProbe();
        int environmentReads = 0;
        foreach (double now in times)
        {
            if (environment.Take(now, false)) environmentReads++;
            audio.Observe(now);
        }
        int expected = (int)Math.Floor((times[^1] - times[0]) * 20 + .000001) + 1;
        Check(environmentReads == expected && audio.ParameterReads == expected,
            $"scene={times.Length}, expected20={expected}, environment={environmentReads}, audio={audio.ParameterReads}");
        Check(environmentReads < times.Length * .72);
    }
});
Test("original 60Hz input remains 20Hz and a 10Hz input never fabricates missing snapshots", () =>
{
    foreach (int hz in new[] { 60, 10 })
    {
        var clock = new EnvironmentSampleClock(); int reads = 0;
        for (int i = 0; i <= hz * 10; i++) if (clock.Take(i / (double)hz, false)) reads++;
        Check(reads == Math.Min(20, hz) * 10 + 1);
    }
});
Test("a discrete boundary reads once immediately and does not postpone the original periodic phase", () =>
{
    var clock = new EnvironmentSampleClock(); Check(clock.Take(0, false)); Check(!clock.Take(.016, false));
    Check(clock.Take(.025, true)); Check(!clock.Take(.025, true)); Check(!clock.Take(.04, false));
    Check(clock.Take(.05, false)); Check(!clock.Take(.075, false)); Check(clock.Take(.1, false));
});
Test("a same-time edge after an earlier skipped observation is admitted exactly once", () =>
{
    var clock = new EnvironmentSampleClock(); Check(clock.Take(1, false)); Check(!clock.Take(1.02, false));
    Check(clock.Take(1.02, true)); Check(!clock.Take(1.02, false)); Check(!clock.Take(1.02, true));
});
Test("pause repeated calls retain cached parameters and do not restart recording time", () =>
{
    var environment = new EnvironmentSampleClock(); var audio = new ParameterReadProbe(); Check(environment.Take(1, false));
    var baseline = audio.Observe(1);
    for (int i = 0; i < 1000; i++)
    { Check(!environment.Take(1, false)); Check(!environment.Take(1, true)); Check(ReferenceEquals(audio.Observe(1), baseline)); }
    Check(audio.ParameterReads == 1); Check(environment.Take(1.05, false)); audio.Observe(1.05); Check(audio.ParameterReads == 2);
});
Test("long stalls consume one current observation without catch-up work", () =>
{
    var clock = new NativeParameterSampleClock(); Check(clock.Take(0)); Check(clock.Take(100));
    for (int i = 0; i < 1000; i++) Check(!clock.Take(100));
    Check(!clock.Take(100.01)); Check(clock.Take(100.05));
});
Test("reset and a backwards environment clock establish a fresh baseline", () =>
{
    var clock = new EnvironmentSampleClock(); Check(clock.Take(100, false)); Check(clock.Take(1, false));
    Check(!clock.Take(1.01, false)); Check(clock.Take(1.05, false)); clock.Reset(); Check(clock.Take(1.05, false));
    Check(!clock.Take(1.05, false));
});
Test("native sound starts keep separate identities even at one paused game timestamp", () =>
{
    var probe = new ParameterReadProbe(); var first = probe.Start(1); var second = probe.Start(1);
    Check(first.Key != second.Key && first.StartedAt == second.StartedAt && probe.ParameterReads == 2);
    Check(ReferenceEquals(second, probe.Observe(1)) && probe.ParameterReads == 2);
    var pending = AudioReplayRules.CompletedBetweenSamples(first, 1.02);
    Check(pending.Transient && pending.Key == first.Key && pending.StartedAt == 1 && pending.EndTime == 1.02);
});
Test("native sound event reads preserve the periodic phase and right-side birth playback rules", () =>
{
    var probe = new ParameterReadProbe(); probe.Start(0); var started = probe.Start(.025);
    Check(probe.ParameterReads == 2); probe.Observe(.05); Check(probe.ParameterReads == 3);
    var selector = new AudioSceneEventPolicy(); var transient = AudioReplayRules.CompletedBetweenSamples(started, 1d / 30);
    Check(selector.Select([], [transient], .026, 0, 1d / 30, true).Count == 1);
    Check(selector.Select([], [transient], .024, 0, 1d / 30, true).Count == 0);
    Check(!AudioReplayRules.ShouldSound(true, 1, false)); Check(!AudioReplayRules.ShouldSound(false, 1, true));
});
Test("cached unchanged audio metadata is returned until its next native parameter slot", () =>
{
    var probe = new ParameterReadProbe(); var first = probe.Observe(0); Check(ReferenceEquals(first, probe.Observe(1d / 30)));
    Check(!ReferenceEquals(first, probe.Observe(2d / 30)) && probe.ParameterReads == 2);
});
Test("environment directory scheduling and immutable checkpoint arrays remain independent", () =>
{
    var directory = new EnvironmentDiscoverySchedule(); Check(directory.Take(1, 0, EnvironmentDirectory.None) == EnvironmentDirectory.All);
    for (int i = 1; i < 3000; i++) Check(directory.Take(1, i / 30d, EnvironmentDirectory.None) == EnvironmentDirectory.None);
    directory.Changed(EnvironmentDirectory.Fog); Check(directory.Take(1, 100, EnvironmentDirectory.None) == EnvironmentDirectory.Fog);
    Check(directory.Take(1, 100.01, EnvironmentDirectory.None) == EnvironmentDirectory.None);
    var frame = new object(); var baseline = new[] { frame }; var builder = new EnvironmentSnapshotArray<object>(baseline, 1);
    builder.Set(0, frame); Check(ReferenceEquals(builder.Build(), baseline));
});
Test("clock calls reject nonfinite time and work after long-running native game sessions", () =>
{
    foreach (double time in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
    { bool threw = false; try { new NativeParameterSampleClock().Take(time); } catch (ArgumentOutOfRangeException) { threw = true; } Check(threw); }
    var clock = new EnvironmentSampleClock(); int reads = 0;
    for (int i = 0; i <= 3000; i++) if (clock.Take(1e9 + i / 30d, false)) reads++;
    Check(reads == 2001);
});
Test("steady-state parameter scheduling allocates no arrays tasks or query directories", () =>
{
    var environment = new EnvironmentSampleClock(); var audio = new NativeParameterSampleClock();
    for (int i = 0; i < 100; i++) { environment.Take(i / 30d, false); audio.Take(i / 30d); }
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 100; i < 6100; i++) { environment.Take(i / 30d, false); audio.Take(i / 30d); }
    Check(GC.GetAllocatedBytesForCurrentThread() == before);
});
Console.WriteLine($"TOTAL: {passed} native 20Hz cadence checks passed. Native audio output is not simulated.");

// Exercises the production audio scheduling policy with an instrumented
// parameter-read boundary; no Unity audio engine is run.
sealed class ParameterReadProbe
{
    private AudioParameterSampleClock clock;
    private AudioReplayFrame? last;
    private int serial;
    public int ParameterReads { get; private set; }
    public AudioReplayFrame Start(double now) => Observe(now, true);
    public AudioReplayFrame Observe(double now, bool started = false)
    {
        bool fresh = started || last == null;
        if (fresh) serial++;
        if (!clock.Take(now, fresh)) return last!;
        ParameterReads++;
        return last = new AudioReplayFrame
        {
            Key = "sound:" + serial, Clip = "native", StartedAt = fresh ? now : last!.StartedAt,
            AnchorTime = now, Offset = 0, Duration = 1, Pitch = 1,
        };
    }
}
