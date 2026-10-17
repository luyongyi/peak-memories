using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LocalCinemaSmoke;

// Test-only companion. No reference to the developing Mod DLL is needed.
[BepInPlugin("local.peakreplay.cinema-smoke", "Protected Cinema Smoke", "0.0.1")]
[BepInDependency("cn.mylus.peakreplaylab", BepInDependency.DependencyFlags.HardDependency)]
[DefaultExecutionOrder(31000)]
public sealed class CinemaSmokeProbe : BaseUnityPlugin
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly Harmony harmony = new("local.peakreplay.cinema-smoke");
    private static CinemaSmokeProbe? active;
    private readonly Stopwatch clock = new();
    private readonly Dictionary<string, string> configs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<object> evidence = new();
    private readonly List<string> captures = new();
    private readonly Dictionary<string, OwnedFile> ownedFiles = new(StringComparer.OrdinalIgnoreCase);
    private BaseUnityPlugin? owner;
    private object? theatre, playback, cinema;
    private Camera? camera;
    private CameraSnapshot? before;
    private PoseSnapshot? deterministicPose, shotStartPose;
    private PoseSnapshot? savedFirstPose;
    private MethodInfo? execute;
    private ConstructorInfo? commandConstructor;
    private Type? commandKind;
    private string output = "", recording = "", recordingHash = "", save = "", saveHash = "", error = "", phase = "Title";
    private string projectName = "", sidecarPath = "", sidecarHash = "", firstSidecarHash = "";
    private int savedKeyCount;
    private double savedFirstTime, savedFirstFocus;
    private bool sidecarOwned, sidecarRemoved, overwriteChanged, loadedPoseRestored;
    private double phaseAt, nextHeartbeat, startTime, sampleTime;
    private bool armed, guardWrites, finished, testPassed, f8Queued, f8Released;
    private int maximumSeconds = 180;
    private int manualSeconds;
    private double nextManualSnapshot;
    private double nextOwnedSnapshot, editedKeyTime;
    private PoseSnapshot? exactEditedPose;
    private bool manualStarted, batchEditingPassed, recoveryPassed, projectManagementPassed;
    private bool markerPointerPassed;
    private int markerUndoBefore;
    private const string RuntimeInputSource = "InputSystem.QueueStateEvent runtime injection; no OS or physical input";
    private bool runtimeInputTouched, runtimeInputPassed;
    private double inputPausedTime, wheelDistanceBefore;
    private float injectedScroll, scrollUnitsPerNotch, flightDistance, flightExpectedDistance;
    private int flightFrames;
    private Vector3 flightLastPosition;

    private sealed class OwnedFile
    {
        public string ProjectName = "", Kind = "", Path = "", Hash = "";
        public bool CreatedByProbe = true, Removed;
    }

    private object[] Keys() => ((System.Collections.IEnumerable)(Read(Read(Read(cinema, "project"), "Track"), "Keyframes")
        ?? throw new InvalidOperationException("No production keyframes."))).Cast<object>().ToArray();
    private object? State => Read(cinema, "state", true);
    private string Pending => Read(State, "PendingAction", true)?.ToString() ?? "";
    private string RecoveryPath => sidecarPath + ".recovery.camera.json";

    private void ClaimFile(string name, string kind)
    {
        Require(name == projectName || name == projectName + "-copy" || name == projectName + "-renamed", "Unexpected generated project name.");
        string path = Path.GetFullPath(recording) + "." + name + ".camera.json" + (kind == "Recovery" ? ".recovery.camera.json" : "");
        Require(kind == "Plan" || kind == "Recovery", "Unexpected owned file kind.");
        Require(!File.Exists(path) && !Directory.Exists(path), "A generated test path already exists; it cannot be claimed.");
        ownedFiles.Add(path, new OwnedFile { ProjectName = name, Kind = kind, Path = path });
        WriteOwnedProof();
    }

    private void WriteOwnedProof() => Write("owned-files-proof.json", new { Recording = recording, ProjectName = projectName, Files = ownedFiles.Values.ToArray() });
    private void CaptureOwnedFile(string path)
    {
        Require(ownedFiles.TryGetValue(path, out var owned), "No prior ownership claim for the exact generated path.");
        Require(!Directory.Exists(path), "Generated file is now a directory.");
        if (File.Exists(path))
        {
            Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "Generated file is now a symbolic link.");
            owned!.Hash = Hash(path); owned.Removed = false;
        }
        else owned!.Removed = true;
        WriteOwnedProof();
    }

    private void ClaimProject()
    {
        projectName = "cinema-smoke-" + Guid.NewGuid().ToString("N").Substring(0, 16);
        sidecarPath = Path.GetFullPath(recording) + "." + projectName + ".camera.json";
        ClaimFile(projectName, "Plan"); ClaimFile(projectName, "Recovery");
        sidecarOwned = true;
        Write("sidecar-proof.json", new { CreatedByProbe = true, ProjectName = projectName, Recording = recording, Path = sidecarPath, Hash = "", Removed = false });
        Command("SetProjectName", 0, projectName);
    }

    private static object? Read(object? target, string name, bool optional = false)
    {
        if (target == null) return null;
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, Flags | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(target);
            var property = type.GetProperty(name, Flags | BindingFlags.DeclaredOnly);
            if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(target);
        }
        if (optional) return null;
        throw new MissingMemberException(target.GetType().FullName, name);
    }
    private static object? Invoke(object target, string name, params object[] args)
    {
        var method = target.GetType().GetMethods(Flags).SingleOrDefault(value => value.Name == name && value.GetParameters().Length == args.Length)
            ?? throw new MissingMethodException(target.GetType().FullName, name);
        return method.Invoke(target, args);
    }
    private static void Set(object target, string name, object value)
    {
        var property = target.GetType().GetProperty(name, Flags) ?? throw new MissingMemberException(target.GetType().FullName, name);
        property.SetValue(target, value);
    }
    private static bool Bool(object? target, string name) => Read(target, name) is bool value && value;
    private static double Number(object? target, string name) => Convert.ToDouble(Read(target, name), CultureInfo.InvariantCulture);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Hash(string path)
    {
        if (!File.Exists(path)) return "ABSENT";
        using var sha = SHA256.Create(); using var file = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
    }
    private static bool AllowWrite() => active?.guardWrites != true;
    private void Block(Type type, string method)
    {
        var target = AccessTools.DeclaredMethod(type, method) ?? throw new MissingMethodException(type.FullName, method);
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(CinemaSmokeProbe), nameof(AllowWrite)));
    }
    private void Awake()
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("--peak-cinema-smoke")) { enabled = false; return; }
        try
        {
            string Arg(string key) => args.SingleOrDefault(value => value.StartsWith(key + "=", StringComparison.Ordinal))?.Substring(key.Length + 1).Trim('"') ?? "";
            output = Arg("--cinema-output"); recording = Arg("--cinema-recording"); save = Arg("--cinema-save-path"); saveHash = Arg("--cinema-save-hash");
            if (int.TryParse(Arg("--cinema-max-seconds"), out int requested)) maximumSeconds = requested;
            if (int.TryParse(Arg("--cinema-manual-seconds"), out int manual)) manualSeconds = manual;
            Require(maximumSeconds >= 30 && maximumSeconds <= 180, "A total runtime bound of 30–180 seconds is required.");
            Require(manualSeconds >= 0 && manualSeconds <= 120, "Manual inspection must be bounded to 0–120 seconds.");
            Require(Path.IsPathFullyQualified(output) && !Directory.Exists(output) && !File.Exists(output), "A new absolute output directory is required.");
            Require(Path.IsPathFullyQualified(recording) && recording.EndsWith(".peakrun", StringComparison.OrdinalIgnoreCase) && File.Exists(recording) && (File.GetAttributes(recording) & FileAttributes.ReparsePoint) == 0, "An existing ordinary sealed full recording is required.");
            var nativeSave = typeof(Peak.Quicksave).GetProperty("SavePath", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as string;
            Require(nativeSave != null && string.Equals(Path.GetFullPath(nativeSave), Path.GetFullPath(save), StringComparison.OrdinalIgnoreCase), "Protect the actual native quicksave path.");
            Require(!Directory.Exists(save) && (saveHash == "ABSENT" || saveHash.Length == 64 && saveHash.All(Uri.IsHexDigit)) && Hash(save) == saveHash, "Protected quicksave proof mismatch.");
            Require(SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null, "A graphical PEAK process is required.");
            Directory.CreateDirectory(output);
            foreach (var path in Directory.GetFiles(Paths.ConfigPath, "*.cfg", SearchOption.AllDirectories)) configs[path] = Hash(path);
            recordingHash = Hash(recording);
            active = this; guardWrites = true;
            Block(typeof(ConfigFile), "Save");
            foreach (var method in new[] { "SaveNow", "DestroySaveData", "FinalizeRunSetup" }) Block(typeof(Peak.Quicksave), method);
            foreach (var method in new[] { "SetSteamStatInternal", "IncrementSteamStat", "ThrowAchievement" }) Block(typeof(AchievementManager), method);
            armed = true; clock.Start(); Enter("Title");
        }
        catch (Exception exception) { error = exception.ToString(); Logger.LogError(exception); Finish(false); }
    }

    private void Enter(string next) { phase = next; phaseAt = clock.Elapsed.TotalSeconds; Write("phase.json", new { Phase = phase, ElapsedSeconds = clock.Elapsed.TotalSeconds, Error = error }); }
    private void Write(string file, object value) { if (output.Length != 0 && Directory.Exists(output)) File.WriteAllText(Path.Combine(output, file), JsonConvert.SerializeObject(value, Formatting.Indented)); }
    private bool Settled(double target) => playback != null && !Bool(playback, "Buffering") && Math.Abs(Number(playback, "Time") - target) < .05 && clock.Elapsed.TotalSeconds - phaseAt > .4;
    private void Seek(double time) { Require(playback != null, "Playback is unavailable."); Set(playback!, "Paused", true); Invoke(playback!, "Seek", time); }
    private void Command(string kind, double number = 0, string text = "")
    {
        Require(cinema != null && execute != null && commandKind != null && commandConstructor != null, "Cinematic command interface is unavailable.");
        var command = commandConstructor!.Invoke(new[] { Enum.Parse(commandKind!, kind), (object)number, text });
        execute!.Invoke(cinema, new[] { command });
        string status = Read(Read(cinema, "state", true), "Status", true)?.ToString() ?? "";
        Require(!status.StartsWith("操作未完成：", StringComparison.Ordinal), "Cinematic command " + kind + " failed: " + status);
    }
    private void Screenshot(string name)
    {
        Require(Screen.width == 1280 && Screen.height == 720, "The smoke must render at 1280×720.");
        string path = Path.Combine(output, name + ".png"); ScreenCapture.CaptureScreenshot(path); captures.Add(path);
    }
    private string EffectsDiagnostic() => Read(cinema, "EffectsDiagnostic", true)?.ToString() ?? Read(Read(cinema, "effects", true), "Diagnostic", true)?.ToString() ?? "unavailable";

    private void QueueRuntimeInput(Key[] keys, bool right = false, float scroll = 0)
    {
        Require(Application.isFocused && Keyboard.current != null && Mouse.current != null, "Focused native input devices are required for runtime injection.");
        runtimeInputTouched = true;
        InputSystem.QueueStateEvent(Keyboard.current!, new KeyboardState(keys));
        InputSystem.QueueStateEvent(Mouse.current!, new MouseState
        {
            // Keep wheel input above the replay drawer so native UI raycasts do
            // not intentionally consume the camera's wheel gesture.
            position = new Vector2(Screen.width * .5f, Screen.height * .9f),
            buttons = (ushort)(right ? 2 : 0), scroll = new Vector2(0, scroll)
        });
    }

    private void ReleaseRuntimeInput()
    {
        if (!runtimeInputTouched && !f8Queued) return;
        try { if (Keyboard.current != null) InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState()); } catch { }
        try
        {
            if (Mouse.current != null) InputSystem.QueueStateEvent(Mouse.current, new MouseState
            { position = new Vector2(Screen.width * .5f, Screen.height * .9f) });
        }
        catch { }
    }

    private void ClickTab(string label, int expectedIndex)
    {
        var button = UnityEngine.Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Single(value => value.gameObject.name == "Cinema action " + label && value.gameObject.activeInHierarchy);
        Require(button.interactable, "Cinema tab is not interactable: " + label);
        button.onClick.Invoke();
        Require(Convert.ToInt32(Read(Read(cinema, "editor"), "selectedTab"), CultureInfo.InvariantCulture) == expectedIndex, "The native UI tab action did not select " + label + ".");
        evidence.Add(new { Kind = "EditorTab", Label = label, Index = expectedIndex, RealButtonOnClickInvoked = true });
    }

    private void InjectMarkerDrag(double targetTime)
    {
        Require(Application.isFocused && EventSystem.current != null, "Focused native EventSystem is required for marker pointer injection.");
        var editor = Read(cinema, "editor");
        var pool = (Read(editor, "markers") as System.Collections.IEnumerable)?.Cast<object>().ToArray()
            ?? throw new InvalidOperationException("No production marker pool.");
        var marker = pool.OfType<Component>().Single(value => value.gameObject.activeInHierarchy && Number(value, "index") == 0);
        var rect = (RectTransform)marker.transform;
        var track = Read(editor, "markerTrack") as RectTransform ?? throw new InvalidOperationException("No marker track.");
        var canvas = marker.GetComponentInParent<Canvas>();
        Require(canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay && track.rect.width > 0, "An overlay canvas with a laid-out marker track is required.");
        Canvas.ForceUpdateCanvases();
        Vector2 start = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center));
        float fraction = (float)(targetTime / Number(playback, "Duration"));
        Vector2 end = RectTransformUtility.WorldToScreenPoint(null, track.TransformPoint(new Vector3(track.rect.xMin + fraction * track.rect.width, track.rect.center.y, 0)));
        var pointer = new PointerEventData(EventSystem.current)
        {
            pointerId = -1, button = PointerEventData.InputButton.Left,
            position = start, pressPosition = start, eligibleForClick = true,
            useDragThreshold = true
        };
        var hits = new List<RaycastResult>(); EventSystem.current!.RaycastAll(pointer, hits);
        var trace = new
        {
            Kind = "MarkerPointerRaycast", Source = "Unity ExecuteEvents pointer injection; no OS or physical mouse input",
            Marker = marker.gameObject.name, BoundIndex = Number(marker, "index"), Start = new[] { start.x, start.y }, End = new[] { end.x, end.y },
            TargetTime = targetTime, Canvas = canvas!.gameObject.name, SortingOrder = canvas!.sortingOrder,
            TrackRect = new[] { track.rect.xMin, track.rect.yMin, track.rect.width, track.rect.height },
            Hits = hits.Take(12).Select(hit => new { Name = hit.gameObject.name, Root = hit.gameObject.transform.root.name,
                PointerDownHandler = ExecuteEvents.GetEventHandler<IPointerDownHandler>(hit.gameObject)?.name,
                hit.sortingOrder, hit.depth, hit.distance }).ToArray()
        };
        evidence.Add(trace); Write("marker-raycast.json", trace);
        Require(hits.Count > 0 && ExecuteEvents.GetEventHandler<IPointerDownHandler>(hits[0].gameObject) == marker.gameObject,
            "The marker center is blocked by the top raycast hit: " + (hits.Count == 0 ? "<no hit>" : hits[0].gameObject.name));
        pointer.pointerCurrentRaycast = pointer.pointerPressRaycast = hits[0];
        pointer.pointerPress = pointer.rawPointerPress = pointer.pointerDrag = marker.gameObject;
        markerUndoBefore = ((System.Collections.ICollection)Read(cinema, "undo")!).Count;
        Require(markerUndoBefore == 0 && !Bool(State, "Dirty"), "Marker pointer regression must begin at a clean saved history baseline.");
        Require(ExecuteEvents.Execute(marker.gameObject, pointer, ExecuteEvents.pointerDownHandler), "Marker has no pointer-down handler.");
        ExecuteEvents.Execute(marker.gameObject, pointer, ExecuteEvents.initializePotentialDrag);
        ExecuteEvents.Execute(marker.gameObject, pointer, ExecuteEvents.beginDragHandler);
        Require(Bool(marker, "Dragging"), "Marker pointer-down/begin-drag did not start the production gesture.");
        pointer.position = end; pointer.delta = end - start;
        ExecuteEvents.Execute(marker.gameObject, pointer, ExecuteEvents.dragHandler);
        ExecuteEvents.Execute(marker.gameObject, pointer, ExecuteEvents.endDragHandler);
        ExecuteEvents.Execute(marker.gameObject, pointer, ExecuteEvents.pointerUpHandler);
        Require(!Bool(marker, "Dragging"), "Production marker drag did not release.");
    }

    private void CaptureSidecarProof()
    {
        if (!sidecarOwned || sidecarPath.Length == 0 || !File.Exists(sidecarPath)) return;
        Require((File.GetAttributes(sidecarPath) & FileAttributes.ReparsePoint) == 0, "The generated sidecar was replaced with a link.");
        sidecarHash = Hash(sidecarPath);
        CaptureOwnedFile(sidecarPath);
        // A successful formal Save removes recovery. During manual inspection a
        // failed Save must not bless a separately changed recovery for deletion.
        if (!manualStarted || !File.Exists(RecoveryPath)) CaptureOwnedFile(RecoveryPath);
        Write("sidecar-proof.json", new { CreatedByProbe = true, ProjectName = projectName, Recording = recording, Path = sidecarPath, Hash = sidecarHash, Removed = false });
    }

    private void SaveSidecar()
    {
        // The first save is admitted only after a fresh exact name was checked.
        // Later writes must still match our own last completed hash.
        Require(sidecarOwned && sidecarPath == Path.GetFullPath(recording) + "." + projectName + ".camera.json", "The test sidecar identity is invalid.");
        Require(sidecarHash.Length == 0 ? !File.Exists(sidecarPath) && !Directory.Exists(sidecarPath) : File.Exists(sidecarPath) && Hash(sidecarPath) == sidecarHash, "The test sidecar changed outside the probe.");
        try { Command("Save"); }
        finally { CaptureSidecarProof(); } // Also cover a failed command after a successful disk write.
        Require(File.Exists(sidecarPath) && sidecarHash.Length == 64, "Production Save did not create a completed sidecar.");
    }

    private bool CleanupSidecar()
    {
        if (!sidecarOwned) return true;
        try
        {
            Require(projectName.StartsWith("cinema-smoke-", StringComparison.Ordinal) && projectName.Length == 29 && projectName.Substring(13).All(Uri.IsHexDigit), "Unexpected test sidecar name.");
            Require(sidecarPath == Path.GetFullPath(recording) + "." + projectName + ".camera.json", "Refusing a sidecar cleanup path outside the exact generated filename.");
            bool cleaned = true;
            foreach (var owned in ownedFiles.Values)
            {
                try
                {
                    Require(owned.CreatedByProbe && (owned.ProjectName == projectName || owned.ProjectName == projectName + "-copy" || owned.ProjectName == projectName + "-renamed"), "Unexpected owned file project.");
                    string exact = Path.GetFullPath(recording) + "." + owned.ProjectName + ".camera.json" + (owned.Kind == "Recovery" ? ".recovery.camera.json" : "");
                    Require((owned.Kind == "Plan" || owned.Kind == "Recovery") && owned.Path == exact, "Unexpected owned file cleanup path.");
                    string? parent = Path.GetDirectoryName(exact);
                    while (!string.IsNullOrEmpty(parent))
                    {
                        Require((File.GetAttributes(parent) & FileAttributes.ReparsePoint) == 0, "An owned-file ancestor is now a link or junction.");
                        parent = Path.GetDirectoryName(parent);
                    }
                    Require(!Directory.Exists(exact), "Generated sidecar is now a directory.");
                    if (File.Exists(exact))
                    {
                        Require(owned.Hash.Length == 64 && (File.GetAttributes(exact) & FileAttributes.ReparsePoint) == 0 && Hash(exact) == owned.Hash, "Generated file hash changed; it was retained for inspection: " + exact);
                        File.Delete(exact);
                    }
                    owned.Removed = !File.Exists(exact) && !Directory.Exists(exact);
                    cleaned &= owned.Removed;
                }
                catch (Exception exception) { cleaned = false; error += "\nOwned file cleanup: " + exception.Message; }
            }
            WriteOwnedProof();
            sidecarRemoved = !Directory.Exists(sidecarPath) && !File.Exists(sidecarPath);
            Write("sidecar-proof.json", new { CreatedByProbe = true, ProjectName = projectName, Recording = recording, Path = sidecarPath, Hash = sidecarHash, Removed = sidecarRemoved });
            return cleaned;
        }
        catch (Exception exception) { error += "\nSidecar cleanup: " + exception; return false; }
    }

    private void LateUpdate()
    {
        if (!armed || finished) return;
        try
        {
            double now = clock.Elapsed.TotalSeconds;
            // Record only scripted writes to freshly claimed recovery paths. Once
            // manual inspection starts, never bless a changed file for deletion.
            if (!manualStarted && sidecarOwned && now >= nextOwnedSnapshot)
            {
                nextOwnedSnapshot = now + .25;
                if (!Bool(State, "AutosaveBusy") && File.Exists(RecoveryPath)) CaptureOwnedFile(RecoveryPath);
            }
            if (now >= maximumSeconds) { error += "\nTotal native smoke timeout."; TryClose(); Finish(false); return; }
            if (now >= nextHeartbeat) { nextHeartbeat = now + 2; Write("heartbeat.json", new { Phase = phase, ElapsedSeconds = now }); }
            if (!owner)
            {
                if (!Chainloader.PluginInfos.TryGetValue("cn.mylus.peakreplaylab", out var info)) return;
                owner = info.Instance; theatre = Read(owner, "theatre");
                Require(theatre != null, "Production replay theatre is not initialized.");
            }
            switch (phase)
            {
                case "Title":
                    if (SceneManager.GetActiveScene().name != "Title" || LoadingScreenHandler.loading || now - phaseAt < 1) break;
                    // This is the same production Open used by the normal library.
                    // Public ReplayAutomation.OpenReplay rejects external recordings;
                    // the probe never changes that guard or duplicates private data.
                    Invoke(owner!, "CloseLibrary"); Invoke(theatre!, "Open", recording); Enter("Opening"); break;
                case "Opening":
                    playback = Read(theatre, "Playback");
                    if (playback == null || Bool(playback, "Buffering") || Read(owner, "replayConsole", true) == null || Read(owner, "uiPlayback", true) == null) break;
                    camera = Read(playback, "PresentationCamera", true) as Camera ?? Read(playback, "camera") as Camera;
                    Require(camera, "Production presentation camera is unavailable.");
                    startTime = Math.Min(5, Math.Max(0, Number(playback, "Duration") - 8));
                    sampleTime = startTime + 3;
                    Require(Number(playback, "Duration") >= sampleTime + 1, "Recording is too short for a six-second cinematic shot.");
                    Seek(startTime); Enter("InitialSeek"); break;
                case "InitialSeek":
                    if (!Settled(startTime)) break;
                    before = CameraSnapshot.Take(camera!); Screenshot("00-third-person"); Enter("OpenCinema"); break;
                case "OpenCinema":
                    if (now - phaseAt < .5) break;
                    Invoke(owner!, "ToggleCinema"); cinema = Read(owner, "cinema");
                    Require(cinema != null, "Cinema mode failed to open.");
                    execute = cinema!.GetType().GetMethods(Flags).SingleOrDefault(method => (method.Name == "Execute" || method.Name.EndsWith(".Execute", StringComparison.Ordinal)) && method.GetParameters().Length == 1);
                    Require(execute != null, "CinemaController has no Execute command method.");
                    var commandType = execute!.GetParameters()[0].ParameterType;
                    commandKind = commandType.GetProperty("Kind", Flags)!.PropertyType;
                    commandConstructor = commandType.GetConstructor(Flags, null, new[] { commandKind, typeof(double), typeof(string) }, null);
                    Require(commandConstructor != null, "Command constructor shape differs from Kind/Number/Text.");
                    Enter("EditorReady"); break;
                case "EditorReady":
                    if (now - phaseAt < 2) break;
                    var editorState = Read(cinema, "state");
                    if (!Bool(editorState, "IdentityReady")) break;
                    // Claim the unique normal and recovery paths before the first
                    // edit; the ten-second autosave must never touch "default".
                    ClaimProject();
                    // Target-name selection is not an active-document switch.
                    // Save the initial key now so autosave is bound to this name.
                    SaveSidecar();
                    Command("SetFov", 52); Command("SetFocusMode", 1); Command("SetFocusDistance", 5);
                    Command("SetBlur", .3); Command("SetDofQuality", 2);
                    Command("SetColorPreset", 1); Command("SetColorStrength", .7);
                    Command("AddKey"); Command("SetPreset", 1); Command("SetPresetAmount", 2); Command("SetPresetDuration", 6); Command("GeneratePreset");
                    Require(Number(cinema, "KeyframeCount") >= 2, "AddKey/GeneratePreset did not create a cinematic track.");
                    Enter("EffectsReady"); break;
                case "EffectsReady":
                    if (now - phaseAt < 1) break;
                    string diagnostic = EffectsDiagnostic();
                    evidence.Add(new { Kind = "NativeEffects", Diagnostic = diagnostic, Camera = CameraSnapshot.Take(camera!), Width = Screen.width, Height = Screen.height });
                    Require(diagnostic.Contains("color=True") && (diagnostic.Contains("dof=bokeh") || diagnostic.Contains("dof=gaussian")), "Native grading/DOF not active: " + diagnostic);
                    Require(Math.Abs(camera!.fieldOfView - 52) < .05, "SetFov did not reach the native camera.");
                    shotStartPose = PoseSnapshot.Take(camera);
                    Screenshot("01-editor-warm-dof"); Enter("TabCameraSelect"); break;
                case "TabCameraSelect":
                    if (now - phaseAt < .5) break;
                    ClickTab("镜头", 1); Enter("TabCameraCapture"); break;
                case "TabCameraCapture":
                    if (now - phaseAt < .4) break;
                    Screenshot("05-tab-camera"); Enter("TabDepthSelect"); break;
                case "TabDepthSelect":
                    if (now - phaseAt < .4) break;
                    ClickTab("景深", 2); Enter("TabDepthCapture"); break;
                case "TabDepthCapture":
                    if (now - phaseAt < .4) break;
                    Screenshot("06-tab-depth"); Enter("TabColorSelect"); break;
                case "TabColorSelect":
                    if (now - phaseAt < .4) break;
                    ClickTab("调色", 3); Enter("TabColorCapture"); break;
                case "TabColorCapture":
                    if (now - phaseAt < .4) break;
                    Screenshot("07-tab-color"); Enter("TabKeysSelect"); break;
                case "TabKeysSelect":
                    if (now - phaseAt < .3) break;
                    ClickTab("关键帧", 4); Enter("TabKeysCapture"); break;
                case "TabKeysCapture":
                    if (now - phaseAt < .4) break;
                    Screenshot("10-tab-keyframes"); Enter("TabProjectsSelect"); break;
                case "TabProjectsSelect":
                    if (now - phaseAt < .3) break;
                    ClickTab("方案", 5); Enter("TabProjectsCapture"); break;
                case "TabProjectsCapture":
                    if (now - phaseAt < .4) break;
                    Screenshot("11-tab-projects"); Enter("TabMotionReturn"); break;
                case "TabMotionReturn":
                    if (now - phaseAt < .4) break;
                    ClickTab("运镜", 0); Enter("PreviewStart"); break;
                case "PreviewStart":
                    if (now - phaseAt < .5) break;
                    Command("Preview"); Require(Bool(cinema, "Previewing"), "The Preview command did not start cinematic playback."); Enter("Preview"); break;
                case "Preview":
                    if (now - phaseAt < .8) break;
                    Seek(sampleTime); Enter("SampleA"); break;
                case "SampleA":
                    if (!Settled(sampleTime)) break;
                    deterministicPose = PoseSnapshot.Take(camera!); Screenshot("02-preview-sample");
                    Require(shotStartPose != null && !shotStartPose.Matches(deterministicPose), "The native camera did not move along the generated Slide track.");
                    evidence.Add(new { Kind = "PoseA", Time = Number(playback, "Time"), Pose = deterministicPose, Diagnostic = EffectsDiagnostic() });
                    Seek(startTime + 1); Enter("SeekAway"); break;
                case "SeekAway":
                    if (!Settled(startTime + 1)) break;
                    Seek(sampleTime); Enter("SampleB"); break;
                case "SampleB":
                    if (!Settled(sampleTime)) break;
                    var repeated = PoseSnapshot.Take(camera!);
                    Require(deterministicPose != null && deterministicPose.Matches(repeated), "Paused seek did not produce a deterministic camera pose/FOV.");
                    evidence.Add(new { Kind = "PoseB", Time = Number(playback, "Time"), Pose = repeated, Matches = true });
                    Command("Stop"); Enter("StopCapture"); break;
                case "StopCapture":
                    if (now - phaseAt < .4) break;
                    Screenshot("03-editor-after-seek"); Enter("SidecarFirstSave"); break;
                case "SidecarFirstSave":
                    if (now - phaseAt < .4) break;
                    savedKeyCount = (int)Number(cinema, "KeyframeCount");
                    var keys = Read(Read(Read(cinema, "project"), "Track"), "Keyframes") as System.Collections.IEnumerable;
                    Require(keys != null, "Cannot read the production keyframe track for sidecar verification.");
                    var firstKey = keys!.Cast<object>().First();
                    savedFirstPose = PoseSnapshot.FromCorePose(Read(firstKey, "Pose")!);
                    savedFirstTime = Number(firstKey, "Time"); savedFirstFocus = Number(Read(firstKey, "Pose"), "FocusDistance");
                    SaveSidecar(); firstSidecarHash = sidecarHash;
                    evidence.Add(new { Kind = "SidecarFirstSave", Path = sidecarPath, Hash = sidecarHash, KeyframeCount = savedKeyCount, FirstPose = savedFirstPose });
                    Enter("SidecarOverwrite"); break;
                case "SidecarOverwrite":
                    if (now - phaseAt < .3) break;
                    Command("SetColorStrength", .65); SaveSidecar();
                    overwriteChanged = sidecarHash != firstSidecarHash;
                    Require(overwriteChanged && Number(cinema, "KeyframeCount") == savedKeyCount, "Production overwrite did not update the sidecar while retaining camera keys.");
                    evidence.Add(new { Kind = "SidecarOverwrite", Hash = sidecarHash, PayloadChanged = overwriteChanged, KeyframeCount = savedKeyCount });
                    Command("DeleteKey"); Command("SetFov", 91); Command("SetColorPreset", 3);
                    Require(Number(cinema, "KeyframeCount") == savedKeyCount - 1, "The deliberate in-memory edit did not remove one key.");
                    Command("Load");
                    Require(Pending == "Load" && Number(cinema, "KeyframeCount") == savedKeyCount - 1, "Dirty Load did not preserve edits behind its confirmation.");
                    evidence.Add(new { Kind = "DirtyLoadProtection", PendingAction = Pending, EditsPreserved = true });
                    Command("ConfirmDiscard");
                    Require(Number(cinema, "KeyframeCount") == savedKeyCount, "Production Load did not restore saved camera keys.");
                    Enter("SidecarLoadSettled"); break;
                case "SidecarLoadSettled":
                    if (!Settled(savedFirstTime)) break;
                    var loadedPose = PoseSnapshot.Take(camera!);
                    var loadedState = Read(cinema, "state");
                    loadedPoseRestored = savedFirstPose != null && savedFirstPose.Matches(loadedPose);
                    Require(loadedPoseRestored && Math.Abs(Number(loadedState, "FocusDistance") - savedFirstFocus) < .001 && Math.Abs(Number(loadedState, "ColorStrength") - .65) < .001 && Number(loadedState, "ColorPreset") == 1, "Load did not restore the saved key pose/focus/Warm settings.");
                    Require(Hash(sidecarPath) == sidecarHash, "Loading a sidecar unexpectedly rewrote it.");
                    evidence.Add(new { Kind = "SidecarLoad", KeyframeCount = Number(cinema, "KeyframeCount"), Pose = loadedPose, PoseRestored = loadedPoseRestored, FocusDistance = Number(loadedState, "FocusDistance"), WarmStrength = Number(loadedState, "ColorStrength") });
                    Screenshot("08-loaded-sidecar"); Command("SelectKey", 0); ClickTab("关键帧", 4); Enter("MarkerPointerReady"); break;
                case "MarkerPointerReady":
                    if (!Settled(savedFirstTime)) break;
                    editedKeyTime = savedFirstTime + .1;
                    InjectMarkerDrag(editedKeyTime); Enter("MarkerPointerCommitted"); break;
                case "MarkerPointerCommitted":
                    if (now - phaseAt < .3) break;
                    double pointerMovedTime = Number(Keys()[0], "Time");
                    int pointerUndoCount = ((System.Collections.ICollection)Read(cinema, "undo")!).Count;
                    Require(Math.Abs(pointerMovedTime - editedKeyTime) < .0001 && pointerUndoCount == markerUndoBefore + 1 && Bool(State, "Dirty"), "Marker pointer gesture did not commit exactly one MoveKey edit.");
                    Command("Undo");
                    Require(Math.Abs(Number(Keys()[0], "Time") - savedFirstTime) < .0001 && !Bool(State, "Dirty") &&
                        ((System.Collections.ICollection)Read(cinema, "undo")!).Count == markerUndoBefore,
                        "One Undo did not return the marker gesture to the saved clean document.");
                    markerPointerPassed = true;
                    evidence.Add(new { Kind = "MarkerPointerDrag", Source = "Unity ExecuteEvents pointer injection; no OS or physical mouse input",
                        ExpectedTime = editedKeyTime, CommittedTime = pointerMovedTime, UndoStepsAdded = pointerUndoCount - markerUndoBefore, UndoRestoredClean = true });
                    Command("SelectKey", 0); Enter("EditKeySelected"); break;
                case "EditKeySelected":
                    if (!Settled(savedFirstTime)) break;
                    Require(!Bool(State, "Dirty"), "Loaded plan unexpectedly starts dirty.");
                    editedKeyTime = savedFirstTime + .1;
                    Command("MoveKey", editedKeyTime);
                    Require(Keys().Any(key => Math.Abs(Number(key, "Time") - editedKeyTime) < .0001), "MoveKey did not update the track time.");
                    Command("Undo"); Require(!Bool(State, "Dirty") && Math.Abs(Number(Keys()[0], "Time") - savedFirstTime) < .0001, "Undo did not return to the saved time and clean state.");
                    Command("Redo"); Require(Keys().Any(key => Math.Abs(Number(key, "Time") - editedKeyTime) < .0001), "Redo did not restore moved time.");
                    Command("Undo"); Require(!Bool(State, "Dirty"), "Undo to the saved document must clear Dirty.");
                    // Segment guards use loaded replay evidence. Decode the
                    // target before copying; keep the first key selected.
                    Seek(editedKeyTime); Enter("DuplicateTargetSettled"); break;
                case "DuplicateTargetSettled":
                    if (!Settled(editedKeyTime)) break;
                    Command("DuplicateKey", editedKeyTime);
                    Require(Number(cinema, "KeyframeCount") == savedKeyCount + 1, "DuplicateKey did not add one key.");
                    Command("Undo"); Require(Number(cinema, "KeyframeCount") == savedKeyCount && !Bool(State, "Dirty"), "Undo duplicate did not restore saved track.");
                    Seek(savedFirstTime); Enter("PreciseEditReady"); break;
                case "PreciseEditReady":
                    if (!Settled(savedFirstTime)) break;
                    var p = savedFirstPose!.Position; var q = Quaternion.Euler(10, 45, 5);
                    exactEditedPose = new PoseSnapshot { Position = new[] { p[0] + .25f, p[1] + .25f, p[2] + .25f }, Rotation = new[] { q.x, q.y, q.z, q.w }, Fov = 48 };
                    string editText = string.Join(",", exactEditedPose.Position.Select(value => value.ToString("R", CultureInfo.InvariantCulture)).Concat(new[] { "10", "45", "5", "48", "4" }));
                    Command("EditKeyPose", 0, editText); Enter("ExactEditSettled"); break;
                case "ExactEditSettled":
                    if (!Settled(savedFirstTime)) break;
                    var exactNative = PoseSnapshot.Take(camera!);
                    Require(exactEditedPose != null && exactEditedPose.Matches(exactNative) && Math.Abs(Number(State, "FocusDistance") - 4) < .001, "Precise pose/FOV/focus did not reach the native camera.");
                    evidence.Add(new { Kind = "NativeKeyEditing", MoveUndoRedoPassed = true, DuplicateUndoPassed = true, ExactPose = exactNative, FocusDistance = Number(State, "FocusDistance") });
                    ClickTab("关键帧", 4); Screenshot("12-exact-key-pose");
                    Enter("ExactEditCaptureDone"); break;
                case "ExactEditCaptureDone":
                    if (now - phaseAt < .3) break;
                    Command("Undo"); Require(!Bool(State, "Dirty"), "Undo precise pose did not return to the saved document.");
                    SaveSidecar();
                    Command("SetExposure", .4); Command("Close");
                    Require(Pending == "Close" && !Bool(cinema, "IsDisposed") && ReferenceEquals(Read(owner, "cinema"), cinema), "Dirty Close disposed the editor without confirmation.");
                    Screenshot("13-unsaved-close-confirmation");
                    Enter("CloseConfirmationCaptured"); break;
                case "CloseConfirmationCaptured":
                    if (now - phaseAt < .3) break;
                    Command("CancelPending"); Require(Pending.Length == 0 && !Bool(cinema, "IsDisposed"), "CancelPending did not retain the editor.");
                    Command("Undo"); Require(!Bool(State, "Dirty"), "Undo after cancelling Close did not return to the saved document.");
                    // Exercise the Save branch on a pending Load so this same
                    // controller stays alive for the original F8 restore check.
                    Command("SetExposure", .2); Command("Load");
                    Require(Pending == "Load", "Dirty Load did not offer Save and continue.");
                    try { Command("ConfirmSave"); } finally { CaptureSidecarProof(); }
                    Require(Pending.Length == 0 && !Bool(State, "Dirty") && Math.Abs(Number(State, "Exposure") - .2) < .001, "ConfirmSave did not save then continue loading the document.");
                    batchEditingPassed = true;
                    evidence.Add(new { Kind = "DirtyCloseProtection", CancelRetainsEditor = true, UndoReturnsToSavedClean = true, SaveAndContinueLoadPassed = true });
                    Command("SetColorStrength", .42); Enter("RecoveryWait"); break;
                case "RecoveryWait":
                    Require(now - phaseAt <= 15, "Automatic recovery was not written within fifteen seconds.");
                    if (!File.Exists(RecoveryPath) || Bool(State, "AutosaveBusy") || now - phaseAt < .4) break;
                    var recoveryJson = JObject.Parse(File.ReadAllText(RecoveryPath));
                    if (Math.Abs((double)(recoveryJson["settings"]?["colorStrength"] ?? -1) - .42) > .001) break;
                    CaptureOwnedFile(RecoveryPath);
                    Require(Hash(sidecarPath) == sidecarHash, "Automatic recovery overwrote the normal saved plan.");
                    Command("SetColorStrength", .18); Command("RestoreRecovery");
                    Require(Pending == "RestoreRecovery", "Dirty recovery restore did not request confirmation.");
                    Command("ConfirmDiscard");
                    Require(Math.Abs(Number(State, "ColorStrength") - .42) < .001 && Bool(State, "Dirty"), "Confirmed recovery did not restore the captured unsaved settings.");
                    evidence.Add(new { Kind = "AutomaticRecovery", Path = RecoveryPath, RestoredStrength = Number(State, "ColorStrength"), NormalPlanUnchanged = Hash(sidecarPath) == sidecarHash, RestoredDocumentDirty = Bool(State, "Dirty") });
                    Command("Load"); Require(Pending == "Load", "Loading after recovery did not protect restored edits."); Command("ConfirmDiscard");
                    Enter("RecoveryLoaded"); break;
                case "RecoveryLoaded":
                    if (!Settled(savedFirstTime)) break;
                    Require(!Bool(State, "Dirty") && Math.Abs(Number(State, "ColorStrength") - .65) < .001, "Load after recovery did not return to the formal saved plan.");
                    SaveSidecar(); Require(!File.Exists(RecoveryPath), "Formal Save did not remove the recovery copy.");
                    recoveryPassed = true;
                    ClaimFile(projectName + "-copy", "Plan"); ClaimFile(projectName + "-renamed", "Plan");
                    Command("CopyProject", 0, projectName + "-copy");
                    string copyPath = sidecarPath.Substring(0, sidecarPath.Length - ".camera.json".Length) + "-copy.camera.json";
                    CaptureOwnedFile(copyPath); string copyHash = Hash(copyPath);
                    Require(copyHash.Length == 64 && Hash(sidecarPath) == sidecarHash, "CopyProject did not preserve its source plan.");
                    Command("Load");
                    Require(Number(cinema, "KeyframeCount") == savedKeyCount && Math.Abs(Number(State, "ColorStrength") - .65) < .001, "The copied plan cannot restore its saved content.");
                    Command("RenameProject", 0, projectName + "-renamed");
                    string renamedPath = sidecarPath.Substring(0, sidecarPath.Length - ".camera.json".Length) + "-renamed.camera.json";
                    CaptureOwnedFile(copyPath); CaptureOwnedFile(renamedPath);
                    Command("RefreshProjects"); var catalog = (Read(State, "Projects") as string[]) ?? Array.Empty<string>();
                    Require(!File.Exists(copyPath) && Hash(renamedPath) == copyHash && catalog.Contains(projectName) && catalog.Contains(projectName + "-renamed") && !catalog.Any(value => value.EndsWith(".recovery", StringComparison.OrdinalIgnoreCase)), "Rename/catalog result is incorrect.");
                    evidence.Add(new { Kind = "NativeProjectManagement", CopyAndRenamePassed = true, Catalog = catalog }); projectManagementPassed = true;
                    Command("SetProjectName", 0, projectName); Command("Load");
                    if (Pending == "Load") Command("ConfirmDiscard");
                    Enter("BeforeF8Settled"); break;
                case "BeforeF8Settled":
                    if (!Settled(savedFirstTime)) break;
                    Require(!Bool(State, "Dirty") && Pending.Length == 0, "F8 restoration must start from a clean saved plan.");
                    ClickTab("运镜", 0); Enter("F8Press"); break;
                case "F8Press":
                    if (now - phaseAt < 1) break;
                    Require(Application.isFocused, "PEAK lost foreground focus before the native F8 shortcut check.");
                    Require(Keyboard.current != null, "No native keyboard to test F8.");
                    InputSystem.QueueStateEvent(Keyboard.current!, new KeyboardState(Key.F8)); f8Queued = true; Enter("F8Release"); break;
                case "F8Release":
                    InputSystem.QueueStateEvent(Keyboard.current!, new KeyboardState()); f8Released = true; Enter("Restored"); break;
                case "Restored":
                    if (Read(owner, "cinema", true) != null || now - phaseAt < .5) break;
                    var restored = CameraSnapshot.Take(camera!);
                    Require(before != null && before.Matches(restored), "F8 did not restore native URP flags/mask/trigger/update mode/FOV.");
                    Require(!UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None).Any(volume => volume.gameObject.name.StartsWith("Cinema ", StringComparison.Ordinal)), "Cinematic runtime volumes survived F8.");
                    evidence.Add(new { Kind = "F8Restore", Passed = true, Before = before, After = restored });
                    Screenshot("04-restored-third-person");
                    Set(playback!, "Paused", true); inputPausedTime = Number(playback, "Time");
                    Require(!Bool(playback, "FreeCamera"), "Runtime wheel verification must begin in third person.");
                    QueueRuntimeInput(Array.Empty<Key>()); Enter("InputWheelPrepare"); break;
                case "InputWheelPrepare":
                    if (now - phaseAt < .1) break;
                    wheelDistanceBefore = Number(playback, "distance");
                    scrollUnitsPerNotch = InputSystem.settings.scrollDeltaBehavior == InputSettings.ScrollDeltaBehavior.KeepPlatformSpecificInputRange &&
                        Application.platform == RuntimePlatform.WindowsPlayer ? 120 : 1;
                    // QueueStateEvent receives device-state units. Uniform mode
                    // has already normalized a Windows hardware notch to 1.
                    injectedScroll = scrollUnitsPerNotch * (wheelDistanceBefore > 3.5 ? 1 : -1);
                    QueueRuntimeInput(Array.Empty<Key>(), scroll: injectedScroll); Enter("InputWheelObserve"); break;
                case "InputWheelObserve":
                    float observedScroll = Mouse.current!.scroll.ReadValue().y;
                    double wheelDistanceAfter = Number(playback, "distance");
                    Require(Math.Abs(observedScroll - injectedScroll) < .001, "Runtime wheel state was not delivered in the expected device units.");
                    Require(Math.Abs(Math.Abs(wheelDistanceAfter - wheelDistanceBefore) - 2.5) < .02, "One runtime-injected wheel notch did not change third-person distance by 2.5 m.");
                    evidence.Add(new { Kind = "RuntimeInputWheel", Source = RuntimeInputSource,
                        ScrollBehavior = InputSystem.settings.scrollDeltaBehavior.ToString(), WindowsHardwareUnitsPerNotch = 120,
                        InjectedDeviceScroll = injectedScroll, ObservedDeviceScroll = observedScroll, DeviceUnitsPerNotch = scrollUnitsPerNotch,
                        DistanceBefore = wheelDistanceBefore, DistanceAfter = wheelDistanceAfter, DistanceDelta = wheelDistanceAfter - wheelDistanceBefore });
                    ReleaseRuntimeInput(); Enter("InputCameraTogglePrepare"); break;
                case "InputCameraTogglePrepare":
                    if (now - phaseAt < .1) break;
                    QueueRuntimeInput(new[] { Key.C }); Enter("InputCameraToggleObserve"); break;
                case "InputCameraToggleObserve":
                    Require(Bool(playback, "FreeCamera"), "Runtime-injected C did not enter the production free camera.");
                    evidence.Add(new { Kind = "RuntimeInputCameraToggle", Source = RuntimeInputSource, FreeCamera = true });
                    ReleaseRuntimeInput(); Enter("InputFlightPrepare"); break;
                case "InputFlightPrepare":
                    if (now - phaseAt < .1) break;
                    Require(Bool(playback, "Paused") && Math.Abs(Number(playback, "Time") - inputPausedTime) < .0001, "Playback did not remain paused before runtime flight input.");
                    flightLastPosition = camera!.transform.position;
                    flightDistance = flightExpectedDistance = 0; flightFrames = 0;
                    QueueRuntimeInput(new[] { Key.W }, right: true); Enter("InputFlightObserve"); break;
                case "InputFlightObserve":
                    Require(Application.isFocused && Bool(playback, "CameraLooking") && Bool(playback, "FreeCamera"), "Runtime RMB did not capture the production free camera.");
                    Require(Keyboard.current!.wKey.isPressed && Mouse.current!.rightButton.isPressed, "Runtime flight controls were not held in native input state.");
                    Require(Bool(playback, "Paused") && Math.Abs(Number(playback, "Time") - inputPausedTime) < .0001, "Runtime flight advanced the paused replay clock.");
                    Vector3 flightPosition = camera!.transform.position;
                    flightDistance += Vector3.Distance(flightLastPosition, flightPosition); flightLastPosition = flightPosition;
                    flightExpectedDistance += (float)Number(playback, "FreeCameraSpeed") * Mathf.Min(Time.unscaledDeltaTime, .1f); flightFrames++;
                    if (flightFrames < 6 || now - phaseAt < .25)
                    { QueueRuntimeInput(new[] { Key.W }, right: true); break; }
                    Require(flightExpectedDistance > .02f && Math.Abs(flightDistance - flightExpectedDistance) <= Mathf.Max(.002f, flightExpectedDistance * .03f), "Paused runtime RMB + W did not produce the expected production free-camera movement.");
                    evidence.Add(new { Kind = "RuntimeInputPausedFlight", Source = RuntimeInputSource, Keys = "RMB + W",
                        Frames = flightFrames, Distance = flightDistance, ExpectedDistanceFromProductionSpeedAndFrameDelta = flightExpectedDistance,
                        PausedTimeBefore = inputPausedTime, PausedTimeAfter = Number(playback, "Time"), Speed = Number(playback, "FreeCameraSpeed") });
                    ReleaseRuntimeInput(); Enter("InputReleaseObserve"); break;
                case "InputReleaseObserve":
                    if (now - phaseAt < .1) break;
                    Require(!Bool(playback, "CameraLooking") && !Keyboard.current!.wKey.isPressed && !Mouse.current!.rightButton.isPressed,
                        "Runtime flight input did not release completely.");
                    QueueRuntimeInput(new[] { Key.C }); Enter("InputReturnThirdPerson"); break;
                case "InputReturnThirdPerson":
                    Require(!Bool(playback, "FreeCamera"), "Runtime-injected C did not return to third person.");
                    ReleaseRuntimeInput(); runtimeInputPassed = true; testPassed = true;
                    evidence.Add(new { Kind = "RuntimeInputReleased", Source = RuntimeInputSource, FreeCamera = false, ReleaseQueued = true });
                    if (manualSeconds > 0)
                    {
                        manualStarted = true; Invoke(owner!, "ToggleCinema"); cinema = Read(owner, "cinema");
                        Require(cinema != null, "Cinema did not reopen for manual inspection.");
                        Enter("ManualReady");
                    }
                    else Enter("CaptureFinish");
                    break;
                case "ManualReady":
                    if (!Bool(State, "IdentityReady")) break;
                    Command("SetProjectName", 0, projectName); Command("Load");
                    Require(Pending.Length == 0 && !Bool(State, "Dirty"), "Manual inspection did not load the owned clean test plan.");
                    Enter("ManualHold"); break;
                case "ManualHold":
                    if (now >= nextManualSnapshot)
                    {
                        nextManualSnapshot = now + .25;
                        var currentCinema = Read(owner, "cinema", true);
                        Write("manual-state.json", new
                        {
                            ElapsedSeconds = now, RemainingSeconds = Math.Max(0, manualSeconds - (now - phaseAt)),
                            Focused = Application.isFocused, Time = Number(playback, "Time"), RequestedTime = Number(playback, "RequestedTime"),
                            Keyboard = Keyboard.current == null ? null : new { Keyboard.current.displayName, Keyboard.current.deviceId, Keyboard.current.enabled, Keyboard.current.native, LastKeyTime = Keyboard.current.lastUpdateTime },
                            Paused = Bool(playback, "Paused"), Buffering = Bool(playback, "Buffering"), FreeCamera = Bool(playback, "FreeCamera"),
                            FlySpeed = Number(playback, "FreeCameraSpeed"), Pose = PoseSnapshot.Take(camera!),
                            CinemaActive = currentCinema != null, State = Read(currentCinema, "state", true),
                            Tab = Read(Read(currentCinema, "editor", true), "selectedTab", true),
                            EditingText = Read(Read(currentCinema, "editor", true), "EditingText", true)
                        });
                    }
                    if (now - phaseAt < manualSeconds && !File.Exists(Path.Combine(output, "manual-finish"))) break;
                    var finalManualCinema = Read(owner, "cinema", true);
                    if (finalManualCinema != null)
                    {
                        cinema = finalManualCinema;
                        // The tester must use the real UI to return to clean.
                        // Do not undo input or accept a manually changed file.
                        bool cleanOwnedDocument = !Bool(State, "Dirty") && !Bool(cinema, "dirty") &&
                            string.Equals(Read(cinema, "projectName")?.ToString(), projectName, StringComparison.Ordinal) &&
                            File.Exists(sidecarPath) && Hash(sidecarPath) == sidecarHash;
                        if (cleanOwnedDocument)
                        {
                            SaveSidecar();
                            evidence.Add(new { Kind = "ManualCleanFinish", Dirty = false, OwnedNameVerified = true, OriginalSavedHashVerified = true, FormalSaveRemovedRecovery = !File.Exists(RecoveryPath) });
                        }
                        else
                        {
                            testPassed = false;
                            error += "\nManual inspection ended with unsaved edits, another target name, or a changed formal file. Generated files were retained according to their last verified hashes.";
                        }
                    }
                    Invoke(owner!, "DisposePresentation"); Screenshot("09-manual-finish"); Enter("CaptureFinish"); break;
                case "CaptureFinish":
                    if (now - phaseAt < 1) break;
                    Require(captures.All(File.Exists), "At least one requested screenshot did not finish writing.");
                    Invoke(owner!, "CloseTheatre"); Enter("Closing"); break;
                case "Closing":
                    if (Bool(theatre, "Busy") || SceneManager.GetActiveScene().name != "Title") break;
                    Finish(testPassed); break;
                case "FailureClosing":
                    if (!Bool(theatre, "Busy") || now - phaseAt > 12) Finish(false);
                    break;
            }
        }
        catch (Exception exception)
        {
            error += "\n" + exception; Logger.LogError(exception);
            if (phase == "FailureClosing") Finish(false);
            else { TryClose(); Enter("FailureClosing"); }
        }
    }

    private void TryClose()
    {
        ReleaseRuntimeInput();
        try { if (owner) Invoke(owner!, "DisposePresentation"); } catch (Exception exception) { error += "\nCinema cleanup: " + exception.Message; }
        if (!manualStarted && sidecarOwned && File.Exists(RecoveryPath))
        {
            try { CaptureOwnedFile(RecoveryPath); } catch (Exception exception) { error += "\nRecovery proof: " + exception.Message; }
        }
        try { if (owner) Invoke(owner!, "CloseTheatre"); } catch (Exception exception) { error += "\nReplay cleanup: " + exception.Message; }
    }
    private void Finish(bool passed)
    {
        if (finished) return; finished = true; armed = false;
        ReleaseRuntimeInput();
        bool sidecarCleanup = CleanupSidecar();
        bool unchangedSave = save.Length != 0 && !Directory.Exists(save) && Hash(save) == saveHash;
        bool unchangedRecording = recording.Length != 0 && Hash(recording) == recordingHash;
        bool unchangedConfigs = configs.All(pair => Hash(pair.Key) == pair.Value);
        Write("summary.json", new
        {
            Passed = passed && unchangedSave && unchangedRecording && unchangedConfigs && sidecarCleanup, Error = error,
            ElapsedSeconds = clock.Elapsed.TotalSeconds, MaximumSeconds = maximumSeconds, ManualSeconds = manualSeconds,
            PluginVersion = owner?.GetType().Assembly.GetName().Version?.ToString(), PluginMvid = owner?.GetType().Assembly.ManifestModule.ModuleVersionId,
            NativeGraphicsDevice = SystemInfo.graphicsDeviceType.ToString(), Width = Screen.width, Height = Screen.height,
            Recording = recording, RecordingSha256 = recordingHash, RecordingUnchanged = unchangedRecording,
            QuicksaveUnchanged = unchangedSave, ConfigurationsUnchangedDuringProbe = unchangedConfigs,
            F8Queued = f8Queued, F8Released = f8Released, RuntimeInputPassed = runtimeInputPassed, RuntimeInputSource, Evidence = evidence, Screenshots = captures,
            BatchEditingPassed = batchEditingPassed, MarkerPointerPassed = markerPointerPassed, RecoveryPassed = recoveryPassed, ProjectManagementPassed = projectManagementPassed, OwnedFiles = ownedFiles.Values.ToArray(),
            Sidecar = new { ProjectName = projectName, Path = sidecarPath, CreatedByProbe = sidecarOwned, FirstHash = firstSidecarHash, FinalHash = sidecarHash, OverwriteChanged = overwriteChanged, LoadedPoseRestored = loadedPoseRestored, Removed = sidecarRemoved },
            VisualAcceptance = "Pending inspection of screenshots; runtime configuration alone does not prove image quality or performance.",
        });
        // Keep Harmony write protection active throughout other plugins' shutdown.
        Application.Quit();
    }
    private void OnApplicationQuit()
    {
        if (armed) { error += "\nApplication exited before smoke completion."; TryClose(); Finish(false); }
    }

    private sealed class PoseSnapshot
    {
        public float[] Position = Array.Empty<float>(), Rotation = Array.Empty<float>(); public float Fov;
        public static PoseSnapshot Take(Camera camera)
        {
            var p = camera.transform.position; var q = camera.transform.rotation;
            return new() { Position = new[] { p.x, p.y, p.z }, Rotation = new[] { q.x, q.y, q.z, q.w }, Fov = camera.fieldOfView };
        }
        public static PoseSnapshot FromCorePose(object pose)
        {
            var p = Read(pose, "Position"); var q = Read(pose, "Rotation");
            return new() { Position = new[] { (float)Number(p, "X"), (float)Number(p, "Y"), (float)Number(p, "Z") },
                Rotation = new[] { (float)Number(q, "X"), (float)Number(q, "Y"), (float)Number(q, "Z"), (float)Number(q, "W") }, Fov = (float)Number(pose, "FieldOfView") };
        }
        public bool Matches(PoseSnapshot other) => Vector3.Distance(new Vector3(Position[0], Position[1], Position[2]), new Vector3(other.Position[0], other.Position[1], other.Position[2])) < .002f &&
            Quaternion.Angle(new Quaternion(Rotation[0], Rotation[1], Rotation[2], Rotation[3]), new Quaternion(other.Rotation[0], other.Rotation[1], other.Rotation[2], other.Rotation[3])) < .02f && Math.Abs(Fov - other.Fov) < .002f;
    }
    private sealed class CameraSnapshot
    {
        public bool Post; public string Depth = "", UpdateMode = ""; public int Mask, TriggerInstance; public float Fov;
        public static CameraSnapshot Take(Camera camera)
        {
            var data = camera.GetComponent<UniversalAdditionalCameraData>() ?? throw new InvalidOperationException("No native URP camera data.");
            return new() { Post = data.renderPostProcessing, Depth = data.requiresDepthOption.ToString(), Mask = data.volumeLayerMask,
                TriggerInstance = data.volumeTrigger ? data.volumeTrigger.GetInstanceID() : 0, UpdateMode = camera.GetVolumeFrameworkUpdateMode().ToString(), Fov = camera.fieldOfView };
        }
        public bool Matches(CameraSnapshot other) => Post == other.Post && Depth == other.Depth && Mask == other.Mask && TriggerInstance == other.TriggerInstance && UpdateMode == other.UpdateMode && Math.Abs(Fov - other.Fov) < .01f;
    }
}
