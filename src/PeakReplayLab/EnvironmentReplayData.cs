using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;

namespace PeakReplayLab;

public sealed class EnvironmentReplayFrame
{
    // Schema 13 preserves every real 20 Hz observation, including stationary
    // samples. Older formats leave the observation clock explicitly unknown.
    public bool SampleTimeKnown { get; set; }
    public double SampleTime { get; set; }
    public EnvironmentWindFrame[] Winds { get; set; } = Array.Empty<EnvironmentWindFrame>();
    public EnvironmentLavaFrame[] Lava { get; set; } = Array.Empty<EnvironmentLavaFrame>();
    public EnvironmentFogFrame? Fog { get; set; }
    public float WeatherBlend { get; set; }
    public float GlobalWind { get; set; }
    public float RainFactor { get; set; }
    public float SnowFactor { get; set; }
    public float HeightFogAmount { get; set; }
    public float HeightFogSpirit { get; set; }
}

public sealed class EnvironmentWindFrame
{
    public string Key { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Active { get; set; }
    public float[] Direction { get; set; } = new float[3];
    public float Intensity { get; set; }
    public float StormProgress { get; set; }
    public float TimeUntilStorm { get; set; }
    public float SecondsUntilSwitch { get; set; }
    public float Duration { get; set; }
    public float ActiveFor { get; set; }
    public EnvironmentStormFrame[] Storms { get; set; } = Array.Empty<EnvironmentStormFrame>();
}

public sealed class EnvironmentStormFrame
{
    public string Key { get; set; } = "";
    public int Kind { get; set; }
    public float Factor { get; set; }
    public float Intensity { get; set; }
}

public sealed class EnvironmentLavaFrame
{
    public string Key { get; set; } = "";
    public int Kind { get; set; }
    public bool Active { get; set; }
    public float[] Position { get; set; } = new float[3];
    public bool Started { get; set; }
    public bool Ended { get; set; }
    public float ProgressTime { get; set; }
    public bool FogPlaneActive { get; set; }
}

public sealed class EnvironmentFogFrame
{
    public string Key { get; set; } = "";
    public float[] Point { get; set; } = new float[3];
    public float Size { get; set; }
    public float Padding { get; set; }
    public float Enable { get; set; }
    public float Reveal { get; set; }
    public float CloseFog { get; set; }
    public bool Active { get; set; }
    public int Origin { get; set; }
    public bool Moving { get; set; }
    public bool Arrived { get; set; }
}

public static class EnvironmentReplayRules
{
    public const int MaximumWinds = 16, MaximumLava = 32, MaximumStorms = 8;
    // OrbFogHandler evaluates its authored Hermite curve without Clamp01; its
    // installed fade has a 1.001134... peak. DisableFog also writes 1-c/t after
    // incrementing c, so its final coroutine frame can be negative. Preserve
    // these raw shader values within a bounded one-unit overshoot envelope.
    public const float MinimumFogEnable = -1, MaximumFogEnable = 2;
    public static void Validate(EnvironmentReplayFrame? frame)
    {
        if (frame == null) return;
        if (!ReplayRules.Finite(frame.SampleTime) || frame.SampleTimeKnown && frame.SampleTime < -1)
            throw Error(nameof(frame.SampleTime), null, "value=" + frame.SampleTime.ToString("R", CultureInfo.InvariantCulture) + "; expected a finite observation clock, with opening baseline >= -1.");
        if (frame.Winds == null || frame.Winds.Length > MaximumWinds || frame.Lava == null || frame.Lava.Length > MaximumLava)
            throw Error("Directories", null, "Wind or lava directory is missing or exceeds its entity limit.");
        Check(frame.WeatherBlend, -10, 10, nameof(frame.WeatherBlend)); Check(frame.GlobalWind, -10, 10, nameof(frame.GlobalWind));
        Check(frame.RainFactor, 0, 10, nameof(frame.RainFactor)); Check(frame.SnowFactor, 0, 10, nameof(frame.SnowFactor));
        Check(frame.HeightFogAmount, -10, 10, nameof(frame.HeightFogAmount)); Check(frame.HeightFogSpirit, -10, 10, nameof(frame.HeightFogSpirit));
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var wind in frame.Winds)
        {
            if (wind == null) throw Error(nameof(frame.Winds), null, "Null wind entry."); Key(wind.Key, keys); Vector(wind.Direction, nameof(wind.Direction), wind.Key);
            Check(wind.Intensity, -10, 10, nameof(wind.Intensity), wind.Key);
            // These two native fields really are clamped normalized progress,
            // unlike the unclamped coroutine/AnimationCurve fog enable value.
            Check(wind.StormProgress, 0, 1, nameof(wind.StormProgress), wind.Key); Check(wind.TimeUntilStorm, 0, 1, nameof(wind.TimeUntilStorm), wind.Key);
            Check(wind.SecondsUntilSwitch, -3600, 14400, nameof(wind.SecondsUntilSwitch), wind.Key); Check(wind.Duration, 0, 14400, nameof(wind.Duration), wind.Key); Check(wind.ActiveFor, 0, 14400, nameof(wind.ActiveFor), wind.Key);
            if (wind.Storms == null || wind.Storms.Length > MaximumStorms) throw Error(nameof(wind.Storms), wind.Key, "Storm directory is missing or exceeds its entity limit.");
            foreach (var storm in wind.Storms)
            {
                if (storm == null || storm.Kind < 0 || storm.Kind > 2) throw Error("Storm.Kind", wind.Key, "Null or unsupported storm kind.");
                Key(storm.Key, keys); Check(storm.Factor, 0, 10, nameof(storm.Factor), storm.Key); Check(storm.Intensity, -10, 10, nameof(storm.Intensity), storm.Key);
            }
        }
        foreach (var lava in frame.Lava)
        {
            if (lava == null || lava.Kind < 0 || lava.Kind > 1) throw Error("Lava.Kind", null, "Null or unsupported lava kind.");
            Key(lava.Key, keys); Vector(lava.Position, nameof(lava.Position), lava.Key); Check(lava.ProgressTime, 0, 14400, nameof(lava.ProgressTime), lava.Key);
        }
        if (frame.Fog is { } fog)
        {
            Key(fog.Key, keys); Vector(fog.Point, nameof(fog.Point), fog.Key); Check(fog.Size, -1000, 10000000, nameof(fog.Size), fog.Key); Check(fog.Padding, 0, 100000, nameof(fog.Padding), fog.Key);
            Check(fog.Enable, MinimumFogEnable, MaximumFogEnable, nameof(fog.Enable), fog.Key);
            Check(fog.Reveal, 0, 1, nameof(fog.Reveal), fog.Key); Check(fog.CloseFog, 0, 10, nameof(fog.CloseFog), fog.Key);
            if (fog.Origin < 0 || fog.Origin > 128) throw Error(nameof(fog.Origin), fog.Key, "Unsupported origin=" + fog.Origin.ToString(CultureInfo.InvariantCulture));
        }
    }
    public static long Estimate(EnvironmentReplayFrame? frame)
    {
        if (frame == null) return 0;
        // Keep legacy decoded-page indices comparable. The pre-clock 120-byte
        // conservative wrapper estimate still covers the expanded unknown DTO;
        // known Schema 13 samples additionally retain their observation clock.
        long bytes = 120 + (frame.SampleTimeKnown ? 16 : 0) + 48 + (frame.Winds.Length + frame.Lava.Length) * 8L;
        foreach (var wind in frame.Winds)
        {
            bytes += 180 + wind.Key.Length * 2L + wind.Storms.Length * 8L;
            foreach (var storm in wind.Storms) bytes += 64 + storm.Key.Length * 2L;
        }
        foreach (var lava in frame.Lava) bytes += 144 + lava.Key.Length * 2L;
        if (frame.Fog is { } fog) bytes += 160 + fog.Key.Length * 2L;
        return bytes;
    }
    private static void Key(string key, HashSet<string> keys)
    { if (string.IsNullOrEmpty(key) || key.Length > 2048 || !keys.Add(key)) throw Error("Key", key, "Empty, too long or duplicate native identity."); }
    private static void Check(float value, float min, float max, string field, string? key = null, int axis = -1)
    {
        if (!ReplayRules.Finite(value) || value < min || value > max)
            throw Error(field, key, (axis < 0 ? "" : "axis=" + axis.ToString(CultureInfo.InvariantCulture) + "; ") +
                "value=" + value.ToString("R", CultureInfo.InvariantCulture) + "; finite range=[" + min.ToString("R", CultureInfo.InvariantCulture) + "," + max.ToString("R", CultureInfo.InvariantCulture) + "].");
    }
    private static void Vector(float[] value, string field, string key)
    { if (value == null || value.Length != 3) throw Error(field, key, "Expected a three-component vector."); for (int i = 0; i < 3; i++) Check(value[i], -1000000, 1000000, field, key, i); }
    private static InvalidDataException Error(string field, string? key, string reason) => new("Invalid recorded environment state: field=" + field +
        (string.IsNullOrEmpty(key) ? "" : "; source=" + (key!.Length <= 160 ? key : key.Substring(0, 160) + "…")) + "; " + reason);
}

// Numeric state blends only inside a single uninterrupted event. Wind direction
// never sweeps between separate gusts, and unknown endpoints never create calm.
public static class EnvironmentReplayMath
{
    public static float Mix(bool sameEvent, float mix) => sameEvent ? Math.Max(0, Math.Min(1, mix)) : 0;
    public static bool SameWindEvent(EnvironmentWindFrame left, EnvironmentWindFrame right) =>
        left.Key == right.Key && left.Enabled == right.Enabled && left.Active == right.Active &&
        left.Direction[0] == right.Direction[0] && left.Direction[1] == right.Direction[1] && left.Direction[2] == right.Direction[2] &&
        (!left.Active || right.ActiveFor + .001f >= left.ActiveFor);
    public static float Lerp(float left, float right, float mix) => left + (right - left) * mix;
    public static bool SameLavaEvent(EnvironmentLavaFrame left, EnvironmentLavaFrame right) =>
        left.Key == right.Key && left.Kind == right.Kind && left.Active == right.Active && left.Started == right.Started &&
        left.Ended == right.Ended && right.ProgressTime + .001f >= left.ProgressTime;
}
