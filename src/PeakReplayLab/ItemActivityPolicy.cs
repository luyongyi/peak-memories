using System;

namespace PeakReplayLab;

// Clock advancement is a distinct activity signal, never an event-window renewal.
// A non-looping pose remains finished even while Unity increments normalizedTime.
internal sealed class NativeAnimationClock
{
    private bool known, wasEnabled, wasLooping, wasTransitioning, wasAdvancing;
    private int state;
    private float cursor, rate;
    public long Revision { get; private set; }
    public bool Observe(bool enabled, int stateHash, float normalizedTime, bool looping, bool transitioning, float playbackRate)
    {
        bool valid = enabled && !float.IsNaN(normalizedTime) && !float.IsInfinity(normalizedTime) &&
            !float.IsNaN(playbackRate) && !float.IsInfinity(playbackRate);
        float effectiveRate = !float.IsNaN(playbackRate) && !float.IsInfinity(playbackRate) ? playbackRate : 0;
        float position = valid ? looping ? normalizedTime : Math.Max(0, Math.Min(1, normalizedTime)) : 0;
        bool changed = !known || enabled != wasEnabled || stateHash != state || looping != wasLooping ||
            transitioning != wasTransitioning || effectiveRate != rate;
        bool advancing = valid && effectiveRate != 0 && (transitioning ||
            (!known || changed ? looping || effectiveRate > 0 && position < 1 || effectiveRate < 0 && position > 0 : position != cursor));
        // A paused/culling-held clock may resume without changing state or
        // speed. Wake its sleeping geometry once on that activity boundary.
        if (changed || advancing != wasAdvancing) Revision++;
        known = true; wasEnabled = enabled; wasLooping = looping; wasTransitioning = transitioning;
        state = stateHash; cursor = position; rate = effectiveRate; wasAdvancing = advancing;
        return advancing;
    }
}

// Independent clocks let a pulsing light remain accurate while the item geometry
// sleeps. Unknown script capability warrants a bounded safety poll, not 60 Hz heat.
internal sealed class ItemActivityPolicy
{
    private double nextLight = double.NegativeInfinity;
    private double nextUnknown = double.NegativeInfinity;
    private double nextChildren = double.NegativeInfinity;
    private double nextAnimatedRoot = double.NegativeInfinity;
    private double lastLight = double.NegativeInfinity, lastChildren = double.NegativeInfinity, lastAnimatedRoot = double.NegativeInfinity;
    // Scene callers already passed the shared 30 Hz clock. A second relative
    // deadline would discard jittered scene observations; still dedupe repeated
    // reads by different item paths within that same observation.
    public bool LightDue(double now, bool dirty = false, bool followSceneClock = false) =>
        dirty || (followSceneClock ? now > lastLight + 1e-8 : now + 1e-8 >= nextLight);
    public void LightObserved(double now) { lastLight = now; nextLight = now + 1d / 30; }
    public bool UnknownDue(double now) => now + 1e-8 >= nextUnknown;
    public void UnknownObserved(double now, int identity) => nextUnknown = now + .5 + Math.Abs(identity % 17) * .011;
    public bool FastChildrenDue(double now, bool followSceneClock = false) =>
        followSceneClock ? now > lastChildren + 1e-8 : now + 1e-8 >= nextChildren;
    public void ChildrenObserved(double now) { lastChildren = now; nextChildren = now + 1d / 30; }
    public bool AnimatedRootDue(double now, bool followSceneClock = false) =>
        followSceneClock ? now > lastAnimatedRoot + 1e-8 : now + 1e-8 >= nextAnimatedRoot;
    public void RootObserved(double now) { lastAnimatedRoot = now; nextAnimatedRoot = now + 1d / 30; }
    public static bool ChildrenDue(bool dirty, bool initialOrFinal, bool semanticChildrenDue, bool unknownFallback) =>
        dirty || initialOrFinal || semanticChildrenDue || unknownFallback;
    public static bool CanSleep(bool settled, bool attached, bool creature, bool animationMoving, bool eventPending) =>
        settled && !attached && !creature && !animationMoving && !eventPending;
    // A physics body being awake is potential simulation work, not observed
    // movement. Quiet bodies still retain the bounded physical safety fallback.
    public static bool ObservedMotion(bool hasBaseline, bool rootMoved, float linearVelocitySquared, float angularVelocitySquared) =>
        !hasBaseline || rootMoved || linearVelocitySquared > .0001f || angularVelocitySquared > .0001f;
    public static bool FinalChildrenDue(bool childrenAlreadyRead, bool fallbackRead, bool wasSettled,
        bool dirty, bool moving, bool attached) =>
        !childrenAlreadyRead && (!fallbackRead || !wasSettled || dirty || moving || attached);
}

// Dedupe on immutable packet values before touching any Unity Transform/body.
// Compare against the last admitted packet, so small cumulative motion is kept.
internal sealed class ItemMotionPacketPolicy
{
    private bool known;
    private float px, py, pz, qx, qy, qz, qw, vx, vy, vz, ax, ay, az;
    public bool Observe(float x, float y, float z, float rx, float ry, float rz, float rw,
        float velocityX, float velocityY, float velocityZ, float angularX, float angularY, float angularZ)
    {
        double norm = (double)rx * rx + (double)ry * ry + (double)rz * rz + (double)rw * rw;
        if (double.IsNaN(norm) || double.IsInfinity(norm) || norm < .5 || norm > 1.5 ||
            !Finite(x) || !Finite(y) || !Finite(z) || !Finite(velocityX) || !Finite(velocityY) || !Finite(velocityZ) ||
            !Finite(angularX) || !Finite(angularY) || !Finite(angularZ)) return false;
        bool same = known && Near(px, x) && Near(py, y) && Near(pz, z) &&
            Math.Abs((double)qx * rx + (double)qy * ry + (double)qz * rz + (double)qw * rw) > .999999 &&
            Near(vx, velocityX) && Near(vy, velocityY) && Near(vz, velocityZ) &&
            Near(ax, angularX) && Near(ay, angularY) && Near(az, angularZ);
        if (same) return false;
        known = true; px = x; py = y; pz = z; qx = rx; qy = ry; qz = rz; qw = rw;
        vx = velocityX; vy = velocityY; vz = velocityZ; ax = angularX; ay = angularY; az = angularZ;
        return true;
    }
    private static bool Near(float a, float b) => Math.Abs(a - b) <= .0001f;
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
