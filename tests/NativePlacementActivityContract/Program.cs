using PeakReplayLab;
using UnityEngine;

int passed=0;
void Check(bool value,string message="") { if(!value)throw new Exception("Placement activity assertion failed: "+message); }
void Test(string name,Action action) { action();passed++;Console.WriteLine("PASS "+name); }
GameObject Flag(out Animator animator)
{
    var source=new GameObject();animator=new Animator();source.Put(new CheckpointFlag {anim=animator});return source;
}
Test("native checkpoint flag samples every admitted jittered scene tick with no repeated component discovery",()=>
{
    foreach(int renderHz in new[]{60,58})
    {
        var source=Flag(out var animator);var main=new SampleClock(60);var scene=new SampleClock(30);int ticks=0, componentReads=-1,directoryReads=-1;
        for(int i=0;i<renderHz*100;i++)
        {
            double now=1000+i/(double)renderHz+(renderHz==60?(i%4<2?.0015:-.0015):0);
            if(!main.ShouldCapture(now)||!scene.ShouldCapture(now))continue;
            animator.State.normalizedTime=(float)(now-1000);
            Check(NativePlacementResources.IsAnimating(source,now,followSceneClock:true));ticks++;
            if(componentReads<0){componentReads=source.ComponentReads;directoryReads=source.DirectoryReads;}
            Check(source.ComponentReads==componentReads&&source.DirectoryReads==directoryReads);
        }
        Check(ticks>=2990&&animator.StateReads==ticks&&animator.TransitionReads==ticks);
    }
});
Test("placement scene clock deduplicates same timestamps while standalone callers keep their local deadline",()=>
{
    var source=Flag(out var animator);
    Check(NativePlacementResources.IsAnimating(source,0,followSceneClock:true));
    for(int i=0;i<100;i++)Check(NativePlacementResources.IsAnimating(source,0,followSceneClock:true));
    Check(animator.StateReads==1);
    animator.State.normalizedTime=.02f;
    Check(NativePlacementResources.IsAnimating(source,.02,followSceneClock:true)&&animator.StateReads==2);
    var legacy=Flag(out var old);
    Check(NativePlacementResources.IsAnimating(legacy,0));old.State.normalizedTime=.02f;
    Check(NativePlacementResources.IsAnimating(legacy,.02)&&old.StateReads==1);
    old.State.normalizedTime=.04f;Check(NativePlacementResources.IsAnimating(legacy,.04)&&old.StateReads==2);
});
Test("native placement growth starts and stops on adjacent scene ticks without manufacturing geometry activity",()=>
{
    var source=new GameObject();var vine=new MagicBeanVine();source.Put(vine);
    Check(!NativePlacementResources.IsAnimating(source,0,followSceneClock:true));
    vine.Growing=true;Check(NativePlacementResources.IsAnimating(source,.02,followSceneClock:true));
    vine.Growing=false;Check(!NativePlacementResources.IsAnimating(source,.04,followSceneClock:true));
    var stove=new GameObject();stove.Put(new Campfire());
    Check(!NativePlacementResources.IsAnimating(stove,.04,followSceneClock:true));
    var hidden=Flag(out var animator);hidden.activeInHierarchy=false;
    Check(!NativePlacementResources.IsAnimating(hidden,.04,followSceneClock:true)&&hidden.ComponentReads==0&&animator.StateReads==0);
});
Test("native flag pause and terminal animation become inactive without force dirtying or delaying the stop",()=>
{
    var source=Flag(out var animator);
    Check(NativePlacementResources.IsAnimating(source,0,followSceneClock:true));
    animator.speed=0;Check(!NativePlacementResources.IsAnimating(source,.02,followSceneClock:true));
    animator.speed=1;animator.State.loop=false;animator.State.normalizedTime=1.5f;
    Check(!NativePlacementResources.IsAnimating(source,.04,followSceneClock:true));
    animator.State.normalizedTime=2;Check(!NativePlacementResources.IsAnimating(source,.06,followSceneClock:true));
    animator.State.normalizedTime=.5f;Check(NativePlacementResources.IsAnimating(source,.08,followSceneClock:true));
});
Test("settled released piton stops scale reads and a new crack target resumes at the next scene observation",()=>
{
    var source=new GameObject();var crack=new GameObject();var piton=new ShittyPiton {Breaking=true,CrackScale=.75f,crack=crack};source.Put(piton);
    crack.transform.localScale=new Vector3(.75f,.75f,.75f);
    Check(NativePlacementResources.IsAnimating(source,0,followSceneClock:true));
    Check(!NativePlacementResources.IsAnimating(source,.02,followSceneClock:true));int reads=crack.transform.ScaleReads;
    for(int i=2;i<6000;i++)Check(!NativePlacementResources.IsAnimating(source,i*.02,followSceneClock:true));
    Check(crack.transform.ScaleReads==reads);
    piton.CrackScale=.8f;Check(NativePlacementResources.IsAnimating(source,120,followSceneClock:true)&&crack.transform.ScaleReads==reads+1);
    piton.Disabled=true;Check(!NativePlacementResources.IsAnimating(source,120.02,followSceneClock:true));
});
Test("spawned independent scene lights preserve every jittered observation while geometry remains asleep",()=>
{
    var life=new SpawnedCaptureLifecycle(30,followSceneClock:true);var lights=new ItemActivityPolicy();var scene=new SampleClock(30);
    life.Observed(0,false);int ticks=0,lightReads=0,geometryReads=0;
    for(int i=1;i<6000;i++)
    {
        double now=i/60d+(i%4<2?.0015:-.0015);if(!scene.ShouldCapture(now))continue;ticks++;
        if(lights.LightDue(now,followSceneClock:true)){lightReads++;lights.LightObserved(now);}
        Check(!lights.LightDue(now,followSceneClock:true));
        if(life.ShouldRead(now,true,false,false))geometryReads++;
    }
    Check(ticks>2900&&lightReads==ticks&&geometryReads==0);
});
Console.WriteLine($"TOTAL: {passed} native placement activity checks passed. Production helpers are compiled against deterministic Unity doubles; no game is launched.");
