using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

public static class ObjectTrackTests
{
    private static ReplayHeader Header() => new() { Schema = 10, Scene = "Level_Synthetic", GameVersion = "test" };
    private static ItemFrame Item(string key = "item-1") => new() { Key = key, ItemId = 4, Name = "Test mushroom", Prefab = "Mushroom", Pose = new ObjectPose { Nodes = new[] { new NodePose { Path = "." } } } };
    private static CrateFrame Crate() => new() { Key = "crate-1", Kind = "Luggage", Animation = new CrateAnimationFrame { Clip = "Open", Time = .4f } };
    private static ReplayFrame Frame(double t) => new() { T = t };
    private static string Json(object value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static void Check(bool value) { if (!value) throw new Exception("Object track assertion failed."); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is InvalidDataException || e is JsonException || e is ArgumentOutOfRangeException) { return; }
        throw new Exception("Invalid object track was accepted.");
    }

    public static void Run(Action<string, Action> test)
    {
        test("current schema defaults to 60 Hz with supported metadata", () =>
        {
            var header = new ReplayHeader { Scene = "Level_Synthetic" };
            Check(header.Schema == ReplayRules.CurrentSchema && header.SampleHz == 60 && ReplayRules.MaxFrames == 7201);
            ReplayRules.Validate(header);
        });
        test("schema 2 missing fields cannot be promoted to current defaults", () =>
        {
            var header = JObject.FromObject(Header()); header["Schema"] = 2; header.Remove("SampleHz"); header.Remove("Fidelity"); header.Remove("Recorder");
            string actor = "{\"Type\":\"frame\",\"T\":0,\"Actors\":[{\"Id\":\"1\"}]}";
            Reject(() => Read(header.ToString(Formatting.None), actor, actor.Replace("\"T\":0", "\"T\":0.05")));
        });
        test("schema 2 is not promoted into a new object recording", () =>
        {
            var h = Header(); h.Schema = 2;
            var f = Frame(0); f.Items = new[] { Item() };
            Reject(() => Read(Json(h), Json(f), Json(Frame(.1))));
        });
        test("known backpack inventory supports real ID zero and temporary slot 250", () =>
        {
            var f = Frame(0); f.Actors = new[] { new ActorFrame { Id = "a", InventoryKnown = true, BackpackType = 2, BackpackWorn = true,
                Inventory = new[] { new InventoryFrame { Slot = 250, ItemId = 0, Empty = false, Equipped = true }, new InventoryFrame { Slot = 0, Backpack = true, Empty = true } } } };
            ReplayRules.Validate(f, -1);
            var c = ReadFrames(f, Frame(.1));
            Check(!c.Frames[0].Actors[0].Inventory[0].Empty && c.Frames[0].Actors[0].Inventory[1].Empty);
        });
        test("first-person hidden backpack contents round trip through archive", () =>
        {
            var b = new RollingBuffer(); var item = Item(); item.State = 2; item.RevealBackpackContents = true;
            b.Add(new ReplayFrame { T = 0, Items = new[] { item } }); b.Add(new ReplayFrame { T = .1, Items = new[] { item } });
            WithArchive(b.Snapshot(Header(), DateTime.UtcNow), (path, _) =>
            {
                var clip = ReplayFiles.Read(path);
                Check(clip.Frames.All(f => f.Items[0].RevealBackpackContents && f.Items[0].State == 2));
                Check(ReferenceEquals(clip.Frames[0].Items[0], clip.Frames[1].Items[0]));
            });
        });
        test("backpack visibility override cannot affect held or world items", () =>
        {
            var f = Frame(0); var item = Item(); item.State = 0; item.RevealBackpackContents = true; f.Items = new[] { item };
            Reject(() => ReplayRules.Validate(f, -1)); item.State = 2; ReplayRules.Validate(f, -1);
        });
        test("partial inventory remains marked unknown instead of dropping the whole frame", () =>
        {
            var f = Frame(0); f.Actors = new[] { new ActorFrame { Id = "a", Inventory = new[] { new InventoryFrame() } } };
            ReplayRules.Validate(f, -1);
            var clip = ReadFrames(f, Frame(.1));
            Check(!clip.Frames[0].Actors[0].InventoryKnown && clip.Frames[0].Actors[0].Inventory.Length == 1);
        });
        test("duplicate inventory slots rejected but separate backpack slots accepted", () =>
        {
            var actor = new ActorFrame { Id = "a", InventoryKnown = true, Inventory = new[] { new InventoryFrame { Slot = 0 }, new InventoryFrame { Slot = 0 } } };
            var f = Frame(0); f.Actors = new[] { actor };
            Reject(() => ReplayRules.Validate(f, -1));
            actor.Inventory[1].Backpack = true; ReplayRules.Validate(f, -1);
        });
        test("invalid backpack type is rejected", () =>
        {
            var f = Frame(0); f.Actors = new[] { new ActorFrame { Id = "a", BackpackType = 5 } };
            Reject(() => ReplayRules.Validate(f, -1));
        });
        test("actor local joints round trip alongside object and inventory tracks", () =>
        {
            var actor = new ActorFrame { Id = "a", JointPose = new[] { ".", "./Arm_L#0", "./Arm_R#0", "./Elbow_L#0", "./Elbow_R#0", "./Hand_L#0", "./Hand_R#0" }
                .Select(p => new NodePose { Path = p, SampleTime = 0 }).ToArray() };
            var f = Frame(0); f.Actors = new[] { actor }; ReplayRules.Validate(f, -1);
            var c = ReadFrames(f, Frame(.1)); Check(c.Frames[0].Actors[0].JointPose.Length == 7);
            Check(c.Frames[0].Actors[0].JointPose[5].Path == "./Hand_L#0");
            actor.JointPose[0].Path = ""; Reject(() => ReplayRules.Validate(f, -1));
        });
        test("duplicate actor local joint rejected", () =>
        {
            var f = Frame(0); f.Actors = new[] { new ActorFrame { Id = "a", JointPose = new[] { new NodePose { Path = "./Hand_L#0" }, new NodePose { Path = "./Hand_L#0" } } } };
            Reject(() => ReplayRules.Validate(f, -1));
        });
        test("duplicate item and crate identities rejected", () =>
        {
            var f = Frame(0); f.Items = new[] { Item(), Item() }; Reject(() => ReplayRules.Validate(f, -1));
            f.Items = Array.Empty<ItemFrame>(); f.Crates = new[] { Crate(), Crate() }; Reject(() => ReplayRules.Validate(f, -1));
        });
        test("item and crate limits enforced", () =>
        {
            var f = Frame(0); f.Items = Enumerable.Range(0, ReplayRules.MaxItems + 1).Select(i => Item(i.ToString())).ToArray(); Reject(() => ReplayRules.Validate(f, -1));
            f.Items = Array.Empty<ItemFrame>(); f.Crates = Enumerable.Range(0, ReplayRules.MaxCrates + 1).Select(i => new CrateFrame { Key = i.ToString(), Kind = "box" }).ToArray(); Reject(() => ReplayRules.Validate(f, -1));
        });
        test("invalid item floats and object quaternion rejected", () =>
        {
            var item = Item(); var f = Frame(0); f.Items = new[] { item };
            item.Fuel = float.NaN; Reject(() => ReplayRules.Validate(f, -1)); item.Fuel = -1;
            item.Pose.Rotation = new float[4]; Reject(() => ReplayRules.Validate(f, -1));
        });
        test("item IDs cannot overflow native ushort lookups", () =>
        {
            var item = Item(); var f = Frame(0); f.Items = new[] { item }; item.ItemId = 65536;
            Reject(() => ReplayRules.Validate(f, -1)); item.ItemId = 65535; ReplayRules.Validate(f, -1);
        });
        test("crate paths longer than item keys survive state and removal deltas", () =>
        {
            var f = Frame(0); var crate = Crate(); crate.Key = new string('x', 2048); f.Crates = new[] { crate };
            var last = JObject.Parse(Delta(Frame(.1), false)); last["RemovedCrates"] = new JArray(crate.Key);
            var clip = Read(Json(Header()), Delta(f, true), last.ToString(Formatting.None));
            Check(clip.Frames[0].Crates[0].Key.Length == 2048 && clip.Frames[1].Crates.Length == 0);
        });
        test("duplicate or oversized node poses rejected", () =>
        {
            var item = Item(); var f = Frame(0); f.Items = new[] { item };
            item.Pose.Nodes = new[] { new NodePose { Path = "." }, new NodePose { Path = "." } }; Reject(() => ReplayRules.Validate(f, -1));
            item.Pose.Nodes = Enumerable.Range(0, ReplayRules.MaxNodes + 1).Select(i => new NodePose { Path = i.ToString() }).ToArray(); Reject(() => ReplayRules.Validate(f, -1));
        });
        test("crate animation invalid time and missing identity rejected", () =>
        {
            var c = Crate(); var f = Frame(0); f.Crates = new[] { c }; ReplayRules.Validate(f, -1);
            c.Animation!.Time = float.NaN; Reject(() => ReplayRules.Validate(f, -1)); c.Animation.Time = -1; Reject(() => ReplayRules.Validate(f, -1));
            c.Animation.Time = 86401; Reject(() => ReplayRules.Validate(f, -1)); c.Animation.Time = 0;
            c.Animation.Clip = new string('x', 1025); Reject(() => ReplayRules.Validate(f, -1)); c.Animation.Clip = ""; Reject(() => ReplayRules.Validate(f, -1));
        });
        test("capture events use unbounded game time while saved clips remain bounded", () =>
        {
            var f = Frame(9999); f.Events = new[] { Event(10, 9998.9) };
            ReplayRules.ValidateCapture(f, 9998); Reject(() => ReplayRules.Validate(f, -1));
        });
        test("disk validation does not trust capture's immutable-reference cache", () =>
        {
            var f = Frame(0); f.Items = new[] { Item() }; ReplayRules.ValidateCapture(f, -1);
            f.Items[0].Fuel = float.NaN;
            Reject(() => ReplayRules.Validate(f, -1));
        });
        test("future, duplicate and non-monotonic item events rejected", () =>
        {
            var f = Frame(1); f.Events = new[] { Event(1, 2) }; Reject(() => ReplayRules.Validate(f, -1));
            f.Events = new[] { Event(1, .1), Event(1, .2) }; Reject(() => ReplayRules.Validate(f, -1));
            f.Events = new[] { Event(1, .2), Event(2, .1) }; Reject(() => ReplayRules.Validate(f, -1));
        });
        test("snapshot rebases event copies and preserves shared object state", () =>
        {
            var b = new RollingBuffer(); var item = Item(); var first = Frame(400); first.Items = new[] { item };
            first.Events = new[] { Event(1, 399.99), Event(2, 400) }; b.Add(first);
            var second = Frame(400.1); second.Items = new[] { item }; second.Events = new[] { Event(3, 400.05) }; b.Add(second);
            var clip = b.Snapshot(Header(), DateTime.UtcNow);
            Check(clip.Frames[0].Events.Length == 1 && clip.Frames[0].Events[0].T == 0);
            Check(Math.Abs(clip.Frames[1].Events[0].T - .05) < 1e-8 && second.Events[0].T == 400.05);
            Check(!ReferenceEquals(second.Events[0], clip.Frames[1].Events[0]) && ReferenceEquals(item, clip.Frames[1].Items[0]));
        });
        test("shared object memory is counted only once across real frames", () =>
        {
            var item = Item(); var crate = Crate(); var b = new RollingBuffer();
            var first = Frame(0); first.Items = new[] { item }; first.Crates = new[] { crate }; b.Add(first);
            long before = b.EstimatedBytes; var second = Frame(.1); second.Items = new[] { item }; second.Crates = new[] { crate }; b.Add(second);
            long expected = RollingBuffer.Estimate(second) - RollingBuffer.Estimate(item) - RollingBuffer.Estimate(crate);
            Check(b.EstimatedBytes - before == expected);
        });
        test("evicting the last object reference releases its memory", () =>
        {
            var b = new RollingBuffer(); var first = Frame(0); first.Items = new[] { Item() }; first.Crates = new[] { Crate() }; b.Add(first);
            var second = Frame(121); b.Add(second); Check(b.Count == 1 && b.EstimatedBytes == RollingBuffer.Estimate(second));
        });
        test("one oversized frame rolls back acquired shared-object accounting", () =>
        {
            var b = new RollingBuffer(4096); b.Add(Frame(0)); long previous = b.EstimatedBytes;
            var f = Frame(.1); f.Items = Enumerable.Range(0, 20).Select(i => Item(i.ToString())).ToArray();
            try { b.Add(f); throw new Exception("Oversized frame accepted."); }
            catch (InvalidOperationException) { }
            Check(b.Count == 1 && b.EstimatedBytes == previous); b.Add(Frame(.2)); Check(b.Count == 2);
        });
        test("changed object snapshots remain available for backward seeking", () =>
        {
            var b = new RollingBuffer(); var first = Frame(0); first.Items = new[] { Item() }; b.Add(first);
            var second = Frame(.1); second.Items = new[] { Item() }; second.Items[0].Used = true; b.Add(second);
            var c = b.Snapshot(Header(), DateTime.UtcNow); Check(c.At(.1).Left.Items[0].Used && !c.At(0).Left.Items[0].Used);
        });
        test("archive writes a complete first frame then reference deltas", () => WithArchive(BuildClip(), (path, text) =>
        {
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(JObject.Parse).ToArray();
            Check((bool)lines[1]["ObjectsFull"]! && !(bool)lines[2]["ObjectsFull"]!);
            Check(lines[1]["Items"]!.Count() == 2 && lines[2]["Items"]!.Count() == 0 && lines[2]["Crates"]!.Count() == 0);
            Check(lines[3]["Items"]!.Count() == 1 && lines[3]["RemovedItems"]!.Count() == 1 && lines[3]["RemovedCrates"]!.Count() == 1);
            var read = ReplayFiles.Read(path);
            Check(ReferenceEquals(read.Frames[0].Items[0], read.Frames[1].Items[0]));
            Check(read.Frames[2].Items.Length == 1 && read.Frames[2].Items[0].Used && read.Frames[2].Crates.Length == 0);
            Check(!read.At(0).Left.Items[0].Used && read.At(.2).Left.Items[0].Used);
            Check(read.Frames[0].Crates[0].Animation!.Clip == "Open" && read.Frames[0].Crates[0].Animation!.Time == .4f);
        }));
        test("first delta cannot omit full object state", () =>
            Reject(() => Read(Json(Header()), Delta(Frame(0), false), Delta(Frame(.1), false))));
        test("deleting unknown identities is rejected", () =>
            Reject(() => Read(Json(Header()), Delta(Frame(0), true), Delta(Frame(.1), false, new[] { "missing" }))));
        test("duplicate removal and update-removal conflicts rejected", () =>
        {
            var first = Frame(0); first.Items = new[] { Item() }; var second = Frame(.1); second.Items = new[] { Item() };
            Reject(() => Read(Json(Header()), Delta(first, true), Delta(second, false, new[] { "item-1" })));
            Reject(() => Read(Json(Header()), Delta(first, true), Delta(Frame(.1), false, new[] { "item-1", "item-1" })));
        });
        test("events cannot silently replay twice across archive frames", () =>
        {
            var f = Frame(0); f.Events = new[] { Event(1, 0) }; var g = Frame(.1); g.Events = new[] { Event(1, .1) };
            Reject(() => ReadFrames(f, g));
        });
        test("60 Hz item ring keeps two minutes within the default budget", () =>
        {
            var b = new RollingBuffer(); var items = Enumerable.Range(0, 256).Select(i => Item(i.ToString())).ToArray();
            for (int i = 0; i <= 7200; i++) b.Add(new ReplayFrame { T = i / 60d, Items = items });
            Check(b.Count == 7201 && b.Duration == 120 && !b.MemoryLimited && b.EstimatedBytes < 24 * 1024 * 1024);
        });
    }

