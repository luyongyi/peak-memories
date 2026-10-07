using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PeakReplayLab;

public sealed class DebugRunListing
{
    public DebugRunCatalogEntry[] Runs { get; set; } = Array.Empty<DebugRunCatalogEntry>();
    public string[] Warnings { get; set; } = Array.Empty<string>();
    public bool Truncated { get; set; }
}

public sealed class DebugRunCatalogEntry
{
    public string Path { get; set; } = "";
    public string Id { get; set; } = "";
    public string StartedUtc { get; set; } = "";
    public string Scene { get; set; } = "";
    // "open" deliberately means unfinished: the recorder may still be running,
    // or the process may have stopped without producing run-end.
    public string Status { get; set; } = "open";
    public string Fault { get; set; } = "";
    public string Error { get; set; } = "";
    public bool Complete { get; set; }
    public bool Truncated { get; set; }
    public bool ManifestIncomplete { get; set; }
    public int PartialCount { get; set; }
    public int CaptureGapCount { get; set; }
    public int UnsealedSegments { get; set; }
    public long GapOver100ms { get; set; }
    public long GapOver500ms { get; set; }
    public double LargestGapSeconds { get; set; }
    public bool GapDetailsTruncated { get; set; }
    public long AcceptedFrames { get; set; }
    public long WrittenFrames { get; set; }
    public double Duration { get; set; }
    public DebugRunPartEntry[] Parts { get; set; } = Array.Empty<DebugRunPartEntry>();
}

public sealed class DebugRunPartEntry
{
    public int Index { get; set; }
    public string Path { get; set; } = "";
    public double NativeStart { get; set; }
    public double NativeEnd { get; set; }
    public double SessionStart { get; set; }
    public double SessionEnd { get; set; }
    public double Duration => NativeEnd - NativeStart;
    public int FrameCount { get; set; }
    public long CompressedBytes { get; set; }
    public ReplayHeader? Header { get; set; }
    // The final filename and manifest say the writer sealed this segment. Open
    // still validates every record/footer before playback; this is not ReadAll.
    public bool Sealed { get; set; }
    public bool Playable { get; set; }
    public bool MetadataDeferred { get; set; }
    public string Error { get; set; } = "";
}

// Local, bounded metadata discovery only. Never follows a manifest path outside
// the explicitly supplied debug root, recurses into unrelated folders, or loads
// all segments into one ReplayClip. Each selected file keeps the normal 512 MiB
// replay reader/decompression/decoded-memory limits.
public static class DebugRunCatalog
{
    public const int MaximumManifestBytes = 1024 * 1024;
    private const int MaximumLineCharacters = 64 * 1024;

