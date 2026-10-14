using System;
using System.Collections.Generic;

namespace PeakReplayLab;

// Schema 22 periodic scene observations can deliver a voice whose native start
// occurred between those observations. Birth admission follows that recorded
// timestamp; it does not wait for the next 30 Hz scene snapshot to become left.
internal sealed class AudioSceneEventPolicy
{
    private readonly List<AudioReplayFrame> selected = new(AudioReplayRules.MaxVoices);
    private readonly HashSet<string> present = new(StringComparer.Ordinal);

    public IReadOnlyList<AudioReplayFrame> Select(AudioReplayFrame[] left, AudioReplayFrame[] right,
        double time, double leftTime, double rightTime, bool includeSceneEvents)
    {
        if (!includeSceneEvents || ReferenceEquals(left, right) || !Finite(time) || !Finite(leftTime) ||
            !Finite(rightTime) || rightTime <= leftTime || rightTime - leftTime > .5 ||
            time < leftTime - .000001 || time > rightTime + .000001) return left;
        selected.Clear(); present.Clear();
        foreach (var frame in left) { selected.Add(frame); present.Add(frame.Key); }
        foreach (var frame in right)
        {
            if (selected.Count >= AudioReplayRules.MaxVoices) break;
            if (present.Contains(frame.Key) || !Finite(frame.StartedAt) || frame.StartedAt < leftTime - .000001 ||
                frame.StartedAt > rightTime + .000001 || time < frame.StartedAt) continue;
            selected.Add(frame); present.Add(frame.Key);
        }
        return selected;
    }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
