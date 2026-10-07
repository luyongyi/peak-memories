using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class CreatureCapacityTests
{
    public static void Run(Action<string, Action> test)
    {
        test("compact static traps and dynamic creatures retain independent maximum capacities through codec", () =>
        {
            var traps = Enumerable.Range(0, CreatureReplayRules.MaximumStaticTraps).Select(Trap);
            var dynamic = Enumerable.Range(0, CreatureReplayRules.MaximumDynamicEntities).Select(Spider);
            var frame = new ReplayFrame { Creatures = traps.Concat(dynamic).ToArray() };
            ReplayRules.Validate(frame, -1);
            var record = new ReplayDeltaCodec.Writer().Encode(frame);
            string json = JsonConvert.SerializeObject(record, ReplayFiles.Json);
            Check(json.Length < ReplayRules.MaxLineCharacters);
            var read = new ReplayDeltaCodec.Reader().Decode(JObject.Parse(json));
            Check(read.Creatures.Length == CreatureReplayRules.MaximumEntities);
            Check(read.Creatures.Count(CreatureReplayRules.CompactStaticTrap) == CreatureReplayRules.MaximumStaticTraps);
            Check(read.Creatures.Count(x => x.Kind == "spider") == CreatureReplayRules.MaximumDynamicEntities);
        });
        test("static allowance cannot be used to exceed the dynamic creature cap", () =>
        {
            var frame = new ReplayFrame { Creatures = Enumerable.Range(0, CreatureReplayRules.MaximumDynamicEntities + 1).Select(Spider).ToArray() };
            Reject(() => ReplayRules.Validate(frame, -1));
        });
        test("large static allowance rejects detailed hierarchies while preserving legacy trap records", () =>
        {
            var detailed = Enumerable.Range(0, CreatureReplayRules.MaximumDynamicEntities + 1).Select(i =>
            {
                var frame = Trap(i); frame.Pose.Nodes = new[] { new NodePose { Path = "mesh" } }; return frame;
            }).ToArray();
            ReplayRules.Validate(new ReplayFrame { Creatures = detailed.Take(CreatureReplayRules.MaximumDynamicEntities).ToArray() }, -1);
            Reject(() => ReplayRules.Validate(new ReplayFrame { Creatures = detailed }, -1));
            var compact = Enumerable.Range(0, CreatureReplayRules.MaximumStaticTraps + 1).Select(Trap).ToArray();
            Reject(() => ReplayRules.Validate(new ReplayFrame { Creatures = compact }, -1));
        });
        test("compact trap disappearance reintroduction and back seek retain static source identity", () =>
        {
            var alive = Trap(1); var exploded = Trap(1); exploded.Pose.Active = false;
            var writer = new ReplayDeltaCodec.Writer(); var records = new List<JObject>();
            foreach (var value in new[] { alive, exploded, alive })
                records.Add(JObject.Parse(JsonConvert.SerializeObject(writer.Encode(new ReplayFrame { T = records.Count * .1, Creatures = new[] { value } }), ReplayFiles.Json)));
            foreach (int seek in new[] { 2, 1, 0 })
            {
                var reader = new ReplayDeltaCodec.Reader(); ReplayFrame read = null!;
                for (int i = 0; i <= seek; i++) read = reader.Decode(records[i]);
                Check(CreatureReplayRules.CompactStaticTrap(read.Creatures[0]));
                Check(read.Creatures[0].Pose.Active == (seek != 1) && read.Creatures[0].SourcePath == alive.SourcePath);
            }
        });
    }
    private static CreatureReplayFrame Trap(int i) => new() { Key = "trap:" + i, Kind = "spore-trap", Resource = "spore-trap", SourcePath = "map/Roots/trap:" + i };
    private static CreatureReplayFrame Spider(int i) => new() { Key = "spider:" + i, Kind = "spider", Resource = "spider" };
    private static void Check(bool value) { if (!value) throw new Exception("Creature capacity or seek mismatch."); }
    private static void Reject(Action work) { try { work(); } catch (System.IO.InvalidDataException) { return; } throw new Exception("Creature capacity violation was accepted."); }
}
