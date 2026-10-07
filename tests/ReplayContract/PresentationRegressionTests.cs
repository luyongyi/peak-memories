using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class PresentationRegressionTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Presentation regression failed."); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception e) when (e is InvalidDataException || e is ArgumentOutOfRangeException) { return; } throw new Exception("Invalid data accepted."); }
    private static SpawnedReplayFrame Mushroom(string key = "mushroom") => new()
    { Key = key, Kind = "bounce-shroom", Resource = "BounceShroomSpawn" };
    public static void Run(Action<string, Action> test)
    {
        test("native grid sheet null placeholder never enters AddSprite", () =>
        {
            Check(!PresentationResourceFailures.CopySpriteSlot(false, false));
            Check(!PresentationResourceFailures.CopySpriteSlot(false, true));
            Check(!PresentationResourceFailures.CopySpriteSlot(true, false));
            Check(PresentationResourceFailures.CopySpriteSlot(true, true));
        });
        test("failed native effect resource cannot rebuild on every second or entity", () =>
        {
            var failures = new PresentationResourceFailures(); Check(failures.Allows("fire")); failures.Reject("fire");
            for (int frame = 0; frame < 7200; frame++) Check(!failures.Allows("fire"));
            Check(failures.Allows("sphere")); Check(new PresentationResourceFailures().Allows("fire"));
        });
        test("failed resource cache has a finite ceiling", () =>
        {
            var failures = new PresentationResourceFailures(2); failures.Reject("a"); failures.Reject("a");
            Check(failures.Allows("b")); failures.Reject("b");
            for (int i = 0; i < 10000; i++) failures.Reject("other" + i);
            Check(!failures.Allows("c"));
        });
        test("schema 7 spawned terrain roundtrip delta removal and backward read", () =>
        {
            var source = new ReplayFrame { T = 0, Spawned = new[] { Mushroom() } };
            var writer = new ReplayDeltaCodec.Writer(ReplayRules.CurrentSchema); var reader = new ReplayDeltaCodec.Reader(ReplayRules.CurrentSchema);
            var first = reader.Decode(JObject.FromObject(writer.Encode(source)));
            var second = reader.Decode(JObject.FromObject(writer.Encode(new ReplayFrame { T = .1, Spawned = source.Spawned })));
            var last = reader.Decode(JObject.FromObject(writer.Encode(new ReplayFrame { T = .2 })));
            Check(first.Spawned.Length == 1 && last.Spawned.Length == 0 && ReferenceEquals(first.Spawned, second.Spawned));
            Check(first.Spawned[0].Resource == "BounceShroomSpawn");
        });
        test("schema 7 crop preserves already present generated terrain and shared budget", () =>
        {
            var values = new[] { Mushroom() }; var buffer = new RollingBuffer();
            buffer.Add(new ReplayFrame { T = 500, Spawned = values }); long size = buffer.EstimatedBytes;
            var next = new ReplayFrame { T = 501, Spawned = values }; buffer.Add(next);
            Check(buffer.EstimatedBytes - size == RollingBuffer.EstimateFrame(next));
            var saved = buffer.Snapshot(new ReplayHeader { Scene = "Test" }, DateTime.UtcNow);
            Check(saved.Header.Schema == ReplayRules.CurrentSchema && saved.Frames[0].T == 0 && saved.Frames[0].Spawned.Length == 1);
            Check(ReferenceEquals(saved.Frames[0].Spawned[0], values[0]));
            buffer.Add(new ReplayFrame { T = 622 }); Check(buffer.Count == 1 && buffer.EstimatedBytes == RollingBuffer.Estimate(new ReplayFrame()));
        });
        test("schema 6 cannot silently omit generated terrain and schema 7 requires it", () =>
        {
            var frame = new ReplayFrame { Spawned = new[] { Mushroom() } };
            Reject(() => new ReplayDeltaCodec.Writer(6).Encode(frame));
            var token = JObject.FromObject(new ReplayDeltaCodec.Writer(ReplayRules.CurrentSchema).Encode(frame));
            Reject(() => new ReplayDeltaCodec.Reader(6).Decode(token));
            token.Remove("Spawned"); Reject(() => new ReplayDeltaCodec.Reader(ReplayRules.CurrentSchema).Decode(token));
        });
        test("generated terrain identity and full pose validated inside frame", () =>
        {
            var frame = new ReplayFrame { Spawned = new[] { Mushroom(), Mushroom() } }; Reject(() => ReplayRules.Validate(frame, -1));
            frame.Spawned = new[] { Mushroom() }; frame.Spawned[0].Pose.Position[0] = float.NaN;
            Reject(() => ReplayRules.Validate(frame, -1));
        });
    }
}
