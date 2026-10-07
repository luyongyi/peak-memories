using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PeakReplayLab;

internal enum ReplayStage { Capture, Actors, World, Items, Crates, Ropes, Effects, Audio, Spawned, Balloons, Creatures, Validate, Buffer, Playback, PlaybackRopes, PlaybackEffects, PlaybackAudio, PlaybackSpawned, PlaybackBalloons, PlaybackCreatures, PlaybackEnvironment, SaveSnapshot, Enqueue, Appearance, Lights, StaticTraps, DynamicCreatures, Environment, Discovery }

// Main-thread timings, not GPU time or whole-game FPS. Fixed storage; scopes do not
// allocate, and percentile sorting/string formatting happens only when requested.
internal static class ReplayPerformance
{
    private sealed class Meter
    {
        public readonly double[] Recent = new double[256];
        public readonly double[] Sorted = new double[256];
        public int Count, Cursor;
        public long Samples, Allocations;
        public double Total, Maximum;
        public void Add(double milliseconds, long bytes)
        {
            Recent[Cursor] = milliseconds; Cursor = (Cursor + 1) % Recent.Length;
            if (Count < Recent.Length) Count++;
            Samples++; Total += milliseconds; Maximum = Math.Max(Maximum, milliseconds);
            Allocations += Math.Max(0, bytes);
        }
        public double P95()
        {
            if (Count == 0) return 0;
            Array.Copy(Recent, Sorted, Count); Array.Sort(Sorted, 0, Count);
            return Sorted[Math.Max(0, (int)Math.Ceiling(Count * .95) - 1)];
        }
        public double P99()
        {
            if (Count == 0) return 0;
            Array.Copy(Recent, Sorted, Count); Array.Sort(Sorted, 0, Count);
            return Sorted[Math.Max(0, (int)Math.Ceiling(Count * .99) - 1)];
        }
    }

    private static readonly Meter[] meters = CreateMeters();
    public static bool Enabled { get; set; } = true;
    private static bool allocationCounterAvailable = true;
    private static readonly int[] gcAtReset = { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
    private static Meter[] CreateMeters()
    {
        var result = new Meter[Enum.GetValues(typeof(ReplayStage)).Length];
        for (int i = 0; i < result.Length; i++) result[i] = new Meter();
        return result;
    }
    public readonly struct Scope : IDisposable
    {
        private readonly ReplayStage stage;
        private readonly long start, allocated;
        internal Scope(ReplayStage stage)
        {
            this.stage = stage; start = Enabled ? Stopwatch.GetTimestamp() : 0;
            allocated = start != 0 ? Allocated() : 0;
        }
        public void Dispose()
        {
            if (start == 0) return;
            long bytes = Allocated() - allocated;
            meters[(int)stage].Add((Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency, bytes);
        }
    }
    private static long Allocated()
    {
        if (!allocationCounterAvailable) return 0;
        try { return GC.GetAllocatedBytesForCurrentThread(); }
        catch (NotSupportedException) { allocationCounterAvailable = false; return 0; }
        catch (MissingMethodException) { allocationCounterAvailable = false; return 0; }
    }
    public static Scope Measure(ReplayStage stage) => new(stage);
    public static string Summary(bool playback)
    {
        var stage = playback ? ReplayStage.Playback : ReplayStage.Capture;
        var meter = meters[(int)stage];
        if (!Enabled || meter.Samples == 0) return "性能统计：尚无样本";
        return string.Format(CultureInfo.InvariantCulture, "{0}主线程 avg {1:F2} / p95 {2:F2} / max {3:F2} ms · 分配 {4:F1} KiB/次",
            playback ? "回放" : "采集", meter.Total / meter.Samples, meter.P95(), meter.Maximum,
            meter.Allocations / (double)meter.Samples / 1024);
    }
    public static string Report()
    {
        var text = new StringBuilder("Replay performance (CPU scopes, p95/p99=latest256; nested scopes overlap; allocations=thread bytes, not retained memory):");
        for (int i = 0; i < meters.Length; i++)
        {
            var m = meters[i]; if (m.Samples == 0) continue;
            text.AppendFormat(CultureInfo.InvariantCulture, " {0}[n={1},avg={2:F3},p95={3:F3},p99={4:F3},max={5:F3}ms,alloc={6:F0}B];",
                (ReplayStage)i, m.Samples, m.Total / m.Samples, m.P95(), m.P99(), m.Maximum, m.Allocations / (double)m.Samples);
        }
        if (!allocationCounterAvailable) text.Append(" allocation counter unavailable;");
        return text.ToString();
    }
    internal sealed class StageReport
    {
        public string Stage { get; set; } = "";
        public long Samples { get; set; }
        public double AverageMs { get; set; }
        public double RecentP95Ms { get; set; }
        public double RecentP99Ms { get; set; }
        public double MaximumMs { get; set; }
        public double AllocatedBytesPerSample { get; set; }
        public bool AllocationCounterAvailable { get; set; }
    }
    public static StageReport[] Snapshot()
    {
        var result = new StageReport[meters.Length];
        for (int i = 0; i < meters.Length; i++)
        {
            var m = meters[i];
            result[i] = new StageReport { Stage = ((ReplayStage)i).ToString(), Samples = m.Samples,
                AverageMs = m.Samples == 0 ? 0 : m.Total / m.Samples, RecentP95Ms = m.P95(), RecentP99Ms = m.P99(),
                MaximumMs = m.Maximum, AllocatedBytesPerSample = m.Samples == 0 ? 0 : m.Allocations / (double)m.Samples,
                AllocationCounterAvailable = allocationCounterAvailable };
        }
        return result;
    }
    public static int[] GarbageCollectionsSinceReset() => new[]
    { GC.CollectionCount(0) - gcAtReset[0], GC.CollectionCount(1) - gcAtReset[1], GC.CollectionCount(2) - gcAtReset[2] };
    public static void Reset()
    {
        foreach (var m in meters)
        { m.Count = m.Cursor = 0; m.Samples = m.Allocations = 0; m.Total = m.Maximum = 0; }
        for (int i = 0; i < gcAtReset.Length; i++) gcAtReset[i] = GC.CollectionCount(i);
    }
}
