using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class NativeSharedPackingTests
{
    private const int Schema = 26;
    private const int TrapCount = 384, SpiderCount = 72, ChangedTrap = 93;
    private const double TrapChangeTime = 2.25;

    public static void Run(Action<string, Action> test)
    {
        test("schema26 300 native clock checkpoints share rig and appearance before whole entity packing", () =>
        {
            var nodes = Nodes(48); var visuals = Visuals(0);
            var frames = Enumerable.Range(0, 300).Select(i => NativeFrame(i, nodes, visuals)).ToArray();
            var packed = Pack(frames);
            Check(packed.Writer.Blocks.All(block => block.Kind != 0 && block.Kind != 3 && block.Kind != 10),
                "Changing frog/raven clocks created a whole entity or static directory block.");
            Check(packed.Writer.Blocks.Count(block => block.Kind == 8) == 1,
                "Unchanged native rigs were serialized more than once.");
            Check(packed.Writer.Blocks.Count(block => block.Kind == 6) == 1,
                "Unchanged native renderer state was serialized more than once.");
            for (int i = packed.Records.Count - 1; i >= 0; i--)
            {
                var record = JObject.Parse(packed.Records[i]);
                foreach (string track in new[] { "Items", "Creatures" })
                {
                    Check(record[track]![0]!["$Shared"] == null);
                    Check(record[track]![0]!["Visuals"]!["$Shared"] != null);
                    Check(record[track]![0]!["Pose"]!["Nodes"]!["$Shared"] != null);
                }
                Check(Json(Decode(packed, i)) == Json(frames[i]), "A reverse native checkpoint lost its clock or geometry.");
            }
            Check(packed.Raw.All(raw => !raw.Contains("\"$Shared\"", StringComparison.Ordinal)),
                "A shared native leaf contained another shared reference.");
        });

        test("schema26 native leaf packing retains root action clip topology and appearance changes", () =>
        {
            var frames = new List<ReplayFrame>();
            for (int i = 0; i < 12; i++)
            {
                var frame = NativeFrame(i, Nodes(i >= 7 ? 12 : 48), Visuals(i >= 9 ? 1 : 0));
                var item = frame.Items[0]; var raven = frame.Creatures[0];
                item.Pose.Position[0] = i / 8f; raven.Pose.Position[2] = i / 4f;
                item.State = i >= 4 ? 2 : 0; item.HolderId = i >= 4 ? "scout" : "";
                if (i >= 5)
                {
                    raven.State = 1; raven.IdleAnimation!.Clip = CreatureIdleAnimationRules.RavenFlyAway;
                    raven.IdleAnimation.ClipTime = .75;
                }
                if (i == 8)
                {
                    item.Pose.Nodes[0].Active = false;
                    raven.Pose.Nodes[1].Visible = false;
                }
                frames.Add(frame);
            }
            var packed = Pack(frames.ToArray());
            Check(packed.Writer.Blocks.All(block => block.Kind != 0 && block.Kind != 3),
                "A native state change bypassed immutable leaf packing.");
            Check(packed.Writer.Blocks.Count(block => block.Kind == 8) >= 2 && packed.Writer.Blocks.Count(block => block.Kind == 6) == 2);
            foreach (int i in new[] { 11, 0, 8, 3, 7, 9, 5, 2, 10, 4, 1, 6 })
                Check(Json(Decode(packed, i)) == Json(frames[i]), "An independent native baseline borrowed state from another page.");
            Check(packed.Raw.All(raw => !raw.Contains("\"$Shared\"", StringComparison.Ordinal)));
        });

        test("schema26 static forest uses one order leaf and changes its directory only with actual static state", () => InRoot(root =>
        {
            var frames = ForestFrames(); var path = Save(root, frames); var info = FullReplayArchive.ReadInfo(path);
            var blocks = info.SharedBlocks ?? throw new Exception("Missing native forest shared blocks.");
            Check(info.Header.Schema == Schema && info.Pages.Length >= 10);
            Check(blocks.Count(block => block.Kind == 9) == 1,
                "An unchanged 457-creature order was duplicated across pages.");
            Check(blocks.Count(block => block.Kind == 10) == 2,
                "Moving dynamic state rewrote the static directory, or the consumed trap was omitted.");
            var directoryIds = new HashSet<int>(); int? orderId = null;
            foreach (var page in info.Pages.AsEnumerable().Reverse())
            {
                var records = PageRecords(path, page); var baseline = records[0];
                int directoryId = DirectReference(baseline["StaticCreatures"]);
                directoryIds.Add(directoryId);
                Check(blocks[directoryId].Kind == 10 && directoryId < page.SharedThrough);
                int currentOrder = DirectReference(baseline["CreatureOrder"]);
                Check(blocks[currentOrder].Kind == 9 && (!orderId.HasValue || orderId == currentOrder));
                orderId = currentOrder;
                Check(((JArray)baseline["Creatures"]!).Count == 1,
                    "A baseline repeated static creatures alongside its direct directory reference.");
                Check(records.Skip(1).All(record => record["StaticCreatures"] == null),
                    "A static directory appeared outside an ObjectsFull baseline.");
                // This call creates a new file handle, decoder and resolver for
                // every page; the latest directory must work before page zero.
                var clip = FullReplayArchive.ReadPage(path, info, page.Index);
                foreach (var frame in clip.Frames)
                    Check(Json(frame) == Json(frames[(int)Math.Round(frame.T * 4)]),
                        "A cold forest seek changed static state, dynamic motion, or original creature order.");
            }
            Check(directoryIds.Count == 2);
            foreach (int id in directoryIds)
            {
                var raw = JObject.Parse(ReadBlock(path, blocks[id])); var values = (JArray)raw["Value"]!;
                Check(raw.Count == 1 && values.Count == TrapCount + SpiderCount);
                Check(values.All(value => value["Full"] == null && value["Pose"] != null && value["Key"] != null));
                Check(!Json(raw).Contains("\"$Shared\"", StringComparison.Ordinal), "Static directory entries formed a reference chain.");
            }
            int firstDirectory = DirectReference(PageRecords(path, info.Pages[0])[0]["StaticCreatures"]);
            var wrongKind = FullReplayArchive.ReadInfo(path); wrongKind.SharedBlocks![firstDirectory].Kind = 9;
            Reject(() => FullReplayArchive.ReadPage(path, wrongKind, 0));
            var future = FullReplayArchive.ReadInfo(path); future.Pages[0].SharedThrough = firstDirectory;
            Reject(() => FullReplayArchive.ReadPage(path, future, 0));
        }));

        test("schema26 live nonzero capture origin rebases static directory clocks once and preserves independent histories", () => InRoot(root =>
        {
            const double origin = 120;
            var expected = ForestFrames();
            var replayEffect = new EffectReplayFrame { Key = "nonzero-origin-spore", Resource = "spore", AnchorTime = TrapChangeTime };
            foreach (var frame in expected)
            {
                frame.SceneSampleTimeKnown = true; frame.SceneSampleTime = frame.T;
                if (frame.T >= TrapChangeTime && frame.T < TrapChangeTime + 1) frame.Effects = new[] { replayEffect };
            }
            var native = expected.Select(frame =>
            {
                var copy = JsonConvert.DeserializeObject<ReplayFrame>(Json(frame))!;
                copy.T += origin; copy.SceneSampleTime += origin;
                foreach (var creature in copy.Creatures)
                {
                    if (creature.SampleTimeKnown) creature.SampleTime += origin;
                    if (creature.IdleAnimation != null) creature.IdleAnimation.AnchorTime += origin;
                }
                foreach (var effect in copy.Effects) effect.AnchorTime += origin;
                return copy;
            }).ToArray();
            string path = Save(root, native); var info = FullReplayArchive.ReadInfo(path);
            Check(info.Pages.Length >= 10 && info.SharedBlocks!.Any(block => block.Kind == 10));
            using var playback = FullReplayArchive.OpenPlayback(path, info);
            foreach (int index in Enumerable.Range(0, info.Pages.Length).Reverse())
            {
                var page = FullReplayArchive.ReadPage(path, info, index);
                var history = playback.LoadHistory(index, default);
                Check(history.Frames.Count == page.Frames.Count);
                for (int slot = 0; slot < page.Frames.Count; slot++)
                {
                    var frame = page.Frames[slot]; var original = expected[(int)Math.Round(frame.T * 4)];
                    Check(Json(frame) == Json(original), "Native absolute clocks leaked into a relative page baseline.");
                    Check(Json(history.Frames[slot].Effects) == Json(original.Effects), "Nonzero origin changed independent effect history.");
                    foreach (var spider in frame.Creatures.Where(creature => creature.IdleAnimation != null))
                        Check(spider.SampleTime == -2 && spider.IdleAnimation!.AnchorTime == -2);
                }
            }
            foreach (var block in info.SharedBlocks!.Where(block => block.Kind == 10))
                foreach (var creature in JObject.Parse(ReadBlock(path, block))["Value"]!.Where(value => value["IdleAnimation"]?.Type == JTokenType.Object))
                    Check((double)creature["SampleTime"]! == -2 && (double)creature["IdleAnimation"]!["AnchorTime"]! == -2,
                        "A direct typed directory serialized the unre-based native source DTO.");
            Check(native.All(frame => frame.Creatures.Where(creature => creature.IdleAnimation != null)
                .All(creature => creature.SampleTime == origin - 2 && creature.IdleAnimation!.AnchorTime == origin - 2)),
                "Writing relative leaves mutated the native capture baseline.");
        }));

        test("schema26 direct order and static leaves retain interleaved entity order and reject malformed references", () =>
        {
            var trap = Trap(0, true); var idle = IdleSpider(0, Nodes(16), Visuals(0)); var moving = Moving(0);
            var frame = new ReplayFrame { Creatures = new[] { trap, moving, idle } };
            var baseline = JObject.Parse(Json(new ReplayDeltaCodec.Writer(Schema).Encode(frame)));
            baseline["Creatures"] = new JArray(baseline["Creatures"]![1]!.DeepClone());
            baseline["StaticCreatures"] = Ref(1); baseline["CreatureOrder"] = Ref(0);
            var order = frame.Creatures.Select(creature => creature.Key).ToArray(); var directory = new[] { trap, idle };
            int calls = 0;
            object Resolve(Type type, int id)
            {
                calls++;
                if (id >= 2) throw new InvalidDataException("Future test shared block.");
                if (id == 0) { Check(type == typeof(string[])); return order; }
                Check(id == 1 && type == typeof(CreatureReplayFrame[])); return directory;
            }
            object ResolveForShape(Type type, int id) => type == typeof(string[]) ? order : directory;
            Check(Json(new ReplayDeltaCodec.StreamReader(Schema, Resolve).DecodeRecord(Json(baseline))) == Json(frame));
            Check(calls == 2);
            Reject(() => new ReplayDeltaCodec.StreamReader(Schema).DecodeRecord(Json(baseline)));
            Reject(() => new ReplayDeltaCodec.StreamReader(25, Resolve).DecodeRecord(Json(baseline)));
            foreach (string field in new[] { "CreatureOrder", "StaticCreatures" })
            {
                var wrong = (JObject)baseline.DeepClone(); wrong[field] = Ref(1);
                Reject(() => new ReplayDeltaCodec.StreamReader(Schema, (_, _) => Array.Empty<NativeRendererFrame>()).DecodeRecord(Json(wrong)));
                foreach (string malformed in new[] {
                    "{\"$Shared\":-1}", "{\"$Shared\":0.0}", "{\"$Shared\":2147483648}",
                    "{\"$Shared\":0,\"Value\":[]}", "{\"$Shared\":0,\"$Shared\":0}", "{\"$Shared\":{\"$Shared\":0}}" })
                {
                    string token = Json(baseline[field]);
                    string broken = Json(baseline).Replace("\"" + field + "\":" + token, "\"" + field + "\":" + malformed, StringComparison.Ordinal);
                    Reject(() => new ReplayDeltaCodec.StreamReader(Schema, ResolveForShape).DecodeRecord(broken));
                }
                var future = (JObject)baseline.DeepClone(); future[field] = Ref(2);
                Reject(() => new ReplayDeltaCodec.StreamReader(Schema, Resolve).DecodeRecord(Json(future)));
            }
            var delta = (JObject)baseline.DeepClone(); delta["ObjectsFull"] = false;
            Reject(() => new ReplayDeltaCodec.StreamReader(Schema, Resolve).DecodeRecord(Json(delta)));
        });

        test("schema26 typed directory leaves forbid nested references entity patches and legacy schemas", () =>
        {
            var directory = new[] { Trap(0, true), IdleSpider(0, Nodes(16), Visuals(0)) };
            var order = directory.Select(creature => creature.Key).ToArray();
            foreach (var leaf in new[] { (Value: (object)directory, Type: typeof(CreatureReplayFrame[])), (Value: (object)order, Type: typeof(string[])) })
            {
                string valid = Json(new { Value = leaf.Value });
                Check(Json(DecodeLeaf(valid, leaf.Type, Schema)) == Json(leaf.Value));
                Reject(() => DecodeLeaf(valid, leaf.Type, 25));
                foreach (string broken in new[] { "{\"Value\":[{\"$Shared\":0}]}", "{\"$Shared\":0}",
                    valid + "{}", "{\"Value\":[],\"Value\":[]}", "{\"Value\":[],\"Extra\":1}" })
                    Reject(() => DecodeLeaf(broken, leaf.Type, Schema));
            }
            var nested = JObject.Parse(Json(new { Value = directory }));
            nested["Value"]![1]!["Pose"]!["Nodes"] = Ref(0);
            Reject(() => DecodeLeaf(Json(nested), typeof(CreatureReplayFrame[]), Schema));
            nested = JObject.Parse(Json(new { Value = directory })); nested["Value"]![1]!["Visuals"] = Ref(0);
            Reject(() => DecodeLeaf(Json(nested), typeof(CreatureReplayFrame[]), Schema));
            nested = JObject.Parse(Json(new { Value = directory })); nested["Value"]![0]!["Full"] = true;
            Reject(() => DecodeLeaf(Json(nested), typeof(CreatureReplayFrame[]), Schema));
        });

        test("schema26 pure static directory without an order retains every seeded baseline entity", () =>
        {
            var directory = new[] { Trap(0, true), IdleSpider(0, Nodes(16), Visuals(0)) };
            var expected = new ReplayFrame { Creatures = directory };
            var baseline = JObject.Parse(Json(new ReplayDeltaCodec.Writer(Schema).Encode(expected)));
            baseline.Remove("Creatures"); baseline.Remove("CreatureOrder"); baseline["StaticCreatures"] = Ref(0);
            var reader = new ReplayDeltaCodec.StreamReader(Schema, (type, id) =>
            { Check(type == typeof(CreatureReplayFrame[]) && id == 0); return directory; });
            var decoded = reader.DecodeRecord(Json(baseline))!;
            Check(Json(decoded) == Json(expected));
            var delta = JObject.Parse(Json(new ReplayDeltaCodec.Writer(Schema).Encode(new ReplayFrame { T = 1 })));
            delta["ObjectsFull"] = false; delta["World"] = new JObject();
            Check(Json(reader.DecodeRecord(Json(delta))!.Creatures) == Json(directory));
        });
        test("schema26 directory and order leaves enforce unique eligible entities and bounded identities and counts", () =>
        {
            void Directory(CreatureReplayFrame[] creatures) => DecodeLeaf(Json(new { Value = creatures }), typeof(CreatureReplayFrame[]), Schema);
            void Order(string[] keys) => DecodeLeaf(Json(new { Value = keys }), typeof(string[]), Schema);
            Reject(() => Directory(new[] { Trap(0, true), Trap(0, false) }));
            Reject(() => Directory(new[] { Moving(0) }));
            Reject(() => Directory(NativeFrame(0, Nodes(16), Visuals(0)).Creatures));
            var oversizedKey = Trap(0, true); oversizedKey.Key = new string('k', 257);
            Reject(() => Directory(new[] { oversizedKey }));
            Reject(() => Order(new[] { new string('k', 2049) }));
            Reject(() => Order(new[] { "" }));
            Reject(() => Order(Enumerable.Range(0, ReplayRules.MaxCreatures + 1).Select(i => "key-" + i).ToArray()));
            Reject(() => Directory(Enumerable.Range(0, CreatureReplayRules.MaximumStaticTraps + 1).Select(i => Trap(i, true)).ToArray()));
        });
    }

    private sealed class Packed
    {
        internal readonly FullReplaySharedWriter Writer = new();
        internal readonly List<string> Records = new();
        internal readonly List<int> Through = new();
        internal string[] Raw = Array.Empty<string>();
    }
    private static Packed Pack(ReplayFrame[] frames)
    {
        var packed = new Packed();
        foreach (var frame in frames)
        {
            var record = new ReplayDeltaCodec.Writer(Schema).Encode(frame);
            packed.Writer.EncodeBaseline(record, frame); packed.Records.Add(Json(record)); packed.Through.Add(packed.Writer.Blocks.Count);
        }
        using var output = new MemoryStream(); packed.Writer.FlushPending(output);
        packed.Raw = packed.Writer.Blocks.Select(block => ReadBlock(output, block)).ToArray();
        return packed;
    }
    private static ReplayFrame Decode(Packed packed, int index)
    {
        var values = new Dictionary<int, object>();
        object Resolve(Type type, int id)
        {
            if (id < 0 || id >= packed.Through[index]) throw new InvalidDataException("Unknown or future test shared block.");
            Check(BlockType(packed.Writer.Blocks[id].Kind) == type, "Shared block kind did not match its typed field.");
            if (!values.TryGetValue(id, out var value))
            {
                var reader = new ReplayDeltaCodec.StreamReader(Schema);
                value = packed.Writer.Blocks[id].Kind < 6 ? reader.DecodeSharedRecord(packed.Raw[id], type, "Key") : DecodeLeaf(packed.Raw[id], type, Schema);
                values.Add(id, value);
            }
            return value;
        }
        return new ReplayDeltaCodec.StreamReader(Schema, Resolve).DecodeRecord(packed.Records[index])!;
    }
    private static Type BlockType(int kind) => kind switch
    {
        0 => typeof(ItemFrame), 1 => typeof(CrateFrame), 2 => typeof(RopeReplayFrame), 3 => typeof(CreatureReplayFrame),
        4 => typeof(InteractionReplayFrame), 5 => typeof(SpawnedReplayFrame), 6 => typeof(NativeRendererFrame[]),
        7 => typeof(NativeLightFrame[]), 8 => typeof(NodePose[]), 9 => typeof(string[]), 10 => typeof(CreatureReplayFrame[]),
        _ => throw new InvalidDataException("Unknown test shared block kind.")
    };
    private static object DecodeLeaf(string raw, Type type, int schema)
    { char[] characters = raw.ToCharArray(); return new ReplayDeltaCodec.StreamReader(schema).DecodeSharedValue(characters, characters.Length, type); }
    private static ReplayFrame NativeFrame(int index, NodePose[] nodes, NativeRendererFrame[] visuals)
    {
        double time = index / 4d;
        return new ReplayFrame { T = time,
            Items = new[] { new ItemFrame { Key = "frog-native", ItemId = 172, Pose = new ObjectPose { Nodes = nodes }, Visuals = visuals,
                IdleAnimation = Clock(CreatureIdleAnimationRules.FrogIdle, time) } },
            Creatures = new[] { new CreatureReplayFrame { Key = "raven-native", Kind = "voice-thief", Resource = "voice-thief",
                SampleTimeKnown = true, SampleTime = time, ClockMode = CreatureReplayRules.IdleClock,
                Pose = new ObjectPose { Nodes = nodes }, Visuals = visuals, IdleAnimation = Clock(CreatureIdleAnimationRules.RavenIdle, time) } } };
    }
    private static CreatureIdleAnimation Clock(string clip, double time) => new() { Clip = clip, AnchorTime = time, ClipTime = .25, Speed = 1 };
    private static ReplayFrame[] ForestFrames()
    {
        var nodes = Nodes(16); var visuals = Visuals(0);
        var original = Enumerable.Range(0, TrapCount).Select(i => Trap(i, true))
            .Concat(Enumerable.Range(0, SpiderCount).Select(i => IdleSpider(i, nodes, visuals))).ToArray();
        var changed = (CreatureReplayFrame[])original.Clone(); changed[ChangedTrap] = Trap(ChangedTrap, false);
        return Enumerable.Range(0, 49).Select(i =>
        {
            var stationary = i / 4d < TrapChangeTime ? original : changed;
            // Put motion between two static groups, so restoring the directory
            // requires the original combined order instead of appending it.
            var creatures = stationary.Take(TrapCount).Concat(new[] { Moving(i) }).Concat(stationary.Skip(TrapCount)).ToArray();
            return new ReplayFrame { T = i / 4d, Creatures = creatures };
        }).ToArray();
    }
    private static CreatureReplayFrame Trap(int id, bool active) => new()
    {
        Key = "forest-trap-" + id, Kind = "spore-trap", Resource = "spore-trap", State = active ? 0 : 1,
        SourcePath = "0:Map/0:Forest/0:Hazards/" + id + ":Forest_SporeFungus (Clone)",
        Pose = new ObjectPose { Active = active, Position = new[] { id * 1.271f, 182.37f + id * .12f, id * -1.097f } },
    };
    private static CreatureReplayFrame IdleSpider(int id, NodePose[] nodes, NativeRendererFrame[] visuals) => new()
    {
        Key = "forest-idle-spider-" + id, Kind = "spider", Resource = "spider-web", SampleTimeKnown = true, SampleTime = -2,
        ClockMode = CreatureReplayRules.IdleClock, Pose = new ObjectPose { Position = new[] { id * 2f, 6f, id * -3f }, Nodes = nodes },
        Visuals = visuals, Line = new CreatureReplayLine { Enabled = true, WorldSpace = true, Points = new float[120], Width = .02f },
        IdleAnimation = new CreatureIdleAnimation { Clip = CreatureIdleAnimationRules.SpiderIdle, AnchorTime = -2, ClipTime = .2, Speed = .33f, RopePhase = .7f },
    };
    private static CreatureReplayFrame Moving(int index) => new()
    {
        Key = "forest-moving-spider", Kind = "spider", Resource = "spider-normal", SampleTimeKnown = true, SampleTime = index / 4d,
        Pose = new ObjectPose { Position = new[] { index / 8f, 4f, 0f }, Nodes = Nodes(16) },
    };
    private static NodePose[] Nodes(int count) => Enumerable.Range(0, count).Select(i => new NodePose("root/branch-" + i,
        new[] { i * .134f, i * .017f, i * .135f }, new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f })).ToArray();
    private static NativeRendererFrame[] Visuals(int variant) => Enumerable.Range(0, 4).Select(i => new NativeRendererFrame
    {
        Path = "root/mesh-" + i, Mesh = "native-mesh-" + i, Enabled = variant == 0 || i != 0, BlendShapes = new[] { variant * 30f },
        Materials = new[] { new NativeMaterialFrame { Name = "native-material-" + i, Shader = "native-shader", Properties = new[] {
            new NativeShaderPropertyFrame { Name = "_Color", Kind = 1, Values = new[] { .2f, variant / 3f, .5f, 1f } },
            new NativeShaderPropertyFrame { Name = "_MainTex", Kind = 3, Texture = "native-texture-" + i } } } },
    }).ToArray();
    private static JObject Ref(int id) => new() { ["$Shared"] = id };
    private static int DirectReference(JToken? value)
    { Check(value is JObject reference && reference.Count == 1 && reference["$Shared"]?.Type == JTokenType.Integer, "Expected one direct shared reference."); return (int)value!["$Shared"]!; }
    private static string Save(string root, ReplayFrame[] frames)
    {
        var writer = new FullReplayWriter(root, new ReplayHeader { Schema = Schema, Scene = "Level_NativeSharedForest" });
        foreach (var frame in frames) Check(writer.TryEnqueue(frame));
        var result = writer.CompleteAsync().GetAwaiter().GetResult();
        Check(result.Status == "completed", result.Fault ?? "Native forest recording failed."); return result.FilePath;
    }
    private static List<JObject> PageRecords(string path, FullReplayPage page)
    {
        using var file = File.OpenRead(path); file.Position = page.Offset;
        using var slice = new FullReplayArchive.PageSlice(file, page.Length, default);
        using var gzip = new GZipStream(slice, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8); _ = reader.ReadLine();
        var records = new List<JObject>();
        while (reader.ReadLine() is { } line)
        { var record = JObject.Parse(line); if ((string?)record["Type"] != "end") records.Add(record); }
        return records;
    }
    private static string ReadBlock(string path, FullReplaySharedBlock block)
    { using var file = File.OpenRead(path); return ReadBlock(file, block); }
    private static string ReadBlock(Stream file, FullReplaySharedBlock block)
    {
        file.Position = block.Offset;
        using var slice = new FullReplayArchive.PageSlice(file, block.Length, default);
        using var gzip = new GZipStream(slice, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8); return reader.ReadToEnd();
    }
    private static string Json(object? value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static void Check(bool value, string message = "Native shared packing assertion failed.")
    { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is InvalidDataException || e is JsonException || e is IOException || e is ArgumentException) { return; }
        throw new Exception("Malformed native shared packing was accepted.");
    }
    private static void InRoot(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "PeakNativeShared-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            string resolved = Path.GetFullPath(root), prefix = Path.Combine(Path.GetTempPath(), "PeakNativeShared-");
            if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected native shared test directory.");
            Directory.Delete(root, true);
        }
    }
}
