using System.Diagnostics;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using PeakReplayLab;

// Offline, latest-reader comparison of two encodings of the same sampled run.
// No writer, game process, disk seek cache, or decoded page cache participates.
internal static class PairedReplayDecodeInspection
{
    private const int MaximumSelectedPages = 12, Rounds = 3, RandomSeed = 41827;

    public static void Run(string baselinePath, string candidatePath, string outputDirectory)
    {
        using var process = Process.GetCurrentProcess();
        ProcessPriorityClass? previousPriority = null;
        bool belowNormal = false;
        try
        {
            previousPriority = process.PriorityClass;
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            belowNormal = process.PriorityClass == ProcessPriorityClass.BelowNormal;
        }
        catch { }
        try { RunCore(baselinePath, candidatePath, outputDirectory, belowNormal); }
        finally
        {
            if (previousPriority.HasValue)
                try { process.PriorityClass = previousPriority.Value; } catch { }
        }
    }

    private static void RunCore(string baselinePath, string candidatePath, string outputDirectory, bool belowNormal)
    {
        string oldPath = Path.GetFullPath(baselinePath), newPath = Path.GetFullPath(candidatePath);
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Paired comparison requires two distinct archive files.");
        string directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        string reportPath = Path.Combine(directory, "paired-decode.json");
        if (File.Exists(reportPath)) throw new IOException("Paired report already exists; select a new output directory.");
        var baselineInfo = FullReplayArchive.ReadInfo(oldPath);
        var candidateInfo = FullReplayArchive.ReadInfo(newPath);
        CheckComparable(baselineInfo, candidateInfo);
        int[] selected = SelectPages(baselineInfo.Pages.Length);
        using var baseline = new Source(oldPath, baselineInfo);
        using var candidate = new Source(newPath, candidateInfo);

        // Exercise every selected page once in both formats, so later pages'
        // first code paths/I/O do not leak into the steady repeated comparison.
        // Preserve these first touches as a separate measured series: warming
        // must never hide startup tails or be presented as cold seek evidence.
        var warmupRows = new List<PairMeasurement>(selected.Length);
        int[] warmupOrder = Order(selected, -1);
        for (int slot = 0; slot < warmupOrder.Length; slot++)
        {
            int page = warmupOrder[slot];
            warmupRows.Add(MeasurePair(baseline, candidate, page, Array.IndexOf(selected, page) % 2 != 0,
                0, slot, baselineInfo.Pages[page].Start));
        }
        var rows = new List<PairMeasurement>(selected.Length * Rounds);
        for (int round = 0; round < Rounds; round++)
        {
            int[] order = Order(selected, round);
            for (int slot = 0; slot < order.Length; slot++)
            {
                int page = order[slot];
                // Each particular page flips its first reader every round,
                // even though the page visit order is shuffled independently.
                bool candidateFirst = (round + Array.IndexOf(selected, page)) % 2 != 0;
                rows.Add(MeasurePair(baseline, candidate, page, candidateFirst, round + 1, slot, baselineInfo.Pages[page].Start));
            }
        }

        // Retain at most one page from each source. Do not allocate reflection
        // comparison state between timed loads, where it could skew later GCs.
        long verifiedFrames = 0, verifiedJointClocks = 0;
        foreach (int page in selected)
        {
            var verified = VerifyPair(baseline, candidate, page);
            verifiedFrames += verified.Frames; verifiedJointClocks += verified.JointClocks;
        }
        var report = new
        {
            BaselineFile = oldPath, CandidateFile = newPath,
            BaselineSchema = baselineInfo.Header.Schema, CandidateSchema = candidateInfo.Header.Schema,
            BaselineContainerVersion = baselineInfo.Version, CandidateContainerVersion = candidateInfo.Version,
            SampleHz = baselineInfo.Header.SampleHz, PageCount = baselineInfo.Pages.Length,
            SelectedPages = selected, SelectedPageCount = selected.Length, Rounds, RandomSeed,
            TimedPairs = rows.Count, WarmupLoadsPerFile = selected.Length, ProcessBelowNormal = belowNormal,
            WarmupPolicy = "one fixed shuffled pass over all selected pages in each format; first-touch timings preserved separately",
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ServerGC = System.Runtime.GCSettings.IsServerGC,
            Reader = "same latest-reader process; independent reusable raw scratch per file; no decoded cache",
            Timer = "Stopwatch.GetTimestamp immediately around archive page Load only",
            Allocation = "GC.GetAllocatedBytesForCurrentThread delta around Load only",
            Baseline = Summary(rows.Select(row => row.Baseline)), Candidate = Summary(rows.Select(row => row.Candidate)),
            PairedCandidateToBaseline = Ratios(rows),
            FirstTouchPreconditioning = new
            {
                Baseline = Summary(warmupRows.Select(row => row.Baseline)), Candidate = Summary(warmupRows.Select(row => row.Candidate)),
                PairedCandidateToBaseline = Ratios(warmupRows), Rows = warmupRows,
                Note = "First selected-page touch in this process, including runtime initialization; OS caches are uncontrolled. These samples are retained separately and are not silently discarded from the report.",
            },
            ByPage = rows.GroupBy(row => row.Page).OrderBy(group => group.Key).Select(group => new
            {
                Page = group.Key, Start = baselineInfo.Pages[group.Key].Start, End = baselineInfo.Pages[group.Key].End,
                Frames = baselineInfo.Pages[group.Key].FrameCount,
                Baseline = Summary(group.Select(row => row.Baseline)), Candidate = Summary(group.Select(row => row.Candidate)),
                PairedCandidateToBaseline = Ratios(group),
            }).ToArray(),
            ByRound = rows.GroupBy(row => row.Round).Select(group => new
            {
                Round = group.Key, Baseline = Summary(group.Select(row => row.Baseline)),
                Candidate = Summary(group.Select(row => row.Candidate)), PairedCandidateToBaseline = Ratios(group),
            }).ToArray(),
            ByFirstReader = rows.GroupBy(row => row.CandidateFirst).Select(group => new
            {
                First = group.Key ? "candidate" : "baseline", Pairs = group.Count(), PairedCandidateToBaseline = Ratios(group),
            }).ToArray(),
            AllSelectedFrameFieldsEqual = true, JointSampleTimesBitExact = true, VerifiedFrames = verifiedFrames,
            VerifiedJointClocks = verifiedJointClocks, ValidationAfterAllTimedLoads = true,
            Rows = rows,
            Note = "Paired warmed decoder measurements, not cold disk latency or Unity frame rate. Both formats are read by this executable's current reader. All selected pages receive one preconditioning read in each format, whose complete timings are separately retained. Operating-system caches are not cleared; no forced collection is used. Natural collections during Load are included; GC pause deltas are process-wide diagnostics and may include overlapping background collection. Candidate/baseline ratios below 1 favor candidate. P95 uses nearest rank; twelve representative pages across three rounds are a small diagnostic sample. No timed outliers are removed. Full-field and exact joint-clock validation runs after all measurements; effects in main frames are included, separate effect-history loading is not timed. Original archives are read-only.",
        };
        string json = JsonConvert.SerializeObject(report, Formatting.Indented);
        // Refuse accidental replacement of an earlier diagnostic or an input.
        using (var output = new StreamWriter(new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write))) output.Write(json);
        Console.WriteLine(json);
    }

    private sealed class Source : IDisposable
    {
        private readonly ReplayLineBufferPool lines = new();
        private readonly FullReplaySharedScratchPool shared = new();
        private readonly Func<int, CancellationToken, ReplayClip> load;
        internal Source(string path, FullReplayInfo info) =>
            load = FullReplayArchive.CreatePageLoader(path, info, buffers: lines, sharedBuffers: shared);
        internal ReplayClip Load(int page) => load(page, default);
        public void Dispose() { lines.Dispose(); shared.Dispose(); }
    }

    private sealed class Measurement
    {
        public double Milliseconds { get; set; }
        public long AllocatedBytes { get; set; }
        public int Gen0Collections { get; set; }
        public int Gen1Collections { get; set; }
        public int Gen2Collections { get; set; }
        public double GCPauseMilliseconds { get; set; }
        public int Frames { get; set; }
    }
    private sealed class PairMeasurement
    {
        public int Round { get; set; }
        public int Slot { get; set; }
        public int Page { get; set; }
        public double Start { get; set; }
        public bool CandidateFirst { get; set; }
        public Measurement Baseline { get; set; } = null!;
        public Measurement Candidate { get; set; } = null!;
        public double CandidateToBaselineTimeRatio { get; set; }
        public double CandidateToBaselineAllocationRatio { get; set; }
    }

    // Prevent JIT liveness from retaining the previous page across the next
    // load. Only small measurement objects survive a completed invocation.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Measurement Measure(Source source, int page)
    {
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        TimeSpan pause = GC.GetTotalPauseDuration();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        var clip = source.Load(page);
        long end = Stopwatch.GetTimestamp();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        TimeSpan elapsedPause = GC.GetTotalPauseDuration() - pause;
        int after0 = GC.CollectionCount(0), after1 = GC.CollectionCount(1), after2 = GC.CollectionCount(2);
        if (!clip.Complete || clip.Frames.Count == 0) throw new InvalidDataException("Paired load returned an incomplete page.");
        GC.KeepAlive(clip);
        return new Measurement { Milliseconds = (end - start) * 1000d / Stopwatch.Frequency, AllocatedBytes = bytes,
            Gen0Collections = after0 - gen0, Gen1Collections = after1 - gen1, Gen2Collections = after2 - gen2,
            GCPauseMilliseconds = elapsedPause.TotalMilliseconds, Frames = clip.Frames.Count };
    }
    private static PairMeasurement MeasurePair(Source baseline, Source candidate, int page, bool candidateFirst, int round, int slot, double start)
    {
        Measurement oldSample, newSample;
        if (candidateFirst)
        { newSample = Measure(candidate, page); oldSample = Measure(baseline, page); }
        else
        { oldSample = Measure(baseline, page); newSample = Measure(candidate, page); }
        return new PairMeasurement
        {
            Round = round, Slot = slot, Page = page, Start = start, CandidateFirst = candidateFirst,
            Baseline = oldSample, Candidate = newSample,
            CandidateToBaselineTimeRatio = newSample.Milliseconds / oldSample.Milliseconds,
            CandidateToBaselineAllocationRatio = (double)newSample.AllocatedBytes / oldSample.AllocatedBytes,
        };
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Frames, long JointClocks) VerifyPair(Source baseline, Source candidate, int page)
    {
        var oldClip = baseline.Load(page); var newClip = candidate.Load(page);
        if (oldClip.Frames.Count != newClip.Frames.Count) throw new InvalidDataException("Paired frame count differs at page " + page);
        var comparer = new ContinuousReplayInspection.ImmutableDataComparer();
        long clocks = 0;
        for (int i = 0; i < oldClip.Frames.Count; i++)
        {
            if (!comparer.Equal(oldClip.Frames[i], newClip.Frames[i]))
                throw new InvalidDataException($"Paired frame fields differ at page {page}, frame {i}.");
            clocks += EventStorageInspection.VerifyJointClocks(oldClip.Frames[i], newClip.Frames[i]);
        }
        return (oldClip.Frames.Count, clocks);
    }

    internal static void CheckComparable(FullReplayInfo baseline, FullReplayInfo candidate)
    {
        if (baseline.Pages.Length != candidate.Pages.Length || baseline.FrameCount != candidate.FrameCount ||
            BitConverter.DoubleToInt64Bits(baseline.Duration) != BitConverter.DoubleToInt64Bits(candidate.Duration) ||
            baseline.Header.SampleHz != candidate.Header.SampleHz)
            throw new InvalidDataException("Paired archives do not cover identical frames, times and sample rate.");
        for (int i = 0; i < baseline.Pages.Length; i++)
        {
            var a = baseline.Pages[i]; var b = candidate.Pages[i];
            if (a.Index != b.Index || a.FirstFrameIndex != b.FirstFrameIndex || a.FrameCount != b.FrameCount || a.Overlap != b.Overlap ||
                BitConverter.DoubleToInt64Bits(a.Start) != BitConverter.DoubleToInt64Bits(b.Start) ||
                BitConverter.DoubleToInt64Bits(a.End) != BitConverter.DoubleToInt64Bits(b.End))
                throw new InvalidDataException("Paired archive page boundaries differ at page " + i);
        }
    }
    internal static int[] SelectPages(int count)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        int selected = Math.Min(count, MaximumSelectedPages);
        if (selected == 1) return new[] { 0 };
        return Enumerable.Range(0, selected).Select(i => (int)Math.Round((count - 1d) * i / (selected - 1))).ToArray();
    }
    internal static int[] Order(int[] pages, int round)
    {
        var result = (int[])pages.Clone(); var random = new Random(RandomSeed + round);
        for (int i = result.Length - 1; i > 0; i--)
        { int other = random.Next(i + 1); (result[i], result[other]) = (result[other], result[i]); }
        return result;
    }
    private static object Summary(IEnumerable<Measurement> values)
    {
        var samples = values.ToArray();
        return new { Loads = samples.Length, MedianMs = Median(samples.Select(x => x.Milliseconds)),
            P95Ms = P95(samples.Select(x => x.Milliseconds)), MedianAllocatedBytes = Median(samples.Select(x => (double)x.AllocatedBytes)),
            P95AllocatedBytes = P95(samples.Select(x => (double)x.AllocatedBytes)),
            Gen0Collections = samples.Sum(x => x.Gen0Collections), Gen1Collections = samples.Sum(x => x.Gen1Collections),
            Gen2Collections = samples.Sum(x => x.Gen2Collections),
            TotalGCPauseMilliseconds = samples.Sum(x => x.GCPauseMilliseconds),
            P95GCPauseMilliseconds = P95(samples.Select(x => x.GCPauseMilliseconds)) };
    }
    private static object Ratios(IEnumerable<PairMeasurement> values)
    {
        var samples = values.ToArray();
        return new { MedianTimeRatio = Median(samples.Select(x => x.CandidateToBaselineTimeRatio)),
            P95TimeRatio = P95(samples.Select(x => x.CandidateToBaselineTimeRatio)),
            MedianAllocationRatio = Median(samples.Select(x => x.CandidateToBaselineAllocationRatio)),
            P95AllocationRatio = P95(samples.Select(x => x.CandidateToBaselineAllocationRatio)),
            RatioOfTimeMedians = Median(samples.Select(x => x.Candidate.Milliseconds)) / Median(samples.Select(x => x.Baseline.Milliseconds)),
            RatioOfAllocationMedians = Median(samples.Select(x => (double)x.Candidate.AllocatedBytes)) / Median(samples.Select(x => (double)x.Baseline.AllocatedBytes)) };
    }
    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(x => x).ToArray();
        if (sorted.Length == 0) throw new ArgumentException("Empty measurement set.");
        return sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2];
    }
    internal static double P95(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(x => x).ToArray();
        if (sorted.Length == 0) throw new ArgumentException("Empty measurement set.");
        return sorted[(int)Math.Ceiling(sorted.Length * .95) - 1];
    }

    internal static void RunHelperTests(Action<string, Action> test)
    {
        static void Check(bool value) { if (!value) throw new Exception("Paired inspection helper assertion failed."); }
        test("paired selection covers endpoints and shuffles reproducibly without duplicates", () =>
        {
            foreach (int count in new[] { 1, 2, 11, 12, 18, 124 })
            {
                int[] pages = SelectPages(count);
                Check(pages.Length == Math.Min(count, MaximumSelectedPages) && pages.Distinct().Count() == pages.Length);
                Check(pages[0] == 0 && pages[pages.Length - 1] == count - 1);
                for (int round = 0; round < Rounds; round++)
                {
                    Check(Order(pages, round).OrderBy(x => x).SequenceEqual(pages));
                    Check(Order(pages, round).SequenceEqual(Order(pages, round)));
                }
            }
            Check(SelectPages(1).SequenceEqual(new[] { 0 }));
        });
        test("paired metadata rejects changed page boundaries and uses nearest-rank statistics", () =>
        {
            FullReplayInfo Info() => new() { FrameCount = 2, Duration = 1, Pages = new[] { new FullReplayPage { Start = 0, End = 1, FrameCount = 2 } } };
            var a = Info(); var b = Info(); CheckComparable(a, b);
            b.Pages[0].Overlap = true;
            bool rejected = false; try { CheckComparable(a, b); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && Median(new[] { 4d, 1, 3, 2 }) == 2.5 && P95(Enumerable.Range(1, 20).Select(x => (double)x)) == 19);
        });
    }
}
