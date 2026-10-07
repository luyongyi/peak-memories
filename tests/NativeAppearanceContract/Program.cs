using System.Text.Json;
using PeakReplayLab;

int passed = 0;
void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
void Check(bool value) { if (!value) throw new Exception("Native appearance contract failed."); }
NativeRendererFrame[] Sample() => new[]
{
    new NativeRendererFrame
    {
        Path = "0:Apple%20Berry", Mesh = "AppleBerry\n372:1", Index = 0, Layer = 21, RenderingLayerMask = 17,
        LightProbeUsage = 1, ReflectionProbeUsage = 1, ShadowCastingMode = 1, ReceiveShadows = true, ProbeAnchor = ".",
        Materials = new[] { new NativeMaterialFrame { Name = "M_AppleYellow", Shader = "Shader Graphs/Item", RenderQueue = 2000,
            Properties = new[] { new NativeShaderPropertyFrame { Name = "_Tint", Kind = 1, Values = new[] { .6037736f, .4324894f, 0f, 1f } },
                new NativeShaderPropertyFrame { Name = "_HueShifts", Kind = 2, Values = new[] { -.02f, 0f, 0f, 0f } },
                new NativeShaderPropertyFrame { Name = "_Texture1", Kind = 3, Texture = "AppleTexture" } } } },
    },
};
NativeRendererFrame[] Copy(NativeRendererFrame[] source) => JsonSerializer.Deserialize<NativeRendererFrame[]>(JsonSerializer.Serialize(source))!;

Test("old recording has unknown appearance", () => { Check(NativeVisualAppearanceRules.Validate(null)); Check(!NativeVisualAppearanceRules.Same(null, Array.Empty<NativeRendererFrame>())); });
Test("native yellow tint keeps exact channels", () => { var a = Sample(); Check(NativeVisualAppearanceRules.Validate(a)); var b = Copy(a); Check(NativeVisualAppearanceRules.Same(a, b)); Check(b[0].Materials[0].Properties[0].Values[2] == 0); });
Test("HDR and signed hue controls survive", () => { var a = Sample(); a[0].Materials[0].Properties[0].Values = new[] { 4f, 2.19f, -.1f, 1f }; Check(NativeVisualAppearanceRules.Validate(a)); Check(NativeVisualAppearanceRules.Same(a, Copy(a))); });
Test("material switches are snapshot boundaries", () => { var a = Sample(); var b = Copy(a); b[0].Materials[0].Name = "M_AppleYellow_Colorblind"; Check(!NativeVisualAppearanceRules.Same(a, b)); Check(NativeVisualAppearanceRules.Same(a, Copy(a))); });
Test("texture switches are snapshot boundaries", () => { var a = Sample(); var b = Copy(a); b[0].Materials[0].Properties[2].Texture = "ZombieEyes"; Check(!NativeVisualAppearanceRules.Same(a, b)); });
Test("mesh switch survives arbitrary seek", () => { var a = Sample(); var b = Copy(a); b[0].Mesh = "MushroomMan\n438:2"; Check(!NativeVisualAppearanceRules.Same(a, b)); Check(NativeVisualAppearanceRules.Same(a, Copy(a))); });
Test("per-renderer visibility stays independent", () => { var a = Sample(); var b = Copy(a); b[0].Enabled = false; Check(!NativeVisualAppearanceRules.Same(a, b)); });
Test("light layers and probes are recorded state", () => { var a = Sample(); var b = Copy(a); b[0].RenderingLayerMask = 1; Check(!NativeVisualAppearanceRules.Same(a, b)); b = Copy(a); b[0].LightProbeUsage = 0; Check(!NativeVisualAppearanceRules.Same(a, b)); });
Test("render order is captured without asset mutation", () => { var a = Sample(); var b = Copy(a); b[0].Materials[0].RenderQueue = 3009; Check(NativeVisualAppearanceRules.Validate(b)); Check(!NativeVisualAppearanceRules.Same(a, b)); });
Test("reject unknown shader property", () => { var a = Sample(); a[0].Materials[0].Properties[0].Name = "_UnboundedUserProperty"; Check(!NativeVisualAppearanceRules.Validate(a)); });
Test("reject live interaction override", () => { var a = Sample(); a[0].Materials[0].Properties[0] = new NativeShaderPropertyFrame { Name = "_Interactable", Kind = 0, Values = new[] { 1f } }; Check(!NativeVisualAppearanceRules.Validate(a)); });
Test("reject incorrect property kinds", () => { var a = Sample(); a[0].Materials[0].Properties[0].Kind = 3; Check(!NativeVisualAppearanceRules.Validate(a)); });
Test("reject texture numeric payload", () => { var a = Sample(); a[0].Materials[0].Properties[2].Values = new[] { 1f }; Check(!NativeVisualAppearanceRules.Validate(a)); });
Test("reject NaN, infinity and extreme HDR", () => { foreach (float value in new[] { float.NaN, float.PositiveInfinity, 65537f }) { var a = Sample(); a[0].Materials[0].Properties[0].Values[0] = value; Check(!NativeVisualAppearanceRules.Validate(a)); } });
Test("reject duplicate renderer identities", () => { var a = Sample(); Check(!NativeVisualAppearanceRules.Validate(new[] { a[0], Copy(a)[0] })); });
Test("reject duplicate shader controls", () => { var a = Sample(); a[0].Materials[0].Properties = new[] { a[0].Materials[0].Properties[0], a[0].Materials[0].Properties[0] }; Check(!NativeVisualAppearanceRules.Validate(a)); });
Test("reject renderer/material limits", () => { Check(!NativeVisualAppearanceRules.Validate(new NativeRendererFrame[257])); var a = Sample(); a[0].Materials = new NativeMaterialFrame[17]; Check(!NativeVisualAppearanceRules.Validate(a)); });
Test("reject excessive texture identity", () => { var a = Sample(); a[0].Materials[0].Properties[2].Texture = new string('x', 257); Check(!NativeVisualAppearanceRules.Validate(a)); });
Test("reject invalid lighting enum", () => { var a = Sample(); a[0].LightProbeUsage = 4; Check(!NativeVisualAppearanceRules.Validate(a)); });
Test("budget includes backing arrays and nested headers", () => { var a = Sample(); long minimum = 32 + 8 + 216 + a[0].Path.Length * 2L + a[0].Mesh.Length * 2L + a[0].ProbeAnchor.Length * 2L + 8 + 128 + 3 * 8 + 3 * 128; Check(NativeVisualAppearanceRules.Estimate(a) >= minimum); Check(NativeVisualAppearanceRules.Estimate(null) == 0); });
NativeLightFrame[] LightSample() => new[] { new NativeLightFrame { Path = "1:EnableWhenLit/0:Light", Color = new[] { 1f, .47f, .15f, 1f },
    Intensity = 4.25f, Range = 13f, RenderingLayerMask = 0x80000001u, CullingMask = -9, Cookie = "NativeCookie", CookieSize2D = new[] { 3f, 5f } } };
