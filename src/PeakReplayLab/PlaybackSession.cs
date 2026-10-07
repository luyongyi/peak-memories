using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace PeakReplayLab;

internal sealed class PlaybackSession : IDisposable
{
    private readonly IReplayTimeline timeline;
    private readonly Dictionary<string, VisualActor> actors;
    private readonly List<(Renderer Renderer, bool Enabled)> renderers = new();
    private readonly List<InputAction> enabledActions = new();
    private readonly List<InputActionAsset> assets = new();
    private readonly List<CharacterInput> inputs = new();
    private readonly HashSet<string> presentActors = new(StringComparer.Ordinal);
    private readonly List<string> focusCandidates = new();
    private readonly List<ItemEvent> recentEvents = new(3);
    private ItemFrame? heldItem;
    private bool presentingOwned;
    private bool oldPresenting;
    private readonly float oldTimeScale;
    private readonly CursorLockMode oldLock;
    private readonly bool oldCursor;
    private readonly ReplayCursorPolicy cursor = new();
    private bool cursorFocusSubscribed;
    private readonly int sceneHandle;
    private readonly Action<Exception> reportRestoreError;
    private readonly Action<WorldFrame>? applyWorld;
    private Camera camera = null!;
    private ReplayView? view;
    private ItemPlayback? items;
    private LuggagePlayback? crates;
    private RopeReplayPlayback? ropes;
    private EffectReplayPlayback? effects;
    private AudioReplayPlayback? audio;
    private SpawnedReplayPlayback? spawned;
    private BalloonReplayPlayback? balloons;
    private CreatureReplayPlayback? creatures;
    private EnvironmentReplayPlayer? environment;
    private ReplayNameplates? nameplates;
    private double audioScrubMuteUntil;
    private bool AudioPaused => Paused || Buffering || UnityEngine.Time.unscaledTimeAsDouble < audioScrubMuteUntil;
    private bool historyWarning;
    private ReplayFrame? currentFrame;
    private ReplayFrame? hudRightFrame;
    private float hudMix;
    public ReplayHudState? FocusHudState { get; private set; }
    public string ObjectWarning { get; private set; } = "";
    public int RecordedJointCount => currentFrame?.Actors.Sum(actor => actor.JointPose.Length) ?? 0;
    public int MissingJointCount => actors.Values.Sum(actor => actor.Visible ? actor.MissingJointCount : 0);
    public int RecordedItemCount => currentFrame?.Items.Length ?? 0;
    public int RecordedCrateCount => currentFrame?.Crates.Length ?? 0;
    public int VisibleItemCount => items?.VisibleCount ?? 0;
    public int MissingItemCount => items?.MissingCount ?? 0;
    public int SampleHz => timeline.Header.SampleHz;
    public ItemFrame? HeldItem => heldItem;
    public int UiRevision { get; private set; }
    public string PresentationDiagnostic => (view?.Describe() ?? "view-not-ready") +
        $"; schema={timeline.Header.Schema}; items={RecordedItemCount}; renderedItems={VisibleItemCount}; missingItems={MissingItemCount}; crates={RecordedCrateCount}; cachedEvents={timeline.CachedEventCount}; cachedPages={timeline.CachedPageCount}" +
        $"; actorJoints={RecordedJointCount}; missingActorJoints={MissingJointCount}" +
        $"; ropes={currentFrame?.Ropes.Length ?? 0}; effects={currentFrame?.Effects.Length ?? 0}; renderedEffects={effects?.VisibleCount ?? 0}; incompleteEffects={effects?.MissingCount ?? 0}; gameSounds={currentFrame?.Audio.Length ?? 0}; spawnedTerrain={currentFrame?.Spawned.Length ?? 0}; balloons={currentFrame?.Balloons.Length ?? 0}; renderedBalloons={balloons?.VisibleCount ?? 0}; missingBalloons={balloons?.MissingCount ?? 0}; creatures={currentFrame?.Creatures.Length ?? 0}; renderedCreatures={creatures?.VisibleCount ?? 0}; missingCreatures={creatures?.MissingCount ?? 0}; environmentKnown={currentFrame?.World.Environment != null}; windSources={currentFrame?.World.Environment?.Winds.Length ?? 0}; lavaSources={currentFrame?.World.Environment?.Lava.Length ?? 0}; {audio?.Diagnostic ?? "no-recorded-audio-track"}";
    private bool disposed;
    private float yaw, pitch = 15, distance = 5;
    private static readonly FieldInfo[] InputFlags = typeof(CharacterInput).GetFields(BindingFlags.Public | BindingFlags.Instance)
        .Where(f => f.FieldType == typeof(bool) && (f.Name.EndsWith("Pressed", StringComparison.Ordinal) || f.Name.EndsWith("Released", StringComparison.Ordinal))).ToArray();
    private static readonly object ClearedFlag = false;

