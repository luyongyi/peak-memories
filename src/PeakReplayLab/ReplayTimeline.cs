using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
    public readonly SceneReplayBracket Scene;
    public ReplayTimelineSample(ReplayClip clip, double time)
    { (Left, Right, Mix) = clip.At(time); Joints = null; Time = Math.Max(clip.Frames[0].T, Math.Min(clip.Duration, time)); Environment = new(Left.World.Environment, Right.World.Environment, Left.T, Right.T, Mix); Scene = SceneReplayBracket.Legacy(Left,Right,Mix); }
    public ReplayTimelineSample(ReplayFrame left, ReplayFrame right, float mix)
    { Left = left; Right = right; Mix = mix; Joints = null; Time = left.T + (right.T - left.T) * mix; Environment = new(left.World.Environment, right.World.Environment, left.T, right.T, mix); Scene = SceneReplayBracket.Legacy(left,right,mix); }
    private ReplayTimelineSample(ReplayTimelineSample source, ActorJointTimeline joints)
    {
        Left = source.Left; Right = source.Right; Mix = source.Mix; Time = source.Time; Joints = joints;
        if (!joints.Environment.TrySample(Time, out Environment)) throw new InvalidOperationException("缺少环境的真实采样范围。");
        Scene = source.Scene;
        if (Left.SceneSampleTimeKnown && !joints.Scene.TrySample(Time, out Scene)) throw new InvalidOperationException("缺少场景的真实采样范围。");
    }
    public ReplayTimelineSample WithJoints(ActorJointTimeline joints) => new(this, joints);
    private ReplayTimelineSample(ReplayTimelineSample source, SceneReplayBracket scene)
    { Left=source.Left;Right=source.Right;Mix=source.Mix;Time=source.Time;Joints=source.Joints;Environment=source.Environment;Scene=scene; }
    internal ReplayTimelineSample WithScene(SceneReplayBracket scene) => new(this,scene);
}

internal readonly struct ReplayPageRange
{
    public readonly double Start, End;
    public ReplayPageRange(double start, double end) { Start = start; End = end; }
}

