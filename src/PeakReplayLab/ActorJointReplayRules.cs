using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace PeakReplayLab;

// The character root uses world TRS. All descendants use parent-local TRS.
public static class ActorJointReplayRules
{
    public const int MaximumJoints = 256;
    public const int MaximumPathLength = 1024;

    public static void Validate(NodePose[] joints)
    {
        if (joints == null || joints.Length > MaximumJoints) throw Invalid();
        string? previous = null;
        foreach (var joint in joints)
        {
            if (joint == null || string.IsNullOrEmpty(joint.Path) || joint.Path.Length > MaximumPathLength ||
                previous != null && StringComparer.Ordinal.Compare(previous, joint.Path) >= 0 ||
                !ReplayRules.Finite(joint.SampleTime) || Math.Abs(joint.SampleTime) > 1_000_000_000 ||
                !Vector(joint.Position, 3) || !Vector(joint.Rotation, 4) || !Vector(joint.Scale, 3)) throw Invalid();
            double norm = 0;
            foreach (float value in joint.Rotation) norm += value * (double)value;
            if (norm < .25 || norm > 2.25) throw Invalid();
            previous = joint.Path;
        }
    }

    public static bool SameTopology(NodePose[] left, NodePose[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++)
            if (!StringComparer.Ordinal.Equals(left[i].Path, right[i].Path)) return false;
        return true;
    }

    public static long Estimate(NodePose[] joints)
    {
        if (joints.Length == 0) return 0;
        long size = 24 + joints.Length * 8L;
        foreach (var joint in joints) size += 256 + joint.Path.Length * 2L;
        return size;
    }

    private static bool Vector(float[] value, int size)
    {
        if (value == null || value.Length != size) return false;
        foreach (float component in value) if (!ReplayRules.Finite(component) || Math.Abs(component) > 1e7) return false;
        return true;
    }
    private static InvalidDataException Invalid() => new("Invalid full-body joint pose.");
}
