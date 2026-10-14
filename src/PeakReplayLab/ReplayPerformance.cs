using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PeakReplayLab;

internal enum ReplayStage { Capture, Actors, World, Items, Crates, Ropes, Effects, Audio, Spawned, Balloons, Creatures, Validate, Buffer, Playback, PlaybackRopes, PlaybackEffects, PlaybackAudio, PlaybackSpawned, PlaybackBalloons, PlaybackCreatures, PlaybackEnvironment, SaveSnapshot, Enqueue, Appearance, Lights, StaticTraps, DynamicCreatures, Environment, Discovery, PlaybackSample, PlaybackWorld, PlaybackActors, PlaybackItems, PlaybackCrates, PlaybackCamera, PlaybackAudioEnforce, PlaybackAudioClock, PlaybackAudioApply, PlaybackAudioSeek, PlaybackAudioStart, PlaybackAudioStop, Interactions, PlaybackInteractions, PlaybackAudioPrepare, PlaybackWorldPrepare }

// Main-thread-only recorder. Stopwatch is calibrated to Unity realtime once at
// LateUpdate entry, without querying Unity or allocating for each measured call.
// Raw entries retain enums and values; reporting alone creates strings and DTOs.
internal sealed class ReplaySlowCpuDiagnostics
{
    internal const int Capacity = 128;
    internal const double MinimumMilliseconds = 1000d / 60;
    internal readonly struct Context
    {
        internal readonly bool Active, HasFrameContext;
        internal readonly long Epoch, StartedAt;
        internal readonly int ThreadId, StartFrame;
        internal readonly double StartRealtime;
        internal Context(long epoch, long startedAt, int threadId, bool hasFrameContext, int startFrame, double startRealtime)
        { Active = true; Epoch = epoch; StartedAt = startedAt; ThreadId = threadId;
          HasFrameContext = hasFrameContext; StartFrame = startFrame; StartRealtime = startRealtime; }
    }
    private struct RawEntry
    {
        internal ReplayStage Stage;
        internal bool HasFrameContext;
        internal int StartFrame, EndFrame, GlobalCollections;
        internal double StartRealtime, EndRealtime, Milliseconds;
    }
    internal sealed class Entry
    {
        public string Stage { get; set; } = "";
        public bool HasFrameContext { get; set; }
        public int StartFrame { get; set; }
        public int EndFrame { get; set; }
        public double StartRealtime { get; set; }
        public double EndRealtime { get; set; }
        public double Milliseconds { get; set; }
        public int GlobalCollections { get; set; }
    }
    private readonly RawEntry[] recent = new RawEntry[Capacity];
    private readonly long frequency;
    private long epoch, frameTicks;
    private int cursor, count, frame = -1, frameThread;
    private double frameRealtime;
    private bool enabled = true, hasFrameContext;
    public ReplaySlowCpuDiagnostics(long frequency)
    { if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency)); this.frequency = frequency; }
    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value) return;
            enabled = value; epoch++; hasFrameContext = false;
        }
    }
    public void SetFrameContext(int frame, double realtime, long ticks, int threadId)
    {
        if (!enabled) return;
        hasFrameContext = frame >= 0 && realtime >= 0 && !double.IsNaN(realtime) && !double.IsInfinity(realtime);
        if (!hasFrameContext) return;
        this.frame = frame; frameRealtime = realtime; frameTicks = ticks; frameThread = threadId;
    }
    public Context Begin(long ticks, int threadId)
    {
        if (!enabled || hasFrameContext && threadId != frameThread) return default;
        return new Context(epoch, ticks, threadId, hasFrameContext, hasFrameContext ? frame : -1,
            hasFrameContext ? frameRealtime + (ticks - frameTicks) / (double)frequency : -1);
    }
    public bool IsCurrent(in Context context) => enabled && context.Active && context.Epoch == epoch;
    public void End(ReplayStage stage, in Context context, long ticks, int collections, int threadId)
    {
        if (!IsCurrent(context) || threadId != context.ThreadId || ticks < context.StartedAt) return;
        double ms = (ticks - context.StartedAt) * 1000d / frequency;
        if (ms < MinimumMilliseconds) return;
        recent[cursor] = new RawEntry { Stage = stage, HasFrameContext = context.HasFrameContext,
            StartFrame = context.StartFrame, EndFrame = context.HasFrameContext && hasFrameContext ? frame : -1,
            StartRealtime = context.StartRealtime,
            EndRealtime = context.HasFrameContext ? context.StartRealtime + ms / 1000 : -1,
            Milliseconds = ms, GlobalCollections = Math.Max(0, collections) };
        cursor = (cursor + 1) % Capacity; count = Math.Min(Capacity, count + 1);
    }
    public Entry[] Snapshot()
    {
        var result = new Entry[count];
        for (int i = 0; i < count; i++)
        {
            var value = recent[(cursor - count + Capacity + i) % Capacity];
            result[i] = new Entry { Stage = value.Stage.ToString(), HasFrameContext = value.HasFrameContext,
                StartFrame = value.StartFrame, EndFrame = value.EndFrame, StartRealtime = value.StartRealtime,
                EndRealtime = value.EndRealtime, Milliseconds = value.Milliseconds, GlobalCollections = value.GlobalCollections };
        }
        return result;
    }
    public void Reset()
    {
        cursor = count = 0; epoch++;
        // Keep this frame's calibration for scopes started after the reset.
        // The epoch prevents a still-open pre-reset scope from writing back.
    }
}

