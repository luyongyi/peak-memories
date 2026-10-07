using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PeakReplayLab;

internal readonly struct ReplayTimelineSample
{
    public readonly ReplayFrame Left, Right;
    public readonly float Mix;
    public readonly ActorJointTimeline? Joints;
    public readonly double Time;
    public readonly EnvironmentReplayBracket Environment;
    public ReplayTimelineSample(ReplayClip clip, double time)
    { (Left, Right, Mix) = clip.At(time); Joints = null; Time = Math.Max(clip.Frames[0].T, Math.Min(clip.Duration, time)); Environment = new(Left.World.Environment, Right.World.Environment, Left.T, Right.T, Mix); }
    public ReplayTimelineSample(ReplayFrame left, ReplayFrame right, float mix)
    { Left = left; Right = right; Mix = mix; Joints = null; Time = left.T + (right.T - left.T) * mix; Environment = new(left.World.Environment, right.World.Environment, left.T, right.T, mix); }
    private ReplayTimelineSample(ReplayTimelineSample source, ActorJointTimeline joints)
    {
        Left = source.Left; Right = source.Right; Mix = source.Mix; Time = source.Time; Joints = joints;
        if (!joints.Environment.TrySample(Time, out Environment)) throw new InvalidOperationException("缺少环境的真实采样范围。");
    }
    public ReplayTimelineSample WithJoints(ActorJointTimeline joints) => new(this, joints);
}

internal readonly struct ReplayPageRange
{
    public readonly double Start, End;
    public ReplayPageRange(double start, double end) { Start = start; End = end; }
}

// All times are relative to the whole recording, never to a loaded page. Only
// TrySample requests IO. Effects' historical lookups must not seek the cache.
internal interface IReplayTimeline : IDisposable
{
    ReplayHeader Header { get; }
    double Duration { get; }
    long FrameCount { get; }
    string[] ActorIds { get; }
    bool Complete { get; }
    int CachedPageCount { get; }
    int CachedEventCount { get; }
    bool TrySample(double time, out ReplayTimelineSample sample);
    bool TryHistory(double time, out ReplayTimelineSample sample);
    void FillRecentEvents(double time, string actorId, List<ItemEvent> output);
}

internal sealed class MemoryReplayTimeline : IReplayTimeline
{
    private readonly ReplayClip clip;
    private readonly ReplayPageEvents events;
    private readonly ActorJointTimeline joints;
    public ReplayHeader Header => clip.Header;
    public double Duration => clip.Duration;
    public long FrameCount => clip.Frames.Count;
    public string[] ActorIds { get; }
    public bool Complete => clip.Complete;
    public int CachedPageCount => 1;
    public int CachedEventCount => events.Count;
    public MemoryReplayTimeline(ReplayClip clip)
    {
        this.clip = clip;
        ActorIds = clip.Frames.SelectMany(f => f.Actors).Select(a => a.Id).Distinct(StringComparer.Ordinal).ToArray();
        events = new ReplayPageEvents(clip);
        joints = new ActorJointTimeline(clip.Frames, true);
    }
    public bool TrySample(double time, out ReplayTimelineSample sample)
    { sample = new ReplayTimelineSample(clip, time).WithJoints(joints); return joints.CanSample(sample.Left.Actors, sample.Time); }
    public bool TryHistory(double time, out ReplayTimelineSample sample) => TrySample(time, out sample);
    public void FillRecentEvents(double time, string actorId, List<ItemEvent> output)
    { output.Clear(); events.Append(time, actorId, output); ReplayPageEvents.Finish(output); }
    public void Dispose() { }
}

internal sealed class PagedReplayTimeline : IReplayTimeline
{
    private sealed class LoadedPage
    {
        public readonly ReplayClip Clip;
        public readonly ReplayPageEvents Events;
        public readonly long DecodedBytes;
        public LoadedPage(ReplayClip clip)
        {
            Clip = clip; Events = new ReplayPageEvents(clip);
            var budget = new ContinuousReplayBudget();
            foreach (var frame in clip.Frames) budget.Commit(frame, budget.Additional(frame));
            DecodedBytes = budget.Bytes;
        }
    }
    private readonly ReplayPageRange[] pages;
    private readonly Func<int, CancellationToken, ReplayClip> load;
    private readonly Dictionary<int, LoadedPage> cache = new();
    private readonly Dictionary<int, Exception> failures = new();
    private readonly List<int> remove = new(4);
    private Task<LoadedPage>? reading;
    private CancellationTokenSource? cancellation;
    private int readingIndex = -1, desired, lookaheadLast, lookbehindFirst;
    internal const long MaximumCachedDecodedBytes = 256L * 1024 * 1024;
    private const int MaximumCachedPages = 64;
    private bool disposed;
    private int cacheRevision, jointRevision = -1, buildingJointRevision = -1;
    private ActorJointTimeline? jointWindow;
    private Task<ActorJointTimeline>? buildingJoints;
    private CancellationTokenSource? jointCancellation;
    public ReplayHeader Header { get; }
    public double Duration { get; }
    public long FrameCount { get; }
    public string[] ActorIds { get; }
    public bool Complete { get; }
    public int CachedPageCount => cache.Count;
    public int CachedEventCount => cache.Values.Sum(p => p.Events.Count);
    internal long CachedDecodedBytes => cache.Values.Sum(p => p.DecodedBytes);
    internal int PendingPageCount => reading == null ? 0 : 1;

