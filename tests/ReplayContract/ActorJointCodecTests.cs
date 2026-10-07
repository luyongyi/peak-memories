using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class ActorJointCodecTests
{
    public static void Run(Action<string, Action> test)
    {
        test("joint binary baseline preserves 256 paths flags scales and distinct sample times", () =>
        {
            var source = Joints(256);
            for (int i = 0; i < source.Length; i++)
            { source[i].SampleTime = i / 60d; source[i].Active = i % 2 == 0; source[i].Visible = i % 3 == 0; }
            Equal(source, ActorJointCodec.Decode(ActorJointCodec.Encode(source, null), null));
        });
        test("joint binary rotation roundtrip stays below 0.02 degrees for random and tied components", () =>
        {
            var random = new Random(113);
            for (int batch = 0; batch < 20; batch++)
            {
                var nodes = Joints(256);
                foreach (var node in nodes)
                {
                    var q = Enumerable.Range(0, 4).Select(_ => random.NextDouble() * 2 - 1).ToArray();
                    double norm = Math.Sqrt(q.Sum(x => x * x)); node.Rotation = q.Select(x => (float)(x / norm)).ToArray();
                }
                nodes[0].Rotation = new[] { .5f, .5f, .5f, .5f };
                nodes[1].Rotation = new[] { -.5f, .5f, -.5f, .5f };
                Equal(nodes, ActorJointCodec.Decode(ActorJointCodec.Encode(nodes, null), null));
            }
        });
        test("identity parent rotations remain exactly zero after smallest-three encoding", () =>
        {
            var decoded = ActorJointCodec.Decode(ActorJointCodec.Encode(Joints(128), null), null);
            foreach (var node in decoded) Check(node.Rotation.SequenceEqual(new[] { 0f, 0f, 0f, 1f }));
        });
        test("world-root full and delta rotations retain exact float bits and sub-grid changes", () =>
        {
            var source = Joints(1); source[0].Path = "."; source[0].Rotation = new[] { -0f, .0000001f, 0f, 1f };
            var decoded = ActorJointCodec.Decode(ActorJointCodec.Encode(source, null), null);
            Check(source[0].Rotation.Select(BitConverter.SingleToInt32Bits).SequenceEqual(decoded[0].Rotation.Select(BitConverter.SingleToInt32Bits)));
            var changed = Clone(source); changed[0].SampleTime = .1; changed[0].Rotation[1] = .0000002f;
            Check(!ActorJointCodec.Same(changed, source));
            var after = ActorJointCodec.Decode(ActorJointCodec.Encode(changed, source), decoded);
            Check(changed[0].Rotation.Select(BitConverter.SingleToInt32Bits).SequenceEqual(after[0].Rotation.Select(BitConverter.SingleToInt32Bits)));
            // A root-to-bone arm of 100 km must not amplify codec rotation error.
            var arm = new System.Numerics.Vector3(100000, 0, 0);
            System.Numerics.Quaternion Q(float[] q) => new(q[0], q[1], q[2], q[3]);
            Check(System.Numerics.Vector3.Transform(arm, Q(changed[0].Rotation)) == System.Numerics.Vector3.Transform(arm, Q(after[0].Rotation)));
        });
        test("root rotation full payload rejects zero nonfinite and truncated float quaternions", () =>
        {
            var source = Joints(1); source[0].Path = ".";
            byte[] payload = Convert.FromBase64String(ActorJointCodec.Encode(source, null));
            const int rotationStart = 36;
            var zero = (byte[])payload.Clone(); Array.Clear(zero, rotationStart, 16);
            Reject(() => ActorJointCodec.Decode(Convert.ToBase64String(zero), null));
            var nan = (byte[])payload.Clone(); Array.Copy(BitConverter.GetBytes(float.NaN), 0, nan, rotationStart, 4);
            Reject(() => ActorJointCodec.Decode(Convert.ToBase64String(nan), null));
            Reject(() => ActorJointCodec.Decode(Convert.ToBase64String(payload, 0, rotationStart + 15), null));
        });
        test("topology replacement switches correctly between packed child and exact root rotations", () =>
        {
            var child = Joints(1); child[0].Rotation = new[] { 0f, .1234567f, 0f, .9923493f };
            var root = Clone(child); root[0].Path = ".";
            var decodedChild = ActorJointCodec.Decode(ActorJointCodec.Encode(child, null), null);
            var decodedRoot = ActorJointCodec.Decode(ActorJointCodec.Encode(root, child), decodedChild);
            Check(root[0].Rotation.SequenceEqual(decodedRoot[0].Rotation));
            Equal(child, ActorJointCodec.Decode(ActorJointCodec.Encode(child, root), decodedRoot));
        });
        test("nonuniform parent scales retain float bits across full and tiny delta changes", () =>
        {
            var source = Joints(3); source[0].Scale = new[] { 1.000019f, .999973f, -0f };
            source[1].Scale = new[] { .0100003f, 1.232345f, -1.0000002f };
            var decoded = ActorJointCodec.Decode(ActorJointCodec.Encode(source, null), null);
            for (int i = 0; i < source.Length; i++)
                Check(source[i].Scale.Select(BitConverter.SingleToInt32Bits).SequenceEqual(decoded[i].Scale.Select(BitConverter.SingleToInt32Bits)));
            var changed = Clone(source); changed[0].Scale[0] += .0000002f; changed[0].SampleTime = .1;
            Check(!ActorJointCodec.Same(changed, source));
            var after = ActorJointCodec.Decode(ActorJointCodec.Encode(changed, source), decoded);
            Check(changed[0].Scale.Select(BitConverter.SingleToInt32Bits).SequenceEqual(after[0].Scale.Select(BitConverter.SingleToInt32Bits)));
            // The source rig can put its physical hip far from a parent.
            Check(changed[0].Scale[0] * 100000d == after[0].Scale[0] * 100000d);
        });
        test("joint binary coordinates escape narrow integers without clamping", () =>
        {
            var nodes = Joints(4);
            nodes[0].Position = new[] { -3.2768f, 3.2768f, 70.12345f };
            nodes[1].Position = new[] { -214748.3648f, 214748.3648f, 1e7f };
            nodes[2].Scale = new[] { 0f, -9.12345f, 1000000f };
            nodes[3].SampleTime = -.1;
            Equal(nodes, ActorJointCodec.Decode(ActorJointCodec.Encode(nodes, null), null));
        });
        test("stationary joint resampling is encoded even when all transform fields match", () =>
        {
            var writer = new ReplayDeltaCodec.Writer(); var reader = new ReplayDeltaCodec.Reader();
            var first = new ReplayFrame { Actors = new[] { new ActorFrame { Id = "a", JointPose = Joints(128) } } };
            var before = reader.Decode(Token(writer.Encode(first)));
            var next = new ReplayFrame { T = .1, Actors = new[] { new ActorFrame { Id = "a", JointPose = Joints(128) } } };
            for (int i = 0; i < 37; i++) next.Actors[0].JointPose[i].SampleTime = .1;
            var token = Token(writer.Encode(next)); var packed = token["Actors"]![0]!["JointPose"]!.Value<string>()!;
            Check(Convert.FromBase64String(packed).Length < 150);
            var after = reader.Decode(token);
            Check(before.Actors[0].JointPose[0].SampleTime == 0 && after.Actors[0].JointPose[0].SampleTime == .1);
            Check(ReferenceEquals(before.Actors[0].JointPose[37], after.Actors[0].JointPose[37]));
        });
        test("joint binary cadence retains separately timed 60 30 and 10 Hz samples", () =>
        {
            var prior = Joints(128); var decoded = ActorJointCodec.Decode(ActorJointCodec.Encode(prior, null), null);
            for (int frame = 1; frame < 60; frame++)
            {
                var current = Clone(prior);
                for (int i = 0; i < current.Length; i++)
                    if (i < 37 || i < 58 && frame % 2 == 0 || i >= 58 && frame % 6 == 0)
                    { current[i].SampleTime = frame / 60d; current[i].Position[0] += .003f; }
                decoded = ActorJointCodec.Decode(ActorJointCodec.Encode(current, prior), decoded);
                Equal(current, decoded); prior = current;
            }
        });
        test("joint quantization compares stored grid values and never accumulates slow-motion drift", () =>
        {
            var previous = Joints(1); var decoded = ActorJointCodec.Decode(ActorJointCodec.Encode(previous, null), null);
            for (int i = 1; i < 5000; i++)
            {
                var next = Joints(1); next[0].Position[0] = i * .000011f; next[0].SampleTime = i / 60d;
                decoded = ActorJointCodec.Decode(ActorJointCodec.Encode(next, previous), decoded);
                Check(Math.Abs(next[0].Position[0] - decoded[0].Position[0]) <= .000051);
                previous = next;
            }
            Check(decoded[0].Position[0] > .0549f);
        });
        test("joint sub-grid changes are omitted while crossing the next grid is preserved", () =>
        {
            var source = Joints(1); var tiny = Clone(source); tiny[0].Position[0] = .00001f;
            Check(ActorJointCodec.Same(source, tiny));
            tiny[0].Position[0] = .00006f; Check(!ActorJointCodec.Same(source, tiny));
        });
        test("joint payload rejects every truncated prefix and trailing bytes", () =>
        {
            byte[] full = Convert.FromBase64String(ActorJointCodec.Encode(Joints(3), null));
            for (int size = 0; size < full.Length; size++)
                Reject(() => ActorJointCodec.Decode(Convert.ToBase64String(full, 0, size), null));
            Reject(() => ActorJointCodec.Decode(Convert.ToBase64String(full.Concat(new byte[] { 0 }).ToArray()), null));
        });
        test("joint payload rejects invalid mode magic version counts masks paths and numeric state", () =>
        {
            var full = Convert.FromBase64String(ActorJointCodec.Encode(Joints(1), null));
            int entry = 25, position = entry + 4 + Encoding.UTF8.GetByteCount(Joints(1)[0].Path);
            foreach (Action<byte[]> mutate in new Action<byte[]>[] {
                bytes => bytes[0] = 0, bytes => bytes[1] = 0, bytes => bytes[2] = 2,
                bytes => { bytes[3] = 1; bytes[4] = 1; }, bytes => bytes[13] = 2,
                bytes => Array.Copy(BitConverter.GetBytes(double.NaN), 0, bytes, 15, 8),
                bytes => bytes[23] = 0, bytes => bytes[entry] = 1,
                bytes => bytes[entry + 1] = 0, bytes => bytes[entry + 1] = 64,
                bytes => bytes[entry + 2] = 0, bytes => bytes[entry + 4] = 0xff,
                bytes => bytes[position + 6 + 5] |= 0x80,
                bytes => bytes[^2] = 4, bytes => bytes[^1] = 1,
                bytes => bytes[5] ^= 1,
            })
            { var bytes = (byte[])full.Clone(); mutate(bytes); Reject(() => ActorJointCodec.Decode(Convert.ToBase64String(bytes), null)); }
            Reject(() => ActorJointCodec.Decode("%%%", null));
            Reject(() => ActorJointCodec.Decode(new string('A', ActorJointCodec.MaximumPayloadBytes * 2), null));
        });
        test("joint delta rejects missing or wrong same-size topology baselines", () =>
        {
            var before = Joints(2); var after = Clone(before); after[0].SampleTime = .1;
            string delta = ActorJointCodec.Encode(after, before);
            Reject(() => ActorJointCodec.Decode(delta, null));
            Reject(() => ActorJointCodec.Decode(delta, Joints(1)));
            var wrong = Clone(before); wrong[1].Path += "other";
            Reject(() => ActorJointCodec.Decode(delta, wrong));
        });
        test("joint deltas reject changed state at an equal or decreasing sample time", () =>
        {
            var before = Joints(1); before[0].SampleTime = .1;
            var next = Clone(before); next[0].Position[0] = 1;
            Reject(() => ActorJointCodec.Encode(next, before));
            next[0].SampleTime = .05; Reject(() => ActorJointCodec.Encode(next, before));
            next[0].SampleTime = .2;
            var valid = Convert.FromBase64String(ActorJointCodec.Encode(next, before));
            foreach (double time in new[] { .05, .1 })
            {
                var bytes = (byte[])valid.Clone(); Array.Copy(BitConverter.GetBytes(time), 0, bytes, 15, 8);
                Reject(() => ActorJointCodec.Decode(Convert.ToBase64String(bytes), before));
            }
            // Complete page baselines can begin with a prior independent sample.
            next[0].SampleTime = -.05;
            Equal(next, ActorJointCodec.Decode(ActorJointCodec.Encode(next, null), null));
        });
        test("joint fields reject legacy JSON arrays objects null and malformed binary", () =>
        {
            foreach (JToken value in new JToken[] { new JArray(), new JObject(), JValue.CreateNull(), new JValue(123), new JValue("bad") })
            {
                var frame = new ReplayFrame { Actors = new[] { new ActorFrame { Id = "a", JointPose = Joints(2) } } };
                var token = Token(new ReplayDeltaCodec.Writer().Encode(frame)); token["Actors"]![0]!["JointPose"] = value;
                Reject(() => new ReplayDeltaCodec.Reader().Decode(token));
            }
        });
        test("joint delta rejects duplicate unordered out-of-range indices and unknown masks", () =>
        {
            var before = Joints(2); var after = Clone(before);
            foreach (var node in after) node.SampleTime = .1;
            var payload = Convert.FromBase64String(ActorJointCodec.Encode(after, before));
            foreach (Action<byte[]> mutate in new Action<byte[]>[] {
                bytes => bytes[28] = 0, bytes => bytes[25] = 2,
                bytes => bytes[26] = 0, bytes => bytes[26] = 64,
                bytes => bytes[27] = 1,
            })
            { var bytes = (byte[])payload.Clone(); mutate(bytes); Reject(() => ActorJointCodec.Decode(Convert.ToBase64String(bytes), before)); }
        });
        test("joint binary topology changes replace complete poses and allow empty tracks", () =>
        {
            var before = Joints(3); var after = Joints(3); after[2].Path += "replacement";
            Equal(after, ActorJointCodec.Decode(ActorJointCodec.Encode(after, before), before));
            Check(ActorJointCodec.Decode(ActorJointCodec.Encode(Array.Empty<NodePose>(), after), after).Length == 0);
            Equal(before, ActorJointCodec.Decode(ActorJointCodec.Encode(before, Array.Empty<NodePose>()), Array.Empty<NodePose>()));
        });
        test("joint binary compressed moving stream is smaller than equivalent JSON node deltas", () =>
        {
            var binary = new StringBuilder(); var json = new StringBuilder(); NodePose[]? previous = null;
            for (int frame = 0; frame < 360; frame++)
            {
                var current = previous == null ? Joints(128) : Clone(previous);
                var changes = new List<object>();
                for (int i = 0; i < current.Length; i++)
                {
                    if (previous != null && !(i < 37 || i < 58 && frame % 2 == 0 || i >= 58 && frame % 6 == 0)) continue;
                    double t = frame / 60d; current[i].SampleTime = t;
                    current[i].Position = new[] { (float)Math.Sin(t * 1.1 + i) * .31f, (float)Math.Cos(t * .73 + i) * .25f, (float)Math.Sin(t * 2.3 + i * .1) * .1f };
                    current[i].Rotation = new[] { 0f, (float)Math.Sin(t * .61 + i), 0f, (float)Math.Cos(t * .61 + i) };
                    changes.Add(new object[] { i, 3, current[i].Position, current[i].Rotation });
                }
                binary.AppendLine(ActorJointCodec.Encode(current, previous));
                json.AppendLine(JsonConvert.SerializeObject(previous == null ? (object)current : new { Length = 128, Changes = changes }, ReplayFiles.Json));
                previous = current;
            }
            long binaryBytes = Compressed(binary.ToString()), jsonBytes = Compressed(json.ToString());
            Console.WriteLine($"Joint codec moving fixture: binary {binaryBytes:N0} B, JSON {jsonBytes:N0} B, ratio {(double)binaryBytes / jsonBytes:P1}.");
            Check(binaryBytes < jsonBytes * .8);
        });
    }

    internal static NodePose[] Joints(int count) => Enumerable.Range(0, count).Select(i => new NodePose
    { Path = $"0:Hip/{i:000}:Joint", Position = new[] { i * .03125f, i * .0625f, -i * .015625f }, Scale = new[] { 1f, 1.125f, .875f } }).ToArray();
    internal static NodePose[] Clone(NodePose[] nodes) => nodes.Select(n => new NodePose { Path = n.Path,
        Position = (float[])n.Position.Clone(), Rotation = (float[])n.Rotation.Clone(), Scale = (float[])n.Scale.Clone(),
        Active = n.Active, Visible = n.Visible, SampleTime = n.SampleTime }).ToArray();
    internal static void Equal(NodePose[] expected, NodePose[] actual, double timeOffset = 0)
    {
        Check(expected.Length == actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            var a = expected[i]; var b = actual[i];
            Check(a.Path == b.Path && a.Active == b.Active && a.Visible == b.Visible && Math.Abs(a.SampleTime - timeOffset - b.SampleTime) < 1e-8);
            for (int axis = 0; axis < 3; axis++)
            {
                Check(Math.Abs(a.Position[axis] - b.Position[axis]) <= Math.Max(.000051, Math.Abs(a.Position[axis]) * 1.2e-7));
                Check(Math.Abs(a.Scale[axis] - b.Scale[axis]) <= Math.Max(.000051, Math.Abs(a.Scale[axis]) * 1.2e-7));
            }
            double dot = 0, aa = 0, bb = 0;
            for (int axis = 0; axis < 4; axis++) { dot += a.Rotation[axis] * (double)b.Rotation[axis]; aa += a.Rotation[axis] * (double)a.Rotation[axis]; bb += b.Rotation[axis] * (double)b.Rotation[axis]; }
            double angle = 2 * Math.Acos(Math.Min(1, Math.Abs(dot) / Math.Sqrt(aa * bb))) * 180 / Math.PI;
            Check(angle < .02);
        }
    }
    private static long Compressed(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true))
        { byte[] bytes = Encoding.UTF8.GetBytes(text); gzip.Write(bytes, 0, bytes.Length); }
        return output.Length;
    }
    private static JObject Token(object value) => JObject.FromObject(value, JsonSerializer.Create(ReplayFiles.Json));
    internal static void Check(bool value) { if (!value) throw new Exception("Joint codec contract failed."); }
    internal static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is InvalidDataException || e is ArgumentException || e is JsonException) { return; }
        throw new Exception("Invalid joint codec payload was accepted.");
    }
}
