using System;
using System.Linq;
using UnityEngine;
using PeakReplayLab;

internal static class LuggageScopeTests
{
    private static void Check(bool value) { if (!value) throw new Exception("actual luggage capture scope contract failed"); }
    private static (GameObject Coast, GameObject Forest) Map()
    {
        UnityEngine.Object.NativeRegistry.Clear(); VisualReplica.Reads=VisualReplica.Releases=0; VisualReplica.ThrowNextRead=false; Time.timeAsDouble=0; Time.frameCount=0;
        var coast=new GameObject("coast"); var forest=new GameObject("forest"); forest.SetActive(false);
        MapHandler.Instance=new MapHandler { segments=new[]{new MapHandler.MapSegment(coast),new MapHandler.MapSegment(forest)} };
        return (coast,forest);
    }
    private static Luggage Crate(GameObject root, string name, bool animation=false)
    {
        var source=new GameObject(name); source.transform.parent=root.transform;
        var crate=new Luggage(source);
        if (animation) source.Components[typeof(Animator)]=new Animator(source);
        return crate;
    }
    private static void Drain(CaptureSegmentScope scope, int passes=32) { for(int i=0;i<passes;i++) scope.Drain(); }
    public static void Run(Action<string,Action> test)
    {
        test("actual luggage capture never builds future-map animator or pose directories", () =>
        {
            var map=Map(); var coast=Crate(map.Coast,"coast-crate"); var future=Crate(map.Forest,"future-crate",true);
            using var scope=new CaptureSegmentScope(); using var capture=new LuggageCapture();
            Check(VisualReplica.Reads==0 && future.gameObject.ComponentReads==0);
            Drain(scope); var frames=capture.Capture(0);
            Check(frames.Length==1 && frames[0].Key==coast.gameObject.name);
            Check(future.gameObject.ComponentReads==0 && VisualReplica.Reads==1);
        });
        test("actual luggage retirement leaves immutable prior page state and records next map only after activation", () =>
        {
            var map=Map(); var coast=Crate(map.Coast,"old-crate"); var forest=Crate(map.Forest,"new-crate");
            using var scope=new CaptureSegmentScope(); using var capture=new LuggageCapture();
            Drain(scope); var old=capture.Capture(0); Check(old.Length==1 && old[0].Pose.Active);
            MapHandler.Instance!.currentSegment=1; scope.Observe(); Drain(scope);
            Check(capture.Capture(.033).Single().Key=="old-crate" && forest.gameObject.ComponentReads==0);
            map.Coast.SetActive(false); map.Forest.SetActive(true); scope.Observe(); Drain(scope);
            var next=capture.Capture(.066);
            Check(next.Length==1 && next[0].Key=="new-crate" && VisualReplica.Releases==1);
            Check(old.Length==1 && old[0].Key=="old-crate" && old[0].Pose.Active && !old[0].Open);
        });
        test("actual open before deferred baseline bypasses backlog with its real clip anchor while future action remains excluded", () =>
        {
            var map=Map(); var coast=Crate(map.Coast,"open-now",true); var future=Crate(map.Forest,"never-open",true);
            using var scope=new CaptureSegmentScope(); using var capture=new LuggageCapture();
            coast.IsOpen=future.IsOpen=true;
            LuggageEventObserver.Emit(coast,LuggageEventKind.Open,.012);
            LuggageEventObserver.Emit(future,LuggageEventKind.Open,.012);
            Time.frameCount=1; var frames=capture.Capture(.033);
            Check(frames.Length==1 && frames[0].Open && frames[0].Animation!=null);
            Check(frames[0].Animation!.AnchorTime==.012 && frames[0].Animation!.Clip=="clip-open");
            Check(future.gameObject.ComponentReads==0);
        });
        test("actual initial chest pose work stays bounded by the global admission budget", () =>
        {
            var map=Map(); for(int i=0;i<20;i++) Crate(map.Coast,"crate-"+i);
            using var scope=new CaptureSegmentScope(); using var capture=new LuggageCapture();
            Check(capture.Capture(0).Length==0 && VisualReplica.Reads==0);
            scope.Drain(); var first=capture.Capture(.033);
            Check(first.Length>0 && first.Length<=4 && VisualReplica.Reads<=4);
            Drain(scope); Check(capture.Capture(.066).Length==20);
        });
        test("actual native root reactivation builds a new full baseline without changing retained history", () =>
        {
            var map=Map(); var coast=Crate(map.Coast,"restored-crate");
            using var scope=new CaptureSegmentScope(); using var capture=new LuggageCapture();
            Drain(scope); var before=capture.Capture(0);
            map.Coast.SetActive(false); scope.Observe(); Check(capture.Capture(.033).Length==0);
            coast.IsOpen=true; map.Coast.SetActive(true); scope.Observe(); Drain(scope);
            var after=capture.Capture(.066);
            Check(after.Length==1 && after[0].Open && !before[0].Open && before[0].Pose.Active);
        });
        test("disposing actual chest capture cancels queued admission and isolates a later recorder", () =>
        {
            var map=Map(); Crate(map.Coast,"disposed-before-baseline");
            using var scope=new CaptureSegmentScope(); var capture=new LuggageCapture(); capture.Dispose();
            Drain(scope); Check(VisualReplica.Reads==0);
            using var next=new LuggageCapture(); Drain(scope); Check(next.Capture(0).Length==1);
        });
        test("actual chest initial snapshot obeys a shared expired budget and readiness waits for every pose", () =>
        {
            var map=Map(); for(int i=0;i<8;i++) Crate(map.Coast,"budget-crate-"+i);
            using var scope=new CaptureSegmentScope(); using var capture=new LuggageCapture(); Drain(scope);
            long tick=0;
            using(CaptureBootstrapWorkBudget.Begin(2,()=>tick++,1000,true))
                Check(capture.Capture(0).Length==1 && VisualReplica.Reads==1 && !capture.InitialBaselineReady);
            Check(capture.Capture(.033).Length==8 && capture.InitialBaselineReady);
        });
        test("actual chest native open bypasses an exhausted initial budget while inactive future action stays excluded", () =>
        {
            var map=Map(); var active=Crate(map.Coast,"urgent-open",true); var future=Crate(map.Forest,"future-open",true);
            using var scope=new CaptureSegmentScope(); using var capture=new LuggageCapture();
            active.IsOpen=future.IsOpen=true; LuggageEventObserver.Emit(active,LuggageEventKind.Open,0); LuggageEventObserver.Emit(future,LuggageEventKind.Open,0);
            Time.frameCount=1; long tick=0;
            using(CaptureBootstrapWorkBudget.Begin(2,()=>tick,1000,true))
            { tick=2; var frames=capture.Capture(.033); Check(frames.Length==1 && frames[0].Open && capture.InitialBaselineReady); }
            Check(future.gameObject.ComponentReads==0 && VisualReplica.Reads==1);
        });
        test("actual initial-only chest reads retain static history but a new native action still updates its pose", () =>
        {
            var map=Map(); var active=Crate(map.Coast,"idle-then-open",true);
            using var scope=new CaptureSegmentScope(); using var capture=new LuggageCapture(); Drain(scope);
            var old=capture.Capture(0); int before=VisualReplica.Reads;
            using(CaptureBootstrapWorkBudget.Begin(initialOnly:true)) Check(ReferenceEquals(old,capture.Capture(1)) && VisualReplica.Reads==before);
            active.IsOpen=true; LuggageEventObserver.Emit(active,LuggageEventKind.Open,1.01); Time.frameCount=1;
            using(CaptureBootstrapWorkBudget.Begin(initialOnly:true)) Check(capture.Capture(1.04)[0].Open && VisualReplica.Reads>before);
            Check(!old[0].Open);
        });
        test("actual initial pose failure remains not ready and recovers without a false completed baseline", () =>
        {
            var map=Map(); Crate(map.Coast,"retry-pose");
            using var scope=new CaptureSegmentScope(); using var capture=new LuggageCapture(); Drain(scope);
            VisualReplica.ThrowNextRead=true; Check(capture.Capture(0).Length==0 && !capture.InitialBaselineReady);
            Check(capture.Capture(.033).Length==1 && capture.InitialBaselineReady);
        });
        test("actual transient chest event read failure preserves its prior checkpoint instead of inventing disappearance", () =>
        {
            var map=Map(); var source=Crate(map.Coast,"event-read-failure");
            using var scope=new CaptureSegmentScope(); using var capture=new LuggageCapture(); Drain(scope);
            var before=capture.Capture(0);
            LuggageEventObserver.Emit(source,LuggageEventKind.Appearance,1); VisualReplica.ThrowNextRead=true;
            Check(ReferenceEquals(before,capture.Capture(1)) && capture.InitialBaselineReady);
            Check(capture.Capture(1.033).Length==1 && before.Length==1 && before[0].Pose.Active);
        });
        test("actual inactive chest and explicitly excluded ambiguous paths finish initialization without fake visible frames", () =>
        {
            var map=Map(); var inactive=Crate(map.Coast,"inactive-in-current-root"); inactive.gameObject.SetActive(false);
            Crate(map.Coast,"ambiguous-path"); Crate(map.Coast,"ambiguous-path");
            using var scope=new CaptureSegmentScope(); using var capture=new LuggageCapture(); Drain(scope);
            var frames=capture.Capture(0); Check(frames.Length==1 && !frames[0].Pose.Active && capture.InitialBaselineReady);
        });
    }
}
