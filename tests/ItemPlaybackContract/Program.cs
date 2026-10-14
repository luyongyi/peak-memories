using PeakReplayLab;
using UnityEngine;

int passed = 0;
void Check(bool value, string detail = "") { if (!value) throw new Exception("Item playback contract failed: " + detail); }
void Tick() { Time.frameCount++; Time.unscaledTime += 1f / 60; }
void Test(string name, Action test)
{
    Time.frameCount = 0; Time.unscaledTime = 0;
    ItemDatabase.Prefab = new Item(); Item.ALL_ITEMS.Clear();
    VisualReplica.Created.Clear(); VisualReplica.CanStartBuild = true; VisualReplica.DeferBuild = VisualReplica.FailApply = false;
    NativeVisualAppearance.Playback.Created.Clear(); NativeVisualAppearance.Playback.Defer = false;
    NativeLightAppearance.Playback.Created.Clear(); NativeLightAppearance.Playback.Defer = false;
    FrogIdleAnimationRuntime.Created.Clear(); FrogIdleAnimationRuntime.CanCreate=true;
    EarlyWormAnimationRuntime.Created.Clear();EarlyWormAnimationRuntime.CanCreate=true;
    test(); passed++; Console.WriteLine("PASS " + name);
}
void Exhaust(PresentationWorkBudget budget)
{ Tick(); budget.BeginFrame(Time.frameCount); for (int i = 0; i < PresentationWorkBudget.MaximumBuildSteps; i++) Check(budget.Build()); Check(!budget.Build()); }
void Apply(ItemPlayback playback, ItemFrame frame, bool seek = false)
{ var frames = new[] { frame }; playback.Apply(frames, frames, 0, seek); }
void Absent(ItemPlayback playback)
{ playback.Apply(Array.Empty<ItemFrame>(), Array.Empty<ItemFrame>(), 0); }
ItemFrame Page(ItemFrame source) => new() { Key = source.Key, Pose = source.Pose };

