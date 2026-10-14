namespace PeakReplayLab;

internal static class CreatureReplayRules {internal const int ActiveClock=0,IdleClock=1,SleepingClock=2;}

// Timeline indexing/cuts/independent pages are exercised with the production
// timeline in ReplayContract. This double supplies independent motion brackets
// to verify ItemPlayback does not use global frame clocks or old metadata.
internal sealed class ItemObservationTimeline
{
    internal ItemFrame Left=null!,Right=null!;
    internal int Queries;
    internal bool TrySample(string key,double time,out ItemFrame left,out ItemFrame right,out float mix)
    {
        Queries++;left=Left;right=Right;mix=0;
        if(left==null||right==null||left.Key!=key)return false;
        mix=right.SampleTime==left.SampleTime?0:(float)Math.Clamp((time-left.SampleTime)/(right.SampleTime-left.SampleTime),0,1);
        return true;
    }
}

internal sealed class EarlyWormAnimationRuntime : IDisposable
{
    internal static readonly List<EarlyWormAnimationRuntime> Created=new();
    internal static bool CanCreate=true;
    internal readonly List<double> Times=new();
    internal bool Disposed;
    private bool sampled;private double time;
    internal static bool TryCreate(VisualReplica visual,Item source,out EarlyWormAnimationRuntime? runtime)
    {runtime=null;if(!CanCreate)return false;runtime=new();Created.Add(runtime);return true;}
    internal void Apply(CreatureIdleAnimation frame,double replayTime)
    {if(Disposed||sampled&&time==replayTime)return;sampled=true;time=replayTime;Times.Add(replayTime);}
    internal void Invalidate()=>sampled=false;
    public void Dispose()=>Disposed=true;
}
