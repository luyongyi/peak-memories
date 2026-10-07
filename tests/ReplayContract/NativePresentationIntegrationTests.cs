using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class NativePresentationIntegrationTests
{
    public static void Run(Action<string, Action> test)
    {
        test("native placements retain resource identity appearance lifetime and legacy version boundaries", () =>
        {
            foreach (var pair in new[] { ("piton", "0_items/climbingspikehammered"), ("fragile-piton", "0_items/climbingspikehammered_shitty"),
                ("fragile-piton-hand", "0_items/climbingspikehammered_shitty hand"), ("checkpoint-flag", "flag_planted_checkpoint"),
                ("portable-stove", "portablestovetop_placed"), ("magic-bean-vine", "magicbeanvine"),
                ("cloud-fungus", "0_items/cloudfungusplaced"), ("anti-sphere", "antisphere_projectile") })
            {
                var placed = new SpawnedReplayFrame { Key = "placed", Kind = pair.Item1, Resource = pair.Item2, Visuals = Visuals() };
                var frame = new ReplayFrame { Spawned = new[] { placed } }; ReplayRules.Validate(frame, -1);
                var read = new ReplayDeltaCodec.Reader().Decode(Token(new ReplayDeltaCodec.Writer().Encode(frame)));
                Check(Json(read.Spawned) == Json(frame.Spawned));
                foreach (int old in new[] { 10, 11 }) Reject(() => new ReplayDeltaCodec.Writer(old).Encode(frame));
                placed.Resource = "unverified"; Reject(() => ReplayRules.Validate(frame, -1));
            }
        });
        test("creatures silk wraps and material variants survive current deltas removals and reintroduction", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(); var reader = new ReplayDeltaCodec.Reader();
            var creature = new CreatureReplayFrame { Key = "spider", Kind = "spider", Resource = "spider",
                Line = new CreatureReplayLine { Enabled = true, Width = .02f, Points = Enumerable.Range(0, 120).Select(i => i * .01f).ToArray() } };
            var item = new ItemFrame { Key = "berry", Visuals = Visuals() };
            var actor = new ActorFrame { Id = "actor", WebWrap = new WebWrapReplayFrame { Parts = new[] { new WebWrapReplayPart { Path = "web", Active = true, Clip = .42f } } } };
            var first = new ReplayFrame { Creatures = new[] { creature }, Items = new[] { item }, Actors = new[] { actor } };
            foreach (var frame in new[] { first, new ReplayFrame { T = .1 }, new ReplayFrame { T = .2, Creatures = first.Creatures, Items = first.Items, Actors = first.Actors } })
            {
                var result = reader.Decode(Token(writer.Encode(frame)));
                ReplayRules.Validate(result, frame.T - 1);
                Check(Json(result.Creatures) == Json(frame.Creatures) && Json(result.Items) == Json(frame.Items) && Json(result.Actors) == Json(frame.Actors));
            }
        });
        test("native renderer uint masks and unknown old materials remain distinct from known empty", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(); var reader = new ReplayDeltaCodec.Reader();
            foreach (var visuals in new NativeRendererFrame[]?[] { null, Array.Empty<NativeRendererFrame>(), Visuals(), null })
            {
                var result = reader.Decode(Token(writer.Encode(new ReplayFrame { Items = new[] { new ItemFrame { Key = "item", Visuals = visuals } } })));
                Check(Json(result.Items[0].Visuals) == Json(visuals));
            }
            foreach (int schema in new[] { 10, 11 })
            {
                var result = new ReplayDeltaCodec.Reader(schema).Decode(Token(new ReplayDeltaCodec.Writer(schema).Encode(new ReplayFrame { Items = new[] { new ItemFrame { Key = "item" } } })));
                Check(result.Items[0].Visuals == null);
            }
        });
        test("oversized silk duplicate wraps and nonnative material properties are rejected", () =>
        {
            var c = new CreatureReplayFrame { Key = "spider", Kind = "spider", Resource = "spider", Line = new CreatureReplayLine { Points = new float[385] } };
            Reject(() => ReplayRules.Validate(new ReplayFrame { Creatures = new[] { c } }, -1));
            var part = new WebWrapReplayPart { Path = "same" };
            Reject(() => ReplayRules.Validate(new ReplayFrame { Actors = new[] { new ActorFrame { Id = "actor", WebWrap = new() { Parts = new[] { part, part } } } } }, -1));
            var visual = Visuals(); visual[0].Materials[0].Properties[0].Name = "unverified";
            Reject(() => ReplayRules.Validate(new ReplayFrame { Items = new[] { new ItemFrame { Key = "item", Visuals = visual } } }, -1));
        });
        test("native item and placed lights retain unknown empty active and extinguished states through seeks", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(); var reader = new ReplayDeltaCodec.Reader();
            var records = new List<object>(); var expected = new List<ReplayFrame>();
            var extinguished = Lights(); extinguished[0].Enabled = false; extinguished[0].Intensity = 0;
            foreach (var lights in new NativeLightFrame[]?[] { null, Array.Empty<NativeLightFrame>(), Lights(), extinguished, null })
            {
                var frame = new ReplayFrame { T = expected.Count * .1,
                    Items = new[] { new ItemFrame { Key = "lantern", Lights = lights } },
                    Spawned = new[] { new SpawnedReplayFrame { Key = "stove", Kind = "portable-stove", Resource = "portablestovetop_placed", Lights = lights } } };
                ReplayRules.Validate(frame, frame.T - 1);
                var encoded = writer.Encode(frame); records.Add(encoded); expected.Add(frame);
                var read = reader.Decode(Token(encoded));
                Check(Json(read.Items[0].Lights) == Json(lights) && Json(read.Spawned[0].Lights) == Json(lights));
            }
            // Reconstruct each seek from a fresh reader and its earlier baseline, including extinction and unknown gaps.
            for (int seek = records.Count - 1; seek >= 0; seek--)
            {
                var seekReader = new ReplayDeltaCodec.Reader(); ReplayFrame read = null!;
                for (int i = 0; i <= seek; i++) read = seekReader.Decode(Token(records[i]));
                Check(Json(read.Items[0].Lights) == Json(expected[seek].Items[0].Lights));
                Check(Json(read.Spawned[0].Lights) == Json(expected[seek].Spawned[0].Lights));
            }
        });
        test("native lights cannot leak into legacy codecs or bypass bounded validation", () =>
        {
            foreach (int schema in new[] { 10, 11 })
            {
                var old = new ReplayFrame { Items = new[] { new ItemFrame { Key = "light" } },
                    Spawned = new[] { new SpawnedReplayFrame { Key = "cannon", Kind = "scout-cannon", Resource = "scoutcannon_placed" } } };
                var decoded = new ReplayDeltaCodec.Reader(schema).Decode(Token(new ReplayDeltaCodec.Writer(schema).Encode(old)));
                Check(decoded.Items[0].Lights == null && decoded.Spawned[0].Lights == null);
                old.Items[0].Lights = Lights(); Reject(() => new ReplayDeltaCodec.Writer(schema).Encode(old));
                old.Items[0].Lights = null; old.Spawned[0].Lights = Lights(); Reject(() => new ReplayDeltaCodec.Writer(schema).Encode(old));
            }
            foreach (var bad in new[] { Lights(), Lights(), Lights(), Lights(), Lights() }.Select((v, i) =>
            {
                if (i == 0) v[0].Intensity = float.NaN;
                if (i == 1) v[0].CookieSize2D = new float[3];
                if (i == 2) v[0].InnerSpotAngle = 100;
                if (i == 3) v = new[] { v[0], v[0] };
                if (i == 4) v = Enumerable.Range(0, NativeLightRules.MaximumLights + 1).Select(n => new NativeLightFrame { Path = "light-" + n }).ToArray();
                return v;
            }))
                Reject(() => ReplayRules.Validate(new ReplayFrame { Items = new[] { new ItemFrame { Key = "light", Lights = bad } } }, -1));
            var malformed = Token(new ReplayDeltaCodec.Writer().Encode(new ReplayFrame { Items = new[] { new ItemFrame { Key = "light", Lights = Lights() } } }));
            var cookie = malformed.Descendants().OfType<JProperty>().Single(p => p.Name == nameof(NativeLightFrame.CookieSize2D));
            cookie.Value = new JArray(1, 2, 3);
            Reject(() => new ReplayDeltaCodec.Reader().Decode(malformed));
        });
        test("native light memory and rolling rebase preserve complete item and placed snapshots", () =>
        {
            var item = new ItemFrame { Key = "lantern", Lights = Lights() };
            var placed = new SpawnedReplayFrame { Key = "cannon", Kind = "scout-cannon", Resource = "scoutcannon_placed", Lights = Lights(),
                Animation = new CrateAnimationFrame { Anchored = true, AnchorTime = .2, Clip = "CannonLight", Rate = 1, Duration = 1 } };
            Check(RollingBuffer.Estimate(item) >= RollingBuffer.Estimate(new ItemFrame { Key = item.Key }) + NativeLightRules.Estimate(item.Lights));
            Check(SpawnedReplayRules.Estimate(placed) >= NativeLightRules.Estimate(placed.Lights));
            Check(ReferenceEquals(SpawnedReplayRules.Rebase(placed, .1).Lights, placed.Lights));
            var b = new RollingBuffer();
            b.Add(new ReplayFrame { T = .1, Items = new[] { item }, Spawned = new[] { placed } });
            b.Add(new ReplayFrame { T = .2, Items = new[] { item }, Spawned = new[] { placed } });
            var clip = b.Snapshot(new ReplayHeader { Scene = "Test" }, DateTime.UtcNow);
            Check(clip.Frames.All(f => Json(f.Items[0].Lights) == Json(item.Lights) && Json(f.Spawned[0].Lights) == Json(placed.Lights)));
        });
        test("new tracks are included in bounded rolling and page memory accounting", () =>
        {
            var empty = new ReplayFrame();
            var frame = new ReplayFrame { Creatures = new[] { new CreatureReplayFrame { Key = "mine", Kind = "spore-trap", Resource = "spore-trap" } },
                Items = new[] { new ItemFrame { Key = "item", Visuals = Visuals() } }, Actors = new[] { new ActorFrame { Id = "actor", WebWrap = new() { Parts = new[] { new WebWrapReplayPart { Path = "web" } } } } } };
            Check(RollingBuffer.Estimate(frame) > RollingBuffer.Estimate(empty));
            var b = new RollingBuffer(); b.Add(frame); b.Add(new ReplayFrame { T = .1, Creatures = frame.Creatures, Items = frame.Items, Actors = frame.Actors });
            var clip = b.Snapshot(new ReplayHeader { Scene = "Test" }, DateTime.UtcNow);
            Check(clip.Frames.All(f => f.Creatures.Length == 1 && f.Actors[0].WebWrap != null && f.Items[0].Visuals != null));
            var page = new ContinuousReplayBudget(); page.Commit(frame, page.Additional(frame));
            Check(page.Bytes >= RollingBuffer.Estimate(frame));
        });
    }
    private static NativeRendererFrame[] Visuals() => new[] { new NativeRendererFrame { RenderingLayerMask = uint.MaxValue,
        Materials = new[] { new NativeMaterialFrame { Name = "native", Shader = "native", Properties = new[] { new NativeShaderPropertyFrame { Name = "_Tint", Kind = 1, Values = new[] { 1f, .8f, .1f, 1f } } } } } } };
    private static NativeLightFrame[] Lights() => new[] { new NativeLightFrame { Path = "lamp", Intensity = 2.75f,
        Color = new[] { 1f, .65f, .22f, 1f }, Cookie = "native-cookie", CookieSize2D = new[] { 5f, 8f },
        CullingMask = int.MinValue, RenderingLayerMask = uint.MaxValue, UseColorTemperature = true, ColorTemperature = 3000 } };
    private static string Json(object? value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static JObject Token(object value) => JObject.Parse(Json(value));
    private static void Check(bool value) { if (!value) throw new Exception("Native presentation storage integration failed."); }
    private static void Reject(Action action)
    { try { action(); } catch (System.IO.InvalidDataException) { return; } throw new Exception("Invalid presentation was accepted."); }
}
