using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using PeakReplayLab;

internal static class DebugRunCatalogTests
{
    public static void Run(Action<string, Action> test)
    {
        test("debug catalog lists sealed segment using manifest rather than zero streamed header duration", () => InRoot(root =>
        {
            string run = CreateRun(root); var part = Segment(run, 1, 100, 130);
            Manifest(run, Start(), part, End("completed", 2, 1));
            var entry = DebugRunCatalog.Scan(root).Runs.Single(); var recorded = entry.Parts.Single();
            Check(entry.Complete && recorded.Playable && recorded.Sealed && recorded.FrameCount == 2);
            Check(recorded.Header!.Duration == 0 && recorded.Header.FrameCount == 0);
            Check(recorded.SessionStart == 0 && recorded.SessionEnd == 30 && recorded.Duration == 30);
        }));
        test("debug catalog open session never claims complete when the writer may have crashed", () => InRoot(root =>
        {
            string run = CreateRun(root); Manifest(run, Start(), Segment(run, 1, 50, 70));
            var entry = DebugRunCatalog.Scan(root).Runs.Single();
            Check(entry.Status == "open" && !entry.Complete && entry.Parts.Single().Playable);
        }));
        test("debug catalog partial tail is visible but never offered for playback", () => InRoot(root =>
        {
            string run = CreateRun(root); File.WriteAllText(Path.Combine(run, "segment-000002.peakreplay.partial"), "unfinished");
            Manifest(run, Start(), Segment(run, 1, 50, 70));
            var entry = DebugRunCatalog.Scan(root).Runs.Single();
            Check(entry.PartialCount == 1 && entry.Parts.Length == 1 && entry.Parts[0].Playable && !entry.Complete);
        }));
        test("debug catalog retains earlier sealed segments in a faulted run", () => InRoot(root =>
        {
            string run = CreateRun(root); Manifest(run, Start(), Segment(run, 1, 50, 70), End("faulted", 2, 1, "bounded queue full"));
            var entry = DebugRunCatalog.Scan(root).Runs.Single();
            Check(entry.Status == "faulted" && entry.Fault == "bounded queue full" && !entry.Complete && entry.Parts[0].Playable);
        }));
        test("debug catalog accepts overlapping real boundary frames and gives session ranges", () => InRoot(root =>
        {
            string run = CreateRun(root); Manifest(run, Start(), Segment(run, 1, 500, 530), Segment(run, 2, 530, 555), End("completed", 3, 2));
            var entry = DebugRunCatalog.Scan(root).Runs.Single();
            Check(entry.Complete && entry.Duration == 55 && entry.Parts[1].SessionStart == 30 && entry.Parts[1].SessionEnd == 55);
        }));
        test("debug catalog does not hide gaps or unsealed single-frame segments", () => InRoot(root =>
        {
            string run = CreateRun(root); Manifest(run, Start(), Segment(run, 1, 500, 520),
                new { Type = "capture-gap" }, new { Type = "segment-incomplete" }, Segment(run, 3, 570, 590), End("completed", 4, 2));
            var entry = DebugRunCatalog.Scan(root).Runs.Single();
            Check(entry.CaptureGapCount == 1 && entry.UnsealedSegments == 1 && !entry.Complete);
            Check(entry.Parts[1].Playable && entry.Parts[1].SessionStart == 70);
        }));
        test("debug catalog path traversal in a manifest never authorizes a playable file", () => InRoot(root =>
        {
            string run = CreateRun(root); var part = Segment(run, 1, 0, 1); part["File"] = "../outside.peakreplay";
            Manifest(run, Start(), part, End("completed", 2, 1));
            var entry = DebugRunCatalog.Scan(root).Runs.Single(); Check(entry.Status == "invalid" && !entry.Parts.Any(p => p.Playable));
        }));
        test("debug catalog will not trust a partial filename marked segment-complete", () => InRoot(root =>
        {
            string run = CreateRun(root); var part = Segment(run, 1, 0, 1); part["File"] = "segment-000001.peakreplay.partial";
            Manifest(run, Start(), part); Check(DebugRunCatalog.Scan(root).Runs.Single().Status == "invalid");
        }));
        test("debug catalog detects missing and resized final segments", () => InRoot(root =>
        {
            string run = CreateRun(root); var first = Segment(run, 1, 0, 1); var second = Segment(run, 2, 1, 2);
            File.Delete(Path.Combine(run, "segment-000001.peakreplay"));
            File.AppendAllText(Path.Combine(run, "segment-000002.peakreplay"), "unexpected");
            Manifest(run, Start(), first, second, End("completed", 3, 2));
            var entry = DebugRunCatalog.Scan(root).Runs.Single(); Check(!entry.Complete && entry.Parts.All(p => !p.Playable && p.Error.Length > 0));
        }));
        test("debug catalog rejects duplicate part identities and backwards native time", () => InRoot(root =>
        {
            string run = CreateRun(root); var first = Segment(run, 1, 50, 70);
            Manifest(run, Start(), first, first); Check(DebugRunCatalog.Scan(root).Runs.Single().Status == "invalid");
            Manifest(run, Start(), first, Segment(run, 2, 60, 80)); Check(DebugRunCatalog.Scan(root).Runs.Single().Status == "invalid");
        }));
        test("debug catalog ignores unowned files and never recurses into nested locations", () => InRoot(root =>
        {
            string run = CreateRun(root); Manifest(run, Start(), Segment(run, 1, 50, 70));
            Directory.CreateDirectory(Path.Combine(root, "unrelated")); File.WriteAllText(Path.Combine(root, "other.peakreplay"), "leave alone");
            Directory.CreateDirectory(Path.Combine(run, "nested", "run-hidden"));
            Check(DebugRunCatalog.Scan(root).Runs.Length == 1);
        }));
        test("debug catalog tolerates append in progress without claiming session completion", () => InRoot(root =>
        {
            string run = CreateRun(root); Manifest(run, Start(), Segment(run, 1, 50, 70));
            File.AppendAllText(Path.Combine(run, "manifest.ndjson"), "{\"Type\":\"run-end\"");
            var entry = DebugRunCatalog.Scan(root).Runs.Single();
            Check(entry.ManifestIncomplete && entry.Status == "open" && !entry.Complete && entry.Parts.Single().Playable);
        }));
        test("debug catalog is bounded before opening every segment header", () => InRoot(root =>
        {
            string run = CreateRun(root); Manifest(run, Start(), Segment(run, 1, 1, 2), Segment(run, 2, 2, 3), End("completed", 3, 2));
            var list = DebugRunCatalog.Scan(root, maximumHeaders: 1); var entry = list.Runs.Single();
            Check(list.Truncated && entry.Parts[0].Playable && !entry.Parts[1].Playable && entry.Parts[1].MetadataDeferred);
        }));
        test("debug catalog bounds visible runs and parts explicitly", () => InRoot(root =>
        {
            string first = CreateRun(root, "run-a"), second = CreateRun(root, "run-b");
            Manifest(first, Start()); Manifest(second, Start(), Segment(second, 1, 1, 2), Segment(second, 2, 2, 3));
            var list = DebugRunCatalog.Scan(root, maxRuns: 1, maxPartsPerRun: 1);
            Check(list.Truncated && list.Runs.Length == 1 && list.Runs[0].Id == "run-b" && list.Runs[0].Parts.Length == 1);
        }));
        test("debug catalog excessive manifests and malformed records cannot be mistaken as complete", () => InRoot(root =>
        {
            string run = CreateRun(root); File.WriteAllText(Path.Combine(run, "manifest.ndjson"), new string('x', DebugRunCatalog.MaximumManifestBytes + 1));
            Check(DebugRunCatalog.Scan(root).Runs.Single().Status == "invalid");
            Manifest(run, Start(), new { Type = "unrecognized-event" }); Check(DebugRunCatalog.Scan(root).Runs.Single().Status == "invalid");
        }));
        test("debug catalog completed zero-frame run has no fictitious playable segment", () => InRoot(root =>
        {
            string run = CreateRun(root); Manifest(run, Start(), End("completed", 0, 0));
            var entry = DebugRunCatalog.Scan(root).Runs.Single(); Check(entry.Complete && entry.Parts.Length == 0 && entry.Duration == 0);
        }));
        test("debug catalog has no effects when an explicit root does not exist", () => InRoot(root =>
        {
            string absent = Path.Combine(root, "absent"); var list = DebugRunCatalog.Scan(absent);
            Check(list.Runs.Length == 0 && !Directory.Exists(absent));
        }));
        test("debug catalog bounded gap details retain authoritative completion totals", () => InRoot(root =>
        {
            string run = CreateRun(root); Manifest(run, Start(), Segment(run, 1, 50, 70),
                new { Type = "capture-gap", FromNativeTime = 60, ToNativeTime = 60.5 },
                new { Type = "capture-gap-summary", DetailsTruncated = true, DetailedLimit = 256 },
                new { Type = "run-end", Status = "completed", AcceptedFrames = 2, WrittenFrames = 2, CompletedSegments = 1,
                    GapOver100ms = 999, GapOver500ms = 20, LargestGapSeconds = 1.25 });
            var entry = DebugRunCatalog.Scan(root).Runs.Single();
            Check(entry.Complete && entry.GapDetailsTruncated && entry.CaptureGapCount == 999 && entry.GapOver500ms == 20 && entry.LargestGapSeconds == 1.25);
        }));
        test("debug catalog keeps initial session origin before first playable segment", () => InRoot(root =>
        {
            string run = CreateRun(root); Manifest(run,
                new { Type = "run-start", StartedUtc = "2026-09-26T13:00:00Z", Scene = "Level_6", NativeStart = 20 },
                new { Type = "segment-incomplete", NativeStart = 20, NativeEnd = 20 }, Segment(run, 2, 60, 90), End("faulted", 3, 1, "insufficient-frames"));
            var entry = DebugRunCatalog.Scan(root).Runs.Single();
            Check(entry.Parts[0].SessionStart == 40 && entry.Parts[0].SessionEnd == 70 && entry.Duration == 70 && !entry.Complete);
        }));
        test("debug catalog understands zero-frame writer fault with null native origin", () => InRoot(root =>
        {
            string run = CreateRun(root); Manifest(run,
                new { Type = "run-start", StartedUtc = "2026-09-26T13:00:00Z", Scene = "Level_6", NativeStart = (double?)null },
                End("faulted", 0, 0, "insufficient-frames"));
            var entry = DebugRunCatalog.Scan(root).Runs.Single();
            Check(entry.Status == "faulted" && !entry.Complete && entry.Parts.Length == 0 && entry.Error == "");
        }));
    }
    private static object Start() => new { Type = "run-start", Schema = ReplayRules.CurrentSchema, StartedUtc = "2026-09-26T13:00:00Z", Scene = "Level_6", SampleHz = 60 };
    private static object End(string status, long frames, int parts, string fault = "") => new
    { Type = "run-end", Status = status, Fault = fault, AcceptedFrames = frames, WrittenFrames = frames, CompletedSegments = parts };
    private static string CreateRun(string root, string name = "run-20260926T130000000Z-test")
    { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    private static Dictionary<string, object> Segment(string run, int index, double start, double end)
    {
        string file = "segment-" + index.ToString("D6") + ".peakreplay", path = Path.Combine(run, file);
        using (var stream = File.Create(path))
        using (var gzip = new GZipStream(stream, CompressionMode.Compress))
        using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
        {
            // Catalog intentionally reads metadata only; clicking playback uses
            // ReplayFiles.Read to validate all frame records and the footer.
            writer.WriteLine(JsonConvert.SerializeObject(new ReplayHeader { Schema = ReplayRules.CurrentSchema, Scene = "Level_6", Duration = 0, FrameCount = 0 }, ReplayFiles.Json));
            writer.WriteLine("{\"Type\":\"end\"}");
        }
        return new Dictionary<string, object> { ["Type"] = "segment-complete", ["Index"] = index, ["File"] = file,
            ["NativeStart"] = start, ["NativeEnd"] = end, ["FrameCount"] = 2, ["CompressedBytes"] = new FileInfo(path).Length };
    }
    private static void Manifest(string run, params object[] events) => File.WriteAllLines(Path.Combine(run, "manifest.ndjson"), events.Select(e => JsonConvert.SerializeObject(e)));
    private static void Check(bool condition) { if (!condition) throw new Exception("Debug catalog assertion failed."); }
    private static void InRoot(Action<string> action)
    {
        string parent = Path.GetFullPath(Path.GetTempPath());
        string root = Path.Combine(parent, "peak-debug-catalog-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(resolved).Length < 50)
                throw new InvalidOperationException("Unsafe test cleanup path.");
            Directory.Delete(resolved, true);
        }
    }
}
