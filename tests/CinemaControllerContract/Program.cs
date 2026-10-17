using System.Globalization;
using PeakReplayLab;
using PeakReplayLab.Cinema;
using UnityEngine;
using UnityEngine.InputSystem;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
string root = Directory.GetCurrentDirectory();
while (!Directory.Exists(Path.Combine(root, "tests", "CinemaControllerContract")))
    root = Directory.GetParent(root)?.FullName ?? throw new InvalidOperationException("Run within the repository.");
string checks = Path.Combine(root, "local", "checks"); Directory.CreateDirectory(checks);
string fixtures = Path.Combine(checks, "cinema-controller-fixtures-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixtures);
var output = new List<string>(); int passed = 0, failed = 0, assertions = 0;

void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
void Near(double actual, double expected, string message, double tolerance = .0001) => Check(Math.Abs(actual - expected) <= tolerance, message + $" ({actual} != {expected})");
void Throws<T>(Action action, string message) where T : Exception
{ try { action(); } catch (T) { assertions++; return; } throw new Exception(message); }
void Command(TestRig rig, CinemaEditorCommandKind kind, double number = 0, string text = "") => rig.Controller!.Execute(new(kind, number, text));
void Key(TestRig rig, double time, float x, string segment = "A", float fov = 60, float focus = 10)
{
    rig.Session.Commit(time, segment); rig.Camera.transform.position = new(x, 0, 0); rig.Camera.transform.rotation = Quaternion.identity;
    Command(rig, CinemaEditorCommandKind.SetFov, fov); Command(rig, CinemaEditorCommandKind.SetFocusDistance, focus);
    Command(rig, CinemaEditorCommandKind.SetTransition, (int)CinemaTransition.Linear); Command(rig, CinemaEditorCommandKind.AddKey);
}
void Ready(TestRig rig)
{
    var deadline = DateTime.UtcNow.AddSeconds(3);
    while (rig.Editor.LastState?.IdentityReady != true && DateTime.UtcNow < deadline)
    { rig.Controller!.CameraApplied(); Thread.Sleep(1); }
    Check(rig.Editor.LastState?.IdentityReady == true, "bounded background identity completes");
}
void Restored(TestRig rig)
{
    var p = rig.Camera.transform.position; var q = rig.Camera.transform.rotation;
    Check(p.x == rig.InitialPosition.x && p.y == rig.InitialPosition.y && p.z == rig.InitialPosition.z &&
        q.x == rig.InitialRotation.x && q.y == rig.InitialRotation.y && q.z == rig.InitialRotation.z && q.w == rig.InitialRotation.w,
        "original camera transform restored");
    Near(rig.Camera.fieldOfView, rig.InitialFov, "original FOV restored");
    Check(rig.Session.FreeCamera == rig.InitialFree && rig.Session.Paused == rig.InitialPaused && rig.Session.ShowNames == rig.InitialNames,
        "original free/paused/names restored");
    Check(rig.ConsoleVisible && rig.HudVisible, "ordinary console and HUD restored");
    Check(rig.Session.Extension == null, "session attachment released");
}
void Run(string name, Action test)
{
    Keyboard.current = new(); Mouse.current = new(); CinemaEditorUI.Reset(); CinemaEffects.Reset();
    try { test(); passed++; output.Add("PASS " + name); } catch (Exception error) { failed++; output.Add("FAIL " + name + ": " + error); }
}

try
{
    Run("all quality choices reach the renderer without collapsing medium into low", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open();
        foreach (var quality in new[] { CinemaDofQuality.Low, CinemaDofQuality.Medium, CinemaDofQuality.High })
        {
            Command(rig, CinemaEditorCommandKind.SetDofQuality, (int)quality);
            Check((int)CinemaEffects.Latest!.LastSettings!.DofQuality == (int)quality, "quality selection forwarded exactly");
        }
    });

    Run("ordinary playback is untouched until explicit cinema construction", () =>
    {
        using var rig = new TestRig(fixtures);
        Check(CinemaEffects.Created == 0 && CinemaEditorUI.Created == 0 && rig.Session.Extension == null, "no implicit cinema resources");
        Check(!rig.Session.FreeCamera && !rig.Session.Paused && rig.Session.ShowNames && rig.ConsoleVisible && rig.HudVisible, "ordinary defaults retained");
        rig.Open(); Check(rig.Session.FreeCamera && rig.Session.Paused && ReferenceEquals(rig.Session.Extension, rig.Controller), "explicit entry owns camera");
        Check(!rig.ConsoleVisible && !rig.HudVisible && !rig.Session.ShowNames, "cinema defaults hide recording overlays");
        Ready(rig);
    });
    Run("normal close and double disposal restore previous camera and playback state", () =>
    {
        using var rig = new TestRig(fixtures, free: true, paused: true, names: false); rig.Open(); Ready(rig);
        var editor = rig.Editor; var effects = CinemaEffects.Latest!;
        rig.Camera.transform.position = new(99, 88, 77); rig.Camera.transform.rotation = Quaternion.Euler(0, 100, 30);
        Command(rig, CinemaEditorCommandKind.SetFov, 31); rig.Session.SetFreeCamera(false); rig.Session.Paused = false; rig.Session.ShowNames = true;
        rig.Controller!.Dispose(); Restored(rig); int calls = rig.VisibilityCalls;
        rig.Controller.Dispose(); Check(editor.Disposals == 1 && effects.Disposals == 1 && rig.VisibilityCalls == calls, "double disposal has no repeated effects");
        Check(!rig.Controller.ApplyCamera(.1f), "disposed controller releases camera");
        Command(rig, CinemaEditorCommandKind.AddKey); Check(rig.Controller.KeyframeCount == 0, "disposed commands are inert");
    });
    Run("UI construction failure rolls back effects and session state", () =>
    {
        using var rig = new TestRig(fixtures); CinemaEditorUI.ThrowOnConstruction = true;
        Throws<InvalidOperationException>(rig.Open, "injected UI failure escapes constructor");
        Restored(rig); Check(CinemaEffects.Latest?.Disposals == 1, "effects created before failed UI released");
    });
    Run("failure after attachment also rolls back before returning", () =>
    {
        using var rig = new TestRig(fixtures); rig.ThrowOnConsoleHide = true;
        Throws<InvalidOperationException>(rig.Open, "visibility callback construction failure");
        Restored(rig); Check(CinemaEditorUI.Latest?.Disposals == 1 && CinemaEffects.Latest?.Disposals == 1, "partial construction resources released");
    });
    Run("missing recording cannot leave cinema active", () =>
    {
        using var rig = new TestRig(fixtures); File.Delete(rig.Source);
        Throws<FileNotFoundException>(rig.Open, "missing source rejected"); Restored(rig);
        Check(CinemaEffects.Created == 0 && CinemaEditorUI.Created == 0, "source checked before allocating cinema resources");
    });
    Run("first UI refresh failure after attachment restores the entire mode", () =>
    {
        using var rig = new TestRig(fixtures); CinemaEditorUI.ThrowOnFirstRefresh = true;
        Throws<InvalidOperationException>(rig.Open, "injected first refresh failure"); Restored(rig);
        Check(CinemaEditorUI.Latest?.Disposals == 1 && CinemaEffects.Latest?.Disposals == 1, "resources released after first refresh failure");
    });
    Run("independent restores continue through UI, effects, and HUD failures", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig);
        rig.Editor.ThrowOnDispose = true; CinemaEffects.Latest!.ThrowOnDispose = true; rig.ThrowOnHudRestore = true;
        rig.Camera.transform.position = new(45, 0, 0); rig.Camera.fieldOfView = 25;
        rig.Controller!.Dispose();
        Check(rig.Controller.IsDisposed && rig.Session.Extension == null && rig.ConsoleVisible, "later cleanup survives earlier exceptions");
        Near(rig.Camera.fieldOfView, rig.InitialFov, "camera restored after failures");
        Check(rig.Camera.transform.position.x == rig.InitialPosition.x && rig.Session.FreeCamera == rig.InitialFree && rig.Session.Paused == rig.InitialPaused,
            "playback state restored after failures");
        Check(rig.Messages.Count(message => message.Contains("Cinema restore")) == 3, "each failed independent restore reported");
    });
    Run("preview samples committed replay time and freezes during buffering", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); Key(rig, 0, 0, fov: 60, focus: 5); Key(rig, 10, 10, fov: 40, focus: 15);
        Command(rig, CinemaEditorCommandKind.Preview); Check(rig.Controller!.Previewing && !rig.Session.Paused && rig.Session.Buffering, "preview seeks and starts playback");
        rig.Session.Commit(5); Check(rig.Controller.ApplyCamera(.001f), "preview owns camera");
        Near(rig.Camera.transform.position.x, 5, "position from replay time"); Near(rig.Camera.fieldOfView, 50, "FOV from replay time");
        rig.Controller.CameraApplied(); Near(CinemaEffects.Latest!.Focus, 10, "focus from replay time");
        rig.Session.Seek(9); Check(rig.Controller.ApplyCamera(100), "buffering keeps camera owned");
        Near(rig.Camera.transform.position.x, 5, "requested cursor cannot move camera early");
        rig.Session.Commit(9); rig.Controller.ApplyCamera(0); Near(rig.Camera.transform.position.x, 9, "seek applies after commit");
        rig.Session.Paused = true; rig.Controller.ApplyCamera(50); Near(rig.Camera.transform.position.x, 9, "wall delta and pause do not drift");
        rig.Session.Commit(5); rig.Controller.ApplyCamera(.9f); Near(rig.Camera.transform.position.x, 5, "repeated seek has same result");
        rig.Session.Commit(10); rig.Session.Paused = false; rig.Controller.ApplyCamera(.01f); Check(rig.Session.Paused, "multi-key preview stops at last key");
    });
    Run("pending seek is displayed without advancing the preview camera", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); Key(rig, 0, 0); Key(rig, 10, 10);
        Command(rig, CinemaEditorCommandKind.Preview); rig.Session.Commit(5); rig.Controller!.ApplyCamera(.01f);
        Command(rig, CinemaEditorCommandKind.Seek, 9);
        var state = rig.Editor.LastState!;
        Check(state.Buffering && state.Previewing, "explicit seek preserves automatic preview while reading");
        Near(state.Time, 5, "editor retains committed playback time");
        Near(state.RequestedTime, 9, "editor receives requested seek time");
        Near(state.DisplayTime, 9, "clock and timeline can show pending target");
        Check(rig.Controller.ApplyCamera(100), "preview keeps camera ownership while reading");
        Near(rig.Camera.transform.position.x, 5, "display target cannot advance camera before commit");
        rig.Session.Commit(8); rig.Controller.ApplyCamera(.01f); rig.Controller.CameraApplied();
        Check(!state.Buffering, "committed state clears editor buffering");
        Near(state.RequestedTime, 9, "last seek request may remain after commit");
        Near(state.DisplayTime, 8, "display returns to actual committed cursor after reading");
        Near(rig.Camera.transform.position.x, 8, "preview samples committed cursor instead of displayed request");
    });
    Run("camera preview never interpolates across segments", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); Key(rig, 0, 0, "A"); Key(rig, 10, 100, "B");
        Command(rig, CinemaEditorCommandKind.Preview); rig.Session.Commit(5, "A"); rig.Controller!.ApplyCamera(.01f);
        Near(rig.Camera.transform.position.x, 0, "A holds before segment cut");
        rig.Session.Commit(10, "B"); rig.Controller.ApplyCamera(.01f); Near(rig.Camera.transform.position.x, 100, "B begins at its key");
    });
    Run("manual motion takes over without changing existing keys", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); Key(rig, 0, 0); Key(rig, 10, 10);
        Command(rig, CinemaEditorCommandKind.Preview); rig.Session.Commit(5); rig.Controller!.ApplyCamera(.01f);
        Keyboard.current!.wKey.isPressed = true;
        Check(rig.Controller.ApplyCamera(.01f) && rig.Controller.Previewing, "typing movement without looking does not take over");
        rig.Session.CameraLooking = true;
        Check(!rig.Controller.ApplyCamera(.01f) && !rig.Controller.Previewing && rig.Session.FreeCamera, "look plus movement returns host free camera");
        Check(rig.Controller.KeyframeCount == 2, "manual takeover preserves path");
        Keyboard.current.wKey.isPressed = false; Command(rig, CinemaEditorCommandKind.Preview); rig.Session.Commit(5);
        rig.Session.CameraReadsLookDelta = false; Mouse.current!.delta.Value = new(3, 0);
        Check(rig.Controller.ApplyCamera(.01f) && rig.Controller.Previewing, "suppressed first mouse delta cannot take over");
        rig.Session.CameraReadsLookDelta = true;
        Check(!rig.Controller.ApplyCamera(.01f) && !rig.Controller.Previewing, "real mouse movement takes over");
    });
    Run("text editing does not fire cinema playback shortcuts", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); rig.Editor.EditingText = true;
        Keyboard.current!.spaceKey.wasPressedThisFrame = true; Keyboard.current.tabKey.wasPressedThisFrame = true; rig.Controller!.HandleInput();
        Check(rig.Session.Paused && rig.Session.FocusCycles == 0, "text editing owns shortcuts");
        rig.Editor.EditingText = false; rig.Controller.HandleInput(); Check(!rig.Session.Paused && rig.Session.FocusCycles == 1, "shortcuts work after leaving text field");
    });
    Run("jump key waits for committed pose and synchronizes manual camera orientation", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); Key(rig, 0, 0); Key(rig, 10, 10);
        rig.Session.Commit(10); int sync = rig.Session.SyncCalls; Command(rig, CinemaEditorCommandKind.PreviousKey);
        Check(rig.Session.Buffering && rig.Session.Paused && rig.Session.RequestedTime == 0, "jump requests correct key");
        rig.Camera.transform.position = new(55, 0, 0); rig.Controller!.ApplyCamera(.01f); Near(rig.Camera.transform.position.x, 55, "pending jump does not apply early");
        rig.Session.Commit(0); Check(rig.Controller.ApplyCamera(.01f), "committed jump applies pose"); Near(rig.Camera.transform.position.x, 0, "jump position");
        Check(rig.Session.SyncCalls > sync, "manual orientation synchronized");
    });
    Run("explicit seek cancels an unfinished keyframe pose jump", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); Key(rig, 0, 0); Key(rig, 10, 10);
        Command(rig, CinemaEditorCommandKind.PreviousKey);
        Check(rig.Session.Buffering && rig.Session.RequestedTime == 0, "key navigation starts a pending pose jump");
        Command(rig, CinemaEditorCommandKind.Seek, 5);
        rig.Camera.transform.position = new(55, 0, 0);
        Check(!rig.Controller!.ApplyCamera(.01f), "cancelled pose jump leaves manual camera owned by host while reading");
        rig.Session.Commit(5);
        Check(!rig.Controller.ApplyCamera(.01f), "replacement seek does not apply a keyframe pose");
        Near(rig.Camera.transform.position.x, 55, "replacement seek preserves manual position");
        rig.Session.Commit(0);
        Check(!rig.Controller.ApplyCamera(.01f), "returning to old key time cannot consume a cancelled pose jump");
        Near(rig.Camera.transform.position.x, 55, "old pending pose cannot snap the camera later");
    });
    Run("bad, mismatched, and out-of-duration loads preserve the current track", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); Key(rig, 0, 0); Key(rig, 10, 10);
        Command(rig, CinemaEditorCommandKind.Save);
        string fingerprint = CinemaRecordingIdentity.Read(rig.Source);
        Command(rig, CinemaEditorCommandKind.SetProjectName, text: "bad"); string path = CinemaRecordingIdentity.Sidecar(rig.Source, "bad");
        File.WriteAllText(path, "broken JSON"); Command(rig, CinemaEditorCommandKind.Load);
        Check(rig.Controller!.KeyframeCount == 2, "invalid JSON retains keys");
        var other = new CinemaProject("other-recording"); other.Track.Upsert(new(0, "A", new(new(99, 0, 0), CinemaQuaternion.Identity)));
        CinemaProjectStore.SaveAtomic(path, other); Command(rig, CinemaEditorCommandKind.Load); Check(rig.Controller.KeyframeCount == 2, "foreign binding retains keys");
        var beyond = new CinemaProject(fingerprint); beyond.Track.Upsert(new(rig.Session.Duration + 1, "A", new(new(99, 0, 0), CinemaQuaternion.Identity)));
        CinemaProjectStore.SaveAtomic(path, beyond); Command(rig, CinemaEditorCommandKind.Load); Check(rig.Controller.KeyframeCount == 2, "duration check retains keys");
        Command(rig, CinemaEditorCommandKind.Preview); rig.Session.Commit(5); rig.Controller.ApplyCamera(.01f); Near(rig.Camera.transform.position.x, 5, "original path still previews");
        Check(rig.Messages.Count(message => message.StartsWith("Cinema editor command Load")) == 3, "each refused load reported");
    });
    Run("named saves roundtrip separately without modifying original recording", () =>
    {
        using var rig = new TestRig(fixtures); byte[] original = File.ReadAllBytes(rig.Source); rig.Open(); Ready(rig);
        Key(rig, 0, 0, fov: 55); Command(rig, CinemaEditorCommandKind.SetProjectName, text: "开场"); Command(rig, CinemaEditorCommandKind.Save);
        Key(rig, 10, 10, fov: 35); Command(rig, CinemaEditorCommandKind.SetProjectName, text: "结尾"); Command(rig, CinemaEditorCommandKind.Save);
        string fingerprint = CinemaRecordingIdentity.Read(rig.Source);
        var first = CinemaProjectStore.Load(CinemaRecordingIdentity.Sidecar(rig.Source, "开场"), fingerprint);
        var second = CinemaProjectStore.Load(CinemaRecordingIdentity.Sidecar(rig.Source, "结尾"), fingerprint);
        Check(first.Track.Count == 1 && second.Track.Count == 2, "named projects remain separate");
        Check(original.SequenceEqual(File.ReadAllBytes(rig.Source)), "source recording bytes unchanged");
        Command(rig, CinemaEditorCommandKind.SetProjectName, text: "开场"); Command(rig, CinemaEditorCommandKind.Load);
        Check(rig.Controller!.KeyframeCount == 1 && rig.Session.Buffering, "valid load selects saved path and seeks");
        rig.Session.Commit(0); rig.Controller.ApplyCamera(.01f); Near(rig.Camera.fieldOfView, 55, "loaded optics restored");
    });
    Run("overwrite rejects a selected key from another time or segment", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); Key(rig, 0, 0); Key(rig, 10, 10);
        rig.Session.Commit(10); Command(rig, CinemaEditorCommandKind.PreviousKey); rig.Session.Commit(0); rig.Controller!.ApplyCamera(.01f);
        rig.Session.Commit(20, "B"); rig.Camera.transform.position = new(999, 0, 0); Command(rig, CinemaEditorCommandKind.OverwriteKey);
        Check(rig.Controller.KeyframeCount == 2, "failed overwrite preserves count");
        Command(rig, CinemaEditorCommandKind.Preview); rig.Session.Commit(0, "A"); rig.Controller.ApplyCamera(.01f); Near(rig.Camera.transform.position.x, 0, "cross-time overwrite preserves original pose and segment");
    });
    Run("generating a preset replaces its interval and retains outside keys", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); Key(rig, 0, 0); Key(rig, 3, 100); Key(rig, 6, 6); Key(rig, 9, 9);
        rig.Session.Commit(0); rig.Camera.transform.position = new(0, 0, 0); rig.Camera.transform.rotation = Quaternion.identity;
        Command(rig, CinemaEditorCommandKind.SetPreset, (int)CinemaPreset.Slide); Command(rig, CinemaEditorCommandKind.SetPresetAmount, 4);
        Command(rig, CinemaEditorCommandKind.SetPresetDuration, 6); Command(rig, CinemaEditorCommandKind.GeneratePreset);
        Check(rig.Session.Paused && !rig.Controller!.Previewing, "generation ends in manual paused framing");
        Command(rig, CinemaEditorCommandKind.Save); var saved = CinemaProjectStore.Load(CinemaRecordingIdentity.Sidecar(rig.Source, "default"), CinemaRecordingIdentity.Read(rig.Source));
        Check(saved.Track.Count == 18 && saved.Track.Keyframes[^1].Time == 9, "generated interval replaces old keys and retains outside key");
        saved.Track.TrySample(3, "A", out var middle); Near(middle.Position.X, 2, "obsolete middle key cannot interrupt generated path");
        saved.Track.TrySample(9, "A", out var outside); Near(outside.Position.X, 9, "outside pose preserved");
    });
    Run("generated motion selects its real first-key transition", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig);
        rig.Session.Commit(2); rig.Camera.transform.position = new(10, 0, 0);
        Command(rig, CinemaEditorCommandKind.SetPreset, (int)CinemaPreset.Slide);
        Command(rig, CinemaEditorCommandKind.SetPresetDuration, 6);
        Command(rig, CinemaEditorCommandKind.SetTransition, (int)CinemaTransition.Cut);
        Command(rig, CinemaEditorCommandKind.GeneratePreset);
        Check(rig.Editor.LastState!.KeyIndex == 0, "generation selects the generated first key");
        Check(rig.Editor.LastState.Transition == CinemaTransition.Linear, "editor highlights actual generated transition rather than previous choice");
        Command(rig, CinemaEditorCommandKind.Save);
        var saved = CinemaProjectStore.Load(CinemaRecordingIdentity.Sidecar(rig.Source, "default"), CinemaRecordingIdentity.Read(rig.Source));
        Check(saved.Track.Keyframes.All(key => key.Transition == CinemaTransition.Linear), "generated track keeps its linear segments and built-in overall easing");
        Command(rig, CinemaEditorCommandKind.OverwriteKey); Command(rig, CinemaEditorCommandKind.Save);
        var overwritten = CinemaProjectStore.Load(CinemaRecordingIdentity.Sidecar(rig.Source, "default"), CinemaRecordingIdentity.Read(rig.Source));
        Check(overwritten.Track.Keyframes[0].Transition == CinemaTransition.Linear, "overwriting selected start pose retains its true transition");
    });
    Run("preset limit failure preserves the entire prior track transactionally", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); string fingerprint = CinemaRecordingIdentity.Read(rig.Source);
        var full = new CinemaProject(fingerprint);
        for (int i = 0; i < CinemaLimits.MaxKeyframes; i++) full.Track.Upsert(new(100 + i * 2, "A", new(new(i, 0, 0), CinemaQuaternion.Identity)));
        string path = CinemaRecordingIdentity.Sidecar(rig.Source, "default"); CinemaProjectStore.SaveAtomic(path, full); Command(rig, CinemaEditorCommandKind.Load);
        rig.Session.Commit(0); rig.Camera.transform.position = new(0, 0, 0); rig.Camera.transform.rotation = Quaternion.identity;
        Command(rig, CinemaEditorCommandKind.GeneratePreset); Check(rig.Controller!.KeyframeCount == CinemaLimits.MaxKeyframes, "failed preset preserves key count");
        Command(rig, CinemaEditorCommandKind.Save); var unchanged = CinemaProjectStore.Load(path, fingerprint);
        Check(unchanged.Track.Keyframes[0].Time == 100 && unchanged.Track.Keyframes[^1].Time == 4194, "failed preset preserves whole time range");
        Near(unchanged.Track.Keyframes[^1].Pose.Position.X, 2047, "failed preset preserves final pose");
        Check(rig.Messages.Any(message => message.StartsWith("Cinema editor command GeneratePreset")), "limit failure is reported");
    });
    Run("actor focus and clean preview preserve clear fallback and overlay controls", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); rig.Camera.transform.position = new(0, 0, 0);
        rig.Session.FocusPoints[rig.Session.FocusId] = new(0, 0, 12); Command(rig, CinemaEditorCommandKind.SetFocusMode, (int)CinemaDofMode.Actor);
        Near(CinemaEffects.Latest!.Focus, 12, "actor distance drives focus");
        rig.Session.FocusPoints.Clear(); rig.Controller!.CameraApplied(); Check(CinemaEffects.Latest.LastSettings!.DofMode == CinemaEffectFocusMode.Off, "missing actor disables blur");
        Check(rig.Editor.LastState!.Warning.Contains("对焦人物"), "missing focus target explained");
        Command(rig, CinemaEditorCommandKind.ToggleHud); Command(rig, CinemaEditorCommandKind.ToggleNames);
        Check(rig.HudVisible && rig.Session.ShowNames, "HUD and names can be enabled");
        Command(rig, CinemaEditorCommandKind.ToggleCleanPreview); Check(!rig.HudVisible && !rig.Session.ShowNames && rig.Editor.LastState!.CleanPreview, "clean preview suppresses overlays");
        Command(rig, CinemaEditorCommandKind.ToggleCleanPreview); Check(rig.HudVisible && rig.Session.ShowNames, "leaving clean preview respects stored toggles");
        Command(rig, CinemaEditorCommandKind.SetContrast, 25); Near(CinemaEffects.Latest.LastSettings!.Contrast, 25, "normalized grade mapped to URP percent");
        Command(rig, CinemaEditorCommandKind.SetTemperature, -30); Near(CinemaEffects.Latest.LastSettings.Temperature, -30, "temperature mapped to URP percent");
    });
    Run("select move and duplicate preserve every unrelated key and reject occupied or foreign times", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); Key(rig, 0, 1); Key(rig, 10, 2); Key(rig, 20, 3);
        Command(rig, CinemaEditorCommandKind.SelectKey, 1); rig.Session.Commit(10); rig.Controller!.ApplyCamera(.01f);
        Command(rig, CinemaEditorCommandKind.MoveKey, 12);
        Check(rig.Editor.LastState!.Keys.Select(k => k.Time).SequenceEqual(new[] { 0d, 12d, 20d }), "move changes one key time only");
        Check(rig.Editor.LastState.Keys.Select(k => k.Pose.Position.X).SequenceEqual(new[] { 1f, 2f, 3f }), "move preserves all poses");
        Command(rig, CinemaEditorCommandKind.DuplicateKey, 16);
        Check(rig.Editor.LastState.Keys.Select(k => k.Time).SequenceEqual(new[] { 0d, 12d, 16d, 20d }), "duplicate retains original");
        Command(rig, CinemaEditorCommandKind.MoveKey, 20);
        Check(rig.Editor.LastState.Keys[2].Time == 16 && rig.Editor.LastState.Status.Contains("已有关键帧"), "occupied target refused transactionally");
        rig.Session.Segments[30] = "B"; Command(rig, CinemaEditorCommandKind.MoveKey, 32);
        Check(rig.Editor.LastState.Keys[2].Time == 16 && rig.Editor.LastState.Status.Contains("同一关卡"), "cross-segment move refused");
        rig.Session.Segments.Clear(); Command(rig, CinemaEditorCommandKind.MoveKey, 18);
        Check(rig.Editor.LastState.Keys[2].Time == 16 && rig.Editor.LastState.Status.Contains("尚未读取"), "unknown segment requires reading first");
        Command(rig, CinemaEditorCommandKind.Undo);
        Check(rig.Editor.LastState.Keys.Select(k => k.Time).SequenceEqual(new[] { 0d, 12d, 20d }), "failed edits add no undo entries");
        Command(rig, CinemaEditorCommandKind.Undo);
        Check(rig.Editor.LastState.Keys.Select(k => k.Time).SequenceEqual(new[] { 0d, 10d, 20d }), "move can be undone");
        Check(rig.Editor.LastState.KeyIndex == 1, "undo restores key selection");
    });
    Run("exact pose editing validates all input before changing a key or camera", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); Key(rig, 0, 1); Key(rig, 10, 2);
        Command(rig, CinemaEditorCommandKind.SelectKey, 0); rig.Session.Commit(0); rig.Controller!.ApplyCamera(.01f);
        Command(rig, CinemaEditorCommandKind.EditKeyPose, text: "1.25,2.5,-3,12,35,17,47,6.5");
        var edited = rig.Editor.LastState!.Keys[0]; Near(edited.Pose.Position.X, 1.25, "precise position");
        Near(edited.Pose.FieldOfView, 47, "precise fov"); Near(edited.Pose.FocusDistance, 6.5, "precise focus");
        Near(rig.Camera.fieldOfView, 47, "camera receives precise view");
        Check(Math.Abs(edited.Pose.Rotation.Z) > .01, "precise roll stored");
        foreach (string invalid in new[] { "1,2,3", "NaN,2,3,0,0,0,60,10", "Infinity,2,3,0,0,0,60,10", "1000001,2,3,0,0,0,60,10", "1,2,3,0,0,0,141,10", "1,2,3,0,0,0,60,0", "1,2,3,0,999999,0,60,10" })
        {
            Command(rig, CinemaEditorCommandKind.EditKeyPose, text: invalid);
            Check(rig.Editor.LastState.Keys[0].Pose.FieldOfView == 47 && rig.Editor.LastState.Keys[0].Pose.Position.X == 1.25f, "invalid pose leaves original key");
        }
        rig.Session.Commit(0, "B"); Command(rig, CinemaEditorCommandKind.EditKeyPose, text: "4,5,6,0,0,0,60,10");
        Check(rig.Editor.LastState.Keys[0].Pose.Position.X == 1.25f, "different active level cannot rewrite key pose");
        Command(rig, CinemaEditorCommandKind.Undo); Near(rig.Editor.LastState.Keys[0].Pose.Position.X, 1, "one undo reverts last successful precise edit");
        Command(rig, CinemaEditorCommandKind.Redo); Near(rig.Editor.LastState.Keys[0].Pose.Position.X, 1.25, "redo reapplies pose");
    });
    Run("undo compares document contents to the real save point and restores camera settings", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); Key(rig, 0, 1); Command(rig, CinemaEditorCommandKind.Save);
        Check(!rig.Editor.LastState!.Dirty, "saved point clean");
        Command(rig, CinemaEditorCommandKind.SetColorPreset, (int)CinemaColorPreset.Warm);
        Check(rig.Editor.LastState.Dirty, "grade edit dirty"); Command(rig, CinemaEditorCommandKind.Undo);
        Check(!rig.Editor.LastState.Dirty && rig.Editor.LastState.ColorPreset == CinemaColorPreset.Original, "undo to saved contents clean");
        Command(rig, CinemaEditorCommandKind.Redo); Check(rig.Editor.LastState.Dirty, "redo away from saved point dirty");
        Command(rig, CinemaEditorCommandKind.Save); Check(!rig.Editor.LastState.Dirty, "second saved point clean");
        Command(rig, CinemaEditorCommandKind.Undo); Check(rig.Editor.LastState.Dirty, "undo behind newer save point dirty");
        Command(rig, CinemaEditorCommandKind.Redo); Check(!rig.Editor.LastState.Dirty, "redo to newer saved contents clean");
        float before = rig.Camera.fieldOfView; Command(rig, CinemaEditorCommandKind.SetFov, 37); Command(rig, CinemaEditorCommandKind.Undo);
        Near(rig.Camera.fieldOfView, before, "undo restores camera optics"); Check(!rig.Editor.LastState.Dirty, "uncommitted camera optics do not alter saved track");
    });
    Run("continuous sliders merge into one undo and branching drops redo", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig);
        Command(rig, CinemaEditorCommandKind.SetColorStrength, .6); Command(rig, CinemaEditorCommandKind.SetColorStrength, .7); Command(rig, CinemaEditorCommandKind.SetColorStrength, .8);
        Command(rig, CinemaEditorCommandKind.Undo); Near(rig.Editor.LastState!.ColorStrength, .5, "one undo returns to slider drag beginning");
        Command(rig, CinemaEditorCommandKind.Redo); Near(rig.Editor.LastState.ColorStrength, .8, "redo restores drag result");
        Command(rig, CinemaEditorCommandKind.Undo); Command(rig, CinemaEditorCommandKind.ToggleGrid);
        Check(!rig.Editor.LastState!.CanRedo, "new branch clears redo");
    });
    Run("history remains bounded to sixty four successful edits", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig);
        for (int i = 0; i < 66; i++) { rig.Session.Commit(i); rig.Camera.transform.position = new(i, 0, 0); Command(rig, CinemaEditorCommandKind.AddKey); }
        for (int i = 0; i < 70; i++) Command(rig, CinemaEditorCommandKind.Undo);
        Check(rig.Controller!.KeyframeCount == 2 && !rig.Editor.LastState!.CanUndo, "only latest sixty four edits retained");
        for (int i = 0; i < 64; i++) Command(rig, CinemaEditorCommandKind.Redo);
        Check(rig.Controller.KeyframeCount == 66 && !rig.Editor.LastState!.CanRedo, "bounded history remains fully redoable");
    });
    Run("close protects dirty edits and cancellation preserves callback and camera ownership", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); Key(rig, 0, 1); bool called = false;
        rig.Controller!.RequestClose(() => called = true);
        Check(rig.Editor.LastState!.PendingAction == "Close" && !rig.Controller.IsDisposed && !called, "dirty exit waits for explicit choice");
        float fov = rig.Camera.fieldOfView; Command(rig, CinemaEditorCommandKind.SetFov, 20);
        Near(rig.Camera.fieldOfView, fov, "pending dialog blocks editor commands");
        Keyboard.current!.hKey.wasPressedThisFrame = true; Keyboard.current.spaceKey.wasPressedThisFrame = true; rig.Controller.HandleInput();
        Check(!rig.Editor.LastState!.CleanPreview && rig.Session.Paused, "pending dialog blocks shortcuts");
        Command(rig, CinemaEditorCommandKind.CancelPending);
        Check(!rig.Controller.IsDisposed && rig.Controller.KeyframeCount == 1 && rig.Editor.LastState.PendingAction.Length == 0 && !called, "cancel retains editing session");
        rig.Controller.RequestClose(() => called = true); rig.Controller.RequestClose(); Command(rig, CinemaEditorCommandKind.ConfirmSave);
        Check(rig.Controller.IsDisposed && called, "confirmed save exits once and preserves host callback");
        Restored(rig);
    });
    Run("failed save leaves dirty dialog and document open", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); Key(rig, 0, 4); bool called = false;
        string path = CinemaRecordingIdentity.Sidecar(rig.Source, "default"); Directory.CreateDirectory(path);
        rig.Controller!.RequestClose(() => called = true); Command(rig, CinemaEditorCommandKind.ConfirmSave);
        Check(!rig.Controller.IsDisposed && !called && rig.Editor.LastState!.PendingAction == "Close" && rig.Editor.LastState.Dirty, "save failure cannot close the editor");
        Check(rig.Controller.KeyframeCount == 1 && rig.Editor.LastState!.Status.Contains("操作未完成"), "failure keeps work and reports issue");
        Directory.Delete(path); Command(rig, CinemaEditorCommandKind.ConfirmSave);
        Check(rig.Controller.IsDisposed && called && File.Exists(path), "same confirmation can succeed after file issue resolved");
    });
    Run("load save protection saves active edits without overwriting selected destination", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); Key(rig, 0, 1);
        Command(rig, CinemaEditorCommandKind.SetProjectName, text: "first"); Command(rig, CinemaEditorCommandKind.Save);
        Command(rig, CinemaEditorCommandKind.SetProjectName, text: "second"); rig.Camera.transform.position = new(2, 0, 0); Command(rig, CinemaEditorCommandKind.OverwriteKey); Command(rig, CinemaEditorCommandKind.Save);
        Command(rig, CinemaEditorCommandKind.SetProjectName, text: "first"); Command(rig, CinemaEditorCommandKind.Load); rig.Session.Commit(0); rig.Controller!.ApplyCamera(.01f);
        rig.Camera.transform.position = new(3, 0, 0); Command(rig, CinemaEditorCommandKind.OverwriteKey);
        Command(rig, CinemaEditorCommandKind.RefreshProjects); int target = Array.IndexOf(rig.Editor.LastState!.Projects, "second");
        Command(rig, CinemaEditorCommandKind.SelectProject, target); Command(rig, CinemaEditorCommandKind.Load);
        Check(rig.Editor.LastState.PendingAction == "Load" && rig.Controller.KeyframeCount == 1, "dirty load waits");
        Command(rig, CinemaEditorCommandKind.ConfirmSave);
        string fingerprint = CinemaRecordingIdentity.Read(rig.Source);
        var first = CinemaProjectStore.Load(CinemaRecordingIdentity.Sidecar(rig.Source, "first"), fingerprint);
        var second = CinemaProjectStore.Load(CinemaRecordingIdentity.Sidecar(rig.Source, "second"), fingerprint);
        Near(first.Track.Keyframes[0].Pose.Position.X, 3, "pending save writes active original project");
        Near(second.Track.Keyframes[0].Pose.Position.X, 2, "destination project is not overwritten");
        Check(rig.Editor.LastState.ProjectName == "second" && !rig.Editor.LastState.Dirty, "then selected target is loaded cleanly");
    });
    Run("forced disposal preserves a separate recovery and explicit discard does not resurrect it", () =>
    {
        using var rig = new TestRig(fixtures); byte[] original = File.ReadAllBytes(rig.Source); rig.Open(); Ready(rig); Key(rig, 0, 7);
        rig.Controller!.Dispose(); string recovery = CinemaProjectFiles.RecoveryPath(rig.Source, "default");
        Check(File.Exists(recovery) && !File.Exists(CinemaRecordingIdentity.Sidecar(rig.Source, "default")), "forced exit saves recovery only");
        rig.Open(); Ready(rig); Check(rig.Editor.LastState!.RecoveryAvailable, "recovery offered on next entry");
        Command(rig, CinemaEditorCommandKind.RestoreRecovery);
        Check(rig.Controller!.KeyframeCount == 1 && rig.Editor.LastState.Dirty, "recovered project remains unsaved");
        rig.Session.Commit(0); rig.Controller.ApplyCamera(.01f); Near(rig.Camera.transform.position.x, 7, "recovery restores pose");
        Command(rig, CinemaEditorCommandKind.Close); Command(rig, CinemaEditorCommandKind.ConfirmDiscard);
        Check(rig.Controller.IsDisposed && !File.Exists(recovery), "explicit discard removes recovery and suppresses disposal rewrite");
        Check(original.SequenceEqual(File.ReadAllBytes(rig.Source)), "source recording untouched by recovery and discard");
        rig.Open(); Ready(rig); Check(!rig.Editor.LastState!.RecoveryAvailable, "discarded work does not reappear");
    });
    Run("restoring a recovery while dirty reads it before discarding current edits", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig);
        var recovered = new CinemaProject(CinemaRecordingIdentity.Read(rig.Source)); recovered.Track.Upsert(new(0, "A", new(new(9, 0, 0), CinemaQuaternion.Identity)));
        CinemaProjectFiles.SaveRecovery(rig.Source, "default", recovered); Key(rig, 0, 5);
        Command(rig, CinemaEditorCommandKind.RestoreRecovery); Check(rig.Editor.LastState!.PendingAction == "RestoreRecovery", "dirty restore asks first");
        Command(rig, CinemaEditorCommandKind.ConfirmDiscard);
        Check(rig.Editor.LastState.PendingAction.Length == 0 && rig.Editor.LastState.Dirty && rig.Editor.LastState.Keys[0].Pose.Position.X == 9, "discard then restore does not delete desired recovery");
    });
    Run("save then restore caches the recovery before normal save cleans its file", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); Key(rig, 0, 3);
        var recovered = new CinemaProject(CinemaRecordingIdentity.Read(rig.Source)); recovered.Track.Upsert(new(0, "A", new(new(9, 0, 0), CinemaQuaternion.Identity)));
        CinemaProjectFiles.SaveRecovery(rig.Source, "default", recovered);
        Command(rig, CinemaEditorCommandKind.RestoreRecovery); Command(rig, CinemaEditorCommandKind.ConfirmSave);
        Check(rig.Editor.LastState!.PendingAction.Length == 0 && rig.Editor.LastState.Dirty && rig.Editor.LastState.Keys[0].Pose.Position.X == 9, "recovery restored after saving active work");
        var saved = CinemaProjectStore.Load(CinemaRecordingIdentity.Sidecar(rig.Source, "default"), CinemaRecordingIdentity.Read(rig.Source));
        Near(saved.Track.Keyframes[0].Pose.Position.X, 3, "current edits saved before applying recovery");
    });

    Run("automatic recovery is asynchronous separate and removed by successful save", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); Key(rig, 0, 8);
        typeof(CinemaController).GetField("nextRecoveryAt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(rig.Controller, DateTime.MinValue);
        rig.Controller!.CameraApplied();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (rig.Editor.LastState!.AutosaveBusy && DateTime.UtcNow < deadline) { Thread.Sleep(1); rig.Controller.CameraApplied(); }
        string recovery = CinemaProjectFiles.RecoveryPath(rig.Source, "default");
        Check(File.Exists(recovery) && rig.Editor.LastState.AutosaveStatus.Contains("已保存"), "background recovery finishes");
        Check(!File.Exists(CinemaRecordingIdentity.Sidecar(rig.Source, "default")), "autosave never overwrites normal project");
        Command(rig, CinemaEditorCommandKind.Save);
        Check(!File.Exists(recovery) && !rig.Editor.LastState.Dirty && !rig.Editor.LastState.AutosaveBusy, "explicit save drains task and removes stale recovery");
    });
    Run("automatic recovery failure is visible and cannot prevent ordinary playback restoration", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); Key(rig, 0, 8);
        string path = CinemaProjectFiles.RecoveryPath(rig.Source, "default"); Directory.CreateDirectory(path);
        typeof(CinemaController).GetField("nextRecoveryAt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(rig.Controller, DateTime.MinValue);
        rig.Controller!.CameraApplied();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (rig.Editor.LastState!.AutosaveBusy && DateTime.UtcNow < deadline) { Thread.Sleep(1); rig.Controller.CameraApplied(); }
        Check(rig.Editor.LastState.Warning.Contains("自动副本保存失败") && rig.Editor.LastState.Dirty, "asynchronous write failure explained while edits remain");
        rig.Controller.Dispose(); Restored(rig);
        Check(rig.Messages.Any(message => message.StartsWith("Cinema recovery on exit")), "failed forced recovery reported without blocking cleanup");
        Directory.Delete(path);
    });

    Run("project listing copy rename and new project keep saved plans independent", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); Key(rig, 0, 1); Command(rig, CinemaEditorCommandKind.Save);
        Command(rig, CinemaEditorCommandKind.CopyProject, text: "copy");
        Check(rig.Editor.LastState!.ProjectName == "copy" && rig.Editor.LastState.Projects.Contains("default") && rig.Editor.LastState.Projects.Contains("copy"), "copy creates separate named plan");
        Command(rig, CinemaEditorCommandKind.RenameProject, text: "renamed");
        Check(rig.Editor.LastState.Projects.Contains("renamed") && !rig.Editor.LastState.Projects.Contains("copy"), "rename catalog refreshed");
        Command(rig, CinemaEditorCommandKind.SetColorPreset, (int)CinemaColorPreset.Cool); Command(rig, CinemaEditorCommandKind.NewProject);
        Check(rig.Editor.LastState.PendingAction == "NewProject", "new project protects existing edits"); Command(rig, CinemaEditorCommandKind.CancelPending);
        Check(rig.Editor.LastState.Keys.Count == 1 && rig.Editor.LastState.ColorPreset == CinemaColorPreset.Cool, "cancel new retains project settings");
        Command(rig, CinemaEditorCommandKind.NewProject); Command(rig, CinemaEditorCommandKind.ConfirmDiscard);
        Check(rig.Editor.LastState.Keys.Count == 0 && rig.Editor.LastState.ProjectName.StartsWith("新方案") && !rig.Editor.LastState.CanUndo, "new plan starts empty with fresh history");
        Check(File.Exists(CinemaRecordingIdentity.Sidecar(rig.Source, "default")) && File.Exists(CinemaRecordingIdentity.Sidecar(rig.Source, "renamed")), "saved plans retained");
    });

    Run("recovery-only unsaved new project is discoverable after reopening", () =>
    {
        using var rig = new TestRig(fixtures); rig.Open(); Ready(rig); Command(rig, CinemaEditorCommandKind.NewProject);
        string name = rig.Editor.LastState!.ProjectName; Key(rig, 0, 11); rig.Controller!.Dispose();
        Check(!File.Exists(CinemaRecordingIdentity.Sidecar(rig.Source, name)), "new unsaved project has no ordinary file");
        rig.Open(); Ready(rig);
        Check(rig.Editor.LastState!.Projects.Contains(name) && rig.Editor.LastState.RecoveryProjects.Contains(name), "orphan recovery offered in project catalog");
        Command(rig, CinemaEditorCommandKind.SelectProject, Array.IndexOf(rig.Editor.LastState.Projects, name));
        Check(rig.Editor.LastState.RecoveryAvailable && !rig.Editor.LastState.Dirty, "selecting recovery name changes target only");
        Command(rig, CinemaEditorCommandKind.Load); Check(rig.Editor.LastState.Status.Contains("只有自动恢复"), "ordinary load explains recovery route");
        Command(rig, CinemaEditorCommandKind.RestoreRecovery);
        Check(rig.Editor.LastState.Keys.Count == 1 && rig.Editor.LastState.Keys[0].Pose.Position.X == 11 && rig.Editor.LastState.Dirty, "recover-only project restores work");
    });

    Run("bounded file identity follows content length timestamp and survives a move", () =>
    {
        string source = Path.Combine(fixtures, "identity.peakrun"); byte[] data = Enumerable.Range(0, 200000).Select(i => (byte)(i * 13)).ToArray();
        File.WriteAllBytes(source, data); DateTime timestamp = new(2026, 10, 16, 1, 2, 3, DateTimeKind.Utc); File.SetLastWriteTimeUtc(source, timestamp);
        string initial = CinemaRecordingIdentity.Read(source); Check(initial.Length == 64 && initial.All(c => char.IsAsciiHexDigit(c)), "SHA256 identity format");
        string moved = Path.Combine(fixtures, "moved.peakrun"); File.Move(source, moved); Check(CinemaRecordingIdentity.Read(moved) == initial, "unchanged move keeps identity");
        data[0] ^= 1; File.WriteAllBytes(moved, data); File.SetLastWriteTimeUtc(moved, timestamp); Check(CinemaRecordingIdentity.Read(moved) != initial, "changed header rejects old identity");
        data[0] ^= 1; data[^1] ^= 1; File.WriteAllBytes(moved, data); File.SetLastWriteTimeUtc(moved, timestamp); Check(CinemaRecordingIdentity.Read(moved) != initial, "changed tail rejects old identity");
        data[^1] ^= 1; File.WriteAllBytes(moved, data[..^1]); File.SetLastWriteTimeUtc(moved, timestamp); Check(CinemaRecordingIdentity.Read(moved) != initial, "changed length rejects old identity");
        File.WriteAllBytes(moved, data); File.SetLastWriteTimeUtc(moved, timestamp.AddSeconds(1)); Check(CinemaRecordingIdentity.Read(moved) != initial, "changed write timestamp rejects old identity");
        foreach (string name in new[] { "", "..", "../escape", "a/b", "x\ny", new string('a', 41) })
            Throws<ArgumentException>(() => CinemaRecordingIdentity.Sidecar(moved, name), "unsafe plan name rejected");
        Check(CinemaRecordingIdentity.Sidecar(moved, "  镜头一  ") == Path.GetFullPath(moved) + ".镜头一.camera.json", "trimmed named sidecar stays beside recording");
    });
}
finally
{
    // All normal tests await identity; constructor-aborted workers read only tiny fixture files.
    for (int attempt = 0; ; attempt++)
    {
        try { Directory.Delete(fixtures, true); break; }
        catch (IOException) when (attempt < 10) { Thread.Sleep(20); }
    }
    output.Add($"CinemaControllerContract: {passed} passed, {failed} failed, {assertions} assertions (state logic only; no rendering validation).");
    File.WriteAllLines(Path.Combine(checks, "cinema-controller.log"), output);
    foreach (string line in output) Console.WriteLine(line);
}
return failed == 0 ? 0 : 1;