    public PagedReplayTimeline(ReplayHeader header, double duration, long frameCount, string[] actorIds,
        ReplayPageRange[] pages, Func<int, CancellationToken, ReplayClip> load, bool complete = true)
    {
        if (!ReplayRules.Finite(duration) || duration < 0 || frameCount < 2 || pages.Length == 0 || pages.Length > 100000)
            throw new InvalidDataException("Invalid continuous replay timeline.");
        for (int i = 0; i < pages.Length; i++)
            if (!ReplayRules.Finite(pages[i].Start) || !ReplayRules.Finite(pages[i].End) || pages[i].Start < 0 ||
                pages[i].End < pages[i].Start || pages[i].End > duration ||
                (i > 0 && (pages[i].Start < pages[i - 1].Start || pages[i].Start < pages[i - 1].End)))
                throw new InvalidDataException("Invalid continuous replay page ranges.");
        if (pages[0].Start != 0 || pages[pages.Length - 1].End != duration || actorIds.Length > ReplayRules.MaxActors ||
            actorIds.Any(string.IsNullOrEmpty) || actorIds.Distinct(StringComparer.Ordinal).Count() != actorIds.Length)
            throw new InvalidDataException("Invalid continuous replay participants or endpoints.");
        Header = header; Duration = duration; FrameCount = frameCount; ActorIds = (string[])actorIds.Clone(); Complete = complete;
        this.pages = (ReplayPageRange[])pages.Clone(); this.load = load;
        Schedule();
    }

    public bool TrySample(double time, out ReplayTimelineSample sample)
    {
        if (disposed) throw new ObjectDisposedException(nameof(PagedReplayTimeline));
        if (!ReplayRules.Finite(time)) throw new ArgumentOutOfRangeException(nameof(time));
        time = Math.Max(0, Math.Min(Duration, time));
        desired = FindPage(time);
        // A byte-limited page can contain just one real frame. A low-rate
        // observation may therefore be several pages away, not merely next1.
        // Ordinary 10-second pages still prefetch only their immediate next.
        lookaheadLast = Math.Max(Math.Min(desired + 1, pages.Length - 1), FindPage(Math.Min(Duration, pages[desired].End + .5)));
        // Environments have frame.T anchors rather than wire SampleTime fields.
        // Tiny byte-limited pages need enough real history to recover the prior
        // 20 Hz key; ordinary pages retain their two-page particle history.
        lookbehindFirst = Math.Min(Math.Max(0, desired - 2), FindPage(Math.Max(0, pages[desired].Start - .5)));
        if (lookaheadLast - lookbehindFirst + 1 > MaximumCachedPages)
            throw new InvalidDataException("人物关节所需的读取范围超过分页数量上限。");
        Trim();
        if (reading != null && !InWindow(readingIndex)) cancellation?.Cancel();
        Poll();
        ThrowRequiredFailure(desired);
        for (int history = lookbehindFirst; history < desired; history++) ThrowRequiredFailure(history);
        Schedule();
        // Two preceding normal 10-second pages cover the native presentation's
        // 12-second particle warm-up. Size-limited shorter pages may still need
        // approximation, which the read-only history callback reports explicitly.
        bool missingHistory = false;
        for (int history = lookbehindFirst; history < desired; history++) if (!cache.ContainsKey(history)) { missingHistory = true; break; }
        if (!cache.TryGetValue(desired, out var page) || missingHistory)
        { sample = default; return false; }
        var last = page.Clip.Frames[page.Clip.Frames.Count - 1];
        if (time > last.T && desired + 1 < pages.Length && pages[desired + 1].Start - last.T <= .5)
        {
            // A budget boundary need not duplicate its previous frame. Bridge
            // only real neighboring samples; never invent poses across a stall.
            ThrowRequiredFailure(desired + 1);
            if (!cache.TryGetValue(desired + 1, out var next)) { sample = default; return false; }
            var first = next.Clip.Frames[0]; double gap = first.T - last.T;
            if (gap > 0 && gap <= .5)
            { sample = new ReplayTimelineSample(last, first, (float)Math.Max(0, Math.Min(1, (time - last.T) / gap))); return ReadyJoints(ref sample); }
        }
        sample = new ReplayTimelineSample(page.Clip, time); return ReadyJoints(ref sample);
    }