// Main-thread timings, not GPU time or whole-game FPS. Fixed storage; scopes do not
// allocate, and percentile sorting/string formatting happens only when requested.
internal static class ReplayPerformance
{
    private sealed class Meter
    {
        public readonly double[] Recent = new double[256];
        public readonly double[] Sorted = new double[256];
        public int Count, Cursor;
        public long Samples, Allocations, SamplesWithGc, ObservedGlobalCollections;
        public double Total, Maximum, MaximumMsWithGc, MaximumMsWithoutGc;
        public void Add(double milliseconds, long bytes, int collections)
        {
            Recent[Cursor] = milliseconds; Cursor = (Cursor + 1) % Recent.Length;
            if (Count < Recent.Length) Count++;
            Samples++; Total += milliseconds; Maximum = Math.Max(Maximum, milliseconds);
            Allocations += Math.Max(0, bytes);
            if (collections > 0)
            { SamplesWithGc++; ObservedGlobalCollections += collections; MaximumMsWithGc = Math.Max(MaximumMsWithGc, milliseconds); }
            else MaximumMsWithoutGc = Math.Max(MaximumMsWithoutGc, milliseconds);
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
    private static readonly ReplaySlowCpuDiagnostics slowCpu = new(Stopwatch.Frequency);
    public static bool Enabled { get => slowCpu.Enabled; set => slowCpu.Enabled = value; }
    private static bool allocationCounterAvailable = true;
    private static bool allocationCounterChecked;
    private static readonly int maximumGeneration = GC.MaxGeneration;
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
        private readonly int collections;
        private readonly ReplaySlowCpuDiagnostics.Context context;
        internal Scope(ReplayStage stage)
        {
            this.stage = stage; start = Enabled ? Stopwatch.GetTimestamp() : 0;
            context = start != 0 ? slowCpu.Begin(start, Environment.CurrentManagedThreadId) : default;
            allocated = start != 0 ? Allocated() : 0;
            collections = start != 0 ? GC.CollectionCount(maximumGeneration) : 0;
        }
        public void Dispose()
        {
            if (start == 0 || !slowCpu.IsCurrent(context)) return;
            long endedAt = Stopwatch.GetTimestamp();
            long bytes = Allocated() - allocated;
            int globalCollections = Math.Max(0, GC.CollectionCount(maximumGeneration) - collections);
            double ms = (endedAt - start) * 1000d / Stopwatch.Frequency;
            meters[(int)stage].Add(ms, bytes, globalCollections);
            if (ms >= ReplaySlowCpuDiagnostics.MinimumMilliseconds)
                slowCpu.End(stage, context, endedAt, globalCollections, Environment.CurrentManagedThreadId);
        }
    }
    private static long Allocated()
    {
        if (!allocationCounterAvailable) return 0;
        try
        {
            if (!allocationCounterChecked)
            {
                allocationCounterChecked = true;
                long before = GC.GetAllocatedBytesForCurrentThread();
                var probe = new byte[64];
                long after = GC.GetAllocatedBytesForCurrentThread();
                GC.KeepAlive(probe);
                // Some Unity runtimes expose the API but always return zero.
                // Verify it once instead of reporting that no allocation occurs.
                if (after <= before) { allocationCounterAvailable = false; return 0; }
            }
            return GC.GetAllocatedBytesForCurrentThread();
        }
        catch (NotSupportedException) { allocationCounterAvailable = false; return 0; }
        catch (MissingMethodException) { allocationCounterAvailable = false; return 0; }
    }
    public static Scope Measure(ReplayStage stage) => new(stage);
    public static void SetFrameContext(int frame, double realtime)
    {
        if (Enabled) slowCpu.SetFrameContext(frame, realtime, Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId);
    }
    public static ReplaySlowCpuDiagnostics.Entry[] SlowCpuSnapshot() => slowCpu.Snapshot();
    public static string Summary(bool playback)
    {
        var stage = playback ? ReplayStage.Playback : ReplayStage.Capture;
        var meter = meters[(int)stage];
        if (!Enabled || meter.Samples == 0) return "性能统计：尚无样本";
        string allocation = allocationCounterAvailable
            ? string.Format(CultureInfo.InvariantCulture, "分配 {0:F1} KiB/次", meter.Allocations / (double)meter.Samples / 1024)
            : "分配计数不可用";
        return string.Format(CultureInfo.InvariantCulture, "{0}主线程 avg {1:F2} / p95 {2:F2} / max {3:F2} ms · {4}",
            playback ? "回放" : "采集", meter.Total / meter.Samples, meter.P95(), meter.Maximum, allocation);
    }
    public static string Report()
    {
        var text = new StringBuilder("Replay performance (CPU scopes, p95/p99=latest256; nested scopes overlap; allocations=thread bytes, not retained memory):");
        for (int i = 0; i < meters.Length; i++)
        {
            var m = meters[i]; if (m.Samples == 0) continue;
            text.AppendFormat(CultureInfo.InvariantCulture, " {0}[n={1},avg={2:F3},p95={3:F3},p99={4:F3},max={5:F3}ms,alloc={6:F0}B,gcScopes={7},maxWithoutGc={8:F3}ms];",
                (ReplayStage)i, m.Samples, m.Total / m.Samples, m.P95(), m.P99(), m.Maximum, m.Allocations / (double)m.Samples,
                m.SamplesWithGc, m.MaximumMsWithoutGc);
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
        public long SamplesOverlappingGlobalGc { get; set; }
        public long ObservedGlobalCollections { get; set; }
        public double MaximumMsOverlappingGlobalGc { get; set; }
        public double MaximumMsWithoutGlobalGc { get; set; }
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
                AllocationCounterAvailable = allocationCounterAvailable, SamplesOverlappingGlobalGc = m.SamplesWithGc,
                ObservedGlobalCollections = m.ObservedGlobalCollections, MaximumMsOverlappingGlobalGc = m.MaximumMsWithGc,
                MaximumMsWithoutGlobalGc = m.MaximumMsWithoutGc };
        }
        return result;
    }
    public static int[] GarbageCollectionsSinceReset() => new[]
    { GC.CollectionCount(0) - gcAtReset[0], GC.CollectionCount(1) - gcAtReset[1], GC.CollectionCount(2) - gcAtReset[2] };
    public static void Reset()
    {
        slowCpu.Reset();
        foreach (var m in meters)
        { m.Count = m.Cursor = 0; m.Samples = m.Allocations = m.SamplesWithGc = m.ObservedGlobalCollections = 0;
          m.Total = m.Maximum = m.MaximumMsWithGc = m.MaximumMsWithoutGc = 0; }
        for (int i = 0; i < gcAtReset.Length; i++) gcAtReset[i] = GC.CollectionCount(i);
    }
}

