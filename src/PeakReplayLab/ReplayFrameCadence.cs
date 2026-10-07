using System;

namespace PeakReplayLab;

// Actual rendered-frame intervals, independent of recording's capped sample
// clock. Fixed windows; sorting/reporting runs only on a diagnostic request.
internal sealed class ReplayFrameCadence
{
    internal sealed class Report
    {
        public int Frames { get; set; }
        public long ObservedFrames { get; set; }
        public double AverageFrameMs { get; set; }
        public double AverageFps { get; set; }
        public double P95FrameMs { get; set; }
        public double P99FrameMs { get; set; }
        public double MaximumFrameMs { get; set; }
        public double? OnePercentLowFps { get; set; }
        public double? PointOnePercentLowFps { get; set; }
    }
    private sealed class Window
    {
        public const int Capacity = 8192;
        private readonly double[] recent = new double[Capacity];
        private readonly double[] sorted = new double[Capacity];
        private int count, cursor;
        private long observed;
        public void Add(double milliseconds)
        { recent[cursor] = milliseconds; cursor = (cursor + 1) % Capacity; count = Math.Min(Capacity, count + 1); observed++; }
        public Report Snapshot()
        {
            if (count == 0) return new Report();
            Array.Copy(recent, sorted, count); Array.Sort(sorted, 0, count);
            double total = 0; for (int i = 0; i < count; i++) total += sorted[i];
            double Tail(int n)
            { double sum = 0; for (int i = count - n; i < count; i++) sum += sorted[i]; return 1000 * n / sum; }
            return new Report
            {
                Frames = count, ObservedFrames = observed, AverageFrameMs = total / count,
                AverageFps = 1000 * count / total,
                P95FrameMs = sorted[(int)Math.Ceiling(count * .95) - 1],
                P99FrameMs = sorted[(int)Math.Ceiling(count * .99) - 1], MaximumFrameMs = sorted[count - 1],
                OnePercentLowFps = count >= 100 ? Tail((int)Math.Ceiling(count * .01)) : null,
                PointOnePercentLowFps = count >= 1000 ? Tail((int)Math.Ceiling(count * .001)) : null,
            };
        }
        public void Reset() { count = cursor = 0; observed = 0; }
    }
    private readonly Window recording = new(), stopped = new();
    private bool primed, previousRecording;
    private double warmup;
    // Initial discovery, mode changes and background sealing are excluded from
    // steady-state comparison. The subsequent 2-second warmup is explicit.
    public void Observe(double seconds, bool isRecording, bool eligible)
    {
        if (!eligible || !ReplayRules.Finite(seconds) || seconds <= 0 || seconds > 5)
        { primed = false; warmup = 0; return; }
        if (!primed) { primed = true; previousRecording = isRecording; warmup = 2; return; }
        // deltaTime belongs to the preceding rendered frame's work, so charge
        // the preceding mode before changing the next frame's mode.
        if (warmup > 0) warmup -= seconds;
        else (previousRecording ? recording : stopped).Add(seconds * 1000);
        if (isRecording != previousRecording) warmup = 2;
        previousRecording = isRecording;
    }
    public Report Recording => recording.Snapshot();
    public Report Stopped => stopped.Snapshot();
    public void Reset() { recording.Reset(); stopped.Reset(); primed = false; warmup = 0; }
}