internal sealed class TestRig : IDisposable
{
    internal readonly PlaybackSession Session = new();
    internal Camera Camera => Session.PresentationCamera;
    internal CinemaController? Controller;
    internal CinemaEditorUI Editor => CinemaEditorUI.Latest ?? throw new InvalidOperationException("No editor.");
    internal readonly string Source;
    internal readonly Vector3 InitialPosition;
    internal readonly Quaternion InitialRotation;
    internal readonly float InitialFov;
    internal readonly bool InitialFree, InitialPaused, InitialNames;
    internal bool ConsoleVisible = true, HudVisible = true;
    internal bool ThrowOnConsoleHide, ThrowOnHudRestore;
    internal int VisibilityCalls;
    internal readonly List<string> Messages = new();
    internal TestRig(string fixtureRoot, bool free = false, bool paused = false, bool names = true)
    {
        string directory = Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        Source = Path.Combine(directory, "source.peakrun"); File.WriteAllBytes(Source, Enumerable.Range(0, 1024).Select(i => (byte)(i * 7)).ToArray());
        Camera.transform.SetPositionAndRotation(new(2, 3, 4), Quaternion.Euler(17, 33, 0)); Camera.fieldOfView = 77;
        Session.SetFreeCamera(free); Session.Paused = paused; Session.ShowNames = names;
        InitialPosition = Camera.transform.position; InitialRotation = Camera.transform.rotation; InitialFov = Camera.fieldOfView;
        InitialFree = free; InitialPaused = paused; InitialNames = names;
    }
    internal void Open() => Controller = new CinemaController(Session, Source,
        visible => { VisibilityCalls++; if (!visible && ThrowOnConsoleHide) throw new InvalidOperationException("injected console hide failure"); ConsoleVisible = visible; },
        visible => { VisibilityCalls++; if (visible && ThrowOnHudRestore) throw new InvalidOperationException("injected HUD restore failure"); HudVisible = visible; },
        message => Messages.Add(message));
    public void Dispose() => Controller?.Dispose();
}
