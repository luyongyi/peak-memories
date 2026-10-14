using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;

namespace PeakReplayLab;

// Schema 23. Native structs are accepted only by their exact audited Unity
// layout. Pure bytes preserve hidden RNG/emission accumulators as well as each
// particle's initial velocity/parent seed, which public position alone loses.
[Newtonsoft.Json.JsonConverter(typeof(EffectParticleCheckpointJsonConverter))]
public sealed class EffectParticleCheckpoint
{
    public double ObservedAt { get; set; }
    public double RootPhase { get; set; }
    public string Layout { get; set; } = "";
    public int Count { get; set; }
    public byte[] State { get; set; } = Array.Empty<byte>();
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string Digest { get; set; } = "";
}

public static class EffectParticleCheckpointRules
{
    private sealed class Validated { }
    private static readonly ConditionalWeakTable<EffectParticleCheckpoint, Validated> validated = new();
    public const int MaximumParticles = 16384;
    public const int MaximumStateBytes = 4096;
    public const int MaximumParticleStride = 256;
    public const int MaximumDataBytes = MaximumParticles * MaximumParticleStride;
    public const int MaximumLayoutLength = 512;
    // Leave one scene observation's margin: ordinary Scene30 interpolation
    // still restores at most roughly one second after its last checkpoint.
    public const double IntervalSeconds = .9;
    // Legacy restoration is intentionally bounded and explicit. It never
    // substitutes a shortened, visibly incomplete tail and calls it complete.
    public const double MaximumLegacyPhase = 120;

    public static void Validate(EffectParticleCheckpoint checkpoint)
    { _ = validated.GetValue(checkpoint, ValidateOnce); }
    private static Validated ValidateOnce(EffectParticleCheckpoint checkpoint)
    {
        if (checkpoint == null || !Finite(checkpoint.ObservedAt) || Math.Abs(checkpoint.ObservedAt) >= 1e12 ||
            !Finite(checkpoint.RootPhase) || checkpoint.RootPhase < 0 || checkpoint.RootPhase >= 1e7 ||
            checkpoint.Layout == null || checkpoint.Layout.Length == 0 || checkpoint.Layout.Length > MaximumLayoutLength ||
            checkpoint.Count < 0 || checkpoint.Count > MaximumParticles || checkpoint.State == null ||
            checkpoint.State.Length == 0 || checkpoint.State.Length > MaximumStateBytes || checkpoint.Data == null ||
            checkpoint.Data.Length > MaximumDataBytes || (checkpoint.Count == 0 ? checkpoint.Data.Length != 0 :
                checkpoint.Data.Length == 0 || checkpoint.Data.Length % checkpoint.Count != 0 ||
                checkpoint.Data.Length / checkpoint.Count > MaximumParticleStride) ||
            checkpoint.Digest == null || checkpoint.Digest.Length != 64)
            throw new InvalidDataException("Invalid particle checkpoint bounds.");
        if (!string.Equals(checkpoint.Digest, Digest(checkpoint), StringComparison.Ordinal))
            throw new InvalidDataException("Particle checkpoint checksum mismatch.");
        return new Validated();
    }

    // Observation time is deliberately excluded: cropping changes only the
    // replay clock, and does not alter the native state/phase payload.
    public static string Digest(EffectParticleCheckpoint checkpoint)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(checkpoint.Layout));
        hash.AppendData(BitConverter.GetBytes(checkpoint.Count));
        hash.AppendData(BitConverter.GetBytes(checkpoint.RootPhase));
        hash.AppendData(checkpoint.State); hash.AppendData(checkpoint.Data);
        var digest = hash.GetHashAndReset();
        return BitConverter.ToString(digest).Replace("-", "");
    }
    public static long Estimate(EffectParticleCheckpoint checkpoint) =>
        256L + checkpoint.Layout.Length * 2L + checkpoint.Digest.Length * 2L + checkpoint.State.Length + checkpoint.Data.Length;

    public static EffectParticleCheckpoint Rebase(EffectParticleCheckpoint checkpoint, double origin) =>
        origin == 0 ? checkpoint : new EffectParticleCheckpoint
        {
            ObservedAt = checkpoint.ObservedAt - origin, RootPhase = checkpoint.RootPhase,
            Layout = checkpoint.Layout, Count = checkpoint.Count, State = checkpoint.State,
            Data = checkpoint.Data, Digest = checkpoint.Digest,
        };
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

internal sealed class EffectParticleCheckpointRebaser
{
    private readonly double origin;
    // Legacy checkpoints can carry multi-MiB payloads. Keep only the smallest
    // recent working set; the output shares immutable Data rather than copying.
    private readonly BoundedReferenceCache<EffectParticleCheckpoint, EffectParticleCheckpoint> checkpoints = new(16);
    private readonly BoundedReferenceCache<EffectParticleFrame[], EffectParticleFrame[]> arrays = new(16);
    private readonly Func<EffectParticleCheckpoint, EffectParticleCheckpoint> shift;
    private readonly Func<EffectParticleFrame[], EffectParticleFrame[]> shiftArray;
    internal EffectParticleCheckpointRebaser(double origin)
    { this.origin = origin; shift = source => EffectParticleCheckpointRules.Rebase(source, origin); shiftArray = Shift; }
    internal EffectParticleFrame[]? Apply(EffectParticleFrame[]? source) =>
        source == null || origin == 0 ? source : arrays.GetValue(source, shiftArray);
    private EffectParticleFrame[] Shift(EffectParticleFrame[] source)
    {
        EffectParticleFrame[]? result = null;
        for (int i = 0; i < source.Length; i++)
        {
            var frame = source[i]; if (frame.Checkpoint == null) continue;
            result ??= (EffectParticleFrame[])source.Clone();
            result[i] = new EffectParticleFrame(frame.Pose, frame.Colors, frame.Parameters)
            {
                Emitting = frame.Emitting, Paused = frame.Paused, EmissionEnabled = frame.EmissionEnabled,
                ColorMode = frame.ColorMode, MaterialVariant = frame.MaterialVariant,
                SimulationSpace = frame.SimulationSpace, MeshShapeSpace = frame.MeshShapeSpace,
                Checkpoint = checkpoints.GetValue(frame.Checkpoint, shift),
            };
        }
        return result ?? source;
    }
}
