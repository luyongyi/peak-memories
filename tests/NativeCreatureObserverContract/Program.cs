using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PeakReplayLab;
int passed = 0;
void Check(bool value) { if (!value) throw new Exception("Trap completion observation assertion failed."); }
void Test(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { Console.Error.WriteLine("FAIL " + name + ": " + e); Environment.Exit(1); }
}
if (args.Contains("--ghost-bodies-only"))
{
    GhostBodyObserverTests.Run(Test);
    Console.WriteLine($"TOTAL: {passed} ghost body lifecycle checks passed. Unity rendering is not simulated.");
    return;
}
Test("end-of-frame yield cannot dirty the trap before its native deactivation", () =>
{
    var source = new TriggerEvent(); int changed = 0; bool? active = null;
    using var observer = new CreatureReplayObserver(_ => { }, changed: go => { changed++; active = go.activeSelf; });
    source.triggerEvent.Callback = () => source.gameObject.SetActive(false);
    var routine = source.StartObservedRoutine();
    Check(routine.MoveNext() && source.gameObject.activeSelf && changed == 0);
    Check(!routine.MoveNext() && changed == 1 && active == false && source.hasActivated && source.hasTriggered);
    Check(!routine.MoveNext() && changed == 1);
});
Test("an immediate native event is observed after the callback in the same tick", () =>
{
    var source = new TriggerEvent { waitForRenderFrame = false }; var order = new List<int>();
    using var observer = new CreatureReplayObserver(_ => { }, changed: _ => order.Add(2));
    source.triggerEvent.Callback = () => order.Add(1);
    Check(!source.StartObservedRoutine().MoveNext() && order.SequenceEqual(new[] { 1, 2 }));
});
Test("the native one-shot guard never emits a second completion", () =>
{
    var source = new TriggerEvent { hasTriggered = true }; int changes = 0;
    using var observer = new CreatureReplayObserver(_ => { }, changed: _ => changes++);
    Check(!source.StartObservedRoutine().MoveNext() && changes == 0);
});
Test("a native event exception retains its original exception and bookkeeping", () =>
{
    var source = new TriggerEvent { waitForRenderFrame = false }; int changes = 0; var expected = new ApplicationException("native callback");
    using var observer = new CreatureReplayObserver(_ => { }, changed: _ => changes++);
    source.triggerEvent.Callback = () => throw expected;
    try { source.StartObservedRoutine().MoveNext(); throw new Exception("Native callback exception was swallowed."); }
    catch (ApplicationException e) { Check(ReferenceEquals(e, expected)); }
    Check(changes == 0 && !source.hasActivated && !source.hasTriggered);
});
Test("failing listeners cannot suppress other observers or native bookkeeping", () =>
{
    var source = new TriggerEvent { waitForRenderFrame = false }; int changes = 0;
    using var broken = new CreatureReplayObserver(_ => { }, changed: _ => throw new Exception("observer"));
    using var healthy = new CreatureReplayObserver(_ => { }, changed: _ => changes++);
    Check(!source.StartObservedRoutine().MoveNext() && changes == 1 && source.hasTriggered);
});
Test("a replay hide observer can receive an already inactive trap", () =>
{
    var source = new TriggerEvent { waitForRenderFrame = false }; bool? active = null;
    using var observer = new CreatureReplayObserver(go => active = go.activeSelf);
    source.triggerEvent.Callback = () => source.gameObject.SetActive(false);
    Check(!source.StartObservedRoutine().MoveNext() && active == false);
});
Test("resource observation failures cannot suppress native one-shot bookkeeping", () =>
{
    var source = new TriggerEvent { waitForRenderFrame = false };
    using var observer = new CreatureReplayObserver(_ => { }); CreatureReplayResources.Fail = true;
    try { Check(!source.StartObservedRoutine().MoveNext() && source.hasTriggered); }
    finally { CreatureReplayResources.Fail = false; }
});
Test("disposing the last observer removes its completion patch", () =>
{
    int changes = 0; var observer = new CreatureReplayObserver(_ => { }, changed: _ => changes++); observer.Dispose(); observer.Dispose();
    var source = new TriggerEvent { waitForRenderFrame = false };
    Check(!source.StartObservedRoutine().MoveNext() && source.hasTriggered && changes == 0);
    var method = AccessTools.EnumeratorMoveNext(AccessTools.DeclaredMethod(typeof(TriggerEvent), "TriggerRoutine"));
    Check(Harmony.GetPatchInfo(method) == null || Harmony.GetPatchInfo(method).Owners.Count == 0);
});
Test("a changed native coroutine layout is rejected without a guessed observation point", () =>
{
    var method = AccessTools.EnumeratorMoveNext(AccessTools.DeclaredMethod(typeof(TriggerEvent), "TriggerRoutine"));
    var transpiler = AccessTools.DeclaredMethod(typeof(CreatureReplayObserver), "ObserveTrapInvocation");
    var invoke = AccessTools.DeclaredMethod(typeof(UnityEngine.Events.UnityEvent), "Invoke");
    foreach (int count in new[] { 0, 2 })
    {
        var codes = Enumerable.Range(0, count).Select(_ => new CodeInstruction(OpCodes.Callvirt, invoke)).ToArray();
        var result = (IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { codes, method })!;
        try { result.ToArray(); throw new Exception("Unrecognized native shape was accepted."); }
        catch (InvalidOperationException) { }
    }
});
Test("independent native creature births are observed without polling update loops", () =>
{
    var seen = new List<UnityEngine.GameObject>();
    using var observer = new CreatureReplayObserver(seen.Add);
    var mob = new Mob(); mob.Awake();
    var scoutmaster = new Scoutmaster(); scoutmaster.OnEnable(); scoutmaster.Start();
    var antlion = new Antlion(); antlion.Start();
    var venus = new VenusFlyTrap(); venus.Start();
    var tumbleweed = new TumbleWeed(); tumbleweed.Start();
    var raven = new Peak.Island.VoiceThief(); raven.Start();
    var ghost = new Peak.ScoutmasterGhostOrbiter(); ghost.Start();
    var capy = new Capybara(); capy.OnEnable();
    var mirage = new MirageLuggage(); mirage.OnEnable();
    Check(seen.Count == 10 && seen.Count(go => ReferenceEquals(go, scoutmaster.gameObject)) == 2);
    foreach (var go in new[] { mob.gameObject, scoutmaster.gameObject, antlion.gameObject, venus.gameObject,
        tumbleweed.gameObject, raven.gameObject, ghost.gameObject, capy.gameObject, mirage.gameObject }) Check(seen.Contains(go));
});
Test("skeleton birth is observed after the native ragdoll pose has been copied", () =>
{
    var skeleton = new Skelleton(); int seen = 0;
    using var observer = new CreatureReplayObserver(go => { Check(skeleton.NativePoseCopied); Check(ReferenceEquals(go, skeleton.gameObject)); seen++; });
    skeleton.SpawnSkelly(); Check(seen == 1 && skeleton.NativePoseCopied);
});
Test("player zombie conversion invalidates the existing variant through changed callbacks", () =>
{
    var zombie = new MushroomZombie(); int born = 0, changed = 0;
    using var observer = new CreatureReplayObserver(_ => born++, changed: go => { Check(ReferenceEquals(go, zombie.gameObject)); changed++; });
    zombie.RPC_Arise(); Check(changed == 1 && born == 0);
});
NativeEnvironmentObserverTests.Run(Test);
LuggageObserverTests.Run(Test);
ScoutmasterActivityTests.Run(Test);
TrapSettingsObserverTests.Run(Test);
CreatureDormancyActivityTests.Run(Test);
GhostBodyObserverTests.Run(Test);
FaunaEventObserverTests.Run(Test);
LowRateActionObserverTests.Run(Test);
Console.WriteLine($"TOTAL: {passed} native lifecycle completion checks passed. Managed coroutine scheduling is tested; Unity rendering is not simulated.");
