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

[BepInPlugin("cn.mylus.peakreplaylab", "PEAK Replay Lab", "0.7.4")]
[DefaultExecutionOrder(10000)]
public sealed partial class Plugin : BaseUnityPlugin
{
    private sealed class Entry { public string Path = ""; public ReplayHeader? Header; public string Error = ""; }
    private ConfigEntry<int> sampleHz = null!;
    private ConfigEntry<int> budgetMiB = null!;
    private ConfigEntry<Key> saveKey = null!;
    private ConfigEntry<bool> performanceMetrics = null!;
    private RollingCapture? capture;
    private ReplaySafety? safety;
    private ItemEventCapture? itemEvents;
    private ReplayTheatre? theatre;
    private MemoriesMenu? menu;
    private NativeMemoriesLibrary? nativeLibrary;
    private NativeReplayHud? replayHud;
    private NativeReplayConsole? replayConsole;
    private PlaybackSession? uiPlayback;
    private MemoriesLibraryItem[] highlightCards = Array.Empty<MemoriesLibraryItem>();
    private MemoriesLibraryItem[] fullRunCards = Array.Empty<MemoriesLibraryItem>();
    private ReplayNotice? notice;
    private Task<string>? saving;
    private Task<Entry[]>? listing;
    private Entry[] entries = Array.Empty<Entry>();
    private int captureScene;
    private bool library, showStats, wasBusy, disabledByError, quitting, nativeUiFailed;
    private string status = "进入关卡后自动缓存最近 120 秒，按 F6 保存。";
    private double nextCaptureRetry, nextMenuTick;
    private double nextStatsTick;
    private string? cachedStats;
    private string cachedPerformance = "";
    private string saveKeyLabel = "F6";
    private string DirectoryPath => automationOutputRoot != null ? Path.Combine(automationOutputRoot, "Memories") :
        Path.Combine(Paths.BepInExRootPath, "PeakReplayLab", "Memories");

    private void Awake()
    {
        notice = new ReplayNotice();
        sampleHz = Config.Bind("Recording", "SampleHz", 60, new ConfigDescription("Target capture Hz; limited by rendered FPS. Recording.Mode chooses rolling memory OR one continuous file.", new AcceptableValueRange<int>(5, 60)));
        budgetMiB = Config.Bind("Recording", "BufferBudgetMiB", 256, new ConfigDescription("Conservative rolling sample budget, not preallocated. Oldest frames are evicted when full; F9 shows actual duration.", new AcceptableValueRange<int>(32, 1024)));
        saveKey = Config.Bind("Controls", "SaveHighlightKey", Key.F6, "Save the previous 120 seconds without stopping capture.");
        performanceMetrics = Config.Bind("Diagnostics", "PerformanceMetrics", true, "Bounded CPU timings and rendered-frame cadence; F9 or recording stop saves a diagnostic JSON under PeakReplayLab/Performance. Frame cadence excludes warmup and sealing; CPU timings include initial capture; no GPU attribution.");
        ReplayPerformance.Enabled = performanceMetrics.Value;
        performanceMetrics.SettingChanged += (_, _) => ReplayPerformance.Enabled = performanceMetrics.Value;
        saveKeyLabel = saveKey.Value.ToString();
        saveKey.SettingChanged += (_, _) => saveKeyLabel = saveKey.Value.ToString();
        InitializeFullRuns();
        InitializeAutomation();
        try
        {
            InitializeLibraryManagement();
            safety = new ReplaySafety();
            itemEvents = new ItemEventCapture(message => Logger.LogWarning(message));
            if (itemEvents.HookFailures > 0) Logger.LogWarning($"Item event observers: {itemEvents.HookFailures} hooks unavailable; object snapshots remain enabled.");
            theatre = new ReplayTheatre(Note, e => { RecordAutomationError(e); Logger.LogError(e); }, message => Logger.LogInfo(message));
            menu = new MemoriesMenu(OpenLibrary);
            nativeLibrary = new NativeMemoriesLibrary(CloseLibrary,
                () => { if (fullRunLibrary) RefreshFullRuns(); else RefreshLibrary(); },
                OpenLibraryFolder,
                full => { fullRunLibrary = full; if (full) RefreshFullRuns(); else RefreshLibrary(); },
                path => Guard(() =>
                {
                    if (LibraryManagementBusy) { Note("正在处理录像文件，请稍候再播放。"); return; }
                    theatre.Open(path); library = false; nextMenuTick = 0;
                }),
                mode => Guard(() => SetRecordingMode(mode)), () => theatre.Close(),
                RequestReplayDeletion, ConfirmReplayDeletion, CancelReplayDeletion);
        }
        catch (Exception e) { disabledByError = true; Logger.LogError(e); Note("回放保护未能安装，已停用新回放功能；原足迹 Mod 不受影响。"); }
        SceneManager.activeSceneChanged += SceneChanged;
        Logger.LogInfo($"PEAK Memories 0.7.4: native replay HUD / {sampleHz.Value} Hz / mode={activeRecordingMode}. " + DirectoryPath);
    }

