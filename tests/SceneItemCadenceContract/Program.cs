using PeakReplayLab;
using UnityEngine;

int passed = 0;
void Check(bool condition, string message = "Scene item cadence assertion failed") { if (!condition) throw new Exception(message); }
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); Environment.Exit(1); }
}
IEnumerable<double> SceneTimes(Func<int, double> step, int count = 6000)
{
    var global = new SampleClock(60); var scene = new SceneCaptureSchedule(); double now = 0;
    for (int i = 0; i < count; i++)
    {
        now += step(i);
        if (global.ShouldCapture(now) && scene.Due(now)) yield return now;
    }
}

Test("every real jittered Scene30 observation retains active roots children lights shaders and Animator probes", () =>
{
    foreach (Func<int, double> step in new Func<int, double>[]
        { i => 1d / 60 + (i % 3 - 1) * .0015, i => i % 2 == 0 ? .015 : .019, _ => 1d / 58 })
    {
        var activity = new ItemActivityPolicy(); var appearance = new NativeCapturePolicy(0);
        var source = new Animator(); var animation = new NativeCaptureAnimationActivity([source]);
        int scenes = 0, roots = 0, children = 0, lights = 0, shaders = 0;
        foreach (double now in SceneTimes(step))
        {
            scenes++; source.State.normalizedTime = (float)now;
            if (activity.AnimatedRootDue(now, followSceneClock: true)) { roots++; activity.RootObserved(now); }
            if (activity.FastChildrenDue(now, followSceneClock: true)) { children++; activity.ChildrenObserved(now); }
            if (activity.LightDue(now, followSceneClock: true)) { lights++; activity.LightObserved(now); }
            if (appearance.AnimatedDue(now, followSceneClock: true)) { shaders++; appearance.AnimatedObserved(now); }
            animation.Read(now, followSceneClock: true);
        }
        Check(scenes > 2800 && roots == scenes && children == scenes && lights == scenes && shaders == scenes);
        Check(animation.ProbeCount == scenes && source.StateQueries == scenes);
    }
});
Test("standalone legacy helpers retain deadlines while Scene callers avoid the observed 20Hz jitter alias", () =>
{
    var legacy = new ItemActivityPolicy(); var scheduled = new ItemActivityPolicy(); int oldReads = 0, reads = 0;
    foreach (double now in SceneTimes(i => 1d / 60 + (i % 3 - 1) * .0015))
    {
        if (legacy.FastChildrenDue(now)) { oldReads++; legacy.ChildrenObserved(now); }
        if (scheduled.FastChildrenDue(now, followSceneClock: true)) { reads++; scheduled.ChildrenObserved(now); }
    }
    Check(reads == 3000 && oldReads == 2000, $"Scene={reads}, legacy={oldReads}");
});
Test("two paths in one Scene observation do not repeat light shader geometry or Animator reads", () =>
{
    var activity = new ItemActivityPolicy(); activity.LightObserved(1); activity.ChildrenObserved(1); activity.RootObserved(1);
    Check(!activity.LightDue(1, followSceneClock: true)); Check(!activity.FastChildrenDue(1, true)); Check(!activity.AnimatedRootDue(1, true));
    var appearance = new NativeCapturePolicy(0); appearance.FullObserved(1);
    Check(!appearance.AnimatedDue(1, true) && !appearance.NeedsRead(1, true, true, true));
    var source = new Animator(); var animation = new NativeCaptureAnimationActivity([source]);
    animation.Read(1, followSceneClock: true); animation.Read(1, followSceneClock: true);
    Check(animation.ProbeCount == 1 && source.StateQueries == 1);
});
Test("Scene animated reads leave full metadata fallback at its original spread deadline", () =>
{
    var appearance = new NativeCapturePolicy(28); appearance.FullObserved(0);
    foreach (double now in SceneTimes(_ => 1d / 60, 30))
    {
        Check(!appearance.FullDue(now, true, false)); appearance.AnimatedObserved(now);
    }
    Check(!appearance.FullDue(.7, true, false)); Check(appearance.FullDue(.75, true, false));
    Check(!appearance.NeedsRead(.6, false, true, true)); Check(!appearance.NeedsRead(.6, true, false, true));
});
Test("unchanged enabled animation phases do not create new semantic wake revisions", () =>
{
    var source = new Animator(); var animation = new NativeCaptureAnimationActivity([source]);
    animation.Read(0, followSceneClock: true); long revision = animation.Revision;
    foreach (double now in SceneTimes(i => 1d / 60 + (i % 3 - 1) * .0015, 300))
    { source.State.normalizedTime = (float)now; Check(animation.Read(now, followSceneClock: true)); }
    Check(animation.Revision == revision);
});
Test("finished or disabled animations stay dormant after one state boundary", () =>
{
    var source = new Animator(); source.State.loop = false; source.State.normalizedTime = 1;
    var animation = new NativeCaptureAnimationActivity([source]); animation.Read(0, followSceneClock: true); long revision = animation.Revision;
    foreach (double now in SceneTimes(_ => 1d / 60, 120))
    { source.State.normalizedTime = 1 + (float)now; Check(!animation.Read(now, followSceneClock: true)); }
    Check(animation.Revision == revision);
    source.isActiveAndEnabled = false; animation.Read(3, followSceneClock: true); revision = animation.Revision;
    animation.Read(3.02, followSceneClock: true); Check(!animation.Advancing && animation.Revision == revision);
    Check(ItemActivityPolicy.CanSleep(true, false, false, animation.Advancing, false));
});
Test("audited interaction controllers release every Animator query after a finite idle lease", () =>
{
    var source = new Animator(); source.State.loop = false; source.State.normalizedTime = 1;
    var animation = new NativeCaptureAnimationActivity([source], eventDriven: true); animation.Wake(0);
    for (int i = 0; i <= 12; i++) Check(!animation.Read(i / 30d, followSceneClock: true));
    long probes = animation.ProbeCount; int queries = source.StateQueries;
    for (int i = 13; i <= 6012; i++) Check(!animation.Read(i / 30d, followSceneClock: true));
    Check(probes <= 8 && animation.ProbeCount == probes && source.StateQueries == queries);
    source.State.fullPathHash = 2; source.State.normalizedTime = 0;
    animation.Wake(201, .25); Check(animation.Read(201, followSceneClock: true));
    Check(animation.ProbeCount == probes + 1 && source.StateQueries == queries + 1);
});
Test("the native Grave empty looping state cannot claim animation from normalized time alone", () =>
{
    var source = new Animator { ClipCount = 0 };
    var animation = new NativeCaptureAnimationActivity([source], eventDriven: true); animation.Wake(0);
    for (int i = 0; i < 6000; i++)
    { source.State.normalizedTime = i / 30f; Check(!animation.Read(i / 30d, followSceneClock: true)); }
    Check(animation.ProbeCount <= 8 && source.StateQueries <= 8 && source.ClipCountQueries <= 8);
    source.Transition = true; animation.Wake(201, 2); Check(animation.Read(201, followSceneClock: true));
    source.ClipCount = 1; source.Transition = false; source.State.loop = false; source.State.normalizedTime = .1f;
    Check(animation.Read(201 + 1d / 30, followSceneClock: true));
});
Test("actual finite and looping interaction animation outlives its event lease and stops at its native terminal pose", () =>
{
    var source = new Animator(); source.State.loop = false;
    var animation = new NativeCaptureAnimationActivity([source], eventDriven: true); animation.Wake(0, .25);
    for (int i = 0; i < 90; i++)
    { source.State.normalizedTime = i / 90f; Check(animation.Read(i / 30d, followSceneClock: true)); }
    source.State.normalizedTime = 1; Check(animation.Read(3, followSceneClock: true)); // The real terminal movement is retained.
    source.State.normalizedTime = 2; Check(!animation.Read(91d / 30, followSceneClock: true));
    long probes = animation.ProbeCount; int queries = source.StateQueries;
    for (int i = 92; i < 6092; i++) Check(!animation.Read(i / 30d, followSceneClock: true));
    Check(animation.ProbeCount == probes && source.StateQueries == queries);
    source.State.fullPathHash = 3; source.State.loop = true; animation.Wake(204, .25);
    for (int i = 0; i < 300; i++)
    { source.State.normalizedTime = i / 30f; Check(animation.Read(204 + i / 30d, followSceneClock: true)); }
    Check(animation.ProbeCount == probes + 300);
    source.isActiveAndEnabled = false; Check(!animation.Read(214, followSceneClock: true));
    probes = animation.ProbeCount; animation.Read(215, followSceneClock: true); Check(animation.ProbeCount == probes);
    source.isActiveAndEnabled = true; animation.Wake(216); Check(animation.Read(216, followSceneClock: true));
});
Test("static event-only items never enter the active work set because Scene time advances", () =>
{
    var work = new ItemCaptureWorkSet<object>(); var item = new object(); work.Wake(item); work.Sleep(item);
    int reads = 0;
    foreach (double now in SceneTimes(i => 1d / 60 + (i % 3 - 1) * .0015))
        foreach (var active in work.Active) { _ = now; _ = active; reads++; }
    Check(reads == 0 && work.SleepingCount == 1 && ItemSleepPolicy.EventOnly(true, true, true));
    work.Wake(item); Check(work.Active.Count == 1 && work.SleepingCount == 0);
});
Test("unknown capability safety polls retain their half-second spread and cannot become Scene30 work", () =>
{
    var policy = new ItemActivityPolicy(); int reads = 0; double last = double.NegativeInfinity;
    foreach (double now in SceneTimes(i => 1d / 60 + (i % 3 - 1) * .0015, 600))
    {
        if (!policy.UnknownDue(now)) continue;
        Check(now - last >= .5); reads++; last = now; policy.UnknownObserved(now, 9);
    }
    Check(reads > 10 && reads < 20);
});
Test("real event boundaries can still force an immediate same-time snapshot", () =>
{
    var activity = new ItemActivityPolicy(); activity.LightObserved(1);
    Check(activity.LightDue(1, dirty: true, followSceneClock: true));
    var appearance = new NativeCapturePolicy(0); appearance.FullObserved(1);
    Check(appearance.FullDue(1, true, true));
    var source = new Animator(); var animation = new NativeCaptureAnimationActivity([source]); animation.Read(1, followSceneClock: true);
    source.State.fullPathHash = 2; long revision = animation.Revision; animation.Read(1, force: true, followSceneClock: true);
    Check(animation.ProbeCount == 2 && animation.Revision > revision);
    animation.Invalidate(); animation.Read(1, followSceneClock: true); Check(animation.ProbeCount == 3);
});
Test("pause and multi-second stalls produce no duplicate or catch-up geometry probes", () =>
{
    var scene = new SceneCaptureSchedule(); var source = new Animator(); var animation = new NativeCaptureAnimationActivity([source]);
    void Capture(double now) { if (scene.Due(now)) animation.Read(now, followSceneClock: true); }
    Capture(1); for (int i = 0; i < 100; i++) Capture(1); Check(animation.ProbeCount == 1);
    Capture(10); Capture(10); Check(animation.ProbeCount == 2);
});
Test("cached active policy and Animator reads allocate no arrays or query directories", () =>
{
    var activity = new ItemActivityPolicy(); var appearance = new NativeCapturePolicy(0);
    var source = new Animator(); var animation = new NativeCaptureAnimationActivity([source]);
    void Read(double now)
    {
        activity.LightDue(now, followSceneClock: true); activity.LightObserved(now);
        activity.FastChildrenDue(now, true); activity.ChildrenObserved(now); activity.AnimatedRootDue(now, true); activity.RootObserved(now);
        appearance.AnimatedDue(now, true); appearance.AnimatedObserved(now); animation.Read(now, followSceneClock: true);
    }
    for (int i = 0; i < 100; i++) Read(i);
    long before = GC.GetAllocatedBytesForCurrentThread(); for (int i = 100; i < 6100; i++) Read(i);
    Check(GC.GetAllocatedBytesForCurrentThread() == before);
});
Console.WriteLine($"TOTAL: {passed} scene item cadence checks passed. Native animation output is not simulated.");
