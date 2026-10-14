using System;

namespace PeakReplayLab;

// Schema 22: one bounded immutable birth observation when a native effect
// changes or ends before the next scene tick. Identity/resource/kind belong to
// its containing run; this evidence cannot contain another effect snapshot.
public sealed class EffectReplayInitialState
{
    public EffectReplayInitialState() : this(new float[3], new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f }) { }
    public EffectReplayInitialState(float[] position, float[] rotation, float[] scale)
    { Position = position; Rotation = rotation; Scale = scale; }
    public double ObservedAt { get; set; }
    public double UntilTime { get; set; }
    public bool Ended { get; set; }
    public double? EndTime { get; set; }
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
    public long[] Seeds { get; set; } = Array.Empty<long>();
    public string Clip { get; set; } = "";
    public float ShaderRandom { get; set; }
    public EffectParticleFrame[]? Particles { get; set; }
    public EffectForceFieldFrame? ForceField { get; set; }

    internal static EffectReplayInitialState Rebase(EffectReplayInitialState source, double origin,
        EffectParticleFrame[]? particles = null) => new(source.Position, source.Rotation, source.Scale)
    {
        ObservedAt = source.ObservedAt - origin, UntilTime = source.UntilTime - origin, Ended = source.Ended,
        EndTime = source.EndTime.HasValue ? source.EndTime.Value - origin : null,
        AnchorTime = source.AnchorTime - origin, Phase = source.Phase, Rate = source.Rate, Loop = source.Loop,
        Emitting = source.Emitting, EmissionStopPhase = source.EmissionStopPhase, Paused = source.Paused,
        Seed = source.Seed, Seeds = source.Seeds, Clip = source.Clip, ShaderRandom = source.ShaderRandom,
        Particles = particles ?? (origin == 0 || source.Particles == null ? source.Particles : new EffectParticleCheckpointRebaser(origin).Apply(source.Particles)), ForceField = source.ForceField,
    };
}

// One crop/save owns one clock origin. Many changing effect frames can share
// their immutable initial evidence without creating another DTO every tick.
internal sealed class EffectReplayInitialStateRebaser
{
    private readonly double origin;
    private readonly EffectParticleCheckpointRebaser particles;
    private readonly BoundedReferenceCache<EffectReplayInitialState, EffectReplayInitialState> states = new(128);
    private readonly Func<EffectReplayInitialState, EffectReplayInitialState> factory;
    internal EffectReplayInitialStateRebaser(double origin) { this.origin = origin; particles = new(origin); factory = Shift; }
    internal EffectParticleFrame[]? Particles(EffectParticleFrame[]? source) => particles.Apply(source);
    private EffectReplayInitialState Shift(EffectReplayInitialState source) => EffectReplayInitialState.Rebase(source, origin, particles.Apply(source.Particles));
    internal EffectReplayInitialState? Apply(EffectReplayInitialState? source) => source == null || origin == 0 ? source : states.GetValue(source, factory);
}
