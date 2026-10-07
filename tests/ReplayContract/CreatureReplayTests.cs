using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class CreatureReplayTests
{
    public static void Run(Action<string, Action> test)
    {
        test("creature poses and native silk retain sampled geometry without enemy commands", () =>
        {
            var source = Spider(); CreatureReplayRules.Validate(source);
            string json = JsonConvert.SerializeObject(source, ReplayFiles.Json);
            var copy = JsonConvert.DeserializeObject<CreatureReplayFrame>(json, ReplayFiles.Json)!;
            CreatureReplayRules.Validate(copy);
            Check(copy.Line!.Points[4] == 4 && copy.State == 2 && !json.Contains("RPC") && !json.Contains("Collider"));
        });
        test("creature interpolation does not bridge inactive traps or a changed lifetime", () =>
        {
            var a = Spider(); var b = Spider(); Check(CreatureReplayRules.CanInterpolate(a, b));
            b.Pose.Active = false; Check(!CreatureReplayRules.CanInterpolate(a, b)); b.Pose.Active = true;
            b.Key = "replacement"; Check(!CreatureReplayRules.CanInterpolate(a, b)); b.Key = a.Key;
            b.SourcePath = "changed-template"; Check(!CreatureReplayRules.CanInterpolate(a, b));
        });
        test("mine active disappearance and backward data preserve before explosion state", () =>
        {
            var mine = new CreatureReplayFrame { Key = "native-trap", Kind = "spore-trap", Resource = "spore-trap", SourcePath = "map/trap", Pose = new ObjectPose() };
            var writer = new ReplayDeltaCodec.Writer(); var reader = new ReplayDeltaCodec.Reader(ReplayRules.CurrentSchema);
            var before = new ReplayFrame { T = 0, Creatures = new[] { mine } };
            var hidden = new CreatureReplayFrame { Key = mine.Key, Kind = mine.Kind, Resource = mine.Resource, SourcePath = mine.SourcePath, State = 1, Pose = new ObjectPose { Active = false } };
            var a = reader.Decode(Token(writer.Encode(before)));
            var b = reader.Decode(Token(writer.Encode(new ReplayFrame { T = .1, Creatures = new[] { hidden } })));
            Check(a.Creatures[0].Pose.Active && !b.Creatures[0].Pose.Active && !CreatureReplayRules.CanInterpolate(a.Creatures[0], b.Creatures[0]));
            Check(a.Creatures[0].Pose.Active);
        });
        test("creature validation bounds resources hierarchy and silk samples", () =>
        {
            var value = Spider(); value.Resource = "arbitrary-network-prefab"; Bad(value);
            value = Spider(); value.Pose.Nodes = new NodePose[CreatureReplayRules.MaximumNodes + 1]; Bad(value);
            value = Spider(); value.Line!.Points = new float[4]; Bad(value);
            value = Spider(); value.Line!.Points[0] = float.NaN; Bad(value);
            value = Spider(); value.State = 4; Bad(value);
            value = Spider(); value.Pose.Rotation = new float[4]; Bad(value);
        });
        test("native wrap clips and transform animation remain per part", () =>
        {
            var value = Wrap(); WebWrapReplayRules.Validate(value);
            var copy = JsonConvert.DeserializeObject<WebWrapReplayFrame>(JsonConvert.SerializeObject(value, ReplayFiles.Json), ReplayFiles.Json)!;
            Check(copy.Parts[0].Clip != copy.Parts[1].Clip && copy.Parts[0].ParentScale[0] == 2);
            Reject(() => { copy.Parts[0].Clip = float.NaN; WebWrapReplayRules.Validate(copy); });
            copy = Wrap(); copy.Parts[1].Path = copy.Parts[0].Path; Reject(() => WebWrapReplayRules.Validate(copy));
        });
        test("web wrap null is unknown and known empty remains a separate state", () =>
        {
            WebWrapReplayRules.Validate(null); WebWrapReplayRules.Validate(new WebWrapReplayFrame());
            Check(WebWrapReplayRules.Estimate(null) == 0 && WebWrapReplayRules.Estimate(Wrap()) > 0);
        });
        test("creature budgets release shared states only after their last frame", () =>
        {
            var sample = Spider(); var budget = new ReplayTrackBudget<CreatureReplayFrame>(CreatureReplayRules.Estimate); var frames = new[] { sample };
            budget.Acquire(frames); long bytes = budget.Bytes;
            budget.Acquire(frames); Check(bytes == budget.Bytes); budget.Release(frames); Check(bytes == budget.Bytes); budget.Release(frames); Check(budget.Bytes == 0);
        });
    }
    private static CreatureReplayFrame Spider() => new()
    {
        Key = "spider-synthetic", Kind = "spider", Resource = "spider", State = 2,
        Pose = new ObjectPose { Nodes = new[] { new NodePose { Path = "0:Spider/0:Body" } } },
        Line = new CreatureReplayLine { Enabled = true, WorldSpace = true, Width = .02f, Points = new[] { 1f, 2f, 3f, 4f, 4f, 5f } },
    };
    private static WebWrapReplayFrame Wrap() => new() { Parts = new[]
    {
        new WebWrapReplayPart { Path = "./body#0/wrap#0", Active = true, Clip = .8f, ParentScale = new[] { 2f, 2f, 2f } },
        new WebWrapReplayPart { Path = "./body#0/wrap#1", Active = true, Clip = .3f },
    } };
    private static JObject Token(object value) => JObject.Parse(JsonConvert.SerializeObject(value, ReplayFiles.Json));
    private static void Bad(CreatureReplayFrame value) => Reject(() => CreatureReplayRules.Validate(value));
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Malformed creature state was accepted."); }
    private static void Check(bool condition) { if (!condition) throw new Exception("Creature replay assertion failed."); }
}
