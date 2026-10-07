using Newtonsoft.Json;
using PeakReplayLab;

internal static class FullReplayTimestampTests
{
    public static void Run(Action<string, Action> test)
    {
        test("standalone stored replay validation still rejects times above 121 seconds", () =>
        {
            Reject(() => ReplayRules.Validate(new ReplayFrame { T = 121.01 }, -1));
            Reject(() => ReplayRules.ValidateStored(new ReplayFrame { T = 121.01 }, -1, new ReplayRules.ValidationMemo()));
            using var text = Records(121, 121.01);
            Reject(() => ReplayFiles.Read(text));
        });
        test("container-only reader accepts global four-hour endpoint without changing event timestamps", () =>
        {
            var frame = new ReplayFrame { T = 14400, Events = new[] { new ItemEvent { Sequence = 1, T = 14399.5, Kind = "use" } } };
            using var text = Records(new ReplayFrame { T = 14399 }, frame);
            var clip = ReplayFiles.Read(text, decodedByteLimit: 64L * 1024 * 1024, maximumStoredTime: 14400);
            Check(clip.Complete && clip.Frames[0].T == 14399 && clip.Duration == 14400 && clip.Frames[1].Events[0].T == 14399.5);
        });
        test("container stored replay rejects timestamps beyond four hours and nonfinite timestamps", () =>
        {
            foreach (double time in new[] { 14400.001, double.NaN, double.PositiveInfinity, -1 })
            {
                using var text = Records(time);
                Reject(() => ReplayFiles.Read(text, allowSingleFrame: true, maximumStoredTime: 14400));
            }
        });
        test("stored replay time limit cannot be disabled with unbounded or invalid values", () =>
        {
            foreach (double limit in new[] { 0, -1, double.NaN, double.PositiveInfinity, 14400.01 })
            {
                using var text = Records(0, .1);
                Reject(() => ReplayFiles.Read(text, maximumStoredTime: limit));
            }
        });
    }
    private static StringReader Records(params double[] times) => Records(times.Select(t => new ReplayFrame { T = t }).ToArray());
    private static StringReader Records(params ReplayFrame[] frames)
    {
        var codec = new ReplayDeltaCodec.Writer(ReplayRules.CurrentSchema);
        var records = new List<string> { JsonConvert.SerializeObject(new ReplayHeader { Schema = ReplayRules.CurrentSchema, Scene = "Level_TimeLimitTest" }, ReplayFiles.Json) };
        records.AddRange(frames.Select(frame => JsonConvert.SerializeObject(codec.Encode(frame), ReplayFiles.Json)));
        records.Add("{\"Type\":\"end\"}");
        return new StringReader(string.Join("\n", records));
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is InvalidDataException || e is ArgumentOutOfRangeException || e is JsonException) { return; }
        throw new Exception("Invalid replay timestamp was accepted.");
    }
    private static void Check(bool value) { if (!value) throw new Exception("Replay timestamp changed."); }
}
