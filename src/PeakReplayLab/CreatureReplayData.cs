using System;
using System.Collections.Generic;
using System.IO;

namespace PeakReplayLab;

// Observed mesh/rig output, not enemy commands. State is diagnostic; the
// sampled native bones drive movement, attack, stun and death visuals.
public sealed class CreatureReplayFrame
{
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Resource { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public int State { get; set; }
    public ObjectPose Pose { get; set; } = new();
    public CreatureReplayLine? Line { get; set; }
    public NativeRendererFrame[]? Visuals { get; set; }
}

public sealed class CreatureReplayLine
{
    public bool Enabled { get; set; }
    public bool WorldSpace { get; set; }
    public float Width { get; set; }
    public float[] Points { get; set; } = Array.Empty<float>(); // flattened native xyz
}

public sealed class WebWrapReplayFrame
{
    public WebWrapReplayPart[] Parts { get; set; } = Array.Empty<WebWrapReplayPart>();
}

public sealed class WebWrapReplayPart
{
    public string Path { get; set; } = "";
    public bool Active { get; set; }
    public float Clip { get; set; }
    public float[] Rotation { get; set; } = new[] { 0f, 0f, 0f, 1f };
    public float[] ParentScale { get; set; } = new[] { 1f, 1f, 1f };
}

public static class CreatureReplayRules
{
    public const int MaximumDynamicEntities = 256;
    public const int MaximumStaticTraps = 8192;
    public const int MaximumEntities = MaximumDynamicEntities + MaximumStaticTraps;
    public const int MaximumNodes = 1024;
    public const int MaximumLinePoints = 128;
    public static readonly Type[] KnownTypes = { typeof(CreatureReplayFrame), typeof(CreatureReplayLine), typeof(WebWrapReplayFrame), typeof(WebWrapReplayPart) };
    public static bool CompactStaticTrap(CreatureReplayFrame frame) => frame.Kind == "spore-trap" &&
        frame.SourcePath.Length != 0 && frame.Pose.Nodes.Length == 0 && frame.Visuals == null && frame.Line == null;
    public static void ValidateCounts(CreatureReplayFrame[] frames)
    {
        int dynamic = 0, traps = 0, detailedTraps = 0;
        foreach (var frame in frames)
        {
            if (frame.Kind == "spore-trap")
            {
                if (++traps > MaximumStaticTraps) throw new InvalidDataException("Static trap directory exceeds its limit.");
                // Preserve old full-pose trap recordings without allowing thousands
                // of detailed hierarchies through the new compact static allowance.
                if (!CompactStaticTrap(frame) && ++detailedTraps > MaximumDynamicEntities)
                    throw new InvalidDataException("Detailed trap directory exceeds its legacy limit.");
            }
            else if (++dynamic > MaximumDynamicEntities) throw new InvalidDataException("Dynamic creature directory exceeds its limit.");
        }
    }
    public static CreatureReplayFrame Rebase(CreatureReplayFrame frame, double start) => frame;
    public static bool CanInterpolate(CreatureReplayFrame a, CreatureReplayFrame b) =>
        a.Key == b.Key && a.Kind == b.Kind && a.Resource == b.Resource && a.SourcePath == b.SourcePath && a.Pose.Active && b.Pose.Active;
    public static void Validate(CreatureReplayFrame frame)
    {
        if (frame == null || frame.Pose == null) throw new InvalidDataException("Missing recorded creature.");
        Text(frame.Key, 256, true); Text(frame.Kind, 64, true); Text(frame.Resource, 256, true); Text(frame.SourcePath, 2048, false);
        if (frame.Resource != frame.Kind || frame.Kind != "spider" && frame.Kind != "mushroom-zombie" && frame.Kind != "spore-trap")
            throw new InvalidDataException("Unknown native creature resource.");
        if (frame.State < 0 || frame.State > (frame.Kind == "spider" ? 3 : frame.Kind == "mushroom-zombie" ? 6 : 1))
            throw new InvalidDataException("Invalid native creature state.");
        if (frame.Pose.Nodes == null || frame.Pose.Nodes.Length > MaximumNodes) throw new InvalidDataException("Creature hierarchy exceeds limits.");
        Vector(frame.Pose.Position, 3, 1_000_000); Quaternion(frame.Pose.Rotation); Vector(frame.Pose.Scale, 3, 1000);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in frame.Pose.Nodes)
        {
            if (node == null) throw new InvalidDataException("Missing creature node.");
            Text(node.Path, 2048, true);
            if (!paths.Add(node.Path)) throw new InvalidDataException("Duplicate creature node.");
            Vector(node.Position, 3, 1_000_000); Quaternion(node.Rotation); Vector(node.Scale, 3, 1000);
        }
        if (frame.Line != null)
        {
            if (frame.Kind != "spider" || frame.Line.Points == null || frame.Line.Points.Length % 3 != 0 || frame.Line.Points.Length > MaximumLinePoints * 3)
                throw new InvalidDataException("Invalid spider silk line.");
            Vector(frame.Line.Points, frame.Line.Points.Length, 1_000_000);
            if (!Finite(frame.Line.Width) || frame.Line.Width < 0 || frame.Line.Width > 1000) throw new InvalidDataException("Invalid spider silk width.");
        }
        if (!NativeVisualAppearanceRules.Validate(frame.Visuals)) throw new InvalidDataException("Invalid native creature appearance.");
    }
    public static long Estimate(CreatureReplayFrame frame)
    {
        long bytes = 448L + 2L * (frame.Key.Length + frame.Kind.Length + frame.Resource.Length + frame.SourcePath.Length) + frame.Pose.Nodes.Length * 8L;
        foreach (var node in frame.Pose.Nodes) bytes += 224L + node.Path.Length * 2L;
        if (frame.Line != null) bytes += 96L + frame.Line.Points.Length * 4L;
        bytes += NativeVisualAppearanceRules.Estimate(frame.Visuals);
        return bytes;
    }
    internal static void Text(string value, int max, bool required)
    { if (value == null || value.Length > max || required && string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Invalid creature identity."); }
    internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    internal static void Vector(float[] values, int length, float max)
    {
        if (values == null || values.Length != length) throw new InvalidDataException("Invalid creature vector.");
        foreach (float value in values) if (!Finite(value) || Math.Abs(value) >= max) throw new InvalidDataException("Invalid creature number.");
    }
    internal static void Quaternion(float[] values)
    {
        Vector(values, 4, 10); double norm = 0; foreach (float value in values) norm += value * (double)value;
        if (norm < .5 || norm > 1.5) throw new InvalidDataException("Invalid creature orientation.");
    }
}

public static class WebWrapReplayRules
{
    public const int MaximumParts = 32;
    public static void Validate(WebWrapReplayFrame? frame)
    {
        if (frame == null) return;
        if (frame.Parts == null || frame.Parts.Length > MaximumParts) throw new InvalidDataException("Web wraps exceed limits.");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in frame.Parts)
        {
            if (part == null) throw new InvalidDataException("Missing web wrap.");
            CreatureReplayRules.Text(part.Path, 2048, true);
            if (!paths.Add(part.Path) || !CreatureReplayRules.Finite(part.Clip) || part.Clip < 0 || part.Clip > 1)
                throw new InvalidDataException("Invalid recorded web wrap.");
            CreatureReplayRules.Quaternion(part.Rotation); CreatureReplayRules.Vector(part.ParentScale, 3, 1000);
        }
    }
    public static long Estimate(WebWrapReplayFrame? frame)
    {
        if (frame == null) return 0;
        long bytes = 72 + frame.Parts.Length * 8L;
        foreach (var part in frame.Parts) bytes += 176L + part.Path.Length * 2L;
        return bytes;
    }
}
