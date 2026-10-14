using System;
using System.Collections;
using Photon.Pun;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakReplayLab;

public sealed partial class Plugin
{
    private CaptureBootstrapHooks? bootstrapHooks;
    private NativeSpawnOriginCapture? nativeSpawnOrigins;
    private CaptureBootstrapGate? bootstrapGate;
    private int bootstrapScene = int.MinValue, bootstrapFailedScene = int.MinValue, bootstrapStepFrame = -1;
    private bool bootstrapFailureReported;
    private double bootstrapStartedAt, bootstrapCompletedAt;

    private bool BootstrapContextAllowed => !disabledByError && !quitting && !automationEnding &&
        !ReplaySafety.Active && theatre?.Busy != true;
    private static bool NativeIslandScene => MapHandler.Exists ||
        SceneManager.GetActiveScene().name.StartsWith("Level_", StringComparison.Ordinal);
    private bool BootstrapSceneFailed => bootstrapFailedScene == SceneManager.GetActiveScene().handle;
    internal object BootstrapDiagnostics => new
    {
        Scene = bootstrapScene, HostObserved = bootstrapGate?.HostObserved ?? false,
        RestoreRequired = bootstrapGate?.RestoreRequired ?? false,
        RestorePrepared = bootstrapGate?.RestorePrepared ?? false,
        Ready = capture?.BootstrapReady ?? false, Failed = BootstrapSceneFailed,
        Failure = bootstrapGate?.Failure ?? "", StartedAt = bootstrapStartedAt, CompletedAt = bootstrapCompletedAt,
        Pending = capture?.BootstrapPendingCount ?? 0,
        ObservedActorPrefix = capture?.BootstrapHasObservedActors ?? false,
        Capture = capture?.BootstrapDiagnostics,
        SpawnOrigins = nativeSpawnOrigins?.Count ?? 0, SpawnOriginHookFailures = nativeSpawnOrigins?.HookFailures ?? 0,
        Guarantee = "A gated new local host spawns only after the current native map baseline is ready. Existing actors and client loading prefixes continue to be recorded; no prejoin history or late-read backfill is claimed.",
    };

    private void InitializeCaptureOrigins()
    {
        nativeSpawnOrigins = new NativeSpawnOriginCapture(message => Logger.LogWarning(message));
        NativeSpawnOriginCapture.OriginChanged += RefreshCapturedOrigin;
    }
    private void RefreshCapturedOrigin(GameObject source) => capture?.RefreshOrigin(source);
    private void DisposeCaptureOrigins()
    {
        NativeSpawnOriginCapture.OriginChanged -= RefreshCapturedOrigin;
        nativeSpawnOrigins?.Dispose(); nativeSpawnOrigins = null;
    }
    private void InitializeCaptureBootstrap() => bootstrapHooks = new CaptureBootstrapHooks(
        AllowNativeHostSpawn, insideHost => bootstrapGate?.AllowNativeRestore(insideHost) ?? true,
        WaitForCaptureBootstrap, FailCaptureBootstrap);

    private CaptureBootstrapGate CurrentBootstrapGate()
    {
        int scene = SceneManager.GetActiveScene().handle;
        if (bootstrapGate != null && bootstrapScene == scene) return bootstrapGate;
        bootstrapGate?.Cancel();
        bootstrapScene = scene; bootstrapStepFrame = -1; bootstrapFailureReported = false;
        bootstrapStartedAt = Time.realtimeSinceStartupAsDouble; bootstrapCompletedAt = 0;
        return bootstrapGate = new CaptureBootstrapGate(bootstrapStartedAt);
    }

    private bool AllowNativeHostSpawn()
    {
        if (!BootstrapContextAllowed || !NativeIslandScene || !PhotonNetwork.IsMasterClient || Character.localCharacter)
            return true;
        var gate = CurrentBootstrapGate();
        gate.ObserveHost(GameHandler.IsOnIslandAndInitialized, Peak.Quicksave.ShouldUseSaveData,
            Time.frameCount, Time.realtimeSinceStartupAsDouble);
        if (gate.NeedsRestore(Time.frameCount))
        {
            // This is exactly the native host restore operation, moved before
            // cooperative baseline construction. The original host body later
            // skips this one duplicate only; its spawn and RPC code is untouched.
            Peak.Quicksave.PopulateMapAndPlayerStates();
            capture?.InvalidateBootstrap();
            gate.Restored();
        }
        if (gate.Failed) ReportCaptureBootstrapFailure(gate);
        return gate.AllowHostSpawn(Time.frameCount, Time.realtimeSinceStartupAsDouble);
    }

