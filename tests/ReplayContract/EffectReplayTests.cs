using PeakReplayLab;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class EffectReplayTests
{
    public static void Run(Action<string, Action> test)
    {
        test("native sphere shape ignores stale GhostBall renderer union reference", () =>
        {
            Check(!EffectReplayRules.UsesRendererShape(0)); // actual FlameDie Sphere with a stale MeshRenderer pointer
            Check(EffectReplayRules.UsesRendererShape(13)); // actual Flame mesh
            Check(EffectReplayRules.UsesRendererShape(14)); // unsupported character/chicken skin must still be diagnosed
            Check(EffectReplayRules.UsesRendererShape(20));
            foreach (int shape in new[] { 1, 2, 4, 5, 6, 10, 12, 17, 18, 19 }) Check(!EffectReplayRules.UsesRendererShape(shape));
        });
        test("recorded static mesh surface preserves world history across seeks crop and null transitions", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(15); var tree = new ReplayDeltaCodec.Reader(15); var stream = new ReplayDeltaCodec.StreamReader(15);
            var history = new List<EffectReplayFrame>();
            for (int tick = 0; tick < 4; tick++)
            {
                var effect = Modules(); effect.Particles![0].MeshShapeSpace = tick == 0 || tick == 3 ? null : new ObjectPose
                { Position = new[] { tick * 4f, 2f, 3f }, Rotation = new[] { 0f, .6f, 0f, .8f }, Scale = new[] { 2f, 2f, 2f } };
                string line = Json(writer.Encode(new ReplayFrame { T = 10 + tick, Effects = new[] { effect } }));
                var decoded = stream.DecodeRecord(line)!; Check(Json(decoded) == Json(tree.Decode(JObject.Parse(line))));
                Check(Json(decoded.Effects[0]) == Json(effect)); EffectReplayRules.Validate(decoded.Effects[0]); history.Add(decoded.Effects[0]);
            }
            Check(history[0].Particles![0].MeshShapeSpace == null && history[3].Particles![0].MeshShapeSpace == null);
            Check(history[1].Particles![0].MeshShapeSpace!.Position[0] == 4 && history[2].Particles![0].MeshShapeSpace!.Position[0] == 8);
            var cropped = EffectReplayRules.Rebase(history[1], 11);
            Check(ReferenceEquals(cropped.Particles![0].MeshShapeSpace, history[1].Particles![0].MeshShapeSpace));
            Check(EffectReplayRules.TimeAtPhase(cropped, 0) == EffectReplayRules.TimeAtPhase(history[1], 0) - 11);
        });
        test("mesh emission surfaces reject unbounded hierarchies and include only new nullable bytes", () =>
        {
            var effect = Modules(); long legacy = EffectReplayRules.Estimate(effect);
            effect.Particles![0].MeshShapeSpace = new ObjectPose(); Check(EffectReplayRules.Estimate(effect) == legacy + 224);
            effect.Particles[0].MeshShapeSpace!.Nodes = new[] { new NodePose() }; Bad(effect);
            effect.Particles[0].MeshShapeSpace = new ObjectPose { Position = new[] { float.NaN, 0f, 0f } }; Bad(effect);
            effect.Particles[0].MeshShapeSpace = new ObjectPose { Rotation = new float[4] }; Bad(effect);
            effect.Particles[0].MeshShapeSpace = null; Check(EffectReplayRules.Estimate(effect) == legacy);
        });
        test("native constant color ignores uninitialized inactive minimum slots", () =>
        {
            // Exact invalid Campfire_Flame native minimum from the first 0.8.10
            // capture. Its active maximum is ordinary opaque white.
            var colors = new[] { -1.061874E37f, 4.590794E-41f, 8.96831E-44f, 0f, 1f, 1f, 1f, 1f };
            EffectReplayRules.CanonicalizeParticleColors(0, colors);
            Check(colors.All(x => x == 1));
            var effect = Modules(); effect.Particles![0].ColorMode = 0; effect.Particles[0].Colors = colors; EffectReplayRules.Validate(effect);
            for (int mode = 1; mode <= 4; mode++)
            {
                var values = Enumerable.Repeat(float.NaN, 8).ToArray();
                if (mode == 2) values = Enumerable.Range(0, 8).Select(x => (float)x).ToArray();
                EffectReplayRules.CanonicalizeParticleColors(mode, values);
                Check(mode == 2 ? values.SequenceEqual(Enumerable.Range(0, 8).Select(x => (float)x)) : values.All(x => x == 0));
            }
        });
        test("native curve modes preserve used values and canonicalize ignored union slots", () =>
        {
            var constant = new[] { 0f, float.NaN, 50f, float.PositiveInfinity };
            EffectReplayRules.CanonicalizeParticleCurve(0, constant); Check(constant.SequenceEqual(new[] { 0f, 50f, 50f, 1f }));
            var pair = new[] { 3f, .5f, 1f, float.NegativeInfinity };
            EffectReplayRules.CanonicalizeParticleCurve(3, pair); Check(pair.SequenceEqual(new[] { 3f, .5f, 1f, 1f }));
            foreach (int mode in new[] { 1, 2 })
            {
                var curve = new[] { (float)mode, float.NaN, float.PositiveInfinity, 2.5f };
                EffectReplayRules.CanonicalizeParticleCurve(mode, curve); Check(curve.SequenceEqual(new[] { (float)mode, 0f, 0f, 2.5f }));
            }
        });
        test("schema 15 particle parameters agree in both readers across changes and null transitions", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(15); var tree = new ReplayDeltaCodec.Reader(15); var stream = new ReplayDeltaCodec.StreamReader(15);
            var saved = new List<(ReplayFrame Frame, string Json)>();
            for (int i = 0; i < 7; i++)
            {
                var effect = Modules();
                if (i == 0 || i == 6) { effect.Particles = null; effect.ForceField = null; }
                else
                {
                    var p = effect.Particles![0]; p.Colors[0] = i / 7f; p.Parameters[12] = i * 10; p.MaterialVariant = i % 2;
                    p.EmissionEnabled = i % 2 == 0; p.Paused = i == 3; p.Pose.Active = i != 4;
                    if (i % 2 == 0) { p.SimulationSpace = null; effect.ForceField = null; }
                    else { p.SimulationSpace!.Position[1] = i; effect.ForceField!.Gravity[1] = -i; }
                }
                string record = Json(writer.Encode(new ReplayFrame { T = 10 + i, Effects = new[] { effect } }));
                var expected = tree.Decode(JObject.Parse(record)); var actual = stream.DecodeRecord(record)!;
                Check(Json(actual) == Json(expected)); Check(Json(actual.Effects[0]) == Json(effect));
                EffectReplayRules.Validate(actual.Effects[0]); saved.Add((actual, Json(actual)));
            }
            foreach (var snapshot in saved) Check(Json(snapshot.Frame) == snapshot.Json);
        });
        test("unchanged particle module and force snapshots retain shared delta states", () =>
        {
            var effect = Modules(); var writer = new ReplayDeltaCodec.Writer(15); var reader = new ReplayDeltaCodec.StreamReader(15);
            var before = reader.DecodeRecord(Json(writer.Encode(new ReplayFrame { T = 10, Effects = new[] { effect } })))!;
            var after = reader.DecodeRecord(Json(writer.Encode(new ReplayFrame { T = 11, Effects = new[] { effect } })))!;
            Check(ReferenceEquals(before.Effects, after.Effects));
            Check(ReferenceEquals(before.Effects[0].Particles, after.Effects[0].Particles));
            Check(ReferenceEquals(before.Effects[0].ForceField, after.Effects[0].ForceField));
        });
        test("schema 14 retains unknown particle parameters and rejects schema 15 modules", () =>
        {
            var effect = Sample(); var source = new ReplayFrame { T = 10, Effects = new[] { effect } };
            string legacy = Json(new ReplayDeltaCodec.Writer(14).Encode(source));
            Check(new ReplayDeltaCodec.StreamReader(14).DecodeRecord(legacy)!.Effects[0].Particles == null);
            Check(new ReplayDeltaCodec.Reader(14).Decode(JObject.Parse(legacy)).Effects[0].ForceField == null);
            source.Effects = new[] { Modules() }; string newer = Json(new ReplayDeltaCodec.Writer(15).Encode(source));
            Reject(() => new ReplayDeltaCodec.Reader(14).Decode(JObject.Parse(newer)));
            Reject(() => new ReplayDeltaCodec.StreamReader(14).DecodeRecord(newer));
        });
        test("particle module validation rejects malformed vectors curves variants and dangling force fields", () =>
        {
            var f = Modules(); f.Particles![0].Colors = new float[9]; Bad(f);
            f = Modules(); f.Particles![0].Colors[0] = float.NaN; Bad(f);
            f = Modules(); f.Particles![0].Parameters[0] = .5f; Bad(f);
            f = Modules(); f.Particles![0].Parameters[13] = 101; Bad(f);
            f = Modules(); f.Particles![0].Parameters[12] = -1; Bad(f);
            f = Modules(); f.Particles![0].MaterialVariant = 2; Bad(f);
            f = Modules(); f.Particles![0].Pose = null!; Bad(f);
            f = Modules(); f.Particles![0].Pose.Rotation = new float[4]; Bad(f);
            f = Modules(); f.Particles = null; Bad(f);
            f = Modules(); f.ForceField!.Pose = null!; Bad(f);
            f = Modules(); f.ForceField!.Gravity[3] = float.PositiveInfinity; Bad(f);
            f = Modules(); f.Particles![0].SimulationSpace!.Nodes = new[] { new NodePose() }; Bad(f);
            f = Modules(); f.Particles = new[] { f.Particles![0] }; Bad(f);
        });
        test("particle parameter array caps are enforced by both bounded readers", () =>
        {
            var f = Modules(); f.Particles![0].Parameters = new float[1000];
            string record = Json(new ReplayDeltaCodec.Writer(15).Encode(new ReplayFrame { T = 10, Effects = new[] { f } }));
            Reject(() => new ReplayDeltaCodec.Reader(15).Decode(JObject.Parse(record)));
            Reject(() => new ReplayDeltaCodec.StreamReader(15).DecodeRecord(record));
        });
        test("crop shifts only the effect clock and preserves immutable module and force spaces", () =>
        {
            var before = Modules(); var after = EffectReplayRules.Rebase(before, 12); EffectReplayRules.Validate(after);
            Check(ReferenceEquals(before.Particles, after.Particles) && ReferenceEquals(before.ForceField, after.ForceField));
            Check(EffectReplayRules.PhaseAt(before, 14) == EffectReplayRules.PhaseAt(after, 2));
            Check(EffectReplayRules.TimeAtPhase(before, 0) - 12 == EffectReplayRules.TimeAtPhase(after, 0));
            Check(ReferenceEquals(before.Particles![0].SimulationSpace, after.Particles![0].SimulationSpace));
        });
        test("particle validation and stable memory estimation allocate no arrays per observation", () =>
        {
            var f = Modules(); for (int i = 0; i < 100; i++) { EffectReplayRules.Validate(f); EffectReplayRules.Estimate(f); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) { EffectReplayRules.Validate(f); EffectReplayRules.Estimate(f); }
            Check(GC.GetAllocatedBytesForCurrentThread() == before);
            Check(EffectReplayRules.Estimate(f) >= EffectReplayRules.Estimate(Sample()) + 2 * 512 + 224 + 320);
        });
        test("spore burst born between samples retains its phase-zero emission", () =>
        {
            var born = Sample(); born.AnchorTime = 726.7354506448412; born.Phase = .05101040378212929; born.Rate = 1;
            double left = 726.6844402423469, birth = EffectReplayRules.TimeAtPhase(born, 0);
            var bracket = new EffectReplayHistorySample(Array.Empty<EffectReplayFrame>(), new[] { born }, left, born.AnchorTime);
            EffectReplayHistorySample? History(double t) => t < left
                ? new EffectReplayHistorySample(Array.Empty<EffectReplayFrame>(), Array.Empty<EffectReplayFrame>(), left - .02, left) : bracket;
            Check(EffectReplayRules.TryAdmittedBracketState(born, birth, () => true, History, out var state));
            Check(ReferenceEquals(state, born));
            // Rebuilding from a later seek must recover the actual birth pose.
            var later = Sample(); later.Rate = 1; later.AnchorTime = born.AnchorTime + 1; later.Phase = born.Phase + 1;
            later.Position = new[] { 99f, 0f, 0f };
            Check(EffectReplayRules.TryAdmittedBracketState(later, birth, () => true, History, out state));
            Check(ReferenceEquals(state, born));
        });
        test("particle birth reconstruction does not bridge older absence or another generation", () =>
        {
            var born = Sample(); born.AnchorTime = 10; born.Phase = .05; born.Rate = 1;
            EffectReplayHistorySample? History(double _) => new(Array.Empty<EffectReplayFrame>(), new[] { born }, 9.9, 10);
            Check(EffectReplayRules.TryAdmittedBracketState(born, 9.91, () => true, History, out var state) && state == null);
            var other = Sample(); other.Seed++;
            Check(EffectReplayRules.TryAdmittedBracketState(other, 9.97, () => true, History, out state) && state == null);
            born.AnchorTime = 9.9 + born.Phase - EffectReplayRules.BirthClockTolerance * 2;
            Check(EffectReplayRules.TryAdmittedBracketState(born, 9.97, () => true, History, out state) && state == null);
            born.AnchorTime = 10;
            born.Phase = 1;
            Check(EffectReplayRules.TryAdmittedBracketState(born, 9.97, () => true, History, out state) && state == null);
        });
        test("particle birth reconstruction waits for IO and budget and excludes ambiguous clocks", () =>
        {
            var born = Sample(); born.AnchorTime = 10; born.Phase = .05; born.Rate = 1; int calls = 0;
            EffectReplayHistorySample? History(double _) { calls++; return new(Array.Empty<EffectReplayFrame>(), new[] { born }, 9.9, 10); }
            Check(!EffectReplayRules.TryAdmittedBracketState(born, 9.97, () => false, History, out _) && calls == 0);
            Check(!EffectReplayRules.TryAdmittedBracketState(born, 9.97, () => true, _ => null, out _));
            born.Loop = true;
            Check(EffectReplayRules.TryAdmittedBracketState(born, 9.97, () => true, History, out var state) && state == null);
            born.Loop = false; born.Paused = true;
            Check(EffectReplayRules.TryAdmittedBracketState(born, 9.97, () => true, History, out state) && state == null);
            born.Paused = false; born.Rate = 0;
            Check(EffectReplayRules.TryAdmittedBracketState(born, 9.97, () => true, History, out state) && state == null);
        });
        test("effect failure identities keep resource and clip boundaries distinct", () =>
        {
            var failures = new PresentationResourceFailures();
            failures.Reject("fire|smoke", "burst");
            Check(!failures.Allows("fire|smoke", "burst"));
            Check(failures.Allows("fire", "smoke|burst") && failures.Allows("fire|smoke", "other"));
            failures.Reject("legacy"); Check(!failures.Allows("legacy") && !failures.Allows("legacy", ""));
        });
        test("steady effect resource checks allocate no joined identity per rendered frame", () =>
        {
            var failures = new PresentationResourceFailures();
            string resource = new string('r', 256), clip = new string('c', 64);
            failures.Reject(resource, clip);
            for (int frame = 0; frame < 100; frame++) { failures.Allows(resource, clip); failures.Allows(resource, "healthy"); }
            long before = GC.GetAllocatedBytesForCurrentThread(); bool rejected = true, healthy = true;
            for (int frame = 0; frame < 10000; frame++)
            { rejected &= !failures.Allows(resource, clip); healthy &= failures.Allows(resource, "healthy"); }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(rejected && healthy && bytes == 0);
        });
        test("particle preroll waits for missing history instead of substituting the seek pose", () =>
        {
            Check(!EffectReplayRules.TryHistoricalState(Sample(), null, out var state) && state == null);
        });
        test("known absent emitter history remains absent and does not invent particles", () =>
        {
            Check(EffectReplayRules.TryHistoricalState(Sample(), Array.Empty<EffectReplayFrame>(), out var state) && state == null);
        });
        test("particle history selects the recorded emitter generation and transform", () =>
        {
            var current = Sample(); var old = Sample(); old.Seed++;
            var historical = Sample(); historical.Position = new[] { 12f, 3f, 4f };
            Check(EffectReplayRules.TryHistoricalState(current, new[] { old }, out var absent) && absent == null);
            Check(EffectReplayRules.TryHistoricalState(current, new[] { old, historical }, out var state) && ReferenceEquals(state, historical));
        });
        test("replica build microsteps share the global deadline and have a bounded per-frame count", () =>
        {
            double clock = 0; var budget = new PresentationWorkBudget(() => clock); budget.BeginFrame(1);
            int operations = 0; while (budget.Build()) operations++;
            Check(operations == PresentationWorkBudget.MaximumBuildSteps);
            budget.BeginFrame(2); Check(budget.Build()); clock = PresentationWorkBudget.MaximumMilliseconds;
            Check(!budget.Build() && !budget.Create() && !budget.Step());
            budget.BeginFrame(3); Check(budget.Build());
        });
        test("backward seek warmup retains progress rather than restarting against the last completed phase", () =>
        {
            var frame = Sample(); frame.Phase = 10;
            Check(!EffectReplayRules.RequiresRestart(frame, 10, 10, 100, frame.Seed, frame.Seeds, true, true, true, false));
            Check(EffectReplayRules.RequiresRestart(frame, 10, 10, 100, frame.Seed, frame.Seeds, true, true, false, false));
            Check(!EffectReplayRules.RequiresRestart(frame, 10, 10, 100, frame.Seed, frame.Seeds, true, false, true, false));
        });
        test("a seeded emitter becoming active restarts once and completes across many budgeted frames", () =>
        {
            var frame = Sample(); frame.Phase = 1;
            long seed = 0; long[] seeds = Array.Empty<long>(); bool emitting = false, warming = false;
            double warm = 0, completedPhase = 100; int resets = 0;
            for (int renderFrame = 0; renderFrame < 40; renderFrame++)
            {
                if (EffectReplayRules.RequiresRestart(frame, 10, 10, completedPhase, seed, seeds, emitting, true, warming, false))
                {
                    resets++; warming = true; warm = 0;
                    seed = frame.Seed; seeds = frame.Seeds; emitting = frame.Emitting;
                }
                if (warming)
                {
                    warm = EffectReplayRules.NextSimulationPhase(warm, frame.Phase);
                    if (warm >= frame.Phase - .00001) { warming = false; completedPhase = frame.Phase; }
                }
            }
            Check(resets == 1 && !warming && completedPhase == frame.Phase);
        });
        test("a newer seek or emitter generation still cancels an unfinished particle warmup", () =>
        {
            var frame = Sample(); frame.Phase = 10;
            Check(EffectReplayRules.RequiresRestart(frame, 10, 10, 100, frame.Seed, frame.Seeds, true, true, true, true));
            Check(EffectReplayRules.RequiresRestart(frame, 10, 10, 100, frame.Seed + 1, frame.Seeds, true, true, true, false));
            Check(EffectReplayRules.RequiresRestart(frame, 10, 10, 100, frame.Seed, new long[] { 999 }, true, true, true, false));
        });
        test("exhausted particle budget does not invoke a historical lookup or build its directory", () =>
        {
            double clock = 0; var budget = new PresentationWorkBudget(() => clock); budget.BeginFrame(1); Check(budget.Step());
            clock = PresentationWorkBudget.MaximumMilliseconds; int lookups = 0;
            EffectReplayFrame[]? History(double time) { lookups++; return new[] { Sample() }; }
            Check(!EffectReplayRules.TryAdmittedHistoricalState(Sample(), 10, budget.Step, History, out var state));
            Check(state == null && lookups == 0);
            budget.BeginFrame(2);
            Check(EffectReplayRules.TryAdmittedHistoricalState(Sample(), 10, budget.Step, History, out state));
            Check(state != null && lookups == 1);
        });
        test("admitted missing particle history waits without advancing the fixed batch", () =>
        {
            var batch = new EffectReplayRecoveryBatch(); var frame = Sample(); batch.Restart(frame, 10, 0);
            var budget = new PresentationWorkBudget(() => 0);
            Check(!EffectReplayRules.TryAdmittedHistoricalState(batch.Anchor, batch.SampleTime, budget.Step, _ => null, out _));
            Check(batch.Phase == 0 && batch.Pending && !batch.HasPresented);
            Check(EffectReplayRules.TryAdmittedHistoricalState(batch.Anchor, batch.SampleTime, budget.Step, _ => Array.Empty<EffectReplayFrame>(), out var state));
            Check(state == null); batch.CommitSlice(batch.NextPhase); Check(batch.Phase > 0);
        });
        test("rate one hundred recovery converges to its fixed target while playback continues at thirty FPS", () =>
        {
            var frame = Sample(); frame.Rate = 100; var batch = new EffectReplayRecoveryBatch(); batch.Restart(frame, 12, 0);
            double targetPhase = batch.TargetPhase, playbackTime = 12; int renderFrames = 0;
            while (batch.Pending)
            {
                playbackTime += 1.0 / 30;
                for (int slice = 0; slice < 64 && batch.HasSlice; slice++) batch.CommitSlice(batch.NextPhase);
                if (!batch.HasSlice) batch.Complete();
                Check(batch.TargetTime == 12 && batch.TargetPhase == targetPhase && ++renderFrames < 100);
            }
            Check(batch.HasPresented && batch.Lags(frame, playbackTime));
            batch.CatchUp(frame, playbackTime); double catchupTarget = batch.TargetPhase;
            for (int slice = 0; slice < 64 && batch.HasSlice; slice++) batch.CommitSlice(batch.NextPhase);
            Check(batch.Pending && batch.HasPresented && batch.TargetPhase == catchupTarget);
        });
        test("particle recovery keeps its original phase to history clock mapping until a new batch", () =>
        {
            var frame = Sample(); frame.Rate = 100;
            var batch = new EffectReplayRecoveryBatch(); batch.Restart(frame, 12, 101);
            Check(batch.SampleTime == 11 && batch.TargetTime == 12);
            var later = Sample(); later.Rate = 2; later.AnchorTime = 20; later.Phase = 500;
            batch.CommitSlice(batch.NextPhase);
            Check(ReferenceEquals(batch.Anchor, frame));
            Check(Math.Abs(batch.SampleTime - EffectReplayRules.TimeAtPhase(frame, batch.Phase)) < 1e-12);
            Check(batch.TargetPhase == 201 && EffectReplayRules.PhaseAt(later, 30) == 520);
        });
        test("a new particle seek replaces the fixed target and hides the previous generation until rebuilt", () =>
        {
            var frame = Sample(); var batch = new EffectReplayRecoveryBatch(); batch.Restart(frame, 12, 0);
            for (int slice = 0; slice < 30; slice++) batch.CommitSlice(batch.NextPhase);
            var replacement = Sample(); replacement.Seed++; replacement.AnchorTime = 50; replacement.Phase = .1;
            batch.Restart(replacement, 50, 0);
            Check(batch.Pending && !batch.HasPresented && batch.Phase == 0 && batch.TargetPhase == .1 && batch.TargetTime == 50);
            Check(ReferenceEquals(batch.Anchor, replacement));
            while (batch.HasSlice) batch.CommitSlice(batch.NextPhase);
            batch.Complete(); Check(batch.HasPresented && !batch.Pending && batch.Phase == .1);
        });
        test("particle simulation slices remain bounded at high recorded rates", () =>
        {
            var frame = Sample(); frame.Rate = 100;
            double target = EffectReplayRules.PhaseAt(frame, 10.25), phase = frame.Phase;
            int steps = 0;
            while (phase < target)
            {
                double next = EffectReplayRules.NextSimulationPhase(phase, target);
                Check(next > phase && next - phase <= EffectReplayRules.MaximumSimulationPhaseStep + 1e-12);
                phase = next; Check(++steps <= 751);
            }
            Check(phase == target && steps >= 750);
        });
        test("particle simulation slices reach a fractional target without overshooting", () =>
        {
            Check(EffectReplayRules.NextSimulationPhase(12, 12.001) == 12.001);
            Check(EffectReplayRules.NextSimulationPhase(12, 12) == 12);
        });
        test("native effect clock advances from native phase and rate", () =>
        { var f = Sample(); Check(EffectReplayRules.PhaseAt(f, 12) == 5); });
        test("native effect clock respects pause and delayed start", () =>
        { var f = Sample(); f.Paused = true; Check(EffectReplayRules.PhaseAt(f, 12) == 1);
          f.Paused = false; Check(EffectReplayRules.PhaseAt(f, 9) == 1); });
        test("effect snapshot crop preserves ongoing lifetime without mutating capture", () =>
        {
            var f = Sample(); var cropped = EffectReplayRules.Rebase(f, 12);
            Check(f.AnchorTime == 10 && cropped.AnchorTime == -2);
            Check(EffectReplayRules.PhaseAt(cropped, 0) == EffectReplayRules.PhaseAt(f, 12));
            Check(ReferenceEquals(cropped.Seeds, f.Seeds) && ReferenceEquals(cropped.Position, f.Position));
        });
        test("effect emission stop is a phase not an absolute timestamp", () =>
        {
            var f = Sample(); f.Emitting = false; f.EmissionStopPhase = 3;
            var cropped = EffectReplayRules.Rebase(f, 12); EffectReplayRules.Validate(cropped);
            Check(cropped.EmissionStopPhase == 3 && !cropped.Emitting && EffectReplayRules.PhaseAt(cropped, 1) == 7);
        });
        test("effect backward seek is stateless and repeatable", () =>
        { var f = Sample(); double before = EffectReplayRules.PhaseAt(f, 11); EffectReplayRules.PhaseAt(f, 13);
          Check(before == EffectReplayRules.PhaseAt(f, 11)); });
        test("effect seeds preserve full unsigned native range", () =>
        { var f = Sample(); f.Seed = uint.MaxValue; f.Seeds = new long[] { 0, uint.MaxValue }; EffectReplayRules.Validate(f); });
        test("effect invalid seed negative phase and clock are rejected", () =>
        {
            var f = Sample(); f.Seed = (long)uint.MaxValue + 1; Bad(f);
            f = Sample(); f.Seeds = new long[] { -1 }; Bad(f);
            f = Sample(); f.Phase = -.001; Bad(f);
            f = Sample(); f.AnchorTime = double.NaN; Bad(f);
        });
        test("effect invalid orientation and excessive child systems are rejected", () =>
        { var f = Sample(); f.Rotation = new float[4]; Bad(f); f = Sample(); f.Seeds = new long[65]; Bad(f); });
        test("native mesh effect requires actual animation resource identity", () =>
        { var f = Sample(); f.Kind = 1; Bad(f); f.Clip = "Explosion"; EffectReplayRules.Validate(f); });
        test("antigravity sphere uses native mesh identity and observed scale without a fake animation", () =>
        {
            var f = Sample(); f.Kind = 2; f.Resource = "antigravity/AntiSphere"; f.Clip = "";
            f.Scale = new[] { .2f, .2f, .2f }; f.Paused = true; f.Rate = 0;
            EffectReplayRules.Validate(f); var copy = EffectReplayRules.Rebase(f, 20);
            Check(copy.Kind == 2 && copy.Clip == "" && ReferenceEquals(f.Scale, copy.Scale));
            f.Kind = 3; Bad(f);
        });
        test("paused particle preroll traces its anchor instead of the later paused seek cursor", () =>
        {
            var f = Sample(); f.Paused = true;
            Check(EffectReplayRules.PhaseAt(f, 100) == 1 && EffectReplayRules.TimeAtPhase(f, 1) == 10);
            Check(EffectReplayRules.TimeAtPhase(f, 0) == 9.5);
            var cropped = EffectReplayRules.Rebase(f, 12);
            Check(EffectReplayRules.TimeAtPhase(cropped, 0) == -2.5);
        });
        test("effect preroll and creation use one finite global frame budget", () =>
        {
            var budget = new PresentationWorkBudget(() => 0); int steps = 0, creations = 0;
            for (int entity = 0; entity < 512; entity++)
            {
                if (budget.Create()) creations++;
                for (int step = 0; step < 360; step++) if (budget.Step()) steps++;
            }
            Check(steps == PresentationWorkBudget.MaximumSteps && creations == PresentationWorkBudget.MaximumCreations);
            Check(!budget.Step() && !budget.Create());
            Check(new PresentationWorkBudget(() => 0).Step());
        });
        test("presentation creation and preroll share the wall clock deadline", () =>
        {
            double clock = 100;
            var budget = new PresentationWorkBudget(() => clock);
            budget.BeginFrame(1); Check(budget.Create());
            clock += PresentationWorkBudget.MaximumMilliseconds;
            Check(!budget.Step() && !budget.Create());
            budget.BeginFrame(1); Check(!budget.Create());
            budget.BeginFrame(2); Check(budget.Step() && budget.Create());
        });
        test("equal decoded particle seeds do not require a restart", () =>
        {
            Check(EffectReplayRules.SameSeeds(new long[] { 123, uint.MaxValue }, new long[] { 123, uint.MaxValue }));
            Check(!EffectReplayRules.SameSeeds(new long[] { 123 }, new long[] { 124 }));
            Check(!EffectReplayRules.SameSeeds(new long[] { 123 }, new long[] { 123, 0 }));
        });
        test("effect budget includes child seed array and resource identities", () =>
        { var f = Sample(); long first = EffectReplayRules.Estimate(f); f.Seeds = new long[64]; Check(EffectReplayRules.Estimate(f) >= first + 62 * 8); });
    }
    private static EffectReplayFrame Sample() => new()
    { Key = "native-effect-test", Resource = "particle/test", AnchorTime = 10, Phase = 1, Rate = 2, Seed = 123, Seeds = new long[] { 123, 456 } };
    private static EffectReplayFrame Modules()
    {
        var effect = Sample();
        effect.Particles = Enumerable.Range(0, 2).Select(_ => new EffectParticleFrame(new NodePose(), new[] { 1f, 0f, 0f, 1f, 0f, 1f, 0f, 1f },
            new[] { 0f, 1f, 1f, 1f, 0f, 10f, 10f, 1f, 0f, 0f, 0f, 1f, 1f, 1f })
            { Emitting = true, EmissionEnabled = true, SimulationSpace = new ObjectPose() }).ToArray();
        effect.ForceField = new EffectForceFieldFrame(new ObjectPose(), new[] { 3f, -.1f, .1f, 1f }) { Enabled = true };
        return effect;
    }
    private static string Json(object value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static void Reject(Action action)
    { try { action(); } catch (Exception e) when (e is InvalidDataException || e is JsonException || e is ArgumentException) { return; } throw new Exception("Invalid effect record accepted."); }
    private static void Check(bool condition) { if (!condition) throw new Exception("Effect replay assertion failed."); }
    private static void Bad(EffectReplayFrame value)
    { try { EffectReplayRules.Validate(value); } catch (InvalidDataException) { return; } throw new Exception("Invalid effect data was accepted."); }
}