    public double Time { get; private set; }
    public double Duration => timeline.Duration;
    public bool Buffering { get; private set; }
    public double RequestedTime { get; private set; }
    public string BufferingLabel => Buffering ? $"正在读取 {TimeSpan.FromSeconds(RequestedTime):hh\\:mm\\:ss} 附近的录像，播放时钟已暂停…" : "";
    public bool Paused { get; set; }
    public float Speed { get; set; } = 1;
    public bool FreeCamera { get; private set; }
    public bool CameraLooking => cursor.Looking;
    public string FocusId { get; private set; } = "";
    public ActorFrame? FocusState => actors.TryGetValue(FocusId, out var a) && a.Visible ? a.State : null;
    public int VisibleActors { get; private set; }
    public static bool IsSolo => !PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom.PlayerCount <= 1;

    public PlaybackSession(ReplayClip clip, Dictionary<string, VisualActor> actors, Action<Exception> reportRestoreError, Action<WorldFrame>? applyWorld = null, Action<string>? warning = null)
        : this(new MemoryReplayTimeline(clip), actors, reportRestoreError, applyWorld, warning) { }

    public PlaybackSession(IReplayTimeline timeline, Dictionary<string, VisualActor> actors, Action<Exception> reportRestoreError, Action<WorldFrame>? applyWorld = null, Action<string>? warning = null)
    {
        this.timeline = timeline;
        this.actors = actors;
        this.reportRestoreError = reportRestoreError;
        this.applyWorld = applyWorld;
        if (!ReplayRules.SupportedSchema(timeline.Header.Schema)) throw new InvalidOperationException("此录像格式不受支持，请重新录制。");
        if (!IsSolo) throw new InvalidOperationException("Playback is solo-only. Recording with friends is supported.");
        if (timeline.FrameCount < 2) throw new InvalidOperationException("Record at least two frames first.");
        if (!ReplayRules.Compatible(timeline.Header, Capture.Header(timeline.Header.SampleHz)))
            throw new InvalidOperationException("Map route or game version changed. Record again in this map.");
        foreach (string id in timeline.ActorIds)
            if (!actors.ContainsKey(id)) throw new InvalidOperationException("Missing visual template for a recorded participant.");
        sceneHandle = SceneManager.GetActiveScene().handle;
        oldTimeScale = UnityEngine.Time.timeScale;
        oldLock = Cursor.lockState;
        oldCursor = Cursor.visible;
        try
        {
            view = new ReplayView(reportRestoreError);
            camera = view.Camera;
            nameplates = new ReplayNameplates(reportRestoreError);
            void Warn(string text) { ObjectWarning = text; warning?.Invoke(text); }
            environment = new EnvironmentReplayPlayer(Warn);
            items = new ItemPlayback(Warn, actors);
            crates = new LuggagePlayback(Warn);
            spawned = new SpawnedReplayPlayback(Warn);
            balloons = new BalloonReplayPlayback(actors, Warn);
            creatures = new CreatureReplayPlayback(Warn);
            ropes = new RopeReplayPlayback(Warn);
            effects = new EffectReplayPlayback(Warn);
            effects.SetTimeline(t =>
            {
                if (this.timeline.TryHistory(t, out var history)) return history.Left.Effects;
                if (!historyWarning)
                {
                    historyWarning = true;
                    Warn("长特效的部分历史位于当前读取范围之外；该特效预热将近似呈现，人物和物品时间不受影响。");
                }
                return Array.Empty<EffectReplayFrame>();
            });
            audio = new AudioReplayPlayback(Warn);
            foreach (var c in Capture.Players())
            {
                foreach (var r in c.GetComponentsInChildren<Renderer>(true)) renderers.Add((r, r.enabled));
                if (c.input) inputs.Add(c.input);
                if (c.input && c.input.actions && !assets.Contains(c.input.actions)) assets.Add(c.input.actions);
            }
            // CharacterInput.Sample reads the shared actions, not necessarily its own actions field.
            if (InputSystem.actions && !assets.Contains(InputSystem.actions)) assets.Add(InputSystem.actions);
            foreach (var asset in assets)
            {
                enabledActions.AddRange(asset.Where(action => action.enabled));
                asset.Disable();
            }
            ClearInputs();
            oldPresenting = ReplaySafety.Presenting;
            ReplaySafety.Presenting = true; presentingOwned = true;
            UnityEngine.Time.timeScale = 0;
            Application.focusChanged += ApplicationFocusChanged;
            cursorFocusSubscribed = true;
            FocusId = timeline.ActorIds.FirstOrDefault() ?? "";
            yaw = camera.transform.eulerAngles.y;
            Seek(0);
            ApplyCamera(0);
            nameplates.Apply(camera, actors);
        }
        catch { Dispose(); throw; }
    }

