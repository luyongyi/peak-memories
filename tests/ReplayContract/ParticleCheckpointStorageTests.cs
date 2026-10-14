using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class ParticleCheckpointStorageTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("compressed particle checkpoint wire stays lossless in full DTO delta dictionary tree and stream paths", () =>
        {
            var checkpoint = Checkpoint(); checkpoint.Count = 256;
            checkpoint.Data = Enumerable.Range(0,256*132).Select(i=>(byte)(i%132==0?i/132:0)).ToArray();
            checkpoint.Digest = EffectParticleCheckpointRules.Digest(checkpoint);
            var token = JObject.FromObject(new ReplayDeltaCodec.Writer().Encode(Frame(10,checkpoint)));
            Check(token["Effects"]![0]!["Particles"]![0]!["Checkpoint"]!["Data"]!.Value<string>()!.StartsWith("PS23D1:",StringComparison.Ordinal));
            Both(token,frame=>Check(frame.Effects[0].Particles![0].Checkpoint!.Data.SequenceEqual(checkpoint.Data)));
            var patch = typeof(ReplayDeltaCodec).GetMethod("Patch",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null,new object?[]{checkpoint,null,null,23});
            var encoded=JObject.FromObject(patch!);Check(encoded["Data"]!.Value<string>()!.StartsWith("PS23D1:",StringComparison.Ordinal));
            token["Effects"]![0]!["Particles"]![0]!["Checkpoint"]=encoded;
            Both(token,frame=>Check(frame.Effects[0].Particles![0].Checkpoint!.Digest==checkpoint.Digest));
            encoded["Data"]=Convert.ToBase64String(checkpoint.Data);
            Both(token,frame=>Check(frame.Effects[0].Particles![0].Checkpoint!.Data.SequenceEqual(checkpoint.Data)));
        });
        test("particle checkpoint tree and streaming storage retain bytes and reject corrupt payloads", () =>
        {
            var source = Frame(10, Checkpoint());
            var token = JObject.FromObject(new ReplayDeltaCodec.Writer().Encode(source));
            Both(token, value => Check(value.Effects[0].Particles![0].Checkpoint!.Data.SequenceEqual(new byte[] { 5, 6, 7, 8 })));
            foreach (string field in new[] { "Digest", "Count", "State", "Data", "Layout" })
            {
                var bad = (JObject)token.DeepClone(); var checkpoint = (JObject)bad["Effects"]![0]!["Particles"]![0]!["Checkpoint"]!;
                checkpoint[field] = field switch { "Digest" => new string('0', 64), "Count" => -1, "Layout" => "", _ => "!!" };
                RejectBoth(bad);
            }
            var oversized = (JObject)token.DeepClone(); oversized["Effects"]![0]!["Particles"]![0]!["Checkpoint"]!["State"] = Convert.ToBase64String(new byte[4097]);
            RejectBoth(oversized);
        });
        test("particle checkpoint cannot enter older schema but legacy particles still round trip", () =>
        {
            foreach (int schema in Enumerable.Range(15, 8))
            {
                Reject(() => new ReplayDeltaCodec.Writer(schema).Encode(Frame(10, Checkpoint())));
                var frame = Frame(10, null); var text = Json(new ReplayDeltaCodec.Writer(schema).Encode(frame));
                Check(new ReplayDeltaCodec.Reader(schema).Decode(JObject.Parse(text)).Effects[0].Particles![0].Checkpoint == null);
                Check(new ReplayDeltaCodec.StreamReader(schema).DecodeRecord(text)!.Effects[0].Particles![0].Checkpoint == null);
            }
        });
        test("particle checkpoint crop shifts only replay observation clock and shares native payload", () =>
        {
            var original = Checkpoint(); var rebaser = new EffectParticleCheckpointRebaser(9);
            var particles = Frame(10, original).Effects[0].Particles!;
            var shifted = rebaser.Apply(particles)!;
            Check(ReferenceEquals(shifted, rebaser.Apply(particles)) && shifted[0].Checkpoint!.ObservedAt == .5);
            var value = shifted[0].Checkpoint!;
            Check(value.RootPhase == original.RootPhase && ReferenceEquals(value.State, original.State) && ReferenceEquals(value.Data, original.Data) && value.Digest == original.Digest);
            EffectParticleCheckpointRules.Validate(value);
        });
        test("particle checkpoint cannot observe the future in current or initial effect state", () =>
        {
            foreach (bool initial in new[] { false, true })
            {
                var checkpoint = Checkpoint(); checkpoint.ObservedAt = 10.1;
                var source = Frame(10, initial ? null : checkpoint);
                if (initial) source.Effects[0].InitialState = new EffectReplayInitialState
                { ObservedAt = 9.5, UntilTime = 9.6, Seeds = source.Effects[0].Seeds, Particles = Frame(10, checkpoint).Effects[0].Particles };
                var token = JObject.FromObject(new ReplayDeltaCodec.Writer().Encode(source));
                RejectBoth(token);
                string text = Json(new ReplayHeader { Scene = "Level_CheckpointTime" }) + "\n" + Json(token) + "\n{\"Type\":\"end\"}\n";
                Reject(() => ReplayFiles.Read(new StringReader(text), allowSingleFrame: true));
                Reject(() => ReplayFiles.ReadEffects(new StringReader(text), allowSingleFrame: true));
            }
            var past = Checkpoint(); past.ObservedAt = -.5;
            Both(JObject.FromObject(new ReplayDeltaCodec.Writer().Encode(Frame(0, past))), value => Check(value.Effects[0].Particles![0].Checkpoint!.ObservedAt == -.5));
        });
        test("particle checkpoint nullable field is omitted in legacy current and initial module JSON", () =>
        {
            var frame = Frame(10, null); frame.Effects[0].InitialState = new EffectReplayInitialState
            { ObservedAt = 9.5, UntilTime = 9.6, Seeds = frame.Effects[0].Seeds, Particles = frame.Effects[0].Particles };
            string text = Json(new ReplayDeltaCodec.Writer(22).Encode(frame)); Check(!text.Contains("\"Checkpoint\"", StringComparison.Ordinal));
            Check(new ReplayDeltaCodec.Reader(22).Decode(JObject.Parse(text)).Effects[0].InitialState!.Particles![0].Checkpoint == null);
            Check(new ReplayDeltaCodec.StreamReader(22).DecodeRecord(text)!.Effects[0].InitialState!.Particles![0].Checkpoint == null);
            foreach (bool inInitial in new[] { false, true })
            {
                var token = JObject.Parse(text); var particle = inInitial ? token["Effects"]![0]!["InitialState"]!["Particles"]![0] : token["Effects"]![0]!["Particles"]![0];
                particle!["Checkpoint"] = JValue.CreateNull();
                Reject(() => new ReplayDeltaCodec.Reader(22).Decode(token)); Reject(() => new ReplayDeltaCodec.StreamReader(22).DecodeRecord(Json(token)));
            }
        });
        test("particle checkpoint byte arrays have strict types complete fields and bounded allocation", () =>
        {
            var valid = JObject.FromObject(new ReplayDeltaCodec.Writer().Encode(Frame(10, Checkpoint())));
            foreach (string field in new[] { "State", "Data" })
            foreach (JToken bad in new JToken[] { JValue.CreateNull(), new JArray(1,2,3,4), new JValue(123), new JValue(true), new JValue("") })
            { var token=(JObject)valid.DeepClone(); token["Effects"]![0]!["Particles"]![0]!["Checkpoint"]![field]=bad.DeepClone(); RejectBoth(token); }
            foreach (string field in new[] { "ObservedAt", "RootPhase", "Layout", "Count", "State", "Data", "Digest" })
            { var token=(JObject)valid.DeepClone(); ((JObject)token["Effects"]![0]!["Particles"]![0]!["Checkpoint"]!).Remove(field); RejectBoth(token); }
            var oversized=(JObject)valid.DeepClone();oversized["Effects"]![0]!["Particles"]![0]!["Checkpoint"]!["Data"]=Convert.ToBase64String(new byte[EffectParticleCheckpointRules.MaximumDataBytes+1]);RejectBoth(oversized);
            var maximum=Checkpoint();maximum.Count=EffectParticleCheckpointRules.MaximumParticles;maximum.State=new byte[EffectParticleCheckpointRules.MaximumStateBytes];maximum.Data=new byte[EffectParticleCheckpointRules.MaximumDataBytes];maximum.Digest=EffectParticleCheckpointRules.Digest(maximum);
            Both(JObject.FromObject(new ReplayDeltaCodec.Writer().Encode(Frame(10,maximum))), value=>Check(value.Effects[0].Particles![0].Checkpoint!.Data.Length==EffectParticleCheckpointRules.MaximumDataBytes));
        });
        test("particle checkpoint full module replacement can clear a prior checkpoint without inheritance", () =>
        {
            var writer=new ReplayDeltaCodec.Writer();var tree=new ReplayDeltaCodec.Reader();var stream=new ReplayDeltaCodec.StreamReader(23);
            var first=writer.Encode(Frame(10,Checkpoint()));tree.Decode(JObject.FromObject(first));stream.DecodeRecord(Json(first));
            var next=writer.Encode(Frame(10.02,null));var a=tree.Decode(JObject.FromObject(next));var b=stream.DecodeRecord(Json(next))!;
            Check(a.Effects[0].Particles![0].Checkpoint==null&&b.Effects[0].Particles![0].Checkpoint==null);
        });
        test("particle checkpoint continuous crop keeps one shared payload across initial and current states", () =>
        {
            var cp=Checkpoint();var source=Frame(10,cp);source.Effects[0].InitialState=new EffectReplayInitialState
            {ObservedAt=9.5,UntilTime=9.6,Seeds=source.Effects[0].Seeds,Particles=source.Effects[0].Particles};
            var rebaser=new ContinuousReplayRebaser(9);var a=rebaser.Apply(source);var b=rebaser.Apply(Frame(10.02,cp));
            var initial=a.Effects[0].InitialState!;var current=a.Effects[0].Particles![0].Checkpoint!;
            Check(current.ObservedAt==.5&&initial.ObservedAt==.5&&Math.Abs(initial.UntilTime-.6)<1e-12&&ReferenceEquals(current,initial.Particles![0].Checkpoint)&&ReferenceEquals(current,b.Effects[0].Particles![0].Checkpoint));
            Check(ReferenceEquals(cp.Data,current.Data)&&cp.ObservedAt==9.5);ReplayRules.ValidateCapture(a,-1);
        });
        test("duplicate checkpoint JSON fields are rejected in ordinary and projected effects readers", () =>
        {
            string record=Json(new ReplayDeltaCodec.Writer().Encode(Frame(10,Checkpoint())));string duplicated=record.Replace("\"State\":","\"State\":\"AQIDBA==\",\"State\":",StringComparison.Ordinal);Check(duplicated!=record);
            Reject(()=>new ReplayDeltaCodec.StreamReader(23).DecodeRecord(duplicated));
            string text=Json(new ReplayHeader{Scene="Level_CheckpointDuplicate"})+"\n"+duplicated+"\n{\"Type\":\"end\"}\n";
            Reject(()=>ReplayFiles.Read(new StringReader(text),allowSingleFrame:true));Reject(()=>ReplayFiles.ReadEffects(new StringReader(text),allowSingleFrame:true));
        });
        test("particle checkpoint independent reverse pages and effect-only history retain exact state", () =>
        {
            string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "peak-particle-storage-"));
            string root = prefix + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(root);
            try
            {
                var checkpoint = Checkpoint();
                var writer = new FullReplayWriter(root, new ReplayHeader { Scene = "Level_ParticleCheckpoint" }, new ContinuousReplayOptions { SegmentFrameLimit = 13 });
                for (int i = 0; i < 121; i++) Check(writer.TryEnqueue(Frame(10 + i / 60d, checkpoint)));
                var task = writer.CompleteAsync(); Check(task.Wait(TimeSpan.FromSeconds(20))); var result = task.GetAwaiter().GetResult();
                Check(result.Status == "completed"); var info = FullReplayArchive.ReadInfo(result.FilePath);
                Check(info.Header.Schema == ReplayRules.CurrentSchema && info.Pages.Length > 2);
                foreach (var page in info.Pages.AsEnumerable().Reverse())
                {
                    var clip = FullReplayArchive.ReadPage(result.FilePath, info, page.Index);
                    var history = FullReplayArchive.ReadEffectHistory(result.FilePath, info, page.Index, default);
                    foreach (var frame in clip.Frames)
                    {
                        var value = frame.Effects[0].Particles![0].Checkpoint!;
                        Check(value.ObservedAt == -.5 && value.RootPhase == checkpoint.RootPhase && value.Data.SequenceEqual(checkpoint.Data) && value.Digest == checkpoint.Digest);
                        var effect = history.Frames.Single(x => x.T == frame.T).Effects[0];
                        Check(effect.Particles![0].Checkpoint!.Digest == value.Digest);
                    }
                }
            }
            finally { if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unexpected test path."); Directory.Delete(root, true); }
        });
    }
    private static EffectParticleCheckpoint Checkpoint()
    {
        var value = new EffectParticleCheckpoint { ObservedAt = 9.5, RootPhase = 40, Layout = "test-native-layout", Count = 1, State = new byte[] { 1, 2, 3, 4 }, Data = new byte[] { 5, 6, 7, 8 } };
        value.Digest = EffectParticleCheckpointRules.Digest(value); return value;
    }
    private static ReplayFrame Frame(double time, EffectParticleCheckpoint? checkpoint)
    {
        var parameters = new float[14]; parameters[12] = .1f; parameters[13] = 1;
        return new ReplayFrame { T = time, Effects = new[] { new EffectReplayFrame { Key = "event-particle:checkpoint:1:1", Resource = "native-test", Phase = 40.5, AnchorTime = time, Seeds = new[] { 1L },
            Particles = new[] { new EffectParticleFrame(new NodePose { Path = "0:particle" }, Enumerable.Repeat(1f, 8).ToArray(), parameters) { Checkpoint = checkpoint } } } } };
    }
    private static void Both(JObject token, Action<ReplayFrame> check)
    { var tree = new ReplayDeltaCodec.Reader().Decode(token); ReplayRules.ValidateCapture(tree, -1); check(tree); var stream = new ReplayDeltaCodec.StreamReader(23).DecodeRecord(Json(token))!; ReplayRules.ValidateCapture(stream, -1); check(stream); }
    private static void RejectBoth(JObject token) { Reject(() => Both(token, _ => { })); Reject(() => { var frame = new ReplayDeltaCodec.StreamReader(23).DecodeRecord(Json(token))!; ReplayRules.ValidateCapture(frame, -1); }); }
    private static string Json(object value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static void Check(bool value) { if (!value) throw new Exception("Particle checkpoint storage assertion failed."); }
    private static void Reject(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException or JsonException or ArgumentOutOfRangeException) { return; } throw new Exception("Invalid particle checkpoint accepted."); }
}
