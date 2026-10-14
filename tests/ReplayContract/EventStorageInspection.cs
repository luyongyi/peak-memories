using System.Diagnostics;
using Newtonsoft.Json;
using PeakReplayLab;

internal static class EventStorageInspection
{
    // Explicit offline inspection: reads the selected original, writes a NEW
    // sampled archive only under the supplied output directory. No Unity/game
    // launch, deployment, original rewrite, or participant data in the report.
    public static void Run(string sourcePath, string outputDirectory, bool continuousSamples = false)
    {
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        string originalPath = Path.GetFullPath(sourcePath);
        string root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);
        var sourceInfo = FullReplayArchive.ReadInfo(originalPath);
        int[] selected = SelectPages(sourceInfo, continuousSamples);
        var sourceCoverage = Coverage(selected.Select(i => sourceInfo.Pages[i]));
        var header = JsonConvert.DeserializeObject<ReplayHeader>(JsonConvert.SerializeObject(sourceInfo.Header, ReplayFiles.Json), ReplayFiles.Json)!;
        header.Schema = ReplayRules.CurrentSchema;
        header.Recorder = "offline-event-storage-inspection";
        var loadSource = FullReplayArchive.CreatePageLoader(originalPath, sourceInfo);
        var positions = new List<(int Page, int Frame)>();
        var writer = new FullReplayWriter(root, header);
        double lastTime = double.NegativeInfinity;
        var writeClock = Stopwatch.StartNew();
        try
        {
            foreach (int pageIndex in selected)
            {
                var page = loadSource(pageIndex, default);
                for (int frameIndex = 0; frameIndex < page.Frames.Count; frameIndex++)
                {
                    var frame = page.Frames[frameIndex];
                    if (frame.T <= lastTime) continue;
                    var deadline = DateTime.UtcNow.AddSeconds(90);
                    while (writer.Stats.QueuedFrames >= 8)
                    {
                        if (writer.Fault != null) throw writer.Fault;
                        if (DateTime.UtcNow >= deadline) throw new TimeoutException("Offline write stalled.");
                        Thread.Sleep(2);
                    }
                    if (!writer.TryEnqueue(frame)) throw new InvalidDataException("Offline enqueue failed: " + writer.FailureCode);
                    positions.Add((pageIndex, frameIndex)); lastTime = frame.T;
                }
            }
        }
        catch (Exception e)
        {
            writer.InterruptAsync("offline-inspection-failure", e).GetAwaiter().GetResult();
            throw;
        }
        var result = writer.CompleteAsync("offline-sampled-verification").GetAwaiter().GetResult();
        writeClock.Stop();
        if (result.Status != "completed" || result.WrittenFrames != positions.Count)
            throw new InvalidDataException("Offline write failed: " + result.FaultCode + ": " + result.Fault);
        var info = FullReplayArchive.ReadInfo(result.FilePath);
        var loadNew = FullReplayArchive.CreatePageLoader(result.FilePath, info);
        var loadEffects = FullReplayArchive.CreateEffectPageLoader(info);
        var seen = new HashSet<long>();
        int expectedPage = -1;
        ReplayClip? expectedClip = null;
        var origin = loadSource(selected[0], default).Frames[0].T;
        var rebaser = new ContinuousReplayRebaser(origin);
        var decodeTimes = new List<double>();
        var allocations = new List<long>();
        long reverseJointSamples = 0, randomJointSamples = 0, randomFrames = 0;

