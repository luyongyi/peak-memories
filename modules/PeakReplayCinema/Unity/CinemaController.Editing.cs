using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace PeakReplayLab.Cinema;

// Editing owns bounded in-memory snapshots and separate recovery sidecars. It never
// rewrites the source recording or installs a dependency in the capture pipeline.
internal sealed partial class CinemaController
{
    private const int HistoryLimit = 64;
    private readonly List<EditorSnapshot> undo = new(), redo = new();
    private CinemaEditorCommandKind? lastHistoryKind;
    private DateTime lastHistoryAt;
    private CinemaProject savedProject = new("pending");
    private string savedProjectName = "default";
    private string pendingAction = "", pendingProjectName = "";
    private Action? afterClose;
    private string[] projects = Array.Empty<string>(), recoveryProjects = Array.Empty<string>();
    private bool recoveryAvailable, discardRecovery;
    private Task? recoveryWrite;
    private DateTime nextRecoveryAt = DateTime.UtcNow.AddSeconds(10);
    private string autosaveStatus = "", recoveryWarning = "";
    private int editRevision, trackRevision;

    private sealed class EditorSnapshot
    {
        internal readonly CinemaProject Project;
        internal readonly string Name;
        internal readonly double? Selected;
        internal readonly CinemaPose Camera;
        internal readonly CinemaTransition Transition;
        internal readonly CinemaPreset Preset;
        internal readonly float Amount, Duration;
        internal readonly bool Previewing, Paused, Free;
        internal readonly double? PendingPose;
        internal EditorSnapshot(CinemaController owner)
        {
            Project = CloneProject(owner.project); Name = owner.projectName;
            Selected = owner.selectedTime; Camera = owner.CapturePose();
            Transition = owner.transition; Preset = owner.preset;
            Amount = owner.amount; Duration = owner.shotDuration;
            Previewing = owner.previewing; Paused = owner.session.Paused; Free = owner.session.FreeCamera; PendingPose = owner.pendingPose;
        }
    }

