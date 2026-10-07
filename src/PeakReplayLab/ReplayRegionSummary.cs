using System;
using System.Collections.Generic;
using System.Linq;

namespace PeakReplayLab;

// Small, optional catalog metadata. It describes observed footage, not the map's
// available route. Missing metadata on an older recording means unknown.
public sealed class ReplayRegionSummary
{
    public int Version { get; set; } = 1;
    public string[] Regions { get; set; } = Array.Empty<string>();
    public string LastRegion { get; set; } = "";
    public bool PeakExtraction { get; set; }
    public bool NadirEntered { get; set; }
}

// Native event evidence is immutable and capture-only. It also bridges the final
// event-to-LateUpdate interval when scene departure prevents another sample.
internal sealed class ReplayRegionOutcome
{
    public readonly double Time;
    public readonly bool Nadir;
    public ReplayRegionOutcome(double time, bool nadir) { Time = time; Nadir = nadir; }
}

public static class ReplayRegionCovers
{
    private static readonly HashSet<string> known = new(StringComparer.Ordinal)
    { "shore", "roots", "tropics", "alpine", "mesa", "volcano", "swamp", "kiln", "temple", "peak", "nadir" };

    public static bool Valid(ReplayRegionSummary? summary) => summary == null ||
        summary.Version == 1 && summary.Regions != null && summary.Regions.Length <= 11 &&
        summary.Regions.All(key => key != null && known.Contains(key)) &&
        summary.Regions.Distinct(StringComparer.Ordinal).Count() == summary.Regions.Length &&
        summary.LastRegion != null && (summary.LastRegion.Length == 0 || summary.Regions.Contains(summary.LastRegion)) &&
        (!summary.Regions.Contains("peak") || summary.PeakExtraction) &&
        (!summary.Regions.Contains("nadir") || summary.NadirEntered);

    public static string[] Keys(ReplayRegionSummary? summary, bool fullRun)
    {
        if (summary == null || !Valid(summary)) return Array.Empty<string>();
        var keys = summary.Regions.Where(key => key != "peak" || !summary.NadirEntered).ToArray();
        if (fullRun || keys.Length == 0) return keys;
        string last = keys.Contains(summary.LastRegion) ? summary.LastRegion : keys[keys.Length - 1];
        return new[] { last };
    }

    // Native Segment is NOT the MapHandler array: Peak=5 shares array entry4;
    // Void=6 uses entry5. Only supported native routes get ordinary biome art.
    internal static string Key(string[] route, int segment)
    {
        if (segment == 6) return "nadir"; // Actual recorded Void frame, not route availability.
        int index = segment == 5 ? 4 : segment;
        if (index < 0 || index > 4 || index >= route.Length) return "";
        string biome = route[index].Trim();
        return index switch
        {
            0 when biome == "Shore" => "shore",
            1 when biome == "Roots" => "roots",
            1 when biome == "Tropics" => "tropics",
            2 when biome == "Alpine" => "alpine",
            2 when biome == "Mesa" => "mesa",
            3 when biome == "Volcano" => "volcano",
            3 when biome == "Swamp" => "swamp",
            4 when biome == "Volcano" && route[3].Trim() == "Volcano" => "kiln",
            4 when (biome == "Swamp" || biome == "Temple") && route[3].Trim() == "Swamp" => "temple",
            _ => "",
        };
    }
}

// Used only on save/I/O workers. At most eleven keys survive an arbitrarily long
// recording; repeated 60 Hz frames do not allocate or grow this list.
internal sealed class ReplayRegionAccumulator
{
    private readonly string[] route;
    private readonly List<string> regions = new(11);
    private string last = "";
    private int previousSegment = -1;
    private bool peakExtraction, nadirEntered;

    public ReplayRegionAccumulator(ReplayHeader header) => route = (header.Route ?? "").Split(',');
    public void Observe(ReplayFrame frame) => Observe(frame.World.Segment, frame.CoverPeakExtraction, frame.CoverNadirEntered);
    public void Observe(int segment, bool extracted = false, bool priorNadir = false)
    {
        nadirEntered |= segment == 6 || priorNadir;
        if (nadirEntered) regions.Remove("peak");
        if (segment != previousSegment)
        {
            previousSegment = segment;
            string key = ReplayRegionCovers.Key(route, segment);
            if (key.Length != 0) Add(key);
        }
        if (extracted && !nadirEntered)
        { peakExtraction = true; Add("peak"); }
    }
    private void Add(string key)
    {
        if (!regions.Contains(key)) regions.Add(key);
        last = key;
    }
    public void ObserveOutcome(ReplayRegionOutcome? outcome, double start, double end)
    {
        if (outcome == null || !ReplayRules.Finite(outcome.Time) || outcome.Time < start || outcome.Time > end + .5) return;
        if (outcome.Nadir)
        { nadirEntered = true; regions.Remove("peak"); Add("nadir"); }
        else if (!nadirEntered) { peakExtraction = true; Add("peak"); }
    }
    public ReplayRegionSummary Snapshot() => new()
    {
        Regions = regions.ToArray(), LastRegion = regions.Contains(last) ? last : regions.LastOrDefault() ?? "",
        PeakExtraction = peakExtraction, NadirEntered = nadirEntered,
    };
}
