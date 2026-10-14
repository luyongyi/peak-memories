using System;
using System.Runtime.CompilerServices;

namespace PeakReplayLab;

// Pure selection/mapping shared by presentation and seek history. InitialState
// has no recursive frame identity: it always belongs to the containing run.
public static class EffectSceneEventPlayback
{
    private static readonly ConditionalWeakTable<EffectReplayInitialState, EffectReplayFrame> initialFrames = new();
    public static EffectReplayFrame? Select(EffectReplayFrame frame, double time, bool enabled)
    {
        var initial = enabled ? frame.InitialState : null;
        if (initial == null) return frame;
        bool nativeBirth = EffectSceneEventSelection.TryBirth(true, frame.Key, frame.Kind, initial.Loop, initial.Paused,
            initial.AnchorTime, initial.Phase, initial.Rate, out double birth);
        if (time < (nativeBirth ? birth : initial.ObservedAt)) return null;
        if (initial.Ended && time >= (initial.EndTime ?? initial.UntilTime)) return null;
        if (time >= initial.UntilTime) return frame;
        return InitialFrame(frame, initial);
    }
    public static bool IncludeRight(EffectReplayFrame frame, double leftTime, double rightTime, double time,
        bool enabled, bool presentOnLeft = false)
    {
        if (!enabled || presentOnLeft) return false;
        var proof = frame.InitialState == null ? frame : InitialFrame(frame, frame.InitialState);
        if (EffectSceneEventSelection.IncludeRight(true, false, proof.Key, proof.Kind, proof.Loop, proof.Paused,
            proof.AnchorTime, proof.Phase, proof.Rate, leftTime, rightTime, time, out _)) return true;
        // A loop, paused visual or passive sphere has no reversible native
        // birth clock. Its explicit first observation is still proof that it
        // existed at that admitted global tick; never infer an earlier birth.
        double observedAt = frame.InitialState?.ObservedAt ?? frame.AnchorTime;
        return EffectCaptureRunKey.IsNewRun(frame.Key, frame.Kind) &&
            !double.IsNaN(observedAt) && !double.IsInfinity(observedAt) &&
            !double.IsNaN(leftTime) && !double.IsNaN(rightTime) && !double.IsNaN(time) &&
            !double.IsInfinity(leftTime) && !double.IsInfinity(rightTime) && !double.IsInfinity(time) &&
            rightTime > leftTime && observedAt >= leftTime && observedAt <= rightTime &&
            time >= observedAt && time >= leftTime && time <= rightTime;
    }
    public static EffectReplayFrame InitialFrame(EffectReplayFrame frame, EffectReplayInitialState initial) =>
        initialFrames.GetValue(initial, value => new EffectReplayFrame(value.Position, value.Rotation, value.Scale)
        {
            Key = frame.Key, Resource = frame.Resource, Kind = frame.Kind,
            AnchorTime = initial.AnchorTime, Phase = initial.Phase, Rate = initial.Rate, Loop = initial.Loop,
            Emitting = initial.Emitting, EmissionStopPhase = initial.EmissionStopPhase, Paused = initial.Paused,
            Seed = initial.Seed, Seeds = initial.Seeds, Clip = initial.Clip, ShaderRandom = initial.ShaderRandom,
            Particles = initial.Particles, ForceField = initial.ForceField,
            // Keep the boundary available to the preroll slice planner.
            InitialState = initial,
        });
    public static double PhaseAt(EffectReplayFrame frame, double time)
    {
        var initial = frame.InitialState;
        if (initial != null && time < initial.UntilTime)
        {
            double elapsed = initial.Paused ? 0 : time - initial.AnchorTime;
            // The first run's measured native clock also admits its pre-anchor
            // birth. Unsupported loops retain the conservative legacy clamp.
            if (!EffectSceneEventSelection.TryBirth(true, frame.Key, frame.Kind, initial.Loop, initial.Paused,
                initial.AnchorTime, initial.Phase, initial.Rate, out _)) elapsed = Math.Max(0, elapsed);
            return Math.Max(0, initial.Phase + elapsed * initial.Rate);
        }
        return Math.Max(0, frame.Phase + (frame.Paused ? 0 : Math.Max(0, time - frame.AnchorTime) * frame.Rate));
    }
    public static double TimeAtPhase(EffectReplayFrame frame, double phase)
    {
        var initial = frame.InitialState;
        if (initial != null)
        {
            double boundary = PhaseAt(frame, initial.UntilTime);
            if (phase < boundary)
                return initial.Rate > 0 && !initial.Paused
                    ? Math.Min(initial.UntilTime, initial.AnchorTime + (phase - initial.Phase) / initial.Rate)
                    : initial.AnchorTime;
            if (phase == boundary) return initial.UntilTime;
        }
        if (frame.Paused) return frame.AnchorTime;
        return frame.Rate > 0 ? frame.AnchorTime + (phase - frame.Phase) / frame.Rate : frame.AnchorTime;
    }
    public static double CutPhase(EffectReplayFrame anchor, double fromPhase, double nextPhase, bool enabled)
    {
        if (!enabled || anchor.InitialState == null) return nextPhase;
        double boundary = PhaseAt(anchor, anchor.InitialState.UntilTime);
        return boundary > fromPhase + 1e-10 && boundary < nextPhase ? boundary : nextPhase;
    }
    public static bool CrossesBoundary(EffectReplayFrame frame, double previousTime, double time, bool enabled) =>
        enabled && frame.InitialState != null && previousTime < frame.InitialState.UntilTime && time >= frame.InitialState.UntilTime;
    public static bool Changed(EffectReplayFrame first, EffectReplayFrame current) =>
        first.Rate != current.Rate || first.Paused != current.Paused || first.Emitting != current.Emitting ||
        first.Loop != current.Loop || first.Clip != current.Clip || first.ShaderRandom != current.ShaderRandom ||
        !ReferenceEquals(first.Particles, current.Particles) || !ReferenceEquals(first.ForceField, current.ForceField);
    public static EffectReplayInitialState Evidence(EffectReplayFrame first, double observedAt, double untilTime, bool ended) =>
        new(first.Position, first.Rotation, first.Scale)
        {
            ObservedAt = observedAt, UntilTime = untilTime, Ended = ended,
            AnchorTime = first.AnchorTime, Phase = first.Phase, Rate = first.Rate, Loop = first.Loop,
            Emitting = first.Emitting, EmissionStopPhase = first.EmissionStopPhase, Paused = first.Paused,
            Seed = first.Seed, Seeds = first.Seeds, Clip = first.Clip, ShaderRandom = first.ShaderRandom,
            Particles = first.Particles, ForceField = first.ForceField,
        };
    public static EffectReplayFrame WithInitial(EffectReplayFrame frame, EffectReplayInitialState initial) =>
        new(frame.Position, frame.Rotation, frame.Scale)
        {
            Key = frame.Key, Resource = frame.Resource, Kind = frame.Kind,
            AnchorTime = frame.AnchorTime, Phase = frame.Phase, Rate = frame.Rate, Loop = frame.Loop,
            Emitting = frame.Emitting, EmissionStopPhase = frame.EmissionStopPhase, Paused = frame.Paused,
            Seed = frame.Seed, Seeds = frame.Seeds, Clip = frame.Clip, ShaderRandom = frame.ShaderRandom,
            Particles = frame.Particles, ForceField = frame.ForceField, InitialState = initial,
        };
}