    public static DebugRunListing Scan(string explicitRoot, int maxRuns = 64, int maxPartsPerRun = 512, int maximumHeaders = 1024)
    {
        if (string.IsNullOrWhiteSpace(explicitRoot)) throw new ArgumentException("An explicit debug recording root is required.", nameof(explicitRoot));
        if (maxRuns < 1 || maxRuns > 64 || maxPartsPerRun < 1 || maxPartsPerRun > 512 || maximumHeaders < 1 || maximumHeaders > 4096)
            throw new ArgumentOutOfRangeException("Debug catalog bounds are outside supported limits.");
        var result = new DebugRunListing(); var warnings = new List<string>();
        string root = System.IO.Path.GetFullPath(explicitRoot);
        if (!Directory.Exists(root)) return result;
        try { CheckDirectoryChain(root); }
        catch (Exception e) when (ExpectedReadError(e))
        { result.Warnings = new[] { e.Message }; return result; }
        var candidates = new List<string>();
        try
        {
            // A bound on discovery itself, before sorting; do not accidentally
            // enumerate an arbitrary-size folder to obtain a small final list.
            int examined = 0;
            foreach (string directory in Directory.EnumerateDirectories(root, "run-*", SearchOption.TopDirectoryOnly))
            {
                if (++examined > 512) { result.Truncated = true; break; }
                string name = System.IO.Path.GetFileName(directory);
                if (!RunName(name)) continue;
                if (Reparse(directory)) { warnings.Add("Skipped linked debug run: " + name); continue; }
                if (!Within(root, directory)) continue;
                candidates.Add(directory);
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        { warnings.Add("Debug run directory discovery was incomplete: " + e.Message); result.Truncated = true; }
        candidates.Sort((a, b) => string.Compare(System.IO.Path.GetFileName(b), System.IO.Path.GetFileName(a), StringComparison.Ordinal));
        if (candidates.Count > maxRuns) result.Truncated = true;
        var runs = new List<DebugRunCatalogEntry>(); int headersLeft = maximumHeaders;
        foreach (string directory in candidates.Take(maxRuns))
        {
            var run = ReadRun(root, directory, maxPartsPerRun, ref headersLeft);
            result.Truncated |= run.Truncated; runs.Add(run);
        }
        result.Runs = runs.ToArray(); result.Warnings = warnings.Take(64).ToArray(); return result;
    }

    private static DebugRunCatalogEntry ReadRun(string root, string directory, int maxParts, ref int headersLeft)
    {
        var run = new DebugRunCatalogEntry { Path = directory, Id = System.IO.Path.GetFileName(directory) };
        var parts = new List<DebugRunPartEntry>();
        try
        {
            CheckDirectoryChain(directory);
            if (!Within(root, directory)) throw new InvalidDataException("Debug run escaped its configured root.");
            string manifest = System.IO.Path.Combine(directory, "manifest.ndjson");
            if (!File.Exists(manifest)) throw new InvalidDataException("Debug run has no manifest; no files are assumed playable.");
            if (Reparse(manifest)) throw new InvalidDataException("Linked debug manifests are not allowed.");
            if (new FileInfo(manifest).Length > MaximumManifestBytes) throw new InvalidDataException("Debug manifest exceeds the metadata budget.");
            bool started = false, ended = false; long declaredParts = -1;
            var indices = new HashSet<int>(); double origin = double.NaN, lastEnd = double.NaN; int lastIndex = 0;
            using (var stream = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
            {
                int total = 0, records = 0; string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    total += line.Length + 1;
                    if (total > MaximumManifestBytes || line.Length > MaximumLineCharacters || ++records > 8192)
                        throw new InvalidDataException("Debug manifest exceeds bounded metadata limits.");
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JObject value;
                    try { value = Parse(line); }
                    catch (JsonException) when (reader.EndOfStream && !ended)
                    { run.ManifestIncomplete = true; break; } // append in progress or torn final line
                    if (ended) throw new InvalidDataException("Debug manifest contains records after run-end.");
                    string type = Text(value, "Type", 64);
                    if (type == "run-start")
                    {
                        if (started || parts.Count != 0) throw new InvalidDataException("Duplicate or misplaced debug run-start.");
                        started = true; run.StartedUtc = Text(value, "StartedUtc", 64); run.Scene = Text(value, "Scene", 200);
                        if (value["NativeStart"] != null && value["NativeStart"]!.Type != JTokenType.Null)
                        {
                            origin = Number(value, "NativeStart");
                            if (origin < 0) throw new InvalidDataException("Invalid debug session native origin.");
                        }
                    }
                    else if (type == "segment-complete")
                    {
                        if (!started) throw new InvalidDataException("Debug segment precedes run-start.");
                        var part = ReadPart(directory, value);
                        if (!indices.Add(part.Index)) throw new InvalidDataException("Duplicate debug segment identity.");
                        if (double.IsNaN(origin)) origin = part.NativeStart;
                        if (part.Index <= lastIndex || part.NativeStart < origin ||
                            !double.IsNaN(lastEnd) && part.NativeStart < lastEnd - .001)
                            throw new InvalidDataException("Debug segment order or boundary continuity is invalid.");
                        lastEnd = part.NativeEnd; lastIndex = part.Index;
                        part.SessionStart = part.NativeStart - origin; part.SessionEnd = part.NativeEnd - origin;
                        run.Duration = Math.Max(run.Duration, part.SessionEnd);
                        if (parts.Count < maxParts) parts.Add(part); else run.Truncated = true;
                    }
                    else if (type == "capture-gap" || type == "segment-incomplete")
                    {
                        if (!started) throw new InvalidDataException("Debug diagnostics precede run-start.");
                        if (type == "capture-gap") { run.CaptureGapCount++; run.GapOver100ms++; }
                        else run.UnsealedSegments++;
                        string startKey = type == "capture-gap" ? "FromNativeTime" : "NativeStart";
                        string endKey = type == "capture-gap" ? "ToNativeTime" : "NativeEnd";
                        if (value[startKey] != null && value[endKey] != null)
                        {
                            double start = Number(value, startKey), end = Number(value, endKey);
                            if (start < 0 || end < start) throw new InvalidDataException("Invalid debug diagnostic time range.");
                            if (double.IsNaN(origin)) origin = start;
                            run.Duration = Math.Max(run.Duration, end - origin);
                        }
                    }
                    else if (type == "capture-gap-summary")
                    {
                        if (!started || value["DetailsTruncated"]?.Type != JTokenType.Boolean || !value["DetailsTruncated"]!.Value<bool>())
                            throw new InvalidDataException("Invalid debug capture gap detail summary.");
                        run.GapDetailsTruncated = true;
                    }
                    else if (type == "run-end")
                    {
                        if (!started) throw new InvalidDataException("Debug run-end precedes run-start.");
                        ended = true; run.Status = Text(value, "Status", 32);
                        if (run.Status != "completed" && run.Status != "faulted") throw new InvalidDataException("Unknown debug run completion status.");
                        run.Fault = OptionalText(value, "Fault", 4096);
                        run.AcceptedFrames = Integer(value, "AcceptedFrames", 0, 1000000000);
                        run.WrittenFrames = Integer(value, "WrittenFrames", 0, 1000000000);
                        declaredParts = Integer(value, "CompletedSegments", 0, 1000000);
                        if (value["GapOver100ms"] != null) run.GapOver100ms = Integer(value, "GapOver100ms", 0, 1000000000);
                        if (value["GapOver500ms"] != null) run.GapOver500ms = Integer(value, "GapOver500ms", 0, 1000000000);
                        if (value["LargestGapSeconds"] != null) run.LargestGapSeconds = Number(value, "LargestGapSeconds");
                        if (run.LargestGapSeconds < 0 || run.GapOver500ms > run.GapOver100ms)
                            throw new InvalidDataException("Invalid debug capture gap totals.");
                        run.CaptureGapCount = (int)run.GapOver100ms;
                    }
                    else throw new InvalidDataException("Unknown debug manifest record type.");
                }
            }
            if (!started) throw new InvalidDataException("Debug manifest has no complete run-start.");
            int filesExamined = 0;
            foreach (string partial in Directory.EnumerateFiles(directory, "segment-*.peakreplay.partial", SearchOption.TopDirectoryOnly))
            {
                if (++filesExamined > 512) { run.Truncated = true; break; }
                if (PartialName(System.IO.Path.GetFileName(partial)) && !Reparse(partial)) run.PartialCount++;
            }
            foreach (var part in parts)
            {
                try
                {
                    CheckDirectoryChain(directory);
                    if (!File.Exists(part.Path)) throw new InvalidDataException("Sealed debug segment is missing.");
                    if (Reparse(part.Path)) throw new InvalidDataException("Linked debug segments are not allowed.");
                    long length = new FileInfo(part.Path).Length;
                    if (length != part.CompressedBytes || length <= 0 || length > ReplayRules.MaxBytes)
                        throw new InvalidDataException("Sealed debug segment size differs from its manifest.");
                    part.Sealed = true;
                    if (headersLeft <= 0) { part.MetadataDeferred = true; run.Truncated = true; continue; }
                    headersLeft--;
                    var header = ReplayFiles.ReadHeader(part.Path);
                    if (header.Scene != run.Scene) throw new InvalidDataException("Debug segment scene differs from its session.");
                    part.Header = header; part.Playable = true;
                }
                catch (Exception e) when (ExpectedReadError(e)) { part.Error = e.Message; }
            }
            run.Complete = ended && run.Status == "completed" && declaredParts == indices.Count &&
                run.AcceptedFrames == run.WrittenFrames && run.PartialCount == 0 && run.UnsealedSegments == 0 && !run.ManifestIncomplete &&
                !run.Truncated && parts.All(p => p.Sealed && p.Error.Length == 0);
            if (ended && run.Status == "completed" && !run.Complete)
                run.Error = "Run completion could not be confirmed: missing/truncated parts, remaining partial data, or mismatched counters.";
        }
        catch (Exception e) when (ExpectedReadError(e))
        {
            run.Error = e.Message; run.Complete = false; run.Status = "invalid";
            // A malformed manifest cannot authorize even earlier-looking paths.
            foreach (var part in parts) { part.Playable = false; part.Error = "Session manifest is invalid."; }
        }
        run.Parts = parts.ToArray(); return run;
    }

    private static DebugRunPartEntry ReadPart(string directory, JObject value)
    {
        int index = (int)Integer(value, "Index", 1, 1000000);
        string file = Text(value, "File", 128);
        if (file != "segment-" + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".peakreplay")
            throw new InvalidDataException("Debug segment filename is not an owned final segment.");
        string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, file));
        if (!Within(directory, path)) throw new InvalidDataException("Debug segment escaped its session directory.");
        double start = Number(value, "NativeStart"), end = Number(value, "NativeEnd");
        if (start < 0 || end < start || end - start > 121) throw new InvalidDataException("Invalid debug segment native time range.");
        return new DebugRunPartEntry { Index = index, Path = path, NativeStart = start, NativeEnd = end,
            FrameCount = (int)Integer(value, "FrameCount", 2, ReplayRules.MaxFrames),
            CompressedBytes = Integer(value, "CompressedBytes", 1, ReplayRules.MaxBytes) };
    }