    public void Tick(float delta)
    {
        using var timing = ReplayPerformance.Measure(ReplayStage.Playback);
        if (!IsSolo) throw new InvalidOperationException("A player joined. Playback stopped and live controls restored.");
        if (SceneManager.GetActiveScene().handle != sceneHandle) throw new InvalidOperationException("Scene changed. Playback stopped.");
        if (!camera) throw new InvalidOperationException("Replay camera was removed.");
        UnityEngine.Time.timeScale = 0;
        foreach (var asset in assets) if (asset) asset.Disable();
        ClearInputs();
        foreach (var saved in renderers) if (saved.Renderer && saved.Renderer.enabled) saved.Renderer.enabled = false;
        view!.Enforce();
        items?.Enforce();
        crates?.Enforce();
        using (ReplayPerformance.Measure(ReplayStage.PlaybackRopes)) ropes?.Enforce();
        using (ReplayPerformance.Measure(ReplayStage.PlaybackEffects)) effects?.Enforce();
        using (ReplayPerformance.Measure(ReplayStage.PlaybackAudio)) audio?.Enforce();
        using (ReplayPerformance.Measure(ReplayStage.PlaybackSpawned)) spawned?.Enforce();
        using (ReplayPerformance.Measure(ReplayStage.PlaybackBalloons)) balloons?.Enforce();
        using (ReplayPerformance.Measure(ReplayStage.PlaybackCreatures)) creatures?.Enforce();
        environment?.Enforce();
        audio?.SetClock(AudioPaused, Speed);
        if (Buffering) ApplyTime(RequestedTime, false);
        else if (!Paused)
        {
            ApplyTime(Time + delta * Speed, false);
            if (Time >= Duration) { Paused = true; audio?.SetClock(true, Speed); }
        }
        ApplyCamera(delta);
        environment?.UpdateCamera(camera.transform.position);
        environment?.UpdateVisualClock(Time);
        nameplates?.Apply(camera, actors);
    }

    private void ClearInputs()
    {
        foreach (var input in inputs)
        {
            if (!input) continue;
            input.movementInput = Vector2.zero;
            input.lookInput = Vector2.zero;
            input.scrollInput = 0;
            foreach (var field in InputFlags) field.SetValue(input, ClearedFlag);
        }
    }

    public void Seek(double time)
    {
        // GUI dragging can alternate seek and normal ticks. Debounce sound so a
        // moving slider cannot restart the same short explosion every render frame.
        audioScrubMuteUntil = UnityEngine.Time.unscaledTimeAsDouble + .15;
        audio?.SetClock(true, Speed);
        ApplyTime(time, true);
    }

