using System;
using System.Collections.Generic;

namespace PeakReplayLab;

internal enum ActorJointPriority { P0, P1, P2 }

// Classify once when the skeleton catalogue changes. All transforms remain in
// the catalogue: priority changes observation frequency, never skin bindings.
internal static class ActorJointSamplingPolicy
{
    public const int CoreHz = 60, DetailHz = 30, AuxiliaryHz = 10;
    public const double LongGapSeconds = .25, HandTransitionSeconds = .25;

    private static readonly HashSet<string> DetailBones = new(StringComparer.Ordinal)
    {
        // Every finger chain stays at P0: real gestures can change too quickly
        // for 30 Hz observations even when interpolation is correct.
        "S_Toe_1_L", "S_Toe_2_L", "S_Heel_L", "S_Toe_1_R", "S_Toe_2_R", "S_Heel_R", "Face",
    };
    private static readonly HashSet<string> AuxiliaryRoots = new(StringComparer.Ordinal)
    {
        "ShoeWeight_L", "ShoeWeight_R", "PantWeight_L", "PantWeight_R", "ShirtArm_L", "ShirtArm_R",
        "WaistR1", "WaistL1", "WaistF1", "WaistB1", "Sash_Base", "Sash_Shoulder", "Detail",
    };
    private static readonly HashSet<string> DetailClothing = new(StringComparer.Ordinal)
    {
        "Chest", "ChestF", "ChestB", "Collar", "CollarB", "Collar_L", "Collar_R",
        "Collar_L.001", "Collar_R.001", "Sash_F", "Sash_B",
    };

    public static ActorJointPriority Classify(string path)
    {
        if (path == ".") return ActorJointPriority.P0;
        string[] pieces = path.Split('/');
        for (int i = 0; i < pieces.Length; i++)
        {
            int ordinal = pieces[i].LastIndexOf('#');
            pieces[i] = Uri.UnescapeDataString(ordinal < 0 ? pieces[i] : pieces[i].Substring(0, ordinal));
        }
        int first = pieces.Length > 0 && pieces[0] == "." ? 1 : 0;
        int length = pieces.Length - first;
        if (length <= 0 || pieces[first] != "Scout") return ActorJointPriority.P0;
        if (length == 1 || length == 2 && pieces[first + 1] == "Armature") return ActorJointPriority.P2;
        if (length < 3 || pieces[first + 1] != "Armature" || pieces[first + 2] != "Hip")
            return ActorJointPriority.P0;
        string leaf = pieces[pieces.Length - 1];
        int hat = Array.IndexOf(pieces, "Hat", first + 2);
        if (hat >= 0)
        {
            int relative = pieces.Length - hat;
            if (relative == 1 || relative == 2 && leaf == "PropellerHat" ||
                relative == 3 && pieces[hat + 1] == "PropellerHat" && leaf == "Armature")
                return ActorJointPriority.P2;
            if (relative >= 4 && pieces[hat + 1] == "PropellerHat" && pieces[hat + 2] == "Armature" &&
                (leaf == "Bone" || leaf == "Bone.001")) return ActorJointPriority.P1;
            return ActorJointPriority.P0;
        }
        // A name like Hip_L.014 is auxiliary only inside its known clothing
        // branch. Unknown bones elsewhere keep the safest, highest frequency.
        if (AuxiliaryRoots.Contains(leaf)) return ActorJointPriority.P2;
        for (int i = first + 3; i < pieces.Length - 1; i++)
        {
            string branch = pieces[i];
            if ((branch == "ShoeWeight_L" && Numbered(leaf, "Hip_R.", 14, 21)) ||
                (branch == "ShoeWeight_R" && Numbered(leaf, "Hip_L.", 14, 21)) ||
                (branch == "PantWeight_L" && Numbered(leaf, "Hip_R.", 8, 12)) ||
                (branch == "PantWeight_R" && Numbered(leaf, "Hip_L.", 8, 12)) ||
                (branch == "ShirtArm_L" && Numbered(leaf, "Hip.002_R.", 18, 24) && leaf != "Hip.002_R.022") ||
                (branch == "ShirtArm_R" && Numbered(leaf, "Hip.002_L.", 18, 23)) ||
                (branch == "Sash_Shoulder" && (leaf == "Hip.002_R.025" || leaf == "Hip.002_R.026")) ||
                (branch == "Sash_Base" && leaf == "SashWeight") ||
                (branch == "Detail" && DetailClothing.Contains(leaf))) return ActorJointPriority.P2;
        }
        return DetailBones.Contains(leaf) ? ActorJointPriority.P1 : ActorJointPriority.P0;
    }

    // Deadlines stay on the first sample's timeline. Waiting "interval after the
    // last rendered frame" would turn a 30 Hz tier into about 20 Hz at 58 fps.
    public static bool Due(int hz, double now, double lastSample, double origin, bool force = false)
    {
        if (double.IsNaN(lastSample)) return true;
        if (now == lastSample) return false;
        if (force || now < lastSample) return true;
        double tolerance = Math.Min(.001, Math.Max(.000001, Math.Max(Math.Abs(now), Math.Abs(origin)) * hz * 1e-14));
        return Math.Floor((now - origin) * hz + tolerance) > Math.Floor((lastSample - origin) * hz + tolerance);
    }

    private static bool Numbered(string value, string prefix, int minimum, int maximum) =>
        value.StartsWith(prefix, StringComparison.Ordinal) && value.Length == prefix.Length + 3 &&
        value[prefix.Length] >= '0' && value[prefix.Length] <= '9' &&
        value[prefix.Length + 1] >= '0' && value[prefix.Length + 1] <= '9' &&
        value[prefix.Length + 2] >= '0' && value[prefix.Length + 2] <= '9' &&
        int.TryParse(value.Substring(prefix.Length), out int number) && number >= minimum && number <= maximum;
}
