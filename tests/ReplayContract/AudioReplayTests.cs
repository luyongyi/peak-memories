using PeakReplayLab;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class AudioReplayTests
{
    private static AudioReplayFrame Sound(bool loop = true) => new()
    {
        Key = "sound:1", Clip = "synthetic-sfx|480000|48000|1", StartedAt = 100,
        AnchorTime = 100, Offset = 2, Duration = 10, Loop = loop,
        Position = new[] { 1f, 2f, 3f }, Volume = .5f, Pitch = 1,
    };
    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        void Reject(Action action)
        {
            bool rejected = false;
            try { action(); } catch (System.IO.InvalidDataException) { rejected = true; }
            check(rejected);
        }
        test("audio native resource metadata validates without audio bytes", () => AudioReplayRules.Validate(Sound()));
        test("schema 15 preserves observed metal footstep pitch above three through both readers and playback clock", () =>
        {
            var audio = Sound(false); audio.Clip = "Au_Step_Metal_Plate5|31388|44100|2";
            audio.Pitch = 3.021165f; audio.Offset = 0; audio.Duration = .711746f;
            AudioReplayRules.Validate(audio); AudioReplayRules.ValidateVersion(audio, 15);
            var frame = new ReplayFrame { T = 100, Actors = new[] { new ActorFrame { Id = "actor" } }, Audio = new[] { audio } };
            string json = JsonConvert.SerializeObject(new ReplayDeltaCodec.Writer(15).Encode(frame), ReplayFiles.Json);
            var tree = new ReplayDeltaCodec.Reader(15).Decode(JObject.Parse(json));
            var stream = new ReplayDeltaCodec.StreamReader(15).DecodeRecord(json)!;
            check(tree.Audio[0].Pitch == audio.Pitch && stream.Audio[0].Pitch == audio.Pitch);
            check(AudioReplayRules.PlaybackPitch(audio.Pitch, 1) == audio.Pitch);
            check(AudioReplayRules.PlaybackPitch(audio.Pitch, .5f) == audio.Pitch * .5f);
            check(AudioReplayRules.PlaybackPitch(audio.Pitch, 8) == audio.Pitch * 8);
            check(Math.Abs(AudioReplayRules.Cursor(audio, 100.1) - (double)audio.Pitch * .1) < 1e-8);
            var cropped = AudioReplayRules.Rebase(audio, 100);
            check(cropped.Pitch == audio.Pitch && Math.Abs(AudioReplayRules.Cursor(cropped, .1) - AudioReplayRules.Cursor(audio, 100.1)) < 1e-8);
        });
        test("native audio pitch stays bounded while legacy formats retain their original limit", () =>
        {
            foreach (float pitch in new[] { -AudioReplayRules.MaximumNativePitch, -5f, 3.021165f, 5f, 19.5f, AudioReplayRules.MaximumNativePitch })
            {
                var audio = Sound(); audio.Pitch = pitch; AudioReplayRules.Validate(audio); AudioReplayRules.ValidateVersion(audio, 15);
                for (int schema = 10; schema <= 14; schema++) Reject(() => AudioReplayRules.ValidateVersion(audio, schema));
            }
            foreach (float pitch in new[] { -32.001f, 32.001f, float.PositiveInfinity, float.NegativeInfinity, float.NaN })
            { var audio = Sound(); audio.Pitch = pitch; Reject(() => AudioReplayRules.Validate(audio)); }
            foreach (float pitch in new[] { -3f, 0f, 3f })
            { var audio = Sound(); audio.Pitch = pitch; AudioReplayRules.Validate(audio); AudioReplayRules.ValidateVersion(audio, 14); }
        });
        test("native charge and rope pitches round trip without opening the extended range to old schemas", () =>
        {
            foreach (float pitch in new[] { 3.021165f, 5f, 19.5f })
            {
                var audio = Sound(); audio.Pitch = pitch;
                var frame = new ReplayFrame { T = 100, Actors = new[] { new ActorFrame { Id = "actor" } }, Audio = new[] { audio } };
                var encoded = JObject.FromObject(new ReplayDeltaCodec.Writer(15).Encode(frame));
                check(new ReplayDeltaCodec.Reader(15).Decode(encoded).Audio[0].Pitch == pitch);
                check(new ReplayDeltaCodec.StreamReader(15).DecodeRecord(encoded.ToString(Formatting.None))!.Audio[0].Pitch == pitch);
                for (int schema = 10; schema <= 14; schema++)
                {
                    Reject(() => ReplayRules.ValidateVersion(frame, schema));
                    Reject(() => new ReplayDeltaCodec.Writer(schema).Encode(frame));
                    var legacy = new ReplayFrame { T = 100, Actors = frame.Actors, Audio = new[] { Sound() } };
                    var tampered = JObject.FromObject(new ReplayDeltaCodec.Writer(schema).Encode(legacy));
                    tampered["Audio"]![0]![nameof(AudioReplayFrame.Pitch)] = pitch;
                    Reject(() => new ReplayDeltaCodec.Reader(schema).Decode(tampered));
                    Reject(() => new ReplayDeltaCodec.StreamReader(schema).DecodeRecord(tampered.ToString(Formatting.None)));
                }
            }
        });
        test("audio loop cursor uses replay time not wall time", () =>
        {
            var a = Sound(); check(AudioReplayRules.Cursor(a, 103) == 5);
            check(AudioReplayRules.Cursor(a, 113) == 5); check(AudioReplayRules.Cursor(a, 98) == 0);
        });
        test("audio one shot clips at end instead of wrapping into another explosion", () =>
        { var a = Sound(false); check(AudioReplayRules.Cursor(a, 113) == 10); check(AudioReplayRules.Cursor(a, 90) == 0); });
        test("audio seek and pause remain silent without replaying earlier starts", () =>
        {
            check(!AudioReplayRules.ShouldSound(true, 1, false));
            check(!AudioReplayRules.ShouldSound(false, 1, true));
            check(!AudioReplayRules.ShouldSound(false, 0, false));
            check(!AudioReplayRules.ShouldSound(false, float.NaN, false));
            check(AudioReplayRules.ShouldSound(false, 2, false));
        });
        test("audio crop rebases anchors while preserving exact cursor and historical frame", () =>
        {
            var a = Sound(); var b = AudioReplayRules.Rebase(a, 105);
            check(a.AnchorTime == 100 && a.StartedAt == 100 && b.AnchorTime == -5 && b.StartedAt == -5);
            check(ReferenceEquals(a.Position, b.Position));
            check(AudioReplayRules.Cursor(a, 106) == AudioReplayRules.Cursor(b, 1));
            AudioReplayRules.Validate(b);
        });
        test("audio reverse native pitch wraps loops and preserves crop", () =>
        { var a = Sound(); a.Pitch = -1; check(AudioReplayRules.Cursor(a, 103) == 9); });
        test("audio rejects nonfinite cursor pitch and position", () =>
        {
            var a = Sound(); a.Offset = double.NaN; Reject(() => AudioReplayRules.Validate(a));
            a = Sound(); a.Pitch = float.PositiveInfinity; Reject(() => AudioReplayRules.Validate(a));
            a = Sound(); a.Position[1] = float.NaN; Reject(() => AudioReplayRules.Validate(a));
        });
        test("audio enforces resource key and distance bounds", () =>
        {
            var a = Sound(); a.Clip = new string('a', 1025); Reject(() => AudioReplayRules.Validate(a));
            a = Sound(); a.MaxDistance = .1f; Reject(() => AudioReplayRules.Validate(a));
            a = Sound(); a.Rolloff = 9; Reject(() => AudioReplayRules.Validate(a));
        });
        test("audio estimate and zero-origin crop do not clone immutable state", () =>
        { var a = Sound(); check(AudioReplayRules.Estimate(a) > 224); check(ReferenceEquals(a, AudioReplayRules.Rebase(a, 0))); });
        test("audio short shot survives one sample without mutating its start", () =>
        {
            var a = Sound(false); var ended = AudioReplayRules.CompletedBetweenSamples(a, 100.01);
            check(!a.Transient && ended.Transient && ended.EndTime == 100.01);
            AudioReplayRules.Validate(ended);
            var cropped = AudioReplayRules.Rebase(ended, 105);
            check(cropped.StartedAt == -5 && Math.Abs(cropped.EndTime + 4.99) < .000001);
            AudioReplayRules.Validate(cropped);
        });
        test("audio short shot cannot restart after seek or after first playback", () =>
        {
            check(AudioReplayRules.MayStartTransient(true, false, false));
            check(!AudioReplayRules.MayStartTransient(true, true, false));
            check(!AudioReplayRules.MayStartTransient(true, false, true));
        });
        test("audio clock speed rejects negative infinity and does not double pitch cursor", () =>
        {
            check(!AudioReplayRules.ShouldSound(false, -1, false));
            check(!AudioReplayRules.ShouldSound(false, float.PositiveInfinity, false));
            check(AudioReplayRules.ShouldSound(false, .25f, false));
            var a = Sound(); a.Pitch = 2; check(AudioReplayRules.Cursor(a, 101) == 4);
        });
    }
}
