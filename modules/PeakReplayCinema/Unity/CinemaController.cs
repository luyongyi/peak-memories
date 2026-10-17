using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PeakReplayLab.Cinema;

// Playback is the host, never a dependency of Core. Camera time is sampled from
// the committed replay cursor; no cinema animation integrates wall-clock time.
internal sealed partial class CinemaController : IReplayPresentationExtension, ICinemaEditorHost
{
    private readonly PlaybackSession session;
    private readonly Camera camera;
    private readonly string sourcePath;
    private readonly Action<bool> consoleVisibility, hudVisibility;
    private readonly Action<string> report;
    private readonly Vector3 previousPosition;
    private readonly Quaternion previousRotation;
    private readonly float previousFov;
    private readonly bool previousFree, previousPaused, previousNames;
    private CinemaEditorUI? editor;
    private CinemaEffects? effects;
    private Task<string>? identity;
    private CinemaProject project = new("pending");
    private readonly CinemaEditorState state = new();
    private readonly CinemaEffectSettings effectSettings = new();
    private string projectName = "default", status = "添加关键帧保存当前构图 · F8 返回普通回放 · H 纯画面";
    private string identityWarning = "", trackWarning = "", focusWarning = "";
    private bool identityReady, dirty, previewing, cleanPreview;
    private double? selectedTime, pendingPose;
    private CinemaPreset preset;
    private CinemaTransition transition;
    private float amount = 5, shotDuration = 6, focusDistance = 10;
    public bool IsDisposed { get; private set; }
    public bool Previewing => previewing;
    public bool HasPendingAction => pendingAction.Length != 0;
    public string EffectsDiagnostic => effects?.Diagnostic ?? "inactive";
    public int KeyframeCount => project.Track.Count;

    public CinemaController(PlaybackSession session, string sourcePath, Action<bool> consoleVisibility,
        Action<bool> hudVisibility, Action<string> report)
    {
        this.session = session; camera = session.PresentationCamera;
        this.sourcePath = Path.GetFullPath(sourcePath);
        this.consoleVisibility = consoleVisibility; this.hudVisibility = hudVisibility; this.report = report;
        previousPosition = camera.transform.position; previousRotation = camera.transform.rotation;
        previousFov = camera.fieldOfView; previousFree = session.FreeCamera;
        previousPaused = session.Paused; previousNames = session.ShowNames;
        try
        {
            if (!File.Exists(this.sourcePath)) throw new FileNotFoundException("录像文件已不可用。", this.sourcePath);
            session.SetFreeCamera(true); session.Paused = true;
            effects = new CinemaEffects(camera);
            editor = new CinemaEditorUI(this);
            session.AttachPresentation(this);
            consoleVisibility(false);
            string identityPath = this.sourcePath;
            identity = Task.Run(() => CinemaRecordingIdentity.Read(identityPath));
            CameraApplied();
        }
        catch { Dispose(); throw; }
    }

    public void HandleInput()
    {
        if (IsDisposed) return;
        var keyboard = Keyboard.current;
        if (keyboard == null || editor?.EditingText == true || pendingAction.Length != 0) return;
        if (keyboard.ctrlKey.isPressed && keyboard.zKey.wasPressedThisFrame) { Execute(new(CinemaEditorCommandKind.Undo)); return; }
        if (keyboard.ctrlKey.isPressed && keyboard.yKey.wasPressedThisFrame) { Execute(new(CinemaEditorCommandKind.Redo)); return; }
        if (keyboard.hKey.wasPressedThisFrame) Execute(new(CinemaEditorCommandKind.ToggleCleanPreview));
        if (keyboard.cKey.wasPressedThisFrame) TakeOver("已手动接管镜头，添加或覆盖关键帧保存构图。");
        if (keyboard.spaceKey.wasPressedThisFrame) Execute(new(CinemaEditorCommandKind.TogglePlayback));
        if (keyboard.tabKey.wasPressedThisFrame) session.CycleFocus();
    }

