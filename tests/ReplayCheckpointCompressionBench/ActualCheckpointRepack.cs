using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class ActualCheckpointRepack
{
    internal static void DiagnoseFirst(string repo)
    {
        string summary=Path.Combine(repo,"local/checks/segment-capture-live-0.8.23-20261013/session/resume-fixed-0825/summary.json");
        string source=JObject.Parse(File.ReadAllText(summary))["RecordingFile"]!.Value<string>()!;
        string output=Directory.GetDirectories(Path.Combine(repo,"local/checks/full-remediation-0.8.25-20261013/checkpoint-compression"),"repack-*").OrderByDescending(x=>x,StringComparer.Ordinal).First();
        string target=Directory.GetFiles(Path.Combine(output,"Recordings"),"*.peakrun").Single();
        var left=FullReplayArchive.ReadPage(source,FullReplayArchive.ReadInfo(source),0).Frames[0];
        var right=FullReplayArchive.ReadPage(target,FullReplayArchive.ReadInfo(target),0).Frames[0];
        int found=0;var aHash=new ObjectHashes();var bHash=new ObjectHashes();
        void Diff(object? a,object? b,string path)
        {
            if(found>=20||aHash.Of(a).AsSpan().SequenceEqual(bHash.Of(b)))return;
            if(a==null||b==null||a.GetType()!=b.GetType()||a.GetType().IsValueType||a is string)
            {found++;Console.WriteLine(path+": "+JsonConvert.SerializeObject(a)+" => "+JsonConvert.SerializeObject(b));return;}
            if(a is Array aa && b is Array bb)
            {if(aa.Length!=bb.Length){found++;Console.WriteLine(path+".Length: "+aa.Length+" => "+bb.Length);return;}for(int i=0;i<aa.Length;i++)Diff(aa.GetValue(i),bb.GetValue(i),path+"["+i+"]");return;}
            foreach(var member in ObjectHashes.Shape(a.GetType())) Diff(member is PropertyInfo p?p.GetValue(a):((FieldInfo)member).GetValue(a),member is PropertyInfo q?q.GetValue(b):((FieldInfo)member).GetValue(b),path+"."+member.Name);
        }
        Diff(left,right,"Frame");Console.WriteLine("Differences: "+found);
    }
    internal static async Task Run(string repo,bool verifyExisting=false)
    {
        string sourceSummary=Path.Combine(repo,"local/checks/segment-capture-live-0.8.23-20261013/session/resume-fixed-0825/summary.json");
        string source=JObject.Parse(File.ReadAllText(sourceSummary))["RecordingFile"]!.Value<string>()!;
        string outputBase=Path.Combine(repo,"local/checks/full-remediation-0.8.25-20261013/checkpoint-compression");
        string output=verifyExisting?Directory.GetDirectories(outputBase,"repack-*").OrderByDescending(x=>x,StringComparer.Ordinal).First():Path.Combine(outputBase,"repack-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        string before=DigestFile(source); var info=FullReplayArchive.ReadInfo(source);
        var expected=new List<string>();var expectedStrict=new List<string>();var expectedJoints=new List<JointSnapshot[]>(); var expectedCps=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        var writer=verifyExisting?null:new FullReplayWriter(Path.Combine(output,"Recordings"),info.Header);
        double previous=-1; long droppedOverlap=0; int maximumQueue=0;
        var watch=System.Diagnostics.Stopwatch.StartNew();
        try
        {
            for(int p=0;p<info.Pages.Length;p++)
            {
                var page=FullReplayArchive.ReadPage(source,info,p);var hash=new ObjectHashes(ignoreActorJointRotations:true);var strictHash=new ObjectHashes();
                foreach(var frame in page.Frames)
                {
                    if(frame.T<=previous) { droppedOverlap++;continue; }
                    if(writer?.Fault!=null)throw writer.Fault;
                    while(writer?.Stats.QueuedFrames>=60)
                    { if(writer.Fault!=null)throw writer.Fault;await Task.Delay(5); }
                    expected.Add(Convert.ToHexString(hash.Of(frame)));expectedStrict.Add(Convert.ToHexString(strictHash.Of(frame)));
                    expectedJoints.Add(frame.Actors.SelectMany(actor=>actor.JointPose).Select(joint=>new JointSnapshot(joint.Path,joint.SampleTime,joint.Rotation)).ToArray());
                    CollectCheckpoints(frame,expectedCps);
                    if(writer!=null&&!writer.TryEnqueue(frame))throw writer.Fault??new Exception("Actual frame rejected by worker writer.");
                    maximumQueue=Math.Max(maximumQueue,writer?.Stats.QueuedFrames??0);previous=frame.T;
                }
                Console.WriteLine($"REPACK source page {p+1}/{info.Pages.Length}, frames={expected.Count}.");
            }
        }
        catch(Exception error) { if(writer!=null)await writer.InterruptAsync("checkpoint-compression-repack-failed",error);throw; }
        var result=writer==null?null:await writer.CompleteAsync(info.Reason);long repackMs=watch.ElapsedMilliseconds;
        if(result!=null&&(result.Status!="completed"||result.UnwrittenFrames!=0||result.WrittenFrames!=expected.Count))throw new Exception("Repacked archive was not fully completed: "+JsonConvert.SerializeObject(result));
        string outputFile=result?.FilePath??Directory.GetFiles(Path.Combine(output,"Recordings"),"*.peakrun").Single();
        var actual=FullReplayArchive.ReadInfo(outputFile);var actualCps=new Dictionary<string,byte[]>(StringComparer.Ordinal);int verified=0,strictlyChangedFrames=0;double maximumJointAngleDegrees=0;previous=-1;
        for(int p=0;p<actual.Pages.Length;p++)
        {
            var page=FullReplayArchive.ReadPage(outputFile,actual,p);var hash=new ObjectHashes(ignoreActorJointRotations:true);var strictHash=new ObjectHashes();
            foreach(var frame in page.Frames)
            {
                if(frame.T<=previous)continue;
                string got=Convert.ToHexString(hash.Of(frame));if(verified>=expected.Count||got!=expected[verified])throw new Exception($"Decoded frame semantic/hash mismatch at {verified} T={frame.T:R}.");
                if(Convert.ToHexString(strictHash.Of(frame))!=expectedStrict[verified])strictlyChangedFrames++;
                var joints=frame.Actors.SelectMany(actor=>actor.JointPose).ToArray();var prior=expectedJoints[verified];
                if(joints.Length!=prior.Length)throw new Exception("Actor joint count mismatch.");
                for(int j=0;j<joints.Length;j++)
                {
                    if(joints[j].Path!=prior[j].Path||joints[j].SampleTime!=prior[j].SampleTime)throw new Exception("Actor joint path/time mismatch.");
                    var a=prior[j].Rotation;var b=joints[j].Rotation;
                    if(prior[j].Path=="."&&!a.AsSpan().SequenceEqual(b))throw new Exception("Exact world-root joint rotation changed.");
                    double dot=0,aa=0,bb=0;for(int k=0;k<4;k++){dot+=(double)a[k]*b[k];aa+=(double)a[k]*a[k];bb+=(double)b[k]*b[k];}
                    double angle=2*Math.Acos(Math.Min(1,Math.Abs(dot)/Math.Sqrt(aa*bb)))*180/Math.PI;
                    maximumJointAngleDegrees=Math.Max(maximumJointAngleDegrees,angle);if(angle>=.02)throw new Exception($"Local actor joint rotation exceeded existing codec .02 degree tolerance: {angle:R}.");
                }
                CollectCheckpoints(frame,actualCps);verified++;previous=frame.T;
            }
            Console.WriteLine($"VERIFY output page {p+1}/{actual.Pages.Length}, frames={verified}.");
        }
        if(verified!=expected.Count||actualCps.Count!=expectedCps.Count)throw new Exception("Frame/checkpoint count mismatch.");
        foreach(var cp in expectedCps)if(!actualCps.TryGetValue(cp.Key,out var value)||!value.AsSpan().SequenceEqual(cp.Value))throw new Exception("Native checkpoint State/Data/Digest mismatch: "+cp.Key);
        string after=DigestFile(source);if(before!=after)throw new Exception("Read-only source changed.");
        long sourceBytes=new FileInfo(source).Length,outputBytes=new FileInfo(outputFile).Length;
        var report=new {Source=source,SourceSHA256=before,SourceUnchanged=true,Output=outputFile,OutputSHA256=DigestFile(outputFile),ReaderWriterAssembly=typeof(FullReplayArchive).Assembly.Location,ReaderWriterSHA256=DigestFile(typeof(FullReplayArchive).Assembly.Location),SourceBytes=sourceBytes,OutputBytes=outputBytes,Ratio=(double)outputBytes/sourceBytes,SavedPercent=100d*(1-(double)outputBytes/sourceBytes),Frames=verified,DroppedPageOverlap=droppedOverlap,UniqueCheckpoints=expectedCps.Count,EveryFrameExceptActorLocalJointRotationValueHashEqual=true,ActorJointPathSampleTimeAndWorldRootRotationExact=true,StrictScalarHashChangedFrames=strictlyChangedFrames,MaximumActorLocalJointRotationErrorDegrees=maximumJointAngleDegrees,ActorLocalJointRotationErrorLimitDegrees=.02,EveryCheckpointStateDataDigestBitExact=true,SourceDuration=info.Duration,OutputDuration=actual.Duration,SourcePages=info.Pages.Length,OutputPages=actual.Pages.Length,MaximumQueuedFrames=maximumQueue,RepackMilliseconds=repackMs,TotalMilliseconds=watch.ElapsedMilliseconds,Result=result,Scope="Pure .NET production worker writer; identical decoded actual .25 input frames, no resampling/cadence changes, duplicate internal page overlap removed once. All model values except local actor joint rotations are recursively hashed exactly; raw CPF State/Data/Digest additionally checked for every unique native checkpoint. The existing lossy ActorJointCodec requantizes local quaternions (including q/-q equivalent signs) on a second encode; its measured angular error must stay below the existing .02 degree contract, while joint path/time and world-root quaternion remain exact. Original is read-only; generated archive lives in a fresh isolated directory. This isolates codec/container change, not game main-thread capture performance."};
        File.WriteAllText(Path.Combine(output,"repack-result.json"),JsonConvert.SerializeObject(report,Formatting.Indented));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(output)!,"latest-repack-result.json"),JsonConvert.SerializeObject(report,Formatting.Indented));
        Console.WriteLine(JsonConvert.SerializeObject(report,Formatting.Indented));
    }
    private static void CollectCheckpoints(ReplayFrame frame,Dictionary<string,byte[]> target)
    {
        void Add(EffectParticleFrame[]? particles)
        {
            if(particles==null)return;
            foreach(var part in particles)
            {
                var cp=part.Checkpoint;if(cp==null)continue;
                string key=cp.Digest+"/"+cp.ObservedAt.ToString("R",System.Globalization.CultureInfo.InvariantCulture);
                if(target.ContainsKey(key))continue;
                using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                hash.AppendData(cp.State);hash.AppendData(cp.Data);hash.AppendData(Encoding.UTF8.GetBytes(cp.Digest));
                hash.AppendData(BitConverter.GetBytes(cp.Count));hash.AppendData(BitConverter.GetBytes(cp.RootPhase));
                hash.AppendData(Encoding.UTF8.GetBytes(cp.Layout));target.Add(key,hash.GetHashAndReset());
            }
        }
        foreach(var effect in frame.Effects){Add(effect.Particles);Add(effect.InitialState?.Particles);}
    }
    private static string DigestFile(string path){using var input=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(input));}
    private sealed record JointSnapshot(string Path,double SampleTime,float[] Rotation);
    private sealed class ObjectHashes(bool ignoreActorJointRotations=false)
    {
        private readonly ConditionalWeakTable<object,Box> cache=new();
        private static readonly ConcurrentDictionary<Type,MemberInfo[]> shapes=new();
        private sealed class Box(byte[] data){internal readonly byte[] Data=data;}
        internal byte[] Of(object? value)
        {
            if(value==null)return SHA256.HashData(new byte[]{0});
            var type=value.GetType();
            if(type.IsValueType||value is string)return SHA256.HashData(Encoding.UTF8.GetBytes(type.FullName+":"+JsonConvert.SerializeObject(value)));
            return cache.GetValue(value,Compute).Data;
        }
        private Box Compute(object value)
        {
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var type=value.GetType();hash.AppendData(Encoding.UTF8.GetBytes(type.FullName!));
            if(value is byte[] bytes)hash.AppendData(bytes);
            else if(value is Array array){hash.AppendData(BitConverter.GetBytes(array.Length));foreach(var item in array)hash.AppendData(Of(item));}
            else foreach(var member in shapes.GetOrAdd(type,Shape))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(member.Name));
                if(ignoreActorJointRotations&&value is ActorFrame actor&&member.Name==nameof(ActorFrame.JointPose))
                {
                    hash.AppendData(BitConverter.GetBytes(actor.JointPose.Length));
                    foreach(var joint in actor.JointPose)foreach(var jointMember in Shape(typeof(NodePose)))
                    {if(jointMember.Name==nameof(NodePose.Rotation))continue;hash.AppendData(Encoding.UTF8.GetBytes(jointMember.Name));hash.AppendData(Of(jointMember is PropertyInfo jointProperty?jointProperty.GetValue(joint):((FieldInfo)jointMember).GetValue(joint)));}
                }
                else hash.AppendData(Of(member is PropertyInfo property?property.GetValue(value):((FieldInfo)member).GetValue(value)));
            }
            return new Box(hash.GetHashAndReset());
        }
        internal static MemberInfo[] Shape(Type type)=>type.GetProperties(BindingFlags.Public|BindingFlags.Instance)
            .Where(p=>p.CanRead&&p.GetIndexParameters().Length==0&&p.GetCustomAttribute<JsonIgnoreAttribute>()==null).Cast<MemberInfo>()
            .Concat(type.GetFields(BindingFlags.Public|BindingFlags.Instance).Where(f=>f.GetCustomAttribute<JsonIgnoreAttribute>()==null))
            .OrderBy(m=>m.Name,StringComparer.Ordinal).ToArray();
    }
}
