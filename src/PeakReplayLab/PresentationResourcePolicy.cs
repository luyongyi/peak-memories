using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PeakReplayLab;

// One budget belongs to the playback session. Creation and particle pre-roll
// share a wall-clock deadline, even when several tracks run in one render frame.
public sealed class PresentationWorkBudget
{
    public const int MaximumSteps = 192, MaximumCreations = 4, MaximumBuildSteps = 256;
    public const double MaximumMilliseconds = 4;
    private readonly Func<double> milliseconds;
    private int frame = -1, steps, creations, buildSteps;
    private double started;
    private bool running;
    public PresentationWorkBudget(Func<double>? milliseconds = null)
    { this.milliseconds = milliseconds ?? TimestampMilliseconds; }
    private static double TimestampMilliseconds() => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
    public void BeginFrame(int value)
    {
        if (frame == value) return;
        frame = value; steps = creations = buildSteps = 0; running = false;
    }
    private bool Available()
    {
        double now = milliseconds();
        if (!running) { started = now; running = true; }
        return now - started < MaximumMilliseconds;
    }
    public bool Create()
    { if (creations >= MaximumCreations || !Available()) return false; creations++; return true; }
    public bool Step()
    { if (steps >= MaximumSteps || !Available()) return false; steps++; return true; }
    // A complex replica consumes small main-thread operations, rather than one
    // admission followed by an unbounded hierarchy/renderer construction.
    public bool Build()
    { if (buildSteps >= MaximumBuildSteps || !Available()) return false; buildSteps++; return true; }
}

// A native resource incompatibility is not transient. Retrying the same failed
// particle prefab every second created a periodic main-thread allocation spike.
public sealed class PresentationResourceFailures
{
    // A healthy effect asks on every rendered frame. Keep resource and clip as
    // their existing strings instead of allocating a joined identity each time.
    private readonly HashSet<(string Resource, string Clip)> failed = new();
    private readonly int maximum;
    public PresentationResourceFailures(int maximum = 4096)
    { if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum)); this.maximum = maximum; }
    public bool Allows(string key) => Allows(key, "");
    public void Reject(string key) => Reject(key, "");
    public bool Allows(string resource, string clip) => failed.Count < maximum && !failed.Contains((resource, clip));
    public void Reject(string resource, string clip) { if (failed.Count < maximum) failed.Add((resource, clip)); }
    public static bool CopySpriteSlot(bool spritesMode, bool hasSprite) => spritesMode && hasSprite;
}
