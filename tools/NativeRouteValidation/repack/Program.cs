using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

if (args.Length != 2) throw new ArgumentException("source summary.json or .peakrun, new private output directory");
using var process = Process.GetCurrentProcess();
try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
string input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
JObject? summary = input.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? JObject.Parse(File.ReadAllText(input)) : null;
string source = summary == null ? input : Path.GetFullPath((string)summary["RecordingFile"]!);
if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Select a new output directory; repack never replaces evidence.");
Directory.CreateDirectory(output);
string sourceHash = Digest(source);
var info = FullReplayArchive.ReadInfo(source);
int targetSchema=(int)typeof(ReplayRules).GetField(nameof(ReplayRules.CurrentSchema))!.GetRawConstantValue()!;
if (!info.Complete || info.Header.Schema < 25 || info.Header.Schema > targetSchema) throw new InvalidDataException("Repack requires a complete schema25/26 source archive.");
var header = JsonConvert.DeserializeObject<ReplayHeader>(JsonConvert.SerializeObject(info.Header))!;
header.Schema = targetSchema;
if(targetSchema!=info.Header.Schema)header.Recorder = new ReplayHeader().Recorder;
var clock = Stopwatch.StartNew();
string candidatePath = ArchiveRepack.Write(source,info,header,output,targetSchema);
var saved = new { FilePath=candidatePath,Status="completed",AcceptedFrames=info.FrameCount,WrittenFrames=info.FrameCount,UnwrittenFrames=0L,BytesWritten=new FileInfo(candidatePath).Length,Duration=info.Duration };
var candidate = FullReplayArchive.ReadInfo(saved.FilePath);
if (info.Pages.Length != candidate.Pages.Length || info.FrameCount != candidate.FrameCount ||
    BitConverter.DoubleToInt64Bits(info.Duration) != BitConverter.DoubleToInt64Bits(candidate.Duration))
    throw new InvalidDataException("Repack changed the independent page/timeline boundaries.");
