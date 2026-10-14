using System;
using System.Linq;
using PeakReplayLab;
using UnityEngine;
using Object = UnityEngine.Object;

int passed = 0;
void Check(bool value, string message = "actual environment capture scope contract failed") { if (!value) throw new Exception(message); }
GameObject Child(GameObject root, string name) { var value = new GameObject(name); value.transform.parent = root.transform; return value; }
(GameObject Coast, GameObject Future) Map()
{
    var coast = new GameObject("coast"); var future = new GameObject("future") { activeSelf = false };
    MapHandler.Instance = new MapHandler { segments = new[] { new MapHandler.MapSegment(coast), new MapHandler.MapSegment(future) } };
    return (coast, future);
}
EnvironmentReplayFrame At(double time) { Time.timeAsDouble = time; return EnvironmentReplayCapture.Sample(); }
void Drain(CaptureSegmentScope scope) { for (int i = 0; i < 64; i++) scope.Drain(); }
void Test(string name, Action action)
{
    try
    {
        EnvironmentReplayCapture.Reset(); Object.NativeRegistry.Clear(); Time.timeAsDouble = 0;
        EnvironmentComponents.ParentQueries = EnvironmentComponents.TreeQueries = 0; Transform.ParentReads = 0;
        action(); passed++; Console.WriteLine("PASS " + name);
    }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); System.Environment.Exit(1); }
    finally { EnvironmentReplayCapture.Reset(); CaptureSegmentScope.Active?.Dispose(); MapHandler.Instance = null; }
}

