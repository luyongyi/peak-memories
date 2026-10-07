using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class FullReplayArchiveTests
{
    public static void Run(Action<string, Action> test)
    {
        test("full replay streams one file and preserves a seekable complete 180-second timeline", () => InRoot(root =>
        {
            var original = Enumerable.Range(0, 181).Select(i => Frame(500 + i, i)).ToArray();
            string unchanged = Json(original[0]); var header = Header();
            var writer = new FullReplayWriter(root, header); header.Participants[0] = "mutated";
            foreach (var frame in original) Check(writer.TryEnqueue(frame));
            Wait(() => writer.Stats.WrittenFrames == original.Length || writer.Fault != null);
            Check(writer.Fault == null && File.Exists(writer.PartialPath) && !File.Exists(writer.FilePath));
            Check(Directory.GetFiles(root).Length == 1 && Directory.GetDirectories(root).Length == 0);
            var result = Done(writer); Check(result.Status == "completed" && result.WrittenFrames == 181 && result.Duration == 180);
            Check(Directory.GetFiles(root).Single() == result.FilePath && result.FilePath.EndsWith(".peakrun"));
            var info = FullReplayArchive.ReadInfo(result.FilePath);
            Check(info.Complete && info.Duration == 180 && info.FrameCount == 181 && info.Pages.Length == 18);
            Check(info.ActorIds.SequenceEqual(new[] { "actor" }) && info.Header.Participants[0] == "Synthetic");
            var expected = new ContinuousReplayRebaser(500); var unique = new HashSet<double>();
            foreach (int index in Enumerable.Range(0, info.Pages.Length).Reverse())
            {
                var page = info.Pages[index]; var read = FullReplayArchive.ReadPage(result.FilePath, info, index);
                Check(read.Complete && read.Frames.Count == page.FrameCount && read.Frames[0].T == page.Start && read.Duration == page.End);
                foreach (var frame in read.Frames)
                {
                    var source = original.Single(f => f.T == frame.T + 500);
                    Check(JToken.DeepEquals(JToken.Parse(Json(expected.Apply(source))), JToken.Parse(Json(frame))));
                    unique.Add(frame.T);
                }
            }
            Check(unique.Count == 181 && unique.Contains(180) && Json(original[0]) == unchanged);
            var mid = FullReplayArchive.ReadPage(result.FilePath, info, 13).At(135.5);
            Check(mid.Left.T >= 130 && mid.Right.T <= 140);
        }));
        test("full replay bound pages preserve all samples across very large gaps including isolated frames", () => InRoot(root =>
        {
            var writer = new FullReplayWriter(root, Header());
            foreach (double t in new[] { 100d, 101, 500 }) Check(writer.TryEnqueue(Frame(t)));
            var result = Done(writer); var info = FullReplayArchive.ReadInfo(result.FilePath);
            Check(result.Status == "completed" && info.Duration == 400 && info.FrameCount == 3 && info.Pages.Length == 2);
            Check(info.Pages[1].FrameCount == 1 && !info.Pages[1].Overlap && info.Pages[1].FirstFrameIndex == 2);
            Check(FullReplayArchive.ReadPage(result.FilePath, info, 1).Frames.Single().T == 400);
            Check(info.LargestGapSeconds == 399 && info.GapOver500ms == 2);
        }));
        test("full replay zero and one frames cannot create a successful final file", () => InRoot(root =>
        {
            foreach (int count in new[] { 0, 1 })
            {
                var writer = new FullReplayWriter(root, Header()); if (count != 0) Check(writer.TryEnqueue(Frame(0)));
                var result = Done(writer);
                Check(result.Status == "faulted" && result.FaultCode == "insufficient-frames");
                Check(File.Exists(writer.PartialPath) && !File.Exists(writer.FilePath));
            }
        }));
        test("full replay preserves between-sample events at budget-induced non-overlap page boundaries", () => InRoot(root =>
        {
            var first = Simple(100); var second = Simple(101); var third = Simple(102);
            second.Events = new[] { new ItemEvent { Sequence = 17, T = 100.25, Kind = "use", ActorId = "actor", ItemKey = "item" } };
            third.Events = new[] { new ItemEvent { Sequence = 18, T = 101.9, Kind = "throw", ActorId = "actor", ItemKey = "item" } };
            var source = new[] { first, second, third };
            var writer = new FullReplayWriter(root, Header(), new ContinuousReplayOptions { SegmentDecodedByteLimit = 2300 });
            foreach (var frame in source) Check(writer.TryEnqueue(frame));
            var result = Done(writer); Check(result.Status == "completed");
            var info = FullReplayArchive.ReadInfo(result.FilePath);
            Check(info.Pages.Length >= 2 && info.Pages.Skip(1).All(p => !p.Overlap));
            var events = new List<ItemEvent>();
            for (int i = 0; i < info.Pages.Length; i++)
            {
                var clip = FullReplayArchive.ReadPage(result.FilePath, info, i);
                foreach (var frame in clip.Frames) events.AddRange(frame.Events);
            }
            Check(events.Select(e => e.Sequence).SequenceEqual(new long[] { 17, 18 }));
            Check(events[0].T == second.Events[0].T - 100 && events[1].T == third.Events[0].T - 100);
            Check(events[0].T < info.Pages[1].Start);
        }));
        test("full replay preserves events observed before the first sample after a long capture gap", () => InRoot(root =>
        {
            var frames = new[] { Simple(100), Simple(101), Simple(500), Simple(501) };
            frames[2].Events = new[] { new ItemEvent { Sequence = 44, T = 499.75, Kind = "use", ActorId = "actor" } };
            frames[3].Events = new[] { new ItemEvent { Sequence = 45, T = 500.2, Kind = "throw", ActorId = "actor" } };
            var writer = new FullReplayWriter(root, Header()); foreach (var frame in frames) Check(writer.TryEnqueue(frame));
            var result = Done(writer); Check(result.Status == "completed");
            var info = FullReplayArchive.ReadInfo(result.FilePath); Check(info.Pages.Length == 2 && !info.Pages[1].Overlap);
            var page = FullReplayArchive.ReadPage(result.FilePath, info, 1);
            Check(page.Frames[0].T == 400 && page.Frames[0].Events.Single().Sequence == 44 && page.Frames[0].Events[0].T == 399.75);
            Check(page.Frames[1].Events.Single().Sequence == 45 && page.Frames[1].Events[0].T == frames[3].Events[0].T - 100);
        }));
        test("full replay capture interruption drains accepted samples but retains only a faulted partial", () => InRoot(root =>
        {
            var writer = new FullReplayWriter(root, Header());
            Check(writer.TryEnqueue(Frame(100)) && writer.TryEnqueue(Frame(100.1)));
            var task = writer.InterruptAsync("capture-error", new IOException("synthetic interruption"));
            Check(ReferenceEquals(task, writer.CompleteAsync("later")) && !writer.TryEnqueue(Frame(101)));
            var result = Await(task);
            Check(result.Status == "faulted" && result.FaultCode == "capture-interrupted" && result.Reason == "capture-error");
            Check(result.WrittenFrames == 2 && File.Exists(writer.PartialPath) && !File.Exists(writer.FilePath));
            Check(!FullReplayArchive.ReadInfo(writer.PartialPath).Complete);
        }));
        test("full replay bounded producer queue overflow never silently drops an accepted frame", () => InRoot(root =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var writer = new FullReplayWriter(root, Header(), new ContinuousReplayOptions { QueueFrameLimit = 2 }, path =>
            { entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(10))); return NewFile(path); });
            try
            {
                Check(entered.Wait(TimeSpan.FromSeconds(10)));
                var a = Frame(0); var b = Frame(.1);
                Check(writer.TryEnqueue(a, RollingBuffer.Estimate(a)) && writer.TryEnqueue(b, RollingBuffer.Estimate(b)));
                Check(!writer.TryEnqueue(Frame(.2)) && writer.Stats.QueuedFrames == 2 && writer.Stats.QueuedBytes > 0);
            }
            finally { release.Set(); }
            var result = Done(writer);
            Check(result.FaultCode == "queue-overflow" && result.WrittenFrames == 2 && result.AcceptedFrames == 2);
            Check(writer.Stats.QueuedBytes == 0 && !File.Exists(writer.FilePath));
            var huge = new FullReplayWriter(root, Header());
            Check(!huge.TryEnqueue(Frame(0), long.MaxValue) && huge.Stats.QueuedBytes == 0); Done(huge);
        }));
        test("full replay disk write errors retain the single partial without an apparently complete final", () => InRoot(root =>
        {
            var writer = new FullReplayWriter(root, Header(), null, path => new FailedDisk(NewFile(path)));
            writer.TryEnqueue(Frame(0)); writer.TryEnqueue(Frame(.1)); var result = Done(writer);
            Check(result.Status == "faulted" && result.FaultCode == "write-failure");
            Check(File.Exists(writer.PartialPath) && !File.Exists(writer.FilePath) && Directory.GetFiles(root).Length == 1);
        }));
        test("full replay raw and decoded page budgets rotate internally without adding physical files", () => InRoot(root =>
        {
            foreach (bool decoded in new[] { true, false })
            {
                var options = new ContinuousReplayOptions();
                if (decoded) options.SegmentDecodedByteLimit = 4096; else options.SegmentUncompressedByteLimit = 4096;
                var writer = new FullReplayWriter(root, Header(), options);
                for (int i = 0; i < 15; i++) Check(writer.TryEnqueue(Simple(i * .01)));
                var result = Done(writer); Check(result.Status == "completed" && result.WrittenFrames == 15);
                var info = FullReplayArchive.ReadInfo(result.FilePath); Check(info.Pages.Length > 1);
                Check(info.Pages.All(p => decoded ? p.DecodedBytes <= 4096 : p.RawBytes <= 4096));
                Check(info.Pages.Sum(p => p.FrameCount - (p.Overlap ? 1 : 0)) == 15);
                for (int i = 0; i < info.Pages.Length; i++) Check(FullReplayArchive.ReadPage(result.FilePath, info, i).Complete);
            }
            Check(Directory.GetFiles(root).Length == 2 && Directory.GetDirectories(root).Length == 0);
        }));
        test("full replay index rejects corrupt offsets, counts, timing and actor identities before page reads", () => InRoot(root =>
        {
            foreach (Action<FullReplayInfo> corrupt in new Action<FullReplayInfo>[]
            {
                info => info.Pages[0].Offset = long.MaxValue,
                info => info.Pages[0].Length = long.MaxValue,
                info => info.FrameCount++,
                info => info.Pages[0].Start = 1,
                info => info.Pages[0].DecodedBytes = 65L * 1024 * 1024,
                info => info.ActorIds = new[] { "duplicate", "duplicate" },
            })
            {
                string path = Save(root); var info = FullReplayArchive.ReadInfo(path); corrupt(info); RewriteIndex(path, info);
                Reject(() => FullReplayArchive.ReadInfo(path));
            }
        }));
        test("full replay singleton overlap boundary remains readable when an adjacent heavy frame needs its own page", () => InRoot(root =>
        {
            var writer = new FullReplayWriter(root, Header(), new ContinuousReplayOptions { SegmentSeconds = .1, SegmentDecodedByteLimit = 4096 });
            var heavy = Simple(.11);
            heavy.Actors = Enumerable.Range(0, 4).Select(i => new ActorFrame { Id = "actor-" + i }).ToArray();
            Check(writer.TryEnqueue(Simple(0)) && writer.TryEnqueue(Simple(.1)) && writer.TryEnqueue(heavy));
            var result = Done(writer); Check(result.Status == "completed");
            var info = FullReplayArchive.ReadInfo(result.FilePath);
            Check(info.Pages.Any(p => p.Overlap && p.FrameCount == 1) && info.FrameCount == 3);
            for (int i = 0; i < info.Pages.Length; i++) Check(FullReplayArchive.ReadPage(result.FilePath, info, i).Complete);
        }));
        test("full replay page rejects altered gzip content and false page frame metadata", () => InRoot(root =>
        {
            string path = Save(root); var info = FullReplayArchive.ReadInfo(path);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            { file.Position = info.Pages[0].Offset; file.WriteByte(0); }
            Reject(() => FullReplayArchive.ReadPage(path, info, 0));
            string other = Save(root); var bad = FullReplayArchive.ReadInfo(other);
            bad.ActorIds = new[] { "not-present-in-frame" }; RewriteIndex(other, bad);
            var claimed = FullReplayArchive.ReadInfo(other); Reject(() => FullReplayArchive.ReadPage(other, claimed, 0));
            string crcPath = Save(root); var crcInfo = FullReplayArchive.ReadInfo(crcPath); var crcPage = crcInfo.Pages[0];
            using (var file = new FileStream(crcPath, FileMode.Open, FileAccess.ReadWrite))
            {
                file.Position = crcPage.Offset + crcPage.Length - 8; int original = file.ReadByte();
                file.Position--; file.WriteByte((byte)(original ^ 128));
            }
            Reject(() => FullReplayArchive.ReadPage(crcPath, crcInfo, 0));
        }));
        test("full replay metadata rejects missing footer and cancelled reads do not open files", () => InRoot(root =>
        {
            string path = Save(root);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Write)) file.SetLength(file.Length - 1);
            Reject(() => FullReplayArchive.ReadInfo(path));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { FullReplayArchive.ReadInfo("does-not-exist", cancel.Token); }
            catch (OperationCanceledException) { return; }
            throw new Exception("Cancelled metadata read did not throw.");
        }));
    }
    private static ReplayHeader Header() => new() { Scene = "Level_FullArchiveTest", Participants = new[] { "Synthetic" } };
    private static ReplayFrame Simple(double time) => new() { T = time, Actors = new[] { new ActorFrame { Id = "actor", Position = new[] { (float)time, 1f, 2f } } } };
    private static ReplayFrame Frame(double time, int sequence = 0)
    {
        var frame = Simple(time);
        frame.Items = new[] { new ItemFrame { Key = "item", ItemId = 1 } };
        frame.Crates = new[] { new CrateFrame { Key = "crate", Kind = "luggage", Animation = new CrateAnimationFrame { Clip = "open", Anchored = true, AnchorTime = 490, Duration = 2 } } };
        frame.Ropes = new[] { new RopeReplayFrame { Key = "rope", Kind = "rope", Resource = "native-rope", Points = new[] { new RopeReplayPoint() }, Bones = new[] { new RopeReplayPoint() } } };
        frame.Effects = new[] { new EffectReplayFrame { Key = "effect", Resource = "native-effect", AnchorTime = 491 } };
        frame.Audio = new[] { new AudioReplayFrame { Key = "audio", Clip = "native-audio", StartedAt = 492, AnchorTime = 493, Duration = 2, Loop = true } };
        frame.Spawned = new[] { new SpawnedReplayFrame { Key = "cannon", Resource = "scoutcannon_placed", Kind = "scout-cannon", Animation = new CrateAnimationFrame { Clip = "CannonLight", Anchored = true, AnchorTime = 493, Duration = 3 } } };
        frame.Balloons = new[] { new BalloonReplayFrame { Key = "balloon", OwnerId = "actor", Started = 494 } };
        if (sequence > 0) frame.Events = new[] { new ItemEvent { Sequence = sequence, T = time, Kind = "use", ActorId = "actor", ItemKey = "item" } };
        return frame;
    }
    private static string Save(string root)
    {
        var writer = new FullReplayWriter(root, Header()); Check(writer.TryEnqueue(Frame(500)) && writer.TryEnqueue(Frame(501)));
        var result = Done(writer); Check(result.Status == "completed"); return result.FilePath;
    }
    private static void RewriteIndex(string path, FullReplayInfo info)
    {
        byte[] text = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(info, ReplayFiles.Json));
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write); file.Position = info.IndexOffset;
        using var writer = new BinaryWriter(file, Encoding.UTF8, true);
        writer.Write(text); writer.Write((long)text.Length); writer.Write(Encoding.ASCII.GetBytes("PEAKEND1")); writer.Flush(); file.SetLength(file.Position);
    }
    private static string Json(object value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static FullReplayResult Done(FullReplayWriter writer) => Await(writer.CompleteAsync());
    private static FullReplayResult Await(Task<FullReplayResult> task)
    { Check(task.Wait(TimeSpan.FromSeconds(20)), "Full replay worker timed out."); return task.GetAwaiter().GetResult(); }
    private static Stream NewFile(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    private static void Wait(Func<bool> ready)
    { var end = DateTime.UtcNow.AddSeconds(20); while (!ready()) { Check(DateTime.UtcNow < end, "Full replay worker timed out."); Thread.Sleep(1); } }
    private static void Check(bool condition, string reason = "Assertion failed.") { if (!condition) throw new Exception(reason); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is InvalidDataException || e is JsonException || e is IOException || e is ArgumentException) { return; }
        throw new Exception("Corrupt full replay was accepted.");
    }
    private static void InRoot(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "peak-full-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); } finally { Directory.Delete(root, true); }
    }
    private sealed class FailedDisk : Stream
    {
        private readonly Stream inner;
        public FailedDisk(Stream inner) => this.inner = inner;
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Synthetic write failure.");
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
