using System;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakReplayLab;

public sealed partial class Plugin
{
    private readonly ReplayFrameCadence frameCadence = new();
    private readonly ReplayFrameCadence playbackCadence = new();
    private readonly ReplayHitchDiagnostics playbackHitches = new();
    private PlaybackSession? measuredPlayback;
    private Task? performanceSaving;
    private int reportedSeekSequence;
    private bool reportedSeekWaiting;
    private bool performanceObservationWarned;
    private double nextSeekReportAt;

    private void ObserveGameFrame()
    {
        bool eligible = performanceMetrics.Value && !disabledByError && !ReplaySafety.Active && theatre?.Busy != true &&
            closingFullRun == null && capture != null && GameHandler.IsOnIslandAndInitialized &&
            Character.localCharacter && !Character.localCharacter.inAirport;
        // The rolling window is always sampled. Compare additional full-file
        // writing on/off, not an inactive recorder against active capture.
        frameCadence.Observe(Time.unscaledDeltaTime, fullRun != null, eligible);
        var playback = theatre?.Playback;
        if (!ReferenceEquals(measuredPlayback, playback))
        { measuredPlayback = playback; playbackCadence.Reset(); playbackHitches.Reset(); reportedSeekSequence = 0; reportedSeekWaiting = false; nextSeekReportAt = 0; }
        playbackCadence.Observe(Time.unscaledDeltaTime, false, performanceMetrics.Value &&
            playback != null && !playback.Buffering && !playback.Paused);
        playbackHitches.Observe(Time.unscaledDeltaTime, playback?.Time ?? 0, playback?.Buffering ?? false,
            playback?.Paused ?? false, GC.CollectionCount(GC.MaxGeneration), performanceMetrics.Value && playback != null);
        double now = Time.realtimeSinceStartupAsDouble;
        if (performanceMetrics.Value && playback != null &&
            (playback.SeekSequence > reportedSeekSequence ||
                playback.SeekSequence == reportedSeekSequence && reportedSeekWaiting && !playback.Buffering) &&
            now - playback.LastSeekRequestedAt >= 2 && now >= nextSeekReportAt &&
            (performanceSaving == null || performanceSaving.IsCompleted))
        {
            reportedSeekSequence = playback.SeekSequence; reportedSeekWaiting = playback.Buffering; nextSeekReportAt = now + 5;
            SavePerformanceReport(playback.Buffering ? "seek-wait" : "seek-settled");
        }
    }
    private void SavePerformanceReport(string reason, string? recordingFile = null)
    {
        if (!performanceMetrics.Value) return;
        Logger.LogInfo(ReplayPerformance.Report());
        if (performanceSaving != null && !performanceSaving.IsCompleted) return;
        var report = new
        {
            Schema = 10, Recorder = "PeakReplayLab/0.8.25", CreatedUtc = DateTime.UtcNow.ToString("O"),
            Scene = capture?.Header.Scene ?? SceneManager.GetActiveScene().name,
            CurrentScene = SceneManager.GetActiveScene().name, Reason = reason, Recording = recordingFile,
            PlaybackSourceFile = theatre?.Playback != null ? theatre.SourceFileName : null,
            TargetSampleHz = sampleHz.Value,
            SceneCaptureWork = capture?.SceneDiagnostics,
            SegmentCaptureWork = capture?.SegmentDiagnostics,
            ItemCaptureWork = capture?.ItemDiagnostics,
            RopeCaptureWork = capture?.RopeDiagnostics,
            InteractionCaptureWork = capture?.InteractionDiagnostics,
            SpawnedCaptureWork = capture?.SpawnedDiagnostics,
            CreatureCaptureWork = capture?.CreatureDiagnostics,
            AudioCaptureWork = capture?.AudioDiagnostics,
            EffectCaptureWork = capture?.EffectDiagnostics,
            Measurement = "Rolling highlight capture stays active in both recording groups. RecordingFrames measures full-file writing enabled; StoppedFrames measures full-file writing disabled. PlaybackFrames measures the current presentation while playing, excluding buffering, pause and the first 2 seconds after resuming. CPU scopes include initial capture and accumulate since CPU reset; CPU percentiles use latest 256 scopes. Unity unscaled rendered-frame windows use latest 8192 eligible frames per group, excluding background sealing and the first 2 seconds after phase changes. No GPU attribution. Low FPS = reciprocal of mean slowest ceil(N*percent) frame times, requires >=100/1000 frames. GC overlap observes maximum-generation global collections while a CPU scope is open; it does not identify the allocating thread or attribute the whole scope duration to GC. Thread allocation counters are probed once for support; zero or unavailable counters do not imply zero allocation.",
            Stages = ReplayPerformance.Snapshot(), RecordingFrames = frameCadence.Recording, StoppedFrames = frameCadence.Stopped,
            SlowCpuScopes = ReplayPerformance.SlowCpuSnapshot(),
            SlowAudioStarts = theatre?.Playback?.SlowAudioStarts,
            SlowAudioStartMeasurement = "Latest 128 native Play/UnPause calls >=16.666667ms in the current playback session, in completion order. Stopwatch timestamps immediately surround the native call; start/end realtime use the same Unity realtime calibration immediately before that call. Clip metadata is read outside this native-call interval. Frame numbers are Unity frameCount before/after the call. GlobalCollections is the maximum-generation collection-count difference around the native call, not GC duration or allocation attribution. Clip/VoiceKey are the existing recorded identifiers; no voice-chat clips are discovered. ClipBefore/ClipAfter Available=false means metadata was unavailable. LoadState uses Unity AudioDataLoadState: 0=Unloaded, 1=Loading, 2=Loaded, 3=Failed. LoadType uses Unity AudioClipLoadType: 0=DecompressOnLoad, 1=CompressedInMemory, 2=Streaming.",
            SlowCpuScopeMeasurement = "Latest 128 completed CPU scopes >=16.666667ms since CPU reset, in completion order. Nested scopes overlap and must not be added. Stopwatch timestamps are mapped to Unity realtime at LateUpdate entry; frame numbers denote the latest observed LateUpdate frame at scope start/end. HasFrameContext=false marks uncalibrated initialization/cleanup (frame/realtime=-1). EndRealtime uses the same calibration as StartRealtime. GlobalCollections counts maximum-generation collections while the scope was open, not GC duration or allocating-thread attribution. Disabling drops in-flight/new scopes; reset clears records and drops pre-reset scopes.",
            PlaybackFrames = playbackCadence.Stopped, PlaybackDiagnostic = theatre?.Playback?.PresentationDiagnostic,
            PlaybackHitches = playbackHitches.Snapshot(),
            HitchMeasurement = "Rendered frames >=33.333ms, including buffering and pause with explicit flags. GC counts cover the interval between main-thread observations; Unity frame-start timing can shift their association by one frame. Overlap is not a GC duration or allocating-thread attribution. Latest 64 slow frames are retained, counters cover the playback session.",
            PlaybackBufferingNoticeVisible = replayConsole?.IsBufferingNoticeVisible,
            RecentPlaybackSeeks = theatre?.Playback?.SeekDiagnostics.Snapshot(Time.realtimeSinceStartupAsDouble),
            GlobalGarbageCollectionsSinceCpuReset = ReplayPerformance.GarbageCollectionsSinceReset(),
            RuntimeMaximumGcGeneration = GC.MaxGeneration, ManagedHeapBytes = GC.GetTotalMemory(false),
            Queue = fullRun?.Stats,
            EnvironmentObservationAtFailure = reason == "capture-error" ? EnvironmentReplayCapture.LastObservation : null,
        };
        string directory = Path.Combine(Paths.BepInExRootPath, "PeakReplayLab", "Performance");
        string path = Path.Combine(directory, "performance-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".json");
        // The immutable, small diagnostic DTO is built here; all JSON and file
        // work stays on a background task. No recording is read or rewritten.
        performanceSaving = Task.Run(() =>
        {
            try
            { Directory.CreateDirectory(directory); File.WriteAllText(path, JsonConvert.SerializeObject(report, Formatting.Indented)); Logger.LogInfo("Replay performance report: " + path); }
            catch (Exception e) { Logger.LogWarning("Performance report save failed: " + e.Message); }
        });
    }
}
