using System;

namespace PeakReplayLab;

[Flags]
internal enum EnvironmentDirectory
{
    None = 0, Winds = 1, RisingLava = 2, MovingLava = 4, Fog = 8,
    All = Winds | RisingLava | MovingLava | Fog,
}

// Discovery is scene work, not frame work. Known destroyed sources are repaired
// immediately; new late-spawned sources have a staggered, bounded fallback.
internal sealed class EnvironmentDiscoverySchedule
{
    internal const double ProbeInterval = 2.5;
    private int scene = -1, family;
    private double nextProbe, lastTime = double.NaN;

    public void Reset() { scene = -1; family = 0; nextProbe = 0; lastTime = double.NaN; }

    public EnvironmentDirectory Take(int currentScene, double now, EnvironmentDirectory invalid)
    {
        if (double.IsNaN(now) || double.IsInfinity(now)) throw new ArgumentOutOfRangeException(nameof(now));
        if (scene != currentScene)
        {
            scene = currentScene; family = 0; lastTime = now; nextProbe = now + ProbeInterval;
            return EnvironmentDirectory.All;
        }
        if (now < lastTime)
        {
            // A clock rewind reschedules the fallback; it must not trigger all
            // four scene scans or a burst of missed probes.
            nextProbe = now + ProbeInterval;
        }
        lastTime = now;
        if (now < nextProbe) return invalid;
        nextProbe = now + ProbeInterval;
        var due = (EnvironmentDirectory)(1 << family);
        family = (family + 1) % 4;
        return invalid | due;
    }
}

internal sealed class EnvironmentSampleClock
{
    public const double Interval = .05;
    private double next, last = double.NaN;
    public void Reset() { next = 0; last = double.NaN; }
    public bool Take(double now, bool edge)
    {
        if (double.IsNaN(now) || double.IsInfinity(now)) throw new ArgumentOutOfRangeException(nameof(now));
        bool due = double.IsNaN(last) || now < last || now + .000001 >= next || edge;
        last = now;
        if (due) next = now + Interval;
        return due;
    }
}

// Recording snapshots are immutable. Allocate an array only when the first
// changed slot is found; unchanged and inactive environments reuse their array.
internal struct EnvironmentSnapshotArray<T> where T : class
{
    private readonly T[]? baseline;
    private readonly int count;
    private T[]? changed;

    public EnvironmentSnapshotArray(T[]? previous, int count)
    {
        this.count = count;
        baseline = previous != null && previous.Length == count ? previous : null;
        changed = null;
    }

    public void Set(int index, T value)
    {
        if (changed != null) { changed[index] = value; return; }
        if (baseline != null && ReferenceEquals(baseline[index], value)) return;
        changed = baseline != null ? (T[])baseline.Clone() : new T[count];
        changed[index] = value;
    }

    public T[] Build() => changed ?? baseline ?? Array.Empty<T>();
}
