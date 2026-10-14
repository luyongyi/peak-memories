using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class FullReplayArchiveTests
{
    public static void Run(Action<string, Action> test)
    {
        test("typed delta reader matches the independent tree codec in all supported schemas with arbitrary field order", () =>
        {
            foreach (int schema in Enumerable.Range(10, ReplayRules.CurrentSchema - 9))
            {
                var frames = StreamFrames(); var writer = new ReplayDeltaCodec.Writer(schema);
                var records = frames.Select(f => JObject.FromObject(writer.Encode(f))).ToArray();
                var expected = CompareStream(records.Select(Json), schema);
                var reordered = records.Select((r, i) => Json(ReverseProperties(r, i))).ToArray();
                var actual = CompareStream(reordered, schema);
                Check(Json(expected) == Json(actual));
            }
        });
        test("typed sparse deltas preserve previous immutable poses arrays and non JSON joint sample clocks across back seeks", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(); var records = StreamFrames().Select(f => Json(writer.Encode(f))).ToArray();
            var read = CompareStream(records, ReplayRules.CurrentSchema);
            Check(read[0].Actors[0].JointPose[0].SampleTime == 500 && read[1].Actors[0].JointPose[0].SampleTime == 501);
            Check(read[0].Actors[0].Position[0] == 500 && read[1].Actors[0].Position[0] == 501);
            Check(!ReferenceEquals(read[0].Actors[0], read[1].Actors[0]));
            Check(ReferenceEquals(read[0].Actors[0].Appearance, read[1].Actors[0].Appearance));
            var clip = new ReplayClip(); clip.Frames.AddRange(read);
            Check(clip.At(500).Left.Actors[0].JointPose[0].SampleTime == 500);
            Check(clip.At(503).Left.Actors[0].JointPose[0].SampleTime == 503);
            Check(clip.At(500).Left.Actors[0].Position[0] == 500);
        });
        test("typed reader preserves removal reintroduction order and rope point patches without mutating the baseline", () =>
        {
            var source = StreamFrames(); var writer = new ReplayDeltaCodec.Writer();
            var records = source.Select(f => JObject.FromObject(writer.Encode(f))).ToArray();
            Check(records[1]["Ropes"]![0]!["Points"] is JObject);
            var read = CompareStream(records.Select(Json), ReplayRules.CurrentSchema);
            Check(read[0].Items.Select(i => i.Key).SequenceEqual(new[] { "item-a", "item-b" }));
            Check(read[1].Items.Single().Key == "item-b");
            Check(read[2].Items.Select(i => i.Key).SequenceEqual(new[] { "item-a", "item-b" }));
            Check(read[3].Items.Select(i => i.Key).SequenceEqual(new[] { "item-b", "item-a" }));
            Check(read[0].Ropes[0].Points[0].Position[0] == 0 && read[1].Ropes[0].Points[0].Position[0] == 1);
        });
        test("typed reader rejects duplicate unknown incomplete and schema forbidden fields and invalid primitive types", () =>
        {
            foreach (int schema in Enumerable.Range(10, ReplayRules.CurrentSchema - 9))
            {
                string original = Json(new ReplayDeltaCodec.Writer(schema).Encode(StreamFrames()[0]));
                void Bad(Action<JObject> change)
                {
                    var token = JObject.Parse(original); change(token); RejectBoth(Json(token), schema);
                }
                Bad(t => t["Unexpected"] = 1);
                Bad(t => t["World"]!["Unexpected"] = 1);
                Bad(t => ((JObject)t["World"]!).Remove("Day"));
                Bad(t => t["World"]!["Day"] = "1");
                Bad(t => t["Actors"]![0]!["Full"] = false);
                Bad(t => t["Actors"]![0]!["Unexpected"] = true);
                Bad(t => t["ActorOrder"] = new JArray("missing"));
                Bad(t => t["RemovedActors"] = new JArray("actor"));
                if (schema == 10) Bad(t => t["Actors"]![0]!["HudState"] = null);
                if (schema < 12) Bad(t => t["World"]!["Environment"] = null);
                if (schema < 14) Bad(t => t["Actors"]![0]!["RouteState"] = null);
                RejectBoth(original[..^1] + ",\"T\":500}", schema);
                string world = Json(JObject.Parse(original)["World"]!);
                RejectBoth(original.Replace(world, world[..^1] + ",\"Day\":1}"), schema);
                string actor = Json(JObject.Parse(original)["Actors"]![0]!);
                RejectBoth(original.Replace(actor, actor[..^1] + ",\"Full\":true}"), schema);
            }
        });
        test("typed reader wraps overflowing integers and rejects malformed rope delta tuples", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(); var records = StreamFrames().Select(f => JObject.FromObject(writer.Encode(f))).ToArray();
            var overflow = (JObject)records[0].DeepClone(); overflow["World"]!["Day"] = long.MaxValue;
            RejectBoth(Json(overflow), ReplayRules.CurrentSchema);
            foreach (Action<JObject> change in new Action<JObject>[] {
                t => t["Ropes"]![0]!["Points"]!["Length"] = 2,
                t => t["Ropes"]![0]!["Points"]!["Changes"]![0]![0] = 99,
                t => t["Ropes"]![0]!["Points"]!["Changes"]![0]![1] = 32,
                t => ((JArray)t["Ropes"]![0]!["Points"]!["Changes"]![0]!).Add(1),
                t => ((JArray)t["Ropes"]![0]!["Points"]!["Changes"]!).Add(t["Ropes"]![0]!["Points"]!["Changes"]![0]!.DeepClone()),
            })
            {
                var bad = (JObject)records[1].DeepClone(); change(bad);
                RejectBoth(Json(bad), ReplayRules.CurrentSchema, Json(records[0]));
            }
        });
        test("typed reader checks inherited creature kind line limits when Kind follows Line", () =>
        {
            var frame = StreamFrames()[0];
            frame.Creatures = new[] { new CreatureReplayFrame { Key = "frog", Kind = "frog-tongue", Resource = "frog", Line = new CreatureReplayLine { Points = new float[CreatureReplayRules.MaximumFrogLinePoints * 3] } } };
            var writer = new ReplayDeltaCodec.Writer(); var first = JObject.FromObject(writer.Encode(frame));
            CompareStream(new[] { Json(ReverseProperties(first, 0)) }, ReplayRules.CurrentSchema);
            var changed = (JObject)first.DeepClone(); changed["ObjectsFull"] = false;
            changed["Creatures"] = new JArray(new JObject { ["Key"] = "frog", ["Kind"] = "spider-silk" });
            RejectBoth(Json(changed), ReplayRules.CurrentSchema, Json(first));
        });
        test("typed footer parsing preserves arbitrary property order and strict duplicate and trailing JSON rejection", () =>
        {
            var reader = new ReplayDeltaCodec.StreamReader(ReplayRules.CurrentSchema);
            Check(reader.DecodeRecord("{\"Ignored\":{\"x\":1},\"Type\":\"end\"}") == null);
            Reject(() => reader.DecodeRecord("{\"Type\":\"end\",\"Type\":\"end\"}"));
            Reject(() => reader.DecodeRecord("{\"Type\":\"end\"} {}"));
        });
        test("typed reader retains only bounded empty patch scratch and never references reused input characters", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(); var reader = new ReplayDeltaCodec.StreamReader(ReplayRules.CurrentSchema);
            var frames = new List<ReplayFrame>(); var snapshots = new List<string>();
            foreach (var frame in StreamFrames())
            {
                string record = Json(writer.Encode(frame)); char[] buffer = record.ToCharArray();
                var decoded = reader.DecodeRecord(buffer, buffer.Length)!;
                frames.Add(decoded); snapshots.Add(Json(decoded)); Array.Fill(buffer, 'x');
            }
            for (int i = 0; i < frames.Count; i++) Check(Json(frames[i]) == snapshots[i]);
            var pool = (System.Collections.ICollection)typeof(ReplayDeltaCodec.StreamReader).GetField("patchPool", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(reader)!;
            Check(pool.Count > 0 && pool.Count <= 2048);
            foreach (object patch in pool)
            {
                var type = patch.GetType();
                Check(((System.Collections.ICollection)type.GetField("Fields", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(patch)!).Count == 0);
                Check(type.GetField("Key", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(patch) == null);
            }
            foreach (string field in new[] { "arrayScratch", "floatScratch" })
            {
                var scratch = (System.Collections.ICollection)typeof(ReplayDeltaCodec.StreamReader).GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(reader)!;
                Check(scratch.Count <= 4);
                foreach (System.Collections.IList list in scratch)
                {
                    Check(list.Count == 0);
                    Check((int)list.GetType().GetProperty("Capacity")!.GetValue(list)! <= 1024);
                }
            }
        });
        test("typed full baselines overwrite prototype defaults and keep distinct mutable model containers in every supported schema", () =>
        {
            foreach (int schema in Enumerable.Range(10, ReplayRules.CurrentSchema - 9))
            {
                var source = new[] { Frame(500), Frame(501) };
                foreach (var frame in source) frame.Items[0].Pose.Nodes = new[] { new NodePose("rig/hand", new[] { (float)frame.T, 3f, 4f }, new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f }) };
                // A fresh writer deliberately emits two complete baselines.
                var decoded = CompareStream(source.Select(f => Json(new ReplayDeltaCodec.Writer(schema).Encode(f))), schema);
                Check(!ReferenceEquals(decoded[0].Actors[0], decoded[1].Actors[0]));
                Check(!ReferenceEquals(decoded[0].Actors[0].Appearance, decoded[1].Actors[0].Appearance));
                Check(!ReferenceEquals(decoded[0].Items[0].Pose, decoded[1].Items[0].Pose));
                Check(!ReferenceEquals(decoded[0].Items[0].Pose.Nodes[0], decoded[1].Items[0].Pose.Nodes[0]));
                Check(!ReferenceEquals(decoded[0].Items[0].Pose.Nodes[0].Position, decoded[1].Items[0].Pose.Nodes[0].Position));
                Check(decoded[0].Items[0].Pose.Nodes[0].SampleTime == 0);
                if (schema == 10) Check(decoded[0].Actors[0].HudState == null);
                if (schema < 12) Check(decoded[0].Actors[0].WebWrap == null && decoded[0].World.Environment == null && decoded[0].Items[0].Visuals == null);
                if (schema < 14) Check(decoded[0].Actors[0].RouteState == null);
            }
        });
        test("typed small vector sharing preserves every bit including negative zero and stays inside one reader", () =>
        {
            var reader = new ReplayDeltaCodec.StreamReader(ReplayRules.CurrentSchema); var other = new ReplayDeltaCodec.StreamReader(ReplayRules.CurrentSchema);
            var method = typeof(ReplayDeltaCodec.StreamReader).GetMethod("SmallFloats", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            float[] Vector(ReplayDeltaCodec.StreamReader target, int length, float a, float b = 0, float c = 0, float d = 0) =>
                (float[])method.Invoke(target, new object[] { length, a, b, c, d })!;
            for (int length = 1; length <= 4; length++)
            for (int mask = 0; mask < 1 << length; mask++)
            {
                float[] values = Enumerable.Range(0, 4).Select(i => (float)((mask >> i) & 1)).ToArray();
                var first = Vector(reader, length, values[0], values[1], values[2], values[3]);
                Check(first.Length == length && first.SequenceEqual(values.Take(length)));
                Check(ReferenceEquals(first, Vector(reader, length, values[0], values[1], values[2], values[3])));
                Check(!ReferenceEquals(first, Vector(other, length, values[0], values[1], values[2], values[3])));
            }
            float negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
            var negative = Vector(reader, 3, negativeZero);
            Check(BitConverter.SingleToInt32Bits(negative[0]) == unchecked((int)0x80000000));
            Check(!ReferenceEquals(negative, Vector(reader, 3, 0)));
            Check(ReferenceEquals(Vector(reader, 3, 2), Vector(reader, 3, 2)));
            var cached = (Array)typeof(ReplayDeltaCodec.StreamReader).GetField("simpleVectors", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(reader)!;
            Check(cached.Length == 30);
            var token = JObject.FromObject(new ReplayDeltaCodec.Writer().Encode(Frame(500)));
            token["Actors"]![0]!["Position"] = new JRaw("[-0.0,0.0,0.0]");
            var decoded = reader.DecodeRecord(Json(token))!;
            Check(BitConverter.SingleToInt32Bits(decoded.Actors[0].Position[0]) == unchecked((int)0x80000000));
        });
        test("bounded arbitrary vector reuse compares all float bits and eviction never overwrites prior values", () =>
        {
            var reader = new ReplayDeltaCodec.StreamReader(ReplayRules.CurrentSchema);
            var method = typeof(ReplayDeltaCodec.StreamReader).GetMethod("SmallFloats", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            float[] Vector(int length, float a, float b = 0, float c = 0, float d = 0) =>
                (float[])method.Invoke(reader, new object[] { length, a, b, c, d })!;
            foreach (int bits in new[] { unchecked((int)0x80000000), 0x3f000001, 0x7fc00001, 0x7fc00002, unchecked((int)0xffc00001), 1, int.MaxValue })
            {
                float value = BitConverter.Int32BitsToSingle(bits);
                for (int length = 1; length <= 4; length++)
                {
                    var first = Vector(length, value, value, value, value);
                    Check(first.All(v => BitConverter.SingleToInt32Bits(v) == bits));
                    Check(ReferenceEquals(first, Vector(length, value, value, value, value)));
                    if (length > 1) Check(!ReferenceEquals(first, Vector(length - 1, value, value, value, value)));
                }
            }
            var held = Enumerable.Range(2, 32).Select(i => Vector(3, i, i + .5f, -i)).ToArray();
            // More unique vectors than slots forces collisions and replacement.
            for (int i = 100; i < 8500; i++)
            {
                var vector = Vector(3, i, i + .5f, -i);
                Check(vector.SequenceEqual(new[] { (float)i, i + .5f, -i }));
            }
            for (int i = 0; i < held.Length; i++) Check(held[i].SequenceEqual(new[] { (float)i + 2, i + 2.5f, -i - 2f }));
            var cache = (float[]?[])typeof(ReplayDeltaCodec.StreamReader).GetField("shortVectors", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(reader)!;
            Check(cache.Length == 8160 && cache.All(v => v == null || v.Length is >= 1 and <= 4));
        });
        test("complete JSON node reuse preserves bit distinctions property order hash collisions and independent joint clocks", () =>
        {
            foreach (int schema in Enumerable.Range(10, ReplayRules.CurrentSchema - 9))
            {
                const string firstPath = "udbxxqgemc", collidingPath = "myqknzbqmz";
                var source = Enumerable.Range(0, 9).Select(i => Frame(500 + i)).ToArray();
                for (int i = 0; i < source.Length; i++)
                {
                    source[i].Items[0].Pose.Nodes = new[] { new NodePose(firstPath, new[] { 2.5f, 0f, 0f }, new[] { .125f, .25f, 0f, 1f }, new[] { 1f, 1f, 1f }) };
                    source[i].Actors[0].JointPose = new[] { new NodePose(".", new[] { 2.5f, 0f, 0f }, new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f }, true, true, 500 + i) };
                }
                source[2].Items[0].Pose.Nodes[0].Path = collidingPath;
                source[4].Items[0].Pose.Nodes[0].Active = false;
                source[5].Items[0].Pose.Nodes[0].Visible = false;
                source[6].Items[0].Pose.Nodes[0].Position[0] = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(2.5f) + 1);
                source[8].Items[0].Pose.Nodes[0].Path = "rig/雪";
                var records = source.Select(f => JObject.FromObject(new ReplayDeltaCodec.Writer(schema).Encode(f))).ToArray();
                records[7]["Items"]![0]!["Pose"]!["Nodes"]![0]!["Position"] = new JRaw("[2.5,-0.0,0.0]");
                var read = CompareStream(records.Select((r, i) => Json(ReverseProperties(r, i))), schema);
                NodePose Node(int i) => read[i].Items[0].Pose.Nodes[0];
                Check(ReferenceEquals(Node(0), Node(1)));
                Check(Node(2).Path == collidingPath && Node(3).Path == firstPath && !ReferenceEquals(Node(2), Node(3)));
                Check(Node(0).Active && !Node(4).Active && Node(0).Visible && !Node(5).Visible);
                Check(BitConverter.SingleToInt32Bits(Node(0).Position[0]) + 1 == BitConverter.SingleToInt32Bits(Node(6).Position[0]));
                Check(BitConverter.SingleToInt32Bits(Node(0).Position[1]) == 0 && BitConverter.SingleToInt32Bits(Node(7).Position[1]) == unchecked((int)0x80000000));
                Check(Node(8).Path == "rig/雪" && read.All(f => BitConverter.DoubleToInt64Bits(f.Items[0].Pose.Nodes[0].SampleTime) == 0));
                for (int i = 0; i < read.Length; i++) Check(read[i].Actors[0].JointPose[0].SampleTime == 500 + i);
                Check(!ReferenceEquals(read[0].Actors[0].JointPose[0], read[1].Actors[0].JointPose[0]));
                var reader = new ReplayDeltaCodec.StreamReader(schema); reader.DecodeRecord(Json(records[0]));
                var cache = (NodePose?[])typeof(ReplayDeltaCodec.StreamReader).GetField("sharedNodes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(reader)!;
                Check(cache.Length == 4096 && cache.All(n => n == null || BitConverter.DoubleToInt64Bits(n.SampleTime) == 0));
            }
        });
        test("typed metadata string reuse preserves escaped paths and prior frames across complete baselines", () =>
        {
            var source = new[] { Frame(500), Frame(501) };
            foreach (var frame in source)
            {
                frame.Items[0].Name = "岩石\"\\雪";
                frame.Items[0].Pose.Nodes = new[] { new NodePose("rig/hand/child", new[] { (float)frame.T, 2f, 3f }, new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f }) };
            }
            var records = source.Select(f => Json(new ReplayDeltaCodec.Writer().Encode(f))).ToArray();
            var decoded = CompareStream(records, ReplayRules.CurrentSchema);
            Check(ReferenceEquals(decoded[0].Items[0].Key, decoded[1].Items[0].Key));
            Check(ReferenceEquals(decoded[0].Items[0].Name, decoded[1].Items[0].Name));
            Check(ReferenceEquals(decoded[0].Items[0].Pose.Nodes[0].Path, decoded[1].Items[0].Pose.Nodes[0].Path));
            Check(ReferenceEquals(decoded[0].Items[0].Pose.Rotation, decoded[1].Items[0].Pose.Rotation));
            Check(decoded[0].Items[0].Pose.Nodes[0].Position[0] == 500 && decoded[1].Items[0].Pose.Nodes[0].Position[0] == 501);
            var other = new ReplayDeltaCodec.StreamReader(ReplayRules.CurrentSchema).DecodeRecord(records[0])!;
            Check(!ReferenceEquals(decoded[0].Items[0].Pose.Nodes[0].Path, other.Items[0].Pose.Nodes[0].Path));
        });
        test("typed metadata string scratch enforces both entry and character caps without retaining overflow values", () =>
        {
            var method = typeof(ReplayDeltaCodec.StreamReader).GetMethod("SharedText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(string) }, null)!;
            string Share(ReplayDeltaCodec.StreamReader reader, string value) => (string)method.Invoke(reader, new object[] { value })!;
            var entries = new ReplayDeltaCodec.StreamReader(ReplayRules.CurrentSchema);
            for (int i = 0; i < 4100; i++) Share(entries, "metadata-" + i);
            var strings = (System.Collections.IDictionary)typeof(ReplayDeltaCodec.StreamReader).GetField("sharedStrings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(entries)!;
            Check(strings.Count == 4096);
            var buckets = (System.Collections.IDictionary)typeof(ReplayDeltaCodec.StreamReader).GetField("sharedStringBuckets", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(entries)!;
            Check(buckets.Values.Cast<System.Collections.ICollection>().Sum(bucket => bucket.Count) == strings.Count);
            string original = (string)strings["metadata-0"]!;
            Check(ReferenceEquals(original, Share(entries, new string("metadata-0".ToCharArray()))));
            Check(!strings.Contains("metadata-4099"));
            var characters = new ReplayDeltaCodec.StreamReader(ReplayRules.CurrentSchema);
            string almostFull = new('x', 1024 * 1024 - 1); Share(characters, almostFull);
            string overflow = new string(new[] { 'y', 'z' }); Check(ReferenceEquals(overflow, Share(characters, overflow)));
            var bounded = (System.Collections.IDictionary)typeof(ReplayDeltaCodec.StreamReader).GetField("sharedStrings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(characters)!;
            Check(bounded.Count == 1 && !bounded.Contains(overflow));
            Check((int)typeof(ReplayDeltaCodec.StreamReader).GetField("sharedCharacters", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(characters)! == almostFull.Length);
        });
        test("canonical metadata span lookup compares full text on hash collisions and preserves prior frames", () =>
        {
            const string firstName = "udbxxqgemc", secondName = "myqknzbqmz";
            var hash = typeof(ReplayDeltaCodec.StreamReader).GetMethod("TextHash", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            Check((int)hash.Invoke(null, new object[] { firstName })! == (int)hash.Invoke(null, new object[] { secondName })!);
            var source = new[] { Frame(500), Frame(501), Frame(502) };
            source[0].Items[0].Name = source[2].Items[0].Name = firstName; source[1].Items[0].Name = secondName;
            var decoded = CompareStream(source.Select(f => Json(new ReplayDeltaCodec.Writer().Encode(f))), ReplayRules.CurrentSchema);
            Check(decoded[0].Items[0].Name == firstName && decoded[1].Items[0].Name == secondName && decoded[2].Items[0].Name == firstName);
            Check(!ReferenceEquals(decoded[0].Items[0].Name, decoded[1].Items[0].Name));
            Check(ReferenceEquals(decoded[0].Items[0].Name, decoded[2].Items[0].Name));
        });
        test("canonical numeric tokens retain integer versus float conversion and exact legacy fallback bits", () =>
        {
            foreach (string value in new[] { "-0.0", "9007199791611905", "0.1", "1.0000000596046448", "0.100000001490116119384765625", "1e-320" })
            {
                var token = JObject.FromObject(new ReplayDeltaCodec.Writer().Encode(Frame(500)));
                token["Actors"]![0]!["Stamina"] = new JRaw(value);
                string record = Json(token); char[] characters = record.ToCharArray();
                var canonical = new ReplayDeltaCodec.StreamReader(ReplayRules.CurrentSchema).DecodeRecord(characters, characters.Length)!;
                // A JSON comment deliberately selects the unchanged adapter.
                var compatibility = new ReplayDeltaCodec.StreamReader(ReplayRules.CurrentSchema).DecodeRecord("/*legacy*/" + record)!;
                Check(BitConverter.SingleToInt32Bits(canonical.Actors[0].Stamina) == BitConverter.SingleToInt32Bits(compatibility.Actors[0].Stamina));
                Check(Json(canonical) == Json(compatibility));
                Array.Fill(characters, 'x');
                Check(canonical.Actors[0].Id == "actor");
            }
        });
        test("typed cached field writers preserve scalar bits unsigned boundaries and nullable sparse replacements", () =>
        {
            var first = Frame(500); var second = Frame(501);
            first.Items[0].Visuals = new[] { new NativeRendererFrame { RenderingLayerMask = uint.MaxValue } };
            first.Items[0].Lights = new[] { new NativeLightFrame { RenderingLayerMask = uint.MaxValue, CullingMask = int.MinValue } };
            first.Effects[0].Seed = uint.MaxValue;
            first.Effects[0].ShaderRandom = BitConverter.Int32BitsToSingle(0x3eaaaaab);
            first.Effects[0].Phase = BitConverter.Int64BitsToDouble(0x3fd5555555555555);
            first.Events = new[] { new ItemEvent { T = 500, Sequence = long.MaxValue, Kind = "use", ActorId = "actor", ItemKey = "item" } };
            var writer = new ReplayDeltaCodec.Writer(); var initial = JObject.FromObject(writer.Encode(first));
            initial["Items"]![0]!["Fuel"] = new JRaw("-0.0");
            var read = CompareStream(new[] { Json(initial), Json(writer.Encode(second)) }, ReplayRules.CurrentSchema);
            Check(read[0].Items[0].Visuals![0].RenderingLayerMask == uint.MaxValue);
            Check(read[0].Items[0].Lights![0].RenderingLayerMask == uint.MaxValue && read[0].Items[0].Lights![0].CullingMask == int.MinValue);
            Check(BitConverter.SingleToInt32Bits(read[0].Items[0].Fuel) == unchecked((int)0x80000000));
            Check(BitConverter.SingleToInt32Bits(read[0].Effects[0].ShaderRandom) == 0x3eaaaaab);
            Check(BitConverter.DoubleToInt64Bits(read[0].Effects[0].Phase) == 0x3fd5555555555555);
            Check(read[0].Effects[0].Seed == uint.MaxValue && read[0].Events[0].Sequence == long.MaxValue);
            Check(read[1].Items[0].Visuals == null && read[1].Items[0].Lights == null && read[1].Events.Length == 0);
        });
        test("reusable record buffers preserve both character and UTF8 byte limits", () =>
        {
            try { ReplayFiles.Read(new StringReader(new string(' ', ReplayRules.MaxLineCharacters + 1))); }
            catch (InvalidDataException e) { Check(e.Message.Contains("8 MiB")); goto Utf8; }
            throw new Exception("Oversized character record was accepted.");
        Utf8:
            try { ReplayFiles.Read(new StringReader(new string('汉', ReplayRules.MaxLineCharacters / 2 + 1))); }
            catch (InvalidDataException e) { Check(e.Message.Contains("UTF-8")); return; }
            throw new Exception("Oversized UTF8 record was accepted.");
        });
        test("loader text scratch reuse never changes old frame strings vectors negative zero or joint clocks", () =>
        {
            using var pool = new ReplayLineBufferPool();
            var original = StreamFrames(); original[0].Items[0].Name = "old-岩石\\\"";
            string text = BufferedText(original, 65536, negativeZero: true);
            var first = BufferedRead(text, pool);
            string snapshot = Json(first.Frames);
            var node = first.Frames[0].Actors[0].JointPose[0];
            var position = first.Frames[0].Actors[0].Position;
            long sampleBits = BitConverter.DoubleToInt64Bits(node.SampleTime);
            Check(BitConverter.SingleToInt32Bits(position[0]) == unchecked((int)0x80000000));
            var storage = pool.Rent(); Check(storage.Length >= 65536);
            Array.Fill(storage, 'x'); pool.Return(storage);
            var replacement = StreamFrames(); replacement[0].Items[0].Name = "replacement";
            var second = BufferedRead(BufferedText(replacement, 131072), pool);
            Check(second.Frames[0].Items[0].Name == "replacement" && first.Frames[0].Items[0].Name == "old-岩石\\\"");
            Check(Json(first.Frames) == snapshot && ReferenceEquals(first.Frames[0].Actors[0].Position, position));
            Check(BitConverter.SingleToInt32Bits(position[0]) == unchecked((int)0x80000000));
            Check(ReferenceEquals(first.Frames[0].Actors[0].JointPose[0], node) && BitConverter.DoubleToInt64Bits(node.SampleTime) == sampleBits);
        });
        test("shared text scratch isolates full effect and subsequent full decoder baselines", () =>
        {
            using var pool = new ReplayLineBufferPool();
            string text = BufferedText(StreamFrames(), 16384);
            var full = BufferedRead(text, pool);
            var history = BufferedRead(text, pool, effectsOnly: true);
            Check(history.Frames.Count == full.Frames.Count && history.Frames.All(f => f.Actors.Length == 0 && f.Items.Length == 0));
            Check(Json(history.Frames.Select(FullReplayArchive.EffectsOnlyFrame)) == Json(full.Frames.Select(FullReplayArchive.EffectsOnlyFrame)));
            var replacement = Simple(600); replacement.Actors[0].Id = "second-recording";
            var second = BufferedRead(BufferedText(new[] { replacement }), pool);
            Check(second.Frames.Single().Actors.Single().Id == "second-recording" && second.Frames[0].Items.Length == 0);
            var writer = new ReplayDeltaCodec.Writer(); writer.Encode(Frame(500));
            string noBaseline = Json(Header()) + "\n" + Json(writer.Encode(Frame(501))) + "\n{\"Type\":\"end\"}\n";
            Reject(() => BufferedRead(noBaseline, pool));
            Reject(() => BufferedRead(noBaseline, pool, effectsOnly: true));
            Check(BufferedRead(text, pool).Frames[0].Actors[0].Id == "actor");
        });
        test("simultaneous buffered reads lease independent storage and preserve both decoded results", () =>
        {
            using var pool = new ReplayLineBufferPool();
            var seeded = new char[131072]; pool.Return(seeded);
            using var entered = new CountdownEvent(2); using var resume = new ManualResetEventSlim();
            Task<ReplayClip> Start(string name) => Task.Run(() =>
            {
                var frame = Simple(600); frame.Actors[0].Id = name;
                using var reader = new ObservingTextReader(BufferedText(new[] { frame }, 65536), _ =>
                { entered.Signal(); Check(resume.Wait(TimeSpan.FromSeconds(10)), "Concurrent reader was not released."); }, once: true);
                return ReplayFiles.ReadBuffered(reader, pool, default, false, ReplayRules.MaxBytes, FullReplayArchive.MaximumDuration);
            });
            var first = Start("first-reader"); var second = Start("second-reader");
            try
            {
                Check(entered.Wait(TimeSpan.FromSeconds(10)), "Concurrent page reads did not both acquire storage.");
                var thirdLease = pool.Rent(); Check(!ReferenceEquals(thirdLease, seeded)); pool.Return(thirdLease);
            }
            finally { resume.Set(); }
            Check(Task.WaitAll(new Task[] { first, second }, TimeSpan.FromSeconds(10)));
            Check(first.Result.Frames[0].Actors[0].Id == "first-reader" && second.Result.Frames[0].Actors[0].Id == "second-reader");
            var retained = pool.Rent(); Check(retained.Length == seeded.Length); pool.Return(retained);
        });
        test("buffer leases return after normal reads cancelled reads bad headers bad records and input failures", () =>
        {
            using var pool = new ReplayLineBufferPool(); var seeded = new char[131072]; pool.Return(seeded);
            void Returned() { var actual = pool.Rent(); Check(ReferenceEquals(actual, seeded)); pool.Return(actual); }
            string text = BufferedText(StreamFrames(), 16384);
            BufferedRead(text, pool); Returned();
            Reject(() => BufferedRead("not-json\n", pool)); Returned();
            Reject(() => BufferedRead(Json(Header()) + "\n{\"Type\":\"frame\",\"T\":500}\n", pool)); Returned();
            using (var source = new CancellationTokenSource())
            {
                using var reader = new ObservingTextReader(text, index => { if (index == text.IndexOf('\n') + 5) source.Cancel(); });
                bool cancelled = false;
                try { ReplayFiles.ReadBuffered(reader, pool, source.Token, false, ReplayRules.MaxBytes, FullReplayArchive.MaximumDuration); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled); Returned();
            }
            using (var source = new CancellationTokenSource())
            {
                source.Cancel(); bool cancelled = false;
                try { BufferedRead(text, pool, cancellation: source.Token); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled); Returned();
            }
            using (var failed = new ObservingTextReader(text, index => { if (index == 100) throw new IOException("Synthetic text read failure."); }))
                Reject(() => ReplayFiles.ReadBuffered(failed, pool, default, false, ReplayRules.MaxBytes, FullReplayArchive.MaximumDuration));
            Returned(); Check(BufferedRead(text, pool).Frames.Count == 4);
        });
        test("pooled line growth retains only bounded final storage and keeps character and UTF8 limits", () =>
        {
            using var pool = new ReplayLineBufferPool();
            Reject(() => BufferedRead(new string(' ', ReplayRules.MaxLineCharacters + 1), pool));
            var large = pool.Rent(); Check(large.Length == ReplayRules.MaxLineCharacters); pool.Return(large);
            Reject(() => BufferedRead(new string('汉', ReplayRules.MaxLineCharacters / 2 + 1), pool));
            var same = pool.Rent(); Check(ReferenceEquals(same, large)); pool.Return(same);
            pool.Return(new char[4096]); Check(ReferenceEquals(pool.Rent(), large));
            pool.Return(large);
            var tooLarge = new char[ReplayRules.MaxLineCharacters + 1]; pool.Return(tooLarge);
            Check(ReferenceEquals(pool.Rent(), large));
            Check(BufferedRead(BufferedText(new[] { Simple(600) }), pool).Frames.Count == 1);
        });
        test("disposing a scratch pool during an active read drops its returning buffer", () =>
        {
            using var pool = new ReplayLineBufferPool(); var seeded = new char[131072]; pool.Return(seeded);
            using var reader = new ObservingTextReader(BufferedText(new[] { Simple(600) }, 65536), _ => pool.Dispose(), once: true);
            var clip = ReplayFiles.ReadBuffered(reader, pool, default, false, ReplayRules.MaxBytes, FullReplayArchive.MaximumDuration);
            Check(clip.Frames.Single().Actors.Single().Id == "actor" && PoolSpare(pool) == null);
            pool.Return(seeded); Check(PoolSpare(pool) == null);
            bool rejected = false; try { pool.Rent(); } catch (ObjectDisposedException) { rejected = true; }
            Check(rejected);
        });
        test("warm large-record reads avoid repeat growth allocation while preserving the same output", () =>
        {
            string text = BufferedText(new[] { Simple(600) }, 1024 * 1024);
            using var warm = new ReplayLineBufferPool(); BufferedRead(text, warm);
            var storage = warm.Rent(); int capacity = storage.Length; warm.Return(storage);
            using var cold = new ReplayLineBufferPool();
            long before = GC.GetAllocatedBytesForCurrentThread(); var coldClip = BufferedRead(text, cold);
            long coldBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread(); var warmClip = BufferedRead(text, warm);
            long warmBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(coldBytes - warmBytes >= 2L * capacity, "Warm read still allocated its retained character buffer.");
            Check(Json(coldClip.Frames) == Json(warmClip.Frames));
            Check(ReferenceEquals(warm.Rent(), storage));
        });
        test("playback source shares scratch across reads and disposal releases idle and returning storage", () => InRoot(root =>
        {
            string path = SaveLegacy(root, StreamFrames(), splitAt: 2);
            var info = FullReplayArchive.ReadInfo(path);
            using var source = FullReplayArchive.OpenPlayback(path, info, createSeekCaches: false);
            var pool = (ReplayLineBufferPool)typeof(FullReplayPlaybackSource).GetField("readBuffers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(source)!;
            source.Load(0, default); Check(PoolSpare(pool) != null);
            var first = source.LoadHistory(1, default); Check(first.Frames.All(f => f.Actors.Length == 0));
            Check(source.Load(1, default).Frames.All(f => f.Actors.Length == 1));
            var returning = pool.Rent(); source.Dispose(); pool.Return(returning);
            Check(PoolSpare(pool) == null);
            bool rejected = false; try { source.Load(0, default); } catch (ObjectDisposedException) { rejected = true; }
            Check(rejected);
            using var idleSource = FullReplayArchive.OpenPlayback(path, info, createSeekCaches: false);
            var idlePool = (ReplayLineBufferPool)typeof(FullReplayPlaybackSource).GetField("readBuffers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(idleSource)!;
            idleSource.Load(0, default); Check(PoolSpare(idlePool) != null);
            idleSource.Dispose(); Check(PoolSpare(idlePool) == null);
        }));
        test("full replay streams one file and preserves a seekable complete 180-second timeline", () => InRoot(root =>
        {
            var original = Enumerable.Range(0, 181).Select(i => Frame(500 + i, i)).ToArray();
            string unchanged = Json(original[0]); var header = Header();
            var writer = new FullReplayWriter(root, header); header.Participants[0] = "mutated";
            foreach (var frame in original) Check(writer.TryEnqueue(frame));
            Wait(() => writer.Stats.WrittenFrames == original.Length || writer.Fault != null);
            Check(writer.Fault == null && File.Exists(writer.PartialPath) && !File.Exists(writer.FilePath));
            Check(Directory.GetFiles(root).Length == 1 && Directory.GetDirectories(root).Length == 0);
            var result = Done(writer); Check(result.Status == "completed" && result.WrittenFrames == 181 && result.Duration == 180);
            Check(Directory.GetFiles(root).Single() == result.FilePath && result.FilePath.EndsWith(".peakrun"));
            var info = FullReplayArchive.ReadInfo(result.FilePath);
            Check(info.Version == 4 && info.Complete && info.Duration == 180 && info.FrameCount == 181 && info.Pages.Length == 180);
            Check(info.Pages.All(p => p.End - p.Start <= 1.000001 && p.EffectHistory != null));
            Check(info.ActorIds.SequenceEqual(new[] { "actor" }) && info.Header.Participants[0] == "Synthetic");
            var expected = new ContinuousReplayRebaser(500); var unique = new HashSet<double>();
            foreach (int index in Enumerable.Range(0, info.Pages.Length).Reverse())
            {
                var page = info.Pages[index]; var read = FullReplayArchive.ReadPage(result.FilePath, info, index);
                Check(read.Complete && read.Frames.Count == page.FrameCount && read.Frames[0].T == page.Start && read.Duration == page.End);
                foreach (var frame in read.Frames)
                {
                    var source = original.Single(f => f.T == frame.T + 500);
                    Check(JToken.DeepEquals(JToken.Parse(Json(expected.Apply(source))), JToken.Parse(Json(frame))));
                    unique.Add(frame.T);
                }
            }
            Check(unique.Count == 181 && unique.Contains(180) && Json(original[0]) == unchanged);
            var mid = FullReplayArchive.ReadPage(result.FilePath, info, 135).At(135.5);
            Check(mid.Left.T == 135 && mid.Right.T == 136 && mid.Mix == 0); // A 1 Hz capture gap is held, never invented.
        }));
        test("full replay bound pages preserve all samples across very large gaps including isolated frames", () => InRoot(root =>
        {
            var writer = new FullReplayWriter(root, Header());
            foreach (double t in new[] { 100d, 101, 500 }) Check(writer.TryEnqueue(Frame(t)));
            var result = Done(writer); var info = FullReplayArchive.ReadInfo(result.FilePath);
            Check(result.Status == "completed" && info.Duration == 400 && info.FrameCount == 3 && info.Pages.Length == 2);
            Check(info.Pages[1].FrameCount == 1 && !info.Pages[1].Overlap && info.Pages[1].FirstFrameIndex == 2);
            Check(FullReplayArchive.ReadPage(result.FilePath, info, 1).Frames.Single().T == 400);
            Check(info.LargestGapSeconds == 399 && info.GapOver500ms == 2);
        }));
        test("playback page loader snapshots mutable index metadata once and preserves page validation", () => InRoot(root =>
        {
            string path = Save(root); var info = FullReplayArchive.ReadInfo(path);
            var load = FullReplayArchive.CreatePageLoader(path, info);
            info.ActorIds[0] = "changed";
            info.Header.Participants[0] = "changed";
            info.Pages[0].Offset = 0;
            Reject(() => FullReplayArchive.ReadPage(path, info, 0));
            var page = load(0, CancellationToken.None);
            Check(page.Complete && page.Frames[0].Actors[0].Id == "actor" && page.Header.Participants[0] == "Synthetic");
            var fresh = FullReplayArchive.ReadInfo(path); var bound = fresh.Pages[0];
            using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                file.Position = bound.Offset + bound.Length - 8; int original = file.ReadByte();
                file.Position--; file.WriteByte((byte)(original ^ 128));
            }
            Reject(() => load(0, CancellationToken.None));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { load(0, cancelled.Token); }
            catch (OperationCanceledException) { return; }
            throw new Exception("Cancelled playback page load did not throw.");
        }));
        test("full replay zero and one frames cannot create a successful final file", () => InRoot(root =>
        {
            foreach (int count in new[] { 0, 1 })
            {
                var writer = new FullReplayWriter(root, Header()); if (count != 0) Check(writer.TryEnqueue(Frame(0)));
                var result = Done(writer);
                Check(result.Status == "faulted" && result.FaultCode == "insufficient-frames");
                Check(File.Exists(writer.PartialPath) && !File.Exists(writer.FilePath));
            }
        }));
        test("full replay preserves between-sample events at budget-induced non-overlap page boundaries", () => InRoot(root =>
        {
            var first = Simple(100); var second = Simple(101); var third = Simple(102);
            second.Events = new[] { new ItemEvent { Sequence = 17, T = 100.25, Kind = "use", ActorId = "actor", ItemKey = "item" } };
            third.Events = new[] { new ItemEvent { Sequence = 18, T = 101.9, Kind = "throw", ActorId = "actor", ItemKey = "item" } };
            var source = new[] { first, second, third };
            var writer = new FullReplayWriter(root, Header(), new ContinuousReplayOptions { SegmentDecodedByteLimit = 2300 });
            foreach (var frame in source) Check(writer.TryEnqueue(frame));
            var result = Done(writer); Check(result.Status == "completed");
            var info = FullReplayArchive.ReadInfo(result.FilePath);
            Check(info.Pages.Length >= 2 && info.Pages.Skip(1).All(p => !p.Overlap));
            var events = new List<ItemEvent>();
            for (int i = 0; i < info.Pages.Length; i++)
            {
                var clip = FullReplayArchive.ReadPage(result.FilePath, info, i);
                foreach (var frame in clip.Frames) events.AddRange(frame.Events);
            }
            Check(events.Select(e => e.Sequence).SequenceEqual(new long[] { 17, 18 }));
            Check(events[0].T == second.Events[0].T - 100 && events[1].T == third.Events[0].T - 100);
            Check(events[0].T < info.Pages[1].Start);
        }));
        test("full replay preserves events observed before the first sample after a long capture gap", () => InRoot(root =>
        {
            var frames = new[] { Simple(100), Simple(101), Simple(500), Simple(501) };
            frames[2].Events = new[] { new ItemEvent { Sequence = 44, T = 499.75, Kind = "use", ActorId = "actor" } };
            frames[3].Events = new[] { new ItemEvent { Sequence = 45, T = 500.2, Kind = "throw", ActorId = "actor" } };
            var writer = new FullReplayWriter(root, Header()); foreach (var frame in frames) Check(writer.TryEnqueue(frame));
            var result = Done(writer); Check(result.Status == "completed");
            var info = FullReplayArchive.ReadInfo(result.FilePath); Check(info.Pages.Length == 2 && !info.Pages[1].Overlap);
            var page = FullReplayArchive.ReadPage(result.FilePath, info, 1);
            Check(page.Frames[0].T == 400 && page.Frames[0].Events.Single().Sequence == 44 && page.Frames[0].Events[0].T == 399.75);
            Check(page.Frames[1].Events.Single().Sequence == 45 && page.Frames[1].Events[0].T == frames[3].Events[0].T - 100);
        }));
        test("full replay capture interruption drains accepted samples but retains only a faulted partial", () => InRoot(root =>
        {
            var writer = new FullReplayWriter(root, Header());
            Check(writer.TryEnqueue(Frame(100)) && writer.TryEnqueue(Frame(100.1)));
            var task = writer.InterruptAsync("capture-error", new IOException("synthetic interruption"));
            Check(ReferenceEquals(task, writer.CompleteAsync("later")) && !writer.TryEnqueue(Frame(101)));
            var result = Await(task);
            Check(result.Status == "faulted" && result.FaultCode == "capture-interrupted" && result.Reason == "capture-error");
            Check(result.WrittenFrames == 2 && File.Exists(writer.PartialPath) && !File.Exists(writer.FilePath));
            Check(!FullReplayArchive.ReadInfo(writer.PartialPath).Complete);
        }));
        test("full replay bounded producer queue overflow never silently drops an accepted frame", () => InRoot(root =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var writer = new FullReplayWriter(root, Header(), new ContinuousReplayOptions { QueueFrameLimit = 2 }, path =>
            { entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(10))); return NewFile(path); });
            try
            {
                Check(entered.Wait(TimeSpan.FromSeconds(10)));
                var a = Frame(0); var b = Frame(.1);
                Check(writer.TryEnqueue(a, RollingBuffer.Estimate(a)) && writer.TryEnqueue(b, RollingBuffer.Estimate(b)));
                Check(!writer.TryEnqueue(Frame(.2)) && writer.Stats.QueuedFrames == 2 && writer.Stats.QueuedBytes > 0);
            }
            finally { release.Set(); }
            var result = Done(writer);
            Check(result.FaultCode == "queue-overflow" && result.WrittenFrames == 2 && result.AcceptedFrames == 2);
            Check(writer.Stats.QueuedBytes == 0 && !File.Exists(writer.FilePath));
            var huge = new FullReplayWriter(root, Header());
            Check(!huge.TryEnqueue(Frame(0), long.MaxValue) && huge.Stats.QueuedBytes == 0); Done(huge);
        }));
        test("full replay disk write errors retain the single partial without an apparently complete final", () => InRoot(root =>
        {
            var writer = new FullReplayWriter(root, Header(), null, path => new FailedDisk(NewFile(path)));
            writer.TryEnqueue(Frame(0)); writer.TryEnqueue(Frame(.1)); var result = Done(writer);
            Check(result.Status == "faulted" && result.FaultCode == "write-failure");
            Check(File.Exists(writer.PartialPath) && !File.Exists(writer.FilePath) && Directory.GetFiles(root).Length == 1);
        }));
        test("full replay raw and decoded page budgets rotate internally without adding physical files", () => InRoot(root =>
        {
            foreach (bool decoded in new[] { true, false })
            {
                var options = new ContinuousReplayOptions();
                if (decoded) options.SegmentDecodedByteLimit = 4096; else options.SegmentUncompressedByteLimit = 4096;
                var writer = new FullReplayWriter(root, Header(), options);
                for (int i = 0; i < 15; i++) Check(writer.TryEnqueue(Simple(i * .01)));
                var result = Done(writer); Check(result.Status == "completed" && result.WrittenFrames == 15);
                var info = FullReplayArchive.ReadInfo(result.FilePath); Check(info.Pages.Length > 1);
                Check(info.Pages.All(p => decoded ? p.DecodedBytes <= 4096 : p.RawBytes <= 4096));
                Check(info.Pages.Sum(p => p.FrameCount - (p.Overlap ? 1 : 0)) == 15);
                for (int i = 0; i < info.Pages.Length; i++) Check(FullReplayArchive.ReadPage(result.FilePath, info, i).Complete);
            }
            Check(Directory.GetFiles(root).Length == 2 && Directory.GetDirectories(root).Length == 0);
        }));
        test("full replay index rejects corrupt offsets, counts, timing and actor identities before page reads", () => InRoot(root =>
        {
            foreach (Action<FullReplayInfo> corrupt in new Action<FullReplayInfo>[]
            {
                info => info.Pages[0].Offset = long.MaxValue,
                info => info.Pages[0].Length = long.MaxValue,
                info => info.FrameCount++,
                info => info.Pages[0].Start = 1,
                info => info.Pages[0].DecodedBytes = 65L * 1024 * 1024,
                info => info.ActorIds = new[] { "duplicate", "duplicate" },
            })
            {
                string path = Save(root); var info = FullReplayArchive.ReadInfo(path); corrupt(info); RewriteIndex(path, info);
                Reject(() => FullReplayArchive.ReadInfo(path));
            }
        }));
        test("full replay singleton overlap boundary remains readable when an adjacent heavy frame needs its own page", () => InRoot(root =>
        {
            var writer = new FullReplayWriter(root, Header(), new ContinuousReplayOptions { SegmentSeconds = .1, SegmentDecodedByteLimit = 4096 });
            var heavy = Simple(.11);
            heavy.Actors = Enumerable.Range(0, 4).Select(i => new ActorFrame { Id = "actor-" + i }).ToArray();
            Check(writer.TryEnqueue(Simple(0)) && writer.TryEnqueue(Simple(.1)) && writer.TryEnqueue(heavy));
            var result = Done(writer); Check(result.Status == "completed");
            var info = FullReplayArchive.ReadInfo(result.FilePath);
            Check(info.Pages.Any(p => p.Overlap && p.FrameCount == 1) && info.FrameCount == 3);
            for (int i = 0; i < info.Pages.Length; i++) Check(FullReplayArchive.ReadPage(result.FilePath, info, i).Complete);
        }));
        test("full replay page rejects altered gzip content and false page frame metadata", () => InRoot(root =>
        {
            string path = Save(root); var info = FullReplayArchive.ReadInfo(path);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            { file.Position = info.Pages[0].Offset; file.WriteByte(0); }
            Reject(() => FullReplayArchive.ReadPage(path, info, 0));
            string other = Save(root); var bad = FullReplayArchive.ReadInfo(other);
            bad.ActorIds = new[] { "not-present-in-frame" }; RewriteIndex(other, bad);
            var claimed = FullReplayArchive.ReadInfo(other); Reject(() => FullReplayArchive.ReadPage(other, claimed, 0));
            string crcPath = Save(root); var crcInfo = FullReplayArchive.ReadInfo(crcPath); var crcPage = crcInfo.Pages[0];
            using (var file = new FileStream(crcPath, FileMode.Open, FileAccess.ReadWrite))
            {
                file.Position = crcPage.Offset + crcPage.Length - 8; int original = file.ReadByte();
                file.Position--; file.WriteByte((byte)(original ^ 128));
            }
            Reject(() => FullReplayArchive.ReadPage(crcPath, crcInfo, 0));
        }));
        test("full replay metadata rejects missing footer and cancelled reads do not open files", () => InRoot(root =>
        {
            string path = Save(root);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Write)) file.SetLength(file.Length - 1);
            Reject(() => FullReplayArchive.ReadInfo(path));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { FullReplayArchive.ReadInfo("does-not-exist", cancel.Token); }
            catch (OperationCanceledException) { return; }
            throw new Exception("Cancelled metadata read did not throw.");
        }));
        test("one-second full replay index supports a complete four-hour recording", () => InRoot(root =>
        {
            var writer = new FullReplayWriter(root, Header());
            for (int second = 0; second <= (int)FullReplayArchive.MaximumDuration; second++)
            {
                // Keep this duration/index regression independent of producer
                // throughput and the intentionally bounded capture queue.
                if (second % 100 == 0) Wait(() => writer.Stats.QueuedFrames < 100 || writer.Fault != null);
                Check(writer.TryEnqueue(Simple(second)), writer.FailureCode ?? "Four-hour sample was rejected.");
            }
            var result = Done(writer); Check(result.Status == "completed", result.Fault ?? "Four-hour recording failed.");
            var info = FullReplayArchive.ReadInfo(result.FilePath);
            Check(info.Duration == FullReplayArchive.MaximumDuration && info.Pages.Length == 14400 && info.Version == 4);
            Check(info.Pages.All(p => p.End - p.Start <= 1.000001 && p.EffectHistory != null));
            Check(info.FileLength - info.IndexOffset - 16 <= FullReplayArchive.MaximumIndexBytes);
            Check(FullReplayArchive.ReadPage(result.FilePath, info, 14399).Frames.Last().T == FullReplayArchive.MaximumDuration);
        }));
        test("independent effect history reads retain emitter clocks without reading damaged actor payloads", () => InRoot(root =>
        {
            var writer = new FullReplayWriter(root, Header());
            foreach (var frame in new[] { Frame(500), Frame(500.25, 1), Frame(500.5, 2) }) Check(writer.TryEnqueue(frame));
            var result = Done(writer); var info = FullReplayArchive.ReadInfo(result.FilePath);
            Check(info.Version == 4 && info.Pages[0].EffectHistory!.DecodedBytes < info.Pages[0].DecodedBytes);
            using var source = FullReplayArchive.OpenPlayback(result.FilePath, info);
            var history = source.LoadHistory(0, CancellationToken.None);
            Check(history.Frames.Count == 3 && history.Frames.All(f => f.Actors.Length == 0 && f.Items.Length == 0 && f.Events.Length == 0));
            Check(history.Frames.Select(f => f.T).SequenceEqual(new[] { 0d, .25, .5 }));
            Check(history.Frames.All(f => f.Effects.Single().AnchorTime == -9));
            using (var file = new FileStream(result.FilePath, FileMode.Open, FileAccess.ReadWrite))
            { file.Position = info.Pages[0].Offset; file.WriteByte(0); }
            File.SetLastWriteTimeUtc(result.FilePath, new DateTime(new FileInfo(result.FilePath).LastWriteTimeUtc.Ticks, DateTimeKind.Utc));
            // A fresh source sees the changed timestamp; the independent stream
            // itself remains readable while the main payload is invalid.
            using var fresh = FullReplayArchive.OpenPlayback(result.FilePath, FullReplayArchive.ReadInfo(result.FilePath));
            Check(fresh.LoadHistory(0, CancellationToken.None).Frames.Count == 3);
            Reject(() => fresh.Load(0, CancellationToken.None));
        }));
        test("full replay version 3 requires bounded independently checked effect history metadata", () => InRoot(root =>
        {
            foreach (Action<FullReplayInfo> corrupt in new Action<FullReplayInfo>[]
            {
                info => info.Pages[0].EffectHistory = null,
                info => info.Pages[0].EffectHistory!.Offset++,
                info => info.Pages[0].EffectHistory!.Length = long.MaxValue,
                info => info.Pages[0].EffectHistory!.DecodedBytes = info.Pages[0].DecodedBytes + 1,
                info => info.Version = 5,
            })
            {
                string path = Save(root); var info = FullReplayArchive.ReadInfo(path); corrupt(info); RewriteIndex(path, info);
                Reject(() => FullReplayArchive.ReadInfo(path));
            }
            string crcPath = Save(root); var valid = FullReplayArchive.ReadInfo(crcPath); var history = valid.Pages[0].EffectHistory!;
            using (var file = new FileStream(crcPath, FileMode.Open, FileAccess.ReadWrite))
            { file.Position = history.Offset + history.Length - 8; int b = file.ReadByte(); file.Position--; file.WriteByte((byte)(b ^ 64)); }
            using var source = FullReplayArchive.OpenPlayback(crcPath, FullReplayArchive.ReadInfo(crcPath));
            Check(source.Load(0, CancellationToken.None).Frames.Count == 2);
            Reject(() => source.LoadHistory(0, CancellationToken.None));
        }));
        test("legacy seek caches are generated only for visited pages and expose real small blocks on reopening", () => InRoot(root =>
        {
            string path = SaveLegacy(root, Enumerable.Range(0, 25).Select(i => Frame(100 + i / 2d, i)).ToArray(), splitAt: 13);
            var info = FullReplayArchive.ReadInfo(path); Check(info.Version == 1 && info.Pages.Length == 2);
            using (var source = FullReplayArchive.OpenPlayback(path, info, createSeekCaches: true))
            {
                Check(source.Ranges.Length == 2 && !Directory.Exists(path + ".seekcache"));
                source.LoadHistory(1, CancellationToken.None);
                Check(source.CacheWrite == null && !Directory.Exists(path + ".seekcache"), "Effect history started optional full-scene cache serialization.");
                var clip = source.Load(1, CancellationToken.None);
                Check(clip.Frames[0].T == 6.5 && clip.Duration == 12);
                Check(source.CacheWrite != null && source.CacheWrite.Wait(TimeSpan.FromSeconds(20)));
                Check(source.Ranges.Length == 2, "A live playback source changed its stable page indices.");
                Check(Directory.GetFiles(path + ".seekcache", "*.seekpage").Length == 1);
                Check(File.Exists(Path.Combine(path + ".seekcache", "page-00001.seekpage")) &&
                    !File.Exists(Path.Combine(path + ".seekcache", "page-00000.seekpage")));
            }
            using var reopened = FullReplayArchive.OpenPlayback(path, FullReplayArchive.ReadInfo(path));
            Check(reopened.Ranges.Length > 2 && reopened.Ranges[0].Start == 0 && reopened.Ranges[0].End == 6);
            var all = new SortedDictionary<double, ReplayFrame>();
            for (int child = 1; child < reopened.Ranges.Length; child++)
            {
                Check(reopened.Ranges[child].End - reopened.Ranges[child].Start <= 1.000001);
                var clip = reopened.Load(child, CancellationToken.None);
                Check(clip.Frames[0].T == reopened.Ranges[child].Start && clip.Duration == reopened.Ranges[child].End);
                foreach (var frame in clip.Frames) all.TryAdd(frame.T, frame);
                var effects = reopened.LoadHistory(child, CancellationToken.None);
                Check(effects.Frames.Select(f => f.T).SequenceEqual(clip.Frames.Select(f => f.T)) && effects.Frames.All(f => f.Actors.Length == 0));
                Check(JToken.DeepEquals(JToken.Parse(Json(effects.Frames.Select(f => f.Effects))), JToken.Parse(Json(clip.Frames.Select(f => f.Effects)))));
            }
            var expected = FullReplayArchive.ReadPage(path, info, 1);
            Check(JToken.DeepEquals(JToken.Parse(Json(all.Values)), JToken.Parse(Json(expected.Frames))));
            Check(reopened.CacheWrite == null, "A valid small-cache read unnecessarily decoded the old whole page.");
        }));
        test("interactive legacy playback never creates optional seek caches", () => InRoot(root =>
        {
            string path = SaveLegacy(root, Enumerable.Range(0, 25).Select(i => Frame(100 + i / 2d, i)).ToArray());
            using var source = FullReplayArchive.OpenPlayback(path, FullReplayArchive.ReadInfo(path));
            var history = source.LoadHistory(0, CancellationToken.None); var full = source.Load(0, CancellationToken.None);
            Check(source.CacheWrite == null && !Directory.Exists(path + ".seekcache"));
            Check(JToken.DeepEquals(JToken.Parse(Json(history.Frames)), JToken.Parse(Json(full.Frames.Select(FullReplayArchive.EffectsOnlyFrame)))));
        }));
        test("legacy history projection matches full effects clocks seeds order removals and map witnesses in every schema", () =>
        {
            for (int schema = 10; schema <= ReplayRules.CurrentSchema; schema++)
            {
                var header = Header(); header.Schema = schema; header.MapObjects = new[] { "first", "second" };
                EffectReplayFrame Effect(string key, int seed, double phase) => new()
                { Key = key, Resource = "native-" + key, Seed = seed, Seeds = new[] { (long)seed, seed + 1L }, AnchorTime = -.5, Phase = phase,
                    Position = new[] { (float)phase, 2f, 3f }, Rate = 2, Loop = true, ShaderRandom = (float)phase };
                var frames = Enumerable.Range(0, 8).Select(i => Frame(i / 4d, i)).ToArray();
                foreach (var frame in frames) frame.World.ActiveMapObjects = new[] { frame.T < 1, frame.T >= 1 };
                frames[0].Effects = new[] { Effect("a", 3, 1), Effect("b", 7, 2) };
                frames[1].Effects = frames[0].Effects;
                frames[2].Effects = new[] { frames[0].Effects[1], Effect("a", 9, 3) };
                frames[3].Effects = new[] { frames[2].Effects[1] };
                frames[4].Effects = Array.Empty<EffectReplayFrame>();
                frames[5].Effects = new[] { Effect("a", 10, 4) };
                frames[6].Effects = new[] { Effect("a", 10, 4.5) }; frames[6].Effects[0].Emitting = false; frames[6].Effects[0].EmissionStopPhase = 5;
                frames[7].Effects = new[] { Effect("a", 10, 5) }; frames[7].Effects[0].Paused = true;
                var codec = new ReplayDeltaCodec.Writer(schema);
                string text = string.Join("\n", new[] { Json(header) }.Concat(frames.Select(f => Json(codec.Encode(f)))).Append("{\"Type\":\"end\"}"));
                var full = ReplayFiles.Read(new StringReader(text)); var history = ReplayFiles.ReadEffects(new StringReader(text));
                Check(JToken.DeepEquals(JToken.Parse(Json(history.Frames)), JToken.Parse(Json(full.Frames.Select(FullReplayArchive.EffectsOnlyFrame)))));
                Check(history.Frames.All(f => f.Actors.Length == 0 && f.Items.Length == 0 && f.Creatures.Length == 0 && f.Audio.Length == 0));
            }
        });
        test("legacy effect projection rejects malformed effect deltas frame clocks and map shape", () =>
        {
            var header = Header(); var codec = new ReplayDeltaCodec.Writer(header.Schema);
            var baseline = JObject.FromObject(codec.Encode(Frame(0)));
            var next = JObject.FromObject(codec.Encode(Frame(.25)));
            foreach (Action<JObject> corrupt in new Action<JObject>[]
            {
                p => p["T"] = -1,
                p => p["ObjectsFull"] = false,
                p => p["World"]![nameof(WorldFrame.ActiveMapObjects)] = new JArray(true),
                p => p["World"]!["unknown"] = 1,
                p => p["Effects"]![0]![nameof(EffectReplayFrame.Seed)] = -1,
                p => ((JObject)p["Effects"]![0]!).Remove(nameof(EffectReplayFrame.Resource)),
                p => p["unknown"] = 1,
            })
            {
                var damaged = (JObject)baseline.DeepClone(); corrupt(damaged);
                string text = Json(header) + "\n" + Json(damaged) + "\n" + Json(next) + "\n{\"Type\":\"end\"}";
                Reject(() => ReplayFiles.ReadEffects(new StringReader(text)));
            }
        });
        test("discarded damaged or stale seek caches fall back to the original real samples", () => InRoot(root =>
        {
            string path = SaveLegacy(root, Enumerable.Range(0, 13).Select(i => Frame(100 + i / 2d, i)).ToArray());
            var info = FullReplayArchive.ReadInfo(path);
            using (var first = FullReplayArchive.OpenPlayback(path, info, createSeekCaches: true))
            { first.Load(0, CancellationToken.None); Check(first.CacheWrite != null && first.CacheWrite.Wait(TimeSpan.FromSeconds(20))); }
            string cache = Directory.GetFiles(path + ".seekcache", "*.seekpage").Single();
            using (var opened = FullReplayArchive.OpenPlayback(path, info))
            {
                Check(opened.Ranges.Length > 1); var range = opened.Ranges[1]; File.Delete(cache);
                var fallback = opened.Load(1, CancellationToken.None);
                Check(fallback.Frames[0].T == range.Start && fallback.Duration == range.End);
                Check(opened.LoadHistory(1, CancellationToken.None).Frames.All(f => f.Actors.Length == 0));
            }
            using (var rebuild = FullReplayArchive.OpenPlayback(path, info, createSeekCaches: true))
            { rebuild.Load(0, CancellationToken.None); Check(rebuild.CacheWrite != null && rebuild.CacheWrite.Wait(TimeSpan.FromSeconds(20))); }
            using (var file = new FileStream(cache, FileMode.Open, FileAccess.ReadWrite))
            { file.Position = file.Length - 20; int b = file.ReadByte(); file.Position--; file.WriteByte((byte)(b ^ 1)); }
            using (var damaged = FullReplayArchive.OpenPlayback(path, info)) Check(damaged.Ranges.Length == 1);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            using var stale = FullReplayArchive.OpenPlayback(path, FullReplayArchive.ReadInfo(path));
            Check(stale.Ranges.Length == 1);
        }));
        test("legacy fast blocks preserve between-sample events empty actor cuts and observation clocks", () => InRoot(root =>
        {
            var frames = Enumerable.Range(0, 13).Select(i => Simple(100 + i / 4d)).ToArray();
            for (int i = 0; i < frames.Length; i++)
            {
                double observed = 100 + (i / 2) / 2d - .125;
                frames[i].Actors[0].JointPose = new[] { new NodePose { Path = ".", SampleTime = observed, Position = new[] { (float)(i / 2), 0f, 0f } } };
                frames[i].World.Environment = new EnvironmentReplayFrame { SampleTimeKnown = true, SampleTime = observed };
            }
            frames[4].Actors = Array.Empty<ActorFrame>();
            frames[5].Actors[0].Position = new[] { 1000f, 0f, 0f };
            frames[5].Events = new[] { new ItemEvent { Sequence = 9, T = 101.1, Kind = "use", ActorId = "actor" } };
            string path = SaveLegacy(root, frames); var info = FullReplayArchive.ReadInfo(path);
            using (var source = FullReplayArchive.OpenPlayback(path, info, createSeekCaches: true))
            { source.Load(0, CancellationToken.None); Check(source.CacheWrite != null && source.CacheWrite.Wait(TimeSpan.FromSeconds(20))); }
            using var fast = FullReplayArchive.OpenPlayback(path, info);
            var union = new SortedDictionary<double, ReplayFrame>();
            for (int i = 0; i < fast.Ranges.Length; i++) foreach (var frame in fast.Load(i, CancellationToken.None).Frames) union.TryAdd(frame.T, frame);
            Check(union[1].Actors.Length == 0 && union[1.25].Actors[0].Position[0] == 1000);
            Check(union[1.25].Events.Single().T == frames[5].Events[0].T - 100);
            Check(union[1.25].Actors[0].JointPose[0].SampleTime == .875 && union[1.25].World.Environment!.SampleTime == .875);
            Check(JToken.DeepEquals(JToken.Parse(Json(union.Values)), JToken.Parse(Json(FullReplayArchive.ReadPage(path, info, 0).Frames))));
        }));
        test("damaged cached payload falls back and cancellation leaves no temporary seek files", () => InRoot(root =>
        {
            string path = SaveLegacy(root, Enumerable.Range(0, 25).Select(i => Frame(100 + i / 4d, i)).ToArray());
            var info = FullReplayArchive.ReadInfo(path);
            using (var first = FullReplayArchive.OpenPlayback(path, info, createSeekCaches: true))
            { first.Load(0, CancellationToken.None); Check(first.CacheWrite != null && first.CacheWrite.Wait(TimeSpan.FromSeconds(20))); }
            string cache = Directory.GetFiles(path + ".seekcache", "*.seekpage").Single();
            using (var fast = FullReplayArchive.OpenPlayback(path, info))
            {
                Check(fast.Ranges.Length > 1);
                using (var file = new FileStream(cache, FileMode.Open, FileAccess.ReadWrite))
                { file.Position = 44; file.WriteByte(0); } // Only the first compressed main block is damaged.
                var fallback = fast.Load(0, CancellationToken.None);
                Check(fallback.Frames[0].T == fast.Ranges[0].Start && fallback.Duration == fast.Ranges[0].End);
                Check(fast.LoadHistory(0, CancellationToken.None).Frames.All(f => f.Actors.Length == 0));
            }
            File.Delete(cache);
            var cancel = FullReplayArchive.OpenPlayback(path, info, createSeekCaches: true);
            cancel.Load(0, CancellationToken.None); var pending = cancel.CacheWrite; cancel.Dispose();
            Check(pending == null || pending.Wait(TimeSpan.FromSeconds(20)));
            Check(Directory.GetFiles(path + ".seekcache", "*.partial").Length == 0);
            try { cancel.Load(0, CancellationToken.None); throw new Exception("Disposed playback source admitted a read."); }
            catch (ObjectDisposedException) { }
        }));
    }
    private static ReplayHeader Header() => new() { Scene = "Level_FullArchiveTest", Participants = new[] { "Synthetic" } };
    private static string BufferedText(ReplayFrame[] frames, int padding = 0, bool negativeZero = false)
    {
        var lines = new List<string> { Json(Header()) }; var writer = new ReplayDeltaCodec.Writer();
        for (int i = 0; i < frames.Length; i++)
        {
            var record = JObject.FromObject(writer.Encode(frames[i]));
            if (negativeZero && i == 0) record["Actors"]![0]!["Position"] = new JRaw("[-0.0,1.0,2.0]");
            lines.Add((i == 0 ? new string(' ', padding) : "") + Json(record));
        }
        lines.Add("{\"Type\":\"end\"}"); return string.Join("\n", lines) + "\n";
    }
    private static ReplayClip BufferedRead(string text, ReplayLineBufferPool pool, bool effectsOnly = false, CancellationToken cancellation = default)
    {
        using var reader = new StringReader(text);
        return ReplayFiles.ReadBuffered(reader, pool, cancellation, effectsOnly, ReplayRules.MaxBytes, FullReplayArchive.MaximumDuration);
    }
    private static char[]? PoolSpare(ReplayLineBufferPool pool) =>
        (char[]?)typeof(ReplayLineBufferPool).GetField("spare", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(pool);
    private sealed class ObservingTextReader : TextReader
    {
        private readonly StringReader inner;
        private readonly Action<int> observe;
        private readonly bool once;
        private int index;
        internal ObservingTextReader(string text, Action<int> observe, bool once = false)
        { inner = new StringReader(text); this.observe = observe; this.once = once; }
        public override int Read()
        { int current = index++; if (!once || current == 0) observe(current); return inner.Read(); }
        protected override void Dispose(bool disposing)
        { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private static ReplayFrame[] StreamFrames()
    {
        var frames = Enumerable.Range(0, 4).Select(i => Frame(500 + i)).ToArray();
        for (int i = 0; i < frames.Length; i++)
        {
            frames[i].Actors[0].JointPose = new[] { new NodePose(".", new[] { (float)i, 0f, 0f }, new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f }, true, true, 500 + i) };
            frames[i].Ropes[0].Points[0].Position = new[] { (float)i, 0f, 0f };
            frames[i].Items = i == 1 ? new[] { new ItemFrame { Key = "item-b", ItemId = 2 } } :
                new[] { new ItemFrame { Key = "item-a", ItemId = 1 }, new ItemFrame { Key = "item-b", ItemId = 2 } };
            if (i == 3) Array.Reverse(frames[i].Items);
        }
        return frames;
    }
    private static JToken ReverseProperties(JToken value, int frame)
    {
        if (value is JObject obj)
        {
            var result = new JObject();
            foreach (var field in obj.Properties().Reverse()) result.Add(field.Name, ReverseProperties(field.Value, frame));
            foreach (string late in new[] { "Full", "Kind", frame % 2 == 0 ? "Type" : "ObjectsFull" })
                if (result.Property(late) is JProperty property) { property.Remove(); result.Add(property); }
            return result;
        }
        return value is JArray array ? new JArray(array.Select(v => ReverseProperties(v, frame))) : value.DeepClone();
    }
    private static ReplayFrame[] CompareStream(IEnumerable<string> records, int schema)
    {
        var native = new ReplayDeltaCodec.StreamReader(schema); var tree = new ReplayDeltaCodec.Reader(schema);
        var result = new List<ReplayFrame>(); var snapshots = new List<string>(); var clocks = new List<double[]>();
        foreach (string record in records)
        {
            var expected = tree.Decode(JObject.Parse(record)); var actual = native.DecodeRecord(record)!;
            Check(Json(expected) == Json(actual), "Typed and tree frame fields disagree.");
            Check(expected.Actors.SelectMany(a => a.JointPose).Select(j => j.SampleTime).SequenceEqual(actual.Actors.SelectMany(a => a.JointPose).Select(j => j.SampleTime)));
            result.Add(actual); snapshots.Add(Json(actual)); clocks.Add(actual.Actors.SelectMany(a => a.JointPose).Select(j => j.SampleTime).ToArray());
        }
        for (int i = 0; i < result.Count; i++)
        {
            Check(Json(result[i]) == snapshots[i], "A later patch mutated an earlier frame.");
            Check(result[i].Actors.SelectMany(a => a.JointPose).Select(j => j.SampleTime).SequenceEqual(clocks[i]));
        }
        return result.ToArray();
    }
    private static void RejectBoth(string record, int schema, string? baseline = null)
    {
        var native = new ReplayDeltaCodec.StreamReader(schema); var tree = new ReplayDeltaCodec.Reader(schema);
        if (baseline != null) { native.DecodeRecord(baseline); tree.Decode(JObject.Parse(baseline)); }
        Reject(() => tree.Decode(JObject.Parse(record, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error })));
        Reject(() => native.DecodeRecord(record));
    }
    private static ReplayFrame Simple(double time) => new() { T = time, Actors = new[] { new ActorFrame { Id = "actor", Position = new[] { (float)time, 1f, 2f } } } };
    private static ReplayFrame Frame(double time, int sequence = 0)
    {
        var frame = Simple(time);
        frame.Items = new[] { new ItemFrame { Key = "item", ItemId = 1 } };
        frame.Crates = new[] { new CrateFrame { Key = "crate", Kind = "luggage", Animation = new CrateAnimationFrame { Clip = "open", Anchored = true, AnchorTime = 490, Duration = 2 } } };
        frame.Ropes = new[] { new RopeReplayFrame { Key = "rope", Kind = "rope", Resource = "native-rope", Points = new[] { new RopeReplayPoint() }, Bones = new[] { new RopeReplayPoint() } } };
        frame.Effects = new[] { new EffectReplayFrame { Key = "effect", Resource = "native-effect", AnchorTime = 491 } };
        frame.Audio = new[] { new AudioReplayFrame { Key = "audio", Clip = "native-audio", StartedAt = 492, AnchorTime = 493, Duration = 2, Loop = true } };
        frame.Spawned = new[] { new SpawnedReplayFrame { Key = "cannon", Resource = "scoutcannon_placed", Kind = "scout-cannon", Animation = new CrateAnimationFrame { Clip = "CannonLight", Anchored = true, AnchorTime = 493, Duration = 3 } } };
        frame.Balloons = new[] { new BalloonReplayFrame { Key = "balloon", OwnerId = "actor", Started = 494 } };
        if (sequence > 0) frame.Events = new[] { new ItemEvent { Sequence = sequence, T = time, Kind = "use", ActorId = "actor", ItemKey = "item" } };
        return frame;
    }
    private static string Save(string root)
    {
        var writer = new FullReplayWriter(root, Header()); Check(writer.TryEnqueue(Frame(500)) && writer.TryEnqueue(Frame(501)));
        var result = Done(writer); Check(result.Status == "completed"); return result.FilePath;
    }
    private static string SaveLegacy(string root, ReplayFrame[] frames, int splitAt = -1)
    {
        string path = Path.Combine(root, "legacy-" + Guid.NewGuid().ToString("N") + ".peakrun");
        var header = Header(); var rebased = frames.Select(new ContinuousReplayRebaser(frames[0].T).Apply).ToArray();
        var pages = new List<FullReplayPage>();
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var binary = new BinaryWriter(file, Encoding.UTF8, true);
        byte[] head = Encoding.UTF8.GetBytes(Json(header)); binary.Write(Encoding.ASCII.GetBytes("PEAKRUN1")); binary.Write(head.Length); binary.Write(head); binary.Flush();
        int first = 0;
        while (first < rebased.Length)
        {
            int last = splitAt > first ? splitAt - 1 : rebased.Length - 1;
            long offset = file.Position, rawBytes = 0; var budget = new ContinuousReplayBudget();
            using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest, true))
            using (var text = new StreamWriter(gzip, new UTF8Encoding(false), 4096, true))
            {
                void Write(object value) { string line = Json(value); rawBytes += Encoding.UTF8.GetByteCount(line) + 1; text.Write(line); text.Write('\n'); }
                Write(header); var codec = new ReplayDeltaCodec.Writer(header.Schema);
                for (int i = first; i <= last; i++) { budget.Commit(rebased[i], budget.Additional(rebased[i])); Write(codec.Encode(rebased[i])); }
                Write(new { Type = "end" });
            }
            pages.Add(new FullReplayPage { Index = pages.Count, Start = rebased[first].T, End = rebased[last].T,
                Offset = offset, Length = file.Position - offset, RawBytes = rawBytes, DecodedBytes = budget.Bytes,
                FrameCount = last - first + 1, FirstFrameIndex = first });
            first = last + 1;
        }
        var index = new FullReplayIndex { Version = 1, Header = header, Duration = rebased.Last().T, FrameCount = frames.Length,
            ActorIds = frames.SelectMany(f => f.Actors).Select(a => a.Id).Distinct().ToArray(), Complete = true, Reason = "legacy-test", Pages = pages.ToArray() };
        byte[] encoded = Encoding.UTF8.GetBytes(Json(index)); binary.Write(encoded); binary.Write((long)encoded.Length); binary.Write(Encoding.ASCII.GetBytes("PEAKEND1"));
        return path;
    }
    private static void RewriteIndex(string path, FullReplayInfo info)
    {
        byte[] text = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(info, ReplayFiles.Json));
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write); file.Position = info.IndexOffset;
        using var writer = new BinaryWriter(file, Encoding.UTF8, true);
        writer.Write(text); writer.Write((long)text.Length); writer.Write(Encoding.ASCII.GetBytes("PEAKEND1")); writer.Flush(); file.SetLength(file.Position);
    }
    private static string Json(object value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static FullReplayResult Done(FullReplayWriter writer) => Await(writer.CompleteAsync());
    private static FullReplayResult Await(Task<FullReplayResult> task)
    { Check(task.Wait(TimeSpan.FromSeconds(20)), "Full replay worker timed out."); return task.GetAwaiter().GetResult(); }
    private static Stream NewFile(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    private static void Wait(Func<bool> ready)
    { var end = DateTime.UtcNow.AddSeconds(20); while (!ready()) { Check(DateTime.UtcNow < end, "Full replay worker timed out."); Thread.Sleep(1); } }
    private static void Check(bool condition, string reason = "Assertion failed.") { if (!condition) throw new Exception(reason); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is InvalidDataException || e is JsonException || e is IOException || e is ArgumentException) { return; }
        throw new Exception("Corrupt full replay was accepted.");
    }
    private static void InRoot(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "peak-full-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); } finally { Directory.Delete(root, true); }
    }
    private sealed class FailedDisk : Stream
    {
        private readonly Stream inner;
        public FailedDisk(Stream inner) => this.inner = inner;
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Synthetic write failure.");
        public override void Flush() => inner.Flush();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
