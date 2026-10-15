using PeakReplayLab;

internal static class ScaleAppliedObserverTests
{
    internal static void Run(Action<string,Action> test, Func<ItemEventCapture> observe)
    {
        test("applied scale observes native child writes once and 1800 static Updates resolve the owner only once",()=>
        {
            using var observer=observe();var item=new Item();var scaler=new ItemScaleSyncer{ItemOwner=item};
            ItemCapture.OnNotify=(kind,source)=>Check(kind=="child-scale"&&ReferenceEquals(source,item)&&scaler.Mesh.localScale.x==scaler.currentScale&&scaler.NativeCalls>0);
            scaler.Update();for(int i=0;i<1800;i++)scaler.Update();
            Check(scaler.NativeCalls==1801&&scaler.ComponentLookups==1&&ItemCapture.Notices.Count==1);
        });
        test("real sub-RPC growth and backpack half scale produce only child observations without material inventory or motion events",()=>
        {
            using var observer=observe();var item=new Item();var scaler=new ItemScaleSyncer{ItemOwner=item};
            scaler.Update();scaler.currentScale=1.000001f;scaler.Update();scaler.currentScale=1.000002f;scaler.Update();
            scaler.currentScale=2;scaler.Update();scaler.Update(true);scaler.Update(true);scaler.Update(false);
            Check(ItemCapture.Notices.Count==6&&ItemCapture.Notices.All(n=>n.Kind=="child-scale"&&ReferenceEquals(n.Item,item)));
            Check(scaler.currentScale==2&&scaler.Mesh.localScale.x==2&&scaler.ComponentLookups==1&&ItemEventCapture.Drain(10).Length==0);
        });
        test("negative and nonfinite applied scale are ignored without poisoning later finite observations",()=>
        {
            using var observer=observe();var item=new Item();var scaler=new ItemScaleSyncer{ItemOwner=item};
            foreach(float value in new[]{-1f,float.NaN,float.PositiveInfinity,float.NegativeInfinity}){scaler.currentScale=value;scaler.Update();}
            Check(ItemCapture.Notices.Count==0&&scaler.ComponentLookups==0&&scaler.NativeCalls==4);
            scaler.currentScale=0;scaler.Update();scaler.Update();scaler.currentScale=1;scaler.Update();
            Check(ItemCapture.Notices.Count==2&&scaler.ComponentLookups==1&&scaler.Mesh.localScale.x==1);
        });
        test("presentation applied scale keeps original native behavior while emitting no observation or owner query",()=>
        {
            using var observer=observe();var item=new Item();var scaler=new ItemScaleSyncer{ItemOwner=item};ReplaySafety.Active=true;
            scaler.currentScale=3;scaler.Update(true);Check(scaler.NativeCalls==1&&scaler.Mesh.localScale.x==1.5f&&ItemCapture.Notices.Count==0&&scaler.ComponentLookups==0);
            ReplaySafety.Active=false;scaler.Update(true);Check(ItemCapture.Notices.Count==1&&scaler.ComponentLookups==1);
            ReplaySafety.Active=true;scaler.currentScale=4;scaler.Update();ReplaySafety.Active=false;scaler.Update();
            Check(ItemCapture.Notices.Count==2&&scaler.Mesh.localScale.x==4);
        });
        test("native ApplyScale exceptions propagate unchanged and failed writes cannot consume a scale change",()=>
        {
            using var observer=observe();var scaler=new ItemScaleSyncer{ItemOwner=new Item()};scaler.Update();
            var failure=new InvalidOperationException("native-scale-fixture");scaler.NativeFailure=failure;scaler.currentScale=2;
            Exception? observed=null;try{scaler.Update();}catch(Exception error){observed=error;}
            Check(ReferenceEquals(observed,failure)&&ItemCapture.Notices.Count==1&&scaler.Mesh.localScale.x==1);
            scaler.NativeFailure=null;scaler.Update();Check(ItemCapture.Notices.Count==2&&scaler.Mesh.localScale.x==2);
        });
        test("disposing scale observer removes Harmony injection without changing future native scale writes",()=>
        {
            var observer=observe();var scaler=new ItemScaleSyncer{ItemOwner=new Item()};scaler.Update();observer.Dispose();
            scaler.currentScale=5;scaler.Update();Check(ItemCapture.Notices.Count==1&&scaler.NativeCalls==2&&scaler.Mesh.localScale.x==5);
        });
    }
    private static void Check(bool value) { if(!value)throw new Exception("Scale applied observer contract failed."); }
}
