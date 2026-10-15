using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class FaunaNativeAnimationStorageTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("raven flying clip clamps instead of looping through absolute reverse seeks", () =>
        {
            var flying = Clock(CreatureIdleAnimationRules.RavenFlyAway, 100, .25);
            CreatureIdleAnimationRules.Validate(flying);
            foreach (double time in new[] { 105d, 99, 100.5, 101, 150, 100.5 })
                Equal(CreatureIdleAnimationRules.SampleTime(flying, time), Math.Clamp(.25 + time - 100, 0, 3));
            flying.ClipTime = 3; CreatureIdleAnimationRules.Validate(flying);
            foreach (var clip in new[] { CreatureIdleAnimationRules.RavenIdle, CreatureIdleAnimationRules.FrogIdle })
            {
                var idle = Clock(clip, 100, .25); CreatureIdleAnimationRules.Validate(idle);
                double length = CreatureIdleAnimationRules.ClipLength(clip);
                Equal(CreatureIdleAnimationRules.SampleTime(idle, 99), ((.25 - 1) % length + length) % length);
                idle.ClipTime = length; Reject(() => CreatureIdleAnimationRules.Validate(idle));
                idle.ClipTime = 0; idle.RopePhase = .1f; Reject(() => CreatureIdleAnimationRules.Validate(idle));
            }
            flying.ClipTime = 3.000001; Reject(() => CreatureIdleAnimationRules.Validate(flying));
            flying.ClipTime = 0; flying.RopePhase = .1f; Reject(() => CreatureIdleAnimationRules.Validate(flying));
        });
        test("schema24 item and raven animation survive tree and typed stream state transitions", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(24); var tree = new ReplayDeltaCodec.Reader(24);
            var stream = new ReplayDeltaCodec.StreamReader(24);
            foreach (var source in new[] { Frame(0, Frog(0), Raven(0, false)), Frame(1, Frog(0), Raven(1, true)),
                Frame(2, Frog(2).WithIdleAnimation(null), Raven(1, true)), Frame(3, Frog(3), Raven(3, false)) })
            {
                string json = Json(writer.Encode(source)); var left = tree.Decode(JObject.Parse(json)); var right = stream.DecodeRecord(json)!;
                ReplayRules.Validate(left, -1); ReplayRules.Validate(right, -1);
                Check(Json(left) == Json(right));
                Check(Json(left.Items[0].IdleAnimation) == Json(source.Items[0].IdleAnimation));
                Check(Json(left.Creatures[0].IdleAnimation) == Json(source.Creatures[0].IdleAnimation));
            }
        });
        test("older schema item baselines keep exact omitted field and native limits", () =>
        {
            Check(ReplayRules.CurrentSchema == 27 && ReplayRules.SupportedSchema(25) && ReplayRules.SupportedSchema(26) && !ReplayRules.SupportedSchema(9));
            foreach (int schema in Enumerable.Range(10, 14))
            {
                var plain = Frog(0).WithIdleAnimation(null); var token = JObject.FromObject(new ReplayDeltaCodec.Writer(schema).Encode(Frame(0, plain)));
                Check(token["Items"]![0]!["IdleAnimation"] == null);
                Check(new ReplayDeltaCodec.Reader(schema).Decode(token).Items[0].IdleAnimation == null);
                Check(new ReplayDeltaCodec.StreamReader(schema).DecodeRecord(token.ToString(Formatting.None))!.Items[0].IdleAnimation == null);
                Reject(() => new ReplayDeltaCodec.Writer(schema).Encode(Frame(0, Frog(0))));
            }
            foreach (int schema in Enumerable.Range(21, 3))
                foreach (bool fly in new[] { false, true })
                    Reject(() => new ReplayDeltaCodec.Writer(schema).Encode(Frame(0, null, Raven(0, fly))));
            var legacy = Frog(0).WithIdleAnimation(null); long old = RollingBuffer.Estimate(legacy);
            Check(RollingBuffer.Estimate(Frog(0)) - old == 128 + CreatureIdleAnimationRules.FrogIdle.Length * 2);
            Check(CreatureReplayRules.MaximumDynamicEntities == 256 && CreatureReplayRules.MaximumStaticTraps == 8192 && ReplayRules.MaxItems == 1024);
        });
        test("animation descriptor baselines and species gates reject malformed mixed clocks", () =>
        {
            var encoded = JObject.FromObject(new ReplayDeltaCodec.Writer(24).Encode(Frame(0, Frog(0), Raven(0, false))));
            foreach (string field in new[] { "Clip", "AnchorTime", "ClipTime", "Speed", "RopePhase" })
            {
                foreach (string track in new[] { "Items", "Creatures" })
                {
                    var bad = (JObject)encoded.DeepClone(); ((JObject)bad[track]![0]!["IdleAnimation"]!).Remove(field);
                    RejectBoth(bad);
                }
            }
            foreach (Action<ItemFrame> corrupt in new Action<ItemFrame>[]
            { x => x.ItemId = 1, x => x.IdleAnimation!.Clip = CreatureIdleAnimationRules.RavenIdle, x => x.IdleAnimation!.RopePhase = .5f })
            { var bad = Frog(0); corrupt(bad); Reject(() => ReplayRules.Validate(Frame(0, bad), -1)); }
            foreach (Action<CreatureReplayFrame> corrupt in new Action<CreatureReplayFrame>[]
            { x => x.Kind = x.Resource = "condor", x => x.State = 1, x => x.ClockMode = CreatureReplayRules.ActiveClock,
              x => x.IdleAnimation!.Clip = CreatureIdleAnimationRules.FrogIdle, x => x.IdleAnimation!.AnchorTime = 1,
              x => x.Line = new CreatureReplayLine() })
            { var bad = Raven(0, false); corrupt(bad); Reject(() => CreatureReplayRules.Validate(bad)); }
            foreach (int schema in new[] { 21, 22, 23 })
            {
                Reject(() => new ReplayDeltaCodec.Reader(schema).Decode((JObject)encoded.DeepClone()));
                Reject(() => new ReplayDeltaCodec.StreamReader(schema).DecodeRecord(encoded.ToString(Formatting.None)));
                var ravenOnly = JObject.FromObject(new ReplayDeltaCodec.Writer(24).Encode(Frame(0, null, Raven(0, false))));
                Reject(() => new ReplayDeltaCodec.Reader(schema).Decode(ravenOnly));
                Reject(() => new ReplayDeltaCodec.StreamReader(schema).DecodeRecord(ravenOnly.ToString(Formatting.None)));
            }
        });
        test("frog clocks rebase once through bounded shared caches and clip snapshots without source mutation", () =>
        {
            const double start = 12345.123456789; var frog = Frog(start - .123456789);
            var items = new[] { frog }; var source = Frame(start, frog); source.Items = items;
            string before = Json(source); var rebase = new ContinuousReplayRebaser(start);
            var a = rebase.Apply(source); var b = rebase.Apply(source); var next = Frame(start + .5, frog); next.Items = items;
            Check(ReferenceEquals(a.Items, b.Items) && ReferenceEquals(a.Items, rebase.Apply(next).Items));
            Check(!ReferenceEquals(a.Items[0], frog) && ReferenceEquals(a.Items[0].Pose, frog.Pose));
            Check(a.Items[0].IdleAnimation!.AnchorTime == frog.IdleAnimation!.AnchorTime - start);
            foreach (double time in new[] { start, start + .000001, start + .5, start + 500 })
                Equal(CreatureIdleAnimationRules.SampleTime(frog.IdleAnimation, time), CreatureIdleAnimationRules.SampleTime(a.Items[0].IdleAnimation!, time - start));
            Check(Json(source) == before);
            var buffer = new RollingBuffer(); buffer.Add(source); buffer.Add(next);
            var clip = buffer.Snapshot(new ReplayHeader { Scene = "Level_FrogClock" }, DateTime.UtcNow);
            Check(ReferenceEquals(clip.Frames[0].Items, clip.Frames[1].Items));
            Equal(clip.Frames[0].Items[0].IdleAnimation!.AnchorTime, frog.IdleAnimation.AnchorTime - start);
            Check(Json(source) == before);
            var plain = Frog(0).WithIdleAnimation(null);
            Check(ReferenceEquals(plain, ItemAnimationTimeline.Rebase(plain, 100)) && ReferenceEquals(frog, ItemAnimationTimeline.Rebase(frog, 0)));
        });
        test("item clock rebase caches remain bounded while hot state identity survives cold array churn", () =>
        {
            const double origin = 100; var frog = Frog(90); var plain = Frog(0).WithIdleAnimation(null);
            string before = Json(frog); var source = new[] { plain, frog }; var rebaser = new ItemAnimationRebaser(origin);
            var shifted = rebaser.Apply(source);
            Check(ReferenceEquals(shifted, rebaser.Apply(source)) && ReferenceEquals(shifted[0], plain));
            for (int i = 0; i < ItemAnimationRebaser.MaximumCachedStates + ItemAnimationRebaser.MaximumCachedArrays + 10; i++)
            {
                var cold = Frog(origin + i); var translated = rebaser.Apply(new[] { cold });
                Equal(translated[0].IdleAnimation!.AnchorTime, i);
                var hot = rebaser.Apply(new[] { frog }); Check(ReferenceEquals(hot[0], shifted[1]));
                Check(rebaser.CachedStates <= ItemAnimationRebaser.MaximumCachedStates && rebaser.CachedArrays <= ItemAnimationRebaser.MaximumCachedArrays);
            }
            Check(rebaser.CachedStates == ItemAnimationRebaser.MaximumCachedStates && rebaser.CachedArrays == ItemAnimationRebaser.MaximumCachedArrays);
            Check(Json(frog) == before && ReferenceEquals(shifted[1].Pose, frog.Pose));
            // The cold source array may have been evicted; the hot immutable
            // item still retains its translated identity and original clock.
            var again = rebaser.Apply(source); Check(ReferenceEquals(again[1], shifted[1]));
            var otherOrigin = new ItemAnimationRebaser(105).Apply(source);
            Equal(otherOrigin[1].IdleAnimation!.AnchorTime, -15);
            Check(!ReferenceEquals(otherOrigin[1], shifted[1]) && ReferenceEquals(otherOrigin[1].Pose, frog.Pose));
        });
        test("legacy item arrays retain identity and evicted frog clocks retranslate from the immutable source", () =>
        {
            var rebaser = new ItemAnimationRebaser(10); var legacy = new[] { Frog(0).WithIdleAnimation(null) };
            Check(ReferenceEquals(legacy, rebaser.Apply(legacy)) && rebaser.CachedStates == 0);
            var frog = Frog(9); var source = new[] { frog }; var first = rebaser.Apply(source);
            for (int i = 0; i <= ItemAnimationRebaser.MaximumCachedStates; i++) rebaser.Apply(new[] { Frog(11 + i) });
            var again = rebaser.Apply(source);
            Check(!ReferenceEquals(first[0], again[0]));
            Equal(first[0].IdleAnimation!.AnchorTime, -1); Equal(again[0].IdleAnimation!.AnchorTime, -1); Equal(frog.IdleAnimation!.AnchorTime, 9);
            Check(ReferenceEquals(again[0].Pose, frog.Pose) && ReferenceEquals(legacy, rebaser.Apply(legacy)));
            Check(ReferenceEquals(source, new ItemAnimationRebaser(0).Apply(source)));
        });
        test("frog and raven shared snapshots remain independent across reverse cold page seeks", () => InRoot(root =>
        {
            const double origin = 1000.123456789; var frog = Frog(origin - .4); var idle = Raven(origin - .7, false);
            var flight = Raven(origin + 1.25, true); var frames = new List<ReplayFrame>();
            for (int i = 0; i <= 48; i++) frames.Add(Frame(origin + i / 8d, frog, i < 10 ? idle : flight));
            var writer = new FullReplayWriter(root, new ReplayHeader { Scene = "Level_FaunaClock" });
            foreach (var frame in frames) Check(writer.TryEnqueue(frame));
            var result = writer.CompleteAsync().GetAwaiter().GetResult(); Check(result.Status == "completed" && result.WrittenFrames == frames.Count);
            var info = FullReplayArchive.ReadInfo(result.FilePath); Check(info.Version == 4 && info.Header.Schema == ReplayRules.CurrentSchema && info.Pages.Length >= 5);
            foreach (var page in info.Pages.AsEnumerable().Reverse())
            {
                var decoded = FullReplayArchive.ReadPage(result.FilePath, info, page.Index);
                foreach (var frame in decoded.Frames)
                {
                    int index = (int)Math.Round(frame.T * 8); var expected = frames[index];
                    Equal(frame.Items[0].IdleAnimation!.AnchorTime, frog.IdleAnimation!.AnchorTime - origin);
                    Equal(CreatureIdleAnimationRules.SampleTime(frame.Items[0].IdleAnimation!, frame.T), CreatureIdleAnimationRules.SampleTime(frog.IdleAnimation, expected.T));
                    var raven = frame.Creatures[0]; Check(raven.IdleAnimation!.Clip == expected.Creatures[0].IdleAnimation!.Clip);
                    Equal(CreatureIdleAnimationRules.SampleTime(raven.IdleAnimation, frame.T), CreatureIdleAnimationRules.SampleTime(expected.Creatures[0].IdleAnimation!, expected.T));
                }
            }
            Check(frog.IdleAnimation!.AnchorTime == origin - .4 && idle.SampleTime == origin - .7 && flight.SampleTime == origin + 1.25);
        }));
        test("raven resource checkpoints cut fallback geometry and remain deterministic after backwards selection", () =>
        {
            var idle = Raven(9, false); var active = Raven(10.1, false); var next = Raven(10.2, false);
            foreach (var sample in new[] { active, next })
            { sample.ClockMode = CreatureReplayRules.ActiveClock; sample.IdleAnimation = null; }
            active.Pose.Position[0] = 1; next.Pose.Position[0] = 2;
            var fly = Raven(10.3, true); var builder = new CreatureObservationTimeline.Builder();
            foreach (var frame in new[] { Frame(10, null, idle), Frame(10.1, null, active), Frame(10.2, null, next), Frame(10.3, null, fly), Frame(10.4, null, fly) }) builder.Observe(frame);
            var timeline = builder.Build(false);
            foreach (double time in new[] { 10.35, 10.05, 10.15, 10.299, 10.35, 10.05 })
            {
                Check(timeline.TrySample(idle.Key, time, out var left, out var right, out float mix));
                if (time < 10.1) Check(ReferenceEquals(left, idle) && ReferenceEquals(left, right) && mix == 0);
                else if (time < 10.2) { Check(ReferenceEquals(left, active) && ReferenceEquals(right, next)); Check(Math.Abs(mix - .5f) < .00001); }
                else if (time < 10.3) Check(ReferenceEquals(left, next) && ReferenceEquals(left, right) && mix == 0);
                else Check(ReferenceEquals(left, fly) && ReferenceEquals(left, right) && mix == 0);
            }
            var cold = new CreatureObservationTimeline.Builder(); cold.Observe(Frame(100, null, fly)); var coldPage = cold.Build(false);
            Check(coldPage.CanSample(new[] { fly }, 100) && coldPage.TrySample(fly.Key, 100, out var held, out _, out _));
            Equal(CreatureIdleAnimationRules.SampleTime(fly.IdleAnimation!, 100), 3);
        });
        test("held scene clocks cannot conceal changed frog or raven animation descriptors", () =>
        {
            foreach (bool frog in new[] { true, false })
            {
                var first = Frame(1, Frog(0), Raven(0, false)); first.SceneSampleTimeKnown = true; first.SceneSampleTime = 1;
                var changed = Frame(1.01, frog ? Frog(.25) : first.Items[0], frog ? first.Creatures[0] : Raven(.25, false));
                changed.SceneSampleTimeKnown = true; changed.SceneSampleTime = 1;
                var builder = new SceneObservationTimeline.Builder(); builder.Observe(first);
                Reject(() => builder.Observe(changed));
            }
        });
    }
    private static CreatureIdleAnimation Clock(string clip, double anchor, double time = .25) => new() { Clip = clip, AnchorTime = anchor, ClipTime = time, Speed = 1 };
    private static ItemFrame Frog(double time) => new() { Key = "frog-172", ItemId = 172, IdleAnimation = Clock(CreatureIdleAnimationRules.FrogIdle, time) };
    private static CreatureReplayFrame Raven(double time, bool flying) => new()
    { Key = "raven-native", Kind = "voice-thief", Resource = "voice-thief", State = flying ? 1 : 0,
      SampleTimeKnown = true, SampleTime = time, ClockMode = CreatureReplayRules.IdleClock,
      IdleAnimation = Clock(flying ? CreatureIdleAnimationRules.RavenFlyAway : CreatureIdleAnimationRules.RavenIdle, time) };
    private static ReplayFrame Frame(double time, ItemFrame? frog = null, CreatureReplayFrame? raven = null) => new()
    { T = time, Items = frog == null ? Array.Empty<ItemFrame>() : new[] { frog }, Creatures = raven == null ? Array.Empty<CreatureReplayFrame>() : new[] { raven } };
    private static void RejectBoth(JObject value)
    { Reject(() => new ReplayDeltaCodec.Reader().Decode((JObject)value.DeepClone())); Reject(() => new ReplayDeltaCodec.StreamReader(ReplayRules.CurrentSchema).DecodeRecord(value.ToString(Formatting.None))); }
    private static string Json(object? value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static void Equal(double actual, double expected) => Check(Math.Abs(actual - expected) < 1e-9);
    private static void Check(bool value) { if (!value) throw new Exception("Native fauna animation storage assertion failed."); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception e) when (e is InvalidDataException || e is JsonException) { return; } throw new Exception("Invalid native fauna animation was accepted."); }
    private static void InRoot(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "PeakFaunaClock-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "PeakFaunaClock-"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected fauna clock test directory.");
            Directory.Delete(root, true);
        }
    }
}
