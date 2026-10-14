using PeakReplayLab;
using UnityEngine;

int passed = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); }
void Test(string name, Action test)
{
    TestScene.Reset(); Time.frameCount = 0; VisualReplica.Created.Clear();
    NativeVisualAppearance.Playback.Created.Clear(); NativeLightAppearance.Playback.Created.Clear();
    NativeVisualAppearance.Playback.Defer = NativeLightAppearance.Playback.Defer = false;
    NativeVisualAppearance.Playback.Calls = 0;
    CreatureIdleAnimationRuntime.Reset(); SpiderIdleLinePlayback.Reset();
    test(); passed++; Console.WriteLine("PASS " + name);
}
void Exhaust(PresentationWorkBudget budget)
{
    budget.BeginFrame(++Time.frameCount);
    for (int i = 0; i < PresentationWorkBudget.MaximumSteps; i++) Check(budget.Step(), "step limit changed");
    Check(!budget.Step(), "shared step budget must remain bounded");
}

foreach (string kind in new[] { "Spawned", "Interaction", "Creature" })
{
    Test(kind + ": sequential budget misses retain the committed static object", () =>
    {
        using var h = new Harness(kind); var original = new Sample(); h.Apply(original);
        var replica = VisualReplica.Created.Single(); Check(replica.Root.activeSelf, "initial object was never shown");
        for (int i = 0; i < 10; i++)
        {
            var changed = new Sample(i + 1); Exhaust(h.Budget); h.Apply(changed);
            Check(replica.Root.activeSelf && replica.Root.Hides == 0, "budget miss hid the completed object");
            Check(h.Visible == 1, "retained object not counted as visible");
            Time.frameCount++; h.Enforce(); Check(ReferenceEquals(replica.LastPose, changed.Pose), "paused completion lost latest pose");
        }
    });
    Test(kind + ": first reconstruction stays hidden until appearance is ready", () =>
    {
        using var h = new Harness(kind); NativeVisualAppearance.Playback.Defer = true; h.Apply(new Sample());
        var replica = VisualReplica.Created.Single(); Check(!replica.Root.activeSelf, "incomplete initial appearance shown");
        NativeVisualAppearance.Playback.Defer = false; Time.frameCount++; h.Enforce();
        Check(replica.Root.activeSelf, "paused initial request did not complete");
    });
    Test(kind + ": paused construction resumes after the shared build limit is reached", () =>
    {
        using var h = new Harness(kind); h.Budget.BeginFrame(Time.frameCount);
        for (int i = 0; i < PresentationWorkBudget.MaximumBuildSteps; i++) Check(h.Budget.Build(), "build limit changed");
        var target = new Sample(); h.Apply(target, seek: true); var replica = VisualReplica.Created.Single();
        Check(!replica.Root.activeSelf, "half-built visual became visible");
        Time.frameCount++; h.Enforce();
        Check(replica.Root.activeSelf && ReferenceEquals(replica.LastPose, target.Pose), "paused construction failed to continue");
    });
    Test(kind + ": seek replaces an unfinished appearance and stays hidden while deferred", () =>
    {
        using var h = new Harness(kind); h.Apply(new Sample()); var replica = VisualReplica.Created.Single();
        NativeVisualAppearance.Playback.Defer = true; Time.frameCount++; h.Apply(new Sample(1));
        var destination = new Sample(2); Time.frameCount++; h.Apply(destination, seek: true);
        Check(!replica.Root.activeSelf, "seek exposed previous position/state");
        Time.frameCount++; h.Enforce(); Check(!replica.Root.activeSelf, "deferred seek became visible");
        NativeVisualAppearance.Playback.Defer = false; Time.frameCount++; h.Enforce();
        Check(replica.Root.activeSelf && ReferenceEquals(replica.LastPose, destination.Pose), "seek completion used old pose");
        Check(ReferenceEquals(NativeVisualAppearance.Playback.Created.Last().Applied, destination.Visuals), "seek completed obsolete appearance");
    });
    Test(kind + ": inactive snapshots hide immediately even with no presentation budget", () =>
    {
        using var h = new Harness(kind); h.Apply(new Sample()); var replica = VisualReplica.Created.Single();
        Exhaust(h.Budget); h.Apply(new Sample { Pose = new() { Active = false } });
        Check(!replica.Root.activeSelf && h.Visible == 0, "inactive object retained stale visibility");
    });
    Test(kind + ": same-key sleeping object cancels its previous unfinished pass", () =>
    {
        using var h = new Harness(kind); h.Apply(new Sample()); var replica = VisualReplica.Created.Single();
        NativeVisualAppearance.Playback.Defer = true; Time.frameCount++; h.Apply(new Sample(1));
        Time.frameCount++; h.ApplyNone(); Check(!replica.Root.activeSelf, "removed object remained visible");
        var returned = new Sample(2); Time.frameCount++; h.Apply(returned); Check(!replica.Root.activeSelf, "pooled object exposed obsolete appearance");
        NativeVisualAppearance.Playback.Defer = false; Time.frameCount++; h.Enforce();
        Check(replica.Root.activeSelf && ReferenceEquals(NativeVisualAppearance.Playback.Created.Last().Applied, returned.Visuals), "sleeping object resumed obsolete work");
        Check(VisualReplica.Created.Count == 1, "same-key pool failed to reuse completed replica");
    });
    Test(kind + ": different-key reintroduction never exposes a pooled object's old state", () =>
    {
        using var h = new Harness(kind); h.Apply(new Sample());
        Time.frameCount++; h.ApplyNone();
        var target = new Sample(2) { Key = "replacement" }; NativeVisualAppearance.Playback.Defer = true;
        Time.frameCount++; h.Apply(target);
        Check(VisualReplica.Created.All(r => !r.Root.activeSelf), "replacement exposed its predecessor");
        NativeVisualAppearance.Playback.Defer = false; Time.frameCount++; h.Enforce();
        var shown = VisualReplica.Created.Single(r => r.Root.activeSelf);
        Check(ReferenceEquals(shown.LastPose, target.Pose), "replacement used predecessor pose");
        Check(VisualReplica.Created.Count == (kind == "Interaction" ? 2 : 1), "template pooling unexpectedly changed");
    });
    Test(kind + ": completion of an older pass keeps the latest paused target pending", () =>
    {
        using var h = new Harness(kind); h.Apply(new Sample());
        NativeVisualAppearance.Playback.Defer = true; Time.frameCount++; var older = new Sample(1); h.Apply(older);
        var latest = new Sample(2); NativeVisualAppearance.Playback.Defer = false; Time.frameCount++; h.Apply(latest);
        var appearance = NativeVisualAppearance.Playback.Created.Last();
        Check(ReferenceEquals(appearance.Applied, older.Visuals), "stub failed to model finishing an older incremental pass");
        Time.frameCount++; h.Enforce();
        Check(ReferenceEquals(appearance.Applied, latest.Visuals), "latest target lost after finishing older pass");
        int completeCalls = NativeVisualAppearance.Playback.Calls; Time.frameCount++; h.Enforce();
        Check(NativeVisualAppearance.Playback.Calls == completeCalls, "completed paused state keeps doing presentation work");
    });
    Test(kind + ": continuously changing samples do not starve first presentation", () =>
    {
        using var h = new Harness(kind); NativeVisualAppearance.Playback.Defer = true; h.Apply(new Sample());
        var replica = VisualReplica.Created.Single();
        for (int i = 1; i <= 8; i++)
        {
            NativeVisualAppearance.Playback.Defer = i % 2 == 1; Time.frameCount++; h.Apply(new Sample(i));
            if (i >= 2) Check(replica.Root.activeSelf, "changing snapshots starved or hid presentation");
        }
        Check(replica.PoseApplies >= 4 && replica.Root.Hides == 0, "completed passes were not committed");
    });
    Test(kind + ": disposal restores native renderers and cancels pending replay work", () =>
    {
        var h = new Harness(kind); h.Apply(new Sample()); var replica = VisualReplica.Created.Single();
        var native = TestScene.Native.GetComponent<MeshRenderer>()!; Check(native.forceRenderingOff, "native body not claimed");
        Exhaust(h.Budget); h.Apply(new Sample(2), seek: true); h.Dispose(); Time.frameCount++; h.Enforce();
        Check(!replica.Root.activeSelf && !native.forceRenderingOff, "disposed track leaked visibility or resumed");
    });
    if (kind == "Creature") continue;
    Test(kind + ": deferred lights retain the committed root and resume while paused", () =>
    {
        using var h = new Harness(kind); h.Apply(new Sample()); var replica = VisualReplica.Created.Single();
        var changed = new Sample(1); NativeLightAppearance.Playback.Defer = true; Time.frameCount++; h.Apply(changed);
        Check(replica.Root.activeSelf && replica.Root.Hides == 0, "lighting budget miss hid completed root");
        NativeLightAppearance.Playback.Defer = false; Time.frameCount++; h.Enforce();
        Check(ReferenceEquals(NativeLightAppearance.Playback.Created.Last().Applied, changed.Lights), "lights failed to resume");
    });
    Test(kind + ": seek restart reaches lights after appearance took multiple frames", () =>
    {
        using var h = new Harness(kind); h.Apply(new Sample()); var replica = VisualReplica.Created.Single();
        NativeLightAppearance.Playback.Defer = true; Time.frameCount++; h.Apply(new Sample(1));
        var destination = new Sample(2); NativeVisualAppearance.Playback.Defer = true;
        Time.frameCount++; h.Apply(destination, seek: true);
        Time.frameCount++; h.Enforce(); Check(!replica.Root.activeSelf, "seek shown before appearance readiness");
        NativeVisualAppearance.Playback.Defer = NativeLightAppearance.Playback.Defer = false; Time.frameCount++; h.Enforce();
        Check(replica.Root.activeSelf && ReferenceEquals(NativeLightAppearance.Playback.Created.Last().Applied, destination.Lights), "deferred seek forgot to cancel old lights");
    });
    Test(kind + ": completing obsolete lights schedules the latest paused lights", () =>
    {
        using var h = new Harness(kind); h.Apply(new Sample());
        var older = new Sample(1); NativeLightAppearance.Playback.Defer = true; Time.frameCount++; h.Apply(older);
        var latest = new Sample(2); NativeLightAppearance.Playback.Defer = false; Time.frameCount++; h.Apply(latest);
        Time.frameCount++; h.Enforce();
        Check(ReferenceEquals(NativeLightAppearance.Playback.Created.Last().Applied, latest.Lights), "latest lights lost after older completion");
    });
}
Test("Creature: actual observation bracket and mix replace global-frame mix for both pose and native silk", () =>
{
    var nativeLine = TestScene.Native.AddComponent<LineRenderer>();
    TestScene.Native.AddComponent<Spider>().line = nativeLine;
    CreatureReplayFrame Frame(int revision, float x) => new()
    {
        Key = "clock-spider", Kind = "spider", SampleTimeKnown = true,
        Pose = new ObjectPose { Revision = revision },
        Line = new CreatureReplayLine { Enabled = true, WorldSpace = true, Width = x + 1,
            Points = new[] { x, 0f, 0f, x + 1, 0f, 0f } },
    };
    var globalLeft = Frame(100, 100); var globalRight = Frame(200, 200);
    var observedLeft = Frame(10, 0); var observedRight = Frame(20, 10);
    var observations = new CreatureObservationTimeline { Key = globalLeft.Key, Left = observedLeft, Right = observedRight, Mix = .25f };
    var budget = new PresentationWorkBudget(() => 0);
    using var playback = new CreatureReplayPlayback(workBudget: budget);
    playback.Apply(new[] { globalLeft }, new[] { globalRight }, .9f, 4.125, false, observations);
    var replica = VisualReplica.Created.Single();
    Check(observations.Calls == 1 && observations.LastRequestedTime == 4.125, "production Apply did not request the observation clock at display time");
    Check(ReferenceEquals(replica.LastLeftPose, observedLeft.Pose) && ReferenceEquals(replica.LastRightPose, observedRight.Pose) &&
        replica.LastMix == .25f, "global-frame bracket or mix leaked into observed native bones");
    var silk = replica.Root.GetComponent<LineRenderer>()!;
    Check(silk.enabled && silk.positionCount == 2 && Math.Abs(silk.Positions[0].x - 2.5f) < .00001f &&
        Math.Abs(silk.widthMultiplier - 3.5f) < .00001f, "native silk used global frame mix instead of the observed .25 bracket");
});

