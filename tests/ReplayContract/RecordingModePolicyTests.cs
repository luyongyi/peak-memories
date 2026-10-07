using PeakReplayLab;

internal static class RecordingModePolicyTests
{
    public static void Run(Action<string, Action> test)
    {
        test("recording modes are mutually exclusive for their save commands", () =>
        {
            foreach (var mode in new[] { ReplayRecordingMode.Rolling120, ReplayRecordingMode.Continuous })
                Check(RecordingModePolicy.CanSaveHighlight(mode) != RecordingModePolicy.CanStartContinuous(mode));
            Check(RecordingModePolicy.CanSaveHighlight(ReplayRecordingMode.Rolling120));
            Check(!RecordingModePolicy.CanSaveHighlight(ReplayRecordingMode.Continuous));
        });
        test("mode switching requires idle title without recording saving or playback", () =>
        {
            for (int flags = 0; flags < 32; flags++)
                Check(RecordingModePolicy.CanSwitch((flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0,
                    (flags & 8) != 0, (flags & 16) != 0) == (flags == 1));
        });
        test("unknown persisted recording mode cannot silently start either mode", () =>
        {
            var unknown = (ReplayRecordingMode)99;
            Check(!RecordingModePolicy.IsKnown(unknown) && !RecordingModePolicy.CanSaveHighlight(unknown) && !RecordingModePolicy.CanStartContinuous(unknown));
            Reject(() => RecordingModePolicy.RetainedFrameLimit(unknown));
        });
        test("continuous buffer keeps only two real samples after more than 120 seconds", () =>
        {
            var buffer = new RollingBuffer(frameLimit: RecordingModePolicy.RetainedFrameLimit(ReplayRecordingMode.Continuous));
            for (int i = 0; i <= 9000; i++) { buffer.Add(Frame(i / 60d)); Check(buffer.Count <= 2); }
            var saved = buffer.Snapshot(Header(), DateTime.UtcNow);
            Check(buffer.Count == 2 && buffer.Duration < .017 && saved.Frames.Count == 2);
            Check(saved.Frames[0].Actors[0].Position[0] == (float)(8999 / 60d));
            Check(saved.Frames[1].Actors[0].Position[0] == 150f && !buffer.MemoryLimited);
        });
        test("default rolling mode still retains the original 120 second window", () =>
        {
            var buffer = new RollingBuffer();
            for (int i = 0; i <= 9000; i++) buffer.Add(Frame(i / 60d));
            Check(buffer.Count >= 7200 && buffer.Count <= 7201 && buffer.Duration <= 120);
            Check(buffer.RetainedFrameLimit == ReplayRules.MaxFrames && RecordingModePolicy.KeepsRollingHistory(ReplayRecordingMode.Rolling120));
            Check(!RecordingModePolicy.KeepsRollingHistory(ReplayRecordingMode.Continuous));
        });
        test("continuous frame accounting matches rolling accounting without extra full world estimation", () =>
        {
            var rolling = new RollingBuffer(); var continuous = new RollingBuffer(frameLimit: 2);
            var item = new ItemFrame { Key = "static-item", ItemId = 1, Prefab = "synthetic", Name = "synthetic" };
            var items = new[] { item };
            for (int i = 0; i < 100; i++)
            {
                var frame = Frame(i / 60d); frame.Items = items;
                rolling.Add(frame); continuous.Add(frame);
                Check(rolling.LastFrameEstimatedBytes == continuous.LastFrameEstimatedBytes);
                Check(continuous.LastFrameEstimatedBytes == RollingBuffer.Estimate(frame));
            }
            Check(continuous.EstimatedBytes < rolling.EstimatedBytes && continuous.Count == 2);
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
        test("continuous retention never loses the latest frame under a fitting memory budget", () =>
        {
            var buffer = new RollingBuffer(2048, frameLimit: 2);
            for (int i = 0; i < 60; i++) { buffer.Add(new ReplayFrame { T = i }); Check(buffer.Count > 0 && buffer.EstimatedBytes <= 2048); }
            Check(buffer.Snapshot(Header(), DateTime.UtcNow).Frames[^1].T == 1);
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
