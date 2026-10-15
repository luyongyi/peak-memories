using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json;
using PeakReplayLab;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LocalTrajectoryAudit;

// Never shipped with the release plugin. Complete explicit launch arguments are
// mandatory, and every export still goes through the actual menu/card guards.
[BepInPlugin("local.peakreplay.trajectory-audit", "Protected Native Trajectory Audit", "0.0.1")]
[BepInDependency("cn.mylus.peakreplaylab", BepInDependency.DependencyFlags.HardDependency)]
[DefaultExecutionOrder(31000)]
public sealed class TrajectoryAuditProbe : BaseUnityPlugin
{
    public sealed class Plan
    {
        public string RecentPath = "", LargePath = "";
        public bool RunCancellation = true;
        public double CancelAfterSeconds = 2, BaselineSeconds = 10, ResultHoldSeconds = 6;
        public int MaximumSeconds = 1500;
    }
    private sealed class Round
    {
        public string Name = "", Recording = "", Output = "";
        public bool Cancel, ExpectDuplicate, RequestAccepted, CancelObserved, UiFinished;
        public long Start, ExportStart, ExportStop, UiStop, UploadStart, UploadStop;
        public string ExportError = "";
        public int ProgressChanges;
        public float LastProgress = -1;
        public FrameMeter ExportFrames = new(), UploadFrames = new();
        public Task? Worker, Sender;
        public Task<object>? Digest;
        public object? PackageMetrics, Receipt;
    }
    private sealed class FrameMeter
    {
        private readonly double[] values = new double[180000];
        private int count, over50, over100;
        private double total, max;
        public void Add(double ms)
        {
            if (count < values.Length) values[count] = ms;
            count++; total += ms; max = Math.Max(max, ms);
            if (ms > 50) over50++;
            if (ms > 100) over100++;
        }
        public object Summary()
        {
            var sorted = values.Take(Math.Min(count, values.Length)).OrderBy(x => x).ToArray();
            return new { Samples = count, RetainedSamples = sorted.Length, MeanMs = count == 0 ? 0 : total / count,
                P95Ms = sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], MaxMs = max,
                Over50Ms = over50, Over100Ms = over100, Measurement = "Main-thread LateUpdate wall interval; includes presentation and probe overhead, not exclusive CPU time" };
        }
    }
    private static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static TrajectoryAuditProbe? active;
    private readonly Harmony harmony = new("local.peakreplay.trajectory-audit");
    private readonly List<Round> rounds = new();
    private readonly FrameMeter baseline = new();
    private readonly Dictionary<string, string> configHashes = new(StringComparer.OrdinalIgnoreCase);
    private Plugin? owner;
    private ConfigEntry<string>? endpointSetting;
    private string oldEndpoint = "", endpoint = "", output = "", savePath = "", saveHash = "", error = "", phase = "Title";
    private Plan plan = new();
    private bool armed, protectedWrites, restored;
    private long begun, phaseStart, previousFrame, nextHeartbeat;
    private int roundIndex;
    private Round? current;

    private static object? Read(object? source, string name)
    {
        if (source == null) return null;
        for (var type = source.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, Flags | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(source);
            var property = type.GetProperty(name, Flags | BindingFlags.DeclaredOnly);
            if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(source);
        }
        throw new MissingMemberException(source.GetType().FullName, name);
    }
    private static object? Invoke(object target, string name, params object[] args) =>
        (AccessTools.DeclaredMethod(target.GetType(), name) ?? throw new MissingMethodException(target.GetType().FullName, name)).Invoke(target, args);
    private static double Seconds(long from, long to) => (to - from) / (double)Stopwatch.Frequency;
    private static string Hash(string path)
    {
        if (!File.Exists(path)) return "ABSENT";
        using var sha = SHA256.Create(); using var file = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static bool AllowWrite() => active?.protectedWrites != true;
    private void Block(Type type, string method)
    {
        var target = AccessTools.DeclaredMethod(type, method) ?? throw new MissingMethodException(type.FullName, method);
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(TrajectoryAuditProbe), nameof(AllowWrite)));
    }
    private static bool BeforeExport(string recordingPath, ref string outputDirectory, ref Round? __state)
    {
        var probe = active;
        Require(probe?.armed == true && probe.current != null, "Unexpected trajectory export during protected test.");
        var round = probe!.current!;
        Require(string.Equals(Path.GetFullPath(recordingPath), round.Recording, StringComparison.OrdinalIgnoreCase), "Unexpected recording export.");
        outputDirectory = round.Output;
        round.ExportStart = Stopwatch.GetTimestamp(); __state = round;
        return true;
    }
    private static Exception? AfterExport(Exception? __exception, Round? __state)
    {
        if (__state != null)
        {
            __state.ExportStop = Stopwatch.GetTimestamp();
            __state.ExportError = __exception?.GetType().Name ?? "";
        }
        return __exception;
    }
    private static void GuardUpload(IReadOnlyList<string> packagePaths, Uri endpoint)
    {
        var probe = active;
        Require(probe?.armed == true && endpoint.AbsoluteUri == probe.endpoint && endpoint.Host == "127.0.0.1", "Only the explicit local loopback endpoint is allowed.");
        string prefix = probe!.output + Path.DirectorySeparatorChar;
        Require(packagePaths.All(path => Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)), "Only this session's independent trajectory packages may be uploaded.");
    }
    private void Awake()
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("--peak-trajectory-audit")) { enabled = false; return; }
        try
        {
            string Arg(string key) => args.SingleOrDefault(x => x.StartsWith(key + "=", StringComparison.Ordinal))?.Substring(key.Length + 1).Trim('"') ?? "";
            string label = Arg("--peak-trajectory-label"), root = Arg("--peak-trajectory-output-root"), planPath = Arg("--peak-trajectory-plan");
            endpoint = Arg("--peak-trajectory-endpoint"); savePath = Arg("--peak-trajectory-save-path"); saveHash = Arg("--peak-trajectory-save-hash");
            Require(label.Length is > 0 and <= 64 && label.All(c => char.IsLetterOrDigit(c) || c == '-'), "A fresh alphanumeric/hyphen label is required.");
            Require(Path.IsPathFullyQualified(root) && Path.IsPathFullyQualified(planPath), "Explicit absolute session root and plan are required.");
            Require(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null, "A graphical native PEAK process is required.");
            var uri = TrajectoryUploader.Endpoint(endpoint);
            Require(uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.Port > 0 && uri.AbsolutePath == "/api/route-uploads", "Only an explicitly supplied numeric IPv4 loopback API endpoint is admitted.");
            endpoint = uri.AbsoluteUri;
            plan = JsonConvert.DeserializeObject<Plan>(File.ReadAllText(planPath)) ?? throw new InvalidDataException("Missing plan.");
            Require(plan.MaximumSeconds >= 1200 && plan.MaximumSeconds <= 2400 && plan.BaselineSeconds >= 5 && plan.BaselineSeconds <= 30 && plan.CancelAfterSeconds >= .5 && plan.CancelAfterSeconds <= 30 && plan.ResultHoldSeconds >= 0 && plan.ResultHoldSeconds <= 30, "Plan bounds are invalid.");
            var nativeSave = typeof(Peak.Quicksave).GetProperty("SavePath", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as string;
            Require(nativeSave != null && Path.IsPathFullyQualified(savePath) && string.Equals(Path.GetFullPath(nativeSave!), Path.GetFullPath(savePath), StringComparison.OrdinalIgnoreCase), "The actual native save path must be protected.");
            Require(!Directory.Exists(savePath) && (saveHash == "ABSENT" || saveHash.Length == 64 && saveHash.All(Uri.IsHexDigit)) && Hash(savePath) == saveHash, "Protected save proof mismatch.");
            string candidate = Path.Combine(Path.GetFullPath(root), label); Require(!Directory.Exists(candidate), "Session already exists.");
            output = candidate;
            foreach (var file in Directory.GetFiles(Paths.ConfigPath, "*", SearchOption.AllDirectories)) configHashes[file] = Hash(file);
            Directory.CreateDirectory(output);
            active = this; protectedWrites = true;
            // Config save protection is installed before touching the in-memory endpoint.
            Block(typeof(ConfigFile), "Save");
            foreach (var name in new[] { "SaveNow", "DestroySaveData", "FinalizeRunSetup" }) Block(typeof(Peak.Quicksave), name);
            foreach (var name in new[] { "SetSteamStatInternal", "IncrementSteamStat", "ThrowAchievement" }) Block(typeof(AchievementManager), name);
            harmony.Patch(AccessTools.DeclaredMethod(typeof(ReplayTrajectoryExporter), "Export"), prefix: new HarmonyMethod(typeof(TrajectoryAuditProbe), nameof(BeforeExport)), finalizer: new HarmonyMethod(typeof(TrajectoryAuditProbe), nameof(AfterExport)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(TrajectoryUploader), "UploadManyAsync"), prefix: new HarmonyMethod(typeof(TrajectoryAuditProbe), nameof(GuardUpload)));
            AddRound("recent-normal", plan.RecentPath, false, false);
            AddRound("recent-duplicate", plan.RecentPath, false, true);
            if (plan.RunCancellation) AddRound("large-cancel", plan.LargePath, true, false);
            AddRound("large-retry", plan.LargePath, false, false);
            begun = previousFrame = phaseStart = Stopwatch.GetTimestamp(); armed = true; WritePhase();
        }
        catch (Exception exception) { error = exception.ToString(); Logger.LogError("Trajectory probe refused: " + exception); Finish(false); }
    }
    private void AddRound(string name, string path, bool cancel, bool duplicate)
    {
        Require(Path.IsPathFullyQualified(path) && path.EndsWith(".peakrun", StringComparison.OrdinalIgnoreCase) && File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "An existing ordinary sealed full recording is required.");
        rounds.Add(new Round { Name = name, Recording = Path.GetFullPath(path), Output = Path.Combine(output, name, "exports"), Cancel = cancel, ExpectDuplicate = duplicate });
    }
    private void Enter(string next)
    {
        phase = next; phaseStart = Stopwatch.GetTimestamp(); WritePhase();
    }
    private void WritePhase() => File.WriteAllText(Path.Combine(output, "phase.json"), JsonConvert.SerializeObject(new { Phase = phase, Round = current?.Name, ElapsedSeconds = Seconds(begun, Stopwatch.GetTimestamp()), Error = error }));
    private void LateUpdate()
    {
        if (!armed) return;
        long now = Stopwatch.GetTimestamp(); double frameMs = Seconds(previousFrame, now) * 1000; previousFrame = now;
        if (phase == "Baseline") baseline.Add(frameMs);
        if (phase == "Export") current!.ExportFrames.Add(frameMs);
        if (phase == "Upload") current!.UploadFrames.Add(frameMs);
        try
        {
            Require(Seconds(begun, now) < plan.MaximumSeconds, "Native test exceeded its total runtime bound.");
            if (now >= nextHeartbeat)
            {
                nextHeartbeat = now + 5 * Stopwatch.Frequency;
                File.WriteAllText(Path.Combine(output, "heartbeat.json"), JsonConvert.SerializeObject(new { Phase = phase, Round = current?.Name, ElapsedSeconds = Seconds(begun, now) }));
            }
            if (owner == null)
            {
                if (!Chainloader.PluginInfos.TryGetValue("cn.mylus.peakreplaylab", out var info) || info.Instance is not Plugin plugin) return;
                owner = plugin;
                endpointSetting = (ConfigEntry<string>)Read(owner, "routeUploadEndpoint")!;
                oldEndpoint = endpointSetting.Value; endpointSetting.Value = endpoint;
            }
            Require(SceneManager.GetActiveScene().name == "Title" || phase == "Title", "The test must remain in the native Title scene.");
            if (phase == "Title")
            {
                Require(Seconds(begun, now) < 180, "Native Title/library was not ready within three minutes.");
                if (SceneManager.GetActiveScene().name != "Title" || !UnityEngine.Object.FindFirstObjectByType<MainMenuMainPage>()) return;
                Invoke(owner, "OpenLibrary");
                if (Read(owner, "library") is not true) return;
                var ui = Read(owner, "nativeLibrary")!;
                ((Action<bool>)Read(ui, "chooseCollection")!)(true);
                Enter("Catalog"); return;
            }
            if (phase == "Catalog")
            {
                Require(Seconds(phaseStart, now) < 180, "The real sealed recording catalog did not become ready.");
                if (Read(owner, "listing") != null || Read(owner, "fullRunListing") != null) return;
                Require(Read(owner, "library") is true && Read(owner, "fullRunLibrary") is true, "The real full recording library must be open.");
                foreach (var round in rounds) Require(FindCard(round.Recording) != null, "A requested recording is absent or unplayable in the native catalog.");
                Enter("Baseline"); return;
            }
            if (phase == "Baseline") { if (Seconds(phaseStart, now) >= plan.BaselineSeconds) StartRound(); return; }
            if (phase == "Export")
            {
                var progress = Read(owner, "routeProgress"); float value = (float)Read(progress, "Value")!;
                if (value != current!.LastProgress) { current.LastProgress = value; current.ProgressChanges++; }
                if (current.Cancel && Seconds(current.Start, now) >= plan.CancelAfterSeconds)
                {
                    Require(current.Worker != null && !current.Worker.IsCompleted, "The cancellation fixture already completed before its cancellation point.");
                    Invoke(owner, "CancelRouteUpload"); Enter("Cancellation"); return;
                }
                string routeError = (string)Read(owner, "routeError")!;
                Require(routeError.Length == 0, "Production export error: " + routeError);
                if (Read(owner, "routeExport") != null) return;
                var package = Read(owner, "routePackage") as TrajectoryExportResult;
                Require(package != null && current.Worker?.Status == TaskStatus.RanToCompletion, "Production menu export did not yield a successful package.");
                Require(current.ExportStart > 0 && current.ExportStop >= current.ExportStart, "The scoped production Export timing hook was not observed.");
                current.UiStop = now; current.UiFinished = true;
                string[] paths = package!.Paths.ToArray();
                current.Digest = Task.Run(() => PackageSummary(paths));
                Enter("Digest"); return;
            }
            if (phase == "Cancellation")
            {
                if (!current!.Worker!.IsCompleted) return;
                current.CancelObserved = current.Worker.IsCanceled || current.Worker.Exception?.Flatten().InnerExceptions.Any(x => x is OperationCanceledException) == true;
                Require(current.CancelObserved && Read(owner, "routeExport") == null && Read(owner, "routePackage") == null, "Cancellation did not stop and clear production export work.");
                current.UiStop = now;
                // Observe the original task even though the production cancellation cleared its field.
                _ = current.Worker.Exception;
                Enter("Hold"); return;
            }
            if (phase == "Digest")
            {
                if (!current!.Digest!.IsCompleted) return;
                current.PackageMetrics = current.Digest.GetAwaiter().GetResult();
                Invoke(owner, "ConfirmRouteUpload");
                current.Sender = Read(owner, "routeSending") as Task;
                Require(current.Sender != null, "The guarded production upload confirmation did not start HTTP work.");
                current.UploadStart = now; Enter("Upload"); return;
            }
            if (phase == "Upload")
            {
                string routeError = (string)Read(owner, "routeError")!;
                Require(routeError.Length == 0, "Production upload error: " + routeError);
                if (Read(owner, "routeSending") != null) return;
                Require(Read(owner, "routeFinished") is true && current!.Sender!.Status == TaskStatus.RanToCompletion, "Production upload did not acknowledge success.");
                var receipt = Read(current!.Sender, "Result") as TrajectoryUploadReceipt;
                Require(receipt != null && receipt.UploadIds.Length > 0 && (!current.ExpectDuplicate || receipt.Duplicate), "Production receipt/duplicate confirmation was invalid.");
                var package = (TrajectoryExportResult)Read(owner, "routePackage")!;
                Require(File.Exists(package.Path + ".receipt.json"), "The production local acknowledgement was not saved.");
                current.Receipt = new { receipt!.Duplicate, receipt.ModerationStatus, receipt.MapCompatibility, Parts = receipt.UploadIds.Length, LocalAcknowledgementWritten = true };
                current.UploadStop = now; Enter("Hold"); return;
            }
            if (phase == "Hold")
            {
                if (Seconds(phaseStart, now) < plan.ResultHoldSeconds) return;
                WriteRound(current!); Invoke(owner, "CancelRouteUpload"); roundIndex++;
                if (roundIndex == rounds.Count) { Finish(true); return; }
                StartRound();
            }
        }
        catch (Exception exception) { error = (exception is TargetInvocationException invocation && invocation.InnerException != null ? invocation.InnerException : exception).ToString(); Logger.LogError(error); Finish(false); }
    }
    private object? FindCard(string path) => ((IEnumerable)Read(owner, "fullRunCards")!).Cast<object>().FirstOrDefault(card => Read(card, "IsFullRun") is true && Read(card, "Playable") is true && string.Equals((string)Read(card, "Path")!, path, StringComparison.OrdinalIgnoreCase));
    private void StartRound()
    {
        current = rounds[roundIndex];
        Require(FindCard(current.Recording) != null && Read(owner, "FileManagementAllowed") is true && Read(owner, "library") is true, "Actual menu/card guards are not ready.");
        current.Start = Stopwatch.GetTimestamp();
        current.RequestAccepted = Invoke(owner!, "RequestRouteExport", current.Recording) is true;
        Require(current.RequestAccepted, "The production RequestRouteExport guard declined the real catalog card.");
        current.Worker = Read(owner, "routeExport") as Task;
        var ui = Read(owner, "nativeLibrary")!;
        AccessTools.Field(ui.GetType(), "selected").SetValue(ui, FindCard(current.Recording));
        var pageType = ui.GetType().GetNestedType("Page", BindingFlags.NonPublic)!;
        Invoke(ui, "Navigate", Enum.Parse(pageType, "Routes"));
        Enter("Export");
    }
    private static object PackageSummary(string[] paths)
    {
        var parts = new List<object>(); long points = 0, events = 0;
        foreach (string path in paths)
        {
            using var input = File.OpenRead(path); using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var memory = new MemoryStream(); gzip.CopyTo(memory); byte[] bytes = memory.ToArray();
            using var sha = SHA256.Create(); string hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
            var package = JsonConvert.DeserializeObject<TrajectoryPackage>(System.Text.Encoding.UTF8.GetString(bytes)) ?? throw new InvalidDataException("Invalid trajectory package.");
            long partPoints = package.Players.Sum(player => (long)player.Points.Count), partEvents = package.Players.Sum(player => (long)player.Events.Count);
            points += partPoints; events += partEvents;
            parts.Add(new { JsonSha256 = hash, DecodedBytes = bytes.Length, CompressedBytes = new FileInfo(path).Length, Players = package.Players.Count, PointCount = partPoints, EventCount = partEvents });
        }
        return new { PointCount = points, EventCount = events, Parts = parts };
    }
    private object RoundSummary(Round round) => new
    {
        round.Name, round.Cancel, round.RequestAccepted, round.CancelObserved, round.UiFinished,
        ExportSeconds = round.ExportStop == 0 ? (double?)null : Seconds(round.ExportStart, round.ExportStop),
        UiSeconds = round.UiStop == 0 ? (double?)null : Seconds(round.Start, round.UiStop),
        UploadUiSeconds = round.UploadStop == 0 ? (double?)null : Seconds(round.UploadStart, round.UploadStop),
        round.ProgressChanges, round.ExportError, Package = round.PackageMetrics, round.Receipt,
        ExportFrames = round.ExportFrames.Summary(), UploadFrames = round.UploadFrames.Summary()
    };
    private void WriteRound(Round round) => File.WriteAllText(Path.Combine(output, round.Name + ".json"), JsonConvert.SerializeObject(RoundSummary(round), Formatting.Indented));
    private void Restore()
    {
        if (restored) return; restored = true;
        if (owner != null) Invoke(owner, "CancelRouteUpload");
        if (endpointSetting != null) endpointSetting.Value = oldEndpoint;
        // Leave all Harmony write protections armed throughout application shutdown.
    }
    private void Finish(bool passed)
    {
        if (output.Length == 0 || !Directory.Exists(output)) { armed = false; enabled = false; return; }
        try { Restore(); } catch (Exception exception) { passed = false; error += "\nRestore: " + exception; }
        armed = false;
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonConvert.SerializeObject(new
        {
            Passed = passed, Error = error, ElapsedSeconds = begun == 0 ? 0 : Seconds(begun, Stopwatch.GetTimestamp()),
            Runtime = Environment.Version.ToString(), PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString(),
            NativeGraphicsDevice = SystemInfo.graphicsDeviceType.ToString(), NativeScene = SceneManager.GetActiveScene().name,
            Endpoint = endpoint, OriginalEndpointRestored = endpointSetting == null || endpointSetting.Value == oldEndpoint,
            BaselineFrames = baseline.Summary(), Rounds = rounds.Select(RoundSummary).ToArray(),
            QuicksaveUnchanged = !Directory.Exists(savePath) && Hash(savePath) == saveHash,
            ConfigurationsUnchangedDuringProbe = configHashes.All(entry => Hash(entry.Key) == entry.Value),
            Note = "Graphical native PEAK/Unity Mono production menu, export, upload and acknowledgement; frame wall intervals are not exclusive main-thread CPU timings"
        }, Formatting.Indented));
        phase = passed ? "Passed" : "Failed"; WritePhase(); Application.Quit();
    }
    private void OnApplicationQuit() { if (armed) { error = "Application exited before all test rounds completed."; Finish(false); } else Restore(); }
}
