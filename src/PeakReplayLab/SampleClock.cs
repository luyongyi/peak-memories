using System;

namespace PeakReplayLab;

/// <summary>
/// Selects real rendered frames on a fixed-rate timeline. It never invents catch-up
/// samples and never modifies the game's frame rate or physics clock.
/// </summary>
public sealed class SampleClock
{
    private readonly int hz;
    private bool started;
    private double origin, lastObserved, lastCaptured;
    private long nextSlot;

    public long Captured { get; private set; }
    /// <summary>Target-rate slots with no real captured frame, including stalls.</summary>
    public long Skipped { get; private set; }
    /// <summary>Actual samples per game-time second since the first capture.</summary>
    public double ObservedRate => Captured < 2 || lastCaptured <= origin ? 0 : (Captured - 1) / (lastCaptured - origin);

    public SampleClock(int hz)
    {
        if (hz < 1 || hz > 1000) throw new ArgumentOutOfRangeException(nameof(hz));
        this.hz = hz;
    }

    public bool ShouldCapture(double now)
    {
        if (double.IsNaN(now) || double.IsInfinity(now) || now < 0)
            throw new ArgumentOutOfRangeException(nameof(now), "Capture requires finite, nonnegative game time.");
        if (!started)
        {
            started = true;
            origin = lastObserved = lastCaptured = now;
            nextSlot = 1;
            Captured = 1;
            return true;
        }

        // Pauses, a second caller in the same frame, and a backwards clock must not
        // create duplicate/non-monotonic recording timestamps. A new scene should Reset.
        if (now <= lastObserved) return false;
        double elapsedSlots = (now - origin) * hz;
        if (double.IsInfinity(elapsedSlots) || elapsedSlots >= long.MaxValue - 4096d)
            throw new ArgumentOutOfRangeException(nameof(now), "Capture clock exceeds supported duration.");
        lastObserved = now;

        // This epsilon only absorbs double arithmetic noise, not a fraction of a
        // rendered frame. Absolute-clock cancellation grows after long game sessions.
        double toleranceSlots = Math.Max(0.000001d, Math.Max(Math.Abs(now), Math.Abs(origin)) * hz * 1e-14d);
        toleranceSlots = Math.Min(toleranceSlots, 0.001d);
        if (elapsedSlots + toleranceSlots < nextSlot) return false;

        long dueSlot = (long)Math.Floor(elapsedSlots + toleranceSlots);
        Skipped += dueSlot - nextSlot;
        nextSlot = dueSlot + 1;
        lastCaptured = now;
        Captured++;
        return true;
    }

    public void Reset()
    {
        started = false;
        origin = lastObserved = lastCaptured = 0;
        nextSlot = 0;
        Captured = Skipped = 0;
    }
}
