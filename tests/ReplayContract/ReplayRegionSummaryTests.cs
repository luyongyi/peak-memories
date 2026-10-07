using Newtonsoft.Json;
using PeakReplayLab;

internal static class ReplayRegionSummaryTests
{
    private static ReplayHeader Header() => new() { Scene = "Level_Test", Route = "Shore,Roots,Alpine,Swamp,Temple,Void" };
    private static ReplayFrame Frame(double time, int segment, bool extraction = false, bool priorNadir = false) => new()
    { T = time, World = new WorldFrame { Segment = segment }, CoverPeakExtraction = extraction, CoverNadirEntered = priorNadir };
    private static void Check(bool condition) { if (!condition) throw new Exception("Region cover assertion failed."); }
    private static string[] Keys(ReplayRegionAccumulator summary, bool full = true) => ReplayRegionCovers.Keys(summary.Snapshot(), full);
    public static void Run(Action<string, Action> test)
    {
        test("region covers never infer visits from route availability", () =>
        {
            var regions = new ReplayRegionAccumulator(Header());
            Check(Keys(regions).Length == 0);
            regions.Observe(0); Check(Keys(regions).SequenceEqual(new[] { "shore" }));
            Check(MemoriesLibraryItem.FromHighlight("old.peakreplay", Header(), "").CoverKeys.Length == 0);
        });
        test("region covers preserve observed route chronology and separate shared finale from Peak", () =>
        {
            var regions = new ReplayRegionAccumulator(Header());
            foreach (int segment in new[] { 0, 1, 1, 2, 3, 4, 5 }) regions.Observe(segment);
            Check(Keys(regions).SequenceEqual(new[] { "shore", "roots", "alpine", "swamp", "temple" }));
            Check(Keys(regions, false).SequenceEqual(new[] { "temple" }));
            regions.Observe(5, extracted: true);
            Check(Keys(regions).Last() == "peak" && Keys(regions, false).Single() == "peak");
        });
        test("Nadir excludes Peak even when extraction or manual segment5 appeared earlier", () =>
        {
            var regions = new ReplayRegionAccumulator(Header());
            regions.Observe(4); regions.Observe(5, extracted: true); regions.Observe(6); regions.Observe(5, extracted: true);
            Check(Keys(regions).SequenceEqual(new[] { "temple", "nadir" }));
            Check(!Keys(regions).Contains("peak"));
            var contradictory = new ReplayRegionSummary { Regions = new[] { "peak", "nadir" }, LastRegion = "peak", PeakExtraction = true, NadirEntered = true };
            Check(ReplayRegionCovers.Keys(contradictory, false).Single() == "nadir");
        });
        test("summit-only final outcome never invents a Nadir visit", () =>
        {
            var regions = new ReplayRegionAccumulator(Header()); regions.Observe(4); regions.Observe(4, extracted: true);
            Check(Keys(regions).SequenceEqual(new[] { "temple", "peak" }));
            Check(!regions.Snapshot().NadirEntered);
        });
        test("branch-specific biomes and final Kiln are mapped using observed segments", () =>
        {
            var header = Header(); header.Route = "Shore,Tropics,Mesa,Volcano,Volcano,Void";
            var regions = new ReplayRegionAccumulator(header);
            for (int i = 0; i < 5; i++) regions.Observe(i);
            Check(Keys(regions).SequenceEqual(new[] { "shore", "tropics", "mesa", "volcano", "kiln" }));
        });
        test("unrecognized ordinary route labels do not borrow biome art", () =>
        {
            var header = Header(); header.Route = "Peak,Void,Grasslands,Temple,Peak";
            var regions = new ReplayRegionAccumulator(header);
            for (int i = 0; i < 6; i++) regions.Observe(i);
            Check(Keys(regions).Length == 0);
            regions.Observe(6); Check(Keys(regions).Single() == "nadir");
        });
        test("installed Citadel route uses repeated Swamp while contradictory finale pairs remain unknown", () =>
        {
            var header = Header(); header.Route = "Shore,Roots,Alpine,Swamp,Swamp,Void";
            var regions = new ReplayRegionAccumulator(header); regions.Observe(3); regions.Observe(4);
            Check(Keys(regions).SequenceEqual(new[] { "swamp", "temple" }));
            header.Route = "Shore,Roots,Alpine,Volcano,Swamp,Void";
            regions = new ReplayRegionAccumulator(header); regions.Observe(4); Check(Keys(regions).Length == 0);
            header.Route = "Shore,Roots,Alpine,Swamp,Volcano,Void";
            regions = new ReplayRegionAccumulator(header); regions.Observe(4); Check(Keys(regions).Length == 0);
        });
        test("highlight cover is current observed biome after backtracking", () =>
        {
            var regions = new ReplayRegionAccumulator(Header()); regions.Observe(0); regions.Observe(1); regions.Observe(0);
            Check(Keys(regions).SequenceEqual(new[] { "shore", "roots" }));
            Check(Keys(regions, false).Single() == "shore");
        });
        test("rolling highlights derive covers from retained frames and honor prior Nadir exclusion", () =>
        {
            var buffer = new RollingBuffer(); buffer.Add(Frame(0, 0)); buffer.Add(Frame(180, 3)); buffer.Add(Frame(181, 4));
            var clip = buffer.Snapshot(Header(), DateTime.UtcNow);
            Check(ReplayRegionCovers.Keys(clip.Header.RegionSummary, true).SequenceEqual(new[] { "swamp", "temple" }));
            Check(MemoriesLibraryItem.FromHighlight("test.peakreplay", clip.Header, "").CoverKeys.Single() == "temple");
            buffer.Add(Frame(182, 5, extraction: true, priorNadir: true));
            Check(!ReplayRegionCovers.Keys(buffer.Snapshot(Header(), DateTime.UtcNow).Header.RegionSummary, true).Contains("peak"));
        });
        test("capture-only outcome flags never alter existing frame JSON schema", () =>
        {
            string json = JsonConvert.SerializeObject(Frame(0, 4, true, true), ReplayFiles.Json);
            Check(!json.Contains("CoverPeakExtraction") && !json.Contains("CoverNadirEntered"));
        });
        test("invalid cover metadata cannot contain paths arbitrary tags or unverified Peak", () =>
        {
            foreach (var summary in new[]
            {
                new ReplayRegionSummary { Regions = new[] { "../peak" } },
                new ReplayRegionSummary { Regions = new[] { "peak" } },
                new ReplayRegionSummary { Regions = new[] { "nadir" } },
                new ReplayRegionSummary { Regions = new[] { "shore", "shore" } },
                new ReplayRegionSummary { Regions = new[] { "shore" }, LastRegion = "peak" },
                new ReplayRegionSummary { Version = 999 },
            })
            {
                Check(!ReplayRegionCovers.Valid(summary) && ReplayRegionCovers.Keys(summary, true).Length == 0);
                var header = Header(); header.RegionSummary = summary;
                try { ReplayRules.Validate(header); throw new Exception("Invalid summary accepted."); } catch (InvalidDataException) { }
            }
        });
        test("full recording footer stores actual summaries without changing page headers", () => InRoot(root =>
        {
            var writer = new FullReplayWriter(root, Header());
            foreach (var frame in new[] { Frame(100, 0), Frame(101, 1), Frame(102, 4), Frame(103, 6) }) Check(writer.TryEnqueue(frame));
            var task = writer.CompleteAsync(); Check(task.Wait(TimeSpan.FromSeconds(10)));
            Check(task.Result.Status == "completed");
            var info = FullReplayArchive.ReadInfo(task.Result.FilePath);
            Check(info.Header.RegionSummary == null);
            Check(ReplayRegionCovers.Keys(info.RegionSummary, true).SequenceEqual(new[] { "shore", "roots", "temple", "nadir" }));
            Check(FullReplayArchive.ReadPage(task.Result.FilePath, info, 0).Frames.Count == 4);
            var card = MemoriesLibraryItem.FromFullRun(task.Result.FilePath, info, new FileInfo(task.Result.FilePath).Length, "");
            Check(card.IsFullRun && card.CoverKeys.Length == 4 && !card.CoverKeys.Contains("peak"));
        }));
        test("new highlight cover header survives archive round trip", () => InRoot(root =>
        {
            var buffer = new RollingBuffer(); buffer.Add(Frame(10, 4)); buffer.Add(Frame(11, 4, true));
            string path = ReplayArchive.Save(root, buffer.Snapshot(Header(), DateTime.UtcNow));
            var header = ReplayFiles.ReadHeader(path);
            Check(ReplayRegionCovers.Keys(header.RegionSummary, false).Single() == "peak");
            Check(!MemoriesLibraryItem.FromHighlight(path, header, "").IsFullRun);
            Check(ReplayFiles.Read(path).Complete);
        }));
        test("outcome after final sample is included only inside the bounded terminal interval", () =>
        {
            var header = Header(); var buffer = new RollingBuffer(); buffer.Add(Frame(10, 4)); buffer.Add(Frame(11, 4));
            header.CoverOutcome = new ReplayRegionOutcome(11.01, false);
            var prepare = buffer.PrepareSnapshot(header, DateTime.UtcNow);
            header.CoverOutcome = new ReplayRegionOutcome(11.02, true);
            Check(ReplayRegionCovers.Keys(prepare().Header.RegionSummary, false).Single() == "peak");
            Check(ReplayRegionCovers.Keys(buffer.Snapshot(header, DateTime.UtcNow).Header.RegionSummary, false).Single() == "nadir");
            header.CoverOutcome = new ReplayRegionOutcome(20, false);
            Check(!ReplayRegionCovers.Keys(buffer.Snapshot(header, DateTime.UtcNow).Header.RegionSummary, true).Contains("peak"));
            header.CoverOutcome = new ReplayRegionOutcome(9, false);
            Check(!ReplayRegionCovers.Keys(buffer.Snapshot(header, DateTime.UtcNow).Header.RegionSummary, true).Contains("peak"));
            Check(!JsonConvert.SerializeObject(header, ReplayFiles.Json).Contains("CoverOutcome"));
        });
        test("full recording captures final native outcome without requiring another 60 Hz frame", () => InRoot(root =>
        {
            var header = Header(); var writer = new FullReplayWriter(root, header);
            Check(writer.TryEnqueue(Frame(100, 4))); Check(writer.TryEnqueue(Frame(101, 4)));
            header.CoverOutcome = new ReplayRegionOutcome(101.01, false);
            var completion = writer.CompleteAsync(); Check(completion.Wait(TimeSpan.FromSeconds(10)));
            var info = FullReplayArchive.ReadInfo(completion.Result.FilePath);
            Check(ReplayRegionCovers.Keys(info.RegionSummary, false).Single() == "peak");
        }));
        test("legacy unknown full-run cards remain neutral and retain full-run identity", () =>
        {
            Check(MemoriesLibraryItem.FromFullRun("old.peakrun", null, 0, "unknown").IsFullRun);
            Check(MemoriesLibraryItem.FromFullRun("old.peakrun", null, 0, "unknown").CoverKeys.Length == 0);
        });
    }
    private static void InRoot(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "peak-region-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); } finally { Directory.Delete(root, true); }
    }
}