    private static ItemEvent Event(long sequence, double time) => new() { Sequence = sequence, T = time, Kind = "use", ItemKey = "item-1", ItemId = 4 };
    private static string Delta(ReplayFrame f, bool full, string[]? removed = null)
    {
        var token = JObject.Parse(Json(new ReplayDeltaCodec.Writer(10).Encode(f)));
        token["ObjectsFull"] = full;
        if (removed != null) token["RemovedItems"] = new JArray(removed);
        return token.ToString(Formatting.None);
    }

    private static ReplayClip ReadFrames(params ReplayFrame[] frames)
    {
        var writer = new ReplayDeltaCodec.Writer(10);
        return Read(new[] { Json(Header()) }.Concat(frames.Select(frame => Json(writer.Encode(frame)))).ToArray());
    }

    private static ReplayClip BuildClip()
    {
        var b = new RollingBuffer(); var one = Item(); var two = Item("item-2"); var crate = Crate();
        b.Add(new ReplayFrame { T = 0, Items = new[] { one, two }, Crates = new[] { crate } });
        b.Add(new ReplayFrame { T = .1, Items = new[] { one, two }, Crates = new[] { crate } });
        var used = Item(); used.Used = true;
        b.Add(new ReplayFrame { T = .2, Items = new[] { used }, Events = new[] { Event(4, .2) } });
        return b.Snapshot(Header(), DateTime.UtcNow);
    }

