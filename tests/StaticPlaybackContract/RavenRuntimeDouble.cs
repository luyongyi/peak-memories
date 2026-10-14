namespace PeakReplayLab;

// Only isolates the real CreatureReplayPlayback orchestration. Real Raven clip
// signatures and sterile-clone sampling are covered by RavenAnimationContract.
internal sealed class RavenAnimationRuntime : IDisposable
{
    internal static readonly List<RavenAnimationRuntime> Created = new();
    private readonly VisualReplica visual;
    private CreatureIdleAnimation? sampledClock;
    private double sampledTime;
    internal int Samples;
    internal bool Disposed;
    private RavenAnimationRuntime(VisualReplica visual) { this.visual = visual; Created.Add(this); }
    internal static bool IsRaven(string clip) => clip == CreatureIdleAnimationRules.RavenIdle || clip == CreatureIdleAnimationRules.RavenFlyAway;
    internal static bool TryCreate(VisualReplica visual, UnityEngine.GameObject source, CreatureIdleAnimation animation, out RavenAnimationRuntime? runtime)
    { runtime = IsRaven(animation.Clip) && visual.IsReady ? new RavenAnimationRuntime(visual) : null; return runtime != null; }
    internal void Apply(CreatureIdleAnimation animation, double time)
    {
        double phase = CreatureIdleAnimationRules.SampleTime(animation, time);
        if (ReferenceEquals(sampledClock, animation) && sampledTime == phase) return;
        if (Disposed) throw new InvalidOperationException("Disposed raven runtime reused.");
        sampledClock = animation; sampledTime = phase; Samples++; visual.BoneMarker = 2000 + phase;
    }
    internal void Invalidate() => sampledClock = null;
    public void Dispose() { Disposed = true; sampledClock = null; }
}
