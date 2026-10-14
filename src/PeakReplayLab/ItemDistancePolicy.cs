using System;

namespace PeakReplayLab;

// Distance checks use captured player positions and a cached item position.
// Distance alone never establishes rest: the owning track observes motion until
// ItemRestPolicy settles, then uses staggered probes to detect a later wake-up.
internal sealed class ItemDistancePolicy
{
    private readonly double spread;
    private double nextProbe = double.NegativeInfinity;
    private long revision = long.MinValue;
    private bool known, active, included;
    private float x, y, z;

    public ItemDistancePolicy(int identity) => spread = Math.Abs(identity % 17) * .011;
    public bool Active => active;

    public bool ShouldProbe(double now, long currentRevision) =>
        !known || currentRevision != revision || now >= nextProbe;

    public void Observed(double now, long currentRevision, float px, float py, float pz)
    {
        Schedule(now, currentRevision);
        active = true; x = px; y = py; z = pz;
    }

    public void Inactive(double now, long currentRevision)
    {
        Schedule(now, currentRevision);
        active = included = false;
    }

    private void Schedule(double now, long currentRevision)
    {
        known = true; revision = currentRevision; nextProbe = now + 1 + spread;
    }

    // A known holder is necessarily within range of a recorded player. This also
    // handles equipped backpack poses, whose recorded placement stays fixed even
    // while the owner moves. An entry transition forces a fresh full checkpoint.
    public bool Include(ActorFrame[] actors, float radius, string? holderId, out bool entered)
    {
        bool inside = active && radius <= 0;
        if (active && !inside)
        {
            double limit = (double)radius * radius;
            foreach (var actor in actors)
            {
                if (!string.IsNullOrEmpty(holderId) && actor.Id == holderId) { inside = true; break; }
                var p = actor.Position;
                if (p.Length < 3) continue;
                double dx = (double)x - p[0], dy = (double)y - p[1], dz = (double)z - p[2];
                double squared = dx * dx + dy * dy + dz * dz;
                if (!double.IsNaN(squared) && !double.IsInfinity(squared) && squared <= limit)
                { inside = true; break; }
            }
        }
        entered = inside && !included;
        included = inside;
        return inside;
    }
}

// One quiet sample is insufficient (a thrown object can pause at its apex).
// Require a continuous one-second quiet window; compare against its starting
// pose as well as per-sample physics so slow cumulative drift cannot settle.
internal sealed class ItemRestPolicy
{
    private double quietSince = double.NaN, lastObserved = double.NaN;
    private bool hasAnchor;
    private float x, y, z, qx, qy, qz, qw;
    public bool Settled { get; private set; }
    public bool ShouldCapture(bool inRange, bool exempt) => inRange || exempt || !Settled;

    public void Wake()
    { Settled = false; quietSince = lastObserved = double.NaN; hasAnchor = false; }

    public void Observed(double now, bool moving, ObjectPose? pose = null)
    {
        if (double.IsNaN(now) || double.IsInfinity(now)) { Wake(); return; }
        if (!double.IsNaN(lastObserved) && (now < lastObserved || now - lastObserved > .75)) Wake();
        if (moving)
        { Wake(); lastObserved = now; return; }
        if (pose != null && hasAnchor && AnchorMoved(pose)) Wake();
        lastObserved = now;
        if (double.IsNaN(quietSince))
        {
            quietSince = now;
            if (pose != null)
            {
                x = pose.Position[0]; y = pose.Position[1]; z = pose.Position[2];
                qx = pose.Rotation[0]; qy = pose.Rotation[1]; qz = pose.Rotation[2]; qw = pose.Rotation[3];
                hasAnchor = true;
            }
        }
        Settled = now - quietSince + 1e-8 >= 1;
    }

    private bool AnchorMoved(ObjectPose pose) =>
        Math.Abs(pose.Position[0] - x) > .0001f || Math.Abs(pose.Position[1] - y) > .0001f || Math.Abs(pose.Position[2] - z) > .0001f ||
        Math.Abs(pose.Rotation[0] - qx) > .0001f || Math.Abs(pose.Rotation[1] - qy) > .0001f ||
        Math.Abs(pose.Rotation[2] - qz) > .0001f || Math.Abs(pose.Rotation[3] - qw) > .0001f;

    public static bool RootMoved(ObjectPose a, ObjectPose b)
    {
        for (int i = 0; i < 3; i++) if (Math.Abs(a.Position[i] - b.Position[i]) > .0001f) return true;
        for (int i = 0; i < 4; i++) if (Math.Abs(a.Rotation[i] - b.Rotation[i]) > .0001f) return true;
        return false;
    }
}