    public bool ApplyCamera(float realDelta)
    {
        if (IsDisposed) return false;
        if (pendingAction.Length != 0) return true;
        if (previewing && ManualInput()) TakeOver("已手动接管镜头，原关键帧保留。");
        if (previewing)
        {
            if (session.Buffering) return true;
            if (project.Track.TrySample(session.Time, session.PresentationSegment, out var pose))
            { ApplyPose(pose); trackWarning = ""; }
            else trackWarning = "当前关卡没有对应的镜头关键帧，请为该关卡另设机位。";
            var keys = project.Track.Keyframes;
            if (keys.Count > 1 && session.Time >= keys[keys.Count - 1].Time) session.Paused = true;
            return true;
        }
        if (pendingPose.HasValue && !session.Buffering && Math.Abs(session.Time - pendingPose.Value) < .02)
        {
            pendingPose = null;
            if (project.Track.TrySample(session.Time, session.PresentationSegment, out var pose))
            { ApplyPose(pose); session.SyncCameraOrientation(); return true; }
        }
        return false;
    }

    private bool ManualInput()
    {
        if (!session.CameraLooking) return false;
        var keyboard = Keyboard.current;
        if (keyboard != null && (keyboard.wKey.isPressed || keyboard.aKey.isPressed || keyboard.sKey.isPressed ||
            keyboard.dKey.isPressed || keyboard.qKey.isPressed || keyboard.eKey.isPressed)) return true;
        return session.CameraReadsLookDelta && Mouse.current?.delta.ReadValue().sqrMagnitude > .01f;
    }

    public void CameraApplied()
    {
        if (IsDisposed) return;
        if (identity is { IsCompleted: true })
        {
            try { project.Fingerprint = identity.GetAwaiter().GetResult(); identityReady = true; InitializeSavedBaseline(); RefreshProjects(); }
            catch (Exception error) { identityWarning = "录像身份读取失败，保存与加载暂不可用：" + error.Message; }
            identity = null;
        }
        UpdateRecovery();
        var settings = project.Settings;
        float activeFocus = focusDistance;
        focusWarning = "";
        effectSettings.Preset = (CinemaEffectPreset)settings.ColorPreset;
        effectSettings.Intensity = settings.ColorStrength; effectSettings.Exposure = settings.Exposure;
        effectSettings.Contrast = (settings.Contrast - 1) * 100;
        effectSettings.Saturation = (settings.Saturation - 1) * 100;
        effectSettings.Temperature = settings.Temperature * 100;
        effectSettings.DofMode = (CinemaEffectFocusMode)settings.DofMode;
        effectSettings.BlurStrength = settings.Blur;
        effectSettings.DofQuality = (CinemaEffectQuality)settings.Quality;
        if (settings.DofMode == CinemaDofMode.Actor)
        {
            if (session.TryGetActorFocusPoint(settings.FocusActorId, out var target))
                activeFocus = Mathf.Max(.05f, Vector3.Distance(camera.transform.position, target));
            else
            {
                effectSettings.DofMode = CinemaEffectFocusMode.Off;
                focusWarning = "对焦人物当前未显示，暂时关闭电影虚化。";
            }
        }
        effects?.Apply(effectSettings, activeFocus);
        session.ShowNames = !settings.HideNames && !cleanPreview;
        hudVisibility(!settings.HideHud && !cleanPreview);
        RefreshState();
        editor?.Refresh(state, session.CameraLooking);
    }

    private void RefreshState()
    {
        var settings = project.Settings;
        state.Time = session.Time; state.RequestedTime = session.RequestedTime; state.Duration = session.Duration;
        state.Paused = session.Paused; state.Previewing = previewing; state.Buffering = session.Buffering;
        state.Speed = session.Speed; state.Dirty = dirty; state.IdentityReady = identityReady;
        state.CleanPreview = cleanPreview; state.ProjectName = projectName;
        RefreshEditingState();
        state.KeyCount = project.Track.Count; state.KeyIndex = SelectedIndex();
        state.Status = session.Buffering ? session.BufferingLabel : status;
        state.Warning = JoinWarnings(identityWarning, trackWarning, focusWarning, recoveryWarning, effects?.Warning ?? "");
        state.Preset = preset; state.Amount = amount; state.ShotDuration = shotDuration; state.Transition = transition;
        state.Fov = camera.fieldOfView; state.Grid = settings.Grid; state.Aspect = settings.FrameAspect;
        state.HideHud = settings.HideHud; state.HideNames = settings.HideNames;
        state.FocusMode = settings.DofMode; state.FocusDistance = focusDistance; state.BlurStrength = settings.Blur;
        state.DofQuality = settings.Quality; state.ColorPreset = settings.ColorPreset;
        state.ColorStrength = settings.ColorStrength; state.Exposure = settings.Exposure;
        state.Contrast = (settings.Contrast - 1) * 100;
        state.Saturation = (settings.Saturation - 1) * 100; state.Temperature = settings.Temperature * 100;
    }

