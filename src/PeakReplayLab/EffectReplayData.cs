using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace PeakReplayLab;

// Native visual resources are referenced, never embedded. Particle seed + clock
// replaces per-particle recording. Moving emitter transforms remain observed data.
public sealed class EffectReplayFrame
{
    public EffectReplayFrame() : this(new float[3], new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f }) { }
    public EffectReplayFrame(float[] position, float[] rotation, float[] scale) { Position = position; Rotation = rotation; Scale = scale; }
    public string Key { get; set; } = "";
    public string Resource { get; set; } = "";
    public int Kind { get; set; } // 0 particle system, 1 animated explosion mesh, 2 native antigravity sphere mesh
    public float[] Position { get; set; }
    public float[] Rotation { get; set; }
    public float[] Scale { get; set; }
    public double AnchorTime { get; set; }
    public double Phase { get; set; }
    public float Rate { get; set; } = 1;
    public bool Loop { get; set; }
    public bool Emitting { get; set; } = true;
    public double EmissionStopPhase { get; set; } = -1;
    public bool Paused { get; set; }
    public long Seed { get; set; }
    public long[] Seeds { get; set; } = Array.Empty<long>(); // authored child particle systems, in stable hierarchy order
    public string Clip { get; set; } = "";
    public float ShaderRandom { get; set; }
    // Schema 15: null is unknown in older recordings, not an all-zero module.
    public EffectParticleFrame[]? Particles { get; set; }
    public EffectForceFieldFrame? ForceField { get; set; }
    public EffectReplayInitialState? InitialState { get; set; }
}

public sealed class EffectParticleFrame
{
    public EffectParticleFrame() : this(new NodePose(), Array.Empty<float>(), Array.Empty<float>()) { }
    public EffectParticleFrame(NodePose pose, float[] colors, float[] parameters) { Pose = pose; Colors = colors; Parameters = parameters; }
    public NodePose Pose { get; set; }
    public bool Emitting { get; set; }
    public bool Paused { get; set; }
    public bool EmissionEnabled { get; set; }
    public int ColorMode { get; set; }
    public int MaterialVariant { get; set; } // 0 authored material, 1 native BeeSwarm bug-phobia material
    public float[] Colors { get; set; } // min RGBA, max RGBA
    // Four floats per native curve: mode, constantMin, constantMax, multiplier.
    // startSize, emission.rateOverTime, emission.rateOverDistance, then shape
    // radius and simulationSpeed. Native authored curves remain in the prefab.
    public float[] Parameters { get; set; }
    public ObjectPose? SimulationSpace { get; set; }
    // Observed world pose of the audited GhostBall static emission surface.
    // Null in prior recordings; never a serialized mesh or arbitrary resource.
    public ObjectPose? MeshShapeSpace { get; set; }
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public EffectParticleCheckpoint? Checkpoint { get; set; }
}

public sealed class EffectForceFieldFrame
{
    public EffectForceFieldFrame() : this(new ObjectPose(), Array.Empty<float>()) { }
    public EffectForceFieldFrame(ObjectPose pose, float[] gravity) { Pose = pose; Gravity = gravity; }
    public ObjectPose Pose { get; set; }
    public bool Enabled { get; set; }
    public float[] Gravity { get; set; }
}

public readonly struct EffectReplayHistorySample
{
    public readonly EffectReplayFrame[] Left, Right;
    public readonly double LeftTime, RightTime;
    public EffectReplayHistorySample(EffectReplayFrame[] left, EffectReplayFrame[] right, double leftTime, double rightTime)
    { Left = left; Right = right; LeftTime = leftTime; RightTime = rightTime; }
}

