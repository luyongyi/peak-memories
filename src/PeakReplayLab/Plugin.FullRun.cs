using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace PeakReplayLab;

public sealed partial class Plugin
{
    private sealed class FullRunEntry
    {
        public string Path = "", Error = "";
        public FullReplayInfo? Info;
        public long Bytes;
    }
    private ConfigEntry<ReplayRecordingMode> recordingMode = null!;
    private ReplayRecordingMode activeRecordingMode;
    private ConfigEntry<Key> fullRunKey = null!;
    private FullReplayWriter? fullRun, closingFullRunWriter;
    private Task<FullReplayResult>? closingFullRun;
    private Action<ReplayFrame, long> fullRunSink = null!;
    private bool fullRunLibrary;
    private int stoppedFullRunScene = int.MinValue;
    private string cachedFullRunStatus = "";
    private double nextFullRunStatus;
    private Task<FullRunEntry[]>? fullRunListing;
    private FullRunEntry[] fullRunEntries = Array.Empty<FullRunEntry>();
    private bool IsContinuous => activeRecordingMode == ReplayRecordingMode.Continuous;
    private string FullRunDirectoryPath => automationOutputRoot != null ? Path.Combine(automationOutputRoot, "Recordings") :
        Path.Combine(Paths.BepInExRootPath, "PeakReplayLab", "Recordings");
    private string ModeHelp => IsContinuous
        ? $"持续录制 · 每局一个完整录像 · {fullRunKey.Value} 停止 / 重新开始"
        : $"内存 120 秒 · 按 {saveKeyLabel} 保存过去的片段 · 不自动写录像";

    private void InitializeFullRuns()
    {
        recordingMode = Config.Bind("Recording", "Mode", ReplayRecordingMode.Rolling120,
            "Two exclusive modes: Rolling120 keeps the previous 120 seconds in RAM until F6 Save; Continuous auto-starts one streamed .peakrun per live island, F4 stops/restarts. Switch in main-menu Memories.");
        activeRecordingMode = recordingMode.Value;
        if (!RecordingModePolicy.IsKnown(activeRecordingMode))
        {
            activeRecordingMode = ReplayRecordingMode.Rolling120;
            Logger.LogWarning("Unknown Recording.Mode; using Rolling120 without continuous writes.");
        }
        fullRunKey = Config.Bind("Controls", "FullRunKey", Key.F4,
            "In Continuous mode: stop/start a full recording on a live island. Choose the mode from main-menu Memories.");
        fullRunSink = AcceptFullRunFrame;
        fullRunKey.SettingChanged += (_, _) => nextFullRunStatus = 0;
        status = ModeHelp;
    }

    private void SetRecordingMode(ReplayRecordingMode mode, bool persist = true)
    {
        if (persist && automationOutputRoot != null) { Note("自动验收会话结束后才能切换常规录制模式。"); return; }
        if (mode == activeRecordingMode) return;
        if (LibraryManagementBusy || theatre?.Busy == true || ReplaySafety.Active || !RecordingModePolicy.CanSwitch(
            SceneManager.GetActiveScene().name == "Title", fullRun != null, closingFullRun != null, saving != null))
        { Note("请回到主菜单并等保存结束，再切换录制模式。"); return; }
        // Never let a two-frame live window be mistaken for a 120-second highlight.
        capture?.Dispose(); capture = null; captureScene = int.MinValue;
        activeRecordingMode = mode;
        if (persist) { recordingMode.Value = mode; Config.Save(); }
        stoppedFullRunScene = int.MinValue;
        nextCaptureRetry = nextStatsTick = nextFullRunStatus = 0;
        cachedStats = null; cachedFullRunStatus = "";
        Note(IsContinuous ? "已切换：持续录制。进入关卡自动写入一个完整文件，F4 停止；不保留 120 秒滚动缓存。"
            : "已切换：内存 120 秒。按 F6 才保存，不持续写盘；已清空上个模式的临时缓存。", notify: false);
    }

