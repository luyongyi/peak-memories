using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class CreatureExtendedCoverageTests
{
    private static readonly string[] additions = { "beetle", "scoutmaster", "antlion", "venus-flytrap", "tumbleweed", "voice-thief", "skeleton", "scoutmaster-ghost", "capybara" };
    public static void Run(Action<string, Action> test)
    {
        test("independent creature kinds require a fixed resource whitelist and scene-only identities", () =>
        {
            foreach (string kind in additions)
            {
                var frame = Creature(kind, 0); CreatureReplayRules.Validate(frame);
                Check(!CreatureDirectoryRules.IsLegacyKind(kind));
                frame.Resource = "Resources/" + kind; Reject(() => CreatureReplayRules.Validate(frame));
                frame.Resource = "frog-tongue"; Reject(() => CreatureReplayRules.Validate(frame));
                if (CreatureDirectoryRules.RequiresScenePath(kind))
                { frame.Resource = kind; frame.SourcePath = ""; Reject(() => CreatureReplayRules.Validate(frame)); }
            }
            var unknown = Creature("unrecognized-native-ai", 0); Reject(() => CreatureReplayRules.Validate(unknown));
        });
        test("normal and web spiders plus NPC and player zombies retain distinct immutable resources", () =>
        {
            foreach (var pair in new[] { ("spider", "spider-normal"), ("spider", "spider-web"),
                ("mushroom-zombie", "mushroom-zombie-npc"), ("mushroom-zombie", "mushroom-zombie-player") })
            {
                var before = Creature(pair.Item1, 0); before.Resource = pair.Item2; CreatureReplayRules.Validate(before);
                var after = Creature(pair.Item1, 1); after.Resource = pair.Item2;
                Check(CreatureReplayRules.CanInterpolate(before, after));
                after.Resource = pair.Item1; Check(!CreatureReplayRules.CanInterpolate(before, after));
            }
            foreach (string name in new[] { "Capybara", "Capybara (3)", "Capybara (3)(Clone)", "Capybara Void" }) Check(CreatureDirectoryRules.IsCapybaraName(name));
            foreach (string name in new[] { "Capybara Extra", "Luggage", "SomeCapybara", "Capybara (fake)" }) Check(!CreatureDirectoryRules.IsCapybaraName(name));
            Check(CreatureDirectoryRules.IsWebSpiderName("SpiderDropper Web Variant (2)(Clone)"));
        });
        test("all independent NPCs preserve final bones appearance and disappearance across bidirectional delta seeks", () =>
        {
            var frames = Enumerable.Range(0, 5).Select(i => new ReplayFrame { T = i,
                Creatures = additions.Select(kind => Creature(kind, i)).ToArray() }).ToArray();
            frames[3].Creatures = Array.Empty<CreatureReplayFrame>();
            var writer = new ReplayDeltaCodec.Writer();
            var records = frames.Select(frame => JObject.Parse(JsonConvert.SerializeObject(writer.Encode(frame), ReplayFiles.Json))).ToArray();
            foreach (int target in new[] { 4, 1, 3, 0, 2, 4 })
            {
                var reader = new ReplayDeltaCodec.Reader(); ReplayFrame current = null!;
                for (int i = 0; i <= target; i++) current = reader.Decode(records[i]);
                Check(current.Creatures.Length == (target == 3 ? 0 : additions.Length));
                foreach (var creature in current.Creatures)
                {
                    Check(creature.Pose.Position[0] == target && creature.Pose.Nodes[0].Position[1] == target * .1f);
                    Check(creature.Visuals![0].Materials[0].Properties[0].Values[0] == target * .2f);
                    Check(creature.Resource == creature.Kind);
                }
            }
            Check(frames[0].Creatures[0].Pose.Position[0] == 0 && frames[4].Creatures[0].Pose.Position[0] == 4);
        });
        test("independent creature page baselines retain variants when read in reverse order", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "peak-independent-creatures-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                var writer = new FullReplayWriter(root, new ReplayHeader { Scene = "NativeCreatureContract", Participants = new[] { "Synthetic" } }, new ContinuousReplayOptions { SegmentFrameLimit = 2 });
                for (int i = 0; i < 6; i++)
                {
                    var zombie = Creature("mushroom-zombie", i); zombie.Resource = i % 2 == 0 ? "mushroom-zombie-npc" : "mushroom-zombie-player";
                    Check(writer.TryEnqueue(new ReplayFrame { T = i, Actors = new[] { new ActorFrame { Id = "actor" } }, Creatures = additions.Select(kind => Creature(kind, i)).Append(zombie).ToArray() }));
                }
                var done = writer.CompleteAsync(); Check(done.Wait(TimeSpan.FromSeconds(10))); var result = done.GetAwaiter().GetResult(); Check(result.Status == "completed");
                var info = FullReplayArchive.ReadInfo(result.FilePath); Check(info.Pages.Length > 1);
                foreach (int index in Enumerable.Range(0, info.Pages.Length).Reverse())
                    foreach (var frame in FullReplayArchive.ReadPage(result.FilePath, info, index).Frames)
                    {
                        int i = (int)frame.T; Check(frame.Creatures.Length == additions.Length + 1);
                        Check(frame.Creatures[^1].Resource == (i % 2 == 0 ? "mushroom-zombie-npc" : "mushroom-zombie-player"));
                        Check(frame.Creatures[2].Kind == "antlion" && frame.Creatures[2].SourcePath == "NativeMap/antlion");
                    }
            }
            finally { Directory.Delete(root, true); }
        });
    }
    private static CreatureReplayFrame Creature(string kind, int sample) => new()
    {
        Key = "native-" + kind, Kind = kind, Resource = kind, SourcePath = CreatureDirectoryRules.RequiresScenePath(kind) ? "NativeMap/" + kind : "",
        Pose = new ObjectPose { Position = new[] { (float)sample, 0f, 0f }, Nodes = new[] { new NodePose { Path = "0:NativeMesh", Position = new[] { 0f, sample * .1f, 0f } } } },
        Visuals = new[] { new NativeRendererFrame { Path = "0:NativeMesh", Materials = new[] { new NativeMaterialFrame { Name = "native", Shader = "native",
            Properties = new[] { new NativeShaderPropertyFrame { Name = "_Opacity", Values = new[] { sample * .2f } } } } } } },
    };
    private static void Check(bool value) { if (!value) throw new Exception("Independent creature coverage assertion failed."); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Unknown creature resource was accepted."); }
}
