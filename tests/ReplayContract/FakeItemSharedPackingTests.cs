using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static partial class FullReplaySharedBlockTests
{
    private const int FakeSchema = 27, FakeCount = 1000;

    private static void RunFakeItems(Action<string, Action> test)
    {
        test("schema27 1000 static fake item sources share across native-origin pages and preserve hide reveal removal and histories", () => InRoot(root =>
        {
            const double origin = 120;
            var expected = FakeFrames();
            var native = expected.Select(frame =>
            {
                var value = JsonConvert.DeserializeObject<ReplayFrame>(Json(frame))!;
                value.T += origin; value.SceneSampleTime += origin;
                foreach (var effect in value.Effects) effect.AnchorTime += origin;
                return value;
            }).ToArray();
            string nativeBefore = Json(native);
            string path = Save(root, native); var info = FullReplayArchive.ReadInfo(path);
            Check(info.Header.Schema == FakeSchema && info.Pages.Length >= 10);
            var blocks = info.SharedBlocks!;
            Check(blocks.Count(block => block.Kind == 4) == FakeCount + 2,
                "A dormant source path was repeated, or a real visibility/root change was omitted.");
            Check(blocks.Count(block => block.Kind == 9) == 3,
                "Unchanged interaction orders were rewritten, or removal/reordering was lost.");
            Check(blocks.All(block => block.Kind == 4 || block.Kind == 9), "Compact fake sources unexpectedly created geometry leaves.");
            Check(blocks.Where(block => block.Kind == 4).All(block => block.RawBytes < 512), "Fixture no longer exercises compact source sharing.");
            using var playback = FullReplayArchive.OpenPlayback(path, info);
            foreach (var page in info.Pages.AsEnumerable().Reverse())
            {
                var baseline = StaticTrapPageRecords(path, page)[0];
                foreach (var entry in (JArray)baseline["Interactions"]!)
                    Check(entry["$Shared"] != null || ((string?)entry["Key"])?.StartsWith("dynamic-", StringComparison.Ordinal) == true,
                        "An independent baseline repeated a compact source DTO.");
                int order = (int)baseline["InteractionOrder"]!["$Shared"]!;
                Check(order < page.SharedThrough && blocks[order].Kind == 9);
                // Every call owns a fresh decoder/resolver; no earlier page or
                // visibility event may be needed to restore the last page.
                var clip = FullReplayArchive.ReadPage(path, info, page.Index);
                var history = playback.LoadHistory(page.Index, default);
                Check(history.Frames.Count == clip.Frames.Count);
                for (int i = 0; i < clip.Frames.Count; i++)
                {
                    var frame = clip.Frames[i]; var wanted = expected[(int)Math.Round(frame.T * 4)];
                    Check(Json(frame) == Json(wanted), "Independent fake item seek changed DTO state or order.");
                    Check(Json(history.Frames[i].Effects) == Json(wanted.Effects), "Native-origin source change corrupted effect history.");
                }
            }
            foreach (var block in blocks.Where(block => block.Kind == 4))
            {
                string raw = ReadFakeBlock(path, block);
                Check(!raw.Contains("\"$Shared\"", StringComparison.Ordinal), "A compact source formed a nested reference.");
                var value = (InteractionReplayFrame)new ReplayDeltaCodec.StreamReader(FakeSchema).DecodeSharedRecord(raw, typeof(InteractionReplayFrame), "Key");
                Check(InteractionReplayRules.CompactFakeItem(value) && value.Pose.Nodes.Length == 0 && value.SourcePath.Length != 0);
            }
            Check(Json(native) == nativeBefore, "Writing relative pages mutated native capture DTOs.");
        }));

        test("schema27 rebuilt compact fake identities share once per state while small ordinary interactions stay inline", () =>
        {
            var shared = new FullReplaySharedWriter(FakeSchema);
            var initial = new ReplayFrame { Interactions = new[] { FakeItem(0), DynamicInteraction(0, 0) } };
            string? first = null;
            for (int i = 0; i < 10; i++)
            {
                var frame = JsonConvert.DeserializeObject<ReplayFrame>(Json(initial))!;
                var record = new ReplayDeltaCodec.Writer(FakeSchema).Encode(frame); shared.EncodeBaseline(record, frame);
                first ??= Json(record); Check(Json(record) == first && shared.Blocks.Count == 1);
                var entries = (JArray)JObject.Parse(Json(record))["Interactions"]!;
                Check(entries[0]["$Shared"] != null && entries[1]["$Shared"] == null);
            }
            var hidden = new ReplayFrame { Interactions = new[] { FakeItem(0, false) } };
            shared.EncodeBaseline(new ReplayDeltaCodec.Writer(FakeSchema).Encode(hidden), hidden);
            Check(shared.Blocks.Count == 2 && shared.DeferredHashCount == 0 && shared.NodeTemplateCount == 0);
            var revealed = new ReplayFrame { Interactions = new[] { FakeItem(0) } };
            var encoded = new ReplayDeltaCodec.Writer(FakeSchema).Encode(revealed); shared.EncodeBaseline(encoded, revealed);
            Check((int)JObject.Parse(Json(encoded))["Interactions"]![0]!["$Shared"]! == 0 && shared.Blocks.Count == 2);
        });

        test("schema27 compact fake sources retain bounded catalogue fallback without changing known references", () =>
        {
            var writer = new FullReplaySharedWriter(FakeSchema);
            var known = new ReplayFrame { Interactions = new[] { FakeItem(0) } };
            writer.EncodeBaseline(new ReplayDeltaCodec.Writer(FakeSchema).Encode(known), known);
            while (writer.Blocks.Count < FullReplayArchive.MaximumSharedBlocks) writer.Blocks.Add(new FullReplaySharedBlock());
            var next = new ReplayFrame { Interactions = new[] { FakeItem(0), FakeItem(1) } };
            var encoded = new ReplayDeltaCodec.Writer(FakeSchema).Encode(next); writer.EncodeBaseline(encoded, next);
            var entries = (JArray)JObject.Parse(Json(encoded))["Interactions"]!;
            Check(entries[0]["$Shared"] != null && entries[1]["$Shared"] == null && entries[1]["SourcePath"] != null);
            var decoded = new ReplayDeltaCodec.StreamReader(FakeSchema, (type, id) =>
            { Check(type == typeof(InteractionReplayFrame) && id == 0); return known.Interactions[0]; }).DecodeRecord(Json(encoded));
            Check(Json(decoded) == Json(next) && writer.Blocks.Count == FullReplayArchive.MaximumSharedBlocks);
        });

        test("schema27 interaction order leaves honor 256 entries and 8192 characters without expanding smaller orders", () =>
        {
            foreach (var example in new[] { (Count: 255, Characters: 0, Shared: false), (Count: 256, Characters: 0, Shared: true),
                (Count: 64, Characters: 8191, Shared: false), (Count: 64, Characters: 8192, Shared: true) })
            {
                var values = Enumerable.Range(0, example.Count).Select(i => FakeItem(i)).ToArray();
                if (example.Characters != 0)
                    for (int i = 0; i < values.Length; i++)
                        values[i].Key = new string('k', i == values.Length - 1 && example.Characters == 8191 ? 119 : 120) + i.ToString("D8");
                var frame = new ReplayFrame { Interactions = values };
                var packed = PackFakeBaselines(frame);
                var order = JObject.Parse(packed.Records[0])["InteractionOrder"]!;
                Check((order is JObject) == example.Shared && packed.Writer.Blocks.Count(block => block.Kind == 9) == (example.Shared ? 1 : 0));
                Check(Json(DecodeFakeBaseline(packed, 0)) == Json(frame));
            }
        });

        test("schema27 maximum 8192 static plus 768 dynamic interaction order reads independently and older schemas keep their limit", () =>
        {
            var values = Enumerable.Range(0, InteractionReplayRules.MaximumStaticFakeItems).Select(i => FakeItem(i))
                .Concat(Enumerable.Range(0, InteractionReplayRules.MaximumDynamicEntities).Select(i => DynamicInteraction(i, 0))).ToArray();
            Check(values.Length == 8960 && values.Length > ReplayRules.MaxCreatures);
            var first = new ReplayFrame { Interactions = values };
            var second = new ReplayFrame { T = 1, Interactions = values.AsEnumerable().Reverse().ToArray() };
            ReplayRules.Validate(first, -1); ReplayRules.Validate(second, 0);
            var packed = PackFakeBaselines(first, second);
            Check(packed.Writer.Blocks.Count(block => block.Kind == 9) == 2);
            Check(Json(DecodeFakeBaseline(packed, 1)) == Json(second));
            Check(Json(DecodeFakeBaseline(packed, 0)) == Json(first));
            string leaf = Json(new { Value = values.Select(value => value.Key).ToArray() });
            Check(((string[])DecodeFakeOrder(leaf, FakeSchema)).Length == values.Length);
            Reject(() => DecodeFakeOrder(leaf, 26));
            Reject(() => DecodeFakeOrder(Json(new { Value = Enumerable.Range(0, 8961).Select(i => "key-" + i).ToArray() }), FakeSchema));
            // A valid generic leaf can still be too large for its consuming
            // track: CreatureOrder must retain the original creature bound.
            var record = JObject.Parse(Json(new ReplayDeltaCodec.Writer(FakeSchema).Encode(new ReplayFrame())));
            record["CreatureOrder"] = new JObject { ["$Shared"] = 0 };
            Reject(() => new ReplayDeltaCodec.StreamReader(FakeSchema, (type, id) => values.Select(value => value.Key).ToArray()).DecodeRecord(Json(record)));
        });

        test("schema26 rejects fake item kind on both inline and shared page baselines", () =>
        {
            var frame = new ReplayFrame { Interactions = new[] { FakeItem(0) } };
            Reject(() => ReplayRules.ValidateVersion(frame, 26));
            Reject(() => new ReplayDeltaCodec.Writer(26).Encode(frame));
            string inline = Json(new ReplayDeltaCodec.Writer(FakeSchema).Encode(frame));
            Reject(() => new ReplayDeltaCodec.StreamReader(26).DecodeRecord(inline));
            Reject(() => new ReplayDeltaCodec.Reader(26).Decode(JObject.Parse(inline)));
            var packed = PackFakeBaselines(frame);
            Reject(() => new ReplayDeltaCodec.StreamReader(26, (type, id) => frame.Interactions[0]).DecodeRecord(packed.Records[0]));
        });
    }

    private static ReplayFrame[] FakeFrames()
    {
        var initial = Enumerable.Range(0, FakeCount).Select(i => FakeItem(i)).ToArray();
        var hidden = (InteractionReplayFrame[])initial.Clone(); hidden[73] = FakeItem(73, false);
        var moved = (InteractionReplayFrame[])initial.Clone(); moved[74] = FakeItem(74); moved[74].SourcePath += "/0:NewSource";
        moved[74].Pose.Position = new[] { 8f, 9f, 10f }; moved[74].Pose.Rotation = new[] { 0f, .6f, 0f, .8f }; moved[74].Pose.Scale = new[] { .5f, .6f, .7f };
        var effect = new EffectReplayFrame { Key = "native-fake-pickup-effect", Resource = "spore", AnchorTime = 2.25, Seed = 71237 };
        return Enumerable.Range(0, 49).Select(i =>
        {
            double t = i / 4d;
            var state = t < 2.25 || t >= 4.5 && t < 6.25 ? initial : t < 4.5 ? hidden : moved;
            IEnumerable<InteractionReplayFrame> sources = state;
            if (t >= 8.5 && t < 9.5) sources = sources.Where(value => value.Key != initial[75].Key);
            if (t >= 10) sources = sources.Reverse();
            return new ReplayFrame { T = t, SceneSampleTimeKnown = true, SceneSampleTime = t,
                Interactions = sources.Take(500).Concat(new[] { DynamicInteraction(0, i), DynamicInteraction(1, i) })
                    .Concat(sources.Skip(500)).ToArray(),
                Effects = t >= 2.25 && t <= 3.75 ? new[] { effect } : Array.Empty<EffectReplayFrame>() };
        }).ToArray();
    }
    private static InteractionReplayFrame FakeItem(int id, bool active = true) => new()
    {
        Key = "fake-" + id, Kind = "fake-item", Resource = "fake-item/172", SourcePath = "0:Map/2:Alpine/0:Sources/" + id + ":SnowPile",
        Pose = new ObjectPose { Active = active, Position = new[] { id * .25f, 8f, id * -.125f } },
    };
    private static InteractionReplayFrame DynamicInteraction(int id, int sample) => new()
    {
        Key = "dynamic-" + id, Kind = "break-debris", Resource = "native-debris", Pose = new ObjectPose { Position = new[] { sample * .125f, (float)id, 3f } },
    };
    private sealed class FakePacked
    {
        internal readonly FullReplaySharedWriter Writer = new(FakeSchema);
        internal readonly List<string> Records = new();
        internal readonly List<int> Through = new();
        internal string[] Raw = Array.Empty<string>();
    }
    private static FakePacked PackFakeBaselines(params ReplayFrame[] frames)
    {
        var packed = new FakePacked();
        foreach (var frame in frames)
        {
            var record = new ReplayDeltaCodec.Writer(FakeSchema).Encode(frame); packed.Writer.EncodeBaseline(record, frame);
            packed.Records.Add(Json(record)); packed.Through.Add(packed.Writer.Blocks.Count);
        }
        using var output = new MemoryStream(); packed.Writer.FlushPending(output);
        packed.Raw = packed.Writer.Blocks.Select(block => ReadFakeBlock(output, block)).ToArray();
        return packed;
    }
    private static ReplayFrame DecodeFakeBaseline(FakePacked packed, int page)
    {
        object Resolve(Type type, int id)
        {
            if (id < 0 || id >= packed.Through[page]) throw new InvalidDataException("Future shared source.");
            int kind = packed.Writer.Blocks[id].Kind;
            Check(kind == 4 && type == typeof(InteractionReplayFrame) || kind == 9 && type == typeof(string[]));
            return kind == 4 ? new ReplayDeltaCodec.StreamReader(FakeSchema).DecodeSharedRecord(packed.Raw[id], type, "Key") : DecodeFakeOrder(packed.Raw[id], FakeSchema);
        }
        return new ReplayDeltaCodec.StreamReader(FakeSchema, Resolve).DecodeRecord(packed.Records[page])!;
    }
    private static object DecodeFakeOrder(string raw, int schema)
    { char[] characters = raw.ToCharArray(); return new ReplayDeltaCodec.StreamReader(schema).DecodeSharedValue(characters, characters.Length, typeof(string[])); }
    private static string ReadFakeBlock(string path, FullReplaySharedBlock block)
    { using var file = File.OpenRead(path); return ReadFakeBlock(file, block); }
    private static string ReadFakeBlock(Stream file, FullReplaySharedBlock block)
    {
        file.Position = block.Offset;
        using var slice = new FullReplayArchive.PageSlice(file, block.Length, default);
        using var gzip = new GZipStream(slice, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8); return reader.ReadToEnd();
    }
}
