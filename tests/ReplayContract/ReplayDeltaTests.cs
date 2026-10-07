using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class ReplayDeltaTests
{
    public static void Run(Action<string, Action> test)
    {
        test("current codec omits unchanged actor inventory appearance and item nodes", () =>
        {
            var clip = Sample(); var writer = new ReplayDeltaCodec.Writer();
            Token(writer.Encode(clip.Frames[0])); var delta = Token(writer.Encode(clip.Frames[1]));
            var actor = delta["Actors"]![0]!; var item = delta["Items"]![0]!;
            Check(actor["Name"] == null && actor["Appearance"] == null && actor["Inventory"] == null);
            Check(item["Prefab"] == null && item["Pose"]!["Nodes"] == null && item["Pose"]!["Position"] != null);
            Check(!delta["World"]!.HasValues);
        });
        test("current codec compares unshared legacy values without JSON comparison", () =>
        {
            var clip = Sample(); var writer = new ReplayDeltaCodec.Writer(); writer.Encode(clip.Frames[0]);
            var second = Copy(clip.Frames[1]); var delta = Token(writer.Encode(second));
            Check(delta["Actors"]![0]!["Inventory"] == null && delta["Items"]![0]!["Pose"]!["Nodes"] == null);
        });
        test("current codec lossless round trip shares unchanged nested data", () => InTemp(dir =>
        {
            var clip = Sample(); var read = ReplayFiles.Read(ReplayArchive.Save(dir, clip));
            EqualFrames(clip, read);
            Check(ReferenceEquals(read.Frames[0].Actors[0].Inventory, read.Frames[1].Actors[0].Inventory));
            Check(ReferenceEquals(read.Frames[0].Actors[0].Appearance, read.Frames[1].Actors[0].Appearance));
            Check(ReferenceEquals(read.Frames[0].Items[0].Pose.Nodes, read.Frames[1].Items[0].Pose.Nodes));
            Check(ReferenceEquals(read.Frames[0].World, read.Frames[1].World));
            Check(ReferenceEquals(read.Frames[0].Crates, read.Frames[1].Crates));
            Check(read.Frames[0].Items[0].Pose.Position[0] == 0 && read.Frames[1].Items[0].Pose.Position[0] == 1);
        }));
        test("current codec explicit empty arrays false zero and null clear previous values", () => InTemp(dir =>
        {
            var clip = Sample(); var second = clip.Frames[1];
            second.Actors[0].Inventory = Array.Empty<InventoryFrame>(); second.Actors[0].InventoryKnown = false;
            second.Items[0].Uses = 0; second.Items[0].Lit = false; second.Items[0].Pose.Nodes = Array.Empty<NodePose>();
            second.Crates = new[] { Copy(clip.Frames[0].Crates[0]) }; second.Crates[0].Animation = null;
            var read = ReplayFiles.Read(ReplayArchive.Save(dir, clip)); EqualFrames(clip, read);
            Check(read.Frames[1].Actors[0].Inventory.Length == 0 && read.Frames[1].Items[0].Uses == 0);
            Check(read.Frames[1].Items[0].Pose.Nodes.Length == 0 && read.Frames[1].Crates[0].Animation == null);
        }));
        test("current codec entity removal reappearance order and backward seek", () => InTemp(dir =>
        {
            var clip = Sample(); var original = Copy(clip.Frames[0]);
            clip.Frames[1].Actors = Array.Empty<ActorFrame>(); clip.Frames[1].Items = Array.Empty<ItemFrame>();
            clip.Frames[1].Crates = Array.Empty<CrateFrame>();
            original.T = .04; original.Actors[0].Name = "changed";
            var other = Copy(original.Actors[0]); other.Id = "second-test-actor";
            original.Actors = new[] { other, original.Actors[0] }; clip.Frames.Add(original);
            clip.Header.Duration = .04; clip.Header.FrameCount = 3;
            var read = ReplayFiles.Read(ReplayArchive.Save(dir, clip)); EqualFrames(clip, read);
            Check(read.At(.04).Left.Actors[0].Id == "second-test-actor");
            Check(read.At(0).Left.Actors[0].Name == "test"); Check(read.At(.02).Left.Actors.Length == 0);
        }));
        test("current codec actor-only reorder preserves exact array order", () => InTemp(dir =>
        {
            var clip = Sample(); var a = clip.Frames[0].Actors[0]; var b = Copy(a); b.Id = "second";
            clip.Frames[0].Actors = new[] { a, b }; clip.Frames[1].Actors = new[] { b, a };
            EqualFrames(clip, ReplayFiles.Read(ReplayArchive.Save(dir, clip)));
        }));
        test("current codec crate animation clears and restarts without mutating checkpoints", () => InTemp(dir =>
        {
            var clip = Sample(); var third = Copy(clip.Frames[1]); third.T = .04;
            clip.Frames[1].Crates[0].Animation = null;
            third.Crates[0].Animation!.AnchorTime = .04;
            clip.Frames.Add(third); clip.Header.Duration = .04; clip.Header.FrameCount = 3;
            var read = ReplayFiles.Read(ReplayArchive.Save(dir, clip)); EqualFrames(clip, read);
            Check(read.Frames[0].Crates[0].Animation!.AnchorTime == -.5);
            Check(read.Frames[1].Crates[0].Animation == null && read.Frames[2].Crates[0].Animation!.AnchorTime == .04);
        }));
        test("current codec explicit later baseline resets old entities and world", () =>
        {
            var tokens = Records(); var reader = new ReplayDeltaCodec.Reader(); reader.Decode(tokens[0]);
            var baseline = (JObject)tokens[0].DeepClone(); baseline["T"] = .04;
            baseline["Actors"] = new JArray(); baseline["ActorOrder"] = new JArray();
            baseline["Items"] = new JArray(); baseline["ItemOrder"] = new JArray();
            var restored = reader.Decode(baseline);
            Check(restored.Actors.Length == 0 && restored.Items.Length == 0 && restored.Crates.Length == 1);
        });
        test("current codec rejects missing first full baseline", () =>
        { var tokens = Records(); tokens[0]["ObjectsFull"] = false; Bad(() => new ReplayDeltaCodec.Reader().Decode(tokens[0])); });
        test("current codec rejects incomplete initial actor and nested pose baseline", () =>
        {
            var tokens = Records(); ((JObject)tokens[0]["Actors"]![0]!).Remove("Inventory");
            Bad(() => new ReplayDeltaCodec.Reader().Decode(tokens[0]));
            tokens = Records(); ((JObject)tokens[0]["Items"]![0]!["Pose"]!).Remove("Nodes");
            Bad(() => new ReplayDeltaCodec.Reader().Decode(tokens[0]));
        });
        test("current codec rejects unknown entity delta", () =>
        { var tokens = Records(); tokens[1]["Items"]![0]!["Key"] = "never-created"; BadSecond(tokens); });
        test("current codec rejects duplicate updates and update removal conflicts", () =>
        {
            var tokens = Records(); ((JArray)tokens[1]["Items"]!).Add(tokens[1]["Items"]![0]!.DeepClone()); BadSecond(tokens);
            tokens = Records(); ((JArray)tokens[1]["RemovedItems"]!).Add("item-one"); BadSecond(tokens);
        });
        test("current codec rejects duplicate unknown removals and invalid ordering", () =>
        {
            var tokens = Records(); ((JArray)tokens[1]["RemovedActors"]!).Add("missing"); BadSecond(tokens);
            tokens = Records(); tokens[1]["ActorOrder"] = new JArray("missing"); BadSecond(tokens);
            tokens = Records(); tokens[1]["RemovedCrates"] = new JArray("crate-one", "crate-one"); BadSecond(tokens);
        });
        test("current codec rejects null and type-coerced fields instead of filling defaults", () =>
        {
            var tokens = Records(); tokens[1]["Actors"]![0]!["Inventory"] = JValue.CreateNull(); BadSecond(tokens);
            tokens = Records(); tokens[1]["Items"]![0]!["Lit"] = "false"; BadSecond(tokens);
            tokens = Records(); tokens[1]["Items"]![0]!["Uses"] = .5; BadSecond(tokens);
            tokens = Records(); tokens[1]["Items"]![0]!["Full"] = 1; BadSecond(tokens);
        });
        test("current codec rejects unknown fields and polymorphic reference metadata", () =>
        {
            var tokens = Records(); tokens[1]["Items"]![0]!["$ref"] = "item-one"; BadSecond(tokens);
            tokens = Records(); tokens[1]["Mystery"] = 1; BadSecond(tokens);
        });
        test("current codec expanded new invalid values still receive full validation", () => InTemp(dir =>
        {
            var tokens = Records(); tokens[1]["Items"]![0]!["Pose"]!["Rotation"] = new JArray(0, 0, 0, 0);
            string file = Path.Combine(dir, "invalid.ndjson"); WriteFixture(file, Sample().Header, tokens);
            Bad(() => ReplayFiles.Read(file));
        }));
        test("current codec refuses legacy full-frame records masquerading as field deltas", () => InTemp(dir =>
        {
            var clip = Sample(); string file = Path.Combine(dir, "legacy.ndjson");
            WriteFixture(file, clip.Header, clip.Frames.Select(Token).ToArray()); Bad(() => ReplayFiles.Read(file));
        }));
        test("current codec still enforces expanded entity limits", () =>
        {
            var tokens = Records(); var reader = new ReplayDeltaCodec.Reader(); reader.Decode(tokens[0]);
            var updates = new JArray(); var template = (JObject)tokens[0]["Actors"]![0]!;
            for (int i = 0; i < ReplayRules.MaxActors; i++) { var value = (JObject)template.DeepClone(); value["Id"] = "added-" + i; updates.Add(value); }
            tokens[1]["Actors"] = updates; tokens[1].Remove("ActorOrder"); Bad(() => reader.Decode(tokens[1]));
        });
    }

    private static ReplayClip Sample()
    {
        var actor = new ActorFrame { Id = "test-actor", Name = "test", InventoryKnown = true,
            Inventory = new[] { new InventoryFrame { Empty = false, Slot = 0, ItemId = 1, Instance = "item-one", Equipped = true } } };
        var item = new ItemFrame { Key = "item-one", ItemId = 1, Prefab = "test-item", Name = "test-item", Uses = 3, Lit = true,
            Pose = new ObjectPose { Nodes = new[] { new NodePose { Path = "." }, new NodePose { Path = "0:mesh" } } } };
        var crate = new CrateFrame { Key = "crate-one", Kind = "Luggage", Animation = new CrateAnimationFrame
            { Clip = "Open", Anchored = true, AnchorTime = -.5, Duration = 1 } };
        var first = new ReplayFrame { T = 0, Actors = new[] { actor }, Items = new[] { item }, Crates = new[] { crate } };
        var second = Copy(first); second.T = .02; second.Actors[0].Position[0] = 1; second.Items[0].Pose.Position[0] = 1;
        var clip = new ReplayClip { Header = new ReplayHeader { Schema = ReplayRules.CurrentSchema, Scene = "Level_Test", Duration = .02, FrameCount = 2 } };
        clip.Frames.Add(first); clip.Frames.Add(second); return clip;
    }
    private static JObject[] Records()
    { var writer = new ReplayDeltaCodec.Writer(); return Sample().Frames.Select(f => Token(writer.Encode(f))).ToArray(); }
    private static void BadSecond(JObject[] tokens)
    { var reader = new ReplayDeltaCodec.Reader(); reader.Decode(tokens[0]); Bad(() => reader.Decode(tokens[1])); }
    private static JObject Token(object value) => JObject.FromObject(value, JsonSerializer.Create(ReplayFiles.Json));
    private static T Copy<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value, ReplayFiles.Json), ReplayFiles.Json)!;
    private static void Check(bool condition) { if (!condition) throw new Exception("Delta assertion failed."); }
    private static void Bad(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Expected invalid delta rejection."); }
    private static void WriteFixture(string file, ReplayHeader header, JObject[] frames)
    { File.WriteAllLines(file, new[] { JsonConvert.SerializeObject(header) }.Concat(frames.Select(f => f.ToString(Formatting.None))).Append("{\"Type\":\"end\"}")); }
    private static void InTemp(Action<string> action)
    {
        string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string directory = Path.GetFullPath(Path.Combine(tempRoot, "PeakReplayDeltaTests-" + Guid.NewGuid().ToString("N")));
        if (!directory.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe temporary test path.");
        Directory.CreateDirectory(directory);
        try { action(directory); } finally { Directory.Delete(directory, true); }
    }
    private static long ExpandedBytes(string path)
    {
        using var reader = ReplayArchive.OpenText(path); long total = 0; string? line;
        while ((line = reader.ReadLine()) != null) total += System.Text.Encoding.UTF8.GetByteCount(line) + 2;
        return total;
    }
    private sealed class IdentityComparer : IEqualityComparer<object>
    {
        public new bool Equals(object? a, object? b) => ReferenceEquals(a, b);
        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }
    private static void EqualFrames(ReplayClip expected, ReplayClip actual)
    {
        Check(expected.Frames.Count == actual.Frames.Count);
        var verified = new Dictionary<object, object>(new IdentityComparer());
        for (int i = 0; i < expected.Frames.Count; i++) Check(Equal(expected.Frames[i], actual.Frames[i], verified));
    }
    private static bool Equal(object? a, object? b, Dictionary<object, object> verified)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.GetType() != b.GetType()) return false;
        Type type = a.GetType();
        if (type.IsValueType || a is string) return a.Equals(b);
        if (verified.TryGetValue(a, out var old) && ReferenceEquals(old, b)) return true;
        if (a is Array aa && b is Array bb)
        {
            if (aa.Length != bb.Length) return false;
            for (int i = 0; i < aa.Length; i++) if (!Equal(aa.GetValue(i), bb.GetValue(i), verified)) return false;
        }
        else foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (property.CanRead && property.GetIndexParameters().Length == 0 && !Equal(property.GetValue(a), property.GetValue(b), verified)) return false;
        verified[a] = b; return true;
    }
}
