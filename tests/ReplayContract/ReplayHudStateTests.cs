using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class ReplayHudStateTests
{
    public static void Run(Action<string, Action> test)
    {
        test("HUD state is explicitly unknown until captured, independently of inventory", () =>
        {
            var actor = new ActorFrame { Id = "actor" };
            Check(actor.HudState == null && !actor.InventoryKnown && actor.Inventory.Length == 0);
            actor.InventoryKnown = true;
            Check(actor.HudState == null && actor.Inventory.Length == 0,
                "Known empty inventory must not imply known stamina or afflictions.");
        });
        test("schema 11 saves all recorded stamina afflictions and native indicators", () => InRoot(root =>
        {
            var clip = Clip(Frame(0, State(1)), Frame(.25, State(2)));
            var decoded = ReplayFiles.Read(ReplayArchive.Save(root, clip));
            Check(decoded.Header.Schema == ReplayRules.CurrentSchema && decoded.Complete);
            for (int i = 0; i < clip.Frames.Count; i++) EqualState(clip.Frames[i].Actors[0].HudState, decoded.Frames[i].Actors[0].HudState);
            Check(decoded.Frames[0].Actors[0].HudState!.Afflictions.Length == ReplayHudState.StatusCount);
        }));
        test("HUD interpolation blends captured values and holds discrete indicators", () =>
        {
            var left = State(1); var right = State(2);
            left.Stamina = .2f; right.Stamina = .8f;
            left.MaxStamina = .4f; right.MaxStamina = .6f;
            left.ExtraStamina = .3f; right.ExtraStamina = .9f;
            left.Petrify = .1f; right.Petrify = .9f;
            string before = Json(left), after = Json(right);
            var actual = ReplayHudState.Interpolate(left, right, .25f)!;
            Near(actual.Stamina, .35f); Near(actual.MaxStamina, .45f);
            Near(actual.ExtraStamina, .45f); Near(actual.Petrify, .3f);
            for (int i = 0; i < ReplayHudState.StatusCount; i++) Near(actual.Afflictions[i], left.Afflictions[i] * .75f + right.Afflictions[i] * .25f);
            Check(actual.Invincible == left.Invincible && actual.CanGetHungry == left.CanGetHungry && actual.Rainbow == left.Rainbow &&
                actual.MoraleKnown == left.MoraleKnown && actual.MoraleBoost == left.MoraleBoost);
            Check(Json(left) == before && Json(right) == after && !ReferenceEquals(actual.Afflictions, left.Afflictions));
        });
        test("HUD unknown endpoints never interpolate an invented zero status", () =>
        {
            var known = State(1);
            Check(ReplayHudState.Interpolate(null, known, .5f) == null);
            Check(ReferenceEquals(ReplayHudState.Interpolate(known, null, .5f), known));
            Check(ReplayHudState.Interpolate(null, null, .5f) == null);
            Check(ReferenceEquals(ReplayHudState.Interpolate(null, known, 1), known));
            Check(ReplayHudState.Interpolate(known, null, 1) == null);
            Check(ReferenceEquals(ReplayHudState.Interpolate(known, State(2), 0), known));
        });
        test("HUD validation rejects malformed arrays nonfinite and out-of-range values", () =>
        {
            foreach (Action<ReplayHudState> corrupt in new Action<ReplayHudState>[]
            {
                s => s.Stamina = float.NaN, s => s.MaxStamina = float.PositiveInfinity,
                s => s.ExtraStamina = -1, s => s.Stamina = 1.01f,
                s => s.Petrify = 1.01f, s => s.Afflictions = new float[14],
                s => s.Afflictions = null!, s => s.Afflictions[1] = float.NaN,
                s => s.Afflictions[1] = -.001f, s => s.Afflictions[1] = 2.01f,
                s => s.Afflictions[0] = 1.01f, s => { s.MoraleKnown = false; s.MoraleBoost = true; },
            })
            {
                var state = State(1); corrupt(state);
                Reject(() => ReplayRules.Validate(Frame(0, state), -1));
            }
            var valid = State(1); valid.Afflictions = Enumerable.Repeat(1.5f, ReplayHudState.StatusCount).ToArray();
            valid.Afflictions[0] = 1;
            ReplayRules.Validate(Frame(0, valid), -1);
            Check(valid.StatusSum > 2, "The native outline must be able to show status overflow.");
        });
        test("recorded HUD storage participates in rolling memory limits and immutable snapshots", () =>
        {
            long ordinary = RollingBuffer.Estimate(Frame(0, null)), withStatus = RollingBuffer.Estimate(Frame(0, State(1)));
            Check(withStatus - ordinary >= ReplayHudState.StatusCount * sizeof(float),
                "Recorded status arrays escaped the rolling replay memory budget.");
            var buffer = new RollingBuffer(4096);
            buffer.Add(Frame(400, State(1))); buffer.Add(Frame(400.25, null));
            var snapshot = buffer.Snapshot(Header(), DateTime.UtcNow);
            for (int i = 2; i < 50; i++) buffer.Add(Frame(400 + i * .25, State(i % 5)));
            Check(buffer.MemoryLimited && buffer.EstimatedBytes <= 4096);
            EqualState(State(1), snapshot.At(0).Left.Actors[0].HudState);
            Check(snapshot.At(.25).Left.Actors[0].HudState == null);
        });
        test("HUD actor delta omits equal state and patches only changed fields", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(); var reader = new ReplayDeltaCodec.Reader();
            var first = reader.Decode(Token(writer.Encode(Frame(0, State(1)))));
            var same = Token(writer.Encode(Frame(.25, State(1))));
            Check(((JArray)same["Actors"]!).Count == 0, "Equal HUD snapshots should not force actor updates.");
            var unchanged = reader.Decode(same);
            Check(ReferenceEquals(first.Actors[0].HudState, unchanged.Actors[0].HudState));
            var source = State(1); source.Afflictions[2] = .731f;
            var changed = Token(writer.Encode(Frame(.5, source)));
            var statePatch = (JObject)changed["Actors"]![0]!["HudState"]!;
            Check(statePatch.Properties().Select(p => p.Name).SequenceEqual(new[] { "Afflictions" }));
            var decoded = reader.Decode(changed);
            EqualState(source, decoded.Actors[0].HudState);
            Near(first.Actors[0].HudState!.Afflictions[2], State(1).Afflictions[2]);
        });
        test("HUD actor delta clears known state then restores a complete independent state", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(); var reader = new ReplayDeltaCodec.Reader();
            var before = reader.Decode(Token(writer.Encode(Frame(0, State(1)))));
            var clear = Token(writer.Encode(Frame(.25, null)));
            Check(clear["Actors"]![0]!["HudState"]!.Type == JTokenType.Null);
            Check(reader.Decode(clear).Actors[0].HudState == null);
            EqualState(State(2), reader.Decode(Token(writer.Encode(Frame(.5, State(2))))).Actors[0].HudState);
            EqualState(State(1), before.Actors[0].HudState);
            reader.Decode(Token(writer.Encode(new ReplayFrame { T = .75 })));
            var reappeared = reader.Decode(Token(writer.Encode(Frame(1, null))));
            Check(reappeared.Actors[0].HudState == null, "A reappearing actor inherited a removed actor's HUD.");
        });
        test("schema 10 remains compatible and reads HUD as unknown without inventing data", () =>
        {
            var header = Header(); header.Schema = 10;
            ReplayRules.Validate(header);
            Check(ReplayRules.Compatible(header, Header()) && ReplayRules.Compatible(Header(), header));
            var writer = new ReplayDeltaCodec.Writer(10);
            var legacy = Frame(0, null); legacy.Actors[0].Stamina = .37f; legacy.Actors[0].ExtraStamina = .19f;
            var baseline = Token(writer.Encode(legacy));
            Check(baseline["Actors"]![0]!["HudState"] == null);
            using var text = new StringReader(Json(header) + "\n" + Json(baseline) + "\n" + Json(writer.Encode(Frame(.25, null))) + "\n{\"Type\":\"end\"}");
            var decoded = ReplayFiles.Read(text);
            Check(decoded.Complete && decoded.Header.Schema == 10 && decoded.Frames.All(f => f.Actors[0].HudState == null));
            Near(decoded.Frames[0].Actors[0].Stamina, .37f);
            Reject(() => new ReplayDeltaCodec.Writer(10).Encode(Frame(0, State(1))));
        });
        test("pre-HUD schema 10 full recordings still open seek and remain playable in the library", () => InRoot(root =>
        {
            string path = LegacyFullFile(root);
            var info = FullReplayArchive.ReadInfo(path);
            Check(info.Complete && info.Header.Schema == 10 && info.Pages.Length == 1);
            var clip = FullReplayArchive.ReadPage(path, info, 0);
            Check(clip.Frames.All(f => f.Actors[0].HudState == null));
            Near(clip.At(0).Left.Actors[0].Stamina, .37f);
            Near(clip.At(.5).Left.Actors[0].Stamina, .19f);
            Check(MemoriesLibraryModel.FromFullRun(path, info, new FileInfo(path).Length, "").Playable,
                "Supported pre-HUD full recordings disappeared from playable memoirs.");
            var highlight = Header(); highlight.Schema = 10; highlight.Duration = .5; highlight.FrameCount = 3;
            Check(MemoriesLibraryModel.FromHighlight("legacy.peakreplay", highlight, "").Playable,
                "Supported pre-HUD highlights disappeared from playable memoirs.");
        }));
        test("schema 11 baseline requires explicit known or unknown HUD while deltas may inherit", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(11);
            var baseline = Token(writer.Encode(Frame(0, null)));
            Check(baseline["Actors"]![0]!["HudState"]!.Type == JTokenType.Null);
            var absent = (JObject)baseline.DeepClone(); ((JObject)absent["Actors"]![0]!).Remove("HudState");
            Reject(() => new ReplayDeltaCodec.Reader(11).Decode(absent));
            var reader = new ReplayDeltaCodec.Reader(11); reader.Decode(baseline);
            Check(reader.Decode(Token(writer.Encode(Frame(.25, null)))).Actors[0].HudState == null);
        });
        test("native inventory fuel and cooked presentation entries survive schema 11 deltas", () => InRoot(root =>
        {
            var first = Frame(0, State(1)); var second = Frame(.25, State(2)); var third = Frame(.5, null);
            first.Actors[0].InventoryKnown = second.Actors[0].InventoryKnown = third.Actors[0].InventoryKnown = true;
            first.Actors[0].Inventory = new[] { new InventoryFrame { Slot = 0, ItemId = 4, Empty = false, Fuel = 700, UiFuel = .625f, Cooked = 3 } };
            second.Actors[0].Inventory = new[] { new InventoryFrame { Slot = 0, ItemId = 4, Empty = false, Fuel = 699, UiFuel = .25f, Cooked = 4 } };
            third.Actors[0].Inventory = new[] { new InventoryFrame { Slot = 0, ItemId = 4, Empty = false, UiFuel = -1, Cooked = -1 } };
            var decoded = ReplayFiles.Read(ReplayArchive.Save(root, Clip(first, second, third)));
            Near(decoded.Frames[0].Actors[0].Inventory[0].UiFuel, .625f);
            Near(decoded.Frames[1].Actors[0].Inventory[0].UiFuel, .25f);
            Check(decoded.Frames[0].Actors[0].Inventory[0].Cooked == 3 && decoded.Frames[1].Actors[0].Inventory[0].Cooked == 4);
            Check(decoded.Frames[2].Actors[0].Inventory[0].UiFuel == -1 && decoded.Frames[2].Actors[0].Inventory[0].Cooked == -1);
            Near(decoded.At(0).Left.Actors[0].Inventory[0].Fuel, 700);
        }));
        test("schema 10 inventory defaults native fuel and cooking data to unknown", () =>
        {
            var frame = Frame(0, null); frame.Actors[0].InventoryKnown = true;
            frame.Actors[0].Inventory = new[] { new InventoryFrame { Slot = 0, ItemId = 4, Empty = false, Fuel = 25 } };
            var token = Token(new ReplayDeltaCodec.Writer(10).Encode(frame));
            var slot = token["Actors"]![0]!["Inventory"]![0]!;
            Check(slot["UiFuel"] == null && slot["Cooked"] == null);
            var decoded = new ReplayDeltaCodec.Reader(10).Decode(token).Actors[0].Inventory[0];
            Check(decoded.UiFuel == -1 && decoded.Cooked == -1 && decoded.Fuel == 25,
                "Raw fuel is not the native normalized fuel bar.");
        });
        test("schema 11 rejects absent or invalid native inventory presentation entries", () =>
        {
            var frame = Frame(0, null); frame.Actors[0].Inventory = new[] { new InventoryFrame { Slot = 0 } };
            var token = Token(new ReplayDeltaCodec.Writer(11).Encode(frame));
            foreach (string field in new[] { "UiFuel", "Cooked" })
            {
                var absent = (JObject)token.DeepClone(); ((JObject)absent["Actors"]![0]!["Inventory"]![0]!).Remove(field);
                Reject(() => new ReplayDeltaCodec.Reader(11).Decode(absent));
            }
            foreach (float fuel in new[] { -.5f, 1.01f, float.NaN })
            { frame.Actors[0].Inventory[0].UiFuel = fuel; Reject(() => ReplayRules.Validate(frame, -1)); }
            frame.Actors[0].Inventory[0].UiFuel = -1; frame.Actors[0].Inventory[0].Cooked = -2;
            Reject(() => ReplayRules.Validate(frame, -1));
        });
        test("unknown inventory remains distinct from known empty across HUD deltas and seeks", () => InRoot(root =>
        {
            var unknown = Frame(0, State(1)); var empty = Frame(.25, State(2)); var unsynced = Frame(.5, null);
            empty.Actors[0].InventoryKnown = true;
            var decoded = ReplayFiles.Read(ReplayArchive.Save(root, Clip(unknown, empty, unsynced)));
            Check(!decoded.At(0).Left.Actors[0].InventoryKnown && decoded.At(.25).Left.Actors[0].InventoryKnown &&
                !decoded.At(.5).Left.Actors[0].InventoryKnown);
            Check(decoded.Frames.All(f => f.Actors[0].Inventory.Length == 0));
            Check(decoded.At(0).Left.Actors[0].HudState != null && decoded.At(.5).Left.Actors[0].HudState == null);
        }));
        test("HUD full-run page baselines survive random forward and backward seeks", () => InRoot(root =>
        {
            const double origin = 17001.25;
            var frames = Enumerable.Range(0, 121).Select(i => Frame(origin + i * .25, i % 7 == 0 ? null : State(i % 5))).ToArray();
            var writer = new FullReplayWriter(root, Header(), new ContinuousReplayOptions { SegmentSeconds = 5 });
            foreach (var frame in frames) Check(writer.TryEnqueue(frame));
            var completion = writer.CompleteAsync(); Check(completion.Wait(10000));
            var result = completion.GetAwaiter().GetResult(); Check(result.Status == "completed");
            var info = FullReplayArchive.ReadInfo(result.FilePath); Check(info.Pages.Length >= 6 && info.Header.Schema == ReplayRules.CurrentSchema);
            using var timeline = new PagedReplayTimeline(info.Header, info.Duration, info.FrameCount, info.ActorIds,
                info.Pages.Select(p => new ReplayPageRange(p.Start, p.End)).ToArray(),
                (index, token) => FullReplayArchive.ReadPage(result.FilePath, info, index, token));
            var random = new Random(7129);
            var queries = info.Pages.Select(p => p.Start).Reverse()
                .Concat(Enumerable.Range(0, 60).Select(_ => random.Next(0, 120) * .25 + .125));
            foreach (double time in queries)
            {
                var sample = Ready(timeline, time);
                int leftIndex = (int)Math.Round(sample.Left.T * 4), rightIndex = (int)Math.Round(sample.Right.T * 4);
                var left = sample.Left.Actors[0].HudState; var right = sample.Right.Actors[0].HudState;
                EqualState(frames[leftIndex].Actors[0].HudState, left);
                EqualState(frames[rightIndex].Actors[0].HudState, right);
                EqualState(ReplayHudState.Interpolate(frames[leftIndex].Actors[0].HudState, frames[rightIndex].Actors[0].HudState, sample.Mix),
                    ReplayHudState.Interpolate(left, right, sample.Mix));
                Check(timeline.CachedPageCount <= 5, "A HUD seek grew the recording cache beyond its neighboring pages.");
            }
        }));
        test("HUD continuous segment rotation rebases only time and preserves known state", () => InRoot(root =>
        {
            var frames = Enumerable.Range(0, 13).Select(i => Frame(400 + i * .25, i % 3 == 0 ? null : State(i % 5))).ToArray();
            var writer = new ContinuousReplayWriter(root, Header(), new ContinuousReplayOptions { SegmentSeconds = 1 });
            foreach (var frame in frames) Check(writer.TryEnqueue(frame));
            var completion = writer.CompleteAsync(); Check(completion.Wait(10000));
            var result = completion.GetAwaiter().GetResult(); Check(result.Status == "completed" && result.CompletedSegments >= 3);
            var records = File.ReadAllLines(Path.Combine(writer.RunDirectory, "manifest.ndjson")).Select(JObject.Parse);
            foreach (var record in records.Where(r => r.Value<string>("Type") == "segment-complete"))
            {
                var clip = ReplayFiles.Read(Path.Combine(writer.RunDirectory, record.Value<string>("File")!));
                double nativeStart = record.Value<double>("NativeStart");
                foreach (var frame in clip.Frames)
                {
                    int source = (int)Math.Round((nativeStart + frame.T - 400) * 4);
                    EqualState(frames[source].Actors[0].HudState, frame.Actors[0].HudState);
                }
            }
        }));
    }

    private static ReplayHeader Header() => new() { Scene = "Level_HudContract", GameVersion = "synthetic", BuildId = 22, GameAssembly = new string('a', 32), Route = "Shore,Alpine", Participants = new[] { "Synthetic" } };
    private static ReplayHudState State(int variant) => new()
    {
        Stamina = .2f + variant * .1f, MaxStamina = .75f - variant * .05f, ExtraStamina = variant * .05f,
        Afflictions = Enumerable.Range(0, ReplayHudState.StatusCount).Select(i => (i + variant) * .012f).ToArray(),
        Petrify = variant * .1f, Invincible = variant % 2 == 0, CanGetHungry = variant % 2 != 0,
        Rainbow = variant % 2 == 0, MoraleKnown = variant % 2 == 0, MoraleBoost = variant % 4 == 0,
    };
    private static ReplayFrame Frame(double time, ReplayHudState? state) => new() { T = time, Actors = new[] { new ActorFrame { Id = "actor", Name = "Synthetic", HudState = state } } };
    private static ReplayClip Clip(params ReplayFrame[] frames)
    {
        var clip = new ReplayClip { Header = Header(), Complete = true }; clip.Frames.AddRange(frames);
        clip.Header.Duration = clip.Duration; clip.Header.FrameCount = frames.Length;
        return clip;
    }
    private static string LegacyFullFile(string root)
    {
        // Build the old physical container independently of the current writer:
        // schema 10 pages have no HudState, UiFuel or Cooked inventory fields.
        var header = Header(); header.Schema = 10;
        var frames = new[] { Frame(0, null), Frame(.25, null), Frame(.5, null) };
        frames[0].Actors[0].Stamina = .37f; frames[1].Actors[0].Stamina = .25f; frames[2].Actors[0].Stamina = .19f;
        var codec = new ReplayDeltaCodec.Writer(10);
        byte[] headerBytes = Encoding.UTF8.GetBytes(Json(header));
        byte[] raw = Encoding.UTF8.GetBytes(string.Join("\n", new[] { Json(header) }
            .Concat(frames.Select(f => Json(codec.Encode(f)))).Append("{\"Type\":\"end\"}")));
        byte[] compressed;
        using (var memory = new MemoryStream())
        {
            using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, true)) gzip.Write(raw);
            compressed = memory.ToArray();
        }
        var budget = new ContinuousReplayBudget(); foreach (var frame in frames) budget.Commit(frame, budget.Additional(frame));
        var index = new FullReplayIndex
        {
            Header = header, Duration = .5, FrameCount = frames.Length, ActorIds = new[] { "actor" }, Complete = true,
            Reason = "synthetic-legacy-fixture", GapOver100ms = 2, LargestGapSeconds = .25,
            Pages = new[] { new FullReplayPage { Index = 0, Start = 0, End = .5, Offset = 12 + headerBytes.Length,
                Length = compressed.Length, RawBytes = raw.Length, DecodedBytes = budget.Bytes, FrameCount = frames.Length } },
        };
        byte[] indexBytes = Encoding.UTF8.GetBytes(Json(index));
        string path = Path.Combine(root, "schema-10-before-hud.peakrun");
        using (var file = new BinaryWriter(File.Create(path), Encoding.UTF8))
        {
            file.Write(Encoding.ASCII.GetBytes("PEAKRUN1")); file.Write(headerBytes.Length); file.Write(headerBytes);
            file.Write(compressed); file.Write(indexBytes); file.Write((long)indexBytes.Length);
            file.Write(Encoding.ASCII.GetBytes("PEAKEND1"));
        }
        return path;
    }
    private static string Json(object? value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static JObject Token(object value) => JObject.Parse(Json(value));
    private static void EqualState(ReplayHudState? expected, ReplayHudState? actual)
    { Check(JToken.DeepEquals(JToken.Parse(Json(expected)), JToken.Parse(Json(actual))), "Captured HUD state changed across storage or seek."); }
    private static void Near(float actual, float expected) => Check(Math.Abs(actual - expected) < .000001f, $"Expected {expected}, got {actual}.");
    private static ReplayTimelineSample Ready(PagedReplayTimeline timeline, double time)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 5000)
        { if (timeline.TrySample(time, out var sample)) return sample; Thread.Sleep(1); }
        throw new Exception("Timed out waiting for HUD page.");
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is InvalidDataException || e is ArgumentOutOfRangeException || e is JsonException) { return; }
        throw new Exception("Malformed HUD data was accepted.");
    }
    private static void Check(bool condition, string message = "Replay HUD contract assertion failed.")
    { if (!condition) throw new Exception(message); }
    private static void InRoot(Action<string> action)
    {
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string root = Path.GetFullPath(Path.Combine(temp, "peak-hud-contract-" + Guid.NewGuid().ToString("N")));
        Check(root.StartsWith(temp, StringComparison.OrdinalIgnoreCase), "Unsafe test fixture path.");
        Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            // Timeline.Dispose cancels prefetch without blocking the UI thread.
            // On Windows its worker can still be closing the page file when
            // fixture cleanup begins. Allow that bounded teardown to finish.
            var cleanup = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                try { Directory.Delete(root, true); break; }
                catch (IOException) when (cleanup.ElapsedMilliseconds < 2000)
                { System.Threading.Thread.Sleep(10); }
            }
        }
    }
}
