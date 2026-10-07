using System.Collections.Concurrent;
using PeakReplayLab;

internal static class ReplayQueueBudgetTests
{
    public static void Run(Action<string, Action> test)
    {
        test("queue shared immutable creature directories do not grow by a full snapshot per frame", () =>
        {
            var shared = Creatures(); var frames = Enumerable.Range(0, 312).Select(i => Frame(i / 60d, shared)).ToArray();
            long oldTotal = frames.Sum(RollingBuffer.Estimate); var budget = new ReplayQueueBudget();
            var leases = new List<ReplayQueueBudget.Reservation>();
            foreach (var frame in frames)
            { Check(budget.TryReserve(frame, 128L * 1024 * 1024, 360, out var lease, out _)); leases.Add(lease!); }
            Check(oldTotal > 128L * 1024 * 1024 && budget.Current.Bytes < oldTotal / 20 && budget.Current.Frames == 312);
            long remaining = budget.Current.Bytes; leases[0].Dispose(); Check(budget.Current.Bytes < remaining && budget.Current.Bytes > remaining / 2);
            foreach (var lease in leases) lease.Dispose(); budget.ClearBaseline(); Check(budget.Current.Bytes == 0 && budget.Current.Frames == 0);
        });
        test("queue shares nested poses renderer materials and lights across changed wrappers", () =>
        {
            var sample = Creatures()[0]; var budget = new ReplayQueueBudget();
            var leases = new List<ReplayQueueBudget.Reservation>(); long initial = 0;
            for (int i = 0; i < 20; i++)
            {
                var changed = new CreatureReplayFrame { Key = sample.Key, Kind = sample.Kind, Resource = sample.Resource,
                    SourcePath = sample.SourcePath, State = i % 2, Pose = sample.Pose, Visuals = sample.Visuals };
                Check(budget.TryReserve(Frame(i / 60d, new[] { changed }), long.MaxValue, 360, out var lease, out _)); leases.Add(lease!);
                if (i == 0) initial = budget.Current.Bytes;
            }
            Check(budget.Current.Bytes < initial + 20 * 3000);
            foreach (var lease in leases) lease.Dispose(); budget.ClearBaseline(); Check(budget.Current.Bytes == 0);
        });
        test("queue true unique snapshot byte overflow rolls back every acquired child", () =>
        {
            var budget = new ReplayQueueBudget(); var a = Frame(0, Creatures(8));
            Check(budget.TryReserve(a, long.MaxValue, 360, out var lease, out _)); long retained = budget.Current.Bytes;
            Check(!budget.TryReserve(Frame(.1, Creatures(8)), retained + 1, 360, out var rejected, out var attempt));
            Check(rejected == null && attempt.Bytes > retained + 1 && budget.Current.Bytes == retained && budget.Current.Frames == 1);
            lease!.Dispose(); Check(budget.Current.Bytes == 0 && budget.Current.Frames == 0);
        });
        test("queue frame bound includes all leases and release permits the next frame", () =>
        {
            var budget = new ReplayQueueBudget(); var shared = Creatures(1);
            Check(budget.TryReserve(Frame(0, shared), long.MaxValue, 2, out var a, out _));
            Check(budget.TryReserve(Frame(.1, shared), long.MaxValue, 2, out var b, out _));
            Check(!budget.TryReserve(Frame(.2, shared), long.MaxValue, 2, out _, out var rejected) && rejected.Frames == 3);
            a!.Dispose(); a.Dispose(); Check(budget.Current.Frames == 1);
            Check(budget.TryReserve(Frame(.3, shared), long.MaxValue, 2, out var c, out _));
            b!.Dispose(); c!.Dispose(); budget.ClearBaseline(); Check(budget.Current.Bytes == 0 && budget.Current.Frames == 0);
        });
        test("queue producer and worker releases race without negative bytes or leaked graph references", () =>
        {
            var budget = new ReplayQueueBudget(); var pending = new ConcurrentQueue<ReplayQueueBudget.Reservation>();
            var shared = Creatures(8); int done = 0;
            var worker = Task.Run(() =>
            {
                while (Volatile.Read(ref done) == 0 || !pending.IsEmpty)
                { if (pending.TryDequeue(out var lease)) lease.Dispose(); else Thread.Yield(); }
            });
            try
            {
                for (int i = 0; i < 1200; i++)
                {
                    var frame = Frame(i / 60d, shared);
                    ReplayQueueBudget.Reservation? lease;
                    while (!budget.TryReserve(frame, 16L * 1024 * 1024, 32, out lease, out _)) Thread.Yield();
                    // Reserve once more only through the already obtained lease.
                    Check(budget.Current.Bytes >= 0 && budget.Current.Frames <= 32);
                    pending.Enqueue(lease!);
                }
            }
            finally { Volatile.Write(ref done, 1); Check(worker.Wait(TimeSpan.FromSeconds(10))); }
            budget.ClearBaseline(); Check(budget.Current.Bytes == 0 && budget.Current.Frames == 0 && budget.Current.PeakFrames <= 32);
        });
        test("queue idle capture baseline retains shared directories without repeating full graph allocation", () =>
        {
            var budget = new ReplayQueueBudget(); var shared = Creatures();
            Check(budget.TryReserve(Frame(0, shared), long.MaxValue, 1, out var first, out _)); first!.Dispose();
            long baselineBytes = budget.Current.Bytes;
            Check(baselineBytes > 0 && budget.Current.Frames == 0);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 1; i <= 1000; i++)
            {
                Check(budget.TryReserve(Frame(i / 60d, shared), baselineBytes + 8192, 1, out var lease, out _));
                Check(budget.Current.Frames == 1 && budget.Current.Bytes <= baselineBytes + 8192);
                lease!.Dispose(); Check(budget.Current.Frames == 0 && budget.Current.Bytes == baselineBytes);
            }
            // A full directory walk allocates hundreds of KiB. A drained live
            // queue must allocate only the small changed frame/world wrappers.
            Check(GC.GetAllocatedBytesForCurrentThread() - before < 16L * 1024 * 1024);
            budget.ClearBaseline(); budget.ClearBaseline(); Check(budget.Current.Bytes == 0);
        });
        test("queue reclaims an idle baseline once when a different snapshot needs the same byte budget", () =>
        {
            var budget = new ReplayQueueBudget();
            Check(budget.TryReserve(Frame(0, Creatures(8)), long.MaxValue, 1, out var a, out _)); a!.Dispose();
            long limit = budget.Current.Bytes + 128;
            Check(budget.TryReserve(Frame(.1, Creatures(8)), limit, 1, out var b, out _));
            Check(budget.Current.Bytes <= limit && budget.Current.Frames == 1);
            b!.Dispose(); Check(budget.Current.Bytes > 0 && budget.Current.Frames == 0);
            budget.ClearBaseline(); Check(budget.Current.Bytes == 0);
        });
        test("queue baseline eviction never releases a live worker capture or bypasses its byte bound", () =>
        {
            var budget = new ReplayQueueBudget();
            Check(budget.TryReserve(Frame(0, Creatures(8)), long.MaxValue, 2, out var worker, out _));
            long retained = budget.Current.Bytes;
            Check(!budget.TryReserve(Frame(.1, Creatures(8)), retained + 128, 2, out _, out var attempt));
            Check(attempt.Bytes > retained + 128 && budget.Current.Bytes == retained && budget.Current.Frames == 1);
            budget.ClearBaseline(); Check(budget.Current.Bytes == retained && budget.Current.Frames == 1);
            worker!.Dispose(); Check(budget.Current.Bytes == 0 && budget.Current.Frames == 0);
        });
        test("queue rejects a cached frame under a lowered byte limit and rolls its baseline back completely", () =>
        {
            var budget = new ReplayQueueBudget(); var frame = Frame(0, Creatures(8));
            Check(budget.TryReserve(frame, long.MaxValue, 1, out var a, out _)); a!.Dispose();
            Check(!budget.TryReserve(frame, 1024, 1, out _, out var attempt));
            Check(attempt.Bytes > 1024 && budget.Current.Bytes == 0 && budget.Current.Frames == 0);
        });
        test("queue identical frame reservations keep independent worker leases after baseline cleanup", () =>
        {
            var budget = new ReplayQueueBudget(); var frame = Frame(0, Creatures(8));
            Check(budget.TryReserve(frame, long.MaxValue, 2, out var a, out _)); long bytes = budget.Current.Bytes;
            Check(budget.TryReserve(frame, bytes, 2, out var b, out _));
            Check(budget.Current.Bytes == bytes && budget.Current.Frames == 2);
            budget.ClearBaseline(); a!.Dispose(); a.Dispose();
            Check(budget.Current.Bytes == bytes && budget.Current.Frames == 1);
            b!.Dispose(); Check(budget.Current.Bytes == 0 && budget.Current.Frames == 0);
        });
        test("full and segmented writers accept a stalled shared directory without invented byte overflow", () => InRoot(root =>
        {
            foreach (bool full in new[] { true, false })
            {
                using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                var shared = Creatures(16); var frames = Enumerable.Range(0, 24).Select(i => Frame(i / 60d, shared)).ToArray();
                var measure = new ReplayQueueBudget(); Check(measure.TryReserve(frames[0], long.MaxValue, 360, out var measured, out _));
                long limit = Math.Max(1024, measure.Current.Bytes + 128000); measured!.Dispose();
                Check(frames.Sum(RollingBuffer.Estimate) > limit);
                var options = new ContinuousReplayOptions { QueueByteLimit = limit, QueueFrameLimit = 360 };
                Func<string, Stream> open = path => { entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(10))); return NewFile(path); };
                var single = full ? new FullReplayWriter(root, Header(), options, open) : null;
                var segmented = full ? null : new ContinuousReplayWriter(root, Header(), options, open);
                try
                {
                    Check(entered.Wait(TimeSpan.FromSeconds(10)));
                    foreach (var frame in frames) Check(full ? single!.TryEnqueue(frame) : segmented!.TryEnqueue(frame));
                    var stats = full ? single!.Stats : segmented!.Stats;
                    Check(stats.QueuedFrames == 24 && stats.QueuedBytes <= limit);
                }
                finally { release.Set(); }
                if (full) { var result = Await(single!.CompleteAsync()); Check(result.Status == "completed" && result.WrittenFrames == 24 && single.Stats.QueuedBytes == 0); }
                else { var result = Await(segmented!.CompleteAsync()); Check(result.Status == "completed" && result.WrittenFrames == 24 && segmented.Stats.QueuedBytes == 0); }
            }
        }));
        test("full and segmented writer reservations include the frame currently blocked inside serialization", () => InRoot(root =>
        {
            foreach (bool full in new[] { true, false })
            {
                using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                int fullWrites = 0; var shared = Creatures();
                Func<string, Stream> open = path =>
                {
                    bool payload = full || path.EndsWith(".peakreplay.partial", StringComparison.Ordinal);
                    return payload ? new BlockingPayload(NewFile(path), entered, release, () => !full || Interlocked.Increment(ref fullWrites) > 3) : NewFile(path);
                };
                var options = new ContinuousReplayOptions { QueueFrameLimit = 2 };
                var single = full ? new FullReplayWriter(root, Header(), options, open) : null;
                var segmented = full ? null : new ContinuousReplayWriter(root, Header(), options, open);
                try
                {
                    Check(full ? single!.TryEnqueue(Frame(0, shared)) : segmented!.TryEnqueue(Frame(0, shared)));
                    Check(entered.Wait(TimeSpan.FromSeconds(10)));
                    var before = full ? single!.Stats : segmented!.Stats;
                    Check(before.QueuedFrames == 1 && before.QueuedBytes > 0 && before.WrittenFrames == 0);
                    Check(full ? single!.TryEnqueue(Frame(.1, shared)) : segmented!.TryEnqueue(Frame(.1, shared)));
                    Check(!(full ? single!.TryEnqueue(Frame(.2, shared)) : segmented!.TryEnqueue(Frame(.2, shared))));
                }
                finally { release.Set(); }
                if (full) { var result = Await(single!.CompleteAsync()); Check(result.FaultCode == "queue-overflow" && result.WrittenFrames == 2 && result.Fault!.Contains("3/2 frames") && single.Stats.QueuedBytes == 0); }
                else { var result = Await(segmented!.CompleteAsync()); Check(result.FaultCode == "queue-overflow" && result.WrittenFrames == 2 && result.Fault!.Contains("3/2 frames") && segmented.Stats.QueuedBytes == 0); }
            }
        }));
        test("full and segmented writers reject genuinely different snapshots during blocked I/O and release every byte", () => InRoot(root =>
        {
            foreach (bool full in new[] { true, false })
            {
                using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                int writes = 0; var first = Frame(0, Creatures()); var measure = new ReplayQueueBudget();
                Check(measure.TryReserve(first, long.MaxValue, 2, out var sample, out _));
                long limit = measure.Current.Bytes + 8192; sample!.Dispose(); measure.ClearBaseline();
                Func<string, Stream> open = path =>
                {
                    bool payload = full || path.EndsWith(".peakreplay.partial", StringComparison.Ordinal);
                    return payload ? new BlockingPayload(NewFile(path), entered, release, () => !full || Interlocked.Increment(ref writes) > 3) : NewFile(path);
                };
                var options = new ContinuousReplayOptions { QueueByteLimit = limit, QueueFrameLimit = 2 };
                var single = full ? new FullReplayWriter(root, Header(), options, open) : null;
                var segmented = full ? null : new ContinuousReplayWriter(root, Header(), options, open);
                try
                {
                    Check(full ? single!.TryEnqueue(first) : segmented!.TryEnqueue(first));
                    Check(entered.Wait(TimeSpan.FromSeconds(10)));
                    Check(!(full ? single!.TryEnqueue(Frame(.1, Creatures())) : segmented!.TryEnqueue(Frame(.1, Creatures()))));
                    var stats = full ? single!.Stats : segmented!.Stats;
                    Check(stats.QueuedFrames == 1 && stats.QueuedBytes > 0 && stats.QueuedBytes <= limit);
                }
                finally { release.Set(); }
                if (full) { var result = Await(single!.CompleteAsync()); Check(result.FaultCode == "queue-overflow" && result.WrittenFrames == 1 && result.UnwrittenFrames == 0 && single.Stats.QueuedBytes == 0); }
                else { var result = Await(segmented!.CompleteAsync()); Check(result.FaultCode == "queue-overflow" && result.WrittenFrames == 1 && result.UnwrittenFrames == 0 && segmented.Stats.QueuedBytes == 0); }
            }
        }));
        test("full and segmented writer completion races release the capture baseline after accepted frames drain", () => InRoot(root =>
        {
            foreach (bool full in new[] { true, false })
                for (int run = 0; run < 20; run++)
                {
                    var shared = Creatures(1); var single = full ? new FullReplayWriter(root, Header()) : null;
                    var segmented = full ? null : new ContinuousReplayWriter(root, Header());
                    Check(full ? single!.TryEnqueue(Frame(0, shared)) : segmented!.TryEnqueue(Frame(0, shared)));
                    Check(full ? single!.TryEnqueue(Frame(.1, shared)) : segmented!.TryEnqueue(Frame(.1, shared)));
                    using var start = new ManualResetEventSlim();
                    var producer = Task.Run(() =>
                    {
                        start.Wait();
                        for (int i = 2; i < 80; i++)
                            if (!(full ? single!.TryEnqueue(Frame(i / 10d, shared)) : segmented!.TryEnqueue(Frame(i / 10d, shared)))) break;
                    });
                    start.Set();
                    if (full)
                    {
                        var result = Await(single!.CompleteAsync()); Check(producer.Wait(TimeSpan.FromSeconds(10)));
                        Check(result.Status == "completed" && result.AcceptedFrames == result.WrittenFrames && single.Stats.QueuedBytes == 0 && single.Stats.QueuedFrames == 0);
                    }
                    else
                    {
                        var result = Await(segmented!.CompleteAsync()); Check(producer.Wait(TimeSpan.FromSeconds(10)));
                        Check(result.Status == "completed" && result.AcceptedFrames == result.WrittenFrames && segmented.Stats.QueuedBytes == 0 && segmented.Stats.QueuedFrames == 0);
                    }
                }
        }));
    }
    private static ReplayHeader Header() => new() { Scene = "Level_QueueBudgetTest" };
    private static ReplayFrame Frame(double time, CreatureReplayFrame[] creatures) => new() { T = time, Creatures = creatures };
    private static CreatureReplayFrame[] Creatures(int count = 256) => Enumerable.Range(0, count).Select(i => new CreatureReplayFrame
    {
        Key = "spider:" + i, Kind = "spider", Resource = "spider",
        Pose = new ObjectPose { Nodes = Enumerable.Range(0, 8).Select(n => new NodePose { Path = "0:Body/" + n + ":Leaf" }).ToArray() },
        Visuals = new[] { new NativeRendererFrame { Path = ".", Materials = new[] { new NativeMaterialFrame { Name = "Native fungus", Shader = "Native shader",
            Properties = new[] { new NativeShaderPropertyFrame { Name = "_Color", Kind = 1, Values = new[] { 1f, .5f, .2f, 1f } } } } } } },
    }).ToArray();
    private static Stream NewFile(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    private static T Await<T>(Task<T> task) { Check(task.Wait(TimeSpan.FromSeconds(20))); return task.Result; }
    private static void InRoot(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "PeakReplayQueue-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); } finally { Directory.Delete(root, true); }
    }
    private static void Check(bool value) { if (!value) throw new Exception("Queue budget assertion failed."); }
    private sealed class BlockingPayload : Stream
    {
        private readonly Stream inner; private readonly ManualResetEventSlim entered, release; private readonly Func<bool> block;
        public BlockingPayload(Stream inner, ManualResetEventSlim entered, ManualResetEventSlim release, Func<bool> block)
        { this.inner = inner; this.entered = entered; this.release = release; this.block = block; }
        public override void Write(byte[] bytes, int offset, int count) { if (block()) { entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(10))); } inner.Write(bytes, offset, count); }
        public override void Flush() => inner.Flush(); protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long length) => throw new NotSupportedException();
    }
}
