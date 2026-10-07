using Newtonsoft.Json;
using PeakReplayLab;

// Read-only, bounded catalog inspection for local debugging. No frame payload or
// private actor identity is printed and no recording is rewritten.
if (args.Length > 1 && args[0] == "--inspect-covers")
{
    foreach (string path in args.Skip(1))
    {
        bool full = path.EndsWith(".peakrun", StringComparison.OrdinalIgnoreCase);
        var info = full ? FullReplayArchive.ReadInfo(path) : null;
        var header = full ? info!.Header : ReplayFiles.ReadHeader(path);
        var summary = full ? info!.RegionSummary : header.RegionSummary;
        Console.WriteLine(JsonConvert.SerializeObject(new
        {
            file = Path.GetFileName(path), known = summary != null, schema = header.Schema,
            duration = full ? info!.Duration : header.Duration,
            regions = ReplayRegionCovers.Keys(summary, true), cover = ReplayRegionCovers.Keys(summary, full),
        }));
    }
    return;
}

int passed = 0;
void Test(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { Console.Error.WriteLine("FAIL " + name + ": " + e); Environment.Exit(1); }
}
void Check(bool value) { if (!value) throw new Exception("Assertion failed."); }
void Reject(Action action)
{
    try { action(); } catch (Exception e) when (e is InvalidDataException || e is JsonException || e is ArgumentOutOfRangeException) { return; }
    throw new Exception("Invalid input was accepted.");
}
ActorFrame Actor() => new() { Id = "1234", Name = "synthetic" };
ReplayFrame Frame(double t) => new() { T = t, Actors = new[] { Actor() } };
// Current-format fixtures use the same field codec as the recorder.
ReplayHeader Header() => new() { Schema = ReplayRules.CurrentSchema, Scene = "Level_Synthetic", GameVersion = "test", BuildId = 123, Route = "Shore,Roots,Alpine,Caldera,Kiln" };
ReplayClip Clip() { var c = new ReplayClip { Header = Header() }; c.Frames.AddRange(new[] { Frame(.05), Frame(.1), Frame(.15), Frame(.2) }); return c; }
string Json(object value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
string ValidText(bool footer = true)
{
    var writer = new ReplayDeltaCodec.Writer();
    return string.Join('\n', new[] { Json(Header()), Json(writer.Encode(Frame(0))), Json(writer.Encode(Frame(.05))), footer ? "{\"Type\":\"end\"}" : "" });
}
ReplayClip Read(string text)
{
    string path = Path.Combine(Path.GetTempPath(), "peak-replay-contract-" + Guid.NewGuid().ToString("N") + ".ndjson");
    try { File.WriteAllText(path, text); return ReplayFiles.Read(path); }
    finally { File.Delete(path); }
}

if (args.Contains("--hud-only"))
{
    ReplayHudStateTests.Run(Test);
    ReplayHudLayoutTests.Run(Test);
    Console.WriteLine($"TOTAL: {passed} HUD checks passed. No Unity runtime is simulated by these tests.");
    return;
}

if (args.Contains("--cursor-only"))
{
    ReplayCursorPolicyTests.Run(Test);
    Console.WriteLine($"TOTAL: {passed} cursor checks passed. No Unity runtime is simulated by these tests.");
    return;
}

if (args.Contains("--drawer-only"))
{
    ReplayDrawerMotionTests.Run(Test);
    Console.WriteLine($"TOTAL: {passed} drawer checks passed. No Unity runtime is simulated by these tests.");
    return;
}

if (args.Contains("--concurrent-only"))
{
    ConcurrentRecordingTests.Run(Test);
    Console.WriteLine($"TOTAL: {passed} concurrent recording checks passed. Files contain synthetic data only; no Unity runtime is simulated.");
    return;
}

Test("valid header and actor", () => { ReplayRules.Validate(Header()); ReplayRules.Validate(Frame(0), -1); });
Test("same map compatibility", () => Check(ReplayRules.Compatible(Header(), Header())));
Test("formats before schema 10 are incompatible even on the same map", () =>
{
    for (int schema = 2; schema < 10; schema++)
    {
        var old = Header(); old.Schema = schema;
        Check(!ReplayRules.Compatible(old, Header()) && !ReplayRules.Compatible(Header(), old));
    }
});
Test("different scene rejected", () => { var h = Header(); h.Scene = "other"; Check(!ReplayRules.Compatible(Header(), h)); });
Test("different version rejected", () => { var h = Header(); h.GameVersion = "other"; Check(!ReplayRules.Compatible(Header(), h)); });
Test("different build rejected", () => { var h = Header(); h.BuildId++; Check(!ReplayRules.Compatible(Header(), h)); });
Test("different assembly identity rejected even if build unknown", () => { var h = Header(); h.GameAssembly = new string('a', 32); Check(!ReplayRules.Compatible(Header(), h)); });
Test("different route rejected", () => { var h = Header(); h.Route = "Mesa,Caldera,Kiln"; Check(!ReplayRules.Compatible(Header(), h)); });
Test("unsupported schema rejected", () => { var h = Header(); h.Schema = ReplayRules.CurrentSchema + 1; Reject(() => ReplayRules.Validate(h)); });
Test("invalid sample rate rejected", () => { var h = Header(); h.SampleHz = 120; Reject(() => ReplayRules.Validate(h)); });
Test("negative seek clamps", () => { var (a, _, mix) = Clip().At(-1); Check(a.T == .05 && mix == 0); });
Test("seek before first sample clamps without negative lerp", () => Check(Clip().At(.01).Mix == 0));
Test("seek interpolates", () => { var (a, b, mix) = Clip().At(.125); Check(a.T == .1 && b.T == .15 && Math.Abs(mix - .5) < .0001); });
Test("exact sample", () => { var (a, _, mix) = Clip().At(.15); Check(a.T == .15 && mix == 0); });
Test("end seek clamps", () => { var (a, b, mix) = Clip().At(999); Check(a.T == .2 && b.T == .2 && mix == 0); });
Test("backwards seek independent of history", () => { var c = Clip(); c.At(.2); Check(c.At(.075).Left.T == .05); });
Test("long recording gap does not interpolate", () => { var c = new ReplayClip(); c.Frames.Add(Frame(0)); c.Frames.Add(Frame(1)); Check(c.At(.5).Mix == 0); });
Test("NaN seek rejected", () => Reject(() => Clip().At(double.NaN)));
Test("infinite time rejected", () => Reject(() => ReplayRules.Validate(Frame(double.PositiveInfinity), -1)));
Test("duplicate timestamps rejected", () => Reject(() => ReplayRules.Validate(Frame(1), 1)));
Test("decreasing time rejected", () => Reject(() => ReplayRules.Validate(Frame(1), 2)));
Test("too long clip rejected", () => Reject(() => ReplayRules.Validate(Frame(901), 1)));
Test("duplicate participant IDs rejected", () => { var f = Frame(0); f.Actors = new[] { Actor(), Actor() }; Reject(() => ReplayRules.Validate(f, -1)); });
Test("too many actors rejected", () => { var f = Frame(0); f.Actors = Enumerable.Range(0, 17).Select(i => new ActorFrame { Id = i.ToString() }).ToArray(); Reject(() => ReplayRules.Validate(f, -1)); });
Test("empty frame allowed for despawn", () => ReplayRules.Validate(new ReplayFrame { T = 0 }, -1));
Test("invalid position shape rejected", () => { var f = Frame(0); f.Actors[0].Position = new float[4]; Reject(() => ReplayRules.Validate(f, -1)); });
Test("non-finite position rejected", () => { var f = Frame(0); f.Actors[0].Position[0] = float.NaN; Reject(() => ReplayRules.Validate(f, -1)); });
Test("zero quaternion rejected", () => { var f = Frame(0); f.Actors[0].Rotation = new float[4]; Reject(() => ReplayRules.Validate(f, -1)); });
Test("invalid body-root orientation rejected", () => { var f = Frame(0); f.Actors[0].HipRotation = new float[4]; Reject(() => ReplayRules.Validate(f, -1)); });
Test("limp state needs no per-bone data", () => { var f = Frame(0); f.Actors[0].Limp = true; ReplayRules.Validate(f, -1); Check(!Json(f).Contains("Bones")); });
Test("non-finite stamina rejected", () => { var f = Frame(0); f.Actors[0].Stamina = float.PositiveInfinity; Reject(() => ReplayRules.Validate(f, -1)); });
Test("round-trip with footer", () => { var c = Read(ValidText()); Check(c.Complete && c.Frames.Count == 2 && c.Frames[0].Actors[0].Id == "1234"); });
Test("clean interrupted prefix is incomplete", () => Check(!Read(ValidText(false)).Complete));
Test("malformed trailing frame rejected", () => Reject(() => Read(ValidText(false) + "{\"Type\":")));
Test("content after footer rejected", () => Reject(() => Read(ValidText() + "\n" + Json(Frame(.1)))));
Test("duplicate JSON properties rejected", () => Reject(() => Read(ValidText(false) + "{\"Type\":\"end\",\"Type\":\"end\"}")));
Test("duplicate header properties rejected", () => Reject(() => Read("{\"Type\":\"header\",\"Scene\":\"a\",\"Scene\":\"b\"}\n")));
Test("multiple JSON objects in a line rejected", () => Reject(() => Read(ValidText(false) + "{} {}")));
Test("deep JSON rejected before deserialization", () => Reject(() => Read(ValidText(false) + "{\"data\":" + new string('[', 30) + "0" + new string(']', 30) + "}")));
Test("oversized single line rejected", () => Reject(() => Read(new string(' ', ReplayRules.MaxLineCharacters + 1))));
Test("one frame cannot play", () => Reject(() => Read(Json(Header()) + "\n" + Json(Frame(0)))));
Test("polymorphic metadata never loads types", () => {
    string text = ValidText().Replace("\"Type\":\"header\"", "\"$type\":\"Not.Real.Type, Nope\",\"Type\":\"header\"");
    Check(Read(text).Header.Scene == "Level_Synthetic");
});
Test("negative cosmetic ID rejected", () => { var f = Frame(0); f.Actors[0].Appearance.Hat = -1; Reject(() => ReplayRules.Validate(f, -1)); });
Test("invalid eye state rejected", () => { var f = Frame(0); f.Actors[0].Appearance.EyeState = 3; Reject(() => ReplayRules.Validate(f, -1)); });
Test("duplicate map identities rejected", () => { var h = Header(); h.MapObjects = new[] { "Root", "Root" }; Reject(() => ReplayRules.Validate(h)); });
Test("map activation track must match map header", () => { var f = Frame(0); f.World.ActiveMapObjects = new bool[1]; Reject(() => Read(Json(Header()) + "\n" + Json(f) + "\n" + Json(Frame(.05)))); });
Test("long saved timeline rejected even without duration metadata", () => Reject(() => Read(Json(Header()) + "\n" + Json(Frame(0)) + "\n" + Json(Frame(122)))));
Test("cancelled read does not open a file", () => {
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    try { ReplayFiles.Read("nonexistent-replay", cancel.Token); }
    catch (OperationCanceledException) { return; }
    throw new Exception("Read ignored cancellation.");
});

Test("rolling buffer retains at most 120 seconds", () => { var b = new RollingBuffer(); for (int i = 0; i < 6000; i++) b.Add(Frame(i / 20d)); Check(b.Duration <= 120 && b.Count <= 2401 && b.Count >= 2400); });
Test("buffer clock may exceed 15 minutes", () => { var b = new RollingBuffer(); b.Add(Frame(99999)); b.Add(Frame(99999.05)); Check(b.Count == 2); });
Test("snapshot time starts at zero without mutating capture", () => { var b = new RollingBuffer(); b.Add(Frame(500)); b.Add(Frame(500.05)); var c = b.Snapshot(Header(), DateTime.UtcNow); Check(c.Frames[0].T == 0); b.Add(Frame(500.1)); Check(b.Count == 3 && c.Frames.Count == 2); });
Test("snapshot has historical appearance without Unity references", () => { var b = new RollingBuffer(); var f = Frame(10); f.Actors[0].Appearance.Hat = 4; b.Add(f); b.Add(Frame(11)); var c = b.Snapshot(Header(), DateTime.UtcNow); Check(c.Frames[0].Actors[0].Appearance.Hat == 4); });
Test("snapshot preserves exact assembly identity", () => { var b = new RollingBuffer(); b.Add(Frame(0)); b.Add(Frame(.05)); var h = Header(); h.GameAssembly = new string('b', 32); Check(b.Snapshot(h, DateTime.UtcNow).Header.GameAssembly == h.GameAssembly); });
Test("maximum sample rate stays bounded", () => { var b = new RollingBuffer(); for (int i = 0; i < 10000; i++) b.Add(Frame(i / 60d)); Check(b.Count <= 7201 && b.Count >= 7200 && b.Duration <= 120); });
Test("memory budget evicts instead of growing without bound", () => { var b = new RollingBuffer(4096); for (int i = 0; i < 100; i++) b.Add(Frame(i / 20d)); Check(b.EstimatedBytes <= 4096 && b.MemoryLimited && b.Count < 100); });
Test("overwriting ring does not mutate an in-flight save", () => { var b = new RollingBuffer(); b.Add(Frame(0)); b.Add(Frame(.05)); var c = b.Snapshot(Header(), DateTime.UtcNow); for (int i = 1; i <= 300; i++) b.Add(Frame(i)); Check(c.Frames.Count == 2 && c.Frames[0].T == 0 && b.Duration <= 120); });
Test("archive survives process-independent round trip", () => {
    string dir = Path.Combine(Path.GetTempPath(), "peak-memoir-test-" + Guid.NewGuid().ToString("N"));
    string? path = null;
    try {
        var b = new RollingBuffer(); b.Add(Frame(400)); b.Add(Frame(400.05));
        Check(!Directory.Exists(dir)); path = ReplayArchive.Save(dir, b.Snapshot(Header(), DateTime.UtcNow));
        var c = ReplayFiles.Read(path); Check(c.Complete && c.Frames[0].T == 0 && c.Header.FrameCount == 2);
        Check(ReplayFiles.ReadHeader(path).Scene == "Level_Synthetic");
    } finally { if (path != null) File.Delete(path); if (Directory.Exists(dir)) Directory.Delete(dir); }
});
Test("failed archive leaves neither a final file nor temporary debris", () => {
    string dir = Path.Combine(Path.GetTempPath(), "peak-memoir-failure-" + Guid.NewGuid().ToString("N"));
    try {
        var b = new RollingBuffer(); b.Add(Frame(0)); b.Add(Frame(.05)); var c = b.Snapshot(Header(), DateTime.UtcNow);
        c.Frames[1].Actors[0].Position[0] = float.NaN;
        Reject(() => ReplayArchive.Save(dir, c)); Check(!Directory.EnumerateFiles(dir).Any());
    } finally { if (Directory.Exists(dir)) Directory.Delete(dir); }
});
Test("each explicit save has a distinct file without overwriting", () => {
    string dir = Path.Combine(Path.GetTempPath(), "peak-memoir-unique-" + Guid.NewGuid().ToString("N"));
    var files = new List<string>();
    try {
        var b = new RollingBuffer(); b.Add(Frame(0)); b.Add(Frame(.05)); var c = b.Snapshot(Header(), DateTime.UtcNow);
        files.Add(ReplayArchive.Save(dir, c)); files.Add(ReplayArchive.Save(dir, c));
        Check(files[0] != files[1] && files.All(File.Exists));
        Check(files.All(p => ReplayFiles.Read(p).Complete));
    } finally { foreach (string path in files) File.Delete(path); if (Directory.Exists(dir)) Directory.Delete(dir); }
});
SampleClockTests.Run(Test);
WorldSegmentIndexTests.Run(Test);
ObjectTrackTests.Run(Test);
ItemOptimizationTests.Run(Test, Check);
PerformanceContractTests.Run(Test, Check);
FrameCadenceTests.Run(Test);
NativeCapturePolicyTests.Run(Test);
EnvironmentPerformanceTests.Run(Test);
ActorJointReplayTests.Run(Test);
ActorJointCodecTests.Run(Test);
ActorJointSamplingTests.Run(Test, Check);
ActorJointTimelineTests.Run(Test);
CrateTimelineTests.Run(Test);
ReplayDeltaTests.Run(Test);
PresentationTrackTests.Run(Test);
AudioReplayTests.Run(Test, Check);
RopeReplayTests.Run(Test);
EffectReplayTests.Run(Test);
BackpackAttachmentTests.Run(Test, Check);
PresentationRegressionTests.Run(Test);
SpawnedReplayTests.Run(Test);
WindowEntityTests.Run(Test);
BalloonReplayTests.Run(Test);
CannonReplayTests.Run(Test);
ItemCaptureOptimizationTests.Run(Test, Check);
DebugRunCatalogTests.Run(Test);
ContinuousReplayTests.Run(Test);
RecordingModePolicyTests.Run(Test);
MemoriesLibraryModelTests.Run(Test);
ReplayRegionSummaryTests.Run(Test);
NativeMemoriesNoteLayoutTests.Run(Test);
NativeMemoriesRecordingControlsTests.Run(Test);
ReplayTrashTests.Run(Test);
FullReplayArchiveTests.Run(Test);
FullReplayTimestampTests.Run(Test);
ConcurrentRecordingTests.Run(Test);
ReplayTimelineTests.Run(Test);
ReplayHudStateTests.Run(Test);
ReplayHudLayoutTests.Run(Test);
ReplayCursorPolicyTests.Run(Test);
ReplayDrawerMotionTests.Run(Test);
EnvironmentIntegrationTests.Run(Test);
NativePresentationIntegrationTests.Run(Test);
EnvironmentReplayMathTests.Run(Test);
ReplayVisualClockTests.Run(Test);
CreatureReplayTests.Run(Test);
ReplayQueueBudgetTests.Run(Test);
CreatureDirectoryTests.Run(Test);
CreatureCapacityTests.Run(Test);
Console.WriteLine($"TOTAL: {passed} checks passed. No Unity runtime is simulated by these tests.");

int inspectArg = Array.IndexOf(args, "--inspect");
int fullInspectArg = Array.IndexOf(args, "--full-inspect");
if (fullInspectArg >= 0)
{
    if (fullInspectArg + 1 >= args.Length) throw new ArgumentException("--full-inspect needs a replay file.");
    FullReplayInspection.Run(args[fullInspectArg + 1]);
}
int continuousInspectArg = Array.IndexOf(args, "--continuous-inspect");
if (continuousInspectArg >= 0)
{
    if (continuousInspectArg + 1 >= args.Length) throw new ArgumentException("--continuous-inspect needs a replay file.");
    ContinuousReplayInspection.Run(args[continuousInspectArg + 1]);
}
if (inspectArg >= 0)
{
    if (inspectArg + 1 >= args.Length) throw new ArgumentException("--inspect needs a replay file.");
    var replay = ReplayFiles.Read(args[inspectArg + 1]);
    Console.WriteLine($"INSPECT scene={replay.Header.Scene} schema={replay.Header.Schema} frames={replay.Frames.Count} duration={replay.Duration:F2}s " +
        $"firstT={replay.Frames[0].T:F6} lastT={replay.Frames[^1].T:F6} complete={replay.Complete} maxActors={replay.Frames.Max(f => f.Actors.Length)} mapSwitches={replay.Header.MapObjects.Length} " +
        $"maxItems={replay.Frames.Max(f => f.Items.Length)} maxCrates={replay.Frames.Max(f => f.Crates.Length)} events={replay.Frames.Sum(f => f.Events.Length)} " +
        $"maxRopes={replay.Frames.Max(f => f.Ropes.Length)} maxEffects={replay.Frames.Max(f => f.Effects.Length)} maxGameSounds={replay.Frames.Max(f => f.Audio.Length)} maxSpawned={replay.Frames.Max(f => f.Spawned.Length)} " +
        $"knownInventorySamples={replay.Frames.Sum(f => f.Actors.Count(a => a.InventoryKnown))} maxInventorySlots={replay.Frames.Max(f => f.Actors.Sum(a => a.Inventory.Length))}");
}

if (args.Contains("--benchmark"))
{
    ObjectTrackTests.Benchmark();
    static ReplayFrame Synthetic(double t, int count) => new()
    {
        T = t, World = new WorldFrame { ActiveMapObjects = new bool[25] },
        Actors = Enumerable.Range(0, count).Select(i => new ActorFrame
        {
            Id = "7656119800000000" + i, Name = "Benchmark player " + i, Position = new[] { (float)t, 50f, i * 2f },
            Appearance = new Appearance { Hat = i, Outfit = i },

        }).ToArray(),
    };
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static string Benchmark(int count, int budgetMiB = 256)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetTotalMemory(true);
        var b = new RollingBuffer(budgetMiB * 1024L * 1024);
        long allocated = GC.GetTotalAllocatedBytes(true);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i <= 7200; i++) b.Add(Synthetic(i / 60d, count));
        watch.Stop();
        long transient = GC.GetTotalAllocatedBytes(true) - allocated;
        long retained = GC.GetTotalMemory(true) - before;
        string result = $"MEMORY actors={count} budgetMiB={budgetMiB} frames={b.Count} seconds={b.Duration:F2} managedMiB={retained / 1048576d:F2} accountedMiB={b.EstimatedBytes / 1048576d:F2} allocatedMiB={transient / 1048576d:F2} buildMs={watch.ElapsedMilliseconds} limited={b.MemoryLimited}";
        GC.KeepAlive(b);
        return result;
    }
    foreach (int count in new[] { 1, 4, 8, 16 }) Console.WriteLine(Benchmark(count));
}