    private void ApplyTime(double time, bool explicitSeek)
    {
        if (!ReplayRules.Finite(time)) return;
        double previousTime = Time;
        RequestedTime = Math.Max(0, Math.Min(Duration, time));
        if (explicitSeek) UiRevision++;
        if (!timeline.TrySample(RequestedTime, out var sample))
        {
            Buffering = true;
            audio?.SetClock(true, Speed);
            return;
        }
        bool wasBuffering = Buffering;
        Buffering = false;
        Time = RequestedTime;
        if (currentFrame != null && Time == previousTime && !wasBuffering) return;
        bool discontinuity = currentFrame == null || explicitSeek || wasBuffering || Time < previousTime || Time - previousTime > .25;
        var left = sample.Left; var right = sample.Right; float mix = sample.Mix;
        currentFrame = left;
        hudRightFrame = right; hudMix = mix;
        applyWorld?.Invoke(left.World);
        using (ReplayPerformance.Measure(ReplayStage.PlaybackEnvironment))
            environment?.Apply(sample.Environment.Left, sample.Environment.Right, sample.Environment.Mix, Time, camera.transform.position);
        using (ReplayPerformance.Measure(ReplayStage.PlaybackCreatures)) creatures?.Apply(left.Creatures, right.Creatures, mix, Time, discontinuity);
        crates?.Apply(left.Crates, right.Crates, mix, Time);
        using (ReplayPerformance.Measure(ReplayStage.PlaybackRopes)) ropes?.Apply(left.Ropes, right.Ropes, mix, Time, discontinuity);
        using (ReplayPerformance.Measure(ReplayStage.PlaybackEffects)) effects?.Apply(left.Effects, right.Effects, mix, Time, discontinuity);
        using (ReplayPerformance.Measure(ReplayStage.PlaybackSpawned)) spawned?.Apply(left.Spawned, right.Spawned, mix, Time, discontinuity);
        using (ReplayPerformance.Measure(ReplayStage.PlaybackAudio))
        {
            audio?.SetClock(AudioPaused, Speed);
            audio?.Apply(left.Audio, right.Audio, mix, Time, discontinuity);
        }
        // Recorded local joints drive this visual hierarchy. Hide only actors
        // absent at the current recorded timestamp.
        presentActors.Clear();
        foreach (var state in left.Actors) presentActors.Add(state.Id);
        foreach (var pair in actors) if (!presentActors.Contains(pair.Key)) pair.Value.Hide();
        VisibleActors = 0;
        foreach (var state in left.Actors)
        {
            var actor = actors[state.Id];
            if (sample.Joints == null || !sample.Joints.TryActor(state.Id, sample.Time, out var joints))
                throw new InvalidOperationException("缺少人物关节的真实采样范围。");
            actor.Pose(state, joints);
            if (actor.Visible) VisibleActors++;
        }
        // Slot attachments must follow the current actor pose, not last frame's
        // backpack transform or the original physics-driven item's world position.
        items?.Apply(left.Items, right.Items, mix);
        using (ReplayPerformance.Measure(ReplayStage.PlaybackBalloons)) balloons?.Apply(left.Balloons, right.Balloons, mix, Time, discontinuity);
        RefreshHeldItem();
        RefreshFocusedHud();
    }

    public IReadOnlyList<ItemEvent> RecentEvents()
    {
        timeline.FillRecentEvents(Time, FocusId, recentEvents);
        return recentEvents;
    }

    public void CycleFocus()
    {
        focusCandidates.Clear();
        foreach (var actor in actors) if (actor.Value.Visible) focusCandidates.Add(actor.Key);
        if (focusCandidates.Count == 0) return;
        FocusId = focusCandidates[(focusCandidates.IndexOf(FocusId) + 1) % focusCandidates.Count];
        UiRevision++; RefreshHeldItem(); RefreshFocusedHud();
    }

    private void RefreshFocusedHud()
    {
        ReplayHudState? right = null;
        if (hudRightFrame != null)
            foreach (var actor in hudRightFrame.Actors)
                if (actor.Id == FocusId) { right = actor.HudState; break; }
        FocusHudState = ReplayHudState.Interpolate(FocusState?.HudState, right, hudMix);
    }

    private void RefreshHeldItem()
    {
        heldItem = null;
        if (currentFrame == null) return;
        foreach (var item in currentFrame.Items)
            if (item.HolderId == FocusId && item.State == (int)ItemState.Held) { heldItem = item; break; }
    }

