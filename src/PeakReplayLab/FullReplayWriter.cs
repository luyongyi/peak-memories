using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace PeakReplayLab;

// A single create-new file is streamed from the first accepted capture. Gzip
// pages and the final seek index live INSIDE that file: no chunk files, manifest,
// full-game RAM buffer or stop-time merge. Only this worker touches the file.
public sealed class FullReplayWriter
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
    private readonly List<FullReplayPage> pages = new();
    private readonly HashSet<string> actors = new(StringComparer.Ordinal);
    private readonly ReplayHeader template;
    private readonly ReplayHeader sourceMetadata;
    private ReplayRegionOutcome? finalOutcome;
    private readonly ReplayRegionAccumulator regions;
    private readonly ContinuousReplayOptions options;
    private readonly Func<string, Stream> openNew;
    private readonly Stopwatch flushClock = Stopwatch.StartNew();
    private Failure? failure;
    private string? completionReason;
    private Stream? file;
    private Page? page;
    private ReplayFrame? previous;
    private int accepting = 1, completing, finished, producerBusy, completedPages;
    private long acceptedFrames, writtenFrames, bytesWritten, gap100, gap500;
    private double largestGap, nativeStart, nativeEnd;
    private long eventSequence = -1;
    private double eventTime = -1;
    private bool renamed;
    public string FilePath { get; }
    public string PartialPath => FilePath + ".partial";
    public Task<FullReplayResult> Completion { get; }
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
        CompletedSegments = Volatile.Read(ref completedPages), BytesWritten = Interlocked.Read(ref bytesWritten),
        GapOver100ms = Interlocked.Read(ref gap100), GapOver500ms = Interlocked.Read(ref gap500),
        LargestGapSeconds = Volatile.Read(ref largestGap), NativeStart = Volatile.Read(ref nativeStart), NativeEnd = Volatile.Read(ref nativeEnd),
            };
        }
    }

    public FullReplayWriter(string root, ReplayHeader header, ContinuousReplayOptions? options = null)
        : this(root, header, options, path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.SequentialScan)) { }

    internal FullReplayWriter(string root, ReplayHeader header, ContinuousReplayOptions? options, Func<string, Stream> openNew)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Missing full recording directory.", nameof(root));
        this.options = (options ?? new ContinuousReplayOptions()).CopyChecked();
        this.options.SegmentSeconds = Math.Min(10, this.options.SegmentSeconds);
        this.options.SegmentFrameLimit = Math.Min(601, this.options.SegmentFrameLimit);
        this.options.SegmentDecodedByteLimit = Math.Min(64L * 1024 * 1024, this.options.SegmentDecodedByteLimit);
        this.options.SegmentUncompressedByteLimit = Math.Min(128L * 1024 * 1024, this.options.SegmentUncompressedByteLimit);
        this.openNew = openNew ?? throw new ArgumentNullException(nameof(openNew));
        sourceMetadata = header ?? throw new ArgumentNullException(nameof(header));
        template = CopyHeader(sourceMetadata);
        regions = new ReplayRegionAccumulator(template);
        if (template.Schema != ReplayRules.CurrentSchema) throw new ArgumentException("Full replay requires the current replay schema.", nameof(header));
        ReplayRules.Validate(template);
        FilePath = Path.Combine(Path.GetFullPath(root), $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.peakrun");
        Completion = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public bool TryEnqueue(ReplayFrame frame)
    {
        if (Volatile.Read(ref accepting) == 0) return false;
        try { return TryEnqueue(frame, RollingBuffer.Estimate(frame)); }
        catch (Exception e) { Fail("invalid-frame", e); return false; }
    }
    // The existing whole-frame estimate remains a conservative single-frame
    // guard. Queue memory follows shared references, including the worker's
    // current lease, rather than summing that full estimate for every sample.
    public bool TryEnqueue(ReplayFrame frame, long estimatedBytes)
    {
        if (Volatile.Read(ref accepting) == 0) return false;
        if (Interlocked.CompareExchange(ref producerBusy, 1, 0) != 0)
        { Fail("concurrent-producer", new InvalidOperationException("Full replay accepts one capture producer.")); return false; }
        ReplayQueueBudget.Reservation? reservation = null;
        try
        {
            if (Volatile.Read(ref accepting) == 0) return false;
            if (frame == null || !ReplayRules.Finite(frame.T) || frame.T < 0 || estimatedBytes <= 0)
            { Fail("invalid-frame", new InvalidDataException("Invalid full replay frame or memory estimate.")); return false; }
            if (estimatedBytes > options.QueueByteLimit)
            { Fail("queue-overflow", new IOException($"One capture frame estimate {estimatedBytes} bytes exceeds queue byte limit {options.QueueByteLimit} (frame limit {options.QueueFrameLimit}).")); return false; }
            if (!queueBudget.TryReserve(frame, options.QueueByteLimit, options.QueueFrameLimit, out reservation, out var attempted))
            {
                Fail("queue-overflow", new IOException($"Full recording stopped: retained queue, capture baseline and in-flight writer would reach {attempted.Frames}/{options.QueueFrameLimit} frames and {attempted.Bytes}/{options.QueueByteLimit} bytes; accepted peak {attempted.PeakFrames} frames / {attempted.PeakBytes} bytes. No frames were silently skipped."));
                return false;
            }
            queue.Enqueue(new Pending(frame, reservation!)); reservation = null;
            Interlocked.Increment(ref acceptedFrames); return true;
        }
        catch (Exception e) { Fail("enqueue-failure", e); return false; }
        finally
        {
            reservation?.Dispose();
            Volatile.Write(ref producerBusy, 0); Signal();
        }
    }
    public Task<FullReplayResult> CompleteAsync(string reason = "manual-stop")
    {
        Interlocked.CompareExchange(ref finalOutcome, Volatile.Read(ref sourceMetadata.CoverOutcome), null);
        Interlocked.CompareExchange(ref completionReason, Short(reason, 128), null);
        Volatile.Write(ref accepting, 0); Volatile.Write(ref completing, 1); Signal(); return Completion;
    }
    public Task<FullReplayResult> InterruptAsync(string reason, Exception error)
    {
        Interlocked.CompareExchange(ref completionReason, Short(reason, 128), null);
        Fail("capture-interrupted", error ?? new IOException("Capture was interrupted.")); return Completion;
    }
    private void Fail(string code, Exception error)
    {
        Interlocked.CompareExchange(ref failure, new Failure(code, error), null);
        Volatile.Write(ref accepting, 0); Volatile.Write(ref completing, 1); Signal();
    }
    private void Signal() { try { wake.Set(); } catch (ObjectDisposedException) { } }

    private FullReplayResult Run()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            file = new CountedFile(openNew(PartialPath), this);
            WriteContainerHeader();
            while (true)
            {
                if (queue.TryDequeue(out var pending))
                {
                    try { Process(pending.Frame); }
                    finally { pending.Reservation.Dispose(); }
                    FlushDue(); continue;
                }
                if (Volatile.Read(ref completing) != 0 && Volatile.Read(ref producerBusy) == 0) break;
                FlushDue(); wake.WaitOne(Math.Min(options.FlushMilliseconds, 250));
            }
            FinishPage();
            if (writtenFrames < 2) Fail("insufficient-frames", new InvalidDataException("Full recording needs at least two captured frames."));
            WriteIndex(); file.Flush(); file.Dispose(); file = null;
            if (Fault == null) { File.Move(PartialPath, FilePath); renamed = true; }
        }
        catch (Exception e)
        {
            Fail(e is StopException stop ? stop.Code : e is InvalidDataException ? "invalid-data" : "write-failure", e);
        }
        finally
        {
            Volatile.Write(ref accepting, 0); Volatile.Write(ref completing, 1);
            var spin = new SpinWait(); while (Volatile.Read(ref producerBusy) != 0) spin.SpinOnce();
            while (queue.TryDequeue(out var abandoned))
            { abandoned.Reservation.Dispose(); }
            queueBudget.ClearBaseline();
            try { page?.Dispose(); } catch (Exception e) { Fail("write-failure", e); }
            try { file?.Dispose(); } catch (Exception e) { Fail("write-failure", e); }
            page = null; file = null; Volatile.Write(ref finished, 1); wake.Dispose();
        }
        var stats = Stats; var failed = Volatile.Read(ref failure);
        return new FullReplayResult
        {
            FilePath = renamed ? FilePath : PartialPath, Status = failed == null ? "completed" : "faulted",
            Reason = completionReason ?? (failed == null ? "manual-stop" : "safety-stop"), FaultCode = failed?.Code,
            Fault = failed == null ? null : Short(failed.Error.Message, 2048),
            AcceptedFrames = stats.AcceptedFrames, WrittenFrames = stats.WrittenFrames,
            UnwrittenFrames = Math.Max(0, stats.AcceptedFrames - stats.WrittenFrames), CompletedPages = stats.CompletedSegments,
            BytesWritten = stats.BytesWritten, GapOver100ms = stats.GapOver100ms, GapOver500ms = stats.GapOver500ms,
            LargestGapSeconds = stats.LargestGapSeconds, Duration = Math.Max(0, stats.NativeEnd - stats.NativeStart),
        };
    }

    private void Process(ReplayFrame frame)
    {
        if (previous != null && frame.T <= previous.T) throw new InvalidDataException("Full recording capture time did not increase.");
        if (previous == null) Volatile.Write(ref nativeStart, frame.T);
        if (frame.T - nativeStart > options.MaxRunSeconds) throw new StopException("run-time-limit", "Full recording reached its duration limit.");
        foreach (var e in frame.Events)
        {
            if (e == null || e.Sequence <= eventSequence || !ReplayRules.Finite(e.T) || e.T < 0 || e.T < eventTime || e.T > frame.T + .001)
                throw new InvalidDataException("Full recording item events are not ordered.");
            eventSequence = e.Sequence; eventTime = e.T;
        }
        foreach (var actor in frame.Actors)
            actors.Add(actor.Id);
        double gap = previous == null ? 0 : frame.T - previous.T;
        if (gap > largestGap) Volatile.Write(ref largestGap, gap);
        if (gap > .100001) Interlocked.Increment(ref gap100);
        if (gap > .500001) Interlocked.Increment(ref gap500);
        if (page == null) OpenPage(frame, false);
        else if (frame.T - page.NativeStart > options.SegmentSeconds || page.Frames >= options.SegmentFrameLimit)
        {
            FinishPage();
            if (previous != null && gap <= options.SegmentSeconds) OpenPage(previous, true);
            else OpenPage(frame, false);
        }
        if (page!.NativeEnd != frame.T)
        {
            if (!page.TryWrite(frame))
            {
                // One-frame internal pages are valid. If an adjacent pair does
                // not fit a bounded seek page, preserve both frames separately.
                FinishPage();
                OpenPage(frame, false);
            }
        }
        regions.Observe(frame);
        previous = frame; Volatile.Write(ref nativeEnd, frame.T); Interlocked.Increment(ref writtenFrames);
    }
    private void OpenPage(ReplayFrame first, bool overlap)
    {
        if (pages.Count >= 8192) throw new StopException("page-limit", "Full recording reached its bounded seek index limit.");
        page = new Page(this, first.T, writtenFrames - (overlap ? 1 : 0), overlap);
        if (!page.TryWrite(first)) throw new StopException("page-size-limit", "One frame exceeds the full recording seek-page safety budget.");
    }
    private void FinishPage()
    {
        if (page == null) return;
        var ending = page; ending.EndFile();
        pages.Add(new FullReplayPage
        {
            Index = pages.Count, Start = ending.NativeStart - nativeStart, End = ending.NativeEnd - nativeStart,
            Offset = ending.Offset, Length = file!.Position - ending.Offset,
            RawBytes = ending.RawBytes, DecodedBytes = ending.Budget.Bytes,
            FrameCount = ending.Frames, FirstFrameIndex = ending.FirstFrameIndex, Overlap = ending.Overlap,
        });
        page = null; Interlocked.Increment(ref completedPages); file!.Flush(); flushClock.Restart();
    }
    private void FlushDue()
    {
        if (flushClock.ElapsedMilliseconds < options.FlushMilliseconds) return;
        page?.Flush(); file?.Flush(); flushClock.Restart();
    }
    private void WriteContainerHeader()
    {
        byte[] header = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(template, ReplayFiles.Json));
        if (header.Length > 65536) throw new StopException("header-limit", "Full recording header exceeds 64 KiB.");
        using var binary = new BinaryWriter(file!, Encoding.UTF8, true);
        binary.Write(Encoding.ASCII.GetBytes("PEAKRUN1")); binary.Write(header.Length); binary.Write(header); binary.Flush();
    }
    private void WriteIndex()
    {
        var failed = Volatile.Read(ref failure);
        regions.ObserveOutcome(Volatile.Read(ref finalOutcome), nativeStart, nativeEnd);
        var index = new FullReplayIndex
        {
            RegionSummary = regions.Snapshot(),
            Header = CopyHeader(template), Duration = Math.Max(0, nativeEnd - nativeStart), FrameCount = writtenFrames,
            ActorIds = actors.OrderBy(x => x, StringComparer.Ordinal).ToArray(), Pages = pages.ToArray(),
            Complete = failed == null, Reason = completionReason ?? (failed == null ? "manual-stop" : "safety-stop"),
            FaultCode = failed?.Code, Fault = failed == null ? null : Short(failed.Error.Message, 2048),
            GapOver100ms = gap100, GapOver500ms = gap500, LargestGapSeconds = largestGap,
        };
        byte[] encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(index, ReplayFiles.Json));
        if (encoded.Length > 4 * 1024 * 1024) throw new StopException("index-limit", "Full recording seek index exceeds 4 MiB.");
        ((CountedFile)file!).WritingIndex = true;
        using var binary = new BinaryWriter(file, Encoding.UTF8, true);
        binary.Write(encoded); binary.Write((long)encoded.Length); binary.Write(Encoding.ASCII.GetBytes("PEAKEND1")); binary.Flush();
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

    private sealed class Page : IDisposable
    {
        private readonly FullReplayWriter owner;
        private readonly GZipStream gzip;
        private readonly StreamWriter writer;
        private readonly ReplayDeltaCodec.Writer codec;
        private readonly ReplayRules.ValidationMemo validation = new();
        private readonly ContinuousReplayRebaser rebaser;
        private double previousTime = -1, previousEventTime = -1;
        private long previousEventSequence = -1;
        private bool disposed;
        public readonly ContinuousReplayBudget Budget = new();
        public readonly double NativeStart;
        public readonly long FirstFrameIndex, Offset;
        public readonly bool Overlap;
        public double NativeEnd { get; private set; }
        public int Frames { get; private set; }
        public long RawBytes { get; private set; }
        public Page(FullReplayWriter owner, double start, long firstIndex, bool overlap)
        {
            this.owner = owner; NativeStart = NativeEnd = start; FirstFrameIndex = firstIndex; Overlap = overlap;
            codec = new ReplayDeltaCodec.Writer(owner.template.Schema);
            // Internal pages keep the one whole-run clock. Rebasing to this
            // page's first sample would discard events observed between the
            // previous sample and this first sample on a non-overlap boundary.
            Offset = owner.file!.Position; rebaser = new ContinuousReplayRebaser(owner.nativeStart);
            gzip = new GZipStream(owner.file!, CompressionLevel.Fastest, true);
            writer = new StreamWriter(gzip, new UTF8Encoding(false), 65536, true);
            try { Write(JsonConvert.SerializeObject(owner.template, ReplayFiles.Json)); }
            catch { Dispose(); throw; }
        }
        public bool TryWrite(ReplayFrame native)
        {
            var frame = rebaser.Apply(native);
            ReplayRules.ValidateStored(frame, previousTime, validation, maximumStoredTime: FullReplayArchive.MaximumDuration);
            ReplayRules.ValidateVersion(frame, owner.template.Schema);
            if (frame.World.ActiveMapObjects.Length != owner.template.MapObjects.Length) throw new InvalidDataException("Full recording map state changed shape.");
            foreach (var e in frame.Events)
                if (e.Sequence <= previousEventSequence || e.T < previousEventTime) throw new InvalidDataException("Page item events are not ordered.");
            long increment = Budget.Additional(frame);
            if (Budget.Bytes + increment > owner.options.SegmentDecodedByteLimit) return false;
            string line = JsonConvert.SerializeObject(codec.Encode(frame), ReplayFiles.Json);
            long size = Encoding.UTF8.GetByteCount(line) + 1L;
            if (line.Length > ReplayRules.MaxLineCharacters || size > ReplayRules.MaxLineCharacters)
                throw new StopException("record-size-limit", "One full recording record exceeds 8 MiB.");
            if (RawBytes + size + 128 > owner.options.SegmentUncompressedByteLimit) return false;
            Write(line); Budget.Commit(frame, increment);
            foreach (var e in frame.Events) { previousEventSequence = e.Sequence; previousEventTime = e.T; }
            previousTime = frame.T; NativeEnd = native.T; Frames++; return true;
        }
        private void Write(string text)
        {
            long bytes = Encoding.UTF8.GetByteCount(text) + 1L;
            if (bytes > ReplayRules.MaxLineCharacters || RawBytes + bytes > owner.options.SegmentUncompressedByteLimit)
                throw new StopException("page-size-limit", "Full recording page exceeds its byte limit.");
            writer.Write(text); writer.Write('\n'); RawBytes += bytes;
        }
        public void Flush() { writer.Flush(); gzip.Flush(); }
        public void EndFile() { Write("{\"Type\":\"end\"}"); Dispose(); }
        public void Dispose()
        {
            if (disposed) return; disposed = true; Exception? first = null;
            try { writer.Dispose(); } catch (Exception e) { first = e; }
            try { gzip.Dispose(); } catch (Exception e) { first ??= e; }
            if (first != null) throw first;
        }
    }

    private sealed class CountedFile : Stream
    {
        private readonly Stream inner;
        private readonly FullReplayWriter owner;
        public bool WritingIndex;
        public CountedFile(Stream inner, FullReplayWriter owner) { this.inner = inner; this.owner = owner; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            long reserve = WritingIndex ? 0 : Math.Min(4L * 1024 * 1024, owner.options.MaxRunBytes / 4);
            if (Interlocked.Read(ref owner.bytesWritten) + count > owner.options.MaxRunBytes - reserve)
                throw new StopException("run-byte-limit", "Full recording reached its configured disk-byte limit.");
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
