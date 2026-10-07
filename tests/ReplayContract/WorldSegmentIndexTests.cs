using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class WorldSegmentIndexTests
{
    public static void Run(Action<string, Action> test)
    {
        test("ordinary world segments retain their map layer indices", () =>
        {
            for (int segment = 0; segment <= 4; segment++)
                Check(WorldSegmentIndex.Resolve(segment, 6) == segment);
        });
        test("Peak enum uses the final mountain layer even when Nadir is available", () =>
        {
            Check(WorldSegmentIndex.Resolve(5, 5) == 4);
            Check(WorldSegmentIndex.Resolve(5, 6) == 4);
        });
        test("Void enum uses the appended Nadir layer and requires that layer", () =>
        {
            Check(WorldSegmentIndex.Resolve(6, 6) == 5);
            Reject(() => WorldSegmentIndex.Resolve(6, 5));
        });
        test("world segment seeks resolve independently of previous stage", () =>
        {
            int[] recorded = { 4, 6, 5, 0, 6, 4 };
            int[] layers = { 4, 5, 4, 0, 5, 4 };
            for (int i = 0; i < recorded.Length; i++)
                Check(WorldSegmentIndex.Resolve(recorded[i], 6) == layers[i]);
        });
        test("unknown or unavailable world segments never address unrelated map layers", () =>
        {
            foreach (int segment in new[] { -1, 7, 10, int.MaxValue })
                Reject(() => WorldSegmentIndex.Resolve(segment, 20));
            Reject(() => WorldSegmentIndex.Resolve(0, 0));
            Reject(() => WorldSegmentIndex.Resolve(4, 4));
            Reject(() => WorldSegmentIndex.Resolve(5, 4));
        });
        test("current codec preserves raw Peak and Void enum values", () =>
        {
            foreach (int schema in new[] { ReplayRules.CurrentSchema })
            {
                var writer = new ReplayDeltaCodec.Writer(schema);
                var reader = new ReplayDeltaCodec.Reader(schema);
                int[] segments = { 4, 6, 5, 0 };
                int[] layers = { 4, 5, 4, 0 };
                for (int i = 0; i < segments.Length; i++)
                {
                    int segment = segments[i];
                    var source = new ReplayFrame { T = i * .1, World = new WorldFrame { Segment = segment } };
                    var restored = reader.Decode(JObject.FromObject(writer.Encode(source)));
                    Check(restored.World.Segment == segment);
                    Check(WorldSegmentIndex.Resolve(restored.World.Segment, 6) == layers[i]);
                }
            }
        });
        test("Nadir presentation enters and restores the mountain on backward seek", () =>
        {
            var state = new WorldEnvironmentState(true, false);
            Check(state.Transition(4, out bool mountain, out bool nadir) && mountain && !nadir);
            Check(state.Transition(6, out mountain, out nadir) && !mountain && nadir);
            Check(!state.Transition(6, out mountain, out nadir) && !mountain && nadir);
            Check(state.Transition(5, out mountain, out nadir) && mountain && !nadir);
            Check(!state.Transition(0, out mountain, out nadir) && mountain && !nadir);
            Check(state.Transition(6, out mountain, out nadir) && !mountain && nadir);
            Check(state.Transition(0, out mountain, out nadir) && mountain && !nadir);
        });
        test("a clip starting inside Nadir applies its state without a prior mountain frame", () =>
        {
            var state = new WorldEnvironmentState(true, false);
            Check(state.Transition(6, out bool mountain, out bool nadir) && !mountain && nadir);
        });
        test("leaving Nadir restores the original ancestor and native flag states", () =>
        {
            foreach (bool initialMountain in new[] { false, true })
            foreach (bool initialVoid in new[] { false, true })
            {
                var state = new WorldEnvironmentState(initialMountain, initialVoid);
                state.Transition(6, out _, out _);
                Check(state.Transition(4, out bool mountain, out bool nadir));
                Check(mountain == initialMountain && nadir == initialVoid);
            }
        });
    }

    private static void Check(bool value)
    {
        if (!value) throw new Exception("World segment mapping failed.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Invalid world segment was accepted.");
    }
}