    private bool ReadyJoints(ref ReplayTimelineSample sample)
    {
        if (buildingJoints != null && buildingJoints.IsCompleted)
        {
            try
            {
                var completed = buildingJoints.GetAwaiter().GetResult();
                if (buildingJointRevision == cacheRevision) { jointWindow = completed; jointRevision = cacheRevision; }
            }
            catch (OperationCanceledException) { }
            finally { buildingJoints = null; jointCancellation?.Dispose(); jointCancellation = null; }
        }
        if (jointRevision != cacheRevision)
        {
            if (buildingJoints == null)
            {
                // Snapshot immutable decoded pages. Indexing can touch hundreds
                // of thousands of joints; all that work stays off the UI thread.
                var clipList = new List<ReplayClip>();
                int lastIncluded = lookbehindFirst - 1;
                for (int index = lookbehindFirst; index <= lookaheadLast; index++)
                {
                    // A missing/failed middle page might contain an actor's
                    // disappearance or a cut. Never join around that hole.
                    if (!cache.TryGetValue(index, out var loaded)) break;
                    clipList.Add(loaded.Clip); lastIncluded = index;
                }
                var clips = clipList.ToArray();
                bool knownEnd = lastIncluded == pages.Length - 1;
                buildingJointRevision = cacheRevision;
                jointCancellation = new CancellationTokenSource();
                var token = jointCancellation.Token;
                buildingJoints = Task.Run(() => new ActorJointTimeline(clips.SelectMany(clip => clip.Frames), knownEnd, token), token);
            }
            // A newly prefetched page need not interrupt playback while the
            // previous immutable index still brackets every requested node.
            // It will naturally buffer when genuinely missing future samples.
        }
        if (jointWindow == null || !jointWindow.CanSample(sample.Left.Actors, sample.Time) || !jointWindow.Environment.TrySample(sample.Time, out _))
        {
            // The current page can end between two real low-rate observations.
            // Wait for the prefetched page instead of holding then jumping.
            if (jointRevision == cacheRevision)
                for (int future = desired + 1; future <= lookaheadLast; future++) ThrowRequiredFailure(future);
            return false;
        }
        sample = sample.WithJoints(jointWindow); return true;
    }

    private void InvalidateJoints()
    { cacheRevision++; jointCancellation?.Cancel(); }

    public bool TryHistory(double time, out ReplayTimelineSample sample)
    {
        sample = default;
        if (disposed || !ReplayRules.Finite(time) || time < 0 || time > Duration) return false;
        int index = FindPage(time);
        if (!cache.TryGetValue(index, out var page)) return false;
        if (time > pages[index].End)
        {
            // Preserve the old clip's neighboring-frame behavior at a budget
            // boundary, but do not treat a real capture stall as emitter history.
            if (!cache.TryGetValue(index + 1, out var next)) return false;
            var left = page.Clip.Frames[page.Clip.Frames.Count - 1]; var right = next.Clip.Frames[0];
            double gap = right.T - left.T;
            if (gap <= 0 || gap > .5) return false;
            sample = new ReplayTimelineSample(left, right, (float)Math.Max(0, Math.Min(1, (time - left.T) / gap)));
            return true;
        }
        sample = new ReplayTimelineSample(page.Clip, time); return true;
    }

    public void FillRecentEvents(double time, string actorId, List<ItemEvent> output)
    {
        output.Clear();
        foreach (var page in cache.Values) page.Events.Append(time, actorId, output);
        ReplayPageEvents.Finish(output);
    }

    private int FindPage(double time)
    {
        int lo = 0, hi = pages.Length - 1;
        while (lo < hi)
        {
            int middle = (lo + hi + 1) / 2;
            if (pages[middle].Start <= time) lo = middle; else hi = middle - 1;
        }
        return lo;
    }

    private void Trim()
    {
        remove.Clear(); foreach (int index in cache.Keys) if (!InWindow(index)) remove.Add(index);
        foreach (int index in remove) cache.Remove(index);
        if (remove.Count != 0) InvalidateJoints();
        remove.Clear(); foreach (int index in failures.Keys) if (!InWindow(index)) remove.Add(index);
        foreach (int index in remove) failures.Remove(index);
    }

