using System;
using System.IO;
using Newtonsoft.Json;

namespace PeakReplayLab;

// A null ActorFrame.HudState means no status snapshot was recorded. A known
// snapshot includes every native status segment, even when its value is zero.
// These are observed presentation values; playback never reapplies gameplay
// afflictions or recomputes health from inventory, pose, or elapsed time.
public sealed class ReplayHudState
{
    // CharacterAfflictions.STATUSTYPE in the recorded game build: Injury,
    // Hunger, Cold, Poison, Crab, Curse, Drowsy, Weight, Hot, Thorns, Spores,
    // Web, Arrow, Petrify, FlyTrap. Actual petrification is held separately by
    // CharacterData, and BarAffliction.isPetrify reads that separate value.
    public const int StatusCount = 15;
    public float Stamina { get; set; }
    public float MaxStamina { get; set; }
    public float ExtraStamina { get; set; }
    public float[] Afflictions { get; set; }
    public float Petrify { get; set; }
    public bool Invincible { get; set; }
    public bool CanGetHungry { get; set; }
    public bool Rainbow { get; set; }
    // Morale is an ephemeral HUD animation, not a CharacterData field. It is
    // known only when this actor was the live observed character at capture.
    public bool MoraleKnown { get; set; }
    public bool MoraleBoost { get; set; }

    public ReplayHudState() : this(new float[StatusCount]) { }
    internal ReplayHudState(float[] afflictions) => Afflictions = afflictions;

    [JsonIgnore]
    public float StatusSum
    {
        get { float sum = 0; foreach (float status in Afflictions) sum += status; return sum; }
    }

    public static void Validate(ReplayHudState? state)
    {
        if (state == null) return;
        if (!Unit(state.Stamina) || !Unit(state.MaxStamina) || !Unit(state.ExtraStamina) || !Unit(state.Petrify) ||
            state.Afflictions == null || state.Afflictions.Length != StatusCount || !state.MoraleKnown && state.MoraleBoost)
            throw new InvalidDataException("Invalid replay HUD state.");
        for (int i = 0; i < state.Afflictions.Length; i++)
            if (!ReplayRules.Finite(state.Afflictions[i]) || state.Afflictions[i] < 0 || state.Afflictions[i] > (i == 0 ? 1 : 2))
                throw new InvalidDataException("Invalid replay status segment.");
    }

    private static bool Unit(float value) => ReplayRules.Finite(value) && value >= 0 && value <= 1;
    internal static long Estimate(ReplayHudState? state) => state == null ? 0 : 96 + 24 + state.Afflictions.Length * 4L;

    // Unknown samples are held without manufacturing healthy zero values.
    // Discrete icons use the left sample until its next observation, including
    // during a paused seek. Native status values and stamina interpolate only
    // between two complete snapshots from the same continuous actor interval.
    public static ReplayHudState? Interpolate(ReplayHudState? left, ReplayHudState? right, float mix)
        => InterpolateCore(left, right, mix, null);

    // This buffer belongs to the live presentation, never to a recorded frame.
    // Reusing it avoids allocating a state and its status array every render tick.
    internal static ReplayHudState? InterpolateForPresentation(ReplayHudState? left, ReplayHudState? right, float mix, ReplayHudState buffer)
        => InterpolateCore(left, right, mix, buffer);

    private static ReplayHudState? InterpolateCore(ReplayHudState? left, ReplayHudState? right, float mix, ReplayHudState? buffer)
    {
        if (!ReplayRules.Finite(mix)) throw new ArgumentOutOfRangeException(nameof(mix));
        if (mix <= 0) return left;
        if (mix >= 1) return right;
        if (left == null || right == null || ReferenceEquals(left, right)) return left;
        var result = buffer ?? new ReplayHudState();
        if (ReferenceEquals(result, left) || ReferenceEquals(result, right) ||
            ReferenceEquals(result.Afflictions, left.Afflictions) || ReferenceEquals(result.Afflictions, right.Afflictions) ||
            result.Afflictions.Length != StatusCount)
            throw new ArgumentException("Presentation HUD storage must be independent of recorded frames.", nameof(buffer));
        for (int i = 0; i < StatusCount; i++) result.Afflictions[i] = Lerp(left.Afflictions[i], right.Afflictions[i], mix);
        result.Stamina = Lerp(left.Stamina, right.Stamina, mix); result.MaxStamina = Lerp(left.MaxStamina, right.MaxStamina, mix);
        result.ExtraStamina = Lerp(left.ExtraStamina, right.ExtraStamina, mix);
        result.Petrify = Lerp(left.Petrify, right.Petrify, mix); result.Invincible = left.Invincible;
        result.CanGetHungry = left.CanGetHungry; result.Rainbow = left.Rainbow;
        result.MoraleKnown = left.MoraleKnown; result.MoraleBoost = left.MoraleBoost;
        return result;
    }

    private static float Lerp(float left, float right, float mix) => left + (right - left) * mix;
}
