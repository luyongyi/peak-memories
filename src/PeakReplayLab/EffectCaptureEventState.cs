using System;
using System.Collections.Generic;
using System.Globalization;

namespace PeakReplayLab;

// Event signatures contain no geometry. Movement, normal clock progression,
// and repeated action callbacks cannot renew a 60 Hz geometry sampling lease.
internal readonly struct EffectCaptureRunSignature
{
    public readonly bool Alive, Emitting, Paused, Loop;
    public readonly long SeedFingerprint;
    public readonly float Phase, Rate;
    public readonly int State;
    public EffectCaptureRunSignature(bool alive, bool emitting, bool paused, bool loop,
        long seedFingerprint, float phase, float rate, int state)
    { Alive = alive; Emitting = emitting; Paused = paused; Loop = loop; SeedFingerprint = seedFingerprint; Phase = phase; Rate = rate; State = state; }
}

internal sealed class EffectCaptureEventState
{
    private bool known;
    private EffectCaptureRunSignature last;
    public bool StartsRun { get; private set; }
    public void Reset() { known = false; last = default; StartsRun = false; }
    public bool Observe(EffectCaptureRunSignature value)
    {
        StartsRun = value.Alive && (!known || !last.Alive ||
            value.SeedFingerprint != last.SeedFingerprint || value.State != last.State ||
            value.Emitting && !last.Emitting || !value.Loop && value.Phase + .05f < last.Phase);
        bool boundary = !known ? value.Alive : value.Alive != last.Alive || value.Alive &&
            (value.Emitting != last.Emitting || value.Paused != last.Paused || value.Loop != last.Loop || value.Rate != last.Rate || StartsRun);
        known = true; last = value; return boundary;
    }
}