// Bounded records distinguish a requested seek, IO buffering, an intentional
// pause and an unresponsive playback clock. No strings or arrays per tick.
internal sealed class ReplaySeekDiagnostics
{
    internal sealed class Entry
    {
        public int Sequence { get; set; }
        public double TargetSeconds { get; set; }
        public double RequestedAt { get; set; }
        public double TargetPoseReadyMs { get; set; } = -1;
        public bool Superseded { get; set; }
        public bool PausedAtRequest { get; set; }
        public bool Paused { get; set; }
        public bool Buffering { get; set; }
        public int ObservedFrames { get; set; }
        public int BufferingFrames { get; set; }
        public double BufferingMs { get; set; }
        public double LongestBufferingMs { get; set; }
        public int RebufferCount { get; set; }
        public double CurrentSeconds { get; set; }
        public double MaximumPlaybackCpuMs { get; set; }
        public double MaximumRenderedFrameMs { get; set; }
        public double ClockNotAdvancingMs { get; set; }
        public double PendingMs { get; set; }
        internal Entry Copy() => (Entry)MemberwiseClone();
    }
    internal const int Capacity = 32;
    private readonly Entry?[] entries = new Entry?[Capacity];
    private int cursor, count, sequence;
    private Entry? latest;
    private double lastClock = double.NaN, lastAdvancedAt;
    private double lastObservedAt, bufferingSince;
    private bool wasBuffering;
    public void Request(double target, bool paused, double now)
    {
        AccountBuffering(now);
        if (latest != null && latest.TargetPoseReadyMs < 0)
        { latest.Superseded = true; latest.PendingMs = Math.Max(0, (now - latest.RequestedAt) * 1000); }
        latest = new Entry { Sequence = ++sequence, TargetSeconds = target, RequestedAt = now, PausedAtRequest = paused, Paused = paused, Buffering = true };
        entries[cursor] = latest; cursor = (cursor + 1) % Capacity; count = Math.Min(Capacity, count + 1);
        lastClock = double.NaN; lastAdvancedAt = now;
        lastObservedAt = bufferingSince = now; wasBuffering = true;
    }
    private void AccountBuffering(double now)
    {
        if (latest != null && wasBuffering)
        {
            latest.BufferingMs += Math.Max(0, (now - lastObservedAt) * 1000);
            latest.LongestBufferingMs = Math.Max(latest.LongestBufferingMs, Math.Max(0, (now - bufferingSince) * 1000));
        }
        lastObservedAt = now;
    }
    public void Observe(double now, double time, bool buffering, bool paused, double cpuMs, double frameMs)
    {
        if (latest == null) return;
        AccountBuffering(now);
        if (buffering && !wasBuffering)
        { bufferingSince = now; if (latest.TargetPoseReadyMs >= 0) latest.RebufferCount++; }
        wasBuffering = buffering;
        latest.ObservedFrames++; if (buffering) latest.BufferingFrames++;
        latest.Paused = paused; latest.Buffering = buffering; latest.CurrentSeconds = time;
        latest.MaximumPlaybackCpuMs = Math.Max(latest.MaximumPlaybackCpuMs, cpuMs);
        latest.MaximumRenderedFrameMs = Math.Max(latest.MaximumRenderedFrameMs, frameMs);
        if (latest.TargetPoseReadyMs < 0 && !buffering && Math.Abs(time - latest.TargetSeconds) < .00001)
        { latest.TargetPoseReadyMs = Math.Max(0, (now - latest.RequestedAt) * 1000); latest.PendingMs = latest.TargetPoseReadyMs; }
        if (paused || buffering || time != lastClock) lastAdvancedAt = now;
        latest.ClockNotAdvancingMs = paused || buffering ? 0 : Math.Max(0, (now - lastAdvancedAt) * 1000);
        lastClock = time;
    }
    public Entry[] Snapshot(double now)
    {
        var result = new Entry[count];
        for (int i = 0; i < count; i++)
        {
            var value = entries[(cursor - count + Capacity + i) % Capacity]!.Copy();
            if (i == count - 1 && wasBuffering)
            {
                value.BufferingMs += Math.Max(0, (now - lastObservedAt) * 1000);
                value.LongestBufferingMs = Math.Max(value.LongestBufferingMs, Math.Max(0, (now - bufferingSince) * 1000));
            }
            if (!value.Superseded && value.TargetPoseReadyMs < 0) value.PendingMs = Math.Max(0, (now - value.RequestedAt) * 1000);
            result[i] = value;
        }
        return result;
    }
}