    private static CinemaProject CloneProject(CinemaProject source)
    {
        var s = source.Settings;
        return new CinemaProject(source.Fingerprint)
        {
            Version = source.Version,
            Track = new CinemaTrack(new List<CinemaKeyframe>(source.Track.Keyframes)),
            Settings = new CinemaSettings
            {
                ColorPreset = s.ColorPreset, ColorStrength = s.ColorStrength,
                Exposure = s.Exposure, Contrast = s.Contrast, Saturation = s.Saturation,
                Temperature = s.Temperature, DofMode = s.DofMode, Blur = s.Blur,
                Quality = s.Quality, FocusActorId = s.FocusActorId, FrameAspect = s.FrameAspect,
                Grid = s.Grid, HideHud = s.HideHud, HideNames = s.HideNames
            }
        };
    }
    private static bool SamePose(CinemaPose a, CinemaPose b) =>
        a.Position.X == b.Position.X && a.Position.Y == b.Position.Y && a.Position.Z == b.Position.Z &&
        a.Rotation.X == b.Rotation.X && a.Rotation.Y == b.Rotation.Y && a.Rotation.Z == b.Rotation.Z && a.Rotation.W == b.Rotation.W &&
        a.FieldOfView == b.FieldOfView && a.FocusDistance == b.FocusDistance;
    private static bool SameProject(CinemaProject a, CinemaProject b)
    {
        if (a.Fingerprint != b.Fingerprint || a.Version != b.Version || a.Track.Count != b.Track.Count) return false;
        var x = a.Settings; var y = b.Settings;
        if (x.ColorPreset != y.ColorPreset || x.ColorStrength != y.ColorStrength || x.Exposure != y.Exposure ||
            x.Contrast != y.Contrast || x.Saturation != y.Saturation || x.Temperature != y.Temperature ||
            x.DofMode != y.DofMode || x.Blur != y.Blur || x.Quality != y.Quality || x.FocusActorId != y.FocusActorId ||
            x.FrameAspect != y.FrameAspect || x.Grid != y.Grid || x.HideHud != y.HideHud || x.HideNames != y.HideNames) return false;
        for (int i = 0; i < a.Track.Count; i++)
        {
            var left = a.Track.Keyframes[i]; var right = b.Track.Keyframes[i];
            if (left.Time != right.Time || left.SegmentId != right.SegmentId || left.Transition != right.Transition || !SamePose(left.Pose, right.Pose)) return false;
        }
        return true;
    }
    private EditorSnapshot Snapshot() => new(this);
    private void InitializeSavedBaseline()
    {
        // Identity completion must not accidentally turn edits made while hashing
        // into a saved document. Only update the fingerprint of the empty baseline.
        savedProject.Fingerprint = project.Fingerprint;
        RecomputeDirty();
    }
    private void RecomputeDirty() => dirty = recoveryRestored || !SameProject(project, savedProject);
    private void MarkSaved()
    {
        savedProject = CloneProject(project); savedProjectName = projectName; dirty = false; recoveryRestored = false;
        lastHistoryKind = null;
    }
    private static bool IsHistoryCommand(CinemaEditorCommandKind kind) => kind switch
    {
        CinemaEditorCommandKind.AddKey or CinemaEditorCommandKind.OverwriteKey or CinemaEditorCommandKind.DeleteKey or
        CinemaEditorCommandKind.MoveKey or CinemaEditorCommandKind.DuplicateKey or CinemaEditorCommandKind.EditKeyPose or
        CinemaEditorCommandKind.GeneratePreset or CinemaEditorCommandKind.SetTransition or
        CinemaEditorCommandKind.SetProjectName or CinemaEditorCommandKind.ResetHorizon or CinemaEditorCommandKind.FrameActor or
        CinemaEditorCommandKind.SetFov or CinemaEditorCommandKind.SetFocusDistance or CinemaEditorCommandKind.ToggleGrid or
        CinemaEditorCommandKind.SetAspect or CinemaEditorCommandKind.ToggleHud or CinemaEditorCommandKind.ToggleNames or
        CinemaEditorCommandKind.SetFocusMode or CinemaEditorCommandKind.SetBlur or CinemaEditorCommandKind.SetDofQuality or
        CinemaEditorCommandKind.SetColorPreset or CinemaEditorCommandKind.SetColorStrength or CinemaEditorCommandKind.SetExposure or
        CinemaEditorCommandKind.SetContrast or CinemaEditorCommandKind.SetSaturation or CinemaEditorCommandKind.SetTemperature or
        CinemaEditorCommandKind.ResetGrade => true,
        _ => false
    };
    private static bool IsContinuous(CinemaEditorCommandKind kind) => kind switch
    {
        CinemaEditorCommandKind.SetFov or CinemaEditorCommandKind.SetFocusDistance or CinemaEditorCommandKind.SetBlur or
        CinemaEditorCommandKind.SetColorStrength or CinemaEditorCommandKind.SetExposure or CinemaEditorCommandKind.SetContrast or
        CinemaEditorCommandKind.SetSaturation or CinemaEditorCommandKind.SetTemperature => true,
        _ => false
    };
    private void RecordHistory(CinemaEditorCommandKind kind, EditorSnapshot before)
    {
        bool projectChanged = !SameProject(before.Project, project);
        if (!projectChanged && before.Name == projectName && before.Transition == transition && SamePose(before.Camera, CapturePose())) return;
        DateTime now = DateTime.UtcNow;
        if (!IsContinuous(kind) || lastHistoryKind != kind || now - lastHistoryAt > TimeSpan.FromSeconds(.6))
        {
            undo.Add(before);
            if (undo.Count > HistoryLimit) undo.RemoveAt(0);
        }
        redo.Clear(); lastHistoryKind = kind; lastHistoryAt = now;
        editRevision++;
        if (projectChanged) trackRevision++;
        discardRecovery = false;
    }
    private void RestoreSnapshot(EditorSnapshot snapshot, bool rollback = false)
    {
        TakeOver("已恢复编辑步骤。"); project = CloneProject(snapshot.Project);
        projectName = snapshot.Name; selectedTime = snapshot.Selected; transition = snapshot.Transition;
        preset = snapshot.Preset; amount = snapshot.Amount; shotDuration = snapshot.Duration;
        ApplyPose(snapshot.Camera); session.SyncCameraOrientation();
        if (rollback) { previewing = snapshot.Previewing; pendingPose = snapshot.PendingPose; session.SetFreeCamera(snapshot.Free); session.Paused = snapshot.Paused; }
        editRevision++; trackRevision++; RecomputeDirty(); RefreshRecoveryAvailability();
    }
    private void Undo()
    {
        if (undo.Count == 0) return;
        redo.Add(Snapshot()); var snapshot = undo[undo.Count - 1]; undo.RemoveAt(undo.Count - 1);
        RestoreSnapshot(snapshot); lastHistoryKind = null; status = "已撤销；可重做恢复。";
    }
    private void Redo()
    {
        if (redo.Count == 0) return;
        undo.Add(Snapshot()); var snapshot = redo[redo.Count - 1]; redo.RemoveAt(redo.Count - 1);
        RestoreSnapshot(snapshot); lastHistoryKind = null; status = "已重做。";
    }
    private void ClearHistory() { undo.Clear(); redo.Clear(); lastHistoryKind = null; editRevision++; trackRevision++; }

