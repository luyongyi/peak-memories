using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class NativeColorEncodingTests
{
    public static void Run(Action<string, Action> test)
    {
        test("native color encoding retains legacy omitted fields and rejects schema downgrade", () =>
        {
            for (int schema = 12; schema <= 18; schema++)
            {
                var frame = Frame(0, 0, schema >= 15);
                string line = Json(new ReplayDeltaCodec.Writer(schema).Encode(frame));
                Check(!line.Contains("ColorEncoding"), "Legacy color encoding was not omitted.");
                Check(Json(new ReplayDeltaCodec.Reader(schema).Decode(JObject.Parse(line))) == Json(frame));
                Check(Json(new ReplayDeltaCodec.StreamReader(schema).DecodeRecord(line)) == Json(frame));
                Check(Json(new ReplayDeltaCodec.StreamReader(schema).DecodeRecord(EscapedField(line))) == Json(frame));
                if (schema == 18) continue;
                foreach (int encoding in new[] { 0, 1 })
                {
                    var record = JObject.Parse(line); Property(record)["ColorEncoding"] = encoding;
                    RejectRecord(record, schema);
                }
                Reject(() => new ReplayDeltaCodec.Writer(schema).Encode(Frame(0, 1, schema >= 15)));
            }
        });
        test("native raw colors survive sparse deltas in every appearance track without changing prior frames", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(18);
            var tree = new ReplayDeltaCodec.Reader(18); var stream = new ReplayDeltaCodec.StreamReader(18);
            var saved = new List<(ReplayFrame Frame, string Json)>();
            foreach (int i in Enumerable.Range(0, 5))
            {
                int encoding = i is 1 or 2 ? 1 : 0;
                var expected = Frame(i, encoding); string line = Json(writer.Encode(expected));
                var a = tree.Decode(JObject.Parse(line)); var b = stream.DecodeRecord(line)!;
                Check(Json(a) == Json(expected) && Json(b) == Json(expected));
                ReplayRules.Validate(b, i - 1);
                if (i == 1 || i == 3)
                    foreach (string track in new[] { "Items", "Crates", "Creatures", "Spawned", "Interactions" })
                        Check(JObject.Parse(line)[track]![0]!["Visuals"] != null, "An encoding-only change disappeared from its delta.");
                saved.Add((b, Json(b)));
            }
            foreach (var prior in saved) Check(Json(prior.Frame) == prior.Json);
        });
        test("native color encoding rejects malformed numbers and non-color kinds in full delta and shared readers", () =>
        {
            var baseline = JObject.Parse(Json(new ReplayDeltaCodec.Writer(18).Encode(Frame(0, 0))));
            foreach (JToken invalid in new JToken[] { new JValue(-1), new JValue(2), new JValue(2147483648L), new JValue(1.5), new JValue("1"), JValue.CreateNull(), new JValue(true) })
            {
                var broken = (JObject)baseline.DeepClone(); Property(broken)["ColorEncoding"] = invalid;
                RejectRecord(broken, 18);
                RejectShared((JArray)broken["Items"]![0]!["Visuals"]!, 18);
            }
            foreach (int kind in new[] { 0, 2, 3 })
            {
                var broken = (JObject)baseline.DeepClone(); var property = Property(broken);
                property["Kind"] = kind; property["ColorEncoding"] = 1;
                RejectRecord(broken, 18); RejectShared((JArray)broken["Items"]![0]!["Visuals"]!, 18);
            }
            var missing = (JObject)baseline.DeepClone(); Property(missing).Property("Values")!.Remove();
            RejectRecord(missing, 18); // Only the new zero-default field is optional.

            // A bad replacement array on an existing item must not escape the baseline checks.
            var writer = new ReplayDeltaCodec.Writer(18); string first = Json(writer.Encode(Frame(0, 0)));
            var delta = JObject.Parse(Json(writer.Encode(Frame(1, 1)))); Property(delta)["ColorEncoding"] = 2;
            var tree = new ReplayDeltaCodec.Reader(18); tree.Decode(JObject.Parse(first)); Reject(() => tree.Decode(delta));
            var stream = new ReplayDeltaCodec.StreamReader(18); stream.DecodeRecord(first); Reject(() => stream.DecodeRecord(Json(delta)));
        });
        test("native raw colors remain explicit in whole and leaf shared snapshots", () =>
        {
            var frame = Frame(0, 1); string line = Json(new ReplayDeltaCodec.Writer(18).Encode(frame));
            var baseline = JObject.Parse(line); var item = (JObject)baseline["Items"]![0]!;
            var reader = new ReplayDeltaCodec.StreamReader(18);
            Check(Json(reader.DecodeSharedRecord(Json(item), typeof(ItemFrame), "Key")) == Json(frame.Items[0]));
            char[] leaf = Json(new { Value = frame.Items[0].Visuals }).ToCharArray();
            Check(Json(reader.DecodeSharedValue(leaf, leaf.Length, typeof(NativeRendererFrame[]))) == Json(frame.Items[0].Visuals));
            Reject(() => new ReplayDeltaCodec.StreamReader(17).DecodeSharedRecord(Json(item), typeof(ItemFrame), "Key"));
            Reject(() => new ReplayDeltaCodec.StreamReader(17).DecodeSharedValue(leaf, leaf.Length, typeof(NativeRendererFrame[])));
            item["Visuals"] = JObject.Parse("{\"$Shared\":0}");
            Check(Json(new ReplayDeltaCodec.StreamReader(18, (_, _) => frame.Items[0].Visuals!).DecodeRecord(Json(baseline))) == Json(frame));
            Reject(() => new ReplayDeltaCodec.StreamReader(17, (_, _) => frame.Items[0].Visuals!).DecodeRecord(Json(baseline)));
            // Check downgrade validation independently for each track, including injected resolved leaves.
            foreach (string track in new[] { "Items", "Crates", "Creatures", "Spawned", "Interactions" })
            {
                var legacy = JObject.Parse(Json(new ReplayDeltaCodec.Writer(17).Encode(Frame(0, 0))));
                legacy[track]![0]!["Visuals"] = JObject.Parse("{\"$Shared\":0}");
                Reject(() => new ReplayDeltaCodec.StreamReader(17, (_, _) => frame.Items[0].Visuals!).DecodeRecord(Json(legacy)));
            }
        });
        test("v4 independent seek pages keep raw colors distinct and still read schema 17 shared recordings", () => InRoot(root =>
        {
            var frames = Enumerable.Range(0, 33).Select(i =>
            {
                var f = Frame(i / 4d, i < 8 || i >= 24 ? 0 : 1);
                f.Items[0].Pose.Position[0] = i / 8f;
                return f;
            }).ToArray();
            string path = Save(root, frames); var info = FullReplayArchive.ReadInfo(path);
            Check(info.Version == 4 && info.Header.Schema == ReplayRules.CurrentSchema && info.Pages.Length > 3);
            Check(info.SharedBlocks!.Any(b => b.Kind == 6), "Moving objects did not exercise appearance leaf sharing.");
            CheckPages(path, info, frames);

            var oldFrames = frames.Select(f => { var old = Frame(f.T, 0); old.Items[0].Pose.Position[0] = (float)f.T / 2; return old; }).ToArray();
            string legacySource = Save(root, oldFrames); var sourceInfo = FullReplayArchive.ReadInfo(legacySource);
            string legacy = ReencodeV4Schema17(root, legacySource, sourceInfo); var legacyInfo = FullReplayArchive.ReadInfo(legacy);
            Check(legacyInfo.Version == 4 && legacyInfo.Header.Schema == 17 && legacyInfo.SharedBlocks!.Any(b => b.Kind == 6));
            CheckPages(legacy, legacyInfo, oldFrames);
        }));
    }

    private static ReplayFrame Frame(double time, int encoding, bool allTracks = true)
    {
        var frame = new ReplayFrame { T = time, Items = new[] { new ItemFrame { Key = "item", ItemId = 1, Visuals = Visuals(encoding) } } };
        if (!allTracks) return frame;
        frame.Crates = new[] { new CrateFrame { Key = "crate", Kind = "Luggage", Visuals = Visuals(encoding) } };
        frame.Creatures = new[] { new CreatureReplayFrame { Key = "creature", Kind = "spider", Resource = "spider", Visuals = Visuals(encoding) } };
        frame.Spawned = new[] { new SpawnedReplayFrame { Key = "spawn", Kind = "bounce-shroom", Resource = "bounce-shroom", Visuals = Visuals(encoding) } };
        frame.Interactions = new[] { new InteractionReplayFrame { Key = "interaction", Kind = "break-debris", Resource = "native-debris", Visuals = Visuals(encoding) } };
        return frame;
    }
    private static NativeRendererFrame[] Visuals(int encoding) => Enumerable.Range(0, 3).Select(i => new NativeRendererFrame
    {
        Path = "root/mesh-" + i, Materials = new[] { new NativeMaterialFrame { Name = "native", Shader = "native",
            Properties = new[] { new NativeShaderPropertyFrame { Name = "_BaseColor", Kind = 1,
                Values = new[] { .177f, .31f, 1.5f, 1f }, ColorEncoding = encoding } } } },
    }).ToArray();
    private static JObject Property(JObject record) => (JObject)record["Items"]![0]!["Visuals"]![0]!["Materials"]![0]!["Properties"]![0]!;
    private static void RejectRecord(JObject record, int schema)
    {
        Reject(() => new ReplayDeltaCodec.Reader(schema).Decode(record));
        Reject(() => new ReplayDeltaCodec.StreamReader(schema).DecodeRecord(Json(record)));
        // Escaped field names deliberately select the ordinary JSON adapter.
        Reject(() => new ReplayDeltaCodec.StreamReader(schema).DecodeRecord(EscapedField(Json(record))));
    }
    private static void RejectShared(JArray visuals, int schema)
    {
        char[] bytes = Json(new { Value = visuals }).ToCharArray();
        Reject(() => new ReplayDeltaCodec.StreamReader(schema).DecodeSharedValue(bytes, bytes.Length, typeof(NativeRendererFrame[])));
    }
    private static string Save(string root, ReplayFrame[] frames)
    {
        var writer = new FullReplayWriter(root, new ReplayHeader { Scene = "Level_ColorEncoding" });
        foreach (var frame in frames) Check(writer.TryEnqueue(frame));
        var result = writer.CompleteAsync().GetAwaiter().GetResult();
        Check(result.Status == "completed", result.Fault ?? "Recording failed."); return result.FilePath;
    }
    private static void CheckPages(string path, FullReplayInfo info, ReplayFrame[] frames)
    {
        foreach (int index in Enumerable.Range(0, info.Pages.Length).Reverse().Concat(new[] { 0, info.Pages.Length - 1 }))
            foreach (var actual in FullReplayArchive.ReadPage(path, info, index).Frames)
                Check(Json(actual) == Json(frames[(int)Math.Round(actual.T * 4)]), "A direct seek changed color representation.");
    }
    // Build a genuine older-schema v4 fixture using the same public format layout.
    // Its independently compressed page headers and shared snapshots all use 17.
    private static string ReencodeV4Schema17(string root, string source, FullReplayInfo info)
    {
        string path = Path.Combine(root, "schema17-v4.peakrun");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var binary = new BinaryWriter(file, Encoding.UTF8, true);
        var header = JsonConvert.DeserializeObject<ReplayHeader>(Json(info.Header))!; header.Schema = 17;
        byte[] head = Encoding.UTF8.GetBytes(Json(header)); binary.Write(Encoding.ASCII.GetBytes("PEAKRUN1")); binary.Write(head.Length); binary.Write(head); binary.Flush();
        var pages = new List<FullReplayPage>(); var shared = new FullReplaySharedWriter();
        foreach (var prior in info.Pages)
        {
            var clip = FullReplayArchive.ReadPage(source, info, prior.Index); long offset = file.Position, bytes = 0;
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest, true))
            using (var text = new StreamWriter(gzip, new UTF8Encoding(false), 4096, true))
            {
                void Write(object value) { string line = Json(value); bytes += Encoding.UTF8.GetByteCount(line) + 1; text.Write(line); text.Write('\n'); }
                Write(header); var codec = new ReplayDeltaCodec.Writer(17); bool baseline = true;
                foreach (var frame in clip.Frames)
                {
                    object encoded = codec.Encode(frame); if (baseline) shared.EncodeBaseline(encoded, frame);
                    Write(encoded); baseline = false;
                }
                Write(new { Type = "end" });
            }
            long length = file.Position - offset;
            var history = FullReplayArchive.WriteEffectHistory(file, header, clip.Frames, default); shared.FlushPending(file);
            pages.Add(new FullReplayPage { Index = prior.Index, Start = prior.Start, End = prior.End, Offset = offset, Length = length,
                RawBytes = bytes, DecodedBytes = prior.DecodedBytes, FrameCount = prior.FrameCount, FirstFrameIndex = prior.FirstFrameIndex,
                Overlap = prior.Overlap, EffectHistory = history, SharedThrough = shared.Blocks.Count });
        }
        var index = new FullReplayIndex { Version = 4, Header = header, Duration = info.Duration, FrameCount = info.FrameCount,
            ActorIds = info.ActorIds, Complete = true, Reason = "test", Pages = pages.ToArray(), SharedBlocks = shared.Blocks.ToArray(),
            GapOver100ms = info.GapOver100ms, GapOver500ms = info.GapOver500ms, LargestGapSeconds = info.LargestGapSeconds };
        byte[] data = Encoding.UTF8.GetBytes(Json(index)); binary.Write(data); binary.Write((long)data.Length); binary.Write(Encoding.ASCII.GetBytes("PEAKEND1"));
        return path;
    }
    private static string Json(object? value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static string EscapedField(string line) => line.Replace("\"Type\"", "\"Ty\\u0070e\"");
    private static void Check(bool value, string message = "Native color encoding assertion failed.") { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception e) when (e is InvalidDataException || e is JsonException) { return; } throw new Exception("Invalid native color encoding was accepted."); }
    private static void InRoot(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "peak-color-encoding-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); } finally { Directory.Delete(root, true); }
    }
}
