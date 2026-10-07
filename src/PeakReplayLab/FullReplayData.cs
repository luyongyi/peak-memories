using System;

namespace PeakReplayLab;

// The pages are an internal seek index in ONE physical file, not recordings,
// scene transitions, sidecars or user-visible segments.
public sealed class FullReplayPage
{
    public int Index { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
    public long Offset { get; set; }
    public long Length { get; set; }
    public long RawBytes { get; set; }
    public long DecodedBytes { get; set; }
    public int FrameCount { get; set; }
    public long FirstFrameIndex { get; set; }
    public bool Overlap { get; set; }
}

public class FullReplayIndex
{
    public ReplayRegionSummary? RegionSummary { get; set; }
    public int Version { get; set; } = 1;
    public ReplayHeader Header { get; set; } = new();
    public double Duration { get; set; }
    public long FrameCount { get; set; }
    public string[] ActorIds { get; set; } = Array.Empty<string>();
    public bool Complete { get; set; }
    public string Reason { get; set; } = "";
    public string? FaultCode { get; set; }
    public string? Fault { get; set; }
    public FullReplayPage[] Pages { get; set; } = Array.Empty<FullReplayPage>();
    public long GapOver100ms { get; set; }
    public long GapOver500ms { get; set; }
    public double LargestGapSeconds { get; set; }
}

public sealed class FullReplayInfo : FullReplayIndex
{
    internal long FileLength, HeaderEnd, IndexOffset;
    internal string Path = "";
}

public sealed class FullReplayResult
{
    public string FilePath { get; internal set; } = "";
    public string Status { get; internal set; } = "completed";
    public string Reason { get; internal set; } = "manual-stop";
    public string? FaultCode { get; internal set; }
    public string? Fault { get; internal set; }
    public long AcceptedFrames { get; internal set; }
    public long WrittenFrames { get; internal set; }
    public long UnwrittenFrames { get; internal set; }
    public int CompletedPages { get; internal set; }
    public int CompletedSegments => CompletedPages;
    public long BytesWritten { get; internal set; }
    public long GapOver100ms { get; internal set; }
    public long GapOver500ms { get; internal set; }
    public double LargestGapSeconds { get; internal set; }
    public double Duration { get; internal set; }
}
