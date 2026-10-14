using System;
using System.Collections.Generic;
using PeakReplayLab;
using UnityEngine;

internal static class EnvelopeScopeTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception("Envelope scope contract failed: " + message); }

    private static GameObject Child(GameObject parent, string name = "source")
    { var source = new GameObject(name); source.transform.parent = parent.transform; return source; }

    private static GameObject BiomeRoot(string name)
    { var source = new GameObject(name); _ = new Biome(source); return source; }

    private static MapHandler Map(params MapHandler.MapSegment[] segments)
    { var map = new MapHandler { segments = segments }; MapHandler.Instance = map; return map; }

    private static long Metric(CaptureSegmentScope scope, string name) =>
        Convert.ToInt64(scope.Diagnostics.GetType().GetProperty(name)!.GetValue(scope.Diagnostics));

    private static void Drain(CaptureSegmentScope scope)
    { for (int i = 0; i < 32 && scope.HasPending; i++) scope.Drain(10, 128); }

    private static GameObject? Resolve(GameObject source, Dictionary<GameObject, GameObject> origins)
    {
        for (var node = source.transform; node; node = node.parent)
            if (origins.TryGetValue(node.gameObject, out var owner)) return owner;
        return null;
    }

    internal static void Run(Action<string, Action> test)
    {
        test("biome envelopes deny future and active unselected sibling details before baseline work", () =>
        {
            var rawBiome = BiomeRoot("unselected main biome");
            var selectedBiome = BiomeRoot("selected variant biome");
            var futureBiome = BiomeRoot("future biome");
            var unusedBiome = BiomeRoot("unused variant biome");
            var raw = Child(rawBiome, "raw segment");
            var selected = Child(selectedBiome, "selected segment");
            var future = Child(futureBiome, "future segment"); future.SetActive(false);
            var unused = Child(unusedBiome, "unused segment");
            var variant = new MapHandler.MapSegment(selected);
            var map = Map(new MapHandler.MapSegment(raw) { SelectedVariant = variant }, new(future));
            map.variantSegments = [variant, new(unused)];
            using var scope = new CaptureSegmentScope(); int builds = 0, preserves = 0;
            using var client = scope.CreateClient(_ => builds++, _ => { }, _ => { preserves++; return true; });
            foreach (var source in new[] { Child(rawBiome), Child(futureBiome), Child(unusedBiome) })
            {
                Check(source.activeInHierarchy, "the excluded sibling itself remains active");
                Check(scope.FindRoot(source)?.IsEnvelope == true, "native biome siblings must be classified");
                Check(!client.Admit(source, urgent: true) && !client.Admit(source, urgentUnknown: true), "future and unselected siblings must stay excluded");
            }
            var active = Child(selectedBiome);
            Check(!client.Admit(active), "selected envelope baseline must use the bounded queue");
            Drain(scope);
            Check(builds == 1 && preserves == 0 && client.Admit(active), "only the selected active envelope may build");
        });

        test("retained campfire cannot preserve active biome sibling effects after segment shutdown", () =>
        {
            var biome = BiomeRoot("biome"); var parent = Child(biome, "segment");
            var campfire = Child(biome, "campfire"); var sibling = Child(biome, "authored fire");
            var campfireEffect = Child(campfire, "campfire fire"); Map(new MapHandler.MapSegment(parent, campfire));
            using var scope = new CaptureSegmentScope(); var retired = new List<int>(); int preserves = 0;
            using var client = scope.CreateClient(_ => { }, retired.Add, go => { preserves++; return go && go.activeInHierarchy; });
            Check(client.Admit(sibling, urgent: true) && client.Admit(campfireEffect, urgent: true), "initial effects must be admitted");
            Check(ReferenceEquals(scope.FindRoot(campfireEffect)?.Source, campfire), "exact campfire ownership must precede its biome envelope");
            parent.SetActive(false); scope.Observe();
            Check(sibling.activeInHierarchy && campfireEffect.activeInHierarchy, "native sibling and retained campfire both remain active");
            Check(retired.Count == 1 && retired[0] == sibling.GetInstanceID() && preserves == 1, "active sibling liveness cannot create global ownership");
            Check(!client.Admit(sibling, urgent: true) && client.Admit(campfireEffect), "campfire retention must not reactivate general biome siblings");
            scope.Observe(); Check(retired.Count == 1, "retirement must occur once");
            campfire.SetActive(false); scope.Observe();
            Check(retired.Count == 2 && retired[1] == campfireEffect.GetInstanceID(), "campfire keeps its independent shutdown");
        });

        test("shared biome envelope remains active while any selected segment parent is active", () =>
        {
            var biome = BiomeRoot("shared biome"); var first = Child(biome, "first segment");
            var second = Child(biome, "second segment"); second.SetActive(false);
            var sibling = Child(biome); Map(new(first), new(second));
            using var scope = new CaptureSegmentScope(); int builds = 0, retired = 0, preserves = 0;
            using var client = scope.CreateClient(_ => builds++, _ => retired++, _ => { preserves++; return true; });
            Check(client.Admit(sibling, urgent: true), "first active driver must own sibling");
            first.SetActive(false); second.SetActive(true); scope.Observe();
            Check(client.Admit(sibling) && retired == 0 && preserves == 0, "driver handoff must use OR rather than first or last owner state");
            second.SetActive(false); scope.Observe();
            Check(retired == 1 && preserves == 1 && !client.Admit(sibling, urgent: true), "last driver shutdown must retire sibling");
            second.SetActive(true); scope.Observe(); Drain(scope);
            Check(builds == 1 && retired == 1 && client.Admit(sibling), "a later driver activation must rebuild one baseline");
        });

        test("exact serialized ancestor takes precedence over a nearer inactive biome envelope", () =>
        {
            var exact = new GameObject("outer serialized segment");
            var innerBiome = Child(exact, "nested biome"); _ = new Biome(innerBiome);
            var innerParent = Child(innerBiome, "inner serialized segment"); innerParent.SetActive(false);
            var sibling = Child(innerBiome, "inner sibling"); Map(new(exact), new(innerParent));
            using var scope = new CaptureSegmentScope(); int retired = 0;
            using var client = scope.CreateClient(_ => { }, _ => retired++);
            var owner = scope.FindRoot(sibling);
            Check(owner != null && !owner.IsEnvelope && ReferenceEquals(owner.Source, exact), "a remembered envelope cannot hide a farther exact root");
            Check(client.Admit(sibling, urgent: true) && !client.Admit(Child(innerParent), urgent: true), "nearest exact serialized roots keep their own activation semantics");
            exact.SetActive(false); scope.Observe();
            Check(retired == 1 && !client.Admit(sibling), "outer exact ownership must retain its shutdown boundary");
        });

        test("nested known biome envelope cannot fall back to an active outer biome", () =>
        {
            var outer = BiomeRoot("outer biome"); var outerParent = Child(outer, "outer segment");
            var inner = Child(outer, "inner biome"); _ = new Biome(inner);
            var innerParent = Child(inner, "inner segment"); innerParent.SetActive(false);
            var innerSibling = Child(inner); Map(new(outerParent), new(innerParent));
            using var scope = new CaptureSegmentScope();
            using var client = scope.CreateClient(_ => { }, _ => { });
            Check(ReferenceEquals(scope.FindRoot(innerSibling)?.Source, inner), "nearest known biome must own its siblings");
            Check(!client.Admit(innerSibling, urgent: true) && client.Admit(Child(outer), urgent: true), "inactive nested envelope cannot inherit outer activation");
        });

        test("unregistered nested biome keeps conservative unknown fallback inside an inactive outer envelope", () =>
        {
            var outer = BiomeRoot("native outer biome"); var parent = Child(outer, "native segment"); parent.SetActive(false);
            var moddedBiome = Child(outer, "unregistered nested biome"); _ = new Biome(moddedBiome);
            var moddedSibling = Child(moddedBiome); Map(new MapHandler.MapSegment(parent));
            using var scope = new CaptureSegmentScope();
            using var client = scope.CreateClient(_ => { }, _ => { });
            Check(scope.FindRoot(moddedSibling) == null && client.Admit(moddedSibling, urgent: true), "unregistered nearest biome must remain unknown");
            Check(!client.Admit(Child(outer), urgent: true), "known native outer siblings must still be excluded");
        });

        test("native spawn origins classify unparented sources by the same biome envelope boundaries", () =>
        {
            var activeBiome = BiomeRoot("active biome"); var activeParent = Child(activeBiome);
            var futureBiome = BiomeRoot("future biome"); var futureParent = Child(futureBiome); futureParent.SetActive(false);
            var activeSpawner = Child(activeBiome); var futureSpawner = Child(futureBiome);
            Map(new(activeParent), new(futureParent));
            var activeSource = new GameObject(); var futureSource = new GameObject();
            var origins = new Dictionary<GameObject, GameObject> { [activeSource] = activeSpawner, [futureSource] = futureSpawner };
            using var scope = new CaptureSegmentScope(originResolver: go => Resolve(go, origins));
            using var client = scope.CreateClient(_ => { }, _ => { }, _ => true);
            Check(ReferenceEquals(scope.FindRoot(futureSource)?.Source, futureBiome), "unparented native origin must resolve to its biome envelope");
            Check(client.Admit(activeSource, urgent: true) && !client.Admit(futureSource, urgent: true), "origin envelopes must enforce the same active-driver gate");
        });

        test("physical exact serialized ownership takes precedence over an inactive sibling-envelope origin", () =>
        {
            var activeBiome = BiomeRoot("active biome"); var activeParent = Child(activeBiome, "active segment");
            var futureBiome = BiomeRoot("future biome"); var futureParent = Child(futureBiome, "future segment"); futureParent.SetActive(false);
            var futureSpawner = Child(futureBiome, "future sibling spawner");
            var source = Child(activeParent, "physically transferred source"); Map(new(activeParent), new(futureParent));
            int resolves = 0, retired = 0;
            using var scope = new CaptureSegmentScope(originResolver: _ => { resolves++; return futureSpawner; });
            using var client = scope.CreateClient(_ => { }, _ => retired++);
            Check(client.Admit(source, urgent: true), "physical active root must override an old inactive origin envelope");
            Check(ReferenceEquals(scope.FindRoot(source)?.Source, activeParent) && resolves == 0, "exact physical ancestry must resolve before any origin lookup");
            scope.RefreshOrigin(source);
            Check(client.Admit(source) && retired == 0 && resolves == 0, "origin refresh cannot override a real physical root");
        });

        test("physical native envelope takes precedence over conflicting native origins in either activation direction", () =>
        {
            foreach (bool physicalActive in new[] { true, false })
            {
                var activeBiome = BiomeRoot("active biome"); var activeParent = Child(activeBiome, "active segment");
                var inactiveBiome = BiomeRoot("inactive biome"); var inactiveParent = Child(inactiveBiome, "inactive segment"); inactiveParent.SetActive(false);
                var physicalBiome = physicalActive ? activeBiome : inactiveBiome;
                var conflictingOrigin = physicalActive ? inactiveParent : activeParent;
                var source = Child(physicalBiome, "authored physical sibling"); Map(new(activeParent), new(inactiveParent));
                int resolves = 0, retired = 0;
                using var scope = new CaptureSegmentScope(originResolver: _ => { resolves++; return conflictingOrigin; });
                using var client = scope.CreateClient(_ => { }, _ => retired++, _ => true);
                var owner = scope.FindRoot(source);
                Check(owner?.IsEnvelope == true && ReferenceEquals(owner.Source, physicalBiome), "physical biome envelope must resolve before a conflicting exact origin root");
                Check(source.activeInHierarchy && client.Admit(source, urgent: true) == physicalActive, "native physical envelope activity must decide sibling admission");
                scope.RefreshOrigin(source);
                Check(client.Admit(source, urgentUnknown: true) == physicalActive && resolves == 0 && retired == 0, "origin refresh and global metadata cannot override verified physical envelope ownership");
            }
        });

        test("late exact roots reject admissions previously owned by an active envelope as origin rejection", () =>
        {
            var biome = BiomeRoot("shared biome"); var driver = Child(biome, "known active segment");
            var future = Child(biome, "late future segment"); future.SetActive(false);
            var unused = Child(biome, "late unselected segment");
            var futureSource = Child(future); var unusedSource = Child(unused);
            var lateMain = new MapHandler.MapSegment(null); var lateVariant = new MapHandler.MapSegment(null);
            var map = Map(new(driver), lateMain); map.variantSegments = [lateVariant];
            using var scope = new CaptureSegmentScope(); int builds = 0, preserves = 0;
            var rejected = new Dictionary<int, bool>(); SegmentCaptureClient? client = null;
            using (client = scope.CreateClient(_ => builds++, id => rejected[id] = client!.OriginRejection, _ => { preserves++; return true; }))
            {
                Check(scope.FindRoot(futureSource)?.IsEnvelope == true && scope.FindRoot(unusedSource)?.IsEnvelope == true, "initial ownership must use the populated envelope");
                Check(client.Admit(futureSource, urgent: true) && client.Admit(unusedSource, urgent: true), "unknown exact boundaries remain conservative initially");
                lateMain.SetRawRoots(future); lateVariant.SetRawRoots(unused); scope.Observe();
                Check(rejected.Count == 2 && rejected[futureSource.GetInstanceID()] && rejected[unusedSource.GetInstanceID()], "late exact roots must reclassify envelope members and reject prior evidence");
                Check(preserves == 0 && !client.Admit(futureSource, urgent: true) && !client.Admit(unusedSource, urgent: true), "reclassification cannot use shutdown preservation");
                future.SetActive(true); scope.Observe(); Drain(scope);
                Check(builds == 1 && client.Admit(futureSource) && !client.Admit(unusedSource, urgent: true), "selected exact activation may rebuild while an unselected root stays excluded");
            }
        });

        test("late native root rebind touches its indexed descendants rather than thousands of unrelated globals", () =>
        {
            MapHandler.Instance = null;
            using var scope = new CaptureSegmentScope(); int retired = 0;
            using var client = scope.CreateClient(_ => { }, _ => retired++);
            for (int i = 0; i < 8000; i++)
                Check(client.Admit(new GameObject("unrelated global"), urgent: true), "global source before map directory");
            var future = new GameObject("late future") { activeSelf = false };
            var affected = Child(future);
            Check(client.Admit(affected, urgent: true), "late source initially has no known native owner");
            long walks = Metric(scope, "ParentWalks");
            Map(new MapHandler.MapSegment(future)); scope.Observe();
            Check(Metric(scope, "ParentWalks") - walks == 1, "one newly indexed descendant needs one hierarchy rebind");
            Check(retired == 1 && !client.Admit(affected, urgent: true), "affected source must retire before the next capture");
            Check(Metric(scope, "Admitted") == 8000 && Metric(scope, "PendingRebind") == 0,
                "unrelated global sources keep their captured runs without being walked");
        });

        test("late inactive root rebinds unadmitted candidates in bounded batches and guards true events", () =>
        {
            MapHandler.Instance = null;
            using var scope = new CaptureSegmentScope(); int builds = 0;
            using var client = scope.CreateClient(_ => builds++, _ => { });
            var future = new GameObject("late future") { activeSelf = false };
            var candidates = new List<GameObject>();
            for (int i = 0; i < 4096; i++)
            {
                var source = Child(future); candidates.Add(source);
                Check(!client.Admit(source), "normal baseline waits for the shared drain");
            }
            long walks = Metric(scope, "ParentWalks");
            Map(new MapHandler.MapSegment(future)); scope.Observe();
            Check(Metric(scope, "ParentWalks") - walks <= 64 && Metric(scope, "PendingRebind") > 0,
                "one native observation cannot walk every deferred future candidate");
            Check(!client.Admit(candidates[candidates.Count - 1], urgent: true) && builds == 0,
                "a real event must synchronously resolve its source before allowing capture");
            int observations = 0;
            while (Metric(scope, "PendingRebind") != 0 && observations++ < 4096)
            {
                walks = Metric(scope, "ParentWalks"); scope.Observe();
                Check(Metric(scope, "ParentWalks") - walks <= 64,
                    "each follow-up observation must stay within the rebind batch");
            }
            Check(Metric(scope, "PendingRebind") == 0 && Metric(scope, "MaximumRebindBatch") <= 64 && builds == 0,
                "every candidate eventually rebinds without a future-map baseline");
        });

        test("late nested exact root invalidates an admitted outer exact binding immediately", () =>
        {
            var outer = new GameObject("selected outer root");
            var nested = Child(outer, "late unselected nested root"); var source = Child(nested);
            var lateVariant = new MapHandler.MapSegment(null);
            var map = Map(new MapHandler.MapSegment(outer)); map.variantSegments = [lateVariant];
            using var scope = new CaptureSegmentScope(); int retired = 0;
            using var client = scope.CreateClient(_ => { }, _ => retired++);
            Check(client.Admit(source, urgent: true), "known outer root initially owns nested source");
            lateVariant.SetRawRoots(nested); scope.Observe();
            Check(retired == 1 && !client.Admit(source, urgent: true),
                "new nearer unselected root must reject prior outer-root admission in the same observation");
        });

        test("late native root reclassifies unparented spawns through their indexed origin ancestry", () =>
        {
            var future = new GameObject("late future") { activeSelf = false };
            var spawner = Child(future, "native spawner"); var source = new GameObject("detached native spawn");
            MapHandler.Instance = null;
            using var scope = new CaptureSegmentScope(originResolver: go => ReferenceEquals(go, source) ? spawner : null);
            bool rejected = false; SegmentCaptureClient? client = null;
            using (client = scope.CreateClient(_ => { }, _ => rejected = client!.OriginRejection))
            {
                Check(client.Admit(source, urgent: true), "unregistered native origin is initially global");
                Map(new MapHandler.MapSegment(future)); scope.Observe();
                Check(rejected && !client.Admit(source, urgent: true),
                    "new native owner must reject the prior global birth without requiring a parent move");
            }
        });

        test("late root marks every affected admission before a retirement callback reenters another source", () =>
        {
            MapHandler.Instance = null;
            var future = new GameObject("late future") { activeSelf = false };
            var first = Child(future, "first"); var second = Child(future, "second");
            using var scope = new CaptureSegmentScope();
            SegmentCaptureClient? client = null; int retired = 0;
            using (client = scope.CreateClient(_ => { }, id =>
            {
                retired++;
                var other = id == first.GetInstanceID() ? second : first;
                Check(!client!.Admit(other, urgent: true), "reentrant event cannot admit the other newly excluded source");
            }))
            {
                Check(client.Admit(first, urgent: true) && client.Admit(second, urgent: true),
                    "both sources precede the native boundary");
                Map(new MapHandler.MapSegment(future)); scope.Observe();
                Check(retired == 2 && !client.Admit(first, urgent: true) && !client.Admit(second, urgent: true),
                    "both admitted sources retire once on the same directory boundary");
            }
        });

        test("reentrant late-root rejection cannot resurrect a client disposed by another rejection", () =>
        {
            MapHandler.Instance = null;
            var future = new GameObject("late future") { activeSelf = false };
            var first = Child(future, "first"); var second = Child(future, "second");
            using var scope = new CaptureSegmentScope();
            SegmentCaptureClient? client = null; int retired = 0;
            client = scope.CreateClient(_ => { }, id =>
            {
                if (++retired == 1)
                    Check(!client!.Admit(id == first.GetInstanceID() ? second : first, urgent: true),
                        "nested rejection must not re-admit an excluded source");
                else client!.Dispose();
            });
            Check(client.Admit(first, urgent: true) && client.Admit(second, urgent: true),
                "both sources precede the late native boundary");
            Map(new MapHandler.MapSegment(future)); scope.Observe();
            Check(retired == 2 && Metric(scope, "Clients") == 0 && Metric(scope, "PendingRebind") == 0,
                "nested disposal releases every stale native binding");
        });

        test("forget and client disposal cancel deferred directory rebind work", () =>
        {
            MapHandler.Instance = null;
            using var scope = new CaptureSegmentScope();
            var future = new GameObject("late future") { activeSelf = false };
            using var first = scope.CreateClient(_ => { }, _ => { });
            var doomed = new List<GameObject>();
            for (int i = 0; i < 512; i++)
            {
                var source = Child(future); doomed.Add(source); first.Admit(source);
            }
            Map(new MapHandler.MapSegment(future)); scope.Observe();
            Check(Metric(scope, "PendingRebind") > 0, "some deferred entries must remain after the first budget");
            first.Forget(doomed[doomed.Count - 1].GetInstanceID());
            first.Dispose();
            Check(Metric(scope, "PendingRebind") == 0 && !scope.HasPending,
                "removed candidates cannot retain bootstrap readiness or native object references");
            scope.Observe(); Check(Metric(scope, "PendingRebind") == 0,
                "stale queue tokens may drain without resurrecting disposed entries");
        });

        test("envelope item detach preserves its child FX before authored sibling retirement in either admission order", () =>
        {
            foreach (bool childrenFirst in new[] { false, true })
            {
                var biome = BiomeRoot("biome"); var parent = Child(biome, "segment");
                var spawner = Child(biome, "authored sibling spawner"); Map(new MapHandler.MapSegment(parent));
                var item = new GameObject("native unparented item"); var child = Child(item, "item FX");
                var sibling = Child(biome, "authored static FX");
                var origins = new Dictionary<GameObject, GameObject> { [item] = spawner };
                using var scope = new CaptureSegmentScope(originResolver: go => Resolve(go, origins));
                var retired = new List<int>(); int detaches = 0;
                using var items = scope.CreateClient(_ => { }, retired.Add, source =>
                {
                    detaches++; origins.Remove(source); scope.RefreshOrigin(source); return true;
                });
                using var effects = scope.CreateClient(_ => { }, retired.Add, go => go && go.activeInHierarchy);
                if (childrenFirst) Check(effects.Admit(child, urgent: true), "child admission before its item");
                Check(items.Admit(item, urgent: true), "item admission");
                if (!childrenFirst) Check(effects.Admit(child, urgent: true), "child admission after its item");
                Check(effects.Admit(sibling, urgent: true), "authored sibling admission");
                parent.SetActive(false); scope.Observe();
                Check(detaches == 1 && retired.Count == 1 && retired[0] == sibling.GetInstanceID(), "item callback and child rebind must precede actual retirements");
                Check(items.Admit(item) && effects.Admit(child) && !effects.Admit(sibling, urgent: true), "only genuine detached sources may preserve their bindings");
                Check(scope.FindRoot(item) == null && scope.FindRoot(child) == null && sibling.activeInHierarchy, "detachment must change ownership while authored sibling liveness remains insufficient");
            }
        });

        test("ordinary observe walks no candidates or parent chains with a populated biome envelope", () =>
        {
            var biome = BiomeRoot("biome"); var parent = Child(biome, "segment"); Map(new MapHandler.MapSegment(parent));
            using var scope = new CaptureSegmentScope(); int retired = 0;
            using var client = scope.CreateClient(_ => { }, _ => retired++, _ => true);
            for (int i = 0; i < 2048; i++)
                Check(client.Admit(Child(Child(biome), "sibling FX"), urgent: true), "populated envelope admission");
            long walks = Metric(scope, "ParentWalks"); Transform.ParentReads = 0;
            for (int i = 0; i < 1000; i++) scope.Observe();
            Check(Metric(scope, "ParentWalks") == walks && Transform.ParentReads == 0, "steady observation must inspect cached native roots without revisiting candidate hierarchies");
            Check(retired == 0 && Metric(scope, "Admitted") == 2048 && !scope.HasPending, "steady observation must preserve admitted envelope members");
        });
    }
}
