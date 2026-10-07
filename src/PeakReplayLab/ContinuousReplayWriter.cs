using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace PeakReplayLab;

// Explicit opt-in debug recording. The producer publishes immutable pure-data
// frames; one dedicated background worker owns all serialization and file I/O.
// This never reads, snapshots or modifies the 120-second highlight buffer.
public sealed class ContinuousReplayWriter
{
    private sealed class Pending
    {
        public readonly ReplayFrame Frame;
        public readonly ReplayQueueBudget.Reservation Reservation;
        public Pending(ReplayFrame frame, ReplayQueueBudget.Reservation reservation) { Frame = frame; Reservation = reservation; }
    }
    private sealed class Failure
    {
        public readonly string Code;
        public readonly Exception Error;
        public Failure(string code, Exception error) { Code = code; Error = error; }
    }
    private sealed class StopException : IOException
    {
        public readonly string Code;
        public StopException(string code, string message) : base(message) { Code = code; }
    }
    private readonly ConcurrentQueue<Pending> queue = new();
    private readonly ReplayQueueBudget queueBudget = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly ReplayHeader template;
    private readonly ContinuousReplayOptions options;
    private readonly Func<string, Stream> openNew;
    private readonly DateTime startedUtc = DateTime.UtcNow;
    private readonly Stopwatch flushClock = Stopwatch.StartNew();
    private Failure? failure;
    private string? completionReason;
    private int accepting = 1, completing, finished, producerBusy, completedSegments;
    private long acceptedFrames, writtenFrames, bytesWritten, gap100, gap500;
    private double largestGap, nativeStart, nativeEnd;
    private bool hasNativeStart, manifestStarted;
    private StreamWriter? manifest;
    private Segment? segment;
    private int segmentIndex;
    private ReplayFrame? previousNative;
    private long globalEventSequence = -1;
    private double globalEventTime = -1;
    private const int DetailedGapLimit = 256;
    public string RunDirectory { get; }
    public Task<ContinuousReplayResult> Completion { get; }
    public Exception? Fault => Volatile.Read(ref failure)?.Error;
    public string? FailureCode => Volatile.Read(ref failure)?.Code;
    public ContinuousReplayStats Stats
    {
        get
        {
            var pending = queueBudget.Current;
            return new()
            {
        State = Fault != null ? "Faulted" : Volatile.Read(ref finished) != 0 ? "Completed" : Volatile.Read(ref completing) != 0 ? "Completing" : "Recording",
        AcceptedFrames = Interlocked.Read(ref acceptedFrames), WrittenFrames = Interlocked.Read(ref writtenFrames),
        QueuedFrames = pending.Frames, QueuedBytes = pending.Bytes,
        CompletedSegments = Volatile.Read(ref completedSegments), BytesWritten = Interlocked.Read(ref bytesWritten),
        GapOver100ms = Interlocked.Read(ref gap100), GapOver500ms = Interlocked.Read(ref gap500),
        LargestGapSeconds = Volatile.Read(ref largestGap), NativeStart = Volatile.Read(ref nativeStart), NativeEnd = Volatile.Read(ref nativeEnd),
            };
        }
    }

