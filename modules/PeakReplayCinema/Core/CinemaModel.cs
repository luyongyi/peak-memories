using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace PeakReplayLab.Cinema;

public enum CinemaTransition { Smooth, Linear, Cut }
public enum CinemaPreset { PushPull, Slide, Orbit, Rise }
public enum CinemaColorPreset { Original, Warm, Cool, Mono }
public enum CinemaDofMode { Off, Manual, Actor }
public enum CinemaDofQuality { Low, Medium, High }
public enum CinemaFrameAspect { Native, Widescreen16x9, Cinema21x9 }

public static class CinemaLimits
{
    public const int MaxKeyframes = 2048;
    public const int MaxFileBytes = 2 * 1024 * 1024;
    public const double MaxTime = 7 * 24 * 60 * 60;
    public const float MaxCoordinate = 1000000;
    public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    internal static void Range(double value, double min, double max, string name)
    {
        if (!Finite(value) || value < min || value > max) throw new ArgumentOutOfRangeException(name);
    }
    internal static void Text(string value, int maxLength, string name, bool allowEmpty = false)
    {
        if (value == null || (!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Length > maxLength)
            throw new ArgumentException("Invalid " + name + ".", name);
        foreach (char c in value) if (char.IsControl(c)) throw new ArgumentException("Invalid " + name + ".", name);
    }
    internal static void EnumValue<T>(T value, string name) where T : struct
    {
        if (!Enum.IsDefined(typeof(T), value)) throw new ArgumentOutOfRangeException(name);
    }
}

// Explicit opt-in properties keep computed lengths and other derived values out of JSON.
[JsonObject(MemberSerialization.OptIn)]
public readonly struct CinemaVector3
{
    [JsonProperty("x", Required = Required.Always)] public float X { get; }
    [JsonProperty("y", Required = Required.Always)] public float Y { get; }
    [JsonProperty("z", Required = Required.Always)] public float Z { get; }
    [JsonConstructor]
    public CinemaVector3(float x, float y, float z)
    {
        CinemaLimits.Range(x, -CinemaLimits.MaxCoordinate, CinemaLimits.MaxCoordinate, nameof(x));
        CinemaLimits.Range(y, -CinemaLimits.MaxCoordinate, CinemaLimits.MaxCoordinate, nameof(y));
        CinemaLimits.Range(z, -CinemaLimits.MaxCoordinate, CinemaLimits.MaxCoordinate, nameof(z));
        X = x; Y = y; Z = z;
    }
}

[JsonObject(MemberSerialization.OptIn)]
public readonly struct CinemaQuaternion
{
    [JsonProperty("x", Required = Required.Always)] public float X { get; }
    [JsonProperty("y", Required = Required.Always)] public float Y { get; }
    [JsonProperty("z", Required = Required.Always)] public float Z { get; }
    [JsonProperty("w", Required = Required.Always)] public float W { get; }
    public static CinemaQuaternion Identity => new CinemaQuaternion(0, 0, 0, 1);
    [JsonConstructor]
    public CinemaQuaternion(float x, float y, float z, float w)
    {
        CinemaLimits.Range(x, -1000000, 1000000, nameof(x));
        CinemaLimits.Range(y, -1000000, 1000000, nameof(y));
        CinemaLimits.Range(z, -1000000, 1000000, nameof(z));
        CinemaLimits.Range(w, -1000000, 1000000, nameof(w));
        double length = Math.Sqrt((double)x * x + (double)y * y + (double)z * z + (double)w * w);
        if (length < 0.000001) throw new ArgumentException("Rotation must be a nonzero quaternion.");
        X = (float)(x / length); Y = (float)(y / length); Z = (float)(z / length); W = (float)(w / length);
    }
    internal void Validate()
    {
        double length = (double)X * X + (double)Y * Y + (double)Z * Z + (double)W * W;
        if (!CinemaLimits.Finite(length) || Math.Abs(length - 1) > 0.00001)
            throw new ArgumentException("Rotation must be normalized.");
    }
}

[JsonObject(MemberSerialization.OptIn)]
public readonly struct CinemaPose
{
    [JsonProperty("position", Required = Required.Always)] public CinemaVector3 Position { get; }
    [JsonProperty("rotation", Required = Required.Always)] public CinemaQuaternion Rotation { get; }
    [JsonProperty("fov", Required = Required.Always)] public float FieldOfView { get; }
    [JsonProperty("focusDistance", Required = Required.Always)] public float FocusDistance { get; }
    [JsonConstructor]
    public CinemaPose(CinemaVector3 position, CinemaQuaternion rotation, [JsonProperty("fov")] float fieldOfView = 60, float focusDistance = 10)
    {
        Position = position; Rotation = rotation; FieldOfView = fieldOfView; FocusDistance = focusDistance;
        Validate();
    }
    public void Validate()
    {
        CinemaLimits.Range(Position.X, -CinemaLimits.MaxCoordinate, CinemaLimits.MaxCoordinate, nameof(Position));
        CinemaLimits.Range(Position.Y, -CinemaLimits.MaxCoordinate, CinemaLimits.MaxCoordinate, nameof(Position));
        CinemaLimits.Range(Position.Z, -CinemaLimits.MaxCoordinate, CinemaLimits.MaxCoordinate, nameof(Position));
        Rotation.Validate();
        CinemaLimits.Range(FieldOfView, 10, 140, nameof(FieldOfView));
        CinemaLimits.Range(FocusDistance, 0.05, 100000, nameof(FocusDistance));
    }
}

[JsonObject(MemberSerialization.OptIn)]
public sealed class CinemaKeyframe
{
    [JsonProperty("time", Required = Required.Always)] public double Time { get; }
    [JsonProperty("segmentId", Required = Required.Always)] public string SegmentId { get; }
    [JsonProperty("pose", Required = Required.Always)] public CinemaPose Pose { get; }
    // Transition describes the interval starting at this key. Cut holds it until the next key.
    [JsonProperty("transition", Required = Required.Always)] public CinemaTransition Transition { get; }
    [JsonConstructor]
    public CinemaKeyframe(double time, string segmentId, CinemaPose pose, CinemaTransition transition = CinemaTransition.Smooth)
    {
        CinemaLimits.Range(time, 0, CinemaLimits.MaxTime, nameof(time));
        CinemaLimits.Text(segmentId, 128, nameof(segmentId));
        CinemaLimits.EnumValue(transition, nameof(transition));
        pose.Validate();
        Time = time; SegmentId = segmentId; Pose = pose; Transition = transition;
    }
}

[JsonObject(MemberSerialization.OptIn)]
public sealed class CinemaSettings
{
    [JsonProperty("colorPreset")] public CinemaColorPreset ColorPreset { get; set; } = CinemaColorPreset.Original;
    [JsonProperty("colorStrength")] public float ColorStrength { get; set; } = 0.5f;
    [JsonProperty("exposure")] public float Exposure { get; set; }
    [JsonProperty("contrast")] public float Contrast { get; set; } = 1;
    [JsonProperty("saturation")] public float Saturation { get; set; } = 1;
    [JsonProperty("temperature")] public float Temperature { get; set; }
    [JsonProperty("dofMode")] public CinemaDofMode DofMode { get; set; } = CinemaDofMode.Off;
    [JsonProperty("blur")] public float Blur { get; set; } = 0.25f;
    [JsonProperty("quality")] public CinemaDofQuality Quality { get; set; } = CinemaDofQuality.Medium;
    [JsonProperty("focusActorId")] public string FocusActorId { get; set; } = "";
    [JsonProperty("frameAspect")] public CinemaFrameAspect FrameAspect { get; set; } = CinemaFrameAspect.Native;
    [JsonProperty("grid")] public bool Grid { get; set; }
    [JsonProperty("hideHUD")] public bool HideHud { get; set; } = true;
    [JsonProperty("hideNames")] public bool HideNames { get; set; } = true;
    public void Validate()
    {
        CinemaLimits.EnumValue(ColorPreset, nameof(ColorPreset)); CinemaLimits.EnumValue(DofMode, nameof(DofMode));
        CinemaLimits.EnumValue(Quality, nameof(Quality)); CinemaLimits.EnumValue(FrameAspect, nameof(FrameAspect));
        CinemaLimits.Range(ColorStrength, 0, 1, nameof(ColorStrength)); CinemaLimits.Range(Exposure, -5, 5, nameof(Exposure));
        CinemaLimits.Range(Contrast, 0.5, 1.5, nameof(Contrast)); CinemaLimits.Range(Saturation, 0, 2, nameof(Saturation));
        CinemaLimits.Range(Temperature, -1, 1, nameof(Temperature)); CinemaLimits.Range(Blur, 0, 1, nameof(Blur));
        CinemaLimits.Text(FocusActorId, 256, nameof(FocusActorId), true);
    }
}

[JsonObject(MemberSerialization.OptIn)]
public sealed class CinemaProject
{
    public const int CurrentVersion = 1;
    [JsonProperty("version", Required = Required.Always)] public int Version { get; set; } = CurrentVersion;
    [JsonProperty("recordingFingerprint", Required = Required.Always)] public string Fingerprint { get; set; } = "";
    [JsonProperty("track", Required = Required.Always)] public CinemaTrack Track { get; set; } = new CinemaTrack();
    [JsonProperty("settings", Required = Required.Always)] public CinemaSettings Settings { get; set; } = new CinemaSettings();
    public CinemaProject() { }
    public CinemaProject(string fingerprint) { Fingerprint = fingerprint; Validate(); }
    public void Validate()
    {
        if (Version != CurrentVersion) throw new InvalidDataException("Unsupported cinema project version.");
        CinemaLimits.Text(Fingerprint, 256, nameof(Fingerprint));
        if (Track == null || Settings == null) throw new InvalidDataException("Missing cinema project data.");
        Track.Validate(); Settings.Validate();
    }
}
