using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using BepInEx;
using BepInEx.Bootstrap;
using Peak.Network;
using Photon.Pun;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem;

namespace PeakReplayLab;

internal sealed class ReplayTheatre : IDisposable
{
    private enum Phase { Idle, Read, Disconnect, Load, Spawn, Prepare, Play, Return, Title }
    private Phase phase;
    private readonly Action<string> note;
    private readonly Action<Exception> error;
    private readonly Action<string>? trace;
    private readonly Dictionary<string, VisualActor> actors = new(StringComparer.Ordinal);
    private readonly List<(BaseUnityPlugin Plugin, bool Enabled)> suspended = new();
    private Task<IReplayTimeline>? reading;
    private CancellationTokenSource? readCancellation;
    private readonly List<InputAction> oldActions = new();
    private string failure = "";
    private IReplayTimeline? timeline;
    private IEnumerator? disconnect;
    private AsyncOperation? loading;
    private NativeReplayLoading? nativeLoading;
    private bool titleLoadStarted;
    private double deadline;
    private bool scope;
    private bool recoveryStarted;
    private bool oldOffline, oldQuicksave;
    private float oldTimeScale;
    private CursorLockMode oldLock;
    private bool oldCursor;
    private string[] ids = Array.Empty<string>();
    private int spawnReadyFrame = -1;
    public PlaybackSession? Playback { get; private set; }
    public bool Busy => phase != Phase.Idle;
    public bool Loading => Busy && phase != Phase.Play;
    public bool NativeLoadingVisible => nativeLoading?.Visible == true;
    public string Label { get; private set; } = "";

    public ReplayTheatre(Action<string> note, Action<Exception> error, Action<string>? trace = null) { this.note = note; this.error = error; this.trace = trace; }

    public void Open(string path)
    {
        if (Busy) return;
        if (SceneManager.GetActiveScene().name != "Title" || PhotonNetwork.InRoom || NetCode.Matchmaking.InLobby)
            throw new InvalidOperationException("请先退出本局，回到游戏主菜单再打开回忆录。");
        if (LoadingScreenHandler.loading) throw new InvalidOperationException("游戏仍在加载，请稍后打开回忆录。");
        failure = "";
        readCancellation = new CancellationTokenSource();
        var token = readCancellation.Token;
        reading = Task.Run<IReplayTimeline>(() =>
        {
            IReplayTimeline result;
            if (string.Equals(Path.GetExtension(path), ".peakrun", StringComparison.OrdinalIgnoreCase))
            {
                var info = FullReplayArchive.ReadInfo(path, token);
                result = new PagedReplayTimeline(info.Header, info.Duration, info.FrameCount, info.ActorIds,
                    info.Pages.Select(p => new ReplayPageRange(p.Start, p.End)).ToArray(),
                    (index, cancellation) => FullReplayArchive.ReadPage(path, info, index, cancellation), info.Complete);
            }
            else result = new MemoryReplayTimeline(ReplayFiles.Read(path, token));
            if (token.IsCancellationRequested) { result.Dispose(); token.ThrowIfCancellationRequested(); }
            return result;
        }, token);
        Transition(Phase.Read, "读取录像索引并核验版本…");
    }

    private void Transition(Phase next, string label)
    {
        phase = next; Label = label;
        deadline = Time.realtimeSinceStartupAsDouble + 120;
        note(label);
    }

