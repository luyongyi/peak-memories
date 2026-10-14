using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class CreatureActivationLifecycleTests
{
    private const string Key = "creature:scoutmaster:same-native-instance";
    public static void Run(Action<string, Action> test)
    {
        test("dormant scoutmaster omission encodes activation disappearance and same-key reactivation as complete independent births", () =>
        {
            foreach (int schema in Enumerable.Range(15, ReplayRules.CurrentSchema - 14))
            {
                var frames = Frames(); var writer = new ReplayDeltaCodec.Writer(schema);
                var tree = new ReplayDeltaCodec.Reader(schema); var stream = new ReplayDeltaCodec.StreamReader(schema);
                var retained = new List<(ReplayFrame Frame, string Json)>();
                foreach (var frame in frames)
                {
                    string encoded = Json(writer.Encode(frame)); var record = JObject.Parse(encoded);
                    if (frame.T == 1.25 || frame.T == 4.8)
                    {
                        var birth = (JObject)record["Creatures"]![0]!;
                        Check((bool?)birth["Full"] == true, "Activation was encoded against an absent creature.");
                        Check(birth["Pose"]!["Nodes"] is JArray && birth["Visuals"] is JArray, "Activation omitted complete native bones or appearance.");
                    }
                    if (frame.T == 2.45 || frame.T == 6)
                        Check(record["RemovedCreatures"]!.Values<string>().SequenceEqual(new[] { Key }), "Disappearance did not remove the same native identity.");
                    var a = tree.Decode(record); var b = stream.DecodeRecord(encoded)!;
                    Check(Json(a.Creatures) == Json(frame.Creatures) && Json(b.Creatures) == Json(frame.Creatures));
                    ReplayRules.ValidateVersion(b, schema);
                    retained.Add((b, Json(b)));
                }
                foreach (var saved in retained) Check(Json(saved.Frame) == saved.Json, "A later activation mutated an earlier decoded checkpoint.");
            }
        });
        test("reintroducing the exact prior immutable creature after removal still writes a full birth", () =>
        {
            var creature = Creature(1); var writer = new ReplayDeltaCodec.Writer();
            var first = Frame(0, creature); var absent = Frame(.05); var returned = Frame(.1, creature);
            string start = Json(writer.Encode(first)); string end = Json(writer.Encode(absent)); string wake = Json(writer.Encode(returned));
            Check((bool?)JObject.Parse(wake)["Creatures"]![0]!["Full"] == true);
            var reader = new ReplayDeltaCodec.StreamReader(ReplayRules.CurrentSchema);
            Check(reader.DecodeRecord(start)!.Creatures.Length == 1);
            Check(reader.DecodeRecord(end)!.Creatures.Length == 0);
            Check(Json(reader.DecodeRecord(wake)!.Creatures.Single()) == Json(creature));
        });
        test("independent page baselines forget a previously decoded active scoutmaster when seeking into dormancy", () =>
        {
            var frames = Frames(); var reader = new ReplayDeltaCodec.StreamReader(ReplayRules.CurrentSchema); var tree = new ReplayDeltaCodec.Reader();
            // Each record starts its own complete page; deliberately reuse the
            // decoders so a future seek cannot leak a creature into an empty page.
            foreach (int index in new[] { 8, 0, 4, 6, 3, 10, 9, 1, 7 })
            {
                var expected = frames[index]; string record = Json(new ReplayDeltaCodec.Writer().Encode(expected));
                Check((bool?)JObject.Parse(record)["ObjectsFull"] == true);
                Check(Json(reader.DecodeRecord(record)!.Creatures) == Json(expected.Creatures));
                Check(Json(tree.Decode(JObject.Parse(record)).Creatures) == Json(expected.Creatures));
            }
        });
        test("real full archives independently decode dormant active disappeared and reactivated creature pages in reverse order", () => InRoot(root =>
        {
            var frames = Frames(); string path = Save(root, frames); var info = FullReplayArchive.ReadInfo(path);
            Check(info.Pages.Length >= 5 && info.SharedBlocks!.Any(block => block.Kind == 3), "Fixture did not exercise independent shared creature baselines.");
            var expected = frames.ToDictionary(frame => frame.T);
            foreach (int pageIndex in Enumerable.Range(0, info.Pages.Length).Reverse().Concat(new[] { 0, info.Pages.Length - 1 }))
            {
                // Public ReadPage creates a fresh codec and resolves only this
                // page's immutable shared references; no preceding page replay.
                var page = FullReplayArchive.ReadPage(path, info, pageIndex);
                foreach (var frame in page.Frames) Check(Json(frame.Creatures) == Json(expected[frame.T].Creatures));
            }
            Check(FullReplayArchive.ReadPage(path, info, 0).Frames.All(frame => frame.Creatures.Length == 0));
            Check(FullReplayArchive.ReadPage(path, info, info.Pages.Length - 1).Frames.All(frame => frame.Creatures.Length == 0));
        }));
        test("real paged timeline seeks around activation never resurrect a dormant creature or keep its earlier bones and appearance", () => InRoot(root =>
        {
            var frames = Frames(); string path = Save(root, frames); var info = FullReplayArchive.ReadInfo(path);
            using var timeline = new PagedReplayTimeline(info.Header, info.Duration, info.FrameCount, info.ActorIds,
                info.Pages.Select(page => new ReplayPageRange(page.Start, page.End)).ToArray(),
                FullReplayArchive.CreatePageLoader(path, info), complete: info.Complete,
                decodedPageBytes: info.Pages.Select(page => page.DecodedBytes).ToArray());
            foreach (double target in new[] { 4.8, 0, 1.225, 1.25, 2.45, 3.6, 4.85, 1.3, 6, 1.25, 4.8 })
            {
                var sample = Ready(timeline, target);
                var expected = frames.Last(frame => frame.T <= target);
                Check(Json(sample.Left.Creatures) == Json(expected.Creatures), "Wrong creature lifetime or old visual state after seek to " + target);
                if (sample.Left.Creatures.Length != 0)
                {
                    var creature = sample.Left.Creatures.Single();
                    Check(creature.Key == Key && creature.Pose.Active && creature.Pose.Nodes.Length == 12);
                    Check(creature.Visuals != null && creature.Visuals[0].Materials.Length == 1);
                }
            }
        }));
    }
    private static ReplayFrame[] Frames()
    {
        var first = Creature(1); var firstMoved = Creature(2); var reactivated = Creature(3); var secondMoved = Creature(4);
        return new[] { Frame(0), Frame(.05), Frame(1.2), Frame(1.25, first), Frame(1.3, firstMoved), Frame(2.4, firstMoved),
            Frame(2.45), Frame(3.6), Frame(4.8, reactivated), Frame(4.85, secondMoved), Frame(6), Frame(6.05) };
    }
    private static ReplayFrame Frame(double time, params CreatureReplayFrame[] creatures) => new()
    { T = time, Actors = new[] { new ActorFrame { Id = "synthetic-actor" } }, Creatures = creatures };
    private static CreatureReplayFrame Creature(int revision) => new()
    {
        Key = Key, Kind = "scoutmaster", Resource = "scoutmaster", State = 0,
        Pose = new ObjectPose { Position = new[] { revision * 4f, 0f, 0f }, Active = true,
            Nodes = Enumerable.Range(0, 12).Select(i => new NodePose { Path = i + ":native-bone", Position = new[] { 0f, i * .1f + revision, 0f } }).ToArray() },
        Visuals = new[] { new NativeRendererFrame { Path = "0:native-bone", Mesh = "scoutmaster-mesh-" + revision,
            BlendShapes = new[] { revision * 5f }, Materials = new[] { new NativeMaterialFrame { Name = "scoutmaster-native", Shader = "native",
                Properties = new[] { new NativeShaderPropertyFrame { Name = "_Tint", Kind = 1, Values = new[] { revision * .125f, .25f, .5f, 1f } } } } } } },
    };
    private static string Save(string root, ReplayFrame[] frames)
    {
        var writer = new FullReplayWriter(root, new ReplayHeader { Scene = "SyntheticScoutmasterLifecycle", Participants = new[] { "Synthetic" } });
        foreach (var frame in frames) Check(writer.TryEnqueue(frame));
        var done = writer.CompleteAsync(); Check(done.Wait(TimeSpan.FromSeconds(10)), "Full archive writer did not finish.");
        var result = done.GetAwaiter().GetResult(); Check(result.Status == "completed", result.Status); return result.FilePath;
    }
    private static ReplayTimelineSample Ready(IReplayTimeline timeline, double time)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        { if (timeline.TrySample(time, out var sample)) return sample; Thread.Sleep(1); }
        throw new Exception("Paged creature activation seek timed out at " + time);
    }
    private static void InRoot(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "peak-scoutmaster-lifecycle-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, true); }
    }
    private static string Json(object value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static void Check(bool condition, string message = "")
    { if (!condition) throw new Exception("Scoutmaster activation serialization contract failed: " + message); }
}
