using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

// Sleep changes sampling, not object lifetime. These fixtures deliberately hold
// the same complete native checkpoint for long idle spans and keep root/child
// visibility distinct, as Mob.UpdateSleeping does in the game.
internal static class CreatureDormancyTests
{
    public static void Run(Action<string, Action> test)
    {
        RunLifecycle(test);
        test("sleeping beetle checkpoint remains present with hidden native visuals across unchanged frames", () =>
        {
            var sleeping = Beetle(sleeping: true, revision: 0);
            var awake = Beetle(sleeping: false, revision: 1);
            var finalSleep = Beetle(sleeping: true, revision: 2);
            var frames = new[] { Frame(0, sleeping), Frame(1, sleeping), Frame(2, sleeping),
                Frame(2.001, awake), Frame(2.04, awake), Frame(3, finalSleep), Frame(8, finalSleep) };
            foreach (int schema in Enumerable.Range(15, ReplayRules.CurrentSchema - 14))
            {
                var writer = new ReplayDeltaCodec.Writer(schema);
                var tree = new ReplayDeltaCodec.Reader(schema);
                var stream = new ReplayDeltaCodec.StreamReader(schema);
                var prior = new List<(ReplayFrame Frame, string Json)>();
                foreach (var frame in frames)
                {
                    string record = Json(writer.Encode(frame)); var token = JObject.Parse(record);
                    Check(token["RemovedCreatures"]?.HasValues != true, "Sleep removed the native beetle lifetime.");
                    var a = tree.Decode(token); var b = stream.DecodeRecord(record)!;
                    Check(a.Creatures.Length == 1 && b.Creatures.Length == 1);
                    Check(Json(a.Creatures) == Json(frame.Creatures) && Json(b.Creatures) == Json(frame.Creatures));
                    Check(b.Creatures[0].Pose.Active, "Native mob root remains active while its visuals sleep.");
                    ReplayRules.ValidateVersion(b, schema);
                    prior.Add((b, Json(b)));
                }
                foreach (var saved in prior) Check(Json(saved.Frame) == saved.Json, "A wake mutated an earlier sleeping checkpoint.");
            }
        });
        test("idle spider cached silk and same-state bonk motion survive legacy and current delta decoders", () =>
        {
            var idle = Spider(0, 0);
            var bonk = Spider(0, 1); // Bonk changes native bones while SpiderState stays Idle.
            var dropped = Spider(1, 2); var grabbing = Spider(2, 3); var letGo = Spider(3, 4); var returned = Spider(0, 5);
            var frames = new[] { Frame(0, idle), Frame(2, idle), Frame(2.001, bonk), Frame(2.04, bonk),
                Frame(3, dropped), Frame(3.001, grabbing), Frame(4, letGo), Frame(7, returned), Frame(12, returned) };
            foreach (int schema in Enumerable.Range(15, ReplayRules.CurrentSchema - 14))
            {
                var writer = new ReplayDeltaCodec.Writer(schema); var tree = new ReplayDeltaCodec.Reader(schema);
                var stream = new ReplayDeltaCodec.StreamReader(schema);
                foreach (var frame in frames)
                {
                    string record = Json(writer.Encode(frame));
                    var a = tree.Decode(JObject.Parse(record)); var b = stream.DecodeRecord(record)!;
                    Check(Json(a.Creatures) == Json(frame.Creatures) && Json(b.Creatures) == Json(frame.Creatures));
                    var spider = b.Creatures.Single();
                    Check(spider.Line != null && spider.Line.Points.Length == 40 * 3, "Idle checkpoint lost native silk.");
                    ReplayRules.ValidateVersion(b, schema);
                }
            }
            Check(idle.Pose.Nodes[0].Position[1] == 0 && idle.Line!.Points[119] == 0);
        });
        test("sleeping and culled creatures retain complete page baselines for independent reverse loads", () => InRoot(root =>
        {
            var frames = LifecycleFrames(); string path = Save(root, frames); var info = FullReplayArchive.ReadInfo(path);
            Check(info.Pages.Length >= 4 && info.SharedBlocks!.Any(block => block.Kind == 3), "Fixture did not create shared independent creature baselines.");
            var expected = frames.ToDictionary(frame => frame.T);
            foreach (int pageIndex in Enumerable.Range(0, info.Pages.Length).Reverse().Concat(new[] { 0, info.Pages.Length - 1, 1 }))
                foreach (var frame in FullReplayArchive.ReadPage(path, info, pageIndex).Frames)
                {
                    Check(Json(frame.Creatures) == Json(expected[frame.T].Creatures));
                    Check(frame.Creatures.Length == 2, "Sampling dormancy was decoded as disappearance.");
                }
        }));
        test("paged seeks around wake drop final sleep and culling restore held bones silk and visibility", () => InRoot(root =>
        {
            var frames = LifecycleFrames(); string path = Save(root, frames); var info = FullReplayArchive.ReadInfo(path);
            using var timeline = new PagedReplayTimeline(info.Header, info.Duration, info.FrameCount, info.ActorIds,
                info.Pages.Select(page => new ReplayPageRange(page.Start, page.End)).ToArray(),
                FullReplayArchive.CreatePageLoader(path, info), complete: info.Complete,
                decodedPageBytes: info.Pages.Select(page => page.DecodedBytes).ToArray());
            foreach (double target in new[] { 8d, 0, 1.999, 2, 2.025, 3, 5, 7, 2.05, 8, 1, 5.5, 6 })
            {
                var sample = Ready(timeline, target); var expected = frames.Last(frame => frame.T <= target);
                Check(Json(sample.Left.Creatures) == Json(expected.Creatures), "Seek inherited another activity phase at " + target);
                var beetle = sample.Left.Creatures[0]; var spider = sample.Left.Creatures[1];
                Check(beetle.Pose.Active && beetle.Pose.Nodes.Length == 2);
                Check(spider.Line!.Points.Length == 120 && spider.Pose.Nodes.Length == 2);
                if (target < 2 || target >= 5) Check(!beetle.Pose.Nodes[0].Active && !beetle.Pose.Nodes[0].Visible);
                if (target >= 6 && target < 7) Check(!spider.Pose.Active && !spider.Line.Enabled);
            }
        }));
    }
    private static void RunLifecycle(Action<string, Action> test)
    {
        test("sleeping native beetle takes one initial complete state and then skips all tree reads", () =>
        {
            var policy = new CreatureCaptureLifecycle(spider: false, beetle: true); int reads = 0;
            for (int i = 0; i < 3600; i++) if (policy.Due(i / 60d, true, true, 0, false)) reads++;
            Check(reads == 1, "A sleeping beetle kept sampling its hidden tree: " + reads);
            Check(policy.ClockMode == 2, "Sleeping beetle did not use held-clock mode.");
        });
        test("beetle wake and final sleep bypass the previous native sample deadline immediately", () =>
        {
            var policy = new CreatureCaptureLifecycle(spider: false, beetle: true);
            Check(policy.Due(0, true, true, 0, false));
            Check(!policy.Due(.001, true, true, 0, false));
            Check(policy.Due(.002, true, false, 0, false), "Wake waited for a later timer tick.");
            Check(policy.ClockMode == 0 && !policy.Due(.003, true, false, 0, false));
            Check(policy.Due(.04, true, false, 0, false), "Awake locomotion lost its active cadence.");
            Check(policy.Due(.041, true, true, 0, false), "Final sleep did not record hidden native visual children.");
            Check(!policy.Due(.042, true, true, 0, false) && !policy.Due(5, true, true, 0, false));
        });
        test("native culling and reactivation each preserve one immediate complete creature checkpoint", () =>
        {
            var policy = new CreatureCaptureLifecycle(spider: true, beetle: false);
            Check(policy.Due(0, false, false, 0, false));
            Check(!policy.Due(5, false, false, 0, false), "Inactive creature was periodically sampled.");
            Check(policy.Due(5.001, true, false, 0, false));
            Check(policy.Due(5.002, false, false, 0, false));
            Check(!policy.Due(5.003, false, false, 0, false));
            Check(policy.Due(5.004, true, false, 1, false));
        });
        test("web spider idle sleeps after its finite native setup while drop grab and release wake immediately", () =>
        {
            var policy = new CreatureCaptureLifecycle(spider: true, beetle: false); int idleReads = 0;
            for (int i = 0; i < 3600; i++) if (policy.Due(i / 60d, true, false, 0, false)) idleReads++;
            Check(idleReads >= 1 && idleReads <= 12, "Idle spider continued reading its tree after the setup burst: " + idleReads);
            Check(policy.ClockMode == 1);
            Check(policy.Due(60.001, true, false, 1, false), "Dropped edge was throttled by the idle timer.");
            Check(policy.ClockMode == 0);
            Check(policy.Due(60.002, true, false, 2, false), "Grabbing edge was throttled by the active timer.");
            Check(policy.Due(60.003, true, false, 3, false), "LetGo edge was throttled by the active timer.");
            Check(policy.Due(60.004, true, false, 0, false), "Final returned Idle pose was lost.");
        });
        test("same-state native spider events are one immediate wake and native stun remains active until its final edge", () =>
        {
            var policy = new CreatureCaptureLifecycle(spider: true, beetle: false);
            Check(policy.Due(0, true, false, 0, false));
            Check(policy.Due(.25, true, false, 0, false), "Initial native setup did not save its final Idle checkpoint.");
            Check(!policy.Due(1, true, false, 0, false), "Idle spider resumed polling without an event.");
            Check(!policy.Due(1.001, true, false, 0, false));
            policy.NotifyNativeEvent();
            Check(policy.Due(1.002, true, false, 0, false), "Idle Bonk event was hidden behind an idle deadline.");
            Check(!policy.Due(1.003, true, false, 0, false), "One event became unbounded per-frame sampling.");
            Check(policy.Due(1.004, true, false, 0, true));
            Check(policy.ClockMode == 0 && policy.Due(1.04, true, false, 0, true));
            Check(policy.Due(6.041, true, false, 0, false), "Stun finished without a final native pose.");
            Check(policy.ClockMode == 1);
        });
        test("active native creatures keep 30 Hz observations without spider-only dormancy heuristics", () =>
        {
            foreach (var policy in new[] { new CreatureCaptureLifecycle(false, false), new CreatureCaptureLifecycle(false, true), new CreatureCaptureLifecycle(true, false) })
            {
                int reads = 0;
                for (int i = 0; i < 3600; i++) if (policy.Due(i / 60d, true, false, 2, false)) reads++;
                Check(reads >= 1750 && reads <= 1801, "True active creature cadence changed: " + reads);
                Check(policy.ClockMode == 0);
            }
        });
    }
    private static ReplayFrame[] LifecycleFrames()
    {
        var sleep = Beetle(true, 0); var wake = Beetle(false, 1); var moved = Beetle(false, 2); var finalSleep = Beetle(true, 3);
        var idle = Spider(0, 0); var dropped = Spider(1, 1); var grabbing = Spider(2, 2); var release = Spider(3, 3); var returned = Spider(0, 4);
        var culled = Spider(0, 4, active: false);
        return new[] { Frame(0, sleep, idle), Frame(1, sleep, idle), Frame(2, wake, dropped), Frame(2.05, moved, grabbing),
            Frame(3, moved, release), Frame(5, finalSleep, returned), Frame(6, finalSleep, culled),
            Frame(7, finalSleep, returned), Frame(8, finalSleep, returned) };
    }
    private static CreatureReplayFrame Beetle(bool sleeping, int revision) => new()
    {
        Key = "native-beetle-held-checkpoint", Kind = "beetle", Resource = "beetle", State = 0,
        Pose = new ObjectPose { Position = new[] { revision * 2f, 0f, 0f }, Active = true,
            Nodes = Nodes(revision, !sleeping) },
        Visuals = new[] { new NativeRendererFrame { Path = "0:Visuals", Mesh = "native-beetle-mesh" } },
    };
    private static CreatureReplayFrame Spider(int state, int revision, bool active = true) => new()
    {
        Key = "native-spider-held-checkpoint", Kind = "spider", Resource = "spider-web", State = state,
        Pose = new ObjectPose { Active = active, Nodes = Nodes(revision, true) },
        Line = new CreatureReplayLine { Enabled = active, WorldSpace = true, Width = .02f,
            Points = Enumerable.Repeat((float)revision, 120).ToArray() },
        Visuals = new[] { new NativeRendererFrame { Path = "0:Visuals", Mesh = "native-spider-mesh" } },
    };
    private static NodePose[] Nodes(int revision, bool visible) => new[]
    {
        new NodePose { Path = "0:Visuals", Position = new[] { 0f, (float)revision, 0f }, Active = visible, Visible = visible },
        new NodePose { Path = "0:Visuals/0:Bone", Rotation = new[] { 0f, 0f, 0f, 1f }, Active = true, Visible = visible },
    };
    private static ReplayFrame Frame(double time, params CreatureReplayFrame[] creatures) => new()
    { T = time, Actors = new[] { new ActorFrame { Id = "synthetic-dormancy-actor" } }, Creatures = creatures };
    private static string Save(string root, ReplayFrame[] frames)
    {
        var writer = new FullReplayWriter(root, new ReplayHeader { Scene = "SyntheticCreatureDormancy", Participants = new[] { "Synthetic" } },
            new ContinuousReplayOptions { SegmentFrameLimit = 2 });
        foreach (var frame in frames) Check(writer.TryEnqueue(frame));
        var done = writer.CompleteAsync(); Check(done.Wait(TimeSpan.FromSeconds(10)), "Full writer did not finish.");
        var result = done.GetAwaiter().GetResult(); Check(result.Status == "completed", result.Status); return result.FilePath;
    }
    private static ReplayTimelineSample Ready(IReplayTimeline timeline, double time)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        { if (timeline.TrySample(time, out var sample)) return sample; Thread.Sleep(1); }
        throw new Exception("Dormancy seek timed out at " + time);
    }
    private static void InRoot(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "peak-creature-dormancy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, true); }
    }
    private static string Json(object value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static void Check(bool condition, string message = "")
    { if (!condition) throw new Exception("Creature dormancy persistence contract failed: " + message); }
}
