using System.Collections.Concurrent;
using System.Diagnostics;
using PeakReplayLab;

int passed = 0;
void Check(bool condition, string message = "Scene index budget assertion failed") { if (!condition) throw new Exception(message); }
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); Environment.Exit(1); }
}
ReplayClip Page(int index, bool sceneKnown = true, bool heldTail = false)
{
    var clip = new ReplayClip { Complete = true };
    for (int i = 0; i <= 4; i++)
    {
        double time = index + i * .25;
        clip.Frames.Add(new ReplayFrame { T = time, SceneSampleTimeKnown = sceneKnown,
            SceneSampleTime = sceneKnown ? heldTail && i == 4 ? time - .25 : time : 0 });
    }
    return clip;
}
long DecodedBytes(ReplayClip clip)
{
    var budget = new ContinuousReplayBudget();
    foreach (var frame in clip.Frames) budget.Commit(frame, budget.Additional(frame));
    return budget.Bytes;
}
long SceneBytes(ReplayClip clip)
{
    var builder = new SceneObservationTimeline.Builder(); foreach (var frame in clip.Frames) builder.Observe(frame);
    return builder.Build(true).EstimatedIndexBytes;
}
ReplayTimelineSample Ready(PagedReplayTimeline timeline, double time)
{
    var watch = Stopwatch.StartNew();
    while (watch.ElapsedMilliseconds < 5000)
    {
        if (timeline.TrySample(time, out var sample)) return sample;
        Thread.Sleep(1);
    }
    throw new Exception("Timed out waiting for budget test page: " + timeline.LoadingDiagnostic);
}

