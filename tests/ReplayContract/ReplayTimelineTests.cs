using System.Collections.Concurrent;
using System.Diagnostics;
using PeakReplayLab;

internal static class ReplayTimelineTests
{
    public static void Run(Action<string, Action> test)
    {
        test("continuous playback keeps a whole-run clock beyond rolling clip limits", () =>
        {
            using var timeline = Create(180);
            Check(timeline.Duration == 1800 && timeline.FrameCount == 7201);
            var sample = Ready(timeline, 1705.125);
            Check(sample.Left.T == 1705 && sample.Right.T == 1705.25 && sample.Mix == .5f);
            Check(timeline.Header.Duration == 0, "Timeline metadata must not mutate the bounded legacy header.");
        });
        test("cache miss never waits for disk and resumes the requested global time", () =>
        {
            using var release = new ManualResetEventSlim();
            using var timeline = Create(4, (index, token) => { release.Wait(token); return Page(index); });
            var watch = Stopwatch.StartNew();
            Check(!timeline.TrySample(25.125, out _));
            Check(watch.ElapsedMilliseconds < 200, "Main thread blocked on a page decoder.");
            release.Set();
            Check(Ready(timeline, 25.125).Left.T == 25);
        });
        test("forward boundary keeps world time and interpolation continuous", () =>
        {
            using var timeline = Create(4);
            Check(Ready(timeline, 9.875).Left.T == 9.75);
            Check(Ready(timeline, 10).Left.T == 10);
            var next = Ready(timeline, 10.125);
            Check(next.Left.T == 10 && next.Right.T == 10.25 && next.Mix == .5f);
        });
        test("backward seeks reload only bounded neighboring pages", () =>
        {
            var calls = new ConcurrentQueue<int>();
            using var timeline = Create(100, (index, token) => { calls.Enqueue(index); return Page(index); });
            foreach (double time in new[] { 2d, 805d, 415d, 15d, 995d, 5d })
            {
                Check(Ready(timeline, time).Left.T == time);
                Check(timeline.CachedPageCount <= 4 && timeline.PendingPageCount <= 1);
            }
            Check(calls.Count < 30, "Seeking decoded the entire recording.");
        });
        test("history lookup neither schedules IO nor evicts playback pages", () =>
        {
            var calls = new ConcurrentQueue<int>();
            using var timeline = Create(8, (index, token) => { calls.Enqueue(index); return Page(index); });
            Ready(timeline, 45); DrainPrefetch(timeline, 45);
            int count = calls.Count, cached = timeline.CachedPageCount;
            for (int i = 0; i < 100; i++) Check(!timeline.TryHistory(1, out _));
            Check(calls.Count == count && timeline.CachedPageCount == cached);
            Check(timeline.TryHistory(35, out var previous) && previous.Left.T == 35);
        });
        test("seek remains buffered until previous page history is present", () =>
        {
            using var previousReady = new ManualResetEventSlim();
            using var timeline = Create(5, (index, token) =>
            { if (index == 2) previousReady.Wait(token); return Page(index); });
            var watch = Stopwatch.StartNew();
            while (timeline.CachedPageCount == 0 && watch.ElapsedMilliseconds < 3000)
            { Check(!timeline.TrySample(35, out _)); Thread.Sleep(1); }
            Check(!timeline.TrySample(35, out _));
            previousReady.Set();
            Check(Ready(timeline, 35).Left.T == 35 && timeline.TryHistory(25, out _));
        });
        test("rapid seeks cancel stale IO and never run multiple page readers", () =>
        {
            using var started = new ManualResetEventSlim();
            int active = 0, maximum = 0, cancelled = 0;
            using var timeline = Create(20, (index, token) =>
            {
                int current = Interlocked.Increment(ref active);
                maximum = Math.Max(maximum, current);
                try
                {
                    if (index == 0)
                    {
                        started.Set();
                        try { token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); }
                        catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
                    }
                    return Page(index);
                }
                finally { Interlocked.Decrement(ref active); }
            });
            Check(started.Wait(3000));
            Check(!timeline.TrySample(155, out _));
            Check(Ready(timeline, 55).Left.T == 55);
            Check(maximum == 1 && cancelled == 1);
        });
        test("dispose cancels an inflight read without waiting", () =>
        {
            using var started = new ManualResetEventSlim();
            using var accessHandle = new ManualResetEventSlim();
            using var cancelled = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            var timeline = Create(2, (index, token) =>
            {
                started.Set();
                // Guarantee disposal happens after the delegate starts but
                // before its first access to the source-owned kernel handle.
                accessHandle.Wait();
                try
                {
                    token.WaitHandle.WaitOne();
                    if (token.IsCancellationRequested) cancelled.Set();
                    token.ThrowIfCancellationRequested(); return Page(index);
                }
                finally { finished.Set(); }
            });
            Check(started.Wait(3000)); var watch = Stopwatch.StartNew();
            timeline.Dispose();
            long elapsed = watch.ElapsedMilliseconds;
            accessHandle.Set();
            Check(elapsed < 200 && cancelled.Wait(3000) && finished.Wait(3000));
            timeline.Dispose(); Check(timeline.CachedPageCount == 0);
        });
        test("recent event tail crosses a page boundary without duplicate events", () =>
        {
            using var timeline = Create(4, (index, token) =>
            {
                var page = Page(index);
                foreach (var frame in page.Frames)
                    if (frame.T == 9.5 || frame.T == 10 || frame.T == 10.5)
                        frame.Events = new[] { new ItemEvent { T = frame.T, Sequence = (long)(frame.T * 10), ActorId = "actor" } };
                return page;
            });
            Ready(timeline, 11);
            var events = new List<ItemEvent>(); timeline.FillRecentEvents(11, "actor", events);
            Check(events.Select(e => e.T).SequenceEqual(new[] { 10.5, 10, 9.5 }));
            timeline.FillRecentEvents(9.75, "actor", events);
            Check(events.Count == 1 && events[0].T == 9.5);
        });
        test("recent events filter actors and merge global events in bounded order", () =>
        {
            var clip = Page(0);
            clip.Frames[0].Events = new[]
            {
                new ItemEvent { T = 1, Sequence = 1, ActorId = "actor" },
                new ItemEvent { T = 2, Sequence = 2, ActorId = "other" },
                new ItemEvent { T = 3, Sequence = 3, ActorId = "" },
                new ItemEvent { T = 4, Sequence = 4, ActorId = "actor" },
                new ItemEvent { T = 5, Sequence = 5, ActorId = "actor" }
            };
            using var timeline = new MemoryReplayTimeline(clip); var events = new List<ItemEvent>();
            timeline.FillRecentEvents(5, "actor", events);
            Check(events.Select(e => e.Sequence).SequenceEqual(new long[] { 5, 4, 3 }));
            timeline.FillRecentEvents(10, "actor", events); Check(events.Count == 0);
        });
        test("capture gap holds last pose instead of inventing interpolation or history", () =>
        {
            using var timeline = new PagedReplayTimeline(new ReplayHeader(), 30, 82, new[] { "actor" },
                new[] { new ReplayPageRange(0, 10), new ReplayPageRange(20, 30) },
                (index, token) => Page(index == 0 ? 0 : 2));
            var sample = Ready(timeline, 15);
            Check(sample.Left.T == 10 && sample.Mix == 0);
            Check(!timeline.TryHistory(15, out _));
            Check(Ready(timeline, 20).Left.T == 20);
        });
        test("two previous pages retain the native twelve-second effects lookback", () =>
        {
            using var timeline = Create(8);
            Ready(timeline, 40.25);
            Check(timeline.TryHistory(28.25, out var historical) && historical.Left.T == 28.25);
            Check(timeline.CachedPageCount <= 4);
        });
        test("singleton boundary pages are valid and do not reset the run clock", () =>
        {
            using var timeline = new PagedReplayTimeline(new ReplayHeader(), 20, 81, new[] { "actor" },
                new[] { new ReplayPageRange(0, 10), new ReplayPageRange(10, 10), new ReplayPageRange(10, 20) },
                (index, token) =>
                {
                    if (index != 1) return Page(index == 0 ? 0 : 1);
                    var singleton = new ReplayClip { Complete = true };
                    singleton.Frames.Add(new ReplayFrame { T = 10 }); return singleton;
                });
            Check(Ready(timeline, 10.125).Left.T == 10);
            Check(Ready(timeline, 9.75).Left.T == 9.75);
        });
        test("nonoverlapping page boundary interpolates only neighboring real samples", () =>
        {
            using var timeline = new PagedReplayTimeline(new ReplayHeader(), 20, 81, new[] { "actor" },
                new[] { new ReplayPageRange(0, 10), new ReplayPageRange(10.25, 20) },
                (index, token) => { var page = Page(index); if (index == 1) page.Frames.RemoveAt(0); return page; });
            var sample = Ready(timeline, 10.125);
            Check(sample.Left.T == 10 && sample.Right.T == 10.25 && sample.Mix == .5f);
            Check(timeline.TryHistory(10.125, out var history) && history.Left.T == 10 && history.Mix == .5f);
        });
        test("global timestamp regrouping tolerates sub-microsecond page endpoint rounding", () =>
        {
            using var timeline = Create(3, (index, token) =>
            {
                var page = Page(index);
                page.Frames[0].T += 1e-10;
                page.Frames[page.Frames.Count - 1].T -= 1e-10;
                return page;
            });
            Check(Ready(timeline, 20.125).Left.T > 20 && Ready(timeline, 20.125).Left.T < 20.000001);
        });
        test("failed required decode surfaces a controlled error", () =>
        {
            using var timeline = Create(3, (index, token) => throw new InvalidDataException("bad page"));
            bool failed = false;
            try { Ready(timeline, 2); } catch (InvalidDataException) { failed = true; }
            Check(failed);
        });
        test("page index mismatch is rejected before presentation", () =>
        {
            using var timeline = Create(3, (index, token) => Page(index + 1));
            bool failed = false;
            try { Ready(timeline, 2); } catch (InvalidDataException) { failed = true; }
            Check(failed);
        });
        test("memory adapter preserves ordinary replay pose and clock behavior", () =>
        {
            var clip = Page(0); using var timeline = new MemoryReplayTimeline(clip);
            Check(timeline.Complete && timeline.FrameCount == 41 && timeline.ActorIds.SequenceEqual(new[] { "actor" }));
            Check(timeline.TrySample(4.125, out var actual)); var expected = clip.At(4.125);
            Check(ReferenceEquals(actual.Left, expected.Left) && actual.Mix == expected.Mix);
        });
        test("real single-file playback seeks fractional timestamp boundaries in either direction", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "peak-timeline-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                const double origin = 17001.333333333;
                var writer = new FullReplayWriter(root, new ReplayHeader { Scene = "Level_TimelineTest" },
                    new ContinuousReplayOptions { SegmentSeconds = 1.25 });
                for (int i = 0; i <= 140; i++)
                    Check(writer.TryEnqueue(new ReplayFrame { T = origin + i / 7d, Actors = new[] { new ActorFrame { Id = "actor" } } }));
                var completion = writer.CompleteAsync(); Check(completion.Wait(10000));
                var result = completion.GetAwaiter().GetResult(); Check(result.Status == "completed");
                var info = FullReplayArchive.ReadInfo(result.FilePath);
                using var timeline = new PagedReplayTimeline(info.Header, info.Duration, info.FrameCount, info.ActorIds,
                    info.Pages.Select(p => new ReplayPageRange(p.Start, p.End)).ToArray(),
                    (index, token) => FullReplayArchive.ReadPage(result.FilePath, info, index, token));
                foreach (var page in info.Pages.AsEnumerable().Reverse())
                {
                    var sample = Ready(timeline, page.Start);
                    Check(Math.Abs(sample.Left.T - page.Start) < 1e-6);
                    Check(timeline.CachedPageCount <= 4);
                }
                foreach (var page in info.Pages)
                    Check(Ready(timeline, page.End).Left.T >= page.Start - 1e-6);
            }
            finally { Directory.Delete(root, true); }
        });
        test("invalid global ranges and excessive actors cannot enter a timeline", () =>
        {
            Reject(() => new PagedReplayTimeline(new ReplayHeader(), double.NaN, 2, Array.Empty<string>(),
                new[] { new ReplayPageRange(0, 1) }, (i, c) => Page(i)));
            Reject(() => new PagedReplayTimeline(new ReplayHeader(), 10, 2, Array.Empty<string>(),
                new[] { new ReplayPageRange(0, 8), new ReplayPageRange(7, 10) }, (i, c) => Page(i)));
            Reject(() => new PagedReplayTimeline(new ReplayHeader(), 10, 2, Enumerable.Range(0, 17).Select(i => i.ToString()).ToArray(),
                new[] { new ReplayPageRange(0, 10) }, (i, c) => Page(i)));
        });
    }

    private static PagedReplayTimeline Create(int count, Func<int, CancellationToken, ReplayClip>? load = null) =>
        new(new ReplayHeader(), count * 10, count * 40 + 1, new[] { "actor" },
            Enumerable.Range(0, count).Select(i => new ReplayPageRange(i * 10, (i + 1) * 10)).ToArray(), load ?? ((index, token) => Page(index)));
    private static ReplayClip Page(int index)
    {
        var clip = new ReplayClip { Complete = true };
        for (int i = 0; i <= 40; i++) clip.Frames.Add(new ReplayFrame { T = index * 10 + i * .25, Actors = new[] { new ActorFrame { Id = "actor" } } });
        return clip;
    }
    private static ReplayTimelineSample Ready(PagedReplayTimeline timeline, double time)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 5000)
        {
            if (timeline.TrySample(time, out var sample)) return sample;
            Thread.Sleep(1);
        }
        throw new Exception("Timed out waiting for test page.");
    }
    private static void DrainPrefetch(PagedReplayTimeline timeline, double time)
    {
        var watch = Stopwatch.StartNew();
        while (timeline.PendingPageCount > 0 && watch.ElapsedMilliseconds < 5000)
        { timeline.TrySample(time, out _); Thread.Sleep(1); }
    }
    private static void Reject(Func<IDisposable> create)
    {
        try { using var item = create(); } catch (InvalidDataException) { return; }
        throw new Exception("Expected invalid timeline rejection.");
    }
    private static void Check(bool condition, string message = "Replay timeline assertion failed.")
    { if (!condition) throw new Exception(message); }
}
