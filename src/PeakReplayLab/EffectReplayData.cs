using System;
using System.IO;

namespace PeakReplayLab;

// Native visual resources are referenced, never embedded. Particle seed + clock
// replaces per-particle recording. Moving emitter transforms remain observed data.
public sealed class EffectReplayFrame
{
    public string Key { get; set; } = "";
    public string Resource { get; set; } = "";
    public int Kind { get; set; } // 0 particle system, 1 animated explosion mesh, 2 native antigravity sphere mesh
    public float[] Position { get; set; } = new float[3];
    public float[] Rotation { get; set; } = new[] { 0f, 0f, 0f, 1f };
    public float[] Scale { get; set; } = new[] { 1f, 1f, 1f };
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
}

public static class EffectReplayRules
{
    public const int Maximum = 512;
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
        double norm = 0; foreach (float value in f.Rotation) norm += (double)value * value;
        if (norm < .5 || norm > 1.5) throw new InvalidDataException("Invalid effect orientation.");
    }
    public static long Estimate(EffectReplayFrame f) => 352 + f.Seeds.Length * 8L + 2L * (f.Key.Length + f.Resource.Length + f.Clip.Length);
    public static double PhaseAt(EffectReplayFrame f, double time) =>
        Math.Max(0, f.Phase + (f.Paused ? 0 : Math.Max(0, time - f.AnchorTime) * f.Rate));
    // A paused effect's final phase happened at its anchor, not at the seek time.
    public static double TimeAtPhase(EffectReplayFrame f, double phase) =>
        f.Rate > 0 ? f.AnchorTime + (phase - f.Phase) / f.Rate : f.AnchorTime;
    public static EffectReplayFrame Rebase(EffectReplayFrame f, double start) => new()
    {
        Key = f.Key, Resource = f.Resource, Kind = f.Kind, Position = f.Position, Rotation = f.Rotation, Scale = f.Scale,
        AnchorTime = f.AnchorTime - start, Phase = f.Phase, Rate = f.Rate, Loop = f.Loop,
        Emitting = f.Emitting, EmissionStopPhase = f.EmissionStopPhase, Paused = f.Paused, Seed = f.Seed, Seeds = f.Seeds, Clip = f.Clip, ShaderRandom = f.ShaderRandom,
    };
    private static bool Vector(float[]? values, int size)
    {
        if (values == null || values.Length != size) return false;
        foreach (float value in values) if (!Finite(value) || Math.Abs(value) >= 1e7) return false;
        return true;
    }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

// Shared global work budget, not 512 independent per-effect budgets. Large seeks
// warm hidden effects over several render frames without blocking the player.
public sealed class EffectReplayWorkBudget
{
    public const int MaximumSteps = 192, MaximumCreations = 4;
    private int steps, creations;
    public bool Step() { if (steps >= MaximumSteps) return false; steps++; return true; }
    public bool Create() { if (creations >= MaximumCreations) return false; creations++; return true; }
}
