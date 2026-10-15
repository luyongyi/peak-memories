using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

// Compare the optimized upload view with the independent full JObject codec.
// Skipped payload corruption tests below certify selective reading only, not
// suitability of that fixture for visual playback.
internal static class TrajectoryProjectionTests
{
    internal static void Run(Action<string, Action> test)
    {
        foreach (int schema in new[] { 10, 13, 14, 26, 27 })
        {
            int version = schema;
            test($"trajectory projection schema {version} matches full route fields and exported package through reorder removal rejoin and reset", () =>
            {
                var frames = Frames(version);
                var writer = new ReplayDeltaCodec.Writer(version);
                var route = new ReplayDeltaCodec.StreamReader(version, trajectoryOnly: true);
                var full = new ReplayDeltaCodec.Reader(version);
                var projected = new List<ReplayFrame>(); var expanded = new List<ReplayFrame>();
                var header = Header(version);
                var expected = new ReplayTrajectoryExporter.Builder(header, frames[^1].T);
                var actual = new ReplayTrajectoryExporter.Builder(header, frames[^1].T);
                for (int i = 0; i < frames.Length; i++)
                {
                    // Independent page baselines must discard an old actor set.
                    if (i == frames.Length - 1) writer = new ReplayDeltaCodec.Writer(version);
                    var record = JObject.FromObject(writer.Encode(frames[i]));
                    var a = full.Decode(record); var b = route.DecodeRecord(Json(record))!;
                    ReplayRules.ValidateTrajectoryStored(b, i == 0 ? -1 : frames[i - 1].T, 121);
                    Equal(Route(a), Route(b), "Route semantics differ at frame " + i);
                    Check(b.Actors.All(actor => actor.JointPose.Length == 0 && actor.Inventory.Length == 0), "Visual payload survived projection.");
                    expected.Observe(a); actual.Observe(b); expanded.Add(a); projected.Add(b);
                }
                Equal(JObject.FromObject(expected.Finish()), JObject.FromObject(actual.Finish()), "Export changed after projection.");
                // Later null/reset/rejoin changes cannot mutate earlier snapshots.
                for (int i = 0; i < frames.Length; i++) Equal(Route(expanded[i]), Route(projected[i]), "Historical snapshot mutated.");
                Check(projected[0].Actors[0].Appearance.EyeState == 0 && projected[1].Actors[0].Appearance.EyeState == 2, "Legacy death evidence was lost.");
                Check(projected[3].Actors.Length == 0 && projected[4].Actors.Single().Id == "a" && projected[^1].Actors.Single().Id == "c", "Removal/rejoin/reset semantics changed.");
            });
        }

        test("trajectory partial native route patches hold old fields and null clears evidence", () =>
        {
            var frames = Frames(27); var writer = new ReplayDeltaCodec.Writer(27);
            var route = new ReplayDeltaCodec.StreamReader(27, trajectoryOnly: true);
            var first = route.DecodeRecord(Json(writer.Encode(frames[0])))!;
            var token = JObject.FromObject(writer.Encode(frames[1]));
            var a = (JObject)((JArray)token["Actors"]!).Single(value => (string?)value["Id"] == "a");
            a["RouteState"] = new JObject { ["Alive"] = false, ["WarpSequence"] = 3 };
            var second = route.DecodeRecord(Json(token))!; var state = second.Actors.Single(value => value.Id == "a").RouteState!;
            Check(!state.Alive && state.WarpSequence == 3 && state.LocalOwner && state.SharedRunKey == first.Actors[0].RouteState!.SharedRunKey, "Partial route state replaced old evidence.");
            Check(ReferenceEquals(state.Center, first.Actors[0].RouteState!.Center) && first.Actors[0].RouteState!.Alive, "Partial patch mutated or recopied held coordinates.");
            var third = route.DecodeRecord(Json(writer.Encode(frames[2])))!;
            Check(third.Actors.Single(value => value.Id == "a").RouteState == null, "Explicit null retained native evidence.");
        });

        test("trajectory joint-only frames reuse the retained actor without decoding binary joints", () =>
        {
            var first = Frames(27)[0]; var second = Clone(first); second.T = .1;
            second.Actors[0].JointPose[0].Position[0] = 1; second.Actors[0].JointPose[0].SampleTime = .1;
            var writer = new ReplayDeltaCodec.Writer(27);
            var route = new ReplayDeltaCodec.StreamReader(27, trajectoryOnly: true);
            var baseline = route.DecodeRecord(Json(writer.Encode(first)))!;
            var record = JObject.FromObject(writer.Encode(second));
            Check(record["Actors"]![0]!["JointPose"] != null, "Fixture did not emit a joint delta.");
            record["Actors"]![0]!["JointPose"] = "not base64 or joint data";
            var held = route.DecodeRecord(Json(record))!;
            Check(ReferenceEquals(baseline.Actors[0], held.Actors[0]), "Joint-only update allocated a new route actor.");
            var full = new ReplayDeltaCodec.Reader(27); full.Decode(JObject.FromObject(new ReplayDeltaCodec.Writer(27).Encode(first)));
            Reject(() => full.Decode(record));
        });

        test("trajectory shared ActorOrder resolves only its order leaf and preserves explicit participant ordering", () =>
        {
            foreach (int schema in new[] { 26, 27 })
            {
                var actors = Enumerable.Range(0, 256).Select(index => Actor(schema, 0, "actor-" + index)).ToArray();
                var frame = new ReplayFrame { Actors = actors };
                var raw = new ReplayDeltaCodec.Writer(schema).Encode(frame); var shared = new FullReplaySharedWriter(schema);
                shared.EncodeBaseline(raw, frame); var record = JObject.FromObject(raw);
                Check(record["ActorOrder"]?["$Shared"] != null && shared.Blocks.Count == 1 && shared.Blocks[0].Kind == 9, "Fixture did not create native shared actor order.");
                // These references must never reach the shared resolver.
                record["Items"] = new JArray(new JObject { ["$Shared"] = 1001 });
                record["Creatures"] = new JArray(new JObject { ["$Shared"] = 1002 });
                record["StaticCreatures"] = new JObject { ["$Shared"] = 1003 };
                record["Effects"] = new JArray(new JObject { ["$Shared"] = 1004 });
                int calls = 0;
                var route = new ReplayDeltaCodec.StreamReader(schema, (type, id) =>
                {
                    Check(type == typeof(string[]) && id == 0, "Resolved an unrelated shared visual dependency.");
                    calls++; return actors.Select(value => value.Id).Reverse().ToArray();
                }, trajectoryOnly: true).DecodeRecord(Json(record))!;
                Check(calls == 1 && route.Actors.Select(value => value.Id).SequenceEqual(actors.Select(value => value.Id).Reverse()), "Shared participant ordering changed.");
                Check(route.Items.Length + route.Creatures.Length + route.Effects.Length == 0, "Skipped track was materialized.");
                Reject(() => new ReplayDeltaCodec.StreamReader(schema, (_, _) => new[] { "missing" }, trajectoryOnly: true).DecodeRecord(Json(record)));
            }
        });

        test("trajectory escaped names and Type-last fallback skip joint decoding and match canonical route output", () =>
        {
            var record = Baseline(); record["Actors"]![0]!["JointPose"] = "deliberately-invalid-joints";
            record["Creatures"] = new JArray(new JObject { ["$Shared"] = 9 });
            record["Effects"] = new JArray(new JObject { ["$Shared"] = 10 });
            var canonical = new ReplayDeltaCodec.StreamReader(27, (_, _) => throw new Exception("Unexpected shared read"), trajectoryOnly: true).DecodeRecord(Json(record))!;
            record.Property("Type")!.Remove(); record.Add("Type", "frame");
            string fallback = Json(record).Replace("\"Position\":", "\"Pos\\u0069tion\":", StringComparison.Ordinal)
                .Replace("\"EyeState\":", "\"EyeSt\\u0061te\":", StringComparison.Ordinal);
            var projector = new ReplayEffectProjection(trajectoryOnly: true);
            Check(!projector.TryProject(fallback.ToCharArray(), fallback.Length), "Fixture did not use JSON fallback.");
            var actual = new ReplayDeltaCodec.StreamReader(27, (_, _) => throw new Exception("Unexpected shared read"), trajectoryOnly: true).DecodeRecord(fallback)!;
            Equal(Route(canonical), Route(actual), "Noncanonical syntax changed projected route.");
            Check(actual.Actors.All(value => value.JointPose.Length == 0), "Fallback decoded bones.");
        });

        test("trajectory retained unknown fields and incomplete baselines remain rejected", () =>
        {
            foreach (string path in new[] { "UnknownFrame", "World.UnknownWorld", "Actors.0.UnknownActor", "Actors.0.RouteState.UnknownRoute", "Actors.0.Appearance.UnknownAppearance" })
            {
                var record = Baseline(); Set(record, path, new JValue(0)); Reject(() => Decode(record));
            }
            foreach (string path in new[] { "T", "ObjectsFull", "World.Segment", "Actors.0.Id", "Actors.0.Name", "Actors.0.Position", "Actors.0.Appearance.EyeState", "Actors.0.RouteState.Center" })
            {
                var record = Baseline(); Remove(record, path); Reject(() => Decode(record));
            }
        });

        test("trajectory duplicate retained fields reject in fast and escaped-name JSON paths", () =>
        {
            string original = Json(Baseline());
            foreach (var pair in new[] { ("\"T\":0.0", "\"T\":0.0,\"T\":0.0"),
                ("\"Segment\":0", "\"Segment\":0,\"Segment\":0"),
                ("\"Id\":\"a\"", "\"Id\":\"a\",\"Id\":\"a\""),
                ("\"EyeState\":0", "\"EyeState\":0,\"EyeSt\\u0061te\":0"),
                ("\"Alive\":true", "\"Alive\":true,\"Alive\":false") })
            {
                string changed = original.Replace(pair.Item1, pair.Item2, StringComparison.Ordinal);
                Check(changed != original, "Duplicate fixture did not change the input: " + pair.Item1);
                Reject(() => new ReplayDeltaCodec.StreamReader(27, trajectoryOnly: true).DecodeRecord(changed));
            }
        });

        test("trajectory invalid identity updates removals and permutations reject", () =>
        {
            var initial = Baseline();
            foreach (Action<JObject> mutate in new Action<JObject>[]
            {
                value => ((JArray)value["Actors"]!).Add(value["Actors"]![0]!.DeepClone()),
                value => value["ActorOrder"] = new JArray("a", "a"),
                value => value["ActorOrder"] = new JArray("b", "unknown"),
                value => value["ActorOrder"] = new JArray("a"),
                value => value["RemovedActors"] = new JArray("a"),
                value => value["Actors"]![0]!["Full"] = false,
            }) { var record = (JObject)initial.DeepClone(); mutate(record); Reject(() => Decode(record)); }
            foreach (var removed in new[] { new JArray("unknown"), new JArray("a", "a") })
            {
                var reader = new ReplayDeltaCodec.StreamReader(27, trajectoryOnly: true); reader.DecodeRecord(Json(initial));
                var delta = new JObject { ["Type"] = "frame", ["T"] = .1, ["ObjectsFull"] = false, ["World"] = new JObject(),
                    ["SceneSampleTimeKnown"] = false, ["SceneSampleTime"] = 0, ["RemovedActors"] = removed };
                Reject(() => reader.DecodeRecord(Json(delta)));
            }
        });

        test("trajectory buffered reader rejects nonmonotonic clocks invalid native evidence and malformed discarded JSON", () =>
        {
            foreach (Action<JObject> mutate in new Action<JObject>[]
            {
                value => value["T"] = -1,
                value => value["World"]!["Segment"] = 11,
                value => value["Actors"]![0]!["Position"] = new JArray(0, 1),
                value => value["Actors"]![0]!["Appearance"]!["EyeState"] = 3,
                value => value["Actors"]![0]!["RouteState"]!["SampleTime"] = 10,
                value => value["Actors"]![0]!["RouteState"]!["SharedRunKey"] = "not-a-hash",
                value => value["SceneSampleTime"] = .1,
            }) { var record = Baseline(); mutate(record); Reject(() => ReadRecords(new[] { Json(record) })); }
            var baseline = Baseline(); var duplicateTime = Baseline();
            Reject(() => ReadRecords(new[] { Json(baseline), Json(duplicateTime) }));
            foreach (string discarded in new[] { "[1,,2]", "undefined", "new Date(0)", "[{\"value\":new Date(0)}]" })
            {
                var withoutItems = (JObject)baseline.DeepClone(); withoutItems.Remove("Items");
                string badDiscarded = Json(withoutItems).Insert(1, "\"Items\":" + discarded + ",");
                Reject(() => ReadRecords(new[] { badDiscarded }));
            }
        });

        test("trajectory canonical and noncanonical end markers preserve footer rules", () =>
        {
            foreach (string footer in new[] { "{\"Type\":\"end\"}", "{\"opaque\":[{\"value\":1}],\"Type\":\"end\"}", "{\"Ty\\u0070e\":\"end\"}" })
            {
                var clip = ReadRecords(new[] { Json(Baseline()) }, footer); Check(clip.Complete && clip.Frames.Count == 1, "Footer was not accepted.");
            }
            Reject(() => ReadRecords(new[] { Json(Baseline()) }, "{\"Type\":\"end\",\"Type\":\"end\"}"));
            Reject(() => ReadRecords(new[] { Json(Baseline()) }, "{\"Type\":\"end\"}\n" + Json(Baseline())));
        });
    }

