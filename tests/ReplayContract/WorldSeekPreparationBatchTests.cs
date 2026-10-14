using PeakReplayLab;

internal static class WorldSeekPreparationBatchTests
{
    public static void Run(Action<string, Action> test)
    {
        test("far world switches never share a rendered frame or destination pose", () =>
        {
            var order = new List<string>();
            var batch = new WorldSeekPreparationBatch();
            batch.Add(() => order.Add("segment"), false);
            batch.Add(() => order.Add("native-root"), true);
            batch.Add(() => order.Add("map-node"), true);
            batch.Add(() => order.Add("day-night"), true);
            Check(!batch.Advance(10) && order.SequenceEqual(new[] { "segment", "native-root" }));
            Check(!batch.Advance(10) && order.Count == 2);
            Check(!batch.Advance(11) && order.SequenceEqual(new[] { "segment", "native-root", "map-node" }));
            Check(batch.Advance(12) && order.SequenceEqual(new[] { "segment", "native-root", "map-node", "day-night" }));
            Check(batch.Advance(12) && order.Count == 4);
        });

        test("newer seek cancels old map operations even within the same frame", () =>
        {
            var order = new List<string>();
            var batch = new WorldSeekPreparationBatch();
            batch.Add(() => order.Add("old-active-root"), true);
            batch.Add(() => order.Add("old-future-root"), true);
            Check(!batch.Advance(20));
            batch.Clear();
            batch.Add(() => order.Add("latest-root"), true);
            Check(!batch.Advance(20) && order.SequenceEqual(new[] { "old-active-root" }));
            Check(batch.Advance(21) && order.SequenceEqual(new[] { "old-active-root", "latest-root" }));
        });

        test("unchanged world target has no staged calls", () =>
        {
            var batch = new WorldSeekPreparationBatch();
            Check(batch.Advance(30));
            batch.Add(() => throw new Exception("canceled stale world operation ran"), true);
            batch.Clear();
            Check(batch.Advance(30));
        });

        test("large native shader setup remains bounded per rendered frame", () =>
        {
            int calls = 0;
            var batch = new WorldSeekPreparationBatch();
            for (int i = 0; i < 40; i++) batch.Add(() => calls++, false);
            int frames = 0;
            while (!batch.Advance(40 + frames++))
            {
                Check(calls <= frames * 8);
                Check(frames < 41);
            }
            Check(calls == 40 && frames >= 5);
        });
    }

    private static void Check(bool value)
    {
        if (!value) throw new Exception("World seek preparation failed.");
    }
}
