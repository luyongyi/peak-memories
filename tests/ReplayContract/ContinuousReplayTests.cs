using System.IO.Compression;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class ContinuousReplayTests
{
    public static void Run(Action<string, Action> test)
    {
        test("continuous writer preserves more than 120 seconds in independently readable bounded segments", () => InRoot(root =>
        {
            var header = Header(); var frames = Enumerable.Range(0, 181).Select(i => RichFrame(500 + i, i + 100)).ToArray();
            string before = Json(frames[0]); var writer = new ContinuousReplayWriter(root, header);
            header.Participants[0] = "changed after start"; header.Scene = "changed";
            foreach (var frame in frames) Check(writer.TryEnqueue(frame));
            var result = Complete(writer);
            Check(result.Status == "completed" && result.WrittenFrames == 181 && result.CompletedSegments == 6 && result.UnwrittenFrames == 0);
            var parts = Records(writer).Where(r => (string?)r["Type"] == "segment-complete").ToArray();
            var found = new HashSet<double>(); double? priorEnd = null;
            foreach (var part in parts)
            {
                double start = (double)part["NativeStart"]!, end = (double)part["NativeEnd"]!;
                Check(end - start <= 30 && (priorEnd == null || start == priorEnd)); priorEnd = end;
                var clip = ReplayFiles.Read(Path.Combine(writer.RunDirectory, (string)part["File"]!));
                Check(clip.Complete && clip.Frames.Count == (int)part["FrameCount"]! && clip.Header.Duration == 0 && clip.Header.FrameCount == 0);
                Check(clip.Header.Scene == "Level_ContinuousTests" && clip.Header.Participants[0] == "Synthetic");
                var rebase = new ContinuousReplayRebaser(start);
                var expected = frames.Where(f => f.T >= start && f.T <= end).ToArray();
                for (int i = 0; i < expected.Length; i++)
                {
                    Check(JToken.DeepEquals(JToken.Parse(Json(rebase.Apply(expected[i]))), JToken.Parse(Json(clip.Frames[i]))));
                    found.Add(clip.Frames[i].T + start);
                }
                Check(clip.Frames[0].Ropes.Length == 1 && clip.Frames[0].Spawned.Length == 1 && clip.Frames[0].Balloons.Length == 1);
                Check(clip.Frames[0].Crates[0].Animation!.AnchorTime == 490 - start);
                Check(clip.Frames[0].Effects[0].AnchorTime == 491 - start && clip.Frames[0].Audio[0].StartedAt == 492 - start);
            }
            Check(found.Count == 181 && found.Contains(680) && Json(frames[0]) == before);
            Check(Records(writer).Last().Value<string>("Type") == "run-end");
        }));
        test("continuous frame-cap rollover overlaps only a real boundary and avoids a singleton tail", () => InRoot(root =>
        {
            var writer = new ContinuousReplayWriter(root, Header(), new ContinuousReplayOptions { SegmentFrameLimit = 4 });
            for (int i = 0; i < 10; i++) Check(writer.TryEnqueue(Frame(i * .01)));
            var result = Complete(writer); var parts = Parts(writer);
            Check(result.Status == "completed" && result.WrittenFrames == 10 && parts.Length == 3 && parts.All(p => p.Frames.Count == 4));
            Check(!Directory.EnumerateFiles(writer.RunDirectory, "*.partial").Any());
        }));
        test("continuous zero and one frame runs explicitly fail instead of claiming a playable save", () => InRoot(root =>
        {
            foreach (int count in new[] { 0, 1 })
            {
                var writer = new ContinuousReplayWriter(root, Header()); if (count != 0) Check(writer.TryEnqueue(Frame(100)));
                var result = Complete(writer);
                Check(result.Status == "faulted" && result.FaultCode == "insufficient-frames" && result.CompletedSegments == 0);
                Check(!Directory.EnumerateFiles(writer.RunDirectory, "*.peakreplay").Any());
                Check(Directory.EnumerateFiles(writer.RunDirectory, "*.partial").Count() == count);
            }
        }));
        test("continuous long gaps start a new baseline without bridging the reader time limit", () => InRoot(root =>
        {
            var writer = new ContinuousReplayWriter(root, Header());
            foreach (double t in new[] { 100d, 101, 500, 501 }) Check(writer.TryEnqueue(Frame(t)));
            var result = Complete(writer); var parts = Parts(writer);
            Check(result.Status == "completed" && parts.Length == 2 && parts.All(p => p.Duration == 1));
            Check(result.WrittenFrames == 4 && result.LargestGapSeconds == 399);
            Check(Records(writer).Any(r => r.Value<string>("Type") == "capture-gap" && r.Value<bool>("DiscontinuousSegment")));
        }));
        test("continuous isolated tail after a long gap stays partial and marks the run faulted", () => InRoot(root =>
        {
            var writer = new ContinuousReplayWriter(root, Header());
            foreach (double t in new[] { 100d, 101, 500 }) Check(writer.TryEnqueue(Frame(t)));
            var result = Complete(writer);
            Check(result.Status == "faulted" && result.FaultCode == "incomplete-segment" && result.CompletedSegments == 1);
            Check(Parts(writer).Single().Complete && Directory.EnumerateFiles(writer.RunDirectory, "*.partial").Count() == 1);
        }));
        test("continuous nonblocking frame queue overflow drains accepted frames and reports failure", () => InRoot(root =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var writer = new ContinuousReplayWriter(root, Header(), new ContinuousReplayOptions { QueueFrameLimit = 2 }, path =>
            { entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(10))); return NewFile(path); });
            try
            {
                Check(entered.Wait(TimeSpan.FromSeconds(10)));
                var a = Frame(0); var b = Frame(.1);
                Check(writer.TryEnqueue(a, RollingBuffer.Estimate(a)) && writer.TryEnqueue(b, RollingBuffer.Estimate(b)));
                Check(!writer.TryEnqueue(Frame(.2)));
                Check(writer.Stats.QueuedFrames == 2 && writer.Stats.QueuedBytes > 0 && writer.FailureCode == "queue-overflow");
            }
            finally { release.Set(); }
            var result = Complete(writer);
            Check(result.Status == "faulted" && result.AcceptedFrames == 2 && result.WrittenFrames == 2 && result.CompletedSegments == 1);
            Check(writer.Stats.QueuedBytes == 0 && writer.Stats.QueuedFrames == 0);
        }));
        test("continuous byte queue overflow and huge estimates never wrap reservation accounting", () => InRoot(root =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var first = Frame(0); var budget = new ReplayQueueBudget();
            Check(budget.TryReserve(first, long.MaxValue, 2, out var measured, out _)); long reservedBytes = budget.Current.Bytes; measured!.Dispose();
            var writer = new ContinuousReplayWriter(root, Header(), new ContinuousReplayOptions { QueueByteLimit = Math.Max(1024, reservedBytes + 1) }, path =>
            { entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(10))); return NewFile(path); });
            try
            {
                Check(entered.Wait(TimeSpan.FromSeconds(10)));
                Check(writer.TryEnqueue(first, RollingBuffer.Estimate(first)) && !writer.TryEnqueue(Frame(.1)));
                Check(writer.Stats.QueuedBytes == reservedBytes && writer.Stats.QueuedFrames == 1);
            }
            finally { release.Set(); }
            Check(Complete(writer).FaultCode == "queue-overflow");
            var huge = new ContinuousReplayWriter(root, Header());
            Check(!huge.TryEnqueue(Frame(0), long.MaxValue) && huge.Stats.QueuedBytes == 0);
            Check(Complete(huge).FaultCode == "queue-overflow");
        }));
        test("continuous disk failure preserves previously sealed parts and leaves diagnostic partial", () => InRoot(root =>
        {
            var writer = new ContinuousReplayWriter(root, Header(), new ContinuousReplayOptions { SegmentFrameLimit = 3 }, path =>
                path.EndsWith("segment-000002.peakreplay.partial", StringComparison.Ordinal) ? new FailingStream(NewFile(path)) : NewFile(path));
            for (int i = 0; i < 6; i++) writer.TryEnqueue(Frame(i * .01));
            var result = Complete(writer);
            Check(result.Status == "faulted" && result.FaultCode == "write-failure" && result.CompletedSegments == 1);
            Check(Parts(writer).Single().Complete && File.Exists(Path.Combine(writer.RunDirectory, "segment-000002.peakreplay.partial")));
            Check(Records(writer).Any(r => r.Value<string>("Type") == "segment-incomplete" && r.Value<string>("Reason") == "write-failure"));
        }));
        test("continuous capture interruption seals accepted tail but can never claim completion", () => InRoot(root =>
        {
            var writer = new ContinuousReplayWriter(root, Header());
            Check(writer.TryEnqueue(Frame(100)) && writer.TryEnqueue(Frame(100.1)));
            var task = writer.InterruptAsync("capture-error", new InvalidOperationException("synthetic capture failure"));
            Check(ReferenceEquals(task, writer.CompleteAsync("later-reason")) && !writer.TryEnqueue(Frame(101)));
            var result = Await(task);
            Check(result.FaultCode == "capture-interrupted" && result.Status == "faulted" && result.Reason == "capture-error");
            Check(result.CompletedSegments == 1 && result.WrittenFrames == 2 && Parts(writer).Single().Complete);
        }));
        test("continuous decoded budget rotates before producing a reader-unsafe file", () => InRoot(root =>
        {
            var writer = new ContinuousReplayWriter(root, Header(), new ContinuousReplayOptions { SegmentDecodedByteLimit = 4096 });
            for (int i = 0; i < 15; i++) Check(writer.TryEnqueue(Frame(i * .01)));
            var result = Complete(writer);
            Check(result.Status == "completed" && result.CompletedSegments > 1 && result.WrittenFrames == 15);
            Check(Records(writer).Where(r => r.Value<string>("Type") == "segment-complete").All(r => r.Value<long>("DecodedBytes") <= 4096));
            Check(Parts(writer).All(p => p.Complete));
        }));
        test("continuous uncompressed budget rotates and oversized adjacent frames fail explicitly", () => InRoot(root =>
        {
            var writer = new ContinuousReplayWriter(root, Header(), new ContinuousReplayOptions { SegmentUncompressedByteLimit = 4096 });
            for (int i = 0; i < 15; i++) Check(writer.TryEnqueue(Frame(i * .01)));
            var result = Complete(writer);
            Check(result.Status == "completed" && result.CompletedSegments > 1 && result.WrittenFrames == 15);
            Check(Records(writer).Where(r => r.Value<string>("Type") == "segment-complete").All(r => r.Value<long>("UncompressedBytes") <= 4096));
            Check(Parts(writer).All(p => p.Complete));
            var tooSmall = new ContinuousReplayWriter(root, Header(), new ContinuousReplayOptions { SegmentDecodedByteLimit = 1024 });
            tooSmall.TryEnqueue(Frame(0)); Check(Complete(tooSmall).FaultCode == "segment-size-limit");
        }));
        test("continuous budget counts reintroduced shared entity while rebase leaves source anchors immutable", () =>
        {
            var frame = RichFrame(500, 100); var budget = new ContinuousReplayBudget();
            long initial = budget.Additional(frame); budget.Commit(frame, initial);
            Check(budget.Additional(frame) == RollingBuffer.EstimateFrame(frame));
            var absent = Frame(501); budget.Commit(absent, budget.Additional(absent));
            Check(budget.Additional(frame) == initial);
            var rebaser = new ContinuousReplayRebaser(500); var left = rebaser.Apply(frame); var right = rebaser.Apply(frame);
            Check(ReferenceEquals(left.Crates, right.Crates) && ReferenceEquals(left.Audio, right.Audio));
            Check(left.Events[0].Sequence == 100 && left.Events[0].T == 0 && frame.Events[0].T == 500);
            Check(left.Spawned[0].Animation!.AnchorTime == -7 && frame.Spawned[0].Animation!.AnchorTime == 493);
            Check(left.Balloons[0].Started == -6 && frame.Balloons[0].Started == 494);
            Check(ReferenceEquals(left.Actors, frame.Actors) && ReferenceEquals(left.Items, frame.Items));
        });
        test("continuous detailed gaps are bounded but final counts remain exact", () => InRoot(root =>
        {
            var writer = new ContinuousReplayWriter(root, Header());
            for (int i = 0; i < 300; i++) Check(writer.TryEnqueue(Frame(i)));
            var result = Complete(writer); var records = Records(writer);
            Check(result.Status == "completed" && result.GapOver100ms == 299 && result.GapOver500ms == 299);
            Check(records.Count(r => r.Value<string>("Type") == "capture-gap") == 256);
            Check(records.Single(r => r.Value<string>("Type") == "capture-gap-summary").Value<bool>("DetailsTruncated"));
            Check(records.Last().Value<long>("GapOver100ms") == 299);
            Check(new FileInfo(Path.Combine(writer.RunDirectory, "manifest.ndjson")).Length < 1024 * 1024);
        }));
    }

    private static ReplayHeader Header() => new() { Scene = "Level_ContinuousTests", Participants = new[] { "Synthetic" } };
    private static ReplayFrame Frame(double time) => new() { T = time, Actors = new[] { new ActorFrame { Id = "actor", Name = "Synthetic", Position = new[] { (float)time, 1f, 2f } } } };
    private static ReplayFrame RichFrame(double time, int sequence)
    {
        var f = Frame(time);
        f.Items = new[] { new ItemFrame { Key = "item", ItemId = 4, Name = "Synthetic", Pose = new ObjectPose { Position = new[] { 1f, 2f, 3f } } } };
        f.Crates = new[] { new CrateFrame { Key = "crate", Kind = "luggage", Open = true, Animation = new CrateAnimationFrame { Clip = "open", Anchored = true, AnchorTime = 490, Duration = 2 } } };
        f.Ropes = new[] { new RopeReplayFrame { Key = "rope", Kind = "rope", Resource = "native-rope", Visible = true, Points = new[] { new RopeReplayPoint() }, Bones = new[] { new RopeReplayPoint() } } };
        f.Effects = new[] { new EffectReplayFrame { Key = "effect", Resource = "native-effect", AnchorTime = 491, Loop = true } };
        f.Audio = new[] { new AudioReplayFrame { Key = "audio", Clip = "native-audio", StartedAt = 492, AnchorTime = 493, Duration = 2, Loop = true } };
        f.Spawned = new[] { new SpawnedReplayFrame { Key = "cannon", Kind = "scout-cannon", Resource = "scoutcannon_placed", Animation = new CrateAnimationFrame { Clip = "CannonLight", Anchored = true, AnchorTime = 493, Duration = 3 } } };
        f.Balloons = new[] { new BalloonReplayFrame { Key = "balloon", OwnerId = "actor", Started = 494 } };
        f.Events = new[] { new ItemEvent { Sequence = sequence, T = time, Kind = "use", ActorId = "actor", ItemKey = "item" } };
        return f;
    }
    private static string Json(object value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static JObject[] Records(ContinuousReplayWriter writer) => File.ReadAllLines(Path.Combine(writer.RunDirectory, "manifest.ndjson")).Select(JObject.Parse).ToArray();
    private static ReplayClip[] Parts(ContinuousReplayWriter writer) => Records(writer).Where(r => r.Value<string>("Type") == "segment-complete")
        .Select(r => ReplayFiles.Read(Path.Combine(writer.RunDirectory, r.Value<string>("File")!))).ToArray();
    private static ContinuousReplayResult Complete(ContinuousReplayWriter writer) => Await(writer.CompleteAsync());
    private static ContinuousReplayResult Await(Task<ContinuousReplayResult> task)
    { Check(task.Wait(TimeSpan.FromSeconds(20)), "Background writer did not complete."); return task.GetAwaiter().GetResult(); }
    private static Stream NewFile(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    private static void Check(bool condition, string message = "Assertion failed.") { if (!condition) throw new Exception(message); }
    private static void InRoot(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "peak-continuous-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
    private sealed class FailingStream : Stream
    {
        private readonly Stream inner;
        public FailingStream(Stream inner) => this.inner = inner;
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Synthetic disk failure.");
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
