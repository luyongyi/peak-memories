using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

string repo = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
if (args.Contains("--compare-first",StringComparer.Ordinal)) { ActualCheckpointRepack.DiagnoseFirst(repo); return; }
if (args.Contains("--repack",StringComparer.Ordinal) || args.Contains("--verify-existing",StringComparer.Ordinal)) { await ActualCheckpointRepack.Run(repo,args.Contains("--verify-existing",StringComparer.Ordinal)); return; }
string sourceSummary = Path.Combine(repo,"local/checks/segment-capture-live-0.8.23-20261013/session/resume-fixed-0825/summary.json");
string source = JObject.Parse(File.ReadAllText(sourceSummary))["RecordingFile"]!.Value<string>()!;
string output = Path.Combine(repo,"local/checks/full-remediation-0.8.25-20261013/checkpoint-compression");
Directory.CreateDirectory(output);
string before = FileDigest(source);
var info = FullReplayArchive.ReadInfo(source);
const int index = 29;
var clip = FullReplayArchive.ReadPage(source,info,index);
var records = new Dictionary<string,Entry>(StringComparer.Ordinal);
int occurrences = 0;
void Visit(EffectReplayFrame effect,EffectParticleFrame[]? particles,string where)
{
    if(particles == null) return;
    for(int i=0;i<particles.Length;i++)
    {
        var cp = particles[i].Checkpoint; if(cp==null) continue; occurrences++;
        string identity = cp.Digest + "/" + cp.ObservedAt.ToString("R",System.Globalization.CultureInfo.InvariantCulture);
        if(!records.TryGetValue(identity,out var item)) records.Add(identity,item=new Entry(cp));
        item.Modules.Add(effect.Key+" / "+i+" / "+particles[i].Pose.Path);
        item.Keys.Add(effect.Key); item.Resources.Add(effect.Resource); item.Locations.Add(where); item.Occurrences++;
    }
}
foreach(var frame in clip.Frames) foreach(var effect in frame.Effects)
{
    Visit(effect,effect.Particles,"current"); Visit(effect,effect.InitialState?.Particles,"birth");
}
if(records.Count==0) throw new Exception("Selected actual page has no particle checkpoint data.");
var options = new[] { "raw-base64", "raw-deflate-fastest", "raw-deflate-optimal", "particle-byte-columns-fastest", "particle-byte-columns-optimal", "fixed-4-byte-planes-fastest", "fixed-4-byte-planes-optimal" };
var aggregate = new List<object>(); var details = new List<object>();
foreach(string option in options)
{
    var dataJson = new JArray(); long rawBytes=0,innerBytes=0; double compressMs=0,decodeMs=0;
    foreach(var item in records.Values)
    {
        var cp=item.Checkpoint; rawBytes+=cp.Data.Length;
        var watch=Stopwatch.StartNew(); byte[] encoded=Encode(cp.Data,cp.Count,option); watch.Stop(); compressMs+=watch.Elapsed.TotalMilliseconds;
        watch.Restart(); byte[] decoded=Decode(encoded,cp.Data.Length,cp.Count,option); watch.Stop();decodeMs+=watch.Elapsed.TotalMilliseconds;
        if(!decoded.AsSpan().SequenceEqual(cp.Data)) throw new Exception("Non-bitexact transform: "+option);
        innerBytes+=encoded.Length;
        var json=JObject.FromObject(cp); json["Data"]=Convert.ToBase64String(encoded);
        if(option!="raw-base64") json["BenchDataEncoding"]=option;
        dataJson.Add(json);
        details.Add(new {Option=option,cp.Digest,cp.Count,Stride=cp.Count==0?0:cp.Data.Length/cp.Count,RawDataBytes=cp.Data.Length,InnerBytes=encoded.Length,InnerRatio=cp.Data.Length==0?0:(double)encoded.Length/cp.Data.Length,Keys=item.Keys.ToArray(),Modules=item.Modules.ToArray()});
    }
    byte[] utf8=Encoding.UTF8.GetBytes(dataJson.ToString(Formatting.None));
    aggregate.Add(new {Option=option,RawDataBytes=rawBytes,InnerBytes=innerBytes,JsonBytes=utf8.Length,OuterGzipFastestBytes=Compress(utf8,CompressionLevel.Fastest,true).Length,OuterGzipOptimalBytes=Compress(utf8,CompressionLevel.Optimal,true).Length,CompressMs=compressMs,DecodeMs=decodeMs,BitExact=true});
}
var page=info.Pages[index];
var members=new List<(string Name,byte[] Raw,long Stored)> {("page",ReadMember(source,page.Offset,page.Length),page.Length)};
if(page.EffectHistory!=null) members.Add(("effect-history",ReadMember(source,page.EffectHistory.Offset,page.EffectHistory.Length),page.EffectHistory.Length));
var memberResults=new List<object>();
foreach(var member in members) foreach(var option in options)
{
    int replacements=0; long rawData=0,encodedData=0;
    var lines=Encoding.UTF8.GetString(member.Raw).Split('\n',StringSplitOptions.RemoveEmptyEntries);
    for(int i=0;i<lines.Length;i++)
    {
        var token=JToken.Parse(lines[i]);
        foreach(var obj in Objects(token))
        {
            if(obj["Data"]?.Type!=JTokenType.String || obj["State"]?.Type!=JTokenType.String || obj["Digest"]?.Type!=JTokenType.String || obj["Layout"]?.Type!=JTokenType.String || obj["Count"]==null) continue;
            var raw=Convert.FromBase64String(obj["Data"]!.Value<string>()!);int count=obj["Count"]!.Value<int>();
            var encoded=Encode(raw,count,option); if(!Decode(encoded,raw.Length,count,option).AsSpan().SequenceEqual(raw))throw new Exception("Member transform mismatch");
            replacements++;rawData+=raw.Length;encodedData+=encoded.Length;obj["Data"]=Convert.ToBase64String(encoded);
            if(option!="raw-base64")obj["BenchDataEncoding"]=option;
        }
        lines[i]=token.ToString(Formatting.None);
    }
    byte[] rawMember=Encoding.UTF8.GetBytes(string.Join("\n",lines)+"\n");
    memberResults.Add(new {Member=member.Name,Option=option,CheckpointOccurrences=replacements,RawDataBytes=rawData,EncodedDataBytes=encodedData,JsonBytes=rawMember.Length,OriginalStoredBytes=member.Stored,OuterGzipFastestBytes=Compress(rawMember,CompressionLevel.Fastest,true).Length,OuterGzipOptimalBytes=Compress(rawMember,CompressionLevel.Optimal,true).Length,BitExact=true});
}
string after=FileDigest(source); if(before!=after)throw new Exception("Read-only source changed during benchmark.");
var report=new {Source=source,SourceSHA256=before,SourceUnchanged=true,ReaderAssembly=typeof(FullReplayArchive).Assembly.Location,ReaderSHA256=FileDigest(typeof(FullReplayArchive).Assembly.Location),PageHuman=30,PageIndex=index,PageStart=page.Start,PageEnd=page.End,Frames=clip.Frames.Count,CheckpointOccurrences=occurrences,UniqueCheckpoints=records.Count,UniqueKeys=records.Values.SelectMany(x=>x.Keys).Distinct().Count(),UniqueModules=records.Values.SelectMany(x=>x.Modules).Distinct().Count(),TotalNativeParticles=records.Values.Sum(x=>(long)x.Checkpoint.Count),TotalRawDataBytes=records.Values.Sum(x=>(long)x.Checkpoint.Data.Length),TotalRawStateBytes=records.Values.Sum(x=>(long)x.Checkpoint.State.Length),Checkpoints=records.Values.Select(x=>new {x.Checkpoint.Digest,x.Checkpoint.ObservedAt,x.Checkpoint.RootPhase,x.Checkpoint.Layout,x.Checkpoint.Count,DataBytes=x.Checkpoint.Data.Length,StateBytes=x.Checkpoint.State.Length,x.Occurrences,Keys=x.Keys.ToArray(),Modules=x.Modules.ToArray(),Resources=x.Resources.ToArray(),Locations=x.Locations.ToArray()}).ToArray(),Aggregate=aggregate,Members=memberResults,Details=details,Measurement="One actual independently decoded page. Byte transforms and inverse are bitexact. Data-only inner Deflate retains native State unchanged; JSON metadata carries explicit benchmark encoding overhead. Timings are cold diagnostic .NET measurements, not Unity capture benchmarks. Actual page plus separate effect-history members retain original records and only replace inline checkpoint Data. Shared snapshot blocks are read through the frozen archive reader, but not rewritten in member compression results."};
File.WriteAllText(Path.Combine(output,"actual-page-30.json"),JsonConvert.SerializeObject(report,Formatting.Indented));
Console.WriteLine($"ACTUAL page 30: {records.Count} unique checkpoints, {records.Values.SelectMany(x=>x.Keys).Distinct().Count()} effect keys, {records.Values.SelectMany(x=>x.Modules).Distinct().Count()} modules, {records.Values.Sum(x=>(long)x.Checkpoint.Count)} particles, {records.Values.Sum(x=>(long)x.Checkpoint.Data.Length)} raw Data bytes.");
Console.WriteLine(JsonConvert.SerializeObject(new {Aggregate=aggregate,Members=memberResults},Formatting.Indented));

