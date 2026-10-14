using PeakReplayLab;
using UnityEngine;

int passed = 0;
void Check(bool condition) { if (!condition) throw new Exception("Audio clip metadata assertion failed."); }
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); Environment.Exit(1); }
}
Test("a steady native clip performs one descriptor read across ten thousand observations", () =>
{
    var clip = new AudioClip(); var metadata = new AudioReplayClipMetadata();
    for (int i = 0; i < 10000; i++) Check(metadata.TryRead(clip));
    Check(clip.NameReads == 1 && clip.DurationReads == 1 && clip.FrequencyReads == 1 &&
        clip.SampleReads == 1 && clip.ChannelReads == 1);
    Check(metadata.Key == "native%2Fsfx%7Cbirds|480000|48000|2" && metadata.Duration == 10 && metadata.Frequency == 48000);
});
Test("an unloaded descriptor is retried and valid metadata is then cached", () =>
{
    var clip = new AudioClip { Duration = 0 }; var metadata = new AudioReplayClipMetadata();
    Check(!metadata.TryRead(clip));
    clip.Duration = 10; Check(metadata.TryRead(clip)); Check(metadata.TryRead(clip));
    Check(clip.DurationReads == 2 && clip.FrequencyReads == 1);
});
Test("an unavailable sample rate is retried without poisoning the source", () =>
{
    var clip = new AudioClip { Frequency = 0 }; var metadata = new AudioReplayClipMetadata();
    Check(!metadata.TryRead(clip));
    clip.Frequency = 44100; Check(metadata.TryRead(clip));
    Check(metadata.Frequency == 44100 && clip.FrequencyReads == 2 && clip.NameReads == 1);
});
Test("a replacement wrapper is read even when its displayed name matches", () =>
{
    var first = new AudioClip(); var replacement = new AudioClip { Samples = 88200, Frequency = 44100, Duration = 2 };
    var metadata = new AudioReplayClipMetadata();
    Check(metadata.TryRead(first)); string key = metadata.Key;
    Check(metadata.TryRead(replacement));
    Check(metadata.Key != key && metadata.Duration == 2 && metadata.Frequency == 44100 && replacement.DurationReads == 1);
});
Test("destroyed wrappers cannot reuse an earlier descriptor", () =>
{
    var clip = new AudioClip(); var metadata = new AudioReplayClipMetadata(); Check(metadata.TryRead(clip));
    clip.Destroyed = true; Check(!metadata.TryRead(clip)); Check(clip.DurationReads == 1);
    var replacement = new AudioClip(); Check(metadata.TryRead(replacement)); Check(replacement.DurationReads == 1);
});
Test("unsupported descriptor lengths and nonfinite duration are not cached", () =>
{
    foreach (float duration in new[] { float.NaN, float.PositiveInfinity, -1, 86401 })
    { var metadata = new AudioReplayClipMetadata(); Check(!metadata.TryRead(new AudioClip { Duration = duration })); }
    var clip = new AudioClip { Name = new string('x', 1025) }; var cache = new AudioReplayClipMetadata();
    Check(!cache.TryRead(clip)); clip.Name = "valid"; Check(cache.TryRead(clip)); Check(clip.NameReads == 2);
});
Console.WriteLine($"Passed {passed} audio clip metadata contracts.");

namespace UnityEngine
{
    // Count native property access without a Unity runtime so the production
    // cache policy's steady and failed-loading paths are directly observable.
    public sealed class AudioClip
    {
        public string Name = "native/sfx|birds";
        public float Duration = 10;
        public int Frequency = 48000, Samples = 480000, Channels = 2;
        public bool Destroyed;
        public int NameReads, DurationReads, FrequencyReads, SampleReads, ChannelReads;
        public string name { get { NameReads++; return Name; } }
        public float length { get { DurationReads++; return Duration; } }
        public int frequency { get { FrequencyReads++; return Frequency; } }
        public int samples { get { SampleReads++; return Samples; } }
        public int channels { get { ChannelReads++; return Channels; } }
        public static implicit operator bool(AudioClip? clip) => clip != null && !clip.Destroyed;
    }
}
