using System;

namespace PeakReplayLab;

// A new host must not spawn into a progressively constructed replay baseline.
// This gate owns no Unity objects, pauses no clocks and never invents old state.
internal sealed class CaptureBootstrapGate
{
    internal const double MaximumWaitSeconds = 60;
    private readonly double startedAt;
    private readonly double maximumWait;
    private int mapReadyFrame = -1;
    internal bool HostObserved { get; private set; }
    internal bool RestoreRequired { get; private set; }
    internal bool RestorePrepared { get; private set; }
    internal bool BaselineReady { get; private set; }
    internal bool Failed { get; private set; }
    internal bool Cancelled { get; private set; }
    internal string Failure { get; private set; } = "";
    private bool skipNativeRestore;

    internal CaptureBootstrapGate(double now, double maximumWait = MaximumWaitSeconds)
    {
        if (!Finite(now) || !Finite(maximumWait) || maximumWait <= 0)
            throw new ArgumentOutOfRangeException(nameof(now));
        startedAt = now; this.maximumWait = maximumWait;
    }
    internal bool Healthy => !Failed && !Cancelled;
    internal static bool AllowLiveCapture(bool mapInitialized, bool localCharacter, bool localInAirport, bool observedActors) =>
        mapInitialized && (localCharacter && !localInAirport || observedActors);
    internal static bool AllowRecordingStart(bool sceneFailed, bool hostObserved, bool baselineReady, bool observedActorPrefix) =>
        !sceneFailed && (!hostObserved || baselineReady || observedActorPrefix);
    internal bool CanPrepare => Healthy && mapReadyFrame >= 0 && (!RestoreRequired || RestorePrepared);
    internal bool NativeFramesElapsed(int frame) => mapReadyFrame >= 0 && frame - mapReadyFrame >= 3;
    internal void RequireRestore(bool required)
    {
        if (required && !RestoreRequired) { RestoreRequired = true; BaselineReady = false; }
    }
    internal void ObserveMap(bool ready, int frame, double now)
    {
        CheckDeadline(now);
        if (Healthy && ready && mapReadyFrame < 0) mapReadyFrame = frame;
    }
    internal void ObserveHost(bool mapReady, bool restoreRequired, int frame, double now)
    {
        HostObserved = true; RequireRestore(restoreRequired);
        ObserveMap(mapReady, frame, now);
    }
    internal bool NeedsRestore(int frame) => Healthy && RestoreRequired && !RestorePrepared && NativeFramesElapsed(frame);
    internal void Restored()
    {
        if (!Healthy || !RestoreRequired || RestorePrepared) throw new InvalidOperationException("Unexpected bootstrap restore.");
        RestorePrepared = true; skipNativeRestore = true;
    }
    // Only the original host-spawn body may consume this token. Other save
    // queries/restoration calls remain native, including the early restore above.
    internal bool AllowNativeRestore(bool insideHostSpawn)
    {
        if (!insideHostSpawn || !skipNativeRestore) return true;
        skipNativeRestore = false; return false;
    }
    internal void CompleteBaseline()
    {
        if (!CanPrepare) throw new InvalidOperationException("Bootstrap baseline completed before native map restoration.");
        BaselineReady = true;
    }
    internal bool AllowHostSpawn(int frame, double now)
    {
        CheckDeadline(now);
        // Failure is open for the game and closed for recording. The plugin
        // reports the failure and disables this scene's automatic writer.
        return !Healthy || BaselineReady && NativeFramesElapsed(frame) && (!RestoreRequired || RestorePrepared);
    }
    internal void CheckDeadline(double now)
    {
        if (!Healthy || BaselineReady) return;
        if (!Finite(now) || now < startedAt || now - startedAt >= maximumWait)
            Fail("回放初态准备超时，已停止本关自动录像以避免保存不完整开头。");
    }
    internal void Fail(string reason)
    {
        if (Failed || Cancelled) return;
        Failed = true; Failure = string.IsNullOrWhiteSpace(reason) ? "回放初态准备失败。" : reason;
    }
    internal void Cancel() { Cancelled = true; skipNativeRestore = false; }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