    private void ProgressCaptureBootstrap()
    {
        if (!BootstrapContextAllowed || !NativeIslandScene || BootstrapSceneFailed ||
            !GameHandler.IsOnIslandAndInitialized) return;
        var gate = CurrentBootstrapGate();
        gate.RequireRestore(Peak.Quicksave.ShouldUseSaveData && PhotonNetwork.IsMasterClient && !Character.localCharacter);
        gate.ObserveMap(true, Time.frameCount, Time.realtimeSinceStartupAsDouble);
        if (gate.Failed) { ReportCaptureBootstrapFailure(gate); return; }
        if (!gate.CanPrepare || bootstrapStepFrame == Time.frameCount) return;
        bootstrapStepFrame = Time.frameCount;
        try
        {
            int scene = SceneManager.GetActiveScene().handle;
            if (capture == null || captureScene != scene)
            {
                capture?.Dispose();
                capture = new RollingCapture(sampleHz.Value, budgetMiB.Value * 1024L * 1024,
                    message => Logger.LogWarning(message), () => itemCaptureDistance.Value,
                    prepareEarly: Capture.Players().Length == 0);
                captureScene = scene; ReplayPerformance.Reset();
            }
            ItemEventCapture.Activate(true);
            if (capture.PrepareBootstrap() && !gate.BaselineReady)
            {
                gate.CompleteBaseline(); bootstrapCompletedAt = Time.realtimeSinceStartupAsDouble;
                Logger.LogInfo($"Replay baseline ready before host spawn: host={gate.HostObserved}, " +
                    $"elapsed={bootstrapCompletedAt - bootstrapStartedAt:F3}s.");
            }
        }
        catch (Exception error) { FailCaptureBootstrap(error); }
    }

    private IEnumerator WaitForCaptureBootstrap()
    {
        // The native loading processes execute first, including host spawn.
        // Holding host spawn keeps WaitForCharacterSpawn alive; this final
        // process additionally covers loading routes which did not wait for it.
        int scene = SceneManager.GetActiveScene().handle;
        while (BootstrapContextAllowed && NativeIslandScene && SceneManager.GetActiveScene().handle == scene)
        {
            ProgressCaptureBootstrap();
            var gate = bootstrapGate;
            if (gate == null || !gate.HostObserved || gate.BaselineReady || !gate.Healthy) yield break;
            gate.CheckDeadline(Time.realtimeSinceStartupAsDouble);
            if (gate.Failed) { ReportCaptureBootstrapFailure(gate); yield break; }
            yield return null;
        }
    }

    private void FailCaptureBootstrap(Exception error)
    {
        var gate = CurrentBootstrapGate();
        gate.Fail("回放初态准备失败，已停止本关自动录像以避免保存不完整开头：" + error.Message);
        RecordAutomationError(error); ReportCaptureBootstrapFailure(gate);
    }
    private void ReportCaptureBootstrapFailure(CaptureBootstrapGate gate)
    {
        if (bootstrapFailureReported) return;
        bootstrapFailureReported = true; bootstrapFailedScene = bootstrapScene;
        // A client prefix may already have a writer. Interrupt it explicitly;
        // never label a baseline failure as a completed full recording.
        InterruptFullRun(new InvalidOperationException(gate.Failure)); stoppedFullRunScene = bootstrapScene;
        fullRunFailure = gate.Failure; nextFullRunStatus = 0;
        capture?.Dispose(); capture = null; captureScene = int.MinValue;
        ItemEventCapture.Activate(false);
        Logger.LogError(gate.Failure); Note(gate.Failure);
    }
    private void ResetCaptureBootstrap()
    {
        bootstrapGate?.Cancel(); bootstrapGate = null;
        bootstrapScene = bootstrapFailedScene = int.MinValue; bootstrapStepFrame = -1;
        bootstrapFailureReported = false; bootstrapStartedAt = bootstrapCompletedAt = 0;
    }
}