    private static string JoinWarnings(params string[] warnings)
    {
        string value = "";
        foreach (string warning in warnings)
            if (warning.Length != 0) value += (value.Length == 0 ? "" : " · ") + warning;
        return value;
    }

    public void Execute(CinemaEditorCommand command)
    {
        if (IsDisposed) return;
        EditorSnapshot? before = null;
        try
        {
            if (pendingAction.Length != 0 && command.Kind != CinemaEditorCommandKind.ConfirmSave &&
                command.Kind != CinemaEditorCommandKind.ConfirmDiscard && command.Kind != CinemaEditorCommandKind.CancelPending) return;
            double value = command.Number;
            if (!CinemaLimits.Finite(value)) throw new ArgumentException("镜头参数必须为有限数值。");
            if (IsHistoryCommand(command.Kind)) before = Snapshot();
            var settings = project.Settings;
            switch (command.Kind)
            {
                case CinemaEditorCommandKind.Close: RequestAction("Close"); break;
                case CinemaEditorCommandKind.Undo: Undo(); break;
                case CinemaEditorCommandKind.Redo: Redo(); break;
                case CinemaEditorCommandKind.SelectKey: SelectKey(value); break;
                case CinemaEditorCommandKind.MoveKey: MoveKey(value, false); break;
                case CinemaEditorCommandKind.DuplicateKey: MoveKey(value, true); break;
                case CinemaEditorCommandKind.EditKeyPose: EditKeyPose(command.Text); break;
                case CinemaEditorCommandKind.RefreshProjects: RefreshProjects(); break;
                case CinemaEditorCommandKind.SelectProject: SelectProject(value); break;
                case CinemaEditorCommandKind.RenameProject: RenameProject(command.Text); break;
                case CinemaEditorCommandKind.CopyProject: CopyProject(command.Text); break;
                case CinemaEditorCommandKind.NewProject: RequestAction("NewProject"); break;
                case CinemaEditorCommandKind.RestoreRecovery: RequestAction("RestoreRecovery"); break;
                case CinemaEditorCommandKind.ConfirmSave: ConfirmPending(true); break;
                case CinemaEditorCommandKind.ConfirmDiscard: ConfirmPending(false); break;
                case CinemaEditorCommandKind.CancelPending: pendingAction = ""; discardRecovery = false; afterClose = null; status = "继续编辑，未保存修改已保留。"; break;
                case CinemaEditorCommandKind.Seek:
                    pendingPose = null;
                    session.Seek(Math.Max(0, Math.Min(session.Duration, value))); break;
                case CinemaEditorCommandKind.TogglePlayback: session.Paused = !session.Paused; break;
                case CinemaEditorCommandKind.SetPlaybackSpeed: session.Speed = (float)Math.Max(.25, Math.Min(2, value)); break;
                case CinemaEditorCommandKind.SetPreset:
                    preset = EnumValue<CinemaPreset>(value); amount = preset == CinemaPreset.Orbit ? 90 : 5; break;
                case CinemaEditorCommandKind.SetPresetAmount: amount = (float)Math.Max(preset == CinemaPreset.Orbit ? -360 : -100, Math.Min(preset == CinemaPreset.Orbit ? 360 : 100, value)); break;
                case CinemaEditorCommandKind.SetPresetDuration: shotDuration = (float)Math.Max(.1, Math.Min(120, value)); break;
                case CinemaEditorCommandKind.GeneratePreset: GeneratePreset(); break;
                case CinemaEditorCommandKind.SetTransition:
                    transition = EnumValue<CinemaTransition>(value);
                    int changeIndex = SelectedIndex();
                    if (changeIndex >= 0)
                    {
                        var key = project.Track.Keyframes[changeIndex];
                        project.Track.Upsert(new(key.Time, key.SegmentId, key.Pose, transition)); dirty = true;
                    }
                    break;
                case CinemaEditorCommandKind.AddKey: AddKey(session.Time); break;
                case CinemaEditorCommandKind.OverwriteKey:
                    int replaceIndex = SelectedIndex();
                    if (replaceIndex < 0) throw new InvalidOperationException("先选择一个关键帧。");
                    var replaceKey = project.Track.Keyframes[replaceIndex];
                    if (session.Buffering || Math.Abs(session.Time - replaceKey.Time) > .02 || replaceKey.SegmentId != session.PresentationSegment)
                        throw new InvalidOperationException("先用前后关键帧按钮回到所选时刻，再覆盖构图。");
                    AddKey(replaceKey.Time); break;
                case CinemaEditorCommandKind.DeleteKey:
                    int removeIndex = SelectedIndex();
                    if (removeIndex < 0) throw new InvalidOperationException("先选择一个关键帧。");
                    TakeOver("已删除所选关键帧。"); project.Track.RemoveAt(project.Track.Keyframes[removeIndex].Time);
                    selectedTime = null; dirty = true; break;
                case CinemaEditorCommandKind.PreviousKey: JumpKey(false); break;
                case CinemaEditorCommandKind.NextKey: JumpKey(true); break;
                case CinemaEditorCommandKind.Preview: Preview(); break;
                case CinemaEditorCommandKind.Stop: TakeOver("预览已停止，可调整当前机位。"); session.Paused = true; break;
                case CinemaEditorCommandKind.SetProjectName:
                    if (command.Text.Length > 40) throw new ArgumentException("方案名称最多 40 个字符。");
                    _ = CinemaProjectFiles.RecoveryPath(sourcePath, command.Text);
                    projectName = command.Text.Trim(); RefreshRecoveryAvailability(); break;
                case CinemaEditorCommandKind.Save: Save(); break;
                case CinemaEditorCommandKind.Load: RequestAction("Load"); break;
                case CinemaEditorCommandKind.ResetHorizon:
                    TakeOver("机位已恢复水平；添加或覆盖关键帧保存。");
                    var angles = camera.transform.eulerAngles;
                    camera.transform.rotation = Quaternion.Euler(angles.x, angles.y, 0); session.SyncCameraOrientation(); break;
                case CinemaEditorCommandKind.FrameActor:
                    if (!session.TryGetActorFocusPoint(session.FocusId, out var subject)) throw new InvalidOperationException("当前没有可见观察人物。");
                    TakeOver("已对准当前人物；添加或覆盖关键帧保存。");
                    var offset = subject - camera.transform.position;
                    if (offset.sqrMagnitude > .0001f) camera.transform.rotation = Quaternion.LookRotation(offset, Vector3.up);
                    session.SyncCameraOrientation(); break;
                case CinemaEditorCommandKind.SetFov:
                    TakeOver("视野已调整；添加或覆盖关键帧保存。"); camera.fieldOfView = (float)Math.Max(10, Math.Min(140, value)); break;
                case CinemaEditorCommandKind.ToggleGrid: settings.Grid = !settings.Grid; dirty = true; break;
                case CinemaEditorCommandKind.SetAspect: settings.FrameAspect = EnumValue<CinemaFrameAspect>(value); dirty = true; break;
                case CinemaEditorCommandKind.ToggleHud: settings.HideHud = !settings.HideHud; dirty = true; break;
                case CinemaEditorCommandKind.ToggleNames: settings.HideNames = !settings.HideNames; dirty = true; break;
                case CinemaEditorCommandKind.ToggleCleanPreview: cleanPreview = !cleanPreview; break;
                case CinemaEditorCommandKind.SetFocusMode:
                    settings.DofMode = EnumValue<CinemaDofMode>(value);
                    if (settings.DofMode == CinemaDofMode.Actor) settings.FocusActorId = session.FocusId;
                    dirty = true; break;
                case CinemaEditorCommandKind.SetFocusDistance:
                    TakeOver("对焦距离已调整；添加或覆盖关键帧保存。"); focusDistance = (float)Math.Max(.05, Math.Min(100000, value)); break;
                case CinemaEditorCommandKind.SetBlur: settings.Blur = (float)Math.Max(0, Math.Min(1, value)); dirty = true; break;
                case CinemaEditorCommandKind.SetDofQuality: settings.Quality = EnumValue<CinemaDofQuality>(value); dirty = true; break;
                case CinemaEditorCommandKind.SetColorPreset: settings.ColorPreset = EnumValue<CinemaColorPreset>(value); dirty = true; break;
                case CinemaEditorCommandKind.SetColorStrength: settings.ColorStrength = (float)Math.Max(0, Math.Min(1, value)); dirty = true; break;
                case CinemaEditorCommandKind.SetExposure: settings.Exposure = (float)Math.Max(-5, Math.Min(5, value)); dirty = true; break;
                case CinemaEditorCommandKind.SetContrast: settings.Contrast = 1 + (float)Math.Max(-50, Math.Min(50, value)) / 100; dirty = true; break;
                case CinemaEditorCommandKind.SetSaturation: settings.Saturation = 1 + (float)Math.Max(-100, Math.Min(100, value)) / 100; dirty = true; break;
                case CinemaEditorCommandKind.SetTemperature: settings.Temperature = (float)Math.Max(-100, Math.Min(100, value)) / 100; dirty = true; break;
                case CinemaEditorCommandKind.ResetGrade:
                    settings.ColorPreset = CinemaColorPreset.Original; settings.ColorStrength = .5f;
                    settings.Exposure = settings.Temperature = 0; settings.Contrast = settings.Saturation = 1;
                    dirty = true; status = "调色已还原为原片。"; break;
                default: throw new ArgumentException("Unsupported cinema editor command.");
            }
            if (before != null) RecordHistory(command.Kind, before);
            else if (command.Kind != CinemaEditorCommandKind.Undo && command.Kind != CinemaEditorCommandKind.Redo) lastHistoryKind = null;
            RecomputeDirty();
            CameraApplied();
        }
        catch (Exception error)
        {
            if (before != null) RestoreSnapshot(before, true);
            RecomputeDirty();
            status = "操作未完成：" + error.Message;
            report("Cinema editor command " + command.Kind + ": " + error);
            if (!IsDisposed) { RefreshState(); editor?.Refresh(state, session.CameraLooking); }
        }
    }