    private void Poll()
    {
        if (reading == null || !reading.IsCompleted) return;
        try
        {
            var page = reading.GetAwaiter().GetResult(); // Completed only: never waits for IO.
            if (InWindow(readingIndex))
            {
                if (CachedDecodedBytes + page.DecodedBytes > MaximumCachedDecodedBytes)
                    throw new InvalidDataException("人物关节所需的读取范围超过回放内存预算。");
                cache[readingIndex] = page; InvalidateJoints();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (InWindow(readingIndex)) failures[readingIndex] = e; }
        reading = null; readingIndex = -1;
        cancellation?.Dispose(); cancellation = null;
    }

    private void ThrowRequiredFailure(int index)
    {
        if (failures.TryGetValue(index, out var failure))
            throw new InvalidDataException("无法读取完整录像的所需时间范围。", failure);
    }

    private void Schedule()
    {
        if (disposed || reading != null) return;
        int index = Missing(desired) ? desired : -1;
        if (index < 0)
            for (int history = desired - 1; history >= lookbehindFirst; history--)
                if (Missing(history)) { index = history; break; }
        if (index < 0)
            for (int future = desired + 1; future <= lookaheadLast; future++)
                if (Missing(future)) { index = future; break; }
        if (index < 0) return;
        readingIndex = index;
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        reading = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var clip = load(index, token);
            token.ThrowIfCancellationRequested();
            var range = pages[index];
            if (!clip.Complete || clip.Frames.Count < 1 || Math.Abs(clip.Frames[0].T - range.Start) > 1e-6 || Math.Abs(clip.Duration - range.End) > 1e-6)
                throw new InvalidDataException("Continuous replay page does not match its index.");
            var result = new LoadedPage(clip);
            token.ThrowIfCancellationRequested();
            return result;
        }, token);
    }

    private bool Missing(int index) => !cache.ContainsKey(index) && !failures.ContainsKey(index);
    private bool InWindow(int index) => index >= lookbehindFirst && index <= lookaheadLast;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var readSource = cancellation; cancellation = null;
        var task = reading; reading = null;
        CancelAndRelease(readSource, task);
        cache.Clear(); failures.Clear();
        var jointSource = jointCancellation; jointCancellation = null;
        var jointTask = buildingJoints; buildingJoints = null; jointWindow = null;
        CancelAndRelease(jointSource, jointTask);
    }

    private static void CancelAndRelease(CancellationTokenSource? source, Task? task)
    {
        source?.Cancel();
        if (task == null) { source?.Dispose(); return; }
        // Cancel wakes the worker, but it may not have accessed token.WaitHandle
        // yet. Disposing its source now would race that access and turn normal
        // cancellation into ObjectDisposedException. The task owns the source
        // until its delegate has finished; this continuation never waits for IO.
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception; // Observe faults even after the UI closed.
            source?.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

// Built on the decoder thread; UI work is O(log events + a small bounded tail),
// not an hours-long scan. Duplicate boundary-frame events share their sequence.
internal sealed class ReplayPageEvents
{
    private readonly Dictionary<string, ItemEvent[]> actors;
    public int Count { get; }
    public ReplayPageEvents(ReplayClip clip)
    {
        actors = clip.Frames.SelectMany(f => f.Events).GroupBy(e => e.ActorId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.T).ThenBy(e => e.Sequence).ToArray(), StringComparer.Ordinal);
        Count = actors.Values.Sum(v => v.Length);
    }
    public void Append(double time, string actorId, List<ItemEvent> output)
    {
        AppendActor(time, actorId, output);
        if (!string.IsNullOrEmpty(actorId)) AppendActor(time, "", output);
    }
    private void AppendActor(double time, string actorId, List<ItemEvent> output)
    {
        if (!actors.TryGetValue(actorId, out var events)) return;
        int lo = 0, hi = events.Length;
        while (lo < hi)
        {
            int middle = (lo + hi) / 2;
            if (events[middle].T <= time) lo = middle + 1; else hi = middle;
        }
        int minimum = Math.Max(0, lo - 3);
        for (int i = lo - 1; i >= minimum && events[i].T >= time - 4; i--) output.Add(events[i]);
    }
    public static void Finish(List<ItemEvent> output)
    {
        output.Sort((a, b) => { int t = b.T.CompareTo(a.T); return t == 0 ? b.Sequence.CompareTo(a.Sequence) : t; });
        for (int i = output.Count - 1; i > 0; i--)
            if (output[i].Sequence == output[i - 1].Sequence) output.RemoveAt(i);
        if (output.Count > 3) output.RemoveRange(3, output.Count - 3);
    }
}