    public void ToggleCamera()
    {
        FreeCamera = !FreeCamera;
        yaw = camera.transform.eulerAngles.y;
        pitch = Mathf.DeltaAngle(0, camera.transform.eulerAngles.x);
    }

    private void ApplyCamera(float dt)
    {
        var mouse = Mouse.current;
        var keys = Keyboard.current;
        ApplyCursor(Application.isFocused, mouse?.rightButton.isPressed == true);
        bool looking = cursor.Looking;
        if (cursor.ReadLookDelta && mouse != null)
        {
            Vector2 motion = mouse.delta.ReadValue();
            yaw += motion.x * .12f;
            pitch = Mathf.Clamp(pitch - motion.y * .12f, -85, 85);
        }
        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0);
        if (FreeCamera)
        {
            camera.transform.rotation = rotation;
            // Require RMB so typing/operating the timeline cannot accidentally move the camera.
            if (looking && keys != null)
            {
                float x = (keys.dKey.isPressed ? 1 : 0) - (keys.aKey.isPressed ? 1 : 0);
                float z = (keys.wKey.isPressed ? 1 : 0) - (keys.sKey.isPressed ? 1 : 0);
                float y = (keys.eKey.isPressed ? 1 : 0) - (keys.qKey.isPressed ? 1 : 0);
                float speed = keys.leftShiftKey.isPressed ? 20 : 6;
                camera.transform.position += (rotation * new Vector3(x, 0, z) + Vector3.up * y) * (speed * dt);
            }
        }
        else if (actors.TryGetValue(FocusId, out var focused) && focused.Visible)
        {
            if (mouse != null && looking) distance = Mathf.Clamp(distance - mouse.scroll.ReadValue().y * .015f, 1, 35);
            Vector3 target = focused.Position + Vector3.up * .65f;
            Vector3 offset = rotation * Vector3.back;
            float actualDistance = distance;
            // Read-only obstruction test; nothing in the replay writes to live physics.
            if (Physics.SphereCast(target, .15f, offset, out var hit, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                actualDistance = Mathf.Clamp(hit.distance - .15f, .4f, distance);
            camera.transform.SetPositionAndRotation(target + offset * actualDistance, rotation);
        }
    }

    private void ApplicationFocusChanged(bool focused)
    {
        if (!disposed) ApplyCursor(focused, Mouse.current?.rightButton.isPressed == true);
    }

    private void ApplyCursor(bool focused, bool rightPressed)
    {
        if (!cursor.Update(focused, rightPressed)) return;
        Cursor.lockState = cursor.Looking ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !cursor.Looking;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // Independent restores: a destroyed scene object must not prevent time/input recovery.
        void Restore(Action action)
        {
            try { action(); }
            catch (Exception e) { try { reportRestoreError(e); } catch { /* Continue restoring even if the logger has gone away. */ } }
        }
        if (cursorFocusSubscribed) Restore(() => { Application.focusChanged -= ApplicationFocusChanged; cursorFocusSubscribed = false; });
        Restore(() => { UnityEngine.Time.timeScale = oldTimeScale; });
        foreach (var a in actors.Values) Restore(a.Hide);
        Restore(() => items?.Dispose());
        Restore(() => crates?.Dispose());
        Restore(() => ropes?.Dispose()); Restore(() => effects?.Dispose()); Restore(() => audio?.Dispose());
        Restore(() => spawned?.Dispose());
        Restore(() => balloons?.Dispose());
        Restore(() => creatures?.Dispose());
        Restore(() => environment?.Dispose());
        Restore(() => nameplates?.Dispose());
        Restore(() => view?.Dispose());
        Restore(timeline.Dispose);
        if (presentingOwned) Restore(() => { ReplaySafety.Presenting = oldPresenting; presentingOwned = false; });
        foreach (var saved in renderers) Restore(() => { if (saved.Renderer) saved.Renderer.enabled = saved.Enabled; });
        Restore(ClearInputs);
        foreach (var action in enabledActions) Restore(action.Enable);
        Restore(() => { Cursor.lockState = oldLock; Cursor.visible = oldCursor; });
    }
}