        long VerifyPage(int index, ReplayClip clip, bool recordCoverage)
        {
            // Validation is deliberately outside the load timing. Source frames
            // stay page-local even for the optional four 30-second windows.
            var history = loadEffects(index, default);
            if (history.Frames.Count != clip.Frames.Count) throw new Exception("Effect frame count changed.");
            var comparer = new ContinuousReplayInspection.ImmutableDataComparer();
            var effectComparer = new ContinuousReplayInspection.ImmutableDataComparer();
            long clocks = 0;
            for (int j = 0; j < clip.Frames.Count; j++)
            {
                long sourceIndex = info.Pages[index].FirstFrameIndex + j;
                var address = positions[checked((int)sourceIndex)];
                if (expectedPage != address.Page)
                { expectedClip = loadSource(address.Page, default); expectedPage = address.Page; }
                var exactSource = rebaser.Apply(expectedClip!.Frames[address.Frame]);
                var expected = NormalizeExistingJointEncoding(exactSource);
                if (!comparer.Equal(expected, clip.Frames[j]))
                    throw new Exception($"Offline round trip changed a field at sampled frame {sourceIndex}: {Difference(expected, clip.Frames[j], "Frame")}");
                clocks += VerifyJointClocks(exactSource, clip.Frames[j]);
                if (!effectComparer.Equal(FullReplayArchive.EffectsOnlyFrame(expected), history.Frames[j]))
                    throw new Exception($"Offline effect history changed at sampled frame {sourceIndex}.");
                if (recordCoverage) seen.Add(sourceIndex);
            }
            return clocks;
        }
        foreach (int index in Enumerable.Range(0, info.Pages.Length).Reverse())
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();
            var clip = loadNew(index, default);
            clock.Stop();
            decodeTimes.Add(clock.Elapsed.TotalMilliseconds);
            allocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
            reverseJointSamples += VerifyPage(index, clip, true);
        }
        if (seen.Count != positions.Count) throw new Exception("Offline archive lost frames.");
        var random = new Random(7182);
        var repeated = new List<double>();
        var repeatedAllocations = new List<long>();
        for (int i = 0; i < 24; i++)
        {
            int index = random.Next(info.Pages.Length);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();
            var clip = loadNew(index, default);
            clock.Stop();
            repeated.Add(clock.Elapsed.TotalMilliseconds);
            repeatedAllocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
            randomJointSamples += VerifyPage(index, clip, false); randomFrames += clip.Frames.Count;
        }
        var outputCoverage = Coverage(info.Pages);
        var report = new
        {
            SourceFile = Path.GetFileName(originalPath), OutputFile = result.FilePath,
            SourceSchema = sourceInfo.Header.Schema, Schema = info.Header.Schema, ContainerVersion = info.Version,
            SourceSampleHz = sourceInfo.Header.SampleHz,
            SamplingMode = continuousSamples ? "four-30-second-windows" : "legacy-18-pages",
            RequestedSourceWindows = continuousSamples ? Windows() : Array.Empty<CoverageInterval>(),
            SelectedSourcePages = selected, UniqueFrames = seen.Count, OutputPages = info.Pages.Length,
            SourceCoveredSeconds = sourceCoverage.Sum(span => span.End - span.Start), SourceCoveredIntervals = sourceCoverage,
            OutputCoveredSeconds = outputCoverage.Sum(span => span.End - span.Start), OutputCoveredIntervals = outputCoverage,
            SampledTimelineDuration = info.Duration, SourceFileBytes = sourceInfo.FileLength,
            SourceSelectedPageBytes = selected.Sum(i => sourceInfo.Pages[i].Length + (sourceInfo.Pages[i].EffectHistory?.Length ?? 0)),
            SourceSelectedPageBytesExcludeSharedBlocks = sourceInfo.SharedBlocks is { Length: > 0 },
            SourceFileComposition = Composition(sourceInfo), OutputFileComposition = Composition(info),
            OutputFileBytes = result.BytesWritten, WriteWallSecondsIncludingSourceDecode = writeClock.Elapsed.TotalSeconds,
            BytesPerCoveredMinute = outputCoverage.Sum(span => span.End - span.Start) > 0
                ? result.BytesWritten * 60d / outputCoverage.Sum(span => span.End - span.Start) : (double?)null,
            AllFieldsEqualAfterExistingActorJointEncoding = true, ExpectedJointEncoding = "frozen-0.8.11-binary-v1",
            JointSampleTimesBitExact = true, ReverseJointSamplesChecked = reverseJointSamples, RandomJointSamplesChecked = randomJointSamples,
            EffectsEqual = true, ReversePageReads = true, RepeatedRandomReads = repeated.Count,
            RandomFramesChecked = randomFrames, RandomFieldsAndEffectsEqual = true,
            ColdPageDecodeMedianMs = Median(decodeTimes), ColdPageDecodeP95Ms = Percentile95(decodeTimes), ColdPageDecodeMaxMs = decodeTimes.Max(),
            ColdPageDecodeMedianAllocatedBytes = Median(allocations.Select(n => (double)n).ToList()),
            ColdPageDecodeP95AllocatedBytes = Percentile95(allocations.Select(n => (double)n).ToList()),
            RepeatPageDecodeMedianMs = Median(repeated), RepeatPageDecodeP95Ms = Percentile95(repeated), RepeatPageDecodeMaxMs = repeated.Max(),
            RepeatPageDecodeMedianAllocatedBytes = Median(repeatedAllocations.Select(n => (double)n).ToList()),
            RepeatPageDecodeP95AllocatedBytes = Percentile95(repeatedAllocations.Select(n => (double)n).ToList()),
            Note = "Offline sampled archive comparison; decode timing excludes source loading and validation and includes runtime warmup. Coverage is the union of included page intervals, excluding sparse omitted gaps. Window mode retains whole intersecting pages, so coverage may slightly exceed 120 seconds. Bytes/minute includes one-time shared blocks/index/header overhead and is not a full-run size forecast. No game frame rate is measured."
        };
        string json = JsonConvert.SerializeObject(report, Formatting.Indented);
        File.WriteAllText(Path.Combine(root, "inspection.json"), json);
        Console.WriteLine(json);
    }

    internal sealed class CoverageInterval
    {
        public double Start { get; set; }
        public double End { get; set; }
        public CoverageInterval(double start, double end) { Start = start; End = end; }
    }
    private static CoverageInterval[] Windows() => new[] { new CoverageInterval(0, 30), new CoverageInterval(300, 330),
        new CoverageInterval(900, 930), new CoverageInterval(1800, 1830) };
    internal static int[] SelectPages(FullReplayInfo info, bool continuousSamples)
    {
        int last = info.Pages.Length - 1;
        if (!continuousSamples)
            return new[] { 0, 1, 2, 30, 31, 32, 300, 301, 302, 900, 901, 902, 1800, 1801, 1802, last - 2, last - 1, last }
                .Where(i => i >= 0 && i <= last).Distinct().OrderBy(i => i).ToArray();
        var windows = Windows();
        // Use actual source time, not page indices: capture gaps and safety
        // boundaries mean "page 300" is not necessarily time 300 seconds.
        return info.Pages.Where(page => windows.Any(window => page.Start < window.End && page.End > window.Start ||
                page.Start == page.End && page.Start >= window.Start && page.Start < window.End))
            .Select(page => page.Index).Distinct().OrderBy(i => i).ToArray();
    }
    internal static CoverageInterval[] Coverage(IEnumerable<FullReplayPage> pages)
    {
        var result = new List<CoverageInterval>();
        foreach (var page in pages.OrderBy(page => page.Start).ThenBy(page => page.End))
        {
            if (result.Count == 0 || page.Start > result[result.Count - 1].End)
                result.Add(new CoverageInterval(page.Start, page.End));
            else result[result.Count - 1].End = Math.Max(result[result.Count - 1].End, page.End);
        }
        return result.ToArray();
    }
    private static object Composition(FullReplayInfo info)
    {
        long main = info.Pages.Sum(page => page.Length), effects = info.Pages.Sum(page => page.EffectHistory?.Length ?? 0);
        long shared = info.SharedBlocks?.Sum(block => block.Length) ?? 0;
        long index = info.FileLength - 16 - info.IndexOffset;
        long accounted = main + effects + shared + index + info.HeaderEnd + 16;
        if (accounted != info.FileLength) throw new Exception("Archive byte accounting does not cover its file.");
        return new
        {
            MainPageBytes = main, EffectHistoryBytes = effects, SharedSnapshotBytes = shared,
            IndexBytes = index, ContainerHeaderBytes = info.HeaderEnd, FooterBytes = 16,
            AccountedBytes = accounted, ExactFileAccounting = true,
            SharedBlocksByKind = info.SharedBlocks?.GroupBy(block => block.Kind).OrderBy(group => group.Key)
                .Select(group => new { Kind = group.Key, Blocks = group.Count(), CompressedBytes = group.Sum(block => block.Length),
                    RawBytes = group.Sum(block => (long)block.RawBytes) }).ToArray(),
        };
    }
    internal static long VerifyJointClocks(ReplayFrame expected, ReplayFrame actual)
    {
        if (expected.Actors.Length != actual.Actors.Length) throw new Exception("Actor count changed while checking joint clocks.");
        long count = 0;
        for (int i = 0; i < expected.Actors.Length; i++)
        {
            var a = expected.Actors[i]; var b = actual.Actors[i];
            if (a.Id != b.Id || a.JointPose.Length != b.JointPose.Length) throw new Exception("Actor joint topology changed while checking clocks.");
            for (int j = 0; j < a.JointPose.Length; j++)
            {
                if (a.JointPose[j].Path != b.JointPose[j].Path || BitConverter.DoubleToInt64Bits(a.JointPose[j].SampleTime) != BitConverter.DoubleToInt64Bits(b.JointPose[j].SampleTime))
                    throw new Exception($"Actor joint sample clock changed at actor slot {i}, node slot {j}.");
                count++;
            }
        }
        return count;
    }

    private static double Median(List<double> values)
    {
        var ordered = values.OrderBy(n => n).ToArray();
        return ordered.Length % 2 == 0 ? (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2 : ordered[ordered.Length / 2];
    }
    private static double Percentile95(List<double> values)
    {
        var ordered = values.OrderBy(n => n).ToArray();
        return ordered[Math.Max(0, (int)Math.Ceiling(ordered.Length * .95) - 1)];
    }

    internal static void RunHelperTests(Action<string, Action> test)
    {
        static void Check(bool value) { if (!value) throw new Exception("Offline inspection helper assertion failed."); }
        test("inspection coverage unions overlaps while excluding sparse gaps and singleton duration", () =>
        {
            var spans = Coverage(new[] { new FullReplayPage { Start = 300, End = 301 }, new FullReplayPage { Start = 1, End = 2 },
                new FullReplayPage { Start = 0, End = 1 }, new FullReplayPage { Start = 310, End = 310 } });
            Check(spans.Length == 3 && spans.Sum(span => span.End - span.Start) == 3);
            Check(spans[0].Start == 0 && spans[0].End == 2 && spans[1].Start == 300 && spans[2].Start == spans[2].End);
        });
        test("continuous inspection chooses actual times with exclusive window ends and legacy selection unchanged", () =>
        {
            var info = new FullReplayInfo { Pages = new[] {
                new FullReplayPage { Index = 0, Start = 0, End = 0 }, new FullReplayPage { Index = 1, Start = 29.5, End = 30.5 },
                new FullReplayPage { Index = 2, Start = 30.5, End = 31.5 }, new FullReplayPage { Index = 3, Start = 299.5, End = 300.5 },
                new FullReplayPage { Index = 4, Start = 329.5, End = 330.5 }, new FullReplayPage { Index = 5, Start = 330.5, End = 331.5 },
                new FullReplayPage { Index = 6, Start = 899, End = 900 }, new FullReplayPage { Index = 7, Start = 930, End = 931 },
                new FullReplayPage { Index = 8, Start = 1800, End = 1801 }, } };
            Check(SelectPages(info, true).SequenceEqual(new[] { 0, 1, 3, 4, 8 }));
            Check(SelectPages(info, false).SequenceEqual(new[] { 0, 1, 2, 6, 7, 8 }));
        });
        test("inspection uses frozen binary v1 and explicitly rejects even one-bit joint clock changes", () =>
        {
            static ReplayFrame Frame(double clock) => new() { Actors = new[] { new ActorFrame { Id = "fixture",
                JointPose = new[] { new NodePose { Path = ".", SampleTime = clock } } } } };
            var first = Frame(.125); var normalized = NormalizeExistingJointEncoding(first);
            Check(Convert.FromBase64String(InspectionActorJointCodecV1.Encode(first.Actors[0].JointPose, null))[1] == 1);
            Check(VerifyJointClocks(first, normalized) == 1);
            foreach (var pair in new[] { (first, Frame(BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(.125) + 1))),
                (Frame(0), Frame(BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000UL)))) })
            {
                bool rejected = false; try { VerifyJointClocks(pair.Item1, pair.Item2); } catch (Exception) { rejected = true; }
                Check(rejected);
            }
        });
    }

    private static readonly System.Reflection.MethodInfo Clone = typeof(object).GetMethod("MemberwiseClone",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    // The preexisting v1 actor codec already quantizes child joints. A decoded old
    // quaternion can land on the opposite side of a largest-component tie when
    // encoded again. Compare the frozen binary-v1 reference's output rather than
    // the production encoder's current default (which may be v2); every object node,
    // effect field, timestamp, root transform and non-joint field stays exact.
    private static ReplayFrame NormalizeExistingJointEncoding(ReplayFrame frame)
    {
        var result = (ReplayFrame)Clone.Invoke(frame, null)!;
        result.Actors = new ActorFrame[frame.Actors.Length];
        for (int i = 0; i < result.Actors.Length; i++)
        {
            var actor = (ActorFrame)Clone.Invoke(frame.Actors[i], null)!;
            actor.JointPose = InspectionActorJointCodecV1.Decode(InspectionActorJointCodecV1.Encode(actor.JointPose, null), null);
            result.Actors[i] = actor;
        }
        return result;
    }

    private static string? Difference(object? left, object? right, string path)
    {
        if (ReferenceEquals(left, right)) return null;
        if (left == null || right == null || left.GetType() != right.GetType()) return path + " null/type mismatch";
        var type = left.GetType();
        if (type.IsValueType || left is string)
            return left.Equals(right) ? null : path + (left is string ? " string mismatch" : $" {left} != {right}");
        if (left is Array a && right is Array b)
        {
            if (a.Length != b.Length) return path + " length mismatch";
            for (int i = 0; i < a.Length; i++)
            { var mismatch = Difference(a.GetValue(i), b.GetValue(i), path + "[" + i + "]"); if (mismatch != null) return mismatch; }
            return null;
        }
        foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
            var mismatch = Difference(property.GetValue(left), property.GetValue(right), path + "." + property.Name);
            if (mismatch != null) return mismatch;
        }
        foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            var mismatch = Difference(field.GetValue(left), field.GetValue(right), path + "." + field.Name);
            if (mismatch != null) return mismatch;
        }
        return null;
    }
}
