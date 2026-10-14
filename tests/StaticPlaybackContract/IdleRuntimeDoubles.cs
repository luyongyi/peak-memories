namespace PeakReplayLab;

// These doubles expose the production playback orchestration. Native clip
// selection/sampling and RopeRender are tested separately in their contracts.
internal sealed class CreatureIdleAnimationRuntime : IDisposable
{
    public static readonly List<CreatureIdleAnimationRuntime> Created = new();
    public static bool Unsupported;
    public static int Attempts;
    private readonly VisualReplica visual;
    private bool sampled;
    private double sampledTime;
    public int ApplyCalls, Samples, Invalidations;
    public double LastReplayTime;
    public bool Disposed;
    private CreatureIdleAnimationRuntime(VisualReplica visual) { this.visual = visual; Created.Add(this); }
    public static bool TryCreate(VisualReplica visual, UnityEngine.GameObject source, CreatureIdleAnimation animation, out CreatureIdleAnimationRuntime? runtime)
    {
        Attempts++; runtime = null;
        if (Unsupported || !visual.IsReady || animation.Clip != CreatureIdleAnimationRules.SpiderIdle) return false;
        runtime = new CreatureIdleAnimationRuntime(visual); return true;
    }
    public void Apply(CreatureIdleAnimation animation, double time)
    {
        if (Disposed) throw new InvalidOperationException("disposed animation reused");
        ApplyCalls++; LastReplayTime = time;
        double phase = CreatureIdleAnimationRules.SampleTime(animation,time);
        if (sampled && sampledTime == phase) return;
        Samples++; sampledTime = phase; sampled = true; visual.BoneMarker = 1000+phase;
    }
    public void Invalidate() { sampled = false; Invalidations++; }
    public void Dispose() { Disposed = true; sampled = false; }
    public static void Reset() { Created.Clear(); Unsupported = false; Attempts = 0; }
}

internal sealed class SpiderIdleLinePlayback
{
    public static readonly List<SpiderIdleLinePlayback> Created = new();
    public static bool Unsupported;
    private readonly VisualReplica visual;
    private readonly UnityEngine.LineRenderer line;
    public int Calls, Accepted;
    public float LastPhase;
    private SpiderIdleLinePlayback(VisualReplica visual, UnityEngine.LineRenderer line)
    { this.visual = visual; this.line = line; Created.Add(this); }
    public static SpiderIdleLinePlayback? TryCreate(VisualReplica visual, UnityEngine.GameObject source, UnityEngine.LineRenderer? line) =>
        Unsupported || line == null || source.GetComponent<Spider>() == null ? null : new(visual,line);
    public bool TryApply(float phase)
    {
        Calls++;
        if (!line.enabled || !line.useWorldSpace || line.positionCount != 40) return false;
        Accepted++; LastPhase = phase;
        for (int i=0;i<40;i++) line.SetPosition(i,new UnityEngine.Vector3((float)visual.BoneMarker,i,phase));
        return true;
    }
    public static void Reset() { Created.Clear(); Unsupported = false; }
}
