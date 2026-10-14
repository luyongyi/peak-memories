using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static partial class FullReplaySharedBlockTests
{
    public static void Run(Action<string, Action> test)
    {
        RunStaticTraps(test);
        RunSharedLimits(test);
        test("shared gzip footer collection preserves every short read split and member boundary", () =>
        {
            byte[] payload = Enumerable.Range(0, 4097).Select(i => (byte)(i * 31 + i / 17)).ToArray();
            byte[] compressed;
            using (var output = new MemoryStream())
            { using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(payload); compressed = output.ToArray(); }
            var footer = new byte[8];
            foreach (int chunk in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 17, 8192 })
            {
                using var source = new ShortReadStream(compressed.Concat(new byte[] { 91, 92, 93 }).ToArray(), chunk);
                using var slice = new FullReplayArchive.PageSlice(source, compressed.Length, default, footer);
                using (var gzip = new GZipStream(slice, CompressionMode.Decompress, true))
                using (var output = new MemoryStream())
                { gzip.CopyTo(output); Check(output.ToArray().SequenceEqual(payload)); }
                Check(slice.Remaining == 0 && source.BytesRead == compressed.Length && footer.SequenceEqual(compressed.Skip(compressed.Length - 8)));
                Check(source.ReadByte() == 91, "Shared member consumed bytes belonging to the next block.");
            }
            // Direct reads exercise nonzero destination offsets and reuse of a
            // footer containing the preceding block's unrelated trailer.
            foreach (int length in new[] { 8, 9, 18, 41 })
            foreach (int chunk in new[] { 1, 3, 7, 8, 11 })
            {
                byte[] input = Enumerable.Range(0, length).Select(i => (byte)(i + length)).ToArray();
                using var source = new ShortReadStream(input, chunk);
                using var slice = new FullReplayArchive.PageSlice(source, length, default, footer);
                var scratch = new byte[27];
                Check(slice.Read(scratch, 4, 0) == 0);
                while (slice.Read(scratch, 5, 13) != 0) { }
                Check(footer.SequenceEqual(input.Skip(length - 8)));
            }
        });
        test("shared gzip sequential footer still rejects truncation damaged trailers and cancellation", () => InRoot(root =>
        {
            byte[] payload = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray(); byte[] compressed;
            using (var output = new MemoryStream())
            { using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(payload); compressed = output.ToArray(); }
            for (int missing = 1; missing <= 8; missing++)
            {
                using var source = new ShortReadStream(compressed.Take(compressed.Length - missing).ToArray(), 3);
                using var slice = new FullReplayArchive.PageSlice(source, compressed.Length, default, new byte[8]);
                using var gzip = new GZipStream(slice, CompressionMode.Decompress, true);
                Reject(() => gzip.CopyTo(Stream.Null));
            }
            using (var cancelled = new CancellationTokenSource())
            using (var source = new ShortReadStream(compressed, 2))
            using (var slice = new FullReplayArchive.PageSlice(source, compressed.Length, cancelled.Token, new byte[8]))
            {
                var buffer = new byte[16]; Check(slice.Read(buffer, 1, 8) == 2); cancelled.Cancel();
                try { slice.Read(buffer, 0, buffer.Length); throw new Exception("Cancelled shared slice continued reading."); }
                catch (OperationCanceledException) { }
            }
            string path = Save(root, Frames()); var info = FullReplayArchive.ReadInfo(path); var block = info.SharedBlocks![0];
            byte[] original = File.ReadAllBytes(path);
            foreach (int fromEnd in new[] { 8, 7, 4, 3, 1 })
            {
                var bytes = (byte[])original.Clone(); bytes[block.Offset + block.Length - fromEnd] ^= 1; File.WriteAllBytes(path, bytes);
                Reject(() => FullReplayArchive.ReadPage(path, info, 0));
            }
            File.WriteAllBytes(path, original);
            Check(FullReplayArchive.ReadPage(path, info, 0).Complete);
        }));
        test("v4 pulled rope geometry remains whole shared state after settling", () => InRoot(root =>
        {
            RopeReplayFrame Rope(int state) => new() { Key = "settled-rope", Kind = "rope", Resource = "rope", Pose = Pose(state),
                Points = Enumerable.Range(0, 40).Select(i => new RopeReplayPoint(new[] { i * .2f, state / 8f + i * .037f, i * .013f }, new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f })).ToArray() };
            var frames = Enumerable.Range(0, 65).Select(i => new ReplayFrame { T = i / 4d, Ropes = new[] { Rope(i < 8 ? 0 : i < 16 ? 1 : 2) } }).ToArray();
            string path = Save(root, frames); var info = FullReplayArchive.ReadInfo(path);
            Check(info.SharedBlocks!.Length == 3 && info.SharedBlocks.All(b => b.Kind == 2));
            foreach (var page in info.Pages.AsEnumerable().Reverse())
                foreach (var frame in FullReplayArchive.ReadPage(path, info, page.Index).Frames)
                    Check(Json(frame) == Json(frames[(int)Math.Round(frame.T * 4)]));
            string legacy = SaveLegacy(root, path, info, true);
            Check(new FileInfo(path).Length <= new FileInfo(legacy).Length + 256, "Settled rope geometry was repeatedly written inline.");
        }));
        test("v4 repeated changed appearance state promotes once to a complete unchained snapshot", () => InRoot(root =>
        {
            var variants = ActiveFrames(); var first = variants[0].Items[0]; var second = variants[25].Items[0]; var third = variants[40].Items[0];
            var frames = Enumerable.Range(0, 161).Select(i => new ReplayFrame { T = i / 4d, Items = new[] {
                JsonConvert.DeserializeObject<ItemFrame>(Json(i < 8 ? first : i < 60 ? second : i < 120 ? third : second))! } }).ToArray();
            string path = Save(root, frames); var info = FullReplayArchive.ReadInfo(path);
            Check(info.SharedBlocks!.Count(b => b.Kind == 0) == 3, "Settled appearance was not promoted to a whole snapshot.");
            Check(info.SharedBlocks!.Count(b => b.Kind >= 6) <= 6);
            foreach (var page in info.Pages.AsEnumerable().Reverse())
                foreach (var frame in FullReplayArchive.ReadPage(path, info, page.Index).Frames)
                    Check(Json(frame) == Json(frames[(int)Math.Round(frame.T * 4)]));
            string legacy = SaveLegacy(root, path, info, true);
            Check(new FileInfo(path).Length < new FileInfo(legacy).Length * 1.15, "Settled appearance repeated large inline baselines.");
            var shared = new FullReplaySharedWriter();
            foreach (var frame in new[] { frames[0], frames[8], frames[9] }) shared.EncodeBaseline(new ReplayDeltaCodec.Writer().Encode(frame), frame);
            Check(shared.DeferredHashCount == 0 && shared.Blocks.Count(b => b.Kind == 0) == 2);
        }));
        test("v4 active appearance leaves preserve materials lights visibility topology and random direct seeks", () => InRoot(root =>
        {
            var frames = ActiveFrames(); string path = Save(root, frames); var info = FullReplayArchive.ReadInfo(path);
            Check(info.Version == 4 && info.SharedBlocks!.Any(b => b.Kind == 6) && info.SharedBlocks!.Any(b => b.Kind == 7) && info.SharedBlocks!.Any(b => b.Kind == 8));
            using var source = FullReplayArchive.OpenPlayback(path, info);
            foreach (int index in Enumerable.Range(0, info.Pages.Length).OrderBy(i => (i * 11) % info.Pages.Length))
            {
                var clip = source.Load(index, default);
                foreach (var frame in clip.Frames) Check(Json(frame) == Json(frames[(int)Math.Round(frame.T * 4)]));
            }
            string legacy = SaveLegacy(root, path, info, true); var old = FullReplayArchive.ReadInfo(legacy);
            Check(old.Version == 3 && old.Header.Schema == 16 && old.SharedBlocks!.All(b => b.Kind < 6));
            for (int index = old.Pages.Length - 1; index >= 0; index--)
                Check(Json(FullReplayArchive.ReadPage(legacy, old, index).Frames) == Json(source.Load(index, default).Frames));
            long reduced = new FileInfo(path).Length, original = new FileInfo(legacy).Length;
            Check(reduced < original * .65, $"Active appearance sharing did not save enough: {reduced}/{original}.");
            Console.WriteLine($"ACTIVE APPEARANCE SIZE: v4={reduced} v3={original} saving={(1 - (double)reduced / original):P1}");
            var bad = FullReplayArchive.ReadInfo(path); bad.Version = 3; Reject(() => FullReplayArchive.ReadPage(path, bad, 0));
            bad = FullReplayArchive.ReadInfo(path); bad.Header.Schema = 16; Reject(() => FullReplayArchive.ReadPage(path, bad, 0));
        }));
        test("v4 leaf codec rejects chains mixed fields invalid indices wrong types and legacy schemas", () =>
        {
            var frame = ActiveFrames()[4]; var baseline = JObject.Parse(Json(new ReplayDeltaCodec.Writer(17).Encode(frame)));
            var item = (JObject)baseline["Items"]![0]!;
            var visual = frame.Items[0].Visuals!;
            string leaf = Json(new { Value = visual }); char[] chars = leaf.ToCharArray();
            var reader = new ReplayDeltaCodec.StreamReader(17);
            Check(Json(reader.DecodeSharedValue(chars, chars.Length, typeof(NativeRendererFrame[]))) == Json(visual));
            Reject(() => new ReplayDeltaCodec.StreamReader(16).DecodeSharedValue(chars, chars.Length, typeof(NativeRendererFrame[])));
            foreach (string broken in new[] { "{\"Value\":[{\"$Shared\":0}]}", "{\"$Shared\":0}", leaf + "{}", "{\"Value\":[],\"Value\":[]}" })
            { var b = broken.ToCharArray(); Reject(() => reader.DecodeSharedValue(b, b.Length, typeof(NativeRendererFrame[]))); }
            foreach (string broken in new[] { "{\"$Shared\":-1}", "{\"$Shared\":0.0}", "{\"$Shared\":2147483648}", "{\"$Shared\":0,\"Value\":[]}", "{\"$Shared\":0,\"$Shared\":0}" })
            {
                string record = Json(baseline); string oldVisual = item["Visuals"]!.ToString(Formatting.None);
                record = record.Replace("\"Visuals\":" + oldVisual, "\"Visuals\":" + broken);
                Reject(() => new ReplayDeltaCodec.StreamReader(17, (type, id) => visual).DecodeRecord(record));
            }
            item["Visuals"] = JObject.Parse("{\"$Shared\":0}"); string valid = Json(baseline);
            Check(Json(new ReplayDeltaCodec.StreamReader(17, (type, id) => visual).DecodeRecord(valid)) == Json(frame));
            Reject(() => new ReplayDeltaCodec.StreamReader(16, (type, id) => visual).DecodeRecord(valid));
            Reject(() => new ReplayDeltaCodec.StreamReader(17).DecodeRecord(valid));
            Reject(() => new ReplayDeltaCodec.StreamReader(17, (type, id) => Array.Empty<NativeLightFrame>()).DecodeRecord(valid));
            Reject(() => reader.DecodeSharedRecord(Json(item), typeof(ItemFrame), "Key"));
        });
        test("v4 node templates stay bounded and old absolute references survive eviction", () =>
        {
            var writer = new FullReplaySharedWriter(); object? first = null; NodePose[]? expected = null;
            for (int i = 0; i < FullReplaySharedWriter.MaximumNodeTemplates + 4; i++)
            {
                var initial = new ReplayFrame { Items = new[] { Item("evict-" + i, 0) } };
                writer.EncodeBaseline(new ReplayDeltaCodec.Writer(17).Encode(initial), initial);
                var changed = new ReplayFrame { Items = new[] { Item("evict-" + i, 1) } };
                object encoded = new ReplayDeltaCodec.Writer(17).Encode(changed); writer.EncodeBaseline(encoded, changed);
                if (i == 0) { first = encoded; expected = changed.Items[0].Pose.Nodes; }
                Check(writer.NodeTemplateCount <= FullReplaySharedWriter.MaximumNodeTemplates && writer.NodeTemplateBytes <= FullReplaySharedWriter.MaximumNodeTemplateBytes);
            }
            Check(writer.NodeTemplateCount == FullReplaySharedWriter.MaximumNodeTemplates);
            var decoded = new ReplayDeltaCodec.StreamReader(17, (type, id) => expected!).DecodeRecord(Json(first!));
            Check(Json(decoded!.Items[0].Pose.Nodes) == Json(expected!));
            // Large node paths exercise the byte cap before the count cap.
            for (int i = 0; i < 40; i++)
            {
                CreatureReplayFrame Make(float x) => new() { Key = "large-" + i, Kind = "scoutmaster", Resource = "scoutmaster", Pose = new ObjectPose { Position = new[] { x, 0, 0 }, Nodes = Enumerable.Range(0, 1024).Select(n => new NodePose(new string('n', 200) + n, new float[3], new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f })).ToArray() } };
                foreach (float x in new[] { 0f, 1f })
                { var f = new ReplayFrame { Creatures = new[] { Make(x) } }; writer.EncodeBaseline(new ReplayDeltaCodec.Writer(17).Encode(f), f); }
                Check(writer.NodeTemplateBytes <= FullReplaySharedWriter.MaximumNodeTemplateBytes && writer.NodeTemplateCount <= FullReplaySharedWriter.MaximumNodeTemplates);
            }
            Check(writer.NodeTemplateCount < FullReplaySharedWriter.MaximumNodeTemplates);
        });
        test("v4 shared node patches require valid absolute baseline length indices and schema", () =>
        {
            var frame = new ReplayFrame { Items = new[] { Item("node-ref", 0) } };
            var record = JObject.Parse(Json(new ReplayDeltaCodec.Writer(17).Encode(frame)));
            var pose = (JObject)record["Items"]![0]!["Pose"]!; var nodes = frame.Items[0].Pose.Nodes;
            pose["Nodes"] = JObject.Parse("{\"$Shared\":0}");
            Check(Json(new ReplayDeltaCodec.StreamReader(17, (type, id) => nodes).DecodeRecord(Json(record))) == Json(frame));
            Reject(() => new ReplayDeltaCodec.StreamReader(16, (type, id) => nodes).DecodeRecord(Json(record)));
            Reject(() => new ReplayDeltaCodec.StreamReader(17, (type, id) => Array.Empty<NativeRendererFrame>()).DecodeRecord(Json(record)));
            foreach (string invalid in new[] {
                "{\"$Shared\":-1}", "{\"$Shared\":0,\"Length\":16}", "{\"$Shared\":0,\"Changes\":[]}",
                "{\"$Shared\":0,\"Length\":15,\"Changes\":[]}", "{\"$Shared\":0,\"Length\":16,\"Changes\":[[16,8,false]]}",
                "{\"$Shared\":0,\"Length\":16,\"Changes\":[[0,8,false],[0,8,true]]}",
                "{\"$Shared\":0,\"Length\":16,\"Changes\":[],\"Extra\":1}" })
            { pose["Nodes"] = JObject.Parse(invalid); Reject(() => new ReplayDeltaCodec.StreamReader(17, (type, id) => nodes).DecodeRecord(Json(record))); }
            pose["Nodes"] = JObject.Parse("{\"$Shared\":0,\"Length\":16,\"Changes\":[[0,1,[7.0,0.0,0.0]]]}");
            var changed = new ReplayDeltaCodec.StreamReader(17, (type, id) => nodes).DecodeRecord(Json(record))!;
            Check(changed.Items[0].Pose.Nodes[0].Position[0] == 7 && nodes[0].Position[0] == 0);
        });
        test("v4 full shared catalogue safely falls back to independent inline baselines", () =>
        {
            var writer = new FullReplaySharedWriter();
            while (writer.Blocks.Count < FullReplayArchive.MaximumSharedBlocks) writer.Blocks.Add(new FullReplaySharedBlock());
            var frame = ActiveFrames()[4]; var record = new ReplayDeltaCodec.Writer(17).Encode(frame); string expected = Json(record);
            Check(writer.EncodeBaseline(record, frame) == 0 && Json(record) == expected);
            Check(Json(new ReplayDeltaCodec.StreamReader(17).DecodeRecord(Json(record))) == Json(frame));
            Check(writer.NodeTemplateBytes == 0 && writer.NodeTemplateCount == 0);
        });
        test("shared snapshots preserve random seeks deletion return order motion and effect history", () => InRoot(root =>
        {
            var frames = Frames(); var path = Save(root, frames); var info = FullReplayArchive.ReadInfo(path);
            Check(info.Version == 4 && info.SharedBlocks!.Length > 0);
            Check(info.Pages.All(p => p.End - p.Start <= 1.000001));
            using var source = FullReplayArchive.OpenPlayback(path, info);
            foreach (int index in Enumerable.Range(0, info.Pages.Length).OrderBy(i => (i * 13) % info.Pages.Length))
            {
                ReplayClip clip = source.Load(index, default);
                foreach (ReplayFrame frame in clip.Frames) Check(Json(frame) == Json(frames[(int)Math.Round(frame.T * 4)]));
                var history = source.LoadHistory(index, default);
                Check(history.Frames.Count == clip.Frames.Count);
                for (int i = 0; i < clip.Frames.Count; i++) Check(Json(history.Frames[i].Effects) == Json(clip.Frames[i].Effects));
            }
            Check(info.SharedBlocks!.Length < frames.Length * 5, "Unchanged baselines were duplicated.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { source.Load(info.Pages.Length - 1, cancelled.Token); throw new Exception("Cancelled page loaded."); }
            catch (OperationCanceledException) { }
        }));
        test("cross page static snapshots reduce bytes against equivalent version 2 one second pages", () => InRoot(root =>
        {
            var path = Save(root, Frames()); var info = FullReplayArchive.ReadInfo(path);
            string legacy = SaveV2(root, path, info); var old = FullReplayArchive.ReadInfo(legacy);
            Check(old.Version == 2 && old.SharedBlocks == null && old.Pages.Length == info.Pages.Length);
            for (int i = info.Pages.Length - 1; i >= 0; i--)
                Check(Json(FullReplayArchive.ReadPage(path, info, i).Frames) == Json(FullReplayArchive.ReadPage(legacy, old, i).Frames));
            long reduced = new FileInfo(path).Length, original = new FileInfo(legacy).Length;
            Check(reduced < original * .8, $"Expected static baseline saving, got {reduced}/{original} bytes.");
            Console.WriteLine($"SHARED BASELINE SIZE: v4={reduced} v2={original} saving={(1 - (double)reduced / original):P1}");
        }));
        test("shared snapshot index rejects type future boundary overlap hash and damaged payload", () => InRoot(root =>
        {
            string path = Save(root, Frames());
            foreach (Action<FullReplayInfo> change in new Action<FullReplayInfo>[] {
                x => x.SharedBlocks![0].Kind = 5,
                x => x.SharedBlocks![0].Offset++,
                x => x.SharedBlocks![0].RawBytes = int.MaxValue,
                x => x.SharedBlocks![0].Hash = new string('0', 64),
                x => x.Pages[0].SharedThrough = x.SharedBlocks!.Length + 1,
                x => x.Pages[1].SharedThrough = 0,
            })
            {
                var broken = FullReplayArchive.ReadInfo(path); change(broken);
                Reject(() => FullReplayArchive.ReadPage(path, broken, 0));
            }
            var info = FullReplayArchive.ReadInfo(path); var block = info.SharedBlocks![0];
            using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            { file.Position = block.Offset + block.Length - 8; int b = file.ReadByte(); file.Position--; file.WriteByte((byte)(b ^ 1)); }
            Reject(() => FullReplayArchive.ReadPage(path, info, 0));
            // Particle pre-roll has no dependency on shared item/model blocks.
            var history = FullReplayArchive.CreateEffectPageLoader(info)(0, default);
            Check(history.Complete);
        }));
        test("equal static snapshots across distinct capture references share one block", () => InRoot(root =>
        {
            var frames = Enumerable.Range(0, 12).Select(i => new ReplayFrame { T = i,
                Items = new[] { Item("same", 0) } }).ToArray();
            string path = Save(root, frames); var info = FullReplayArchive.ReadInfo(path);
            Check(info.SharedBlocks!.Length == 1);
            Check(FullReplayArchive.ReadPage(path, info, info.Pages.Length - 1).Frames[0].Items[0].Key == "same");
        }));
        test("concurrent shared page reads never overwrite each other's scratch or decoded poses", () => InRoot(root =>
        {
            var frames = Frames(); string path = Save(root, frames); var info = FullReplayArchive.ReadInfo(path);
            using var source = FullReplayArchive.OpenPlayback(path, info);
            Task.WaitAll(new[] { 0, info.Pages.Length - 1, 3, 8 }.Select(index => Task.Run(() =>
            {
                var clip = source.Load(index, default);
                foreach (var frame in clip.Frames) Check(Json(frame) == Json(frames[(int)Math.Round(frame.T * 4)]));
            })).ToArray());
        }));
        test("shared scratch retention is bounded and disposed recording does not retain oversized buffers", () =>
        {
            var pool = new FullReplaySharedScratchPool();
            var first = pool.Rent(); var concurrent = pool.Rent(); Check(!ReferenceEquals(first, concurrent));
            first.Bytes = new byte[FullReplaySharedScratchPool.MaximumRetainedLength + 1];
            first.Characters = new char[FullReplaySharedScratchPool.MaximumRetainedLength + 1];
            pool.Return(first); var small = pool.Rent();
            Check(small.Bytes.Length <= FullReplaySharedScratchPool.MaximumRetainedLength && small.Characters.Length <= FullReplaySharedScratchPool.MaximumRetainedLength);
            pool.Dispose(); pool.Return(small); pool.Return(concurrent);
            try { pool.Rent(); throw new Exception("Disposed recording retained shared scratch."); } catch (ObjectDisposedException) { }
        });
    }

    private static ReplayFrame[] ActiveFrames()
    {
        NativeRendererFrame[] Visuals(int state) => Enumerable.Range(0, 48).Select(n => new NativeRendererFrame
        {
            Path = "root/mesh-" + n, Mesh = "native-mesh-" + n, Enabled = state != 1 || n != 0, BlendShapes = new[] { state == 2 ? 30f : 0f },
            Materials = new[] { new NativeMaterialFrame { Name = "native-material-" + n, Shader = "native-shader", Properties = new[] {
                new NativeShaderPropertyFrame { Name = "_Color", Kind = 1, Values = new[] { n / 48f, state / 3f, .5f, 1f } },
                new NativeShaderPropertyFrame { Name = "_MainTex", Kind = 3, Texture = "native-texture-" + n } } } }
        }).ToArray();
        var visuals = new[] { Visuals(0), Visuals(1), Visuals(2) };
        var lights = Enumerable.Range(0, 3).Select(state => new[] { new NativeLightFrame { Path = "root/light", Intensity = state + 1, Enabled = state != 1, Cookie = "native-cookie-" + state } }).ToArray();
        return Enumerable.Range(0, 65).Select(i =>
        {
            int state = i < 20 ? 0 : i < 36 ? 1 : i < 48 ? 2 : 0;
            var items = Enumerable.Range(0, 2).Select(n =>
            {
                var item = Item("active-" + n, 0); item.Pose.Position = new[] { i / 8f + n, 0, 0 };
                var nodes = item.Pose.Nodes;
                nodes[0] = new NodePose(nodes[0].Path, new[] { i / 64f, 0f, 0f }, nodes[0].Rotation, nodes[0].Scale, i % 3 != 1, i % 5 != 0);
                if (state == 2) Array.Reverse(nodes);
                if (i >= 56) item.Pose.Nodes = nodes.Take(12).ToArray();
                item.Visuals = visuals[state]; item.Lights = lights[state]; return item;
            }).ToArray();
            if (i >= 24 && i < 28) items = new[] { items[1] }; // removal/re-entry does not borrow another instance's pose
            return new ReplayFrame { T = i / 4d, Items = items,
                Effects = i >= 31 && i <= 34 ? new[] { new EffectReplayFrame { Key = "spore", Resource = "spore", AnchorTime = 7.75 } } : Array.Empty<EffectReplayFrame>() };
        }).ToArray();
    }
    private static ReplayFrame[] Frames()
    {
        var staticItems = Enumerable.Range(0, 24).Select(i => Item("item-" + i, i)).ToArray();
        var crates = Enumerable.Range(0, 20).Select(i => new CrateFrame { Key = "crate-" + i, Kind = "luggage", Pose = Pose(i) }).ToArray();
        var original = Item("moving", 50);
        var held = Item("moving", 51); held.HolderId = "scout"; held.State = 1;
        var thrown = Item("moving", 52); thrown.Primary = true; thrown.Lit = true; thrown.Fuel = .75f;
        var frames = new List<ReplayFrame>();
        for (int i = 0; i < 81; i++)
        {
            var moving = i < 8 ? original : i < 16 ? held : i < 28 ? thrown : original;
            var items = (i >= 20 && i < 24 ? staticItems : staticItems.Concat(new[] { moving })).ToArray();
            if (i >= 40 && i < 48) Array.Reverse(items);
            frames.Add(new ReplayFrame { T = i / 4d,
                Actors = new[] { new ActorFrame { Id = "scout", Position = new[] { (float)i, 0, 0 } } },
                Items = items, Crates = crates,
                Effects = i >= 15 && i < 22 ? new[] { new EffectReplayFrame { Key = "explosion", Resource = "spore", AnchorTime = 3.75 } } : Array.Empty<EffectReplayFrame>(),
                Events = i == 8 ? new[] { new ItemEvent { Sequence = 1, T = 2, Kind = "pickup", ItemKey = "moving", ActorId = "scout" } } : Array.Empty<ItemEvent>(),
            });
        }
        return frames.ToArray();
    }
    private static ItemFrame Item(string key, int offset) => new() { Key = key, ItemId = 1, Pose = Pose(offset) };
    private static ObjectPose Pose(int offset) => new() { Position = new[] { (float)offset, 0, 0 },
        Nodes = Enumerable.Range(0, 16).Select(i => new NodePose("root/branch-" + i,
            new[] { i * .134f + offset, i * .017f, i * .135f }, new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f })).ToArray() };
    private static string Save(string root, ReplayFrame[] frames)
    {
        var writer = new FullReplayWriter(root, new ReplayHeader { Scene = "Level_SharedSnapshots" });
        foreach (var frame in frames) Check(writer.TryEnqueue(frame));
        var result = writer.CompleteAsync().GetAwaiter().GetResult();
        Check(result.Status == "completed", result.Fault ?? "Recording failed."); return result.FilePath;
    }
    private static string SaveV2(string root, string source, FullReplayInfo info) => SaveLegacy(root, source, info, false);
    private static string SaveLegacy(string root, string source, FullReplayInfo info, bool shared)
    {
        string path = Path.Combine(root, shared ? "version-3.peakrun" : "version-2.peakrun");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var binary = new BinaryWriter(file, Encoding.UTF8, true);
        var header = JsonConvert.DeserializeObject<ReplayHeader>(Json(info.Header))!; header.Schema = 16;
        byte[] head = Encoding.UTF8.GetBytes(Json(header)); binary.Write(Encoding.ASCII.GetBytes("PEAKRUN1")); binary.Write(head.Length); binary.Write(head); binary.Flush();
        var pages = new List<FullReplayPage>();
        var blocks = new List<FullReplaySharedBlock>(); var hashes = new Dictionary<string, int>();
        foreach (var old in info.Pages)
        {
            var clip = FullReplayArchive.ReadPage(source, info, old.Index); long offset = file.Position, bytes = 0;
            var pending = new List<byte[]>(); int firstBlock = blocks.Count;
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest, true))
            using (var text = new StreamWriter(gzip, new UTF8Encoding(false), 4096, true))
            {
                void Write(object value) { string line = Json(value); bytes += Encoding.UTF8.GetByteCount(line) + 1; text.Write(line); text.Write('\n'); }
                Write(header); var codec = new ReplayDeltaCodec.Writer(16); bool baseline = true;
                foreach (var frame in clip.Frames)
                {
                    object encoded = codec.Encode(frame);
                    if (shared && baseline)
                    {
                        var record = (Dictionary<string, object?>)encoded;
                        string[] tracks = { "Items", "Crates", "Ropes", "Creatures", "Interactions", "Spawned" };
                        for (int kind = 0; kind < tracks.Length; kind++)
                            foreach (var array in new[] { (object[])record[tracks[kind]]! })
                                for (int i = 0; i < array.Length; i++)
                                {
                                    byte[] raw = Encoding.UTF8.GetBytes(Json(array[i])); if (raw.Length < 512) continue;
                                    string hash = FullReplayArchive.SharedHash(kind, raw);
                                    if (!hashes.TryGetValue(hash, out int id))
                                    { id = blocks.Count; hashes.Add(hash, id); blocks.Add(new FullReplaySharedBlock { Kind = kind, RawBytes = raw.Length, Hash = hash }); pending.Add(raw); }
                                    array[i] = new Dictionary<string, object?> { ["$Shared"] = id };
                                }
                    }
                    Write(encoded); baseline = false;
                }
                Write(new { Type = "end" });
            }
            long length = file.Position - offset;
            var history = FullReplayArchive.WriteEffectHistory(file, header, clip.Frames, default);
            for (int i = 0; i < pending.Count; i++)
            {
                var block = blocks[firstBlock + i]; block.Offset = file.Position;
                using (var gzip = new GZipStream(file, CompressionLevel.Fastest, true)) gzip.Write(pending[i]);
                block.Length = file.Position - block.Offset;
            }
            pages.Add(new FullReplayPage { Index = old.Index, Start = old.Start, End = old.End, Offset = offset, Length = length,
                RawBytes = bytes, DecodedBytes = old.DecodedBytes, FrameCount = old.FrameCount,
                FirstFrameIndex = old.FirstFrameIndex, Overlap = old.Overlap, EffectHistory = history, SharedThrough = blocks.Count });
        }
        var index = new FullReplayIndex { Version = shared ? 3 : 2, Header = header, Duration = info.Duration, FrameCount = info.FrameCount, SharedBlocks = shared ? blocks.ToArray() : null,
            ActorIds = info.ActorIds, Complete = true, Reason = "test", Pages = pages.ToArray(),
            GapOver100ms = info.GapOver100ms, GapOver500ms = info.GapOver500ms, LargestGapSeconds = info.LargestGapSeconds };
        byte[] data = Encoding.UTF8.GetBytes(Json(index)); binary.Write(data); binary.Write((long)data.Length); binary.Write(Encoding.ASCII.GetBytes("PEAKEND1"));
        return path;
    }
    private static string Json(object? value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    private static void Check(bool value, string message = "Shared snapshot assertion failed.") { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception e) when (e is InvalidDataException || e is JsonException || e is IOException || e is ArgumentException) { return; } throw new Exception("Corrupt shared snapshot accepted."); }
    private static void InRoot(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "peak-shared-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); } finally { Directory.Delete(root, true); }
    }
    private sealed class ShortReadStream : Stream
    {
        private readonly MemoryStream input; private readonly int maximum;
        internal long BytesRead { get; private set; }
        internal ShortReadStream(byte[] bytes, int maximum) { input = new MemoryStream(bytes, false); this.maximum = maximum; }
        public override int Read(byte[] buffer, int offset, int count)
        { int read = input.Read(buffer, offset, Math.Min(count, maximum)); BytesRead += read; return read; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    }
}
