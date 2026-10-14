using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class ReplayObjectNodeCodecTests
{
    public static void Run(Action<string, Action> test)
    {
        foreach (int mask in Enumerable.Range(1, 31))
        {
            int fields = mask;
            test("schema 16 exact object nodes round trip all entity tracks mask " + fields, () =>
            {
                var first = Frame(0, Nodes()); var next = Frame(1, Changed(Nodes(), fields));
                var writer = new ReplayDeltaCodec.Writer(16);
                string baseline = Json(writer.Encode(first)), update = Json(writer.Encode(next));
                var token = Parse(update);
                foreach (string track in Tracks)
                {
                    var delta = token[track]![0]!["Pose"]!["Nodes"]!;
                    Check(delta is JObject && (int)delta["Length"]! == 2 && delta["Changes"]!.Count() == 1);
                    Check((int)delta["Changes"]![0]![0]! == 1 && (int)delta["Changes"]![0]![1]! == fields);
                }
                var tree = new ReplayDeltaCodec.Reader(16); var stream = new ReplayDeltaCodec.StreamReader(16);
                var oldTree = tree.Decode(Parse(baseline)); var oldStream = stream.DecodeRecord(baseline)!;
                string beforeTree = Json(oldTree), beforeStream = Json(oldStream);
                var a = tree.Decode(token); char[] characters = update.ToCharArray(); var b = stream.DecodeRecord(characters, characters.Length)!;
                ReplayRules.Validate(a, 0); ReplayRules.Validate(b, 0);
                Check(Json(a) == Json(b) && Json(b) == Json(next));
                foreach (var pair in PosePairs(oldStream, b))
                {
                    Check(!ReferenceEquals(pair.Old.Nodes, pair.New.Nodes));
                    Check(ReferenceEquals(pair.Old.Nodes[0], pair.New.Nodes[0]));
                    Check(!ReferenceEquals(pair.Old.Nodes[1], pair.New.Nodes[1]));
                    if ((fields & 1) == 0) Check(ReferenceEquals(pair.Old.Nodes[1].Position, pair.New.Nodes[1].Position));
                    if ((fields & 2) == 0) Check(ReferenceEquals(pair.Old.Nodes[1].Rotation, pair.New.Nodes[1].Rotation));
                }
                Check(Json(oldTree) == beforeTree && Json(oldStream) == beforeStream);
            });
        }
        test("schema 16 object node delta preserves float bits including signed zero and one ULP", () =>
        {
            var nodes = Nodes(); var changed = Changed(nodes, 1);
            changed[1].Position = new[] { BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)), BitConverter.Int32BitsToSingle(0x3f800001), float.Epsilon };
            nodes[1].Position = new[] { 0f, 1f, 0f };
            var writer = new ReplayDeltaCodec.Writer(16); var tree = new ReplayDeltaCodec.Reader(16); var stream = new ReplayDeltaCodec.StreamReader(16);
            string full = Json(writer.Encode(Frame(0, nodes))); tree.Decode(Parse(full)); stream.DecodeRecord(full);
            string line = Json(writer.Encode(Frame(1, changed)));
            var a = tree.Decode(Parse(line)); var b = stream.DecodeRecord(line)!;
            for (int i = 0; i < 3; i++)
            {
                int bits = BitConverter.SingleToInt32Bits(changed[1].Position[i]);
                Check(BitConverter.SingleToInt32Bits(a.Items[0].Pose.Nodes[1].Position[i]) == bits);
                Check(BitConverter.SingleToInt32Bits(b.Items[0].Pose.Nodes[1].Position[i]) == bits);
            }
        });
        test("schema 16 topology reorder rename addition removal and empty arrays use complete nodes", () =>
        {
            var sequences = new[] { Nodes(), Enumerable.Reverse(Nodes()).ToArray(), new[] { Node("changed"), Node("tail") },
                new[] { Node("changed") }, Array.Empty<NodePose>(), Nodes(), Nodes() };
            var writer = new ReplayDeltaCodec.Writer(16); var tree = new ReplayDeltaCodec.Reader(16); var stream = new ReplayDeltaCodec.StreamReader(16);
            for (int i = 0; i < sequences.Length; i++)
            {
                var expected = Frame(i, sequences[i]); string line = Json(writer.Encode(expected)); var token = Parse(line);
                if (i < sequences.Length - 1)
                    foreach (string track in Tracks) Check(token[track]![0]!["Pose"]!["Nodes"] is JArray);
                else foreach (string track in Tracks) Check(!token[track]!.Any());
                Check(Json(tree.Decode(token)) == Json(expected) && Json(stream.DecodeRecord(line)!) == Json(expected));
            }
        });
        test("schema 16 node patches support 273-node scoutmaster without changing inactive nodes", () =>
        {
            var first = new ReplayFrame { Creatures = new[] { Creature(Enumerable.Range(0, 273).Select(i => Node("bone/" + i)).ToArray()) } };
            var nextNodes = first.Creatures[0].Pose.Nodes.ToArray(); nextNodes[190] = Node(nextNodes[190].Path); nextNodes[190].Position[2] = .000001f;
            var next = new ReplayFrame { T = 1, Creatures = new[] { Creature(nextNodes) } };
            var writer = new ReplayDeltaCodec.Writer(16); var tree = new ReplayDeltaCodec.Reader(16); var stream = new ReplayDeltaCodec.StreamReader(16);
            string full = Json(writer.Encode(first)); tree.Decode(Parse(full)); var old = stream.DecodeRecord(full)!;
            string update = Json(writer.Encode(next)); Check(update.Length < 1000);
            var a = tree.Decode(Parse(update)); var b = stream.DecodeRecord(update)!;
            Check(Json(a) == Json(next) && Json(a) == Json(b));
            Check(old.Creatures[0].Pose.Nodes[190].Position[2] == 0 && ReferenceEquals(old.Creatures[0].Pose.Nodes[272], b.Creatures[0].Pose.Nodes[272]));
        });
        test("schema 10 through 15 retain complete object node arrays", () =>
        {
            for (int schema = 10; schema <= 15; schema++)
            {
                var first = new ReplayFrame { Items = new[] { Item(Nodes()) } };
                var next = new ReplayFrame { T = 1, Items = new[] { Item(Changed(Nodes(), 31)) } };
                var writer = new ReplayDeltaCodec.Writer(schema); var tree = new ReplayDeltaCodec.Reader(schema); var stream = new ReplayDeltaCodec.StreamReader(schema);
                string full = Json(writer.Encode(first)); tree.Decode(Parse(full)); stream.DecodeRecord(full);
                string line = Json(writer.Encode(next)); Check(Parse(line)["Items"]![0]!["Pose"]!["Nodes"] is JArray);
                Check(Json(tree.Decode(Parse(line))) == Json(next) && Json(stream.DecodeRecord(line)!) == Json(next));
                var modern = Parse(line); modern["Items"]![0]!["Pose"]!["Nodes"] = Patch("{\"Length\":2,\"Changes\":[[1,8,false]]}");
                Reject(() => tree.Decode(modern)); Reject(() => stream.DecodeRecord(Json(modern)));
            }
        });
        test("schema 16 empty node change list shares immutable baseline in both readers", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(16); string first = Json(writer.Encode(Frame(0, Nodes())));
            var delta = Parse(Json(writer.Encode(Frame(1, Changed(Nodes(), 1)))));
            foreach (string track in Tracks) delta[track]![0]!["Pose"]!["Nodes"] = Patch("{\"Changes\":[],\"Length\":2}");
            var tree = new ReplayDeltaCodec.Reader(16); var stream = new ReplayDeltaCodec.StreamReader(16);
            var a = tree.Decode(Parse(first)); var b = stream.DecodeRecord(first)!;
            var c = tree.Decode(delta); var d = stream.DecodeRecord(Json(delta))!;
            Check(ReferenceEquals(a.Items[0].Pose.Nodes, c.Items[0].Pose.Nodes));
            Check(ReferenceEquals(b.Items[0].Pose.Nodes, d.Items[0].Pose.Nodes));
        });
        string[] invalid = {
            "null", "{}", "{\"Length\":2}", "{\"Changes\":[]}", "{\"Length\":2,\"Changes\":[],\"Other\":0}",
            "{\"Length\":null,\"Changes\":[]}", "{\"Length\":\"2\",\"Changes\":[]}", "{\"Length\":2.0,\"Changes\":[]}",
            "{\"Length\":-1,\"Changes\":[]}", "{\"Length\":0,\"Changes\":[]}", "{\"Length\":1025,\"Changes\":[]}",
            "{\"Length\":2,\"Changes\":null}", "{\"Length\":2,\"Changes\":{}}",
            "{\"Length\":2,\"Changes\":[[1,0]]}", "{\"Length\":2,\"Changes\":[[1,32,false]]}",
            "{\"Length\":2,\"Changes\":[[1,-1,false]]}", "{\"Length\":2,\"Changes\":[[1,8.0,false]]}",
            "{\"Length\":2,\"Changes\":[[1,2147483648,false]]}", "{\"Length\":2,\"Changes\":[[1,8,0]]}",
            "{\"Length\":2,\"Changes\":[[1,16,null]]}", "{\"Length\":2,\"Changes\":[[1,8]]}",
            "{\"Length\":2,\"Changes\":[[1,8,false,true]]}", "{\"Length\":2,\"Changes\":[[-1,8,false]]}",
            "{\"Length\":2,\"Changes\":[[2,8,false]]}", "{\"Length\":2,\"Changes\":[[1.0,8,false]]}",
            "{\"Length\":2,\"Changes\":[[1,8,false],[1,16,false]]}", "{\"Length\":2,\"Changes\":[null]}",
            "{\"Length\":2,\"Changes\":[[1,1,[0,0]]]}", "{\"Length\":2,\"Changes\":[[1,1,[0,0,0,0]]]}",
            "{\"Length\":2,\"Changes\":[[1,2,[0,0,1]]]}", "{\"Length\":2,\"Changes\":[[1,2,[0,0,0,0]]]}",
            "{\"Length\":2,\"Changes\":[[1,4,null]]}", "{\"Length\":2,\"Changes\":[[1,4,[1,\"1\",1]]]}",
            "{\"Length\":2,\"Changes\":[[1,1,[0,1e40,0]]]}",
        };
        for (int i = 0; i < invalid.Length; i++)
        {
            string payload = invalid[i]; int index = i;
            test("schema 16 object node malformed delta rejected by both readers " + index, () => RejectNodeDelta(payload));
        }
        test("schema 16 rejects sparse nodes in first baseline or a new full entity", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(16); var full = Parse(Json(writer.Encode(Frame(0, Nodes()))));
            full["Items"]![0]!["Pose"]!["Nodes"] = Patch("{\"Length\":2,\"Changes\":[[1,8,false]]}");
            Reject(() => new ReplayDeltaCodec.Reader(16).Decode(full));
            Reject(() => new ReplayDeltaCodec.StreamReader(16).DecodeRecord(Json(full)));
            var first = Parse(Json(new ReplayDeltaCodec.Writer(16).Encode(Frame(0, Nodes())))); full["ObjectsFull"] = false; full["T"] = 1;
            var tree = new ReplayDeltaCodec.Reader(16); tree.Decode(first);
            var stream = new ReplayDeltaCodec.StreamReader(16); stream.DecodeRecord(Json(first));
            Reject(() => tree.Decode(full)); Reject(() => stream.DecodeRecord(Json(full)));
        });
        test("schema 16 duplicate node delta fields reject typed reader and strict DOM loading", () =>
        {
            foreach (string payload in new[] { "{\"Length\":2,\"Length\":2,\"Changes\":[]}", "{\"Length\":2,\"Changes\":[],\"Changes\":[]}" })
            {
                var writer = new ReplayDeltaCodec.Writer(16); string first = Json(writer.Encode(Frame(0, Nodes())));
                var delta = Parse(Json(writer.Encode(Frame(1, Changed(Nodes(), 1)))));
                delta["Items"]![0]!["Pose"]!["Nodes"] = "UNIQUE_NODE_PATCH";
                string line = Json(delta).Replace("\"UNIQUE_NODE_PATCH\"", payload, StringComparison.Ordinal);
                var stream = new ReplayDeltaCodec.StreamReader(16); stream.DecodeRecord(first);
                Reject(() => stream.DecodeRecord(line)); Reject(() => Parse(line));
            }
        });
        SharedSnapshotTests(test);
    }

    private static void SharedSnapshotTests(Action<string, Action> test)
    {
        test("typed shared snapshot character slices keep canonical and JSON fallback equivalent", () =>
        {
            var full = Parse(Json(new ReplayDeltaCodec.Writer(16).Encode(Frame(0, Changed(Nodes(), 31)))));
            string entity = Json(full["Items"]![0]!); string expected = Json(Item(Changed(Nodes(), 31)));
            foreach (string record in new[] { entity, entity.Replace("\"Key\":", "\"K\\u0065y\":", StringComparison.Ordinal),
                entity.Insert(1, "/* legacy JSON comment */"), entity.Insert(entity.Length - 1, ",") })
            {
                // Deliberately dirty unused buffer capacity: only the caller's
                // valid prefix may be parsed or retained by the decoder.
                char[] buffer = (record + "{{THIS IS NOT PART OF THE RECORD").ToCharArray();
                var reader = new ReplayDeltaCodec.StreamReader(16);
                object result = reader.DecodeSharedRecord(buffer, record.Length, typeof(ItemFrame), "Key");
                Check(Json(result) == expected);
                Array.Fill(buffer, 'x'); Check(Json(result) == expected);
            }
        });
        test("typed shared snapshot canonical and fallback reject duplicates malformed syntax and bad slices", () =>
        {
            var full = Parse(Json(new ReplayDeltaCodec.Writer(16).Encode(Frame(0, Nodes())))); string entity = Json(full["Items"]![0]!);
            foreach (string record in new[] {
                entity.Insert(1, "\"Key\":\"duplicate\","), entity.Insert(1, "\"K\\u0065y\":\"duplicate\","),
                entity + "{}", entity.Substring(0, entity.Length - 1), entity.Replace("\"Key\":", "\"Key\"::", StringComparison.Ordinal),
                entity.Replace("\"ItemId\":1", "\"ItemId\":1.0", StringComparison.Ordinal),
                entity.Replace("\"ItemId\":1", "\"ItemId\":100000000000000000000", StringComparison.Ordinal) })
            {
                char[] buffer = record.ToCharArray();
                Reject(() => new ReplayDeltaCodec.StreamReader(16).DecodeSharedRecord(buffer, buffer.Length, typeof(ItemFrame), "Key"));
            }
            var reader = new ReplayDeltaCodec.StreamReader(16); char[] valid = entity.ToCharArray();
            Reject(() => reader.DecodeSharedRecord(valid, -1, typeof(ItemFrame), "Key"));
            Reject(() => reader.DecodeSharedRecord(valid, valid.Length + 1, typeof(ItemFrame), "Key"));
        });
        test("typed shared entity decoding resolves immutable full state then exact node delta", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(16); var full = Parse(Json(writer.Encode(Frame(0, Nodes()))));
            string entity = Json(full["Items"]![0]!);
            object snapshot = new ReplayDeltaCodec.StreamReader(16).DecodeSharedRecord(entity, typeof(ItemFrame), "Key");
            full["Items"] = new JArray(new JObject { ["$Shared"] = 7 }); int resolved = 0;
            var stream = new ReplayDeltaCodec.StreamReader(16, (type, id) => { Check(type == typeof(ItemFrame) && id == 7); resolved++; return snapshot; });
            var first = stream.DecodeRecord(Json(full))!; Check(ReferenceEquals(first.Items[0], snapshot) && resolved == 1);
            string update = Json(writer.Encode(Frame(1, Changed(Nodes(), 31)))); var next = stream.DecodeRecord(update)!;
            Check(Json(next) == Json(Frame(1, Changed(Nodes(), 31))) && first.Items[0].Pose.Nodes[1].Active);
        });
        foreach (string bad in new[] { "{\"$Shared\":-1}", "{\"$Shared\":0.0}", "{\"$Shared\":\"0\"}",
            "{\"$Shared\":null}", "{\"$Shared\":2147483648}", "{\"$Shared\":0,\"Key\":\"item\"}",
            "{\"Full\":true,\"$Shared\":0}", "{\"$Shared\":0,\"$Shared\":0}" })
        {
            string reference = bad;
            test("typed shared snapshot rejects invalid reference " + bad, () =>
            {
                var full = Parse(Json(new ReplayDeltaCodec.Writer(16).Encode(Frame(0, Nodes())))); full["Items"] = new JArray("UNIQUE_SHARED_PATCH");
                string line = Json(full).Replace("\"UNIQUE_SHARED_PATCH\"", reference, StringComparison.Ordinal);
                Reject(() => new ReplayDeltaCodec.StreamReader(16, (_, _) => Item(Nodes())).DecodeRecord(line));
            });
        }
        test("typed shared snapshots reject legacy context missing resolver type mismatch and reference chains", () =>
        {
            var full = Parse(Json(new ReplayDeltaCodec.Writer(16).Encode(Frame(0, Nodes()))));
            string entity = Json(full["Items"]![0]!); full["Items"] = new JArray(new JObject { ["$Shared"] = 0 });
            Reject(() => new ReplayDeltaCodec.StreamReader(15, (_, _) => Item(Nodes())).DecodeRecord(Json(full)));
            Reject(() => new ReplayDeltaCodec.StreamReader(16).DecodeRecord(Json(full)));
            Reject(() => new ReplayDeltaCodec.StreamReader(16, (_, _) => new CrateFrame()).DecodeRecord(Json(full)));
            Reject(() => new ReplayDeltaCodec.StreamReader(16).DecodeSharedRecord("{\"$Shared\":0}", typeof(ItemFrame), "Key"));
            Reject(() => new ReplayDeltaCodec.StreamReader(16).DecodeSharedRecord(entity + "{}", typeof(ItemFrame), "Key"));
            Reject(() => new ReplayDeltaCodec.StreamReader(15).DecodeSharedRecord(entity, typeof(ItemFrame), "Key"));
            Reject(() => new ReplayDeltaCodec.StreamReader(16, (_, _) => Item(Nodes())).DecodeSharedRecord(entity, typeof(ItemFrame), "Key"));
            var partial = Parse(entity); partial.Remove("Full");
            Reject(() => new ReplayDeltaCodec.StreamReader(16).DecodeSharedRecord(Json(partial), typeof(ItemFrame), "Key"));
        });
    }

    private static readonly string[] Tracks = { "Items", "Crates", "Creatures", "Spawned", "Interactions" };
    private static NodePose Node(string path) => new(path, new[] { 0f, 1f, 0f }, new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f });
    private static NodePose[] Nodes() => new[] { Node("0:root"), Node("0:root/0:bone") };
    private static NodePose[] Changed(NodePose[] previous, int mask)
    {
        var nodes = previous.ToArray(); var old = nodes[1];
        nodes[1] = new NodePose(old.Path, (mask & 1) == 0 ? old.Position : new[] { .123456789f, -1.25f, .000001f },
            (mask & 2) == 0 ? old.Rotation : new[] { .25f, 0f, 0f, .9682458f },
            (mask & 4) == 0 ? old.Scale : new[] { .75f, 1.5f, .99999994f }, (mask & 8) == 0, (mask & 16) == 0);
        return nodes;
    }
    private static ItemFrame Item(NodePose[] nodes) => new() { Key = "item", ItemId = 1, Pose = new ObjectPose { Nodes = nodes } };
    private static CreatureReplayFrame Creature(NodePose[] nodes) => new() { Key = "creature", Kind = "scoutmaster", Resource = "scoutmaster", Pose = new ObjectPose { Nodes = nodes } };
    private static ReplayFrame Frame(double time, NodePose[] nodes) => new()
    {
        T = time, Items = new[] { Item(nodes) }, Crates = new[] { new CrateFrame { Key = "crate", Kind = "Luggage", Pose = new ObjectPose { Nodes = nodes } } },
        Creatures = new[] { Creature(nodes) }, Spawned = new[] { new SpawnedReplayFrame { Key = "spawned", Kind = "bounce-shroom", Resource = "bounce-shroom", Pose = new ObjectPose { Nodes = nodes } } },
        Interactions = new[] { new InteractionReplayFrame { Key = "interaction", Kind = "break-debris", Resource = "debris", Pose = new ObjectPose { Nodes = nodes } } },
    };
    private static IEnumerable<(ObjectPose Old, ObjectPose New)> PosePairs(ReplayFrame a, ReplayFrame b)
    {
        yield return (a.Items[0].Pose, b.Items[0].Pose); yield return (a.Crates[0].Pose, b.Crates[0].Pose);
        yield return (a.Creatures[0].Pose, b.Creatures[0].Pose); yield return (a.Spawned[0].Pose, b.Spawned[0].Pose);
        yield return (a.Interactions[0].Pose, b.Interactions[0].Pose);
    }
    private static void RejectNodeDelta(string payload)
    {
        var writer = new ReplayDeltaCodec.Writer(16); string first = Json(writer.Encode(Frame(0, Nodes())));
        var second = Parse(Json(writer.Encode(Frame(1, Changed(Nodes(), 1))))); second["Items"]![0]!["Pose"]!["Nodes"] = Patch(payload);
        var tree = new ReplayDeltaCodec.Reader(16); tree.Decode(Parse(first));
        var stream = new ReplayDeltaCodec.StreamReader(16); stream.DecodeRecord(first);
        Reject(() => ReplayRules.Validate(tree.Decode(second), 0)); Reject(() => ReplayRules.Validate(stream.DecodeRecord(Json(second))!, 0));
    }
    private static JToken Patch(string value) => JToken.Parse(value);
    private static JObject Parse(string value) => JObject.Parse(value, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
    private static string Json(object value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static void Check(bool value) { if (!value) throw new Exception("Object node codec assertion failed."); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception error) when (error is InvalidDataException || error is JsonException) { return; } throw new Exception("Invalid object node codec data was accepted."); }
}
