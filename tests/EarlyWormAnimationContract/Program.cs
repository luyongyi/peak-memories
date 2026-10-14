using PeakReplayLab;
using UnityEngine;

int passed=0;
void Check(bool value,string detail="") {if(!value)throw new Exception("EarlyWorm animation contract failed: "+detail);}
void Test(string name,Action test) {test();passed++;Console.WriteLine("PASS "+name);}
(Item Item,Peak.EarlyWorm Worm,AnimationClip Walk,AnimationClip Hand,AnimationClip Cooked,RuntimeAnimatorController Controller) Fixture()
{
    var item=new Item();var worm=new Peak.EarlyWorm();item.gameObject.Put(item);item.gameObject.Put(worm);
    worm.rotate.tf=worm.anim.transform;
    var walk=new AnimationClip{name="EarlyWormWalk"};var hand=new AnimationClip{name="EarlyWormInHand"};var cooked=new AnimationClip{name="EarlyWormCooked",length=0};
    var controller=new RuntimeAnimatorController{name="EarlyWorm",Clips=new[]{walk,hand,cooked}};
    worm.anim.runtimeAnimatorController=controller;worm.anim.Current.Add(new(){clip=walk,weight=1});
    return(item,worm,walk,hand,cooked,controller);
}
bool Near(Quaternion a,Quaternion b) => Math.Abs((double)a.x*b.x+(double)a.y*b.y+(double)a.z*b.z+(double)a.w*b.w)>.999999;
Test("known walk records the two native resource clocks without per-tick hierarchy or Animator queries",()=>
{
    var f=Fixture();Check(EarlyWormAnimationCapture.TryCreate(f.Item,out var capture));
    Check(capture!.TryObserve(10,false,out var animation,out bool changed)&&changed);
    Check(animation!.Clip==CreatureIdleAnimationRules.EarlyWormWalk&&animation.ClipTime==.25&&animation.SpinSpeed==-50&&animation.SpinRotation!.Length==4);
    int queries=f.Worm.anim.StateQueries,components=f.Item.gameObject.ComponentReads,rotation=f.Worm.anim.transform.RotationReads;
    for(int i=0;i<100;i++)Check(capture.TryObserve(10+i/1000d,false,out var same,out changed)&&ReferenceEquals(same,animation)&&!changed);
    Check(f.Worm.anim.StateQueries==queries&&f.Item.gameObject.ComponentReads==components&&f.Worm.anim.transform.RotationReads==rotation);
    Check(f.Controller.DirectoryReads==1&&capture.ClockChecks==1&&capture.GeometrySkips==100);
});
Test("five Hertz validation preserves the same clock and quaternion anchor",()=>
{
    var f=Fixture();var capture=new EarlyWormAnimationCapture(f.Item);Check(capture.TryObserve(0,false,out var animation,out _));
    for(int i=1;i<=600;i++)
    {
        double now=i*.2;f.Worm.anim.State.normalizedTime=(float)(.25+now);f.Worm.anim.transform.Rotation=Quaternion.AngleAxis((float)(-50*now),Vector3.up);
        Check(capture.TryObserve(now,false,out var same,out bool changed)&&!changed&&ReferenceEquals(same,animation),"clock "+i);
    }
    Check(capture.ClockChecks==601&&capture.DescriptorChanges==1&&f.Controller.DirectoryReads==1);
});
Test("native ownership events bypass the low frequency deadline and native blends retain fallback",()=>
{
    var f=Fixture();var capture=new EarlyWormAnimationCapture(f.Item);Check(capture.TryObserve(0,false,out _,out _));
    f.Item.itemState=ItemState.Held;f.Worm.anim.Ground=false;f.Worm.rotate.enabled=false;f.Worm.anim.Transition=true;
    Check(!capture.TryObserve(.01,true,out var animation,out bool changed)&&animation==null&&changed);
    f.Worm.anim.Transition=false;f.Worm.anim.State.fullPathHash=2120410058;f.Worm.anim.Current[0]=new(){clip=f.Hand,weight=1};
    Check(capture.TryObserve(.02,true,out animation,out changed)&&changed&&animation!.Clip==CreatureIdleAnimationRules.EarlyWormInHand&&animation.SpinSpeed==0);
});
Test("cooked uses the exact zero-time final native appearance and no procedural spin",()=>
{
    var f=Fixture();f.Cooked.isLooping=false;var capture=new EarlyWormAnimationCapture(f.Item);f.Item.cooking!.timesCookedLocal=1;f.Worm.anim.Cooked=true;f.Worm.rotate.enabled=false;
    f.Worm.anim.State.fullPathHash=824124038;f.Worm.anim.State.loop=false;f.Worm.anim.Current[0]=new(){clip=f.Cooked,weight=1};
    Check(capture.TryObserve(3,false,out var animation,out _)&&animation!.Clip==CreatureIdleAnimationRules.EarlyWormCooked&&animation.ClipTime==0&&animation.Speed==0&&animation.SpinSpeed==0);
    Check(CreatureIdleAnimationRules.SampleTime(animation!,100)==0);
});
Test("unannounced phase pause and extra pivot motion reanchor at the next bounded safety poll",()=>
{
    foreach(int reason in Enumerable.Range(0,3))
    {
        var f=Fixture();var capture=new EarlyWormAnimationCapture(f.Item);Check(capture.TryObserve(0,false,out var before,out _));
        f.Worm.anim.State.normalizedTime=.45f;f.Worm.anim.transform.Rotation=Quaternion.AngleAxis(-10,Vector3.up);
        if(reason==0)f.Worm.anim.State.normalizedTime=.9f;if(reason==1)f.Worm.anim.speed=0;if(reason==2)f.Worm.anim.transform.Rotation=Quaternion.AngleAxis(20,Vector3.up);
        Check(capture.TryObserve(.2,false,out var after,out bool changed)&&changed&&!ReferenceEquals(before,after));
    }
});
Test("modified assets and unsupported animated states fail open to sampled geometry",()=>
{
    foreach(int reason in Enumerable.Range(0,13))
    {
        var f=Fixture();
        if(reason==0)f.Controller.name="other";if(reason==1)f.Worm.anim.runtimeAnimatorController=new AnimatorOverrideController{name="EarlyWorm",Clips=f.Controller.Clips};
        if(reason==2)f.Walk.Events=new[]{new AnimationEvent()};if(reason==3)f.Walk.length=2;if(reason==4)f.Worm.anim.layerCount=2;
        if(reason==5)f.Worm.anim.applyRootMotion=true;if(reason==6)f.Worm.anim.cullingMode=AnimatorCullingMode.CullUpdateTransforms;
        if(reason==7)f.Worm.rotate.rotation=new(1,-50,0);if(reason==8)f.Worm.rotate.tf=new GameObject().transform;
        if(reason==9)f.Worm.anim.Current[0]=new(){clip=f.Walk,weight=.5f};if(reason==10)f.Worm.anim.State.fullPathHash=0;
        if(reason==11)f.Worm.anim.State.normalizedTime=float.NaN;if(reason==12)f.Worm.anim.transform.Rotation=new(0,0,0,0);
        var capture=new EarlyWormAnimationCapture(f.Item);Check(!capture.TryObserve(0,false,out _,out _),"reason "+reason);
    }
});
Test("absolute replay time reproduces idle spin with pause reverse and direct far seek",()=>
{
    var f=Fixture();f.Worm.anim.transform.Rotation=Quaternion.AngleAxis(30,Vector3.up);var capture=new EarlyWormAnimationCapture(f.Item);
    Check(capture.TryObserve(10,false,out var animation,out _));var visual=new VisualReplica();Check(EarlyWormAnimationRuntime.TryCreate(visual,f.Item,out var runtime));
    using(runtime!)
    {
        runtime!.Apply(animation!,10.5);Check(f.Walk.SampleTarget==visual.Clone&&f.Walk.SampleTime==.75f&&Near(visual.Clone.transform.Rotation,Quaternion.AngleAxis(5,Vector3.up)));
        int samples=f.Walk.Samples;runtime.Apply(animation!,10.5);Check(f.Walk.Samples==samples);
        runtime.Apply(animation!,9.75);Check(f.Walk.SampleTime==0&&Near(visual.Clone.transform.Rotation,Quaternion.AngleAxis(42.5f,Vector3.up)));
        runtime.Apply(animation!,1000);Check(f.Walk.SampleTime==.25f&&Near(visual.Clone.transform.Rotation,Quaternion.AngleAxis(210,Vector3.up)));
        Check(Near(f.Worm.anim.transform.Rotation,Quaternion.AngleAxis(30,Vector3.up)),"native source mutated");
        runtime.Invalidate();runtime.Apply(animation!,1000);Check(f.Walk.Samples==samples+3);
    }
    int final=f.Walk.Samples;runtime!.Apply(animation!,0);Check(f.Walk.Samples==final);
});
Test("rebasing shifts the clip and spin anchor equally and retains shared quaternion identity",()=>
{
    var f=Fixture();var capture=new EarlyWormAnimationCapture(f.Item);Check(capture.TryObserve(10,false,out var animation,out _));
    var rebased=CreatureIdleAnimationRules.Rebase(animation!,8);Check(rebased.AnchorTime==2&&ReferenceEquals(rebased.SpinRotation,animation!.SpinRotation));
    Check(CreatureIdleAnimationRules.SampleTime(animation!,12.25)==CreatureIdleAnimationRules.SampleTime(rebased,4.25));
    var visual=new VisualReplica();Check(EarlyWormAnimationRuntime.TryCreate(visual,f.Item,out var runtime));runtime!.Apply(rebased,4.25);Check(Near(visual.Clone.transform.Rotation,Quaternion.AngleAxis(-112.5f,Vector3.up)));
});
Test("runtime cannot map an animation to native source or an incomplete visual",()=>
{
    var f=Fixture();Check(!EarlyWormAnimationRuntime.TryCreate(new VisualReplica{ReturnNative=true},f.Item,out _));
    Check(!EarlyWormAnimationRuntime.TryCreate(new VisualReplica{MappingExists=false},f.Item,out _));
    Check(!EarlyWormAnimationRuntime.TryCreate(new VisualReplica{IsReady=false},f.Item,out _));
});
Test("steady clock fast path allocates no per-tick objects",()=>
{
    var f=Fixture();var capture=new EarlyWormAnimationCapture(f.Item);Check(capture.TryObserve(0,false,out _,out _));
    for(int i=0;i<1000;i++)capture.TryObserve(.1,false,out _,out _);
    long before=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<20000;i++)capture.TryObserve(.1,false,out _,out _);
    Check(GC.GetAllocatedBytesForCurrentThread()==before);
});
Test("visual evaluator cannot automatically advance time or invoke animation events",()=>
{
    var f=Fixture();var capture=new EarlyWormAnimationCapture(f.Item);Check(capture.TryObserve(10,false,out var animation,out _));
    var visual=new VisualReplica();Check(EarlyWormAnimationRuntime.TryCreate(visual,f.Item,out var runtime));
    var animator=visual.Clone.GetComponent<Animator>()!;
    Check(!animator.enabled&&!animator.fireEvents&&!animator.applyRootMotion&&animator.speed==0&&animator.Rebinds==0);
    runtime!.Apply(animation!,10.25);Check(animator.Rebinds==1&&animator.ExplicitUpdates==1&&animator.ExplicitPlays==1&&animator.Ground&&!animator.Cooked);
    runtime.Apply(animation!,10.25);Check(animator.ExplicitUpdates==1);
    Check(f.Worm.anim.ExplicitUpdates==0&&f.Worm.anim.enabled&&f.Worm.anim.fireEvents);
});
Test("pooled visual reuses only its disabled controller-free evaluator without deferred destruction",()=>
{
    var f=Fixture();var visual=new VisualReplica();Check(EarlyWormAnimationRuntime.TryCreate(visual,f.Item,out var first));
    var evaluator=visual.Clone.GetComponent<Animator>()!;first!.Dispose();
    Check(!evaluator.enabled&&evaluator.runtimeAnimatorController==null);
    Check(EarlyWormAnimationRuntime.TryCreate(visual,f.Item,out var second));Check(ReferenceEquals(evaluator,visual.Clone.GetComponent<Animator>())&&evaluator.Rebinds==0);
    second!.Dispose();
});
Test("controller StateMachineBehaviours are rejected before attaching an evaluator to a replay visual",()=>
{
    var f=Fixture();f.Controller.Behaviours=new[]{new StateMachineBehaviour()};var capture=new EarlyWormAnimationCapture(f.Item);
    Check(!capture.Supported&&!capture.TryObserve(0,false,out _,out _));
    var visual=new VisualReplica();Check(!EarlyWormAnimationRuntime.TryCreate(visual,f.Item,out _)&&visual.Clone.GetComponent<Animator>()==null);
});
Test("five Hertz pose invalidation does not rebind while actual binding loss repairs even at the same phase",()=>
{
    var f=Fixture();var capture=new EarlyWormAnimationCapture(f.Item);Check(capture.TryObserve(0,false,out var clock,out _));
    var visual=new VisualReplica();Check(EarlyWormAnimationRuntime.TryCreate(visual,f.Item,out var runtime));var evaluator=visual.Clone.GetComponent<Animator>()!;
    for(int index=0;index<300;index++){runtime!.Invalidate();runtime.Apply(clock!,index*.2);}
    Check(evaluator.Rebinds==1&&evaluator.ExplicitUpdates==300,"refresh rebound stable evaluator");
    evaluator.isInitialized=false;runtime!.Apply(clock!,299*.2);
    Check(evaluator.Rebinds==2&&evaluator.ExplicitUpdates==301,"binding loss bypassed same-time memo");
    runtime.Apply(clock!,299*.2);Check(evaluator.Rebinds==2&&evaluator.ExplicitUpdates==301);
});
Test("hidden first Apply defers binding and evaluation until the clone is actually visible",()=>
{
    var f=Fixture();var capture=new EarlyWormAnimationCapture(f.Item);Check(capture.TryObserve(0,false,out var clock,out _));
    var visual=new VisualReplica();visual.Clone.activeInHierarchy=false;Check(EarlyWormAnimationRuntime.TryCreate(visual,f.Item,out var runtime));
    var evaluator=visual.Clone.GetComponent<Animator>()!;runtime!.Apply(clock!,0);Check(evaluator.Rebinds==0&&evaluator.ExplicitUpdates==0);
    visual.Clone.activeInHierarchy=true;runtime.Apply(clock!,0);Check(evaluator.Rebinds==1&&evaluator.ExplicitUpdates==1);
    visual.Clone.activeInHierarchy=false;evaluator.isInitialized=false;runtime.Apply(clock!,.4);Check(evaluator.Rebinds==1&&evaluator.ExplicitUpdates==1);
    visual.Clone.activeInHierarchy=true;runtime.Apply(clock!,.4);Check(evaluator.Rebinds==2&&evaluator.ExplicitUpdates==2);
});
Console.WriteLine("EarlyWorm animation contracts passed: "+passed);
