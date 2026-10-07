using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class PresentationTrackTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Presentation track assertion failed."); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception e) when (e is InvalidDataException || e is ArgumentOutOfRangeException) { return; } throw new Exception("Invalid presentation accepted."); }
    private static ReplayFrame Sample(double time = 500) => new()
    {
        T = time,
        Ropes = new[] { new RopeReplayFrame { Key = "rope", Kind = "rope", Resource = "native-rope", Visible = true,
            Points = new[] { new RopeReplayPoint { Position = new[] { 1f, 2f, 3f }, Radius = .125f, Height = .7f } } } },
        Effects = new[] { new EffectReplayFrame { Key = "flame", Resource = "native-flame", AnchorTime = 499,
            Phase = .25, Loop = true, Seed = 123, Seeds = new long[] { 123, 456 } } },
        Audio = new[] { new AudioReplayFrame { Key = "sound", Clip = "native-sound", StartedAt = 498, AnchorTime = 499,
            Offset = 1, Duration = 8, Loop = true, Volume = .5f } },
    };
    private static ReplayClip Clip()
    {
        var a = Sample(); var b = new ReplayFrame { T = 500.05, Ropes = a.Ropes, Effects = a.Effects, Audio = a.Audio };
        var buffer = new RollingBuffer(); buffer.Add(a); buffer.Add(b);
        return buffer.Snapshot(new ReplayHeader { Scene = "Test", Schema = ReplayRules.CurrentSchema }, DateTime.UtcNow);
    }
    private static void Temp(Action<string> action)
    {
        string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(parent, "PeakPresentationTests-" + Guid.NewGuid().ToString("N")));
        if (!path.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test path.");
        Directory.CreateDirectory(path);
        try { action(path); } finally { Directory.Delete(path, true); }
    }
    private static JObject Token(object value) => JObject.FromObject(value, JsonSerializer.Create(ReplayFiles.Json));
    public static void Run(Action<string, Action> test)
    {
        test("current format retains full rope shape and native VFX game audio through archive", () => Temp(dir =>
        {
            var c = Clip(); var read = ReplayFiles.Read(ReplayArchive.Save(dir, c));
            Check(JToken.DeepEquals(Token(c.Frames[0]), Token(read.Frames[0])));
            Check(read.Frames[0].Ropes[0].Points[0].Position[2] == 3);
            Check(ReferenceEquals(read.Frames[0].Ropes, read.Frames[1].Ropes));
            Check(ReferenceEquals(read.Frames[0].Effects, read.Frames[1].Effects));
            Check(ReferenceEquals(read.Frames[0].Audio, read.Frames[1].Audio));
        }));
        test("current format ring crop rebases sound and particle clocks without losing first state", () =>
        {
            var first = Sample(); var buffer = new RollingBuffer(); buffer.Add(first);
            buffer.Add(new ReplayFrame { T = 501, Ropes = first.Ropes, Effects = first.Effects, Audio = first.Audio });
            var saved = buffer.Snapshot(new ReplayHeader { Scene = "Test" }, DateTime.UtcNow);
            Check(saved.Frames[0].Effects[0].AnchorTime == -1 && first.Effects[0].AnchorTime == 499);
            Check(saved.Frames[0].Audio[0].StartedAt == -2 && saved.Frames[0].Audio[0].AnchorTime == -1);
            Check(EffectReplayRules.PhaseAt(saved.Frames[0].Effects[0], 0) == 1.25);
            Check(AudioReplayRules.Cursor(saved.Frames[0].Audio[0], 0) == 2);
            Check(ReferenceEquals(saved.Frames[0].Effects, saved.Frames[1].Effects));
        });
        test("stable presentation shapes count once in ring and release when evicted", () =>
        {
            var first = Sample(0); var buffer = new RollingBuffer(); buffer.Add(first); long before = buffer.EstimatedBytes;
            var next = new ReplayFrame { T = .1, Ropes = first.Ropes, Effects = first.Effects, Audio = first.Audio }; buffer.Add(next);
            Check(buffer.EstimatedBytes - before == RollingBuffer.EstimateFrame(next));
            buffer.Add(new ReplayFrame { T = 121 });
            Check(buffer.Count == 1 && buffer.EstimatedBytes == RollingBuffer.Estimate(new ReplayFrame()));
        });
        test("oversized presentation frame rolls back shared budget references", () =>
        {
            var frame = Sample(0); long estimate = RollingBuffer.Estimate(frame);
            var buffer = new RollingBuffer(Math.Max(1024, estimate - 1));
            try { buffer.Add(frame); throw new Exception("Oversize accepted"); } catch (InvalidOperationException) { }
            Check(buffer.Count == 0 && buffer.EstimatedBytes == 0);
            buffer.Add(new ReplayFrame { T = 1 }); Check(buffer.EstimatedBytes == RollingBuffer.Estimate(new ReplayFrame()));
        });
        test("current format despawn and backward seek restore presentation state", () => Temp(dir =>
        {
            var clip = Clip(); clip.Frames[1] = new ReplayFrame { T = clip.Frames[1].T };
            var read = ReplayFiles.Read(ReplayArchive.Save(dir, clip));
            Check(read.At(read.Duration).Left.Ropes.Length == 0 && read.At(read.Duration).Left.Audio.Length == 0);
            Check(read.At(0).Left.Ropes.Length == 1 && read.At(0).Left.Effects.Length == 1);
        }));
        test("schema 5 cannot silently discard current format tracks", () => Temp(dir =>
        { var clip = Clip(); clip.Header.Schema = 5; Reject(() => ReplayArchive.Save(dir, clip)); }));
        test("current format requires complete presentation arrays while schema 5 rejects them", () =>
        {
            var token = Token(new ReplayDeltaCodec.Writer(ReplayRules.CurrentSchema).Encode(Clip().Frames[0]));
            Reject(() => new ReplayDeltaCodec.Reader(5).Decode(token));
            token.Remove("Ropes"); Reject(() => new ReplayDeltaCodec.Reader(ReplayRules.CurrentSchema).Decode(token));
        });
        test("current format validates duplicate presentation identities and seed ranges", () =>
        {
            var frame = Sample(0); frame.Effects = new[] { frame.Effects[0], frame.Effects[0] };
            Reject(() => ReplayRules.Validate(frame, -1));
            frame = Sample(0); frame.Effects[0].Seeds = new long[] { -1 }; Reject(() => ReplayRules.Validate(frame, -1));
        });
        test("save validation does not trust capture presentation cache", () => Temp(dir =>
        {
            var clip = Clip(); ReplayRules.ValidateCapture(clip.Frames[0], -1);
            clip.Frames[0].Ropes[0].Points[0].Position[0] = float.NaN;
            Reject(() => ReplayArchive.Save(dir, clip));
        }));
        test("previous formats are rejected before presentation decoding", () =>
        {
            foreach (int schema in Enumerable.Range(2, 8))
                Reject(() => ReplayRules.Validate(new ReplayHeader { Scene = "Test", Schema = schema }));
        });
    }
}