    public ContinuousReplayWriter(string debugRunsRoot, ReplayHeader header, ContinuousReplayOptions? options = null)
        : this(debugRunsRoot, header, options, path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.SequentialScan)) { }

    // Test seam: the production entry point always uses create-new local files.
    internal ContinuousReplayWriter(string debugRunsRoot, ReplayHeader header, ContinuousReplayOptions? options, Func<string, Stream> openNew)
    {
        if (string.IsNullOrWhiteSpace(debugRunsRoot)) throw new ArgumentException("Missing debug recording root.", nameof(debugRunsRoot));
        this.options = (options ?? new ContinuousReplayOptions()).CopyChecked();
        this.openNew = openNew ?? throw new ArgumentNullException(nameof(openNew));
        template = CopyHeader(header ?? throw new ArgumentNullException(nameof(header)));
        if (template.Schema != ReplayRules.CurrentSchema) throw new ArgumentException("Continuous replay requires the current replay schema.", nameof(header));
        ReplayRules.Validate(template);
        RunDirectory = Path.Combine(Path.GetFullPath(debugRunsRoot), $"run-{startedUtc:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
        Completion = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    // Convenience for pure-data clients. Unity integration should pass its
    // already-computed RollingBuffer.LastFrameEstimatedBytes as a conservative
    // single-frame guard; shared queue retention is measured independently.
    public bool TryEnqueue(ReplayFrame frame)
    {
        if (Volatile.Read(ref accepting) == 0) return false;
        try { return TryEnqueue(frame, RollingBuffer.Estimate(frame)); }
        catch (Exception e) { Fail("invalid-frame", e); return false; }
    }

    // Non-waiting single producer. Reservations include the frame currently
    // being serialized, so a slow disk cannot hide an extra unbounded backlog.
    public bool TryEnqueue(ReplayFrame frame, long estimatedBytes)
    {
        if (Volatile.Read(ref accepting) == 0) return false;
        if (Interlocked.CompareExchange(ref producerBusy, 1, 0) != 0)
        { Fail("concurrent-producer", new InvalidOperationException("Continuous replay has more than one producer.")); return false; }
        ReplayQueueBudget.Reservation? reservation = null;
        try
        {
            if (Volatile.Read(ref accepting) == 0) return false;
            if (frame == null || !ReplayRules.Finite(frame.T) || frame.T < 0 || estimatedBytes <= 0)
            { Fail("invalid-frame", new InvalidDataException("Invalid continuous replay frame or memory estimate.")); return false; }
            if (estimatedBytes > options.QueueByteLimit)
            { Fail("queue-overflow", new IOException($"One capture frame estimate {estimatedBytes} bytes exceeds queue byte limit {options.QueueByteLimit} (frame limit {options.QueueFrameLimit}).")); return false; }
            if (!queueBudget.TryReserve(frame, options.QueueByteLimit, options.QueueFrameLimit, out reservation, out var attempted))
            {
                Fail("queue-overflow", new IOException($"Continuous recording stopped: retained queue, capture baseline and in-flight writer would reach {attempted.Frames}/{options.QueueFrameLimit} frames and {attempted.Bytes}/{options.QueueByteLimit} bytes; accepted peak {attempted.PeakFrames} frames / {attempted.PeakBytes} bytes. No frames were silently skipped."));
                return false;
            }
            queue.Enqueue(new Pending(frame, reservation!)); reservation = null;
            Interlocked.Increment(ref acceptedFrames);
            return true;
        }
        catch (Exception e) { Fail("enqueue-failure", e); return false; }
        finally
        {
            reservation?.Dispose();
            Volatile.Write(ref producerBusy, 0); Signal();
        }
    }

    public Task<ContinuousReplayResult> CompleteAsync(string reason = "manual-stop")
    {
        Interlocked.CompareExchange(ref completionReason, Short(reason, 128), null);
        Volatile.Write(ref accepting, 0); Volatile.Write(ref completing, 1); Signal();
        return Completion;
    }

    // A failed capture can be the last frame of a run: no later successful
    // sample may arrive to reveal a gap. Explicitly fail, but drain and seal all
    // previously accepted frames before completing the background task.
    public Task<ContinuousReplayResult> InterruptAsync(string reason, Exception error)
    {
        Interlocked.CompareExchange(ref completionReason, Short(reason, 128), null);
        Fail("capture-interrupted", error ?? new IOException("Continuous capture was interrupted."));
        return Completion;
    }

    private void Fail(string code, Exception error)
    {
        Interlocked.CompareExchange(ref failure, new Failure(code, error), null);
        Volatile.Write(ref accepting, 0); Volatile.Write(ref completing, 1); Signal();
    }
    private void Signal() { try { wake.Set(); } catch (ObjectDisposedException) { } }

    private ContinuousReplayResult Run()
    {
        Exception? terminal = null;
        try
        {
            Directory.CreateDirectory(RunDirectory);
            manifest = new StreamWriter(new CountingStream(openNew(Path.Combine(RunDirectory, "manifest.ndjson")), this, true), new UTF8Encoding(false), 4096);
            while (true)
            {
                if (queue.TryDequeue(out var pending))
                {
                    try { Process(pending.Frame); }
                    finally { pending.Reservation.Dispose(); }
                    FlushDue();
                    continue;
                }
                if (Volatile.Read(ref completing) != 0 && Volatile.Read(ref producerBusy) == 0) break;
                FlushDue(); wake.WaitOne(Math.Min(options.FlushMilliseconds, 250));
            }
            StartManifest();
            FinishSegment();
            if (Volatile.Read(ref completedSegments) == 0)
                Fail("insufficient-frames", new InvalidDataException("Continuous recording did not contain a playable segment with at least two frames."));
        }
        catch (Exception e)
        {
            terminal = e;
            Fail(e is StopException stop ? stop.Code : e is InvalidDataException ? "invalid-data" : "write-failure", e);
            if (segment != null)
            {
                var failed = segment; segment = null;
                try { failed.Dispose(); } catch { /* Original failure stays visible; retain the exact partial file. */ }
                TryManifest(new { Type = "segment-incomplete", Index = failed.Index, File = failed.PartialName,
                    NativeStart = failed.Start, NativeEnd = failed.End, Duration = failed.End - failed.Start,
                    FrameCount = failed.Frames, Reason = "write-failure", Fault = Short(e.Message, 2048) });
            }
        }
        finally
        {
            Volatile.Write(ref accepting, 0); Volatile.Write(ref completing, 1);
            // A concurrent producer may have passed its initial accepting check.
            // It performs no I/O; wait only on the worker before releasing data.
            var spin = new SpinWait(); while (Volatile.Read(ref producerBusy) != 0) spin.SpinOnce();
            while (queue.TryDequeue(out var abandoned))
            { abandoned.Reservation.Dispose(); }
            queueBudget.ClearBaseline();
        }
        var result = Result(terminal);
        try
        {
            StartManifest();
            WriteManifest(new { Type = "run-end", result.Status, result.Reason, result.FaultCode, result.Fault,
                result.AcceptedFrames, result.WrittenFrames, result.UnwrittenFrames, result.CompletedSegments,
                result.BytesWritten, result.GapOver100ms, result.GapOver500ms, result.LargestGapSeconds,
                result.NativeStart, result.NativeEnd, FinishedUtc = DateTime.UtcNow.ToString("O") });
            manifest?.Flush();
        }
        catch (Exception e) { terminal ??= e; Fail("manifest-failure", e); }
        finally
        {
            try { manifest?.Dispose(); } catch (Exception e) { terminal ??= e; Fail("manifest-failure", e); }
            manifest = null; Volatile.Write(ref finished, 1); wake.Dispose();
        }
        return Result(terminal);
    }

    private void Process(ReplayFrame frame)
    {
        if (previousNative != null && frame.T <= previousNative.T) throw new InvalidDataException("Continuous capture time did not increase.");
        if (!hasNativeStart)
        { nativeStart = frame.T; hasNativeStart = true; StartManifest(); }
        if (frame.T - nativeStart > options.MaxRunSeconds)
            throw new StopException("run-time-limit", "Continuous recording reached its configured maximum run duration.");
        foreach (var value in frame.Events)
        {
            if (value == null || value.Sequence <= globalEventSequence || !ReplayRules.Finite(value.T) ||
                value.T < 0 || value.T < globalEventTime || value.T > frame.T + .001)
                throw new InvalidDataException("Continuous native item events are not ordered.");
            globalEventSequence = value.Sequence; globalEventTime = value.T;
        }
        double gap = previousNative == null ? 0 : frame.T - previousNative.T;
        if (gap > largestGap) Volatile.Write(ref largestGap, gap);
        long gapIndex = gap > .100001 ? Interlocked.Increment(ref gap100) : 0;
        if (gap > .500001) Interlocked.Increment(ref gap500);
        if (gapIndex > 0 && gapIndex <= DetailedGapLimit)
            WriteManifest(new { Type = "capture-gap", FromNativeTime = previousNative!.T, ToNativeTime = frame.T,
                Duration = gap, Over500ms = gap > .500001, DiscontinuousSegment = gap > options.SegmentSeconds });
        else if (gapIndex == DetailedGapLimit + 1)
            WriteManifest(new { Type = "capture-gap-summary", DetailsTruncated = true, DetailedLimit = DetailedGapLimit });
        if (segment == null) OpenSegment(frame);
        else if (frame.T - segment.Start > options.SegmentSeconds || segment.Frames >= options.SegmentFrameLimit)
        {
            FinishSegment();
            // A real sampling gap cannot be hidden by manufacturing bridge frames.
            if (previousNative != null && gap <= options.SegmentSeconds) OpenSegment(previousNative);
            else OpenSegment(frame);
        }
        if (segment!.End != frame.T || segment.Frames == 0)
        {
            if (!segment.TryWrite(frame))
            {
                if (segment.Frames < 2) throw new StopException("segment-size-limit", "Two adjacent frames exceed the independent replay segment safety budget.");
                FinishSegment(); OpenSegment(previousNative!);
                if (!segment!.TryWrite(frame)) throw new StopException("segment-size-limit", "A boundary pair exceeds the independent replay segment safety budget.");
            }
        }
        previousNative = frame; Volatile.Write(ref nativeEnd, frame.T); Interlocked.Increment(ref writtenFrames);
    }

    private void OpenSegment(ReplayFrame first)
    {
        segment = new Segment(this, ++segmentIndex, first.T);
        if (!segment.TryWrite(first)) throw new StopException("segment-size-limit", "One frame exceeds the independent replay segment safety budget.");
    }
    private void FinishSegment()
    {
        if (segment == null) return;
        var ending = segment;
        ending.EndFile();
        if (ending.Frames < 2)
        {
            WriteManifest(new { Type = "segment-incomplete", Index = ending.Index, File = ending.PartialName,
                NativeStart = ending.Start, NativeEnd = ending.End, Duration = ending.End - ending.Start,
                FrameCount = ending.Frames, Reason = "fewer-than-two-frames", ending.PreOriginEvents });
            Fail(completedSegments == 0 ? "insufficient-frames" : "incomplete-segment",
                new InvalidDataException("Continuous recording contains an isolated single-frame segment; its partial file is retained but cannot be played."));
        }
        else
        {
            string target = Path.Combine(RunDirectory, ending.FinalName);
            File.Move(Path.Combine(RunDirectory, ending.PartialName), target);
            WriteManifest(new { Type = "segment-complete", Index = ending.Index, File = ending.FinalName,
                NativeStart = ending.Start, NativeEnd = ending.End, Duration = ending.End - ending.Start,
                FrameCount = ending.Frames, CompressedBytes = new FileInfo(target).Length,
                UncompressedBytes = ending.UncompressedBytes, DecodedBytes = ending.Budget.Bytes, ending.PreOriginEvents });
            Interlocked.Increment(ref completedSegments);
        }
        segment = null; manifest!.Flush(); flushClock.Restart();
    }

    private void StartManifest()
    {
        if (manifestStarted || manifest == null) return;
        WriteManifest(new { Type = "run-start", Version = 1, Schema = template.Schema, StartedUtc = startedUtc.ToString("O"),
            template.Scene, template.Route, template.SampleHz, NativeStart = hasNativeStart ? (double?)nativeStart : null,
            options.SegmentSeconds, options.SegmentFrameLimit, options.SegmentDecodedByteLimit,
            options.SegmentUncompressedByteLimit, options.QueueByteLimit, options.QueueFrameLimit, options.MaxRunSeconds, options.MaxRunBytes });
        manifest.Flush(); manifestStarted = true;
    }
    private void WriteManifest(object value) => manifest!.WriteLine(JsonConvert.SerializeObject(value, ReplayFiles.Json));
    private void TryManifest(object value) { try { if (manifest != null) { StartManifest(); WriteManifest(value); manifest.Flush(); } } catch { } }
    private void FlushDue()
    {
        if (flushClock.ElapsedMilliseconds < options.FlushMilliseconds) return;
        segment?.Flush(); manifest?.Flush(); flushClock.Restart();
    }
    private ContinuousReplayResult Result(Exception? terminal)
    {
        var failed = Volatile.Read(ref failure); var stats = Stats;
        string? text = failed == null ? null : Short(failed.Error.Message, 2048);
        if (terminal != null && !ReferenceEquals(terminal, failed?.Error)) text += " | " + Short(terminal.Message, 2048);
        return new ContinuousReplayResult
        {
            RunDirectory = RunDirectory, Status = failed == null ? "completed" : "faulted", Reason = completionReason ?? (failed == null ? "manual-stop" : "safety-stop"),
            FaultCode = failed?.Code, Fault = text, AcceptedFrames = stats.AcceptedFrames, WrittenFrames = stats.WrittenFrames,
            UnwrittenFrames = Math.Max(0, stats.AcceptedFrames - stats.WrittenFrames), CompletedSegments = stats.CompletedSegments,
            BytesWritten = stats.BytesWritten, GapOver100ms = stats.GapOver100ms, GapOver500ms = stats.GapOver500ms,
            LargestGapSeconds = stats.LargestGapSeconds, NativeStart = stats.NativeStart, NativeEnd = stats.NativeEnd,
        };
    }
    private static string Short(string? value, int max) => string.IsNullOrEmpty(value) ? "unspecified" : value!.Length <= max ? value : value.Substring(0, max);
    private static ReplayHeader CopyHeader(ReplayHeader source) => new()
    {
        Type = source.Type, Schema = source.Schema, Recorder = source.Recorder, Scene = source.Scene, GameVersion = source.GameVersion,
        BuildId = source.BuildId, GameAssembly = source.GameAssembly, Route = source.Route, StartedUtc = source.StartedUtc, SavedUtc = source.SavedUtc,
        RouteContext = source.RouteContext?.Copy(),
        Duration = 0, FrameCount = 0, Participants = (string[])source.Participants.Clone(), MapObjects = (string[])source.MapObjects.Clone(),
        SampleHz = source.SampleHz, Fidelity = source.Fidelity,
    };

    private sealed class Segment : IDisposable
    {
        private readonly ContinuousReplayWriter owner;
        private readonly Stream file;
        private readonly GZipStream gzip;
        private readonly StreamWriter writer;
        private readonly ReplayDeltaCodec.Writer codec;
        private readonly ReplayRules.ValidationMemo validation = new();
        private readonly ContinuousReplayRebaser rebaser;
        private double previous = -1, eventTime = -1;
        private long eventSequence = -1;
        private bool disposed;
        public readonly ContinuousReplayBudget Budget = new();
        public readonly int Index;
        public readonly double Start;
        public double End { get; private set; }
        public int Frames { get; private set; }
        public int PreOriginEvents { get; private set; }
        public long UncompressedBytes { get; private set; }
        public string FinalName => $"segment-{Index:D6}.peakreplay";
        public string PartialName => FinalName + ".partial";
        public Segment(ContinuousReplayWriter owner, int index, double start)
        {
            this.owner = owner; Index = index; Start = End = start; rebaser = new(start);
            codec = new ReplayDeltaCodec.Writer(owner.template.Schema);
            file = new CountingStream(owner.openNew(Path.Combine(owner.RunDirectory, PartialName)), owner, false);
            gzip = new GZipStream(file, CompressionLevel.Fastest, true);
            writer = new StreamWriter(gzip, new UTF8Encoding(false), 65536, true);
            try
            {
                var header = CopyHeader(owner.template);
                header.StartedUtc = owner.startedUtc.AddSeconds(start - owner.nativeStart).ToString("O");
                header.SavedUtc = DateTime.UtcNow.ToString("O"); ReplayRules.Validate(header);
                Write(JsonConvert.SerializeObject(header, ReplayFiles.Json));
            }
            catch { Dispose(); throw; }
        }
        public bool TryWrite(ReplayFrame native)
        {
            var frame = rebaser.Apply(native);
            ReplayRules.ValidateStored(frame, previous, validation); ReplayRules.ValidateVersion(frame, owner.template.Schema);
            if (frame.World.ActiveMapObjects.Length != owner.template.MapObjects.Length) throw new InvalidDataException("Continuous map state shape changed.");
            foreach (var e in frame.Events)
                if (e.Sequence <= eventSequence || e.T < eventTime) throw new InvalidDataException("Segment item events are not ordered.");
            long increment = Budget.Additional(frame);
            if (Budget.Bytes + increment > owner.options.SegmentDecodedByteLimit) return false;
            string line = JsonConvert.SerializeObject(codec.Encode(frame), ReplayFiles.Json);
            long size = Encoding.UTF8.GetByteCount(line) + 1L;
            if (line.Length > ReplayRules.MaxLineCharacters || size > ReplayRules.MaxLineCharacters)
                throw new StopException("record-size-limit", "A continuous replay record exceeds the reader line limit.");
            if (UncompressedBytes + size + 128 > owner.options.SegmentUncompressedByteLimit) return false;
            Write(line); Budget.Commit(frame, increment);
            foreach (var e in frame.Events) { eventSequence = e.Sequence; eventTime = e.T; }
            foreach (var e in native.Events) if (e.T < Start) PreOriginEvents++;
            previous = frame.T; End = native.T; Frames++;
            return true;
        }
        private void Write(string line)
        {
            long size = Encoding.UTF8.GetByteCount(line) + 1L;
            if (size > ReplayRules.MaxLineCharacters || UncompressedBytes + size > owner.options.SegmentUncompressedByteLimit)
                throw new StopException("record-size-limit", "Continuous replay header/footer exceeds its safety limit.");
            writer.Write(line); writer.Write('\n'); UncompressedBytes += size;
        }
        public void Flush() { writer.Flush(); gzip.Flush(); file.Flush(); }
        public void EndFile() { Write("{\"Type\":\"end\"}"); Dispose(); }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            Exception? first = null;
            try { writer.Dispose(); } catch (Exception e) { first = e; }
            try { gzip.Dispose(); } catch (Exception e) { first ??= e; }
            try { file.Dispose(); } catch (Exception e) { first ??= e; }
            if (first != null) throw first;
        }
    }

    // Count actual compressed data plus the manifest, reserving a small bounded
    // tail for fault diagnostics. Limits are enforced before each physical write.
    private sealed class CountingStream : Stream
    {
        private readonly Stream inner;
        private readonly ContinuousReplayWriter owner;
        private readonly bool diagnostic;
        public CountingStream(Stream inner, ContinuousReplayWriter owner, bool diagnostic)
        { this.inner = inner; this.owner = owner; this.diagnostic = diagnostic; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            long reserve = diagnostic ? 0 : Math.Min(65536, owner.options.MaxRunBytes / 4);
            if (Interlocked.Read(ref owner.bytesWritten) + count > owner.options.MaxRunBytes - reserve)
                throw new StopException("run-byte-limit", "Continuous recording reached its configured disk-byte limit.");
            inner.Write(buffer, offset, count); Interlocked.Add(ref owner.bytesWritten, count);
        }
        public override void Flush() => inner.Flush();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