    private void StartAutomaticFullRun()
    {
        if (!IsContinuous || fullRun != null || closingFullRun != null || capture == null ||
            stoppedFullRunScene == captureScene) return;
        // No per-frame retry on failed file creation. F4 permits an explicit retry.
        stoppedFullRunScene = captureScene;
        StartFullRun();
    }

    private void ToggleFullRun()
    {
        if (!IsContinuous) { Note("当前是内存 120 秒模式，请按 F6 保存；可在主菜单回忆录切换到持续录制。"); return; }
        if (theatre?.Busy == true || ReplaySafety.Active) { Note("观看回放时不会再次录制。"); return; }
        if (fullRun != null) { StopFullRun("user-stop"); Note("持续录制已停止，正在封存完整录像…"); return; }
        if (closingFullRun != null) { Note("上一份录像正在封存，请等完成提示。"); return; }
        if (capture == null || captureScene != SceneManager.GetActiveScene().handle ||
            !GameHandler.IsOnIslandAndInitialized || !Character.localCharacter || Character.localCharacter.inAirport)
        { Note("持续录制模式已选择，进入真实关卡后会自动开始。"); return; }
        stoppedFullRunScene = captureScene;
        // A deliberate restart needs a fresh complete baseline, including
        // entities created/destroyed while continuous sampling was stopped.
        capture.Dispose(); capture = null;
        capture = new RollingCapture(sampleHz.Value, budgetMiB.Value * 1024L * 1024,
            message => Logger.LogWarning(message), keepRollingHistory: false);
        ReplayPerformance.Reset();
        StartFullRun();
    }

    private void StartFullRun()
    {
        if (!IsContinuous || capture == null || fullRun != null || closingFullRun != null) return;
        fullRun = new FullReplayWriter(FullRunDirectoryPath, capture.Header);
        AutomationRecordingStarted();
        nextFullRunStatus = 0;
        Logger.LogInfo("Continuous recording started: " + fullRun.FilePath);
        Note("持续录制已开始", notify: false);
    }

    private void AcceptFullRunFrame(ReplayFrame frame, long estimatedBytes)
    {
        var writer = fullRun;
        if (writer != null && !writer.TryEnqueue(frame, estimatedBytes)) StopFullRun("writer-rejected-frame");
    }
    private void StopFullRun(string reason)
    {
        var writer = fullRun;
        if (writer == null) return;
        SavePerformanceReport(reason, writer.FilePath);
        stoppedFullRunScene = captureScene;
        fullRun = null; closingFullRunWriter = writer;
        closingFullRun = writer.CompleteAsync(reason); nextFullRunStatus = 0;
    }
    private void InterruptFullRun(Exception error)
    {
        RecordAutomationError(error);
        var writer = fullRun;
        if (writer == null) return;
        SavePerformanceReport("capture-error", writer.FilePath);
        stoppedFullRunScene = captureScene;
        fullRun = null; closingFullRunWriter = writer;
        closingFullRun = writer.InterruptAsync("capture-error", error); nextFullRunStatus = 0;
    }

