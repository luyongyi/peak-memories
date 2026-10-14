using PeakReplayLab;
using System.Collections;

int passed = 0;
void Check(bool value, string message = "Bootstrap assertion failed") { if (!value) throw new Exception(message); }
void Test(string name, Action action)
{
    ReplaySafety.Active = false; Peak.Quicksave.Restores = 0; Peak.Quicksave.ShouldUseSaveData = false;
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); Environment.Exit(1); }
}
IEnumerator Nothing() { yield break; }
Test("fresh host waits for native map, three frames and explicit snapshot completion", () =>
{
    var gate = new CaptureBootstrapGate(0);
    gate.ObserveHost(false, false, 10, .1); Check(!gate.CanPrepare && !gate.AllowHostSpawn(99, .2));
    gate.ObserveHost(true, false, 100, .3); Check(gate.CanPrepare && !gate.AllowHostSpawn(102, .4));
    Check(!gate.AllowHostSpawn(103, .5), "pending snapshots must hold even after native frame delay");
    gate.CompleteBaseline(); Check(gate.AllowHostSpawn(103, .6));
});
Test("resume preparation waits for restored map and only host body consumes duplicate-restore token", () =>
{
    var gate = new CaptureBootstrapGate(0); gate.ObserveHost(true, true, 10, .1);
    Check(!gate.CanPrepare && !gate.NeedsRestore(12) && gate.NeedsRestore(13));
    gate.Restored(); Check(gate.CanPrepare && gate.AllowNativeRestore(false));
    gate.CompleteBaseline(); Check(gate.AllowHostSpawn(13, .2));
    Check(!gate.AllowNativeRestore(true)); Check(gate.AllowNativeRestore(true), "only one native duplicate may be suppressed");
});
Test("timeout fails open for spawning and never declares incomplete baseline ready", () =>
{
    var gate = new CaptureBootstrapGate(0, 2); gate.ObserveHost(true, false, 10, .1);
    Check(!gate.AllowHostSpawn(20, 1.999)); Check(gate.AllowHostSpawn(20, 2));
    Check(gate.Failed && !gate.BaselineReady && !gate.CanPrepare && gate.Failure.Length != 0);
});
Test("cancellation releases gate without manufacturing baseline completion", () =>
{
    var gate = new CaptureBootstrapGate(0); gate.ObserveHost(true, true, 1, .1); gate.Cancel();
    Check(gate.AllowHostSpawn(1, .2) && !gate.BaselineReady && !gate.CanPrepare && !gate.Failed);
});
Test("late native restore requirement invalidates an earlier map baseline", () =>
{
    var gate = new CaptureBootstrapGate(0); gate.ObserveMap(true, 1, .1); gate.CompleteBaseline();
    gate.ObserveHost(true, true, 4, .2);
    Check(!gate.BaselineReady && !gate.CanPrepare && gate.NeedsRestore(4) && !gate.AllowHostSpawn(4, .2));
});
Test("real teammate frames remain recordable while the local host spawn is held", () =>
{
    Check(CaptureBootstrapGate.AllowLiveCapture(true, false, false, true));
    Check(!CaptureBootstrapGate.AllowLiveCapture(true, false, false, false));
    Check(!CaptureBootstrapGate.AllowLiveCapture(false, true, false, true));
    Check(CaptureBootstrapGate.AllowLiveCapture(true, true, false, false));
    Check(!CaptureBootstrapGate.AllowLiveCapture(true, true, true, false));
});
Test("writer gates incomplete solo bootstrap but preserves real client or teammate prefixes and rejects failures", () =>
{
    Check(!CaptureBootstrapGate.AllowRecordingStart(false, true, false, false));
    Check(CaptureBootstrapGate.AllowRecordingStart(false, true, true, false));
    Check(CaptureBootstrapGate.AllowRecordingStart(false, true, false, true));
    Check(CaptureBootstrapGate.AllowRecordingStart(false, false, false, true));
    Check(!CaptureBootstrapGate.AllowRecordingStart(true, true, true, true));
});
Test("actual Harmony host prefix holds the complete native spawn body", () =>
{
    bool ready = false; var native = new CharacterSpawner();
    using var hooks = new CaptureBootstrapHooks(() => ready, _ => true, Nothing, _ => throw new Exception("unexpected hook failure"));
    native.Tick(); Check(native.Spawned == 0 && Peak.Quicksave.Restores == 0);
    ready = true; native.Tick(); Check(native.Spawned == 1);
});
Test("actual Harmony resume restores exactly once before native spawn without suppressing unrelated restore calls", () =>
{
    Peak.Quicksave.ShouldUseSaveData = true; var gate = new CaptureBootstrapGate(0);
    gate.ObserveHost(true, true, 0, 0); bool ready = false; var native = new CharacterSpawner();
    using var hooks = new CaptureBootstrapHooks(() =>
    {
        if (gate.NeedsRestore(3)) { Peak.Quicksave.PopulateMapAndPlayerStates(); gate.Restored(); }
        if (ready && !gate.BaselineReady) gate.CompleteBaseline();
        return gate.AllowHostSpawn(3, .1);
    }, gate.AllowNativeRestore, Nothing, _ => throw new Exception("unexpected hook failure"));
    native.Tick(); Check(Peak.Quicksave.Restores == 1 && native.Spawned == 0);
    Peak.Quicksave.PopulateMapAndPlayerStates(); Check(Peak.Quicksave.Restores == 2);
    ready = true; native.Tick(); Check(Peak.Quicksave.Restores == 2 && native.Spawned == 1);
    Peak.Quicksave.PopulateMapAndPlayerStates(); Check(Peak.Quicksave.Restores == 3);
});
Test("actual Harmony finalizer retains native exception and clears host invocation context", () =>
{
    var native = new CharacterSpawner { ThrowInside = true }; var inside = new List<bool>();
    using var hooks = new CaptureBootstrapHooks(() => true, value => { inside.Add(value); return true; }, Nothing, _ => { });
    Peak.Quicksave.ShouldUseSaveData = true;
    try { native.Tick(); throw new Exception("native exception swallowed"); }
    catch (InvalidOperationException error) { Check(error.Message == "native failure"); }
    Peak.Quicksave.PopulateMapAndPlayerStates(); Check(inside.SequenceEqual(new[] { true, false }));
});
Test("failure after successful early restore opens native spawn without restoring the save a second time", () =>
{
    Peak.Quicksave.ShouldUseSaveData = true; var gate = new CaptureBootstrapGate(0); gate.ObserveHost(true, true, 0, 0);
    var native = new CharacterSpawner();
    using var hooks = new CaptureBootstrapHooks(() =>
    {
        if (gate.NeedsRestore(3)) { Peak.Quicksave.PopulateMapAndPlayerStates(); gate.Restored(); gate.Fail("initial-state failure"); }
        return gate.AllowHostSpawn(3, .1);
    }, gate.AllowNativeRestore, Nothing, _ => throw new Exception("unexpected hook failure"));
    native.Tick(); Check(native.Spawned == 1 && Peak.Quicksave.Restores == 1 && gate.Failed && !gate.BaselineReady);
});
Test("actual loading iterator preserves native processes and runAfter waits for appended cooperative gate", () =>
{
    var order = new List<string>(); bool ready = false;
    IEnumerator Native() { order.Add("native"); yield return null; }
    IEnumerator Wait() { order.Add("gate"); while (!ready) yield return null; order.Add("ready"); }
    using var hooks = new CaptureBootstrapHooks(() => true, _ => true, Wait, _ => throw new Exception("unexpected hook failure"));
    var routine = new LoadingScreen().LoadingRoutine(() => order.Add("runAfter"), new[] { Native() });
    Check(routine.MoveNext()); Check(routine.MoveNext()); Check(!order.Contains("runAfter"));
    ready = true; Check(!routine.MoveNext());
    Check(order.SequenceEqual(new[] { "native", "gate", "ready", "runAfter" }));
});
Test("replay safety bypasses every bootstrap hook", () =>
{
    using var hooks = new CaptureBootstrapHooks(() => throw new Exception("must not inspect replay host"),
        _ => throw new Exception("must not inspect replay restore"), () => throw new Exception("must not append replay wait"), _ => throw new Exception("unexpected hook failure"));
    ReplaySafety.Active = true; Peak.Quicksave.ShouldUseSaveData = true;
    var native = new CharacterSpawner(); native.Tick(); Check(native.Spawned == 1 && Peak.Quicksave.Restores == 1);
    var routine = new LoadingScreen().LoadingRoutine(() => { }, Array.Empty<IEnumerator>()); Check(!routine.MoveNext());
});
Test("hook failure opens native spawn and reports failure; disposal removes observers", () =>
{
    int errors = 0; var native = new CharacterSpawner();
    using (var hooks = new CaptureBootstrapHooks(() => throw new InvalidOperationException("prepare failure"), _ => true, Nothing, _ => errors++))
    { native.Tick(); Check(errors == 1 && native.Spawned == 1); }
    native.Tick(); Check(errors == 1 && native.Spawned == 2);
});
Test("first-read budget shares one deadline across tracks without blocking normal reads", () =>
{
    long now = 100; Check(!CaptureBootstrapWorkBudget.Active && CaptureBootstrapWorkBudget.TryBeginRead());
    using (CaptureBootstrapWorkBudget.Begin(2, () => now, 1000))
    {
        Check(CaptureBootstrapWorkBudget.Active && CaptureBootstrapWorkBudget.TryBeginRead());
        now = 101; Check(CaptureBootstrapWorkBudget.TryBeginRead());
        now = 102; Check(!CaptureBootstrapWorkBudget.TryBeginRead() && !CaptureBootstrapWorkBudget.HasTime);
        now = 110; Check(!CaptureBootstrapWorkBudget.TryBeginRead());
    }
    Check(!CaptureBootstrapWorkBudget.Active && CaptureBootstrapWorkBudget.TryBeginRead());
});
Test("nested work scopes cannot reset or multiply the loading frame budget", () =>
{
    long now = 0;
    using (CaptureBootstrapWorkBudget.Begin(2, () => now, 1000))
    {
        now = 2;
        using (CaptureBootstrapWorkBudget.Begin(100, () => now, 1000)) Check(!CaptureBootstrapWorkBudget.TryBeginRead());
        Check(CaptureBootstrapWorkBudget.Active && !CaptureBootstrapWorkBudget.TryBeginRead());
    }
    Check(!CaptureBootstrapWorkBudget.Active);
});
Test("budget exception cleanup and invalid arguments do not leak active capture scope", () =>
{
    try { using (CaptureBootstrapWorkBudget.Begin()) throw new InvalidOperationException("read failure"); }
    catch (InvalidOperationException) { }
    Check(!CaptureBootstrapWorkBudget.Active);
    foreach (double invalid in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
    {
        try { using var ignored = CaptureBootstrapWorkBudget.Begin(invalid); throw new Exception("invalid budget accepted"); }
        catch (ArgumentOutOfRangeException) { }
    }
    Check(!CaptureBootstrapWorkBudget.Active);
});
Test("initial-only preparation flag is scoped and nested calls cannot change its policy", () =>
{
    Check(!CaptureBootstrapWorkBudget.InitialOnly);
    using (CaptureBootstrapWorkBudget.Begin(2, initialOnly: true))
    {
        Check(CaptureBootstrapWorkBudget.InitialOnly);
        using (CaptureBootstrapWorkBudget.Begin()) Check(CaptureBootstrapWorkBudget.InitialOnly);
    }
    Check(!CaptureBootstrapWorkBudget.InitialOnly);
    using (CaptureBootstrapWorkBudget.Begin())
    {
        using (CaptureBootstrapWorkBudget.Begin(initialOnly: true)) Check(!CaptureBootstrapWorkBudget.InitialOnly);
    }
});
Test("real static action behind hundreds of deferred initial checkpoints bypasses expired shared budget", () =>
{
    var directory=new StaticCaptureDirectory<object,object>(); int reads=0;
    for(int i=0;i<400;i++) directory.Register(i,new object());
    directory.Changed(399,true);
    var frames=directory.Capture(0,(source,old)=>{ reads++; return source; },beginInitialRead:()=>false);
    Check(reads==1 && frames.Length==1 && !directory.InitialBaselineReady);
    directory.Capture(.033,(source,old)=>{ reads++; return source; },beginInitialRead:()=>false);
    Check(reads==1,"consumed urgent token was replayed as an old normal queue entry");
    Check(directory.Capture(.066,(source,old)=>source).Length==400 && directory.InitialBaselineReady);
});
Test("urgent static read preserves a reentrant event for the next scene observation", () =>
{
    var directory=new StaticCaptureDirectory<object,object>(); var source=new object(); directory.Register(1,source); directory.Changed(1,true); int reads=0;
    object Read(object value,object? old) { if(++reads==1) directory.Changed(1,true); return value; }
    directory.Capture(0,Read,beginInitialRead:()=>false); Check(reads==1);
    directory.Capture(.033,Read,beginInitialRead:()=>false); Check(reads==2 && directory.InitialBaselineReady);
    directory.Capture(.066,Read,beginInitialRead:()=>false); Check(reads==2);
});
Console.WriteLine($"{passed} native capture bootstrap contracts passed.");