for (int index = 0; index < info.Pages.Length; index++)
{
    var a = info.Pages[index]; var b = candidate.Pages[index];
    if (a.Start != b.Start || a.End != b.End || a.FirstFrameIndex != b.FirstFrameIndex || a.FrameCount != b.FrameCount || a.Overlap != b.Overlap)
        throw new InvalidDataException("Repack changed page metadata at " + index);
}
var historyFactory = typeof(FullReplayArchive).GetMethod("CreateEffectPageLoader", BindingFlags.Static | BindingFlags.NonPublic)!;
var effectFrame = typeof(FullReplayArchive).GetMethod("EffectsOnlyFrame", BindingFlags.Static | BindingFlags.NonPublic)!;
var oldHistory = (Func<int,CancellationToken,ReplayClip>)historyFactory.Invoke(null, new object?[] { info, null })!;
var newHistory = (Func<int,CancellationToken,ReplayClip>)historyFactory.Invoke(null, new object?[] { candidate, null })!;
long compared = 0, histories = 0, jointClocks = 0;
for (int index = info.Pages.Length - 1; index >= 0; index--)
{
    // A fresh ReadPage resolver independently restores each destination. No
    // prior page, replay prefix, AI execution or long-lived decoded cache is used.
    var oldPage = FullReplayArchive.ReadPage(source, info, index);
    var newPage = FullReplayArchive.ReadPage(saved.FilePath, candidate, index);
    var oldEffects = oldHistory(index, default); var newEffects = newHistory(index, default);
    if (oldPage.Frames.Count != newPage.Frames.Count || oldEffects.Frames.Count != oldPage.Frames.Count || newEffects.Frames.Count != newPage.Frames.Count)
        throw new InvalidDataException("Page/history frame count differs at " + index);
    var comparer = new ExactComparer();
    for (int slot = 0; slot < oldPage.Frames.Count; slot++)
    {
        var a = oldPage.Frames[slot]; var b = newPage.Frames[slot];
        if (!comparer.Equal(a, b)) throw new InvalidDataException($"Exact DTO data differs at page {index}, frame {slot}: {comparer.Difference}.");
        jointClocks += a.Actors.Sum(actor => actor.JointPose.LongLength);
        var aEffects = (ReplayFrame)effectFrame.Invoke(null, new object[] { a })!;
        var bEffects = (ReplayFrame)effectFrame.Invoke(null, new object[] { b })!;
        if (!comparer.Equal(aEffects, oldEffects.Frames[slot]) || !comparer.Equal(bEffects, newEffects.Frames[slot]) ||
            !comparer.Equal(oldEffects.Frames[slot], newEffects.Frames[slot]))
            throw new InvalidDataException($"Independent effect history differs at page {index}, frame {slot}.");
        compared++; histories++;
    }
    if (index % 100 == 0) Console.WriteLine($"VERIFY page={index}/{info.Pages.Length} compared={compared}");
}
if (Digest(source) != sourceHash) throw new IOException("Source archive changed while repacking.");
var provenance = new
{
    SourceRecording = source, SourceSchema = info.Header.Schema, CandidateSchema = candidate.Header.Schema,
    SourceInstalledVersion = (string?)summary?["InstalledVersion"], SourceInstalledMvid = (string?)summary?["InstalledMvid"],
    SourceSha256 = sourceHash, ReaderAssemblySha256 = Digest(typeof(ReplayFrame).Assembly.Location),
    NativeCaptureUnchanged = true, NativePerformanceDataReused = true,
    ExactAllDtoPropertiesAndFields = true, FloatAndDoubleBitsExact = true, JointObservationClocksExact = true,
    OriginalActorJointWirePayloadsPreserved = true,
    OriginalActorFieldOrderPreserved = true,
    AllIndependentReversePagesEqual = true, AllIndependentEffectHistoriesEqual = true,
};
if (summary != null)
{
    var copy = (JObject)summary.DeepClone(); copy["RecordingFile"] = saved.FilePath;
    copy["RecordingResult"] = JObject.FromObject(saved); copy["OfflineRepack"] = JObject.FromObject(provenance);
    File.WriteAllText(Path.Combine(output, "summary.json"), copy.ToString(Formatting.Indented));
    string heartbeat = Path.Combine(Path.GetDirectoryName(input)!, "heartbeat.jsonl");
    if (File.Exists(heartbeat)) File.Copy(heartbeat, Path.Combine(output, "heartbeat.jsonl"), overwrite: false);
}
var report = new
{
    Source = source, Candidate = saved.FilePath, SourceBytes = new FileInfo(source).Length,
    CandidateBytes = new FileInfo(saved.FilePath).Length, SourceSchema = info.Header.Schema, CandidateSchema = candidate.Header.Schema,
    ContainerVersion = candidate.Version, Frames = info.FrameCount, Pages = info.Pages.Length,
    ComparedStoredFramesIncludingOverlap = compared, ComparedEffectHistoryFrames = histories, JointObservationClocksCompared = jointClocks,
    SourceSharedKinds = Kinds(info), CandidateSharedKinds = Kinds(candidate), ElapsedSeconds = clock.Elapsed.TotalSeconds,
    Provenance = provenance,
    Limits = "Offline storage rewrite of the same captured observations. Capture timing, action coverage and memory evidence remain the original native session; this is not a new native run or a Unity performance measurement. Original Actor patches and joint wire payloads are retained to avoid second-pass quaternion quantization. Scene records and direct shared blocks use the current codec; original page boundaries, decoded budgets and global cover/outcome metadata are retained. Every DTO property/field and all independent effect histories are verified in reverse order.",
};
File.WriteAllText(Path.Combine(output, "repack-verification.json"), JsonConvert.SerializeObject(report, Formatting.Indented));
Console.WriteLine(JsonConvert.SerializeObject(new { report.SourceBytes, report.CandidateBytes, report.Frames, report.Pages, report.ElapsedSeconds, Verified = true }));

