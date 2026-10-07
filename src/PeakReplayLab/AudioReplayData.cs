using System;
using System.IO;

namespace PeakReplayLab;

// Observed native game SFX only. Never microphone samples, voice chat, or audio bytes.
// One immutable live-voice snapshot; Key changes on every new playback, including
// reuse of the game's pooled AudioSource. Absence from a frame means stopped.
public sealed class AudioReplayFrame
{
    public string Key { get; set; } = "";
    public string Clip { get; set; } = "";
    public double StartedAt { get; set; }
    public double AnchorTime { get; set; }
    public double Offset { get; set; }
    public float Duration { get; set; }
    public float[] Position { get; set; } = new float[3];
    public float Volume { get; set; }
    public float Pitch { get; set; } = 1;
    public bool Loop { get; set; }
    public bool Transient { get; set; } // Started and ended between two recorder samples.
    public double EndTime { get; set; }
    public float SpatialBlend { get; set; }
    public float MinDistance { get; set; } = 1;
    public float MaxDistance { get; set; } = 500;
    public float Doppler { get; set; }
    public int Rolloff { get; set; }
}

public static class AudioReplayRules
{
    public const int MaxVoices = 128;
    public static void Validate(AudioReplayFrame value)
    {
        if (value == null || string.IsNullOrWhiteSpace(value.Key) || value.Key.Length > 128 ||
            string.IsNullOrWhiteSpace(value.Clip) || value.Clip.Length > 1024 ||
            !Finite(value.StartedAt) || Math.Abs(value.StartedAt) > 1e9 ||
            !Finite(value.AnchorTime) || Math.Abs(value.AnchorTime) > 1e9 ||
            !Finite(value.EndTime) || Math.Abs(value.EndTime) > 1e9 ||
            (value.Transient && (value.Loop || value.EndTime < value.StartedAt)) ||
            !Finite(value.Offset) || value.Offset < 0 || value.Offset > 86400 ||
            !Range(value.Duration, .000001f, 86400) || value.Offset > value.Duration + .01 ||
            value.Position == null || value.Position.Length != 3 ||
            !Range(value.Volume, 0, 1) || !Range(value.Pitch, -3, 3) ||
            !Range(value.SpatialBlend, 0, 1) || !Range(value.MinDistance, 0, 100000) ||
            !Range(value.MaxDistance, value.MinDistance, 100000) || !Range(value.Doppler, 0, 5) ||
            value.Rolloff < 0 || value.Rolloff > 2)
            throw new InvalidDataException("Invalid replay game sound.");
        foreach (float coordinate in value.Position)
            if (!Finite(coordinate) || Math.Abs(coordinate) >= 10_000_000)
                throw new InvalidDataException("Invalid replay sound position.");
    }

    public static long Estimate(AudioReplayFrame value) => 224L + (value.Key.Length + value.Clip.Length) * 2L;

    public static AudioReplayFrame Rebase(AudioReplayFrame value, double origin) => origin == 0 ? value : Copy(value, origin);
    private static AudioReplayFrame Copy(AudioReplayFrame value, double origin) => new()
    {
        Key = value.Key, Clip = value.Clip, StartedAt = value.StartedAt - origin,
        AnchorTime = value.AnchorTime - origin, Offset = value.Offset, Duration = value.Duration,
        Position = value.Position, Volume = value.Volume, Pitch = value.Pitch, Loop = value.Loop,
        Transient = value.Transient, EndTime = value.Transient ? value.EndTime - origin : value.EndTime,
        SpatialBlend = value.SpatialBlend, MinDistance = value.MinDistance, MaxDistance = value.MaxDistance,
        Doppler = value.Doppler, Rolloff = value.Rolloff,
    };

    public static AudioReplayFrame CompletedBetweenSamples(AudioReplayFrame value, double time)
    {
        var result = Copy(value, 0);
        result.Transient = true; result.EndTime = Math.Max(value.StartedAt, time);
        return result;
    }

    public static double Cursor(AudioReplayFrame value, double time)
    {
        double cursor = value.Offset + (time - value.AnchorTime) * value.Pitch;
        if (!Finite(cursor) || value.Duration <= 0) return 0;
        if (value.Loop) return ((cursor % value.Duration) + value.Duration) % value.Duration;
        return Math.Max(0, Math.Min(value.Duration, cursor));
    }

    // Pure replay-clock policy: scrub/seek stays silent; the next advancing tick
    // may resume the selected live voice at its cursor, never replay earlier events.
    public static bool ShouldSound(bool paused, float speed, bool discontinuity) =>
        !paused && !discontinuity && Finite(speed) && speed > 0;
    public static bool MayStartTransient(bool transient, bool suppressedBySeek, bool alreadyStarted) =>
        transient && !suppressedBySeek && !alreadyStarted;
    private static bool Range(float value, float min, float max) => Finite(value) && value >= min && value <= max;
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

// Authored curves are resolved from this game build rather than adding another
// recording format. Runtime volume/pitch/position remain recorded values and
// cannot change the identity of the source's spatial configuration.
internal readonly struct AudioReplaySpatialKey : IEquatable<AudioReplaySpatialKey>
{
    public readonly string Clip;
    public readonly float Blend, Min, Max;
    public readonly int Rolloff;
    public AudioReplaySpatialKey(string clip, float blend, float min, float max, int rolloff)
    { Clip = clip; Blend = blend; Min = min; Max = max; Rolloff = rolloff; }
    public AudioReplaySpatialKey(AudioReplayFrame frame)
        : this(frame.Clip, frame.SpatialBlend, frame.MinDistance, frame.MaxDistance, frame.Rolloff) { }
    public bool Equals(AudioReplaySpatialKey other) => StringComparer.Ordinal.Equals(Clip, other.Clip) &&
        Blend == other.Blend && Min == other.Min && Max == other.Max && Rolloff == other.Rolloff;
    public override bool Equals(object? other) => other is AudioReplaySpatialKey value && Equals(value);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = StringComparer.Ordinal.GetHashCode(Clip);
            hash = hash * 31 + Blend.GetHashCode(); hash = hash * 31 + Min.GetHashCode();
            hash = hash * 31 + Max.GetHashCode(); return hash * 31 + Rolloff;
        }
    }
    public bool RequiresNativeRolloff => Blend > 0 && Rolloff == 2;
    // Missing/ambiguous old custom curves must not turn into a global constant
    // gain. Unity's native linear mode uses the distances already in the file;
    // this is an explicitly warned approximation, never a fabricated radius.
    public int MissingCurveFallbackRolloff => RequiresNativeRolloff ? 1 : Rolloff;
}
