using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class CreatureCoverageTests
{
    public static void Run(Action<string, Action> test)
    {
        test("native condor motion survives codec and forward backward seek", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(); var records = new List<JObject>();
            foreach (int state in new[] { 0, 1, 2, 3 })
            {
                var value = Condor(state);
                records.Add(JObject.Parse(JsonConvert.SerializeObject(writer.Encode(new ReplayFrame
                    { T = records.Count, Creatures = new[] { value } }), ReplayFiles.Json)));
            }
            foreach (int seek in new[] { 3, 0, 2, 1 })
            {
                var reader = new ReplayDeltaCodec.Reader(); ReplayFrame read = null!;
                for (int i = 0; i <= seek; i++) read = reader.Decode(records[i]);
                Check(read.Creatures[0].Kind == "condor" && read.Creatures[0].State == seek &&
                    read.Creatures[0].Pose.Nodes[0].Rotation[1] == seek * .1f);
            }
            var bad = Condor(4); Reject(() => CreatureReplayRules.Validate(bad));
            bad = Condor(0); bad.Line = Tongue().Line; Reject(() => CreatureReplayRules.Validate(bad));
        });
        test("native frog tongue retains 200 world points without a duplicate body pose", () =>
        {
            var value = Tongue(); CreatureReplayRules.Validate(value);
            string json = JsonConvert.SerializeObject(value, ReplayFiles.Json);
            var copy = JsonConvert.DeserializeObject<CreatureReplayFrame>(json, ReplayFiles.Json)!;
            CreatureReplayRules.Validate(copy);
            Check(copy.Pose.Nodes.Length == 0 && copy.Visuals == null && copy.Line!.WorldSpace &&
                copy.Line.Points.Length == 200 * 3 && copy.Line.Points[599] == 599);
            var spider = Tongue(); spider.Kind = spider.Resource = "spider";
            Reject(() => CreatureReplayRules.Validate(spider));
            var badRecord = Token(new ReplayDeltaCodec.Writer().Encode(new ReplayFrame { Creatures = new[] { value } }));
            var creatureToken = (JObject)((JArray)badRecord["Creatures"]!)[0];
            creatureToken["Kind"] = creatureToken["Resource"] = "spider";
            Reject(() => new ReplayDeltaCodec.Reader().Decode(badRecord));
            var reader = new ReplayDeltaCodec.Reader(); var writer = new ReplayDeltaCodec.Writer();
            reader.Decode(Token(writer.Encode(new ReplayFrame { Creatures = new[] { value } })));
            var inherited = Token(writer.Encode(new ReplayFrame { T = 1, Creatures = new[] { value } }));
            inherited["Creatures"] = new JArray(new JObject { ["Key"] = value.Key, ["Kind"] = "spider", ["Resource"] = "spider" });
            Reject(() => reader.Decode(inherited));
            copy.Line!.Points = new float[(CreatureReplayRules.MaximumFrogLinePoints + 1) * 3];
            Reject(() => CreatureReplayRules.Validate(copy));
        });
        test("frog disappearance and backward seek do not retain future tongue positions", () =>
        {
            var before = Tongue(); var gone = Tongue(); gone.Pose.Active = false; gone.Line!.Enabled = false;
            var writer = new ReplayDeltaCodec.Writer(); var reader = new ReplayDeltaCodec.Reader();
            var first = reader.Decode(Token(writer.Encode(new ReplayFrame { Creatures = new[] { before } })));
            var second = reader.Decode(Token(writer.Encode(new ReplayFrame { T = 1, Creatures = new[] { gone } })));
            Check(first.Creatures[0].Line!.Enabled && !second.Creatures[0].Line!.Enabled &&
                !CreatureReplayRules.CanInterpolate(first.Creatures[0], second.Creatures[0]));
            Check(first.Creatures[0].Line!.Points[599] == 599);
        });
        test("audited 295-renderer zombie appearance fits bounded metadata", () =>
        {
            var appearance = Enumerable.Range(0, 295).Select(i => new NativeRendererFrame
                { Path = "mesh/" + i }).ToArray();
            Check(NativeVisualAppearanceRules.Validate(appearance));
            var value = new CreatureReplayFrame { Key = "zombie", Kind = "mushroom-zombie", Resource = "mushroom-zombie",
                State = 3, Visuals = appearance, Pose = new ObjectPose { Nodes = new[] { new NodePose { Path = "0:Scout/0:Armature/0:Hip" } } } };
            CreatureReplayRules.Validate(value);
        });
        test("full replay independent pages restore native condor and 200-point frog line on random seeks", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "peak-creature-pages-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var writer = new FullReplayWriter(root, new ReplayHeader { Scene = "Level_CreatureCoverage", Participants = new[] { "Synthetic" } },
                    new ContinuousReplayOptions { SegmentFrameLimit = 2 });
                for (int i = 0; i < 6; i++)
                {
                    var tongue = Tongue(); tongue.Line!.Points[599] = i;
                    Check(writer.TryEnqueue(new ReplayFrame { T = 100 + i,
                        Actors = new[] { new ActorFrame { Id = "actor" } }, Creatures = new[] { Condor(i % 4), tongue } }));
                }
                var completion = writer.CompleteAsync();
                Check(completion.Wait(TimeSpan.FromSeconds(10)));
                var result = completion.GetAwaiter().GetResult(); Check(result.Status == "completed");
                var info = FullReplayArchive.ReadInfo(result.FilePath); Check(info.Pages.Length > 1);
                foreach (int page in Enumerable.Range(0, info.Pages.Length).Reverse().Concat(new[] { 0, info.Pages.Length - 1 }))
                {
                    var clip = FullReplayArchive.ReadPage(result.FilePath, info, page);
                    foreach (var frame in clip.Frames)
                    {
                        int expected = (int)frame.T;
                        Check(frame.Creatures[0].State == expected % 4 && frame.Creatures[1].Pose.Nodes.Length == 0 &&
                            frame.Creatures[1].Line!.Points.Length == 200 * 3 && frame.Creatures[1].Line!.Points[599] == expected);
                    }
                }
            }
            finally { Directory.Delete(root, true); }
        });
    }
    private static CreatureReplayFrame Condor(int state) => new()
    {
        Key = "native-condor", Kind = "condor", Resource = "condor", State = state,
        Pose = new ObjectPose { Nodes = new[] { new NodePose { Path = "0:Condor1/0:Armature", Rotation = new[] { 0f, state * .1f, 0f, 1f } } } },
    };
    private static CreatureReplayFrame Tongue() => new()
    {
        Key = "frog-tongue", Kind = "frog-tongue", Resource = "frog-tongue",
        Line = new CreatureReplayLine { Enabled = true, WorldSpace = true, Width = .03f,
            Points = Enumerable.Range(0, 200 * 3).Select(i => (float)i).ToArray() },
    };
    private static JObject Token(object value) => JObject.Parse(JsonConvert.SerializeObject(value, ReplayFiles.Json));
    private static void Check(bool value) { if (!value) throw new Exception("Native creature coverage assertion failed."); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid native creature data was accepted."); }
}