    public void Tick(float delta)
    {
        if (!Busy) return;
        try
        {
            // Native loading curves and the Finish animation use scaled time.
            // Freeze only after that entire flow has completed, never underneath it.
            if (scope && phase != Phase.Play && phase != Phase.Prepare) Time.timeScale = 1;
            nativeLoading?.Check();
            if (scope && phase != Phase.Play && InputSystem.actions) InputSystem.actions.Disable();
            if (phase != Phase.Play && Time.realtimeSinceStartupAsDouble > deadline) throw new TimeoutException("回忆录加载超时，正在返回主菜单。");
            switch (phase)
            {
                case Phase.Read:
                    if (!reading!.IsCompleted) return;
                    timeline = reading.GetAwaiter().GetResult(); reading = null;
                    readCancellation?.Dispose(); readCancellation = null;
                    if (!timeline.Complete) throw new InvalidOperationException("此录像未完整保存，不能自动加载。");
                    var current = Capture.Header(20);
                    if (timeline.Header.GameVersion != current.GameVersion || timeline.Header.BuildId != current.BuildId || timeline.Header.GameAssembly != current.GameAssembly)
                        throw new InvalidOperationException("游戏版本与录像不同。为避免地图或装扮错位，本版拒绝跨版本播放。");
                    if (!(timeline.Header.Scene.StartsWith("Level_", StringComparison.Ordinal) || timeline.Header.Scene.Contains("Island")) ||
                        !Application.CanStreamedLevelBeLoaded(timeline.Header.Scene))
                        throw new InvalidOperationException("本机游戏没有录像对应的关卡场景。");
                    ids = timeline.ActorIds;
                    if (ids.Length > ReplayRules.MaxActors) throw new InvalidOperationException("片段参与者过多。");
                    EnterScope();
                    nativeLoading = new NativeReplayLoading(LoadingScreen.LoadingScreenType.Plane);
                    disconnect = MainMenu.DisconnectForOfflineMode();
                    Transition(Phase.Disconnect, "准备本机离线回放，不加入好友房间…");
                    break;
                case Phase.Disconnect:
                    if (disconnect != null)
                    {
                        if (disconnect.MoveNext()) return;
                        (disconnect as IDisposable)?.Dispose(); disconnect = null;
                    }
                    if (nativeLoading?.ProcessStarted != true) return; // Native fade-out must cover the scene switch.
                    NetworkConnector.ChangeConnectionState<DefaultConnectionState>();
                    loading = SceneManager.LoadSceneAsync(timeline!.Header.Scene, LoadSceneMode.Single);
                    if (loading == null) throw new InvalidOperationException("无法加载录像场景。");
                    Transition(Phase.Load, "加载原场景：" + timeline.Header.Scene);
                    break;
                case Phase.Load:
                    if (!loading!.isDone) return;
                    loading = null;
                    if (!MapHandler.Exists) throw new InvalidOperationException("已加载场景中没有地图组件。");
                    if (!MapHandler.ExistsAndInitialized) MapHandler.InitializeMap();
                    spawnReadyFrame = -1;
                    Transition(Phase.Spawn, "准备人物模型与关节…");
                    break;
                case Phase.Spawn:
                    if (!PhotonNetwork.OfflineMode) throw new InvalidOperationException("离线回放环境意外改变，已中止。");
                    if (!Character.localCharacter || !Character.localCharacter.refs?.hip || !Camera.main || !MapHandler.ExistsAndInitialized) return;
                    if (spawnReadyFrame < 0) { spawnReadyFrame = Time.frameCount; return; }
                    if (Time.frameCount - spawnReadyFrame < 3) return;
                    if (!ReplayRules.Compatible(timeline!.Header, Capture.Header(timeline.Header.SampleHz))) throw new InvalidOperationException("原场景的关卡分支与录像不符。");
                    if (actors.Count < ids.Length)
                    {
                        string id = ids[actors.Count];
                        actors.Add(id, new VisualActor(Character.localCharacter));
                        return; // Keep the native plane visible through template allocation.
                    }
                    nativeLoading?.Release();
                    // LoadingScreen's audio fade uses scaled time. Its loading flag
                    // clears only after the fade-in completes; freezing earlier can
                    // strand the native SFX mixer at -80 dB. Never override user volume.
                    if (nativeLoading?.Finished == false || LoadingScreenHandler.loading) return;
                    ClearNativeLoading();
                    Time.timeScale = 0;
                    foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                        foreach (var canvas in root.GetComponentsInChildren<Canvas>(true)) canvas.enabled = false;
                    Transition(Phase.Prepare, "根据片段重建所有人的历史装扮…");
                    break;
                case Phase.Prepare:
                    if (actors.Count < ids.Length)
                    {
                        string id = ids[actors.Count];
                        actors.Add(id, new VisualActor(Character.localCharacter));
                        return; // Spread visual allocations across frames; none were kept during normal play.
                    }
                    Playback = new PlaybackSession(timeline!, actors, error, WorldTrack.Player(timeline!.Header), message => trace?.Invoke("Replay objects: " + message));
                    trace?.Invoke("Replay presentation: " + Playback.PresentationDiagnostic);
                    Transition(Phase.Play, "回忆录 · 人物 / 物品 / 绳索 / 特效 / 游戏音效 · Esc 返回首页");
                    break;
                case Phase.Play:
                    if (!PhotonNetwork.OfflineMode) throw new InvalidOperationException("已离开本机离线环境。");
                    Playback!.Tick(delta);
                    break;
                case Phase.Return:
                    // Unity cannot cancel a scene load. Wait for it before unloading to Title.
                    if (loading != null && !loading.isDone) return;
                    nativeLoading?.Release();
                    if (nativeLoading?.Finished == false || LoadingScreenHandler.loading) return;
                    ClearNativeLoading();
                    if (PhotonNetwork.InRoom) { PhotonNetwork.LeaveRoom(false); return; }
                    PhotonNetwork.Disconnect();
                    if (PhotonNetwork.IsConnected && !PhotonNetwork.OfflineMode) return;
                    PhotonNetwork.OfflineMode = oldOffline;
                    GameHandler.ClearStatus<IsDisconnectingForOfflineMode>();
                    NetworkConnector.ChangeConnectionState<DefaultConnectionState>();
                    if (SceneManager.GetActiveScene().name == "Title") { Finish(); return; }
                    loading = null; titleLoadStarted = false;
                    nativeLoading = new NativeReplayLoading(LoadingScreen.LoadingScreenType.Basic);
                    Transition(Phase.Title, "返回回忆录首页…");
                    break;
                case Phase.Title:
                    if (!titleLoadStarted)
                    {
                        if (nativeLoading?.ProcessStarted != true) return;
                        titleLoadStarted = true;
                        loading = SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
                        if (loading == null) throw new InvalidOperationException("无法加载回忆录主菜单。");
                    }
                    if (loading != null && !loading.isDone) return;
                    if (SceneManager.GetActiveScene().name != "Title") return;
                    nativeLoading?.Release();
                    if (nativeLoading?.Finished == false || LoadingScreenHandler.loading) return;
                    Finish();
                    break;
            }
        }
        catch (Exception e)
        {
            failure = e.Message;
            Report(e); note(e.Message);
            if (phase == Phase.Return || phase == Phase.Title)
            {
                // LoadScene completes on a subsequent frame. Keep all guards installed
                // until Title has actually replaced the replay scene.
                try
                {
                    ClearNativeLoading();
                    Time.timeScale = 1;
                    titleLoadStarted = true;
                    SceneManager.LoadScene("Title");
                    loading = null;
                    Transition(Phase.Title, "恢复主菜单，回放保护仍保持启用…");
                }
                catch (Exception restoreError)
                {
                    Report(restoreError);
                    deadline = Time.realtimeSinceStartupAsDouble + 120;
                    Label = "无法返回主菜单，请退出并重启游戏；存档保护仍启用。";
                }
            }
            else Close();
        }
    }

