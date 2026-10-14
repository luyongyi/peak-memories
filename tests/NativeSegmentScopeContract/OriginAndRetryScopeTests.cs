using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using PeakReplayLab;
using UnityEngine;

internal static class OriginAndRetryScopeTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Origin/retry scope contract failed."); }
    private static GameObject Child(GameObject parent) { var source = new GameObject(); source.transform.parent = parent.transform; return source; }
    private static GameObject? Resolve(GameObject source, Dictionary<GameObject,GameObject> origins)
    {
        for (var node = source.transform; node; node = node.parent)
            if (origins.TryGetValue(node.gameObject, out var owner)) return owner;
        return null;
    }
    internal static void Run(Action<string,Action> test)
    {
        test("active native room items and child FX audio ropes remain biome-owned at old-root shutdown", () =>
        {
            var root = new GameObject(); MapHandler.Instance = new() { segments = [new(root)] };
            var origins = new Dictionary<GameObject, GameObject>();
            var item = new GameObject(); var effect = Child(item); var audio = Child(item); var rope = Child(item);
            origins[item] = root;
            using var scope = new CaptureSegmentScope(originResolver: go => Resolve(go, origins));
            var retired = new HashSet<int>();
            using var items = scope.CreateClient(_ => { }, id => retired.Add(id));
            using var effects = scope.CreateClient(_ => { }, id => retired.Add(id), go => go && go.activeInHierarchy);
            using var sounds = scope.CreateClient(_ => { }, id => retired.Add(id), go => go && go.activeInHierarchy);
            using var ropes = scope.CreateClient(_ => { }, id => retired.Add(id), go => go && go.activeInHierarchy);
            Check(items.Admit(item, urgent: true) && effects.Admit(effect, urgent: true) && sounds.Admit(audio, urgent: true) && ropes.Admit(rope, urgent: true));
            root.SetActive(false); scope.Observe();
            Check(item.activeInHierarchy && effect.activeInHierarchy && audio.activeInHierarchy && rope.activeInHierarchy);
            Check(retired.Count == 4 && !items.Admit(item) && !effects.Admit(effect) && !sounds.Admit(audio) && !ropes.Admit(rope));
            Check(!scope.HasPending);
        });
        test("native physical reparent to a new active root takes priority over an old spawn origin", () =>
        {
            var old = new GameObject(); var next = new GameObject();
            MapHandler.Instance = new() { segments = [new(old), new(next)] };
            var origins = new Dictionary<GameObject, GameObject>(); var source = Child(old); origins[source] = old;
            using var scope = new CaptureSegmentScope(originResolver: go => Resolve(go, origins));
            int retired = 0, preserves = 0;
            using var client = scope.CreateClient(_ => { }, _ => retired++, _ => { preserves++; return true; });
            Check(client.Admit(source, urgent: true)); source.transform.parent = next.transform;
            old.SetActive(false); scope.Observe();
            Check(retired == 0 && preserves == 0 && client.Admit(source));
            next.SetActive(false); scope.Observe();
            Check(retired == 1 && !client.Admit(source));
        });
        test("genuine detached and independent global tails survive old-root shutdown without an origin", () =>
        {
            var root = new GameObject(); MapHandler.Instance = new() { segments = [new(root)] };
            var origins = new Dictionary<GameObject, GameObject>();
            var detachedTail = Child(root); var globalTail = new GameObject(); var ordinary = Child(root);
            using var scope = new CaptureSegmentScope(originResolver: go => Resolve(go, origins));
            var retired = new List<int>();
            using var client = scope.CreateClient(_ => { }, retired.Add, go => go && go.activeInHierarchy);
            Check(client.Admit(detachedTail, urgent: true) && client.Admit(globalTail, urgent: true) && client.Admit(ordinary, urgent: true));
            detachedTail.transform.parent = null; root.SetActive(false); scope.Observe();
            Check(retired.Count == 1 && retired[0] == ordinary.GetInstanceID());
            Check(client.Admit(detachedTail) && client.Admit(globalTail) && !client.Admit(ordinary));
        });
        test("preserve callback detaches item and child bindings before old-root retirement in either client order", () =>
        {
            foreach (bool childrenFirst in new[] { false, true })
            {
                var root = new GameObject(); MapHandler.Instance = new() { segments = [new(root)] };
                var origins = new Dictionary<GameObject, GameObject>();
                var item = new GameObject(); var child = Child(item); var peer = Child(root);
                origins[item] = root;
                using var scope = new CaptureSegmentScope(originResolver: go => Resolve(go, origins));
                var retired = new List<int>(); int preserves = 0;
                using var itemClient = scope.CreateClient(_ => { }, retired.Add, source =>
                {
                    preserves++; origins.Remove(source); scope.RefreshOrigin(source); return true;
                });
                using var effectClient = scope.CreateClient(_ => { }, retired.Add, go => go && go.activeInHierarchy);
                if (childrenFirst) Check(effectClient.Admit(child, urgent: true));
                Check(itemClient.Admit(item, urgent: true));
                if (!childrenFirst) Check(effectClient.Admit(child, urgent: true));
                Check(effectClient.Admit(peer, urgent: true));
                root.SetActive(false); scope.Observe();
                Check(preserves == 1 && retired.Count == 1 && retired[0] == peer.GetInstanceID());
                Check(itemClient.Admit(item) && effectClient.Admit(child) && !effectClient.Admit(peer));
            }
        });
        test("preserve-time detach keeps a child's failed baseline pending while an unmoved failure retires", () =>
        {
            var root = new GameObject(); MapHandler.Instance = new() { segments = [new(root)] };
            var origins = new Dictionary<GameObject, GameObject>();
            var item = new GameObject(); var child = Child(item); var peer = Child(root);
            origins[item] = root; double now = 0; bool fail = true; int reads = 0;
            using var scope = new CaptureSegmentScope(originResolver: go => Resolve(go, origins), retryClock: () => now);
            using var children = scope.CreateClient(_ => { reads++; if (fail) throw new Exception("pending native baseline"); }, _ => { });
            using var items = scope.CreateClient(_ => { }, _ => { }, source =>
            {
                origins.Remove(source); scope.RefreshOrigin(source); return true;
            });
            children.Admit(child); children.Admit(peer); scope.Drain(10, 128);
            Check(reads == 2 && scope.FailedBaselineCount == 2);
            Check(items.Admit(item, urgent: true)); root.SetActive(false); scope.Observe();
            Check(scope.FailedBaselineCount == 1 && scope.HasPending);
            fail = false; now = .1; scope.Drain(10, 128);
            Check(reads == 3 && scope.FailedBaselineCount == 0 && !scope.HasPending && children.Admit(child) && !children.Admit(peer));
        });
        test("late catalogue rejection discards pending evidence while actual old-root shutdown ends it", () =>
        {
            MapHandler.Instance = null;
            using var scope = new CaptureSegmentScope();
            SegmentCaptureClient? client = null;
            var reasons = new List<bool>();
            var retained = new HashSet<int>();
            var future = new GameObject { activeSelf = false };
            var source = Child(future);
            using (client = scope.CreateClient(go => retained.Add(go.GetInstanceID()), id =>
            {
                reasons.Add(client!.OriginRejection);
                if (client.OriginRejection) retained.Remove(id);
            }))
            {
                Check(client.Admit(source, urgent: true));
                retained.Add(source.GetInstanceID());
                MapHandler.Instance = new() { segments = [new(future)] };
                scope.Observe();
                Check(reasons.Count == 1 && reasons[0] && retained.Count == 0);
                future.SetActive(true); scope.Observe(); scope.Drain();
                Check(retained.Contains(source.GetInstanceID()));
                future.SetActive(false); scope.Observe();
                Check(reasons.Count == 2 && !reasons[1] && retained.Contains(source.GetInstanceID()));
            }
        });
        test("late raw-field future and unselected roots report rejection for admitted sources", () =>
        {
            var active = new GameObject();
            var future = new GameObject { activeSelf = false };
            var variant = new GameObject();
            var laterMain = new MapHandler.MapSegment(null);
            var laterVariant = new MapHandler.MapSegment(null);
            MapHandler.Instance = new() { segments = [new(active), laterMain], variantSegments = [laterVariant] };
            using var scope = new CaptureSegmentScope();
            SegmentCaptureClient? client = null;
            var reasons = new List<bool>();
            using (client = scope.CreateClient(_ => { }, _ => reasons.Add(client!.OriginRejection)))
            {
                Check(client.Admit(Child(future), urgent: true) && client.Admit(Child(variant), urgent: true));
                laterMain.SetRawRoots(future); laterVariant.SetRawRoots(variant); scope.Observe();
                Check(reasons.Count == 2 && reasons.TrueForAll(value => value));
            }
        });
        test("native origin hint cannot override a real physical map parent", () =>
        {
            var actual = new GameObject(); var future = new GameObject { activeSelf = false };
            MapHandler.Instance = new() { segments = [new(actual),new(future)] }; int resolves=0, retired=0;
            using var scope = new CaptureSegmentScope(originResolver: _ => {resolves++;return future;});
            using var client = scope.CreateClient(_=>{},_=>retired++); var source=Child(actual); Check(client.Admit(source,urgent:true));
            scope.RefreshOrigin(source); Check(client.Admit(source)&&resolves==0&&retired==0);
        });
        test("late native origin refresh touches indexed descendants only and identifies origin rejection", () =>
        {
            var future=new GameObject {activeSelf=false};MapHandler.Instance=new(){segments=[new(future)]};var origins=new Dictionary<GameObject,GameObject>();int resolves=0,builds=0,preserves=0;
            using var scope=new CaptureSegmentScope(originResolver: go=>{resolves++;return Resolve(go,origins);});var reasons=new List<bool>();SegmentCaptureClient? client=null;
            using(client=scope.CreateClient(_=>builds++,_=>reasons.Add(client!.OriginRejection),_=>{preserves++;return true;}))
            {
                var source=new GameObject();var child=Child(source);var grandchild=Child(child);
                Check(client.Admit(source,urgent:true)&&client.Admit(child,urgent:true)&&client.Admit(grandchild,urgent:true));
                for(int i=0;i<1000;i++)Check(client.Admit(new GameObject(),urgent:true));
                int before=resolves;origins[source]=future;scope.RefreshOrigin(source);
                Check(resolves-before==3&&reasons.Count==3&&reasons.TrueForAll(value=>value)&&preserves==0);
                Check(!client.Admit(source,urgent:true)&&!client.Admit(child,urgent:true));
                origins.Remove(source);scope.RefreshOrigin(source);Check(scope.HasPending);scope.Drain(10,128);Check(builds==3&&!scope.HasPending);
                future.SetActive(true);scope.Observe();scope.Drain();Check(builds==3&&reasons.Count==3);
            }
        });
        test("detaching an admitted native item preserves its children when old root closes", () =>
        {
            var old=new GameObject();MapHandler.Instance=new(){segments=[new(old)]};var origins=new Dictionary<GameObject,GameObject>();var source=new GameObject();origins[source]=old;
            using var scope=new CaptureSegmentScope(originResolver:go=>Resolve(go,origins));int retired=0;using var client=scope.CreateClient(_=>{},_=>retired++);var child=Child(source);
            Check(client.Admit(source,urgent:true)&&client.Admit(child,urgent:true));origins.Remove(source);scope.RefreshOrigin(source);old.SetActive(false);scope.Observe();
            Check(retired==0&&client.Admit(source)&&client.Admit(child));
        });
        test("incoherent directory defers coalesced origin events until atomic recovery", () =>
        {
            var active=new GameObject();var future=new GameObject{activeSelf=false};var segment=new MapHandler.MapSegment(active);MapHandler.Instance=new(){segments=[segment,new(future)]};
            var origins=new Dictionary<GameObject,GameObject>();int resolves=0;using var scope=new CaptureSegmentScope(originResolver:go=>{resolves++;return Resolve(go,origins);});
            SegmentCaptureClient? client=null;var reasons=new List<bool>();using(client=scope.CreateClient(_=>{},_=>reasons.Add(client!.OriginRejection)))
            {
                var source=new GameObject();Check(client.Admit(source,urgent:true));segment.ThrowResolved=true;scope.Observe();int before=resolves;
                origins[source]=future;for(int i=0;i<1000;i++)scope.RefreshOrigin(source);
                Check(!scope.DirectoryReliable&&scope.HasPending&&reasons.Count==0&&resolves==before&&client.Admit(source));
                segment.ThrowResolved=false;scope.Observe();Check(scope.DirectoryReliable&&reasons.Count==1&&reasons[0]&&resolves==before+1&&!client.Admit(source,urgent:true));
            }
        });
        test("deferred detach resolves before root retirement and never erases admitted history", () =>
        {
            var old=new GameObject();var segment=new MapHandler.MapSegment(old);MapHandler.Instance=new(){segments=[segment]};var origins=new Dictionary<GameObject,GameObject>();var source=new GameObject();origins[source]=old;
            using var scope=new CaptureSegmentScope(originResolver:go=>Resolve(go,origins));int retired=0;using var client=scope.CreateClient(_=>{},_=>retired++);Check(client.Admit(source,urgent:true));
            segment.ThrowResolved=true;old.SetActive(false);scope.Observe();origins.Remove(source);scope.RefreshOrigin(source);Check(retired==0&&client.Admit(source));
            segment.ThrowResolved=false;scope.Observe();Check(retired==0&&client.Admit(source)&&!scope.HasPending);
        });
        test("Forget removes deferred origin dependencies before directory recovery", () =>
        {
            var root=new GameObject();var segment=new MapHandler.MapSegment(root);MapHandler.Instance=new(){segments=[segment]};int resolves=0,retired=0;
            using var scope=new CaptureSegmentScope(originResolver:_=>{resolves++;return null;});using var client=scope.CreateClient(_=>{},_=>retired++);var source=new GameObject();Check(client.Admit(source,urgent:true));
            segment.ThrowResolved=true;scope.Observe();scope.RefreshOrigin(source);client.Forget(source.GetInstanceID());int before=resolves;segment.ThrowResolved=false;scope.Observe();
            Check(!scope.HasPending&&resolves==before&&retired==0);
        });
        test("failed baseline remains pending and retries on increasing monotonic deadlines", () =>
        {
            MapHandler.Instance=null;double now=0;using var scope=new CaptureSegmentScope(retryClock:()=>now);int attempts=0,retires=0;
            using var client=scope.CreateClient(_=>{if(++attempts<3)throw new Exception("not ready");},_=>retires++);client.Admit(new());scope.Drain();
            Check(attempts==1&&retires==1&&scope.FailedBaselineCount==1&&scope.HasPending);
            Transform.ParentReads=0;for(int i=0;i<1000;i++)scope.Drain();Check(attempts==1&&Transform.ParentReads==0);
            now=.099;scope.Drain();Check(attempts==1);now=.1;scope.Drain();Check(attempts==2&&scope.FailedBaselineCount==1);
            now=.299;scope.Drain();Check(attempts==2);now=.301;scope.Drain();Check(attempts==3&&retires==2&&scope.FailedBaselineCount==0&&!scope.HasPending);
        });
        test("permanent callback failures stay nonready without per-frame candidate scans", () =>
        {
            MapHandler.Instance=null;double now=0;using var scope=new CaptureSegmentScope(retryClock:()=>now);int attempts=0;
            using var client=scope.CreateClient(_=>{attempts++;throw new Exception("permanent");},_=>{});
            for(int i=0;i<500;i++)client.Admit(new());for(int i=0;i<1000&&attempts<500;i++)scope.Drain(10,128);
            Check(attempts==500&&scope.FailedBaselineCount==500&&scope.HasPending);Transform.ParentReads=0;
            for(int i=0;i<1000;i++){scope.Observe();scope.Drain();}Check(attempts==500&&Transform.ParentReads==0);
            now=.1;int before=attempts;scope.Drain(10,4);Check(attempts-before==4&&scope.FailedBaselineCount==500&&scope.HasPending);
        });
        test("failed baselines cannot starve another client and share the global work budget", () =>
        {
            MapHandler.Instance=null;double now=0;using var scope=new CaptureSegmentScope(retryClock:()=>now);int failures=0,good=0;
            using var broken=scope.CreateClient(_=>{failures++;throw new Exception("baseline");},_=>{});using var healthy=scope.CreateClient(_=>good++,_=>{});
            for(int i=0;i<10;i++)broken.Admit(new());scope.Drain(10,128);healthy.Admit(new());now=.1;scope.Drain(10,4);Check(good==1&&scope.FailedBaselineCount==10);
            long clock=0;healthy.Admit(new());using(CaptureBootstrapWorkBudget.Begin(2,()=>clock,1000)){clock=2;int before=good;scope.Drain();Check(good==before&&scope.HasPending);}
            scope.Drain();Check(good==2&&failures>=10);
        });
        test("ordinary repeat notifications do not reset failure backoff but true urgent admission can recover", () =>
        {
            MapHandler.Instance=null;double now=0;using var scope=new CaptureSegmentScope(retryClock:()=>now);int attempts=0;using var client=scope.CreateClient(_=>{attempts++;throw new Exception();},_=>{});var source=new GameObject();
            client.Admit(source);scope.Drain();for(int i=0;i<1000;i++)Check(!client.Admit(source));scope.Drain();Check(attempts==1&&scope.FailedBaselineCount==1);
            Check(client.Admit(source,urgent:true)&&scope.FailedBaselineCount==0&&!scope.HasPending);scope.Drain();Check(attempts==1);
        });
        test("reentrant retire notification cannot turn a delayed retry into an immediate retry loop", () =>
        {
            MapHandler.Instance=null;double now=0;using var scope=new CaptureSegmentScope(retryClock:()=>now);int attempts=0;SegmentCaptureClient? client=null;var source=new GameObject();
            using(client=scope.CreateClient(_=>{attempts++;throw new Exception();},_=>client!.Admit(source)))
            {client.Admit(source);scope.Drain(10,128);for(int i=0;i<100;i++)scope.Drain();Check(attempts==1&&scope.FailedBaselineCount==1);now=.1;scope.Drain(10,128);Check(attempts==2&&scope.FailedBaselineCount==1);}
        });
        test("callback Forget and Dispose followed by throw cannot resurrect failed baselines", () =>
        {
            MapHandler.Instance=null;using var scope=new CaptureSegmentScope();SegmentCaptureClient? first=null,second=null;
            first=scope.CreateClient(go=>{first!.Forget(go.GetInstanceID());throw new Exception();},_=>{});second=scope.CreateClient(_=>{second!.Dispose();throw new Exception();},_=>{});
            first.Admit(new());second.Admit(new());scope.Drain(10,128);Check(scope.FailedBaselineCount==0&&!scope.HasPending);first.Dispose();
        });
        test("root shutdown clears failed nonrecord baselines and activation requeues a fresh baseline", () =>
        {
            var root=new GameObject();MapHandler.Instance=new(){segments=[new(root)]};double now=0;using var scope=new CaptureSegmentScope(retryClock:()=>now);bool fail=true;int attempts=0;
            using var client=scope.CreateClient(_=>{attempts++;if(fail)throw new Exception();},_=>{});client.Admit(Child(root));scope.Drain();Check(scope.FailedBaselineCount==1&&scope.HasPending);
            root.SetActive(false);scope.Observe();Check(scope.FailedBaselineCount==0&&!scope.HasPending);fail=false;root.SetActive(true);scope.Observe();Check(scope.HasPending);scope.Drain();Check(attempts==2&&!scope.HasPending);
        });
        test("failed baseline transferred from closing root stays pending until its valid new baseline", () =>
        {
            var old=new GameObject();var next=new GameObject();MapHandler.Instance=new(){segments=[new(old),new(next)]};double now=0;bool fail=true;int attempts=0;
            using var scope=new CaptureSegmentScope(retryClock:()=>now);using var client=scope.CreateClient(_=>{attempts++;if(fail)throw new Exception();},_=>{});var source=Child(old);
            client.Admit(source);scope.Drain();source.transform.parent=next.transform;old.SetActive(false);scope.Observe();Check(scope.FailedBaselineCount==1&&scope.HasPending);
            fail=false;now=.1;scope.Drain();Check(attempts==2&&!scope.HasPending&&client.Admit(source));
        });
        test("Forget and Dispose release delayed retry references without waiting for deadlines", () =>
        {
            MapHandler.Instance=null;using var scope=new CaptureSegmentScope(retryClock:()=>0);using var client=scope.CreateClient(_=>throw new Exception(),_=>{});
            var weak=FailedReference(client,scope);Check(scope.FailedBaselineCount==0&&!scope.HasPending);Collect();Check(!weak.IsAlive);
            var source=new GameObject();client.Admit(source);scope.Drain();Check(scope.FailedBaselineCount==1);client.Dispose();Check(scope.FailedBaselineCount==0&&!scope.HasPending);
        });
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static WeakReference FailedReference(SegmentCaptureClient client,CaptureSegmentScope scope)
    {var source=new GameObject();client.Admit(source);scope.Drain();var weak=new WeakReference(source);client.Forget(source.GetInstanceID());return weak;}
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Collect(){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
}
