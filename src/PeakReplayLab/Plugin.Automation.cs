using System;
using System.IO;
using System.Threading;
using BepInEx;
using Peak.Network;
using Photon.Pun;
using UnityEngine.SceneManagement;

namespace PeakReplayLab;

/// <summary>
/// Opt-in control surface for a separate in-game test driver. All calls, including
/// Status, must run on Unity's main thread. Commands use the normal recorder and
/// theatre; this API never injects frames or runs a second capture/playback loop.
/// </summary>
public sealed class ReplayAutomation
{
    private readonly Plugin owner;
    internal ReplayAutomation(Plugin owner) => this.owner = owner;
    public const int ApiVersion = 1;
    public ReplayAutomationStatus Status => owner.QueryAutomationStatus();
    public ReplayAutomationStatus GetStatus() => Status;
    public void BeginSession(string outputRoot) => owner.BeginAutomationSession(outputRoot);
    public void StartChapter() => owner.StartAutomationChapter();
    public void SealChapter(string reason = "test-chapter-end") => owner.SealAutomationChapter(reason);
    public void OpenReplay(string path) => owner.OpenAutomationReplay(path);
    public void SetPlaybackPaused(bool paused) => owner.PauseAutomationReplay(paused);
    public void SeekReplay(double time) => owner.SeekAutomationReplay(time);
    public void CloseReplay() => owner.CloseAutomationReplay();
    /// <summary>
    /// Requests sealing and closes any replay. Restoration completes at an idle
    /// Title scene. The driver must leave its live game itself, then poll
    /// SessionActive until false; this method does not disconnect a real run.
    /// </summary>
    public void EndSession() => owner.EndAutomationSession();
}

/// <summary>A detached snapshot; it contains no Unity objects or worker tasks.</summary>
public sealed class ReplayAutomationStatus
{
    public bool Available { get; internal set; }
    public bool CanBeginSession { get; internal set; }
    public bool SessionActive { get; internal set; }
    public bool Ending { get; internal set; }
    public string Scene { get; internal set; } = "";
    public string OutputRoot { get; internal set; } = "";
    public ReplayRecordingMode RecordingMode { get; internal set; }
    public bool CaptureReady { get; internal set; }
    public bool Recording { get; internal set; }
    public bool Sealing { get; internal set; }
    public int ChapterIndex { get; internal set; }
    public string RecordingFile { get; internal set; } = "";
    public long AcceptedFrames { get; internal set; }
    public long WrittenFrames { get; internal set; }
    public int QueuedFrames { get; internal set; }
    public long QueuedBytes { get; internal set; }
    public double RecordingDuration { get; internal set; }
    public int CapturedItems { get; internal set; }
    public int CapturedCrates { get; internal set; }
    public int CapturedRopes { get; internal set; }
    public int CapturedEffects { get; internal set; }
    public int CapturedSounds { get; internal set; }
    public int CapturedSpawned { get; internal set; }
    public int CapturedBalloons { get; internal set; }
    public int CapturedCreatures { get; internal set; }
    public long CapturedEvents { get; internal set; }
    public bool PlaybackBusy { get; internal set; }
    public bool PlaybackReady { get; internal set; }
    public bool PlaybackBuffering { get; internal set; }
    public bool PlaybackPaused { get; internal set; }
    public double PlaybackTime { get; internal set; }
    public double PlaybackRequestedTime { get; internal set; }
    public double PlaybackDuration { get; internal set; }
    public int VisibleActors { get; internal set; }
    public int RecordedItems { get; internal set; }
    public int VisibleItems { get; internal set; }
    public int MissingItems { get; internal set; }
    public int RecordedJoints { get; internal set; }
    public int MissingJoints { get; internal set; }
    public string PlaybackLabel { get; internal set; } = "";
    public string PresentationDiagnostic { get; internal set; } = "";
    public string LatestCompletedFile { get; internal set; } = "";
    public ReplayAutomationRecordingResult? LatestRecording { get; internal set; }
    public string LastError { get; internal set; } = "";
}

