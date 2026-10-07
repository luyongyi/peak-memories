using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class EnvironmentPerformanceTests
{
    public static void Run(Action<string, Action> test)
    {
        test("environment discovers a scene once then staggers one family per fallback", () =>
        {
            var schedule = new EnvironmentDiscoverySchedule();
            Check(schedule.Take(1, 0, EnvironmentDirectory.None) == EnvironmentDirectory.All);
            Check(schedule.Take(1, .1, EnvironmentDirectory.None) == EnvironmentDirectory.None);
            foreach (var pair in new[] { (2.5, EnvironmentDirectory.Winds), (5d, EnvironmentDirectory.RisingLava), (7.5, EnvironmentDirectory.MovingLava), (10d, EnvironmentDirectory.Fog) })
                Check(schedule.Take(1, pair.Item1, EnvironmentDirectory.None) == pair.Item2);
            Check(schedule.Take(2, 10.1, EnvironmentDirectory.None) == EnvironmentDirectory.All);
        });
        test("environment source invalidation is immediate without restarting the directory scan cycle", () =>
        {
            var schedule = new EnvironmentDiscoverySchedule(); schedule.Take(1, 0, EnvironmentDirectory.None);
            Check(schedule.Take(1, .02, EnvironmentDirectory.Fog) == EnvironmentDirectory.Fog);
            Check(schedule.Take(1, .04, EnvironmentDirectory.None) == EnvironmentDirectory.None);
            Check(schedule.Take(1, 2.5, EnvironmentDirectory.None) == EnvironmentDirectory.Winds);
        });
        test("environment directory clock rewind and stalls never trigger catch-up scan bursts", () =>
        {
            var schedule = new EnvironmentDiscoverySchedule(); schedule.Take(1, 100, EnvironmentDirectory.None);
            Check(schedule.Take(1, 1, EnvironmentDirectory.None) == EnvironmentDirectory.None);
            Check(schedule.Take(1, 3.5, EnvironmentDirectory.None) == EnvironmentDirectory.Winds);
            Check(schedule.Take(1, 1000, EnvironmentDirectory.None) == EnvironmentDirectory.RisingLava);
            Check(schedule.Take(1, 1000.01, EnvironmentDirectory.None) == EnvironmentDirectory.None);
        });
        test("environment numeric capture stays twenty Hz while a discrete event captures immediately", () =>
        {
            var clock = new EnvironmentSampleClock(); int count = 0;
            for (int i = 0; i <= 60; i++) if (clock.Take(i / 60d, false)) count++;
            Check(count == 21);
            clock.Reset(); Check(clock.Take(0, false)); Check(!clock.Take(.016, false));
            Check(clock.Take(.025, true)); Check(!clock.Take(.04, false)); Check(clock.Take(.075, false));
            Check(clock.Take(.02, false)); // Rewound capture clock establishes a fresh baseline.
        });
        test("unchanged environment snapshot arrays allocate nothing and changed slots preserve the baseline", () =>
        {
            var original = new[] { new EnvironmentWindFrame(), new EnvironmentWindFrame() };
            // Warm JIT before checking allocations.
            var warm = new EnvironmentSnapshotArray<EnvironmentWindFrame>(original, 2); warm.Set(0, original[0]); warm.Set(1, original[1]); warm.Build();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                var builder = new EnvironmentSnapshotArray<EnvironmentWindFrame>(original, 2); builder.Set(0, original[0]); builder.Set(1, original[1]);
                Check(ReferenceEquals(builder.Build(), original));
            }
            Check(GC.GetAllocatedBytesForCurrentThread() == before);
            var second = original[1]; var changed = new EnvironmentWindFrame(); var edit = new EnvironmentSnapshotArray<EnvironmentWindFrame>(original, 2);
            edit.Set(0, original[0]); edit.Set(1, changed); var result = edit.Build();
            Check(!ReferenceEquals(result, original) && ReferenceEquals(result[0], original[0]) && ReferenceEquals(original[1], second) && !ReferenceEquals(original[1], changed));
        });
        test("environment twenty Hz keyframes interpolate across held sixty Hz frames", () =>
        {
            var first = Wind(0); var next = Wind(.05f);
            var index = new EnvironmentReplayTimeline(new[] { Frame(0, first), Frame(.016, first), Frame(.033, first), Frame(.05, next) }, true);
            Check(index.KeyCount == 2); Check(index.TrySample(.025, out var sample));
            Close(sample.LeftTime, 0); Close(sample.RightTime, .05); Close(sample.Mix, .5); Close(WindElapsed(sample), .025);
            using var memory = new MemoryReplayTimeline(Clip(Frame(0, first), Frame(.016, first), Frame(.033, first), Frame(.05, next)));
            Check(memory.TrySample(.025, out var timelineSample)); Close(WindElapsed(timelineSample.Environment), .025);
        });
        test("environment wind clocks at a gust boundary do not sweep direction or activate early", () =>
        {
            var first = Wind(0); first.Winds[0].Active = false;
            var gust = Wind(.01f); gust.SampleTime = .025; gust.Winds[0].Direction = new[] { -1f, 0, 0 };
            var later = Wind(.06f, -1); later.SampleTime = .075;
            var index = new EnvironmentReplayTimeline(new[] { Frame(0, first), Frame(.016, first), Frame(.025, gust), Frame(.075, later) }, true);
            Check(index.TrySample(.024, out var before)); Check(!before.Left!.Winds[0].Active && before.Mix == 0);
            Check(EnvironmentReplayMath.Mix(EnvironmentReplayMath.SameWindEvent(before.Left.Winds[0], before.Right!.Winds[0]), before.Mix) == 0);
            Check(index.TrySample(.025, out var exact)); Check(exact.Left!.Winds[0].Active && exact.Left.Winds[0].Direction[0] == -1);
        });
        test("environment unknown transitions segment cuts and capture gaps remain discrete", () =>
        {
            var a = Wind(0); var b = Wind(.1f);
            foreach (var frames in new[] { new[] { Frame(0, a), Frame(.1, null) }, new[] { Frame(0, null), Frame(.1, b) },
                new[] { Frame(0, a), Frame(.1, b, 1) }, new[] { Frame(0, a), Frame(1, b) } })
            {
                var index = new EnvironmentReplayTimeline(frames, true);
                Check(index.TrySample((frames[1].T - frames[0].T) * .5, out var mid)); Check(mid.Mix == 0 && ReferenceEquals(mid.Left, frames[0].World.Environment));
                Check(index.TrySample(frames[1].T, out var exact)); Check(ReferenceEquals(exact.Left, frames[1].World.Environment));
            }
        });
        test("environment active missing future anchors buffer while inactive unknown and true endpoints hold", () =>
        {
            var active = Wind(0); var inactive = new EnvironmentReplayFrame();
            Check(!new EnvironmentReplayTimeline(new[] { Frame(0, active), Frame(.033, active) }, false).TrySample(.025, out _));
            var stationary = Wind(0); stationary.SampleTime = .05;
            Check(new EnvironmentReplayTimeline(new[] { Frame(0, active), Frame(.05, stationary), Frame(.1, stationary) }, false).TrySample(.025, out _));
            Check(new EnvironmentReplayTimeline(new[] { Frame(0, active), Frame(.033, active) }, true).TrySample(.025, out _));
            Check(new EnvironmentReplayTimeline(new[] { Frame(0, inactive), Frame(.033, inactive) }, false).TrySample(.025, out _));
            Check(new EnvironmentReplayTimeline(new[] { Frame(0, null), Frame(.033, null) }, false).TrySample(.025, out var old) && old.Left == null);
        });
        test("environment independent full page baselines merge without resetting observation clocks", () =>
        {
            var index = new EnvironmentReplayTimeline(new[] { Frame(0, Wind(0)), Frame(.016, Wind(0)), Frame(.033, Wind(0)), Frame(.05, Wind(.05f)) }, true);
            Check(index.KeyCount == 2); Check(index.TrySample(.025, out var sample)); Close(WindElapsed(sample), .025);
        });
        test("environment lava and fog movement interpolate at twenty Hz while completion remains discrete", () =>
        {
            EnvironmentReplayFrame State(float progress) => new()
            {
                SampleTimeKnown = true, SampleTime = progress,
                Lava = new[] { new EnvironmentLavaFrame { Key = "native:lava", Active = true, Started = true, Position = new[] { progress, 0, 0 }, ProgressTime = progress } },
                Fog = new EnvironmentFogFrame { Key = "native:fog", Active = true, Moving = true, Point = new[] { progress, 0, 0 }, Size = 10, Enable = 1 },
            };
            var a = State(0); var b = State(.05f); var done = State(.07f);
            done.Lava[0].Ended = true; done.Lava[0].Active = false; done.Fog!.Moving = false; done.Fog.Arrived = true;
            var index = new EnvironmentReplayTimeline(new[] { Frame(0, a), Frame(.016, a), Frame(.05, b), Frame(.07, done) }, true);
            Check(index.TrySample(.025, out var mid));
            Close(EnvironmentReplayMath.Lerp(mid.Left!.Lava[0].Position[0], mid.Right!.Lava[0].Position[0], mid.Mix), .025);
            Close(EnvironmentReplayMath.Lerp(mid.Left.Fog!.Point[0], mid.Right.Fog!.Point[0], mid.Mix), .025);
            Check(index.TrySample(.069, out var before)); Check(before.Mix == 0 && before.Left!.Lava[0].Active && before.Left.Fog!.Moving);
            Check(index.TrySample(.07, out var exact)); Check(!exact.Left!.Lava[0].Active && exact.Left.Fog!.Arrived);
        });
        test("environment back seek and reverse sampling use identical immutable time anchors", () =>
        {
            var first = Wind(0); var index = new EnvironmentReplayTimeline(new[] { Frame(0, first), Frame(.033, first), Frame(.05, Wind(.05f)), Frame(.1, Wind(.1f)) }, true);
            Check(index.TrySample(.025, out var a)); Check(index.TrySample(.09, out _)); Check(index.TrySample(.025, out var b));
            Close(WindElapsed(a), WindElapsed(b)); Close(a.LeftTime, b.LeftTime); Close(a.RightTime, b.RightTime);
        });
        test("environment held anchors survive the production schema thirteen delta codec", () =>
        {
            var first = Wind(0); var writer = new ReplayDeltaCodec.Writer(); var reader = new ReplayDeltaCodec.Reader();
            var decoded = new[] { Frame(0, first), Frame(.016, first), Frame(.033, first), Frame(.05, Wind(.05f)) }
                .Select(frame => reader.Decode(JObject.Parse(JsonConvert.SerializeObject(writer.Encode(frame), ReplayFiles.Json)))).ToArray();
            Check(ReferenceEquals(decoded[0].World.Environment, decoded[2].World.Environment));
            var index = new EnvironmentReplayTimeline(decoded, true); Check(index.TrySample(.025, out var sample)); Close(WindElapsed(sample), .025);
        });
        test("environment stationary real observations prevent motion from starting before its sampling interval", () =>
        {
            var initial = new EnvironmentReplayFrame { SampleTimeKnown = true, SampleTime = 0 };
            var stationary = new EnvironmentReplayFrame { SampleTimeKnown = true, SampleTime = .05 };
            var moving = new EnvironmentReplayFrame { SampleTimeKnown = true, SampleTime = .1, WeatherBlend = 1 };
            var writer = new ReplayDeltaCodec.Writer(); var reader = new ReplayDeltaCodec.Reader();
            var decoded = new[] { Frame(0, initial), Frame(.033, initial), Frame(.05, stationary), Frame(.083, stationary), Frame(.1, moving) }
                .Select(frame => reader.Decode(JObject.Parse(JsonConvert.SerializeObject(writer.Encode(frame), ReplayFiles.Json)))).ToArray();
            var index = new EnvironmentReplayTimeline(decoded, true); Check(index.KeyCount == 3);
            Check(index.TrySample(.025, out var first)); Close(first.Left!.WeatherBlend, 0); Close(first.Right!.WeatherBlend, 0);
            Check(index.TrySample(.075, out var later)); Close(later.LeftTime, .05); Close(later.RightTime, .1);
            Close(EnvironmentReplayMath.Lerp(later.Left!.WeatherBlend, later.Right!.WeatherBlend, later.Mix), .5);
            var unavailable = new EnvironmentReplayTimeline(new[] { Frame(0, initial), Frame(.033, initial) }, false);
            Check(!unavailable.TrySample(.025, out _)); // A stationary endpoint cannot prove a missing future interval.
        });
        test("environment legacy schema twelve keeps original main-frame interpolation without inventing anchors", () =>
        {
            var a = Wind(0); a.SampleTimeKnown = false;
            var b = Wind(.1f); b.SampleTimeKnown = false;
            var writer = new ReplayDeltaCodec.Writer(12); var reader = new ReplayDeltaCodec.Reader(12);
            var source = new[] { Frame(0, a), Frame(.033, a), Frame(.066, a), Frame(.1, b) };
            var decoded = source.Select(frame => reader.Decode(JObject.Parse(JsonConvert.SerializeObject(writer.Encode(frame), ReplayFiles.Json)))).ToArray();
            Check(decoded.All(frame => !frame.World.Environment!.SampleTimeKnown));
            var index = new EnvironmentReplayTimeline(decoded, true); Check(index.KeyCount == decoded.Length);
            Check(index.TrySample(.05, out var before)); Close(WindElapsed(before), 0);
            Check(index.TrySample(.083, out var after)); Close(WindElapsed(after), .05);
            bool rejected = false;
            try { new ReplayDeltaCodec.Writer(12).Encode(Frame(0, Wind(0))); } catch (InvalidDataException) { rejected = true; }
            Check(rejected); // A clock cannot be silently discarded into the older schema.
        });
        test("environment clock translation preserves shared arrays and a valid negative opening baseline", () =>
        {
            var state = Wind(0); state.SampleTime = 49.96;
            var first = Frame(50, state); var next = Frame(50.016, state);
            var rebaser = new ContinuousReplayRebaser(50); var a = rebaser.Apply(first); var b = rebaser.Apply(next);
            Close(a.World.Environment!.SampleTime, -.04); Close(state.SampleTime, 49.96);
            Check(ReferenceEquals(a.World.Environment, b.World.Environment));
            Check(ReferenceEquals(a.World.Environment.Winds, state.Winds) && ReferenceEquals(a.World.ActiveMapObjects, first.World.ActiveMapObjects));
            ReplayRules.Validate(a, -1); ReplayRules.Validate(b, a.T);
            var buffer = new RollingBuffer(); buffer.Add(first); buffer.Add(next);
            var clip = buffer.Snapshot(new ReplayHeader(), DateTime.UtcNow);
            Close(clip.Frames[0].World.Environment!.SampleTime, -.04); Check(ReferenceEquals(clip.Frames[0].World.Environment, clip.Frames[1].World.Environment));
            var nextState = Wind(.06f); nextState.SampleTime = 50.06;
            var index = new EnvironmentReplayTimeline(new[] { a, b, rebaser.Apply(Frame(50.06, nextState)) }, true);
            Check(index.TrySample(.01, out var mid)); Close(mid.LeftTime, -.04); Close(mid.RightTime, .06);
        });
        test("environment decoded budget preserves legacy page index estimates and charges known clocks", () =>
        {
            var legacy = new EnvironmentReplayFrame();
            var known = new EnvironmentReplayFrame { SampleTimeKnown = true };
            Check(EnvironmentReplayRules.Estimate(legacy) == 120 + 48);
            Check(EnvironmentReplayRules.Estimate(known) == 136 + 48);
            legacy.Winds = known.Winds = Wind(0).Winds;
            Check(EnvironmentReplayRules.Estimate(known) - EnvironmentReplayRules.Estimate(legacy) == 16);
        });
        test("environment future nonfinite conflicting or decreasing observation clocks are rejected", () =>
        {
            foreach (double time in new[] { .1, double.NaN, double.PositiveInfinity, -2 })
            {
                var state = Wind(0); state.SampleTime = time; bool rejected = false;
                try { ReplayRules.Validate(Frame(0, state), -1); } catch (InvalidDataException) { rejected = true; }
                Check(rejected);
            }
            var a = Wind(0); var changed = Wind(.02f); changed.SampleTime = 0;
            foreach (var clock in new[] { 0d, -.01 })
            {
                changed.SampleTime = clock; bool rejected = false;
                try { new EnvironmentReplayTimeline(new[] { Frame(0, a), Frame(.02, changed) }, true); } catch (InvalidDataException) { rejected = true; }
                Check(rejected);
            }
        });
        test("environment clocks and stationary observations survive full-file paging and re-encoding", () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "peak-env-clock-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                EnvironmentReplayFrame State(double time, float weather) => new() { SampleTimeKnown = true, SampleTime = time, WeatherBlend = weather };
                var first = State(49.96, 0); var stationary = State(50.05, 0); var moving = State(50.1, 1);
                var frames = new[] { Frame(50, first), Frame(50.016, first), Frame(50.05, stationary), Frame(50.066, stationary), Frame(50.1, moving), Frame(50.116, moving) };
                var header = new ReplayHeader { Scene = "Level_Synthetic", GameVersion = "test", BuildId = 123, Route = "Shore,Roots,Alpine,Caldera,Kiln" };
                var writer = new FullReplayWriter(directory, header, new ContinuousReplayOptions { SegmentFrameLimit = 2 });
                foreach (var frame in frames) Check(writer.TryEnqueue(frame));
                var completion = writer.CompleteAsync(); Check(completion.Wait(TimeSpan.FromSeconds(10)));
                var result = completion.GetAwaiter().GetResult(); Check(result.Status == "completed" && result.WrittenFrames == frames.Length);
                var info = FullReplayArchive.ReadInfo(result.FilePath); Check(info.Header.Schema == ReplayRules.CurrentSchema && info.Pages.Length > 1);
                var decoded = new List<ReplayFrame>();
                for (int page = 0; page < info.Pages.Length; page++) decoded.AddRange(FullReplayArchive.ReadPage(result.FilePath, info, page).Frames);
                Close(decoded[0].World.Environment!.SampleTime, -.04);
                var codec = new ReplayDeltaCodec.Writer(); var reader = new ReplayDeltaCodec.Reader();
                var reencoded = decoded.GroupBy(frame => frame.T).Select(group => group.First()).Select(frame =>
                    reader.Decode(JObject.Parse(JsonConvert.SerializeObject(codec.Encode(frame), ReplayFiles.Json)))).ToArray();
                var index = new EnvironmentReplayTimeline(reencoded, true);
                Check(index.TrySample(.025, out var early)); Close(early.Left!.WeatherBlend, 0); Close(early.Right!.WeatherBlend, 0);
                Check(index.TrySample(.075, out var later)); Close(later.LeftTime, .05); Close(later.RightTime, .1);
                Close(EnvironmentReplayMath.Lerp(later.Left!.WeatherBlend, later.Right!.WeatherBlend, later.Mix), .5);
                using var timeline = new PagedReplayTimeline(info.Header, info.Duration, info.FrameCount, info.ActorIds,
                    info.Pages.Select(page => new ReplayPageRange(page.Start, page.End)).ToArray(), (page, token) => FullReplayArchive.ReadPage(result.FilePath, info, page, token));
                var sample = Ready(timeline, .075).Environment;
                Close(EnvironmentReplayMath.Lerp(sample.Left!.WeatherBlend, sample.Right!.WeatherBlend, sample.Mix), .5);
                Close(Ready(timeline, .025).Environment.Left!.WeatherBlend, 0);
            }
            finally
            {
                string resolved = Path.GetFullPath(directory);
                string prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "peak-env-clock-";
                if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected environment test directory.");
                if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
            }
        });
        test("environment cross-page interpolation waits for a real future observation and supports back seek", () =>
        {
            using var release = new ManualResetEventSlim(); var held = Wind(0);
            using var timeline = new PagedReplayTimeline(new ReplayHeader(), .1, 7, Array.Empty<string>(),
                new[] { new ReplayPageRange(0, .033), new ReplayPageRange(.033, .1) }, (index, token) =>
                {
                    if (index == 1) release.Wait(token);
                    return index == 0 ? Clip(Frame(0, held), Frame(.016, held), Frame(.033, held)) : Clip(Frame(.033, Wind(0)), Frame(.05, Wind(.05f)), Frame(.1, Wind(.1f)));
                });
            var watch = Stopwatch.StartNew();
            while (timeline.CachedPageCount == 0 && watch.ElapsedMilliseconds < 3000) { Check(!timeline.TrySample(.025, out _)); Thread.Sleep(1); }
            Check(!timeline.TrySample(.025, out _)); release.Set();
            Close(WindElapsed(Ready(timeline, .025).Environment), .025);
            Close(WindElapsed(Ready(timeline, .075).Environment), .075);
            Close(WindElapsed(Ready(timeline, .025).Environment), .025);
        });
        test("environment tiny pages preserve prior twenty Hz anchors instead of treating held baselines as new samples", () =>
        {
            var ranges = Enumerable.Range(0, 10).Select(i => new ReplayPageRange(i / 60d, i / 60d)).ToArray();
            using var timeline = new PagedReplayTimeline(new ReplayHeader(), .15, 10, Array.Empty<string>(), ranges,
                (index, token) => Clip(Frame(index / 60d, Wind((float)(index / 3 * .05)))));
            Close(WindElapsed(Ready(timeline, .041).Environment), .041);
            Close(WindElapsed(Ready(timeline, .12).Environment), .12);
            Close(WindElapsed(Ready(timeline, .041).Environment), .041);
            Check(timeline.CachedPageCount <= 64 && timeline.CachedDecodedBytes <= PagedReplayTimeline.MaximumCachedDecodedBytes);
        });
    }

    private static EnvironmentReplayFrame Wind(float elapsed, float direction = 1) => new()
    {
        SampleTimeKnown = true, SampleTime = elapsed,
        Winds = new[] { new EnvironmentWindFrame { Key = "native:wind", Enabled = true, Active = true, Direction = new[] { direction, 0, 0 },
            Intensity = elapsed, ActiveFor = elapsed, Duration = 10, SecondsUntilSwitch = 10 - elapsed } },
    };
    private static ReplayFrame Frame(double time, EnvironmentReplayFrame? state, int segment = 0) => new() { T = time, World = new WorldFrame { Segment = segment, Environment = state } };
    private static ReplayClip Clip(params ReplayFrame[] frames) { var clip = new ReplayClip { Complete = true }; clip.Frames.AddRange(frames); return clip; }
    private static double WindElapsed(EnvironmentReplayBracket sample) => EnvironmentReplayMath.Lerp(sample.Left!.Winds[0].ActiveFor, sample.Right!.Winds[0].ActiveFor, sample.Mix);
    private static ReplayTimelineSample Ready(PagedReplayTimeline timeline, double time)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 5000) { if (timeline.TrySample(time, out var sample)) return sample; Thread.Sleep(1); }
        throw new Exception("Timed out waiting for real environment anchors.");
    }
    private static void Close(double actual, double expected) => Check(Math.Abs(actual - expected) < .00001, $"Expected {expected}, actual {actual}.");
    private static void Check(bool value, string message = "Environment performance contract failed.") { if (!value) throw new Exception(message); }
}
