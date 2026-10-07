using System;

namespace PeakReplayLab;

// Native shader time has an arbitrary phase when a replay scene finishes loading.
// Retain that phase, then address visual animation by replay time rather than by
// rendered frames. No integration means pause, buffering, speed and seeks agree.
internal sealed class ReplayVisualClock
{
    private readonly double phase;
    private float value;
    private bool ready;
    private bool active = true;

    public ReplayVisualClock(float nativePhase)
    {
        if (float.IsNaN(nativePhase) || float.IsInfinity(nativePhase) || nativePhase < 0)
            throw new ArgumentOutOfRangeException(nameof(nativePhase));
        phase = nativePhase;
    }

    public void SetTime(double recordingTime)
    {
        if (!active) return;
        double resolved = phase + recordingTime;
        // URP also writes _Time.w = time * 3; reject an unusable clock before
        // publishing any shader state. Actual replay times are far below this.
        if (double.IsNaN(recordingTime) || double.IsInfinity(recordingTime) || recordingTime < 0 || resolved > float.MaxValue / 3d)
            throw new ArgumentOutOfRangeException(nameof(recordingTime));
        value = (float)resolved; ready = true;
    }

    public bool Override(bool replayActive, bool presenting, ref float nativeTime)
    {
        if (!active || !ready || !replayActive || !presenting) return false;
        nativeTime = value;
        return true;
    }

    public void Close() { active = false; ready = false; }
}
