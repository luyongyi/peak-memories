namespace PeakReplayLab;

internal static class NativeAnimatedAppearancePolicy
{
    // This is a property selection list after the owning track has established
    // a real native driver/event. Its presence never establishes activity.
    // GhostBall's clip swaps a ForceCustomizationTexture PPtr, whose native
    // Update writes _MainTex on the same material. Opt in only for that track;
    // common player colors/textures do not acquire a new periodic capture path.
    public static bool AnimatedProperty(string name, bool animateMainTexture = false) => name is
        "_Clip" or "_Spin" or "_RopeCutoff" or "_Alpha" or "_Opacity" or
        "_BreakAmount" or "_JitterAmount" or "_Glow" or "_GlowColor" or "_Emission" or "_EmissionStrength" or "_EmissionColor" or "_StatusGlow" or "_Tint" or
        "_Str" or "_DoMirage" or "_Strength" or "_GrainMult" or "_UseTalkSprites" or "_TalkSprite" or "_Eyes" ||
        animateMainTexture && name is "_MainTex" or "_NoiseScroll" or "_Step" or "_LightingAmount" or "_NoiseStr" or "_CracksColor" or "_PlayerColor";
}
