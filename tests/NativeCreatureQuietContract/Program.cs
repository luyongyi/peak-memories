using PeakReplayLab;
using UnityEngine;

int passed = 0;
void Check(bool value, string message = "Native quiet guard failed") { if (!value) throw new Exception(message); }
void Test(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS "+name); } catch (Exception error) { Console.Error.WriteLine("FAIL "+name+": "+error); Environment.Exit(1); } }
GameObject Capy() { var go = new GameObject("Capybara"); go.Add(new Capybara()); go.Add(new WobbleSpinBounce { enabled = false }); go.Add(new Renderer()); return go; }
GameObject Venus()
{
    var go = new GameObject("VenusFlyTrap"); var root = new GameObject("trapRoot"); root.transform.parent = go.transform;
    var controller = new RuntimeAnimatorController { name = "VenusFlyTrap", animationClips = new[]
    { new AnimationClip { name="VenusFlyTrap_Idle",length=1.5f },new AnimationClip { name="VenusFlyTrap_Bite",length=2f/3 },
      new AnimationClip { name="VenusFlyTrap_Biting",length=5f/12 },new AnimationClip { name="VenusFlyTrap_LetGo",length=1f/3 } } };
    var animator = go.Add(new Animator { enabled = false, runtimeAnimatorController = controller });
    go.Add(new VenusFlyTrap { anim = animator,trapRoot = root.transform }); go.Add(new Renderer());
    go.Add(new Peak.GenericOptimizer()); go.Add(new Photon.Pun.PhotonView()); root.Add(new CollisionModifier()); return go;
}
CreatureReplayFrame Frame(GameObject go,string kind)
{
    var t=go.transform; var p=t.position; var q=t.rotation; var s=t.lossyScale;
    return new() { Kind=kind,Resource=kind,Pose=new() { Active=go.activeInHierarchy,Position=new[]{p.x,p.y,p.z},Rotation=new[]{q.x,q.y,q.z,q.w},Scale=new[]{s.x,s.y,s.z} } };
}
(NativeCreatureQuietCapture Capture,CreatureReplayFrame Frame) Hold(GameObject go,string kind)
{
    var capture=new NativeCreatureQuietCapture(go,kind,kind); var frame=Frame(go,kind);
    Check(capture.Due(0,null));capture.CompleteRead(frame);
    Check(capture.Due(.033,frame)&&capture.ClockMode==CreatureReplayRules.SleepingClock);capture.CompleteRead(frame);
    Check(!capture.Due(.066,frame)&&capture.Quiet);return(capture,frame);
}
Test("quiet sampling requires a successful final held baseline and failed reads cannot grant sleep",()=>
{
    var p=new NativeCreatureQuietPolicy();Check(p.Due(0,true,false));p.CompleteRead(false);Check(p.Due(.1,true,false));
    p.CompleteRead(true);Check(!p.Due(.2,true,false));Check(p.Due(.3,false,false)&&p.LastWasBoundary);p.CompleteRead(true);
    Check(p.Due(.4,true,false)&&p.LastWasBoundary);p.CompleteRead(true);Check(!p.Due(.5,true,false));
});
Test("audited static capybara holds immutable geometry without any periodic component directory scans",()=>
{
    var go=Capy();var (capture,frame)=Hold(go,"capybara");int scans=go.ComponentSearches;go.PoseReads=0;long safety=capture.QuietSafetyChecks;
    for(int i=0;i<1800;i++)Check(!capture.Due(1+i/30d,frame));
    Check(go.ComponentSearches==scans&&capture.ClockMode==CreatureReplayRules.SleepingClock);
    Check(capture.QuietSafetyChecks-safety<=81&&go.PoseReads<=243,"quiet safety must not read native roots 1800 times per minute");
});
Test("capybara Wobble enabling and renderer changes immediately wake real observations",()=>
{
    var go=Capy();var(capture,frame)=Hold(go,"capybara");var wobble=go.GetComponent<WobbleSpinBounce>()!;
    wobble.enabled=true;Check(capture.Due(1,frame)&&capture.LastWasBoundary);capture.CompleteRead(frame);
    for(int i=0;i<30;i++)Check(capture.Due(2+i/30d,frame));
    wobble.enabled=false;Check(capture.Due(3.1,frame));capture.CompleteRead(frame);
    Check(capture.Due(3.2,frame)&&capture.ClockMode==CreatureReplayRules.SleepingClock);capture.CompleteRead(frame);Check(!capture.Due(3.3,frame));
    go.GetComponent<Renderer>()!.enabled=false;Check(capture.Due(5,frame)&&capture.RequiresAppearanceRead);
});
Test("capybara unknown controller legacy animation body script and resource fail open",()=>
{
    foreach(int variant in Enumerable.Range(0,5))
    {
        var go=Capy();string resource="capybara";
        if(variant==0)go.Add(new Animator{enabled=false});if(variant==1)go.Add(new Animation{enabled=false});
        if(variant==2)go.Add(new Rigidbody());if(variant==3)go.Add(new UnknownVisualDriver{enabled=false});if(variant==4)resource="unknown";
        var capture=new NativeCreatureQuietCapture(go,"capybara",resource);var frame=Frame(go,"capybara");
        capture.Due(0,null);capture.CompleteRead(frame);for(int i=0;i<10;i++)Check(capture.Due(1+i/30d,frame));
    }
});
Test("held root checks compare against the published pose so cumulative motion and reparenting cannot disappear",()=>
{
    var go=Capy();var(capture,frame)=Hold(go,"capybara");go.transform.position=new(.00008f,0,0);Check(!capture.Due(1,frame));
    go.transform.position=new(.00016f,0,0);Check(capture.Due(2.1,frame)&&capture.LastWasBoundary);
    go.transform.position=new(0,0,0);go.transform.parent=new GameObject("new parent").transform;Check(capture.Due(3.2,frame));
});
Test("native enabled Venus idle never sleeps because its ten streamed channels actually animate",()=>
{
    var go=Venus();go.GetComponent<Animator>()!.enabled=true;var capture=new NativeCreatureQuietCapture(go,"venus-flytrap","venus-flytrap");
    var frame=Frame(go,"venus-flytrap");capture.Due(0,null);capture.CompleteRead(frame);go.PoseReads=0;
    var trap=go.GetComponent<VenusFlyTrap>()!.trapRoot.gameObject;trap.PoseReads=0;
    int scans=go.ComponentSearches;
    for(int i=0;i<300;i++){Check(capture.Due(1+i/30d,frame));capture.CompleteRead(frame);}
    Check(go.ComponentSearches==scans&&capture.ClockMode==CreatureReplayRules.ActiveClock&&go.PoseReads==0&&trap.PoseReads==0&&capture.QuietSafetyChecks==0);
});
Test("native disabled audited idle Venus may hold but target munching and Biting each wake it",()=>
{
    foreach(int signal in Enumerable.Range(0,3))
    {
        var go=Venus();var(capture,frame)=Hold(go,"venus-flytrap");var venus=go.GetComponent<VenusFlyTrap>()!;
        if(signal==0)venus.NativeState(new Character(),false);if(signal==1)venus.NativeState(null,true);if(signal==2)venus.anim.Biting=true;
        Check(capture.Due(signal==2?1.2:.067,frame)&&capture.LastWasBoundary&&capture.ClockMode==CreatureReplayRules.ActiveClock);
    }
});
Test("Venus state transitions controller replacement and enable changes retain full observation",()=>
{
    foreach(int signal in Enumerable.Range(0,5))
    {
        var go=Venus();var(capture,frame)=Hold(go,"venus-flytrap");var anim=go.GetComponent<Animator>()!;
        if(signal==0)anim.Transition=true;if(signal==1)anim.State=145476722;if(signal==2)anim.runtimeAnimatorController=new();
        if(signal==3)anim.enabled=true;if(signal==4)anim.isInitialized=false;
        Check(capture.Due(signal==3?.067:1.2,frame));
    }
});
Test("LetGo protects its half-second root tween for one fixed second and sampling cannot renew the tail",()=>
{
    var go=Venus();var(capture,frame)=Hold(go,"venus-flytrap");capture.NotifyNativeEvent(10);
    for(int i=0;i<30;i++){Check(capture.Due(10+i/30d,frame));capture.CompleteRead(frame);}
    Check(capture.Due(11,frame)&&capture.ClockMode==CreatureReplayRules.SleepingClock);capture.CompleteRead(frame);
    Check(!capture.Due(11.033,frame));
});
Test("Venus trap rotation root replacement and trap parent changes cannot be hidden by a stable main root",()=>
{
    foreach(int signal in Enumerable.Range(0,3))
    {
        var go=Venus();var(capture,frame)=Hold(go,"venus-flytrap");var venus=go.GetComponent<VenusFlyTrap>()!;
        if(signal==0)venus.trapRoot.rotation=new(0,1,0,0);if(signal==1)venus.trapRoot=new GameObject("replacement").transform;
        if(signal==2)venus.trapRoot.parent=new GameObject("new parent").transform;Check(capture.Due(1.2,frame));
    }
});
Test("Venus altered clips events behaviours and unknown visual components reject quiet optimization",()=>
{
    foreach(int signal in Enumerable.Range(0,4))
    {
        var go=Venus();var anim=go.GetComponent<Animator>()!;
        if(signal==0)anim.runtimeAnimatorController!.animationClips[0].length=2;if(signal==1)anim.runtimeAnimatorController!.animationClips[0].events=new object[]{new()};
        if(signal==2)anim.Behaviours=new[]{new StateMachineBehaviour()};if(signal==3)go.Add(new UnknownVisualDriver());
        var capture=new NativeCreatureQuietCapture(go,"venus-flytrap","venus-flytrap");var frame=Frame(go,"venus-flytrap");
        capture.Due(0,null);capture.CompleteRead(frame);Check(capture.Due(1,frame));
    }
});
Test("appearance failure forced refresh nonfinite pose and inactive root cannot reuse a supposedly complete checkpoint",()=>
{
    var go=Capy();var(capture,frame)=Hold(go,"capybara");capture.CompleteRead(frame,false);Check(capture.Due(1,frame));
    capture.CompleteRead(frame);Check(capture.Due(2,frame,true));capture.CompleteRead(frame);
    go.transform.position=new(float.NaN,0,0);Check(capture.Due(3,frame));go.transform.position=new(0,0,0);
    go.activeSelf=false;Check(capture.Due(4,frame));
});
Test("disabled quiet Venus polls root and Animator safety at a staggered rate below 81 checks per minute",()=>
{
    var go=Venus();var(capture,frame)=Hold(go,"venus-flytrap");var venus=go.GetComponent<VenusFlyTrap>()!;
    var animator=venus.anim;go.PoseReads=0;venus.trapRoot.gameObject.PoseReads=0;animator.StateReads=animator.BoolReads=0;
    long baseline=capture.QuietSafetyChecks;
    for(int i=0;i<1800;i++)Check(!capture.Due(1+i/30d,frame));
    Check(capture.QuietSafetyChecks-baseline<=81&&go.PoseReads<=243&&venus.trapRoot.gameObject.PoseReads<=243&&animator.StateReads<=81&&animator.BoolReads<=81);
});
Console.WriteLine($"TOTAL: {passed} native quiet sampling contracts passed. Actual Harmony private field bindings are exercised; Unity rendering is not simulated.");