static IEnumerable<JObject> Objects(JToken token)
{ if(token is JObject obj)yield return obj;foreach(var child in token.Children())foreach(var descendant in Objects(child))yield return descendant; }
static byte[] ReadMember(string path,long offset,long length)
{ using var file=File.OpenRead(path);file.Position=offset;byte[] packed=new byte[checked((int)length)];file.ReadExactly(packed);using var input=new MemoryStream(packed);using var gzip=new GZipStream(input,CompressionMode.Decompress);using var output=new MemoryStream();gzip.CopyTo(output);return output.ToArray(); }
static byte[] Encode(byte[] data,int count,string option)
{ if(option=="raw-base64")return data;var input=option.StartsWith("particle-")?Transpose(data,count==0?1:data.Length/count):option.StartsWith("fixed-")?Transpose(data,4):data;return Compress(input,option.EndsWith("fastest")?CompressionLevel.Fastest:CompressionLevel.Optimal,false); }
static byte[] Decode(byte[] data,int length,int count,string option)
{ if(option=="raw-base64")return data;using var input=new MemoryStream(data);using var zip=new DeflateStream(input,CompressionMode.Decompress);using var output=new MemoryStream();zip.CopyTo(output);var raw=output.ToArray();if(raw.Length!=length)throw new Exception("Length mismatch");return option.StartsWith("particle-")?Untranspose(raw,count==0?1:length/count):option.StartsWith("fixed-")?Untranspose(raw,4):raw; }
static byte[] Transpose(byte[] input,int stride)
{ if(input.Length%stride!=0)throw new Exception("Invalid stride");int count=input.Length/stride;var result=new byte[input.Length];for(int i=0;i<count;i++)for(int b=0;b<stride;b++)result[b*count+i]=input[i*stride+b];return result; }
static byte[] Untranspose(byte[] input,int stride)
{ int count=input.Length/stride;var result=new byte[input.Length];for(int i=0;i<count;i++)for(int b=0;b<stride;b++)result[i*stride+b]=input[b*count+i];return result; }
static byte[] Compress(byte[] input,CompressionLevel level,bool gzip)
{ using var result=new MemoryStream();using(Stream zip=gzip?new GZipStream(result,level,true):new DeflateStream(result,level,true))zip.Write(input);return result.ToArray(); }
static string FileDigest(string path)
{ using var input=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(input)); }
sealed class Entry(EffectParticleCheckpoint checkpoint)
{ public readonly EffectParticleCheckpoint Checkpoint=checkpoint;public readonly HashSet<string> Keys=new(StringComparer.Ordinal),Modules=new(StringComparer.Ordinal),Resources=new(StringComparer.Ordinal),Locations=new(StringComparer.Ordinal);public int Occurrences; }
