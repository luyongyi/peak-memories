using System;
using System.Collections.Generic;

namespace PeakReplayLab;

internal enum AudioSeekLoadState { Unavailable = -1, Unloaded, Loading, Loaded, Failed }

// Selective preparation only: this policy never admits, starts, seeks or advances
// a voice. The caller keeps the replay clock buffered until the selected target
// clips are ready. A native load is non-preemptible, so yield after every call.
internal sealed class AudioSeekPreparationPolicy
{
    internal const double LoadingTimeoutSeconds = 5;
    private readonly Dictionary<string, double> requested = new(StringComparer.Ordinal);
    private readonly HashSet<string> bypassed = new(StringComparer.Ordinal);
    private int sequence, lastLoadFrame = -1;
    private bool active;
    public string TimedOutClip { get; private set; } = "";

    public static bool ShouldPrepare(AudioReplayFrame voice, double time)
    {
        // Apply marks transients suppressed on a seek discontinuity and never
        // starts an already-ended one-shot. Preparation must not load either.
        if (voice.Transient || time < voice.StartedAt) return false;
        return voice.Loop || AudioReplayRules.Cursor(voice, time) < voice.Duration - .001;
    }

    public bool TryPrepare(int seekSequence, int frame, double realtime, IReadOnlyList<AudioReplayFrame> voices,
        Func<string, AudioSeekLoadState> state, Action<string> load)
    {
        TimedOutClip = "";
        if (!active || sequence != seekSequence)
        { active = true; sequence = seekSequence; requested.Clear(); bypassed.Clear(); }
        bool waiting = false;
        foreach (var voice in voices)
        {
            string key = voice.Clip;
            if (bypassed.Contains(key)) continue;
            var current = state(key);
            if (current == AudioSeekLoadState.Loading)
            {
                if (!requested.TryGetValue(key, out double began)) requested.Add(key, began = realtime);
                if (realtime - began >= LoadingTimeoutSeconds)
                { bypassed.Add(key); TimedOutClip = key; continue; }
                waiting = true; continue;
            }
            if (current != AudioSeekLoadState.Unloaded || requested.ContainsKey(key)) continue;
            waiting = true;
            if (lastLoadFrame == frame) continue;
            requested.Add(key, realtime); lastLoadFrame = frame;
            load(key);
            // Even a synchronous success yields, keeping world activation and
            // any other native clip load out of this call's rendered frame.
            return false;
        }
        if (waiting || lastLoadFrame == frame) return false;
        active = false;
        return true;
    }
}