    private void Update()
    {
        UpdateFullRuns();
        UpdateAutomation();
        UpdateLibraryManagement();
        if (disabledByError) { UpdateNotice(); return; }
        var keys = Keyboard.current;
        if (keys != null)
        {
            if (keys.endKey.wasPressedThisFrame || keys.escapeKey.wasPressedThisFrame && (library || theatre!.Busy))
            { if (theatre!.Busy) CloseTheatre(); else if (keys.endKey.wasPressedThisFrame) CloseLibrary(); else nativeLibrary?.Back(); }
            if (saveKey.Value != Key.None && keys[saveKey.Value].wasPressedThisFrame) Guard(SaveHighlight);
            if (fullRunKey.Value != Key.None && keys[fullRunKey.Value].wasPressedThisFrame) Guard(ToggleFullRun);
            if (keys.f7Key.wasPressedThisFrame)
            { if (theatre!.Busy) CloseTheatre(); else if (SceneManager.GetActiveScene().name == "Title") OpenLibrary(); else Note("回到主菜单，点击「回忆录」查看已保存的片段。"); }
            if (keys.f9Key.wasPressedThisFrame)
                ToggleDiagnostics();
            if (theatre!.Playback != null)
            {
                if (keys.spaceKey.wasPressedThisFrame) theatre.Playback.Paused = !theatre.Playback.Paused;
                if (keys.tabKey.wasPressedThisFrame) theatre.Playback.CycleFocus();
                if (keys.hKey.wasPressedThisFrame) replayConsole?.ToggleDrawer();
            }
        }
        if (theatre?.Playback != null && Gamepad.current?.selectButton.wasPressedThisFrame == true)
            replayConsole?.ToggleDrawer();
        if (Gamepad.current?.buttonEast.wasPressedThisFrame == true)
        {
            if (theatre?.Playback != null && replayConsole?.IsExpanded == true) replayConsole.Hide();
            else if (theatre?.Busy == true) CloseTheatre(); else if (library) nativeLibrary?.Back();
        }
        if (saving?.IsCompleted == true)
        {
            try { string path = saving.GetAwaiter().GetResult(); Logger.LogInfo("Saved memory: " + path); Note("精彩片段已收入回忆录 · 回到主菜单即可观看"); if (library) RefreshLibrary(); }
            catch (Exception e) { Logger.LogError(e); Note("保存失败，缓存仍在：" + e.Message); }
            finally { saving = null; }
        }
        if (listing?.IsCompleted == true)
        {
            try
            {
                entries = listing.GetAwaiter().GetResult();
                highlightCards = entries.Select(e => MemoriesLibraryItem.FromHighlight(e.Path, e.Header, e.Error)).ToArray();
            }
            catch (Exception e) { Logger.LogError(e); Note("读取列表失败：" + e.Message); }
            finally { listing = null; }
        }
        if (Time.realtimeSinceStartupAsDouble >= nextMenuTick)
        {
            nextMenuTick = Time.realtimeSinceStartupAsDouble + .25;
            Guard(() => menu!.Tick(library || theatre!.Busy));
        }
        if (!nativeUiFailed)
        {
            try { UpdateNativeLibrary(); }
            catch (Exception e)
            {
                nativeUiFailed = true; Logger.LogError(e); library = false; nextMenuTick = 0; CancelReplayDeletion();
                Guard(() => nativeLibrary?.Hide());
                if (theatre?.Loading == true) theatre.Close();
                Note("原生回忆录界面未能打开，已停止界面重试：" + e.Message);
            }
        }
        UpdateNotice();
    }

