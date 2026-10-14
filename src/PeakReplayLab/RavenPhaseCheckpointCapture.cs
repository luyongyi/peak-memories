using System;

namespace PeakReplayLab;

// A supported clip can calibrate its clock without observing the authored rig
// again. Native actions and resource/visibility/appearance boundaries still
// need their own full checkpoint, including the flight's unkeyed rotations.
internal static class RavenPhaseCheckpointCapture
{
    internal static bool TryCorrect(CreatureReplayFrame? previous, CreatureIdleAnimation animation, int state,
        bool appearanceReady, bool appearanceDirty, bool activeChanged, bool nativeBoundary,
        Func<ObjectPose> readRoot, out CreatureReplayFrame corrected)
    {
        corrected = null!;
        var old = previous?.IdleAnimation;
        if (previous == null || old == null || !appearanceReady || appearanceDirty || activeChanged || nativeBoundary ||
            !previous.SampleTimeKnown || previous.ClockMode != CreatureReplayRules.IdleClock || !previous.Pose.Active ||
            previous.Kind != "voice-thief" || previous.Resource != "voice-thief" || previous.State != state ||
            old.Clip != animation.Clip || old.Speed != animation.Speed ||
            !RavenAnimationRuntime.IsRaven(animation.Clip)) return false;
        var root = readRoot();
        if (!SameRoot(previous.Pose, root)) return false;
        corrected = new CreatureReplayFrame
        {
            Key = previous.Key, Kind = previous.Kind, Resource = previous.Resource, SourcePath = previous.SourcePath,
            State = previous.State, Pose = previous.Pose, Line = previous.Line, Visuals = previous.Visuals,
            SampleTimeKnown = true, SampleTime = animation.AnchorTime, ClockMode = CreatureReplayRules.IdleClock,
            SampleDiscontinuity = false, IdleAnimation = animation,
        };
        return true;
    }
    private static bool SameRoot(ObjectPose a, ObjectPose b)
    {
        if (a.Active != b.Active) return false;
        if (ReferenceEquals(a, b)) return true;
        for (int i = 0; i < 3; i++) if (a.Position[i] != b.Position[i] || a.Scale[i] != b.Scale[i]) return false;
        for (int i = 0; i < 4; i++) if (a.Rotation[i] != b.Rotation[i]) return false;
        return true;
    }
}
