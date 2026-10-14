using System.Collections;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Newtonsoft.Json;
using PeakReplayLab;

// This companion keeps the original same-reader mode intact. Both reader
// assemblies use separate, identically configured noncollectible contexts;
// neither executes in Default. The process/runtime and JSON dependency are
// shared. A prebuilt covariant delegate avoids reflection in timed loads.
internal static class VersionedReplayDecodeInspection
{
    private const int Rounds = 3;
    public static void Run(string baselineAssemblyPath, string baselinePath, string candidatePath, string outputDirectory)
    {
        using var process = Process.GetCurrentProcess(); ProcessPriorityClass? priority = null; bool belowNormal = false;
        try { priority = process.PriorityClass; process.PriorityClass = ProcessPriorityClass.BelowNormal; belowNormal = process.PriorityClass == ProcessPriorityClass.BelowNormal; } catch { }
        try { RunCore(baselineAssemblyPath, baselinePath, candidatePath, outputDirectory, belowNormal); }
        finally { if (priority.HasValue) try { process.PriorityClass = priority.Value; } catch { } }
    }
    private static void RunCore(string assemblyPath, string baselinePath, string candidatePath, string outputDirectory, bool belowNormal)
    {
        string directory = Path.GetFullPath(outputDirectory), oldPath = Path.GetFullPath(baselinePath), newPath = Path.GetFullPath(candidatePath);
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Version comparison requires distinct archive files.");
        string reportPath = Path.Combine(directory, "paired-versioned-decode.json");
        if (File.Exists(reportPath)) throw new IOException("Versioned report already exists; select a new output directory.");
        Directory.CreateDirectory(directory);
        using var baseline = Reader.Isolated(assemblyPath, oldPath);
        using var candidate = Reader.Current(newPath);
        RequireSymmetricContexts(baseline, candidate);
        PairedReplayDecodeInspection.CheckComparable(baseline.Info, candidate.Info);
        int[] selected = PairedReplayDecodeInspection.SelectPages(baseline.Info.Pages.Length);
        var warmup = new List<Pair>(selected.Length); var rows = new List<Pair>(selected.Length * Rounds);
        for (int round = -1; round < Rounds; round++)
        {
            int[] order = PairedReplayDecodeInspection.Order(selected, round);
            for (int slot = 0; slot < order.Length; slot++)
            {
                int page = order[slot], rank = Array.IndexOf(selected, page);
                bool candidateFirst = ((round < 0 ? 0 : round) + rank) % 2 != 0;
                Measurement oldSample, newSample;
                if (candidateFirst) { newSample = Measure(candidate, page); oldSample = Measure(baseline, page); }
                else { oldSample = Measure(baseline, page); newSample = Measure(candidate, page); }
                var pair = new Pair { Round = round + 1, Slot = slot, Page = page, Start = baseline.Info.Pages[page].Start,
                    CandidateFirst = candidateFirst, Baseline = oldSample, Candidate = newSample,
                    TimeRatio = newSample.Milliseconds / oldSample.Milliseconds,
                    AllocationRatio = (double)newSample.AllocatedBytes / oldSample.AllocatedBytes };
                (round < 0 ? warmup : rows).Add(pair);
            }
        }
        long verifiedFrames = 0, verifiedClocks = 0;
        foreach (int page in selected)
        {
            var verified = VerifyPair(baseline, candidate, page);
            verifiedFrames += verified.Frames; verifiedClocks += verified.Clocks;
        }
        var report = new
        {
            Mode = "archived baseline and current reader in separate symmetric noncollectible assembly contexts",
            BaselineReader = baseline.Identity, CandidateReader = candidate.Identity,
            BaselineFile = oldPath, CandidateFile = newPath, BaselineSchema = baseline.Info.Header.Schema,
            CandidateSchema = candidate.Info.Header.Schema, BaselineContainerVersion = baseline.Info.Version,
            CandidateContainerVersion = candidate.Info.Version, SampleHz = baseline.Info.Header.SampleHz,
            PageCount = baseline.Info.Pages.Length, SelectedPages = selected, Rounds, TimedPairs = rows.Count,
            WarmupLoadsPerFile = selected.Length, ProcessBelowNormal = belowNormal,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, ServerGC = System.Runtime.GCSettings.IsServerGC,
            ReaderContextPolicy = "both independent named AssemblyLoadContext instances, isCollectible=false; neither reader uses Default",
            Timer = "Stopwatch.GetTimestamp immediately around prebuilt Func<int,CancellationToken,object> page Load only; no reflection in timed interval",
            Allocation = "GC.GetAllocatedBytesForCurrentThread delta around Load only",
            Scratch = "each version owns its own ReplayLineBufferPool and FullReplaySharedScratchPool; no decoded cache or disk seek cache",
            Baseline = Summary(rows.Select(r => r.Baseline)), Candidate = Summary(rows.Select(r => r.Candidate)),
            PairedCandidateToBaseline = Ratios(rows),
            FirstTouchPreconditioning = new { Baseline = Summary(warmup.Select(r => r.Baseline)),
                Candidate = Summary(warmup.Select(r => r.Candidate)), PairedCandidateToBaseline = Ratios(warmup), Rows = warmup },
            ByPage = rows.GroupBy(r => r.Page).OrderBy(g => g.Key).Select(g => new { Page = g.Key,
                Baseline = Summary(g.Select(r => r.Baseline)), Candidate = Summary(g.Select(r => r.Candidate)), PairedCandidateToBaseline = Ratios(g) }).ToArray(),
            ByRound = rows.GroupBy(r => r.Round).Select(g => new { Round = g.Key, Baseline = Summary(g.Select(r => r.Baseline)),
                Candidate = Summary(g.Select(r => r.Candidate)), PairedCandidateToBaseline = Ratios(g) }).ToArray(),
            ByFirstReader = rows.GroupBy(r => r.CandidateFirst).Select(g => new { First = g.Key ? "candidate" : "baseline",
                Pairs = g.Count(), PairedCandidateToBaseline = Ratios(g) }).ToArray(),
            EachReaderFramesVerifiedAfterTiming = true, AllSerializedFrameFieldsEqual = true,
            JointSampleTimesBitExact = true, VerifiedFrames = verifiedFrames, VerifiedJointClocks = verifiedClocks,
            Rows = rows,
            Note = "The archived baseline DLL and current DLL each execute in their own independent noncollectible AssemblyLoadContext, using identical context policy and shared runtime/JSON dependency. Neither reader executes in Default; pools are disposed, and contexts live until this diagnostic process exits. Only setup uses reflection, and all cross-version field/clock validation follows every timed load. This is .NET, not Unity/Mono. Each selected page is preconditioned once per reader; first-touch timings remain visible separately. Three timed rounds reuse the same page and first-reader schedule as the same-reader helper. OS caches are uncontrolled, no forced GC or outlier removal. GC pause deltas are process-wide diagnostics. Frames include effects; separate effect-history loading is not timed. Ratios below 1 favor candidate. Source files and archived DLL remain read-only.",
        };
        string json = JsonConvert.SerializeObject(report, Formatting.Indented);
        using (var output = new StreamWriter(new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write))) output.Write(json);
        Console.WriteLine(json);
    }

    private sealed class ReaderContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver;
        internal ReaderContext(string path, string name) : base(name, isCollectible: false) { resolver = new(path); }
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(JsonConvert).Assembly.GetName().Name) return typeof(JsonConvert).Assembly;
            string? path = resolver.ResolveAssemblyToPath(name);
            return path == null ? null : LoadFromAssemblyPath(path);
        }
    }
    private sealed class Reader : IDisposable
    {
        internal readonly FullReplayInfo Info;
        internal readonly Func<int, CancellationToken, object> Load;
        internal readonly Func<object, bool> Complete;
        internal readonly Func<object, IList> Frames;
        internal readonly Func<object, Array> Actors, Joints;
        internal readonly Func<object, string> ActorId, NodePath;
        internal readonly Func<object, double> SampleTime;
        internal readonly object Identity;
        private readonly IDisposable lines, shared;
        private Reader(Assembly assembly, string path, ReaderContext context)
        {
            Type archive = Type(assembly, "FullReplayArchive"), clip = Type(assembly, "ReplayClip");
            Type frame = Type(assembly, "ReplayFrame"), actor = Type(assembly, "ActorFrame"), node = Type(assembly, "NodePose");
            Complete = Getter<bool>(clip, "Complete"); Frames = Getter<IList>(clip, "Frames");
            Actors = Getter<Array>(frame, "Actors"); Joints = Getter<Array>(actor, "JointPose");
            ActorId = Getter<string>(actor, "Id"); NodePath = Getter<string>(node, "Path"); SampleTime = Getter<double>(node, "SampleTime");
            object metadata = archive.GetMethod("ReadInfo", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { path, default(CancellationToken) })!;
            // A detached metadata projection is used only for matching page
            // boundaries; this reader's original typed metadata feeds its loader.
            Info = JsonConvert.DeserializeObject<FullReplayInfo>(JsonConvert.SerializeObject(metadata, ReplayFiles.Json), ReplayFiles.Json)!;
            lines = (IDisposable)Activator.CreateInstance(Type(assembly, "ReplayLineBufferPool"), nonPublic: true)!;
            try
            {
                shared = (IDisposable)Activator.CreateInstance(Type(assembly, "FullReplaySharedScratchPool"), nonPublic: true)!;
                try
                {
                    object loader = archive.GetMethod("CreatePageLoader", BindingFlags.NonPublic | BindingFlags.Static)!
                        .Invoke(null, new object[] { path, metadata, default(CancellationToken), lines, shared })!;
                    // Func's return parameter is covariant even when the old
                    // ReplayClip belongs to an isolated assembly load context.
                    Load = (Func<int, CancellationToken, object>)loader;
                }
                catch { shared.Dispose(); throw; }
            }
            catch { lines.Dispose(); throw; }
            string dll = assembly.Location;
            using var dllFile = File.OpenRead(dll);
            Identity = new { Dll = dll, Sha256 = Convert.ToHexString(SHA256.HashData(dllFile)),
                ModuleVersionId = assembly.ManifestModule.ModuleVersionId, FullName = assembly.FullName,
                Schema = (int)Type(assembly, "ReplayRules").GetField("CurrentSchema")!.GetRawConstantValue()!,
                Isolated = AssemblyLoadContext.GetLoadContext(assembly) != AssemblyLoadContext.Default,
                LoadContext = AssemblyLoadContext.GetLoadContext(assembly)?.Name,
                IsCollectible = context.IsCollectible,
                LoaderDeclaringAssembly = Load.Method.DeclaringType?.Assembly.Location };
        }
        internal static Reader Current(string path) => Isolated(typeof(ReplayFrame).Assembly.Location, path, "candidate-replay-reader");
        internal static Reader Isolated(string assemblyPath, string path, string contextName = "baseline-replay-reader")
        {
            string fullPath = Path.GetFullPath(assemblyPath);
            var context = new ReaderContext(fullPath, contextName);
            return new Reader(context.LoadFromAssemblyPath(fullPath), path, context);
        }
        // Exactly two production comparison contexts are intentionally retained
        // until process exit; unloading/collectibility must not differ by side.
        public void Dispose() { try { lines.Dispose(); } finally { shared.Dispose(); } }
    }
    private static Type Type(Assembly assembly, string name) => assembly.GetType("PeakReplayLab." + name, throwOnError: true)!;
    private static void RequireSymmetricContexts(Reader baseline, Reader candidate)
    {
        Assembly oldAssembly = baseline.Load.Method.DeclaringType!.Assembly, newAssembly = candidate.Load.Method.DeclaringType!.Assembly;
        AssemblyLoadContext oldContext = AssemblyLoadContext.GetLoadContext(oldAssembly)!, newContext = AssemblyLoadContext.GetLoadContext(newAssembly)!;
        if (oldAssembly == typeof(ReplayFrame).Assembly || newAssembly == typeof(ReplayFrame).Assembly || oldAssembly == newAssembly ||
            oldContext == newContext || oldContext == AssemblyLoadContext.Default || newContext == AssemblyLoadContext.Default ||
            oldContext.IsCollectible || newContext.IsCollectible)
            throw new InvalidOperationException("Both actual page loader delegates must belong to separate nondefault, noncollectible contexts.");
    }
    private static Func<object, T> Getter<T>(Type owner, string name)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var member = Expression.PropertyOrField(Expression.Convert(instance, owner), name);
        return Expression.Lambda<Func<object, T>>(Expression.Convert(member, typeof(T)), instance).Compile();
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
    private sealed class Pair
    {
        public int Round { get; set; }
        public int Slot { get; set; }
        public int Page { get; set; }
        public double Start { get; set; }
        public bool CandidateFirst { get; set; }
        public Measurement Baseline { get; set; } = null!;
        public Measurement Candidate { get; set; } = null!;
        public double TimeRatio { get; set; }
        public double AllocationRatio { get; set; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Measurement Measure(Reader source, int page)
    {
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        TimeSpan pause = GC.GetTotalPauseDuration(); long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        object clip = source.Load(page, default);
        long end = Stopwatch.GetTimestamp();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        TimeSpan elapsedPause = GC.GetTotalPauseDuration() - pause;
        int after0 = GC.CollectionCount(0), after1 = GC.CollectionCount(1), after2 = GC.CollectionCount(2);
        int frames = source.Frames(clip).Count;
        if (!source.Complete(clip) || frames == 0) throw new InvalidDataException("Reader returned an incomplete page.");
        GC.KeepAlive(clip);
        return new Measurement { Milliseconds = (end - start) * 1000d / Stopwatch.Frequency, AllocatedBytes = bytes,
            Gen0Collections = after0 - gen0, Gen1Collections = after1 - gen1, Gen2Collections = after2 - gen2,
            GCPauseMilliseconds = elapsedPause.TotalMilliseconds, Frames = frames };
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Frames, long Clocks) VerifyPair(Reader baseline, Reader candidate, int page)
    {
        object oldClip = baseline.Load(page, default), newClip = candidate.Load(page, default);
        IList oldFrames = baseline.Frames(oldClip), newFrames = candidate.Frames(newClip);
        if (!baseline.Complete(oldClip) || !candidate.Complete(newClip) || oldFrames.Count != newFrames.Count)
            throw new InvalidDataException("Versioned page frame count/completion differs at page " + page);
        long clocks = 0;
        for (int i = 0; i < oldFrames.Count; i++)
        {
            object oldFrame = oldFrames[i]!, newFrame = newFrames[i]!;
            if (JsonConvert.SerializeObject(oldFrame, ReplayFiles.Json) != JsonConvert.SerializeObject(newFrame, ReplayFiles.Json))
                throw new InvalidDataException($"Versioned serialized frame fields differ at page {page}, frame {i}.");
            Array oldActors = baseline.Actors(oldFrame), newActors = candidate.Actors(newFrame);
            if (oldActors.Length != newActors.Length) throw new InvalidDataException("Versioned actor count differs.");
            for (int a = 0; a < oldActors.Length; a++)
            {
                object oldActor = oldActors.GetValue(a)!, newActor = newActors.GetValue(a)!;
                Array oldNodes = baseline.Joints(oldActor), newNodes = candidate.Joints(newActor);
                if (baseline.ActorId(oldActor) != candidate.ActorId(newActor) || oldNodes.Length != newNodes.Length)
                    throw new InvalidDataException("Versioned actor identity/joint count differs.");
                for (int n = 0; n < oldNodes.Length; n++)
                {
                    object oldNode = oldNodes.GetValue(n)!, newNode = newNodes.GetValue(n)!;
                    if (baseline.NodePath(oldNode) != candidate.NodePath(newNode) ||
                        BitConverter.DoubleToInt64Bits(baseline.SampleTime(oldNode)) != BitConverter.DoubleToInt64Bits(candidate.SampleTime(newNode)))
                        throw new InvalidDataException($"Versioned exact joint clock differs at page {page}, frame {i}, actor slot {a}, node slot {n}.");
                    clocks++;
                }
            }
        }
        return (oldFrames.Count, clocks);
    }
    private static double Median(IEnumerable<double> values) => PairedReplayDecodeInspection.Median(values);
    private static double P95(IEnumerable<double> values) => PairedReplayDecodeInspection.P95(values);
    private static object Summary(IEnumerable<Measurement> values)
    {
        var samples = values.ToArray();
        return new { Loads = samples.Length, MedianMs = Median(samples.Select(x => x.Milliseconds)), P95Ms = P95(samples.Select(x => x.Milliseconds)),
            MaxMs = samples.Max(x => x.Milliseconds), MedianAllocatedBytes = Median(samples.Select(x => (double)x.AllocatedBytes)),
            P95AllocatedBytes = P95(samples.Select(x => (double)x.AllocatedBytes)), Gen0Collections = samples.Sum(x => x.Gen0Collections),
            Gen1Collections = samples.Sum(x => x.Gen1Collections), Gen2Collections = samples.Sum(x => x.Gen2Collections),
            TotalGCPauseMilliseconds = samples.Sum(x => x.GCPauseMilliseconds), P95GCPauseMilliseconds = P95(samples.Select(x => x.GCPauseMilliseconds)) };
    }
    private static object Ratios(IEnumerable<Pair> values)
    {
        var samples = values.ToArray();
        return new { MedianTimeRatio = Median(samples.Select(x => x.TimeRatio)), P95TimeRatio = P95(samples.Select(x => x.TimeRatio)),
            MedianAllocationRatio = Median(samples.Select(x => x.AllocationRatio)), P95AllocationRatio = P95(samples.Select(x => x.AllocationRatio)),
            RatioOfTimeMedians = Median(samples.Select(x => x.Candidate.Milliseconds)) / Median(samples.Select(x => x.Baseline.Milliseconds)),
            RatioOfAllocationMedians = Median(samples.Select(x => (double)x.Candidate.AllocatedBytes)) / Median(samples.Select(x => (double)x.Baseline.AllocatedBytes)) };
    }

    internal static void RunHelperTests(Action<string, Action> test)
    {
        test("symmetric isolated readers execute separate nondefault code and retain exact ignored joint clocks", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "peak-versioned-reader-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); string? path = null;
            try
            {
                var writer = new FullReplayWriter(root, new ReplayHeader { Scene = "Level_ReaderAdapter" });
                for (int i = 0; i < 2; i++)
                {
                    var frame = new ReplayFrame { T = i / 2d, Actors = new[] { new ActorFrame { Id = "reader-fixture",
                        JointPose = new[] { new NodePose("joint", new float[3], new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f }, sampleTime: i / 2d) } } } };
                    if (!writer.TryEnqueue(frame)) throw new Exception("Reader adapter fixture enqueue failed.");
                }
                var saved = writer.CompleteAsync().GetAwaiter().GetResult(); path = saved.FilePath;
                if (saved.Status != "completed") throw new Exception("Reader adapter fixture write failed: " + saved.Fault);
                using var old = Reader.Isolated(typeof(ReplayFrame).Assembly.Location, path);
                using var current = Reader.Current(path);
                RequireSymmetricContexts(old, current);
                PairedReplayDecodeInspection.CheckComparable(old.Info, current.Info);
                var verified = VerifyPair(old, current, 0);
                if (verified.Frames != 2 || verified.Clocks != 2)
                    throw new Exception("Reader adapters did not execute symmetric isolated code or preserve clocks.");
            }
            finally
            {
                if (path != null && File.Exists(path)) File.Delete(path);
                if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
            }
        });
    }
}