    private static ReplayClip Read(params string[] lines)
    {
        string path = Path.Combine(Path.GetTempPath(), "peak-object-test-" + Guid.NewGuid().ToString("N") + ".ndjson");
        try { File.WriteAllText(path, string.Join('\n', lines) + "\n{\"Type\":\"end\"}"); return ReplayFiles.Read(path); }
        finally { File.Delete(path); }
    }

    private static void WithArchive(ReplayClip clip, Action<string, string> verify)
    {
        string directory = Path.Combine(Path.GetTempPath(), "peak-object-archive-" + Guid.NewGuid().ToString("N"));
        string? path = null;
        try
        {
            path = ReplayArchive.Save(directory, clip);
            using var input = ReplayArchive.OpenText(path); string text = input.ReadToEnd(); verify(path, text);
        }
        finally { if (path != null) File.Delete(path); if (Directory.Exists(directory)) Directory.Delete(directory); }
    }

    public static void Benchmark()
    {
        foreach (int actors in new[] { 4, 8, 16 }) Console.WriteLine(Measure(actors));
        Console.WriteLine(Measure(16, 512));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string Measure(int actors, int budgetMiB = 256)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetTotalMemory(true);
        var b = new RollingBuffer(budgetMiB * 1024L * 1024);
        var items = Enumerable.Range(0, 256).Select(i => Item(i.ToString())).ToArray();
        var crates = Enumerable.Range(0, 48).Select(i => new CrateFrame { Key = "crate-" + i, Kind = "Luggage" }).ToArray();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int sample = 0; sample <= 7200; sample++)
        {
            double t = sample / 60d;
            var frame = new ReplayFrame
            {
                T = t, Items = items.ToArray(), Crates = crates.ToArray(),
                Actors = Enumerable.Range(0, actors).Select(i => new ActorFrame
                {
                    Id = "player-" + i, Name = "Synthetic " + i, Position = new[] { (float)t, 0f, (float)i },
                    InventoryKnown = true, BackpackType = 0, BackpackWorn = true,
                    Inventory = Enumerable.Range(0, 8).Select(slot => new InventoryFrame { Slot = slot % 4, Backpack = slot >= 4, ItemId = 4, Empty = false }).ToArray(),
                    JointPose = Enumerable.Range(0, 128).Select(joint => new NodePose { Path = joint == 0 ? "." : "./joint-" + joint.ToString("000") + "#0", SampleTime = t }).ToArray(),
                }).ToArray(),
            };
            ReplayRules.ValidateCapture(frame, sample == 0 ? -1 : (sample - 1) / 60d);
            b.Add(frame);
        }
        watch.Stop(); long retained = GC.GetTotalMemory(true) - before;
        string result = $"OBJECT-MEMORY hz=60 actors={actors} budgetMiB={budgetMiB} staticItems=256 staticCrates=48 frames={b.Count} seconds={b.Duration:F2} managedMiB={retained / 1048576d:F2} accountedMiB={b.EstimatedBytes / 1048576d:F2} buildMs={watch.ElapsedMilliseconds} limited={b.MemoryLimited}";
        GC.KeepAlive(b); return result;
    }
}
