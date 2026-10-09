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
    private Task? performanceSaving;

    private void ObserveGameFrame()
    {
        bool eligible = performanceMetrics.Value && !disabledByError && !ReplaySafety.Active && theatre?.Busy != true &&
            closingFullRun == null && capture != null && GameHandler.IsOnIslandAndInitialized &&
            Character.localCharacter && !Character.localCharacter.inAirport;
        // The rolling window is always sampled. Compare additional full-file
        // writing on/off, not an inactive recorder against active capture.
        frameCadence.Observe(Time.unscaledDeltaTime, fullRun != null, eligible);
    }
    private void SavePerformanceReport(string reason, string? recordingFile = null)
    {
        if (!performanceMetrics.Value) return;
        Logger.LogInfo(ReplayPerformance.Report());
        if (performanceSaving != null && !performanceSaving.IsCompleted) return;
        var report = new
        {
            Schema = 1, Recorder = "PeakReplayLab/0.8.2", CreatedUtc = DateTime.UtcNow.ToString("O"),
            Scene = capture?.Header.Scene ?? SceneManager.GetActiveScene().name,
            CurrentScene = SceneManager.GetActiveScene().name, Reason = reason, Recording = recordingFile,
            TargetSampleHz = sampleHz.Value,
            Measurement = "Rolling highlight capture stays active in both groups. RecordingFrames measures full-file writing enabled; StoppedFrames measures full-file writing disabled. CPU scopes include initial capture and accumulate since CPU reset; CPU percentiles use latest 256 scopes. Unity unscaled rendered-frame windows use latest 8192 eligible frames per group, excluding background sealing and the first 2 seconds after phase changes. No GPU attribution. Low FPS = reciprocal of mean slowest ceil(N*percent) frame times, requires >=100/1000 frames.",
            Stages = ReplayPerformance.Snapshot(), RecordingFrames = frameCadence.Recording, StoppedFrames = frameCadence.Stopped,
            GlobalGarbageCollectionsSinceCpuReset = ReplayPerformance.GarbageCollectionsSinceReset(),
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