static string Digest(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
static object[] Kinds(FullReplayInfo info) => (info.SharedBlocks ?? Array.Empty<FullReplaySharedBlock>()).GroupBy(block => block.Kind).OrderBy(group => group.Key)
    .Select(group => (object)new { Kind = group.Key, Blocks = group.Count(), RawBytes = group.Sum(block => (long)block.RawBytes), CompressedBytes = group.Sum(block => block.Length) }).ToArray();

static class ArchiveRepack
{
    internal static string Write(string source,FullReplayInfo info,ReplayHeader header,string output,int targetSchema)
    {
        var assembly=typeof(ReplayFrame).Assembly;
        var codecType=assembly.GetType("PeakReplayLab.ReplayDeltaCodec+Writer",throwOnError:true)!;
        var sharedType=assembly.GetType("PeakReplayLab.FullReplaySharedWriter",throwOnError:true)!;
        var sharedConstructor=sharedType.GetConstructor(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new[]{typeof(int)},null);
        object shared=sharedConstructor==null?Activator.CreateInstance(sharedType,nonPublic:true)!:sharedConstructor.Invoke(new object[]{targetSchema});
        var encode=codecType.GetMethod("Encode",BindingFlags.Instance|BindingFlags.Public)!;
        var baseline=sharedType.GetMethod("EncodeBaseline",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var flush=sharedType.GetMethod("FlushPending",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var blocks=(List<FullReplaySharedBlock>)sharedType.GetField("Blocks",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(shared)!;
        var writeHistory=typeof(FullReplayArchive).GetMethod("WriteEffectHistory",BindingFlags.Static|BindingFlags.NonPublic)!;
        var effectsOnly=typeof(FullReplayArchive).GetMethod("EffectsOnlyFrame",BindingFlags.Static|BindingFlags.NonPublic)!;
        string path=Path.Combine(output,"repacked.peakrun");
        using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        using var binary=new BinaryWriter(file,Encoding.UTF8,leaveOpen:true);
        byte[] head=Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(header,ReplayFiles.Json));
        binary.Write(Encoding.ASCII.GetBytes("PEAKRUN1"));binary.Write(head.Length);binary.Write(head);binary.Flush();
        var pages=new List<FullReplayPage>();
        for(int index=0;index<info.Pages.Length;index++)
        {
            var original=info.Pages[index];var clip=FullReplayArchive.ReadPage(source,info,index);
            var rawFrames=RawFrames(source,original);
            if(rawFrames.Count!=clip.Frames.Count)throw new InvalidDataException("Original actor wire/frame count differs.");
            object codec=Activator.CreateInstance(codecType,new object[]{targetSchema})!;
            long offset=file.Position,rawBytes=0;
            using(var gzip=new GZipStream(file,CompressionLevel.Fastest,leaveOpen:true))
            using(var text=new StreamWriter(gzip,new UTF8Encoding(false),8192,leaveOpen:true))
            {
                void Write(object value)
                {
                    string line=JsonConvert.SerializeObject(value,ReplayFiles.Json);
                    if(line.Length>ReplayRules.MaxLineCharacters)throw new InvalidDataException("Repacked record exceeds its original line budget.");
                    rawBytes=checked(rawBytes+Encoding.UTF8.GetByteCount(line)+1);
                    if(rawBytes>FullReplayArchive.MaximumPageRawBytes)throw new InvalidDataException("Repacked page exceeds the page budget.");
                    text.Write(line);text.Write('\n');
                }
                Write(header);
                for(int slot=0;slot<clip.Frames.Count;slot++)
                {
                    var record=(Dictionary<string,object?>)encode.Invoke(codec,new object[]{clip.Frames[slot]})!;
                    foreach(string name in new[]{"Actors","RemovedActors","ActorOrder"})
                    {
                        if(rawFrames[slot][name] is JArray values&&(values.Count!=0||targetSchema<26))
                            record[name]=name=="ActorOrder"?values.Select(value=>(string)value!).ToArray():values;
                        else record.Remove(name);
                    }
                    if(slot==0)baseline.Invoke(shared,new object[]{record,clip.Frames[slot]});
                    Write(record);
                }
                Write(new{Type="end"});
            }
            long length=file.Position-offset;
            var effectFrames=clip.Frames.Select(frame=>(ReplayFrame)effectsOnly.Invoke(null,new object[]{frame})!).ToList();
            var history=(FullReplayEffectHistory)writeHistory.Invoke(null,new object[]{file,header,effectFrames,CancellationToken.None})!;
            flush.Invoke(shared,new object[]{file});
            pages.Add(new FullReplayPage{Index=index,Start=original.Start,End=original.End,Offset=offset,Length=length,RawBytes=rawBytes,
                DecodedBytes=original.DecodedBytes,FrameCount=original.FrameCount,FirstFrameIndex=original.FirstFrameIndex,Overlap=original.Overlap,
                EffectHistory=history,SharedThrough=blocks.Count});
            if(index%100==0)Console.WriteLine($"WRITE page={index}/{info.Pages.Length}");
        }
        var result=JsonConvert.DeserializeObject<FullReplayIndex>(JsonConvert.SerializeObject(info,ReplayFiles.Json))!;
        result.Header=header;result.Pages=pages.ToArray();result.SharedBlocks=blocks.ToArray();result.Version=4;result.Reason="offline-same-source-repack";
        byte[] data=Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(result,ReplayFiles.Json));
        if(data.Length>FullReplayArchive.MaximumIndexBytes)throw new InvalidDataException("Repacked index exceeds its budget.");
        binary.Write(data);binary.Write((long)data.Length);binary.Write(Encoding.ASCII.GetBytes("PEAKEND1"));binary.Flush();
        return path;
    }
    private static List<JObject> RawFrames(string source,FullReplayPage page)
    {
        using var file=File.OpenRead(source);file.Position=page.Offset;
        byte[] compressed=new byte[checked((int)page.Length)];file.ReadExactly(compressed);
        using var memory=new MemoryStream(compressed,writable:false);
        using var gzip=new GZipStream(memory,CompressionMode.Decompress);
        using var text=new StreamReader(gzip,Encoding.UTF8);
        var result=new List<JObject>();
        string? line;
        while((line=text.ReadLine())!=null)
        {
            var value=JObject.Parse(line);
            if((string?)value["Type"]=="frame")
            {
                var actorFields=new JObject();
                foreach(string name in new[]{"Actors","RemovedActors","ActorOrder"})
                    if(value[name]!=null)actorFields[name]=value[name];
                result.Add(actorFields);
            }
        }
        return result;
    }
}

sealed class ExactComparer
{
    public string Difference { get; private set; } = "";
    private sealed class PairIdentity : IEqualityComparer<(object Left, object Right)>
    {
        public bool Equals((object Left, object Right) a, (object Left, object Right) b) => ReferenceEquals(a.Left,b.Left) && ReferenceEquals(a.Right,b.Right);
        public int GetHashCode((object Left, object Right) pair) => HashCode.Combine(RuntimeHelpers.GetHashCode(pair.Left),RuntimeHelpers.GetHashCode(pair.Right));
    }
    private readonly HashSet<(object,object)> visited = new(new PairIdentity());
    private static readonly Dictionary<Type,(PropertyInfo[] Properties,FieldInfo[] Fields)> members = new();
    public bool Equal(object? a, object? b, string path = "frame")
    {
        if (ReferenceEquals(a,b)) return true;
        Difference = path;
        if (a == null || b == null || a.GetType() != b.GetType()) return false;
        if (a is float fa && b is float fb) return BitConverter.SingleToInt32Bits(fa) == BitConverter.SingleToInt32Bits(fb);
        if (a is double da && b is double db) return BitConverter.DoubleToInt64Bits(da) == BitConverter.DoubleToInt64Bits(db);
        var type = a.GetType();
        if (type.IsValueType || a is string) return a.Equals(b);
        if (!visited.Add((a,b))) return true;
        if (a is Array aa && b is Array bb)
        {
            if (aa.Rank != 1 || bb.Rank != 1 || aa.Length != bb.Length) return false;
            for (int index = 0; index < aa.Length; index++) if (!Equal(aa.GetValue(index),bb.GetValue(index),path+"["+index+"]")) return false;
            return true;
        }
        if (type.Namespace != "PeakReplayLab") throw new InvalidDataException("Unsupported comparison type: " + type.FullName);
        if (!members.TryGetValue(type,out var shape))
            members.Add(type,shape=(type.GetProperties(BindingFlags.Instance|BindingFlags.Public).Where(field=>field.CanRead&&field.GetIndexParameters().Length==0).ToArray(),type.GetFields(BindingFlags.Instance|BindingFlags.Public)));
        foreach (var property in shape.Properties) if (!Equal(property.GetValue(a),property.GetValue(b),path+"."+property.Name)) return false;
        foreach (var field in shape.Fields) if (!Equal(field.GetValue(a),field.GetValue(b),path+"."+field.Name)) return false;
        return true;
    }
}
