using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class ActorJointReplayTests
{
    public static void Run(Action<string, Action> test)
    {
        test("schema 10 records complete joints for empty hands and limp actors", () =>
        {
            foreach (bool limp in new[] { false, true })
            {
                var frame = Frame(0, 32); frame.Actors[0].Limp = limp;
                Check(frame.Actors[0].Inventory.Length == 0);
                ReplayRules.Validate(frame, -1);
                var token = Token(new ReplayDeltaCodec.Writer(10).Encode(frame));
                var read = new ReplayDeltaCodec.Reader(10).Decode(token);
                EqualJoints(frame.Actors[0].JointPose, read.Actors[0].JointPose);
                Check(read.Actors[0].Limp == limp);
            }
        });
        test("joint node capacity includes far more than the six legacy grip anchors", () =>
        {
            ActorJointReplayRules.Validate(Joints(ActorJointReplayRules.MaximumJoints));
            Reject(() => ActorJointReplayRules.Validate(Joints(ActorJointReplayRules.MaximumJoints + 1)));
            Reject(() => ActorJointReplayRules.Validate(null!));
        });
        test("joint validation rejects duplicate or missing paths inside complete actor frames", () =>
        {
            foreach (string fault in new[] { "duplicate", "empty", "null", "node-null", "path-limit", "unordered" })
            {
                var frame = Frame(0, 2);
                if (fault == "duplicate") frame.Actors[0].JointPose[1].Path = frame.Actors[0].JointPose[0].Path;
                if (fault == "empty") frame.Actors[0].JointPose[0].Path = "";
                if (fault == "null") frame.Actors[0].JointPose[0].Path = null!;
                if (fault == "node-null") frame.Actors[0].JointPose[0] = null!;
                if (fault == "path-limit") frame.Actors[0].JointPose[0].Path = new string('x', ActorJointReplayRules.MaximumPathLength + 1);
                if (fault == "unordered") Array.Reverse(frame.Actors[0].JointPose);
                Reject(() => ReplayRules.Validate(frame, -1));
            }
        });
        test("joint validation rejects malformed positions rotations and scales", () =>
        {
            foreach (string fault in new[] { "position-shape", "position-nan", "zero-rotation", "rotation-infinity", "scale-shape", "scale-infinity" })
            {
                var frame = Frame(0, 2); var node = frame.Actors[0].JointPose[0];
                if (fault == "position-shape") node.Position = new float[2];
                if (fault == "position-nan") node.Position[0] = float.NaN;
                if (fault == "zero-rotation") node.Rotation = new float[4];
                if (fault == "rotation-infinity") node.Rotation[3] = float.PositiveInfinity;
                if (fault == "scale-shape") node.Scale = new float[4];
                if (fault == "scale-infinity") node.Scale[0] = float.PositiveInfinity;
                Reject(() => ReplayRules.Validate(frame, -1));
            }
        });
        test("schema 10 requires joints in every new actor baseline even when the pose is empty", () =>
        {
            var token = Token(new ReplayDeltaCodec.Writer(10).Encode(Frame(0, 0)));
            ((JObject)token["Actors"]![0]!).Remove("JointPose");
            Reject(() => new ReplayDeltaCodec.Reader(10).Decode(token));
        });
        test("schema 10 unchanged joints are shared across changed actor positions", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(10); var reader = new ReplayDeltaCodec.Reader(10);
            var original = Frame(0, 16); var first = reader.Decode(Token(writer.Encode(original)));
            var next = Copy(original); next.T = .02; next.Actors[0].Position[0] = 2;
            var patch = Token(writer.Encode(next));
            Check(patch["Actors"]![0]!["JointPose"] == null);
            var second = reader.Decode(patch);
            Check(ReferenceEquals(first.Actors[0].JointPose, second.Actors[0].JointPose));
            Check(first.Actors[0].Position[0] == 0 && second.Actors[0].Position[0] == 2);
        });
        test("schema 10 joint changes stay within quantization bounds without mutating prior seek points", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(10); var reader = new ReplayDeltaCodec.Reader(10);
            var original = Frame(0, 16); var first = reader.Decode(Token(writer.Encode(original)));
            var next = Copy(original); next.T = .02; var joint = next.Actors[0].JointPose[5]; joint.SampleTime = .02;
            joint.Position = new[] { -.31f, .125f, 4.75f };
            joint.Rotation = new[] { 0f, 1f, 0f, 0f }; joint.Scale = new[] { 1.125f, .875f, 2f };
            joint.Active = false; joint.Visible = false;
            var patch = Token(writer.Encode(next)); Check(patch["Actors"]![0]!["JointPose"] != null);
            var second = reader.Decode(patch);
            EqualJoints(original.Actors[0].JointPose, first.Actors[0].JointPose);
            EqualJoints(next.Actors[0].JointPose, second.Actors[0].JointPose);
            Check(first.Actors[0].JointPose[5].Active && !second.Actors[0].JointPose[5].Active);
        });
        test("one changed joint stores a small delta and shares unaffected joints", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(10); var reader = new ReplayDeltaCodec.Reader(10);
            var source = Frame(0, 64); var first = reader.Decode(Token(writer.Encode(source)));
            var next = Copy(source); next.T = .02; next.Actors[0].JointPose[35].Position[0] += .125f; next.Actors[0].JointPose[35].SampleTime = .02;
            var token = Token(writer.Encode(next)); var patch = token["Actors"]![0]!["JointPose"]!;
            Check(patch.Type == JTokenType.String && patch.Value<string>()!.Length < 100);
            Check(patch.ToString(Formatting.None).Length < JToken.FromObject(next.Actors[0].JointPose).ToString(Formatting.None).Length / 4);
            var second = reader.Decode(token); EqualJoints(next.Actors[0].JointPose, second.Actors[0].JointPose);
            Check(ReferenceEquals(first.Actors[0].JointPose[0], second.Actors[0].JointPose[0]));
            Check(!ReferenceEquals(first.Actors[0].JointPose[35], second.Actors[0].JointPose[35]));
        });
        test("schema 10 full joint replacements preserve identity when topology changes at the same size", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(10); var reader = new ReplayDeltaCodec.Reader(10);
            var original = Frame(0, 4); var first = reader.Decode(Token(writer.Encode(original)));
            var changed = Frame(.02, 4); changed.Actors[0].JointPose[3].Path = "0:Hip/999:Replacement";
            var second = reader.Decode(Token(writer.Encode(changed)));
            Check(!ActorJointReplayRules.SameTopology(first.Actors[0].JointPose, second.Actors[0].JointPose));
            EqualJoints(changed.Actors[0].JointPose, second.Actors[0].JointPose);
            Check(first.Actors[0].JointPose[3].Path == "0:Hip/003:Joint");
        });
        test("schema 10 explicit empty joint poses clear and later restore a complete pose", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(10); var reader = new ReplayDeltaCodec.Reader(10);
            var first = reader.Decode(Token(writer.Encode(Frame(0, 12))));
            var empty = Token(writer.Encode(Frame(.02, 0)));
            Check(empty["Actors"]![0]!["JointPose"]!.Type == JTokenType.String);
            var middle = reader.Decode(empty); var last = reader.Decode(Token(writer.Encode(Frame(.04, 12))));
            Check(first.Actors[0].JointPose.Length == 12 && middle.Actors[0].JointPose.Length == 0 && last.Actors[0].JointPose.Length == 12);
            EqualJoints(first.Actors[0].JointPose, last.Actors[0].JointPose, -.04);
        });
        test("schema 10 actor reappearance replaces vanished joints with its own complete baseline", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(10); var reader = new ReplayDeltaCodec.Reader(10);
            var first = reader.Decode(Token(writer.Encode(Frame(0, 12))));
            var absent = reader.Decode(Token(writer.Encode(new ReplayFrame { T = .02 })));
            var restored = reader.Decode(Token(writer.Encode(Frame(.04, 20, 3))));
            Check(absent.Actors.Length == 0 && first.Actors[0].JointPose.Length == 12);
            EqualJoints(Joints(20, 3, .04), restored.Actors[0].JointPose);
        });
        test("full actor poses cannot silently downgrade to legacy replay schemas", () => InTemp(root =>
        {
            var clip = Clip(Frame(0, 2), Frame(.1, 2));
            for (int schema = 2; schema <= 9; schema++)
            {
                clip.Header.Schema = schema;
                Reject(() => ReplayArchive.Save(Path.Combine(root, schema.ToString()), clip));
                if (schema >= 5) Reject(() => new ReplayDeltaCodec.Writer(schema).Encode(clip.Frames[0]));
            }
        }));
        test("same topology depends on stable joint paths rather than poses or object references", () =>
        {
            var left = Joints(12); var right = Copy(left);
            right[3].Position[0] += 1; right[3].Rotation = new[] { 1f, 0f, 0f, 0f };
            Check(ActorJointReplayRules.SameTopology(left, right));
            right[3].Path += "-replaced"; Check(!ActorJointReplayRules.SameTopology(left, right));
            Check(!ActorJointReplayRules.SameTopology(left, Joints(11)));
            Check(!ActorJointReplayRules.SameTopology(left, Array.Empty<NodePose>()));
            Check(!ActorJointReplayRules.SameTopology(Array.Empty<NodePose>(), left));
        });
        test("topology order changes never interpolate a hand into another joint", () =>
        {
            var left = Joints(2); var swapped = new[] { Copy(left[1]), Copy(left[0]) };
            Check(!ActorJointReplayRules.SameTopology(left, swapped));
            var clip = Clip(Frame(0, 2), Frame(.1, 1));
            Check(clip.At(.099).Left.Actors[0].JointPose.Length == 2);
            Check(clip.At(.1).Left.Actors[0].JointPose.Length == 1);
            clip.At(.1); Check(clip.At(.05).Left.Actors[0].JointPose.Length == 2);
        });
        test("warm full joint validation and topology checks allocate no per-frame working memory", () =>
        {
            var left = Joints(ActorJointReplayRules.MaximumJoints); var right = Copy(left);
            for (int i = 0; i < 100; i++) { ActorJointReplayRules.Validate(left); ActorJointReplayRules.SameTopology(left, right); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) { ActorJointReplayRules.Validate(left); ActorJointReplayRules.SameTopology(left, right); }
            Check(GC.GetAllocatedBytesForCurrentThread() == before);
        });
        test("schema 10 compressed archive preserves joint removal restoration and backwards seek", () => InTemp(root =>
        {
            var frames = new[] { Frame(0, 16), Frame(.02, 0), Frame(.04, 24), Frame(.06, 24) };
            frames[3].Actors[0].JointPose[7].Position[2] = -.875f;
            var read = ReplayFiles.Read(ReplayArchive.Save(root, Clip(frames)));
            Check(read.Complete && read.Header.Schema == 10);
            for (int i = 0; i < frames.Length; i++) EqualJoints(frames[i].Actors[0].JointPose, read.Frames[i].Actors[0].JointPose);
            Check(read.At(.06).Left.Actors[0].JointPose[7].Position[2] == -.875f);
            Check(read.At(.02).Left.Actors[0].JointPose.Length == 0 && read.At(0).Left.Actors[0].JointPose.Length == 16);
        }));
        test("joint data contributes to rolling memory limits and is released after eviction", () =>
        {
            var empty = Frame(0, 0); var full = Frame(0, 64);
            long emptyBytes = RollingBuffer.Estimate(empty), jointBytes = ActorJointReplayRules.Estimate(full.Actors[0].JointPose);
            Check(jointBytes >= 64 * 10 * sizeof(float));
            Check(RollingBuffer.Estimate(full) - emptyBytes >= jointBytes - ActorJointReplayRules.Estimate(empty.Actors[0].JointPose));
            const long budget = 128 * 1024; var buffer = new RollingBuffer(budget);
            for (int i = 0; i < 120; i++) buffer.Add(Frame(i / 60d, 64));
            Check(buffer.MemoryLimited && buffer.Count >= 2 && buffer.Count < 120 && buffer.EstimatedBytes <= budget);
            var captured = buffer.Snapshot(new ReplayHeader { Scene = "Level_JointTests" }, DateTime.UtcNow);
            Check(captured.Frames.All(f => f.Actors[0].JointPose.Length == 64));
            for (int i = 0; i < 123; i++) buffer.Add(new ReplayFrame { T = 200 + i });
            Check(buffer.EstimatedBytes == buffer.Count * RollingBuffer.Estimate(new ReplayFrame()));
            Check(captured.Frames[0].Actors[0].JointPose.Length == 64);
        });
        test("compressed joint records cannot exceed the decoded replay memory budget", () => InTemp(root =>
        {
            string file = ReplayArchive.Save(root, Clip(Frame(0, 64), Frame(.1, 64, 1)));
            using var text = ReplayArchive.OpenText(file);
            Reject(() => ReplayFiles.Read(text, decodedByteLimit: 4096));
        }));
        test("current full replay page baselines retain rebased joints when pages are read backwards", () => InTemp(root =>
        {
            var source = Enumerable.Range(0, 13).Select(i => Frame(500 + i * .1, i == 6 ? 0 : 32, i)).ToArray();
            var writer = new FullReplayWriter(root, new ReplayHeader { Scene = "Level_JointTests" },
                new ContinuousReplayOptions { SegmentSeconds = .4 });
            foreach (var frame in source) Check(writer.TryEnqueue(frame));
            var completed = Await(writer.CompleteAsync()); Check(completed.Status == "completed" && completed.WrittenFrames == source.Length);
            var info = FullReplayArchive.ReadInfo(completed.FilePath);
            Check(info.Header.Schema == ReplayRules.CurrentSchema && info.Pages.Length >= 3); var seen = new HashSet<int>();
            foreach (int index in Enumerable.Range(0, info.Pages.Length).Reverse())
            {
                var page = FullReplayArchive.ReadPage(completed.FilePath, info, index); Check(page.Header.Schema == ReplayRules.CurrentSchema && page.Complete);
                foreach (var frame in page.Frames)
                {
                    int sample = (int)Math.Round(frame.T / .1); EqualJoints(source[sample].Actors[0].JointPose, frame.Actors[0].JointPose, 500);
                    seen.Add(sample);
                }
            }
            Check(seen.Count == source.Length);
        }));
        test("current continuous segments each restore joints from their own complete baseline", () => InTemp(root =>
        {
            var source = Enumerable.Range(0, 10).Select(i => Frame(500 + i * .1, 12, i)).ToArray();
            var writer = new ContinuousReplayWriter(root, new ReplayHeader { Scene = "Level_JointTests" },
                new ContinuousReplayOptions { SegmentFrameLimit = 4 });
            foreach (var frame in source) Check(writer.TryEnqueue(frame));
            var done = Await(writer.CompleteAsync()); Check(done.Status == "completed" && done.CompletedSegments == 3);
            var records = File.ReadLines(Path.Combine(writer.RunDirectory, "manifest.ndjson")).Select(JObject.Parse)
                .Where(r => (string?)r["Type"] == "segment-complete").Reverse(); var seen = new HashSet<int>();
            foreach (var record in records)
            {
                double start = (double)record["NativeStart"]!;
                var clip = ReplayFiles.Read(Path.Combine(writer.RunDirectory, (string)record["File"]!));
                Check(clip.Header.Schema == ReplayRules.CurrentSchema && clip.Complete);
                foreach (var frame in clip.Frames)
                {
                    int sample = (int)Math.Round((frame.T + start - 500) / .1);
                    EqualJoints(source[sample].Actors[0].JointPose, frame.Actors[0].JointPose, start); seen.Add(sample);
                }
            }
            Check(seen.Count == source.Length);
        }));
    }

    private static NodePose[] Joints(int count, float offset = 0, double time = 0) => Enumerable.Range(0, count).Select(i => new NodePose
    {
        SampleTime = time, Path = $"0:Hip/{i:000}:Joint", Position = new[] { offset + i * .03125f, i * .0625f, -i * .015625f },
        Rotation = i % 2 == 0 ? new[] { 0f, 0f, 0f, 1f } : new[] { 0f, 0f, 1f, 0f },
        Scale = new[] { 1f, 1.125f, .875f },
    }).ToArray();
    private static ReplayFrame Frame(double time, int joints, float offset = 0) => new()
    { T = time, Actors = new[] { new ActorFrame { Id = "synthetic-actor", Name = "Synthetic", JointPose = Joints(joints, offset, time) } } };
    private static ReplayClip Clip(params ReplayFrame[] frames)
    {
        var clip = new ReplayClip { Header = new ReplayHeader { Schema = 10, Scene = "Level_JointTests", FrameCount = frames.Length, Duration = frames[^1].T } };
        clip.Frames.AddRange(frames); return clip;
    }
    private static JObject Token(object value) => JObject.FromObject(value, JsonSerializer.Create(ReplayFiles.Json));
    private static T Copy<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value, ReplayFiles.Json), ReplayFiles.Json)!;
    private static void EqualJoints(NodePose[] expected, NodePose[] actual, double timeOffset = 0) =>
        ActorJointCodecTests.Equal(expected, actual, timeOffset);
    private static void Check(bool value) { if (!value) throw new Exception("Actor joint replay contract failed."); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is InvalidDataException || e is JsonException || e is ArgumentException) { return; }
        throw new Exception("Invalid actor joint replay was accepted.");
    }
    private static T Await<T>(Task<T> task)
    { Check(task.Wait(TimeSpan.FromSeconds(20))); return task.GetAwaiter().GetResult(); }
    private static void InTemp(Action<string> action)
    {
        string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string directory = Path.GetFullPath(Path.Combine(tempRoot, "PeakActorJointTests-" + Guid.NewGuid().ToString("N")));
        Check(directory.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)); Directory.CreateDirectory(directory);
        try { action(directory); } finally { Directory.Delete(directory, true); }
    }

}
