using System;
using System.Collections.Generic;
using System.IO;

namespace PeakReplayLab;

// The thrown Item and the non-Item terrain it creates are distinct lifetimes.
// Resources identify installed game meshes; only observed transforms/visibility
// are recorded. No collider, gameplay component, random spawning or damage command.
public sealed class SpawnedReplayFrame
{
    public string Key { get; set; } = "";
    public string Resource { get; set; } = "";
    public string Kind { get; set; } = ""; // bounce-shroom / shelf-shroom / scout-cannon
    public ObjectPose Pose { get; set; } = new();
    public NativeRendererFrame[]? Visuals { get; set; }
    public NativeLightFrame[]? Lights { get; set; }
    // Schema 8: one native clip anchor, never per-frame cannon hierarchy samples.
    public CrateAnimationFrame? Animation { get; set; }
}

public static class SpawnedReplayRules
{
    public const int MaximumEntities = 256;
    public const double AnimationWatchSeconds = 1.25;
    public static readonly Type[] KnownTypes = { typeof(SpawnedReplayFrame) };
    public static SpawnedReplayFrame Rebase(SpawnedReplayFrame frame, double start)
    {
        var a = frame.Animation;
        if (a == null || !a.Anchored || start == 0) return frame;
        return new SpawnedReplayFrame { Key = frame.Key, Kind = frame.Kind, Resource = frame.Resource, Pose = frame.Pose, Visuals = frame.Visuals, Lights = frame.Lights,
            Animation = new CrateAnimationFrame { Clip = a.Clip, Time = a.Time, Anchored = true,
                AnchorTime = a.AnchorTime - start, Rate = a.Rate, Duration = a.Duration, Loop = a.Loop } };
    }
    public static bool ShouldProbe(double now, double nextProbe, double watchUntil, bool dirty) => dirty || now <= watchUntil || now >= nextProbe;
    public static bool CanInterpolate(SpawnedReplayFrame a, SpawnedReplayFrame b) =>
        a.Key == b.Key && a.Resource == b.Resource && a.Kind == b.Kind && a.Pose.Active && b.Pose.Active;
    public static bool LegacyKind(string kind) => kind == "bounce-shroom" || kind == "shelf-shroom" || kind == "scout-cannon";
    public static bool NativePlacement(string kind, string resource) =>
        kind == "piton" && resource == "0_items/climbingspikehammered" ||
        kind == "fragile-piton" && resource == "0_items/climbingspikehammered_shitty" ||
        kind == "fragile-piton-hand" && resource == "0_items/climbingspikehammered_shitty hand" ||
        kind == "checkpoint-flag" && resource == "flag_planted_checkpoint" ||
        kind == "portable-stove" && resource == "portablestovetop_placed" ||
        kind == "magic-bean-vine" && resource == "magicbeanvine" ||
        kind == "cloud-fungus" && resource == "0_items/cloudfungusplaced" ||
        kind == "anti-sphere" && resource == "antisphere_projectile";
    public static void Validate(SpawnedReplayFrame frame)
    {
        if (frame == null) throw new InvalidDataException("Missing spawned terrain snapshot.");
        Text(frame.Key, 256); Text(frame.Resource, 256); Text(frame.Kind, 64);
        if (!LegacyKind(frame.Kind) && !NativePlacement(frame.Kind, frame.Resource)) throw new InvalidDataException("Unsupported spawned terrain kind.");
        if (frame.Kind == "scout-cannon" && frame.Resource != "scoutcannon_placed")
            throw new InvalidDataException("Invalid native cannon resource.");
        if (frame.Animation != null && (frame.Kind != "scout-cannon" || !frame.Animation.Anchored ||
            !CrateAnimationTimeline.Valid(frame.Animation) || frame.Animation.Loop ||
            frame.Animation.Clip != "CannonLight" && frame.Animation.Clip != "CannonFire"))
            throw new InvalidDataException("Invalid spawned cannon animation.");
        if (frame.Pose == null || frame.Pose.Nodes == null || frame.Pose.Nodes.Length > 256)
            throw new InvalidDataException("Invalid spawned terrain pose.");
        if (!NativeVisualAppearanceRules.Validate(frame.Visuals)) throw new InvalidDataException("Invalid native placement appearance.");
        if (!NativeLightRules.Validate(frame.Lights)) throw new InvalidDataException("Invalid native placement lights.");
        Vector(frame.Pose.Position, 3); Rotation(frame.Pose.Rotation); Vector(frame.Pose.Scale, 3);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in frame.Pose.Nodes)
        {
            if (node == null) throw new InvalidDataException("Missing spawned terrain node.");
            Text(node.Path, 2048);
            if (!paths.Add(node.Path)) throw new InvalidDataException("Duplicate spawned terrain node.");
            Vector(node.Position, 3); Rotation(node.Rotation); Vector(node.Scale, 3);
        }
    }
    public static long Estimate(SpawnedReplayFrame frame)
    {
        long bytes = 376L + 2L * (frame.Key.Length + frame.Resource.Length + frame.Kind.Length) + frame.Pose.Nodes.Length * 8L;
        foreach (var node in frame.Pose.Nodes) bytes += 224L + node.Path.Length * 2L;
        if (frame.Animation != null) bytes += 112L + frame.Animation.Clip.Length * 2L;
        return bytes + NativeVisualAppearanceRules.Estimate(frame.Visuals) + NativeLightRules.Estimate(frame.Lights);
    }
    private static void Text(string value, int max)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new InvalidDataException("Invalid spawned terrain identity."); }
    private static void Vector(float[]? values, int size)
    {
        if (values == null || values.Length != size) throw new InvalidDataException("Invalid spawned terrain vector.");
        foreach (float value in values)
            if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) >= 1e7) throw new InvalidDataException("Invalid spawned terrain number.");
    }
    private static void Rotation(float[] values)
    {
        Vector(values, 4); double norm = 0; foreach (float value in values) norm += (double)value * value;
        if (norm < .5 || norm > 1.5) throw new InvalidDataException("Invalid spawned terrain orientation.");
    }
}
