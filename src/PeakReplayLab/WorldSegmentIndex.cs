using System;

namespace PeakReplayLab;

internal static class WorldSegmentIndex
{
    // WorldFrame.Segment stores the game's Segment enum, including in schema 8.
    // Peak (5) shares TheKiln's map layer (4); Void (6) uses the appended layer (5).
    // This mirrors MapHandler.JumpToSegmentLogic without changing recorded data.
    public static int Resolve(int recordedSegment, int mapSegmentCount)
    {
        if (recordedSegment < 0 || recordedSegment > 6)
            throw new InvalidOperationException("Recorded segment is not supported by this game.");
        int index = recordedSegment >= 5 ? recordedSegment - 1 : recordedSegment;
        if (index >= mapSegmentCount)
            throw new InvalidOperationException("Recorded segment is not present in this scene.");
        return index;
    }
}