// Different native playback runs must not overwrite one another while waiting
// for the next scene tick. Rate is relative and survives highlight rebasing.
internal static class EffectCaptureRunKey
{
    public static string Create(string source, long serial, float rate) =>
        "event-" + source + ":" + serial.ToString(CultureInfo.InvariantCulture) + ":" + rate.ToString("R", CultureInfo.InvariantCulture);
    public static bool HasInitialRate(string key, int kind, float rate)
    {
        string prefix = kind == 0 ? "event-particle:" : kind == 1 ? "event-mesh:" : "";
        if (prefix.Length == 0 || !IsNewRun(key, kind)) return false;
        int separator = key.LastIndexOf(':');
        return separator >= prefix.Length && float.TryParse(key.Substring(separator + 1), NumberStyles.Float,
            CultureInfo.InvariantCulture, out float initial) && initial == rate;
    }
    public static bool IsNewRun(string key, int kind)
    {
        string prefix = kind == 0 ? "event-particle:" : kind == 1 ? "event-mesh:" : kind == 2 ? "event-sphere:" : "";
        if (prefix.Length == 0 || !key.StartsWith(prefix, StringComparison.Ordinal)) return false;
        int end = key.LastIndexOf(':'), serialAt = end > 0 ? key.LastIndexOf(':', end - 1) : -1;
        return serialAt > prefix.Length && long.TryParse(key.Substring(serialAt + 1, end - serialAt - 1),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out long serial) && serial > 0 &&
            float.TryParse(key.Substring(end + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float rate) &&
            !float.IsNaN(rate) && !float.IsInfinity(rate) && rate >= 0;
    }
}

// Only an observed one-shot's native clock may bridge the immediately preceding
// absent scene sample. Old schemas and arbitrary future/looping items stay left.
internal static class EffectSceneEventSelection
{
    public static bool TryBirth(bool enabled, string key, int kind, bool loop, bool paused,
        double anchor, double phase, float rate, out double birth)
    {
        birth = 0;
        if (!enabled || loop || paused || rate <= 0 || !Finite(rate) || !Finite(anchor) || !Finite(phase) || phase < 0 ||
            !EffectCaptureRunKey.HasInitialRate(key, kind, rate)) return false;
        birth = anchor - phase / rate;
        return Finite(birth);
    }
    public static bool IncludeRight(bool enabled, bool presentOnLeft, string key, int kind, bool loop, bool paused,
        double anchor, double phase, float rate, double leftTime, double rightTime, double time, out double birth)
    {
        birth = 0;
        return !presentOnLeft && TryBirth(enabled, key, kind, loop, paused, anchor, phase, rate, out birth) &&
            Finite(leftTime) && Finite(rightTime) && Finite(time) && rightTime > leftTime &&
            birth >= leftTime && birth <= rightTime && time >= birth && time >= leftTime && time <= rightTime;
    }
    public static double PhaseAtBirth(double birth, float rate, double time) => Math.Max(0, time - birth) * rate;
    public static bool EmittingAtBirth(bool emitting, double stopPhase, double birth, float rate, double time) =>
        emitting || stopPhase >= 0 && time >= birth && PhaseAtBirth(birth, rate, time) < stopPhase;
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

// Retain both the first immutable read and the last observed boundary until
// one scene delivery. These bounded records never retain Unity objects.
internal sealed class EffectCapturePending<T> where T : class
{
    public readonly T First;
    public T Last;
    public readonly double ObservedAt;
    public readonly bool IsInitialRun;
    public double BoundaryAt = double.NaN, LastAt, EndedAt = double.NaN;
    public EffectCapturePending(T frame, double observedAt, bool isInitialRun)
    { First = Last = frame; ObservedAt = LastAt = observedAt; IsInitialRun = isInitialRun; }
}
internal sealed class EffectCaptureEventBuffer<T> where T : class
{
    private readonly int capacity;
    private readonly Dictionary<string, EffectCapturePending<T>> pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> present = new(StringComparer.Ordinal);
    private readonly List<string> discarded = new();
    public EffectCaptureEventBuffer(int capacity) => this.capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    public int Count => pending.Count;
    public bool Save(string key, T frame, bool replace = false, double observedAt = double.NaN, bool isInitialRun = false)
    {
        if (pending.TryGetValue(key, out var existing))
        {
            if (replace)
            {
                if (!ReferenceEquals(existing.Last, frame) && double.IsNaN(existing.BoundaryAt)) existing.BoundaryAt = observedAt;
                existing.Last = frame; existing.LastAt = observedAt;
            }
            return true;
        }
        if (pending.Count >= capacity) return false;
        pending.Add(key, new EffectCapturePending<T>(frame, observedAt, isInitialRun)); return true;
    }
    public void End(string key, double observedAt)
    { if (pending.TryGetValue(key, out var existing) && double.IsNaN(existing.EndedAt)) existing.EndedAt = observedAt; }
    // A late native origin witness can reject a provisionally unknown source
    // before scene delivery. Cancel every pending run for that exact source;
    // never mutate evidence already emitted in an immutable replay frame.
    public int DiscardSource(string sourceKey)
    {
        discarded.Clear();
        foreach (string key in pending.Keys)
            if (key == sourceKey || key.StartsWith("event-", StringComparison.Ordinal) && key.Length > sourceKey.Length + 6 &&
                string.CompareOrdinal(key, 6, sourceKey, 0, sourceKey.Length) == 0 && key[sourceKey.Length + 6] == ':')
                discarded.Add(key);
        foreach (string key in discarded) pending.Remove(key);
        return discarded.Count;
    }
    public int AppendMissing(List<T> frames, Func<T, string> keyOf,
        Func<EffectCapturePending<T>, T, double, T>? merge = null, double liveObservedAt = double.NaN)
    {
        present.Clear();
        for (int i = 0; i < frames.Count; i++)
        {
            string key = keyOf(frames[i]); present.Add(key);
            if (merge != null && pending.TryGetValue(key, out var initial)) frames[i] = merge(initial, frames[i], liveObservedAt);
        }
        int omitted = 0;
        foreach (var pair in pending)
        {
            if (present.Contains(pair.Key)) continue;
            if (frames.Count >= capacity) { omitted++; continue; }
            frames.Add(merge == null ? pair.Value.Last : merge(pair.Value, pair.Value.Last, pair.Value.LastAt)); present.Add(pair.Key);
        }
        pending.Clear(); return omitted;
    }
    public void Clear() { pending.Clear(); present.Clear(); discarded.Clear(); }
}
