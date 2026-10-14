using System.Runtime.CompilerServices;
using System.Reflection;
using PeakReplayLab;

internal static class ReplayTrackSharedBudgetTests
{
    private sealed class Child
    {
        public override bool Equals(object? other) => other is Child;
        public override int GetHashCode() => 1;
    }
    private sealed class State
    {
        internal long Own;
        internal (object Value,long Bytes)[] Children=Array.Empty<(object,long)>();
    }
    private static void Check(bool value){if(!value)throw new Exception("Shared track budget assertion failed.");}
    private static void Visit(State state,Action<object,long> visit){foreach(var child in state.Children)visit(child.Value,child.Bytes);}
    private static ReplayTrackBudget<EffectReplayFrame> Budget() => new(EffectReplayRules.OwnEstimate,EffectReplayRules.VisitShared);
    internal static void Run(Action<string,Action> test)
    {
        test("shared track children use reference identity and repeated edges release symmetrically",()=>
        {
            var a=new Child();var b=new Child();var one=new State{Own=10,Children=new[]{((object)a,100L),((object)a,100L),((object)b,200L)}};
            var two=new State{Own=20,Children=new[]{((object)a,100L)}};var budget=new ReplayTrackBudget<State>(x=>x.Own,Visit);var first=new[]{one};var second=new[]{two};
            Check(budget.Acquire(first)==410&&budget.Bytes==310);Check(budget.Acquire(second)==120&&budget.Bytes==330);
            budget.Release(first);Check(budget.Bytes==120);budget.Release(second);Check(budget.Bytes==0);
        });
        test("unchanged generic tracks retain their original state and array budget behavior",()=>
        {
            var one=new State{Own=10};var two=new State{Own=20};var budget=new ReplayTrackBudget<State>(x=>x.Own);var first=new[]{one};var second=new[]{one,two};
            Check(budget.Acquire(first)==10&&budget.Acquire(first)==10&&budget.Bytes==10);Check(budget.Acquire(second)==30&&budget.Bytes==30);
            budget.Release(first);Check(budget.Bytes==30);budget.Release(second);Check(budget.Bytes==10);budget.Release(first);Check(budget.Bytes==0);
        });
        test("same effect checkpoints and initial evidence are charged once across arrays and changing frames",()=>
        {
            var cp=Checkpoint(1024);var initial=Initial(cp);var one=Effect(cp,initial);var two=Effect(cp,initial);var first=new[]{one};var second=new[]{two};var mixed=new[]{one,two};var budget=Budget();
            Check(budget.Acquire(first)==EffectReplayRules.Estimate(one));long unique=budget.Bytes;
            Check(budget.Acquire(second)==EffectReplayRules.Estimate(two)&&budget.Bytes==unique+EffectReplayRules.OwnEstimate(two));
            long both=budget.Bytes;budget.Acquire(mixed);budget.Acquire(first);Check(budget.Bytes==both);
            budget.Release(first);budget.Release(second);Check(budget.Bytes==both);budget.Release(mixed);Check(budget.Bytes==unique);budget.Release(first);Check(budget.Bytes==0);
        });
        test("checkpoint replacement releases old payload as soon as its final live frame expires",()=>
        {
            var old=Checkpoint(1024);var next=Checkpoint(2048);var one=Effect(old);var two=Effect(next);var first=new[]{one};var second=new[]{two};var budget=Budget();budget.Acquire(first);budget.Acquire(second);budget.Release(first);
            Check(budget.Bytes==EffectReplayRules.OwnEstimate(two)+EffectParticleCheckpointRules.Estimate(next));budget.Release(second);Check(budget.Bytes==0);
        });
        test("initial evidence legitimately keeps its birth checkpoint until its last live reference",()=>
        {
            var birth=Checkpoint(1024);var current=Checkpoint(2048);var initial=Initial(birth);var one=Effect(birth,initial);var two=Effect(current,initial);var budget=Budget();var first=new[]{one};var second=new[]{two};
            budget.Acquire(first);long before=budget.Bytes;budget.Acquire(second);budget.Release(first);
            Check(budget.Bytes==before-EffectReplayRules.OwnEstimate(one)+EffectReplayRules.OwnEstimate(two)+EffectParticleCheckpointRules.Estimate(current));budget.Release(second);Check(budget.Bytes==0);
        });
        test("retaining the same frame array does not enumerate children or increment child ownership again",()=>
        {
            var state=new State{Own=10,Children=new[]{((object)new Child(),100L)}};int visits=0;var budget=new ReplayTrackBudget<State>(x=>x.Own,(value,visitor)=>{visits++;Visit(value,visitor);});var array=new[]{state};
            budget.Acquire(array);for(int i=0;i<1000;i++)budget.Acquire(array);Check(visits==1&&budget.Bytes==110);
            for(int i=0;i<1000;i++)budget.Release(array);Check(visits==1&&budget.Bytes==110);budget.Release(array);Check(visits==2&&budget.Bytes==0);
        });
        test("effect shared visitor and tracker allocate no per-acquire release callback wrappers",()=>
        {
            var cp=Checkpoint(1024);var array=new[]{Effect(cp,Initial(cp))};var budget=Budget();for(int i=0;i<1000;i++){budget.Acquire(array);budget.Release(array);}
            long before=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<10000;i++){budget.Acquire(array);budget.Release(array);}
            Check(GC.GetAllocatedBytesForCurrentThread()==before&&budget.Bytes==0);
        });
        test("released checkpoint initial state and frame are not retained by rolling budget bookkeeping",()=>
        {
            var budget=Budget();var refs=ReleasedReferences(budget);Collect();Check(refs.All(value=>!value.IsAlive)&&budget.Bytes==0);GC.KeepAlive(budget);
        });
        test("two minutes of 30Hz moving effects do not multiply their shared megabyte checkpoint",()=>
        {
            const int count=3601;var cp=Checkpoint(16384);var initial=Initial(cp);var buffer=new RollingBuffer(16L*1024*1024);long expected=0,shared=0;
            for(int i=0;i<count;i++)
            {
                var effect=Effect(cp,initial);effect.Position[0]=i;var frame=new ReplayFrame{T=i/30d,Effects=new[]{effect}};buffer.Add(frame);
                expected+=RollingBuffer.EstimateFrame(frame)+EffectReplayRules.OwnEstimate(effect);
                if(i==0){shared=buffer.EstimatedBytes-expected;Check(shared>EffectParticleCheckpointRules.Estimate(cp));}
            }
            Check(buffer.Count==count&&!buffer.MemoryLimited&&buffer.EstimatedBytes==expected+shared&&buffer.EstimatedBytes<16L*1024*1024);
            Check(buffer.LastFrameEstimatedBytes>2L*1024*1024);var empty=new ReplayFrame{T=241};buffer.Add(empty);
            var effects=(ReplayTrackBudget<EffectReplayFrame>)typeof(RollingBuffer).GetField("effects",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(buffer)!;
            Check(buffer.Count==1&&buffer.EstimatedBytes==RollingBuffer.EstimateFrame(empty)&&effects.Bytes==0);
        });
        test("rejected oversized isolated frame rolls back every shared ownership edge",()=>
        {
            var cp=Checkpoint(1024);var frame=new ReplayFrame{T=0,Effects=new[]{Effect(cp,Initial(cp))}};var buffer=new RollingBuffer(1024);bool rejected=false;
            try{buffer.Add(frame);}catch(InvalidOperationException){rejected=true;}
            var effects=(ReplayTrackBudget<EffectReplayFrame>)typeof(RollingBuffer).GetField("effects",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(buffer)!;
            Check(rejected&&buffer.Count==0&&buffer.EstimatedBytes==0&&effects.Bytes==0);buffer.Add(new ReplayFrame{T=0});Check(buffer.Count==1);
        });
    }
    private static EffectParticleCheckpoint Checkpoint(int count)
    {
        var checkpoint=new EffectParticleCheckpoint{ObservedAt=0,RootPhase=0,Layout="audited-test-layout",Count=count,State=new byte[32],Data=new byte[count*64]};
        checkpoint.Digest=EffectParticleCheckpointRules.Digest(checkpoint);return checkpoint;
    }
    private static EffectParticleFrame[] Particles(EffectParticleCheckpoint checkpoint) => new[]{new EffectParticleFrame{Colors=new float[8],Parameters=new float[14],Checkpoint=checkpoint}};
    private static EffectReplayInitialState Initial(EffectParticleCheckpoint checkpoint) => new(){ObservedAt=0,UntilTime=.01,Seed=1,Seeds=new long[]{1},Particles=Particles(checkpoint)};
    private static EffectReplayFrame Effect(EffectParticleCheckpoint checkpoint,EffectReplayInitialState? initial=null) => new(){Key="event-particle:test:1:1",Resource="Particles/Test",Seed=1,Seeds=new long[]{1},Particles=Particles(checkpoint),InitialState=initial};
    [MethodImpl(MethodImplOptions.NoInlining)] private static WeakReference[] ReleasedReferences(ReplayTrackBudget<EffectReplayFrame> budget)
    {
        var cp=Checkpoint(1024);var initial=Initial(cp);var frame=Effect(cp,initial);var array=new[]{frame};budget.Acquire(array);budget.Release(array);
        return new[]{new WeakReference(cp),new WeakReference(initial),new WeakReference(frame),new WeakReference(array)};
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Collect(){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
}
