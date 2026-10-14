using System;

namespace PeakReplayLab;

// One periodic budget for every non-player track. Events are queued separately
// by their native callbacks; this clock never manufactures catch-up snapshots.
internal sealed class SceneCaptureSchedule
{
    public const int MaximumHz = 30;
    private readonly SampleClock clock = new(MaximumHz);
    private ReplayFrame? snapshot;
    public double ObservedHz => clock.ObservedRate;
    public long SkippedSlots => clock.Skipped;
    public long CaptureTicks { get; private set; }
    public long ReusedTicks { get; private set; }
    public bool Due(double time) => clock.ShouldCapture(time);
    public void Observed(ReplayFrame frame)
    {
        frame.SceneSampleTimeKnown = true; frame.SceneSampleTime = frame.T;
        snapshot = frame; CaptureTicks++;
    }
    public void Reuse(ReplayFrame frame)
    {
        var scene = snapshot ?? throw new InvalidOperationException("Scene capture requires an initial observation.");
        frame.World = scene.World; frame.Items = scene.Items; frame.Crates = scene.Crates;
        frame.Ropes = scene.Ropes; frame.Effects = scene.Effects; frame.Audio = scene.Audio;
        frame.Spawned = scene.Spawned; frame.Balloons = scene.Balloons;
        frame.Creatures = scene.Creatures; frame.Interactions = scene.Interactions;
        frame.SceneSampleTimeKnown = true; frame.SceneSampleTime = scene.SceneSampleTime;
        ReusedTicks++;
    }
}
