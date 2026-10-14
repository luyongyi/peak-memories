using System;
using System.Collections.Generic;
using System.IO;

namespace PeakReplayLab;

// Observed mesh/rig output, not enemy commands. State is diagnostic; the
// sampled native bones drive movement, attack, stun and death visuals.
public sealed class CreatureReplayFrame
{
    // Schema 20: retain the last actual observation through repeated global
    // frames and dormant pages; only a completed native read stamps a new time.
    public bool SampleTimeKnown { get; set; }
    public double SampleTime { get; set; }
    public int ClockMode { get; set; }
    // Schema 25: an exact native event edge, including repeated actions whose
    // state enum did not change. Do not blend toward this future packet early.
    public bool SampleDiscontinuity { get; set; }
    public bool ShouldSerializeSampleDiscontinuity() => SampleDiscontinuity;
    public CreatureIdleAnimation? IdleAnimation { get; set; }
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
    public const int ActiveClock = 0, IdleClock = 1, SleepingClock = 2;
    [ThreadStatic] private static HashSet<string>? validationPaths;
    public const int MaximumDynamicEntities = 256;
    public const int MaximumStaticTraps = 8192;
    public const int MaximumEntities = MaximumDynamicEntities + MaximumStaticTraps;
    public const int MaximumNodes = 1024;
    public const int MaximumLinePoints = 128;
    // The audited native frog uses BezierRenderer's fixed 200-point tongue.
    public const int MaximumFrogLinePoints = 256;
    public static readonly Type[] KnownTypes = { typeof(CreatureReplayFrame), typeof(CreatureReplayLine), typeof(CreatureIdleAnimation), typeof(WebWrapReplayFrame), typeof(WebWrapReplayPart) };
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
    public static CreatureReplayFrame Rebase(CreatureReplayFrame frame, double start) => !frame.SampleTimeKnown || start == 0 ? frame : new()
    {
        SampleTimeKnown = true, SampleTime = frame.SampleTime - start, ClockMode = frame.ClockMode, SampleDiscontinuity = frame.SampleDiscontinuity,
        IdleAnimation = frame.IdleAnimation == null ? null : CreatureIdleAnimationRules.Rebase(frame.IdleAnimation, start),
        Key = frame.Key, Kind = frame.Kind, Resource = frame.Resource, SourcePath = frame.SourcePath,
        State = frame.State, Pose = frame.Pose, Line = frame.Line, Visuals = frame.Visuals,
    };
    public static bool CanInterpolate(CreatureReplayFrame a, CreatureReplayFrame b) =>
        !b.SampleDiscontinuity && a.Key == b.Key && a.Kind == b.Kind && a.Resource == b.Resource && a.SourcePath == b.SourcePath && a.Pose.Active && b.Pose.Active &&
        (!a.SampleTimeKnown && !b.SampleTimeKnown || a.SampleTimeKnown && b.SampleTimeKnown &&
            a.ClockMode == ActiveClock && b.ClockMode == ActiveClock && a.State == b.State);
    // Called once per observation pair while the page index is built, never
    // once per creature per display tick. Visibility/topology changes are cuts.
    internal static bool CanInterpolateObservation(CreatureReplayFrame a, CreatureReplayFrame b)
    {
        if (!CanInterpolate(a, b) || a.Pose.Nodes.Length != b.Pose.Nodes.Length ||
            (a.Line == null) != (b.Line == null) || a.Line != null && b.Line != null &&
            (a.Line.Enabled != b.Line.Enabled || a.Line.WorldSpace != b.Line.WorldSpace || a.Line.Points.Length != b.Line.Points.Length)) return false;
        for (int i = 0; i < a.Pose.Nodes.Length; i++)
        {
            var left = a.Pose.Nodes[i]; var right = b.Pose.Nodes[i];
            if (left.Path != right.Path || left.Active != right.Active || left.Visible != right.Visible) return false;
        }
        return true;
    }
    public static void Validate(CreatureReplayFrame frame)
    {
        if (frame == null || frame.Pose == null) throw new InvalidDataException("Missing recorded creature.");
        if (frame.IdleAnimation != null)
        {
            CreatureIdleAnimationRules.Validate(frame.IdleAnimation);
            if (!frame.SampleTimeKnown ||
                frame.ClockMode != IdleClock || !frame.Pose.Active || frame.IdleAnimation.AnchorTime != frame.SampleTime ||
                !(frame.IdleAnimation.Clip == CreatureIdleAnimationRules.SpiderIdle && frame.Kind == "spider" && frame.State == 0 &&
                    (frame.Resource == "spider-normal" || frame.Resource == "spider-web") &&
                    frame.Line != null && frame.Line.WorldSpace && frame.Line.Points != null && frame.Line.Points.Length == 120 ||
                  frame.Kind == "voice-thief" && frame.Resource == "voice-thief" && frame.Line == null &&
                    (frame.State == 0 && frame.IdleAnimation.Clip == CreatureIdleAnimationRules.RavenIdle ||
                     frame.State == 1 && frame.IdleAnimation.Clip == CreatureIdleAnimationRules.RavenFlyAway)))
                throw new InvalidDataException("Native creature animation requires a matching active checkpoint.");
        }
        if (!ReplayRules.Finite(frame.SampleTime) || Math.Abs(frame.SampleTime) > 1_000_000 ||
            frame.ClockMode < ActiveClock || frame.ClockMode > SleepingClock ||
            !frame.SampleTimeKnown && (frame.SampleTime != 0 || frame.ClockMode != ActiveClock || frame.SampleDiscontinuity))
            throw new InvalidDataException("Invalid creature observation clock.");
        Text(frame.Key, 256, true); Text(frame.Kind, 64, true); Text(frame.Resource, 256, true); Text(frame.SourcePath, 2048, false);
        if (!CreatureDirectoryRules.ValidResource(frame.Kind, frame.Resource))
            throw new InvalidDataException("Unknown native creature resource.");
        // The historical spore-trap record may have a full pose and no path.
        // New scene-only creature kinds cannot fall back to an unrelated prefab.
        if (!CreatureDirectoryRules.IsLegacyKind(frame.Kind) && CreatureDirectoryRules.RequiresScenePath(frame.Kind) && frame.SourcePath.Length == 0)
            throw new InvalidDataException("Native scene creature requires its validated hierarchy path.");
        if (frame.State < 0 || frame.State > (frame.Kind == "spider" || frame.Kind == "condor" ? 3 : frame.Kind == "mushroom-zombie" ? 6 : 1))
            throw new InvalidDataException("Invalid native creature state.");
        if (frame.Pose.Nodes == null || frame.Pose.Nodes.Length > MaximumNodes) throw new InvalidDataException("Creature hierarchy exceeds limits.");
        Vector(frame.Pose.Position, 3, 1_000_000); Quaternion(frame.Pose.Rotation); Vector(frame.Pose.Scale, 3, 1000);
        var paths = validationPaths ??= new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var node in frame.Pose.Nodes)
            {
                if (node == null) throw new InvalidDataException("Missing creature node.");
                Text(node.Path, 2048, true);
                if (!paths.Add(node.Path)) throw new InvalidDataException("Duplicate creature node.");
                Vector(node.Position, 3, 1_000_000); Quaternion(node.Rotation); Vector(node.Scale, 3, 1000);
            }
            if (frame.Line != null)
            {
                int maximum = frame.Kind == "frog-tongue" ? MaximumFrogLinePoints : MaximumLinePoints;
                if (frame.Kind != "spider" && frame.Kind != "frog-tongue" || frame.Line.Points == null || frame.Line.Points.Length % 3 != 0 || frame.Line.Points.Length > maximum * 3)
                    throw new InvalidDataException("Invalid native creature line.");
                Vector(frame.Line.Points, frame.Line.Points.Length, 1_000_000);
                if (!Finite(frame.Line.Width) || frame.Line.Width < 0 || frame.Line.Width > 1000) throw new InvalidDataException("Invalid native creature line width.");
            }
            if (!NativeVisualAppearanceRules.Validate(frame.Visuals)) throw new InvalidDataException("Invalid native creature appearance.");
        }
        finally { paths.Clear(); }
    }
    public static long Estimate(CreatureReplayFrame frame)
    {
        // Preserve legacy page reservations exactly when the optional clock is
        // absent. Only schema 20 observations add the clock/mode budget.
        long bytes = 448L + (frame.SampleTimeKnown ? 24L : 0L) + 2L * (frame.Key.Length + frame.Kind.Length + frame.Resource.Length + frame.SourcePath.Length) + frame.Pose.Nodes.Length * 8L;
        foreach (var node in frame.Pose.Nodes) bytes += 224L + node.Path.Length * 2L;
        if (frame.Line != null) bytes += 96L + frame.Line.Points.Length * 4L;
        bytes += NativeVisualAppearanceRules.Estimate(frame.Visuals);
        if (frame.IdleAnimation != null) bytes += 128L + frame.IdleAnimation.Clip.Length * 2L;
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
    [ThreadStatic] private static HashSet<string>? validationPaths;
    public const int MaximumParts = 32;
    public static void Validate(WebWrapReplayFrame? frame)
    {
        if (frame == null) return;
        if (frame.Parts == null || frame.Parts.Length > MaximumParts) throw new InvalidDataException("Web wraps exceed limits.");
        var paths = validationPaths ??= new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var part in frame.Parts)
            {
                if (part == null) throw new InvalidDataException("Missing web wrap.");
                CreatureReplayRules.Text(part.Path, 2048, true);
                if (!paths.Add(part.Path) || !CreatureReplayRules.Finite(part.Clip) || part.Clip < 0 || part.Clip > 1)
                    throw new InvalidDataException("Invalid recorded web wrap.");
                CreatureReplayRules.Quaternion(part.Rotation); CreatureReplayRules.Vector(part.ParentScale, 3, 1000);
            }
        }
        finally { paths.Clear(); }
    }
    public static long Estimate(WebWrapReplayFrame? frame)
    {
        if (frame == null) return 0;
        long bytes = 72 + frame.Parts.Length * 8L;
        foreach (var part in frame.Parts) bytes += 176L + part.Path.Length * 2L;
        return bytes;
    }
}
