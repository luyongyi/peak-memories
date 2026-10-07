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
    {
        if (!ReplayRules.Finite(mix)) throw new ArgumentOutOfRangeException(nameof(mix));
        if (mix <= 0) return left;
        if (mix >= 1) return right;
        if (left == null || right == null || ReferenceEquals(left, right)) return left;
        var statuses = new float[StatusCount];
        for (int i = 0; i < statuses.Length; i++) statuses[i] = Lerp(left.Afflictions[i], right.Afflictions[i], mix);
        return new ReplayHudState(statuses)
        {
            Stamina = Lerp(left.Stamina, right.Stamina, mix), MaxStamina = Lerp(left.MaxStamina, right.MaxStamina, mix),
            ExtraStamina = Lerp(left.ExtraStamina, right.ExtraStamina, mix),
            Petrify = Lerp(left.Petrify, right.Petrify, mix), Invincible = left.Invincible,
            CanGetHungry = left.CanGetHungry, Rainbow = left.Rainbow,
            MoraleKnown = left.MoraleKnown, MoraleBoost = left.MoraleBoost,
        };
    }

    private static float Lerp(float left, float right, float mix) => left + (right - left) * mix;
}
