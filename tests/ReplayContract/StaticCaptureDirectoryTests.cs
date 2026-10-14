using PeakReplayLab;
using System.Runtime.CompilerServices;

internal static class StaticCaptureDirectoryTests
{
    private sealed class Trap
    {
        public readonly string Key;
        public bool Active = true, Destroyed;
        public int Reads, Failures;
        public Action? DuringRead;
        public Trap(int id) => Key = "trap:" + id;
        public CreatureReplayFrame? Read(CreatureReplayFrame? previous)
        {
            Reads++;
            if (Failures > 0) { Failures--; throw new InvalidOperationException("native object is initializing"); }
            if (Destroyed) return null;
            Span<float> trs = stackalloc float[] { 1, 2, 3, 0, 0, 0, 1, 1, 1, 1 };
            var pose = CreatureDirectoryRules.StaticPose(previous?.Pose, Active, trs);
            DuringRead?.Invoke();
            return previous != null && ReferenceEquals(previous.Pose, pose) ? previous : new CreatureReplayFrame
            { Key = Key, Kind = "spore-trap", Resource = "spore-trap", State = pose.Active ? 0 : 1, Pose = pose };
        }
    }
    private static readonly Func<Trap, CreatureReplayFrame?, CreatureReplayFrame?> read = (source, previous) => source.Read(previous);
    private static StaticCaptureDirectory<Trap, CreatureReplayFrame> Directory() => new();
    public static void Run(Action<string, Action> test)
    {
        test("static baseline readiness waits for shared budget and failed initial reads without losing work", () =>
        {
            var directory = Directory(); var first = new Trap(1) { Failures = 1 }; var second = new Trap(2);
            directory.Register(1, first); directory.Register(2, second);
            Check(!directory.InitialBaselineReady);
            Check(directory.Capture(0, read, beginInitialRead: () => false).Length == 0 && first.Reads == 0 && second.Reads == 0);
            int remaining = 1; directory.Capture(0, read, beginInitialRead: () => remaining-- > 0);
            Check(!directory.InitialBaselineReady && first.Reads + second.Reads == 1);
            directory.Capture(1, read); directory.Capture(1.25, read); Check(directory.InitialBaselineReady);
            directory.Clear(); Check(directory.InitialBaselineReady);
        });
        test("real static event bypasses exhausted initial budget once while lifecycle stays deferred", () =>
        {
            var directory = Directory(); var source = new Trap(1) { Failures = 1 }; directory.Register(1, source);
            directory.Changed(1, true); directory.Capture(0, read, beginInitialRead: () => false);
            Check(source.Reads == 1 && !directory.InitialBaselineReady);
            directory.Capture(1, read, beginInitialRead: () => false); Check(source.Reads == 1);
            directory.Changed(1, true); directory.Capture(1, read, beginInitialRead: () => false);
            Check(source.Reads == 2 && directory.InitialBaselineReady);
        });
        test("two hundred static traps read once then six thousand idle ticks allocate and read nothing", () =>
        {
            var directory = Directory(); var traps = Enumerable.Range(0, 200).Select(i => new Trap(i)).ToArray();
            for (int i = 0; i < traps.Length; i++) Check(directory.Register(i, traps[i]));
            var first = directory.Capture(0, read); Check(first.Length == 200 && traps.All(t => t.Reads == 1));
            for (int i = 1; i <= 100; i++) Check(ReferenceEquals(first, directory.Capture(i, read)));
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 1; i <= 6000; i++) Check(ReferenceEquals(first, directory.Capture(i * 1000d, read)));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(allocated == 0 && traps.All(t => t.Reads == 1));
        });
        test("repeated native trap completion events coalesce and unchanged states do not produce frames", () =>
        {
            var directory = Directory(); var trap = new Trap(1); directory.Register(1, trap);
            var first = directory.Capture(0, read);
            for (int i = 0; i < 200; i++) Check(directory.Changed(1));
            Check(ReferenceEquals(first, directory.Capture(.1, read)) && trap.Reads == 2);
            Check(ReferenceEquals(first, directory.Capture(1e9, read)) && trap.Reads == 2);
        });
        test("consumed static trap checkpoint persists and earlier armed checkpoint remains seekable", () =>
        {
            var directory = Directory(); var trap = new Trap(1); var other = new Trap(2);
            directory.Register(1, trap); directory.Register(2, other);
            var armed = directory.Capture(0, read); trap.Active = false; directory.Changed(1);
            var consumed = directory.Capture(.05, read);
            Check(!ReferenceEquals(armed, consumed) && armed[0].Pose.Active && !consumed[0].Pose.Active);
            Check(consumed[0].State == 1 && ReferenceEquals(armed[0].Pose.Position, consumed[0].Pose.Position));
            Check(ReferenceEquals(armed[1], consumed[1]) && other.Reads == 1);
            for (int i = 0; i < 100; i++) Check(ReferenceEquals(consumed, directory.Capture(i * 1000, read)));
            Check(trap.Reads == 2 && other.Reads == 1 && armed[0].State == 0);
        });
        test("trigger during initial native read remains queued for next capture without rereading in same tick", () =>
        {
            var directory = Directory(); var trap = new Trap(1); directory.Register(1, trap);
            trap.DuringRead = () => { trap.Active = false; directory.Changed(1); trap.DuringRead = null; };
            var armed = directory.Capture(0, read); Check(armed[0].Pose.Active && trap.Reads == 1);
            var consumed = directory.Capture(.01, read); Check(!consumed[0].Pose.Active && trap.Reads == 2);
            Check(ReferenceEquals(consumed, directory.Capture(100, read)) && trap.Reads == 2);
        });
        test("trigger arriving before first snapshot records only completed native state", () =>
        {
            var directory = Directory(); var trap = new Trap(1); directory.Register(1, trap);
            trap.Active = false; directory.Changed(1); directory.Changed(1);
            var result = directory.Capture(0, read); Check(result.Length == 1 && !result[0].Pose.Active && trap.Reads == 1);
        });
        test("removed queued traps are never read and instance id reuse cannot replay stale work", () =>
        {
            var directory = Directory(); var old = new Trap(1); directory.Register(1, old); directory.Remove(1);
            var current = new Trap(2); directory.Register(1, current);
            var first = directory.Capture(0, read); Check(first.Length == 1 && first[0].Key == current.Key && old.Reads == 0 && current.Reads == 1);
            directory.Changed(1); directory.Remove(1);
            Check(directory.Capture(1, read).Length == 0 && current.Reads == 1 && first.Length == 1 && directory.Count == 0);
        });
        test("queued native destruction retires snapshot and directory slot while consumption retains both", () =>
        {
            var directory = Directory(); var trap = new Trap(1); directory.Register(1, trap); directory.Capture(0, read);
            trap.Active = false; directory.Changed(1); Check(directory.Capture(.1, read).Length == 1 && directory.Count == 1);
            trap.Destroyed = true; directory.Changed(1);
            Check(directory.Capture(.2, read).Length == 0 && directory.Count == 0);
            Check(directory.Capture(1e9, read).Length == 0 && trap.Reads == 3);
        });
        test("one failing static birth does not drop other baselines and retries after deadline without new events", () =>
        {
            var directory = Directory(); var failing = new Trap(0) { Failures = 1 }; directory.Register(0, failing);
            var others = Enumerable.Range(1, 199).Select(i => new Trap(i)).ToArray();
            for (int i = 0; i < others.Length; i++) directory.Register(i + 1, others[i]);
            var partial = directory.Capture(0, read, (_, _) => throw new Exception("diagnostic callback failure"));
            Check(partial.Length == 199 && failing.Reads == 1 && others.All(t => t.Reads == 1));
            for (int i = 1; i < 25; i++) Check(ReferenceEquals(partial, directory.Capture(i / 100d, read)));
            Check(failing.Reads == 1);
            Check(directory.Capture(.25, read).Length == 200 && failing.Reads == 2 && others.All(t => t.Reads == 1));
        });
        test("failed static reads receive three bounded retries and a new event can recover later", () =>
        {
            var directory = Directory(); var trap = new Trap(1) { Failures = 10 }; directory.Register(1, trap);
            foreach (double now in new[] { 0, .25, 1.25, 5.25 }) Check(directory.Capture(now, read).Length == 0);
            Check(trap.Reads == 4);
            for (int i = 0; i < 100; i++) directory.Capture(1e8 + i, read);
            Check(trap.Reads == 4); trap.Failures = 0; directory.Changed(1);
            Check(directory.Capture(1e9, read).Length == 1 && trap.Reads == 5);
        });
        test("new event supersedes retry deadline without duplicate reads and clear drops all pending work", () =>
        {
            var directory = Directory(); var trap = new Trap(1) { Failures = 1 }; directory.Register(1, trap);
            directory.Capture(0, read); directory.Changed(1);
            var frame = directory.Capture(.1, read); Check(frame.Length == 1 && trap.Reads == 2);
            Check(ReferenceEquals(frame, directory.Capture(.25, read)) && trap.Reads == 2);
            directory.Changed(1); directory.Clear();
            Check(directory.Count == 0 && directory.Capture(1e9, read).Length == 0 && trap.Reads == 2);
        });
        test("region retirement releases queued static native bindings before the next capture", () =>
        {
            var directory = Directory(); var reference = RetireQueued(directory, false);
            Collect(); Check(!reference.IsAlive && directory.Count == 0);
            Check(directory.Capture(0, read).Length == 0);
            GC.KeepAlive(directory);
        });
        test("region retirement releases failed static bindings without waiting for retry deadline", () =>
        {
            var directory = Directory(); var reference = RetireQueued(directory, true);
            Collect(); Check(!reference.IsAlive && directory.Count == 0);
            Check(directory.Capture(.25, read).Length == 0);
            GC.KeepAlive(directory);
        });
        test("region retirement releases its cached checkpoint even while an old retry token remains", () =>
        {
            var directory = Directory(); var references = RetireCheckpoint(directory);
            Collect(); Check(references.All(reference => !reference.IsAlive) && directory.Count == 0);
            Check(directory.Capture(1e9, read).Length == 0);
            GC.KeepAlive(directory);
        });
        test("retired static retry token cannot wake a reused instance id", () =>
        {
            var directory = Directory(); var old = new Trap(1) { Failures = 1 };
            directory.Register(1, old); directory.Capture(0, read); directory.Remove(1);
            var current = new Trap(2); directory.Register(1, current);
            var first = directory.Capture(.1, read);
            Check(first.Length == 1 && first[0].Key == current.Key && current.Reads == 1);
            Check(ReferenceEquals(first, directory.Capture(.25, read)) && old.Reads == 1 && current.Reads == 1);
        });
        test("batched region retirement drops current static snapshots without mutating recorded history", () =>
        {
            var directory = Directory(); var traps = Enumerable.Range(0, 8192).Select(i => new Trap(i)).ToArray();
            for (int i = 0; i < traps.Length; i++) directory.Register(i, traps[i]);
            var history = directory.Capture(0, read);
            for (int i = 0; i < traps.Length; i++) Check(directory.Remove(i));
            Check(directory.Count == 0 && directory.Capture(.1, read).Length == 0 && history.Length == 8192);
            Check(history.All(frame => frame.Pose.Active) && traps.All(trap => trap.Reads == 1));
        });
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RetireQueued(StaticCaptureDirectory<Trap, CreatureReplayFrame> directory, bool retry)
    {
        var trap = new Trap(1) { Failures = retry ? 1 : 0 }; var reference = new WeakReference(trap);
        directory.Register(1, trap);
        if (retry) directory.Capture(0, read);
        directory.Remove(1); return reference;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] RetireCheckpoint(StaticCaptureDirectory<Trap, CreatureReplayFrame> directory)
    {
        var trap = new Trap(1); directory.Register(1, trap);
        var snapshot = directory.Capture(0, read);
        var references = new[] { new WeakReference(trap), new WeakReference(snapshot[0]) };
        trap.Failures = 1; directory.Changed(1); directory.Capture(.1, read); directory.Remove(1);
        return references;
    }
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    private static void Check(bool value) { if (!value) throw new Exception("Static event capture assertion failed."); }
}
