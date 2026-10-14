using System.Text;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static partial class FullReplaySharedBlockTests
{
    private static void RunSharedLimits(Action<string, Action> test)
    {
        test("large scene shared expansion above eight MiB records independent tiny baselines without dropping effects", () => InRoot(root =>
        {
            var nodes = LimitNodes(240);
            var armed = Enumerable.Range(0, 112).Select(i => LimitItem(i, nodes, 0)).ToArray();
            var moved = Enumerable.Range(0, armed.Length).Select(i => LimitItem(i, nodes, 1)).ToArray();
            var effect = new[] { new EffectReplayFrame { Key = "limit-spore", Resource = "spore", AnchorTime = .5, Seed = 76181 } };
            var frames = Enumerable.Range(0, 7).Select(i => new ReplayFrame { T = i * .5,
                Items = i == 0 ? armed : moved, Effects = i >= 1 && i <= 3 ? effect : Array.Empty<EffectReplayFrame>() }).ToArray();
            var sharing = new FullReplaySharedWriter(); var initial = new ReplayDeltaCodec.Writer().Encode(frames[0]);
            long expanded = sharing.EncodeBaseline(initial, frames[0]);
            Check(expanded > ReplayRules.MaxLineCharacters && expanded < FullReplayArchive.MaximumPageRawBytes,
                "Fixture must reproduce the old aggregate 8 MiB rejection.");
            Check(sharing.Blocks.All(block => block.RawBytes < ReplayRules.MaxLineCharacters));
            Check(Encoding.UTF8.GetByteCount(Json(initial)) < 16 * 1024, "Referenced baseline unexpectedly embeds object trees.");

            string path = Save(root, frames); var info = FullReplayArchive.ReadInfo(path);
            Check(info.Version == 4 && info.Pages.Length >= 3 && info.FrameCount == frames.Length);
            Check(info.SharedBlocks!.Take(info.Pages[0].SharedThrough).Sum(block => (long)block.RawBytes) == expanded);
            Check(info.SharedBlocks!.Any(block => block.Kind == 8), "Later baseline did not exercise a repeated shared node leaf.");
            Check(info.Pages.All(page => page.DecodedBytes <= FullReplayArchive.MaximumPageDecodedBytes && page.RawBytes <= FullReplayArchive.MaximumPageRawBytes));
            using var playback = FullReplayArchive.OpenPlayback(path, info);
            foreach (var page in info.Pages.AsEnumerable().Reverse())
            {
                var clip = FullReplayArchive.ReadPage(path, info, page.Index);
                var history = playback.LoadHistory(page.Index, default);
                Check(clip.Complete && history.Frames.Count == clip.Frames.Count);
                for (int i = 0; i < clip.Frames.Count; i++)
                {
                    var frame = clip.Frames[i]; var expected = frames[(int)Math.Round(frame.T * 2)];
                    Check(Json(frame) == Json(expected), "Large direct-seek page did not preserve all item or effect fields.");
                    Check(Json(history.Frames[i].Effects) == Json(expected.Effects));
                }
            }
            Console.WriteLine($"LARGE SHARED BASELINE: expanded={expanded} encoded={Encoding.UTF8.GetByteCount(Json(initial))} pages={info.Pages.Length} file={new FileInfo(path).Length}");
        }));
        test("shared leaf page expansion counts repeated block IDs once and resets for each independent page", () =>
        {
            var nodes = LimitNodes(240);
            var writer = new FullReplaySharedWriter();
            var first = new ReplayFrame { Items = Enumerable.Range(0, 112).Select(i => LimitItem(i, nodes, 0)).ToArray() };
            writer.EncodeBaseline(new ReplayDeltaCodec.Writer().Encode(first), first);
            var changed = new ReplayFrame { Items = Enumerable.Range(0, 112).Select(i => LimitItem(i, nodes, 1)).ToArray() };
            var encoded = new ReplayDeltaCodec.Writer().Encode(changed);
            long bytes = writer.EncodeBaseline(encoded, changed);
            var leafIds = JObject.Parse(Json(encoded))["Items"]!.Select(item => (int)item["Pose"]!["Nodes"]!["$Shared"]!).ToArray();
            Check(leafIds.Length == 112 && leafIds.Distinct().Count() == 1);
            Check(bytes == writer.Blocks[leafIds[0]].RawBytes && bytes * leafIds.Length > ReplayRules.MaxLineCharacters,
                "Shared leaf accounting did not follow reader block-ID deduplication.");
            // A new page still accounts an already-known block; catalogue-wide
            // deduplication must not make a later page's expanded budget zero.
            var next = new ReplayFrame { Items = Enumerable.Range(0, 112).Select(i => LimitItem(i, nodes, 2)).ToArray() };
            var again = new ReplayDeltaCodec.Writer().Encode(next);
            Check(writer.EncodeBaseline(again, next) == bytes);
        });
        test("shared page raw cap remains 128 MiB and repeated known references do not spend it twice", () =>
        {
            var writer = new FullReplaySharedWriter();
            var states = Enumerable.Range(0, 17).Select(i => StaticTrap(i, true)).ToArray();
            var first = new ReplayFrame { Creatures = states };
            writer.EncodeBaseline(new ReplayDeltaCodec.Writer().Encode(first), first);
            // Exercise exact accounting boundaries without allocating 128 MiB
            // of redundant test payload; these are existing block metadata.
            foreach (var block in writer.Blocks) block.RawBytes = ReplayRules.MaxLineCharacters;
            var boundary = new ReplayFrame { Creatures = states.Take(16).ToArray() };
            Check(writer.EncodeBaseline(new ReplayDeltaCodec.Writer().Encode(boundary), boundary) == FullReplayArchive.MaximumPageRawBytes);
            var repeated = new ReplayFrame { Creatures = new[] { states[0], states[0], states[1] } };
            // Build an isolated full baseline to test the shared account only:
            // ordinary replay validation separately rejects duplicate keys.
            var record = new Dictionary<string, object?> { ["Creatures"] = new object[3] };
            Check(writer.EncodeBaseline(record, repeated) == 2L * ReplayRules.MaxLineCharacters);
            try { writer.EncodeBaseline(new ReplayDeltaCodec.Writer().Encode(first), first); throw new Exception("128 MiB shared page cap was disabled."); }
            catch (InvalidDataException e) { Check(e.Message == "Shared snapshot expansion exceeds the page budget."); }
        });
        test("individual shared snapshots retain the eight MiB bound", () =>
        {
            var writer = new FullReplaySharedWriter();
            var frame = new ReplayFrame { Items = new[] { LimitItem(0, Array.Empty<NodePose>(), 0) } };
            var record = (Dictionary<string, object?>)new ReplayDeltaCodec.Writer().Encode(frame);
            var update = (Dictionary<string, object?>)((object[])record["Items"]!)[0];
            update["OversizedTestValue"] = new string('x', ReplayRules.MaxLineCharacters);
            try { writer.EncodeBaseline(record, frame); throw new Exception("Individual shared snapshot limit was disabled."); }
            catch (InvalidDataException e) { Check(e.Message == "Shared snapshot exceeds its raw byte limit." && writer.Blocks.Count == 0); }
        });
        test("large shared scene still stops before publishing a page above 64 MiB decoded", () => InRoot(root =>
        {
            var nodes = LimitNodes(500);
            var frame = new ReplayFrame { Items = Enumerable.Range(0, 400).Select(i => LimitItem(i, nodes, 0)).ToArray() };
            long estimate = RollingBuffer.Estimate(frame);
            Check(estimate > FullReplayArchive.MaximumPageDecodedBytes && estimate < 128L * 1024 * 1024);
            var writer = new FullReplayWriter(root, new ReplayHeader { Scene = "Level_SharedDecodedLimit" });
            Check(writer.TryEnqueue(frame));
            var result = writer.CompleteAsync().GetAwaiter().GetResult();
            Check(result.Status == "faulted" && result.FaultCode == "page-size-limit" && result.WrittenFrames == 0,
                "Expanded decoded memory no longer has its independent page guard.");
            Check(!File.Exists(writer.FilePath));
        }));
    }

    private static NodePose[] LimitNodes(int pathLength) => Enumerable.Range(0, ReplayRules.MaxNodes)
        .Select(i => new NodePose("root/" + new string('n', pathLength) + "/joint-" + i,
            new[] { i * .003f, i * .002f, i * .001f }, new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f })).ToArray();
    private static ItemFrame LimitItem(int id, NodePose[] nodes, float offset) => new()
    {
        Key = "large-scene-item:" + id, ItemId = 1,
        Pose = new ObjectPose { Position = new[] { id * 1.21f + offset, 0, 0 }, Nodes = nodes },
    };
}