public sealed class ReplayAutomationRecordingResult
{
    public int ChapterIndex { get; internal set; }
    public string FilePath { get; internal set; } = "";
    public string Status { get; internal set; } = "";
    public string Reason { get; internal set; } = "";
    public string FaultCode { get; internal set; } = "";
    public string Fault { get; internal set; } = "";
    public long AcceptedFrames { get; internal set; }
    public long WrittenFrames { get; internal set; }
    public long UnwrittenFrames { get; internal set; }
    public int CompletedPages { get; internal set; }
    public long BytesWritten { get; internal set; }
    public double Duration { get; internal set; }
    public long GapOver100ms { get; internal set; }
    public long GapOver500ms { get; internal set; }
    public double LargestGapSeconds { get; internal set; }
}

public sealed partial class Plugin
{
    private ReplayAutomation? automation;
    private int automationMainThread;
    private string? automationOutputRoot;
    private string automationLastOutputRoot = "", automationLastError = "", automationLatestCompletedFile = "";
    private ReplayRecordingMode automationOriginalMode;
    private bool automationEnding;
    private int automationChapter;
    private ReplayAutomationRecordingResult? automationLatestRecording;
    public ReplayAutomation Automation => automation ?? throw new InvalidOperationException("Replay automation is not initialized.");

    private void InitializeAutomation()
    {
        automationMainThread = Thread.CurrentThread.ManagedThreadId;
        automation = new ReplayAutomation(this);
    }