    private static T EnumValue<T>(double value) where T : struct
    {
        if (value != Math.Truncate(value) || value < int.MinValue || value > int.MaxValue || !Enum.IsDefined(typeof(T), (int)value))
            throw new ArgumentOutOfRangeException(nameof(value));
        return (T)Enum.ToObject(typeof(T), (int)value);
    }

    private CinemaPose CapturePose()
    {
        float focus = focusDistance;
        if (project.Settings.DofMode == CinemaDofMode.Actor && session.TryGetActorFocusPoint(project.Settings.FocusActorId, out var target))
            focus = Mathf.Max(.05f, Vector3.Distance(camera.transform.position, target));
        var p = camera.transform.position; var q = camera.transform.rotation;
        return new(new(p.x, p.y, p.z), new(q.x, q.y, q.z, q.w), camera.fieldOfView, focus);
    }

    private void ApplyPose(CinemaPose pose)
    {
        camera.transform.SetPositionAndRotation(new(pose.Position.X, pose.Position.Y, pose.Position.Z),
            new(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W));
        camera.fieldOfView = pose.FieldOfView; focusDistance = pose.FocusDistance;
    }

    private void TakeOver(string message)
    {
        previewing = false; pendingPose = null; trackWarning = "";
        session.SetFreeCamera(true); status = message;
    }

