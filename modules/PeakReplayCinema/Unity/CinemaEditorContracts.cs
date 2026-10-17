using System;
using System.Collections.Generic;

namespace PeakReplayLab.Cinema;

internal enum CinemaEditorCommandKind
{
    Seek, TogglePlayback, SetPlaybackSpeed,
    SetPreset, SetPresetAmount, SetPresetDuration, GeneratePreset, SetTransition,
    AddKey, OverwriteKey, DeleteKey, PreviousKey, NextKey, Preview, Stop,
    Save, Load, SetProjectName, ResetHorizon, FrameActor, SetFov,
    ToggleGrid, SetAspect, ToggleHud, ToggleNames, ToggleCleanPreview,
    SetFocusMode, SetFocusDistance, SetBlur, SetDofQuality,
    SetColorPreset, SetColorStrength, SetExposure, SetContrast, SetSaturation, SetTemperature, ResetGrade,
    Close,
    SelectKey, MoveKey, DuplicateKey, EditKeyPose, Undo, Redo,
    RefreshProjects, SelectProject, RenameProject, CopyProject, NewProject, RestoreRecovery,
    ConfirmSave, ConfirmDiscard, CancelPending
}

internal readonly struct CinemaEditorCommand
{
    public CinemaEditorCommandKind Kind { get; }
    public double Number { get; }
    public string Text { get; }
    public CinemaEditorCommand(CinemaEditorCommandKind kind, double number = 0, string text = "")
    { Kind = kind; Number = number; Text = text; }
}

internal interface ICinemaEditorHost
{
    void Execute(CinemaEditorCommand command);
}

internal sealed class CinemaEditorState
{
    public double Time, RequestedTime, Duration;
    public bool Paused, Previewing, Buffering, Dirty, IdentityReady, CleanPreview;
    // UI may show a pending seek while camera sampling still uses committed Time.
    public double DisplayTime => Buffering ? RequestedTime : Time;
    public float Speed = 1;
    public int KeyIndex = -1, KeyCount;
    public string Status = "", Warning = "", ProjectName = "default", ActiveProjectName = "default";
    public IReadOnlyList<CinemaKeyframe> Keys = Array.Empty<CinemaKeyframe>();
    public CinemaPose EditPose;
    public double EditTime;
    public int EditRevision, TrackRevision;
    public bool CanUndo, CanRedo, RecoveryAvailable, AutosaveBusy;
    public string[] Projects = Array.Empty<string>();
    public string[] RecoveryProjects = Array.Empty<string>();
    public int ProjectIndex = -1;
    public string PendingAction = "", AutosaveStatus = "";
    public bool HasPendingAction => PendingAction.Length != 0;
    public CinemaPreset Preset;
    public float Amount = 5, ShotDuration = 6;
    public CinemaTransition Transition;
    public float Fov = 60;
    public bool Grid, HideHud, HideNames;
    public CinemaFrameAspect Aspect;
    public CinemaDofMode FocusMode;
    public float FocusDistance = 10, BlurStrength = .25f;
    public CinemaDofQuality DofQuality;
    public CinemaColorPreset ColorPreset;
    public float ColorStrength = .5f, Exposure, Contrast, Saturation, Temperature;
}
