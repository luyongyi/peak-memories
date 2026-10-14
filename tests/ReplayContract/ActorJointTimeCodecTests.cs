using System.IO.Compression;
using System.Text;
using PeakReplayLab;

internal static class ActorJointTimeCodecTests
{
    private const int Legacy = 16, Grouped = 17;
    private static NodePose[] Joints(int count) => ActorJointCodecTests.Joints(count);
    private static NodePose[] Clone(NodePose[] nodes) => ActorJointCodecTests.Clone(nodes);
    private static void Check(bool value) => ActorJointCodecTests.Check(value);
    private static void Reject(Action action) => ActorJointCodecTests.Reject(action);

    public static void Run(Action<string, Action> test)
    {
        test("joint clock groups retain all 256 stationary observations and exact double bits", () =>
        {
            var before = Joints(256); var after = Clone(before);
            double time = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(.1) + 1);
            foreach (var node in after) node.SampleTime = time;
            string old = ActorJointCodec.Encode(after, before, Legacy), packed = ActorJointCodec.Encode(after, before, Grouped);
            byte[] bytes = Convert.FromBase64String(packed);
            Check(bytes[1] == 2 && bytes.Length == 60 && Convert.FromBase64String(old).Length == 793);
            var decoded = ActorJointCodec.Decode(packed, before, Grouped);
            EqualBits(ActorJointCodec.Decode(old, before, Legacy), decoded);
            for (int i = 0; i < decoded.Length; i++)
            {
                Check(Bits(decoded[i].SampleTime) == Bits(time));
                Check(!ReferenceEquals(decoded[i], before[i]));
                Check(ReferenceEquals(decoded[i].Position, before[i].Position));
                Check(ReferenceEquals(decoded[i].Rotation, before[i].Rotation));
                Check(ReferenceEquals(decoded[i].Scale, before[i].Scale));
            }
            // Every new clock exists even though every pose is stationary.
            Check(bytes[28] == 255 && bytes[^1] == 255);
        });
        test("joint clock format falls back to v1 for sparse wide unique unchanged and full updates", () =>
        {
            foreach (int count in new[] { 0, 1, 7, 8, 9, 128, 255, 256 })
            {
                var before = Joints(count);
                Check(ActorJointCodec.Encode(before, null, Grouped) == ActorJointCodec.Encode(before, null, Legacy));
                string unchanged = ActorJointCodec.Encode(before, before, Grouped);
                Check(unchanged == ActorJointCodec.Encode(before, before, Legacy));
                Check(ReferenceEquals(ActorJointCodec.Decode(unchanged, before, Grouped), before));
                if (count == 0) continue;
                var unique = Clone(before);
                for (int i = 0; i < count; i++) unique[i].SampleTime = (i + 1) / 1000d;
                // Wide bitmaps with single-member groups cannot save bytes.
                // Tiny rigs can still win by omitting one mask per node.
                if (count <= 2 || count >= 9)
                    Check(ActorJointCodec.Encode(unique, before, Grouped) == ActorJointCodec.Encode(unique, before, Legacy));
                EqualBits(ActorJointCodec.Decode(ActorJointCodec.Encode(unique, before, Legacy), before, Legacy),
                    ActorJointCodec.Decode(ActorJointCodec.Encode(unique, before, Grouped), before, Grouped));
                var sparse = Clone(before); sparse[^1].SampleTime = .1;
                Check(ActorJointCodec.Encode(sparse, before, Grouped) == ActorJointCodec.Encode(sparse, before, Legacy));
            }
        });
        test("joint clock multiple groups coexist with every pose field and retain untouched references", () =>
        {
            var before = Joints(256); before[0].Path = ".";
            before = ActorJointCodec.Decode(ActorJointCodec.Encode(before, null, Legacy), null, Legacy);
            var after = Clone(before);
            double[] times = { .10000000000000002, .25, .125, .10000000000000003 };
            for (int i = 0; i < 255; i++) after[i].SampleTime = times[i % times.Length];
            after[0].Rotation = new[] { -0f, .0000002f, 0f, 1f };
            after[1].Position = new[] { -.00006f, 214748.5f, 1e7f };
            after[2].Rotation = new[] { .5f, -.5f, .5f, -.5f };
            after[3].Scale = new[] { -.0f, 1.0000002f, -3.1234567f };
            after[4].Active = false; after[5].Visible = false;
            after[6].Position[0] += .1f; after[6].Rotation = new[] { 0f, .6f, 0f, .8f };
            after[6].Scale[0] += .0000003f; after[6].Active = false; after[6].Visible = false;
            string packed = ActorJointCodec.Encode(after, before, Grouped);
            Check(Convert.FromBase64String(packed)[1] == 2);
            var decoded = ActorJointCodec.Decode(packed, before, Grouped);
            EqualBits(ActorJointCodec.Decode(ActorJointCodec.Encode(after, before, Legacy), before, Legacy), decoded);
            Check(ReferenceEquals(before[255], decoded[255]));
            Check(ReferenceEquals(before[7].Position, decoded[7].Position));
        });
        test("joint grouped decoder supports all time IDs and node indices including zero and 255", () =>
        {
            var before = Joints(256);
            var times = Enumerable.Range(0, 256).Select(i => (i + 1) / 1000d).ToArray();
            var groups = Enumerable.Range(0, 256).Select(i => (Time: (byte)i, Indices: new[] { i })).ToArray();
            var decoded = ActorJointCodec.Decode(Payload(before, times, groups), before, Grouped);
            var expected = Clone(before);
            for (int i = 0; i < expected.Length; i++) expected[i].SampleTime = times[i];
            EqualBits(ActorJointCodec.Decode(ActorJointCodec.Encode(expected, before, Legacy), before, Legacy), decoded);
        });
        test("joint zero sample clocks retain the frozen v1 signed-zero and ordering semantics", () =>
        {
            double negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
            foreach (double firstZero in new[] { 0d, negativeZero })
            {
                var baseline = Joints(256);
                for (int i = 0; i < baseline.Length; i++)
                    baseline[i].SampleTime = i % 2 == 0 ? firstZero : Bits(firstZero) == 0 ? negativeZero : 0d;
                string fullV1 = ActorJointCodec.Encode(baseline, null, Legacy);
                Check(fullV1 == ActorJointCodec.Encode(baseline, null, Grouped));
                var full = ActorJointCodec.Decode(fullV1, null, Grouped);
                // Version 1 uses numeric double keys, so both zero signs share
                // the first table entry. Version 2 must preserve that existing
                // result rather than silently changing the observation model.
                Check(full.All(node => Bits(node.SampleTime) == Bits(firstZero)));
                var before = Joints(256); foreach (var node in before) node.SampleTime = -.1;
                string v1 = ActorJointCodec.Encode(baseline, before, Legacy);
                string v2 = ActorJointCodec.Encode(baseline, before, Grouped);
                Check(Convert.FromBase64String(v2)[1] == 2);
                var decoded = ActorJointCodec.Decode(v2, before, Grouped);
                EqualBits(ActorJointCodec.Decode(v1, before, Legacy), decoded);
                Check(decoded.All(node => Bits(node.SampleTime) == Bits(firstZero)));
            }
            var negative = Joints(128); foreach (var node in negative) node.SampleTime = negativeZero;
            var positive = Clone(negative); foreach (var node in positive) node.SampleTime = 0d;
            string unchanged = ActorJointCodec.Encode(positive, negative, Grouped);
            Check(unchanged == ActorJointCodec.Encode(positive, negative, Legacy));
            Check(ReferenceEquals(ActorJointCodec.Decode(unchanged, negative, Grouped), negative));
            positive[0].Position[0] += 1;
            Reject(() => ActorJointCodec.Encode(positive, negative, Legacy));
            Reject(() => ActorJointCodec.Encode(positive, negative, Grouped));
        });
        test("joint clock packing never increases raw bytes across densities groups and mixed pose masks", () =>
        {
            var random = new Random(7193);
            for (int trial = 0; trial < 500; trial++)
            {
                int count = random.Next(1, 257); var before = Joints(count); var after = Clone(before);
                int groupCount = random.Next(1, count + 1);
                for (int i = 0; i < count; i++)
                    if (random.Next(4) != 0)
                    {
                        after[i].SampleTime = (random.Next(groupCount) + 1) / 600d;
                        if (random.Next(7) == 0) after[i].Position[0] += .01f;
                    }
                string v1 = ActorJointCodec.Encode(after, before, Legacy), v2 = ActorJointCodec.Encode(after, before, Grouped);
                Check(Convert.FromBase64String(v2).Length <= Convert.FromBase64String(v1).Length);
                EqualBits(ActorJointCodec.Decode(v1, before, Legacy), ActorJointCodec.Decode(v2, before, Grouped));
            }
        });
        test("joint mixed 60 30 10 Hz stationary clocks keep identical timeline brackets after forward and reverse seeks", () =>
        {
            var legacyFrames = new List<ReplayFrame>(); var groupedFrames = new List<ReplayFrame>();
            var pages = new List<List<(string Legacy, string Grouped)>>();
            NodePose[]? source = null, legacy = null, grouped = null;
            long legacyBytes = 0, groupedBytes = 0; int groupedUpdates = 0;
            var legacyText = new StringBuilder(); var groupedText = new StringBuilder();
            for (int frame = 0; frame <= 180; frame++)
            {
                var next = source == null ? Joints(128) : Clone(source);
                for (int i = 0; i < next.Length; i++)
                    if (i < 37 || i < 58 && frame % 2 == 0 || i >= 58 && frame % 6 == 0)
                    {
                        next[i].SampleTime = frame / 60d;
                        // Most observations are stationary, some move below
                        // the quantization grid, and some animate visibly.
                        if (i % 17 == 0) next[i].Position[0] += .000011f;
                        if (i % 19 == 0) next[i].Position[1] += .003f;
                    }
                bool baseline = frame % 30 == 0;
                if (baseline) pages.Add(new List<(string, string)>());
                string v1 = ActorJointCodec.Encode(next, baseline ? null : source, Legacy);
                string v2 = ActorJointCodec.Encode(next, baseline ? null : source, Grouped);
                legacyBytes += Convert.FromBase64String(v1).Length; groupedBytes += Convert.FromBase64String(v2).Length;
                if (Convert.FromBase64String(v2)[1] == 2) groupedUpdates++;
                legacyText.AppendLine(v1); groupedText.AppendLine(v2); pages[^1].Add((v1, v2));
                legacy = ActorJointCodec.Decode(v1, baseline ? null : legacy, Legacy);
                grouped = ActorJointCodec.Decode(v2, baseline ? null : grouped, Grouped);
                EqualBits(legacy, grouped);
                legacyFrames.Add(Frame(frame / 60d, legacy)); groupedFrames.Add(Frame(frame / 60d, grouped)); source = next;
            }
            Check(groupedUpdates > 100 && groupedBytes < legacyBytes);
            var oldTimeline = new ActorJointTimeline(legacyFrames, true);
            var newTimeline = new ActorJointTimeline(groupedFrames, true);
            var queries = Enumerable.Range(0, 360).Select(i => i / 120d).Concat(new[] { 2.91, .01, 1.5, .5, 2.999, .015 }).ToArray();
            foreach (double t in queries.Concat(queries.AsEnumerable().Reverse()))
            {
                Check(oldTimeline.TryActor("actor", t, out var a)); Check(newTimeline.TryActor("actor", t, out var b));
                Check(a.Count == b.Count);
                for (int i = 0; i < a.Count; i++)
                {
                    var x = a.At(i); var y = b.At(i);
                    Check(Bits(x.LeftTime) == Bits(y.LeftTime) && Bits(x.RightTime) == Bits(y.RightTime));
                    Check(BitConverter.SingleToInt32Bits(x.Mix) == BitConverter.SingleToInt32Bits(y.Mix));
                    EqualNodeBits(x.Left, y.Left); EqualNodeBits(x.Right, y.Right);
                }
            }
            // Independent page baselines reconstruct identically even when
            // pages are opened repeatedly and in reverse order.
            foreach (int pageIndex in Enumerable.Range(0, pages.Count).Reverse().Concat(new[] { 0, 5, 1, 6, 2 }))
            {
                NodePose[]? state = null;
                for (int offset = 0; offset < pages[pageIndex].Count; offset++)
                {
                    state = ActorJointCodec.Decode(pages[pageIndex][offset].Grouped, state, Grouped);
                    EqualBits(groupedFrames[pageIndex * 30 + offset].Actors[0].JointPose, state);
                }
            }
            Console.WriteLine($"Joint clock fixture: v1 {legacyBytes:N0} B, grouped {groupedBytes:N0} B; gzip v1 {Compressed(legacyText.ToString()):N0} B, grouped {Compressed(groupedText.ToString()):N0} B. All clocks/poses/brackets identical.");
        });
        test("joint clock topology changes and empty tracks remain independent v1 baselines", () =>
        {
            var before = Joints(128); var after = Clone(before);
            foreach (var node in after) node.SampleTime = -.25;
            after[^1].Path += "replacement";
            string replacement = ActorJointCodec.Encode(after, before, Grouped);
            Check(Convert.FromBase64String(replacement)[1] == 1);
            EqualBits(ActorJointCodec.Decode(ActorJointCodec.Encode(after, before, Legacy), before, Legacy), ActorJointCodec.Decode(replacement, before, Grouped));
            var empty = Array.Empty<NodePose>();
            Check(ActorJointCodec.Decode(ActorJointCodec.Encode(empty, before, Grouped), before, Grouped).Length == 0);
            string grouped = Stationary(before, .1);
            Reject(() => ActorJointCodec.Decode(grouped, after, Grouped));
            Reject(() => ActorJointCodec.Decode(grouped, null, Grouped));
            Reject(() => ActorJointCodec.Decode(grouped, Joints(127), Grouped));
        });
        test("joint clock deltas reject equal decreasing nonfinite and per-node stale sample clocks", () =>
        {
            var before = Joints(128); foreach (var node in before) node.SampleTime = .2;
            foreach (double time in new[] { .1, .2, double.NaN, double.PositiveInfinity, 1000000001d })
            {
                var next = Clone(before); foreach (var node in next) { node.SampleTime = time; node.Position[0] += 1; }
                Reject(() => ActorJointCodec.Encode(next, before, Grouped));
                Reject(() => ActorJointCodec.Decode(Payload(before, new[] { time }, new[] { ((byte)0, new[] { 0, 127 }) }), before, Grouped));
            }
            var valid = Convert.FromBase64String(Stationary(before, .3));
            Array.Copy(BitConverter.GetBytes(.2), 0, valid, 15, 8);
            Reject(() => ActorJointCodec.Decode(Convert.ToBase64String(valid), before, Grouped));
            var heterogeneous = Clone(before); heterogeneous[127].SampleTime = .5;
            Reject(() => ActorJointCodec.Decode(Payload(heterogeneous, new[] { .3 }, new[] { ((byte)0, new[] { 0, 127 }) }), heterogeneous, Grouped));
        });
        test("joint grouped payload rejects every truncated prefix trailing bytes old schemas and full mode", () =>
        {
            var before = Joints(33); byte[] bytes = Convert.FromBase64String(Stationary(before, .1));
            Check(bytes[1] == 2);
            for (int length = 0; length < bytes.Length; length++)
                Reject(() => ActorJointCodec.Decode(Convert.ToBase64String(bytes, 0, length), before, Grouped));
            Reject(() => ActorJointCodec.Decode(Convert.ToBase64String(bytes.Concat(new byte[] { 0 }).ToArray()), before, Grouped));
            Reject(() => ActorJointCodec.Decode(Convert.ToBase64String(bytes), before, Legacy));
            foreach (Action<byte[]> mutate in new Action<byte[]>[] {
                b => b[0] = 0, b => b[1] = 3, b => b[2] = 0, b => b[2] = 2,
                b => { b[3] = 1; b[4] = 1; }, b => b[5] ^= 1,
                b => b[13] = 34, b => b[23] = 34, b => b[25] = 0, b => b[25] = 2,
                b => b[27] = 1, b => b[^1] |= 2,
            })
            { var invalid = (byte[])bytes.Clone(); mutate(invalid); Reject(() => ActorJointCodec.Decode(Convert.ToBase64String(invalid), before, Grouped)); }
        });
        test("joint grouped payload rejects duplicate empty unused unordered overlapping and invalid pose entries", () =>
        {
            var before = Joints(33);
            void Bad(double[] times, (byte Time, int[] Indices)[] groups, params byte[][] poses)
                => Reject(() => ActorJointCodec.Decode(Payload(before, times, groups, poses), before, Grouped));
            Bad(new[] { .1 }, Array.Empty<(byte, int[])>());
            Bad(new[] { .1 }, new[] { ((byte)0, Array.Empty<int>()) });
            Bad(new[] { .1, .2 }, new[] { ((byte)0, new[] { 0 }), ((byte)0, new[] { 1 }) });
            Bad(new[] { .1, .2 }, new[] { ((byte)1, new[] { 0 }), ((byte)0, new[] { 1 }) });
            Bad(new[] { .1, .2 }, new[] { ((byte)0, new[] { 0 }), ((byte)1, new[] { 0, 32 }) });
            Bad(new[] { .1, .2 }, new[] { ((byte)0, new[] { 0 }) });
            Bad(new[] { .1, .1 }, new[] { ((byte)0, new[] { 0 }), ((byte)1, new[] { 1 }) });
            Bad(new[] { .1 }, new[] { ((byte)1, new[] { 0 }) });
            // Pose entry: index zero, position+clock, three Int16s, time ID.
            byte[] pose = { 0, 33, 1, 0, 0, 0, 0, 0, 0 };
            Bad(new[] { .1 }, new[] { ((byte)0, new[] { 0, 32 }) }, pose);
            Bad(new[] { .1 }, new[] { ((byte)0, new[] { 32 }) }, pose, pose);
            foreach (byte mask in new byte[] { 0, 1, 32, 64 })
            { var invalid = (byte[])pose.Clone(); invalid[1] = mask; Bad(new[] { .1 }, new[] { ((byte)0, new[] { 32 }) }, invalid); }
            var outside = (byte[])pose.Clone(); outside[0] = 33;
            Bad(new[] { .1 }, new[] { ((byte)0, new[] { 32 }) }, outside);
            var badTime = (byte[])pose.Clone(); badTime[^1] = 1;
            Bad(new[] { .1 }, new[] { ((byte)0, new[] { 32 }) }, badTime);
        });
    }

    private static ReplayFrame Frame(double time, NodePose[] nodes) => new()
    { T = time, Actors = new[] { new ActorFrame { Id = "actor", JointPose = nodes } } };
    private static string Stationary(NodePose[] before, double time)
    {
        var after = Clone(before); foreach (var node in after) node.SampleTime = time;
        return ActorJointCodec.Encode(after, before, Grouped);
    }
    private static string Payload(NodePose[] before, double[] times, (byte Time, int[] Indices)[] groups, params byte[][] poses)
    {
        byte[] header = Convert.FromBase64String(ActorJointCodec.Encode(before, null, Legacy));
        header[1] = 2; header[2] = 1;
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
        {
            writer.Write(header, 0, 13); writer.Write((ushort)times.Length);
            foreach (double time in times) writer.Write(time);
            writer.Write((ushort)poses.Length); foreach (var pose in poses) writer.Write(pose);
            writer.Write((ushort)groups.Length);
            foreach (var group in groups)
            {
                writer.Write(group.Time); var bits = new byte[(before.Length + 7) / 8];
                foreach (int index in group.Indices) bits[index / 8] |= (byte)(1 << (index % 8));
                writer.Write(bits);
            }
        }
        return Convert.ToBase64String(output.ToArray());
    }
    private static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);
    private static void EqualBits(NodePose[] expected, NodePose[] actual)
    {
        Check(expected.Length == actual.Length);
        for (int i = 0; i < expected.Length; i++) EqualNodeBits(expected[i], actual[i]);
    }
    private static void EqualNodeBits(NodePose a, NodePose b)
    {
        Check(a.Path == b.Path && a.Active == b.Active && a.Visible == b.Visible && Bits(a.SampleTime) == Bits(b.SampleTime));
        Check(a.Position.Select(BitConverter.SingleToInt32Bits).SequenceEqual(b.Position.Select(BitConverter.SingleToInt32Bits)));
        Check(a.Rotation.Select(BitConverter.SingleToInt32Bits).SequenceEqual(b.Rotation.Select(BitConverter.SingleToInt32Bits)));
        Check(a.Scale.Select(BitConverter.SingleToInt32Bits).SequenceEqual(b.Scale.Select(BitConverter.SingleToInt32Bits)));
    }
    private static long Compressed(string value)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true))
        { byte[] bytes = Encoding.UTF8.GetBytes(value); gzip.Write(bytes, 0, bytes.Length); }
        return output.Length;
    }
}
