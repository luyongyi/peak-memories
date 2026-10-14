using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class InteractionReplayTests
{
    public static void Run(Action<string, Action> test)
    {
        test("interaction schema 15 readers agree through nullable gloom and independent payload changes", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(15);
            var tree = new ReplayDeltaCodec.Reader(15); var stream = new ReplayDeltaCodec.StreamReader(15);
            var saved = new List<(ReplayFrame Frame, string Json)>();
            float?[] amounts = { null, 0, .31f, 1, null };
            for (int i = 0; i < amounts.Length; i++)
            {
                var ghost = World(); ghost.GloomAmount = amounts[i];
                var ray = Line(); ray.Line!.Points[3] = i * 10; ray.Line.EndColor[0] = i / 4f;
                var text = Subtitle(); text.Text!.Content = i == 2 ? "<b>原生回答</b>" : "Keep climbing."; text.Text.Color[3] = i / 4f;
                var source = Frame(10 + i, ghost, ray, text);
                var record = Json(writer.Encode(source));
                var expected = tree.Decode(JObject.Parse(record)); var actual = stream.DecodeRecord(record)!;
                Check(Json(actual) == Json(expected), "Typed/tree interaction snapshots differ.");
                Check(actual.Interactions[0].GloomAmount == amounts[i]);
                Check(actual.Interactions[1].Line!.Points[3] == i * 10);
                Check(actual.Interactions[2].Text!.Content == text.Text.Content);
                ReplayRules.Validate(actual, i == 0 ? -1 : 9 + i);
                saved.Add((actual, Json(actual)));
            }
            foreach (var snapshot in saved) Check(Json(snapshot.Frame) == snapshot.Json, "Later patches mutated a prior interaction frame.");
        });
        test("interaction tracks preserve shared states through delta readers", () =>
        {
            var value = new[] { World(), Line(), Subtitle() };
            var writer = new ReplayDeltaCodec.Writer(15); var reader = new ReplayDeltaCodec.StreamReader(15);
            var first = reader.DecodeRecord(Json(writer.Encode(Frame(1, value))))!;
            var second = reader.DecodeRecord(Json(writer.Encode(Frame(2, value))))!;
            Check(ReferenceEquals(first.Interactions, second.Interactions));
            Check(ReferenceEquals(first.Interactions[0], second.Interactions[0]));
        });
        test("schema 15 effect history projects interactions away while preserving particle modules and body integrity", () =>
        {
            var header = new ReplayHeader { Schema = 15, Scene = "Synthetic" };
            var frames = Enumerable.Range(0, 4).Select(i => Frame(i, World(), Line(), Subtitle())).ToArray();
            foreach (var frame in frames)
            {
                frame.Interactions[0].GloomAmount = (float)frame.T / 4;
                frame.Effects = new[] { new EffectReplayFrame { Key = "particle", Resource = "native-particle", Seed = 3,
                    Seeds = new long[] { 3 }, AnchorTime = frame.T, Phase = frame.T,
                    Particles = new[] { new EffectParticleFrame { Pose = new NodePose(), Colors = new float[8],
                        Parameters = new float[EffectReplayRules.ParticleParameterCount], Emitting = true, EmissionEnabled = true } } } };
            }
            frames[2].Interactions = Array.Empty<InteractionReplayFrame>();
            var writer = new ReplayDeltaCodec.Writer(15);
            var records = frames.Select(f => Json(writer.Encode(f))).ToArray();
            string Text(string[] body) => Json(header) + "\n" + string.Join("\n", body) + "\n{\"Type\":\"end\"}\n";
            string text = Text(records); using var pool = new ReplayLineBufferPool();
            ReplayClip Read(bool effects) => ReplayFiles.ReadBuffered(new StringReader(text), pool, default, effects,
                ReplayRules.MaxBytes, FullReplayArchive.MaximumDuration);
            var full = Read(false); var effects = Read(true);
            Check(Json(effects.Frames) == Json(full.Frames.Select(FullReplayArchive.EffectsOnlyFrame)));
            Check(effects.Frames.All(f => f.Interactions.Length == 0 && f.Actors.Length == 0 && f.Effects[0].Particles!.Length == 1));
            Check(effects.Frames[3].Effects[0].Particles![0].Parameters.Length == EffectReplayRules.ParticleParameterCount);
            // The lexical projection must consume the entire discarded payload;
            // a malformed body must not turn into a valid effect-only record.
            var damaged = (string[])records.Clone(); damaged[0] = damaged[0].Replace("Keep climbing.", "Keep\nclimbing.");
            text = Text(damaged); Reject(() => Read(true));
            // Unknown top-level members still reach the ordinary schema reader.
            damaged = (string[])records.Clone(); var unknown = JObject.Parse(damaged[0]); unknown["UnexpectedInteractionPayload"] = 1;
            damaged[0] = Json(unknown); text = Text(damaged); Reject(() => Read(true));
            text = Text(records); Check(Read(false).Frames[0].Interactions.Length == 3);
        });
        test("interaction line bounds are checked before large arrays or invalid colours", () =>
        {
            var value = Line(); value.Line!.Points = new float[InteractionReplayRules.MaximumLinePoints * 3]; value.Line.Widths = new float[InteractionReplayRules.MaximumLinePoints];
            InteractionReplayRules.Validate(value);
            value.Line.Points = new float[(InteractionReplayRules.MaximumLinePoints + 1) * 3]; Reject(() => InteractionReplayRules.Validate(value));
            value = Line(); value.Line!.Widths = Array.Empty<float>(); Reject(() => InteractionReplayRules.Validate(value));
            value = Line(); value.Line!.StartColor[2] = float.NaN; Reject(() => InteractionReplayRules.Validate(value));
            value = Line(); value.Line!.Widths[0] = -1; Reject(() => InteractionReplayRules.Validate(value));
        });
        test("native subtitles are bounded and forbidden on other interaction families", () =>
        {
            var value = Subtitle(); value.Text!.Content = new string('字', 4096); InteractionReplayRules.Validate(value);
            value.Text.Content += "字"; Reject(() => InteractionReplayRules.Validate(value));
            value = Subtitle(); value.Text!.FontSize = float.PositiveInfinity; Reject(() => InteractionReplayRules.Validate(value));
            value = World(); value.Text = new InteractionTextFrame(); Reject(() => InteractionReplayRules.Validate(value));
            value = Subtitle(); value.Text = null; Reject(() => InteractionReplayRules.Validate(value));
            value = Line(); value.GloomAmount = 1; Reject(() => InteractionReplayRules.Validate(value));
        });
        test("interaction validation scratch recovers after malformed duplicate node input", () =>
        {
            var value = World(); value.Pose.Nodes = new[] { new NodePose { Path = "." }, new NodePose { Path = "." } };
            Reject(() => InteractionReplayRules.Validate(value));
            value.Pose.Nodes = new[] { new NodePose { Path = "." } }; InteractionReplayRules.Validate(value);
            for (int i = 0; i < 50; i++) InteractionReplayRules.Validate(value);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) InteractionReplayRules.Validate(value);
            Check(GC.GetAllocatedBytesForCurrentThread() - before < 1024, "Validation allocates scratch per immutable state.");
        });
        test("interaction clocks and generations reject cross-lifetime interpolation", () =>
        {
            var a = World(); var b = World(); Check(InteractionReplayRules.CanInterpolate(a, b));
            b.Key += ":next"; Check(!InteractionReplayRules.CanInterpolate(a, b));
            b = World(); b.SourcePath += "/other"; Check(!InteractionReplayRules.CanInterpolate(a, b));
            b = World(); b.Pose.Active = false; Check(!InteractionReplayRules.CanInterpolate(a, b));
            Check(InteractionReplayRules.ShouldProbe(1, 99, true));
            Check(!InteractionReplayRules.ShouldProbe(1, 99, false));
            Check(Math.Abs(InteractionReplayRules.NextProbe(1, 100, true) - 1 - 1d / 30) < 1e-9);
            Check(InteractionReplayRules.NextProbe(1, 100, false) != InteractionReplayRules.NextProbe(1, 101, false));
        });
        test("scene bridge visual collapse survives zero collision points and separate budgets", () =>
        {
            var bridge = Bridge(); bridge.BreakAmount = .8f; bridge.Points = Array.Empty<RopeReplayPoint>();
            RopeReplayRules.Validate(bridge); Check(bridge.Visible && bridge.BreakAmount == .8f);
            var all = Enumerable.Range(0, RopeReplayRules.MaximumSceneVines).Select(i => Bridge("bridge:" + i))
                .Concat(Enumerable.Range(0, RopeReplayRules.MaximumEntities).Select(i => new RopeReplayFrame { Key = "rope:" + i, Kind = "rope", Resource = "RopeDynamic" })).ToArray();
            RopeReplayRules.ValidateCounts(all);
            Reject(() => RopeReplayRules.ValidateCounts(all.Concat(new[] { Bridge("extra") }).ToArray()));
            Reject(() => RopeReplayRules.ValidateCounts(all.Concat(new[] { new RopeReplayFrame() }).ToArray()));
            bridge.Kind = "rope"; Reject(() => RopeReplayRules.Validate(bridge));
        });
        test("schema 14 cannot claim interactions or shader bridge collapse", () =>
        {
            var frame = Frame(1, World()); Reject(() => ReplayRules.ValidateVersion(frame, 14));
            var encoded = Json(new ReplayDeltaCodec.Writer(15).Encode(frame));
            Reject(() => new ReplayDeltaCodec.Reader(14).Decode(JObject.Parse(encoded)));
            Reject(() => new ReplayDeltaCodec.StreamReader(14).DecodeRecord(encoded));
            frame = Frame(1); frame.Ropes = new[] { Bridge() }; Reject(() => ReplayRules.ValidateVersion(frame, 14));
            frame.Ropes[0].SceneVine = false; frame.Ropes[0].BreakAmount = .5f; Reject(() => ReplayRules.ValidateVersion(frame, 14));
        });
        test("bridge break amount and scene classification round trip without changing old defaults", () =>
        {
            var frame = Frame(1); frame.Ropes = new[] { Bridge() }; frame.Ropes[0].BreakAmount = .75f;
            var encoded = Json(new ReplayDeltaCodec.Writer(15).Encode(frame));
            var stream = new ReplayDeltaCodec.StreamReader(15).DecodeRecord(encoded)!;
            var tree = new ReplayDeltaCodec.Reader(15).Decode(JObject.Parse(encoded));
            Check(Json(stream) == Json(tree) && stream.Ropes[0].SceneVine && stream.Ropes[0].BreakAmount == .75f);
            frame.Ropes[0].SceneVine = false; frame.Ropes[0].BreakAmount = 0;
            encoded = Json(new ReplayDeltaCodec.Writer(14).Encode(frame));
            stream = new ReplayDeltaCodec.StreamReader(14).DecodeRecord(encoded)!;
            Check(!stream.Ropes[0].SceneVine && stream.Ropes[0].BreakAmount == 0);
        });
        test("interaction memory accounts once for shared immutable states and crop preserves them", () =>
        {
            var states = new[] { World(), Line(), Subtitle() }; var first = Frame(500, states); var next = Frame(501, states);
            var budget = new ContinuousReplayBudget(); long initial = budget.Additional(first); budget.Commit(first, initial);
            Check(initial - budget.Additional(next) >= states.Sum(InteractionReplayRules.Estimate));
            var shared = new RollingBuffer(); shared.Add(first); long before = shared.EstimatedBytes; shared.Add(next);
            Check(shared.EstimatedBytes - before == RollingBuffer.EstimateFrame(next));
            var clip = shared.Snapshot(new ReplayHeader { Scene = "Synthetic" }, DateTime.UtcNow);
            Check(clip.Frames.Count == 2 && clip.Frames[0].T == 0 && clip.Frames[1].T == 1);
            Check(ReferenceEquals(clip.Frames[0].Interactions, states) && ReferenceEquals(clip.Frames[1].Interactions, states));
            var rebaser = new ContinuousReplayRebaser(500); Check(ReferenceEquals(rebaser.Apply(first).Interactions, states));
        });
        test("interaction removal and reintroduction is charged again by decoder memory budget", () =>
        {
            var value = World(); var budget = new ContinuousReplayBudget();
            var first = Frame(1, value); budget.Commit(first, budget.Additional(first));
            var empty = Frame(2); budget.Commit(empty, budget.Additional(empty));
            var returned = Frame(3, value);
            Check(budget.Additional(returned) == RollingBuffer.EstimateFrame(returned) + InteractionReplayRules.Estimate(value));
        });
        test("native post weights and exact enabled states survive both readers and unknown transitions", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(15); var tree = new ReplayDeltaCodec.Reader(15); var stream = new ReplayDeltaCodec.StreamReader(15);
            var snapshots = new List<(ReplayFrame Frame, string Saved)>();
            for (int i = 0; i < 5; i++)
            {
                var post = Post(); if (i != 0 && i != 4) { post.PostWeights = new[] { i * .25f, i == 2 ? 1.02f : 0 }; post.PostEnabled = new[] { i == 1, i == 2 }; }
                var json = Json(writer.Encode(Frame(i, post))); var a = tree.Decode(JObject.Parse(json)); var b = stream.DecodeRecord(json)!;
                ReplayRules.Validate(a, i - 1); Check(Json(a) == Json(b));
                Check((a.Interactions[0].PostWeights == null) == (i == 0 || i == 4));
                if (i == 2) Check(a.Interactions[0].PostWeights![0] == .5f && !a.Interactions[0].PostEnabled![0]);
                snapshots.Add((a, Json(a)));
            }
            foreach (var saved in snapshots) Check(Json(saved.Frame) == saved.Saved);
        });
        test("post payload is bounded to audited families and immutable budget sharing includes both arrays", () =>
        {
            var post = Post(); post.PostWeights = new[] { .5f, 1f }; post.PostEnabled = new[] { true, false }; InteractionReplayRules.Validate(post);
            long full = InteractionReplayRules.Estimate(post); var unknown = Post(); Check(full - InteractionReplayRules.Estimate(unknown) == 64);
            var first = Frame(1, post); var second = Frame(2, post); var budget = new ContinuousReplayBudget(); budget.Commit(first, budget.Additional(first));
            Check(budget.Additional(second) == RollingBuffer.EstimateFrame(second));
            post.PostEnabled = null; Reject(() => InteractionReplayRules.Validate(post)); post.PostEnabled = new[] { true, true, true }; Reject(() => InteractionReplayRules.Validate(post));
            post.PostEnabled = new[] { true, true }; post.PostWeights = new[] { float.NaN, 0 }; Reject(() => InteractionReplayRules.Validate(post));
            post.PostWeights = new[] { 0f, 0f, 0f }; Reject(() => InteractionReplayRules.Validate(post));
            post.PostWeights = new[] { 0f, 0f }; post.Kind = "campfire"; Reject(() => InteractionReplayRules.Validate(post));
            post = Post(); Reject(() => ReplayRules.ValidateVersion(Frame(1, post), 14));
        });
        test("campfire records actual alternating child visibility without inventing a particle or lighting state", () =>
        {
            var fire = new InteractionReplayFrame { Key = "fire:1", Kind = "campfire", Resource = "campfire", SourcePath = "Map/0:Campfire",
                Pose = new ObjectPose { Nodes = new[] { new NodePose { Path = "0:EnableWhenLit", Active = false }, new NodePose { Path = "1:DisableWhenLit", Active = true } } } };
            InteractionReplayRules.Validate(fire); var writer = new ReplayDeltaCodec.Writer(15); var reader = new ReplayDeltaCodec.StreamReader(15);
            var before = reader.DecodeRecord(Json(writer.Encode(Frame(1, fire))))!;
            var lit = JsonConvert.DeserializeObject<InteractionReplayFrame>(Json(fire))!; lit.Pose.Nodes[0].Active = true; lit.Pose.Nodes[1].Active = false;
            var after = reader.DecodeRecord(Json(writer.Encode(Frame(2, lit))))!;
            Check(!before.Interactions[0].Pose.Nodes[0].Active && before.Interactions[0].Pose.Nodes[1].Active);
            Check(after.Interactions[0].Pose.Nodes[0].Active && !after.Interactions[0].Pose.Nodes[1].Active);
            Check(after.Interactions[0].PostWeights == null && after.Effects.Length == 0);
        });
    }
    private static InteractionReplayFrame Post() => new() { Key = "post:1", Kind = "peak-portal", Resource = "peak-portal", SourcePath = "Map/0:Portal" };
    private static InteractionReplayFrame World() => new() { Key = "world:1", Kind = "ghost-fire", Resource = "ghost-fire", SourcePath = "Map/0:GhostFire" };
    private static InteractionReplayFrame Line() => new() { Key = "line:1", Kind = "clone-line", Resource = "item/1", Line = new InteractionLineFrame { Visible = true, Points = new[] { 1f, 2f, 3f, 4f, 5f, 6f }, Widths = new[] { .1f, .1f } } };
    private static InteractionReplayFrame Subtitle() => new() { Key = "text:1", Kind = "bing-subtitle", Resource = "item/2", Text = new InteractionTextFrame { Visible = true, Content = "Keep climbing.", FontSize = 3 } };
    private static RopeReplayFrame Bridge(string key = "bridge:1") => new() { Key = key, Kind = "vine", Resource = "JungleVine", SourcePath = "Map/0:Bridge", SceneVine = true, Visible = true };
    private static ReplayFrame Frame(double t, params InteractionReplayFrame[] interactions) => new() { T = t, Actors = new[] { new ActorFrame { Id = "scout" } }, Interactions = interactions };
    private static string Json(object value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static void Check(bool value, string message = "Interaction assertion failed.") { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception e) when (e is InvalidDataException || e is JsonException || e is ArgumentException) { return; } throw new Exception("Invalid interaction was accepted."); }
}