    private void EnterScope()
    {
        oldOffline = PhotonNetwork.OfflineMode;
        oldQuicksave = Peak.Quicksave.ShouldUseSaveData;
        oldTimeScale = Time.timeScale;
        oldLock = Cursor.lockState; oldCursor = Cursor.visible;
        scope = true;
        ReplaySafety.Active = true;
        Peak.Quicksave.ShouldUseSaveData = false;
        NetworkingUtilities.EnteringRoom = true;
        if (InputSystem.actions)
        {
            oldActions.AddRange(InputSystem.actions.Where(a => a.enabled));
            InputSystem.actions.Disable();
        }
        // Ordinary play keeps both mods active. Do not feed a replay back into the live footprint recorder.
        foreach (var info in Chainloader.PluginInfos.Values)
            if (info.Instance && info.Instance.GetType().Assembly.GetName().Name == "PeakTrailRecorder")
            { suspended.Add((info.Instance, info.Instance.enabled)); info.Instance.enabled = false; }
        Time.timeScale = 1;
    }

    public void Close()
    {
        if (!Busy) return;
        if (phase == Phase.Return || phase == Phase.Title) return;
        if (scope) ReplaySafety.Returning = true;
        Restore(() => Playback?.Dispose()); Playback = null;
        Restore(() => timeline?.Dispose()); timeline = null;
        foreach (var actor in actors.Values) Restore(actor.Dispose); actors.Clear();
        CancelRead();
        Restore(() => (disconnect as IDisposable)?.Dispose()); disconnect = null;
        if (!scope) { phase = Phase.Idle; return; }
        nativeLoading?.Release();
        Time.timeScale = 1;
        Transition(Phase.Return, "关闭回放，返回主菜单…");
    }