    private static ReplayFrame[] Frames(int schema)
    {
        ReplayFrame Frame(double t, params ActorFrame[] actors) => new() { T = t, World = new WorldFrame { Segment = t >= .2 ? 1 : 0 }, Actors = actors };
        var first = Frame(0, Actor(schema, 0, "a"), Actor(schema, 0, "b"));
        var second = Frame(.1, Actor(schema, .1, "b"), Actor(schema, .1, "a"));
        second.Actors[0].Appearance.EyeState = 2;
        if (second.Actors[0].RouteState != null) { second.Actors[0].RouteState!.Alive = false; second.Actors[1].RouteState!.WarpSequence = 1; }
        var third = Frame(.2, Actor(schema, .2, "a")); third.Actors[0].RouteState = null;
        var rejoin = Frame(.4, Actor(schema, .4, "a")); rejoin.Actors[0].Name = "returned";
        if (rejoin.Actors[0].RouteState != null) rejoin.Actors[0].RouteState!.Finished = true;
        return new[] { first, second, third, Frame(.3), rejoin, Frame(.5, Actor(schema, .5, "c")) };
    }
    private static ActorFrame Actor(int schema, double time, string id) => new()
    {
        Id = id, Name = "Projection " + id, Position = new[] { (float)time, 1f, (float)time * 10 },
        JointPose = new[] { new NodePose("0:Hip", new[] { (float)time, 0f, 0f }, new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f }, sampleTime: time) },
        RouteState = schema < 14 ? null : new ActorRouteState { SampleTime = time, Center = new[] { 1f, 2f, (float)time * 10 },
            Alive = true, LocalOwner = id == "a", RunTimeMs = (long)(time * 1000), SharedRunKey = new string('a', 64) },
    };
    private static ReplayHeader Header(int schema) => new() { Schema = schema, Scene = "Level_3", GameVersion = "projection-contract",
        GameAssembly = new string('a', 32), BuildId = 25667990, Route = "Shore,Roots,Alpine,Volcano,Volcano", StartedUtc = "2026-10-15T00:00:00Z" };
    private static JObject Baseline() => JObject.FromObject(new ReplayDeltaCodec.Writer(27).Encode(Frames(27)[0]));
    private static ReplayFrame Decode(JObject record) => new ReplayDeltaCodec.StreamReader(27, trajectoryOnly: true).DecodeRecord(Json(record))!;
    private static ReplayClip ReadRecords(IEnumerable<string> records, string footer = "{\"Type\":\"end\"}")
    {
        using var text = new StringReader(Json(Header(27)) + "\n" + string.Join("\n", records) + "\n" + footer + "\n");
        return ReplayFiles.ReadBuffered(text, new ReplayLineBufferPool(), default, effectsOnly: false,
            decodedByteLimit: ReplayRules.MaxBytes, maximumStoredTime: 121, trajectoryOnly: true);
    }
    private static JObject Route(ReplayFrame frame) => JObject.FromObject(new { frame.T, frame.World.Segment, frame.SceneSampleTimeKnown,
        frame.SceneSampleTime, Actors = frame.Actors.Select(actor => new { actor.Id, actor.Name, actor.Position, actor.Appearance.EyeState, actor.RouteState }) });
    private static string Json(object value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static T Clone<T>(T value) => JsonConvert.DeserializeObject<T>(Json(value!))!;
    private static void Equal(JToken expected, JToken actual, string message) => Check(JToken.DeepEquals(expected, actual), message);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentOutOfRangeException or FormatException) { return; }
        throw new Exception("Invalid retained recording data was accepted.");
    }
    private static (JContainer Parent, string Name) Parent(JObject root, string path)
    {
        var parts = path.Split('.'); JToken current = root;
        foreach (string part in parts[..^1]) current = current is JArray array ? array[int.Parse(part)] : current[part]!;
        return ((JContainer)current, parts[^1]);
    }
    private static void Set(JObject root, string path, JToken value) { var target = Parent(root, path); ((JObject)target.Parent)[target.Name] = value; }
    private static void Remove(JObject root, string path) { var target = Parent(root, path); ((JObject)target.Parent).Property(target.Name)!.Remove(); }
}
