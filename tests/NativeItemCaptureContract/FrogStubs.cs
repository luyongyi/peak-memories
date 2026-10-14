namespace PeakReplayLab;

internal sealed class CreatureIdleAnimation { }
internal enum FrogIdleRejection
{
    None, Unsettled, Hooks, Fields, EventGrace, Ownership, Cooked, Pulling, Thrown,
    Water, UnknownWater, Tongue, Target, UnsupportedAnimation, HiddenVisibility,
    CurrentAnimation, Interrupted, SleepChanged, RootMotion, ClockChanged,
}
// The gate's native-field and native-clip behavior is tested by the independent
// FrogCaptureContract. This controllable adapter exercises ItemCapture's work set.
internal sealed class FrogCaptureActivity
{
    internal static bool Enabled, ForceRootWake;
    internal static FrogCaptureActivity? Last;
    internal bool Holding {get;private set;}
    internal long RootChecks {get;private set;}
    internal long Rejections(FrogIdleRejection reason) => 0;
    internal static FrogCaptureActivity? Create(Item item) => Enabled&&item.itemID==172?Last=new():null;
    internal bool TryHold(double now,ItemFrame frame,bool interrupted,bool safetyProbe)
    {
        if(!Holding)return false;
        if(safetyProbe)RootChecks++;
        if(interrupted||ForceRootWake){Holding=false;return false;}
        return true;
    }
    internal bool TryEnter(double now,ItemFrame frame,bool settled,out CreatureIdleAnimation? animation)
    {animation=settled?new():null;return Holding=Enabled&&settled&&!ForceRootWake;}
    internal void Release() => Holding=false;
}
