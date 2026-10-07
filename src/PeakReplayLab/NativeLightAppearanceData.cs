using System;
using System.Collections.Generic;

namespace PeakReplayLab;

public sealed class NativeLightFrame
{
    public string Path { get; set; } = ".";
    public int Index { get; set; }
    public bool Enabled { get; set; } = true;
    public int Type { get; set; } = 2;
    public float[] Color { get; set; } = new[] { 1f, 1f, 1f, 1f };
    public float Intensity { get; set; } = 1;
    public float Range { get; set; } = 10;
    public float SpotAngle { get; set; } = 30;
    public float InnerSpotAngle { get; set; }
    public float BounceIntensity { get; set; } = 1;
    public int Shadows { get; set; }
    public float ShadowStrength { get; set; } = 1;
    public float ShadowBias { get; set; } = .05f;
    public float ShadowNormalBias { get; set; } = .4f;
    public float ShadowNearPlane { get; set; } = .2f;
    public int CullingMask { get; set; } = -1;
    public uint RenderingLayerMask { get; set; } = 1;
    public string Cookie { get; set; } = "";
    public float CookieSize { get; set; } = 10;
    public float[] CookieSize2D { get; set; } = new[] { 10f, 10f };
    public int RenderMode { get; set; }
    public bool UseColorTemperature { get; set; }
    public float ColorTemperature { get; set; } = 6570;
}

public static class NativeLightRules
{
    public const int MaximumLights = 32;
    public static bool Validate(NativeLightFrame[]? frames)
    {
        if (frames == null) return true;
        if (frames.Length > MaximumLights) return false;
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var frame in frames)
        {
            if (frame == null || frame.Path == null || frame.Path.Length == 0 || frame.Path.Length > 512 || frame.Index < 0 || frame.Index >= MaximumLights ||
                frame.Type < 0 || frame.Type > 4 || frame.Shadows < 0 || frame.Shadows > 2 || frame.RenderMode < 0 || frame.RenderMode > 2 ||
                frame.Color == null || frame.Color.Length != 4 || frame.CookieSize2D == null || frame.CookieSize2D.Length != 2 ||
                frame.Cookie == null || frame.Cookie.Length > 256 || !identities.Add(frame.Path + "\n" + frame.Index)) return false;
            foreach (float value in frame.Color) if (!Bounded(value, -65536, 65536)) return false;
            foreach (float value in frame.CookieSize2D) if (!Bounded(value, 0, 65536)) return false;
            if (!Bounded(frame.Intensity, 0, 65536) || !Bounded(frame.Range, 0, 100000) || !Bounded(frame.SpotAngle, 0, 179) ||
                !Bounded(frame.InnerSpotAngle, 0, frame.SpotAngle) || !Bounded(frame.BounceIntensity, 0, 65536) || !Bounded(frame.ShadowStrength, 0, 1) ||
                !Bounded(frame.ShadowBias, 0, 100) || !Bounded(frame.ShadowNormalBias, 0, 100) || !Bounded(frame.ShadowNearPlane, 0, 100000) ||
                !Bounded(frame.CookieSize, 0, 65536) || !Bounded(frame.ColorTemperature, 1000, 20000)) return false;
        }
        return true;
    }
    private static bool Bounded(float value, float min, float max) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= min && value <= max;
    public static long Estimate(NativeLightFrame[]? frames)
    { long bytes = frames == null ? 0 : 32 + frames.Length * 8L; if (frames != null) foreach (var frame in frames) bytes += 320 + (frame.Path.Length + frame.Cookie.Length) * 2L; return bytes; }
    public static bool Same(NativeLightFrame[]? a, NativeLightFrame[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            var x = a[i]; var y = b[i];
            if (x.Path != y.Path || x.Index != y.Index || x.Enabled != y.Enabled || x.Type != y.Type || x.Intensity != y.Intensity || x.Range != y.Range ||
                x.SpotAngle != y.SpotAngle || x.InnerSpotAngle != y.InnerSpotAngle || x.BounceIntensity != y.BounceIntensity || x.Shadows != y.Shadows ||
                x.ShadowStrength != y.ShadowStrength || x.ShadowBias != y.ShadowBias || x.ShadowNormalBias != y.ShadowNormalBias || x.ShadowNearPlane != y.ShadowNearPlane ||
                x.CullingMask != y.CullingMask || x.RenderingLayerMask != y.RenderingLayerMask || x.Cookie != y.Cookie || x.CookieSize != y.CookieSize ||
                x.RenderMode != y.RenderMode || x.UseColorTemperature != y.UseColorTemperature || x.ColorTemperature != y.ColorTemperature || x.Color.Length != y.Color.Length ||
                x.CookieSize2D.Length != y.CookieSize2D.Length) return false;
            for (int c = 0; c < x.Color.Length; c++) if (x.Color[c] != y.Color[c]) return false;
            for (int c = 0; c < x.CookieSize2D.Length; c++) if (x.CookieSize2D[c] != y.CookieSize2D[c]) return false;
        }
        return true;
    }
}