CreatureReplayFrame IdleSpider(string key = "idle-spider", int revision = 7)
{
    var nativeLine = TestScene.Native.GetComponent<LineRenderer>() ?? TestScene.Native.AddComponent<LineRenderer>();
    nativeLine.useWorldSpace = true; nativeLine.positionCount = 40;
    var spider = TestScene.Native.GetComponent<Spider>() ?? TestScene.Native.AddComponent<Spider>(); spider.line = nativeLine;
    return new CreatureReplayFrame
    {
        Key=key, Kind="spider", Resource="spider-normal", SampleTimeKnown=true, ClockMode=CreatureReplayRules.IdleClock, State=0,
        Pose=new ObjectPose { Revision=revision },
        Line=new CreatureReplayLine { Enabled=true, WorldSpace=true, Width=.08f, Points=new float[120] },
        IdleAnimation=new CreatureIdleAnimation { Clip=CreatureIdleAnimationRules.SpiderIdle, AnchorTime=10, ClipTime=.25, Speed=.33f, RopePhase=.4f },
    };
}
void Present(CreatureReplayPlayback playback, CreatureReplayFrame frame, double time=10, bool seek=false)
{ Time.frameCount++; playback.Apply(new[]{frame},new[]{frame},0,time,seek); }

Test("Creature idle: paused checkpoint cannot overwrite cached native bones",()=>
{
    var frame=IdleSpider(); using var playback=new CreatureReplayPlayback(workBudget:new(()=>0));
    Present(playback,frame,10.5);
    var visual=VisualReplica.Created.Single();var runtime=CreatureIdleAnimationRuntime.Created.Single();
    double shown=visual.BoneMarker;int samples=runtime.Samples;
    Present(playback,frame,10.5);
    Check(visual.BoneMarker==shown && shown>1000,"held recorded checkpoint overwrote native idle bones while paused");
    Check(runtime.Samples==samples,"paused native clip was sampled again unnecessarily");
    Check(runtime.LastReplayTime==10.5 && playback.VisibleCount==1,"absolute replay time or visible idle body lost");
});
Test("Creature idle: seek to the same phase invalidates overwritten checkpoint bones",()=>
{
    var frame=IdleSpider(); using var playback=new CreatureReplayPlayback(workBudget:new(()=>0));
    Present(playback,frame,10.5);var runtime=CreatureIdleAnimationRuntime.Created.Single();int samples=runtime.Samples;
    Present(playback,frame,10.5,true);
    Check(runtime.Samples==samples+1 && VisualReplica.Created.Single().BoneMarker>1000,"same-phase seek retained recorded frozen bones");
});
Test("Creature idle: action resumes recorded pose and returning idle samples same phase again",()=>
{
    var frame=IdleSpider();using var playback=new CreatureReplayPlayback(workBudget:new(()=>0));Present(playback,frame,10.5);
    var visual=VisualReplica.Created.Single();var runtime=CreatureIdleAnimationRuntime.Created.Single();int samples=runtime.Samples;
    var action=IdleSpider(revision:99);action.ClockMode=CreatureReplayRules.ActiveClock;action.State=2;action.IdleAnimation=null;
    Present(playback,action,10.5);
    Check(visual.BoneMarker==99 && ReferenceEquals(visual.LastPose,action.Pose),"idle sampler overwrote recorded grab/action pose");
    Present(playback,frame,10.5);
    Check(visual.BoneMarker>1000 && runtime.Samples==samples+1,"action-to-idle cached clip failed to restore bones");
});
Test("Creature idle: 40 native silk points written once after native clip, with recorded metadata",()=>
{
    var frame=IdleSpider();using var playback=new CreatureReplayPlayback(workBudget:new(()=>0));Present(playback,frame,10.5);
    var visual=VisualReplica.Created.Single();var line=visual.Root.GetComponent<LineRenderer>()!;
    var idleLine=SpiderIdleLinePlayback.Created.Single();
    Check(line.PositionWrites==40,"native idle silk performed duplicate recorded and generated 40-point passes");
    Check(idleLine.Accepted==1 && idleLine.LastPhase==.4f,"recorded sinceGrab phase not used");
    Check(line.Positions[39].x==(float)visual.BoneMarker && line.enabled && line.widthMultiplier==.08f,"silk preceded clip sampling or lost visibility/width");
});
Test("Creature idle: pooled same-key clip invalidates before paused reintroduction",()=>
{
    var frame=IdleSpider();using var playback=new CreatureReplayPlayback(workBudget:new(()=>0));Present(playback,frame,10.5);
    var runtime=CreatureIdleAnimationRuntime.Created.Single();int samples=runtime.Samples;
    Time.frameCount++;playback.Apply(Array.Empty<CreatureReplayFrame>(),Array.Empty<CreatureReplayFrame>(),0,10.5,false);
    Check(!VisualReplica.Created.Single().Root.activeSelf,"pooled idle visual stayed visible");
    Present(playback,frame,10.5);
    Check(VisualReplica.Created.Count==1 && VisualReplica.Created.Single().BoneMarker>1000,"same-key reintroduction did not reuse or restore native idle bones");
    Check(CreatureIdleAnimationRuntime.Created.Last().Samples>samples || runtime.Samples>samples || CreatureIdleAnimationRuntime.Created.Count>1,"same-phase pooled return did not invalidate idle clip");
});
Test("Creature idle: different-key pool reuse cannot retain predecessor animation phase",()=>
{
    var first=IdleSpider();using var playback=new CreatureReplayPlayback(workBudget:new(()=>0));Present(playback,first,10.5);
    var old=CreatureIdleAnimationRuntime.Created.Single();
    Time.frameCount++;playback.Apply(Array.Empty<CreatureReplayFrame>(),Array.Empty<CreatureReplayFrame>(),0,10.5,false);
    var replacement=IdleSpider("replacement",88);replacement.IdleAnimation!.ClipTime=.75;
    Present(playback,replacement,10.5);
    var visual=VisualReplica.Created.Single();
    Check(Math.Abs(visual.BoneMarker-(1000+CreatureIdleAnimationRules.SampleTime(replacement.IdleAnimation,10.5)))<.000001,"pool reuse leaked predecessor phase or checkpoint");
    Check(old.Disposed || old.Invalidations>=2,"different-key reuse retained cached predecessor phase");
    playback.Dispose();Check(CreatureIdleAnimationRuntime.Created.All(r=>r.Disposed),"native animation runtime leaked after playback disposal");
});
Test("Creature idle: initial native runtime waits for shared build budget before showing",()=>
{
    var frame=IdleSpider();var budget=new PresentationWorkBudget(()=>0);Time.frameCount++;
    budget.BeginFrame(Time.frameCount);
    for(int i=0;i<PresentationWorkBudget.MaximumBuildSteps-4;i++)Check(budget.Build(),"build budget unexpectedly changed");
    using var playback=new CreatureReplayPlayback(workBudget:budget);
    playback.Apply(new[]{frame},new[]{frame},0,10,false);
    Check(VisualReplica.Created.Count==1 && !VisualReplica.Created.Single().Root.activeSelf && playback.PendingCount>0,"partly-built native idle became visible");
    Check(CreatureIdleAnimationRuntime.Created.Count==0,"runtime created outside the shared build budget");
    Time.frameCount++;playback.Enforce();
    Check(playback.VisibleCount==1 && VisualReplica.Created.Single().BoneMarker>1000,"paused native runtime construction failed to resume");
});
Test("Creature idle: unsupported native asset reports missing instead of silently frozen body",()=>
{
    var frame=IdleSpider();CreatureIdleAnimationRuntime.Unsupported=true;
    using var playback=new CreatureReplayPlayback(workBudget:new(()=>0));Present(playback,frame);
    Check(playback.MissingCount==1 && playback.VisibleCount==0 && VisualReplica.Created.All(v=>!v.Root.activeSelf),"unsupported idle asset shown as frozen checkpoint");
});
Test("Creature idle: old and active frames do not create a native idle runtime",()=>
{
    var old=IdleSpider();old.SampleTimeKnown=false;old.ClockMode=CreatureReplayRules.ActiveClock;old.IdleAnimation=null;
    using var playback=new CreatureReplayPlayback(workBudget:new(()=>0));Present(playback,old);
    Check(CreatureIdleAnimationRuntime.Attempts==0 && VisualReplica.Created.Single().BoneMarker==old.Pose.Revision,"legacy geometry unexpectedly used native animation");
    var active=IdleSpider(revision:98);active.ClockMode=CreatureReplayRules.ActiveClock;active.State=1;active.IdleAnimation=null;
    Present(playback,active);
    Check(CreatureIdleAnimationRuntime.Attempts==0 && VisualReplica.Created.Single().BoneMarker==98,"active geometry unexpectedly used native animation");
});
foreach(string kind in new[]{"ghost-ball","player-ghost"})
{
    Test("Creature native body: old schema does not hide "+kind,()=>
    {
        TestScene.Kind=kind;using var playback=new CreatureReplayPlayback(includeExtended:true);
        Check(!TestScene.Native.GetComponent<MeshRenderer>()!.forceRenderingOff,"new independent body hidden while playing old recording");
    });
    Test("Creature native body: schema21 claims and restores "+kind,()=>
    {
        TestScene.Kind=kind;using(var playback=new CreatureReplayPlayback(includeExtended:true,includeNativeBodies:true))
            Check(TestScene.Native.GetComponent<MeshRenderer>()!.forceRenderingOff,"schema21 native body not claimed");
        Check(!TestScene.Native.GetComponent<MeshRenderer>()!.forceRenderingOff,"native body not restored on disposal");
    });
}
RavenPlaybackTests.Run(Test);
Console.WriteLine($"Static playback contracts passed: {passed}");

