using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static partial class FullReplaySharedBlockTests
{
    private const int StaticTrapCount = 200;
    private const int ConsumedTrap = 73;
    private const double TrapConsumptionTime = 10.25;

    private static void RunStaticTraps(Action<string, Action> test)
    {
        test("200 compact static traps share once per state across independent pages and preserve their one explosion", () => InRoot(root =>
        {
            var frames = StaticTrapFrames();
            string path = Save(root, frames); var info = FullReplayArchive.ReadInfo(path);
            Check(info.Version == 4 && info.Header.Schema == ReplayRules.CurrentSchema);
            Check(info.Pages.Length >= 60, "Fixture did not cross enough independent page boundaries.");
            var blocks = info.SharedBlocks!;
            Check(blocks.Length == StaticTrapCount + 1 && blocks.All(block => block.Kind == 3), "A static trap state was duplicated or skipped.");
            Check(blocks.All(block => block.RawBytes >= 398 && block.RawBytes <= 431), "Fixture no longer matches the recorded compact trap size range.");
            Check(info.Pages[0].SharedThrough == StaticTrapCount && info.Pages[^1].SharedThrough == StaticTrapCount + 1);
            int changes = 0;
            foreach (var page in info.Pages)
            {
                var records = StaticTrapPageRecords(path, page);
                var baseline = (JArray)records[0]["Creatures"]!;
                Check(baseline.Count == StaticTrapCount && baseline.All(token => token is JObject record && record.Count == 1 && record["$Shared"] != null),
                    "An independent page repeated a complete static trap snapshot.");
                foreach (var record in records.Skip(1))
                    if (record["Creatures"] is JArray updates)
                        foreach (var update in updates)
                        {
                            changes++;
                            Check((string?)update["Key"] == frames[0].Creatures[ConsumedTrap].Key && (double)record["T"]! == TrapConsumptionTime,
                                "An unchanged trap emitted another state update.");
                        }
            }
            Check(changes == 1, "The single native consumption did not remain exactly one state change.");

            // Each ReadPage creates a fresh resolver: the last page must work
            // before any earlier scene baseline or consumption event is read.
            using var source = FullReplayArchive.OpenPlayback(path, info);
            foreach (int index in Enumerable.Range(0, info.Pages.Length).Reverse())
            {
                var clip = FullReplayArchive.ReadPage(path, info, index);
                var history = source.LoadHistory(index, default);
                Check(history.Frames.Count == clip.Frames.Count);
                for (int i = 0; i < clip.Frames.Count; i++)
                {
                    var frame = clip.Frames[i];
                    var expected = frames[(int)Math.Round(frame.T * 4)];
                    Check(Json(frame) == Json(expected), "Direct page seek changed a trap, its consumed state, or the explosion.");
                    Check(Json(history.Frames[i].Effects) == Json(expected.Effects), "Effect history lost the native explosion across a page boundary.");
                }
            }
            Check(frames.Single(frame => frame.T == TrapConsumptionTime).Effects.Single().Seed == 173731);
            Check(!FullReplayArchive.ReadPage(path, info, info.Pages.Length - 1).Frames[0].Creatures[ConsumedTrap].Pose.Active);

            // Container 3 uses the same complete entity encoding but the old
            // 512-byte threshold. No bones/material leaves occur in this fixture.
            string legacy = SaveLegacy(root, path, info, true);
            var old = FullReplayArchive.ReadInfo(legacy);
            Check(old.Version == 3 && old.SharedBlocks!.Length == 0 && old.Pages.Length == info.Pages.Length);
            foreach (int index in new[] { old.Pages.Length - 1, 10, 0 })
                Check(Json(FullReplayArchive.ReadPage(legacy, old, index).Frames) == Json(FullReplayArchive.ReadPage(path, info, index).Frames));
            long reduced = new FileInfo(path).Length, original = new FileInfo(legacy).Length;
            Check(reduced < original * .8, $"Compact trap sharing did not reduce full file size enough: {reduced}/{original} bytes.");
            Console.WriteLine($"STATIC TRAP SIZE: traps={StaticTrapCount} pages={info.Pages.Length} states={blocks.Length} raw={blocks.Min(block => block.RawBytes)}-{blocks.Max(block => block.RawBytes)} shared={reduced} inline={original} saving={(1 - (double)reduced / original):P1}");
        }));
        test("compact trap sharing deduplicates rebuilt identities and reuses prior states without chains", () =>
        {
            var writer = new FullReplaySharedWriter();
            var initial = new ReplayFrame { Creatures = Enumerable.Range(0, StaticTrapCount).Select(i => StaticTrap(i, true)).ToArray() };
            var first = new ReplayDeltaCodec.Writer().Encode(initial); writer.EncodeBaseline(first, initial);
            string firstReferences = Json(first);
            Check(writer.Blocks.Count == StaticTrapCount);
            for (int page = 1; page <= 8; page++)
            {
                var copy = JsonConvert.DeserializeObject<ReplayFrame>(Json(initial))!;
                Check(!ReferenceEquals(copy.Creatures[0], initial.Creatures[0]));
                var encoded = new ReplayDeltaCodec.Writer().Encode(copy); writer.EncodeBaseline(encoded, copy);
                Check(Json(encoded) == firstReferences && writer.Blocks.Count == StaticTrapCount);
            }
            var consumed = new ReplayFrame { Creatures = new[] { StaticTrap(ConsumedTrap, false) } };
            var changed = new ReplayDeltaCodec.Writer().Encode(consumed); writer.EncodeBaseline(changed, consumed);
            Check(writer.Blocks.Count == StaticTrapCount + 1 && writer.DeferredHashCount == 0 && writer.NodeTemplateCount == 0);
            var returned = new ReplayFrame { Creatures = new[] { StaticTrap(ConsumedTrap, true) } };
            var restored = new ReplayDeltaCodec.Writer().Encode(returned); writer.EncodeBaseline(restored, returned);
            Check((int)JObject.Parse(Json(restored))["Creatures"]![0]!["$Shared"]! == ConsumedTrap && writer.Blocks.Count == StaticTrapCount + 1);
        });
        test("compact traps retain inline fallback at the shared directory limit without changing other small entities", () =>
        {
            var writer = new FullReplaySharedWriter();
            var known = new ReplayFrame { Creatures = new[] { StaticTrap(0, true) } };
            writer.EncodeBaseline(new ReplayDeltaCodec.Writer().Encode(known), known);
            Check(writer.Blocks.Count == 1);
            while (writer.Blocks.Count < FullReplayArchive.MaximumSharedBlocks) writer.Blocks.Add(new FullReplaySharedBlock());
            var next = new ReplayFrame { Creatures = new[] { StaticTrap(0, true), StaticTrap(1, true) } };
            var encoded = new ReplayDeltaCodec.Writer().Encode(next); writer.EncodeBaseline(encoded, next);
            var updates = (JArray)JObject.Parse(Json(encoded))["Creatures"]!;
            Check(updates[0]["$Shared"] != null && updates[1]["$Shared"] == null && updates[1]["Pose"] != null);
            var decoded = new ReplayDeltaCodec.StreamReader(ReplayRules.CurrentSchema, (type, id) => known.Creatures[0]).DecodeRecord(Json(encoded));
            Check(Json(decoded) == Json(next) && writer.Blocks.Count == FullReplayArchive.MaximumSharedBlocks);
            var ordinary = new ReplayFrame { Creatures = new[] { new CreatureReplayFrame { Key = "small-spider", Kind = "spider", Resource = "spider" } } };
            var fresh = new FullReplaySharedWriter(); var inline = new ReplayDeltaCodec.Writer().Encode(ordinary); string before = Json(inline);
            Check(fresh.EncodeBaseline(inline, ordinary) == 0 && fresh.Blocks.Count == 0 && Json(inline) == before);
        });
    }

    private static ReplayFrame[] StaticTrapFrames()
    {
        var initial = Enumerable.Range(0, StaticTrapCount).Select(i => StaticTrap(i, true)).ToArray();
        var consumed = (CreatureReplayFrame[])initial.Clone(); consumed[ConsumedTrap] = StaticTrap(ConsumedTrap, false);
        var explosion = new[] { new EffectReplayFrame { Key = "native-spore-explosion", Resource = "spore", AnchorTime = TrapConsumptionTime,
            Position = initial[ConsumedTrap].Pose.Position, Seed = 173731, Seeds = new long[] { 173731, 99981 } } };
        return Enumerable.Range(0, 241).Select(i => new ReplayFrame { T = i / 4d,
            Creatures = i / 4d < TrapConsumptionTime ? initial : consumed,
            Effects = i / 4d >= TrapConsumptionTime && i / 4d <= 11.75 ? explosion : Array.Empty<EffectReplayFrame>() }).ToArray();
    }
    private static CreatureReplayFrame StaticTrap(int id, bool active) => new()
    {
        Key = "creature:10320" + id + ":" + id, Kind = "spore-trap", Resource = "spore-trap",
        SourcePath = "0:Map/0:Segments/2:Roots/0:MapGeneration/0:BiomeProps/0:Forest/0:Hazards/0:SporeClusters/0:Clump/12:Forest_SporeFungus (Clone)/" + id + ":Forest_SporeFungus",
        State = active ? 0 : 1,
        Pose = new ObjectPose { Active = active, Position = new[] { id * 1.27137f, 182.3765f + id * .11997f, id * -1.09777f } },
    };
    private static List<JObject> StaticTrapPageRecords(string path, FullReplayPage page)
    {
        using var file = File.OpenRead(path); file.Position = page.Offset;
        using var slice = new FullReplayArchive.PageSlice(file, page.Length, default);
        using var gzip = new GZipStream(slice, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        _ = reader.ReadLine();
        var records = new List<JObject>();
        while (reader.ReadLine() is { } line)
        {
            var record = JObject.Parse(line);
            if ((string?)record["Type"] != "end") records.Add(record);
        }
        return records;
    }
}