    private void UpdateFullRuns()
    {
        if (fullRun != null && (fullRun.Fault != null || fullRun.Completion.IsCompleted)) StopFullRun("writer-ended");
        if (closingFullRun?.IsCompleted == true)
        {
            try
            {
                var result = closingFullRun.GetAwaiter().GetResult();
                AutomationRecordingFinished(result);
                Logger.LogInfo($"Continuous recording {result.Status}: {result.FilePath}; reason={result.Reason}; " +
                    $"accepted={result.AcceptedFrames}, written={result.WrittenFrames}, unwritten={result.UnwrittenFrames}, " +
                    $"bytes={result.BytesWritten}, gaps100ms={result.GapOver100ms}, gaps500ms={result.GapOver500ms}, maxGap={result.LargestGapSeconds:F3}s.");
                if (result.Status != "completed")
                {
                    Logger.LogWarning("Continuous recording fault: " + result.FaultCode + ": " + result.Fault);
                    Note($"持续录制异常（{result.FaultCode}）· 已保留未完成文件供排查，请保留录像和日志。");
                }
                else Note($"完整录像已保存 · {ClockLabel(result.Duration)} · {result.BytesWritten / 1000000d:F1} MB · 回忆录 → 完整录像");
                if (library && fullRunLibrary) RefreshFullRuns();
            }
            catch (Exception e) { RecordAutomationError(e); Logger.LogError(e); Note("整局录像封存失败，请保留 Recordings 文件与日志：" + e.Message); }
            finally { closingFullRun = null; closingFullRunWriter = null; nextFullRunStatus = 0; }
        }
        if (fullRunListing?.IsCompleted == true)
        {
            try
            {
                fullRunEntries = fullRunListing.GetAwaiter().GetResult();
                fullRunCards = fullRunEntries.Select(e => MemoriesLibraryItem.FromFullRun(e.Path, e.Info, e.Bytes, e.Error)).ToArray();
            }
            catch (Exception e) { Logger.LogWarning(e); Note("完整录像列表读取失败：" + e.Message); }
            finally { fullRunListing = null; }
        }
    }

    private string FullRunStatus()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (now < nextFullRunStatus) return cachedFullRunStatus;
        nextFullRunStatus = now + .5;
        if (fullRun != null)
        {
            var stats = fullRun.Stats;
            cachedFullRunStatus = $"持续录制 ● {ClockLabel(stats.NativeEnd - stats.NativeStart)} · {stats.BytesWritten / 1000000d:F1} MB · " +
                $"待写 {stats.QueuedFrames} 帧 · {fullRunKey.Value} 停止";
        }
        else if (closingFullRun != null) cachedFullRunStatus = "持续录制 · 正在封存完整录像…";
        else if (IsContinuous) cachedFullRunStatus = stoppedFullRunScene != int.MinValue && stoppedFullRunScene == captureScene && capture != null && SceneManager.GetActiveScene().handle == captureScene
            ? $"持续录制已停止 · {fullRunKey.Value} 开始新录像" : "持续录制模式 · 进入关卡后自动开始 · 一个完整文件";
        else cachedFullRunStatus = "";
        return cachedFullRunStatus;
    }

    private void WaitForFullRunShutdown()
    {
        var completion = closingFullRun;
        if (completion == null) return;
        try
        {
            if (!completion.Wait(1500)) Logger.LogWarning("Continuous recording shutdown drain timed out; .partial is incomplete. " + closingFullRunWriter?.PartialPath);
            else
            {
                var result = completion.GetAwaiter().GetResult();
                Logger.LogInfo($"Continuous recording quit: {result.Status}, unwritten={result.UnwrittenFrames}; {result.FilePath}");
            }
        }
        catch (Exception e) { Logger.LogWarning("Continuous recording shutdown: " + e.Message); }
    }

    private void RefreshFullRuns()
    {
        if (fullRunListing != null || LibraryManagementBusy) return;
        string root = FullRunDirectoryPath;
        fullRunListing = Task.Run(() =>
        {
            if (!Directory.Exists(root)) return Array.Empty<FullRunEntry>();
            return Directory.EnumerateFiles(root, "*.peakrun*").Take(2048)
                .Where(p => p.EndsWith(".peakrun", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".peakrun.partial", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(Path.GetFileName).Take(128).Select(path =>
                {
                    var entry = new FullRunEntry { Path = path };
                    try
                    {
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不读取链接文件。");
                        entry.Bytes = new FileInfo(path).Length;
                        if (path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) entry.Error = "未完成 / 录制中或异常中断，暂不可播放。";
                        else
                        {
                            var info = FullReplayArchive.ReadInfo(path);
                            if (!info.Complete) entry.Error = "录像未完整结束，不可作为完整录像播放。";
                            else entry.Info = info;
                        }
                    }
                    catch (Exception e) { entry.Error = e.Message; }
                    return entry;
                }).ToArray();
        });
    }

    private static string ClockLabel(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}" : $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";
    }

}