    private void UpdateNativeLibrary()
    {
        if (!library && theatre?.Loading != true) { nativeLibrary?.Hide(); return; }
        // Before entering the offline scope use a native index-reading dialog.
        // Afterwards the game's loading screen owns the transition, without
        // flashing another dark modal in its final frame or while returning.
        nativeLibrary?.Tick(new NativeMemoriesLibrary.State
        {
            Items = fullRunLibrary ? fullRunCards : highlightCards,
            FullRuns = fullRunLibrary, Reading = fullRunLibrary ? fullRunListing != null : listing != null,
            Mode = activeRecordingMode, ModeHelp = ModeHelp, Status = status,
            CanManage = CanManageRecordings, Managing = LibraryManagementBusy,
            DeletePreparing = preparingDeletion != null, DeleteMoving = movingDeletion != null,
            DeleteReady = CanConfirmDeletion, DeleteError = deletionError,
            DeleteItem = preparedDeletion?.Item, DeleteBytes = preparedDeletion?.Ticket.Length ?? 0,
            CanSwitch = !LibraryManagementBusy && !ReplaySafety.Active && theatre?.Busy != true && RecordingModePolicy.CanSwitch(
                SceneManager.GetActiveScene().name == "Title", fullRun != null, closingFullRun != null, saving != null),
        }, library, theatre?.Loading == true && !ReplaySafety.Active,
            theatre?.NativeLoadingVisible == true, theatre?.Label ?? "");
    }

    private void CloseLibrary() { CancelReplayDeletion(); library = false; nativeLibrary?.Hide(); nextMenuTick = 0; }

    private void ToggleDiagnostics()
    {
        showStats = !showStats; nextStatsTick = 0;
        if (showStats) replayConsole?.Show();
        SavePerformanceReport("F9", fullRun?.FilePath);
        if (fullRun != null) Logger.LogInfo("Full-run debug: " + FullRunStatus());
        if (theatre?.Playback != null) Logger.LogInfo("Replay presentation: " + theatre.Playback.PresentationDiagnostic);
        if (replayHud != null) Logger.LogInfo("Replay HUD: " + replayHud.Diagnostic);
    }

    private void CloseTheatre() { DisposePlaybackUi(); theatre?.Close(); }

    private void DisposePlaybackUi()
    {
        // Restore UI input independently from replay camera/world cleanup.
        Guard(() => replayConsole?.Dispose()); replayConsole = null;
        Guard(() => replayHud?.Dispose()); replayHud = null; uiPlayback = null;
    }

    private void UpdatePlaybackUi()
    {
        var session = theatre?.Playback;
        bool opened = false;
        if (!ReferenceEquals(session, uiPlayback))
        {
            DisposePlaybackUi();
            if (session == null) return;
            try
            {
                replayHud = new NativeReplayHud(message => Logger.LogWarning(message));
                replayConsole = new NativeReplayConsole(session, CloseTheatre, ToggleDiagnostics, e => Logger.LogError(e));
                uiPlayback = session;
                opened = true;
            }
            catch (Exception e)
            {
                RecordAutomationError(e); Logger.LogError(e); CloseTheatre();
                Note("回放界面未能创建，已恢复游戏控制：" + e.Message); return;
            }
        }
        if (session == null) return;
        try
        {
            replayHud!.Tick(session);
            replayConsole!.Tick(showStats, cachedPerformance, replayHud.Warning);
            if (opened) Logger.LogInfo("Replay HUD: " + replayHud.Diagnostic + "; state-known=" + (session.FocusHudState != null));
        }
        catch (Exception e)
        {
            RecordAutomationError(e); Logger.LogError(e); CloseTheatre();
            Note("回放界面发生错误，已恢复游戏控制：" + e.Message);
        }
    }

    private void UpdateNotice()
    {
        if (showStats && Time.realtimeSinceStartupAsDouble >= nextStatsTick)
        {
            nextStatsTick = Time.realtimeSinceStartupAsDouble + .5;
            cachedPerformance = ReplayPerformance.Summary(theatre?.Playback != null);
            cachedStats = capture != null ? (IsContinuous ? $"持续录制采集 · 仅驻留 {capture.Buffer.Count} 帧 · " : $"缓存 {capture.Buffer.Duration:F1} / 120 秒 · {capture.Buffer.Count} 帧 · ") +
            $"保守计量 {capture.Buffer.EstimatedBytes / 1048576d:F1} / {budgetMiB.Value} MiB" +
            $"\n主采样 {capture.ObservedHz:F1} / {sampleHz.Value} Hz · 关节 P0/P1/P2 目标 60/30/10 Hz · 节点 {capture.JointCount} · 物品 {capture.ItemCount} · 箱子 {capture.CrateCount} · 物品事件 {capture.EventCount}" +
            $"\n绳索/钩爪 {capture.RopeCount} · 特效 {capture.EffectCount} · 游戏音效 {capture.AudioCount} · 放置物 {capture.SpawnedCount} · 绑头气球 {capture.BalloonCount}" +
            (itemEvents?.HookFailures > 0 ? $" · 事件入口缺失 {itemEvents.HookFailures}" : "") +
            (capture.Buffer.MemoryLimited ? "\n预算曾触顶，最旧数据已淘汰" : "") + "\n" + cachedPerformance + " · F9 隐藏" : cachedPerformance;
            string fullRunStats = FullRunStatus();
            if (!string.IsNullOrWhiteSpace(fullRunStats)) cachedStats += "\n" + fullRunStats;
        }
        try { notice?.Tick(library || nativeLibrary?.IsVisible == true,
            theatre?.Playback != null, showStats ? cachedStats : null,
            theatre?.Loading == true ? theatre.Label : null); }
        catch (Exception e) { Logger.LogError(e); }
    }

