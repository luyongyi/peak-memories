using System.IO.Compression;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

int passed = 0;
void Test(string name, Action body)
{
    try { body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); Environment.Exit(1); }
}
void Check(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
void Reject(Action body)
{
    try { body(); } catch (Exception error) when (error is InvalidDataException || error is JsonException || error is ArgumentOutOfRangeException) { return; }
    throw new Exception("Invalid recording was accepted.");
}
void InRoot(Action<string> body)
{
    string root = Path.Combine(Path.GetTempPath(), "peak-trajectory-contract-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try { body(root); }
    finally { Directory.Delete(root, true); }
}
ReplayHeader Header(bool native = true) => new()
{
    Scene = "Level_3", GameVersion = "synthetic-test", GameAssembly = new string('a', 32), BuildId = 25667990,
    Route = "Shore,Roots,Alpine,Volcano,Volcano", StartedUtc = "2026-10-07T00:00:00Z", SampleHz = 60,
    RouteContext = native ? new ReplayRouteContext
    {
        RecordingId = ReplayRouteRules.Hash("synthetic-recording"), RunKey = ReplayRouteRules.Hash("synthetic-shared-run"),
        LevelIndex = 123, LayoutKey = ReplayRouteRules.Hash("synthetic-layout"), Ascent = 3, Custom = false, Mini = false,
        Stages = new[]
        {
            new ReplayRouteStage { Index = 0, Name = "Shore", EnterZCm = 0, ExitZCm = 1000 },
            new ReplayRouteStage { Index = 1, Name = "Roots", EnterZCm = 1000, ExitZCm = 2000 },
            new ReplayRouteStage { Index = 2, Name = "Alpine", EnterZCm = 2000, ExitZCm = 3000 },
            new ReplayRouteStage { Index = 3, Name = "Volcano", EnterZCm = 3000, ExitZCm = 4000 },
            new ReplayRouteStage { Index = 4, Name = "Kiln", EnterZCm = 4000, ExitZCm = 5000 },
        },
        Alignment = Alignment(),
    } : null,
};
ReplayMapAlignment Alignment() => new()
{
    Landmarks = new[]
    {
        new ReplayMapLandmark { Key = "segment-root:0", Kind = "segment-root", StageIndex = 0, Name = "Beach_Segment",
            PositionCm = new[] { -200, 0, 0 }, Rotation = new[] { 0f, 0f, 0f, 1f }, Scale = new[] { 1f, 1f, 1f } },
        new ReplayMapLandmark { Key = "segment-root:1", Kind = "segment-root", StageIndex = 1, Name = "Roots Segment",
            PositionCm = new[] { 1000, 300, 1000 }, Rotation = new[] { 0f, 0f, 0f, 1f }, Scale = new[] { 1f, 1f, 1f } },
        new ReplayMapLandmark { Key = "segment-root:2", Kind = "segment-root", StageIndex = 2, Name = "Alpine_Segment",
            PositionCm = new[] { -1000, 2000, 2000 }, Rotation = new[] { 0f, 0f, 0f, 1f }, Scale = new[] { 1f, 1f, 1f } },
        new ReplayMapLandmark { Key = "progress-point:0", Kind = "progress-point", StageIndex = 0, Name = "Beach_Campfire", PositionCm = new[] { 0, 100, 0 } },
        new ReplayMapLandmark { Key = "progress-point:1", Kind = "progress-point", StageIndex = 1, Name = "Roots_Campfire", PositionCm = new[] { 200, 1000, 1000 } },
        new ReplayMapLandmark { Key = "progress-point:2", Kind = "progress-point", StageIndex = 2, Name = "Alpine_Campfire", PositionCm = new[] { -300, 2000, 2000 } },
        new ReplayMapLandmark { Key = "progress-point:3", Kind = "progress-point", StageIndex = 3, Name = "Volcano_Campfire", PositionCm = new[] { 500, 3000, 3000 } },
        new ReplayMapLandmark { Key = "progress-point:4", Kind = "progress-point", StageIndex = 4, Name = "Kiln_Campfire", PositionCm = new[] { -500, 4000, 4000 } },
        new ReplayMapLandmark { Key = "progress-point:peak", Kind = "progress-point", Name = "Peak_Campfire", PositionCm = new[] { 0, 5000, 5000 } },
    },
};
ActorFrame Actor(double nativeTime, float z, string id = "private-photon-id-alpha", bool owner = true) => new()
{
    Id = id, Name = "Synthetic Scout", Position = new[] { 99f, 98f, z + 97f },
    RouteState = new ActorRouteState
    {
        SampleTime = nativeTime, Center = new[] { 1.25f, 2.5f, z }, Alive = true, LocalOwner = owner,
        RunTimeMs = (long)Math.Round((nativeTime - 200) * 1000) + 90_000,
        SharedRunKey = ReplayRouteRules.Hash("synthetic-shared-run"),
    },
};
ReplayFrame Frame(double nativeTime, params ActorFrame[] actors) => new() { T = nativeTime, Actors = actors };
ReplayFrame PhaseFrame(double frameTime, int segment, params ActorFrame[] actors)
    => new() { T = frameTime, World = new WorldFrame { Segment = segment }, Actors = actors };
FullReplayResult Save(string root, ReplayHeader header, IEnumerable<ReplayFrame> frames, bool interrupted = false)
{
    var writer = new FullReplayWriter(root, header, new ContinuousReplayOptions { QueueFrameLimit = 7201 });
    foreach (var frame in frames) Check(writer.TryEnqueue(frame));
    var task = interrupted ? writer.InterruptAsync("synthetic-stop", new IOException("synthetic interruption")) : writer.CompleteAsync();
    Check(task.Wait(TimeSpan.FromSeconds(20))); var result = task.GetAwaiter().GetResult();
    Check(result.Status == (interrupted ? "faulted" : "completed")); return result;
}
JObject ReadPackage(string path)
{
    using var file = File.OpenRead(path); using var gzip = new GZipStream(file, CompressionMode.Decompress);
    using var text = new StreamReader(gzip); using var json = new JsonTextReader(text);
    return JObject.Load(json);
}
ReplayFrame[] SyntheticFrames()
{
    return Enumerable.Range(0, 721).Select(i =>
    {
        double time = 200 + i / 60d;
        var first = Actor(time, (float)(i / 60d * 2));
        // Second participant starts in the middle of Roots, with the same
        // nickname. Their later crossing is not proof of a complete start.
        var second = Actor(time, (float)(15 + i / 60d), "private-photon-id-beta", false);
        return Frame(time, first, second);
    }).ToArray();
}

Test("schema 14 route snapshot deltas round-trip without mutating earlier states", () =>
{
    var writer = new ReplayDeltaCodec.Writer(); var reader = new ReplayDeltaCodec.Reader();
    var a = Frame(200, Actor(200, 1)); var b = Frame(200.1, Actor(200.1, 2));
    b.Actors[0].RouteState!.Alive = false; b.Actors[0].RouteState!.WarpSequence = 7;
    var first = reader.Decode(JObject.FromObject(writer.Encode(a)));
    var second = reader.Decode(JObject.FromObject(writer.Encode(b)));
    Check(first.Actors[0].RouteState!.Center[2] == 1 && first.Actors[0].RouteState!.Alive);
    Check(second.Actors[0].RouteState!.Center[2] == 2 && !second.Actors[0].RouteState!.Alive && second.Actors[0].RouteState!.WarpSequence == 7);
    var c = Frame(200.2, Actor(200.2, 3)); c.Actors[0].RouteState = null;
    Check(reader.Decode(JObject.FromObject(writer.Encode(c))).Actors[0].RouteState == null);
});
Test("schemas 10 through 13 preserve missing native evidence as unknown", () =>
{
    foreach (int schema in new[] { 10, 11, 12, 13 })
    {
        var frame = Frame(0, new ActorFrame { Id = "legacy", Name = "Synthetic" });
        var writer = new ReplayDeltaCodec.Writer(schema); var reader = new ReplayDeltaCodec.Reader(schema);
        var token = JObject.FromObject(writer.Encode(frame));
        Check(token["Actors"]![0]!["RouteState"] == null);
        Check(reader.Decode(token).Actors[0].RouteState == null);
        token["Actors"]![0]!["RouteState"] = JObject.FromObject(Actor(0, 0).RouteState!);
        Reject(() => new ReplayDeltaCodec.Reader(schema).Decode(token));
        Reject(() => new ReplayDeltaCodec.Writer(schema).Encode(Frame(0, Actor(0, 0))));
    }
});
Test("route states rebase even without joint poses and retain their shared center arrays", () =>
{
    var frame = Frame(200, Actor(200, 1)); var prior = frame.Actors[0].RouteState!;
    var rebaser = new ContinuousReplayRebaser(200); var a = rebaser.Apply(frame); var b = rebaser.Apply(Frame(200.1, frame.Actors));
    Check(a.Actors[0].RouteState!.SampleTime == 0 && prior.SampleTime == 200);
    Check(ReferenceEquals(a.Actors[0].RouteState!.Center, prior.Center));
    Check(ReferenceEquals(a.Actors[0].RouteState, b.Actors[0].RouteState));
});
Test("metadata validates malformed gates, identities, native timestamps and non-finite centers", () =>
{
    var header = Header(); header.RouteContext!.RecordingId = "raw-id"; Reject(() => ReplayRules.Validate(header));
    header = Header(); header.RouteContext!.Stages[1].ExitZCm = 500; Reject(() => ReplayRules.Validate(header));
    var frame = Frame(0, Actor(0, 0)); frame.Actors[0].RouteState!.SampleTime = 1; Reject(() => ReplayRules.Validate(frame, -1));
    frame = Frame(0, Actor(0, 0)); frame.Actors[0].RouteState!.Center[0] = float.NaN; Reject(() => ReplayRules.Validate(frame, -1));
});
Test("recording-start landmarks reject malformed identities, coordinates, transforms and oversized metadata", () =>
{
    var malformed = new Action<ReplayMapAlignment>[]
    {
        value => value.Version = 2,
        value => value.CoordinateSpace = "unity-world-meters",
        value => value.Landmarks = Array.Empty<ReplayMapLandmark>(),
        value => value.Landmarks = Enumerable.Repeat(value.Landmarks[0], 17).ToArray(),
        value => value.Landmarks[1].Key = value.Landmarks[0].Key,
        value => value.Landmarks[0].Key = "player:0",
        value => value.Landmarks[0].StageIndex = 7,
        value => { value.Landmarks[0].Key = "segment-root:6"; value.Landmarks[0].StageIndex = 6; },
        value => value.Landmarks[0].PositionCm = new[] { 1, 2 },
        value => value.Landmarks[0].PositionCm[0] = 100_000_001,
        value => value.Landmarks[0].Name = "map\nsecret",
        value => value.Landmarks[0].Rotation = new[] { 0f, 0f, 0f, 0f },
        value => value.Landmarks[0].Rotation![0] = float.NaN,
        value => value.Landmarks[0].Scale = null,
        value => value.Landmarks[0].Scale![0] = 0,
        value => value.Landmarks[3].Rotation = new[] { 0f, 0f, 0f, 1f },
        value => value.Landmarks[^1].StageIndex = 5,
        value => value.Landmarks[3].PositionCm[2] = 42,
    };
    foreach (var mutate in malformed)
    {
        var header = Header(); mutate(header.RouteContext!.Alignment!);
        Reject(() => ReplayRules.Validate(header));
    }
    // Root reflection is valid source evidence; source matching decides whether
    // the same signed scale is present, rather than inventing a positive scale.
    var reflected = Header(); reflected.RouteContext!.Alignment!.Landmarks[0].Scale![0] = -1;
    ReplayRules.Validate(reflected);
});
Test("present landmark metadata cannot invent missing fields from serializer defaults", () =>
{
    foreach (string property in new[] { "Version", "CoordinateSpace", "Landmarks" })
    {
        var json = JObject.FromObject(Header());
        ((JObject)json["RouteContext"]!["Alignment"]!).Remove(property);
        Reject(() => json.ToObject<ReplayHeader>());
    }
    foreach (string property in new[] { "Key", "Kind", "Name", "PositionCm" })
    {
        var json = JObject.FromObject(Header());
        ((JObject)json["RouteContext"]!["Alignment"]!["Landmarks"]![0]!).Remove(property);
        Reject(() => json.ToObject<ReplayHeader>());
    }
    var legacy = JObject.FromObject(Header()); ((JObject)legacy["RouteContext"]!).Remove("Alignment");
    Check(legacy.ToObject<ReplayHeader>()!.RouteContext!.Alignment == null);
});
Test("complete archives export an independent map-landmark whitelist without moving player points", () => InRoot(root =>
{
    var header = Header(); var source = Save(root, header, SyntheticFrames());
    var result = ReplayTrajectoryExporter.Export(source.FilePath, Path.Combine(root, "out"));
    Check(result.MapLandmarkCount == 9);
    var package = ReadPackage(result.Path);
    var alignment = (JObject)package["map"]!["alignment"]!;
    Check((int?)alignment["version"] == 1 && (string?)alignment["coordinateSpace"] == "unity-world-cm");
    var landmarks = (JArray)alignment["landmarks"]!;
    Check(landmarks.Count == 9 && landmarks.Select(value => (string?)value["key"]).Distinct().Count() == 9);
    Check(landmarks[0]!.Children<JProperty>().Select(value => value.Name).OrderBy(value => value).SequenceEqual(new[]
    { "key", "kind", "stageIndex", "name", "positionCm", "rotation", "scale" }.OrderBy(value => value)));
    Check(landmarks[3]!["rotation"] == null && landmarks[3]!["scale"] == null && landmarks[landmarks.Count - 1]!["stageIndex"] == null);
    Check(landmarks[0]!["positionCm"]![0]!.Value<int>() == -200);
    Check(package["players"]![0]!["points"]![0]![1]!.Value<int>() == 125 && package["players"]![0]!["points"]![0]![2]!.Value<int>() == 250);
    var builder = new ReplayTrajectoryExporter.Builder(header, .1);
    header.RouteContext!.Alignment!.Landmarks[0].PositionCm[0] = 999;
    header.RouteContext.Alignment.Landmarks[0].Rotation![3] = 0;
    header.RouteContext.Alignment.Landmarks[0].Scale![0] = 2;
    builder.Observe(Frame(0, Actor(0, 0))); builder.Observe(Frame(.1, Actor(.1, 1))); var built = builder.Finish();
    Check(built.Map.Alignment!.Landmarks[0].PositionCm[0] == -200 && built.Map.Alignment.Landmarks[0].Rotation![3] == 1 && built.Map.Alignment.Landmarks[0].Scale![0] == 1);
}));
Test("existing schema 14 complete recordings without landmarks retain absent alignment", () => InRoot(root =>
{
    var header = Header(); header.RouteContext!.Alignment = null;
    var source = Save(root, header, SyntheticFrames());
    var info = FullReplayArchive.ReadInfo(source.FilePath); Check(info.Header.Schema == 14 && info.Header.RouteContext!.Alignment == null);
    var result = ReplayTrajectoryExporter.Export(source.FilePath, Path.Combine(root, "out"));
    Check(result.MapLandmarkCount == 0);
    var package = ReadPackage(result.Path);
    Check(package["map"]!["alignment"] == null && (int?)package["map"]!["stages"]![1]!["enterZCm"] == 1000);
    Check(package["players"]![0]!["points"]![0]![1]!.Value<int>() == 125);
}));
Test("complete archive exports only whitelist fields, centimeter centers and unique 10 Hz points across pages", () => InRoot(root =>
{
    var source = Save(root, Header(), SyntheticFrames());
    var info = FullReplayArchive.ReadInfo(source.FilePath); Check(info.Pages.Length >= 2 && info.Pages.Skip(1).Any(p => p.Overlap));
    var result = ReplayTrajectoryExporter.Export(source.FilePath, Path.Combine(root, "export"));
    var package = ReadPackage(result.Path);
    Check((string?)package["format"] == "trajectory-v1" && (int?)package["sampleHz"] == 10 && (string?)package["coordinateUnit"] == "cm");
    Check((long?)package["timeOriginMs"] == 90000 && (int?)package["durationMs"] == 12000);
    Check(result.PlayerCount == 2 && result.PointCount == 242 && result.NativeEvidence && result.StageGatesKnown && result.PlayerNames.Length == 2);
    Check(package.Properties().Select(p => p.Name).OrderBy(s => s).SequenceEqual(new[]
    { "format", "recordingId", "runKey", "timeOriginMs", "startedUtc", "durationMs", "sampleHz", "coordinateUnit", "map", "difficulty", "players" }.OrderBy(s => s)));
    var players = (JArray)package["players"]!;
    Check(players[0]!["key"]!.Value<string>() != players[1]!["key"]!.Value<string>());
    foreach (var player in players)
    {
        Check(ReplayRouteRules.HashKey((string?)player["key"]));
        Check((string?)player["evidence"] == "native-state");
        var points = (JArray)player["points"]!;
        Check(points[0]![1]!.Value<int>() == 125 && points[0]![2]!.Value<int>() == 250);
        for (int i = 1; i < points.Count; i++) Check(points[i]![0]!.Value<int>() - points[i - 1]![0]!.Value<int>() >= 100);
    }
    string json = package.ToString();
    foreach (string forbidden in new[] { "private-photon-id", "Inventory", "Stamina", "JointPose", "Audio", "Appearance", "MapObjects", "GameAssembly" }) Check(!json.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    Check(result.CompressedBytes == new FileInfo(result.Path).Length && result.DecodedBytes < ReplayTrajectoryExporter.MaximumDecodedBytes);
    Check(new FileInfo(source.FilePath).Length == new FileInfo(info.Path).Length);
}));
Test("legacy complete source uploads with absent optional map keys and unknown difficulty/evidence", () => InRoot(root =>
{
    var header = Header(false);
    var frames = new[] { Frame(200, new ActorFrame { Id = "legacy", Name = "Old", Position = new[] { 0f, 0f, 0f } }),
        Frame(200.1, new ActorFrame { Id = "legacy", Name = "Old", Position = new[] { 1f, 0f, 0f } }) };
    var source = Save(root, header, frames); var result = ReplayTrajectoryExporter.Export(source.FilePath, Path.Combine(root, "out"));
    var package = ReadPackage(result.Path);
    Check(package["runKey"] == null && package["timeOriginMs"] == null && package["map"]!["layoutKey"] == null && package["map"]!["levelIndex"] == null);
    Check(package["map"]!["alignment"] == null);
    Check(package["difficulty"]!["ascent"]!.Type == JTokenType.Null && package["difficulty"]!["custom"]!.Type == JTokenType.Null && package["difficulty"]!["mini"]!.Type == JTokenType.Null);
    Check((string?)package["players"]![0]!["evidence"] == "legacy-unknown" && !result.NativeEvidence && !result.StageGatesKnown);
}));
Test("player-local crossings finish only that player, with no invented teleport or gap bridge", () =>
{
    var builder = new ReplayTrajectoryExporter.Builder(Header(), 8);
    builder.Observe(Frame(0, Actor(0, 0), Actor(0, 0, "second", false)));
    builder.Observe(Frame(.1, Actor(.1, 1), Actor(.1, 0, "second", false)));
    builder.Observe(Frame(1, Actor(1, 11), Actor(1, 0, "second", false)));
    var warped = Actor(1.1, 50); warped.RouteState!.WarpSequence = 1;
    builder.Observe(Frame(1.1, warped, Actor(1.1, 0, "second", false)));
    builder.Observe(Frame(4, Actor(4, 55), Actor(4, 0, "second", false)));
    var package = builder.Finish();
    Check(package.Players[0].Events.Count(e => e.Kind == "finish" && e.StageIndex == 0) == 1);
    Check(!package.Players[1].Events.Any(e => e.Kind == "finish"));
    Check(!package.Players[0].Events.Any(e => e.Kind == "finish" && e.StageIndex > 0));
    Check(package.Players[0].Events.Any(e => e.Kind == "warp") && package.Players[0].Events.Count(e => e.Kind == "break") >= 2);
});
Test("death, revive, leave and rejoin break route continuity and never qualify dead crossings", () =>
{
    var builder = new ReplayTrajectoryExporter.Builder(Header(), 2);
    builder.Observe(Frame(0, Actor(0, 0)));
    var dead = Actor(.1, 11); dead.RouteState!.Alive = false; builder.Observe(Frame(.1, dead));
    builder.Observe(Frame(.2, Actor(.2, 12)));
    builder.Observe(Frame(.3)); builder.Observe(Frame(.4, Actor(.4, 13)));
    var package = builder.Finish(); var events = package.Players[0].Events;
    Check(events.Any(e => e.Kind == "dead") && events.Any(e => e.Kind == "revive") && events.Any(e => e.Kind == "leave"));
    Check(events.Count(e => e.Kind == "join") == 2 && !events.Any(e => e.Kind == "finish"));
});
Test("route upload ends at exact death, omits ghosts, restarts at revive and keeps dead rejoin closed", () =>
{
    var builder = new ReplayTrajectoryExporter.Builder(Header(), .6);
    builder.Observe(Frame(0, Actor(0, 0)));
    var death = Actor(.05, 1); death.RouteState!.Alive = false; builder.Observe(Frame(.05, death));
    foreach (double time in new[] { .1, .2 })
    { var ghost = Actor(time, 999); ghost.RouteState!.Alive = false; builder.Observe(Frame(time, ghost)); }
    builder.Observe(Frame(.25, Actor(.25, 2)));
    builder.Observe(Frame(.35, Actor(.35, 3)));
    death = Actor(.4, 4); death.RouteState!.Alive = false; builder.Observe(Frame(.4, death));
    builder.Observe(Frame(.45));
    var returningGhost = Actor(.5, 777); returningGhost.RouteState!.Alive = false; builder.Observe(Frame(.5, returningGhost));
    var player = builder.Finish().Players.Single();
    Check(player.Points.Select(point => point[0]).SequenceEqual(new[] { 50, 250, 350, 400 }));
    Check(player.Points.Select(point => point[3]).SequenceEqual(new[] { 100, 200, 300, 400 }));
    Check(!player.Events.Any(value => value.Kind == "break" && value.TMs == 50));
    Check(player.Events.Where(value => value.TMs == 500).Select(value => value.Kind).SequenceEqual(new[] { "join", "dead", "break" }));
    Check(player.Events.Any(value => value.Kind == "revive" && value.TMs == 250));
});
Test("initial ghosts retain member metadata with no coordinates and legacy dead eyes also stop ghost points", () =>
{
    foreach (bool legacy in new[] { false, true })
    {
        var builder = new ReplayTrajectoryExporter.Builder(Header(!legacy), .3);
        foreach (double time in new[] { 0d, .1, .2, .3 })
        {
            var living = Actor(time, (float)time, "living");
            var ghost = Actor(time, 999, "initial-ghost");
            ghost.RouteState!.Alive = false; ghost.Appearance.EyeState = 2;
            if (legacy) { living.RouteState = ghost.RouteState = null; living.Position = new[] { 0f, 0f, (float)time }; }
            builder.Observe(Frame(time, living, ghost));
        }
        var package = builder.Finish();
        Check(package.Players.Count == 2 && package.Players[0].Points.Count == 4 && package.Players[1].Points.Count == 0);
        Check(package.Players[1].Events.Any(value => value.Kind == "dead"));
    }
});
Test("same-bucket deaths replace a living point, early revivals wait and unobserved deaths never add ghost positions", () =>
{
    var builder = new ReplayTrajectoryExporter.Builder(Header(), .6);
    builder.Observe(Frame(0, Actor(0, 0)));
    builder.Observe(Frame(.1, Actor(.1, 1)));
    var death = Actor(.15, 2); death.RouteState!.Alive = false; builder.Observe(Frame(.15, death));
    builder.Observe(Frame(.16, Actor(.16, 3)));
    builder.Observe(Frame(.25, Actor(.25, 4)));
    builder.Observe(Frame(.3));
    var ghost = Actor(.4, 999); ghost.RouteState!.Alive = false; builder.Observe(Frame(.4, ghost));
    builder.Observe(Frame(.5, ghost));
    var player = builder.Finish().Players.Single();
    Check(player.Points.Select(value => value[0]).SequenceEqual(new[] { 0, 150, 250 }));
    Check(player.Points.All(value => value[3] != 99900));
    Check(player.Points.Select(value => value[0] / 100).Distinct().Count() == player.Points.Count);
});
Test("missing native evidence and dead sticky wins cannot create an authoritative summit", () =>
{
    var builder = new ReplayTrajectoryExporter.Builder(Header(), .2);
    var unknown = Actor(0, 0, "mixed"); unknown.RouteState = null;
    var deadWin = Actor(0, 0, "sticky"); deadWin.RouteState!.Alive = false; deadWin.RouteState.Finished = true;
    builder.Observe(Frame(0, unknown, deadWin));
    foreach (double time in new[] { .1, .2 })
    {
        var mixed = Actor(time, 1, "mixed"); mixed.RouteState!.Finished = true;
        var sticky = Actor(time, 1, "sticky"); sticky.RouteState!.Finished = true;
        builder.Observe(Frame(time, mixed, sticky));
    }
    var players = builder.Finish().Players;
    Check(players[0].Evidence == "legacy-unknown");
    Check(players.All(value => !value.Events.Any(item => item.Kind == "summit")));
});
Test("only individual alive native summit wins emit summit, including first observed winners and excluding Nadir", () =>
{
    var builder = new ReplayTrajectoryExporter.Builder(Header(), .2);
    foreach (double time in new[] { 0d, .1, .2 })
    {
        var initial = Actor(time, 1, "initial-winner"); initial.RouteState!.Finished = true;
        var later = Actor(time, 1, "later-winner"); later.RouteState!.Finished = time > 0;
        var ghost = Actor(time, 1, "dead-winner"); ghost.RouteState!.Alive = false; ghost.RouteState.Finished = true;
        var nadir = Actor(time, 1, "nadir-winner"); nadir.RouteState!.Finished = nadir.RouteState.FinishedNadir = true;
        var crossing = Actor(time, time == 0 ? 49 : 51, "gate-only");
        builder.Observe(Frame(time, initial, later, ghost, nadir, crossing));
    }
    var players = builder.Finish().Players;
    Check(players.Take(2).All(value => value.Events.Count(item => item.Kind == "summit" && item.StageIndex == 4) == 1));
    Check(players.Skip(2).All(value => !value.Events.Any(item => item.Kind == "summit")));
    Check(players.Last().Events.Any(value => value.Kind == "finish" && value.StageIndex == 4));
});
Test("eighty members round-trip full and rolling recordings and all export with whole-member adaptive packets", () => InRoot(root =>
{
    var frames = Enumerable.Range(0, 3).Select(sample => Frame(sample / 10d,
        Enumerable.Range(0, 80).Select(index =>
        {
            var actor = Actor(sample / 10d, sample, "private-player-" + index, index == 0);
            actor.Name = "Synthetic member " + index;
            // Per-member clocks deliberately disagree. The all-member metadata
            // pass must supply the exact same final clock to every packet.
            actor.RouteState!.RunTimeMs = 1000 + index + sample * 100;
            return actor;
        }).ToArray())).ToArray();
    var header = Header(); header.Participants = frames[0].Actors.Select(value => value.Name).ToArray();
    var source = Save(root, header, frames);
    var info = FullReplayArchive.ReadInfo(source.FilePath);
    Check(info.ActorIds.Length == 80 && info.Header.Participants.Length == 80);
    Check(FullReplayArchive.ReadPage(source.FilePath, info, 0).Frames.All(value => value.Actors.Length == 80));
    using (var timeline = new PagedReplayTimeline(info.Header, info.Duration, info.FrameCount, info.ActorIds,
        info.Pages.Select(page => new ReplayPageRange(page.Start, page.End)).ToArray(),
        (index, cancellation) => FullReplayArchive.ReadPage(source.FilePath, info, index, cancellation)))
        Check(timeline.ActorIds.Length == 80);
    var rolling = new RollingBuffer();
    foreach (var frame in frames) rolling.Add(frame);
    var clip = rolling.Snapshot(Header(), DateTime.UtcNow);
    Check(clip.Header.Participants.Length == 80 && clip.Frames.All(value => value.Actors.Length == 80));
    var result = ReplayTrajectoryExporter.ExportWithSmallerLimits(source.FilePath, Path.Combine(root, "parts"),
        ReplayTrajectoryExporter.MaximumCompressedBytes, 6500);
    Check(result.Paths.Length > 1 && result.Path == result.Paths[0] && result.PlayerCount == 80 && result.PointCount == 240);
    var packages = result.Paths.Select(ReadPackage).ToArray();
    var players = packages.SelectMany(value => (JArray)value["players"]!).ToArray();
    Check(players.Length == 80 && players.Select(value => (string)value["key"]!).Distinct().Count() == 80);
    Check(players.All(value => ((JArray)value["points"]!).Count == 3));
    Check(packages.All(value => (string)value["recordingId"]! == result.RecordingId
        && (long)value["timeOriginMs"]! == 1079 && (int)value["durationMs"]! == 200));
    Check(packages.Select(value => value["runKey"]!.ToString()).Distinct().Count() == 1);
    Check(result.CompressedBytes == result.Paths.Sum(value => new FileInfo(value).Length));
    Check(result.DecodedBytes == packages.Sum(value => (long)System.Text.Encoding.UTF8.GetByteCount(value.ToString(Formatting.None))));
    Check(result.PlayerNames.Last() == "Synthetic member 79");
    string canceledOutput = Path.Combine(root, "canceled-parts");
    using var cancellation = new CancellationTokenSource();
    try
    {
        ReplayTrajectoryExporter.ExportWithSmallerLimits(source.FilePath, canceledOutput,
            ReplayTrajectoryExporter.MaximumCompressedBytes, 6500, cancellation.Token,
            progress => { if (progress > .45 && progress < 1) cancellation.Cancel(); });
        throw new Exception("Cancellation between successful member packets was ignored.");
    }
    catch (OperationCanceledException) { }
    Check(!Directory.EnumerateFiles(canceledOutput).Any());
}));
Test("no resampling creates points across native sample gaps or low source cadence", () =>
{
    var builder = new ReplayTrajectoryExporter.Builder(Header(), 10);
    foreach (double t in new[] { 0d, .25, .5, 5, 10 }) builder.Observe(Frame(t, Actor(t, (float)t)));
    var package = builder.Finish(); Check(package.Players[0].Points.Count == 5);
    Check(package.Players[0].Points.Select(p => p[0]).SequenceEqual(new[] { 0, 250, 500, 5000, 10000 }));
    Check(package.Players[0].Events.Count(e => e.Kind == "break") == 2);
});
Test("real archive phase change proves campfire 54m before the title plane using frame time rather than cached actor time", () => InRoot(root =>
{
    var header = Header();
    foreach (var stage in header.RouteContext!.Stages) { stage.EnterZCm *= 100; stage.ExitZCm *= 100; }
    foreach (var landmark in header.RouteContext.Alignment!.Landmarks.Where(value => value.Kind == "progress-point")) landmark.PositionCm[2] *= 100;
    var frames = new[]
    {
        PhaseFrame(200, 3, Actor(200, 3946)),
        PhaseFrame(200.1, 3, Actor(200.1, 3946.1f)),
        PhaseFrame(200.15, 4, Actor(200.1, 3946.1f)),
        PhaseFrame(200.2, 4, Actor(200.2, 3946.2f)),
    };
    var source = Save(root, header, frames); var result = ReplayTrajectoryExporter.Export(source.FilePath, Path.Combine(root, "export"));
    var package = ReadPackage(result.Path); var player = package["players"]![0]!;
    var events = player["events"]!.ToArray();
    Check(events.Count(value => (string?)value["kind"] == "game-stage") == 2);
    Check(events.Any(value => (string?)value["kind"] == "game-stage" && (int?)value["stageIndex"] == 3 && (int?)value["tMs"] == 0));
    Check(events.Any(value => (string?)value["kind"] == "game-stage" && (int?)value["stageIndex"] == 4 && (int?)value["tMs"] == 150));
    Check(events.Count(value => (string?)value["kind"] == "checkpoint" && (int?)value["stageIndex"] == 3 && (int?)value["tMs"] == 150) == 1);
    Check(!events.Any(value => (string?)value["kind"] == "finish"));
    Check((int?)package["map"]!["stages"]![4]!["enterZCm"] == 400000);
    var points = player["points"]!.ToArray();
    Check(points.Select(value => (int)value[0]!).SequenceEqual(new[] { 0, 100, 200 }));
    Check(points.All(value => (int)value[3]! < 400000) && (int?)package["sampleHz"] == 10);
}));
Test("same native phase emits one initial game-stage while old Z finish evidence remains available", () =>
{
    var builder = new ReplayTrajectoryExporter.Builder(Header(), .2);
    builder.Observe(PhaseFrame(0, 3, Actor(0, 39)));
    builder.Observe(PhaseFrame(.1, 3, Actor(.1, 39.5f)));
    builder.Observe(PhaseFrame(.2, 3, Actor(.2, 40.1f)));
    var events = builder.Finish().Players[0].Events;
    Check(events.Count(value => value.Kind == "game-stage") == 1 && events.First(value => value.Kind == "game-stage").StageIndex == 3);
    Check(!events.Any(value => value.Kind == "checkpoint"));
    Check(events.Any(value => value.Kind == "finish" && value.StageIndex == 3));
});
Test("initial midstage evidence does not invent a prior campfire completion", () =>
{
    var builder = new ReplayTrajectoryExporter.Builder(Header(), .1);
    builder.Observe(PhaseFrame(0, 3, Actor(0, 35)));
    builder.Observe(PhaseFrame(.1, 3, Actor(.1, 35.1f)));
    var events = builder.Finish().Players[0].Events;
    Check(events.Count(value => value.Kind == "game-stage") == 1 && events.Single(value => value.Kind == "game-stage").StageIndex == 3);
    Check(!events.Any(value => value.Kind == "checkpoint" || value.Kind == "finish"));
});
Test("global phase advances only the continuously alive player's own checkpoint, excluding dead, rejoined, late, warped and jumping actors", () =>
{
    ActorFrame Named(double time, string name, float z = 35) { var value = Actor(time, z, name, name == "valid"); value.Name = name; return value; }
    ActorFrame[] ActorsAt(double time, bool second, bool changing)
    {
        var values = new List<ActorFrame> { Named(time, "valid"), Named(time, "dead"), Named(time, "warp"), Named(time, "jump"), Named(time, "unknown"), Named(time, "revive"), Named(time, "ongoing-warp") };
        if (!second || changing) values.Add(Named(time, "rejoined"));
        if (changing) values.Add(Named(time, "late"));
        values.Single(value => value.Name == "dead").RouteState!.Alive = !changing;
        values.Single(value => value.Name == "revive").RouteState!.Alive = changing;
        values.Single(value => value.Name == "warp").RouteState!.WarpSequence = changing ? 1 : 0;
        values.Single(value => value.Name == "ongoing-warp").RouteState!.Warping = true;
        if (changing) values.Single(value => value.Name == "jump").RouteState!.Center[0] = 1000;
        if (!changing) values.Single(value => value.Name == "unknown").RouteState = null;
        return values.ToArray();
    }
    var builder = new ReplayTrajectoryExporter.Builder(Header(), .2);
    builder.Observe(PhaseFrame(0, 3, ActorsAt(0, false, false)));
    builder.Observe(PhaseFrame(.1, 3, ActorsAt(.1, true, false)));
    builder.Observe(PhaseFrame(.2, 4, ActorsAt(.2, true, true)));
    var package = builder.Finish();
    Check(package.Players.Single(value => value.Name == "valid").Events.Count(value => value.Kind == "checkpoint" && value.StageIndex == 3 && value.TMs == 200) == 1);
    Check(package.Players.Where(value => value.Name != "valid").All(value => !value.Events.Any(item => item.Kind == "checkpoint")));
    Check(package.Players.All(value => value.Events.Any(item => item.Kind == "game-stage" && item.StageIndex == 4 && item.TMs == 200)));
});
Test("frame gaps, native observation gaps and unknown prior phases cannot supply a campfire checkpoint", () =>
{
    foreach (int scenario in new[] { 0, 1, 2 })
    {
        var builder = new ReplayTrajectoryExporter.Builder(Header(), 2);
        builder.Observe(PhaseFrame(0, 3, Actor(0, 35)));
        if (scenario == 1) builder.Observe(PhaseFrame(.9, 3, Actor(0, 35)));
        if (scenario == 2) builder.Observe(PhaseFrame(.1, 8, Actor(.1, 35)));
        double time = scenario == 2 ? .2 : 1.1;
        builder.Observe(PhaseFrame(time, 4, Actor(time, 35.1f)));
        var events = builder.Finish().Players[0].Events;
        Check(events.Any(value => value.Kind == "game-stage" && value.StageIndex == 4));
        Check(!events.Any(value => value.Kind == "checkpoint"));
        if (scenario != 2) Check(events.Any(value => value.Kind == "break"));
    }
});
Test("a previous path interruption remains a break but does not erase a later real continuous campfire advancement", () =>
{
    foreach (bool dying in new[] { false, true })
    {
        var builder = new ReplayTrajectoryExporter.Builder(Header(), .3);
        builder.Observe(PhaseFrame(0, 3, Actor(0, 35)));
        var broken = Actor(.1, 35); broken.RouteState!.Alive = !dying; broken.RouteState.WarpSequence = dying ? 0 : 1;
        builder.Observe(PhaseFrame(.1, 3, broken));
        var recovered = Actor(.2, 35); recovered.RouteState!.WarpSequence = dying ? 0 : 1;
        builder.Observe(PhaseFrame(.2, 3, recovered));
        var next = Actor(.3, 35.1f); next.RouteState!.WarpSequence = dying ? 0 : 1;
        builder.Observe(PhaseFrame(.3, 4, next));
        var events = builder.Finish().Players[0].Events;
        Check(events.Any(value => value.Kind == "break"));
        Check(events.Count(value => value.Kind == "checkpoint" && value.StageIndex == 3 && value.TMs == 300) == 1);
    }
});
Test("Peak shares stage four and Void phase does not manufacture an ordinary completion or a missing route branch", () =>
{
    foreach (bool withVoid in new[] { true, false })
    {
        var header = Header();
        if (withVoid) header.RouteContext!.Stages = header.RouteContext.Stages.Concat(new[] { new ReplayRouteStage { Index = 5, Name = "Void" } }).ToArray();
        var builder = new ReplayTrajectoryExporter.Builder(header, .2);
        builder.Observe(PhaseFrame(0, 4, Actor(0, 45)));
        builder.Observe(PhaseFrame(.1, 5, Actor(.1, 45.1f)));
        builder.Observe(PhaseFrame(.2, 6, Actor(.2, 45.2f)));
        var events = builder.Finish().Players[0].Events;
        Check(events.Count(value => value.Kind == "game-stage" && value.StageIndex == 4) == 1);
        Check(events.Any(value => value.Kind == "game-stage" && value.StageIndex == 5 && value.TMs == 200) == withVoid);
        Check(!events.Any(value => value.Kind == "checkpoint" || value.Kind == "finish"));
    }
});
Test("nonadjacent and backwards phase changes are not completions and revisiting a checkpoint cannot duplicate it", () =>
{
    var builder = new ReplayTrajectoryExporter.Builder(Header(), .5);
    builder.Observe(PhaseFrame(0, 0, Actor(0, 1)));
    builder.Observe(PhaseFrame(.1, 3, Actor(.1, 1)));
    builder.Observe(PhaseFrame(.2, 4, Actor(.2, 1)));
    builder.Observe(PhaseFrame(.3, 3, Actor(.3, 1)));
    builder.Observe(PhaseFrame(.4, 4, Actor(.4, 1)));
    builder.Observe(PhaseFrame(.5, 2, Actor(.5, 1)));
    var events = builder.Finish().Players[0].Events;
    Check(events.Count(value => value.Kind == "checkpoint") == 1 && events.Single(value => value.Kind == "checkpoint").StageIndex == 3);
    Check(events.Count(value => value.Kind == "game-stage") == 6);
});
Test("recording-scoped fallback hashes keep equal names separate and repeated exports stable", () =>
{
    var h = Header(false);
    TrajectoryPackage Build()
    {
        var b = new ReplayTrajectoryExporter.Builder(h, .1);
        b.Observe(Frame(0, Actor(0, 0, "one"), Actor(0, 0, "two")));
        b.Observe(Frame(.1, Actor(.1, 1, "one"), Actor(.1, 1, "two"))); return b.Finish();
    }
    var first = Build(); var second = Build();
    Check(first.RecordingId == second.RecordingId && first.Players[0].Key == second.Players[0].Key && first.Players[0].Key != first.Players[1].Key);
    h.StartedUtc = "2026-10-07T01:00:00Z"; Check(Build().RecordingId != first.RecordingId);
});
Test("unfinished archives and 120-second or partial names reject without writing an upload", () => InRoot(root =>
{
    var frames = new[] { Frame(200, Actor(200, 0)), Frame(200.1, Actor(200.1, 1)) };
    var fault = Save(root, Header(), frames, true);
    string partial = Directory.GetFiles(root, "*.partial").Single(); string fakeFinal = Path.Combine(root, "fault.peakrun"); File.Copy(partial, fakeFinal);
    Reject(() => ReplayTrajectoryExporter.Export(partial, Path.Combine(root, "out")));
    Reject(() => ReplayTrajectoryExporter.Export(fakeFinal, Path.Combine(root, "out")));
    Reject(() => ReplayTrajectoryExporter.Export(Path.Combine(root, "memory.peakreplay"), Path.Combine(root, "out")));
    Check(!Directory.Exists(Path.Combine(root, "out")));
}));
Test("cancellation during sequential export leaves the source intact and no complete upload", () => InRoot(root =>
{
    var source = Save(root, Header(), SyntheticFrames()); long size = new FileInfo(source.FilePath).Length;
    using var cancellation = new CancellationTokenSource();
    try
    {
        ReplayTrajectoryExporter.Export(source.FilePath, Path.Combine(root, "out"), cancellation.Token,
            progress => { if (progress > .1) cancellation.Cancel(); });
        throw new Exception("Cancellation was ignored.");
    }
    catch (OperationCanceledException) { }
    Check(new FileInfo(source.FilePath).Length == size && !Directory.Exists(Path.Combine(root, "out")));
}));
Test("compressed and decoded limits abort the real package writer and remove only its temporary output", () => InRoot(root =>
{
    var source = Save(root, Header(), SyntheticFrames()); long size = new FileInfo(source.FilePath).Length;
    foreach (bool compressed in new[] { true, false })
    {
        string output = Path.Combine(root, compressed ? "gzip-limit" : "raw-limit"); Directory.CreateDirectory(output);
        string sentinel = Path.Combine(output, "keep.txt"); File.WriteAllText(sentinel, "keep");
        Reject(() => ReplayTrajectoryExporter.ExportWithSmallerLimits(source.FilePath, output,
            compressed ? 30 : ReplayTrajectoryExporter.MaximumCompressedBytes,
            compressed ? ReplayTrajectoryExporter.MaximumDecodedBytes : 100));
        Check(Directory.GetFiles(output).SequenceEqual(new[] { sentinel }) && File.ReadAllText(sentinel) == "keep");
    }
    Check(new FileInfo(source.FilePath).Length == size);
}));
Test("late real RunId after empty or stale startup identity preserves early points and uses reset shared clock", () =>
{
    foreach (bool stale in new[] { true, false })
    {
        var header = Header(); header.RouteContext!.RunKey = stale ? ReplayRouteRules.Hash("old-run") : null;
        var builder = new ReplayTrajectoryExporter.Builder(header, 3);
        foreach (double time in new[] { 0d, .1, 1, 2, 2.1, 3 })
        {
            var actor = Actor(time, (float)time);
            actor.RouteState!.SharedRunKey = time < 2 ? stale ? ReplayRouteRules.Hash("old-run") : "" : ReplayRouteRules.Hash("new-run");
            actor.RouteState.RunTimeMs = time < 2 ? (long)(time * 1000) : (long)((time - 2) * 1000);
            builder.Observe(Frame(time, actor));
        }
        var package = builder.Finish();
        Check(package.RunKey == ReplayRouteRules.Hash("new-run") && package.TimeOriginMs == -2000);
        Check(package.DurationMs == 3000 && package.Players[0].Points[0][0] == 0 && package.Players[0].Points.Count == 6);
        var later = new ReplayTrajectoryExporter.Builder(Header(), 1);
        foreach (double time in new[] { 0d, .1, 1 })
        {
            var actor = Actor(time, (float)time + 3); actor.RouteState!.SharedRunKey = ReplayRouteRules.Hash("new-run");
            actor.RouteState.RunTimeMs = 1000 + (long)(time * 1000); later.Observe(Frame(time, actor));
        }
        var second = later.Finish();
        Check(second.TimeOriginMs == 1000 && package.Players[0].Key == second.Players[0].Key);
    }
});
Test("same RunId arriving before timer reset uses the later reset clock without discarding early points", () =>
{
    var builder = new ReplayTrajectoryExporter.Builder(Header(), 3);
    foreach (double time in new[] { 0d, .1, 1, 2, 2.1, 3 })
    {
        var actor = Actor(time, (float)time);
        actor.RouteState!.RunTimeMs = time < 2 ? 50_000 + (long)(time * 1000) : (long)((time - 2) * 1000);
        builder.Observe(Frame(time, actor));
    }
    var package = builder.Finish();
    Check(package.RunKey == ReplayRouteRules.Hash("synthetic-shared-run") && package.TimeOriginMs == -2000);
    Check(package.Players[0].Points.Count == 6 && package.Players[0].Points[0][0] == 0);
});
Test("unknown shared clock keeps identity recording-scoped instead of inventing a time origin", () =>
{
    var header = Header(); var builder = new ReplayTrajectoryExporter.Builder(header, .1);
    foreach (double time in new[] { 0d, .1 })
    {
        var actor = Actor(time, (float)time); actor.RouteState!.RunTimeMs = -1; builder.Observe(Frame(time, actor));
    }
    var package = builder.Finish(); Check(package.RunKey == null && package.TimeOriginMs == null);
    Check(package.Players[0].Key == ReplayRouteRules.Hash("peak-memories/player/v1/" + package.RecordingId + "/private-photon-id-alpha"));
});
Test("writer snapshots recording context before external mutation", () => InRoot(root =>
{
    var header = Header(); header.CoverOutcome = new ReplayRegionOutcome(200, false);
    var writer = new FullReplayWriter(root, header);
    header.RouteContext!.Ascent = 8; header.RouteContext.Stages[0].ExitZCm = 99999;
    header.RouteContext.Alignment!.Landmarks[0].PositionCm[0] = 999;
    header.RouteContext.Alignment.Landmarks[0].Rotation![3] = 0;
    header.RouteContext.Alignment.Landmarks[0].Scale![0] = 2;
    Check(writer.TryEnqueue(Frame(200, Actor(200, 0))) && writer.TryEnqueue(Frame(200.1, Actor(200.1, 1))));
    var task = writer.CompleteAsync(); Check(task.Wait(TimeSpan.FromSeconds(10)));
    var info = FullReplayArchive.ReadInfo(task.Result.FilePath);
    Check(info.Header.RouteContext!.Ascent == 3 && info.Header.RouteContext.Stages[0].ExitZCm == 1000);
    Check(info.Header.RouteContext.Alignment!.Landmarks[0].PositionCm[0] == -200 && info.Header.RouteContext.Alignment.Landmarks[0].Rotation![3] == 1 && info.Header.RouteContext.Alignment.Landmarks[0].Scale![0] == 1);
}));

int fixtureIndex = Array.IndexOf(args, "--fixture-dir");
if (fixtureIndex >= 0)
{
    if (fixtureIndex + 1 >= args.Length) throw new ArgumentException("--fixture-dir needs a directory.");
    string root = Path.GetFullPath(args[fixtureIndex + 1]); Directory.CreateDirectory(root);
    var source = Save(root, Header(), SyntheticFrames()); var result = ReplayTrajectoryExporter.Export(source.FilePath, root);
    File.WriteAllText(Path.Combine(root, "fixture.json"), JsonConvert.SerializeObject(new
    {
        source = source.FilePath, package = result.Path, result.CompressedBytes, result.DecodedBytes,
        result.PointCount, result.PlayerCount, result.DurationMs, synthetic = true,
    }, Formatting.Indented));
    Console.WriteLine("FIXTURE " + result.Path);
}
int teamFixtureIndex = Array.IndexOf(args, "--team-fixture-dir");
if (teamFixtureIndex >= 0)
{
    if (teamFixtureIndex + 1 >= args.Length) throw new ArgumentException("--team-fixture-dir needs a directory.");
    string root = Path.GetFullPath(args[teamFixtureIndex + 1]); Directory.CreateDirectory(root);
    var frames = new[] { 0d, .1, .15, .16, .25, .3 }.Select(time => Frame(time,
        Enumerable.Range(0, 80).Where(index => index != 4 || time != .1).Select(index =>
        {
            var actor = Actor(time, (float)time, "synthetic-private-member-" + index, index == 0);
            actor.Name = "Synthetic member " + index;
            actor.RouteState!.RunTimeMs = 1000 + (long)Math.Round(time * 1000);
            actor.RouteState.Alive = index switch { 1 or 4 => time < .15, 2 => false, 3 => time != .15, 5 => time != .15, _ => true };
            actor.RouteState.Finished = index == 0 && time >= .25 || index == 5 && time >= .15 || index == 6 && time >= .25;
            if (index == 6 && time == 0) actor.RouteState = null;
            if (actor.RouteState?.Alive == false) actor.RouteState.Center[2] = time == .15 ? .15f : 999;
            return actor;
        }).ToArray())).ToArray();
    var header = Header(); header.Participants = frames[0].Actors.Select(value => value.Name).ToArray();
    var source = Save(root, header, frames);
    var result = ReplayTrajectoryExporter.ExportWithSmallerLimits(source.FilePath, root,
        ReplayTrajectoryExporter.MaximumCompressedBytes, 6500);
    File.WriteAllText(Path.Combine(root, "team-fixture.json"), JsonConvert.SerializeObject(new
    { source = source.FilePath, packages = result.Paths, result.PointCount, result.PlayerCount, synthetic = true }, Formatting.Indented));
    Console.WriteLine("TEAM FIXTURE " + Path.Combine(root, "team-fixture.json"));
}
Console.WriteLine($"TOTAL: {passed} trajectory checks passed. No real recording, identity, Unity runtime or HTTP endpoint is used.");
