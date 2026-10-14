using System.Text.Json;
using PeakReplayLab;

internal static class GhostAppearanceTests
{
    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("ghost animated texture and shader controls are explicitly opted in", () =>
        {
            foreach (string name in new[] { "_MainTex", "_NoiseScroll", "_Step", "_LightingAmount", "_NoiseStr", "_CracksColor", "_PlayerColor" })
            {
                check(!NativeAnimatedAppearancePolicy.AnimatedProperty(name));
                check(!NativeAnimatedAppearancePolicy.AnimatedProperty(name, false));
                check(NativeAnimatedAppearancePolicy.AnimatedProperty(name, true));
            }
            foreach (string name in new[] { "_Clip", "_Opacity", "_BreakAmount", "_EmissionColor", "_Glow", "_Eyes", "_TalkSprite", "_Str", "_Tint" })
            {
                check(NativeAnimatedAppearancePolicy.AnimatedProperty(name));
                check(NativeAnimatedAppearancePolicy.AnimatedProperty(name, true));
            }
            foreach (string name in new[] { "_BaseMap", "_EmissionMap", "_Texture1", "_SkinColor", "_Interactable", "_VertexGhost", "_Unknown" })
                check(!NativeAnimatedAppearancePolicy.AnimatedProperty(name, true));
        });
        test("ghost face changes retain distinct native textures including reverse restore and explicit clear", () =>
        {
            var initial = Frame("GhostFaceIdle");
            var animated = Frame("GhostFaceBlink");
            var cleared = Frame("");
            check(NativeVisualAppearanceRules.Validate(initial) && NativeVisualAppearanceRules.Validate(animated) && NativeVisualAppearanceRules.Validate(cleared));
            check(!NativeVisualAppearanceRules.Same(initial, animated));
            check(!NativeVisualAppearanceRules.Same(animated, cleared));
            check(NativeVisualAppearanceRules.Same(initial, JsonSerializer.Deserialize<NativeRendererFrame[]>(JsonSerializer.Serialize(initial))));
            check(initial[0].Materials[0].Properties[0].Texture == "GhostFaceIdle");
            check(NativePropertyBlockPolicy.RecordedOverrides("_MainTex", 3, cleared[0].Materials[0].Properties));
            check(NativeVisualAppearanceRules.Estimate(animated) >= NativeVisualAppearanceRules.Estimate(cleared));
        });
        test("ghost cracks retain native HDR red channel above seven hundred without color conversion", () =>
        {
            var snapshot = Frame("GhostFaceIdle");
            var color = snapshot[0].Materials[0].Properties[1];
            check(color.Name == "_CracksColor" && color.ColorEncoding == 1 && color.Values[0] == 766.996f);
            check(NativeVisualAppearanceRules.Validate(snapshot));
            var decoded = JsonSerializer.Deserialize<NativeRendererFrame[]>(JsonSerializer.Serialize(snapshot))!;
            check(decoded[0].Materials[0].Properties[1].Values[0] == 766.996f);
            check(NativeVisualAppearanceRules.Same(snapshot, decoded));
            check(NativeShaderColorPolicy.UsesRawVector(1, true, "GhostBall", "GhostBallShader", "_CracksColor"));
            color.Values[0] = 65537;
            check(!NativeVisualAppearanceRules.Validate(snapshot));
        });
    }
    private static NativeRendererFrame[] Frame(string texture) => new[]
    {
        new NativeRendererFrame
        {
            Path = "0:GhostBall", Mesh = "GhostBall\n42:1", Materials = new[]
            {
                new NativeMaterialFrame
                {
                    Name = "GhostBall", Shader = "GhostBallShader", Properties = new[]
                    {
                        new NativeShaderPropertyFrame { Name = "_MainTex", Kind = 3, Texture = texture },
                        new NativeShaderPropertyFrame { Name = "_CracksColor", Kind = 1, ColorEncoding = 1, Values = new[] { 766.996f, 0f, 0f, 1f } },
                        new NativeShaderPropertyFrame { Name = "_PlayerColor", Kind = 1, Values = new[] { .25f, .5f, .75f, 1f } },
                        new NativeShaderPropertyFrame { Name = "_NoiseScroll", Kind = 0, Values = new[] { .2f } },
                        new NativeShaderPropertyFrame { Name = "_Step", Kind = 0, Values = new[] { .5f } },
                        new NativeShaderPropertyFrame { Name = "_LightingAmount", Kind = 0, Values = new[] { 1f } },
                        new NativeShaderPropertyFrame { Name = "_NoiseStr", Kind = 0, Values = new[] { 2f } },
                    },
                },
            },
        },
    };
}