    private void LateUpdate()
    {
        ObserveGameFrame();
        if (disabledByError) return;
        theatre!.Tick(Mathf.Min(Time.unscaledDeltaTime, .1f));
        UpdatePlaybackUi();
        if (wasBusy && !theatre.Busy && SceneManager.GetActiveScene().name == "Title" && automationOutputRoot == null) OpenLibrary();
        wasBusy = theatre.Busy;
        if (automationEnding) { ItemEventCapture.Activate(false); return; }
        if (theatre.Busy || ReplaySafety.Active) { ItemEventCapture.Activate(false); return; }
        if (Time.realtimeSinceStartupAsDouble < nextCaptureRetry) return;
        if (!GameHandler.IsOnIslandAndInitialized || !Character.localCharacter || Character.localCharacter.inAirport)
        { ItemEventCapture.Activate(false); return; }
        try
        {
            int scene = SceneManager.GetActiveScene().handle;
            if (capture == null || captureScene != scene)
            {
                capture?.Dispose();
                capture = new RollingCapture(sampleHz.Value, budgetMiB.Value * 1024L * 1024, message => Logger.LogWarning(message), keepRollingHistory: !IsContinuous);
                captureScene = scene;
                ReplayPerformance.Reset();
                // The native loading/title hint teaches the shortcut. Do not flash a
                // debug-looking notification over the game as soon as capture starts.
                status = $"已启用 {sampleHz.Value} Hz · {ModeHelp}";
                Logger.LogInfo(status);
            }
            StartAutomaticFullRun();
            if (IsContinuous && fullRun == null) { ItemEventCapture.Activate(false); return; }
            ItemEventCapture.Activate(true);
            capture.Tick(fullRun != null ? fullRunSink : null);
        }
        catch (Exception e)
        {
            InterruptFullRun(e);
            nextCaptureRetry = Time.realtimeSinceStartupAsDouble + 5;
            Logger.LogWarning("Rolling capture retry: " + e);
            Note("缓存暂缓，5 秒后重试：" + e.Message);
        }
    }

    private void SaveHighlight()
    {
        if (LibraryManagementBusy) { Note("正在处理录像文件，请稍候再保存。"); return; }
        if (!RecordingModePolicy.CanSaveHighlight(activeRecordingMode)) { Note("当前为持续录制：整局写入一个完整文件，F4 停止；不是 120 秒内存模式。"); return; }
        if (theatre!.Busy) { Note("观看回忆录时不会再次录制回放。"); return; }
        if (saving != null) { Note("上一段正在保存，请稍等完成提示。"); return; }
        if (capture == null || capture.Buffer.Count < 2) { Note("还没有足够的游戏片段可保存。"); return; }
        using var timing = ReplayPerformance.Measure(ReplayStage.SaveSnapshot);
        double duration = capture.Buffer.Duration;
        var buildSnapshot = capture.Buffer.PrepareSnapshot(capture.Header, DateTime.UtcNow);
        string directory = DirectoryPath;
        saving = Task.Run(() =>
        {
            ReplayClip snapshot = buildSnapshot();
            Logger.LogInfo($"Saving schema {snapshot.Header.Schema}: {snapshot.Frames.Count} frames, target {snapshot.Header.SampleHz} Hz, " +
                $"peakItems={snapshot.Frames.Max(f => f.Items.Length)}, peakCrates={snapshot.Frames.Max(f => f.Crates.Length)}, " +
                $"peakRopes={snapshot.Frames.Max(f => f.Ropes.Length)}, peakEffects={snapshot.Frames.Max(f => f.Effects.Length)}, peakGameSounds={snapshot.Frames.Max(f => f.Audio.Length)}, peakSpawned={snapshot.Frames.Max(f => f.Spawned.Length)}, peakBalloons={snapshot.Frames.Max(f => f.Balloons.Length)}, " +
                $"events={snapshot.Frames.Sum(f => f.Events.Length)}, inventoryActors={snapshot.Frames.Max(f => f.Actors.Count(a => a.InventoryKnown))}.");
            return ReplayArchive.Save(directory, snapshot);
        });
        Note($"正在后台保存过去 {duration:F1} 秒… 缓存继续。");
    }

