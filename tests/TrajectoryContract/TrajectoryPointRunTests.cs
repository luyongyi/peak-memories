using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using PeakReplayLab;

internal static class TrajectoryPointRunTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("stationary codec preserves irregular times, gaps, moving centimeters and all events without mutating v1", () =>
        {
            var original = Fixture();
            var before = JObject.FromObject(original, Serializer());
            var wire = TrajectoryPointRunCodec.Encode(original);
            var encoded = JObject.FromObject(wire, Serializer());
            Check((string?)encoded["format"] == "trajectory-v2");
            Check(wire.Players[0].Points.Any(point => point.Length == 6));
            Check(JToken.DeepEquals(before, Canonical(encoded)));
            Check(JToken.DeepEquals(before, JObject.FromObject(original, Serializer())));
            Check(wire.Players[0].Events.Select(value => value.Kind).SequenceEqual(original.Players[0].Events.Select(value => value.Kind)));
        });
        test("stationary run golden wire matches exact non-100ms source timestamps", () =>
        {
            var points = new List<int[]> { new[] { 17, 1, 2, 3 }, new[] { 128, 1, 2, 3 }, new[] { 239, 1, 2, 3 },
                new[] { 350, 1, 2, 3 }, new[] { 453, 1, 2, 3 }, new[] { 553, 1, 2, 3 }, new[] { 675, 1, 2, 3 },
                new[] { 2500, 1, 2, 4 }, new[] { 2600, 1, 2, 4 }, new[] { 2700, 1, 2, 4 } };
            var result = TrajectoryPointRunCodec.EncodePoints(points);
            var expected = new[] { new[] { 17, 1, 2, 3, 111, 4 }, new[] { 453, 1, 2, 3 }, new[] { 553, 1, 2, 3 },
                new[] { 675, 1, 2, 3 }, new[] { 2500, 1, 2, 4, 100, 3 } };
            Check(result.Count == expected.Length && result.Zip(expected).All(pair => pair.First.SequenceEqual(pair.Second)));
        });
        test("moving points and a two-point pause remain original samples with no interpolation", () =>
        {
            var points = new List<int[]> { new[] { 0, 1, 2, 3 }, new[] { 100, 1, 2, 3 }, new[] { 200, 2, 2, 3 }, new[] { 2017, 2, 2, 4 } };
            Check(TrajectoryPointRunCodec.EncodePoints(points).Zip(points).All(pair => pair.First.SequenceEqual(pair.Second)));
            Check(TrajectoryPointRunCodec.EncodePoints(Array.Empty<int[]>()).Count == 0);
        });
        test("stationary codec cancellation is observed even within a very long run", () =>
        {
            var points = Enumerable.Range(0, 100_000).Select(index => new[] { index * 100, 1, 2, 3 }).ToArray();
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { TrajectoryPointRunCodec.EncodePoints(points, cancellation.Token); throw new Exception("Cancellation ignored."); }
            catch (OperationCanceledException) { }
        });
        test("long stationary sequences become one exact run with every original timestamp recoverable", () =>
        {
            var points = Enumerable.Range(0, 36_000).Select(index => new[] { 17 + index * 101, 125, 250, -300 }).ToList();
            var encoded = TrajectoryPointRunCodec.EncodePoints(points);
            Check(encoded.Count == 1 && encoded[0].SequenceEqual(new[] { 17, 125, 250, -300, 101, 36_000 }));
            for (int index = 0; index < points.Count; index++) Check(encoded[0][0] + index * encoded[0][4] == points[index][0]);
        });
    }

    internal static JObject Canonical(JObject wire)
    {
        var result = (JObject)wire.DeepClone();
        if ((string?)result["format"] == "trajectory-v1") return result;
        Check((string?)result["format"] == "trajectory-v2");
        result["format"] = "trajectory-v1";
        foreach (var player in (JArray)result["players"]!)
        {
            var points = new JArray();
            foreach (var token in (JArray)player["points"]!)
            {
                var entry = token.Values<int>().ToArray();
                Check(entry.Length == 4 || entry.Length == 6);
                int count = entry.Length == 6 ? entry[5] : 1, step = entry.Length == 6 ? entry[4] : 0;
                for (int index = 0; index < count; index++) points.Add(new JArray(entry[0] + index * step, entry[1], entry[2], entry[3]));
            }
            player["points"] = points;
        }
        return result;
    }

    internal static void WriteFixture(string directory)
    {
        Directory.CreateDirectory(directory);
        var original = Fixture();
        File.WriteAllText(Path.Combine(directory, "canonical-v1.json"), JObject.FromObject(original, Serializer()).ToString(Formatting.None));
        File.WriteAllText(Path.Combine(directory, "wire-v2.json"), JObject.FromObject(TrajectoryPointRunCodec.Encode(original), Serializer()).ToString(Formatting.None));
    }

    private static JsonSerializer Serializer() => JsonSerializer.Create(new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None,
        ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() }, Formatting = Formatting.None });
    private static void Check(bool condition) { if (!condition) throw new Exception("Stationary point codec contract failed."); }
    private static TrajectoryPackage Fixture() => new()
    {
        RecordingId = new string('a', 64), RunKey = new string('b', 64), TimeOriginMs = -17,
        StartedUtc = "2026-10-15T00:00:00Z", DurationMs = 5200,
        Map = new TrajectoryMap { BuildId = 25306743, Scene = "Level_0", Route = new[] { "Shore", "Roots" }, Stages = new[] {
            new TrajectoryStage { Index = 0, Name = "Shore", EnterZCm = 0, ExitZCm = 1000 },
            new TrajectoryStage { Index = 1, Name = "Roots", EnterZCm = 1000, ExitZCm = 2000 } } },
        Difficulty = new TrajectoryDifficulty { Ascent = 3, Custom = false, Mini = false },
        Players = new List<TrajectoryPlayer> { new() { Key = new string('c', 64), Name = "Synthetic stationary scout", Owner = true, Evidence = "native-state",
            Points = new[] { 17, 128, 239, 350, 453, 553, 675, 2500, 2611, 2722, 2833, 5000, 5100, 5200 }
                .Select(time => new[] { time, 10, 20, time < 2500 ? 0 : time < 5000 ? 900 : 2000 }).ToList(),
            Events = new List<TrajectoryEvent> {
                new() { TMs = 17, Kind = "join" }, new() { TMs = 17, Kind = "game-stage", StageIndex = 0 },
                new() { TMs = 453, Kind = "leave" }, new() { TMs = 553, Kind = "join" },
                new() { TMs = 675, Kind = "dead" }, new() { TMs = 675, Kind = "break" },
                new() { TMs = 2500, Kind = "revive" }, new() { TMs = 2611, Kind = "warp" },
                new() { TMs = 2833, Kind = "game-stage", StageIndex = 1 }, new() { TMs = 2833, Kind = "checkpoint", StageIndex = 0 },
                new() { TMs = 5200, Kind = "finish", StageIndex = 1 }, new() { TMs = 5200, Kind = "summit", StageIndex = 1 },
            } } },
    };
}