Test("actual future wind storm and lava create no sampled state or numeric reads before map activation", () =>
{
    var map = Map(); var current = new WindChillZone(Child(map.Coast, "current-wind"));
    var future = new WindChillZone(Child(map.Future, "future-wind")); var storm = new StormVisual(Child(future.gameObject, "future-storm"));
    new LavaRising(Child(map.Future, "future-controller"), Child(map.Future, "future-lava"));
    using var scope = new CaptureSegmentScope(); var first = At(0); Drain(scope); var sampled = At(.033);
    Check(first.Winds.Length == 0 && first.Lava.Length == 0 && sampled.Winds.Length == 1 && sampled.Lava.Length == 0);
    Check(current.StateReads > 0 && future.StateReads == 0 && storm.StateReads == 0);
    map.Future.SetActive(true); scope.Observe(); Drain(scope); var active = At(.066);
    Check(active.Winds.Length == 2 && active.Lava.Length == 1 && active.Winds.Single(w => w.Key.EndsWith("future-wind")).Storms.Length == 1);
});
Test("actual rising and moving lava sharing one target retain both component descriptors and frames", () =>
{
    var map = Map(); var target = Child(map.Coast, "shared-target");
    new LavaRising(Child(map.Coast, "rising-controller"), target); new MovingLava(target);
    using var scope = new CaptureSegmentScope(); Check(At(0).Lava.Length == 0); Drain(scope);
    var frame = At(.033); Check(frame.Lava.Length == 2 && frame.Lava.Select(value => value.Kind).OrderBy(value => value).SequenceEqual(new[] { 0, 1 }));
});
Test("actual queued shared-target admission rebinds a changed controller without dictionary mutation or old-target rollback", () =>
{
    var map = Map(); var old = Child(map.Future, "old-shared-target"); old.transform.position = new(10, 0, 0);
    var next = Child(map.Coast, "new-target"); next.transform.position = new(20, 0, 0);
    var a = new LavaRising(Child(map.Coast, "controller-a"), old); new LavaRising(Child(map.Coast, "controller-b"), old);
    using var scope = new CaptureSegmentScope(); Check(At(0).Lava.Length == 0);
    a.lava = new Rigidbody(next); map.Future.SetActive(true); scope.Observe(); Drain(scope);
    var frame = At(.033); Check(frame.Lava.Length == 2);
    Check(frame.Lava.Single(value => value.Key.EndsWith("controller-a")).Position[0] == 20);
    Check(frame.Lava.Single(value => value.Key.EndsWith("controller-b")).Position[0] == 10);
});
Test("actual live lava retarget to an inactive map retires old binding then acquires a fresh admitted baseline", () =>
{
    var map = Map(); var old = Child(map.Coast, "old-target"); old.transform.position = new(10, 0, 0);
    var next = Child(map.Future, "new-target"); next.transform.position = new(20, 0, 0);
    var rising = new LavaRising(Child(map.Coast, "controller"), old);
    using var scope = new CaptureSegmentScope(); At(0); Drain(scope); var history = At(.033);
    rising.lava = new Rigidbody(next); EnvironmentReplayObserver.Emit(rising); Check(At(.066).Lava.Length == 0);
    map.Future.SetActive(true); scope.Observe(); Drain(scope); var frame = At(.1);
    Check(frame.Lava.Length == 1 && frame.Lava[0].Position[0] == 20 && history.Lava[0].Position[0] == 10);
});
Test("actual globally owned wind cannot admit storm subtree under future map and retires it independently", () =>
{
    var map = Map(); var global = new WindChillZone(new GameObject("global-wind")); map.Future.transform.parent = global.transform;
    var storm = new StormVisual(Child(map.Future, "future-storm"));
    using var scope = new CaptureSegmentScope(); At(0); Drain(scope); var before = At(.033);
    Check(before.Winds.Single().Storms.Length == 0 && storm.StateReads == 0);
    map.Future.SetActive(true); scope.Observe(); Drain(scope); var active = At(.066);
    Check(active.Winds.Single().Storms.Length == 1 && storm.StateReads > 0);
    int reads = storm.StateReads; map.Future.SetActive(false); scope.Observe(); var after = At(.1);
    Check(after.Winds.Single().Storms.Length == 0 && storm.StateReads == reads && active.Winds.Single().Storms.Length == 1);
});
Test("actual environmental map retirement preserves historical wind storm and lava snapshots", () =>
{
    var map = Map(); var old = new WindChillZone(Child(map.Coast, "old-wind")); new StormVisual(Child(old.gameObject, "old-storm"));
    new LavaRising(Child(map.Coast, "old-controller"), Child(map.Coast, "old-lava")); new WindChillZone(Child(map.Future, "next-wind"));
    using var scope = new CaptureSegmentScope(); At(0); Drain(scope); var history = At(.033);
    map.Coast.SetActive(false); map.Future.SetActive(true); scope.Observe(); Drain(scope); var next = At(.066);
    Check(next.Winds.Length == 1 && next.Winds[0].Key.EndsWith("next-wind") && next.Lava.Length == 0);
    Check(history.Winds.Single().Enabled && history.Winds.Single().Storms.Length == 1 && history.Lava.Single().Active);
});
Test("actual environment Awake and Start discoveries respect the shared four-object baseline budget", () =>
{
    var map = Map(); var sources = Enumerable.Range(0, 16).Select(i => new WindChillZone(Child(map.Coast, "wind-" + i))).ToArray();
    using var scope = new CaptureSegmentScope(); At(0);
    foreach (var source in sources) EnvironmentReplayObserver.Emit(source);
    Check(At(.033).Winds.Length == 0 && sources.All(source => source.StateReads == 0));
    scope.Drain(); var first = At(.066); Check(first.Winds.Length > 0 && first.Winds.Length <= 4);
    Drain(scope); Check(At(.1).Winds.Length == 16);
});
Test("actual global fog is available immediately while fog owned by known maps remains budgeted or excluded", () =>
{
    var map = Map(); var global = new OrbFogHandler(new GameObject("global-fog")); new FogSphere(Child(global.gameObject, "global-sphere"));
    var known = new OrbFogHandler(Child(map.Coast, "known-fog")); new FogSphere(Child(known.gameObject, "known-sphere"));
    var future = new OrbFogHandler(Child(map.Future, "future-fog")); new FogSphere(Child(future.gameObject, "future-sphere"));
    using var scope = new CaptureSegmentScope(); var frame = At(0);
    Check(frame.Fog != null && frame.Fog.Key.EndsWith("global-sphere") && EnvironmentComponents.TreeQueries == 1);
    Drain(scope); At(.033); Check(EnvironmentComponents.TreeQueries == 2);
});
Test("actual destroyed storm or lava component retires only its binding while a shared-target peer remains", () =>
{
    var map = Map(); var wind = new WindChillZone(Child(map.Coast, "wind")); var storm = new StormVisual(Child(wind.gameObject, "storm"));
    var target = Child(map.Coast, "shared"); var rising = new LavaRising(Child(map.Coast, "rising"), target); var moving = new MovingLava(target);
    using var scope = new CaptureSegmentScope(); At(0); Drain(scope); var history = At(.033);
    int stormReads = storm.StateReads; storm.Destroyed = true; rising.Destroyed = true; var after = At(.066);
    Check(after.Winds.Single().Storms.Length == 0 && storm.StateReads == stormReads && after.Lava.Single().Kind == 1);
    var orphan = new StormVisual(Child(wind.gameObject, "orphan-storm")); EnvironmentReplayObserver.Emit(orphan); At(.1); Drain(scope); At(.133);
    int orphanReads = orphan.StateReads; wind.Destroyed = true; moving.Destroyed = true; var retired = At(.166);
    Check(retired.Lava.Length == 0 && retired.Winds.Length == 0 && orphan.StateReads == orphanReads);
    Check(history.Lava.Length == 2 && history.Winds.Single().Storms.Length == 1);
});
Test("actual steady environmental capture uses cached component directories and reset cancels queued admissions", () =>
{
    var map = Map(); var wind = new WindChillZone(Child(map.Coast, "wind")); new StormVisual(Child(wind.gameObject, "storm"));
    using var scope = new CaptureSegmentScope(); At(0); Drain(scope); At(.033);
    EnvironmentComponents.ParentQueries = EnvironmentComponents.TreeQueries = Transform.ParentReads = 0;
    for (int i = 1; i <= 600; i++) { scope.Observe(); At(.033 + i / 30d); }
    Check(EnvironmentComponents.ParentQueries == 0 && EnvironmentComponents.TreeQueries == 0 && Transform.ParentReads == 0);
    EnvironmentReplayCapture.Reset(); At(30); EnvironmentReplayCapture.Reset(); Drain(scope);
    Check(EnvironmentReplayCapture.LastObservation == null);
});
Test("actual initial-only environment holds static baseline but preserves native wind epoch and explicit force", () =>
{
    var map=Map(); var wind=new WindChillZone(Child(map.Coast,"wind"));
    using var scope=new CaptureSegmentScope(); At(0); Drain(scope); var before=At(.033);
    Check(EnvironmentReplayCapture.InitialBaselineReady);
    using(CaptureBootstrapWorkBudget.Begin(initialOnly:true))
    {
        Check(ReferenceEquals(before,At(.034)));
        wind.windActive=true; var after=At(.035);
        Check(after.Winds.Single().Active && !before.Winds.Single().Active);
        Time.timeAsDouble=.036; var forced=EnvironmentReplayCapture.Sample(force:true);
        Check(forced.SampleTime==.036);
    }
});
Test("actual new admitted environment source is not ready until its first native state has been sampled", () =>
{
    var map=Map(); new WindChillZone(Child(map.Coast,"first-wind"));
    using var scope=new CaptureSegmentScope(); At(0); Drain(scope); At(.033); Check(EnvironmentReplayCapture.InitialBaselineReady);
    var next=new WindChillZone(Child(map.Coast,"new-wind")); EnvironmentReplayObserver.Emit(next); At(.034); Drain(scope);
    Check(!EnvironmentReplayCapture.InitialBaselineReady);
    Check(At(.066).Winds.Length==2 && EnvironmentReplayCapture.InitialBaselineReady);
});
Console.WriteLine($"TOTAL: {passed} actual environment/segment capture checks passed. Unity rendering and live frame timing are not simulated.");
