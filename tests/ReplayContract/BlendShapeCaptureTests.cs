using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class BlendShapeCaptureTests
{
    public static void Run(Action<string, Action> test)
    {
        test("blend shape weights round trip immutable sparse snapshots and backward playback", () =>
        {
            var weights = new[] { -5f, 0f, 50.25f, 100f, 120f };
            var first = Frame(0, weights); var second = Frame(.05, new[] { 0f, 5f, 55f, 90f, 125f });
            var writer = new ReplayDeltaCodec.Writer(); var tree = new ReplayDeltaCodec.Reader();
            var stream = new ReplayDeltaCodec.StreamReader(ReplayRules.CurrentSchema);
            var frames = new List<ReplayFrame>();
            foreach (var source in new[] { first, second, Frame(.1, null), Frame(.15, Array.Empty<float>()) })
            {
                var record = Json(writer.Encode(source));
                var a = tree.Decode(JObject.Parse(record)); var b = stream.DecodeRecord(record)!;
                ReplayRules.Validate(b, frames.Count == 0 ? -1 : frames[^1].T);
                Check(Json(a) == Json(b)); frames.Add(b);
            }
            var clip = new ReplayClip(); clip.Frames.AddRange(frames);
            foreach (double target in new[] { .15, 0, .1, .05, 0 }) clip.At(target);
            Check(frames[0].Items[0].Visuals![0].BlendShapes!.SequenceEqual(weights));
            Check(frames[2].Items[0].Visuals![0].BlendShapes == null);
            Check(frames[3].Items[0].Visuals![0].BlendShapes!.Length == 0);
        });
        test("old schemas preserve unknown blend shapes and reject new observation evidence", () =>
        {
            for (int schema = 12; schema <= 14; schema++)
            {
                var old = Frame(0, null); var token = JObject.Parse(Json(new ReplayDeltaCodec.Writer(schema).Encode(old)));
                Check(token["Items"]![0]!["Visuals"]![0]!["BlendShapes"] == null);
                Check(new ReplayDeltaCodec.Reader(schema).Decode(token).Items[0].Visuals![0].BlendShapes == null);
                Check(new ReplayDeltaCodec.StreamReader(schema).DecodeRecord(Json(token))!.Items[0].Visuals![0].BlendShapes == null);
                Reject(() => new ReplayDeltaCodec.Writer(schema).Encode(Frame(0, Array.Empty<float>())));
                token["Items"]![0]!["Visuals"]![0]!["BlendShapes"] = new JArray(1f);
                Reject(() => new ReplayDeltaCodec.Reader(schema).Decode(token));
                Reject(() => new ReplayDeltaCodec.StreamReader(schema).DecodeRecord(Json(token)));
            }
        });
        test("blend shape counts finite weights and immutable memory accounting remain bounded", () =>
        {
            foreach (var invalid in new[] { new float[129], new[] { float.NaN }, new[] { float.PositiveInfinity }, new[] { 65537f } })
                Reject(() => ReplayRules.Validate(Frame(0, invalid), -1));
            var a = Frame(0, null).Items[0].Visuals; var b = Frame(0, new float[128]).Items[0].Visuals;
            Check(NativeVisualAppearanceRules.Estimate(b) - NativeVisualAppearanceRules.Estimate(a) >= 512);
            Check(!NativeVisualAppearanceRules.Same(a, b));
        });
        test("legacy renderer memory estimates remain compatible with persisted page indexes", () =>
        {
            var legacy = new[] { new NativeRendererFrame() };
            Check(NativeVisualAppearanceRules.Estimate(legacy) == 258);
            legacy[0].BlendShapes = Array.Empty<float>();
            Check(NativeVisualAppearanceRules.Estimate(legacy) == 290);
            legacy[0].BlendShapes = new float[3];
            Check(NativeVisualAppearanceRules.Estimate(legacy) == 302);
        });
    }
    private static ReplayFrame Frame(double time, float[]? weights) => new()
    {
        T = time, Items = new[] { new ItemFrame { Key = "candle", ItemId = 1, Visuals = new[] { new NativeRendererFrame { BlendShapes = weights } } } }
    };
    private static string Json(object value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static void Check(bool value) { if (!value) throw new Exception("Blend shape capture assertion failed."); }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid blend shape observation accepted."); }
}
