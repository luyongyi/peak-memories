namespace PeakReplayLab;

public sealed class CreatureIdleAnimation { }
// Native clip validation/phase math are tested with the production runtime in
// FrogCaptureContract; this adapter verifies the ItemPlayback lifecycle.
internal sealed class FrogIdleAnimationRuntime : IDisposable
{
    internal static readonly List<FrogIdleAnimationRuntime> Created=new();
    internal static bool CanCreate=true;
    internal readonly List<double> Times=new();
    internal int Invalidates;
    internal bool Disposed;
    private bool sampled;
    private double time;
    internal static bool TryCreate(VisualReplica visual,Item source,out FrogIdleAnimationRuntime? runtime)
    {runtime=null;if(!CanCreate)return false;runtime=new();Created.Add(runtime);return true;}
    internal void Apply(CreatureIdleAnimation frame,double replayTime)
    {if(Disposed||sampled&&time==replayTime)return;Times.Add(replayTime);time=replayTime;sampled=true;}
    internal void Invalidate(){Invalidates++;sampled=false;}
    public void Dispose(){Disposed=true;}
}
