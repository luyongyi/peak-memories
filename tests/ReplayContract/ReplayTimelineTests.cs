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
        test("a cold seek reads only its target until history is explicitly requested", () =>
        {
            using var initialStarted = new ManualResetEventSlim();
            using var desiredStarted = new ManualResetEventSlim();
            using var releaseDesired = new ManualResetEventSlim();
            using var windowDecoded = new ManualResetEventSlim();
            var calls = new ConcurrentQueue<int>();
            using var timeline = Create(8, (index, token) =>
            {
                if (index == 0) { initialStarted.Set(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); }
                if (index == 4) { desiredStarted.Set(); releaseDesired.Wait(token); }
                var page = Page(index); calls.Enqueue(index);
                if (index == 4) windowDecoded.Set();
                return page;
            });
            Check(initialStarted.Wait(3000));
            var watch = Stopwatch.StartNew();
            while (!desiredStarted.IsSet && watch.ElapsedMilliseconds < 3000)
            { Check(!timeline.TrySample(45, out _)); Thread.Sleep(1); }
            Check(desiredStarted.IsSet); releaseDesired.Set();
            // No TrySample call here: the decoder must continue on its own.
            Check(windowDecoded.Wait(3000), "Reading history still depends on a UI frame between pages.");
            Check(calls.ToArray().SequenceEqual(new[] { 4 }));
            Check(Ready(timeline, 45).Left.T == 45 && timeline.CachedPageCount == 1);
            timeline.RequestHistory(25); DrainPrefetch(timeline, 45);
            Check(calls.ToArray().SequenceEqual(new[] { 4, 2 }), "An explicit history origin decoded an unrequested intermediate page.");
            timeline.RequestHistory(35); DrainPrefetch(timeline, 45);
            Check(calls.ToArray().SequenceEqual(new[] { 4, 2, 3 }));
        });
        test("slow optional history never hides the already decoded target", () =>
        {
            using var futureStarted = new ManualResetEventSlim();
            using var releaseFuture = new ManualResetEventSlim();
            using var timeline = Create(8, (index, token) =>
            {
                if (index == 1) { futureStarted.Set(); releaseFuture.Wait(token); }
                return Page(index);
            });
            Check(Ready(timeline, 35).Left.T == 35);
            timeline.RequestHistory(15);
            Check(futureStarted.Wait(3000) && !releaseFuture.IsSet);
            Check(timeline.TrySample(35, out _) && !timeline.TryHistory(15, out _));
            releaseFuture.Set(); DrainPrefetch(timeline, 35);
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
                Check(timeline.CachedPageCount <= 64 && timeline.PendingPageCount <= 1);
                Check(timeline.CachedDecodedBytes <= PagedReplayTimeline.MaximumCachedDecodedBytes);
            }
            Check(calls.Count < 30, "Seeking decoded the entire recording.");
        });
        test("repeated seeks reuse decoded neighboring windows within the LRU byte and page budgets", () =>
        {
            var calls = new ConcurrentQueue<int>();
            var ranges = Enumerable.Range(0, 12).Select(i => new ReplayPageRange(i * 10, (i + 1) * 10)).ToArray();
            var bounds = Enumerable.Range(0, 12).Select(i => DecodedBytes(Page(i))).ToArray();
            using var timeline = new PagedReplayTimeline(new ReplayHeader(), 120, 481, new[] { "actor" }, ranges,
                (index, token) => { calls.Enqueue(index); return Page(index); }, decodedPageBytes: bounds);
            Ready(timeline, 25); DrainPrefetch(timeline, 25);
            Ready(timeline, 95); DrainPrefetch(timeline, 95);
            int reads = calls.Count;
            int indexes = timeline.JointIndexBuildCount;
            for (int i = 0; i < 20; i++)
            {
                double time = i % 2 == 0 ? 25 : 95;
                Check(Ready(timeline, time).Left.T == time);
                Check(timeline.CachedPageCount <= 64 && timeline.CachedDecodedBytes <= PagedReplayTimeline.MaximumCachedDecodedBytes);
            }
            Check(calls.Count == reads, "Repeated seek decoded pages that were already retained within budget.");
            Check(timeline.JointIndexBuildCount == indexes, "Repeated seek rebuilt immutable page indexes.");
            Check(timeline.CachedPageCount >= 2);
            Check(calls.All(index => index == 0 || index == 2 || index == 9),
                "Cache capacity triggered reads outside the requested neighboring windows.");
        });
        test("a page larger than the verified decoded budget is rejected before reading", () =>
        {
            var ranges = Enumerable.Range(0, 8).Select(i => new ReplayPageRange(i * 10, (i + 1) * 10)).ToArray();
            Reject(() => new PagedReplayTimeline(new ReplayHeader(), 80, 321, new[] { "actor" }, ranges,
                (index, token) => throw new Exception("Over-budget page was read."),
                decodedPageBytes: Enumerable.Repeat(PagedReplayTimeline.MaximumCachedDecodedBytes + 1, 8).ToArray()));
        });
        test("history lookup neither schedules IO nor evicts playback pages", () =>
        {
            var calls = new ConcurrentQueue<int>();
            using var timeline = Create(8, (index, token) => { calls.Enqueue(index); return Page(index); });
            Ready(timeline, 45); DrainPrefetch(timeline, 45);
            timeline.RequestHistory(35); DrainPrefetch(timeline, 45);
            int count = calls.Count, cached = timeline.CachedPageCount;
            for (int i = 0; i < 100; i++) Check(!timeline.TryHistory(1, out _));
            Check(calls.Count == count && timeline.CachedPageCount == cached);
            Check(timeline.TryHistory(35, out var previous) && previous.Left.T == 35);
        });
        test("seek becomes ready before previous particle history is present", () =>
        {
            using var previousReady = new ManualResetEventSlim();
            using var timeline = Create(5, (index, token) =>
            { if (index == 2) previousReady.Wait(token); return Page(index); });
            Check(Ready(timeline, 35).Left.T == 35);
            timeline.RequestHistory(25);
            Check(timeline.TrySample(35, out _) && !timeline.TryHistory(25, out _));
            previousReady.Set();
            DrainPrefetch(timeline, 35);
            Check(timeline.TryHistory(25, out _));
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
            Ready(timeline, 9); Ready(timeline, 11);
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
        test("explicit history retains the native twelve-second effects lookback", () =>
        {
            using var timeline = Create(8);
            Ready(timeline, 40.25);
            timeline.RequestHistory(28.25); DrainPrefetch(timeline, 40.25);
            Check(timeline.TryHistory(28.25, out var historical) && historical.Left.T == 28.25);
            Check(timeline.CachedPageCount <= 64 && timeline.CachedDecodedBytes <= PagedReplayTimeline.MaximumCachedDecodedBytes);
        });
        test("one-second effects history uses its lightweight loader and never builds joint indexes", () =>
        {
            var calls = new ConcurrentQueue<int>(); var mainCalls = new ConcurrentQueue<int>();
            using var timeline = SecondsTimeline(25, (index, token) => { mainCalls.Enqueue(index); return SecondsPage(index); },
                (index, token) => { calls.Enqueue(index); return SecondsPage(index); });
            Ready(timeline, 20.25); int indexes = timeline.JointIndexBuildCount;
            timeline.RequestHistory(8.25); DrainPrefetch(timeline, 20.25);
            Check(calls.ToArray().SequenceEqual(new[] { 8 }), "History loaded pages before the particle cursor requested them.");
            for (int index = 9; index < 20; index++)
            { timeline.RequestHistory(index + .25); DrainPrefetch(timeline, 20.25); Check(timeline.TryHistory(index + .25, out _)); }
            Check(calls.ToArray().SequenceEqual(Enumerable.Range(8, 12)), "Twelve seconds of history used a fixed two-page lookback.");
            Check(timeline.TryHistory(8.25, out var sample) && sample.Left.T == 8.25);
            Check(timeline.CachedHistoryPageCount == 12 && timeline.JointIndexBuildCount == indexes);
            Check(mainCalls.All(index => index == 0 || index == 20), "Particle pre-roll decoded full actor pages.");
        });
        test("later emitter requests preserve earlier in-flight history results", () =>
        {
            using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            using var timeline = SecondsTimeline(25, (index, token) => SecondsPage(index), (index, token) =>
            { if (index == 8) { started.Set(); release.Wait(token); } return SecondsPage(index); });
            Ready(timeline, 20.25); timeline.RequestHistory(8.25); Check(started.Wait(3000));
            timeline.RequestHistory(18.25); release.Set(); DrainPrefetch(timeline, 20.25);
            Check(timeline.TryHistory(8.25, out _) && timeline.TryHistory(18.25, out _));
        });
        test("optional corrupt history reports a warning while the same page remains loadable for the scene", () =>
        {
            using var timeline = SecondsTimeline(8, (index, token) => SecondsPage(index),
                (index, token) => throw new InvalidDataException("bad effects-only block"));
            Ready(timeline, 5.25); timeline.RequestHistory(2.25); DrainPrefetch(timeline, 5.25);
            Check(timeline.TrySample(5.25, out _) && !timeline.TryHistory(2.25, out _) && timeline.HistoryWarning.Length != 0);
            Check(Ready(timeline, 2.25).Left.T == 2.25, "Optional history poisoned the full page failure cache.");
        });
        test("optional history reservations exceeding remaining memory never reject scene readiness", () =>
        {
            int reads = 0; var ranges = Enumerable.Range(0, 5).Select(i => new ReplayPageRange(i, i + 1)).ToArray();
            using var timeline = new PagedReplayTimeline(new ReplayHeader(), 5, 21, Array.Empty<string>(), ranges,
                (index, token) => SecondsPage(index), decodedPageBytes: Enumerable.Repeat(1024L * 1024, 5).ToArray(),
                historyLoader: (index, token) => { Interlocked.Increment(ref reads); return SecondsPage(index); },
                historyDecodedPageBytes: Enumerable.Repeat(PagedReplayTimeline.MaximumCachedDecodedBytes, 5).ToArray());
            Ready(timeline, 4.25); timeline.RequestHistory(2.25);
            Check(timeline.TrySample(4.25, out _) && reads == 0 && timeline.HistoryWarning.Length != 0);
        });
        test("history beyond sixty-four tiny pages streams explicit requests and protects unconsumed origins", () =>
        {
            int reads = 0;
            using var timeline = SecondsTimeline(150, (index, token) => SecondsPage(index),
                (index, token) => { Interlocked.Increment(ref reads); return SecondsPage(index); });
            Ready(timeline, 120.25); timeline.RequestHistory(1.25); timeline.RequestHistory(118.25);
            DrainPrefetch(timeline, 120.25);
            Check(reads == 2, "Two emitter origins decoded the whole interval between them.");
            for (int index = 2; index <= 100; index++)
            { timeline.RequestHistory(index + .25); DrainPrefetch(timeline, 120.25); Check(timeline.TryHistory(index + .25, out _)); }
            Check(timeline.TrySample(120.25, out _) && timeline.CachedPageCount <= 64);
            Check(reads > 64 && timeline.CachedPageCount == 64, "Large history did not exercise more than one bounded batch.");
            Check(timeline.TryHistory(1.25, out _) && timeline.TryHistory(118.25, out _), "A later batch evicted an unconsumed emitter origin.");
            foreach (double time in new[] { 60.25, 90.25 })
            { timeline.RequestHistory(time); DrainPrefetch(timeline, 120.25); Check(timeline.TryHistory(time, out _)); }
            Check(timeline.CachedPageCount <= 64 && timeline.CachedDecodedBytes <= PagedReplayTimeline.MaximumCachedDecodedBytes);
        });
        test("playing prefetch preempts slow history before a scene boundary and keeps one reader", () =>
        {
            using var started = new ManualResetEventSlim(); using var cancelled = new ManualResetEventSlim();
            int attempts = 0, active = 0, maximum = 0;
            var calls = new ConcurrentQueue<string>();
            ReplayClip Load(int index, CancellationToken token, bool history)
            {
                int current = Interlocked.Increment(ref active); maximum = Math.Max(maximum, current);
                try
                {
                    if (history && Interlocked.Increment(ref attempts) == 1)
                    {
                        started.Set(); token.WaitHandle.WaitOne();
                        if (token.IsCancellationRequested) cancelled.Set(); token.ThrowIfCancellationRequested();
                    }
                    calls.Enqueue((history ? "history:" : "main:") + index); return SecondsPage(index);
                }
                finally { Interlocked.Decrement(ref active); }
            }
            using var timeline = SecondsTimeline(30, (index, token) => Load(index, token, false), (index, token) => Load(index, token, true));
            Ready(timeline, 15.25); timeline.RequestHistory(1.25); Check(started.Wait(3000));
            timeline.SetLookAhead(true, 1);
            Check(timeline.TrySample(15.25, out _) && cancelled.Wait(3000), "Optional history blocked early scene prefetch.");
            DrainPrefetch(timeline, 15.25);
            var loaded = calls.ToArray();
            Check(Array.IndexOf(loaded, "main:16") < Array.IndexOf(loaded, "history:1") && loaded.Contains("main:19"));
            Check(maximum == 1 && timeline.TrySample(16.25, out _));
        });
        test("pausing cancels optional forward reads without hiding the ready pose", () =>
        {
            using var started = new ManualResetEventSlim(); using var cancelled = new ManualResetEventSlim();
            var calls = new ConcurrentQueue<int>();
            using var timeline = SecondsTimeline(30, (index, token) =>
            {
                calls.Enqueue(index);
                if (index == 16)
                { started.Set(); token.WaitHandle.WaitOne(); if (token.IsCancellationRequested) cancelled.Set(); token.ThrowIfCancellationRequested(); }
                return SecondsPage(index);
            }, (index, token) => SecondsPage(index));
            Ready(timeline, 15.25); timeline.SetLookAhead(true, 1); Check(timeline.TrySample(15.25, out _));
            Check(started.Wait(3000)); timeline.SetLookAhead(false, 1);
            Check(timeline.TrySample(15.25, out _) && cancelled.Wait(3000)); DrainPrefetch(timeline, 15.25);
            Check(!calls.Any(index => index > 16), "Paused inspection kept scheduling future pages.");
        });
        test("high-speed forward reads stay within eight pages and the existing cache limit", () =>
        {
            var calls = new ConcurrentQueue<int>();
            using var timeline = SecondsTimeline(120, (index, token) => { calls.Enqueue(index); return SecondsPage(index); }, (index, token) => SecondsPage(index));
            Ready(timeline, 20.25); timeline.SetLookAhead(true, 8); timeline.TrySample(20.25, out _); DrainPrefetch(timeline, 20.25);
            Check(calls.Contains(28) && !calls.Any(index => index > 28), "Forward demand decoded more than eight pages.");
            Check(timeline.CachedDecodedBytes <= PagedReplayTimeline.MaximumCachedDecodedBytes && timeline.CachedPageCount <= 64);
        });
        test("optional forward reservations never displace a required scene or exceed one GiB", () =>
        {
            var calls = new ConcurrentQueue<int>(); var ranges = Enumerable.Range(0, 8).Select(index => new ReplayPageRange(index, index + 1)).ToArray();
            using var timeline = new PagedReplayTimeline(new ReplayHeader(), 8, 33, Array.Empty<string>(), ranges,
                (index, token) => { calls.Enqueue(index); return SecondsPage(index); },
                decodedPageBytes: Enumerable.Repeat(PagedReplayTimeline.MaximumCachedDecodedBytes, 8).ToArray());
            Ready(timeline, 4.25); int before = calls.Count;
            timeline.SetLookAhead(true, 8); Check(timeline.TrySample(4.25, out _)); DrainPrefetch(timeline, 4.25);
            Check(calls.Count == before && timeline.TrySample(4.25, out _), "An optional page used the required scene's memory reservation.");
        });
        test("history can evict distant forward pages while retaining the nearest deadline under a one GiB reservation", () =>
        {
            using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            CancellationToken historyToken = default;
            int historyReads = 0; var ranges = Enumerable.Range(0, 30).Select(index => new ReplayPageRange(index, index + 1)).ToArray();
            long pageBytes = DecodedBytes(SecondsPage(15));
            // The declared history bound exactly fits scene 15 plus its next
            // page 16; protecting another short future page would starve it.
            using var timeline = new PagedReplayTimeline(new ReplayHeader(), 30, 121, Array.Empty<string>(), ranges,
                (index, token) => SecondsPage(index), decodedPageBytes: Enumerable.Repeat(1024L * 1024, 30).ToArray(),
                historyLoader: (index, token) =>
                { Interlocked.Increment(ref historyReads); historyToken = token; started.Set(); release.Wait(token); return SecondsPage(index); },
                historyDecodedPageBytes: Enumerable.Repeat(PagedReplayTimeline.MaximumCachedDecodedBytes - pageBytes * 2, 30).ToArray());
            Ready(timeline, 15.25); timeline.SetLookAhead(true, 1); timeline.TrySample(15.25, out _); DrainPrefetch(timeline, 15.25);
            timeline.RequestHistory(1.25); Check(started.Wait(3000));
            Check(!timeline.TryHistory(17.25, out _) && timeline.TrySample(15.25, out _) && !historyToken.IsCancellationRequested,
                "Yielding the second future page immediately cancelled the same history read.");
            release.Set(); DrainPrefetch(timeline, 15.25);
            Check(historyReads == 1 && timeline.TryHistory(1.25, out _), "Distant forward pages permanently starved a real explicit history request.");
            Check(timeline.TrySample(16.25, out _) && timeline.CachedDecodedBytes <= PagedReplayTimeline.MaximumCachedDecodedBytes);
        });
        test("paused history requests admit completed reads without another scene sample", () =>
        {
            using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            using var timeline = SecondsTimeline(30, (index, token) => SecondsPage(index), (index, token) =>
            { started.Set(); release.Wait(token); return SecondsPage(index); });
            Ready(timeline, 15.25); timeline.SetLookAhead(true, 1); timeline.TrySample(15.25, out _); DrainPrefetch(timeline, 15.25);
            timeline.SetLookAhead(false, 1); timeline.RequestHistory(1.25); Check(started.Wait(3000)); release.Set();
            var watch = Stopwatch.StartNew();
            while (!timeline.TryHistory(1.25, out _) && watch.ElapsedMilliseconds < 3000)
            { timeline.RequestHistory(1.25); Thread.Sleep(1); }
            Check(timeline.TryHistory(1.25, out var historical) && historical.Left.T == 1.25,
                "A paused scene needed TrySample before its completed history became visible.");
        });
        test("forward prefetch warms the combined joint window before its real observation boundary", () =>
        {
            using var timeline = new PagedReplayTimeline(new ReplayHeader(), 1.9, 20, new[] { "actor" },
                new[] { new ReplayPageRange(0, .9), new ReplayPageRange(1, 1.9) }, (index, token) =>
                {
                    var clip = new ReplayClip { Complete = true };
                    for (int step = 0; step < 10; step++)
                    {
                        double time = index + step * .1, stamp = time >= .8 && time < 1.2 ? .8 : time;
                        clip.Frames.Add(new ReplayFrame { T = time, Actors = new[] { new ActorFrame { Id = "actor", JointPose = new[]
                        { new NodePose { Path = ".", SampleTime = time },
                          new NodePose { Path = "./cloth#0", SampleTime = stamp, Position = new[] { (float)stamp, 0f, 0f } } } } } });
                    }
                    return clip;
                }, decodedPageBytes: new[] { 1024L * 1024, 1024L * 1024 });
            Ready(timeline, .25); timeline.SetLookAhead(true, 1);
            var watch = Stopwatch.StartNew();
            while (timeline.CachedJointWindowCount == 0 && watch.ElapsedMilliseconds < 3000)
            { Check(timeline.TrySample(.25, out _)); Thread.Sleep(1); }
            Check(timeline.CachedJointWindowCount == 1, "Forward page loaded without warming its joint boundary index.");
            int builds = timeline.JointIndexBuildCount;
            Check(timeline.TrySample(.85, out var sample)); Check(sample.Joints!.TryActor("actor", .85, out var actor));
            Check(Math.Abs(actor.At(1).Mix - .125) < .00001 && timeline.JointIndexBuildCount == builds,
                "Prefetch changed the real low-rate observation interpolation or rebuilt its boundary index.");
        });
        test("an opening predecessor and the next page reuse the warmed two-page joint window", () =>
        {
            using var timeline = new PagedReplayTimeline(new ReplayHeader(), 2.9, 30, new[] { "actor" },
                Enumerable.Range(0, 3).Select(index => new ReplayPageRange(index, index + .9)).ToArray(), (index, token) =>
                {
                    var clip = new ReplayClip { Complete = true };
                    for (int step = 0; step < 10; step++)
                    {
                        double time = index + step * .1, stamp = index > 0 && step == 0 ? index - .05 : time;
                        clip.Frames.Add(new ReplayFrame { T = time, Actors = new[] { new ActorFrame { Id = "actor", JointPose = new[]
                        { new NodePose { Path = ".", SampleTime = time },
                          new NodePose { Path = "./cloth#0", SampleTime = stamp, Position = new[] { (float)stamp, 0f, 0f } } } } } });
                    }
                    return clip;
                }, decodedPageBytes: Enumerable.Repeat(1024L * 1024, 3).ToArray());
            timeline.SetLookAhead(true, 1); Ready(timeline, 1.05); DrainPrefetch(timeline, 1.05);
            int builds = timeline.JointIndexBuildCount;
            Check(timeline.TrySample(1.25, out _));
            Check(timeline.TrySample(1.95, out _) && timeline.TrySample(2.05, out _), "A ready next page waited for a second immutable joint index.");
            Check(timeline.JointIndexBuildCount == builds, "The warmed window was rebuilt after dropping an obsolete predecessor.");
        });
        test("ready sampling and cache counters allocate no per-frame scheduler work", () =>
        {
            using var timeline = Create(2); Ready(timeline, 5.125);
            for (int index = 0; index < 1000; index++)
            { timeline.TrySample(5.125, out _); _ = timeline.CachedEventCount; _ = timeline.CachedDecodedBytes; }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10000; index++)
            { Check(timeline.TrySample(5.125, out _)); _ = timeline.CachedEventCount; _ = timeline.CachedDecodedBytes; }
            Check(GC.GetAllocatedBytesForCurrentThread() - before <= 1024, "Idle ready frames allocated scheduler closures, lists, or boxed cache enumerators.");
        });
        test("a new foreground seek cancels optional history before decoding its target", () =>
        {
            using var started = new ManualResetEventSlim(); int cancelled = 0, active = 0, maximum = 0;
            ReplayClip Load(int index, CancellationToken token, bool history)
            {
                int current = Interlocked.Increment(ref active); maximum = Math.Max(maximum, current);
                try
                {
                    if (history) { started.Set(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); }
                    return SecondsPage(index);
                }
                catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
                finally { Interlocked.Decrement(ref active); }
            }
            using var timeline = SecondsTimeline(25, (index, token) => Load(index, token, false), (index, token) => Load(index, token, true));
            Ready(timeline, 20.25); timeline.RequestHistory(8.25); Check(started.Wait(3000));
            Check(Ready(timeline, 12.25).Left.T == 12.25 && maximum == 1 && cancelled == 1);
        });
        test("disposing a timeline releases its playback source exactly once", () =>
        {
            var owner = new TestOwner();
            var timeline = new PagedReplayTimeline(new ReplayHeader(), 10, 41, Array.Empty<string>(),
                new[] { new ReplayPageRange(0, 10) }, (index, token) => Page(index), sourceOwnership: owner);
            timeline.Dispose(); timeline.Dispose(); Check(owner.Disposals == 1);
        });
        test("an opening environment clock preserves its segment cut using only a necessary predecessor", () =>
        {
            ReplayClip Load(int index)
            {
                var clip = new ReplayClip { Complete = true };
                for (int i = 0; i < 10; i++)
                {
                    double time = index + i / 10d; bool baseline = index == 1 && i < 2;
                    clip.Frames.Add(new ReplayFrame { T = time, World = new WorldFrame { Segment = index == 0 ? 0 : 1,
                        Environment = new EnvironmentReplayFrame { SampleTimeKnown = true, SampleTime = baseline ? .9 : time,
                            WeatherBlend = baseline ? 100 : (float)(time * 100) } } });
                }
                return clip;
            }
            var ranges = new[] { new ReplayPageRange(0, .9), new ReplayPageRange(1, 1.9), new ReplayPageRange(2, 2.9) };
            using (var timeline = new PagedReplayTimeline(new ReplayHeader(), 2.9, 30, Array.Empty<string>(), ranges,
                (index, token) => Load(index), decodedPageBytes: Enumerable.Repeat(1024L * 1024, 3).ToArray()))
            {
                var environment = Ready(timeline, 1.05).Environment;
                double value = environment.Left!.WeatherBlend + (environment.Right!.WeatherBlend - environment.Left.WeatherBlend) * environment.Mix;
                Check(Math.Abs(value - 105) < .00001 && environment.LeftTime == 1, "Cold seek blended across a segment cut.");
            }
            using var initialStarted = new ManualResetEventSlim(); var calls = new ConcurrentQueue<int>();
            using var later = new PagedReplayTimeline(new ReplayHeader(), 2.9, 30, Array.Empty<string>(), ranges,
                (index, token) => { if (index == 0) { initialStarted.Set(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); }
                    calls.Enqueue(index); return Load(index); }, decodedPageBytes: Enumerable.Repeat(1024L * 1024, 3).ToArray());
            Check(initialStarted.Wait(3000)); var bracket = Ready(later, 1.25).Environment;
            Check(bracket.LeftTime == 1.2 && calls.ToArray().SequenceEqual(new[] { 1 }), "Later real samples unnecessarily read the predecessor page.");
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
                    Check(timeline.CachedPageCount <= 64 && timeline.CachedDecodedBytes <= PagedReplayTimeline.MaximumCachedDecodedBytes);
                }
                foreach (var page in info.Pages)
                    Check(Ready(timeline, page.End).Left.T >= page.Start - 1e-6);
            }
            finally { Directory.Delete(root, true); }
        });
        test("invalid global ranges are rejected while large teams keep every actor", () =>
        {
            Reject(() => new PagedReplayTimeline(new ReplayHeader(), double.NaN, 2, Array.Empty<string>(),
                new[] { new ReplayPageRange(0, 1) }, (i, c) => Page(i)));
            Reject(() => new PagedReplayTimeline(new ReplayHeader(), 10, 2, Array.Empty<string>(),
                new[] { new ReplayPageRange(0, 8), new ReplayPageRange(7, 10) }, (i, c) => Page(i)));
            using var large = new PagedReplayTimeline(new ReplayHeader(), 10, 2, Enumerable.Range(0, 80).Select(i => i.ToString()).ToArray(),
                new[] { new ReplayPageRange(0, 10) }, (i, c) => Page(i));
            Check(large.ActorIds.Length == 80);
        });
    }

    private static PagedReplayTimeline Create(int count, Func<int, CancellationToken, ReplayClip>? load = null) =>
        new(new ReplayHeader(), count * 10, count * 40 + 1, new[] { "actor" },
            Enumerable.Range(0, count).Select(i => new ReplayPageRange(i * 10, (i + 1) * 10)).ToArray(), load ?? ((index, token) => Page(index)),
            decodedPageBytes: Enumerable.Repeat(1024L * 1024, count).ToArray(), historyDecodedPageBytes: Enumerable.Repeat(1024L * 1024, count).ToArray());
    private static ReplayClip Page(int index)
    {
        var clip = new ReplayClip { Complete = true };
        for (int i = 0; i <= 40; i++) clip.Frames.Add(new ReplayFrame { T = index * 10 + i * .25, Actors = new[] { new ActorFrame { Id = "actor" } } });
        return clip;
    }
    private static long DecodedBytes(ReplayClip clip)
    {
        var budget = new ContinuousReplayBudget();
        foreach (var frame in clip.Frames) budget.Commit(frame, budget.Additional(frame));
        return budget.Bytes;
    }
    private static PagedReplayTimeline SecondsTimeline(int count, Func<int, CancellationToken, ReplayClip> main, Func<int, CancellationToken, ReplayClip> history) =>
        new(new ReplayHeader(), count, count * 4 + 1, Array.Empty<string>(),
            Enumerable.Range(0, count).Select(i => new ReplayPageRange(i, i + 1)).ToArray(), main,
            decodedPageBytes: Enumerable.Repeat(1024L * 1024, count).ToArray(), historyLoader: history,
            historyDecodedPageBytes: Enumerable.Repeat(1024L * 1024, count).ToArray());
    private static ReplayClip SecondsPage(int index)
    {
        var clip = new ReplayClip { Complete = true };
        for (int i = 0; i <= 4; i++) clip.Frames.Add(new ReplayFrame { T = index + i * .25 });
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
        while ((timeline.PendingPageCount > 0 || timeline.PendingJointIndexCount > 0) && watch.ElapsedMilliseconds < 5000)
        { timeline.TrySample(time, out _); Thread.Sleep(1); }
    }
    private static void Reject(Func<IDisposable> create)
    {
        try { using var item = create(); } catch (InvalidDataException) { return; }
        throw new Exception("Expected invalid timeline rejection.");
    }
    private static void Check(bool condition, string message = "Replay timeline assertion failed.")
    { if (!condition) throw new Exception(message); }
    private sealed class TestOwner : IDisposable
    {
        public int Disposals;
        public void Dispose() { Disposals++; }
    }
}