    private void AddKey(double time)
    {
        if (session.Buffering) throw new InvalidOperationException("等目标画面读取完成后再保存关键帧。");
        var nextKey = new CinemaKeyframe(time, session.PresentationSegment, CapturePose(), transition);
        project.Track.Upsert(nextKey);
        TakeOver("关键帧已保存到当前方案，点击保存写入文件。");
        selectedTime = time; dirty = true;
    }

    private int SelectedIndex()
    {
        if (!selectedTime.HasValue) return -1;
        var keys = project.Track.Keyframes;
        for (int i = 0; i < keys.Count; i++) if (keys[i].Time == selectedTime.Value) return i;
        return -1;
    }

    private void GeneratePreset()
    {
        if (session.Buffering) throw new InvalidOperationException("等目标画面读取完成后再生成镜头。");
        double duration = Math.Min(shotDuration, session.Duration - session.Time);
        if (duration < .01) throw new InvalidOperationException("录像末尾没有足够的镜头时长。");
        CinemaVector3? target = null;
        if (session.TryGetActorFocusPoint(session.FocusId, out var point)) target = new(point.x, point.y, point.z);
        var generated = CinemaPresets.Create(preset, session.Time, duration, session.PresentationSegment, CapturePose(), target, amount);
        var retained = new System.Collections.Generic.List<CinemaKeyframe>();
        foreach (var key in project.Track.Keyframes)
            if (key.Time < session.Time || key.Time > session.Time + duration) retained.Add(key);
        var next = new CinemaTrack(retained);
        next.UpsertRange(generated);
        TakeOver(""); session.Paused = true;
        project.Track = next;
        selectedTime = generated[0].Time; transition = generated[0].Transition; dirty = true;
        status = "当前区间已生成 " + generated.Count + " 个关键帧；点击预览播放或保存方案。";
    }

