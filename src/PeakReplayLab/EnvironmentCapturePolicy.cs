using System;

namespace PeakReplayLab;

[Flags]
internal enum EnvironmentDirectory
{
    None = 0, Winds = 1, RisingLava = 2, MovingLava = 4, Fog = 8,
    All = Winds | RisingLava | MovingLava | Fog,
}

// A scene catalogue is built once. Native lifecycle events enqueue specific
// sources; destroyed cached entries are removed without another scene search.
internal sealed class EnvironmentDiscoverySchedule
{
    private int scene = -1;
    private EnvironmentDirectory pending;

    public void Reset() { scene = -1; pending = EnvironmentDirectory.None; }
    public void Changed(EnvironmentDirectory family) => pending |= family;

    public EnvironmentDirectory Take(int currentScene, double now, EnvironmentDirectory invalid)
    {
        if (double.IsNaN(now) || double.IsInfinity(now)) throw new ArgumentOutOfRangeException(nameof(now));
        if (scene != currentScene)
        {
            scene = currentScene; pending = EnvironmentDirectory.None;
            return EnvironmentDirectory.All;
        }
        var due = invalid | pending; pending = EnvironmentDirectory.None;
        return due;
    }
}

// Native environment/audio parameters retain their 20 Hz phase when their
// caller runs at a different rate. Relative now + .05 deadlines turn a 30 Hz
// input into 15 Hz; fixed slots select real observations without catch-up reads.
internal struct NativeParameterSampleClock
{
    public const double Interval = .05;
    private bool known;
    private double origin, lastObserved, lastRead;
    private long nextSlot;
    public void Reset() => this = default;
    public bool Take(double now, bool edge = false)
    {
        if (double.IsNaN(now) || double.IsInfinity(now)) throw new ArgumentOutOfRangeException(nameof(now));
        if (!known || now < lastObserved)
        {
            known = true; origin = lastObserved = lastRead = now; nextSlot = 1;
            return true;
        }
        lastObserved = now;
        if (now == lastRead) return false;
        double slots = (now - origin) / Interval;
        if (double.IsInfinity(slots) || slots >= long.MaxValue - 4096d)
            throw new ArgumentOutOfRangeException(nameof(now), "Native parameter clock exceeds supported duration.");
        double tolerance = Math.Min(.001, Math.Max(.000001, Math.Max(Math.Abs(now), Math.Abs(origin)) * 20 * 1e-14));
        bool periodic = slots + tolerance >= nextSlot;
        if (!periodic && !edge) return false;
        if (periodic) nextSlot = (long)Math.Floor(slots + tolerance) + 1;
        lastRead = now;
        return true;
    }
}

internal sealed class EnvironmentSampleClock
{
    public const double Interval = NativeParameterSampleClock.Interval;
    private NativeParameterSampleClock clock;
    public void Reset() => clock.Reset();
    public bool Take(double now, bool edge) => clock.Take(now, edge);
}

internal struct AudioParameterSampleClock
{
    private NativeParameterSampleClock clock;
    public bool Take(double now, bool nativeStart = false)
    {
        bool due = clock.Take(now, nativeStart);
        // Two real native plays may begin at the same paused game timestamp.
        // They remain distinct voices; ordinary polling still deduplicates.
        return nativeStart || due;
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
