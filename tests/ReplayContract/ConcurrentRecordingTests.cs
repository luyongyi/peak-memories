using Newtonsoft.Json;
using PeakReplayLab;
using System.Text.RegularExpressions;

internal static class ConcurrentRecordingTests
{
    public static void Run(Action<string, Action> test)
    {
        test("one shared capture stream produces a complete full run and repeated independent F6 clips", () =>
        {
            using var files = new TestFiles();
            var header = Header();
            var rolling = new RollingBuffer();
            var writer = files.Writer(header);
            var source = Enumerable.Range(0, 181).Select(i => Frame(500 + i, i)).ToArray();
            string original = Json(source);
            ReplayClip? early = null;
            foreach (var frame in source)
            {
                rolling.Add(frame);
                Check(writer.TryEnqueue(frame, rolling.LastFrameEstimatedBytes), "A shared full-run sample was rejected.");
                if (frame.T == 560)
                {
                    early = SaveClip(files, rolling, header);
                    Check(writer.Fault == null && writer.Stats.State == "Recording", "F6 stopped the full writer.");
                }
            }
            var late = SaveClip(files, rolling, header);
            var full = Done(writer);
            Check(early != null && early.Complete && early.Frames.Count == 61 && early.Duration == 60, "The early clip lost gameplay.");
            Check(late.Complete && late.Frames.Count == 121 && late.Duration == 120, "The second clip is not the latest 120-second window.");
            Close(late.Frames[0].Actors[0].Position[2], source[60].Actors[0].Position[2]);
            Check(full.Status == "completed" && full.AcceptedFrames == 181 && full.WrittenFrames == 181 && full.Duration == 180,
                "Saving clips altered the full recording.");
            var frames = ReadFull(full);
            Check(frames.Count == 181 && frames[0].T == 0 && frames[^1].T == 180, "Full-run seek pages lost or duplicated source samples.");
            for (int i = 0; i < frames.Count; i++) AssertStoredFrame(frames[i], source[i], 500);
            Check(Json(source) == original, "A clip/full writer mutated shared capture data.");
            for (int i = 0; i < source.Length; i++)
                foreach (var node in source[i].Actors[0].JointPose) Close(node.SampleTime, 500 + i);
            Check(Directory.GetFiles(files.Root, "*.peakrun").Length == 1 && Directory.GetFiles(files.Root, "*.peakreplay").Length == 2,
                "Each save must create only its own recording file.");
        });

        test("stopping and restarting the real full writer leaves the same rolling window continuous", () =>
        {
            using var files = new TestFiles();
            var header = Header();
            var rolling = new RollingBuffer();
            var first = files.Writer(header);
            for (int i = 0; i < 60; i++)
            {
                var frame = Frame(500 + i, i); rolling.Add(frame);
                Check(first.TryEnqueue(frame, rolling.LastFrameEstimatedBytes));
            }
            var firstResult = Done(first);
            Check(firstResult.Status == "completed" && firstResult.Duration == 59 && firstResult.WrittenFrames == 60);
            for (int i = 60; i < 141; i++) rolling.Add(Frame(500 + i, i));
            var beforeRestart = SaveClip(files, rolling, header);
            Check(beforeRestart.Frames.Count == 121 && beforeRestart.Duration == 120);
            Close(beforeRestart.Frames[0].Actors[0].Position[2], 4);
            var second = files.Writer(header);
            for (int i = 141; i < 181; i++)
            {
                var frame = Frame(500 + i, i); rolling.Add(frame);
                Check(second.TryEnqueue(frame, rolling.LastFrameEstimatedBytes));
            }
            var afterRestart = SaveClip(files, rolling, header);
            var secondResult = Done(second);
            Check(afterRestart.Frames.Count == 121 && afterRestart.Duration == 120);
            Close(afterRestart.Frames[0].Actors[0].Position[2], 12);
            Check(secondResult.Status == "completed" && secondResult.Duration == 39 && secondResult.WrittenFrames == 40,
                "Restart replayed old rolling frames into the new full run.");
            var frames = ReadFull(secondResult);
            Close(frames[0].Actors[0].Position[2], 28.2);
            Check(frames[0].T == 0 && frames[^1].T == 39);
        });

        test("a deferred clipped snapshot and backward seeks cannot rebase live full-run clocks or joints", () =>
        {
            using var files = new TestFiles();
            var header = Header();
            // A short memory-limited cache gives the clip and full file different
            // time origins without inventing a second capture implementation.
            var rolling = new RollingBuffer(frameLimit: 21);
            var writer = files.Writer(header);
            var source = Enumerable.Range(0, 160).Select(i => Frame(500 + i / 10d, i)).ToArray();
            string original = Json(source);
            Func<ReplayClip>? pendingSnapshot = null;
            for (int i = 0; i < source.Length; i++)
            {
                rolling.Add(source[i]);
                Check(writer.TryEnqueue(source[i], rolling.LastFrameEstimatedBytes));
                if (i == 139) pendingSnapshot = rolling.PrepareSnapshot(header, DateTime.UtcNow);
            }
            var snapshot = pendingSnapshot!();
            Check(snapshot.Frames.Count == 21);
            Close(snapshot.Duration, 2);
            Close(snapshot.Frames[0].Actors[0].JointPose[0].SampleTime, 0);
            Close(snapshot.Frames[0].Actors[0].RouteState!.SampleTime, 0);
            Close(snapshot.Frames[0].Actors[0].Position[2], source[119].Actors[0].Position[2]);
            Check(!ReferenceEquals(snapshot.Frames[0].Actors[0], source[119].Actors[0]), "Clip rebasing reused a mutable actor wrapper.");
            Check(!ReferenceEquals(snapshot.Frames[0].Actors[0].JointPose[0], source[119].Actors[0].JointPose[0]), "Clip rebasing changed the original node clock.");
            var read = ReplayFiles.Read(ReplayArchive.Save(files.Root, snapshot));
            using (var timeline = new MemoryReplayTimeline(read))
            {
                double? earlyValue = null;
                foreach (double time in new[] { .05, 1.75, .05, 2d, .25, .05 })
                {
                    Check(timeline.TrySample(time, out var sample), "Clip seek could not sample recorded joints.");
                    Check(sample.Joints!.TryActor("synthetic-player", sample.Time, out var actor));
                    var joint = actor.At(1);
                    double value = joint.Left.Position[0] + (joint.Right.Position[0] - joint.Left.Position[0]) * joint.Mix;
                    if (time == .05) { if (earlyValue.HasValue) Close(value, earlyValue.Value); else earlyValue = value; }
                }
            }
            var full = Done(writer);
            Check(full.Status == "completed" && full.WrittenFrames == 160);
            Close(full.Duration, 15.9);
            var frames = ReadFull(full);
            Check(frames.Count == 160);
            for (int i = 0; i < frames.Count; i++) AssertStoredFrame(frames[i], source[i], 500);
            Check(Json(source) == original, "Snapshot construction or seeks contaminated a frame still shared with the full writer.");
            for (int i = 0; i < source.Length; i++)
                foreach (var node in source[i].Actors[0].JointPose) Close(node.SampleTime, 500 + i / 10d);
            var current = rolling.Snapshot(header, DateTime.UtcNow);
            Close(current.Frames[0].Actors[0].Position[2], source[139].Actors[0].Position[2]);
            Close(current.Frames[^1].Actors[0].JointPose[0].SampleTime, 2);
        });

        test("starting full recording from an existing cache preserves earlier joint and animation baselines", () =>
        {
            using var files = new TestFiles();
            var header = Header();
            var rolling = new RollingBuffer();
            rolling.Add(Frame(400, 0));
            rolling.Add(Frame(499.9, 1));
            var writer = files.Writer(header);
            var effect = new EffectReplayFrame { Key = "effect", Resource = "native-effect", AnchorTime = 490, Phase = 2, Rate = 1 };
            var crate = new CrateFrame { Key = "crate", Kind = "luggage", Animation = new CrateAnimationFrame
                { Clip = "open", Anchored = true, AnchorTime = 491, Rate = 1, Duration = 2 } };
            var audio = new AudioReplayFrame { Key = "audio", Clip = "native-audio", StartedAt = 492, AnchorTime = 493, Duration = 2, Loop = true };
            var balloon = new BalloonReplayFrame { Key = "balloon", OwnerId = "synthetic-player", Started = 494 };
            var rope = new RopeReplayFrame { Key = "rope", Kind = "rope", Resource = "native-rope", Points = new[] { new RopeReplayPoint() }, Bones = new[] { new RopeReplayPoint() } };
            var creature = new CreatureReplayFrame { Key = "spider", Kind = "spider", Resource = "spider" };
            var observed = new List<ReplayFrame>();
            for (int i = 0; i < 2; i++)
            {
                var frame = Frame(500 + i / 10d, 2 + i);
                frame.Actors[0].JointPose[1].SampleTime = frame.T - .05;
                frame.Actors[0].RouteState!.SampleTime = frame.T - .05;
                frame.World.Environment = new EnvironmentReplayFrame { SampleTimeKnown = true, SampleTime = frame.T - .05 };
                frame.Crates = new[] { crate }; frame.Effects = new[] { effect }; frame.Audio = new[] { audio };
                frame.Balloons = new[] { balloon }; frame.Ropes = new[] { rope }; frame.Creatures = new[] { creature };
                rolling.Add(frame); observed.Add(frame);
                Check(writer.TryEnqueue(frame, rolling.LastFrameEstimatedBytes));
            }
            var clip = SaveClip(files, rolling, header);
            Check(clip.Frames.Count == 4 && clip.Complete);
            Close(clip.Duration, 100.1);
            var result = Done(writer);
            Check(result.Status == "completed" && result.WrittenFrames == 2);
            Close(result.Duration, .1);
            var frames = ReadFull(result);
            for (int i = 0; i < frames.Count; i++) AssertStoredFrame(frames[i], observed[i], 500);
            var first = frames[0];
            Close(first.Actors[0].JointPose[1].SampleTime, -.05);
            Close(first.Actors[0].RouteState!.SampleTime, -.05);
            Close(first.World.Environment!.SampleTime, -.05);
            Close(first.Crates[0].Animation!.AnchorTime, -9);
            Close(first.Effects[0].AnchorTime, -10);
            Close(first.Audio[0].StartedAt, -8);
            Close(first.Audio[0].AnchorTime, -7);
            Close(first.Balloons[0].Started, -6);
            Check(first.Ropes.Length == 1 && first.Creatures.Length == 1, "The current full baseline lost already-observed objects.");
            Close(observed[0].Actors[0].JointPose[1].SampleTime, 499.95);
            Close(effect.AnchorTime, 490); Close(crate.Animation!.AnchorTime, 491);
            Close(audio.AnchorTime, 493); Close(balloon.Started, 494);
            using var timeline = new MemoryReplayTimeline(FullReplayArchive.ReadPage(result.FilePath, FullReplayArchive.ReadInfo(result.FilePath), 0));
            Check(timeline.TrySample(0, out var sample) && sample.Joints!.TryActor("synthetic-player", sample.Time, out _),
                "A legitimate negative opening observation cannot be played.");
        });

        test("a real full-file disk failure leaves ongoing rolling capture and F6 saving intact", () =>
        {
            using var files = new TestFiles();
            var header = Header();
            var rolling = new RollingBuffer();
            var writer = files.Writer(header, open: path => new FailedDisk(NewFile(path)));
            for (int i = 0; i < 2; i++)
            {
                var frame = Frame(500 + i, i); rolling.Add(frame);
                writer.TryEnqueue(frame, rolling.LastFrameEstimatedBytes);
            }
            var fault = Done(writer);
            Check(fault.Status == "faulted" && fault.FaultCode == "write-failure" && File.Exists(writer.PartialPath) && !File.Exists(writer.FilePath));
            for (int i = 2; i < 181; i++)
            {
                var frame = Frame(500 + i, i); rolling.Add(frame);
                Check(!writer.TryEnqueue(frame, rolling.LastFrameEstimatedBytes), "A faulted full writer unexpectedly resumed.");
            }
            var clip = SaveClip(files, rolling, header);
            Check(clip.Complete && clip.Duration == 120 && clip.Frames.Count == 121);
            Close(clip.Frames[^1].Actors[0].Position[2], 36);
            Check(writer.Stats.QueuedBytes == 0, "Failed full writer leaked queue reservations.");
        });

        test("full writer queue overflow cannot drop or stop the independent rolling clip history", () =>
        {
            using var files = new TestFiles();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var header = Header();
            var rolling = new RollingBuffer();
            var writer = files.Writer(header, new ContinuousReplayOptions { QueueFrameLimit = 2 }, path =>
            { entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(10)), "Blocked file opener timed out."); return NewFile(path); });
            try
            {
                Check(entered.Wait(TimeSpan.FromSeconds(10)), "Full worker did not reach the test gate.");
                for (int i = 0; i < 181; i++)
                {
                    var frame = Frame(500 + i, i); rolling.Add(frame);
                    bool accepted = writer.TryEnqueue(frame, rolling.LastFrameEstimatedBytes);
                    Check(accepted == (i < 2), "Overflow silently accepted or dropped a sample.");
                }
                var clip = SaveClip(files, rolling, header);
                Check(clip.Complete && clip.Duration == 120 && clip.Frames.Count == 121);
                Close(clip.Frames[^1].Actors[0].Position[2], 36);
            }
            finally { release.Set(); }
            var fault = Done(writer);
            Check(fault.Status == "faulted" && fault.FaultCode == "queue-overflow" && fault.AcceptedFrames == 2 && fault.WrittenFrames == 2);
            Check(writer.Stats.QueuedBytes == 0 && File.Exists(writer.PartialPath) && !File.Exists(writer.FilePath));
        });

        test("production capture wiring samples once and F4/F6 never recreate or clear the rolling cache", () =>
        {
            string capture = Method(Source("ReplayCapture.cs"), "public void Tick(");
            Check(Regex.Matches(capture, @"new\s+ReplayFrame\b").Count == 1 && Regex.Matches(capture, @"\bCapture\.Actor\s*\(").Count == 1,
                "Parallel recording added a second actor/frame capture.");
            Check(Regex.Matches(capture, @"\bBuffer\.Add\s*\(frame\)").Count == 1 &&
                Regex.Matches(capture, @"accepted\?\.Invoke\s*\(frame,\s*Buffer\.LastFrameEstimatedBytes\)").Count == 1,
                "The full sink no longer receives the same buffered capture frame.");
            Check(capture.IndexOf("Buffer.Add(frame)", StringComparison.Ordinal) < capture.IndexOf("accepted?.Invoke", StringComparison.Ordinal),
                "A full writer failure can prevent rolling insertion.");
            string plugin = Source("Plugin.cs");
            string update = Method(plugin, "private void LateUpdate(");
            Check(Regex.Matches(update, @"\bcapture\.Tick\s*\(").Count == 1 && update.Contains("capture.Tick(fullRun != null ? fullRunSink : null)"),
                "A second capture path was introduced or full recording now gates all capture.");
            Check(!update.Contains("IsContinuous"), "Turning full recording off disables the rolling update.");
            string save = Method(plugin, "private void SaveHighlight(");
            Check(save.Contains("PrepareSnapshot") && !save.Contains("IsContinuous") && !save.Contains("recordingMode"),
                "F6 is still mutually exclusive with full recording.");
            string full = Source("Plugin.FullRun.cs");
            foreach (string signature in new[] { "private void ToggleFullRun(", "private void StopFullRun(", "private void SetRecordingMode(" })
            {
                string action = Method(full, signature);
                Check(!Regex.IsMatch(action, @"\bcapture\??\.Dispose\s*\(|\bcapture\s*=(?!=)|new\s+RollingCapture\b|\bBuffer\.Clear\s*\("),
                    signature + " destroys the shared clip history.");
            }
            Check(full.Contains("\"ContinuousEnabled\", legacyEnabled"), "Legacy settings no longer initialize the independent persistent full toggle.");
        });
    }

    private static ReplayHeader Header() => new() { Scene = "Level_ConcurrentTest", Route = "Shore,Roots,Alpine,Volcano,Kiln", SampleHz = 10 };
    private static ReplayFrame Frame(double time, int sequence) => new()
    {
        T = time,
        Actors = new[] { new ActorFrame
        {
            Id = "synthetic-player", Name = "合成玩家（仅测试）", Position = new[] { sequence * .01f, 1f, sequence * .2f },
            JointPose = new[]
            {
                new NodePose { Path = ".", SampleTime = time, Position = new[] { sequence * .01f, 1f, sequence * .2f } },
                new NodePose { Path = "./hat#0", SampleTime = time, Position = new[] { sequence * .001f, 1f, 0f } },
            },
            RouteState = new ActorRouteState { SampleTime = time, Center = new[] { sequence * .01f, 1.5f, sequence * .2f }, Alive = true, LocalOwner = true },
        } },
        Events = new[] { new ItemEvent { Sequence = sequence, T = time, Kind = "use", ActorId = "synthetic-player", ItemKey = "synthetic-item" } },
    };

    private static ReplayClip SaveClip(TestFiles files, RollingBuffer rolling, ReplayHeader header) =>
        ReplayFiles.Read(ReplayArchive.Save(files.Root, rolling.Snapshot(header, DateTime.UtcNow)));
    private static List<ReplayFrame> ReadFull(FullReplayResult result)
    {
        var info = FullReplayArchive.ReadInfo(result.FilePath);
        Check(info.Complete);
        var unique = new SortedDictionary<double, ReplayFrame>();
        for (int i = 0; i < info.Pages.Length; i++)
            foreach (var frame in FullReplayArchive.ReadPage(result.FilePath, info, i).Frames) unique.TryAdd(frame.T, frame);
        Check(unique.Count == info.FrameCount, "Full pages do not account for every accepted frame.");
        return unique.Values.ToList();
    }
    private static void AssertStoredFrame(ReplayFrame stored, ReplayFrame source, double origin)
    {
        Close(stored.T, source.T - origin);
        Close(stored.Actors[0].Position[2], source.Actors[0].Position[2]);
        for (int i = 0; i < source.Actors[0].JointPose.Length; i++)
        {
            Close(stored.Actors[0].JointPose[i].SampleTime, source.Actors[0].JointPose[i].SampleTime - origin);
            Close(stored.Actors[0].JointPose[i].Position[0], source.Actors[0].JointPose[i].Position[0], .0002);
        }
        Close(stored.Actors[0].RouteState!.SampleTime, source.Actors[0].RouteState!.SampleTime - origin);
        Check(stored.Events.Length == 1 && stored.Events[0].Sequence == source.Events[0].Sequence);
        Close(stored.Events[0].T, source.Events[0].T - origin);
    }
    private static FullReplayResult Done(FullReplayWriter writer)
    {
        var task = writer.CompleteAsync();
        Check(task.Wait(TimeSpan.FromSeconds(20)), "The full writer failed to complete.");
        return task.GetAwaiter().GetResult();
    }
    private static string Json(object value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static void Close(double actual, double expected, double tolerance = .00001) =>
        Check(Math.Abs(actual - expected) <= tolerance, $"Expected {expected}; actual {actual}.");
    private static void Check(bool value, string message = "Concurrent recording assertion failed.") { if (!value) throw new Exception(message); }

    private static string Source(string name)
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                string path = Path.Combine(dir.FullName, "src", "PeakReplayLab", name);
                if (File.Exists(path)) return File.ReadAllText(path);
            }
        throw new FileNotFoundException("Production source is required for the concurrent recording wiring check.", name);
    }
    private static string Method(string source, string signature)
    {
        int method = source.IndexOf(signature, StringComparison.Ordinal);
        Check(method >= 0, "Missing production method: " + signature);
        int open = source.IndexOf('{', method), depth = 1;
        Check(open >= 0);
        for (int i = open + 1; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
        }
        throw new Exception("Unclosed production method: " + signature);
    }
    private static Stream NewFile(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    private sealed class TestFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "peak-concurrent-test-" + Guid.NewGuid().ToString("N"));
        private readonly List<FullReplayWriter> writers = new();
        public TestFiles() => Directory.CreateDirectory(Root);
        public FullReplayWriter Writer(ReplayHeader header, ContinuousReplayOptions? options = null, Func<string, Stream>? open = null)
        {
            var writer = open == null ? new FullReplayWriter(Root, header, options) : new FullReplayWriter(Root, header, options, open);
            writers.Add(writer); return writer;
        }
        public void Dispose()
        {
            foreach (var writer in writers) Check(writer.CompleteAsync("test-cleanup").Wait(TimeSpan.FromSeconds(20)), "Test writer did not release its file.");
            Directory.Delete(Root, true);
        }
    }
    private sealed class FailedDisk : Stream
    {
        private readonly Stream inner;
        public FailedDisk(Stream inner) => this.inner = inner;
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Synthetic disk failure; no real game data involved.");
        public override void Flush() => inner.Flush();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