// Global collections can suspend the game outside any replay CPU scope. Observe
// the interval between LateUpdates as well; overlap is correlation, not a GC
// duration measurement or attribution to a particular allocating thread.
internal sealed class ReplayHitchDiagnostics
{
    internal struct Hitch
    {
        public double ReplaySeconds { get; set; }
        public double FrameMs { get; set; }
        public int GlobalCollections { get; set; }
        public bool Buffering { get; set; }
        public bool Paused { get; set; }
    }
    internal sealed class Report
    {
        public long ObservedFrames { get; set; }
        public long GlobalCollections { get; set; }
        public long FramesOver33Ms { get; set; }
        public long FramesOver100Ms { get; set; }
        public long SlowFramesOverlappingGlobalGc { get; set; }
        public long ActivePlaybackSlowFrames { get; set; }
        public double MaximumMsOverlappingGlobalGc { get; set; }
        public double MaximumMsWithoutGlobalGc { get; set; }
        public Hitch[] RecentSlowFrames { get; set; } = Array.Empty<Hitch>();
    }
    internal const int Capacity = 64;
    private readonly Hitch[] recent = new Hitch[Capacity];
    private int cursor, count, previousCollections;
    private bool primed;
    private long frames, collections, slow, verySlow, slowGc, activeSlow;
    private double maxWithGc, maxWithoutGc;
    public void Observe(double frameSeconds, double replaySeconds, bool buffering, bool paused, int globalCollections, bool eligible)
    {
        if (!eligible) { primed = false; return; }
        if (!primed) { previousCollections = globalCollections; primed = true; return; }
        int delta = Math.Max(0, globalCollections - previousCollections);
        previousCollections = globalCollections;
        if (!ReplayRules.Finite(frameSeconds) || frameSeconds <= 0) return;
        double ms = frameSeconds * 1000;
        frames++; collections += delta;
        if (delta > 0) maxWithGc = Math.Max(maxWithGc, ms); else maxWithoutGc = Math.Max(maxWithoutGc, ms);
        if (ms < 1000d / 30) return;
        slow++; if (ms >= 100) verySlow++;
        if (delta > 0) slowGc++;
        if (!buffering && !paused) activeSlow++;
        recent[cursor] = new Hitch { ReplaySeconds = replaySeconds, FrameMs = ms, GlobalCollections = delta, Buffering = buffering, Paused = paused };
        cursor = (cursor + 1) % Capacity; count = Math.Min(Capacity, count + 1);
    }
    public Report Snapshot()
    {
        var values = new Hitch[count];
        for (int i = 0; i < count; i++) values[i] = recent[(cursor - count + Capacity + i) % Capacity];
        return new Report { ObservedFrames = frames, GlobalCollections = collections, FramesOver33Ms = slow,
            FramesOver100Ms = verySlow, SlowFramesOverlappingGlobalGc = slowGc, ActivePlaybackSlowFrames = activeSlow,
            MaximumMsOverlappingGlobalGc = maxWithGc, MaximumMsWithoutGlobalGc = maxWithoutGc, RecentSlowFrames = values };
    }
    public void Reset()
    {
        cursor = count = previousCollections = 0; primed = false;
        frames = collections = slow = verySlow = slowGc = activeSlow = 0;
        maxWithGc = maxWithoutGc = 0;
    }
}