public static class EffectReplayRules
{
    private sealed class HistoryDirectory
    {
        public readonly Dictionary<string, EffectReplayFrame> Frames = new(StringComparer.Ordinal);
        public HistoryDirectory(EffectReplayFrame[] frames)
        { foreach (var frame in frames) Frames[frame.Key] = frame; }
    }
    // Historical arrays belong to immutable decoded pages. Weak ownership lets
    // their lookup tables disappear with LRU eviction and replay shutdown.
    private static readonly ConditionalWeakTable<EffectReplayFrame[], HistoryDirectory> histories = new();
    public const int Maximum = 512;
    public static readonly Type[] KnownTypes = { typeof(EffectParticleFrame), typeof(EffectForceFieldFrame), typeof(EffectReplayInitialState), typeof(EffectParticleCheckpoint) };
    public const int ParticleParameterCount = 14;
    // Native enum values: MeshRenderer=13, SkinnedMeshRenderer=14,
    // SpriteRenderer=20. Unused Unity shape union references may remain nonnull.
    public static bool UsesRendererShape(int shapeType) => shapeType is 13 or 14 or 20;
    // Native MinMax unions leave unused slots undefined. Keep the wire format
    // deterministic and bounded without changing any semantically used value.
    public static void CanonicalizeParticleColors(int mode, Span<float> colors)
    {
        if (mode == 0) colors.Slice(4, 4).CopyTo(colors.Slice(0, 4));
        else if (mode != 2) colors.Clear();
    }
    public static void CanonicalizeParticleCurve(int mode, Span<float> curve)
    {
        if (mode == 0) { curve[1] = curve[2]; curve[3] = 1; }
        else if (mode == 3) curve[3] = 1;
        else { curve[1] = 0; curve[2] = 0; }
    }
    // Bound an individual non-preemptible Unity Simulate call. Simulation rate
    // changes the number of slices, never the amount of work in a single call.
    public const double MaximumSimulationPhaseStep = 1.0 / 30;
    // A float particle clock can put phase zero just before the preceding
    // double-precision recording timestamp (1.3 ns in the spore regression).
    public const double BirthClockTolerance = .00001;
    public static double NextSimulationPhase(double current, double target) =>
        Math.Min(target, current + MaximumSimulationPhaseStep);
    // A pending rebuild still carries the last completed phase. Comparing that
    // older phase to a backward seek target would restart the same warmup on
    // every frame and prevent it from ever becoming visible.
    public static bool RequiresRestart(EffectReplayFrame current, double time, double previousTime, double previousPhase,
        long previousSeed, long[] previousSeeds, bool wasEmitting, bool active, bool warming, bool discontinuity) =>
        discontinuity || !warming && (!active || double.IsNaN(previousTime) || time < previousTime || time - previousTime > .25 ||
            PhaseAt(current, time) + .05 < previousPhase) || previousSeed != current.Seed ||
        !SameSeeds(previousSeeds, current.Seeds) || current.Emitting && !wasEmitting;
    public static void Validate(EffectReplayFrame f)
    {
        if (f == null || string.IsNullOrWhiteSpace(f.Key) || f.Key.Length > 256 ||
            string.IsNullOrWhiteSpace(f.Resource) || f.Resource.Length > 2048 || f.Kind < 0 || f.Kind > 2 ||
            f.Clip == null || f.Clip.Length > 1024 || (f.Kind == 1 && string.IsNullOrWhiteSpace(f.Clip)) ||
            !Vector(f.Position, 3) || !Vector(f.Rotation, 4) || !Vector(f.Scale, 3) ||
            !Finite(f.AnchorTime) || Math.Abs(f.AnchorTime) > 1e9 || !Finite(f.Phase) || f.Phase < 0 || f.Phase > 86400 ||
            !Finite(f.Rate) || f.Rate < 0 || f.Rate > 100 || f.Seed < 0 || f.Seed > uint.MaxValue ||
            f.Seeds == null || f.Seeds.Length > 64 ||
            !Finite(f.EmissionStopPhase) || f.EmissionStopPhase < -1 || f.EmissionStopPhase > 86400 ||
            !Finite(f.ShaderRandom) || Math.Abs(f.ShaderRandom) > 1e6)
            throw new InvalidDataException("Invalid native visual effect snapshot.");
        foreach (long seed in f.Seeds) if (seed < 0 || seed > uint.MaxValue) throw new InvalidDataException("Invalid particle seed.");
        ValidateParticleModules(f.Kind, f.Resource, f.Seeds, f.Particles, f.ForceField);
        double norm = 0; foreach (float value in f.Rotation) norm += (double)value * value;
        if (norm < .5 || norm > 1.5) throw new InvalidDataException("Invalid effect orientation.");
        if (f.InitialState != null) ValidateInitialState(f);
    }
    private static void ValidateInitialState(EffectReplayFrame current)
    {
        var state = current.InitialState!;
        if (!Finite(state.ObservedAt) || Math.Abs(state.ObservedAt) > 1e9 ||
            !Finite(state.UntilTime) || Math.Abs(state.UntilTime) > 1e9 || state.ObservedAt > state.UntilTime ||
            state.EndTime.HasValue && (!state.Ended || !Finite(state.EndTime.Value) || Math.Abs(state.EndTime.Value) > 1e9 || state.EndTime.Value < state.UntilTime) ||
            !Finite(state.AnchorTime) || Math.Abs(state.AnchorTime) > 1e9 ||
            !Finite(state.Phase) || state.Phase < 0 || state.Phase > 86400 ||
            !Finite(state.Rate) || state.Rate < 0 || state.Rate > 100 ||
            !Finite(state.EmissionStopPhase) || state.EmissionStopPhase < -1 || state.EmissionStopPhase > 86400 ||
            !Finite(state.ShaderRandom) || Math.Abs(state.ShaderRandom) > 1e6 ||
            state.Clip == null || state.Clip.Length > 1024 || current.Kind == 1 && string.IsNullOrWhiteSpace(state.Clip) ||
            state.Seed != current.Seed || state.Seeds == null || !SameSeeds(state.Seeds, current.Seeds))
            throw new InvalidDataException("Invalid effect initial observation.");
        ValidatePose(state.Position, state.Rotation, state.Scale);
        // The initial observation commonly shares immutable modules with the
        // current state. Do not validate that same directory a second time.
        if (!ReferenceEquals(state.Particles, current.Particles) || !ReferenceEquals(state.ForceField, current.ForceField))
            ValidateParticleModules(current.Kind, current.Resource, state.Seeds, state.Particles, state.ForceField);
    }
    private static void ValidateParticleModules(int kind, string resource, long[] seeds, EffectParticleFrame[]? particles, EffectForceFieldFrame? forceField)
    {
        if (particles != null)
        {
            if (kind != 0 || particles.Length == 0 || particles.Length > 64 || particles.Length != seeds.Length)
                throw new InvalidDataException("Invalid particle module directory.");
            int checkpointParticles = 0;
            for (int index = 0; index < particles.Length; index++)
            {
                var particle = particles[index];
                if (particle == null || particle.Pose == null || particle.MaterialVariant < 0 || particle.MaterialVariant > 1 || particle.ColorMode < 0 || particle.ColorMode > 4 || !Vector(particle.Colors, 8) ||
                    !Vector(particle.Parameters, ParticleParameterCount) || particle.Parameters[12] < 0 || particle.Parameters[13] < 0 || particle.Parameters[13] > 100)
                    throw new InvalidDataException("Invalid particle module state: " + resource + " system=" + index + "; " + ParticleFailure(particle));
                ValidatePose(particle.Pose.Position, particle.Pose.Rotation, particle.Pose.Scale);
                for (int curve = 0; curve < 3; curve++) ValidateCurve(particle.Parameters, curve * 4);
                if (particle.SimulationSpace != null) ValidateSpace(particle.SimulationSpace);
                if (particle.MeshShapeSpace != null) ValidateSpace(particle.MeshShapeSpace);
                if (particle.Checkpoint != null)
                {
                    EffectParticleCheckpointRules.Validate(particle.Checkpoint);
                    checkpointParticles += particle.Checkpoint.Count;
                    if (checkpointParticles > EffectParticleCheckpointRules.MaximumParticles)
                        throw new InvalidDataException("Particle checkpoint group exceeds its native particle budget.");
                }
            }
        }
        if (forceField != null)
        {
            if (particles == null || !Vector(forceField.Gravity, 4)) throw new InvalidDataException("Invalid particle force field.");
            ValidateSpace(forceField.Pose); ValidateCurve(forceField.Gravity, 0);
        }
    }
    private static string ParticleFailure(EffectParticleFrame? particle)
    {
        if (particle == null) return "null particle";
        if (particle.Pose == null) return "null pose";
        string Values(float[]? values)
        {
            if (values == null) return "null";
            // Invalid-state diagnostics only; no string/array work on the normal
            // capture path. Bound malformed data before putting values in logs.
            var parts = new string[Math.Min(values.Length, 16)];
            for (int i = 0; i < parts.Length; i++) parts[i] = values[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            return "length=" + values.Length + " [" + string.Join(",", parts) + "]";
        }
        return "colorMode=" + particle.ColorMode + ", materialVariant=" + particle.MaterialVariant +
            ", colors=" + Values(particle.Colors) + ", parameters=" + Values(particle.Parameters);
    }
    private static void ValidateCurve(float[] values, int offset)
    {
        float mode = values[offset];
        if (mode < 0 || mode > 3 || mode != (int)mode) throw new InvalidDataException("Invalid particle curve mode.");
    }
    private static void ValidatePose(float[] p, float[] r, float[] s)
    {
        if (!Vector(p, 3) || !Vector(r, 4) || !Vector(s, 3)) throw new InvalidDataException("Invalid particle transform.");
        double norm = 0; foreach (float value in r) norm += (double)value * value;
        if (norm < .5 || norm > 1.5) throw new InvalidDataException("Invalid particle transform orientation.");
    }
    private static void ValidateSpace(ObjectPose pose)
    {
        if (pose == null) throw new InvalidDataException("Invalid particle space.");
        ValidatePose(pose.Position, pose.Rotation, pose.Scale);
        if (pose.Nodes == null || pose.Nodes.Length != 0) throw new InvalidDataException("Particle spaces cannot carry hierarchy nodes.");
    }
    public static long Estimate(EffectReplayFrame f)
    {
        long bytes = 352 + f.Seeds.Length * 8L + 2L * (f.Key.Length + f.Resource.Length + f.Clip.Length);
        if (f.Particles != null) foreach (var p in f.Particles) bytes += 512 + (p.SimulationSpace == null ? 0 : 224) + (p.MeshShapeSpace == null ? 0 : 224) + (p.Checkpoint == null ? 0 : EffectParticleCheckpointRules.Estimate(p.Checkpoint));
        if (f.ForceField != null) bytes += 320;
        if (f.InitialState != null)
        {
            var initial = f.InitialState;
            bytes += 352 + initial.Seeds.Length * 8L + initial.Clip.Length * 2L;
            if (initial.Particles != null) foreach (var p in initial.Particles) bytes += 512 + (p.SimulationSpace == null ? 0 : 224) + (p.MeshShapeSpace == null ? 0 : 224) + (p.Checkpoint == null ? 0 : EffectParticleCheckpointRules.Estimate(p.Checkpoint));
            if (initial.ForceField != null) bytes += 320;
        }
        return bytes;
    }
    // Retained tracks share immutable initial observations and native particle
    // payloads between successive 30 Hz states. Count those child identities
    // separately; Estimate above remains a conservative isolated-frame bound.
    public static long OwnEstimate(EffectReplayFrame f)
    {
        long bytes = 352 + f.Seeds.Length * 8L + 2L * (f.Key.Length + f.Resource.Length + f.Clip.Length);
        if (f.Particles != null) foreach (var p in f.Particles) bytes += ParticleOwnEstimate(p);
        return bytes + (f.ForceField == null ? 0 : 320);
    }
    private static long ParticleOwnEstimate(EffectParticleFrame p) => 512 + (p.SimulationSpace == null ? 0 : 224) + (p.MeshShapeSpace == null ? 0 : 224);
    public static void VisitShared(EffectReplayFrame f, Action<object, long> visit)
    {
        if (f.InitialState != null)
        {
            var initial = f.InitialState;
            long own = 352 + initial.Seeds.Length * 8L + initial.Clip.Length * 2L;
            if (initial.Particles != null) foreach (var p in initial.Particles) own += ParticleOwnEstimate(p);
            if (initial.ForceField != null) own += 320;
            visit(initial, own);
            VisitCheckpoints(initial.Particles, visit);
        }
        VisitCheckpoints(f.Particles, visit);
    }
    private static void VisitCheckpoints(EffectParticleFrame[]? particles, Action<object, long> visit)
    {
        if (particles != null) foreach (var particle in particles)
            if (particle.Checkpoint != null) visit(particle.Checkpoint, EffectParticleCheckpointRules.Estimate(particle.Checkpoint));
    }
    public static double PhaseAt(EffectReplayFrame f, double time) => f.InitialState != null ?
        EffectSceneEventPlayback.PhaseAt(f, time) : Math.Max(0, f.Phase + (f.Paused ? 0 : Math.Max(0, time - f.AnchorTime) * f.Rate));
    // A paused effect's final phase happened at its anchor, not at the seek time.
    public static double TimeAtPhase(EffectReplayFrame f, double phase) => f.InitialState != null ?
        EffectSceneEventPlayback.TimeAtPhase(f, phase) : f.Rate > 0 ? f.AnchorTime + (phase - f.Phase) / f.Rate : f.AnchorTime;
    public static bool SameSeeds(long[] a, long[] b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    // Admission includes history lookup and its first immutable-array directory
    // build, rather than accounting only for the subsequent Unity simulation.
    public static bool TryAdmittedHistoricalState(EffectReplayFrame current, double time, Func<bool> admit,
        Func<double, EffectReplayFrame[]?>? sample, out EffectReplayFrame? state)
    {
        state = null;
        if (!admit()) return false;
        if (sample == null) { state = current; return true; }
        return TryHistoricalState(current, sample(Math.Max(0, time)), out state);
    }
    // null means IO is still pending. A known-empty history is different: the
    // emitter did not yet exist, so no particles may be invented at its new pose.
    public static bool TryHistoricalState(EffectReplayFrame current, EffectReplayFrame[]? history, out EffectReplayFrame? state)
    {
        state = null;
        if (history == null) return false;
        if (history.Length != 0 && histories.GetValue(history, frames => new HistoryDirectory(frames)).Frames.TryGetValue(current.Key, out var frame) &&
            frame.Resource == current.Resource && frame.Kind == current.Kind && frame.Seed == current.Seed && SameSeeds(frame.Seeds, current.Seeds))
            state = frame;
        return true;
    }
    public static bool TryAdmittedBracketState(EffectReplayFrame current, double time, Func<bool> admit,
        Func<double, EffectReplayHistorySample?>? sample, out EffectReplayFrame? state, bool includeSceneEvents = false)
    {
        state = null;
        if (!admit()) return false;
        if (sample == null) { state = current; return true; }
        var history = sample(Math.Max(0, time + BirthClockTolerance));
        if (!history.HasValue) return false;
        var bracket = history.Value;
        TryHistoricalState(current, bracket.Left, out state);
        if (state != null)
        { if (includeSceneEvents) state = EffectSceneEventPlayback.Select(state, time, true); return true; }
        TryHistoricalState(current, bracket.Right, out var born);
        if (includeSceneEvents)
        {
            if (born == null) return true;
            if (EffectSceneEventPlayback.IncludeRight(born, bracket.LeftTime, bracket.RightTime, time, true))
                state = EffectSceneEventPlayback.Select(born, time, true);
            return true;
        }
        if (born == null || born.Kind != 0 || born.Loop || born.Paused || born.Rate <= 0 ||
            bracket.RightTime <= bracket.LeftTime || time < bracket.LeftTime - BirthClockTolerance ||
            time > bracket.RightTime + BirthClockTolerance) return true;
        double birth = TimeAtPhase(born, 0);
        // Only the measured clock of the first observation may bridge its
        // immediately preceding absent sample. Never use the seek target pose
        // to fill arbitrary missing history, other generations, or pending IO.
        if (birth >= bracket.LeftTime - BirthClockTolerance && birth <= bracket.RightTime &&
            time >= birth - BirthClockTolerance) state = born;
        return true;
    }
    public static EffectReplayFrame Rebase(EffectReplayFrame f, double start) => Rebase(f, start,
        f.InitialState == null || start == 0 ? f.InitialState : EffectReplayInitialState.Rebase(f.InitialState, start));
    internal static EffectReplayFrame Rebase(EffectReplayFrame f, double start, EffectReplayInitialState? initial,
        EffectParticleFrame[]? particles = null) => new(f.Position, f.Rotation, f.Scale)
    {
        Key = f.Key, Resource = f.Resource, Kind = f.Kind,
        AnchorTime = f.AnchorTime - start, Phase = f.Phase, Rate = f.Rate, Loop = f.Loop,
        Emitting = f.Emitting, EmissionStopPhase = f.EmissionStopPhase, Paused = f.Paused, Seed = f.Seed, Seeds = f.Seeds, Clip = f.Clip, ShaderRandom = f.ShaderRandom,
        Particles = particles ?? (start == 0 || f.Particles == null ? f.Particles : new EffectParticleCheckpointRebaser(start).Apply(f.Particles)), ForceField = f.ForceField, InitialState = initial,
    };
    private static bool Vector(float[]? values, int size)
    {
        if (values == null || values.Length != size) return false;
        foreach (float value in values) if (!Finite(value) || Math.Abs(value) >= 1e7) return false;
        return true;
    }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

// Every recovery batch has an immutable target and clock anchor. A fast emitter
// may outrun the presentation budget; that must delay subsequent batches rather
// than move the first batch's finish line on every render frame.
public sealed class EffectReplayRecoveryBatch
{
    public EffectReplayFrame Anchor { get; private set; } = null!;
    public double Phase { get; private set; }
    public double TargetPhase { get; private set; }
    public double TargetTime { get; private set; }
    public bool Pending { get; private set; }
    public bool HasPresented { get; private set; }
    public bool HasSlice => Pending && Phase < TargetPhase - .00001;
    public double SampleTime => EffectReplayRules.TimeAtPhase(Anchor, Phase);
    public double NextPhase => EffectReplayRules.NextSimulationPhase(Phase, TargetPhase);
    public void Restart(EffectReplayFrame anchor, double targetTime, double startPhase)
    {
        Phase = startPhase; HasPresented = false; Begin(anchor, targetTime);
    }
    public void CatchUp(EffectReplayFrame anchor, double targetTime) => Begin(anchor, targetTime);
    private void Begin(EffectReplayFrame anchor, double targetTime)
    {
        Anchor = anchor; TargetTime = targetTime; TargetPhase = EffectReplayRules.PhaseAt(anchor, targetTime); Pending = true;
    }
    public void CommitSlice(double phase)
    {
        if (!Pending || phase < Phase || phase > TargetPhase) throw new ArgumentOutOfRangeException(nameof(phase));
        Phase = phase;
    }
    public void Complete()
    {
        if (HasSlice) throw new InvalidOperationException("Particle recovery batch is not simulated through its fixed target.");
        Phase = TargetPhase; Pending = false; HasPresented = true;
    }
    public void Present(EffectReplayFrame anchor, double time)
    {
        Anchor = anchor; TargetTime = time; Phase = TargetPhase = EffectReplayRules.PhaseAt(anchor, time);
        Pending = false; HasPresented = true;
    }
    public bool Lags(EffectReplayFrame current, double time) => EffectReplayRules.PhaseAt(current, time) > Phase + .00001;
}
