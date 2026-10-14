using UnityEngine;

namespace PeakReplayLab;

// Spider's RopeRender is a serializable, side-effect-free visual equation;
// it is not a MonoBehaviour. Its time argument is Clamp01(sinceGrab), not
// Time.time. Reuse that native equation on an inert clone after sampling the
// native idle clip so the silk endpoint follows the animated abdomen.
internal sealed class SpiderIdleLinePlayback
{
    private const int NativePointCount = 40;
    private readonly Transform from, to;
    private readonly LineRenderer line;
    private readonly RopeRender equation;

    private SpiderIdleLinePlayback(Transform from, Transform to, LineRenderer line, RopeRender equation)
    { this.from = from; this.to = to; this.line = line; this.equation = equation; }

    public static SpiderIdleLinePlayback? TryCreate(VisualReplica visual, GameObject native, LineRenderer? target)
    {
        if (!native || !target) return null;
        var spider = native.GetComponent<Spider>();
        if (spider == null || !spider || !spider.startPoint || !spider.spiderButt || !spider.line ||
            !spider.line!.useWorldSpace || spider.ropeRender == null ||
            spider.ropeRender.wobbleCurve == null || spider.ropeRender.wobbleOverLineCurve == null ||
            !visual.TryCloneTransform(spider.startPoint!, out var from) ||
            !visual.TryCloneTransform(spider.spiderButt!, out var to)) return null;

        // Copy authored parameters once. No native Spider component, Animator,
        // RPC, coroutine or physics step is run during replay.
        var source = spider.ropeRender;
        var equation = new RopeRender
        {
            wobble = source.wobble,
            scrollSpeed = source.scrollSpeed,
            scale = source.scale,
            wobbleCurve = Copy(source.wobbleCurve),
            wobbleOverLineCurve = Copy(source.wobbleOverLineCurve),
        };
        return new SpiderIdleLinePlayback(from, to, target!, equation);
    }

    // Call after the recorded line visibility/width and the idle bone clip.
    // A missing/changed native template keeps the sampled-line fallback.
    public bool TryApply(float ropePhase)
    {
        if (!from || !to || !line || !line.enabled || !line.useWorldSpace ||
            line.positionCount != NativePointCount || float.IsNaN(ropePhase) ||
            float.IsInfinity(ropePhase) || ropePhase < 0f || ropePhase > 1f) return false;
        equation.DisplayRope(from.position, to.position, ropePhase, line);
        return true;
    }

    private static AnimationCurve Copy(AnimationCurve source) => new(source.keys)
    { preWrapMode = source.preWrapMode, postWrapMode = source.postWrapMode };
}
