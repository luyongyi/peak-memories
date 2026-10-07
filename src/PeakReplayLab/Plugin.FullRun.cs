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
    private ConfigEntry<bool> continuousRecordingEnabled = null!;
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
    private string ModeHelp => $"按 {saveKeyLabel} 随时保存最近最多 120 秒 · " + (IsContinuous
        ? $"完整录制已开启 · {fullRunKey.Value} 关闭并封存"
        : $"完整录制已关闭 · {fullRunKey.Value} 开启");

    private void InitializeFullRuns()
    {
        recordingMode = Config.Bind("Recording", "Mode", ReplayRecordingMode.Rolling120,
            "Legacy setting used only to initialize ContinuousEnabled when that setting is absent. Rolling120 disables full recording; Continuous enables it. Both now keep a 120-second highlight window.");
        bool legacyEnabled = RecordingModePolicy.IsKnown(recordingMode.Value) && recordingMode.Value == ReplayRecordingMode.Continuous;
        if (!RecordingModePolicy.IsKnown(recordingMode.Value)) Logger.LogWarning("Unknown legacy Recording.Mode; defaulting full recording to off.");
        continuousRecordingEnabled = Config.Bind("Recording", "ContinuousEnabled", legacyEnabled,
            "Record one .peakrun per live island automatically. F4 toggles this independent full-recording setting and seals the current file when disabled. F6 highlights remain available whether this is on or off.");
        activeRecordingMode = continuousRecordingEnabled.Value ? ReplayRecordingMode.Continuous : ReplayRecordingMode.Rolling120;
        fullRunKey = Config.Bind("Controls", "FullRunKey", Key.F4,
            "Toggle full recording on/off. Disabling seals the current file; enabling starts a new file on the live island. Does not clear the F6 rolling highlight window.");
        fullRunSink = AcceptFullRunFrame;
        fullRunKey.SettingChanged += (_, _) => nextFullRunStatus = 0;
        status = ModeHelp;
    }

    private void SetRecordingMode(ReplayRecordingMode mode, bool persist = true)
    {
        if (!RecordingModePolicy.IsKnown(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (persist && automationOutputRoot != null) { Note("自动验收会话结束后才能修改完整录制设置。"); return; }
        if (mode == activeRecordingMode) return;
        if (LibraryManagementBusy || RouteUploadBusy || theatre?.Busy == true || ReplaySafety.Active || !RecordingModePolicy.CanSwitch(
            SceneManager.GetActiveScene().name == "Title", fullRun != null, closingFullRun != null, saving != null))
        { Note("请回到主菜单并等保存结束，再修改完整录制设置；游戏中可按 F4 开关。"); return; }
        ApplyFullRecordingSetting(mode == ReplayRecordingMode.Continuous, persist);
        stoppedFullRunScene = int.MinValue;
        nextCaptureRetry = nextStatsTick = nextFullRunStatus = 0;
        cachedStats = null; cachedFullRunStatus = "";
        Note(IsContinuous ? "完整录制已开启，进入关卡自动开始；F6 仍可随时保存最近最多 120 秒。"
            : "完整录制已关闭；120 秒缓存保留，F6 随时保存精彩片段。", notify: false);
    }

    private void ApplyFullRecordingSetting(bool enabled, bool persist)
    {
        activeRecordingMode = enabled ? ReplayRecordingMode.Continuous : ReplayRecordingMode.Rolling120;
        if (persist) continuousRecordingEnabled.Value = enabled;
        nextStatsTick = nextFullRunStatus = 0;
        cachedStats = null; cachedFullRunStatus = "";
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
        if (theatre?.Busy == true || ReplaySafety.Active) { Note("观看回放时不会再次录制。"); return; }
        if (closingFullRun != null) { Note("上一份录像正在封存，请等完成提示。"); return; }
        bool persist = automationOutputRoot == null;
        if (fullRun != null)
        {
            ApplyFullRecordingSetting(false, persist);
            StopFullRun("user-stop");
            Note("完整录制已关闭，正在封存录像；F6 片段缓存继续。");
            return;
        }
        if (capture == null || captureScene != SceneManager.GetActiveScene().handle ||
            !GameHandler.IsOnIslandAndInitialized || !Character.localCharacter || Character.localCharacter.inAirport)
        {
            ApplyFullRecordingSetting(!IsContinuous, persist);
            stoppedFullRunScene = int.MinValue;
            Note(IsContinuous ? "完整录制已开启，进入真实关卡后自动开始；F6 片段可随时保存。"
                : "完整录制已关闭；F6 片段缓存保持。");
            return;
        }
        ApplyFullRecordingSetting(true, persist);
        stoppedFullRunScene = captureScene;
        // Capture has continued throughout: its next immutable frame already
        // contains a complete world baseline. Keep the existing F6 history.
        StartFullRun();
    }

    private void StartFullRun()
    {
        if (!IsContinuous || capture == null || fullRun != null || closingFullRun != null) return;
        // The capture can predate this file by minutes. Clip snapshots have
        // their own UTC window; update only the metadata for this new run.
        capture.Header.StartedUtc = DateTime.UtcNow.ToString("O");
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
        else cachedFullRunStatus = $"完整录制已关闭 · {fullRunKey.Value} 开启 · {saveKeyLabel} 保存片段";
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