    private void OpenLibrary()
    {
        if (disabledByError || theatre!.Busy || SceneManager.GetActiveScene().name != "Title") return;
        if (nativeLibrary?.CanOpen != true || !FindFirstObjectByType<MainMenuMainPage>())
        { Note("请先关闭当前游戏弹窗，回到主菜单首页再打开回忆录。"); return; }
        CancelReplayDeletion(); nativeLibrary.Open();
        nativeUiFailed = false;
        library = true;
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        if (fullRunLibrary) RefreshFullRuns(); else RefreshLibrary(); nextMenuTick = 0;
    }

    private void OpenLibraryFolder()
    {
        try
        {
            string directory = Path.GetFullPath(fullRunLibrary ? FullRunDirectoryPath : DirectoryPath);
            Directory.CreateDirectory(directory);
            // Let the OS open the directory directly, including paths containing
            // spaces or Chinese characters; no command-line shell is needed.
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception e)
        {
            Logger.LogError(e);
            Note("无法打开录像文件夹：" + e.Message);
        }
    }

    private void RefreshLibrary()
    {
        if (listing != null || LibraryManagementBusy) return;
        string directory = DirectoryPath;
        listing = Task.Run(() =>
        {
            if (!Directory.Exists(directory)) return Array.Empty<Entry>();
            return Directory.EnumerateFiles(directory, "*.peakreplay").OrderByDescending(Path.GetFileName).Take(1000).Select(path =>
            {
                var e = new Entry { Path = path };
                try { e.Header = ReplayFiles.ReadHeader(path); } catch (Exception ex) { e.Error = ex.Message; }
                return e;
            }).ToArray();
        });
    }

    private void SceneChanged(Scene before, Scene after)
    {
        DisposePlaybackUi();
        CancelReplayDeletion();
        StopFullRun("scene-change:" + before.name + "->" + after.name);
        frameCadence.Reset();
        stoppedFullRunScene = int.MinValue; nextFullRunStatus = 0;
        nativeLibrary?.Hide(); menu?.Dispose(); capture?.Dispose(); captureScene = int.MinValue; ItemEventCapture.Activate(false); Capture.ClearCatalog(); library = false;
        WorldTrack.Reset();
        // Rolling120 keeps pure data so F6 works after returning to the menu.
        // Continuous seals the one existing file; it never makes a RAM highlight.
    }
    private void Guard(Action action)
    {
        try { action(); } catch (Exception e) { RecordAutomationError(e); Logger.LogError(e); Note(e.Message); }
    }
    private void Note(string message) => Note(message, true);
    private void Note(string message, bool notify)
    {
        status = message; if (notify) notice?.Show(message); Logger.LogInfo(message);
    }
    private void OnApplicationQuit() { quitting = true; StopFullRun("application-quit"); WaitForFullRunShutdown(); }
    private void OnDisable() { DisposePlaybackUi(); StopFullRun("plugin-disabled"); RestoreAutomationOverride(); ItemEventCapture.Activate(false); capture?.Dispose(); captureScene = int.MinValue; if (!quitting) theatre?.Dispose(); nativeLibrary?.Dispose(); menu?.Dispose(); notice?.Dispose(); }
    private void OnDestroy()
    {
        SceneManager.activeSceneChanged -= SceneChanged;
        DisposePlaybackUi();
        StopFullRun("plugin-destroyed");
        if (!quitting) theatre?.Dispose();
        // If the app quits inside the theatre, keep save/achievement guards installed until
        // process exit. Removing them now could let a later game OnDestroy persist the replay.
        if (!quitting || !ReplaySafety.Active) safety?.Dispose();
        itemEvents?.Dispose(); capture?.Dispose();
        nativeLibrary?.Dispose(); menu?.Dispose(); notice?.Dispose();
        // No implicit highlight save. An active continuous stream may drain.
    }

}