    private void Finish()
    {
        recoveryStarted = false;
        ClearNativeLoading(); titleLoadStarted = false;
        loading = null; timeline = null; reading = null;
        if (scope)
        {
            Restore(() => Time.timeScale = oldTimeScale);
            Restore(() => { Cursor.lockState = oldLock; Cursor.visible = oldCursor; });
            Restore(() => Peak.Quicksave.ShouldUseSaveData = oldQuicksave);
            Restore(() => GameHandler.ClearStatus<IsDisconnectingForOfflineMode>());
            Restore(() => NetworkingUtilities.EnteringRoom = false);
            Restore(() => PhotonNetwork.OfflineMode = oldOffline);
            foreach (var saved in suspended) Restore(() => { if (saved.Plugin) saved.Plugin.enabled = saved.Enabled; });
            suspended.Clear();
            foreach (var action in oldActions) Restore(action.Enable);
            oldActions.Clear();
            scope = false;
            Restore(() => ReplaySafety.Active = false);
            Restore(() => { if (!oldOffline && !PhotonNetwork.IsConnected) NetworkingUtilities.ConnectToNetwork(); });
        }
        phase = Phase.Idle;
        note(string.IsNullOrEmpty(failure) ? "已返回主菜单。原足迹 Mod 和正常游戏设置已恢复。" : "回放未完成：" + failure + "（已返回主菜单）");
    }

    private void CancelRead()
    {
        readCancellation?.Cancel(); readCancellation?.Dispose(); readCancellation = null;
        if (reading != null) _ = reading.ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose();
            else _ = t.Exception;
        });
        reading = null;
    }

    private void ClearNativeLoading()
    {
        var owned = nativeLoading; nativeLoading = null;
        if (owned != null) Restore(owned.Dispose);
    }

    private void Report(Exception e) { try { error(e); } catch { /* Recovery must survive a removed plugin logger. */ } }
    private void Restore(Action action) { try { action(); } catch (Exception e) { Report(e); } }

    public void Dispose()
    {
        Close();
        // Disabling/unloading the plugin cannot cancel Unity's pending load. A tiny
        // independent runner completes the same guarded return flow before unpatching.
        if (scope && !recoveryStarted)
        {
            recoveryStarted = true;
            var runner = new GameObject("PEAK Memories - finish safe return").AddComponent<ReplayRecovery>();
            UnityEngine.Object.DontDestroyOnLoad(runner.gameObject);
            runner.Theatre = this;
        }
    }
}

internal sealed class ReplayRecovery : MonoBehaviour
{
    internal ReplayTheatre Theatre = null!;
    private bool quitting;
    private void Update()
    {
        if (quitting) return;
        Theatre.Tick(0);
        if (!Theatre.Busy) Destroy(gameObject);
    }
    private void OnApplicationQuit() { quitting = true; }
}
