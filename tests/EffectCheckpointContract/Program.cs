using PeakReplayLab;
using UnityEngine;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

int passed = 0;
void Check(bool value, string message = "Particle checkpoint contract failed") { if (!value) throw new Exception(message); }
void Test(string name, Action test) { try { test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); Environment.Exit(1); } }
void Reject(Action action) { bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } Check(rejected); }
ParticleSystem Native() => new()
{
    NativeState = new() { Time = 37.5f, Accumulator = .038f, RngA = 0xf1234567, RngB = 917 },
    NativeParticles = [new() { Position = 7.4f, Velocity = 2.2f, InitialVelocity = .7f, RemainingLifetime = 31.2f, RandomSeed = 417, ParentRandomSeed = 938 }],
};
EffectParticleCheckpoint Checkpoint() { var native = Native(); return EffectParticleCheckpointRuntime.Capture(native, EffectParticleCheckpointRuntime.Scratch(native), 52.1, 37.5); }
EffectParticleCheckpoint Copy(EffectParticleCheckpoint source) => new() { ObservedAt = source.ObservedAt, RootPhase = source.RootPhase, Layout = source.Layout, Count = source.Count, State = (byte[])source.State.Clone(), Data = (byte[])source.Data.Clone(), Digest = source.Digest };

Test("40s smoke checkpoint restores all live particles and hidden emission RNG without any simulation", () =>
{
    var source = Native(); var checkpoint = EffectParticleCheckpointRuntime.Capture(source, EffectParticleCheckpointRuntime.Scratch(source), 52.1, 37.5);
    var target = new ParticleSystem(); EffectParticleCheckpointRuntime.Restore(target, checkpoint, EffectParticleCheckpointRuntime.Scratch(target));
    Check(target.NativeState.Time == source.NativeState.Time && target.NativeState.Accumulator == source.NativeState.Accumulator && target.NativeState.RngA == source.NativeState.RngA);
    Check(target.NativeParticles[0].InitialVelocity == .7f && target.NativeParticles[0].ParentRandomSeed == 938 && target.NativeParticles[0].RemainingLifetime == 31.2f);
    Check(source.ParticleReads == 1 && source.StateReads == 1 && target.Pauses == 1);
});
Test("native structs are accepted only under the matching Unity module and field layout fingerprint", () =>
{
    var checkpoint = Copy(Checkpoint()); checkpoint.Layout += "different-module"; checkpoint.Digest = EffectParticleCheckpointRules.Digest(checkpoint);
    Reject(() => EffectParticleCheckpointRuntime.Restore(new(), checkpoint, new ParticleSystem.Particle[32]));
});
Test("tampered playback state and particle payload cannot enter native setters", () =>
{
    var checkpoint = Copy(Checkpoint()); checkpoint.Data[0] ^= 1;
    Reject(() => EffectParticleCheckpointRuntime.Restore(new(), checkpoint, new ParticleSystem.Particle[32]));
    checkpoint = Copy(Checkpoint()); checkpoint.State[0] ^= 1;
    Reject(() => EffectParticleCheckpointRuntime.Restore(new(), checkpoint, new ParticleSystem.Particle[32]));
});
Test("valid checksums cannot disguise malformed native sizes or nonfinite float fields", () =>
{
    var checkpoint = Copy(Checkpoint()); checkpoint.State = new byte[3]; checkpoint.Digest = EffectParticleCheckpointRules.Digest(checkpoint);
    Reject(() => EffectParticleCheckpointRuntime.Validate(checkpoint));
    checkpoint = Copy(Checkpoint()); BitConverter.GetBytes(float.NaN).CopyTo(checkpoint.Data, 0); checkpoint.Digest = EffectParticleCheckpointRules.Digest(checkpoint);
    Reject(() => EffectParticleCheckpointRuntime.Validate(checkpoint));
});
Test("particle counts and native restore scratch capacity remain bounded", () =>
{
    var checkpoint = Copy(Checkpoint()); checkpoint.Count = EffectParticleCheckpointRules.MaximumParticles + 1;
    Reject(() => EffectParticleCheckpointRules.Validate(checkpoint));
    Reject(() => EffectParticleCheckpointRuntime.Restore(new(), Checkpoint(), Array.Empty<ParticleSystem.Particle>()));
    var target = new ParticleSystem(); target.main.maxParticles = 0;
    Reject(() => EffectParticleCheckpointRuntime.Restore(target, Checkpoint(), new ParticleSystem.Particle[32]));
});
Test("zero live particles still retain native emission phase and seed state", () =>
{
    var source = Native(); source.NativeParticles = []; var checkpoint = EffectParticleCheckpointRuntime.Capture(source, EffectParticleCheckpointRuntime.Scratch(source), 53, 39);
    var target = new ParticleSystem(); EffectParticleCheckpointRuntime.Restore(target, checkpoint, EffectParticleCheckpointRuntime.Scratch(target));
    Check(checkpoint.Count == 0 && checkpoint.Data.Length == 0 && target.NativeParticles.Length == 0 && target.NativeState.RngB == source.NativeState.RngB);
});
Test("rebase changes observation time while preserving root phase, payload digest and immutable sharing", () =>
{
    var checkpoint = Checkpoint(); var frame = new EffectParticleFrame(new(), [], []) { Checkpoint = checkpoint };
    var mapper = new EffectParticleCheckpointRebaser(52); var source = new[] { frame };
    var first = mapper.Apply(source)!; var second = mapper.Apply(source)!;
    Check(ReferenceEquals(first, second) && !ReferenceEquals(first, source) && checkpoint.ObservedAt == 52.1);
    var shifted = first[0].Checkpoint!;
    Check(Math.Abs(shifted.ObservedAt - .1) < .000001 && shifted.RootPhase == checkpoint.RootPhase);
    Check(ReferenceEquals(shifted.Data, checkpoint.Data) && shifted.Digest == checkpoint.Digest);
    EffectParticleCheckpointRules.Validate(shifted);
});
Test("accounting includes native bytes and layout rather than only an object reference", () =>
{
    var checkpoint = Checkpoint(); Check(EffectParticleCheckpointRules.Estimate(checkpoint) >= checkpoint.State.Length + checkpoint.Data.Length + checkpoint.Layout.Length * 2 + 256);
});
Test("short explosions avoid particle reads and unsupported custom/trail state cannot advertise checkpoint completeness", () =>
{
    var source = Native(); source.main.startLifetime.constant = 1.2f;
    Check(!EffectParticleCheckpointRuntime.NeedsCheckpoint([source]) && source.ParticleReads == 0 && source.StateReads == 0);
    source.main.startLifetime.constant = 40;
    Check(EffectParticleCheckpointRuntime.NeedsCheckpoint([source]) && EffectParticleCheckpointRuntime.CanCheckpoint([source]));
    source.trails.enabled = true; Check(!EffectParticleCheckpointRuntime.CanCheckpoint([source]));
    source.trails.enabled = false; source.customData.Mode = ParticleSystemCustomDataMode.Vector; Check(!EffectParticleCheckpointRuntime.CanCheckpoint([source]));
});
Test("sparse checkpoint schedule deduplicates pause, supports boundaries and never catches up missed slots", () =>
{
    var schedule = new EffectParticleCheckpointSchedule(); Check(schedule.Due(0));
    Check(!schedule.Due(0, true) && !schedule.Due(.1) && schedule.Due(.3, true) && !schedule.Due(.3, true));
    Check(schedule.Due(20) && !schedule.Due(20) && !schedule.Due(20.1)); Check(schedule.Due(5));
});
Test("origin rejection cancels all unpublished runs for the exact source and leaves other sources", () =>
{
    var buffer = new EffectCaptureEventBuffer<Snapshot>(32);
    foreach (string key in new[] { "event-particle:12:1:1", "event-particle:12:2:1", "event-particle:123:3:1", "event-mesh:12:4:1" }) buffer.Save(key, new(key));
    Check(buffer.DiscardSource("particle:12") == 2);
    var frames = new List<Snapshot>(); buffer.AppendMissing(frames, frame => frame.Key);
    Check(frames.Count == 2 && frames.All(frame => !frame.Key.StartsWith("event-particle:12:")));
});
Test("ordinary old-root end retains short effects while origin cancellation never mutates delivered history", () =>
{
    var buffer = new EffectCaptureEventBuffer<Snapshot>(32); var old = new Snapshot("event-particle:12:1:1");
    buffer.Save(old.Key, old); buffer.End(old.Key, .012); var published = new List<Snapshot>(); buffer.AppendMissing(published, frame => frame.Key);
    Check(published.Count == 1 && ReferenceEquals(published[0], old));
    buffer.Save("event-particle:12:2:1", new("event-particle:12:2:1")); Check(buffer.DiscardSource("particle:12") == 1);
    Check(published.Count == 1 && ReferenceEquals(published[0], old));
});
EffectParticleFrame Module(double time,double phase,string path="")
{
    var cp=Checkpoint(); cp.ObservedAt=time; cp.RootPhase=phase; cp.Digest=EffectParticleCheckpointRules.Digest(cp);
    return new(new NodePose{Path=path},[],[]){Checkpoint=cp};
}
Test("playback restores only a complete same-time same-phase native path group",()=>
{
    var modules=new[]{Module(50,37.5),Module(50,37.5,"0/2")}; var paths=new[]{"","0/2"};
    Check(EffectCheckpointPlaybackPlan.TryCreate(modules,paths,50.8,38.3,out var plan) && plan!.Phase==37.5 && plan.ObservedAt==50);
    modules[1].Checkpoint=null; Check(!EffectCheckpointPlaybackPlan.TryCreate(modules,paths,50.8,38.3,out _));
    modules[1]=Module(50.1,37.5,"0/2"); Check(!EffectCheckpointPlaybackPlan.TryCreate(modules,paths,50.8,38.3,out _));
    modules[1]=Module(50,37.6,"0/2"); Check(!EffectCheckpointPlaybackPlan.TryCreate(modules,paths,50.8,38.3,out _));
    modules[1]=Module(50,37.5,"0/3"); Check(!EffectCheckpointPlaybackPlan.TryCreate(modules,paths,50.8,38.3,out _));
});
Test("recorded checkpoint tail is bounded to one second and never selects future time or future phase",()=>
{
    var modules=new[]{Module(50,150)}; var paths=new[]{""};
    Check(EffectCheckpointPlaybackPlan.TryCreate(modules,paths,50.9,150.9,out _));
    Check(!EffectCheckpointPlaybackPlan.TryCreate(modules,paths,51.1,151.1,out _));
    Check(!EffectCheckpointPlaybackPlan.TryCreate(modules,paths,49.99,150,out _));
    Check(!EffectCheckpointPlaybackPlan.TryCreate(modules,paths,50.5,149.99,out _));
    Check(EffectCheckpointPlaybackPlan.TryCreate(modules,paths,80,150,out _),"a paused exact phase needs no simulation tail");
});
Test("native binding path proof uses sibling indices and rejects emitters outside the root",()=>
{
    var root=new Transform(); var child=new Transform{parent=root,index=2}; var leaf=new Transform{parent=child,index=7};
    Check(EffectParticleCheckpointRuntime.Path(root,root)=="" && EffectParticleCheckpointRuntime.Path(leaf,root)=="2/7");
    bool failed=false; try{EffectParticleCheckpointRuntime.Path(new(),root);}catch(InvalidOperationException){failed=true;} Check(failed);
});
Test("legacy complete preroll admits 37-second smoke through 120 phase and never silently truncates to twelve",()=>
{
    Check(EffectCheckpointPlaybackPlan.LegacySupported(37.5) && EffectCheckpointPlaybackPlan.LegacySupported(120));
    Check(!EffectCheckpointPlaybackPlan.LegacySupported(120.001) && !EffectCheckpointPlaybackPlan.LegacySupported(double.NaN));
});
Test("completed warm cache stays bounded per replica and globally, respects exact past time, run identity and release",()=>
{
    var cache=new EffectParticleWarmCache(8000,4); var owner=new object();
    EffectReplayFrame Frame(int time)=>new(){Key="run:1",Resource="smoke",Seed=7,Seeds=new long[]{7},Particles=new[]{Module(time,time)}};
    for(int i=1;i<=8;i++)cache.Add(owner,Frame(i));
    Check(cache.Count==4 && cache.Bytes<=8000);
    var current=Frame(9); Check(cache.Find(owner,current,new[]{""},7.999,7.999)!.Particles![0].Checkpoint!.ObservedAt==7);
    Check(cache.Find(owner,current,new[]{""},4,4)==null && cache.Find(new object(),current,new[]{""},9,9)==null);
    current.Seed=8; Check(cache.Find(owner,current,new[]{""},9,9)==null); current.Seed=7; current.Key="other-run";
    Check(cache.Find(owner,current,new[]{""},9,9)==null);
    for(int i=0;i<100;i++)cache.Add(new object(),Frame(i)); Check(cache.Bytes<=8000);
    cache.Clear(); Check(cache.Count==0 && cache.Bytes==0);
    cache.Add(owner,Frame(2)); cache.RemoveOwner(owner); Check(cache.Count==0 && cache.Bytes==0);
});
Test("worker particle codec transposes native byte columns losslessly and keeps legacy plain base64", () =>
{
    var bytes = Enumerable.Range(0,256*132).Select(i=>(byte)((i%132)%9==0?i/132:0)).ToArray();
    string text=EffectParticleCheckpointCodec.Encode(bytes,256); Check(text.StartsWith("PS23D1:")&&text.Length<Convert.ToBase64String(bytes).Length/2);
    Check(EffectParticleCheckpointCodec.Decode(text).SequenceEqual(bytes));
    Check(EffectParticleCheckpointCodec.Decode(Convert.ToBase64String(bytes)).SequenceEqual(bytes));
    Check(EffectParticleCheckpointCodec.Encode([],0)=="" && EffectParticleCheckpointCodec.Decode("").Length==0);
    Check(ReferenceEquals(text,EffectParticleCheckpointCodec.Encode(bytes,256)),"immutable payload compression is cached only on the worker codec");
});
Test("worker checkpoint codec bounds allocation count stride and exact decompressed length",()=>
{
    string good=EffectParticleCheckpointCodec.Encode(new byte[256*132],256);
    byte[] Envelope()=>Convert.FromBase64String(good[7..]);
    void Int(byte[] b,int at,int value)=>BitConverter.GetBytes(value).CopyTo(b,at);
    string Wire(byte[] b)=>"PS23D1:"+Convert.ToBase64String(b);
    var b=Envelope();Int(b,0,EffectParticleCheckpointRules.MaximumDataBytes+1);Reject(()=>EffectParticleCheckpointCodec.Decode(Wire(b)));
    b=Envelope();Int(b,4,257);Reject(()=>EffectParticleCheckpointCodec.Decode(Wire(b)));
    b=Envelope();Int(b,8,EffectParticleCheckpointRules.MaximumParticles+1);Reject(()=>EffectParticleCheckpointCodec.Decode(Wire(b)));
    b=Envelope();Int(b,0,257*132);Int(b,8,257);Reject(()=>EffectParticleCheckpointCodec.Decode(Wire(b)));
    b=Envelope();Int(b,0,255*132);Int(b,8,255);Reject(()=>EffectParticleCheckpointCodec.Decode(Wire(b)));
    b=Envelope();Array.Resize(ref b,b.Length-1);Reject(()=>EffectParticleCheckpointCodec.Decode(Wire(b)));
    Reject(()=>EffectParticleCheckpointCodec.Decode("PS23D1:!"));
});
Test("chunked worker decompressor rejects trailing 1 7 8 9 and 8192 compressed bytes",()=>
{
    string good=EffectParticleCheckpointCodec.Encode(new byte[256*132],256);
    foreach(int tail in new[]{1,7,8,9,8192})
    {
        var b=Convert.FromBase64String(good[7..]);Array.Resize(ref b,b.Length+tail);
        BitConverter.GetBytes(b.Length-48).CopyTo(b,12);
        using(var hash=System.Security.Cryptography.SHA256.Create())hash.ComputeHash(b,48,b.Length-48).CopyTo(b,16);
        Reject(()=>EffectParticleCheckpointCodec.Decode("PS23D1:"+Convert.ToBase64String(b)));
    }
});
Test("checkpoint JSON converter compresses only Data and retains raw native bytes and digest",()=>
{
    var cp=Checkpoint();cp.Count=256;cp.Data=new byte[256*132];cp.Digest=EffectParticleCheckpointRules.Digest(cp);
    string text=JsonConvert.SerializeObject(cp);Check(text.Contains("PS23D1:")&&text.Contains(Convert.ToBase64String(cp.State)));
    var restored=JsonConvert.DeserializeObject<EffectParticleCheckpoint>(text)!;
    Check(restored.Count==cp.Count&&restored.Digest==cp.Digest&&restored.Data.SequenceEqual(cp.Data)&&restored.State.SequenceEqual(cp.State));
    Check(JObject.FromObject(cp).ToObject<EffectParticleCheckpoint>()!.Data.SequenceEqual(cp.Data));
    var legacy=JObject.Parse(text);legacy["Data"]=Convert.ToBase64String(cp.Data);
    Check(JsonConvert.DeserializeObject<EffectParticleCheckpoint>(legacy.ToString())!.Data.SequenceEqual(cp.Data));
});
Test("checkpoint JSON converter rejects missing duplicate wrong-type and corrupted payload fields",()=>
{
    var cp=Checkpoint();string text=JsonConvert.SerializeObject(cp);
    foreach(string field in new[]{"Count","State","Data","Digest"})
    {var json=JObject.Parse(text);json.Remove(field);Reject(()=>JsonConvert.DeserializeObject<EffectParticleCheckpoint>(json.ToString()));}
    var bad=JObject.Parse(text);bad["Data"]=new JArray(1,2);Reject(()=>JsonConvert.DeserializeObject<EffectParticleCheckpoint>(bad.ToString()));
    bad=JObject.Parse(text);bad["Digest"]=new string('0',64);Reject(()=>JsonConvert.DeserializeObject<EffectParticleCheckpoint>(bad.ToString()));
    bool duplicate=false;try{JsonConvert.DeserializeObject<EffectParticleCheckpoint>(text.Replace("\"Count\":","\"Count\":1,\"Count\":"));}catch(JsonException){duplicate=true;}Check(duplicate);
});
Test("incompressible checkpoint data remains plain base64 without payload inflation",()=>
{
    var bytes=new byte[1024];new Random(4181).NextBytes(bytes);string text=EffectParticleCheckpointCodec.Encode(bytes,8);
    Check(!text.StartsWith("PS23D1:")&&text==Convert.ToBase64String(bytes));
});
Console.WriteLine($"TOTAL: {passed} actual native checkpoint and origin contracts passed. Unity particle integration is not simulated.");
internal sealed record Snapshot(string Key);
