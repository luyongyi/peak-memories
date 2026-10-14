using System;
using System.Collections.Generic;

namespace PeakReplayLab;

// Only passive renderer state and native asset identities. No shader source, URLs,
// gameplay components or arbitrary resource paths are accepted from a recording.
public sealed class NativeRendererFrame
{
    public string Path { get; set; } = ".";
    public int Index { get; set; }
    public string Mesh { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Layer { get; set; }
    public uint RenderingLayerMask { get; set; } = 1;
    public int LightProbeUsage { get; set; }
    public int ReflectionProbeUsage { get; set; }
    public int ShadowCastingMode { get; set; }
    public bool ReceiveShadows { get; set; }
    public int SortingLayerId { get; set; }
    public int SortingOrder { get; set; }
    public string ProbeAnchor { get; set; } = "";
    // Null is unknown in schemas 10–14; an empty array is an observed mesh
    // without blend shapes. Weights are native values, not normalized guesses.
    public float[]? BlendShapes { get; set; }
    public NativeMaterialFrame[] Materials { get; set; } = Array.Empty<NativeMaterialFrame>();
}

public sealed class NativeMaterialFrame
{
    public string Name { get; set; } = "";
    public string Shader { get; set; } = "";
    public int RenderQueue { get; set; } = -1;
    public NativeShaderPropertyFrame[] Properties { get; set; } = Array.Empty<NativeShaderPropertyFrame>();
}

public sealed class NativeShaderPropertyFrame
{
    // 0 = float, 1 = colour, 2 = vector, 3 = native texture identity.
    public string Name { get; set; } = "";
    public int Kind { get; set; }
    public float[] Values { get; set; } = Array.Empty<float>();
    public string Texture { get; set; } = "";
    // Schema 18: HDR colors store the actual shader vector, regardless of
    // whether native code supplied a material color or a property-block color.
    public int ColorEncoding { get; set; }
    public bool ShouldSerializeColorEncoding() => ColorEncoding != 0;
}

internal static class NativeShaderColorPolicy
{
    public static bool UsesRawVector(int encoding, bool hdr, string material, string shader, string property) =>
        encoding == 1 || hdr && !(material == "Flag Checkpoint" && shader == "WavingFabric" && property == "_BaseColor");
}

internal static class NativePropertyBlockPolicy
{
    public static bool RecordedOverrides(string name, int kind, NativeShaderPropertyFrame[] recorded)
    {
        foreach (var property in recorded) if (property.Name == name && property.Kind == kind) return true;
        return false;
    }
}

public static class NativeVisualAppearanceRules
{
    // Validation runs on independent page workers as well as the capture thread.
    // Scratch is thread-private, bounded by the validated directory limits, and
    // cleared even when a malformed record returns early.
    [ThreadStatic] private static HashSet<(string Path, int Index)>? validationIdentities;
    [ThreadStatic] private static HashSet<string>? validationNames;
    // The native MushroomZombie player hierarchy contains 295 renderers.
    public const int MaximumRenderers = 512;
    public const int MaximumMaterials = 16;
    public const int MaximumProperties = 64;
    public const int MaximumBlendShapes = 128;
    public const float MaximumValue = 65536;
    // Known native numeric/texture properties. In particular, interaction/ghost
    // switches are excluded: replay visuals must not inherit live highlighting.
    public static readonly (string Name, int Kind)[] Properties =
    {
        ("_Tint", 1), ("_Color", 1), ("_BaseColor", 1), ("_EmissionColor", 1), ("_SkinColor", 1), ("_PlayerColor", 1), ("_GlowColor", 1),
        ("_Glow", 0), ("_Clip", 0), ("_UseTalkSprites", 0), ("_Spin", 0), ("_RopeCutoff", 0),
        ("_Metallic", 0), ("_Smoothness", 0), ("_Glossiness", 0), ("_Emission", 0), ("_EmissionStrength", 0),
        ("_Cull", 0), ("_Alpha", 0), ("_Opacity", 0), ("_AlphaClip", 0), ("_JitterAmount", 0), ("_BreakAmount", 0),
        ("_OutlineWidth", 0), ("_ColorMask", 0), ("_UseVertexColor", 0),
        ("_Brightness", 0), ("_HueStr", 0), ("_HueShifts", 2), ("_UseRawVertexColor", 0), ("_LocalTopDirection", 2),
        ("_Color1", 1), ("_Color2", 1), ("_Color3", 1), ("_Color4", 1), ("_StatusColor", 1), ("_StatusGlow", 0),
        ("_S", 0), ("_V", 0), ("_H", 0), ("_HueStr1", 0), ("_HueStr2", 0), ("_TopColor", 1), ("_UseTopColor", 0),
        ("_BaseMetallic", 0), ("_BaseSmooth", 0), ("_VertexColorAmount", 0),
        ("_Str", 0), ("_DoMirage", 0), ("_Strength", 0), ("_GrainMult", 0),
        ("_NoiseScroll", 0), ("_Step", 0), ("_LightingAmount", 0), ("_NoiseStr", 0), ("_CracksColor", 1),
        ("_MainTex", 3), ("_BaseMap", 3), ("_TalkSprite", 3), ("_EmissionMap", 3), ("_Eyes", 3),
        ("_BaseTexture", 3), ("_Texture1", 3), ("_Texture2", 3), ("_Texture3", 3), ("_Texture4", 3),
    };
    public static bool AllowedProperty(string name, int kind)
    {
        // Native shaders sometimes declare a four-component control as Color and
        // sometimes Vector. Capture uses the shader's actual metadata, never a guess.
        foreach (var property in Properties) if (property.Name == name && (property.Kind == kind ||
            (property.Kind == 1 || property.Kind == 2) && (kind == 1 || kind == 2))) return true;
        return false;
    }
    public static bool ValidProperty(NativeShaderPropertyFrame property)
    {
        if (property == null || !AllowedProperty(property.Name, property.Kind) || property.Values == null || property.Texture == null || property.Texture.Length > 256 ||
            property.ColorEncoding < 0 || property.ColorEncoding > 1 || property.ColorEncoding != 0 && property.Kind != 1) return false;
        int count = property.Kind == 3 ? 0 : property.Kind == 0 ? 1 : 4;
        if (property.Values.Length != count || property.Kind != 3 && property.Texture.Length != 0) return false;
        foreach (float value in property.Values) if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > MaximumValue) return false;
        return true;
    }
    public static bool Validate(NativeRendererFrame[]? frames)
    {
        if (frames == null) return true;
        if (frames.Length > MaximumRenderers) return false;
        var identities = validationIdentities ??= new HashSet<(string Path, int Index)>();
        var names = validationNames ??= new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var frame in frames)
            {
                if (frame == null || frame.Path == null || frame.Path.Length == 0 || frame.Path.Length > 512 || frame.Index < 0 || frame.Index >= MaximumMaterials || frame.Mesh == null || frame.Mesh.Length > 256 ||
                    frame.Layer < 0 || frame.Layer > 31 || frame.LightProbeUsage < 0 || frame.LightProbeUsage > 3 || frame.ReflectionProbeUsage < 0 || frame.ReflectionProbeUsage > 2 ||
                    frame.ShadowCastingMode < 0 || frame.ShadowCastingMode > 3 || frame.SortingOrder < -32768 || frame.SortingOrder > 32767 ||
                    frame.ProbeAnchor == null || frame.ProbeAnchor.Length > 512 || frame.Materials == null || frame.Materials.Length > MaximumMaterials ||
                    !identities.Add((frame.Path, frame.Index))) return false;
                if (frame.BlendShapes != null)
                {
                    if (frame.BlendShapes.Length > MaximumBlendShapes) return false;
                    foreach (float weight in frame.BlendShapes)
                        if (float.IsNaN(weight) || float.IsInfinity(weight) || Math.Abs(weight) > MaximumValue) return false;
                }
                foreach (var material in frame.Materials)
                {
                    if (material == null || material.Name == null || material.Name.Length > 256 || material.Shader == null || material.Shader.Length > 256 || material.RenderQueue < -1 || material.RenderQueue > 5000 ||
                        material.Properties == null || material.Properties.Length > MaximumProperties) return false;
                    names.Clear();
                    foreach (var property in material.Properties) if (!ValidProperty(property) || !names.Add(property.Name)) return false;
                }
            }
            return true;
        }
        finally { identities.Clear(); names.Clear(); }
    }
    public static long Estimate(NativeRendererFrame[]? frames)
    {
        long bytes = frames == null ? 0 : 32 + frames.Length * 8L;
        if (frames == null) return bytes;
        foreach (var frame in frames)
        {
            // Keep the schema 12–14 conservative fixed overhead stable: page indexes
            // persist this estimate. The extra reference fits its existing headroom.
            bytes += 216 + (frame.Path.Length + frame.ProbeAnchor.Length + frame.Mesh.Length) * 2L + frame.Materials.Length * 8L + (frame.BlendShapes == null ? 0 : 32 + frame.BlendShapes.Length * 4L);
            foreach (var material in frame.Materials)
            {
                bytes += 128 + (material.Name.Length + material.Shader.Length) * 2L + material.Properties.Length * 8L;
                foreach (var property in material.Properties) bytes += 128 + (property.Name.Length + property.Texture.Length) * 2L + property.Values.Length * 4L;
            }
        }
        return bytes;
    }
    public static bool Same(NativeRendererFrame[]? a, NativeRendererFrame[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            var x = a[i]; var y = b[i];
            if (!SameWeights(x.BlendShapes, y.BlendShapes)) return false;
            if (x.Path != y.Path || x.Index != y.Index || x.Mesh != y.Mesh || x.Enabled != y.Enabled || x.Layer != y.Layer || x.RenderingLayerMask != y.RenderingLayerMask ||
                x.LightProbeUsage != y.LightProbeUsage || x.ReflectionProbeUsage != y.ReflectionProbeUsage || x.ShadowCastingMode != y.ShadowCastingMode ||
                x.ReceiveShadows != y.ReceiveShadows || x.SortingLayerId != y.SortingLayerId || x.SortingOrder != y.SortingOrder || x.ProbeAnchor != y.ProbeAnchor || x.Materials.Length != y.Materials.Length) return false;
            for (int m = 0; m < x.Materials.Length; m++)
            {
                var p = x.Materials[m]; var q = y.Materials[m];
                if (p.Name != q.Name || p.Shader != q.Shader || p.RenderQueue != q.RenderQueue || p.Properties.Length != q.Properties.Length) return false;
                for (int k = 0; k < p.Properties.Length; k++)
                {
                    var v = p.Properties[k]; var w = q.Properties[k];
                    if (v.Name != w.Name || v.Kind != w.Kind || v.Texture != w.Texture || v.ColorEncoding != w.ColorEncoding || v.Values.Length != w.Values.Length) return false;
                    for (int n = 0; n < v.Values.Length; n++) if (v.Values[n] != w.Values[n]) return false;
                }
            }
        }
        return true;
    }
    private static bool SameWeights(float[]? a, float[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