    private static JObject Parse(string line)
    {
        using var text = new StringReader(line);
        using var json = new JsonTextReader(text) { MaxDepth = 16, DateParseHandling = DateParseHandling.None };
        var value = JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (json.Read()) throw new JsonReaderException("Multiple manifest records on one line.");
        return value;
    }
    private static string Text(JObject value, string key, int maximum)
    {
        var token = value[key];
        if (token?.Type != JTokenType.String) throw new InvalidDataException("Missing debug manifest text: " + key);
        string result = token.Value<string>()!;
        if (string.IsNullOrWhiteSpace(result) || result.Length > maximum) throw new InvalidDataException("Invalid debug manifest text: " + key);
        return result;
    }
    private static string OptionalText(JObject value, string key, int maximum)
    {
        if (value[key] == null || value[key]!.Type == JTokenType.Null) return "";
        if (value[key]!.Type != JTokenType.String) throw new InvalidDataException("Invalid debug manifest text: " + key);
        string result = value[key]!.Value<string>()!;
        if (result.Length > maximum) return result.Substring(0, maximum);
        return result;
    }
    private static long Integer(JObject value, string key, long minimum, long maximum)
    {
        var token = value[key];
        if (token?.Type != JTokenType.Integer) throw new InvalidDataException("Missing debug manifest integer: " + key);
        long result = token.Value<long>();
        if (result < minimum || result > maximum) throw new InvalidDataException("Invalid debug manifest integer: " + key);
        return result;
    }
    private static double Number(JObject value, string key)
    {
        var token = value[key];
        if (token?.Type != JTokenType.Float && token?.Type != JTokenType.Integer) throw new InvalidDataException("Missing debug manifest number: " + key);
        double result = token.Value<double>();
        if (!ReplayRules.Finite(result) || Math.Abs(result) > 1e9) throw new InvalidDataException("Invalid debug manifest number: " + key);
        return result;
    }
    private static bool ExpectedReadError(Exception e) => e is InvalidDataException || e is IOException || e is UnauthorizedAccessException || e is JsonException ||
        e is ArgumentException || e is OverflowException || e is DecoderFallbackException;
    private static bool RunName(string name) => name.StartsWith("run-", StringComparison.Ordinal) && name.Length > 4 && name.Length <= 128 &&
        name.All(c => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_');
    private static bool PartialName(string name)
    {
        const string prefix = "segment-", suffix = ".peakreplay.partial";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal)) return false;
        string digits = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
        return digits.Length == 6 && digits.All(c => c >= '0' && c <= '9') && digits != "000000";
    }
    private static bool Within(string directory, string candidate)
    {
        string prefix = System.IO.Path.GetFullPath(directory).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        return System.IO.Path.GetFullPath(candidate).StartsWith(prefix, System.IO.Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
    private static bool Reparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static void CheckDirectoryChain(string path)
    {
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked debug recording directories are not allowed.");
    }
}
