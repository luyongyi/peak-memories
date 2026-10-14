using System;
using System.IO;

namespace PeakReplayLab;

// A native visual clock, not an Animator command. The original gameplay body
// remains absent; the audited clip is sampled on the inert mesh hierarchy only.
public sealed class CreatureIdleAnimation
{
    public string Clip { get; set; } = "";
    public double AnchorTime { get; set; }
    public double ClipTime { get; set; }
    public float Speed { get; set; }
    public float RopePhase { get; set; }
    // Schema 25: audited native EarlyWorm local-Y Rotate, anchored independently
    // of display-frame rate. No arbitrary transform or executable identifiers.
    public float[]? SpinRotation { get; set; }
    public float SpinSpeed { get; set; }
    public bool ShouldSerializeSpinRotation() => SpinRotation != null;
    public bool ShouldSerializeSpinSpeed() => SpinSpeed != 0;
}

public static class CreatureIdleAnimationRules
{
    public const string SpiderIdle = "spider-idle-v1";
    public const double SpiderIdleLength = 1;
    public const string RavenIdle = "raven-idle-v1";
    public const string RavenFlyAway = "raven-fly-away-v1";
    public const string FrogIdle = "frog-idle-v1";
    public const string EarlyWormWalk = "early-worm-walk-v1";
    public const string EarlyWormInHand = "early-worm-in-hand-v1";
    public const string EarlyWormCooked = "early-worm-cooked-v1";
    public const double EarlyWormLoopLength = 1;
    public const double RavenIdleLength = 3, RavenFlyAwayLength = 3, FrogIdleLength = 2;
    public static double ClipLength(string clip) => clip switch
    {
        SpiderIdle => SpiderIdleLength, RavenIdle => RavenIdleLength,
        RavenFlyAway => RavenFlyAwayLength, FrogIdle => FrogIdleLength,
        EarlyWormWalk or EarlyWormInHand => EarlyWormLoopLength, _ => 0,
    };
    public static bool IsEarlyWormClip(string clip) => clip == EarlyWormWalk || clip == EarlyWormInHand || clip == EarlyWormCooked;
    public static void Validate(CreatureIdleAnimation animation)
    {
        double length = animation == null ? 0 : ClipLength(animation.Clip);
        bool cooked = animation?.Clip == EarlyWormCooked;
        if (animation == null || length == 0 && !cooked ||
            !Finite(animation.AnchorTime) || Math.Abs(animation.AnchorTime) > 1_000_000 ||
            !Finite(animation.ClipTime) || animation.ClipTime < 0 ||
            (cooked ? animation.ClipTime != 0 || animation.Speed != 0 :
                animation.Clip == RavenFlyAway ? animation.ClipTime > length : animation.ClipTime >= length) ||
            !Finite(animation.Speed) || animation.Speed < 0 || animation.Speed > 8 ||
            !Finite(animation.RopePhase) || animation.RopePhase < 0 || animation.RopePhase > 1 ||
            animation.Clip != SpiderIdle && animation.RopePhase != 0 ||
            !Finite(animation.SpinSpeed) || Math.Abs(animation.SpinSpeed) > 3600 ||
            !IsEarlyWormClip(animation.Clip) && (animation.SpinRotation != null || animation.SpinSpeed != 0) ||
            animation.SpinSpeed != 0 && animation.SpinRotation == null || cooked && animation.SpinSpeed != 0)
            throw new InvalidDataException("Invalid native creature idle animation clock.");
        if (animation.SpinRotation != null)
        {
            if (animation.SpinRotation.Length != 4) throw new InvalidDataException("Invalid native local rotation anchor.");
            double norm = 0;
            foreach (float value in animation.SpinRotation)
            {
                if (!Finite(value) || Math.Abs(value) >= 10) throw new InvalidDataException("Invalid native local rotation anchor.");
                norm += value * (double)value;
            }
            if (norm < .5 || norm > 1.5) throw new InvalidDataException("Invalid native local rotation anchor.");
        }
    }

    public static CreatureIdleAnimation Rebase(CreatureIdleAnimation animation, double start) => start == 0 ? animation : new()
    {
        Clip = animation.Clip, AnchorTime = animation.AnchorTime - start,
        ClipTime = animation.ClipTime, Speed = animation.Speed, RopePhase = animation.RopePhase,
        SpinRotation = animation.SpinRotation, SpinSpeed = animation.SpinSpeed,
    };

    // Absolute replay time makes pause, backwards seeks and playback speed
    // independent of Unity's real-time animation update or intervening pages.
    public static double SampleTime(CreatureIdleAnimation animation, double replayTime)
    {
        if (!Finite(replayTime) || Math.Abs(replayTime) > 1_000_000) throw new InvalidDataException("Invalid idle animation playback time.");
        Validate(animation);
        if (animation.Clip == EarlyWormCooked) return 0;
        double time = animation.ClipTime + (replayTime - animation.AnchorTime) * animation.Speed;
        double length = ClipLength(animation.Clip);
        return animation.Clip == RavenFlyAway ? Math.Max(0, Math.Min(length, time)) : time - Math.Floor(time / length) * length;
    }

    internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
