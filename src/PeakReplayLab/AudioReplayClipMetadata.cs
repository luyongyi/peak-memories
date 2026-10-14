using System;
using System.Globalization;
using UnityEngine;

namespace PeakReplayLab;

internal static class AudioReplayClips
{
    // Stable native resource identity only; no PCM or other clip asset bytes.
    public static string Key(AudioClip clip) => Key(clip.name, clip.samples, clip.frequency, clip.channels);
    internal static string Key(string name, int samples, int frequency, int channels) =>
        Uri.EscapeDataString(name) + "|" + samples.ToString(CultureInfo.InvariantCulture) + "|" +
        frequency.ToString(CultureInfo.InvariantCulture) + "|" + channels.ToString(CultureInfo.InvariantCulture);
}

// Admitted sources use imported game clips, whose identity, duration and sample
// rate are immutable. Keep one descriptor with each source; steady observations
// and pooled reuse of that clip need no further native metadata reads. A failed
// loading descriptor is not cached, and a replacement wrapper is read anew.
internal struct AudioReplayClipMetadata
{
    private AudioClip? cachedClip;
    public string Key { get; private set; }
    public float Duration { get; private set; }
    public int Frequency { get; private set; }

    public bool TryRead(AudioClip clip)
    {
        if (!clip) return false;
        if (ReferenceEquals(cachedClip, clip)) return true;
        float duration = clip.length;
        if (!(duration > 0) || duration > 86400) return false;
        int frequency = clip.frequency;
        if (frequency <= 0) return false;
        string key = AudioReplayClips.Key(clip.name, clip.samples, frequency, clip.channels);
        if (key.Length > 1024) return false;
        cachedClip = clip; Key = key; Duration = duration; Frequency = frequency;
        return true;
    }
}
