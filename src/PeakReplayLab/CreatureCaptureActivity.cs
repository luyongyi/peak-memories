using HarmonyLib;

namespace PeakReplayLab;

// Cache components once at admission. No Animator inspection, Transform poll,
// distance search, or scene traversal is needed to distinguish native actions.
internal sealed class CreatureCaptureActivity
{
    private readonly Spider? spider;
    private readonly Mob? beetle;
    private readonly bool hasSpider, hasBeetle;
    private readonly CreatureCaptureLifecycle lifecycle;
    private bool idleVisualReady;
    private static readonly AccessTools.FieldRef<Spider, float> stunnedTime =
        AccessTools.FieldRefAccess<Spider, float>("_stunnedTime");
    public CreatureCaptureActivity(Spider? spider, Mob? beetle, bool followSceneClock = false)
    {
        this.spider = spider; this.beetle = beetle;
        hasSpider = !ReferenceEquals(spider, null); hasBeetle = !ReferenceEquals(beetle, null);
        lifecycle = new CreatureCaptureLifecycle(hasSpider, hasBeetle, followSceneClock);
    }
    public bool Due(double now, bool active) => lifecycle.Due(now, active,
        hasBeetle && beetle!.sleeping, hasSpider ? (int)spider!.spiderState : 0,
        hasSpider && stunnedTime(spider!) > 0, idleVisualReady);
    public bool IdleCandidate => lifecycle.IdleCandidate;
    public void CompleteIdleVisual(double now, bool ready)
    { idleVisualReady = ready; lifecycle.CompleteIdleVisual(now, ready); }
    public void NotifyNativeEvent() => lifecycle.NotifyNativeEvent();
    public int ClockMode => lifecycle.ClockMode;
    public bool LastWasBoundary => lifecycle.LastWasBoundary;
}
