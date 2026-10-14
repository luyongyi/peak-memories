using System.Runtime.CompilerServices;
using PeakReplayLab;

internal static class BoundedReferenceCacheTests
{
    private sealed class EqualKey
    {
        internal static int EqualityCalls;
        public override bool Equals(object? other) { EqualityCalls++; return other is EqualKey; }
        public override int GetHashCode() { EqualityCalls++; return 1; }
    }
    internal static void Run(Action<string, Action> test)
    {
        test("bounded identity cache ignores overloaded model equality and refreshes exact LRU hits", () =>
        {
            var cache = new BoundedReferenceCache<EqualKey, object>(2);
            var a = new EqualKey(); var b = new EqualKey(); var c = new EqualKey();
            EqualKey.EqualityCalls = 0; int factories = 0;
            object Make(EqualKey key) { factories++; return new object(); }
            var av = cache.GetValue(a, Make); var bv = cache.GetValue(b, Make);
            Check(!ReferenceEquals(av, bv) && cache.Count == 2);
            Check(cache.TryGetValue(a, out var found) && ReferenceEquals(found, av));
            cache.GetValue(c, Make);
            Check(cache.TryGetValue(a, out found) && ReferenceEquals(found, av) && !cache.TryGetValue(b, out _));
            Check(ReferenceEquals(av, cache.GetValue(a, Make)) && factories == 3 && EqualKey.EqualityCalls == 0);
            cache.Clear(); Check(cache.Count == 0 && cache.Capacity == 2 && !cache.TryGetValue(a, out _));
            cache.Add(a, av); cache.Add(b, bv);
            Reject(() => cache.Add(a, new object()));
            Check(cache.TryGetValue(a, out found) && ReferenceEquals(found, av));
            cache.Add(c, new object()); Check(cache.Count == 2 && !cache.TryGetValue(b, out _));
        });
        test("failed bounded cache factories retain the previous full cache and never store null", () =>
        {
            var cache = new BoundedReferenceCache<object, object>(1); var key = new object(); var other = new object();
            var value = cache.GetValue(key, _ => new object());
            Reject(() => cache.GetValue(other, _ => throw new InvalidOperationException("conversion failed")));
            Check(cache.Count == 1 && cache.TryGetValue(key, out var retained) && ReferenceEquals(retained, value));
            Reject(() => cache.GetValue(other, _ => null!));
            Check(cache.Count == 1 && cache.TryGetValue(key, out retained) && ReferenceEquals(retained, value));
            Reject(() => new BoundedReferenceCache<object, object>(0));
            Reject(() => new BoundedReferenceCache<object, object>(-1));
            Reject(() => cache.GetValue(null!, _ => new object()));
            Reject(() => cache.GetValue(key, null!));
        });
        test("bounded cache eviction releases source and translated objects while preserving a hot entry", () =>
        {
            var cache = new BoundedReferenceCache<object, object>(128); var hot = new object();
            var hotValue = cache.GetValue(hot, _ => new object());
            var evicted = PutWeak(cache);
            for (int i = 0; i < 100_000; i++)
            {
                cache.GetValue(new object(), _ => new object());
                Check(ReferenceEquals(hotValue, cache.GetValue(hot, _ => throw new Exception("hot entry evicted"))) && cache.Count <= 128);
            }
            Check(cache.Count == 128);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Check(!evicted.Key.IsAlive && !evicted.Value.IsAlive);
            GC.KeepAlive(cache);
        });
        test("actor translation pressure reaches every explicit cache bound without changing old samples", () =>
        {
            var rebaser = new ActorJointRebaser(100);
            var hot = Node("hip", 100); ActorFrame[] Make(int i) => new[] { new ActorFrame { Id = "native-test", JointPose = new[] { hot, Node("hand", 100 + i / 60d) },
                RouteState = new ActorRouteState { SampleTime = 100 + i / 60d, Center = new[] { (float)i, 2f, 3f }, Alive = true } } };
            var first = Make(0); var translated = rebaser.Apply(first);
            Check(ReferenceEquals(translated, rebaser.Apply(first)) && translated[0].JointPose[1].SampleTime == 0);
            for (int i = 1; i <= 40_000; i++)
            {
                var source = Make(i); var value = rebaser.Apply(source);
                Check(ReferenceEquals(translated[0].JointPose[0], value[0].JointPose[0]));
                Check(value[0].JointPose[1].SampleTime == source[0].JointPose[1].SampleTime - 100 && source[0].RouteState!.SampleTime == 100 + i / 60d);
                var counts = rebaser.CacheCounts;
                Check(counts.Nodes <= ActorJointRebaser.NodeCapacity && counts.Poses <= ActorJointRebaser.PoseCapacity &&
                    counts.Actors <= ActorJointRebaser.ActorCapacity && counts.Routes <= ActorJointRebaser.RouteCapacity && counts.Sets <= ActorJointRebaser.SetCapacity);
            }
            var full = rebaser.CacheCounts;
            Check(full.Nodes == ActorJointRebaser.NodeCapacity && full.Poses == ActorJointRebaser.PoseCapacity &&
                full.Actors == ActorJointRebaser.ActorCapacity && full.Routes == ActorJointRebaser.RouteCapacity && full.Sets == ActorJointRebaser.SetCapacity);
            var again = rebaser.Apply(first);
            Check(!ReferenceEquals(again, translated) && !ReferenceEquals(again[0].JointPose[1], translated[0].JointPose[1]));
            Check(ReferenceEquals(again[0].JointPose[0], translated[0].JointPose[0]));
            Check(first[0].JointPose[0].SampleTime == 100 && first[0].JointPose[1].SampleTime == 100 && first[0].RouteState!.SampleTime == 100);
            Check(again[0].JointPose[1].SampleTime == 0 && translated[0].JointPose[1].SampleTime == 0 && again[0].RouteState!.SampleTime == 0);
            Check(ReferenceEquals(first[0].JointPose[1].Position, again[0].JointPose[1].Position) && ReferenceEquals(first[0].RouteState!.Center, again[0].RouteState!.Center));
        });
        test("actor translation retains shared actor pose route versions across page array wrappers", () =>
        {
            var node = Node("hip", 123.123456789); var pose = new[] { node };
            var route = new ActorRouteState { SampleTime = node.SampleTime };
            var actor = new ActorFrame { Id = "one", JointPose = pose, RouteState = route };
            var other = new ActorFrame { Id = "two", JointPose = pose, RouteState = route };
            var rebaser = new ActorJointRebaser(123.123456);
            var a = rebaser.Apply(new[] { actor }); var b = rebaser.Apply(new[] { actor, other });
            Check(ReferenceEquals(a[0], b[0]) && ReferenceEquals(b[0].JointPose, b[1].JointPose) && ReferenceEquals(b[0].RouteState, b[1].RouteState));
            Check(ReferenceEquals(node.Position, b[0].JointPose[0].Position) && b[0].JointPose[0].SampleTime == node.SampleTime - 123.123456);
            var independent = new ActorJointRebaser(100).Apply(new[] { actor });
            Check(independent[0].JointPose[0].SampleTime == node.SampleTime - 100 && node.SampleTime == 123.123456789);
            var plain = new[] { new ActorFrame { Id = "legacy" } };
            Check(ReferenceEquals(plain, rebaser.Apply(plain)) && ReferenceEquals(plain, new ActorJointRebaser(0).Apply(plain)));
        });
    }
    private static NodePose Node(string path, double time) => new(path, new[] { 1f, 2f, 3f }, new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f }, sampleTime: time);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Key, WeakReference Value) PutWeak(BoundedReferenceCache<object, object> cache)
    { var key = new object(); var value = cache.GetValue(key, _ => new object()); return (new WeakReference(key), new WeakReference(value)); }
    private static void Check(bool value) { if (!value) throw new Exception("Bounded reference translation mismatch."); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { return; } throw new Exception("Invalid reference cache operation was accepted."); }
}