    private void RequireAutomationThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != automationMainThread)
            throw new InvalidOperationException("ReplayAutomation must be called on Unity's main thread.");
    }

    private bool AutomationTitleIdle => SceneManager.GetActiveScene().name == "Title" &&
        !PhotonNetwork.InRoom && !NetCode.Matchmaking.InLobby && !LoadingScreenHandler.loading &&
        !ReplaySafety.Active && theatre?.Busy != true && fullRun == null && closingFullRun == null &&
        saving == null && !LibraryManagementBusy;

    private bool AutomationCaptureReady => capture != null && captureScene == SceneManager.GetActiveScene().handle &&
        GameHandler.IsOnIslandAndInitialized && Character.localCharacter && !Character.localCharacter.inAirport &&
        !ReplaySafety.Active && theatre?.Busy != true;

    internal ReplayAutomationStatus QueryAutomationStatus()
    {
        RequireAutomationThread();
        var playback = theatre?.Playback;
        var stats = (fullRun ?? closingFullRunWriter)?.Stats;
        bool available = isActiveAndEnabled && !disabledByError && !quitting && theatre != null;
        return new ReplayAutomationStatus
        {
            Available = available, CanBeginSession = available && automationOutputRoot == null && AutomationTitleIdle &&
                listing == null && fullRunListing == null,
            SessionActive = automationOutputRoot != null, Ending = automationEnding,
            Scene = SceneManager.GetActiveScene().name, OutputRoot = automationOutputRoot ?? automationLastOutputRoot,
            RecordingMode = activeRecordingMode, CaptureReady = AutomationCaptureReady,
            Recording = fullRun != null, Sealing = closingFullRun != null, ChapterIndex = automationChapter,
            RecordingFile = fullRun?.FilePath ?? closingFullRunWriter?.FilePath ?? "",
            AcceptedFrames = stats?.AcceptedFrames ?? 0, WrittenFrames = stats?.WrittenFrames ?? 0,
            QueuedFrames = stats?.QueuedFrames ?? 0, QueuedBytes = stats?.QueuedBytes ?? 0,
            RecordingDuration = stats == null ? 0 : Math.Max(0, stats.NativeEnd - stats.NativeStart),
            CapturedItems = capture?.ItemCount ?? 0, CapturedCrates = capture?.CrateCount ?? 0,
            CapturedRopes = capture?.RopeCount ?? 0, CapturedEffects = capture?.EffectCount ?? 0,
            CapturedSounds = capture?.AudioCount ?? 0, CapturedSpawned = capture?.SpawnedCount ?? 0,
            CapturedBalloons = capture?.BalloonCount ?? 0, CapturedCreatures = capture?.CreatureCount ?? 0,
            CapturedEvents = capture?.EventCount ?? 0,
            PlaybackBusy = theatre?.Busy == true, PlaybackReady = playback != null,
            PlaybackBuffering = playback?.Buffering == true, PlaybackPaused = playback?.Paused == true,
            PlaybackTime = playback?.Time ?? 0, PlaybackRequestedTime = playback?.RequestedTime ?? 0,
            PlaybackDuration = playback?.Duration ?? 0, VisibleActors = playback?.VisibleActors ?? 0,
            RecordedItems = playback?.RecordedItemCount ?? 0, VisibleItems = playback?.VisibleItemCount ?? 0,
            MissingItems = playback?.MissingItemCount ?? 0, RecordedJoints = playback?.RecordedJointCount ?? 0,
            MissingJoints = playback?.MissingJointCount ?? 0, PlaybackLabel = theatre?.Label ?? "",
            PresentationDiagnostic = playback?.PresentationDiagnostic ?? "",
            LatestCompletedFile = automationLatestCompletedFile, LatestRecording = automationLatestRecording,
            LastError = automationLastError,
        };
    }

    private void AutomationCommand(Action command, bool requireSession = true)
    {
        RequireAutomationThread();
        try
        {
            if (!isActiveAndEnabled || disabledByError || quitting || theatre == null)
                throw new InvalidOperationException("Replay plugin is unavailable.");
            if (requireSession && (automationOutputRoot == null || automationEnding))
                throw new InvalidOperationException("An active replay automation session is required.");
            command();
        }
        catch (Exception error) { RecordAutomationError(error); throw; }
    }

    internal void BeginAutomationSession(string outputRoot) => AutomationCommand(() =>
    {
        if (automationOutputRoot != null) throw new InvalidOperationException("A replay automation session is already active.");
        if (!AutomationTitleIdle || listing != null || fullRunListing != null)
            throw new InvalidOperationException("Begin replay automation at an idle Title scene, outside any room or lobby.");
        if (string.IsNullOrWhiteSpace(outputRoot) || !Path.IsPathRooted(outputRoot))
            throw new ArgumentException("Automation outputRoot must be an absolute, separate test directory.", nameof(outputRoot));
        string root = Path.GetFullPath(outputRoot);
        string[] normalRoots =
        {
            Path.Combine(Paths.BepInExRootPath, "PeakReplayLab", "Memories"),
            Path.Combine(Paths.BepInExRootPath, "PeakReplayLab", "Recordings"),
            Path.Combine(Paths.BepInExRootPath, "PeakReplayLab", "Trash"),
        };
        foreach (string normal in normalRoots)
            if (ContainsDirectory(root, normal) || ContainsDirectory(normal, root))
                throw new ArgumentException("Automation output cannot overlap the user's Memories, Recordings or Trash directories.", nameof(outputRoot));
        Directory.CreateDirectory(Path.Combine(root, "Recordings"));
        automationOriginalMode = activeRecordingMode;
        automationOutputRoot = automationLastOutputRoot = root;
        automationEnding = false; automationChapter = 0;
        automationLastError = automationLatestCompletedFile = ""; automationLatestRecording = null;
        try
        {
            SetRecordingMode(ReplayRecordingMode.Continuous, persist: false);
            CloseLibrary();
            capture?.Dispose(); capture = null; captureScene = int.MinValue;
            stoppedFullRunScene = int.MinValue; nextCaptureRetry = 0;
        }
        catch (Exception error)
        {
            RecordAutomationError(error); RestoreAutomationOverride(); throw;
        }
        Logger.LogInfo("Replay automation session started: " + root);
    }, requireSession: false);

    internal void StartAutomationChapter() => AutomationCommand(() =>
    {
        if (fullRun != null || closingFullRun != null)
            throw new InvalidOperationException("Seal the existing recording and wait for completion before starting another chapter.");
        if (!AutomationCaptureReady)
            throw new InvalidOperationException("The live island and native player must be initialized before starting a chapter.");
        // The same F4 restart path builds a fresh baseline for objects that changed
        // while recording was stopped, even when the Unity scene did not change.
        ToggleFullRun();
        if (fullRun == null) throw new InvalidOperationException("The native continuous recording did not start.");
    });

    internal void SealAutomationChapter(string reason) => AutomationCommand(() =>
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 256 || reason.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            throw new ArgumentException("A short single-line recording end reason is required.", nameof(reason));
        if (closingFullRun != null) return;
        if (fullRun == null) throw new InvalidOperationException("No automation recording is active.");
        StopFullRun(reason);
    });

    internal void OpenAutomationReplay(string path) => AutomationCommand(() =>
    {
        if (!AutomationTitleIdle) throw new InvalidOperationException("Open the test recording only at an idle Title scene after sealing.");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException("An absolute test recording path is required.", nameof(path));
        string fullPath = Path.GetFullPath(path);
        if (!ContainsDirectory(automationOutputRoot!, fullPath) ||
            (!fullPath.EndsWith(".peakrun", StringComparison.OrdinalIgnoreCase) && !fullPath.EndsWith(".peakreplay", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Open a completed recording inside this automation session's output directory.", nameof(path));
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Automation recording is missing.", fullPath);
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Automation does not open linked recording files.");
        CloseLibrary(); theatre!.Open(fullPath);
    });

    internal void PauseAutomationReplay(bool paused) => AutomationCommand(() =>
    {
        var playback = theatre!.Playback ?? throw new InvalidOperationException("Replay presentation is not ready.");
        playback.Paused = paused;
    });

    internal void SeekAutomationReplay(double time) => AutomationCommand(() =>
    {
        var playback = theatre!.Playback ?? throw new InvalidOperationException("Replay presentation is not ready.");
        if (double.IsNaN(time) || double.IsInfinity(time) || time < 0 || time > playback.Duration)
            throw new ArgumentOutOfRangeException(nameof(time), "Seek time must be within the test recording.");
        playback.Seek(time);
    });

    internal void CloseAutomationReplay() => AutomationCommand(CloseTheatre);

    internal void EndAutomationSession()
    {
        RequireAutomationThread();
        if (automationOutputRoot == null || automationEnding) return;
        automationEnding = true;
        StopFullRun("test-session-end"); CloseTheatre(); CloseLibrary();
        UpdateAutomation();
    }

    private void UpdateAutomation()
    {
        if (!automationEnding || automationOutputRoot == null || !AutomationTitleIdle) return;
        SetRecordingMode(automationOriginalMode, persist: false);
        capture?.Dispose(); capture = null; captureScene = int.MinValue;
        RestoreAutomationOverride();
        Logger.LogInfo("Replay automation session ended; recording mode and normal output directories restored.");
    }

    private void RestoreAutomationOverride()
    {
        if (automationOutputRoot == null) return;
        // Used on disable as well: no ConfigEntry is ever changed by automation.
        activeRecordingMode = automationOriginalMode;
        automationOutputRoot = null; automationEnding = false;
        nextFullRunStatus = nextStatsTick = nextCaptureRetry = 0;
        cachedStats = null; cachedFullRunStatus = "";
    }

    private void AutomationRecordingStarted()
    {
        if (automationOutputRoot == null) return;
        automationChapter++; automationLatestRecording = null;
    }

    private void AutomationRecordingFinished(FullReplayResult result)
    {
        if (automationOutputRoot == null) return;
        automationLatestRecording = new ReplayAutomationRecordingResult
        {
            ChapterIndex = automationChapter, FilePath = result.FilePath, Status = result.Status, Reason = result.Reason,
            FaultCode = result.FaultCode ?? "", Fault = result.Fault ?? "", AcceptedFrames = result.AcceptedFrames,
            WrittenFrames = result.WrittenFrames, UnwrittenFrames = result.UnwrittenFrames, CompletedPages = result.CompletedPages,
            BytesWritten = result.BytesWritten, Duration = result.Duration, GapOver100ms = result.GapOver100ms,
            GapOver500ms = result.GapOver500ms, LargestGapSeconds = result.LargestGapSeconds,
        };
        if (result.Status == "completed") automationLatestCompletedFile = result.FilePath;
        else automationLastError = (result.FaultCode ?? "recording-fault") + ": " + (result.Fault ?? result.Status);
    }

    private void RecordAutomationError(Exception error)
    {
        if (automationOutputRoot != null) automationLastError = error.GetType().Name + ": " + error.Message;
    }

    private static bool ContainsDirectory(string directory, string path)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
