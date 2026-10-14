using System.Reflection;
using PeakReplayLab;

internal static class ReplayQueueTypedBudgetTests
{
    public static void Run(Action<string, Action> test)
    {
        test("typed queue edges exactly match independent reflection for complete shared graphs", () =>
        {
            var first = Fixture(0); var second = Fixture(1, first.Creatures[0]);
            var budget = new ReplayQueueBudget();
            Check(budget.TryReserve(first, long.MaxValue, 8, out var a, out _));
            Check(budget.Current.Bytes == ReflectiveGraphBytes(first));
            Check(budget.TryReserve(second, long.MaxValue, 8, out var b, out _));
            Check(budget.Current.Bytes == ReflectiveGraphBytes(first, second));
            a!.Dispose(); Check(budget.Current.Bytes == ReflectiveGraphBytes(second));
            b!.Dispose(); Check(budget.Current.Bytes == ReflectiveGraphBytes(second));
            budget.ClearBaseline(); Check(budget.Current.Bytes == 0 && budget.Current.Frames == 0);
        });
        test("typed queue edges preserve every nullable reference combination and scalar property overhead", () =>
        {
            // Queue accounting precedes serialization validation. Even malformed
            // null fields must retain the previous estimator's exact semantics.
            for (int mask = 0; mask < 16; mask++)
            {
                var node = new NodePose { Path = (mask & 1) != 0 ? "node" : null!, Position = (mask & 2) != 0 ? new float[3] : null!,
                    Rotation = (mask & 4) != 0 ? new float[4] : null!, Scale = (mask & 8) != 0 ? new float[3] : null!, SampleTime = 12 };
                VerifyOne(new ReplayFrame { Creatures = new[] { new CreatureReplayFrame { Pose = new ObjectPose { Nodes = new[] { node } } } } });
                var pose = new ObjectPose { Position = (mask & 1) != 0 ? new float[3] : null!, Rotation = (mask & 2) != 0 ? new float[4] : null!,
                    Scale = (mask & 4) != 0 ? new float[3] : null!, Nodes = (mask & 8) != 0 ? new[] { new NodePose() } : null! };
                VerifyOne(new ReplayFrame { Creatures = new[] { new CreatureReplayFrame { Pose = pose } } });
            }
            for (int mask = 0; mask < 256; mask++)
            {
                var creature = new CreatureReplayFrame { Key = (mask & 1) != 0 ? "key" : null!, Kind = (mask & 2) != 0 ? "spider" : null!,
                    Resource = (mask & 4) != 0 ? "spider" : null!, SourcePath = (mask & 8) != 0 ? "path" : null!,
                    Pose = (mask & 16) != 0 ? new ObjectPose() : null!, Line = (mask & 32) != 0 ? new CreatureReplayLine() : null,
                    Visuals = (mask & 64) != 0 ? new[] { new NativeRendererFrame() } : null,
                    IdleAnimation = (mask & 128) != 0 ? new CreatureIdleAnimation { Clip = "spider-idle-v1", AnchorTime = 4, ClipTime = .2, Speed = .33f, RopePhase = .7f } : null };
                VerifyOne(new ReplayFrame { Creatures = new[] { creature } });
            }
        });
        test("typed queue byte rejection rolls back partial graphs without releasing live leases", () =>
        {
            var first = Fixture(0); var second = Fixture(1);
            long firstBytes = ReflectiveGraphBytes(first), unionBytes = ReflectiveGraphBytes(first, second);
            foreach (long limit in new[] { firstBytes + 1, firstBytes + 127, (firstBytes + unionBytes) / 2, unionBytes - 1 })
            {
                var budget = new ReplayQueueBudget();
                Check(budget.TryReserve(first, firstBytes, 4, out var a, out _));
                Check(!budget.TryReserve(second, limit, 4, out var rejected, out var attempted));
                Check(rejected == null && attempted.Bytes > limit && budget.Current.Bytes == firstBytes && budget.Current.Frames == 1);
                // Overflow evicts only the baseline cache; the worker lease is intact.
                a!.Dispose(); Check(budget.Current.Bytes == 0 && budget.Current.Frames == 0);
                Check(budget.TryReserve(second, ReflectiveGraphBytes(second), 4, out var b, out _));
                b!.Dispose(); budget.ClearBaseline(); Check(budget.Current.Bytes == 0 && budget.Current.Frames == 0);
            }
        });
        test("typed queue rejects a byte-short full graph then admits the exact reflective bound", () =>
        {
            var frame = Fixture(0); long exact = ReflectiveGraphBytes(frame);
            var budget = new ReplayQueueBudget();
            Check(!budget.TryReserve(frame, exact - 1, 1, out _, out var attempt));
            Check(attempt.Bytes == exact && budget.Current.Bytes == 0 && budget.Current.Frames == 0);
            Check(budget.TryReserve(frame, exact, 1, out var accepted, out _));
            budget.ClearBaseline(); accepted!.Dispose(); Check(budget.Current.Bytes == 0 && budget.Current.Frames == 0);
        });
    }
    private static ReplayFrame Fixture(double time, CreatureReplayFrame? shared = null)
    {
        float[] position = { 1, 2, 3 }, rotation = { 0, 0, 0, 1 };
        var leaf = new NodePose("0:body", position, rotation, position);
        var pose = new ObjectPose(position, rotation, position, true, new[] { leaf, leaf });
        var creature = shared ?? new CreatureReplayFrame { Key = "native", Kind = "spider", Resource = "spider", SourcePath = "scene/native", Pose = pose,
            IdleAnimation = new CreatureIdleAnimation { Clip = "spider-idle-v1", AnchorTime = 4, ClipTime = .2, Speed = .33f, RopePhase = .7f },
            Line = new CreatureReplayLine { Points = position }, Visuals = new[] { new NativeRendererFrame { Materials = new[] { new NativeMaterialFrame {
                Name = "native", Shader = "native", Properties = new[] { new NativeShaderPropertyFrame { Name = "_Color", Kind = 1, Values = rotation } } } } } } };
        // Shares subgraphs both within and between typed and generic models.
        return new ReplayFrame { T = time, Creatures = new[] { creature, creature }, Items = new[] { new ItemFrame { Pose = creature.Pose, Visuals = creature.Visuals } },
            Crates = new[] { new CrateFrame { Pose = pose } } };
    }
    private static void VerifyOne(ReplayFrame frame)
    {
        var budget = new ReplayQueueBudget(); long expected = ReflectiveGraphBytes(frame);
        Check(budget.TryReserve(frame, expected, 1, out var lease, out _)); Check(budget.Current.Bytes == expected);
        lease!.Dispose(); budget.ClearBaseline(); Check(budget.Current.Bytes == 0 && budget.Current.Frames == 0);
    }
    private static long ReflectiveGraphBytes(params ReplayFrame[] frames)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance); long bytes = 0;
        var pending = new Stack<object>(frames);
        while (pending.TryPop(out var value))
        {
            if (!seen.Add(value)) continue;
            long own; object[] children;
            if (value is string text) { own = 32 + text.Length * 2L; children = Array.Empty<object>(); }
            else if (value is Array array)
            {
                var element = array.GetType().GetElementType()!;
                if (element.IsValueType)
                {
                    int width = element == typeof(bool) || element == typeof(byte) || element == typeof(sbyte) ? 1 :
                        element == typeof(short) || element == typeof(ushort) || element == typeof(char) ? 2 :
                        element == typeof(float) || element == typeof(int) || element == typeof(uint) ? 4 : 8;
                    own = 32 + array.LongLength * width; children = Array.Empty<object>();
                }
                else { own = 32 + array.LongLength * 8; children = (object[])array; }
            }
            else
            {
                var properties = value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
                own = 32 + properties.Length * 8L;
                children = properties.Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !p.PropertyType.IsValueType)
                    .Select(p => p.GetValue(value)).Where(v => v != null).Cast<object>().ToArray();
            }
            bytes = checked(bytes + own + 160 + children.Length * 8L);
            foreach (var child in children) if (child != null) pending.Push(child);
        }
        return bytes;
    }
    private static void Check(bool value) { if (!value) throw new Exception("Typed queue budget diverged from reflective accounting."); }
}