    private void JumpKey(bool next)
    {
        var keys = project.Track.Keyframes;
        if (keys.Count == 0) throw new InvalidOperationException("先添加一个关键帧。");
        int index = next ? keys.Count - 1 : 0;
        if (next)
        {
            for (int i = 0; i < keys.Count; i++) if (keys[i].Time > session.Time + .0001) { index = i; break; }
        }
        else
        {
            for (int i = keys.Count - 1; i >= 0; i--) if (keys[i].Time < session.Time - .0001) { index = i; break; }
        }
        TakeOver("已选择关键帧 " + (index + 1) + "。");
        selectedTime = pendingPose = keys[index].Time; transition = keys[index].Transition; editRevision++;
        session.Paused = true; session.Seek(keys[index].Time);
    }

    private void Preview()
    {
        if (project.Track.Count == 0) throw new InvalidOperationException("先添加关键帧或生成预设镜头。");
        previewing = true; pendingPose = null;
        session.Seek(project.Track.Keyframes[0].Time); session.Paused = false;
        status = "按录像时间预览 · 右键转向或移动可手动接管 · H 纯画面";
    }

    private void Save()
    {
        if (!identityReady) throw new InvalidOperationException("录像身份尚未就绪，请稍后再保存。");
        if (project.Track.Count == 0) AddKey(session.Time);
        DrainRecovery();
        _ = CinemaProjectFiles.RecoveryPath(sourcePath, projectName);
        CinemaProjectStore.SaveAtomic(CinemaRecordingIdentity.Sidecar(sourcePath, projectName), project);
        string previousName = savedProjectName;
        MarkSaved();
        CinemaProjectFiles.RemoveRecovery(sourcePath, projectName);
        if (previousName != projectName) CinemaProjectFiles.RemoveRecovery(sourcePath, previousName);
        RefreshProjects(); status = "已保存方案「" + projectName + "」；原录像保持原样。";
    }

    private void Load()
    {
        if (!identityReady) throw new InvalidOperationException("录像身份尚未就绪，请稍后再加载。");
        DrainRecovery();
        string path = CinemaRecordingIdentity.Sidecar(sourcePath, projectName);
        if (!File.Exists(path) && File.Exists(CinemaProjectFiles.RecoveryPath(sourcePath, projectName)))
            throw new InvalidOperationException("此方案只有自动恢复副本，请点击恢复草稿后另行保存。");
        var next = CinemaProjectStore.Load(path, project.Fingerprint);
        foreach (var key in next.Track.Keyframes)
            if (key.Time > session.Duration + .001) throw new InvalidDataException("方案关键帧超出当前录像时长。");
        TakeOver("已加载方案「" + projectName + "」。");
        project = next; dirty = false; selectedTime = null; MarkSaved(); ClearHistory(); RefreshProjects();
        if (project.Track.Count != 0)
        {
            var first = project.Track.Keyframes[0]; selectedTime = pendingPose = first.Time;
            transition = first.Transition; session.Paused = true; session.Seek(first.Time);
        }
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        PreserveRecoveryOnDispose();
        IsDisposed = true; session.DetachPresentation(this);
        void Restore(Action action)
        {
            try { action(); } catch (Exception error) { try { report("Cinema restore: " + error); } catch { } }
        }
        Restore(() => editor?.Dispose()); editor = null;
        Restore(() => effects?.Dispose()); effects = null;
        Restore(() => { if (camera) { camera.transform.SetPositionAndRotation(previousPosition, previousRotation); camera.fieldOfView = previousFov; } });
        Restore(() => session.ShowNames = previousNames);
        Restore(() => session.SetFreeCamera(previousFree));
        Restore(() => session.Paused = previousPaused);
        Restore(() => hudVisibility(true));
        Restore(() => consoleVisibility(true));
        // A bounded identity worker owns its own stream and no Unity objects.
        // Observe failure even if the user closes before it completes.
        if (identity != null) _ = identity.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        identity = null;
    }
}
