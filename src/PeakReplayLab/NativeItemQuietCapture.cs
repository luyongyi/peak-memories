using System;
using UnityEngine;

namespace PeakReplayLab;

// A modeled visual clock may advance without creating another geometry
// observation. Its physical item root still receives the ordinary safety poll.
// Compare with the held checkpoint so small cumulative motion cannot disappear.
internal static class NativeItemQuietCapture
{
    internal static bool RootStable(Item item, ObjectPose pose)
    {
        var body = item.rig;
        if (!body || pose.Position.Length < 3 || pose.Rotation.Length < 4 || pose.Scale.Length < 3) return false;
        var root = item.transform;
        var p = root.position; var r = root.rotation; var s = root.lossyScale;
        if (!Near(p.x, pose.Position[0]) || !Near(p.y, pose.Position[1]) || !Near(p.z, pose.Position[2]) ||
            !Near(s.x, pose.Scale[0]) || !Near(s.y, pose.Scale[1]) || !Near(s.z, pose.Scale[2])) return false;
        double dot = (double)r.x * pose.Rotation[0] + (double)r.y * pose.Rotation[1] +
            (double)r.z * pose.Rotation[2] + (double)r.w * pose.Rotation[3];
        return Finite(dot) && Math.Abs(dot) > .999999 &&
            (body!.isKinematic || body.linearVelocity.sqrMagnitude <= .0001f && body.angularVelocity.sqrMagnitude <= .0001f);
    }

    // The first observation after a held interval cannot interpolate from the
    // old anchor: that would start movement during the preceding quiet seconds.
    internal static bool ResumesHeld(ItemFrame? previous, int mode) => previous != null && previous.SampleTimeKnown &&
        previous.ClockMode != CreatureReplayRules.ActiveClock && mode == CreatureReplayRules.ActiveClock;

    private static bool Near(float a, float b) => Finite(a) && Finite(b) && Math.Abs(a - b) <= .0001f;
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
