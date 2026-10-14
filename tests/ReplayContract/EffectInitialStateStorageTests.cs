using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class EffectInitialStateStorageTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("effect loop paused and sphere startup history uses proved observation without inferred birth", () =>
        {
            foreach (int kind in new[] { 0, 1, 2 })
            {
                var effect = Effect(); var initial = effect.InitialState!;
                effect.Kind = kind; effect.Key = kind == 0 ? "event-particle:initial:1:1" : kind == 1 ? "event-mesh:initial:1:1" : "event-sphere:initial:1:0";
                effect.Loop = initial.Loop = kind == 0; effect.Paused = initial.Paused = kind != 0;
                if (kind == 2) { effect.Rate = initial.Rate = 0; effect.Phase = initial.Phase = 0; }
                initial.Ended = true; initial.EndTime = 10.026;
                Check(!EffectSceneEventPlayback.IncludeRight(effect, 10, 10.033, 10.0009, true));
                Check(EffectSceneEventPlayback.IncludeRight(effect, 10, 10.033, initial.ObservedAt, true));
                var bracket = new EffectReplayHistorySample(Array.Empty<EffectReplayFrame>(), new[] { effect }, 10, 10.033);
                Check(EffectReplayRules.TryAdmittedBracketState(effect, 10.0009, () => true, _ => bracket, out var before, true) && before == null);
                Check(EffectReplayRules.TryAdmittedBracketState(effect, 10.005, () => true, _ => bracket, out var alive, true) && alive != null);
                Check(EffectReplayRules.TryAdmittedBracketState(effect, 10.026, () => true, _ => bracket, out var after, true) && after == null);
                Check(EffectSceneEventPlayback.Select(effect, 10.0009, true) == null);
                Check(EffectSceneEventPlayback.Select(effect, 10.005, true) != null);
            }
        });
        test("effect observed-start fallback requires controlled new-run key or explicit initial evidence and never admits old loops", () =>
        {
            var loop = new EffectReplayFrame { Key = "event-particle:source:2:1", Loop = true, AnchorTime = .016, Phase = 10 };
            Check(!EffectSceneEventPlayback.IncludeRight(loop, 0, .033, .015, true));
            Check(EffectSceneEventPlayback.IncludeRight(loop, 0, .033, .016, true));
            Check(!EffectSceneEventPlayback.IncludeRight(loop, 0, .033, .02, false));
            Check(!EffectSceneEventPlayback.IncludeRight(loop, 0, .033, .02, true, true));
            loop.Key = "particle:legacy"; Check(!EffectSceneEventPlayback.IncludeRight(loop, 0, .033, .02, true));
            loop.Key = "event-particle:source:no-serial:1"; Check(!EffectSceneEventPlayback.IncludeRight(loop, 0, .033, .02, true));
            loop.Key = "event-mesh:source:2:1"; Check(!EffectSceneEventPlayback.IncludeRight(loop, 0, .033, .02, true));
        });
        test("effect first-state selects birth, terminal, ended and legacy boundaries without history", () =>
        {
            var effect = Effect(); var initial = effect.InitialState!;
            Check(EffectSceneEventPlayback.Select(effect, 10, true) == null);
            var birth = EffectSceneEventPlayback.Select(effect, 10.005, true)!;
            Check(birth.Emitting && birth.Particles![0].EmissionEnabled && !effect.Particles![0].EmissionEnabled);
            Check(ReferenceEquals(birth, EffectSceneEventPlayback.Select(effect, 10.005, true)));
            Check(ReferenceEquals(effect, EffectSceneEventPlayback.Select(effect, initial.UntilTime, true)));
            Check(ReferenceEquals(effect, EffectSceneEventPlayback.Select(effect, 10, false)));
            initial.Ended = true; initial.EndTime = 10.026;
            Check(ReferenceEquals(effect, EffectSceneEventPlayback.Select(effect, 10.023, true)));
            Check(EffectSceneEventPlayback.Select(effect, 10.026, true) == null);
            initial.EndTime = null;
            Check(EffectSceneEventPlayback.Select(effect, initial.UntilTime, true) == null);
        });
        test("effect first-state history admission bridges only measured right births and exact endings", () =>
        {
            var effect = Effect(); var bracket = new EffectReplayHistorySample(Array.Empty<EffectReplayFrame>(), new[] { effect }, 10, 10.033);
            EffectReplayFrame? Sample(double time, bool admitted = true)
            { Check(EffectReplayRules.TryAdmittedBracketState(effect, time, () => admitted, _ => bracket, out var state, true) == admitted); return state; }
            Check(Sample(10) == null && Sample(10.006)!.Emitting && !Sample(10.02)!.Emitting);
            effect.InitialState!.Ended = true; effect.InitialState.EndTime = 10.026;
            Check(Sample(10.025) != null && Sample(10.026) == null && Sample(10.02, false) == null);
            bracket = new(new[] { effect }, new[] { effect }, 10.033, 10.066);
            Check(Sample(10.04) == null);
            effect.InitialState.Ended = false; effect.InitialState.EndTime = null;
            Check(ReferenceEquals(effect, Sample(10.04)));
        });
        test("effect first-state piecewise clocks preserve speed, pause, reverse seek and preroll slices", () =>
        {
            var effect = Effect(); effect.Rate = 2;
            Near(EffectReplayRules.PhaseAt(effect, 10.011), .01);
            Near(EffectReplayRules.PhaseAt(effect, 10.025), .029);
            Near(EffectReplayRules.TimeAtPhase(effect, .01), 10.011);
            Near(EffectReplayRules.TimeAtPhase(effect, .029), 10.025);
            Near(EffectSceneEventPlayback.CutPhase(effect, 0, .04, true), .019);
            Near(EffectSceneEventPlayback.CutPhase(effect, .019, .04, true), .04);
            Near(EffectSceneEventPlayback.CutPhase(effect, 0, .04, false), .04);
            effect.Paused = true;
            Near(EffectReplayRules.PhaseAt(effect, 20), .019);
            Near(EffectReplayRules.TimeAtPhase(effect, .01), 10.011);
            Near(EffectReplayRules.TimeAtPhase(effect, .019), 10.02);
            Near(EffectReplayRules.TimeAtPhase(effect, 100), 10.02);
            Near(EffectReplayRules.PhaseAt(effect, 10.011), .01);
            effect.Rate = 0;
            Check(double.IsFinite(EffectReplayRules.TimeAtPhase(effect, 100)));
        });
        test("effect first-state tree and streaming deltas retain shared immutable evidence then clear it", () =>
        {
            var a = Effect(); var b = EffectSceneEventPlayback.WithInitial(a, a.InitialState!); b.Position = new[] { 3f, 2f, 1f };
            var c = Effect(); c.InitialState = null;
            var writer = new ReplayDeltaCodec.Writer(22); var tree = new ReplayDeltaCodec.Reader(22); var stream = new ReplayDeltaCodec.StreamReader(22);
            EffectReplayFrame? oldTree = null, oldStream = null;
            foreach (var source in new[] { a, a, b, c })
            {
                string json = Json(writer.Encode(Frame(10.033, source))); var x = tree.Decode(JObject.Parse(json)).Effects[0]; var y = stream.DecodeRecord(json)!.Effects[0];
                EffectReplayRules.Validate(x); EffectReplayRules.Validate(y);
                Check((x.InitialState == null) == (source.InitialState == null) && (y.InitialState == null) == (source.InitialState == null));
                if (oldTree != null && source.InitialState != null)
                { Check(ReferenceEquals(oldTree.InitialState, x.InitialState) && ReferenceEquals(oldStream!.InitialState, y.InitialState)); }
                if (source == c) Check(oldTree!.InitialState != null && oldStream!.InitialState != null);
                oldTree = x; oldStream = y;
            }
        });
        test("effect first-state optional null and ended fields preserve older baseline size and defaults", () =>
        {
            var effect = Effect(); effect.InitialState = null;
            var token = Token(effect);
            Check(token["Effects"]![0]!["InitialState"] == null);
            Both(token, f => Check(f.Effects[0].InitialState == null));
            token = Token(Effect()); var dto = (JObject)token["Effects"]![0]!["InitialState"]!;
            dto.Remove("Ended"); dto.Remove("EndTime");
            Both(token, f => Check(!f.Effects[0].InitialState!.Ended && !f.Effects[0].InitialState!.EndTime.HasValue));
        });
        test("effect first-state is schema22 only and cannot be injected into historical codecs", () =>
        {
            for (int schema = 10; schema <= 21; schema++)
            {
                Reject(() => new ReplayDeltaCodec.Writer(schema).Encode(Frame(10.033, Effect())));
                var old = Effect(); old.InitialState = null; old.Particles = null; old.ForceField = null;
                var token = JObject.FromObject(new ReplayDeltaCodec.Writer(schema).Encode(Frame(10.033, old)));
                Check(token["Effects"]![0]!["InitialState"] == null);
                Check(new ReplayDeltaCodec.Reader(schema).Decode(token).Effects[0].InitialState == null);
                Check(new ReplayDeltaCodec.StreamReader(schema).DecodeRecord(Json(token))!.Effects[0].InitialState == null);
                token["Effects"]![0]!["InitialState"] = JValue.CreateNull();
                Reject(() => new ReplayDeltaCodec.Reader(schema).Decode(token));
                Reject(() => new ReplayDeltaCodec.StreamReader(schema).DecodeRecord(Json(token)));
            }
        });
        test("effect first-state refuses incomplete, recursive and oversized payloads in both decoders", () =>
        {
            foreach (string field in new[] { "ObservedAt", "UntilTime", "Position", "Rotation", "Scale", "AnchorTime", "Phase", "Rate", "Loop", "Emitting", "EmissionStopPhase", "Paused", "Seed", "Seeds", "Clip", "ShaderRandom", "Particles", "ForceField" })
            { var token = Token(Effect()); ((JObject)token["Effects"]![0]!["InitialState"]!).Remove(field); RejectBoth(token); }
            foreach (string field in new[] { "InitialState", "Key", "Resource", "Kind" })
            { var token = Token(Effect()); token["Effects"]![0]!["InitialState"]![field] = new JObject(); RejectBoth(token); }
            foreach (string field in new[] { "Seeds", "Particles", "Position" })
            { var token = Token(Effect()); token["Effects"]![0]!["InitialState"]![field] = new JArray(Enumerable.Repeat(field == "Particles" ? (JToken)new JObject() : new JValue(0), 65)); RejectBoth(token); }
        });
        test("effect first-state validation bounds clocks, run seeds and all nested native modules", () =>
        {
            foreach (Action<EffectReplayInitialState> corrupt in new Action<EffectReplayInitialState>[] {
                x => x.ObservedAt = double.NaN, x => x.UntilTime = double.PositiveInfinity, x => x.UntilTime = 10,
                x => x.ObservedAt = 1e10, x => x.AnchorTime = double.NegativeInfinity, x => x.Phase = -1,
                x => x.Rate = 101, x => x.Seed = 8, x => x.Seeds = new[] { 8L }, x => x.Position = new float[4],
                x => x.Rotation = new float[4], x => x.Clip = new string('a', 1025), x => x.EndTime = 10.03,
                x => { x.Ended = true; x.EndTime = 10.01; }, x => { x.Ended = true; x.EndTime = double.NaN; },
                x => x.Particles![0].Parameters[13] = 101, x => x.Particles![0].Colors = new float[9],
                x => x.Particles![0].Parameters[0] = 1.5f,
                x => x.Particles![0].SimulationSpace!.Nodes = new[] { new NodePose() },
                x => x.ForceField!.Gravity = new float[5],
            }) { var value = Effect(); corrupt(value.InitialState!); Reject(() => EffectReplayRules.Validate(value)); }
            foreach (Action<EffectReplayInitialState> future in new Action<EffectReplayInitialState>[] {
                x => x.ObservedAt = x.UntilTime = 10.04, x => x.UntilTime = 10.04,
                x => { x.Ended = true; x.EndTime = 10.04; },
            }) { var value = Effect(); future(value.InitialState!); Reject(() => ReplayRules.Validate(Frame(10.033, value), -1)); }
            var negative = EffectReplayRules.Rebase(Effect(), 20); ReplayRules.Validate(Frame(0, negative), -1);
        });
        test("effect first-state estimates include spaces and forces while queue sharing releases every edge", () =>
        {
            var effect = Effect(); var without = Effect(); without.InitialState = null;
            Check(EffectReplayRules.Estimate(effect) - EffectReplayRules.Estimate(without) == 352 + 8 + 512 + 448 + 320);
            long Queue(bool shared)
            {
                var queue = new ReplayQueueBudget(); var a = Frame(10.033, effect);
                var b = Frame(10.05, shared ? EffectSceneEventPlayback.WithInitial(effect, effect.InitialState!) : Effect());
                Check(queue.TryReserve(a, long.MaxValue, 10, out var first, out _));
                Check(queue.TryReserve(b, long.MaxValue, 10, out var second, out _)); long bytes = queue.Current.Bytes;
                Check(!queue.TryReserve(Frame(10.066, Effect()), 1, 10, out var denied, out _) && denied == null);
                first!.Dispose(); second!.Dispose(); queue.ClearBaseline(); Check(queue.Current.Bytes == 0 && queue.Current.Frames == 0); return bytes;
            }
            Check(Queue(true) < Queue(false));
        });
        test("effect first-state continuous and highlight crops shift clocks once and retain evidence sharing", () =>
        {
            var a = Effect(); a.InitialState!.Ended = true; a.InitialState.EndTime = 10.026;
            var b = EffectSceneEventPlayback.WithInitial(a, a.InitialState); b.Position = new[] { 1f, 0f, 0f };
            var origin = 10.033; var rebaser = new ContinuousReplayRebaser(origin);
            var first = rebaser.Apply(Frame(origin, a)); var next = rebaser.Apply(Frame(origin + .02, b));
            Verify(first, next);
            var buffer = new RollingBuffer(32L * 1024 * 1024, 120); buffer.Add(Frame(origin, a)); buffer.Add(Frame(origin + .02, b));
            var clip = buffer.Snapshot(new ReplayHeader { Scene = "Level_EffectInitial" }, DateTime.UtcNow); Verify(clip.Frames[0], clip.Frames[1]);
            void Verify(ReplayFrame x, ReplayFrame y)
            {
                var initial = x.Effects[0].InitialState!;
                Near(initial.ObservedAt, 10.001 - origin); Near(initial.UntilTime, 10.02 - origin); Near(initial.AnchorTime, 10.001 - origin); Near(initial.EndTime!.Value, 10.026 - origin);
                Check(ReferenceEquals(initial, y.Effects[0].InitialState) && ReferenceEquals(initial.Particles, a.InitialState.Particles));
                Check(ReferenceEquals(initial.Seeds, a.InitialState.Seeds) && a.InitialState.ObservedAt == 10.001);
                ReplayRules.Validate(x, -1); ReplayRules.Validate(y, x.T);
            }
        });
        test("effect first-state deep scene comparison rejects changed evidence under one scene timestamp", () =>
        {
            var a = Frame(10.033, Effect()); a.SceneSampleTimeKnown = true; a.SceneSampleTime = a.T;
            var b = Frame(10.05, Effect()); b.SceneSampleTimeKnown = true; b.SceneSampleTime = a.T;
            Check(ReplayDeltaCodec.SameScene(a, b));
            var builder = new SceneObservationTimeline.Builder(); builder.Observe(a); builder.Observe(b); Check(builder.Build(true).ObservationCount == 1);
            b.Effects[0].InitialState!.ShaderRandom = 3;
            Check(!ReplayDeltaCodec.SameScene(a, b));
            Reject(() => { var bad = new SceneObservationTimeline.Builder(); bad.Observe(a); bad.Observe(b); });
        });
        test("effect first-state effect-only projection retains bounded terminal birth evidence", () =>
        {
            var effect = Effect(); effect.InitialState!.Ended = true; effect.InitialState.EndTime = 10.026;
            var writer = new ReplayDeltaCodec.Writer(22);
            string text = Json(new ReplayHeader { Schema = 22, Scene = "Level_EffectInitial" }) + "\n" + Json(writer.Encode(Frame(10.033, effect))) + "\n{\"Type\":\"end\"}";
            using var input = new StringReader(text); var history = ReplayFiles.ReadEffects(input, allowSingleFrame: true);
            var decoded = history.Frames.Single().Effects[0];
            Check(decoded.InitialState!.Ended && decoded.InitialState.EndTime == 10.026 && decoded.InitialState.Particles![0].EmissionEnabled);
            Check(EffectSceneEventPlayback.Select(decoded, 10.005, true)!.Emitting && EffectSceneEventPlayback.Select(decoded, 10.027, true) == null);
        });
        test("effect first-state independent reverse pages and particle history preserve cold-seek clocks and budget", () => InRoot(root =>
        {
            var first = Effect(); first.InitialState!.Ended = true; first.InitialState.EndTime = 10.026;
            var frames = Enumerable.Range(0, 181).Select(i => Frame(10.033 + i / 60d, EffectSceneEventPlayback.WithInitial(first, first.InitialState))).ToArray();
            var writer = new FullReplayWriter(root, new ReplayHeader { Scene = "Level_EffectInitial" }, new ContinuousReplayOptions { SegmentFrameLimit = 13 });
            foreach (var frame in frames) Check(writer.TryEnqueue(frame));
            var complete = writer.CompleteAsync(); Check(complete.Wait(TimeSpan.FromSeconds(20))); var result = complete.GetAwaiter().GetResult(); Check(result.Status == "completed", result.Fault ?? result.Status);
            var info = FullReplayArchive.ReadInfo(result.FilePath); Check(info.Header.Schema == ReplayRules.CurrentSchema && info.Pages.Length > 2);
            foreach (var page in info.Pages.AsEnumerable().Reverse())
            {
                var clip = FullReplayArchive.ReadPage(result.FilePath, info, page.Index); var history = FullReplayArchive.ReadEffectHistory(result.FilePath, info, page.Index, default);
                foreach (var frame in clip.Frames)
                {
                    var effect = frame.Effects[0]; var initial = effect.InitialState!;
                    Near(initial.ObservedAt, -.032); Near(initial.UntilTime, -.013); Near(initial.EndTime!.Value, -.007);
                    Check(initial.Ended && initial.Particles![0].EmissionEnabled && !effect.Particles![0].EmissionEnabled);
                    Check(page.DecodedBytes >= EffectReplayRules.Estimate(effect));
                    var matched = history.Frames.Single(x => x.T == frame.T).Effects[0]; Near(matched.InitialState!.UntilTime, initial.UntilTime);
                    Near(EffectReplayRules.PhaseAt(matched, -.02), .012);
                }
            }
        }));
    }
    private static EffectReplayFrame Effect()
    {
        var first = new EffectReplayFrame { Key = "event-particle:initial:1:1", Resource = "native-test", AnchorTime = 10.001,
            Seed = 9, Seeds = new[] { 9L }, Particles = new[] { Particle(true) }, ForceField = Force() };
        var current = new EffectReplayFrame { Key = first.Key, Resource = first.Resource, AnchorTime = 10.02, Phase = .019,
            Emitting = false, EmissionStopPhase = .019, Seed = first.Seed, Seeds = first.Seeds, Particles = new[] { Particle(false) }, ForceField = Force() };
        return EffectSceneEventPlayback.WithInitial(current, EffectSceneEventPlayback.Evidence(first, 10.001, 10.02, false));
    }
    private static EffectParticleFrame Particle(bool emitting)
    {
        var parameters = new float[14]; parameters[12] = .1f; parameters[13] = 1;
        return new EffectParticleFrame(new NodePose { Path = "0:particle" }, Enumerable.Repeat(1f, 8).ToArray(), parameters) {
            Emitting = emitting, EmissionEnabled = emitting, SimulationSpace = new ObjectPose(), MeshShapeSpace = new ObjectPose(),
        };
    }
    private static EffectForceFieldFrame Force() => new(new ObjectPose(), new[] { 0f, 1f, 1f, 1f }) { Enabled = true };
    private static ReplayFrame Frame(double time, EffectReplayFrame effect) => new() { T = time, Effects = new[] { effect } };
    private static JObject Token(EffectReplayFrame effect) => JObject.FromObject(new ReplayDeltaCodec.Writer(22).Encode(Frame(10.033, effect)));
    private static string Json(object value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static void Both(JObject token, Action<ReplayFrame> check) { check(new ReplayDeltaCodec.Reader(22).Decode(token)); check(new ReplayDeltaCodec.StreamReader(22).DecodeRecord(Json(token))!); }
    private static void RejectBoth(JObject token) { Reject(() => new ReplayDeltaCodec.Reader(22).Decode(token)); Reject(() => new ReplayDeltaCodec.StreamReader(22).DecodeRecord(Json(token))); }
    private static void Check(bool value, string message = "Effect initial-state assertion failed.") { if (!value) throw new Exception(message); }
    private static void Near(double a, double b) => Check(Math.Abs(a - b) < .00000001, $"Expected {b:R}, got {a:R}.");
    private static void Reject(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException or JsonException or ArgumentOutOfRangeException) { return; } throw new Exception("Invalid effect initial state was accepted."); }
    private static void InRoot(Action<string> action)
    { string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "peak-effect-initial-")); string root = prefix + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(root); try { action(root); } finally { if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unexpected test path."); Directory.Delete(root, true); } }
}
