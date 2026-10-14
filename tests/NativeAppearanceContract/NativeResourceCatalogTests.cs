using PeakReplayLab;

internal static class NativeResourceCatalogTests
{
    private sealed class Resource
    {
        public bool Alive = true, ReplayCopy, RuntimeInstance;
    }

    private static NativeVisualResourceCatalog<Resource> Catalog() => new(
        value => value.Alive && !value.ReplayCopy,
        (existing, candidate) => existing.RuntimeInstance && !candidate.RuntimeInstance);

    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("native resource indexing yields to the budget and another visual continues its cursor", () =>
        {
            var catalog = Catalog(); using var a = catalog.Acquire(6); using var b = catalog.Acquire(6);
            var resources = Enumerable.Range(0, 2000).Select(_ => new Resource()).ToArray(); int scans = 0, indexed = 0, credits = 0;
            bool Step() => credits-- > 0;
            Resource[] Discover() { scans++; return resources; }
            string Identity(Resource value) => (++indexed).ToString();
            check(!a.ExpandIncremental(Discover, Identity, Step) && scans == 0);
            credits = 1; check(!a.ExpandIncremental(Discover, Identity, Step) && scans == 1 && indexed == 0);
            credits = 17; check(!a.ExpandIncremental(Discover, Identity, Step) && indexed == 17);
            a.Dispose(); credits = 1983;
            check(b.ExpandIncremental(Discover, Identity, Step) && scans == 1 && indexed == 2000 && b.Count == 2000);
            credits = 1; check(b.ExpandIncremental(Discover, Identity, Step) && scans == 1);
        });

        test("scene refresh discards a partial resource cursor before scanning fresh assets", () =>
        {
            var catalog = Catalog(); using var a = catalog.Acquire(5); int scans = 0;
            var old = new Resource(); var fresh = new Resource(); int credits = 1;
            bool Step() => credits-- > 0;
            check(!a.ExpandIncremental(() => { scans++; return new[] { old }; }, _ => "old", Step));
            catalog.Refresh(); credits = 1;
            check(!a.ExpandIncremental(() => { scans++; return new[] { fresh }; }, _ => "fresh", Step));
            credits = 1; check(a.ExpandIncremental(() => throw new Exception(), _ => "fresh", Step));
            check(scans == 2 && !a.TryGet("old", out _) && a.TryGet("fresh", out var found) && ReferenceEquals(found, fresh));
        });

        test("hundreds of replay clones share one native fallback scan", () =>
        {
            var catalog = Catalog(); var native = new Resource(); int scans = 0;
            var leases = Enumerable.Range(0, 300).Select(_ => catalog.Acquire(7)).ToArray();
            foreach (var lease in leases)
            {
                // Missing identities must not cause every clone (or every frame)
                // to repeat the loaded-material/texture/mesh scan.
                lease.Expand(add => { scans++; add("native", native); });
                check(lease.TryGet("native", out var found) && ReferenceEquals(found, native));
                check(!lease.TryGet("unavailable", out _) && lease.Count == 1);
            }
            check(scans == 1 && catalog.SceneCount == 1);
            for (int i = 0; i < leases.Length - 1; i++) leases[i].Dispose();
            check(leases[^1].TryGet("native", out _));
            leases[^1].Dispose(); check(catalog.SceneCount == 0 && leases[^1].Count == 0);
        });

        test("concurrent scene owners release only their own catalog", () =>
        {
            var catalog = Catalog(); using var first = catalog.Acquire(10); using var second = catalog.Acquire(11);
            var a = new Resource(); var b = new Resource(); first.Register("same", a); second.Register("same", b);
            check(first.TryGet("same", out var foundA) && ReferenceEquals(foundA, a));
            check(second.TryGet("same", out var foundB) && ReferenceEquals(foundB, b));
            first.Dispose(); first.Dispose();
            check(catalog.SceneCount == 1 && second.TryGet("same", out foundB) && ReferenceEquals(foundB, b));
        });

        test("unloaded scene cannot resolve stale resources or erase a replacement scene", () =>
        {
            var catalog = Catalog(); using var old = catalog.Acquire(12); old.Register("old", new Resource());
            catalog.Unload(12); int staleScans = 0; old.Expand(_ => staleScans++);
            check(!old.TryGet("old", out _) && old.Count == 0 && staleScans == 0);
            using var replacement = catalog.Acquire(12); replacement.Register("fresh", new Resource());
            old.Dispose(); check(catalog.SceneCount == 1 && replacement.TryGet("fresh", out _));
        });

        test("scene resource refresh permits one new shared scan", () =>
        {
            var catalog = Catalog(); using var a = catalog.Acquire(13); using var b = catalog.Acquire(13); int scans = 0;
            var old = new Resource(); a.Expand(add => { scans++; add("asset", old); });
            catalog.Refresh(); check(!a.TryGet("asset", out _) && a.Count == 0);
            var fresh = new Resource(); b.Expand(add => { scans++; add("asset", fresh); });
            a.Expand(_ => scans++);
            check(scans == 2 && a.TryGet("asset", out var found) && ReferenceEquals(found, fresh));
        });

        test("replay material copies and destroyed source wrappers never become native resources", () =>
        {
            var catalog = Catalog(); using var lease = catalog.Acquire(14); var copy = new Resource { ReplayCopy = true };
            var destroyed = new Resource { Alive = false }; var native = new Resource();
            lease.Expand(add => { add("copy", copy); add("destroyed", destroyed); add("native", native); });
            check(lease.Count == 1 && !lease.TryGet("copy", out _) && !lease.TryGet("destroyed", out _));
            native.ReplayCopy = true; check(!lease.TryGet("native", out _) && lease.Count == 0);
            var laterDestroyed = new Resource(); lease.Register("later", laterDestroyed); laterDestroyed.Alive = false;
            check(!lease.TryGet("later", out _) && lease.Count == 0);
        });

        test("native prefab wins over runtime instance without replacing independent identity", () =>
        {
            var catalog = Catalog(); using var lease = catalog.Acquire(15);
            var runtime = new Resource { RuntimeInstance = true }; var prefab = new Resource();
            lease.Register("material", runtime); lease.Register("material", prefab); lease.Register("material", runtime);
            check(lease.TryGet("material", out var found) && ReferenceEquals(found, prefab));
            lease.Register("other", runtime); check(lease.TryGet("other", out found) && ReferenceEquals(found, runtime));
        });

        test("failed resource discovery clears partial results and can retry", () =>
        {
            var catalog = Catalog(); using var lease = catalog.Acquire(16); bool failed = false;
            try { lease.Expand(add => { add("partial", new Resource()); throw new InvalidOperationException(); }); }
            catch (InvalidOperationException) { failed = true; }
            check(failed && lease.Count == 0 && !lease.TryGet("partial", out _));
            lease.Expand(add => add("ready", new Resource())); check(lease.TryGet("ready", out _));
        });
    }
}
