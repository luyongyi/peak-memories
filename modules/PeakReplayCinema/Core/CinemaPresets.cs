using System;
using System.Collections.Generic;

namespace PeakReplayLab.Cinema;

public static class CinemaPresets
{
    // Amount is metres for PushPull/Slide/Rise, degrees for Orbit. Positive PushPull moves in.
    // Generated paths start at the exact supplied pose, including its current framing.
    public static IReadOnlyList<CinemaKeyframe> Create(CinemaPreset kind, double startTime, double duration,
        string segmentId, CinemaPose startPose, CinemaVector3? targetCenter = null, float amount = 5)
    {
        CinemaLimits.EnumValue(kind, nameof(kind));
        CinemaLimits.Range(startTime, 0, CinemaLimits.MaxTime, nameof(startTime));
        CinemaLimits.Range(duration, 0.01, CinemaLimits.MaxTime, nameof(duration));
        CinemaLimits.Range(startTime + duration, 0, CinemaLimits.MaxTime, nameof(duration));
        CinemaLimits.Range(amount, kind == CinemaPreset.Orbit ? -360 : -1000,
            kind == CinemaPreset.Orbit ? 360 : 1000, nameof(amount));
        CinemaLimits.Text(segmentId, 128, nameof(segmentId)); startPose.Validate();
        var forward = CinemaMath.Rotate(startPose.Rotation, new CinemaVector3(0, 0, 1));
        var right = CinemaMath.Rotate(startPose.Rotation, new CinemaVector3(1, 0, 0));
        var center = targetCenter ?? CinemaMath.Add(startPose.Position, CinemaMath.Scale(forward, Math.Min(startPose.FocusDistance, 1000)));
        var delta = CinemaMath.Subtract(center, startPose.Position);
        if (kind == CinemaPreset.PushPull && targetCenter.HasValue)
        {
            double distance = CinemaMath.Length(delta);
            forward = CinemaMath.Normalize(delta);
            if (amount > 0) amount = Math.Min(amount, (float)Math.Max(0, distance - 0.25));
        }
        if (kind == CinemaPreset.Orbit && CinemaMath.Length(delta) < 0.25)
            throw new ArgumentException("Orbit requires a center away from the camera.", nameof(targetCenter));
        bool aimAtCenter = targetCenter.HasValue || kind == CinemaPreset.Orbit;
        var initialAim = aimAtCenter ? CinemaMath.LookAt(startPose.Position, center, startPose.Rotation) : startPose.Rotation;
        var framingOffset = CinemaMath.Multiply(CinemaMath.Inverse(initialAim), startPose.Rotation);
        int steps = kind == CinemaPreset.Orbit ? Math.Max(2, (int)Math.Ceiling(Math.Abs(amount) / 5)) : 16;
        var keys = new List<CinemaKeyframe>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            double u = i / (double)steps;
            float eased = (float)(u * u * (3 - 2 * u));
            CinemaVector3 position;
            switch (kind)
            {
                case CinemaPreset.PushPull:
                    position = CinemaMath.Add(startPose.Position, CinemaMath.Scale(forward, amount * eased)); break;
                case CinemaPreset.Slide:
                    position = CinemaMath.Add(startPose.Position, CinemaMath.Scale(right, amount * eased)); break;
                case CinemaPreset.Rise:
                    position = CinemaMath.Add(startPose.Position,
                        CinemaMath.Add(new CinemaVector3(0, amount * eased, 0), CinemaMath.Scale(forward, -Math.Abs(amount) * 0.35f * eased))); break;
                default:
                    var offset = CinemaMath.Subtract(startPose.Position, center);
                    double angle = amount * eased * Math.PI / 180;
                    position = CinemaMath.Add(center, new CinemaVector3(
                        (float)(offset.X * Math.Cos(angle) + offset.Z * Math.Sin(angle)), offset.Y,
                        (float)(-offset.X * Math.Sin(angle) + offset.Z * Math.Cos(angle)))); break;
            }
            var aim = aimAtCenter ? CinemaMath.LookAt(position, center, startPose.Rotation) : startPose.Rotation;
            // Remove the initial framing offset gradually. Blending from the initial world
            // rotation would lose the subject and could flip during an orbit beyond 180 degrees.
            var rotation = CinemaMath.Multiply(aim, CinemaMath.Slerp(framingOffset, CinemaQuaternion.Identity, eased));
            float focus = aimAtCenter ? (float)Math.Max(0.05, Math.Min(100000, CinemaMath.Length(CinemaMath.Subtract(center, position)))) : startPose.FocusDistance;
            var pose = i == 0 ? startPose : new CinemaPose(position, rotation, startPose.FieldOfView,
                startPose.FocusDistance + (focus - startPose.FocusDistance) * eased);
            keys.Add(new CinemaKeyframe(i == steps ? startTime + duration : startTime + duration * u,
                segmentId, pose, CinemaTransition.Linear));
        }
        return keys.AsReadOnly();
    }
}
