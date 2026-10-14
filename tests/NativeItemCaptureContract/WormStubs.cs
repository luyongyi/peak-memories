namespace PeakReplayLab;

// The actual clip/resource validation has its own EarlyWormAnimationContract.
// This controlled adapter exercises ItemCapture's root/appearance ownership.
internal sealed class EarlyWormAnimationCapture
{
    internal static bool Enabled;
    internal static EarlyWormAnimationCapture? Last;
    internal long ClockChecks,GeometrySkips,DescriptorChanges;
    private CreatureIdleAnimation? held;
    internal static bool TryCreate(Item item,out EarlyWormAnimationCapture? capture)
    {capture=Enabled&&item.itemID==184?Last=new():null;return capture!=null;}
    internal bool TryObserve(double now,bool force,out CreatureIdleAnimation? animation,out bool changed)
    {
        ClockChecks++;changed=held==null||force;
        if(changed){held=new();DescriptorChanges++;}else GeometrySkips++;
        animation=held;return true;
    }
    internal void Invalidate() { }
}
