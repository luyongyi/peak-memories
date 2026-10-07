using PeakReplayLab;

internal static class PerformanceContractTests
{
    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("replay headers reject schemas before 10", () =>
        {
            for (int schema = 2; schema < 10; schema++)
            {
                bool rejected = false;
                try { ReplayRules.Validate(new ReplayHeader { Scene = "test", Schema = schema }); }
                catch (InvalidDataException) { rejected = true; }
                check(rejected);
            }
            ReplayRules.Validate(new ReplayHeader { Scene = "test", Schema = 10 });
        });
        test("inventory bitmask separates backpack and player slots including 63 and 250", () =>
        {
            var actor = new ActorFrame { Id = "a", Inventory = new[] {
                new InventoryFrame { Slot = 63 }, new InventoryFrame { Slot = 63, Backpack = true },
                new InventoryFrame { Slot = 250 }, new InventoryFrame { Slot = 250, Backpack = true } } };
            ReplayRules.Validate(new ReplayFrame { T = 0, Actors = new[] { actor } }, -1);
            actor.Inventory[1].Backpack = false;
            try { ReplayRules.Validate(new ReplayFrame { T = 0, Actors = new[] { actor } }, -1); }
            catch (InvalidDataException) { return; }
            throw new Exception("duplicate slot was accepted");
        });
        test("shared object arrays survive reference accounting and eviction", () =>
        {
            var buffer = new RollingBuffer();
            var items = new[] { new ItemFrame { Key = "ground", ItemId = 1 } };
            var crates = new[] { new CrateFrame { Key = "chest", Kind = "Luggage" } };
            for (int i = 0; i <= 7300; i++) buffer.Add(new ReplayFrame { T = i / 60d, Items = items, Crates = crates });
            check(buffer.Count <= 7201 && buffer.EstimatedBytes > 0);
            for (int i = 1; i <= 122; i++) buffer.Add(new ReplayFrame { T = 122 + i });
            check(buffer.Count <= 121 && buffer.EstimatedBytes == buffer.Count * RollingBuffer.Estimate(new ReplayFrame()));
        });
        test("deferred snapshot uses frozen frame references while rolling capture continues", () =>
        {
            var buffer = new RollingBuffer(); buffer.Add(new ReplayFrame { T = 500 }); buffer.Add(new ReplayFrame { T = 501 });
            var build = buffer.PrepareSnapshot(new ReplayHeader { Scene = "test" }, DateTime.UtcNow);
            for (int i = 502; i <= 700; i++) buffer.Add(new ReplayFrame { T = i });
            var clip = build(); check(clip.Frames.Count == 2 && clip.Frames[0].T == 0 && clip.Frames[1].T == 1);
        });
        test("cropped snapshot rebases anchored box without mutating capture", () =>
        {
            var box = new CrateFrame { Key = "box", Kind = "Luggage", Animation = new CrateAnimationFrame {
                Clip = "Open", Anchored = true, AnchorTime = 499, Time = 0, Duration = 4, Rate = 1 } };
            var buffer = new RollingBuffer(); var states = new[] { box };
            buffer.Add(new ReplayFrame { T = 500, Crates = states }); buffer.Add(new ReplayFrame { T = 501, Crates = states });
            var clip = buffer.Snapshot(new ReplayHeader { Scene = "test" }, DateTime.UtcNow);
            var copy = clip.Frames[0].Crates[0];
            check(box.Animation.AnchorTime == 499 && copy.Animation!.AnchorTime == -1);
            check(ReferenceEquals(clip.Frames[0].Crates, clip.Frames[1].Crates));
            check(CrateAnimationTimeline.Sample(copy.Animation!, 0, 4, false) == 1);
        });
        test("timing scopes are bounded and disable cleanly", () =>
        {
            ReplayPerformance.Reset(); ReplayPerformance.Enabled = true;
            for (int i = 0; i < 600; i++) using (ReplayPerformance.Measure(ReplayStage.Capture)) { }
            check(ReplayPerformance.Report().Contains("n=600") && ReplayPerformance.Summary(false).Contains("p95"));
            ReplayPerformance.Enabled = false;
            using (ReplayPerformance.Measure(ReplayStage.Capture)) { }
            check(ReplayPerformance.Report().Contains("n=600")); ReplayPerformance.Enabled = true;
        });
        test("schema 10 anchored box survives archive and static array sharing", () =>
        {
            var states = new[] { new CrateFrame { Key = "chest", Kind = "Luggage", Animation = new CrateAnimationFrame {
                Clip = "Open", Anchored = true, AnchorTime = 99, Duration = 3 } } };
            var buffer = new RollingBuffer();
            buffer.Add(new ReplayFrame { T = 100, Crates = states }); buffer.Add(new ReplayFrame { T = 101, Crates = states });
            string dir = Path.Combine(Path.GetTempPath(), "peak-anchor-test-" + Guid.NewGuid().ToString("N")); string? path = null;
            try
            {
                var snapshot = buffer.Snapshot(new ReplayHeader { Schema = 10, Scene = "test" }, DateTime.UtcNow);
                path = ReplayArchive.Save(dir, snapshot); var clip = ReplayFiles.Read(path);
                check(clip.Header.Schema == 10 && clip.Frames[0].Crates[0].Animation!.Anchored);
                check(ReferenceEquals(clip.Frames[0].Crates, clip.Frames[1].Crates));
                check(CrateAnimationTimeline.Sample(clip.Frames[1].Crates[0].Animation!, 1, 3, false) == 2);
                snapshot.Header.Schema = 9;
                try { ReplayArchive.Save(dir, snapshot); }
                catch (InvalidDataException) { return; }
                throw new Exception("previous replay schemas must not be written");
            }
            finally { if (path != null) File.Delete(path); if (Directory.Exists(dir)) Directory.Delete(dir); }
        });
    }
}
