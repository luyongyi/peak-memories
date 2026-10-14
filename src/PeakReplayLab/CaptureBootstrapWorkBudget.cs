using System;
using System.Diagnostics;

namespace PeakReplayLab;

// One cooperative budget is shared by the scope and first reads of every
// track. A single native read cannot be preempted. Real event reads and normal
// samples do not use this gate; it only defers missing initial baselines.
internal static class CaptureBootstrapWorkBudget
{
    private sealed class Budget
    {
        internal readonly Func<long> Clock;
        internal readonly long Start;
        internal readonly double Ticks;
        internal readonly bool InitialOnly;
        internal Budget(double milliseconds, Func<long> clock, long frequency, bool initialOnly)
        { Clock = clock; Start = clock(); Ticks = milliseconds * frequency / 1000d; InitialOnly = initialOnly; }
        internal bool HasTime => Clock() - (double)Start < Ticks;
    }
    private sealed class Lease : IDisposable
    {
        private readonly Budget budget;
        private readonly bool owner;
        private bool disposed;
        internal Lease(Budget budget, bool owner) { this.budget = budget; this.owner = owner; }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (owner && ReferenceEquals(current, budget)) current = null;
        }
    }
    private static Budget? current;
    private static readonly Func<long> stopwatch = Stopwatch.GetTimestamp;
    internal static bool Active => current != null;
    internal static bool InitialOnly => current?.InitialOnly ?? false;
    internal static bool HasTime => current?.HasTime ?? true;
    internal static bool TryBeginRead() => HasTime;
    internal static IDisposable Begin(double milliseconds = 2, bool initialOnly = false) => Begin(milliseconds, stopwatch, Stopwatch.Frequency, initialOnly);
    internal static IDisposable Begin(double milliseconds, Func<long> clock, long frequency, bool initialOnly = false)
    {
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds <= 0 || clock == null || frequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        // Nested preparation never acquires another two milliseconds in the
        // same rendered frame or overwrites its caller's start timestamp.
        if (current != null) return new Lease(current, false);
        var budget = new Budget(milliseconds, clock, frequency, initialOnly); current = budget;
        return new Lease(budget, true);
    }
}
