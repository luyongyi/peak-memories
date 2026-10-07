using System;

namespace PeakReplayLab;

// Legacy persisted values now select only the optional full-run writer.
// Both known values retain the rolling highlight cache; F6 never toggles it.
public enum ReplayRecordingMode { Rolling120 = 0, Continuous = 1 }

public static class RecordingModePolicy
{
    public static bool IsKnown(ReplayRecordingMode mode) => mode == ReplayRecordingMode.Rolling120 || mode == ReplayRecordingMode.Continuous;
    public static bool CanSwitch(bool isTitle, bool writerActive, bool writerClosing, bool highlightSaving, bool replayActive = false) =>
        isTitle && !writerActive && !writerClosing && !highlightSaving && !replayActive;
    public static bool CanSaveHighlight(ReplayRecordingMode mode) => IsKnown(mode);
    public static bool CanStartContinuous(ReplayRecordingMode mode) => mode == ReplayRecordingMode.Continuous;
    public static bool KeepsRollingHistory(ReplayRecordingMode mode) => IsKnown(mode);
    public static int RetainedFrameLimit(ReplayRecordingMode mode) => mode switch
    {
        ReplayRecordingMode.Rolling120 => ReplayRules.MaxFrames,
        ReplayRecordingMode.Continuous => ReplayRules.MaxFrames,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
