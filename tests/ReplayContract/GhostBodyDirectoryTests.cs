using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class GhostBodyDirectoryTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("ghost bodies use distinct fixed resources and schema 21 native ownership", () =>
        {
            foreach (string kind in new[] { "ghost-ball", "player-ghost" })
            {
                Check(CreatureDirectoryRules.IsKnownKind(kind) && CreatureDirectoryRules.ValidResource(kind, kind));
                Check(CreatureDirectoryRules.BodyRequiresSchema21(kind) && !CreatureDirectoryRules.IsLegacyKind(kind));
                Check(!CreatureDirectoryRules.RequiresScenePath(kind));
                foreach (string other in new[] { "Resources/" + kind, "scoutmaster-ghost", "spider", "GhostBall", "PlayerGhost" })
                    Check(!CreatureDirectoryRules.ValidResource(kind, other));
            }
            foreach (string previous in new[] { "scoutmaster-ghost", "mushroom-zombie", "condor", "beetle", "spore-trap" })
                Check(!CreatureDirectoryRules.BodyRequiresSchema21(previous));
        });
        test("ghost body final poses appearance and absence survive independently decoded reverse seeks", () =>
        {
            foreach (string kind in new[] { "ghost-ball", "player-ghost" })
            {
                var frames = Enumerable.Range(0, 4).Select(i => new ReplayFrame { T = i,
                    Creatures = i == 2 ? Array.Empty<CreatureReplayFrame>() : new[] { Frame(kind, i) } }).ToArray();
                var writer = new ReplayDeltaCodec.Writer();
                var records = frames.Select(x => JObject.Parse(JsonConvert.SerializeObject(writer.Encode(x), ReplayFiles.Json))).ToArray();
                foreach (int index in new[] { 3, 1, 2, 0, 3 })
                {
                    var reader = new ReplayDeltaCodec.Reader(); ReplayFrame current = null!;
                    for (int i = 0; i <= index; i++) current = reader.Decode(records[i]);
                    Check(current.Creatures.Length == (index == 2 ? 0 : 1));
                    if (index == 2) continue;
                    var f = current.Creatures[0];
                    Check(f.Kind == kind && f.Resource == kind && f.State == 0 && f.SampleTime == index);
                    Check(f.Pose.Position[0] == index && f.Pose.Nodes[0].Position[1] == index * .25f);
                    Check(f.Visuals![0].Materials[0].Properties[0].Values[0] == index * .2f);
                }
            }
        });
    }
    private static CreatureReplayFrame Frame(string kind, int i) => new()
    {
        Key = kind, Kind = kind, Resource = kind, State = 0, SampleTimeKnown = true, SampleTime = i,
        Pose = new ObjectPose { Position = new[] { (float)i, 0f, 0f },
            Nodes = new[] { new NodePose { Path = "0:Body", Position = new[] { 0f, i * .25f, 0f } } } },
        Visuals = new[] { new NativeRendererFrame { Path = "0:Body", Materials = new[] { new NativeMaterialFrame
            { Name = "native", Shader = "native", Properties = new[] { new NativeShaderPropertyFrame { Name = "_Opacity", Values = new[] { i * .2f } } } } } } },
    };
    private static void Check(bool value) { if (!value) throw new Exception("Ghost body resource directory assertion failed."); }
}