Test("scene index reservation reclaims an optional old page before the actual-size worker check", () =>
{
    var clip = Page(0); long decoded = DecodedBytes(clip), index = SceneBytes(clip), total = decoded + index;
    // Three decoded payloads fit exactly, but the new scene index does not.
    // Actual allocated test pages are a few KiB, not the production 1 GiB cap.
    long limit = total * 2 + decoded;
    var calls = new ConcurrentQueue<int>();
    using var timeline = new PagedReplayTimeline(new ReplayHeader { Schema = 22, CoverOutcome = null }, 4, 20, [],
        Enumerable.Range(0, 4).Select(i => new ReplayPageRange(i, i + 1)).ToArray(),
        (i, _) => { calls.Enqueue(i); return Page(i); }, decodedPageBytes: Enumerable.Repeat(decoded, 4).ToArray(), cacheByteLimit: limit);
    Ready(timeline, .25); Ready(timeline, 1.25); Check(timeline.CachedPageCount == 2);
    Check(Ready(timeline, 2.25).Scene.LeftTime == 2.25);
    Check(timeline.CachedPageCount == 2 && timeline.CachedDecodedBytes == total * 2 && timeline.CachedDecodedBytes <= limit);
    int reads = calls.Count; Ready(timeline, 1.25);
    Check(calls.Count == reads, "The LRU retained page was discarded along with the one required eviction.");
});
Test("a fully protected required window still rejects actual scene-index bytes beyond its limit", () =>
{
    var opening = Page(0, heldTail: true); var next = Page(1);
    long limit = DecodedBytes(opening) + SceneBytes(opening) + DecodedBytes(next);
    int reads = 0;
    using var timeline = new PagedReplayTimeline(new ReplayHeader { Schema = 22 }, 2, 10, [],
        [new ReplayPageRange(0, 1), new ReplayPageRange(1, 2)], (i, _) => { Interlocked.Increment(ref reads); return i == 0 ? opening : next; },
        decodedPageBytes: [DecodedBytes(opening), DecodedBytes(next)], cacheByteLimit: limit);
    Ready(timeline, .5);
    bool rejected = false; var watch = Stopwatch.StartNew();
    while (watch.ElapsedMilliseconds < 5000)
    {
        try { timeline.TrySample(.9, out _); }
        catch (InvalidDataException) { rejected = true; break; }
        Thread.Sleep(1);
    }
    Check(rejected && reads == 2, "A required actual over-budget window was admitted or never decoded.");
    Check(timeline.CachedPageCount == 1 && timeline.CachedDecodedBytes <= limit);
});
Test("old-schema and no-scene pages retain declared-one-GiB compatibility without allocating one GiB", () =>
{
    foreach (int schema in new[] { 21, 22 })
    {
        using var timeline = new PagedReplayTimeline(new ReplayHeader { Schema = schema }, 3, 15, [],
            Enumerable.Range(0, 3).Select(i => new ReplayPageRange(i, i + 1)).ToArray(), (i, _) => Page(i, sceneKnown: false),
            decodedPageBytes: Enumerable.Repeat(PagedReplayTimeline.MaximumCachedDecodedBytes, 3).ToArray());
        Check(Ready(timeline, .25).Left.T == .25); Check(Ready(timeline, 1.25).Left.T == 1.25);
        Check(timeline.CachedDecodedBytes < 16384 && timeline.CachedPageCount == 1);
    }
});
Test("a conservative scene estimate alone does not reject a tiny actual protected history page", () =>
{
    var main = Page(2, sceneKnown: false); long mainBytes = DecodedBytes(main);
    int historyReads = 0;
    using var timeline = new PagedReplayTimeline(new ReplayHeader { Schema = 22 }, 3, 15, [],
        Enumerable.Range(0, 3).Select(i => new ReplayPageRange(i, i + 1)).ToArray(), (i, _) => Page(i, sceneKnown: false),
        decodedPageBytes: Enumerable.Repeat(1024L * 1024, 3).ToArray(),
        historyLoader: (i, _) => { Interlocked.Increment(ref historyReads); return Page(i, sceneKnown: false); },
        historyDecodedPageBytes: Enumerable.Repeat(PagedReplayTimeline.MaximumCachedDecodedBytes - mainBytes, 3).ToArray());
    Ready(timeline, 2.25); timeline.RequestHistory(.25); var watch = Stopwatch.StartNew();
    while (!timeline.TryHistory(.25, out _) && watch.ElapsedMilliseconds < 5000)
    { timeline.RequestHistory(.25); Thread.Sleep(1); }
    Check(historyReads == 1 && timeline.TryHistory(.25, out _) && timeline.HistoryWarning.Length == 0);
    Check(timeline.CachedDecodedBytes == mainBytes * 2);
});
Test("test cache limits can only reduce the unchanged one-GiB maximum", () =>
{
    foreach (long limit in new[] { 0L, -1L, PagedReplayTimeline.MaximumCachedDecodedBytes + 1 })
    {
        bool rejected = false; int reads = 0;
        try
        {
            using var timeline = new PagedReplayTimeline(new ReplayHeader(), 1, 5, [], [new ReplayPageRange(0, 1)],
                (i, _) => { reads++; return Page(i); }, cacheByteLimit: limit);
        }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected && reads == 0);
    }
});
Test("the decoded minimum used for scene reservation is a frame cost lower bound", () =>
{
    long minimum = RollingBuffer.EstimateFrame(new ReplayFrame());
    Check(minimum > 0);
    foreach (var frame in new[]
    {
        new ReplayFrame { SceneSampleTimeKnown = true },
        new ReplayFrame { Actors = [new ActorFrame { Id = "actor" }] },
        new ReplayFrame { World = new WorldFrame { ActiveMapObjects = new bool[100] } },
    }) Check(RollingBuffer.EstimateFrame(frame) >= minimum);
    var page = Page(0); Check(DecodedBytes(page) / minimum >= page.Frames.Count);
});

// Preserve the existing far-seek, optional-history, declared-bound and
// prefetch contracts against the same production timeline source.
ReplayTimelineTests.Run(Test);
Console.WriteLine($"TOTAL: {passed} scene reservation and timeline checks passed. No Unity runtime is simulated.");