// All times are relative to the whole recording, never to a loaded page. Only
// TrySample (including its optional forward window) and explicit RequestHistory request IO. History readiness queries
// never start reads or change the displayed cursor.
internal interface IReplayTimeline : IDisposable
{
    ReplayHeader Header { get; }
    double Duration { get; }
    long FrameCount { get; }
    string[] ActorIds { get; }
    bool Complete { get; }
    int CachedPageCount { get; }
    int CachedEventCount { get; }
    string HistoryWarning { get; }
    string LoadingDiagnostic { get; }
    bool TrySample(double time, out ReplayTimelineSample sample);
    bool TryHistory(double time, out ReplayTimelineSample sample);
    void RequestHistory(double time);
    void SetLookAhead(bool advancing, double speed);
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
    public string HistoryWarning => "";
    public string LoadingDiagnostic => "memory-ready";
    public MemoryReplayTimeline(ReplayClip clip)
    {
        this.clip = clip;
        ActorIds = clip.Frames.SelectMany(f => f.Actors).Select(a => a.Id).Distinct(StringComparer.Ordinal).ToArray();
        events = new ReplayPageEvents(clip);
        joints = new ActorJointTimeline(clip.Frames, true);
    }
    public bool TrySample(double time, out ReplayTimelineSample sample)
    {
        sample = new ReplayTimelineSample(clip, time);
        if (!joints.Scene.CanSample(sample.Left,sample.Time)) return false;
        sample = sample.WithJoints(joints);
        return joints.CanSample(sample.Left.Actors, sample.Time) && joints.Ropes.CanSample(sample.Left.Ropes, sample.Time) &&
            joints.Creatures.CanSample(sample.Left.Creatures, sample.Time) && joints.Items.CanSample(sample.Left.Items, sample.Time);
    }
    public bool TryHistory(double time, out ReplayTimelineSample sample) => TrySample(time, out sample);
    public void RequestHistory(double time) { }
    public void SetLookAhead(bool advancing, double speed) { }
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
        public readonly ActorJointTimeline? Joints;
        public readonly SceneObservationTimeline? HistoryScene;
        public SceneObservationTimeline Scene => Joints?.Scene ?? HistoryScene!;
        public long SceneIndexBytes => Scene.EstimatedIndexBytes;
        public readonly long DecodedBytes;
        public long LastAccess;
        public LoadedPage(ReplayClip clip, bool knownEnd, CancellationToken token, bool history = false)
        {
            Clip = clip; Events = new ReplayPageEvents(clip);
            var budget = new ContinuousReplayBudget();
            foreach (var frame in clip.Frames) { token.ThrowIfCancellationRequested(); budget.Commit(frame, budget.Additional(frame)); }
            DecodedBytes = budget.Bytes;
            if (!history) Joints = new ActorJointTimeline(clip.Frames, knownEnd, token);
            else
            {
                var scene = new SceneObservationTimeline.Builder();
                foreach(var frame in clip.Frames) { token.ThrowIfCancellationRequested();scene.Observe(frame); }
                HistoryScene=scene.Build(knownEnd);
            }
        }
    }
    private sealed class PageRead
    {
        public readonly int Index;
        public readonly bool History;
        public readonly LoadedPage? Page;
        public readonly Exception? Failure;
        public readonly double DecodeSeconds;
        public PageRead(int index, bool history, LoadedPage page, double decodeSeconds) { Index = index; History = history; Page = page; DecodeSeconds = decodeSeconds; }
        public PageRead(int index, bool history, Exception failure) { Index = index; History = history; Failure = failure; }
    }
    private sealed class JointWindow
    {
        public readonly int First, Last;
        public readonly LoadedPage[] Owners;
        public readonly ActorJointTimeline Joints;
        public long LastAccess;
        public JointWindow(int first, LoadedPage[] owners, ActorJointTimeline joints)
        { First = first; Last = first + owners.Length - 1; Owners = owners; Joints = joints; }
    }
    private readonly ReplayPageRange[] pages;
    private readonly long[] pageDecodedBytes;
    private readonly long[] historyDecodedBytes;
    private readonly Func<int, CancellationToken, ReplayClip> load;
    private readonly Func<int, CancellationToken, ReplayClip>? loadHistory;
    private readonly IDisposable? sourceOwnership;
    private readonly Dictionary<int, LoadedPage> cache = new();
    private readonly Dictionary<int, LoadedPage> historyCache = new();
    private readonly Dictionary<int, Exception> failures = new();
    private readonly Dictionary<int, Exception> historyFailures = new();
    private readonly SortedSet<int> historyRequests = new();
    private readonly List<int> remove = new();
    private readonly List<int> pendingReads = new(MaximumCachedPages);
    private readonly List<JointWindow> jointWindows = new();
    private Task? reading;
    private ConcurrentQueue<PageRead>? completedReads;
    private CancellationTokenSource? cancellation;
    private int readingIndex = -1, desired, sceneFirst, sceneLast, futureLimit, historyFirst;
    private bool readingHistory, readingAhead, historyRequested, disposed, sceneReady, lookAhead, buildingAhead;
    private double requestedTime;
    private double playbackSpeed = 1, longestDecodeSeconds = 1, lastDecodeSeconds;
    private int forwardLast, aheadReads, failedWarmFirst = -1, failedWarmLast = -1;
    private long accessSequence;
    private Task<ActorJointTimeline>? buildingJoints;
    private CancellationTokenSource? jointCancellation;
    private LoadedPage[]? buildingOwners;
    private int buildingFirst, indexBuilds;
    private string waitingFor = "initial-page";
    private int cancelledReads;
    // These are capacities, not allocations. Only the cursor and explicitly
    // requested emitter history and a bounded playing window cause reads;
    // unrelated recording pages stay cold.
    internal const long MaximumCachedDecodedBytes = 1024L * 1024 * 1024;
    // EstimateFrame charges an empty frame's object/header cost on every
    // decoded observation; all populated fields add nonnegative bytes. This
    // derives the minimum from the same accounting used by LoadedPage rather
    // than duplicating its current byte constant in the reservation policy.
    private static readonly long MinimumDecodedFrameBytes = RollingBuffer.EstimateFrame(new ReplayFrame());
    private readonly long cacheByteLimit;
    private const int MaximumCachedPages = 64, MaximumCachedJointWindows = 8;
    private const long MaximumCachedWindowIndexBytes = 128L * 1024 * 1024;
    internal const int MaximumForwardPages = 8;
    public ReplayHeader Header { get; }
    public double Duration { get; }
    public long FrameCount { get; }
    public string[] ActorIds { get; }
    public bool Complete { get; }
    public int CachedPageCount => cache.Count + historyCache.Count;
    public int CachedEventCount
    { get { int count = 0; foreach (var page in cache.Values) count += page.Events.Count; return count; } }
    public string HistoryWarning { get; private set; } = "";
    internal long CachedDecodedBytes
    {
        get
        {
            long bytes = 0;
            foreach (var page in cache.Values) bytes += page.DecodedBytes + page.SceneIndexBytes;
            foreach (var page in historyCache.Values) bytes += page.DecodedBytes + page.SceneIndexBytes;
            return bytes;
        }
    }
    internal int CachedHistoryPageCount => historyCache.Count;
    internal int PendingPageCount => reading == null ? 0 : 1;
    internal int PendingJointIndexCount => buildingJoints == null ? 0 : 1;
    internal int JointIndexBuildCount => Volatile.Read(ref indexBuilds);
    internal int CachedJointWindowCount => jointWindows.Count;
    internal long CachedWindowIndexBytes
    { get { long bytes = 0; foreach (var window in jointWindows) bytes += window.Joints.EstimatedIndexBytes; return bytes; } }
    public string LoadingDiagnostic => $"wait={waitingFor}; targetPage={desired}; required={sceneFirst}..{sceneLast}; futureLimit={futureLimit}; forward={desired}..{forwardLast}; reader={(reading == null ? "idle" : readingHistory ? "history" : readingAhead ? "ahead" : "main")}; readingPage={Volatile.Read(ref readingIndex)}; readerCancelled={cancellation?.IsCancellationRequested == true}; cancelledReads={cancelledReads}; indexPending={buildingJoints != null}; cachedMiB={CachedDecodedBytes / 1048576d:F1}; historyPages={historyCache.Count}; aheadReads={aheadReads}; lastDecodeMs={lastDecodeSeconds * 1000:F1}";

    public PagedReplayTimeline(ReplayHeader header, double duration, long frameCount, string[] actorIds,
        ReplayPageRange[] pages, Func<int, CancellationToken, ReplayClip> load, bool complete = true,
        long[]? decodedPageBytes = null, IDisposable? sourceOwnership = null,
        Func<int, CancellationToken, ReplayClip>? historyLoader = null, long[]? historyDecodedPageBytes = null,
        long cacheByteLimit = MaximumCachedDecodedBytes)
    {
        if (cacheByteLimit < 1 || cacheByteLimit > MaximumCachedDecodedBytes)
            throw new ArgumentOutOfRangeException(nameof(cacheByteLimit));
        this.cacheByteLimit = cacheByteLimit;
        if (!ReplayRules.Finite(duration) || duration < 0 || frameCount < 2 || pages.Length == 0 || pages.Length > 100000)
            throw new InvalidDataException("Invalid continuous replay timeline.");
        for (int i = 0; i < pages.Length; i++)
            if (!ReplayRules.Finite(pages[i].Start) || !ReplayRules.Finite(pages[i].End) || pages[i].Start < 0 ||
                pages[i].End < pages[i].Start || pages[i].End > duration ||
                i > 0 && (pages[i].Start < pages[i - 1].Start || pages[i].Start < pages[i - 1].End))
                throw new InvalidDataException("Invalid continuous replay page ranges.");
        if (pages[0].Start != 0 || pages[pages.Length - 1].End != duration ||
            actorIds.Any(string.IsNullOrEmpty) || actorIds.Distinct(StringComparer.Ordinal).Count() != actorIds.Length)
            throw new InvalidDataException("Invalid continuous replay participants or endpoints.");
        if (decodedPageBytes != null && (decodedPageBytes.Length != pages.Length || decodedPageBytes.Any(bytes => bytes < 1 || bytes > MaximumCachedDecodedBytes)))
            throw new InvalidDataException("Invalid continuous replay decoded page budgets.");
        if (historyDecodedPageBytes != null && (historyDecodedPageBytes.Length != pages.Length || historyDecodedPageBytes.Any(bytes => bytes < 1 || bytes > MaximumCachedDecodedBytes)))
            throw new InvalidDataException("Invalid continuous replay decoded history budgets.");
        Header = header; Duration = duration; FrameCount = frameCount; ActorIds = (string[])actorIds.Clone(); Complete = complete;
        this.pages = (ReplayPageRange[])pages.Clone(); this.load = load; loadHistory = historyLoader; this.sourceOwnership = sourceOwnership;
        pageDecodedBytes = decodedPageBytes == null ? new long[pages.Length] : (long[])decodedPageBytes.Clone();
        historyDecodedBytes = historyDecodedPageBytes == null ? new long[pages.Length] : (long[])historyDecodedPageBytes.Clone();
        futureLimit = Math.Min(1, pages.Length - 1);
        Schedule();
    }

    // Only an actively playing cursor opts into bounded forward reads. A cold
    // seek and paused inspection retain the original target-only IO contract.
    // This setter does not schedule against the cursor from the previous tick.
    public void SetLookAhead(bool advancing, double speed)
    {
        lookAhead = advancing && ReplayRules.Finite(speed) && speed > 0;
        playbackSpeed = lookAhead ? Math.Max(.01, Math.Min(8, speed)) : 1;
        if (!lookAhead)
        {
            forwardLast = desired;
            if (readingAhead && reading != null && !InScene(Volatile.Read(ref readingIndex))) CancelRead();
            if (buildingAhead) jointCancellation?.Cancel();
        }
    }

    private void UpdateForwardRange()
    {
        forwardLast = desired;
        if (!lookAhead) return;
        double seconds = Math.Min(8, Math.Max(2, longestDecodeSeconds * 3 + 1)) * playbackSpeed;
        int last = Math.Min(pages.Length - 1, Math.Min(desired + MaximumForwardPages,
            Math.Max(desired + 1, FindPage(Math.Min(Duration, requestedTime + seconds)))));
        long required = 0;
        int protectedPages = sceneLast - sceneFirst + 1;
        for (int index = sceneFirst; index <= sceneLast; index++)
        {
            long bytes = cache.TryGetValue(index, out var page) ? page.DecodedBytes : pageDecodedBytes[index];
            if (bytes == 0) return; // Unknown cold sizes cannot reserve optional work safely.
            required += bytes;
        }
        for (int index = desired + 1; index <= last; index++)
        {
            if (InScene(index)) { forwardLast = index; continue; }
            long bytes = cache.TryGetValue(index, out var page) ? page.DecodedBytes : pageDecodedBytes[index];
            if (bytes == 0 || required + bytes > cacheByteLimit || protectedPages >= MaximumCachedPages) break;
            required += bytes; protectedPages++; forwardLast = index;
        }
    }

    private bool InForward(int index) => lookAhead && index > desired && index <= forwardLast;
    // Always secure the immediately following scene page first. Further pages
    // use the measured-latency window, but cannot exclude explicit history from
    // the cache merely because a short-page recording has several near pages.
    private int UrgentForwardLast => !lookAhead ? desired : Math.Min(forwardLast, desired + 1);
    private bool HasMissingUrgentForward()
    { for (int index = desired + 1; index <= UrgentForwardLast; index++) if (Missing(index)) return true; return false; }
    private void CancelRead()
    { if (cancellation?.IsCancellationRequested == false) { cancelledReads++; cancellation.Cancel(); } }

    public bool TrySample(double time, out ReplayTimelineSample sample)
    {
        if (disposed) throw new ObjectDisposedException(nameof(PagedReplayTimeline));
        if (!ReplayRules.Finite(time)) throw new ArgumentOutOfRangeException(nameof(time));
        sample = default; requestedTime = Math.Max(0, Math.Min(Duration, time));
        int target = FindPage(requestedTime);
        if (target != desired)
        {
            desired = sceneFirst = sceneLast = historyFirst = target;
            historyRequested = false; historyRequests.Clear(); sceneReady = false; HistoryWarning = "";
            jointCancellation?.Cancel();
        }
        futureLimit = Math.Max(Math.Min(desired + 1, pages.Length - 1), FindPage(Math.Min(Duration, requestedTime + .5)));
        UpdateForwardRange();
        Poll(); PollJoints();
        if (reading != null &&
            (readingHistory && (HasMissingScene() || sceneReady && HasMissingUrgentForward()) ||
             !readingHistory && !InScene(Volatile.Read(ref readingIndex)) && (HasMissingScene() || !InForward(Volatile.Read(ref readingIndex)))))
            CancelRead();
        ThrowSceneFailures();
        if (!cache.TryGetValue(desired, out var page)) { waitingFor = "target-page"; Schedule(); return false; }
        Touch(page);
        var last = page.Clip.Frames[page.Clip.Frames.Count - 1];
        if (requestedTime > last.T && desired + 1 < pages.Length && pages[desired + 1].Start - last.T <= .5)
        {
            sceneLast = Math.Max(sceneLast, desired + 1);
            if (!cache.TryGetValue(desired + 1, out var next)) { waitingFor = "next-frame-page"; Schedule(); return false; }
            var first = next.Clip.Frames[0]; double gap = first.T - last.T;
            if (gap > 0 && gap <= .5)
                sample = new ReplayTimelineSample(last, first, (float)Math.Max(0, Math.Min(1, (requestedTime - last.T) / gap)));
            else sample = new ReplayTimelineSample(page.Clip, requestedTime);
        }
        else sample = new ReplayTimelineSample(page.Clip, requestedTime);
        // The predecessor witnesses only an opening baseline. Once this page's
        // real observations cover the displayed pose, retain its immutable
        // single-page index instead of expanding an old predecessor window at
        // every subsequent boundary.
        if ((sceneFirst != desired || sceneLast != desired) &&
            page.Joints!.CanSample(sample.Left.Actors, sample.Time) &&
            page.Joints.Ropes.CanSample(sample.Left.Ropes, sample.Time) &&
            page.Joints.Creatures.CanSample(sample.Left.Creatures, sample.Time) &&
            page.Joints.Items.CanSample(sample.Left.Items, sample.Time) &&
            page.Joints.Scene.CanSample(sample.Left, sample.Time) &&
            page.Joints.Environment.TrySample(sample.Time, out _) &&
            !page.Joints.RequiresPredecessor(sample.Left.Actors, sample.Time))
        { sceneFirst = sceneLast = desired; }
        for (int attempt = 0; attempt < MaximumCachedPages; attempt++)
        {
            ThrowSceneFailures();
            if (sceneLast - sceneFirst + 1 > MaximumCachedPages) throw new InvalidDataException("人物关节所需的读取范围超过分页数量上限。");
            if (HasMissingScene()) { waitingFor = "sample-pages"; Schedule(); return false; }
            var joints = GetJoints(sceneFirst, sceneLast);
            if (joints == null) { waitingFor = "sample-index"; Schedule(); return false; }
            // Full page baselines can carry clocks older than their first frame.
            // Only those unresolved keys require a predecessor cut witness.
            if (sceneFirst > 0 && joints.RequiresPredecessor(sample.Left.Actors, sample.Time))
            { sceneFirst--; sceneReady = false; continue; }
            if (joints.CanSample(sample.Left.Actors, sample.Time) && joints.Ropes.CanSample(sample.Left.Ropes, sample.Time) &&
                joints.Creatures.CanSample(sample.Left.Creatures, sample.Time) && joints.Items.CanSample(sample.Left.Items, sample.Time) &&
                joints.Scene.CanSample(sample.Left,sample.Time) && joints.Environment.TrySample(sample.Time, out _))
            { sample = sample.WithJoints(joints); sceneReady = true; waitingFor = "ready"; UpdateForwardRange(); WarmForwardIndex(); Schedule(); return true; }
            if (sceneLast < futureLimit) { sceneLast++; sceneReady = false; continue; }
            waitingFor = !joints.CanSample(sample.Left.Actors, sample.Time) ? "joint-observation" :
                !joints.Ropes.CanSample(sample.Left.Ropes, sample.Time) ? "rope-observation" :
                !joints.Creatures.CanSample(sample.Left.Creatures, sample.Time) ? "creature-observation" :
                !joints.Items.CanSample(sample.Left.Items, sample.Time) ? "item-observation" :
                !joints.Scene.CanSample(sample.Left,sample.Time) ? "scene-observation" : "environment-observation";
            Schedule(); return false;
        }
        throw new InvalidDataException("人物关节所需的读取范围超过分页数量上限。");
    }

    public void RequestHistory(double time)
    {
        if (disposed || !ReplayRules.Finite(time)) return;
        // A paused scene no longer calls TrySample every tick. Admit completed
        // history and release a cancelled warm index here, without moving time.
        Poll(); PollJoints();
        int first = FindPage(Math.Max(0, Math.Min(requestedTime, time)));
        // Several emitters can request different pre-roll origins in one frame.
        // Keep their missing points independently until queried successfully.
        historyFirst = historyRequested ? Math.Min(historyFirst, first) : first; historyRequested = true;
        RequestHistoryPage(first);
        if (time > pages[first].End && first + 1 < pages.Length) RequestHistoryPage(first + 1);
        else if(first+1<pages.Length && TryHistoryPage(first,out var loaded) &&
            loaded.Clip.Frames[0].SceneSampleTimeKnown && !loaded.Scene.TrySample(time,out _)) RequestHistoryPage(first+1);
        while (historyRequests.Count > MaximumCachedPages) historyRequests.Remove(historyRequests.Max);
        Schedule();
    }
    private void RequestHistoryPage(int index)
    {
        if (historyFailures.ContainsKey(index)) { HistoryWarning = "部分特效历史无法读取，人物画面仍可播放。"; return; }
        if (!TryHistoryPage(index, out _)) historyRequests.Add(index);
    }

    // This query never reads, schedules, or evicts. Missing history means wait;
    // callers must not substitute the current emitter transform for that time.
    public bool TryHistory(double time, out ReplayTimelineSample sample)
    {
        sample = default;
        if (disposed || !ReplayRules.Finite(time) || time < 0 || time > Duration) return false;
        int index = FindPage(time);
        if (!TryHistoryPage(index, out var page)) return false;
        Touch(page);
        if (time > pages[index].End)
        {
            if (!TryHistoryPage(index + 1, out var next)) return false;
            var left = page.Clip.Frames[page.Clip.Frames.Count - 1]; var right = next.Clip.Frames[0];
            double gap = right.T - left.T;
            if (gap <= 0 || gap > .5) return false;
            sample = new ReplayTimelineSample(left, right, (float)Math.Max(0, Math.Min(1, (time - left.T) / gap)));
            if(!AttachHistoryScene(page,next,time,ref sample)) return false;
            historyRequests.Remove(index); historyRequests.Remove(index + 1); return true;
        }
        sample = new ReplayTimelineSample(page.Clip, time);
        TryHistoryPage(index+1,out var successor);
        if(!AttachHistoryScene(page,successor,time,ref sample)) return false;
        historyRequests.Remove(index); return true;
    }
    private static bool AttachHistoryScene(LoadedPage page, LoadedPage? next, double time, ref ReplayTimelineSample sample)
    {
        if(!sample.Left.SceneSampleTimeKnown) return true;
        if(page.Scene.TrySample(time,out var scene) || next != null && page.Scene.TrySampleWithSuccessor(next.Scene,time,out scene))
        { sample=sample.WithScene(scene);return true; }
        return false;
    }
    private bool TryHistoryPage(int index, out LoadedPage page) => cache.TryGetValue(index, out page!) || historyCache.TryGetValue(index, out page!);

    public void FillRecentEvents(double time, string actorId, List<ItemEvent> output)
    {
        output.Clear(); foreach (var page in cache.Values) page.Events.Append(time, actorId, output);
        ReplayPageEvents.Finish(output);
    }

    private ActorJointTimeline? GetJoints(int first, int last)
    {
        if (first == last) return cache[first].Joints!;
        foreach (var window in jointWindows)
            if (window.First == first && window.Last == last) { window.LastAccess = ++accessSequence; return window.Joints; }
        if (buildingJoints != null)
        {
            if (buildingFirst != first || buildingOwners!.Length != last - first + 1) jointCancellation?.Cancel();
            return null;
        }
        StartJointWindow(first, last, false);
        return null;
    }
    private void StartJointWindow(int first, int last, bool ahead)
    {
        buildingAhead = ahead;
        buildingOwners = Enumerable.Range(first, last - first + 1).Select(index => cache[index]).ToArray();
        var owners = buildingOwners; buildingFirst = first;
        jointCancellation = new CancellationTokenSource(); var token = jointCancellation.Token;
        bool knownEnd = last == pages.Length - 1;
        buildingJoints = Task.Run(() =>
        {
            Interlocked.Increment(ref indexBuilds);
            return new ActorJointTimeline(owners.SelectMany(owner => owner.Clip.Frames), knownEnd, token);
        }, token);
    }

    private void WarmForwardIndex()
    {
        if (!lookAhead || reading != null || buildingJoints != null || desired + 1 >= pages.Length ||
            !cache.ContainsKey(desired + 1)) return;
        // Both the current page's closing observations and the next page's
        // opening baseline use exactly this immutable two-page window. Using
        // sceneFirst here warmed a three-page window while the next tick needed
        // a different two-page key, rebuilding the same joint tracks again.
        int first = desired, last = desired + 1;
        if (last - first + 1 > MaximumCachedPages || first == failedWarmFirst && last == failedWarmLast) return;
        foreach (var window in jointWindows) if (window.First == first && window.Last == last) return;
        for (int index = first; index <= last; index++) if (!cache.ContainsKey(index)) return;
        StartJointWindow(first, last, true);
    }

    private void PollJoints()
    {
        if (buildingJoints == null || !buildingJoints.IsCompleted) return;
        try
        {
            var joints = buildingJoints.GetAwaiter().GetResult();
            var owners = buildingOwners!;
            bool retained = true;
            for (int i = 0; i < owners.Length; i++)
                if (!cache.TryGetValue(buildingFirst + i, out var owner) || !ReferenceEquals(owner, owners[i])) { retained = false; break; }
            if (retained)
            {
                // Cache by immutable page ownership, not the ever-changing read
                // revision. Emitter history arriving cannot invalidate this index.
                var window = new JointWindow(buildingFirst, owners, joints) { LastAccess = ++accessSequence };
                jointWindows.Add(window);
                while (jointWindows.Count > MaximumCachedJointWindows ||
                    jointWindows.Count > 1 && CachedWindowIndexBytes > MaximumCachedWindowIndexBytes)
                    jointWindows.Remove(jointWindows.OrderBy(value => value.LastAccess).First());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (buildingAhead)
        { failedWarmFirst = buildingFirst; failedWarmLast = buildingFirst + buildingOwners!.Length - 1; }
        finally { buildingJoints = null; buildingOwners = null; buildingAhead = false; jointCancellation?.Dispose(); jointCancellation = null; }
    }

    private void Touch(LoadedPage page) => page.LastAccess = ++accessSequence;
    private bool InScene(int index) => index >= sceneFirst && index <= sceneLast;
    private bool Wanted(int index, bool history) => history ? historyRequests.Contains(index) : InScene(index) || InForward(index);
    private bool ProtectedMain(int index) => InScene(index) || InForward(index);
    private bool Missing(int index) => !cache.ContainsKey(index) && !failures.ContainsKey(index);
    private bool HasMissingScene()
    { for (int i = sceneFirst; i <= sceneLast; i++) if (!cache.ContainsKey(i)) return true; return false; }
    private void ThrowSceneFailures()
    {
        for (int i = sceneFirst; i <= sceneLast; i++)
            if (failures.TryGetValue(i, out var failure)) throw new InvalidDataException("无法读取完整录像的所需时间范围。", failure);
    }
    private int FindPage(double time)
    {
        int lo = 0, hi = pages.Length - 1;
        while (lo < hi) { int middle = (lo + hi + 1) / 2; if (pages[middle].Start <= time) lo = middle; else hi = middle - 1; }
        return lo;
    }
    private void Evict(int index)
    {
        jointWindows.RemoveAll(window => index >= window.First && index <= window.Last);
        if (buildingOwners != null && index >= buildingFirst && index < buildingFirst + buildingOwners.Length) jointCancellation?.Cancel();
        cache.Remove(index);
    }
    private bool ReserveReads(List<int> pending, bool history, bool ahead, bool urgent = false)
    {
        long reserve = 0, sceneReserve = 0; bool known = true;
        var bounds = history ? historyDecodedBytes : pageDecodedBytes;
        foreach (int index in pending)
        {
            if (bounds[index] == 0) known = false;
            else
            {
                reserve += bounds[index];
                // At most one scene key exists per decoded frame. Before
                // loading, frame count is bounded by its declared decoded cost.
                // Old schemas have no scene index and need no extra reserve.
                if (Header.Schema >= 22) sceneReserve += 96 + 32 * (bounds[index] / MinimumDecodedFrameBytes);
            }
        }
        if (!known)
        {
            if (ahead) return false;
            remove.Clear(); foreach (int index in cache.Keys) if (!ProtectedForRead(index, history, ahead, urgent) && (!history || !historyRequests.Contains(index))) remove.Add(index);
            foreach (int index in remove) Evict(index);
            remove.Clear(); foreach (int index in historyCache.Keys) if (!history || !historyRequests.Contains(index)) remove.Add(index);
            foreach (int index in remove) historyCache.Remove(index);
            return true;
        }
        long bytes = CachedDecodedBytes;
        while (bytes + reserve + sceneReserve > cacheByteLimit || CachedPageCount + pending.Count > MaximumCachedPages)
        {
            int oldest = -1; bool oldHistory = false; long age = long.MaxValue;
            foreach (var pair in cache)
                if (!ProtectedForRead(pair.Key, history, ahead, urgent) && (!(history || ahead && !urgent) || !historyRequests.Contains(pair.Key)) && pair.Value.LastAccess < age) { oldest = pair.Key; age = pair.Value.LastAccess; }
            foreach (var pair in historyCache)
                if ((!(history || ahead && !urgent) || !historyRequests.Contains(pair.Key)) && pair.Value.LastAccess < age) { oldest = pair.Key; oldHistory = true; age = pair.Value.LastAccess; }
            if (oldest < 0)
            {
                // A declared upper bound may describe a tiny legacy/no-scene
                // page. Once all eligible old pages have been reclaimed, only
                // the speculative scene estimate may be relaxed. The original
                // decoded reservation and worker/Poll actual total stay strict.
                if (bytes + reserve <= cacheByteLimit && CachedPageCount + pending.Count <= MaximumCachedPages) return true;
                if (history) { HistoryWarning = "特效历史超出回放缓存预算，人物画面仍可播放。"; return false; }
                if (ahead) return false;
                throw new InvalidDataException("人物关节所需的读取范围超过回放内存预算。");
            }
            if (oldHistory) { bytes -= historyCache[oldest].DecodedBytes + historyCache[oldest].SceneIndexBytes; historyCache.Remove(oldest); }
            else { bytes -= cache[oldest].DecodedBytes + cache[oldest].SceneIndexBytes; Evict(oldest); }
        }
        return true;
    }
    private bool ProtectedForRead(int index, bool history, bool ahead, bool urgent) => InScene(index) ||
        (history || urgent ? InForward(index) && index <= UrgentForwardLast : ahead && InForward(index));
    private void RecordFailure(int index, bool history, Exception error)
    {
        if (history) { historyFailures[index] = error; historyRequests.Remove(index); HistoryWarning = "部分特效历史无法读取，人物画面仍可播放。"; }
        else failures[index] = error;
    }
    private void Poll()
    {
        if (reading == null) return;
        while (completedReads != null && completedReads.TryDequeue(out var result))
        {
            if (!Wanted(result.Index, result.History)) continue;
            if (result.Failure != null) { RecordFailure(result.Index, result.History, result.Failure); continue; }
            var page = result.Page!;
            if (CachedDecodedBytes + page.DecodedBytes + page.SceneIndexBytes > cacheByteLimit || CachedPageCount >= MaximumCachedPages)
            { RecordFailure(result.Index, result.History, new InvalidDataException("回放页面超过缓存预算。")); cancellation?.Cancel(); continue; }
            Touch(page);
            if (result.History) { historyCache[result.Index] = page; historyDecodedBytes[result.Index] = page.DecodedBytes; }
            else
            {
                historyCache.Remove(result.Index); cache[result.Index] = page; pageDecodedBytes[result.Index] = page.DecodedBytes;
                lastDecodeSeconds = result.DecodeSeconds;
                longestDecodeSeconds = Math.Max(longestDecodeSeconds, result.DecodeSeconds);
            }
        }
        if (!reading.IsCompleted) return;
        try { reading.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (Wanted(readingIndex, readingHistory)) RecordFailure(readingIndex, readingHistory, error); }
        if (completedReads != null && !completedReads.IsEmpty) return;
        reading = null; readingIndex = -1; completedReads = null; cancellation?.Dispose(); cancellation = null;
    }
    private void Schedule()
    {
        if (disposed || reading != null) return;
        // A cancelled index can still own old immutable pages until its worker
        // observes cancellation. Release those owners before reserving a new
        // decode, so they cannot sit outside the shared page-memory budget.
        if (buildingJoints != null)
        { if (HasMissingScene()) jointCancellation?.Cancel(); return; }
        // This list is owned by the sole page worker until Poll releases it.
        // A ready idle frame must not allocate a fresh list just to find no IO.
        var pending = pendingReads; pending.Clear(); bool history = false, ahead = false, urgent = false;
        if (Missing(desired)) pending.Add(desired);
        for (int i = desired - 1; i >= sceneFirst; i--) if (Missing(i)) pending.Add(i);
        for (int i = desired + 1; i <= sceneLast; i++) if (Missing(i)) pending.Add(i);
        // Deadline-near scene pages precede particle history. Farther optional
        // pages wait behind explicitly requested history, and may be evicted by
        // it. A large lookahead window must not starve paused/native effects.
        if (pending.Count == 0 && sceneReady && lookAhead)
            for (int index = desired + 1; index <= UrgentForwardLast; index++)
                if (Missing(index))
                {
                    pending.Add(index);
                    if (ReserveReads(pending, false, true, true)) { ahead = urgent = true; break; }
                    pending.Clear(); break;
                }
        // Each particle cursor requests its actual missing sample. Do not decode
        // the whole interval from its oldest origin to the displayed cursor.
        if (pending.Count == 0 && sceneReady && historyRequested && historyRequests.Count != 0)
        {
            foreach (int index in historyRequests)
                if (!TryHistoryPage(index, out _) && !historyFailures.ContainsKey(index)) { pending.Add(index); history = true; break; }
        }
        if (pending.Count == 0 && sceneReady && lookAhead)
            for (int index = desired + 1; index <= forwardLast; index++)
                if (Missing(index)) { pending.Add(index); ahead = true; break; }
        if (pending.Count == 0) return;
        if (!history) foreach (int index in pending) historyCache.Remove(index);
        if (!ReserveReads(pending, history, ahead, urgent)) return;
        StartRead(pending, history, ahead);
    }
    private void StartRead(List<int> pending, bool history, bool ahead)
    {
        readingIndex = pending[0]; readingHistory = history; readingAhead = ahead;
        if (ahead) aheadReads++;
        cancellation = new CancellationTokenSource(); var token = cancellation.Token;
        var completed = new ConcurrentQueue<PageRead>(); completedReads = completed;
        long decodedBytes = CachedDecodedBytes;
        long readByteLimit = cacheByteLimit;
        var reserved = pending.Select(index => history ? historyDecodedBytes[index] : pageDecodedBytes[index]).ToArray();
        var loader = history ? loadHistory ?? load : load;
        reading = Task.Run(() =>
        {
            for (int position = 0; position < pending.Count; position++)
            {
                int index = pending[position];
                token.ThrowIfCancellationRequested(); Volatile.Write(ref readingIndex, index);
                try
                {
                    var elapsed = Stopwatch.StartNew();
                    var clip = loader(index, token); token.ThrowIfCancellationRequested(); var range = pages[index];
                    if (!clip.Complete || clip.Frames.Count < 1 || Math.Abs(clip.Frames[0].T - range.Start) > 1e-6 || Math.Abs(clip.Duration - range.End) > 1e-6)
                        throw new InvalidDataException("Continuous replay page does not match its index.");
                    var result = new LoadedPage(clip, index == pages.Length - 1, token, history);
                    if (!history) Interlocked.Increment(ref indexBuilds); token.ThrowIfCancellationRequested();
                    if (reserved[position] != 0 && result.DecodedBytes > reserved[position])
                        throw new InvalidDataException("Continuous replay page exceeds its decoded memory reservation.");
                    decodedBytes += result.DecodedBytes + result.SceneIndexBytes;
                    if (decodedBytes > readByteLimit) throw new InvalidDataException("人物关节所需的读取范围超过回放内存预算。");
                    completed.Enqueue(new PageRead(index, history, result, elapsed.Elapsed.TotalSeconds));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { completed.Enqueue(new PageRead(index, history, error)); break; }
            }
        }, token);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CancelAndRelease(cancellation, reading); cancellation = null; reading = null; completedReads = null;
        CancelAndRelease(jointCancellation, buildingJoints); jointCancellation = null; buildingJoints = null; buildingOwners = null;
        jointWindows.Clear(); cache.Clear(); historyCache.Clear(); failures.Clear(); historyFailures.Clear(); historyRequests.Clear(); sourceOwnership?.Dispose();
    }
    private static void CancelAndRelease(CancellationTokenSource? source, Task? task)
    {
        source?.Cancel();
        if (task == null) { source?.Dispose(); return; }
        _ = task.ContinueWith(completed => { _ = completed.Exception; source?.Dispose(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
