using PeakReplayLab;
using UnityEngine;

int passed=0;
void Check(bool value,string detail="") {if(!value)throw new Exception("Frog capture contract failed: "+detail);}
void Test(string name,Action test)
{
    Time.timeAsDouble=0;FrogCaptureActivity.EventHooksHealthy=ItemCapture.EventHooksHealthy=true;ReplaySafety.Active=false;ItemCapture.Dirty=0;
    test();passed++;Console.WriteLine("PASS "+name);
}
(MobItem Item,FrogTongue Frog,AnimationClip Idle,RuntimeAnimatorController Controller) Fixture()
{
    var item=new MobItem();var frog=new FrogTongue {mobItem=item};item.gameObject.Put(item);item.gameObject.Put(frog);
    var idle=new AnimationClip {name="FrogIdle"};
    var directory=new[]{idle,new AnimationClip{name="FrogJump"},new AnimationClip{name="FrogLick"},new AnimationClip{name="FrogLickEnd"},new AnimationClip{name="FrogAirborneIdle"},new AnimationClip{name="HelloMyBaby"}};
    var controller=new RuntimeAnimatorController {name="FatFrog (1)",Clips=directory};
    frog.anim.runtimeAnimatorController=controller;frog.anim.Current.Add(new(){clip=idle,weight=1});
    return(item,frog,idle,controller);
}
ItemFrame Enter(FrogCaptureActivity activity,double now=0)
{
    var checkpoint=new ItemFrame();Check(activity.TryEnter(now,checkpoint,true,out var animation),"known idle rejected");checkpoint.IdleAnimation=animation;return checkpoint;
}
Test("known idle stores phase and eliminates per-tick native bone/Animator/root queries",()=>
{
    var f=Fixture();var a=FrogCaptureActivity.Create(f.Item)!;var frame=Enter(a);
    Check(frame.IdleAnimation!.Clip==CreatureIdleAnimationRules.FrogIdle&&frame.IdleAnimation.ClipTime==.5&&frame.IdleAnimation.Speed==1);
    int queries=f.Frog.anim.StateQueries,components=f.Item.gameObject.ComponentReads;
    for(int i=0;i<600;i++)Check(a.TryHold(i/60d,frame,false,false));
    Check(f.Frog.anim.StateQueries==queries&&f.Item.transform.PositionReads==0&&f.Item.gameObject.ComponentReads==components);
    Check(f.Controller.DirectoryReads==1&&a.IdleSkips==600);
});
Test("staggered safety check keeps a matching native idle clock without reading the rig",()=>
{
    var f=Fixture();var a=FrogCaptureActivity.Create(f.Item)!;var frame=Enter(a);
    f.Frog.anim.State.normalizedTime=.55f;
    Check(a.TryHold(.6,frame,false,true));Check(a.RootChecks==1&&f.Item.transform.PositionReads==1&&f.Controller.DirectoryReads==1);
});
Test("native action releases idle immediately and cannot settle before its finite rotation grace",()=>
{
    var f=Fixture();var a=FrogCaptureActivity.Create(f.Item)!;var frame=Enter(a);
    FrogCaptureActivity.NotifyNativeEvent(f.Frog);Check(!a.Holding&&ItemCapture.Dirty==1&&!a.TryHold(0,frame,false,false));
    Check(!a.TryEnter(.79,frame,true,out _));Check(a.TryEnter(.8,frame,true,out _));
    Time.timeAsDouble=10;Check(FrogCaptureActivity.IsDormant(f.Frog));
});
Test("sleeping alone does not authorize idling during water or a current native action",()=>
{
    foreach(int action in Enumerable.Range(0,6))
    {
        var f=Fixture();f.Frog.sleeping=true;f.Frog.anim.enabled=false;f.Frog.visuals.gameObject.activeSelf=false;
        f.Frog.SetAction(water:action==0,pulling:action==1,thrown:action==2,tongue:action==3?.2f:0,character:action==4?new Character():null,target:action==5?new Rigidbody():null);
        var a=FrogCaptureActivity.Create(f.Item)!;Check(!a.TryEnter(0,new(),true,out _),"action "+action);
    }
});
Test("ordinary native Map hits are not water and use only the cached surface tag",()=>
{
    var f=Fixture();var terrain=f.Frog.SetWaterHit("Terrain")!;var a=FrogCaptureActivity.Create(f.Item)!;
    int components=f.Item.gameObject.ComponentReads;var checkpoint=Enter(a);
    for(int i=0;i<60;i++)Check(a.TryHold(i/60d,checkpoint,false,false));
    Check(terrain.TagComparisons==61&&f.Item.gameObject.ComponentReads==components&&f.Item.transform.PositionReads==0);
    Check(a.Rejections(FrogIdleRejection.Water)==0&&a.LastRejection==FrogIdleRejection.None);
});
Test("a cached actual Water hit rejects idle and releases a held dry-land frog",()=>
{
    var f=Fixture();var a=FrogCaptureActivity.Create(f.Item)!;var checkpoint=Enter(a);f.Frog.SetWaterHit("Water");
    Check(!a.TryHold(.1,checkpoint,false,false)&&!a.Holding&&a.LastRejection==FrogIdleRejection.Water);
    Check(a.Rejections(FrogIdleRejection.Water)==1&&f.Item.transform.PositionReads==0);
    Check(!a.TryEnter(1,checkpoint,true,out _)&&a.Rejections(FrogIdleRejection.Water)==2);
});
Test("inconsistent native water flag and missing cached hit retain capture fallback",()=>
{
    var f=Fixture();f.Frog.SetWaterHit(null);var a=FrogCaptureActivity.Create(f.Item)!;
    Check(!a.TryEnter(0,new(),true,out _)&&a.LastRejection==FrogIdleRejection.UnknownWater&&a.Rejections(FrogIdleRejection.UnknownWater)==1);
    f.Frog.SetWaterHit("Water",nativeInWater:false);Enter(a);
});
Test("idle rejection counters distinguish unsettled roots event grace ownership and animation",()=>
{
    var f=Fixture();var a=FrogCaptureActivity.Create(f.Item)!;
    Check(!a.TryEnter(0,new(),false,out _)&&a.Rejections(FrogIdleRejection.Unsettled)==1);
    f.Item.itemState=ItemState.Held;Check(!a.TryEnter(0,new(),true,out _)&&a.Rejections(FrogIdleRejection.Ownership)==1);
    f.Item.itemState=ItemState.Ground;FrogCaptureActivity.NotifyNativeEvent(f.Frog);
    Check(!a.TryEnter(.1,new(),true,out _)&&a.Rejections(FrogIdleRejection.EventGrace)==1);
    f.Frog.anim.Transition=true;Check(!a.TryEnter(.8,new(),true,out _)&&a.Rejections(FrogIdleRejection.CurrentAnimation)==1);
});
Test("completed native tongue retraction may finish slightly below zero",()=>
{
    var f=Fixture();f.Frog.SetAction(tongue:-.02f);var a=FrogCaptureActivity.Create(f.Item)!;Enter(a);
    f.Frog.SetAction(tongue:float.NaN);Check(!a.TryHold(.1,new(),false,false));
});
Test("native hidden sleep uses one hidden checkpoint and preserves wake visibility",()=>
{
    var f=Fixture();f.Frog.sleeping=true;f.Frog.anim.enabled=false;f.Frog.visuals.gameObject.activeSelf=false;
    var a=FrogCaptureActivity.Create(f.Item)!;var frame=Enter(a);Check(frame.IdleAnimation==null);
    Check(a.TryHold(1,frame,false,true)&&f.Frog.anim.StateQueries==0);
    f.Frog.sleeping=false;Check(!a.TryHold(1.1,frame,false,false));
});
Test("held backpack cooked and unsettled frogs never enter the idle optimization",()=>
{
    foreach(int reason in Enumerable.Range(0,4))
    {
        var f=Fixture();if(reason==0)f.Item.itemState=ItemState.Held;if(reason==1)f.Item.itemState=ItemState.InBackpack;if(reason==2)f.Item.cooking!.timesCookedLocal=1;
        var a=FrogCaptureActivity.Create(f.Item)!;Check(!a.TryEnter(0,new(),reason!=3,out _));
    }
});
Test("unknown controllers overrides transitions blended layers and different clips retain fallback",()=>
{
    foreach(int reason in Enumerable.Range(0,9))
    {
        var f=Fixture();
        switch(reason)
        {
            case 0:f.Controller.name="mod controller";break;
            case 1:f.Frog.anim.runtimeAnimatorController=new AnimatorOverrideController{name="FatFrog (1)",Clips=f.Controller.Clips};break;
            case 2:f.Frog.anim.Transition=true;break;
            case 3:f.Frog.anim.layerCount=2;break;
            case 4:f.Frog.anim.Current[0]=new(){clip=f.Controller.Clips[1],weight=1};break;
            case 5:f.Frog.anim.Current[0]=new(){clip=f.Idle,weight=.5f};break;
            case 6:f.Frog.anim.applyRootMotion=true;break;
            case 7:f.Frog.anim.cullingMode=AnimatorCullingMode.CullUpdateTransforms;break;
            case 8:f.Idle.Events=new[]{new AnimationEvent()};break;
        }
        var a=FrogCaptureActivity.Create(f.Item)!;Check(!a.TryEnter(0,new(),true,out _),"reason "+reason);
    }
});
Test("failed current animation validations are retried at the existing bounded probe interval",()=>
{
    var f=Fixture();f.Frog.anim.Transition=true;var a=FrogCaptureActivity.Create(f.Item)!;
    for(int i=0;i<30;i++)Check(!a.TryEnter(i/60d,new(),true,out _));
    Check(f.Frog.anim.TransitionQueries==1&&f.Item.transform.PositionReads==0);
});
Test("unannounced root velocity and scale motion are detected by the shared safety poll",()=>
{
    foreach(int reason in Enumerable.Range(0,4))
    {
        var f=Fixture();var a=FrogCaptureActivity.Create(f.Item)!;var frame=Enter(a);f.Frog.anim.State.normalizedTime=.55f;
        if(reason==0)f.Item.transform.Position=new(.001f,0,0);if(reason==1)f.Item.rig!.linearVelocity=new(.02f,0,0);
        if(reason==2)f.Item.transform.Scale=new(1,1,1.001f);if(reason==3)f.Item.rig!.angularVelocity=new(0,.02f,0);
        Check(!a.TryHold(.6,frame,false,true),"reason "+reason);Check(a.RootChecks==1);
    }
});
Test("equivalent signed quaternions keep a stationary checkpoint",()=>
{
    var f=Fixture();var a=FrogCaptureActivity.Create(f.Item)!;var frame=Enter(a);f.Frog.anim.State.normalizedTime=.55f;f.Item.transform.Rotation=new(0,0,0,-1);
    Check(a.TryHold(.6,frame,false,true));
});
Test("native pause clock resets or controller replacement end held idle at the safety boundary",()=>
{
    foreach(int reason in Enumerable.Range(0,3))
    {
        var f=Fixture();var a=FrogCaptureActivity.Create(f.Item)!;var frame=Enter(a);
        if(reason==0)f.Frog.anim.State.speed=.5f;if(reason==1)f.Frog.anim.State.normalizedTime=0;
        if(reason==2)f.Frog.anim.runtimeAnimatorController=new RuntimeAnimatorController{name="FatFrog (1)",Clips=f.Controller.Clips};
        Check(!a.TryHold(.6,frame,false,true));
    }
});
Test("degraded event coverage prevents idle and releases an already held checkpoint",()=>
{
    var f=Fixture();var a=FrogCaptureActivity.Create(f.Item)!;var frame=Enter(a);
    FrogCaptureActivity.EventHooksHealthy=false;Check(!a.TryHold(.1,frame,false,false));
    FrogCaptureActivity.EventHooksHealthy=true;ItemCapture.EventHooksHealthy=false;Check(!a.TryEnter(1,frame,true,out _));
});
Test("idle runtime samples only mapped inert bones using absolute pause and reverse time",()=>
{
    var f=Fixture();var a=FrogCaptureActivity.Create(f.Item)!;var frame=Enter(a);var visual=new VisualReplica();
    Check(FrogIdleAnimationRuntime.TryCreate(visual,f.Item,out var runtime));
    using(runtime!)
    {
        runtime!.Apply(frame.IdleAnimation!,0);Check(f.Idle.SampleTime==.5f&&f.Idle.SampleTarget==visual.Clone);
        runtime.Apply(frame.IdleAnimation!,1);Check(f.Idle.SampleTime==1.5f);
        int calls=f.Idle.Samples;runtime.Apply(frame.IdleAnimation!,1);Check(f.Idle.Samples==calls);
        runtime.Apply(frame.IdleAnimation!,-1);Check(f.Idle.SampleTime==1.5f,"same phase may reuse sampled pose");
        runtime.Apply(frame.IdleAnimation!,-.25);Check(f.Idle.SampleTime==.25f);
        runtime.Invalidate();runtime.Apply(frame.IdleAnimation!,-.25);Check(f.Idle.Samples==calls+2);
    }
    int final=f.Idle.Samples;runtime!.Apply(frame.IdleAnimation!,2);Check(f.Idle.Samples==final&&f.Controller.DirectoryReads==1);
});
Test("replay clip mapping cannot target the native game hierarchy",()=>
{
    var f=Fixture();Check(!FrogIdleAnimationRuntime.TryCreate(new VisualReplica{ReturnNative=true},f.Item,out _));
    Check(!FrogIdleAnimationRuntime.TryCreate(new VisualReplica{MappingExists=false},f.Item,out _));
});
Test("native idle cycle offset is recorded exactly once and reset on the replay evaluator",()=>
{
    var f=Fixture();f.Frog.anim.IdleOffset=.375f;var a=FrogCaptureActivity.Create(f.Item)!;var frame=Enter(a);
    Check(frame.IdleAnimation!.ClipTime==1.25,"native phase offset missing");
    var visual=new VisualReplica();Check(FrogIdleAnimationRuntime.TryCreate(visual,f.Item,out var runtime));runtime!.Apply(frame.IdleAnimation,0);
    Check(f.Idle.SampleTime==1.25f&&visual.Clone.GetComponent<Animator>()!.IdleOffset==0&&f.Frog.anim.IdleOffset==.375f);
});
Test("pure visual evaluator has no automatic time events transitions or native hierarchy writes",()=>
{
    var f=Fixture();var a=FrogCaptureActivity.Create(f.Item)!;var frame=Enter(a);var visual=new VisualReplica();Check(FrogIdleAnimationRuntime.TryCreate(visual,f.Item,out var runtime));
    var evaluator=visual.Clone.GetComponent<Animator>()!;Check(!evaluator.enabled&&!evaluator.fireEvents&&!evaluator.applyRootMotion&&evaluator.speed==0&&evaluator.Rebinds==0);
    runtime!.Apply(frame.IdleAnimation!,1);Check(evaluator.Rebinds==1&&evaluator.ExplicitUpdates==1&&evaluator.ExplicitPlays==1&&evaluator.ResetTriggers==2&&!evaluator.Airborne);
    runtime.Apply(frame.IdleAnimation!,1);Check(evaluator.ExplicitUpdates==1&&f.Frog.anim.ExplicitUpdates==0&&f.Frog.anim.enabled&&f.Frog.anim.fireEvents);
});
Test("native StateMachineBehaviours and unknown state hashes retain capture fallback",()=>
{
    var f=Fixture();f.Controller.Behaviours=new[]{new StateMachineBehaviour()};var a=FrogCaptureActivity.Create(f.Item)!;
    Check(!a.TryEnter(0,new(),true,out _));var visual=new VisualReplica();Check(!FrogIdleAnimationRuntime.TryCreate(visual,f.Item,out _)&&visual.Clone.GetComponent<Animator>()==null);
    f=Fixture();f.Frog.anim.State.fullPathHash=0;a=FrogCaptureActivity.Create(f.Item)!;Check(!a.TryEnter(0,new(),true,out _));
});
Test("pooled idle visuals reuse their disabled controller-free evaluator",()=>
{
    var f=Fixture();var visual=new VisualReplica();Check(FrogIdleAnimationRuntime.TryCreate(visual,f.Item,out var first));var evaluator=visual.Clone.GetComponent<Animator>()!;
    first!.Dispose();Check(!evaluator.enabled&&evaluator.runtimeAnimatorController==null);
    Check(FrogIdleAnimationRuntime.TryCreate(visual,f.Item,out var second)&&ReferenceEquals(evaluator,visual.Clone.GetComponent<Animator>()));second!.Dispose();
});
Test("five Hertz packet invalidation reuses bindings and real deactivation loss repairs at identical phase",()=>
{
    var f=Fixture();var capture=FrogCaptureActivity.Create(f.Item)!;var frame=Enter(capture);var visual=new VisualReplica();
    Check(FrogIdleAnimationRuntime.TryCreate(visual,f.Item,out var runtime));var evaluator=visual.Clone.GetComponent<Animator>()!;
    for(int index=0;index<300;index++){runtime!.Invalidate();runtime.Apply(frame.IdleAnimation!,index*.2);}
    Check(evaluator.Rebinds==1&&evaluator.ExplicitUpdates==300,"refresh rebound native bindings");
    evaluator.isInitialized=false;runtime!.Apply(frame.IdleAnimation!,299*.2);
    Check(evaluator.Rebinds==2&&evaluator.ExplicitUpdates==301,"same phase concealed binding loss");
    runtime.Apply(frame.IdleAnimation!,299*.2);Check(evaluator.Rebinds==2&&evaluator.ExplicitUpdates==301);
});
Test("hidden first Apply never binds or evaluates before the visual hierarchy becomes active",()=>
{
    var f=Fixture();var capture=FrogCaptureActivity.Create(f.Item)!;var frame=Enter(capture);var visual=new VisualReplica();visual.Clone.activeInHierarchy=false;
    Check(FrogIdleAnimationRuntime.TryCreate(visual,f.Item,out var runtime));var evaluator=visual.Clone.GetComponent<Animator>()!;
    runtime!.Apply(frame.IdleAnimation!,0);Check(evaluator.Rebinds==0&&evaluator.ExplicitUpdates==0);
    visual.Clone.activeInHierarchy=true;runtime.Apply(frame.IdleAnimation!,0);Check(evaluator.Rebinds==1&&evaluator.ExplicitUpdates==1);
    visual.Clone.activeInHierarchy=false;evaluator.isInitialized=false;runtime.Apply(frame.IdleAnimation!,.4);
    Check(evaluator.Rebinds==1&&evaluator.ExplicitUpdates==1);visual.Clone.activeInHierarchy=true;runtime.Apply(frame.IdleAnimation!,.4);
    Check(evaluator.Rebinds==2&&evaluator.ExplicitUpdates==2);
});
Console.WriteLine("Frog capture and idle contracts passed: "+passed);
