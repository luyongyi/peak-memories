using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 5)
{
    Console.Error.WriteLine("Usage: TrajectoryBenchmark <plugin.dll> <PEAK_Data/Managed> <sealed.peakrun> <new-output-directory> <label>");
    return 2;
}
string pluginPath = Path.GetFullPath(args[0]), managed = Path.GetFullPath(args[1]);
string recording = Path.GetFullPath(args[2]), output = Path.GetFullPath(args[3]), label = args[4];
if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Use a new output directory for each run.");
Directory.CreateDirectory(output);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string path = Path.Combine(managed, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
object Invoke(MethodInfo method, params object?[] arguments)
{
    try { return method.Invoke(null, arguments)!; }
    catch (TargetInvocationException error) when (error.InnerException != null)
    { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
}
T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
var source = new FileInfo(recording);
long sourceBytes = source.Length; DateTime sourceTime = source.LastWriteTimeUtc;
var readInfo = assembly.GetType("PeakReplayLab.FullReplayArchive", true)!.GetMethod("ReadInfo")!;
var info = Invoke(readInfo, recording, CancellationToken.None);
var header = Property<object>(info, "Header");
var export = assembly.GetType("PeakReplayLab.ReplayTrajectoryExporter", true)!.GetMethod("Export")!;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
int progressBucket = -1;
Action<double> progress = value =>
{
    int bucket = (int)Math.Floor(value * 10);
    if (bucket <= progressBucket) return;
    progressBucket = bucket; Console.Error.WriteLine($"{label}: {bucket * 10}%");
};
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
using var process = Process.GetCurrentProcess();
long allocated = GC.GetTotalAllocatedBytes(true);
var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
TimeSpan cpu = process.TotalProcessorTime;
var watch = Stopwatch.StartNew();
var result = Invoke(export, recording, Path.Combine(output, "exports"), cancellation.Token, progress);
watch.Stop();
long exportAllocated = GC.GetTotalAllocatedBytes(true) - allocated;
double cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds;
process.Refresh(); long peakWorkingSet = process.PeakWorkingSet64;
int[] exportCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray();
source.Refresh();
if (source.Length != sourceBytes || source.LastWriteTimeUtc != sourceTime) throw new IOException("Recording changed during export.");
var packages = new List<object>(); long eventCount = 0;
var events = new SortedDictionary<string, long>(StringComparer.Ordinal);
foreach (string path in Property<string[]>(result, "Paths"))
{
    using var file = File.OpenRead(path); using var gzip = new GZipStream(file, CompressionMode.Decompress);
    using var bytes = new MemoryStream(); gzip.CopyTo(bytes);
    byte[] json = bytes.ToArray(); using var doc = JsonDocument.Parse(json);
    foreach (var player in doc.RootElement.GetProperty("players").EnumerateArray())
        foreach (var item in player.GetProperty("events").EnumerateArray())
        {
            string kind = item.GetProperty("kind").GetString()!;
            events.TryGetValue(kind, out long count); events[kind] = count + 1; eventCount++;
        }
    packages.Add(new { DecodedBytes = json.Length, JsonSHA256 = Convert.ToHexString(SHA256.HashData(json)), CompressedBytes = file.Length });
}
var metrics = new
{
    Label = label, Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    LogicalProcessors = Environment.ProcessorCount, PluginVersion = assembly.GetName().Version?.ToString(),
    PluginSHA256 = Hash(pluginPath), Schema = Property<int>(header, "Schema"),
    RecordingBytes = sourceBytes, DurationSeconds = Property<double>(info, "Duration"),
    Frames = Property<long>(info, "FrameCount"), Pages = Property<Array>(info, "Pages").Length,
    WallSeconds = watch.Elapsed.TotalSeconds, CpuSeconds = cpuSeconds, AllocatedBytes = exportAllocated,
    PeakWorkingSetBytes = peakWorkingSet, Collections = exportCollections,
    Points = Property<long>(result, "PointCount"), Players = Property<int>(result, "PlayerCount"),
    EventCount = eventCount, Events = events, Packages = packages,
};
string report = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(output, "metrics.json"), report);
Console.WriteLine(report);
return 0;
