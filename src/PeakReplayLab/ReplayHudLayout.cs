using System;

namespace PeakReplayLab;

// Native StaminaBar/BarAffliction geometry without character access, audio,
// tweens or scaled-time smoothing. Replay seeks apply these final widths.
internal static class ReplayHudLayout
{
    public const float SelectedSlotMultiplier = 1.2f;

    public static float NativeStaminaWidth(float stamina, float fullBarWidth, float offset) =>
        Math.Max(0, stamina * fullBarWidth + offset);

    // Small statuses reserve space for the original icon. Below the game's
    // visible threshold there is no segment, including at exactly 0.01.
    public static float AfflictionWidth(float amount, float fullBarWidth, float minimum) =>
        amount > .01f ? Math.Max(minimum, amount * fullBarWidth) : 0;

    public static float MainOutlineWidth(float statusSum, float fullBarWidth) =>
        14 + Math.Max(1, statusSum) * fullBarWidth;

    // Petrification occupies the entire native extra-stamina outline even
    // when the recorded bonus stamina has been depleted.
    public static float ExtraOutlineWidth(float extraStamina, float fullBarWidth, bool petrify) =>
        Math.Max(20, (petrify ? fullBarWidth : Math.Max(0, extraStamina * fullBarWidth)) + 12);
}