Test("static page reference change retains the berry while renderer work yields", () =>
{
    var budget = new PresentationWorkBudget(() => 0); using var playback = new ItemPlayback(workBudget: budget);
    var first = new ItemFrame(); Apply(playback, first); var replica = VisualReplica.Created.Single();
    var second = Page(first); var frames = new[] { second }; Exhaust(budget); playback.Apply(frames, frames, 0);
    Check(replica.Root.activeSelf && replica.Root.Hides == 0 && replica.PoseApplies == 1);
    Check(playback.VisibleCount == 1 && playback.MissingCount == 0);
    Tick(); playback.Enforce();
    Check(replica.Root.activeSelf && ReferenceEquals(NativeVisualAppearance.Playback.Created.Single().Pass.Applied, second.Visuals));
});
Test("pending work bypasses the static array memo, then completed static frames skip work", () =>
{
    var budget = new PresentationWorkBudget(() => 0); using var playback = new ItemPlayback(workBudget: budget);
    Apply(playback, new()); var latest = new[] { new ItemFrame() }; Exhaust(budget); playback.Apply(latest, latest, 0);
    var appearance = NativeVisualAppearance.Playback.Created.Single(); int calls = appearance.Pass.Calls;
    Tick(); playback.Apply(latest, latest, 0); Check(appearance.Pass.Calls > calls && playback.MissingCount == 0);
    calls = appearance.Pass.Calls; Tick(); playback.Apply(latest, latest, .9f); Check(appearance.Pass.Calls == calls);
});
Test("repeated page boundaries under exhausted budget never toggle a presented berry", () =>
{
    var budget = new PresentationWorkBudget(() => 0); using var playback = new ItemPlayback(workBudget: budget);
    var frame = new ItemFrame(); Apply(playback, frame); var replica = VisualReplica.Created.Single();
    for (int i = 0; i < 32; i++) { frame = Page(frame); Exhaust(budget); Apply(playback, frame); Tick(); playback.Enforce(); }
    Check(replica.Root.activeSelf && replica.Root.Hides == 0 && playback.VisibleCount == 1 && replica.PoseApplies == 1);
});
Test("a deferred native light pass retains the previous complete item", () =>
{
    var budget = new PresentationWorkBudget(() => 0); using var playback = new ItemPlayback(workBudget: budget);
    var frame = new ItemFrame(); Apply(playback, frame); var replica = VisualReplica.Created.Single();
    var latest = Page(frame); latest.Visuals = frame.Visuals; Exhaust(budget); Apply(playback, latest);
    Check(replica.Root.activeSelf && replica.Root.Hides == 0 && NativeLightAppearance.Playback.Created.Single().Pass.Preparing);
    Tick(); playback.Enforce(); Check(ReferenceEquals(NativeLightAppearance.Playback.Created.Single().Pass.Applied, latest.Lights));
});
Test("finishing older appearance A cannot strand the latest static page B", () =>
{
    var budget = new PresentationWorkBudget(() => 0); using var playback = new ItemPlayback(workBudget: budget);
    Apply(playback, new()); var a = new ItemFrame(); Exhaust(budget); Apply(playback, a);
    var b = new ItemFrame(); Tick(); Apply(playback, b);
    var appearance = NativeVisualAppearance.Playback.Created.Single(); Check(ReferenceEquals(appearance.Pass.Applied, a.Visuals));
    Check(playback.VisibleCount == 1 && playback.MissingCount == 0);
    Tick(); playback.Enforce(); Check(ReferenceEquals(appearance.Pass.Applied, b.Visuals));
});
Test("finishing older light request A cannot strand the latest static page B", () =>
{
    var budget = new PresentationWorkBudget(() => 0); using var playback = new ItemPlayback(workBudget: budget);
    var first = new ItemFrame(); Apply(playback, first); var a = Page(first); a.Visuals = first.Visuals;
    Exhaust(budget); Apply(playback, a); var b = Page(a); b.Visuals = first.Visuals; Tick(); Apply(playback, b);
    var lights = NativeLightAppearance.Playback.Created.Single(); Check(ReferenceEquals(lights.Pass.Applied, a.Lights));
    Tick(); playback.Enforce(); Check(ReferenceEquals(lights.Pass.Applied, b.Lights));
});
Test("large animated appearances still present when a completed target is older than the current frame", () =>
{
    using var playback = new ItemPlayback(workBudget: new(() => 0));
    for (int i = 0; i < 9; i++)
    {
        Tick(); Apply(playback, new ItemFrame { Pose = new() { Revision = i }, Visuals = new NativeRendererFrame[600] });
    }
    var replica = VisualReplica.Created.Single(); Check(replica.Root.activeSelf && replica.PoseApplies >= 2, "animated target starvation");
});
Test("initial construction stays hidden until all native appearance and light work completes", () =>
{
    var budget = new PresentationWorkBudget(() => 0); using var playback = new ItemPlayback(workBudget: budget);
    Exhaust(budget); Apply(playback, new()); var replica = VisualReplica.Created.Single(); Check(!replica.Root.activeSelf);
    NativeVisualAppearance.Playback.Defer = true; Tick(); playback.Enforce(); Check(!replica.Root.activeSelf);
    NativeVisualAppearance.Playback.Defer = false; NativeLightAppearance.Playback.Defer = true; Tick(); playback.Enforce(); Check(!replica.Root.activeSelf);
    NativeLightAppearance.Playback.Defer = false; Tick(); playback.Enforce(); Check(replica.Root.activeSelf);
});
Test("creation admission remains bounded by the shared budget", () =>
{
    var budget = new PresentationWorkBudget(() => 0); using var playback = new ItemPlayback(workBudget: budget);
    var frames = Enumerable.Range(0, 7).Select(i => new ItemFrame { Key = "berry" + i }).ToArray();
    playback.Apply(frames, frames, 0); Check(VisualReplica.Created.Count == PresentationWorkBudget.MaximumCreations && playback.MissingCount == 3);
    Tick(); playback.Enforce(); Check(VisualReplica.Created.Count == 7 && playback.VisibleCount == 7);
});
Test("explicit seek hides stale geometry through renderer and light deferrals", () =>
{
    using var playback = new ItemPlayback(workBudget: new(() => 0)); Apply(playback, new()); var replica = VisualReplica.Created.Single();
    NativeVisualAppearance.Playback.Defer = true; Tick(); Apply(playback, new(), true); Check(!replica.Root.activeSelf && playback.MissingCount == 1);
    NativeVisualAppearance.Playback.Defer = false; NativeLightAppearance.Playback.Defer = true; Tick(); playback.Enforce(); Check(!replica.Root.activeSelf);
    NativeLightAppearance.Playback.Defer = false; Tick(); playback.Enforce(); Check(replica.Root.activeSelf && playback.VisibleCount == 1);
});
Test("seek reset survives a build yield and later appearance yield before the light pass runs", () =>
{
    using var playback = new ItemPlayback(workBudget: new(() => 0)); Apply(playback, new());
    NativeLightAppearance.Playback.Defer = true; Tick(); Apply(playback, new()); var lights = NativeLightAppearance.Playback.Created.Single();
    Check(lights.Pass.Preparing); VisualReplica.DeferBuild = true; var sought = new ItemFrame(); Tick(); Apply(playback, sought, true);
    Check(!VisualReplica.Created.Single().Root.activeSelf && lights.Pass.Restarts == 0);
    VisualReplica.DeferBuild = false; NativeVisualAppearance.Playback.Defer = true; Tick(); playback.Enforce(); Check(lights.Pass.Restarts == 0);
    NativeVisualAppearance.Playback.Defer = NativeLightAppearance.Playback.Defer = false; Tick(); playback.Enforce();
    Check(lights.Pass.Restarts == 1 && ReferenceEquals(lights.Pass.Applied, sought.Lights));
});
Test("newer seeks replace both pending native targets before the item becomes visible", () =>
{
    using var playback = new ItemPlayback(workBudget: new(() => 0)); Apply(playback, new());
    NativeVisualAppearance.Playback.Defer = true; Tick(); Apply(playback, new(), true);
    NativeVisualAppearance.Playback.Defer = false; NativeLightAppearance.Playback.Defer = true; Tick(); playback.Enforce();
    var newest = new ItemFrame(); NativeVisualAppearance.Playback.Defer = true; Tick(); Apply(playback, newest, true);
    Check(!VisualReplica.Created.Single().Root.activeSelf);
    NativeVisualAppearance.Playback.Defer = NativeLightAppearance.Playback.Defer = false; Tick(); playback.Enforce();
    Check(ReferenceEquals(NativeVisualAppearance.Playback.Created.Single().Pass.Applied, newest.Visuals));
    Check(ReferenceEquals(NativeLightAppearance.Playback.Created.Single().Pass.Applied, newest.Lights));
    Check(ReferenceEquals(VisualReplica.Created.Single().LastPose, newest.Pose));
});
Test("inactive records hide immediately despite an unfinished appearance", () =>
{
    var budget = new PresentationWorkBudget(() => 0); using var playback = new ItemPlayback(workBudget: budget);
    Apply(playback, new()); Exhaust(budget); Apply(playback, new()); var replica = VisualReplica.Created.Single(); Check(replica.Root.activeSelf);
    Apply(playback, new ItemFrame { Pose = new() { Active = false } }); Check(!replica.Root.activeSelf && playback.VisibleCount == 0 && playback.MissingCount == 0);
    Tick(); playback.Enforce(); Check(!replica.Root.activeSelf);
});
Test("same-key pool wake invalidates the stationary pose memo and remains hidden while pending", () =>
{
    using var playback = new ItemPlayback(workBudget: new(() => 0)); var frame = new ItemFrame(); Apply(playback, frame);
    var replica = VisualReplica.Created.Single(); Tick(); Absent(playback); Check(!replica.Root.activeSelf);
    var wake = Page(frame); NativeVisualAppearance.Playback.Defer = true; Tick(); Apply(playback, wake); Check(!replica.Root.activeSelf);
    NativeVisualAppearance.Playback.Defer = false; Tick(); playback.Enforce();
    Check(VisualReplica.Created.Count == 1 && replica.Root.activeSelf && replica.PoseApplies == 2 && replica.Resets == 0);
});
Test("same-key pool wake cancels the light request left unfinished before disappearance", () =>
{
    using var playback = new ItemPlayback(workBudget: new(() => 0)); Apply(playback, new());
    NativeLightAppearance.Playback.Defer = true; Tick(); Apply(playback, new()); Tick(); Absent(playback);
    var wake = new ItemFrame(); NativeLightAppearance.Playback.Defer = false; Tick(); Apply(playback, wake);
    var lights = NativeLightAppearance.Playback.Created.Single();
    Check(lights.Pass.Restarts == 1 && ReferenceEquals(lights.Pass.Applied, wake.Lights) && VisualReplica.Created.Single().Root.activeSelf);
});
Test("different-key pool reuse restores a fresh native baseline under the existing build budget", () =>
{
    var budget = new PresentationWorkBudget(() => 0); using var playback = new ItemPlayback(workBudget: budget);
    Apply(playback, new()); Tick(); Absent(playback); var replica = VisualReplica.Created.Single();
    Exhaust(budget); Apply(playback, new ItemFrame { Key = "other berry" }); Check(!replica.Root.activeSelf && replica.IsResetting);
    Tick(); playback.Enforce(); Check(replica.Root.activeSelf && replica.Resets == 1 && VisualReplica.Created.Count == 1);
    Check(NativeVisualAppearance.Playback.Created.Count == 2 && NativeLightAppearance.Playback.Created.Count == 2);
});
Test("pose failure retires the broken visual and retry can build a healthy replacement", () =>
{
    var warnings = new List<string>(); using var playback = new ItemPlayback(warnings.Add, workBudget: new(() => 0)); Apply(playback, new());
    VisualReplica.FailApply = true; Tick(); Apply(playback, new()); Check(VisualReplica.Created.Single().Retired && playback.MissingCount == 1 && warnings.Count == 1);
    VisualReplica.FailApply = false; Tick(); Apply(playback, new()); Check(VisualReplica.Created.Count == 1);
    Time.unscaledTime += 2; Tick(); Apply(playback, new()); Check(VisualReplica.Created.Count == 2 && VisualReplica.Created[1].Root.activeSelf);
});
Test("backpack attachment continues on static snapshots and detaches on the recorded ownership change", () =>
{
    string instance = Guid.NewGuid().ToString("N");
    var actor = new VisualActor { State = new() { Id = "actor", InventoryKnown = true, BackpackWorn = true, BackpackType = 1,
        Inventory = new[] { new InventoryFrame { Instance = instance, Backpack = true } } } };
    using var playback = new ItemPlayback(actors: new Dictionary<string, VisualActor> { ["actor"] = actor }, workBudget: new(() => 0));
    var frame = new ItemFrame { Key = instance + "/berry", State = BackpackAttachmentPolicy.InBackpack, HolderId = "actor" }; var frames = new[] { frame };
    playback.Apply(frames, frames, 0); var replica = VisualReplica.Created.Single(); Check(replica.Root.transform.parent == actor.Anchor);
    int applies = NativeVisualAppearance.Playback.Created.Single().Pass.Calls; Tick(); playback.Apply(frames, frames, 0);
    Check(NativeVisualAppearance.Playback.Created.Single().Pass.Calls > applies);
    Tick(); Apply(playback, new ItemFrame { Key = frame.Key }); Check(replica.Root.transform.parent == null && replica.Root.activeSelf);
});
Test("disposing pending work restores originals and prevents later enforcement from resurrecting visuals", () =>
{
    var original = new Item(); Item.ALL_ITEMS.Add(original); var budget = new PresentationWorkBudget(() => 0); var playback = new ItemPlayback(workBudget: budget);
    Apply(playback, new()); playback.Enforce(); Check(original.Renderer.forceRenderingOff);
    Exhaust(budget); Apply(playback, new(), true); playback.Dispose(); playback.Dispose(); Tick(); playback.Enforce();
    Check(!original.Renderer.forceRenderingOff && !VisualReplica.Created.Single().Root.activeSelf && playback.VisibleCount == 0 && playback.MissingCount == 0);
    bool rejected = false; try { Apply(playback, new()); } catch (ObjectDisposedException) { rejected = true; } Check(rejected);
});
Test("static native idle clocks advance without rerunning the item appearance or pose pipeline",()=>
{
    using var playback=new ItemPlayback(workBudget:new(()=>0));var frame=new ItemFrame{IdleAnimation=new()};var frames=new[]{frame};
    playback.Apply(frames,frames,0,replayTime:1);var runtime=FrogIdleAnimationRuntime.Created.Single();var replica=VisualReplica.Created.Single();
    int appearances=NativeVisualAppearance.Playback.Created.Single().Pass.Calls,poses=replica.PoseApplies;
    Tick();playback.Apply(frames,frames,0,replayTime:2);Check(runtime.Times.SequenceEqual(new[]{1d,2d}));
    Check(NativeVisualAppearance.Playback.Created.Single().Pass.Calls==appearances&&replica.PoseApplies==poses);
    Tick();playback.Apply(frames,frames,0,replayTime:2);Check(runtime.Times.Count==2,"paused clock");
    Tick();playback.Apply(frames,frames,0,replayTime:1.5);Check(runtime.Times.Last()==1.5,"reverse clock");
});
Test("leaving native idle restores geometry even when the action reuses the same held Pose",()=>
{
    using var playback=new ItemPlayback(workBudget:new(()=>0));var idle=new ItemFrame{IdleAnimation=new()};Apply(playback,idle);var replica=VisualReplica.Created.Single();
    int poses=replica.PoseApplies;Tick();Apply(playback,new ItemFrame{Pose=idle.Pose});Check(replica.PoseApplies==poses+1);
    int samples=FrogIdleAnimationRuntime.Created.Single().Times.Count;Tick();playback.Enforce();Check(FrogIdleAnimationRuntime.Created.Single().Times.Count==samples);
});
Test("a replaced idle descriptor invalidates a cached same-time animation and held pose",()=>
{
    using var playback=new ItemPlayback(workBudget:new(()=>0));var idle=new ItemFrame{IdleAnimation=new()};Apply(playback,idle);var replica=VisualReplica.Created.Single();var runtime=FrogIdleAnimationRuntime.Created.Single();
    int poses=replica.PoseApplies,samples=runtime.Times.Count;Tick();Apply(playback,new ItemFrame{Pose=idle.Pose,IdleAnimation=new()});
    Check(replica.PoseApplies==poses+1&&runtime.Times.Count==samples+1);
});
Test("deferred native idle construction retries with the requested absolute replay time",()=>
{
    using var playback=new ItemPlayback(workBudget:new(()=>0));var frames=new[]{new ItemFrame{IdleAnimation=new()}};
    NativeLightAppearance.Playback.Defer=true;playback.Apply(frames,frames,0,replayTime:55);Check(FrogIdleAnimationRuntime.Created.Count==0);
    NativeLightAppearance.Playback.Defer=false;Tick();playback.Enforce();Check(FrogIdleAnimationRuntime.Created.Single().Times.Single()==55);
});
Test("different lifetime pool reuse discards native idle runtime state",()=>
{
    using var playback=new ItemPlayback(workBudget:new(()=>0));Apply(playback,new ItemFrame{IdleAnimation=new()});var old=FrogIdleAnimationRuntime.Created.Single();
    Tick();Absent(playback);Tick();Apply(playback,new ItemFrame{Key="other frog",IdleAnimation=new()});
    Check(old.Disposed&&FrogIdleAnimationRuntime.Created.Count==2&&VisualReplica.Created.Count==1);
});
Test("missing native idle resources fail visibly and retain normal retry behavior",()=>
{
    var warnings=new List<string>();using var playback=new ItemPlayback(warnings.Add,workBudget:new(()=>0));FrogIdleAnimationRuntime.CanCreate=false;
    Apply(playback,new ItemFrame{IdleAnimation=new()});Check(playback.MissingCount==1&&warnings.Count==1&&!VisualReplica.Created.Single().Root.activeSelf);
});
Test("ground motion uses its independent five Hertz bracket instead of the global frame mix",()=>
{
    using var playback=new ItemPlayback(workBudget:new(()=>0));
    var a=new ItemFrame{SampleTimeKnown=true,SampleTime=0,ClockMode=CreatureReplayRules.ActiveClock,Pose=new(){Position=new float[]{0,0,0}}};
    var b=new ItemFrame{SampleTimeKnown=true,SampleTime=.2,ClockMode=CreatureReplayRules.ActiveClock,Pose=new(){Position=new float[]{2,0,0}}};
    var observations=new ItemObservationTimeline{Left=a,Right=b};var stored=new[]{a};
    playback.Apply(stored,stored,.9f,replayTime:.05,observations:observations);var replica=VisualReplica.Created.Single();
    Check(replica.Root.transform.position.x==.5f&&ReferenceEquals(replica.LastPose,a.Pose)&&ReferenceEquals(replica.LastNextPose,b.Pose)&&replica.LastMix==.25f);
});
Test("identical global arrays continue sparse movement as replay time changes and remain stable while paused",()=>
{
    using var playback=new ItemPlayback(workBudget:new(()=>0));
    var a=new ItemFrame{SampleTimeKnown=true,SampleTime=0,Pose=new(){Position=new float[]{0,0,0}}};
    var b=new ItemFrame{SampleTimeKnown=true,SampleTime=.2,Pose=new(){Position=new float[]{2,0,0}}};
    var observations=new ItemObservationTimeline{Left=a,Right=b};var stored=new[]{a};
    playback.Apply(stored,stored,0,replayTime:.05,observations:observations);var replica=VisualReplica.Created.Single();
    Tick();playback.Apply(stored,stored,0,replayTime:.1,observations:observations);
    Check(replica.Root.transform.position.x==1&&replica.PoseApplies==2&&observations.Queries==2,"same-array time advance");
    Tick();playback.Enforce();Check(replica.PoseApplies==2&&observations.Queries==2,"pause");
    Tick();playback.Apply(stored,stored,0,replayTime:.025,observations:observations);Check(replica.Root.transform.position.x==.25f&&observations.Queries==3,"reverse");
});
Test("light metadata can update between root observations without inheriting a previous or future motion packet",()=>
{
    using var playback=new ItemPlayback(workBudget:new(()=>0));
    var a=new ItemFrame{SampleTimeKnown=true,SampleTime=0,Pose=new(){Position=new float[]{0,0,0}}};
    var b=new ItemFrame{SampleTimeKnown=true,SampleTime=.2,Pose=new(){Position=new float[]{2,0,0}}};
    var observations=new ItemObservationTimeline{Left=a,Right=b};var stored=new[]{a};
    playback.Apply(stored,stored,0,replayTime:.05,observations:observations);var lights=NativeLightAppearance.Playback.Created.Single();
    Check(ReferenceEquals(lights.Pass.Applied,a.Lights)&&!ReferenceEquals(lights.Pass.Applied,b.Lights),"future metadata leaked");
    var current=new ItemFrame{SampleTimeKnown=true,SampleTime=a.SampleTime,Pose=a.Pose,Visuals=a.Visuals,Lights=new NativeLightFrame[]{new()}};
    Tick();stored=new[]{current};playback.Apply(stored,stored,0,replayTime:.1,observations:observations);
    Check(ReferenceEquals(lights.Pass.Applied,current.Lights)&&current.SampleTime==0&&VisualReplica.Created.Single().Root.transform.position.x==1,"independent light clock");
});
Test("current appearance remains independent of sparse motion and native worm animation advances on the replay clock",()=>
{
    ItemDatabase.Prefab!.itemID=184;using var playback=new ItemPlayback(workBudget:new(()=>0));
    var frame=new ItemFrame{ItemId=184,IdleAnimation=new()};var frames=new[]{frame};
    playback.Apply(frames,frames,0,replayTime:8);var worm=EarlyWormAnimationRuntime.Created.Single();
    Check(FrogIdleAnimationRuntime.Created.Count==0&&worm.Times.SequenceEqual(new[]{8d}));
    int appearances=NativeVisualAppearance.Playback.Created.Single().Pass.Calls,poses=VisualReplica.Created.Single().PoseApplies;
    Tick();playback.Apply(frames,frames,0,replayTime:9);
    Check(worm.Times.SequenceEqual(new[]{8d,9d})&&NativeVisualAppearance.Playback.Created.Single().Pass.Calls==appearances&&VisualReplica.Created.Single().PoseApplies==poses);
    Tick();Absent(playback);Tick();Apply(playback,new ItemFrame{Key="replacement worm",ItemId=184,IdleAnimation=new()});
    Check(worm.Disposed&&EarlyWormAnimationRuntime.Created.Count==2&&VisualReplica.Created.Count==1);
});
Console.WriteLine($"Item playback contracts passed: {passed}");
