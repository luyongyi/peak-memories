using System;
using System.IO;

namespace PeakReplayLab;

// One native TiedBalloon lifetime, not the inventory Balloon/balloon bunch.
// Sway is presentation-only: no per-frame balloon transform or physics state.
public sealed class BalloonReplayFrame
{
    public string Key { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public int ColorIndex { get; set; }
    public float HeadOffset { get; set; } = .5f;
    public double Started { get; set; }
    public bool Active { get; set; } = true;
}

public static class BalloonReplayRules
{
    public const int MaximumEntities = 128;
    public const int NativeColorCount = 6;
    public static readonly Type[] KnownTypes = { typeof(BalloonReplayFrame) };
    public static void Validate(BalloonReplayFrame value)
    {
        if (value == null || string.IsNullOrWhiteSpace(value.Key) || value.Key.Length > 256 ||
            string.IsNullOrWhiteSpace(value.OwnerId) || value.OwnerId.Length > 256 ||
            value.ColorIndex < 0 || value.ColorIndex >= NativeColorCount ||
            !ReplayRules.Finite(value.HeadOffset) || Math.Abs(value.HeadOffset) > 10 ||
            !ReplayRules.Finite(value.Started) || Math.Abs(value.Started) > 1e9)
            throw new InvalidDataException("Invalid tied balloon attachment.");
    }
    public static long Estimate(BalloonReplayFrame value) => 112L + 2L * (value.Key.Length + value.OwnerId.Length);
    public static BalloonReplayFrame Rebase(BalloonReplayFrame value, double start) => new()
    {
        Key = value.Key, OwnerId = value.OwnerId, ColorIndex = value.ColorIndex,
        HeadOffset = value.HeadOffset, Started = value.Started - start, Active = value.Active,
    };
    public static bool SameAttachment(BalloonReplayFrame a, BalloonReplayFrame b) =>
        a.Key == b.Key && a.OwnerId == b.OwnerId && a.ColorIndex == b.ColorIndex &&
        a.HeadOffset == b.HeadOffset && a.Started == b.Started && a.Active == b.Active;

    // Stable across processes and seeks; neither Random nor wall-clock integration.
    // The key distinguishes multiple balloons attached to the same participant.
    public static (float X, float Y, float Z) Sway(string key, double age, int slot = 0, int count = 1)
    {
        uint hash = 2166136261;
        foreach (char c in key) hash = unchecked((hash ^ c) * 16777619);
        double phase = (hash % 65536) * (Math.PI * 2 / 65536);
        // Distinct native instances must remain legible, including a three-balloon
        // bunch and two separate uses. Stable array order supplies the owner slot.
        double bearing = count > 1 ? slot * Math.PI * 2 / count : phase;
        return ((float)(Math.Cos(bearing) * .35 + Math.Sin(age * 1.65 + phase) * .10),
            (float)(Math.Sin(age * 1.2 + phase) * .035),
            (float)(Math.Sin(bearing) * .35 + Math.Cos(age * 1.43 + phase) * .10));
    }
}
