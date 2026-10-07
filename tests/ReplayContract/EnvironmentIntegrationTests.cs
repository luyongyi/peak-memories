using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class EnvironmentIntegrationTests
{
    public static void Run(Action<string, Action> test)
    {
        test("complete recording independent page baselines preserve environment creatures pitons and native lights", () =>
        {
            string temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            string root = System.IO.Path.Combine(temp, "peak-environment-integration-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(root);
            try
            {
                var frames = Enumerable.Range(0, 21).Select(i => new ReplayFrame
                {
                    T = 400 + i * .25, World = new() { Environment = Environment(i % 3) },
                    Creatures = new[] { new CreatureReplayFrame { Key = "trap", Kind = "spore-trap", Resource = "spore-trap", State = i % 2, Pose = new() { Active = i % 2 == 0 } } },
                    Items = new[] { new ItemFrame { Key = "lantern", Lights = i % 3 == 0 ? null : i % 3 == 1 ? Array.Empty<NativeLightFrame>() : new[] { new NativeLightFrame { Intensity = i, Enabled = i % 2 == 0 } } } },
                    Spawned = new[] { new SpawnedReplayFrame { Key = "piton", Kind = "piton", Resource = "0_items/climbingspikehammered", Pose = new() { Position = new[] { (float)i, 0f, 0f } }, Lights = Array.Empty<NativeLightFrame>() } },
                }).ToArray();
                var writer = new FullReplayWriter(root, new ReplayHeader { Scene = "Test" }, new ContinuousReplayOptions { SegmentSeconds = 1 });
                foreach (var frame in frames) Check(writer.TryEnqueue(frame));
                var result = writer.CompleteAsync().GetAwaiter().GetResult(); Check(result.Status == "completed");
                var info = FullReplayArchive.ReadInfo(result.FilePath); Check(info.Pages.Length >= 4);
                foreach (int index in Enumerable.Range(0, info.Pages.Length).Reverse())
                    foreach (var frame in FullReplayArchive.ReadPage(result.FilePath, info, index, default).Frames)
                    {
                        int sample = (int)Math.Round(frame.T * 4);
                        Check(Json(frame.World.Environment) == Json(frames[sample].World.Environment));
                        Check(Json(frame.Creatures) == Json(frames[sample].Creatures));
                        Check(Json(frame.Items) == Json(frames[sample].Items));
                        Check(Json(frame.Spawned) == Json(frames[sample].Spawned));
                    }
            }
            finally
            {
                string target = System.IO.Path.GetFullPath(root);
                if (!target.StartsWith(System.IO.Path.Combine(temp, "peak-environment-integration-"), StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Unsafe test cleanup target.");
                System.IO.Directory.Delete(target, true);
            }
        });
        test("schema 10 and 11 retain unknown environment creatures and wraps", () =>
        {
            foreach (int schema in new[] { 10, 11 })
            {
                var source = new ReplayFrame { Actors = new[] { new ActorFrame { Id = "actor" } } };
                var token = Token(new ReplayDeltaCodec.Writer(schema).Encode(source));
                Check(token["World"]!["Environment"] == null && token["Creatures"] == null && token["Actors"]![0]!["WebWrap"] == null);
                var read = new ReplayDeltaCodec.Reader(schema).Decode(token);
                Check(read.World.Environment == null && read.Creatures.Length == 0 && read.Actors[0].WebWrap == null);
                Check(ReplayRules.SupportedSchema(schema));
            }
        });
        test("current environment delta restores gust direction timers tide and fog after unknown gaps", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(); var reader = new ReplayDeltaCodec.Reader();
            foreach (var source in new[] { Frame(0, Environment(0)), Frame(.1, Environment(1)), Frame(.2, null), Frame(.3, Environment(2)) })
            {
                var result = reader.Decode(Token(writer.Encode(source)));
                Check(Json(source.World.Environment) == Json(result.World.Environment));
                ReplayRules.Validate(result, source.T - 1);
            }
        });
        test("current baseline requires explicit environment and creature tracks", () =>
        {
            var writer = new ReplayDeltaCodec.Writer();
            var token = Token(writer.Encode(Frame(0, null)));
            var missingWorld = (JObject)token.DeepClone(); ((JObject)missingWorld["World"]!).Remove("Environment");
            Reject(() => new ReplayDeltaCodec.Reader().Decode(missingWorld));
            token.Remove("Creatures"); Reject(() => new ReplayDeltaCodec.Reader().Decode(token));
        });
        test("historical codec rejects new recorded environment instead of silently dropping it", () =>
        {
            foreach (int schema in new[] { 10, 11 })
            {
                Reject(() => new ReplayDeltaCodec.Writer(schema).Encode(Frame(0, Environment(0))));
                var token = Token(new ReplayDeltaCodec.Writer(schema).Encode(Frame(0, null)));
                ((JObject)token["World"]!).Add("Environment", JObject.FromObject(Environment(0)));
                Reject(() => new ReplayDeltaCodec.Reader(schema).Decode(token));
            }
        });
        test("environment is preserved by highlight and complete recording time rebasing", () =>
        {
            var buffer = new RollingBuffer(); var a = Frame(400, Environment(1)); var b = Frame(400.1, Environment(2));
            buffer.Add(a); buffer.Add(b);
            var clip = buffer.Snapshot(new ReplayHeader { Scene = "Test" }, DateTime.UtcNow);
            Check(clip.Frames[0].T == 0 && Json(clip.Frames[0].World.Environment) == Json(a.World.Environment));
            var full = new ContinuousReplayRebaser(400).Apply(b);
            Check(Math.Abs(full.T - .1) < .000001 && Json(full.World.Environment) == Json(b.World.Environment));
            Check(RollingBuffer.Estimate(a) > RollingBuffer.Estimate(Frame(400, null)));
        });
        test("duplicate or nonfinite environment cannot enter stored frames", () =>
        {
            var env = Environment(0); env.Winds = new[] { env.Winds[0], env.Winds[0] };
            Reject(() => ReplayRules.Validate(Frame(0, env), -1));
            env = Environment(0); env.Winds[0].Direction[0] = float.NaN;
            Reject(() => ReplayRules.Validate(Frame(0, env), -1));
        });
        test("installed native fog fade Hermite overshoot and DisableFog terminal frame remain exact through codec", () =>
        {
            // Installed level4..level25 fogFadeCurve: the first two values are
            // both 1, with start outgoing slope 0 and end incoming slope < 0.
            // OrbFogHandler.WaitForReveal evaluates this curve after c += dt.
            float at60 = NativeFogFadeFirstSegment((1d / 60) / 5);
            float at40 = NativeFogFadeFirstSegment((1d / 40) / 5);
            float peak = NativeFogFadeFirstSegment(.09926971793174744 * 2 / 3);
            Check(at60 == 1.0000083446502686f && at40 == 1.0000184774398804f);
            Check(peak == 1.0011341789447572f && at60 > 1 && at40 > 1 && peak > 1);
            // Native DisableFog writes 1-c/1 after the final c += deltaTime,
            // before setting ENABLE = 0 on the following coroutine iteration.
            float terminal = 1f - (.99f + .025f);
            Check(terminal < 0 && terminal > -1);
            var codec = new ReplayDeltaCodec.Writer(); var reader = new ReplayDeltaCodec.Reader();
            foreach (float enable in new[] { 1f, at60, at40, peak, terminal, 0f })
            {
                var env = Environment(0); env.Fog!.Enable = enable;
                ReplayRules.Validate(Frame(0, env), -1);
                var decoded = reader.Decode(Token(codec.Encode(Frame(0, env))));
                Check(decoded.World.Environment!.Fog!.Enable == enable);
            }
        });
        test("native fog overshoot survives full writer map transition page baselines and observation rebasing", () =>
        {
            string temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            string root = System.IO.Path.Combine(temp, "peak-fog-native-integration-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(root);
            try
            {
                var values = new[] { 1f, NativeFogFadeFirstSegment((1d / 60) / 5),
                    NativeFogFadeFirstSegment((1d / 40) / 5), NativeFogFadeFirstSegment(.09926971793174744 * 2 / 3),
                    1f - (.99f + .025f), 0f };
                var frames = values.Select((enable, i) =>
                {
                    var env = Environment(0); env.SampleTimeKnown = true; env.SampleTime = 400 + i * .05;
                    env.Fog!.Enable = enable; env.Fog.Reveal = (float)Math.Min(1, i * .2);
                    return new ReplayFrame { T = env.SampleTime, World = new() { Segment = i == 0 ? 0 : 1, Environment = env } };
                }).ToArray();
                var writer = new FullReplayWriter(root, new ReplayHeader { Scene = "Level_Synthetic" },
                    new ContinuousReplayOptions { SegmentFrameLimit = 2 });
                foreach (var frame in frames) Check(writer.TryEnqueue(frame));
                var completion = writer.CompleteAsync(); Check(completion.Wait(TimeSpan.FromSeconds(10)));
                var result = completion.GetAwaiter().GetResult();
                Check(result.Status == "completed" && result.WrittenFrames == frames.Length);
                var info = FullReplayArchive.ReadInfo(result.FilePath); Check(info.Header.Schema == ReplayRules.CurrentSchema && info.Pages.Length >= 3);
                foreach (int page in Enumerable.Range(0, info.Pages.Length).Reverse())
                    foreach (var decoded in FullReplayArchive.ReadPage(result.FilePath, info, page).Frames)
                    {
                        int sample = (int)Math.Round(decoded.T / .05);
                        var env = decoded.World.Environment!;
                        Check(env.Fog!.Enable == values[sample] && env.Fog.Reveal == frames[sample].World.Environment!.Fog!.Reveal);
                        Check(env.SampleTimeKnown && Math.Abs(env.SampleTime - (frames[sample].T - 400)) < .0000001);
                        Check(decoded.World.Segment == frames[sample].World.Segment);
                    }
            }
            finally
            {
                string target = System.IO.Path.GetFullPath(root);
                if (!target.StartsWith(System.IO.Path.Combine(temp, "peak-fog-native-integration-"), StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Unsafe native fog test cleanup target.");
                System.IO.Directory.Delete(target, true);
            }
        });
        test("fog enable overshoot remains bounded finite and field diagnostics retain native identity", () =>
        {
            foreach (float enable in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1.001f, 2.001f, 1000000f })
            {
                var env = Environment(0); env.Fog!.Enable = enable;
                RejectField(() => ReplayRules.Validate(Frame(0, env), -1), "Enable", "fog");
            }
            // Reveal's installed 2x-x² curve stays in [0,1]. The two native
            // wind progress fields are Clamp01 outputs, so keep their bounds.
            foreach (float invalid in new[] { -.0001f, 1.0001f, float.NaN })
            {
                var env = Environment(0); env.Fog!.Reveal = invalid;
                RejectField(() => ReplayRules.Validate(Frame(0, env), -1), "Reveal", "fog");
                env = Environment(0); env.Winds[0].StormProgress = invalid;
                RejectField(() => ReplayRules.Validate(Frame(0, env), -1), "StormProgress", "wind");
                env = Environment(0); env.Winds[0].TimeUntilStorm = invalid;
                RejectField(() => ReplayRules.Validate(Frame(0, env), -1), "TimeUntilStorm", "wind");
            }
            var vector = Environment(0); vector.Winds[0].Direction[2] = float.NaN;
            RejectField(() => ReplayRules.Validate(Frame(0, vector), -1), "Direction", "wind", "axis=2");
            var clock = Environment(0); clock.SampleTimeKnown = true; clock.SampleTime = double.PositiveInfinity;
            RejectField(() => ReplayRules.Validate(Frame(0, clock), -1), "SampleTime", null);
        });
    }
    private static ReplayFrame Frame(double time, EnvironmentReplayFrame? environment) => new() { T = time, World = new() { Environment = environment } };
    private static EnvironmentReplayFrame Environment(int n) => new()
    {
        WeatherBlend = .4f, GlobalWind = .8f, SnowFactor = .5f,
        Winds = new[] { new EnvironmentWindFrame { Key = "wind", Enabled = true, Active = true, Direction = new[] { 1f, 0f, 0f },
            Intensity = .7f, StormProgress = n * .1f, Duration = 10, ActiveFor = n, SecondsUntilSwitch = 10 - n,
            Storms = new[] { new EnvironmentStormFrame { Key = "snow", Kind = 1, Factor = .5f, Intensity = .6f } } } },
        Lava = new[] { new EnvironmentLavaFrame { Key = "lava", Kind = 0, Active = true, Started = true, Position = new[] { 0f, (float)n, 0f }, ProgressTime = n } },
        Fog = new EnvironmentFogFrame { Key = "fog", Point = new[] { 10f, 100f, 20f }, Size = 200, Padding = 20, Enable = 1, Reveal = 1, Active = true, Origin = 2 },
    };
    private static string Json(object? value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static JObject Token(object value) => JObject.Parse(Json(value));
    private static void Check(bool value) { if (!value) throw new Exception("Environment storage integration failed."); }
    private static void Reject(Action action)
    { try { action(); } catch (System.IO.InvalidDataException) { return; } throw new Exception("Invalid environment was accepted."); }
    private static void RejectField(Action action, string field, string? source, string? detail = null)
    {
        try { action(); }
        catch (System.IO.InvalidDataException error)
        {
            Check(error.Message.Contains("field=" + field, StringComparison.Ordinal));
            Check(source == null || error.Message.Contains("source=" + source, StringComparison.Ordinal));
            Check(detail == null || error.Message.Contains(detail, StringComparison.Ordinal));
            Check(error.Message.Contains("value=", StringComparison.Ordinal));
            return;
        }
        throw new Exception("Invalid environment field was accepted.");
    }
    private static float NativeFogFadeFirstSegment(double time)
    {
        const double keyTime = .09926971793174744, endIncomingSlope = -.07712027430534363;
        double u = time / keyTime;
        return (float)(1 + keyTime * endIncomingSlope * (u * u * u - u * u));
    }
}