NativeLightFrame[] LightCopy(NativeLightFrame[] source) => JsonSerializer.Deserialize<NativeLightFrame[]>(JsonSerializer.Serialize(source))!;
Test("unknown lights differ from known empty", () => { Check(NativeLightRules.Validate(null)); Check(NativeLightRules.Validate(Array.Empty<NativeLightFrame>())); Check(!NativeLightRules.Same(null, Array.Empty<NativeLightFrame>())); });
Test("native light colour range layers and cookie roundtrip", () => { var a = LightSample(); Check(NativeLightRules.Validate(a)); Check(NativeLightRules.Same(a, LightCopy(a))); Check(LightCopy(a)[0].RenderingLayerMask == 0x80000001u); });
Test("light toggles survive reverse seek without mutating prior", () => { var a = LightSample(); var b = LightCopy(a); b[0].Enabled = false; Check(!NativeLightRules.Same(a, b)); Check(a[0].Enabled && NativeLightRules.Same(a, LightCopy(a))); });
Test("native flicker intensity changes are retained", () => { var a = LightSample(); var b = LightCopy(a); b[0].Intensity = .1f; Check(!NativeLightRules.Same(a, b)); });
Test("HDR light emission is not clamped to one", () => { var a = LightSample(); a[0].Color[1] = 7; Check(NativeLightRules.Validate(a)); Check(LightCopy(a)[0].Color[1] == 7); });
Test("light snapshot rejects oversized counts and duplicates", () => { Check(!NativeLightRules.Validate(new NativeLightFrame[33])); var a = LightSample(); Check(!NativeLightRules.Validate(new[] { a[0], a[0] })); });
Test("light snapshot rejects invalid numeric shader input", () => { foreach (float value in new[] { -1f, float.NaN, float.PositiveInfinity, 65537f }) { var a = LightSample(); a[0].Intensity = value; Check(!NativeLightRules.Validate(a)); } });
Test("light snapshot rejects invalid shape and cookie dimensions", () => { var a = LightSample(); a[0].Type = 5; Check(!NativeLightRules.Validate(a)); a = LightSample(); a[0].CookieSize2D = new[] { 1f }; Check(!NativeLightRules.Validate(a)); });
Test("spot cone cannot have inner angle outside outer angle", () => { var a = LightSample(); a[0].InnerSpotAngle = 31; Check(!NativeLightRules.Validate(a)); });
Test("native light memory accounts for backing arrays", () => { var a = LightSample(); Check(NativeLightRules.Estimate(a) >= 360); Check(NativeLightRules.Estimate(null) == 0); });
Console.WriteLine($"Native appearance contracts passed: {passed}");
