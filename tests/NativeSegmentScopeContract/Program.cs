using PeakReplayLab;
using UnityEngine;
using System.Threading;

int passed = 0;
void Check(bool value, string message = "Segment scope assertion failed") { if (!value) throw new Exception(message); }
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { CaptureSegmentScope.Active?.Dispose(); Console.Error.WriteLine("FAIL " + name + ": " + error); Environment.Exit(1); }
}
GameObject Child(GameObject root, string name = "source") { var child = new GameObject(name); child.transform.parent = root.transform; return child; }
MapHandler Map(params MapHandler.MapSegment[] segments) => MapHandler.Instance = new MapHandler { segments = segments };
long Metric(CaptureSegmentScope scope, string name) => Convert.ToInt64(scope.Diagnostics.GetType().GetProperty(name)!.GetValue(scope.Diagnostics));
void Finish(CaptureSegmentScope scope, Func<bool> done)
{
    for (int i = 0; i < 10000 && !done(); i++) scope.Drain();
    Check(done(), "bounded drain made no progress");
}

Test("8000 future candidates construct no baseline and ordinary Observe touches only map roots", () =>
{
    var active = new GameObject("shore"); var future = new GameObject("fungal") { activeSelf = false };
    Map(new(active), new(future));
    using var scope = new CaptureSegmentScope(); int builds = 0;
    using var client = scope.CreateClient(_ => builds++, _ => { });
    for (int i = 0; i < 8000; i++) Check(!client.Admit(Child(future)));
    Transform.ParentReads = 0; GameObject.ActiveReads = 0;
    for (int i = 0; i < 6000; i++) { scope.Observe(); scope.Drain(); }
    Check(builds == 0 && Transform.ParentReads == 0);
    Check(GameObject.ActiveReads == 12000, "only two selected roots may be inspected per tick");
    Check(Metric(scope, "Candidates") == 8000 && Metric(scope, "Admitted") == 0 && Metric(scope, "Pending") == 0);
});
Test("early segment index does not retire the old hierarchy or admit the future hierarchy", () =>
{
    var old = new GameObject("old"); var next = new GameObject("next") { activeSelf = false };
    var map = Map(new(old), new(next));
    using var scope = new CaptureSegmentScope(); int builds = 0, retired = 0;
    using var client = scope.CreateClient(_ => builds++, _ => retired++);
    var oldSource = Child(old); var nextSource = Child(next);
    client.Admit(oldSource); client.Admit(nextSource); Finish(scope, () => builds == 1);
    map.currentSegment = 1;
    scope.Observe(); scope.Drain();
    Check(builds == 1 && retired == 0 && client.Admit(oldSource) && !client.Admit(nextSource, urgent: true));
    next.SetActive(true); scope.Observe(); Finish(scope, () => builds == 2);
    Check(retired == 0);
    old.SetActive(false); scope.Observe(); scope.Observe();
    Check(retired == 1 && !client.Admit(oldSource));
});
Test("campfires use their independent actual activation and survive previous parent retirement", () =>
{
    var old = new GameObject("old"); var campfire = new GameObject("old campfire");
    var next = new GameObject("next") { activeSelf = false };
    Map(new(old, campfire), new(next));
    using var scope = new CaptureSegmentScope(); var retired = new List<int>(); int builds = 0;
    using var client = scope.CreateClient(_ => builds++, retired.Add);
    var a = Child(old); var fire = Child(campfire);
    client.Admit(a); client.Admit(fire); Finish(scope, () => builds == 2);
    next.SetActive(true); old.SetActive(false); scope.Observe();
    Check(retired.SequenceEqual(new[] { a.GetInstanceID() }) && client.Admit(fire));
    campfire.SetActive(false); scope.Observe(); Check(retired.Count == 2);
});
Test("raw unselected main and variant roots are known and denied even when hierarchy is active", () =>
{
    var raw = new GameObject("unselected main"); var selected = new GameObject("selected variant");
    var unused = new GameObject("unselected variant");
    var selectedSegment = new MapHandler.MapSegment(selected);
    var main = new MapHandler.MapSegment(raw) { SelectedVariant = selectedSegment };
    var map = Map(main); map.variantSegments = [selectedSegment, new(unused)];
    using var scope = new CaptureSegmentScope(); int builds = 0;
    using var client = scope.CreateClient(_ => builds++, _ => { }, _ => true);
    Check(!client.Admit(Child(raw), urgent: true)); Check(!client.Admit(Child(unused), urgent: true));
    client.Admit(Child(selected)); Finish(scope, () => builds == 1);
    Check(Metric(scope, "Roots") == 3 && Metric(scope, "SelectedRoots") == 1 && Metric(scope, "Admitted") == 1);
});
Test("switching selected variants retires the previous variant even with a preserve hint", () =>
{
    var original = new GameObject("original"); var variant = new GameObject("variant");
    var alt = new MapHandler.MapSegment(variant); var main = new MapHandler.MapSegment(original);
    var map = Map(main); map.variantSegments = [alt];
    using var scope = new CaptureSegmentScope(); int builds = 0, retires = 0, preserves = 0;
    using var client = scope.CreateClient(_ => builds++, _ => retires++, _ => { preserves++; return true; });
    var a = Child(original); var b = Child(variant);
    client.Admit(a); client.Admit(b); Finish(scope, () => builds == 1);
    main.SelectedVariant = alt; scope.Observe(); Finish(scope, () => builds == 2);
    Check(retires == 1 && preserves == 0 && !client.Admit(a, urgent: true) && client.Admit(b));
});
Test("all baseline admissions including unknown roots share the global four-object bound", () =>
{
    MapHandler.Instance = null; using var scope = new CaptureSegmentScope(); int builds = 0;
    using var first = scope.CreateClient(_ => builds++, _ => { }); using var second = scope.CreateClient(_ => builds++, _ => { });
    for (int i = 0; i < 37; i++) { Check(!first.Admit(new())); Check(!second.Admit(new())); }
    Check(builds == 0);
    while (builds < 74) { int before = builds; scope.Drain(); Check(builds - before <= 4 && builds > before); }
    Check(Metric(scope, "MaximumDrainAdmissions") <= 4);
});
Test("fair round robin lets a crate baseline precede thousands of queued traps", () =>
{
    var root = new GameObject(); Map(new MapHandler.MapSegment(root));
    using var scope = new CaptureSegmentScope(); var order = new List<string>();
    using var traps = scope.CreateClient(_ => order.Add("trap"), _ => { });
    using var crate = scope.CreateClient(_ => order.Add("crate"), _ => { });
    using var environment = scope.CreateClient(_ => order.Add("environment"), _ => { });
    for (int i = 0; i < 8000; i++) traps.Admit(Child(root));
    crate.Admit(Child(root)); environment.Admit(Child(root));
    Finish(scope, () => order.Count >= 3);
    Check(order.Take(3).SequenceEqual(new[] { "trap", "crate", "environment" }));
});
Test("a slow callback ends its batch and the next drain resumes at the next client", () =>
{
    MapHandler.Instance = null; using var scope = new CaptureSegmentScope(); var order = new List<string>();
    using var slow = scope.CreateClient(_ => { order.Add("slow"); Thread.Sleep(3); }, _ => { });
    using var other = scope.CreateClient(_ => order.Add("other"), _ => { });
    slow.Admit(new()); slow.Admit(new()); other.Admit(new());
    scope.Drain(); Check(order.SequenceEqual(new[] { "slow" }));
    scope.Drain(); Check(order[1] == "other");
});
Test("urgent true actions bypass baseline only on already observed active roots", () =>
{
    var root = new GameObject { activeSelf = false }; Map(new MapHandler.MapSegment(root));
    using var scope = new CaptureSegmentScope(); int builds = 0;
    using var client = scope.CreateClient(_ => builds++, _ => { }); var source = Child(root);
    Check(!client.Admit(source, urgent: true)); root.SetActive(true);
    Check(!client.Admit(source, urgent: true), "SetActive initialization must not bypass root observation");
    scope.Observe(); Check(client.Admit(source, urgent: true)); scope.Drain();
    Check(builds == 0 && Metric(scope, "UrgentAdmissions") == 1);
    Check(client.Admit(new GameObject("network root"), urgent: true));
});
Test("admission callback safely reenters Register and root activation rebuilds a baseline once", () =>
{
    var root = new GameObject(); Map(new MapHandler.MapSegment(root)); using var scope = new CaptureSegmentScope();
    SegmentCaptureClient? client = null; int builds = 0, retires = 0;
    using (client = scope.CreateClient(source => { Check(client!.Admit(source)); builds++; scope.Drain(); }, _ => retires++))
    {
        var source = Child(root); Check(!client.Admit(source)); Finish(scope, () => builds == 1);
        root.SetActive(false); scope.Observe(); scope.Observe(); Check(retires == 1);
        root.SetActive(true); scope.Observe(); Finish(scope, () => builds == 2);
        scope.Drain(); Check(builds == 2 && retires == 1);
    }
});
Test("detached objects and objects transferred to another active root retain their bindings", () =>
{
    var old = new GameObject(); var next = new GameObject(); Map(new(old), new(next));
    using var scope = new CaptureSegmentScope(); int builds = 0, retires = 0;
    using var client = scope.CreateClient(_ => builds++, _ => retires++);
    var unknown = Child(old); var moved = Child(old); var suspended = Child(old);
    client.Admit(unknown); client.Admit(moved); client.Admit(suspended); Finish(scope, () => builds == 3);
    unknown.transform.parent = null; moved.transform.parent = next.transform; old.SetActive(false); scope.Observe();
    Check(retires == 1 && client.Admit(unknown) && client.Admit(moved) && !client.Admit(suspended));
    Check(Metric(scope, "Preserved") == 2);
});
Test("moving to a known future root cannot inherit admission from an active old root", () =>
{
    var old = new GameObject(); var future = new GameObject { activeSelf = false }; Map(new(old), new(future));
    using var scope = new CaptureSegmentScope(); int retires = 0;
    using var client = scope.CreateClient(_ => { }, _ => retires++, _ => true); var source = Child(old);
    Check(client.Admit(source, urgent: true)); source.transform.parent = future.transform;
    old.SetActive(false); scope.Observe(); Check(retires == 1 && !client.Admit(source, urgent: true));
});
Test("explicit global metadata exception cannot bypass known-root baseline or future and variant exclusion", () =>
{
    var active = new GameObject(); var future = new GameObject { activeSelf = false }; var unselected = new GameObject();
    var map = Map(new(active), new(future)); map.variantSegments = [new(unselected)];
    using var scope = new CaptureSegmentScope(); int builds = 0;
    using var client = scope.CreateClient(_ => builds++, _ => { });
    Check(client.Admit(new GameObject("global fog controller"), urgentUnknown: true));
    Check(!client.Admit(Child(active), urgentUnknown: true));
    Check(!client.Admit(Child(future), urgentUnknown: true));
    Check(!client.Admit(Child(unselected), urgentUnknown: true));
    var moved = new GameObject("initial unknown"); Check(!client.Admit(moved)); moved.transform.parent = future.transform;
    Check(!client.Admit(moved, urgentUnknown: true));
    Finish(scope, () => builds == 1);
    Check(Metric(scope, "GlobalAdmissions") == 1 && Metric(scope, "Admitted") == 2);
});
Test("native pickup refreshes a queued candidate that left an inactive root before its first baseline", () =>
{
    var old = new GameObject { activeSelf = false }; Map(new MapHandler.MapSegment(old));
    using var scope = new CaptureSegmentScope(); int builds = 0;
    using var client = scope.CreateClient(_ => builds++, _ => { }); var source = Child(old);
    Check(!client.Admit(source)); source.transform.parent = null;
    Check(client.Admit(source, urgent: true)); scope.Drain();
    Check(builds == 0 && Metric(scope, "UrgentAdmissions") == 1);
});
Test("preserve is evaluated only at admitted root shutdown, never to admit a future candidate", () =>
{
    var root = new GameObject(); var future = new GameObject { activeSelf = false }; Map(new(root), new(future));
    using var scope = new CaptureSegmentScope(); int preserveCalls = 0, retires = 0;
    using var client = scope.CreateClient(_ => { }, _ => retires++, _ => { preserveCalls++; return true; });
    var active = Child(root); var inactive = Child(future);
    Check(!client.Admit(inactive, urgent: true) && preserveCalls == 0);
    Check(client.Admit(active, urgent: true)); root.SetActive(false); scope.Observe();
    Check(preserveCalls == 1 && retires == 0 && client.Admit(active));
    Check(!client.Admit(inactive, urgent: true) && preserveCalls == 1);
});
Test("Forget, native destruction and client Dispose cancel pending baselines", () =>
{
    MapHandler.Instance = null; using var scope = new CaptureSegmentScope(); int builds = 0; var retired = new List<int>();
    using var client = scope.CreateClient(_ => builds++, retired.Add);
    var forgot = new GameObject(); var destroyed = new GameObject();
    client.Admit(forgot); client.Admit(destroyed); client.Forget(forgot.GetInstanceID()); destroyed.Destroyed = true;
    scope.Drain(); Check(builds == 0 && retired.SequenceEqual(new[] { destroyed.GetInstanceID() }));
    client.Admit(new()); client.Dispose(); scope.Drain(); Check(builds == 0 && Metric(scope, "Clients") == 0);
});
Test("new Unity native object reusing an instance ID cannot inherit an admitted root", () =>
{
    var active = new GameObject(); var future = new GameObject { activeSelf = false }; Map(new(active), new(future));
    using var scope = new CaptureSegmentScope(); int retires = 0;
    using var client = scope.CreateClient(_ => { }, _ => retires++);
    var old = Child(active); Check(client.Admit(old, urgent: true)); old.Destroyed = true;
    var replacement = Child(future); replacement.ReusedId = old.GetInstanceID();
    Check(!client.Admit(replacement, urgent: true) && retires == 1);
    Check(Metric(scope, "Admitted") == 0 && Metric(scope, "Candidates") == 1);
});
Test("callbacks can Forget or Dispose reentrantly without resurrecting bindings", () =>
{
    MapHandler.Instance = null; using var scope = new CaptureSegmentScope(); SegmentCaptureClient? first = null, second = null;
    int builds = 0;
    first = scope.CreateClient(source => { builds++; first!.Forget(source.GetInstanceID()); }, _ => { });
    second = scope.CreateClient(_ => { builds++; second!.Dispose(); }, _ => { });
    first.Admit(new()); second.Admit(new()); Finish(scope, () => builds == 2);
    Check(Metric(scope, "Candidates") == 0); first.Dispose();
});
Test("callback failures are isolated, release partial bindings and bound warning emission", () =>
{
    MapHandler.Instance = null; int warnings = 0; using var scope = new CaptureSegmentScope(_ => { warnings++; throw new Exception("logger"); });
    int retires = 0, good = 0;
    using var failing = scope.CreateClient(_ => throw new Exception("baseline"), _ => { retires++; throw new Exception("retire"); });
    using var working = scope.CreateClient(_ => good++, _ => { });
    for (int i = 0; i < 20; i++) failing.Admit(new()); working.Admit(new());
    Finish(scope, () => retires == 20 && good == 1);
    Check(warnings == 8 && Metric(scope, "Admitted") == 1 && Metric(scope, "CallbackFailures") == 40);
});
Test("retire callback may mutate client lifecycle while other clients retire safely", () =>
{
    var root = new GameObject(); Map(new MapHandler.MapSegment(root)); using var scope = new CaptureSegmentScope();
    SegmentCaptureClient? first = null; int retires = 0;
    first = scope.CreateClient(_ => { }, _ => { retires++; first!.Dispose(); scope.Observe(); });
    using var second = scope.CreateClient(_ => { }, _ => retires++);
    first.Admit(Child(root), urgent: true); second.Admit(Child(root), urgent: true);
    root.SetActive(false); scope.Observe(); Check(retires == 2);
});
Test("late map catalogue reclassifies unknown baselines before heavy capture and appends new roots", () =>
{
    MapHandler.Instance = null; using var scope = new CaptureSegmentScope(); int builds = 0, retires = 0;
    using var client = scope.CreateClient(_ => builds++, _ => retires++);
    var future = new GameObject { activeSelf = false }; var source = Child(future); client.Admit(source);
    var map = Map(new MapHandler.MapSegment(future)); scope.Observe(); scope.Drain(); Check(builds == 0);
    future.SetActive(true); scope.Observe(); Finish(scope, () => builds == 1);
    var voidRoot = new GameObject { activeSelf = false }; var voidSource = Child(voidRoot);
    Check(client.Admit(voidSource, urgent: true)); // unknown is conservative before the map exposes its root
    map.segments = [map.segments[0], new(voidRoot)]; scope.Observe();
    Check(retires == 1 && !client.Admit(voidSource, urgent: true));
});
Test("one scope owns the lifecycle and disposal releases all candidate references", () =>
{
    MapHandler.Instance = null; var scope = new CaptureSegmentScope(); var client = scope.CreateClient(_ => { }, _ => { });
    client.Admit(new()); bool rejected = false;
    try { new CaptureSegmentScope(); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected && ReferenceEquals(CaptureSegmentScope.Active, scope));
    scope.Dispose(); scope.Dispose(); Check(CaptureSegmentScope.Active == null && !client.Admit(new(), urgent: true));
    using var replacement = new CaptureSegmentScope(); Check(ReferenceEquals(CaptureSegmentScope.Active, replacement));
});
Test("native variant getter failure blocks new admissions without retiring valid history and recovers", () =>
{
    var oldRoot = new GameObject(); var variantRoot = new GameObject();
    var variant = new MapHandler.MapSegment(variantRoot);
    var main = new MapHandler.MapSegment(oldRoot) { ThrowResolved = true };
    var map = Map(main); map.variantSegments = [variant];
    int warnings = 0; using var scope = new CaptureSegmentScope(_ => warnings++);
    int builds = 0, retires = 0; using var client = scope.CreateClient(_ => builds++, _ => retires++);
    var oldSource = Child(oldRoot); var variantSource = Child(variantRoot);
    Check(!client.Admit(oldSource, urgent: true)); Check(!client.Admit(variantSource, urgent: true));
    Check(!client.Admit(new GameObject("global metadata"), urgentUnknown: true));
    scope.Drain(); Check(builds == 0 && !scope.DirectoryReliable);
    main.ThrowResolved = false; scope.Observe(); Finish(scope, () => builds == 2);
    Check(client.Admit(oldSource));
    main.SelectedVariant = variant; main.ThrowResolved = true; scope.Observe();
    Check(client.Admit(oldSource), "existing binding survives one incoherent native directory read");
    Check(!client.Admit(Child(oldRoot), urgent: true)); Check(!client.Admit(variantSource, urgent: true));
    Check(!client.Admit(new GameObject("second global metadata"), urgentUnknown: true));
    scope.Drain(); Check(builds == 2 && retires == 0 && warnings == 2);
    main.ThrowResolved = false; scope.Observe(); Finish(scope, () => builds == 4);
    Check(retires == 1 && !client.Admit(oldSource) && client.Admit(variantSource) && scope.DirectoryReliable);
});
Test("late raw fields on existing MapSegment objects reclassify unknown future and unselected sources", () =>
{
    var active = new GameObject(); var future = new GameObject { activeSelf = false }; var unselected = new GameObject();
    var lateMain = new MapHandler.MapSegment(null); var lateVariant = new MapHandler.MapSegment(null);
    var map = Map(new(active), lateMain); map.variantSegments = [lateVariant];
    using var scope = new CaptureSegmentScope(); int builds = 0, retires = 0;
    using var client = scope.CreateClient(_ => builds++, _ => retires++);
    var futureSource = Child(future); var variantSource = Child(unselected);
    Check(client.Admit(futureSource, urgent: true) && client.Admit(variantSource, urgent: true));
    lateMain.SetRawRoots(future); lateVariant.SetRawRoots(unselected); scope.Observe();
    Check(retires == 2 && !client.Admit(futureSource, urgent: true) && !client.Admit(variantSource, urgentUnknown: true));
    future.SetActive(true); scope.Observe(); Finish(scope, () => builds == 1);
    Check(client.Admit(futureSource) && !client.Admit(variantSource, urgent: true));
});
Test("a failed resolved getter cannot consume the pending late-directory rebind boundary", () =>
{
    MapHandler.Instance = null; using var scope = new CaptureSegmentScope(); int retires = 0;
    using var client = scope.CreateClient(_ => { }, _ => retires++);
    var future = new GameObject { activeSelf = false }; var source = Child(future);
    Check(client.Admit(source, urgent: true));
    var segment = new MapHandler.MapSegment(future) { ThrowResolved = true }; Map(segment);
    scope.Observe(); Check(!scope.DirectoryReliable && retires == 0 && client.Admit(source));
    segment.ThrowResolved = false; scope.Observe();
    Check(scope.DirectoryReliable && retires == 1 && !client.Admit(source, urgent: true));
});

EnvelopeScopeTests.Run(Test);
OriginAndRetryScopeTests.Run(Test);
LuggageScopeTests.Run(Test);
Console.WriteLine($"TOTAL: {passed} actual segment-scope/native capture checks passed. Unity rendering and game-frame timing are not simulated.");
