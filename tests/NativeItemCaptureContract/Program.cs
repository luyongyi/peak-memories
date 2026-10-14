using PeakReplayLab;
using UnityEngine;
int passed=0;
void Check(bool value) { if(!value)throw new Exception("Item capture bootstrap contract failed."); }
void Test(string name,Action test)
{
    Item.ALL_ITEMS.Clear(); ItemCapture.EventHooksHealthy=true;VisualReplica.Reads=VisualReplica.Releases=0;VisualReplica.Clock=VisualReplica.ReadCost=0;NativeLightAppearance.Reads=NativeCaptureAnimationActivity.TotalReads=0;Time.timeAsDouble=10;
    FrogCaptureActivity.Enabled=FrogCaptureActivity.ForceRootWake=false;FrogCaptureActivity.Last=null;
    EarlyWormAnimationCapture.Enabled=false;EarlyWormAnimationCapture.Last=null;NativeVisualAppearance.Reads=0;
    try { test();passed++;Console.WriteLine("PASS "+name); }
    catch(Exception error) {Console.Error.WriteLine("FAIL "+name+": "+error);Environment.Exit(1);}
}
Item Born(bool active=true) {var item=new Item();item.gameObject.activeInHierarchy=active;Item.ALL_ITEMS.Add(item);return item;}
Test("DiscoverBaseline is idempotent and cannot claim a frame before its native read",()=>
{
    Born();using var capture=new ItemCapture();Check(!capture.InitialBaselineReady);capture.DiscoverBaseline();int reads=Item.ALL_ITEMS[0].gameObject.ComponentReads;
    capture.DiscoverBaseline();Check(!capture.InitialBaselineReady&&VisualReplica.Reads==0&&Item.ALL_ITEMS[0].gameObject.ComponentReads==reads);
    Check(capture.Capture(Array.Empty<ActorFrame>()).Length==1&&capture.InitialBaselineReady);
});
Test("exhausted shared budget defers first geometry without sleeping or losing dirty state",()=>
{
    var item=Born();using var capture=new ItemCapture();capture.DiscoverBaseline();
    using(CaptureBootstrapWorkBudget.Begin(2,()=>VisualReplica.Clock,1000)) {VisualReplica.Clock=2;Check(capture.Capture(Array.Empty<ActorFrame>()).Length==0&&!capture.InitialBaselineReady&&VisualReplica.Reads==0);}
    var frames=capture.Capture(Array.Empty<ActorFrame>());Check(frames.Length==1&&capture.InitialBaselineReady&&VisualReplica.Reads==1);
});
Test("one global budget continues missing baselines on later rendered frames",()=>
{
    Born();Born();Born();using var capture=new ItemCapture();VisualReplica.ReadCost=2;
    using(CaptureBootstrapWorkBudget.Begin(2,()=>VisualReplica.Clock,1000)) Check(capture.Capture(Array.Empty<ActorFrame>()).Length==1&&!capture.InitialBaselineReady);
    using(CaptureBootstrapWorkBudget.Begin(2,()=>VisualReplica.Clock,1000)) Check(capture.Capture(Array.Empty<ActorFrame>()).Length==2&&!capture.InitialBaselineReady);
    using(CaptureBootstrapWorkBudget.Begin(2,()=>VisualReplica.Clock,1000)) Check(capture.Capture(Array.Empty<ActorFrame>()).Length==3&&capture.InitialBaselineReady);
    Check(VisualReplica.Reads==3);
});
Test("native first-read failure blocks Ready and retries without an invented frame",()=>
{
    var item=Born();item.transform.Failures=1;using var capture=new ItemCapture();Check(capture.Capture(Array.Empty<ActorFrame>()).Length==0&&!capture.InitialBaselineReady);
    Time.timeAsDouble+=1d/30;Check(capture.Capture(Array.Empty<ActorFrame>()).Length==1&&capture.InitialBaselineReady&&VisualReplica.Reads==2);
});
Test("observed inactive sources finish preparation without any geometry",()=>
{
    Born(false);using var capture=new ItemCapture();using(CaptureBootstrapWorkBudget.Begin(2,()=>VisualReplica.Clock,1000)) {VisualReplica.Clock=2;Check(capture.Capture(Array.Empty<ActorFrame>()).Length==0&&capture.InitialBaselineReady&&VisualReplica.Reads==0);}
});
Test("a reactivated missing baseline becomes pending again and respects budget",()=>
{
    var item=Born(false);using var capture=new ItemCapture();capture.Capture(Array.Empty<ActorFrame>());item.gameObject.activeInHierarchy=true;ItemCapture.RegistryChanged(item);
    using(CaptureBootstrapWorkBudget.Begin(2,()=>VisualReplica.Clock,1000)) {VisualReplica.Clock=2;capture.Capture(Array.Empty<ActorFrame>());Check(!capture.InitialBaselineReady&&VisualReplica.Reads==0);}
    Check(capture.Capture(Array.Empty<ActorFrame>()).Length==1&&capture.InitialBaselineReady);
});
Test("destroyed sources release pending baseline and capture bindings",()=>
{
    var item=Born();using var capture=new ItemCapture();capture.DiscoverBaseline();item.Destroyed=true;ItemCapture.Destroying(item);
    Check(capture.Capture(Array.Empty<ActorFrame>()).Length==0&&capture.InitialBaselineReady&&VisualReplica.Reads==0&&VisualReplica.Releases==1);
});
Test("foreign loaded scenes and prefab scenes are rejected before component directories",()=>
{
    var foreign=Born();foreign.gameObject.scene=new(){handle=2,isLoaded=true,Valid=true};var prefab=Born();prefab.gameObject.scene=default;
    using var capture=new ItemCapture();Check(capture.Capture(Array.Empty<ActorFrame>()).Length==0&&capture.InitialBaselineReady&&foreign.gameObject.ComponentReads==0&&prefab.gameObject.ComponentReads==0);
});
Test("registry notifications cannot upgrade a cold item to urgent",()=>
{
    var item=Born();using var capture=new ItemCapture();ItemCapture.RegistryChanged(item);ItemCapture.RegistryChanged(item);
    using(CaptureBootstrapWorkBudget.Begin(2,()=>VisualReplica.Clock,1000)) {VisualReplica.Clock=2;Check(capture.Capture(Array.Empty<ActorFrame>()).Length==0&&!capture.InitialBaselineReady&&VisualReplica.Reads==0);}
});
Test("a real action bypasses the cold budget once and survives registry coalescing",()=>
{
    var item=Born();using var capture=new ItemCapture();ItemCapture.MarkDirty(item);ItemCapture.RegistryChanged(item);
    using(CaptureBootstrapWorkBudget.Begin(2,()=>VisualReplica.Clock,1000)) {VisualReplica.Clock=2;Check(capture.Capture(Array.Empty<ActorFrame>()).Length==1&&capture.InitialBaselineReady&&VisualReplica.Reads==1);}
});
Test("failed urgent initial read consumes its bypass and uses the next shared budget",()=>
{
    var item=Born();item.transform.Failures=1;using var capture=new ItemCapture();ItemCapture.MarkDirty(item);
    using(CaptureBootstrapWorkBudget.Begin(2,()=>VisualReplica.Clock,1000)) {VisualReplica.Clock=2;capture.Capture(Array.Empty<ActorFrame>());Check(!capture.InitialBaselineReady&&VisualReplica.Reads==1);capture.Capture(Array.Empty<ActorFrame>());Check(VisualReplica.Reads==1);}
    Check(capture.Capture(Array.Empty<ActorFrame>()).Length==1&&capture.InitialBaselineReady);
});
Test("directory-read failure cannot claim Ready and retries within the shared budget",()=>
{
    var item=Born();item.gameObject.DirectoryFailures=1;using var capture=new ItemCapture();capture.DiscoverBaseline();Check(!capture.InitialBaselineReady&&VisualReplica.Reads==0);
    using(CaptureBootstrapWorkBudget.Begin(2,()=>VisualReplica.Clock,1000)) {VisualReplica.Clock=2;capture.Capture(Array.Empty<ActorFrame>());Check(!capture.InitialBaselineReady&&VisualReplica.Reads==0);}
    Check(capture.Capture(Array.Empty<ActorFrame>()).Length==1&&capture.InitialBaselineReady&&VisualReplica.Reads==1);
});
Test("destroyed registration retry finishes preparation without geometry",()=>
{
    var item=Born();item.gameObject.DirectoryFailures=1;using var capture=new ItemCapture();capture.DiscoverBaseline();item.Destroyed=true;ItemCapture.Destroying(item);
    using(CaptureBootstrapWorkBudget.Begin(2,()=>VisualReplica.Clock,1000)) {VisualReplica.Clock=2;capture.Capture(Array.Empty<ActorFrame>());Check(capture.InitialBaselineReady&&VisualReplica.Reads==0);}
});
Test("InitialOnly avoids dynamic initialized sources starving missing baselines",()=>
{
    for(int i=0;i<3;i++)Born().rig!.isKinematic=false;using var capture=new ItemCapture();VisualReplica.ReadCost=2;
    for(int expected=1;expected<=3;expected++)
        using(CaptureBootstrapWorkBudget.Begin(2,()=>VisualReplica.Clock,1000,initialOnly:true)) Check(capture.Capture(Array.Empty<ActorFrame>()).Length==expected);
    Check(capture.InitialBaselineReady&&VisualReplica.Reads==3);
});
Test("InitialOnly pauses initialized Animator and light polls while preserving real dirty actions",()=>
{
    var item=Born();item.gameObject.Attach(new Animator());item.gameObject.Attach(new Light());using var capture=new ItemCapture();capture.Capture(Array.Empty<ActorFrame>());
    int reads=VisualReplica.Reads,animation=NativeCaptureAnimationActivity.TotalReads,lights=NativeLightAppearance.Reads;Time.timeAsDouble+=1d/30;
    using(CaptureBootstrapWorkBudget.Begin(2,()=>VisualReplica.Clock,1000,initialOnly:true)) {VisualReplica.Clock=2;capture.Capture(Array.Empty<ActorFrame>());Check(VisualReplica.Reads==reads&&NativeCaptureAnimationActivity.TotalReads==animation&&NativeLightAppearance.Reads==lights);}
    ItemCapture.MarkDirty(item);item.transform.position=new(3,0,0);
    using(CaptureBootstrapWorkBudget.Begin(2,()=>VisualReplica.Clock,1000,initialOnly:true)) {VisualReplica.Clock+=2;Check(capture.Capture(Array.Empty<ActorFrame>())[0].Pose.Position[0]==3&&VisualReplica.Reads>reads);}
});
Test("eligible five Hertz initialized ground motion is never blocked by cold bootstrap budget",()=>
{
    var item=Born();item.rig!.isKinematic=false;using var capture=new ItemCapture();capture.Capture(Array.Empty<ActorFrame>());int before=VisualReplica.Reads;item.transform.position=new(2,0,0);Time.timeAsDouble+=.2;
    using(CaptureBootstrapWorkBudget.Begin(2,()=>VisualReplica.Clock,1000)) {VisualReplica.Clock=2;var frames=capture.Capture(Array.Empty<ActorFrame>());Check(VisualReplica.Reads>before&&frames[0].Pose.Position[0]==2&&capture.InitialBaselineReady);}
});
Test("thirty scene ticks of moving ground physics produce five root observations with true source timestamps",()=>
{
    var item=Born();item.rig!.isKinematic=false;item.rig.linearVelocity=new(1,0,0);using var capture=new ItemCapture();
    capture.Capture(Array.Empty<ActorFrame>());long reads=capture.NativeReads;
    for(int i=1;i<=30;i++){Time.timeAsDouble=10+i/30d;item.transform.position=new(i/30f,0,0);capture.Capture(Array.Empty<ActorFrame>());}
    var frame=capture.Capture(Array.Empty<ActorFrame>())[0];
    Check(capture.NativeReads-reads==5&&capture.SparseSkips>=25&&frame.SampleTimeKnown&&Math.Abs(frame.SampleTime-11)<1e-8&&frame.Pose.Position[0]==1);
});
Test("real semantic events bypass five Hertz once and mark the cut without accelerating the next ordinary tick",()=>
{
    var item=Born();item.rig!.isKinematic=false;item.rig.linearVelocity=new(1,0,0);using var capture=new ItemCapture();
    capture.Capture(Array.Empty<ActorFrame>());long reads=capture.NativeReads;
    Time.timeAsDouble=10.03;item.transform.position=new(2,0,0);ItemCapture.MarkDirty(item);
    var frame=capture.Capture(Array.Empty<ActorFrame>())[0];Check(capture.NativeReads==reads+1&&frame.SampleDiscontinuity&&frame.SampleTime==10.03&&frame.Pose.Position[0]==2);
    Time.timeAsDouble=10.06;item.transform.position=new(3,0,0);capture.Capture(Array.Empty<ActorFrame>());Check(capture.NativeReads==reads+1);
});
Test("thirty continuous physics notifications cannot turn packet delivery into thirty semantic boundaries",()=>
{
    var item=Born();item.rig!.isKinematic=false;item.rig.linearVelocity=new(1,0,0);using var capture=new ItemCapture();
    capture.Capture(Array.Empty<ActorFrame>());long reads=capture.NativeReads;int boundaries=0;
    for(int i=1;i<=30;i++)
    {
        Time.timeAsDouble=10+i/30d;item.transform.position=new(i/30f,0,0);ItemCapture.MarkMotionObservation(item,true);
        long before=capture.NativeReads;var frame=capture.Capture(Array.Empty<ActorFrame>())[0];if(capture.NativeReads>before&&frame.SampleDiscontinuity)boundaries++;
    }
    Check(capture.NativeReads-reads==5&&boundaries==0&&capture.SparseSkips>=25);
});
Test("supported worm clock owns the child animation while its ground root remains independently five Hertz",()=>
{
    EarlyWormAnimationCapture.Enabled=true;var item=new MobItem{itemID=184};item.gameObject.Attach(new Animator());Item.ALL_ITEMS.Add(item);
    item.rig!.isKinematic=false;item.rig.linearVelocity=new(1,0,0);using var capture=new ItemCapture();capture.Capture(Array.Empty<ActorFrame>());
    long reads=capture.NativeReads,children=capture.ChildReads;int appearances=NativeVisualAppearance.Reads;
    for(int i=1;i<=30;i++){Time.timeAsDouble=10+i/30d;item.transform.position=new(i/30f,0,0);capture.Capture(Array.Empty<ActorFrame>());}
    var frame=capture.Capture(Array.Empty<ActorFrame>())[0];
    Check(frame.IdleAnimation!=null&&frame.Pose.Position[0]==1&&capture.NativeReads-reads==5&&capture.ChildReads==children);
    Check(NativeVisualAppearance.Reads==appearances&&NativeCaptureAnimationActivity.TotalReads==0&&capture.WormGeometrySkips==5);
});
Test("settled modeled worms retain one frame and observation while only five Hertz root safety checks continue",()=>
{
    EarlyWormAnimationCapture.Enabled=true;var item=new MobItem{itemID=184};item.gameObject.Attach(new Animator());Item.ALL_ITEMS.Add(item);
    item.rig!.isKinematic=false;using var capture=new ItemCapture();
    for(int i=0;i<=60;i++){Time.timeAsDouble=10+i/30d;capture.Capture(Array.Empty<ActorFrame>());}
    var held=capture.Capture(Array.Empty<ActorFrame>());Check(held[0].ClockMode==CreatureReplayRules.IdleClock&&held[0].IdleAnimation!=null);
    long native=capture.NativeReads,children=capture.ChildReads,checks=capture.WormRootChecks;int appearances=NativeVisualAppearance.Reads;
    for(int i=61;i<=360;i++){Time.timeAsDouble=10+i/30d;Check(ReferenceEquals(held,capture.Capture(Array.Empty<ActorFrame>())));}
    Check(capture.NativeReads==native&&capture.ChildReads==children&&NativeVisualAppearance.Reads==appearances);
    Check(capture.WormRootChecks-checks==50&&capture.WormIdleSkips>=50&&capture.AnimationProbes==0);
});
Test("a modeled worm moving after a long hold creates a fresh cut and keeps later five Hertz movement brackets",()=>
{
    EarlyWormAnimationCapture.Enabled=true;var item=new MobItem{itemID=184};item.gameObject.Attach(new Animator());Item.ALL_ITEMS.Add(item);
    item.rig!.isKinematic=false;using var capture=new ItemCapture();
    for(int i=0;i<=150;i++){Time.timeAsDouble=10+i/30d;capture.Capture(Array.Empty<ActorFrame>());}
    var held=capture.Capture(Array.Empty<ActorFrame>())[0];long reads=capture.NativeReads;
    Time.timeAsDouble=15.2;item.transform.position=new(.5f,0,0);item.rig.linearVelocity=new(1,0,0);
    var resumed=capture.Capture(Array.Empty<ActorFrame>())[0];
    Check(resumed.SampleTime==15.2&&resumed.SampleDiscontinuity&&resumed.ClockMode==CreatureReplayRules.ActiveClock&&resumed.Pose.Position[0]==.5f);
    Check(held.Pose.Position[0]==0&&held.SampleTime<14&&capture.NativeReads==reads+1);
    Time.timeAsDouble=15.4;item.transform.position=new(.7f,0,0);var next=capture.Capture(Array.Empty<ActorFrame>())[0];
    Check(next.SampleTime==15.4&&!next.SampleDiscontinuity&&capture.NativeReads==reads+2);
});
Test("modeled worm pickup throw cook and unsupported clock bypass quiet holding",()=>
{
    foreach(int action in Enumerable.Range(0,4))
    {
        EarlyWormAnimationCapture.Enabled=true;Item.ALL_ITEMS.Clear();var item=new MobItem{itemID=184};item.gameObject.Attach(new Animator());Item.ALL_ITEMS.Add(item);
        using var capture=new ItemCapture();for(int i=0;i<=60;i++){Time.timeAsDouble=10+i/30d;capture.Capture(Array.Empty<ActorFrame>());}
        long reads=capture.NativeReads;Time.timeAsDouble=12.03;
        if(action==0){item.itemState=ItemState.Held;item.trueHolderCharacter=new Character();}
        if(action==1){item.rig!.isKinematic=false;item.rig.linearVelocity=new(2,0,0);item.transform.position=new(1,0,0);}
        if(action==2)item.data=new(){data=new(){[DataEntryKey.CookedAmount]=new IntItemData{Value=1}}};
        if(action==3)EarlyWormAnimationCapture.Last!.Supported=false;
        if(action!=3)ItemCapture.MarkDirty(item);
        else Time.timeAsDouble=12.2;
        var resumed=capture.Capture(Array.Empty<ActorFrame>())[0];
        Check(capture.NativeReads>reads&&resumed.SampleDiscontinuity&&resumed.SampleTime==Time.timeAsDouble);
        if(action==0)Check(resumed.State==(int)ItemState.Held&&resumed.HolderId=="player");
        if(action==1)Check(resumed.Pose.Position[0]==1);
        if(action==2)Check(resumed.Cooked==1);
        if(action==3)Check(resumed.IdleAnimation==null&&resumed.ClockMode==CreatureReplayRules.ActiveClock);
    }
});
Test("stationary continuous wind hints retain the checkpoint and never renew a full-capture lease",()=>
{
    var item=Born();item.rig!.isKinematic=false;using var capture=new ItemCapture();capture.Capture(Array.Empty<ActorFrame>());
    for(int i=1;i<=90;i++){Time.timeAsDouble=10+i/30d;ItemCapture.MarkMotionObservation(item,true);capture.Capture(Array.Empty<ActorFrame>());}
    var held=capture.Capture(Array.Empty<ActorFrame>())[0];long reads=capture.NativeReads,children=capture.ChildReads,probes=capture.ContinuousRootProbes;int components=item.gameObject.ComponentReads;
    for(int i=91;i<=390;i++){Time.timeAsDouble=10+i/30d;ItemCapture.MarkMotionObservation(item,true);Check(ReferenceEquals(held,capture.Capture(Array.Empty<ActorFrame>())[0]));}
    Check(capture.NativeReads==reads&&capture.ChildReads==children&&item.gameObject.ComponentReads==components);
    Check(capture.ContinuousRootProbes-probes>=10&&capture.ContinuousRootProbes-probes<=21);
});
Test("a settled kinematic item still wakes for actual continuing water-zone movement at the bounded root probe",()=>
{
    var item=Born();using var capture=new ItemCapture();capture.Capture(Array.Empty<ActorFrame>());long reads=capture.NativeReads;
    for(int i=1;i<=24;i++){Time.timeAsDouble=10+i/30d;item.transform.position=new(i/30f,0,0);ItemCapture.MarkMotionObservation(item,true);capture.Capture(Array.Empty<ActorFrame>());}
    Check(capture.NativeReads>reads&&capture.ContinuousRootProbes>0&&capture.Capture(Array.Empty<ActorFrame>())[0].Pose.Position[0]>.5f);
    reads=capture.NativeReads;
    for(int i=25;i<=54;i++){Time.timeAsDouble=10+i/30d;item.transform.position=new(i/30f,0,0);ItemCapture.MarkMotionObservation(item,true);capture.Capture(Array.Empty<ActorFrame>());}
    Check(capture.NativeReads-reads>=4&&capture.NativeReads-reads<=6&&capture.Capture(Array.Empty<ActorFrame>())[0].Pose.Position[0]>1.5f);
});
Test("the first continuous hint of a sleeping session gets a prompt root bracket and later hints do not renew its deadline",()=>
{
    var item=Born();using var capture=new ItemCapture();capture.Capture(Array.Empty<ActorFrame>());long reads=capture.NativeReads;
    for(int i=1;i<=7;i++){Time.timeAsDouble=10+i/30d;item.transform.position=new(.01f,0,0);ItemCapture.MarkMotionObservation(item,true);capture.Capture(Array.Empty<ActorFrame>());}
    Check(capture.ContinuousRootProbes==1&&capture.NativeReads>reads&&capture.Capture(Array.Empty<ActorFrame>())[0].Pose.Position[0]==.01f);
});
Test("ground fuel metadata updates at five Hertz without geometry appearance reads or a new root observation",()=>
{
    var item=Born();var fuel=new FloatItemData{Value=100};var ui=new FloatItemData{Value=1};
    item.data=new(){data=new(){[DataEntryKey.Fuel]=fuel,[DataEntryKey.UseRemainingPercentage]=ui}};
    using var capture=new ItemCapture();var initial=capture.Capture(Array.Empty<ActorFrame>())[0];long reads=capture.NativeReads,children=capture.ChildReads;int appearance=NativeVisualAppearance.Reads;
    for(int i=1;i<=30;i++){Time.timeAsDouble=10+i/30d;fuel.Value=100-i;ui.Value=fuel.Value/100;ItemCapture.MarkMetadataObservation(item);capture.Capture(Array.Empty<ActorFrame>());}
    var latest=capture.Capture(Array.Empty<ActorFrame>())[0];
    Check(capture.MetadataReads==5&&latest.Fuel==75&&latest.SampleTime==initial.SampleTime&&ReferenceEquals(latest.Pose,initial.Pose));
    Check(capture.NativeReads==reads&&capture.ChildReads==children&&NativeVisualAppearance.Reads==appearance);
    Time.timeAsDouble=11.03;item.data.data![DataEntryKey.PowerEnabled]=new BoolItemData{Value=true};ItemCapture.MarkDirty(item);
    latest=capture.Capture(Array.Empty<ActorFrame>())[0];Check(latest.Lit&&latest.SampleDiscontinuity&&latest.SampleTime==11.03&&capture.NativeReads>reads&&capture.ChildReads>children);
});
Test("continuous fuel metadata cannot postpone an unknown native behavior safety fallback",()=>
{
    var item=Born();item.gameObject.Attach(new UnclassifiedUpdater());var fuel=new FloatItemData{Value=100};item.data=new(){data=new(){[DataEntryKey.Fuel]=fuel}};
    using var capture=new ItemCapture();capture.Capture(Array.Empty<ActorFrame>());
    for(int i=1;i<=90;i++){Time.timeAsDouble=10+i/30d;ItemCapture.MarkMetadataObservation(item);capture.Capture(Array.Empty<ActorFrame>());}
    long children=capture.ChildReads;
    for(int i=91;i<=240;i++){Time.timeAsDouble=10+i/30d;fuel.Value-=.01f;ItemCapture.MarkMetadataObservation(item);capture.Capture(Array.Empty<ActorFrame>());}
    Check(capture.ChildReads-children>=5&&capture.FallbackReads>=5&&capture.MetadataReads>=30);
});
Test("ten seconds of ordinary static safety fallbacks keep one immutable dormant frame and page baseline identity",()=>
{
    var item=Born();item.rig!.isKinematic=false;using var capture=new ItemCapture();capture.Capture(Array.Empty<ActorFrame>());
    for(int i=1;i<=90;i++){Time.timeAsDouble=10+i/30d;capture.Capture(Array.Empty<ActorFrame>());}
    var heldArray=capture.Capture(Array.Empty<ActorFrame>());var held=heldArray[0];long reads=capture.NativeReads,fallbacks=capture.FallbackReads;
    Check(held.ClockMode==CreatureReplayRules.SleepingClock);var pageFrames=new HashSet<ItemFrame>();
    for(int i=91;i<=390;i++)
    {
        Time.timeAsDouble=10+i/30d;var frames=capture.Capture(Array.Empty<ActorFrame>());
        Check(ReferenceEquals(frames,heldArray)&&ReferenceEquals(frames[0],held)&&frames[0].SampleTime==held.SampleTime);
        if(i%30==0)pageFrames.Add(frames[0]);
    }
    Check(capture.NativeReads>reads&&capture.FallbackReads-fallbacks>=10&&pageFrames.Count==1);
});
Test("a fuel-only change discovered by ordinary fallback keeps the sleeping root packet and observation time",()=>
{
    var item=Born();item.rig!.isKinematic=false;var fuel=new FloatItemData{Value=100};item.data=new(){data=new(){[DataEntryKey.Fuel]=fuel}};
    using var capture=new ItemCapture();capture.Capture(Array.Empty<ActorFrame>());
    for(int i=1;i<=90;i++){Time.timeAsDouble=10+i/30d;capture.Capture(Array.Empty<ActorFrame>());}
    var held=capture.Capture(Array.Empty<ActorFrame>())[0];fuel.Value=99;
    for(int i=91;i<=120;i++){Time.timeAsDouble=10+i/30d;capture.Capture(Array.Empty<ActorFrame>());}
    var changed=capture.Capture(Array.Empty<ActorFrame>())[0];
    Check(changed.Fuel==99&&held.Fuel==100&&!ReferenceEquals(changed,held)&&ReferenceEquals(changed.Pose,held.Pose));
    Check(changed.SampleTime==held.SampleTime&&changed.ClockMode==held.ClockMode&&changed.SampleDiscontinuity==held.SampleDiscontinuity);
});
Test("real unknown child changes and same-root semantic events still create fresh observations after a dormant checkpoint",()=>
{
    var item=Born();item.rig!.isKinematic=false;item.gameObject.Attach(new UnclassifiedUpdater());using var capture=new ItemCapture();capture.Capture(Array.Empty<ActorFrame>());
    for(int i=1;i<=90;i++){Time.timeAsDouble=10+i/30d;capture.Capture(Array.Empty<ActorFrame>());}
    var held=capture.Capture(Array.Empty<ActorFrame>())[0];item.transform.ChildRevision=1;
    for(int i=91;i<=120;i++){Time.timeAsDouble=10+i/30d;capture.Capture(Array.Empty<ActorFrame>());}
    var changed=capture.Capture(Array.Empty<ActorFrame>())[0];Check(changed.SampleTime>held.SampleTime&&changed.Pose.Nodes.Length==1&&held.Pose.Nodes.Length==0);
    Time.timeAsDouble=14.03;ItemCapture.MarkDirty(item);changed=capture.Capture(Array.Empty<ActorFrame>())[0];
    Check(changed.SampleTime==14.03&&changed.SampleDiscontinuity&&changed.ClockMode==CreatureReplayRules.ActiveClock);
});
Test("an active animated packet retains five Hertz source brackets even when sampled root values match",()=>
{
    var item=Born();item.gameObject.Attach(new Animator());using var capture=new ItemCapture();capture.Capture(Array.Empty<ActorFrame>());var clocks=new HashSet<double>();
    for(int i=1;i<=30;i++){Time.timeAsDouble=10+i/30d;var frame=capture.Capture(Array.Empty<ActorFrame>())[0];Check(frame.ClockMode==CreatureReplayRules.ActiveClock);clocks.Add(frame.SampleTime);}
    Check(clocks.Count==6&&clocks.Max()==11);
});
Test("settled frogs keep one immutable idle state while native Animator and child reads stop",()=>
{
    FrogCaptureActivity.Enabled=true;var item=new MobItem{itemID=172};item.gameObject.Attach(new Animator());Item.ALL_ITEMS.Add(item);
    using var capture=new ItemCapture();
    for(int i=0;i<45;i++){Time.timeAsDouble=10+i/30d;capture.Capture(Array.Empty<ActorFrame>());}
    Check(FrogCaptureActivity.Last!.Holding);var first=capture.Capture(Array.Empty<ActorFrame>())[0];
    long reads=capture.NativeReads,children=capture.ChildReads,animators=capture.AnimationProbes;int components=item.gameObject.ComponentReads;
    for(int i=0;i<300;i++){Time.timeAsDouble=12+i/30d;var frame=capture.Capture(Array.Empty<ActorFrame>())[0];Check(ReferenceEquals(first,frame));}
    Check(capture.NativeReads==reads&&capture.ChildReads==children&&capture.AnimationProbes==animators&&item.gameObject.ComponentReads==components);
    Check(capture.FrogRootChecks>0&&capture.FrogIdleSkips>250&&capture.FrogIdleTransitions==1&&first.IdleAnimation!=null);
    item.transform.position=new(2,0,0);ItemCapture.MarkDirty(item);Time.timeAsDouble+=1d/30;
    var active=capture.Capture(Array.Empty<ActorFrame>())[0];Check(active.IdleAnimation==null&&active.Pose.Position[0]==2&&capture.ChildReads>children);
});
Test("frog root safety motion returns to full capture without a native event",()=>
{
    FrogCaptureActivity.Enabled=true;var item=new MobItem{itemID=172};Item.ALL_ITEMS.Add(item);using var capture=new ItemCapture();
    for(int i=0;i<45;i++){Time.timeAsDouble=10+i/30d;capture.Capture(Array.Empty<ActorFrame>());}
    long reads=capture.NativeReads;FrogCaptureActivity.ForceRootWake=true;item.transform.position=new(.1f,0,0);Time.timeAsDouble+=1;
    var frame=capture.Capture(Array.Empty<ActorFrame>())[0];Check(frame.IdleAnimation==null&&frame.Pose.Position[0]==.1f&&capture.NativeReads>reads&&frame.SampleDiscontinuity);
});
Console.WriteLine($"TOTAL: {passed} contracts against actual ItemCapture passed. Native object/geometry adapters are controlled doubles, not Unity engine execution.");

internal sealed class UnclassifiedUpdater:MonoBehaviour {public void Update(){}}
