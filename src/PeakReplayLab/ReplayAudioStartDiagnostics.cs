using System;

namespace PeakReplayLab;

// Main-thread-only, session-local storage. Native clip metadata is captured by
// the caller outside the measured Play/UnPause interval. Keep existing replay
// string references; only a requested report allocates objects or arrays.
internal sealed class ReplayAudioStartDiagnostics
{
    internal const int Capacity = 128;
    internal const double MinimumMilliseconds = 1000d / 60;
    internal readonly struct ClipMetadata
    {
        public bool Available { get; }
        public int LoadState { get; }
        public int LoadType { get; }
        public bool LoadInBackground { get; }
        public bool PreloadAudioData { get; }
        internal ClipMetadata(int loadState, int loadType, bool loadInBackground, bool preloadAudioData)
        { Available = true; LoadState = loadState; LoadType = loadType;
          LoadInBackground = loadInBackground; PreloadAudioData = preloadAudioData; }
    }
    private struct RawEntry
    {
        internal string Clip, VoiceKey;
        internal bool UnPause;
        internal double ReplaySeconds, StartRealtime, EndRealtime, Milliseconds;
        internal int StartFrame, EndFrame, GlobalCollections;
        internal ClipMetadata Before, After;
    }
    internal sealed class Entry
    {
        public string Clip { get; set; } = "";
        public string VoiceKey { get; set; } = "";
        public string Operation { get; set; } = "";
        public double ReplaySeconds { get; set; }
        public double StartRealtime { get; set; }
        public double EndRealtime { get; set; }
        public double Milliseconds { get; set; }
        public int StartFrame { get; set; }
        public int EndFrame { get; set; }
        public int GlobalCollections { get; set; }
        public ClipMetadata ClipBefore { get; set; }
        public ClipMetadata ClipAfter { get; set; }
    }
    private readonly RawEntry[] recent = new RawEntry[Capacity];
    private readonly long frequency;
    private int cursor, count;
    internal ReplayAudioStartDiagnostics(long frequency)
    { if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency)); this.frequency = frequency; }
    internal void Observe(string clip, string voiceKey, bool unPause, double replaySeconds,
        long startedTicks, long endedTicks, double startRealtime, int startFrame, int endFrame,
        int collections, in ClipMetadata before, in ClipMetadata after)
    {
        if (endedTicks < startedTicks) return;
        double milliseconds = (endedTicks - startedTicks) * 1000d / frequency;
        if (milliseconds < MinimumMilliseconds) return;
        recent[cursor] = new RawEntry { Clip = clip, VoiceKey = voiceKey, UnPause = unPause,
            ReplaySeconds = replaySeconds, StartRealtime = startRealtime, EndRealtime = startRealtime + milliseconds / 1000,
            Milliseconds = milliseconds, StartFrame = startFrame, EndFrame = endFrame,
            GlobalCollections = Math.Max(0, collections), Before = before, After = after };
        cursor = (cursor + 1) % Capacity; count = Math.Min(Capacity, count + 1);
    }
    internal Entry[] Snapshot()
    {
        var result = new Entry[count];
        for (int i = 0; i < count; i++)
        {
            var value = recent[(cursor - count + Capacity + i) % Capacity];
            result[i] = new Entry { Clip = value.Clip, VoiceKey = value.VoiceKey, Operation = value.UnPause ? "UnPause" : "Play",
                ReplaySeconds = value.ReplaySeconds, StartRealtime = value.StartRealtime, EndRealtime = value.EndRealtime,
                Milliseconds = value.Milliseconds, StartFrame = value.StartFrame, EndFrame = value.EndFrame,
                GlobalCollections = value.GlobalCollections, ClipBefore = value.Before, ClipAfter = value.After };
        }
        return result;
    }
}
