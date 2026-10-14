using PeakReplayLab;

internal static class PerformanceContractTests
{
    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("validation scratch remains allocation free across repeated visual and creature frames", () =>
        {
            var property = new NativeShaderPropertyFrame { Name = "_Glow", Values = new[] { 1f } };
            var renderers = new[] { new NativeRendererFrame { Materials = new[] { new NativeMaterialFrame { Properties = new[] { property } } } } };
            var lights = new[] { new NativeLightFrame() };
            var creature = new CreatureReplayFrame { Key = "c", Kind = "spider", Resource = "spider", Visuals = renderers,
                Pose = new ObjectPose { Nodes = new[] { new NodePose { Path = "node" } } } };
            var wrap = new WebWrapReplayFrame { Parts = new[] { new WebWrapReplayPart { Path = "node" } } };
            var environment = new EnvironmentReplayFrame { Winds = new[] { new EnvironmentWindFrame { Key = "wind" } },
                Fog = new EnvironmentFogFrame { Key = "fog" } };
            void Validate()
            {
                check(NativeVisualAppearanceRules.Validate(renderers)); check(NativeLightRules.Validate(lights));
                CreatureReplayRules.Validate(creature); WebWrapReplayRules.Validate(wrap); EnvironmentReplayRules.Validate(environment);
            }
            Validate(); long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) Validate();
            check(GC.GetAllocatedBytesForCurrentThread() == before);
            // Concurrent page readers use independent scratch even for identical keys.
            Parallel.For(0, 2000, _ => Validate());
        });
        test("rejected validation records release their duplicate tracking scratch", () =>
        {
            var renderer = new NativeRendererFrame(); var light = new NativeLightFrame();
            check(!NativeVisualAppearanceRules.Validate(new[] { renderer, renderer }));
            check(NativeVisualAppearanceRules.Validate(new[] { renderer }));
            check(!NativeLightRules.Validate(new[] { light, light }));
            check(NativeLightRules.Validate(new[] { light }));
            var property = new NativeShaderPropertyFrame { Name = "_Glow", Values = new[] { 1f } };
            var material = new NativeMaterialFrame { Properties = new[] { property, property } };
            renderer.Materials = new[] { material };
            check(!NativeVisualAppearanceRules.Validate(new[] { renderer }));
            material.Properties = new[] { property };
            check(NativeVisualAppearanceRules.Validate(new[] { renderer }));
            var node = new NodePose { Path = "same" };
            var creature = new CreatureReplayFrame { Key = "c", Kind = "spider", Resource = "spider", Pose = new ObjectPose { Nodes = new[] { node, node } } };
            var part = new WebWrapReplayPart { Path = "same" };
            var wrap = new WebWrapReplayFrame { Parts = new[] { part, part } };
            var wind = new EnvironmentWindFrame { Key = "same" };
            var environment = new EnvironmentReplayFrame { Winds = new[] { wind, wind } };
            void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Duplicate accepted."); }
            Reject(() => CreatureReplayRules.Validate(creature)); creature.Pose.Nodes = new[] { node }; CreatureReplayRules.Validate(creature);
            Reject(() => WebWrapReplayRules.Validate(wrap)); wrap.Parts = new[] { part }; WebWrapReplayRules.Validate(wrap);
            Reject(() => EnvironmentReplayRules.Validate(environment)); environment.Winds = new[] { wind }; EnvironmentReplayRules.Validate(environment);
        });
        test("JSON scratch buffers reuse independent bounded leases without per-record allocations", () =>
        {
            var pool = new ReplayJsonBufferPool();
            var small = pool.Rent(1024); var large = pool.Rent(65536);
            check(!ReferenceEquals(small, large)); pool.Return(small); pool.Return(large);
            var held = pool.Rent(1024);
            // Even an accidental duplicate return must not put one buffer in two slots.
            pool.Return(large); pool.Return(held);
            var first = pool.Rent(1024); var second = pool.Rent(1024);
            check(!ReferenceEquals(first, second)); pool.Return(first); pool.Return(second);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) { var buffer = pool.Rent(1024); pool.Return(buffer); }
            check(GC.GetAllocatedBytesForCurrentThread() == before);
            var oversized = pool.Rent(ReplayJsonBufferPool.MaximumRetainedLength + 1);
            pool.Return(oversized);
            check(!ReferenceEquals(oversized, pool.Rent(oversized.Length)));
            var slots = (char[]?[])typeof(ReplayJsonBufferPool).GetField("slots", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(pool)!;
            check(slots.Length == ReplayJsonBufferPool.Capacity && slots.Sum(s => s?.Length ?? 0) <= ReplayJsonBufferPool.Capacity * ReplayJsonBufferPool.MaximumRetainedLength);
        });
        test("replay headers reject schemas before 10", () =>
        {
            for (int schema = 2; schema < 10; schema++)
            {
                bool rejected = false;
                try { ReplayRules.Validate(new ReplayHeader { Scene = "test", Schema = schema }); }
                catch (InvalidDataException) { rejected = true; }
                check(rejected);
            }
            ReplayRules.Validate(new ReplayHeader { Scene = "test", Schema = 10 });
        });
        test("inventory bitmask separates backpack and player slots including 63 and 250", () =>
        {
            var actor = new ActorFrame { Id = "a", Inventory = new[] {
                new InventoryFrame { Slot = 63 }, new InventoryFrame { Slot = 63, Backpack = true },
                new InventoryFrame { Slot = 250 }, new InventoryFrame { Slot = 250, Backpack = true } } };
            ReplayRules.Validate(new ReplayFrame { T = 0, Actors = new[] { actor } }, -1);
            actor.Inventory[1].Backpack = false;
            try { ReplayRules.Validate(new ReplayFrame { T = 0, Actors = new[] { actor } }, -1); }
            catch (InvalidDataException) { return; }
            throw new Exception("duplicate slot was accepted");
        });
        test("shared object arrays survive reference accounting and eviction", () =>
        {
            var buffer = new RollingBuffer();
            var items = new[] { new ItemFrame { Key = "ground", ItemId = 1 } };
            var crates = new[] { new CrateFrame { Key = "chest", Kind = "Luggage" } };
            for (int i = 0; i <= 7300; i++) buffer.Add(new ReplayFrame { T = i / 60d, Items = items, Crates = crates });
            check(buffer.Count <= 7201 && buffer.EstimatedBytes > 0);
            for (int i = 1; i <= 122; i++) buffer.Add(new ReplayFrame { T = 122 + i });
            check(buffer.Count <= 121 && buffer.EstimatedBytes == buffer.Count * RollingBuffer.Estimate(new ReplayFrame()));
        });
        test("deferred snapshot uses frozen frame references while rolling capture continues", () =>
        {
            var buffer = new RollingBuffer(); buffer.Add(new ReplayFrame { T = 500 }); buffer.Add(new ReplayFrame { T = 501 });
            var build = buffer.PrepareSnapshot(new ReplayHeader { Scene = "test" }, DateTime.UtcNow);
            for (int i = 502; i <= 700; i++) buffer.Add(new ReplayFrame { T = i });
            var clip = build(); check(clip.Frames.Count == 2 && clip.Frames[0].T == 0 && clip.Frames[1].T == 1);
        });
        test("cropped snapshot rebases anchored box without mutating capture", () =>
        {
            var box = new CrateFrame { Key = "box", Kind = "Luggage", Animation = new CrateAnimationFrame {
                Clip = "Open", Anchored = true, AnchorTime = 499, Time = 0, Duration = 4, Rate = 1 } };
            var buffer = new RollingBuffer(); var states = new[] { box };
            buffer.Add(new ReplayFrame { T = 500, Crates = states }); buffer.Add(new ReplayFrame { T = 501, Crates = states });
            var clip = buffer.Snapshot(new ReplayHeader { Scene = "test" }, DateTime.UtcNow);
            var copy = clip.Frames[0].Crates[0];
            check(box.Animation.AnchorTime == 499 && copy.Animation!.AnchorTime == -1);
            check(ReferenceEquals(clip.Frames[0].Crates, clip.Frames[1].Crates));
            check(CrateAnimationTimeline.Sample(copy.Animation!, 0, 4, false) == 1);
        });
        test("timing scopes are bounded and disable cleanly", () =>
        {
            ReplayPerformance.Reset(); ReplayPerformance.Enabled = true;
            for (int i = 0; i < 600; i++) using (ReplayPerformance.Measure(ReplayStage.Capture)) { }
            check(ReplayPerformance.Report().Contains("n=600") && ReplayPerformance.Summary(false).Contains("p95"));
            ReplayPerformance.Enabled = false;
            using (ReplayPerformance.Measure(ReplayStage.Capture)) { }
            check(ReplayPerformance.Report().Contains("n=600")); ReplayPerformance.Enabled = true;
        });
        test("slow CPU scopes retain threshold and calibrated start and end clocks", () =>
        {
            var probe = new ReplaySlowCpuDiagnostics(60000);
            var fast = probe.Begin(0, 7); probe.End(ReplayStage.Playback, fast, 999, 0, 7);
            check(probe.Snapshot().Length == 0);
            var uncalibrated = probe.Begin(1000, 7); probe.End(ReplayStage.PlaybackWorld, uncalibrated, 2000, 1, 7);
            probe.SetFrameContext(10, 100, 60000, 7);
            var calibrated = probe.Begin(60300, 7);
            // Recalibration during a long scope must not change its time origin.
            probe.SetFrameContext(11, 1000, 60600, 7);
            probe.End(ReplayStage.PlaybackAudioSeek, calibrated, 61500, 2, 7);
            var result = probe.Snapshot(); check(result.Length == 2);
            check(!result[0].HasFrameContext && result[0].StartFrame == -1 && result[0].EndFrame == -1 &&
                result[0].StartRealtime == -1 && result[0].EndRealtime == -1 && result[0].GlobalCollections == 1);
            check(result[0].Milliseconds == ReplaySlowCpuDiagnostics.MinimumMilliseconds);
            check(result[1].Stage == "PlaybackAudioSeek" && result[1].HasFrameContext && result[1].StartFrame == 10 && result[1].EndFrame == 11);
            check(Math.Abs(result[1].StartRealtime - 100.005) < 1e-10 && Math.Abs(result[1].EndRealtime - 100.025) < 1e-10);
            check(result[1].Milliseconds == 20 && result[1].GlobalCollections == 2);
        });
        test("slow CPU scope recording allocates nothing and retains detached latest capacity", () =>
        {
            var probe = new ReplaySlowCpuDiagnostics(60000);
            probe.SetFrameContext(0, 0, 0, 7);
            var warm = probe.Begin(0, 7); probe.End(ReplayStage.Playback, warm, 1200, 0, 7); probe.Reset();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++)
            {
                probe.SetFrameContext(i, i, i * 60000L, 7);
                var context = probe.Begin(i * 60000L, 7);
                probe.End(ReplayStage.Playback, context, i * 60000L + 1200, i % 3, 7);
            }
            check(GC.GetAllocatedBytesForCurrentThread() == before);
            var first = probe.Snapshot(); check(first.Length == ReplaySlowCpuDiagnostics.Capacity && first[0].StartFrame == 9872 && first[^1].EndFrame == 9999);
            first[0].Stage = "changed"; first[0].StartFrame = -10;
            var second = probe.Snapshot(); check(second[0].Stage == "Playback" && second[0].StartFrame == 9872 && !ReferenceEquals(first[0], second[0]));
            probe.Reset(); check(probe.Snapshot().Length == 0 && second[^1].StartFrame == 9999);
        });
        test("slow CPU scope disable and reset reject already open scopes", () =>
        {
            var probe = new ReplaySlowCpuDiagnostics(60000); probe.SetFrameContext(1, 1, 60000, 7);
            var completed = probe.Begin(60000, 7); probe.End(ReplayStage.Playback, completed, 61200, 0, 7);
            var open = probe.Begin(62000, 7); probe.Enabled = false;
            probe.SetFrameContext(2, 2, 120000, 7); probe.End(ReplayStage.Playback, open, 63200, 0, 7);
            var disabled = probe.Begin(64000, 7); probe.End(ReplayStage.Playback, disabled, 65200, 0, 7);
            check(probe.Snapshot().Length == 1);
            probe.Enabled = true; probe.End(ReplayStage.Playback, open, 66000, 0, 7);
            var uncalibrated = probe.Begin(67000, 7); probe.End(ReplayStage.Playback, uncalibrated, 68200, 0, 7);
            check(probe.Snapshot().Length == 2 && !probe.Snapshot()[1].HasFrameContext);
            probe.SetFrameContext(3, 3, 180000, 7); var preReset = probe.Begin(180000, 7);
            probe.Reset(); probe.End(ReplayStage.Playback, preReset, 181200, 0, 7);
            check(probe.Snapshot().Length == 0);
            var fresh = probe.Begin(182000, 7); probe.End(ReplayStage.Playback, fresh, 183200, 0, 7);
            check(probe.Snapshot().Length == 1 && probe.Snapshot()[0].HasFrameContext && probe.Snapshot()[0].StartFrame == 3);
        });
        test("slow CPU scope clocks reject invalid calibration and foreign thread scopes", () =>
        {
            var probe = new ReplaySlowCpuDiagnostics(60000); probe.SetFrameContext(1, 1, 60000, 7);
            var foreign = probe.Begin(60000, 8); probe.End(ReplayStage.Playback, foreign, 61200, 0, 8);
            var crossed = probe.Begin(60000, 7); probe.End(ReplayStage.Playback, crossed, 61200, 0, 8);
            var backwards = probe.Begin(62000, 7); probe.End(ReplayStage.Playback, backwards, 61000, 0, 7);
            check(probe.Snapshot().Length == 0);
            probe.SetFrameContext(2, double.NaN, 120000, 7);
            var unknown = probe.Begin(120000, 7); probe.End(ReplayStage.Playback, unknown, 121200, -1, 7);
            var value = probe.Snapshot()[0]; check(!value.HasFrameContext && value.GlobalCollections == 0 && value.Milliseconds == 20);
        });
        test("production CPU scopes discard stale epochs and remain allocation free after warmup", () =>
        {
            ReplayPerformance.Enabled = true; ReplayPerformance.Reset(); ReplayPerformance.SetFrameContext(1, 1);
            var disabled = ReplayPerformance.Measure(ReplayStage.PlaybackAudioClock); ReplayPerformance.Enabled = false;
            disabled.Dispose(); ReplayPerformance.Enabled = true; disabled.Dispose();
            var reset = ReplayPerformance.Measure(ReplayStage.PlaybackAudioClock); ReplayPerformance.Reset(); reset.Dispose();
            check(ReplayPerformance.Snapshot().Single(s => s.Stage == "PlaybackAudioClock").Samples == 0 && ReplayPerformance.SlowCpuSnapshot().Length == 0);
            ReplayPerformance.SetFrameContext(2, 2);
            for (int i = 0; i < 100; i++) using (ReplayPerformance.Measure(ReplayStage.PlaybackAudioClock)) { }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++)
            { ReplayPerformance.SetFrameContext(i + 3, i + 3); using (ReplayPerformance.Measure(ReplayStage.PlaybackAudioClock)) { } }
            check(GC.GetAllocatedBytesForCurrentThread() == before);
            check(ReplayPerformance.Snapshot().Single(s => s.Stage == "PlaybackAudioClock").Samples == 10100);
            ReplayPerformance.Reset();
        });
        test("seek diagnostics distinguish buffering from pause and freeze", () =>
        {
            var probe = new ReplaySeekDiagnostics(); probe.Request(20, false, 1);
            probe.Observe(2, 5, true, false, 3, 16);
            var wait = probe.Snapshot(2)[0]; check(wait.TargetPoseReadyMs == -1 && wait.PendingMs == 1000 && wait.ClockNotAdvancingMs == 0);
            probe.Observe(3, 20, false, false, 7, 19); probe.Observe(4, 20, false, false, 2, 16);
            var ready = probe.Snapshot(4)[0]; check(ready.TargetPoseReadyMs == 2000 && ready.ClockNotAdvancingMs == 1000 && ready.MaximumPlaybackCpuMs == 7);
            probe.Observe(5, 20, false, true, 1, 16); check(probe.Snapshot(5)[0].ClockNotAdvancingMs == 0);
            check(wait.TargetPoseReadyMs == -1);
        });
        test("timing scopes distinguish global collection interruptions from ordinary work", () =>
        {
            ReplayPerformance.Reset(); ReplayPerformance.Enabled = true;
            using (ReplayPerformance.Measure(ReplayStage.Playback)) { GC.Collect(); }
            using (ReplayPerformance.Measure(ReplayStage.Playback)) { }
            var stage = ReplayPerformance.Snapshot().Single(s => s.Stage == "Playback");
            check(stage.Samples == 2 && stage.SamplesOverlappingGlobalGc >= 1 && stage.ObservedGlobalCollections >= 1);
            check(stage.MaximumMsOverlappingGlobalGc > 0 && stage.MaximumMsWithoutGlobalGc >= 0);
            ReplayPerformance.Reset();
            check(ReplayPerformance.Snapshot().Single(s => s.Stage == "Playback").ObservedGlobalCollections == 0);
        });
        test("seek diagnostics measure real buffering time and repeated playback stalls", () =>
        {
            var probe = new ReplaySeekDiagnostics(); probe.Request(20, false, 1);
            probe.Observe(2, 5, true, false, 1, 16); probe.Observe(3, 20, false, false, 1, 16);
            probe.Observe(4, 21, true, false, 1, 16); probe.Observe(6, 21, true, false, 1, 16);
            var pending = probe.Snapshot(7)[0];
            check(pending.BufferingMs == 5000 && pending.LongestBufferingMs == 3000 && pending.RebufferCount == 1);
            probe.Observe(8, 21.1, false, false, 1, 16);
            var ready = probe.Snapshot(8)[0];
            check(ready.BufferingMs == 6000 && ready.LongestBufferingMs == 4000 && ready.RebufferCount == 1);
            check(pending.BufferingMs == 5000);
        });
        test("rapid seek diagnostics retain a bounded latest target history", () =>
        {
            var probe = new ReplaySeekDiagnostics();
            for (int i = 0; i < 200; i++) probe.Request(i, false, i);
            var result = probe.Snapshot(200); check(result.Length == ReplaySeekDiagnostics.Capacity);
            check(result[0].Sequence == 169 && result[^1].Sequence == 200 && result[^1].TargetSeconds == 199);
            check(result[^2].Superseded && result[^2].PendingMs == 1000 && !result[^1].Superseded);
        });
        test("whole-frame hitch diagnostics separate GC overlap from buffering and retain bounded detached records", () =>
        {
            var probe = new ReplayHitchDiagnostics();
            probe.Observe(.016, 0, false, false, 5, true);
            probe.Observe(.12, 1, false, false, 7, true);
            probe.Observe(.05, 2, true, false, 7, true);
            probe.Observe(.016, 2, true, false, 8, true);
            var first = probe.Snapshot();
            check(first.ObservedFrames == 3 && first.GlobalCollections == 3 && first.FramesOver33Ms == 2 && first.FramesOver100Ms == 1);
            check(first.SlowFramesOverlappingGlobalGc == 1 && first.ActivePlaybackSlowFrames == 1);
            check(first.MaximumMsOverlappingGlobalGc == 120 && first.MaximumMsWithoutGlobalGc == 50);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) probe.Observe(.04, i, false, true, 8, true);
            check(GC.GetAllocatedBytesForCurrentThread() - allocated == 0);
            var bounded = probe.Snapshot();
            check(bounded.RecentSlowFrames.Length == ReplayHitchDiagnostics.Capacity && bounded.RecentSlowFrames[^1].ReplaySeconds == 9999);
            check(first.RecentSlowFrames.Length == 2 && first.RecentSlowFrames[0].GlobalCollections == 2 && first.RecentSlowFrames[1].Buffering);
            probe.Observe(.1, 0, false, false, 20, false); probe.Observe(.1, 0, false, false, 22, true);
            check(probe.Snapshot().GlobalCollections == 3);
            probe.Reset(); check(probe.Snapshot().ObservedFrames == 0 && probe.Snapshot().RecentSlowFrames.Length == 0);
        });
        test("schema 10 anchored box survives archive and static array sharing", () =>
        {
            var states = new[] { new CrateFrame { Key = "chest", Kind = "Luggage", Animation = new CrateAnimationFrame {
                Clip = "Open", Anchored = true, AnchorTime = 99, Duration = 3 } } };
            var buffer = new RollingBuffer();
            buffer.Add(new ReplayFrame { T = 100, Crates = states }); buffer.Add(new ReplayFrame { T = 101, Crates = states });
            string dir = Path.Combine(Path.GetTempPath(), "peak-anchor-test-" + Guid.NewGuid().ToString("N")); string? path = null;
            try
            {
                var snapshot = buffer.Snapshot(new ReplayHeader { Schema = 10, Scene = "test" }, DateTime.UtcNow);
                path = ReplayArchive.Save(dir, snapshot); var clip = ReplayFiles.Read(path);
                check(clip.Header.Schema == 10 && clip.Frames[0].Crates[0].Animation!.Anchored);
                check(ReferenceEquals(clip.Frames[0].Crates, clip.Frames[1].Crates));
                check(CrateAnimationTimeline.Sample(clip.Frames[1].Crates[0].Animation!, 1, 3, false) == 2);
                snapshot.Header.Schema = 9;
                try { ReplayArchive.Save(dir, snapshot); }
                catch (InvalidDataException) { return; }
                throw new Exception("previous replay schemas must not be written");
            }
            finally { if (path != null) File.Delete(path); if (Directory.Exists(dir)) Directory.Delete(dir); }
        });
    }
}