    private void RefreshEditingState()
    {
        int index = SelectedIndex();
        state.Keys = project.Track.Keyframes;
        state.EditPose = index >= 0 ? project.Track.Keyframes[index].Pose : CapturePose();
        state.EditTime = index >= 0 ? project.Track.Keyframes[index].Time : session.Time;
        state.EditRevision = editRevision; state.TrackRevision = trackRevision;
        state.CanUndo = undo.Count != 0; state.CanRedo = redo.Count != 0;
        state.ActiveProjectName = savedProjectName;
        state.Projects = projects; state.RecoveryProjects = recoveryProjects; state.ProjectIndex = Array.IndexOf(projects, projectName);
        state.PendingAction = pendingAction; state.RecoveryAvailable = recoveryAvailable;
        state.AutosaveBusy = recoveryWrite != null; state.AutosaveStatus = autosaveStatus;
    }
    private CinemaKeyframe SelectedKey()
    {
        int index = SelectedIndex();
        if (index < 0) throw new InvalidOperationException("先选择一个关键帧。");
        return project.Track.Keyframes[index];
    }
    private void SelectKey(double value)
    {
        if (value != Math.Truncate(value) || value < 0 || value >= project.Track.Count) throw new ArgumentOutOfRangeException(nameof(value), "关键帧不存在。");
        var key = project.Track.Keyframes[(int)value];
        TakeOver("已选择关键帧 " + ((int)value + 1) + "。");
        selectedTime = pendingPose = key.Time; transition = key.Transition;
        session.Paused = true; session.Seek(key.Time); editRevision++;
    }
    private void MoveKey(double time, bool duplicate)
    {
        var key = SelectedKey();
        CinemaLimits.Range(time, 0, session.Duration, nameof(time));
        if (time == key.Time && !duplicate) return;
        foreach (var existing in project.Track.Keyframes)
            if (Math.Abs(existing.Time - time) < .001) throw new InvalidOperationException("目标时刻已有关键帧，请选择其他时间。");
        if (!session.TryGetPresentationSegmentAt(time, out string targetSegment))
            throw new InvalidOperationException("目标时刻尚未读取，请先拖动回放时间条到目标位置并等待读取完成。");
        if (targetSegment != key.SegmentId) throw new InvalidOperationException("关键帧只能移动或复制到同一关卡时段。");
        var next = new CinemaTrack(new List<CinemaKeyframe>(project.Track.Keyframes));
        if (!duplicate) next.RemoveAt(key.Time);
        next.Upsert(new(time, key.SegmentId, key.Pose, key.Transition));
        TakeOver(duplicate ? "机位已复制到目标时间。" : "关键帧时间已调整。");
        project.Track = next; selectedTime = time; transition = key.Transition;
        // Editing time changes the track only. The replay cursor remains stable;
        // selecting the moved marker can explicitly request its new position.
    }
    private void EditKeyPose(string text)
    {
        var key = SelectedKey();
        if (session.Buffering || key.SegmentId != session.PresentationSegment)
            throw new InvalidOperationException("等所选关键帧所属关卡读取完成后再编辑机位。");
        string[] pieces = text.Split(',');
        if (pieces.Length != 8) throw new ArgumentException("机位需要 x,y,z,俯仰,朝向,倾斜,视野,对焦距离 共 8 个数字。");
        var values = new float[8];
        for (int i = 0; i < values.Length; i++)
        {
            if (!float.TryParse(pieces[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !CinemaLimits.Finite(values[i]))
                throw new ArgumentException("机位包含无效数字，请使用小数点。");
        }
        for (int i = 3; i < 6; i++) CinemaLimits.Range(values[i], -36000, 36000, "angle");
        var q = Quaternion.Euler(values[3], values[4], values[5]);
        var pose = new CinemaPose(new(values[0], values[1], values[2]), new(q.x, q.y, q.z, q.w), values[6], values[7]);
        var next = new CinemaKeyframe(key.Time, key.SegmentId, pose, key.Transition);
        project.Track.Upsert(next); TakeOver("所选关键帧机位已精确更新。");
        ApplyPose(pose); session.SyncCameraOrientation();
    }

    private void RefreshProjects()
    {
        recoveryProjects = CinemaProjectFiles.ListRecoveries(sourcePath);
        var names = new SortedSet<string>(CinemaProjectFiles.List(sourcePath), StringComparer.OrdinalIgnoreCase);
        foreach (string name in recoveryProjects) { if (names.Count >= 512) break; names.Add(name); }
        projects = new string[names.Count]; names.CopyTo(projects); RefreshRecoveryAvailability();
    }
    private void RefreshRecoveryAvailability()
    {
        try { recoveryAvailable = File.Exists(CinemaProjectFiles.RecoveryPath(sourcePath, projectName)); }
        catch (ArgumentException) { recoveryAvailable = false; }
    }
    private void SelectProject(double index)
    {
        if (index != Math.Truncate(index) || index < 0 || index >= projects.Length) throw new ArgumentOutOfRangeException(nameof(index));
        projectName = projects[(int)index]; RefreshRecoveryAvailability();
        status = "已选择方案名称，点击加载切换内容。"; editRevision++;
    }
    private void RequireReady()
    {
        if (!identityReady) throw new InvalidOperationException("录像身份尚未就绪，请稍后再操作。");
    }
    private void RenameProject(string name)
    {
        RequireReady();
        if (dirty || projectName != savedProjectName) throw new InvalidOperationException("请先加载或保存当前方案，再重命名。");
        DrainRecovery(); CinemaProjectFiles.Rename(sourcePath, projectName, name, project.Fingerprint);
        projectName = name.Trim(); MarkSaved(); ClearHistory(); RefreshProjects(); status = "方案已重命名。";
    }
    private void CopyProject(string name)
    {
        RequireReady();
        if (dirty || projectName != savedProjectName) throw new InvalidOperationException("请先加载或保存当前方案，再复制。");
        DrainRecovery(); CinemaProjectFiles.Copy(sourcePath, projectName, name, project.Fingerprint);
        projectName = name.Trim(); MarkSaved(); ClearHistory(); RefreshProjects(); status = "已创建方案副本。";
    }

    public void RequestClose(Action? onClosed = null)
    {
        if (IsDisposed) { onClosed?.Invoke(); return; }
        if (pendingAction.Length != 0) return;
        afterClose = onClosed;
        try { RequestAction("Close"); if (!IsDisposed) { RefreshState(); editor?.Refresh(state, session.CameraLooking); } }
        catch (Exception error) { status = "操作未完成：" + error.Message; report("Cinema close: " + error); }
    }
    private void RequestAction(string action)
    {
        if (pendingAction.Length != 0) return;
        if (dirty)
        {
            pendingAction = action; pendingProjectName = savedProjectName;
            status = "当前方案有未保存修改，请选择保存、放弃或继续编辑。";
            session.Paused = true; return;
        }
        PerformAction(action);
    }
    private void ConfirmPending(bool save)
    {
        if (pendingAction.Length == 0) return;
        string action = pendingAction;
        CinemaProject? recovery = null;
        if (action == "RestoreRecovery")
        {
            RequireReady(); DrainRecovery();
            recovery = CinemaProjectFiles.LoadRecovery(sourcePath, projectName, project.Fingerprint);
            ValidateDuration(recovery);
        }
        if (save)
        {
            string targetName = projectName;
            try { projectName = pendingProjectName; Save(); }
            finally { if (action != "Close") projectName = targetName; }
        } // A failure leaves the dialog and all editing state intact.
        else
        {
            DrainRecovery();
            if (action != "RestoreRecovery" || pendingProjectName != projectName)
                CinemaProjectFiles.RemoveRecovery(sourcePath, pendingProjectName);
            recoveryAvailable = false; discardRecovery = true;
        }
        // Keep the pending action on load errors too: the user can cancel without
        // losing the current document or retry after fixing the target file.
        PerformAction(action, recovery);
        pendingAction = "";
    }
    private void PerformAction(string action, CinemaProject? recovery = null)
    {
        switch (action)
        {
            case "Close":
                Dispose(); var callback = afterClose; afterClose = null; callback?.Invoke(); break;
            case "Load": Load(); discardRecovery = false; break;
            case "NewProject":
                TakeOver("已新建空白方案；输入新名称后保存。");
                project = new CinemaProject(project.Fingerprint); selectedTime = null;
                projectName = NextProjectName(); MarkSaved(); ClearHistory(); RefreshProjects(); discardRecovery = false; break;
            case "RestoreRecovery": RestoreRecovery(recovery); discardRecovery = false; break;
            default: throw new ArgumentException("未知待确认操作。");
        }
    }
    private string NextProjectName()
    {
        RefreshProjects();
        for (int i = 1; i < 100000; i++)
        {
            string name = "新方案" + i;
            if (Array.IndexOf(projects, name) < 0 && !File.Exists(CinemaRecordingIdentity.Sidecar(sourcePath, name)) &&
                !File.Exists(CinemaProjectFiles.RecoveryPath(sourcePath, name))) return name;
        }
        throw new InvalidOperationException("无法生成新的方案名称。");
    }
    private void ValidateDuration(CinemaProject candidate)
    {
        foreach (var key in candidate.Track.Keyframes)
            if (key.Time > session.Duration + .001) throw new InvalidDataException("方案关键帧超出当前录像时长。");
    }
    private void RestoreRecovery(CinemaProject? recovered = null)
    {
        RequireReady(); DrainRecovery();
        var next = recovered ?? CinemaProjectFiles.LoadRecovery(sourcePath, projectName, project.Fingerprint);
        ValidateDuration(next);
        CinemaProject baseline;
        // A valid recovery also rescues an absent or damaged ordinary sidecar.
        try { baseline = CinemaProjectStore.Load(CinemaRecordingIdentity.Sidecar(sourcePath, projectName), project.Fingerprint); }
        catch (Exception error) when (error is IOException || error is ArgumentException)
        { baseline = new CinemaProject(project.Fingerprint); }
        TakeOver("已恢复自动副本，请检查后保存正式方案。");
        project = next; savedProject = baseline; savedProjectName = projectName;
        selectedTime = null; ClearHistory();
        // A recovery is deliberately an unsaved edit even if byte-identical to a
        // prior saved copy: it requires an explicit user Save to dismiss it.
        recoveryRestored = true; RecomputeDirty(); RefreshRecoveryAvailability();
        if (project.Track.Count > 0)
        {
            var first = project.Track.Keyframes[0]; selectedTime = pendingPose = first.Time;
            transition = first.Transition; session.Paused = true; session.Seek(first.Time);
        }
    }
    private bool recoveryRestored;
    private void UpdateRecovery()
    {
        if (recoveryWrite is { IsCompleted: true })
        {
            try { recoveryWrite.GetAwaiter().GetResult(); recoveryWarning = ""; autosaveStatus = "自动恢复副本已保存"; }
            catch (Exception error) { recoveryWarning = "自动副本保存失败：" + error.Message; autosaveStatus = "自动保存失败，可手动保存"; }
            recoveryWrite = null;
            try { RefreshProjects(); } catch (Exception error) { recoveryWarning = "恢复列表更新失败：" + error.Message; }
        }
        if (!identityReady || !dirty || discardRecovery || recoveryWrite != null || DateTime.UtcNow < nextRecoveryAt || pendingAction.Length != 0) return;
        nextRecoveryAt = DateTime.UtcNow.AddSeconds(10);
        var snapshot = CloneProject(project); string name = savedProjectName;
        recoveryWrite = Task.Run(() => CinemaProjectFiles.SaveRecovery(sourcePath, name, snapshot));
        autosaveStatus = "正在保存自动恢复副本…";
    }
    private void DrainRecovery()
    {
        if (recoveryWrite == null) return;
        try { recoveryWrite.GetAwaiter().GetResult(); recoveryWarning = ""; }
        catch (Exception error) { recoveryWarning = "自动副本保存失败：" + error.Message; }
        recoveryWrite = null;
    }
    private void PreserveRecoveryOnDispose()
    {
        DrainRecovery();
        if (!dirty || discardRecovery) return;
        try
        {
            if (!identityReady && identity != null)
            { project.Fingerprint = identity.GetAwaiter().GetResult(); identityReady = true; }
            if (!identityReady) return;
            CinemaProjectFiles.SaveRecovery(sourcePath, savedProjectName, CloneProject(project));
        }
        catch (Exception error) { try { report("Cinema recovery on exit: " + error); } catch { } }
    }
}
