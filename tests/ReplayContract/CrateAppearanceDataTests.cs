using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class CrateAppearanceDataTests
{
    public static void Run(Action<string, Action> test)
    {
        test("crate appearance round trips sparse updates removals and unknown states without mutating prior snapshots", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(15);
            var tree = new ReplayDeltaCodec.Reader(15); var stream = new ReplayDeltaCodec.StreamReader(15);
            var saved = new List<(ReplayFrame Frame, string Json)>();
            for (int i = 0; i < 7; i++)
            {
                var crate = Crate(i == 0 || i == 4);
                if (crate.Visuals != null) crate.Visuals[0].Materials[0].Properties[0].Values[0] = i * .2f;
                if (crate.Lights != null) { crate.Lights[0].Enabled = i % 2 == 0; crate.Lights[0].Intensity = i; }
                var frame = Frame(i, i == 5 ? Array.Empty<CrateFrame>() : new[] { crate });
                string record = Json(writer.Encode(frame));
                var a = tree.Decode(JObject.Parse(record)); var b = stream.DecodeRecord(record)!;
                Check(Json(a) == Json(b)); ReplayRules.Validate(b, i - 1);
                Check(Json(b.Crates) == Json(frame.Crates)); saved.Add((b, Json(b)));
            }
            foreach (var snapshot in saved) Check(Json(snapshot.Frame) == snapshot.Json);
        });
        test("legacy crate schema never writes or accepts newly introduced appearance fields even when null", () =>
        {
            for (int schema = 10; schema < 15; schema++)
            {
                var old = Frame(0, Crate(true)); ReplayRules.ValidateVersion(old, schema);
                var record = JObject.FromObject(new ReplayDeltaCodec.Writer(schema).Encode(old));
                var payload = (JObject)record["Crates"]![0]!;
                Check(payload["Visuals"] == null && payload["Lights"] == null);
                var decoded = new ReplayDeltaCodec.StreamReader(schema).DecodeRecord(Json(record))!;
                Check(decoded.Crates[0].Visuals == null && decoded.Crates[0].Lights == null);
                foreach (string field in new[] { "Visuals", "Lights" })
                {
                    var malformed = (JObject)record.DeepClone(); malformed["Crates"]![0]![field] = JValue.CreateNull();
                    Reject(() => new ReplayDeltaCodec.Reader(schema).Decode(malformed));
                    Reject(() => new ReplayDeltaCodec.StreamReader(schema).DecodeRecord(Json(malformed)));
                }
                Reject(() => ReplayRules.ValidateVersion(Frame(0, Crate()), schema));
            }
        });
        test("crate appearance validation rejects malformed data and recovers for subsequent valid states", () =>
        {
            var crate = Crate(); crate.Lights![0].Intensity = float.NaN;
            Reject(() => ReplayRules.Validate(Frame(0, crate), -1));
            crate = Crate(); crate.Visuals![0].Materials[0].Properties[0].Values[0] = float.PositiveInfinity;
            Reject(() => ReplayRules.Validate(Frame(0, crate), -1));
            crate = Crate(); crate.Lights = Enumerable.Range(0, NativeLightRules.MaximumLights + 1).Select(i => new NativeLightFrame { Path = "light" + i }).ToArray();
            Reject(() => ReplayRules.Validate(Frame(0, crate), -1));
            ReplayRules.Validate(Frame(0, Crate()), -1);
        });
        test("crate appearance survives snapshot rebase and contributes to decoded memory only when recorded", () =>
        {
            var crate = Crate(); var legacy = Crate(true);
            crate.Animation = legacy.Animation = new CrateAnimationFrame { Clip = "Luggage/Open", Anchored = true, AnchorTime = 50, Duration = 2, Rate = 1 };
            var rebased = CrateAnimationTimeline.Rebase(crate, 51);
            Check(rebased.Animation!.AnchorTime == -1 && crate.Animation.AnchorTime == 50);
            Check(ReferenceEquals(rebased.Visuals, crate.Visuals) && ReferenceEquals(rebased.Lights, crate.Lights));
            Check(RollingBuffer.Estimate(crate) - RollingBuffer.Estimate(legacy) ==
                NativeVisualAppearanceRules.Estimate(crate.Visuals) + NativeLightRules.Estimate(crate.Lights));
            var budget = new ContinuousReplayBudget(); var first = Frame(0, crate);
            budget.Commit(first, budget.Additional(first));
            var repeated = Frame(1, crate); Check(budget.Additional(repeated) == RollingBuffer.EstimateFrame(repeated));
            var changed = Frame(2, Crate());
            Check(budget.Additional(changed) == RollingBuffer.EstimateFrame(changed) + RollingBuffer.Estimate(changed.Crates[0]));
        });
        test("crate appearance queue memory cannot bypass the byte limit and rejection releases partial references", () =>
        {
            var frame = Frame(0, Crate()); var reference = new ReplayQueueBudget();
            Check(reference.TryReserve(frame, long.MaxValue, 2, out var lease, out var size));
            long bytes = size.Bytes; lease!.Dispose(); reference.ClearBaseline(); Check(reference.Current.Bytes == 0);
            var bounded = new ReplayQueueBudget();
            Check(!bounded.TryReserve(frame, bytes - 1, 2, out var rejected, out _));
            Check(rejected == null && bounded.Current.Bytes == 0 && bounded.Current.Frames == 0);
            Check(bounded.TryReserve(frame, bytes, 2, out var accepted, out var exact) && exact.Bytes == bytes);
            accepted!.Dispose(); bounded.ClearBaseline(); Check(bounded.Current.Bytes == 0);
        });
    }
    private static CrateFrame Crate(bool unknown = false) => new()
    {
        Key = "Map/0:Luggage", Kind = "Luggage",
        Visuals = unknown ? null : new[] { new NativeRendererFrame { Path = ".", Materials = new[] { new NativeMaterialFrame
        { Name = "native", Shader = "native", Properties = new[] { new NativeShaderPropertyFrame { Name = "_Str", Kind = 0, Values = new[] { .25f } } } } } } },
        Lights = unknown ? null : new[] { new NativeLightFrame { Path = "0:Light", Intensity = .5f } },
    };
    private static ReplayFrame Frame(double time, params CrateFrame[] crates) => new() { T = time, Actors = new[] { new ActorFrame { Id = "scout" } }, Crates = crates };
    private static string Json(object value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static void Check(bool value) { if (!value) throw new Exception("Crate appearance assertion failed."); }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid crate appearance was accepted."); }
}
