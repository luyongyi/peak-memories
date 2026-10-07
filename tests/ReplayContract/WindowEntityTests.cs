using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class WindowEntityTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Window entity regression."); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception e) when (e is InvalidDataException || e is ArgumentOutOfRangeException) { return; } throw new Exception("Invalid entity data accepted."); }
    private static JObject Token(object value) => JObject.FromObject(value, JsonSerializer.Create(ReplayFiles.Json));
    private static RopeReplayFrame Rope(float x = 0) => new()
    {
        Key = "rope", Kind = "rope", Resource = "native-rope", CollisionKind = "capsule", Visible = true,
        Points = new[] { new RopeReplayPoint { Position = new[] { x, 0f, 0f }, Radius = .1f, Height = 1 }, new RopeReplayPoint() },
        Bones = new[] { new RopeReplayPoint(), new RopeReplayPoint() },
    };
    private static SpawnedReplayFrame Cannon() => new()
    {
        Key = "cannon", Kind = "scout-cannon", Resource = "scoutcannon_placed",
        Animation = new CrateAnimationFrame { Clip = "CannonFire", Anchored = true, AnchorTime = 1, Duration = .333f },
    };
    public static void Run(Action<string, Action> test)
    {
        test("120s window retains pre-window cannon balloon rope and loot without birth events", () =>
        {
            var cannon = new[] { Cannon() };
            var balloons = new[] { new BalloonReplayFrame { Key = "balloon", OwnerId = "player", ColorIndex = 2, HeadOffset = .5f, Started = 1, Active = true } };
            var rope = new[] { Rope() };
            var loot = new[] { new ItemFrame { Key = "loot", Prefab = "test", Name = "test" } };
            var buffer = new RollingBuffer();
            for (int t = 0; t <= 300; t++) buffer.Add(new ReplayFrame
            {
                T = t, Items = loot, Ropes = rope,
                Spawned = t < 300 ? cannon : Array.Empty<SpawnedReplayFrame>(),
                Balloons = t < 299 ? balloons : Array.Empty<BalloonReplayFrame>(),
            });
            var clip = buffer.Snapshot(new ReplayHeader { Scene = "Test" }, DateTime.UtcNow);
            Check(clip.Duration == 120 && clip.Frames[0].T == 0 && clip.Frames[0].Events.Length == 0);
            Check(clip.Frames[0].Spawned.Length == 1 && clip.Frames[0].Balloons.Length == 1 && clip.Frames[0].Ropes.Length == 1 && clip.Frames[0].Items.Length == 1);
            Check(clip.Frames[0].Balloons[0].Started == -179 && clip.Frames[0].Spawned[0].Animation!.AnchorTime == -179);
            Check(cannon[0].Animation!.AnchorTime == 1 && balloons[0].Started == 1);
            var writer = new ReplayDeltaCodec.Writer(ReplayRules.CurrentSchema); var reader = new ReplayDeltaCodec.Reader(ReplayRules.CurrentSchema);
            var decoded = new ReplayClip { Header = clip.Header };
            foreach (var frame in clip.Frames) decoded.Frames.Add(reader.Decode(Token(writer.Encode(frame))));
            Check(decoded.At(120).Left.Spawned.Length == 0);
            Check(decoded.At(119).Left.Balloons.Length == 0 && decoded.At(119).Left.Spawned.Length == 1);
            Check(decoded.At(0).Left.Balloons.Length == 1 && decoded.At(0).Left.Spawned.Length == 1);
            Check(decoded.At(118).Left.Balloons.Length == 1);
            Check(CrateAnimationTimeline.Sample(decoded.Frames[0].Spawned[0].Animation!, 0, .333, false) == (double).333f);
        });
        test("budget eviction retains live attachments without their birth record", () =>
        {
            var values = new[] { new BalloonReplayFrame { Key = "b", OwnerId = "p", Started = 0, Active = true } };
            var buffer = new RollingBuffer(4096);
            for (int i = 0; i < 100; i++) buffer.Add(new ReplayFrame { T = i / 60d, Balloons = values });
            Check(buffer.MemoryLimited && buffer.Count >= 2 && buffer.Count < 100);
            var clip = buffer.Snapshot(new ReplayHeader { Scene = "Test" }, DateTime.UtcNow);
            Check(clip.Frames[0].Balloons.Length == 1 && clip.Frames[0].Balloons[0].Started < 0);
        });
        test("current format ropes patch only changed node fields and preserve previous frames", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(ReplayRules.CurrentSchema); var reader = new ReplayDeltaCodec.Reader(ReplayRules.CurrentSchema);
            var first = reader.Decode(Token(writer.Encode(new ReplayFrame { Ropes = new[] { Rope() } })));
            var patch = Token(writer.Encode(new ReplayFrame { T = .05, Ropes = new[] { Rope(1) } }));
            var changes = (JArray)patch["Ropes"]![0]!["Points"]!["Changes"]!;
            Check(changes.Count == 1 && changes[0]![0]!.Value<int>() == 0 && changes[0]![1]!.Value<int>() == 1 && ((JArray)changes[0]!).Count == 3);
            Check(patch["Ropes"]![0]!["Bones"] == null);
            var second = reader.Decode(patch);
            Check(second.Ropes[0].Points[0].Position[0] == 1 && first.Ropes[0].Points[0].Position[0] == 0);
            Check(ReferenceEquals(second.Ropes[0].Points[1], first.Ropes[0].Points[1]));
            Check(ReferenceEquals(second.Ropes[0].Bones, first.Ropes[0].Bones));
        });
        test("current format rope topology changes use a new complete point array", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(ReplayRules.CurrentSchema); var reader = new ReplayDeltaCodec.Reader(ReplayRules.CurrentSchema);
            reader.Decode(Token(writer.Encode(new ReplayFrame { Ropes = new[] { Rope() } })));
            var smaller = Rope(); smaller.Points = new[] { new RopeReplayPoint() };
            var patch = Token(writer.Encode(new ReplayFrame { T = .05, Ropes = new[] { smaller } }));
            Check(patch["Ropes"]![0]!["Points"] is JArray);
            Check(reader.Decode(patch).Ropes[0].Points.Length == 1);
        });
        test("current format rope tuples retain every component and dimensional field losslessly", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(ReplayRules.CurrentSchema); var reader = new ReplayDeltaCodec.Reader(ReplayRules.CurrentSchema);
            reader.Decode(Token(writer.Encode(new ReplayFrame { Ropes = new[] { Rope() } })));
            var changed = Rope(4);
            changed.Points[0].Rotation = new[] { 0f, 1f, 0f, 0f };
            changed.Points[0].Scale = new[] { 2f, 3f, 4f }; changed.Points[0].Radius = .2f; changed.Points[0].Height = 2;
            changed.Bones[1].Position[2] = -2;
            var frame = new ReplayFrame { T = .1, Ropes = new[] { changed } };
            var patch = Token(writer.Encode(frame));
            Check(patch["Ropes"]![0]!["Points"]!["Changes"]![0]![1]!.Value<int>() == 31);
            Check(JToken.DeepEquals(Token(frame), Token(reader.Decode(patch))));
        });
        test("current format requires the complete balloon baseline even when empty", () =>
        {
            var token = Token(new ReplayDeltaCodec.Writer(ReplayRules.CurrentSchema).Encode(new ReplayFrame()));
            token.Remove("Balloons"); Reject(() => new ReplayDeltaCodec.Reader(ReplayRules.CurrentSchema).Decode(token));
        });
        test("current format malformed rope point deltas rejected", () =>
        {
            foreach (string fault in new[] { "index", "duplicate", "length", "unknown", "missing", "baseline" })
            {
                var writer = new ReplayDeltaCodec.Writer(ReplayRules.CurrentSchema); var reader = new ReplayDeltaCodec.Reader(ReplayRules.CurrentSchema);
                var baseline = Token(writer.Encode(new ReplayFrame { Ropes = new[] { Rope() } }));
                reader.Decode(baseline);
                var patch = Token(writer.Encode(new ReplayFrame { T = .05, Ropes = new[] { Rope(1) } }));
                var points = (JObject)patch["Ropes"]![0]!["Points"]!; var changes = (JArray)points["Changes"]!;
                if (fault == "index") changes[0]![0] = -1;
                if (fault == "duplicate") changes.Add(changes[0]!.DeepClone());
                if (fault == "length") points["Length"] = 123;
                if (fault == "unknown") changes[0]![1] = 32;
                if (fault == "missing") ((JArray)changes[0]!).RemoveAt(0);
                if (fault == "baseline") { baseline["Ropes"]![0]!["Points"] = points; Reject(() => new ReplayDeltaCodec.Reader(ReplayRules.CurrentSchema).Decode(baseline)); }
                else Reject(() => reader.Decode(patch));
            }
        });
        test("current format cannot downgrade cannon or balloon data", () =>
        {
            Reject(() => new ReplayDeltaCodec.Writer(7).Encode(new ReplayFrame { Spawned = new[] { Cannon() } }));
            Reject(() => new ReplayDeltaCodec.Writer(7).Encode(new ReplayFrame { Balloons = new[] { new BalloonReplayFrame { Key = "b", OwnerId = "p" } } }));
        });
        test("previous spawned format is rejected", () =>
        {
            Reject(() => new ReplayDeltaCodec.Reader(7));
        });
        test("current format file round trip preserves window baseline and removes only at recorded boundary", () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "PeakWindowTest-" + Guid.NewGuid().ToString("N")); string? file = null;
            try
            {
                var b = new RollingBuffer(); var spawn = new[] { Cannon() };
                var balloons = new[] { new BalloonReplayFrame { Key = "b", OwnerId = "p", Started = 10, Active = true } };
                b.Add(new ReplayFrame { T = 200, Spawned = spawn, Balloons = balloons });
                b.Add(new ReplayFrame { T = 200.1, Spawned = spawn });
                file = ReplayArchive.Save(directory, b.Snapshot(new ReplayHeader { Schema = ReplayRules.CurrentSchema, Scene = "Test" }, DateTime.UtcNow));
                var decoded = ReplayFiles.Read(file);
                Check(decoded.Complete && decoded.Header.Schema == ReplayRules.CurrentSchema && decoded.Frames[0].Balloons[0].Started == -190);
                Check(decoded.Frames[1].Balloons.Length == 0 && decoded.Frames[0].Spawned[0].Animation!.AnchorTime == -199);
            }
            finally { if (file != null) File.Delete(file); if (Directory.Exists(directory)) Directory.Delete(directory); }
        });
        test("rope observed-value constructor does not allocate discarded default arrays", () =>
        {
            var p = new float[3]; var q = new[] { 0f, 0f, 0f, 1f }; var s = new[] { 1f, 1f, 1f };
            var point = new RopeReplayPoint(p, q, s);
            Check(ReferenceEquals(p, point.Position) && ReferenceEquals(q, point.Rotation) && ReferenceEquals(s, point.Scale));
            Check(new RopeReplayPoint().Rotation[3] == 1);
        });
    }
}
