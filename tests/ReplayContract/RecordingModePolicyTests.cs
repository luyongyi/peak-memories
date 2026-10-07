using PeakReplayLab;

internal static class RecordingModePolicyTests
{
    public static void Run(Action<string, Action> test)
    {
        test("legacy modes preserve highlight saving while only Continuous enables the full sink", () =>
        {
            foreach (var mode in new[] { ReplayRecordingMode.Rolling120, ReplayRecordingMode.Continuous })
                Check(RecordingModePolicy.CanSaveHighlight(mode) && RecordingModePolicy.KeepsRollingHistory(mode)
                    && RecordingModePolicy.RetainedFrameLimit(mode) == ReplayRules.MaxFrames);
            Check(!RecordingModePolicy.CanStartContinuous(ReplayRecordingMode.Rolling120));
            Check(RecordingModePolicy.CanStartContinuous(ReplayRecordingMode.Continuous));
            Check((int)ReplayRecordingMode.Rolling120 == 0 && (int)ReplayRecordingMode.Continuous == 1);
        });
        test("mode switching requires idle title without recording saving or playback", () =>
        {
            for (int flags = 0; flags < 32; flags++)
                Check(RecordingModePolicy.CanSwitch((flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0,
                    (flags & 8) != 0, (flags & 16) != 0) == (flags == 1));
        });
        test("unknown persisted recording mode cannot silently start either mode", () =>
        {
            foreach (int value in new[] { -1, 2, 99, int.MaxValue })
            {
                var unknown = (ReplayRecordingMode)value;
                Check(!RecordingModePolicy.IsKnown(unknown) && !RecordingModePolicy.CanSaveHighlight(unknown)
                    && !RecordingModePolicy.CanStartContinuous(unknown) && !RecordingModePolicy.KeepsRollingHistory(unknown));
                Reject(() => RecordingModePolicy.RetainedFrameLimit(unknown));
            }
        });
        test("full recording enabled retains every real 60 Hz sample of the last 120 seconds", () =>
        {
            var buffer = new RollingBuffer(frameLimit: RecordingModePolicy.RetainedFrameLimit(ReplayRecordingMode.Continuous));
            for (int i = 0; i <= 9000; i++) { buffer.Add(Frame(i / 60d)); Check(buffer.Count <= ReplayRules.MaxFrames); }
            var saved = buffer.Snapshot(Header(), DateTime.UtcNow);
            Check(buffer.Count >= 7200 && buffer.Count <= 7201 && buffer.Duration <= 120 && saved.Frames.Count == buffer.Count);
            Check(saved.Frames[0].Actors[0].Position[0] >= 30 && saved.Frames[0].Actors[0].Position[0] < 30.017f);
            Check(saved.Frames[^1].Actors[0].Position[0] == 150f && !buffer.MemoryLimited);
            for (int i = 1; i < saved.Frames.Count; i++) Check(Math.Abs(saved.Frames[i].T - saved.Frames[i - 1].T - 1d / 60) < 1e-10);
        });
        test("default rolling mode still retains the original 120 second window", () =>
        {
            var buffer = new RollingBuffer();
            for (int i = 0; i <= 9000; i++) buffer.Add(Frame(i / 60d));
            Check(buffer.Count >= 7200 && buffer.Count <= 7201 && buffer.Duration <= 120);
            Check(buffer.RetainedFrameLimit == ReplayRules.MaxFrames && RecordingModePolicy.KeepsRollingHistory(ReplayRecordingMode.Rolling120));
            Check(RecordingModePolicy.KeepsRollingHistory(ReplayRecordingMode.Continuous));
        });
        test("full recording configuration preserves the same rolling history and shared-state accounting", () =>
        {
            var rolling = new RollingBuffer(frameLimit: RecordingModePolicy.RetainedFrameLimit(ReplayRecordingMode.Rolling120));
            var continuous = new RollingBuffer(frameLimit: RecordingModePolicy.RetainedFrameLimit(ReplayRecordingMode.Continuous));
            var item = new ItemFrame { Key = "static-item", ItemId = 1, Prefab = "synthetic", Name = "synthetic" };
            var items = new[] { item };
            for (int i = 0; i < 100; i++)
            {
                var frame = Frame(i / 60d); frame.Items = items;
                rolling.Add(frame); continuous.Add(frame);
                Check(rolling.LastFrameEstimatedBytes == continuous.LastFrameEstimatedBytes);
                Check(continuous.LastFrameEstimatedBytes == RollingBuffer.Estimate(frame));
            }
            Check(continuous.EstimatedBytes == rolling.EstimatedBytes && continuous.Count == rolling.Count && continuous.Count == 100);
        });
        test("always-on rolling history accounts one large static snapshot once across sixty Hz frames", () =>
        {
            var items = Enumerable.Range(0, 128).Select(index => new ItemFrame
            {
                Key = "static:" + index, Prefab = "synthetic", Name = "synthetic",
                Pose = new ObjectPose { Nodes = Enumerable.Range(0, 32).Select(node => new NodePose("root/" + node,
                    new[] { (float)index, 0f, 0f }, new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f }, true, true, 0)).ToArray() },
            }).ToArray();
            foreach (var mode in new[] { ReplayRecordingMode.Rolling120, ReplayRecordingMode.Continuous })
            {
                var buffer = new RollingBuffer(frameLimit: RecordingModePolicy.RetainedFrameLimit(mode));
                var first = Frame(0); first.Items = items; buffer.Add(first);
                long initialBytes = buffer.EstimatedBytes, frameBytes = RollingBuffer.EstimateFrame(first);
                for (int i = 1; i <= 1200; i++)
                {
                    var frame = Frame(i / 60d); frame.Items = items; buffer.Add(frame);
                    Check(buffer.LastFrameEstimatedBytes == initialBytes);
                }
                Check(buffer.Count == 1201 && !buffer.MemoryLimited);
                Check(buffer.EstimatedBytes == initialBytes + 1200 * frameBytes);
                Check(buffer.EstimatedBytes < 4 * 1024 * 1024);
                var saved = buffer.Snapshot(Header(), DateTime.UtcNow);
                Check(ReferenceEquals(saved.Frames[0].Items, items) && ReferenceEquals(saved.Frames[^1].Items, items));
                Check(items[0].Pose.Nodes[0].SampleTime == 0 && items[127].Pose.Nodes[31].Position[0] == 127);
            }
        });
        test("two frame retention releases superseded shared world states", () =>
        {
            var buffer = new RollingBuffer(frameLimit: 2);
            var baseline = new RollingBuffer(frameLimit: 2);
            var first = Frame(0); first.Items = new[] { new ItemFrame { Key = "large-stale", Name = new string('n', 200), Prefab = "synthetic" } };
            buffer.Add(first);
            var a = Frame(1); var b = Frame(2);
            buffer.Add(a); buffer.Add(b); baseline.Add(a); baseline.Add(b);
            Check(buffer.Count == 2 && buffer.EstimatedBytes == baseline.EstimatedBytes);
        });
        test("always-on highlight cache remains bounded and preserves latest frame under memory pressure", () =>
        {
            foreach (var mode in new[] { ReplayRecordingMode.Rolling120, ReplayRecordingMode.Continuous })
            {
                var buffer = new RollingBuffer(2048, frameLimit: RecordingModePolicy.RetainedFrameLimit(mode));
                for (int i = 0; i < 60; i++) { buffer.Add(new ReplayFrame { T = i }); Check(buffer.Count > 0 && buffer.EstimatedBytes <= 2048); }
                var saved = buffer.Snapshot(Header(), DateTime.UtcNow);
                Check(buffer.MemoryLimited && saved.Frames.Count == buffer.Count && saved.Frames[^1].T == buffer.Duration);
            }
        });
        test("unsupported retention limits are rejected", () =>
        {
            Reject(() => new RollingBuffer(frameLimit: 0)); Reject(() => new RollingBuffer(frameLimit: 1));
            Reject(() => new RollingBuffer(frameLimit: ReplayRules.MaxFrames + 1));
        });
    }
    private static ReplayFrame Frame(double t) => new() { T = t, Actors = new[] { new ActorFrame
    { Id = "synthetic-player", Name = "synthetic", Position = new[] { (float)t, 0f, 0f } } } };
    private static ReplayHeader Header() => new() { Scene = "Level_Synthetic", Route = "synthetic" };
    private static void Reject(Action value)
    { try { value(); } catch (ArgumentOutOfRangeException) { return; } throw new Exception("Invalid recording mode/retention was accepted."); }
    private static void Check(bool value) { if (!value) throw new Exception("Recording mode policy failed."); }
}