sealed class Sample
{
    public string Key = "object";
    public ObjectPose Pose;
    public NativeRendererFrame[] Visuals;
    public NativeLightFrame[] Lights;
    public Sample(int revision = 0)
    { Pose = new() { Revision = revision }; Visuals = new[] { new NativeRendererFrame { Revision = revision } }; Lights = new[] { new NativeLightFrame { Revision = revision } }; }
}
sealed class Harness : IDisposable
{
    public readonly PresentationWorkBudget Budget = new(() => 0);
    private readonly SpawnedReplayPlayback? spawned;
    private readonly InteractionReplayPlayback? interaction;
    private readonly CreatureReplayPlayback? creature;
    public int Visible => spawned?.VisibleCount ?? interaction?.VisibleCount ?? creature!.VisibleCount;
    public Harness(string kind)
    {
        if (kind == "Spawned") spawned = new(workBudget: Budget);
        else if (kind == "Interaction") interaction = new(workBudget: Budget);
        else creature = new(workBudget: Budget);
    }
    public void Apply(Sample frame, bool seek = false) => ApplyMany(new[] { frame }, seek);
    public void ApplyNone() => ApplyMany(Array.Empty<Sample>(), false);
    private void ApplyMany(Sample[] frames, bool seek)
    {
        if (spawned != null)
        {
            var data = frames.Select(f => new SpawnedReplayFrame { Key = f.Key, Pose = f.Pose, Visuals = f.Visuals, Lights = f.Lights }).ToArray();
            spawned.Apply(data, data, 0, Time.frameCount / 60d, seek);
        }
        if (interaction != null)
        {
            var data = frames.Select(f => new InteractionReplayFrame { Key = f.Key, Pose = f.Pose, Visuals = f.Visuals, Lights = f.Lights }).ToArray();
            interaction.Apply(data, data, 0, Time.frameCount / 60d, seek);
        }
        if (creature != null)
        {
            var data = frames.Select(f => new CreatureReplayFrame { Key = f.Key, Pose = f.Pose, Visuals = f.Visuals }).ToArray();
            creature.Apply(data, data, 0, Time.frameCount / 60d, seek);
        }
    }
    public void Enforce() { spawned?.Enforce(); interaction?.Enforce(); creature?.Enforce(); }
    public void Dispose() { spawned?.Dispose(); interaction?.Dispose(); creature?.Dispose(); }
}
