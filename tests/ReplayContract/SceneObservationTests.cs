using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class SceneObservationTests
{
    internal static void Run(Action<string,Action> test)
    {
        test("scene clock: repeated 60 Hz wrappers interpolate actual 30 Hz observations",()=>
        {
            var a=Frame(0,0);var held=Hold(a,1d/60);var b=Frame(2d/60,2d/60);
            var scene=Index(true,a,held,b);
            Check(scene.ObservationCount==2);Check(scene.TrySample(1.5d/60,out var value));
            Check(value.Left==a && value.Right==b && Math.Abs(value.Mix-.75)<.000001);
        });
        test("scene clock: page opening baseline predates its first global frame",()=>
        {
            var a=Frame(10.025,10);var b=Frame(10.05,10.05);
            Check(Index(false,a,b).TrySample(10.03,out var value));
            Check(value.LeftTime==10 && value.Left==a && Math.Abs(value.Mix-.6)<.000001);
        });
        test("scene clock: unresolved cold page tail waits for a real successor",()=>
        {
            var a=Frame(0,0);var held=Hold(a,.025);var index=Index(false,a,held);
            Check(index.TrySample(0,out _) && !index.TrySample(.01,out _));
            Check(Index(true,a,held).TrySample(.025,out var end) && end.Left==end.Right);
        });
        test("scene clock: map activation and segment edges stay discrete",()=>
        {
            var a=Frame(0,0);var b=Frame(1d/30,1d/30);b.World.Segment=1;
            var scene=Index(true,a,b);Check(scene.TrySample(.02,out var value));
            Check(value.Left==a && value.Right==a && value.Mix==0);
            Check(scene.TrySample(b.SceneSampleTime,out var edge) && edge.Left==b);
            b.World.Segment=0;b.World.ActiveMapObjects=new[]{true};
            Check(Index(true,a,b).TrySample(.02,out value) && value.Left==value.Right);
        });
        test("scene clock: capture stalls do not invent motion across a long interval",()=>
        {
            var a=Frame(0,0);var b=Frame(2,2);
            Check(Index(true,a,b).TrySample(1,out var value) && value.Left==value.Right && value.Left==a);
        });
        test("scene clock: invalid and backwards observations are rejected",()=>
        {
            foreach(double time in new[]{double.NaN,double.PositiveInfinity,1d})
                Reject(()=>Index(true,Frame(0,time)));
            Reject(()=>Index(true,Frame(1,1),Frame(2,.5)));
            var invalid=Frame(0,0);invalid.SceneSampleTimeKnown=false;invalid.SceneSampleTime=.1;
            Reject(()=>ReplayRules.Validate(invalid,-1));
        });
        test("scene clock: held observations allow independent equal baselines and reject changed scene data",()=>
        {
            var a=Frame(0,0);a.Items=new[]{new ItemFrame {Key="item",Pose=new ObjectPose {Position=new[]{1f,2f,3f}}}};
            var copy=Frame(.016,0);copy.Items=new[]{new ItemFrame {Key="item",Pose=new ObjectPose {Position=new[]{1f,2f,3f}}}};
            copy.Actors=new[]{new ActorFrame {Id="player",Position=new[]{2f,3f,4f}}};
            Check(Index(true,a,copy).ObservationCount==1);
            copy.Items[0].Pose.Position[0]=5;Reject(()=>Index(true,a,copy));
            var segment=Hold(a,.016);segment.World=new WorldFrame {Segment=1};Reject(()=>Index(true,a,segment));
            var effects=Hold(a,.016);effects.Effects=new[]{new EffectReplayFrame {Key="new"}};Reject(()=>Index(true,a,effects));
        });
        test("scene clock: old schema has no scene index or different bracket",()=>
        {
            var a=new ReplayFrame { T=0 };var b=new ReplayFrame { T=.05 };
            var scene=Index(true,a,b);Check(scene.ObservationCount==0 && scene.EstimatedIndexBytes==0);
            using var memory=new MemoryReplayTimeline(Clip(a,b));Check(memory.TrySample(.025,out var sample));
            Check(sample.Scene.Left==sample.Left && sample.Scene.Right==sample.Right && sample.Scene.Mix==sample.Mix);
        });
        test("scene clock: memory playback keeps independent player and scene brackets",()=>
        {
            var a=Frame(0,0);var held=Hold(a,1d/60);var b=Frame(2d/60,2d/60);
            using var memory=new MemoryReplayTimeline(Clip(a,held,b));Check(memory.TrySample(1.5d/60,out var sample));
            Check(sample.Left==held && sample.Right==b && Math.Abs(sample.Mix-.5)<.000001);
            Check(sample.Scene.Left==a && sample.Scene.Right==b && Math.Abs(sample.Scene.Mix-.75)<.000001);
            Check(sample.Joints!.EstimatedIndexBytes>=sample.Joints.Scene.EstimatedIndexBytes && sample.Joints.Scene.EstimatedIndexBytes>0);
        });
        test("scene clock: schema22 DOM and streaming preserve held and negative opening clocks",()=>
        {
            var a=Frame(0,-1d/60);var held=Hold(a,1d/60);var b=Frame(2d/60,2d/60);
            var writer=new ReplayDeltaCodec.Writer(22);var tree=new ReplayDeltaCodec.Reader(22);var stream=new ReplayDeltaCodec.StreamReader(22);
            foreach(var original in new[]{a,held,b})
            {
                string json=Json(writer.Encode(original));var x=tree.Decode(JObject.Parse(json));var y=stream.DecodeRecord(json)!;
                Check(x.SceneSampleTimeKnown && y.SceneSampleTimeKnown && x.SceneSampleTime==original.SceneSampleTime && y.SceneSampleTime==original.SceneSampleTime);
            }
        });
        test("scene clock: both readers reject incomplete scalar clocks and old schema fields",()=>
        {
            var token=JObject.FromObject(new ReplayDeltaCodec.Writer(22).Encode(Frame(0,0)));
            foreach(string field in new[]{nameof(ReplayFrame.SceneSampleTime),nameof(ReplayFrame.SceneSampleTimeKnown)})
            {
                var missing=(JObject)token.DeepClone();missing.Remove(field);
                Reject(()=>new ReplayDeltaCodec.Reader(22).Decode(missing));
                Reject(()=>new ReplayDeltaCodec.StreamReader(22).DecodeRecord(Json(missing)));
            }
            Reject(()=>new ReplayDeltaCodec.Reader(21).Decode(token));
            Reject(()=>new ReplayDeltaCodec.StreamReader(21).DecodeRecord(Json(token)));
            Reject(()=>new ReplayDeltaCodec.Writer(21).Encode(Frame(0,0)));
            var legacy=JObject.FromObject(new ReplayDeltaCodec.Writer(21).Encode(new ReplayFrame { T=0 }));
            Check(legacy[nameof(ReplayFrame.SceneSampleTime)]==null && !new ReplayDeltaCodec.StreamReader(21).DecodeRecord(Json(legacy))!.SceneSampleTimeKnown);
        });
        test("scene clock: effect-only projection retains actual observation clocks",()=>
        {
            var frames=new[]{Frame(0,0),Frame(.016,0),Frame(.033,.033)};frames[2].World.Segment=2;
            var writer=new ReplayDeltaCodec.Writer(22);
            string text=Json(new ReplayHeader { Schema=22,Scene="Level_SceneClock" })+"\n"+
                string.Join("\n",frames.Select(f=>Json(writer.Encode(f))))+"\n{\"Type\":\"end\"}";
            using var input=new StringReader(text);var history=ReplayFiles.ReadEffects(input);
            Check(history.Frames.Count==3 && history.Frames[1].SceneSampleTimeKnown && history.Frames[1].SceneSampleTime==0 && history.Frames[2].SceneSampleTime==.033 && history.Frames[2].World.Segment==2);
        });
        test("scene clock: continuous and rolling crop rebase once while sharing scene arrays",()=>
        {
            var a=Frame(10.016,10);var b=Frame(10.05,10.05);
            var rebased=new ContinuousReplayRebaser(10.016).Apply(a);
            Check(rebased.T==0 && Math.Abs(rebased.SceneSampleTime+.016)<.0000001 && ReferenceEquals(rebased.Items,a.Items));
            var old=new ContinuousReplayRebaser(10).Apply(new ReplayFrame{T=11});Check(!old.SceneSampleTimeKnown && old.SceneSampleTime==0);
            var buffer=new RollingBuffer(32L*1024*1024,120);buffer.Add(a);buffer.Add(b);
            var clip=buffer.Snapshot(new ReplayHeader { Scene="Level_SceneClock" },DateTime.UtcNow);
            Check(clip.Frames[0].T==0 && Math.Abs(clip.Frames[0].SceneSampleTime+.016)<.0000001 && ReferenceEquals(clip.Frames[0].Items,a.Items));
            Check(Math.Abs(clip.Frames[1].SceneSampleTime-.034)<.0000001);
        });
        test("scene clock: effect history combines a local baseline and bounded next page",()=>
        {
            var a=Frame(0,0);var tail=Hold(a,.025);var b=Frame(.033,.033);
            var scene=Index(false,a,tail);Check(!scene.TrySample(.02,out _));
            Check(scene.TrySampleWithSuccessor(Index(true,b),.02,out var value));
            Check(value.Left==a && value.Right==b && Math.Abs(value.Mix-(.02/.033))<.000001);
        });
        test("scene clock: far cold seek needs destination and successor only",()=>
        {
            var reads=new List<int>();ReplayClip Page(int page)
            {
                lock(reads)reads.Add(page);
                return Clip(Enumerable.Range(0,61).Select(i=>Frame(page+i/60d,page+(Math.Floor((i+1)/2d)*2-1)/60d)).ToArray());
            }
            using var timeline=new PagedReplayTimeline(new ReplayHeader {Schema=22},5,305,Array.Empty<string>(),
                Enumerable.Range(0,5).Select(p=>new ReplayPageRange(p,p+1)).ToArray(),(p,_)=>Page(p));
            Ready(timeline,.5);lock(reads)reads.Clear();var sample=Ready(timeline,3.995);
            Check(sample.Scene.LeftTime<=3.995 && sample.Scene.RightTime>=3.995 && sample.Scene.Mix>0);
            lock(reads)Check(reads.Contains(3) && reads.Contains(4) && !reads.Any(p=>p<3));
        });
        test("scene clock: schema22 full pages decode independently in reverse order",()=>InRoot(root=>
        {
            var frames=Enumerable.Range(0,181).Select(i=>Frame(100+i/60d,100+Math.Floor(i/2d)*2/60d)).ToArray();
            var writer=new FullReplayWriter(root,new ReplayHeader { Scene="Level_SceneClock" },new ContinuousReplayOptions {SegmentFrameLimit=13});
            foreach(var frame in frames)Check(writer.TryEnqueue(frame));
            var completion=writer.CompleteAsync();Check(completion.Wait(TimeSpan.FromSeconds(20)));var result=completion.GetAwaiter().GetResult();Check(result.Status=="completed",result.Fault??result.Status);
            var info=FullReplayArchive.ReadInfo(result.FilePath);Check(info.Header.Schema==ReplayRules.CurrentSchema && info.Pages.Length>2);
            foreach(var page in info.Pages.AsEnumerable().Reverse())
            {
                var clip=FullReplayArchive.ReadPage(result.FilePath,info,page.Index);
                var effects=FullReplayArchive.ReadEffectHistory(result.FilePath,info,page.Index,default);
                foreach(var frame in clip.Frames)
                {
                    Check(frame.SceneSampleTimeKnown && frame.SceneSampleTime<=frame.T+.000001);
                    int index=(int)Math.Round(frame.T*60);Check(Math.Abs(frame.SceneSampleTime-(frames[index].SceneSampleTime-100))<.0000001);
                    var history=effects.Frames.Single(f=>f.T==frame.T);Check(history.SceneSampleTimeKnown && history.SceneSampleTime==frame.SceneSampleTime);
                }
                var scene=Index(page.Index==info.Pages.Length-1,clip.Frames.ToArray());
                Check(scene.ObservationCount<=clip.Frames.Count && scene.EstimatedIndexBytes>0);
            }
        }));
    }
    private static ReplayFrame Frame(double t,double scene) => new() {T=t,SceneSampleTimeKnown=true,SceneSampleTime=scene};
    private static ReplayFrame Hold(ReplayFrame original,double t) => new() {T=t,SceneSampleTimeKnown=true,SceneSampleTime=original.SceneSampleTime,
        World=original.World,Items=original.Items,Crates=original.Crates,Ropes=original.Ropes,Effects=original.Effects,Audio=original.Audio,Spawned=original.Spawned,Balloons=original.Balloons,Creatures=original.Creatures,Interactions=original.Interactions};
    private static ReplayClip Clip(params ReplayFrame[] frames) {var clip=new ReplayClip {Complete=true};clip.Frames.AddRange(frames);return clip;}
    private static SceneObservationTimeline Index(bool end,params ReplayFrame[] frames) {var builder=new SceneObservationTimeline.Builder();foreach(var frame in frames)builder.Observe(frame);return builder.Build(end);}
    private static string Json(object value)=>JsonConvert.SerializeObject(value,ReplayFiles.Json);
    private static void Check(bool value,string message="Assertion failed") {if(!value)throw new Exception(message);}
    private static void Reject(Action action) {try{action();}catch(Exception e)when(e is InvalidDataException or JsonException or ArgumentOutOfRangeException){return;}throw new Exception("Invalid scene clock was accepted.");}
    private static ReplayTimelineSample Ready(IReplayTimeline timeline,double time)
    {var watch=Stopwatch.StartNew();while(watch.Elapsed<TimeSpan.FromSeconds(10)){if(timeline.TrySample(time,out var sample))return sample;Thread.Sleep(1);}throw new Exception("Scene clock seek timed out: "+timeline.LoadingDiagnostic);}
    private static void InRoot(Action<string> action)
    {string prefix=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"peak-scene-clock-"));string root=prefix+Guid.NewGuid().ToString("N");Directory.CreateDirectory(root);try{action(root);}finally{if(!Path.GetFullPath(root).StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new Exception("Unexpected test path.");Directory.Delete(root,true);}}
}
