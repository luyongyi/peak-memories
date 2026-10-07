using System;

namespace PeakReplayLab;

// A persisted selection, not two independent enable flags. Neither hotkey nor
// playback may silently enable the other recording mode.
public enum ReplayRecordingMode { Rolling120 = 0, Continuous = 1 }

public static class RecordingModePolicy
{
    public static bool IsKnown(ReplayRecordingMode mode) => mode == ReplayRecordingMode.Rolling120 || mode == ReplayRecordingMode.Continuous;
    public static bool CanSwitch(bool isTitle, bool writerActive, bool writerClosing, bool highlightSaving, bool replayActive = false) =>
        isTitle && !writerActive && !writerClosing && !highlightSaving && !replayActive;
    public static bool CanSaveHighlight(ReplayRecordingMode mode) => mode == ReplayRecordingMode.Rolling120;
    public static bool CanStartContinuous(ReplayRecordingMode mode) => mode == ReplayRecordingMode.Continuous;
    public static bool KeepsRollingHistory(ReplayRecordingMode mode) => RetainedFrameLimit(mode) > 2;
    public static int RetainedFrameLimit(ReplayRecordingMode mode) => mode switch
    {
        ReplayRecordingMode.Rolling120 => ReplayRules.MaxFrames,
        ReplayRecordingMode.Continuous => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
