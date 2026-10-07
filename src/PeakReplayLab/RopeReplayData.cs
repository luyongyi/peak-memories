using System;
using System.Collections.Generic;
using System.IO;

namespace PeakReplayLab;

// These are observed world-space samples, never instructions to simulate a rope.
// Published frames/arrays are immutable by convention and shared until something changes.
public sealed class RopeReplayPoint
{
    public RopeReplayPoint() : this(new float[3], new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f }) { }
    // Capture already owns immutable arrays; don't allocate three default arrays
    // only to discard them while assigning the observed values.
    public RopeReplayPoint(float[] position, float[] rotation, float[] scale)
    { Position = position; Rotation = rotation; Scale = scale; }
    public float[] Position { get; set; }
    public float[] Rotation { get; set; }
    public float[] Scale { get; set; }
    public float Radius { get; set; }
    public float Height { get; set; }
}

public sealed class RopeReplayFrame
{
    public string Key { get; set; } = "";
    public string Kind { get; set; } = ""; // rope / vine / hook / anchor
    public string Resource { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public ObjectPose Pose { get; set; } = new();
    public bool Visible { get; set; }
    public int Attachment { get; set; }
    public string CollisionKind { get; set; } = "none"; // capsule / box / none
    public RopeReplayPoint[] Points { get; set; } = Array.Empty<RopeReplayPoint>();
    // Native RopeBoneVisualizer output, not skinned mesh vertices. Source points remain
    // available for honest collision/attachment inspection; these 41 bones preserve the
    // game's endpoint folding and remote-client visual smoothing without new physics.
    public RopeReplayPoint[] Bones { get; set; } = Array.Empty<RopeReplayPoint>();
    public float Width { get; set; }
    public float Cutoff { get; set; } = 1;
    public float Hang { get; set; }
    public float LengthScale { get; set; } = 1;
    public float Jitter { get; set; }
}

public static class RopeReplayRules
{
    public const int MaximumEntities = 256;
    public const int MaximumPoints = 128;
    public const int MaximumBones = 64;
    public static readonly Type[] KnownTypes = { typeof(RopeReplayFrame), typeof(RopeReplayPoint) };
    public static RopeReplayFrame Rebase(RopeReplayFrame frame, double start) => frame;

    public static void Validate(RopeReplayFrame frame)
    {
        if (frame == null) throw new InvalidDataException("Missing rope frame.");
        Text(frame.Key, 256, true); Text(frame.Resource, 256, true); Text(frame.SourcePath, 2048, false);
        if (frame.Kind != "rope" && frame.Kind != "vine" && frame.Kind != "hook" && frame.Kind != "anchor")
            throw new InvalidDataException("Unknown rope kind.");
        if (frame.CollisionKind != "none" && frame.CollisionKind != "capsule" && frame.CollisionKind != "box")
            throw new InvalidDataException("Unknown rope collision shape.");
        if (frame.Attachment < 0 || frame.Attachment > 2 || frame.Pose == null || frame.Pose.Nodes == null || frame.Pose.Nodes.Length > 256)
            throw new InvalidDataException("Invalid rope state.");
        Vector(frame.Pose.Position, 3, 1_000_000); Quaternion(frame.Pose.Rotation); Vector(frame.Pose.Scale, 3, 1000);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in frame.Pose.Nodes)
        {
            if (node == null) throw new InvalidDataException("Missing rope node.");
            Text(node.Path, 2048, true);
            if (!paths.Add(node.Path)) throw new InvalidDataException("Duplicate rope node.");
            Vector(node.Position, 3, 1_000_000); Quaternion(node.Rotation); Vector(node.Scale, 3, 1000);
        }
        int limit = frame.Kind == "rope" ? 40 : frame.Kind == "vine" ? 50 : frame.Kind == "anchor" ? 0 : MaximumPoints;
        Points(frame.Points, limit); Points(frame.Bones, MaximumBones);
        if (frame.Kind != "rope" && frame.Bones.Length != 0) throw new InvalidDataException("Only native ropes have rope bones.");
        Nonnegative(frame.Width, 1000); Bounded(frame.Cutoff, 10); Bounded(frame.Hang, 10000);
        Nonnegative(frame.LengthScale, 1000); Nonnegative(frame.Jitter, 10000);
    }

    public static bool CanInterpolate(RopeReplayFrame left, RopeReplayFrame right) =>
        left.Key == right.Key && left.Kind == right.Kind && left.Resource == right.Resource &&
        left.Visible && right.Visible && left.Attachment == right.Attachment && left.CollisionKind == right.CollisionKind &&
        left.Points.Length == right.Points.Length && left.Bones.Length == right.Bones.Length;

    public static long Estimate(RopeReplayFrame frame)
    {
        long bytes = 384L + TextBytes(frame.Key) + TextBytes(frame.Kind) + TextBytes(frame.Resource) + TextBytes(frame.SourcePath) + TextBytes(frame.CollisionKind);
        bytes += 8L * (frame.Points.Length + frame.Bones.Length + frame.Pose.Nodes.Length);
        bytes += 184L * (frame.Points.Length + frame.Bones.Length);
        foreach (var node in frame.Pose.Nodes) bytes += 184L + TextBytes(node.Path);
        return bytes;
    }
    private static long TextBytes(string value) => 24L + value.Length * 2L;
    private static void Points(RopeReplayPoint[] values, int limit)
    {
        if (values == null || values.Length > limit) throw new InvalidDataException("Rope point count exceeds limits.");
        foreach (var point in values)
        {
            if (point == null) throw new InvalidDataException("Missing rope point.");
            Vector(point.Position, 3, 1_000_000); Quaternion(point.Rotation); Vector(point.Scale, 3, 1000);
            Nonnegative(point.Radius, 1000); Nonnegative(point.Height, 10000);
        }
    }
    private static void Text(string text, int max, bool required)
    { if (text == null || text.Length > max || required && string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Invalid rope identity."); }
    private static void Vector(float[] value, int length, float max)
    {
        if (value == null || value.Length != length) throw new InvalidDataException("Invalid rope vector.");
        foreach (float number in value) Bounded(number, max);
    }
    private static void Quaternion(float[] value)
    {
        Vector(value, 4, 2);
        double magnitude = 0; foreach (float number in value) magnitude += number * number;
        if (magnitude < .01 || magnitude > 4) throw new InvalidDataException("Invalid rope rotation.");
    }
    private static void Nonnegative(float value, float max)
    { Bounded(value, max); if (value < 0) throw new InvalidDataException("Negative rope dimension."); }
    private static void Bounded(float value, float max)
    { if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > max) throw new InvalidDataException("Invalid rope number."); }
}

// Authored JungleVine bridges/chains belong to the already-loaded map. They must
// never consume the finite dynamic-entity budget ahead of a newly fired rope.
public static class RopeReplayAdmission
{
    public static bool Record(string kind, bool active, bool spawnedVine) => active &&
        (kind == "rope" || kind == "hook" || kind == "anchor" || kind == "vine" && spawnedVine);
    public static bool CanReplace(bool sourceExists, bool active, bool visible) => !sourceExists || !active || !visible;
}
