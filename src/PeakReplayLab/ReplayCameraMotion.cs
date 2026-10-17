using System;

namespace PeakReplayLab;

// Camera input is independent of the replay clock. Keep fractional wheel input
// and use the input backend's units, never guess them from a fast scroll's size.
internal static class ReplayCameraMotion
{
    public static float WheelNotches(float rawScroll, float unitsPerNotch) =>
        Finite(rawScroll) && Finite(unitsPerNotch) && unitsPerNotch > 0 ? rawScroll / unitsPerNotch : 0;

    public static float ZoomDistance(float current, float notches)
    {
        if (!Finite(current)) current = 5;
        if (!Finite(notches)) notches = 0;
        return Clamp(current - notches * 2.5f, 1, 35);
    }

    public static float FlySpeed(float current, float notches)
    {
        if (!Finite(current)) current = 6;
        current = Clamp(current, .5f, 80);
        if (!Finite(notches)) return current;
        return (float)Math.Max(.5, Math.Min(80, current * Math.Pow(1.25, notches)));
    }

    public static (float X, float Y, float Z) Movement(float x, float y, float z,
        float speed, float delta, bool boost, bool precise)
    {
        if (!Finite(x) || !Finite(y) || !Finite(z) || !Finite(speed) || !Finite(delta) || speed <= 0 || delta <= 0)
            return (0, 0, 0);
        double length = Math.Sqrt((double)x * x + (double)y * y + (double)z * z);
        double scale = Clamp(speed, .5f, 80) * Math.Min(delta, .1f) * (boost ? 3 : 1) * (precise ? .25 : 1);
        if (length > 1) scale /= length;
        return ((float)(x * scale), (float)(y * scale), (float)(z * scale));
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
}
