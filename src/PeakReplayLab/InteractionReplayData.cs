using System;
using System.Collections.Generic;
using System.IO;

namespace PeakReplayLab;

// Passive observations only. Neither resources nor snapshots are executable
// gameplay commands. Every resource is resolved through a native-family allowlist.
public sealed class InteractionReplayFrame
{
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Resource { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public ObjectPose Pose { get; set; } = new();
    public NativeRendererFrame[]? Visuals { get; set; }
    public NativeLightFrame[]? Lights { get; set; }
    public InteractionLineFrame? Line { get; set; }
    public InteractionTextFrame? Text { get; set; }
    public float? GloomAmount { get; set; }
    public float[]? PostWeights { get; set; }
    public bool[]? PostEnabled { get; set; }
}

public sealed class InteractionLineFrame
{
    public bool Visible { get; set; }
    public float[] Points { get; set; } = Array.Empty<float>();
    public float[] Widths { get; set; } = Array.Empty<float>();
    public float[] StartColor { get; set; } = new[] { 1f, 1f, 1f, 1f };
    public float[] EndColor { get; set; } = new[] { 1f, 1f, 1f, 1f };
}

public sealed class InteractionTextFrame
{
    public bool Visible { get; set; }
    public string Content { get; set; } = "";
    public float[] Color { get; set; } = new[] { 1f, 1f, 1f, 1f };
    public float FontSize { get; set; }
}

public static class InteractionReplayRules
{
    [ThreadStatic] private static HashSet<string>? validationPaths;
    public const int MaximumEntities = 768;
    public const int MaximumNodes = 1024;
    public const int MaximumLinePoints = 16;
    public static readonly Type[] KnownTypes = { typeof(InteractionReplayFrame), typeof(InteractionLineFrame), typeof(InteractionTextFrame) };
    public static bool World(string kind) => kind == "scout-statue" || kind == "ghost-fire" || kind == "soul-pillar" || kind == "campfire" || kind == "peak-portal";
    public static bool PostKind(string kind) => kind == "peak-portal" || kind == "warp-compass";
    public static bool LineKind(string kind) => kind == "clone-line" || kind == "arrow-line";
    public static bool Kind(string kind) => World(kind) || PostKind(kind) || kind == "break-debris" || kind == "inert-rocket" || LineKind(kind) || kind == "bing-subtitle" || kind == "turtle-shell" || kind == "stamina-sphere" || kind == "warning-arrow";
    public static InteractionReplayFrame Rebase(InteractionReplayFrame value, double start) => value;
    public static bool CanInterpolate(InteractionReplayFrame left, InteractionReplayFrame right) =>
        left.Key == right.Key && left.Kind == right.Kind && left.Resource == right.Resource && left.SourcePath == right.SourcePath && left.Pose.Active && right.Pose.Active;
    public static bool ShouldProbe(double now, double next, bool dirty) => dirty || now >= next;
    public static double NextProbe(double now, int identity, bool moving) => now + (moving ? 1.0 / 30 : 1 + (identity & 31) * .017);
    public static void Validate(InteractionReplayFrame value)
    {
        if (value == null) throw new InvalidDataException("Missing interaction.");
        Text(value.Key, 256, true); Text(value.Resource, 2048, true); Text(value.SourcePath, 2048, false);
        if (!Kind(value.Kind) || World(value.Kind) && string.IsNullOrWhiteSpace(value.SourcePath)) throw new InvalidDataException("Invalid interaction family.");
        if (value.Pose == null || value.Pose.Nodes == null || value.Pose.Nodes.Length > MaximumNodes) throw new InvalidDataException("Invalid interaction pose.");
        Vector(value.Pose.Position, 3, 1e7f); Rotation(value.Pose.Rotation); Vector(value.Pose.Scale, 3, 1e5f);
        var paths = validationPaths ??= new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var node in value.Pose.Nodes)
            {
                if (node == null) throw new InvalidDataException("Missing interaction node.");
                Text(node.Path, 2048, true); if (!paths.Add(node.Path)) throw new InvalidDataException("Duplicate interaction node.");
                Vector(node.Position, 3, 1e7f); Rotation(node.Rotation); Vector(node.Scale, 3, 1e5f);
            }
        }
        finally { paths.Clear(); }
        if (!NativeVisualAppearanceRules.Validate(value.Visuals) || !NativeLightRules.Validate(value.Lights)) throw new InvalidDataException("Invalid interaction appearance.");
        if ((value.Line != null) != LineKind(value.Kind) || (value.Text != null) != (value.Kind == "bing-subtitle")) throw new InvalidDataException("Invalid interaction payload.");
        if (value.Kind == "arrow-line" && string.IsNullOrWhiteSpace(value.SourcePath)) throw new InvalidDataException("Missing native arrow trap identity.");
        if (value.GloomAmount.HasValue && value.Kind != "ghost-fire") throw new InvalidDataException("Invalid gloom owner.");
        if (value.GloomAmount.HasValue) Number(value.GloomAmount.Value, 10);
        if ((value.PostWeights != null) != (value.PostEnabled != null) || value.PostWeights != null && !PostKind(value.Kind))
            throw new InvalidDataException("Invalid native post-processing owner.");
        if (value.PostWeights != null)
        { Vector(value.PostWeights, 2, 10); if (value.PostEnabled!.Length != 2) throw new InvalidDataException("Invalid post-processing switches."); }
        if (value.Line != null)
        {
            var line = value.Line;
            if (line.Points == null || line.Widths == null || line.Points.Length % 3 != 0 || line.Points.Length > MaximumLinePoints * 3 || line.Widths.Length != line.Points.Length / 3) throw new InvalidDataException("Invalid interaction line.");
            foreach (float p in line.Points) Number(p, 1e7f);
            foreach (float width in line.Widths) { Number(width, 1000); if (width < 0) throw new InvalidDataException("Negative line width."); }
            Vector(line.StartColor, 4, 10000); Vector(line.EndColor, 4, 10000);
        }
        if (value.Text != null)
        {
            Text(value.Text.Content, 4096, false); Vector(value.Text.Color, 4, 10000); Number(value.Text.FontSize, 10000);
            if (value.Text.FontSize < 0) throw new InvalidDataException("Negative subtitle size.");
        }
    }
    public static long Estimate(InteractionReplayFrame value)
    {
        long result = 480L + (value.Key.Length + value.Kind.Length + value.Resource.Length + value.SourcePath.Length) * 2L + value.Pose.Nodes.Length * 8L;
        foreach (var node in value.Pose.Nodes) result += 224L + node.Path.Length * 2L;
        if (value.Line != null) result += 192L + (value.Line.Points.Length + value.Line.Widths.Length) * 4L;
        if (value.Text != null) result += 112L + value.Text.Content.Length * 2L;
        if (value.PostWeights != null) result += 64L;
        return result + NativeVisualAppearanceRules.Estimate(value.Visuals) + NativeLightRules.Estimate(value.Lights);
    }
    private static void Text(string value, int max, bool required)
    { if (value == null || value.Length > max || required && string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Invalid interaction text."); }
    private static void Vector(float[] value, int count, float max)
    { if (value == null || value.Length != count) throw new InvalidDataException("Invalid interaction vector."); foreach (float n in value) Number(n, max); }
    private static void Number(float value, float max)
    { if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > max) throw new InvalidDataException("Invalid interaction number."); }
    private static void Rotation(float[] value)
    { Vector(value, 4, 2); double norm = 0; foreach (float n in value) norm += n * n; if (norm < .5 || norm > 1.5) throw new InvalidDataException("Invalid interaction rotation."); }
}
