using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Photon.Pun;
using UnityEngine;
using UnityEngine.EventSystems;
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
    private readonly WorldReplayPlayer? stagedWorld;
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
    private InteractionReplayPlayback? interactions;
    private EnvironmentReplayPlayer? environment;
    private readonly PresentationWorkBudget workBudget = new();
    private bool seekPending;
    public bool PreparingAudio { get; private set; }
    public bool PreparingWorld { get; private set; }
    private ReplayNameplates? nameplates;
    private double audioScrubMuteUntil;
    private bool AudioPaused => Paused || Buffering || UnityEngine.Time.unscaledTimeAsDouble < audioScrubMuteUntil;
    private Vector3 constructionPosition;
    private Quaternion constructionRotation;
    private long seekStarted;
    private double lastSeekMilliseconds;
    private string historyWarning = "";
    private readonly ReplaySeekDiagnostics seekDiagnostics = new();
    public ReplaySeekDiagnostics SeekDiagnostics => seekDiagnostics;
    public ReplayAudioStartDiagnostics.Entry[] SlowAudioStarts => audio?.SlowStartSnapshot ?? Array.Empty<ReplayAudioStartDiagnostics.Entry>();
    public int SeekSequence { get; private set; }
    public double LastSeekRequestedAt { get; private set; }
    private ReplayFrame? currentFrame;
    private ReplayFrame? hudRightFrame;
    private float hudMix;
    private readonly ReplayHudState presentationHud = new();
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
        $"; actorJoints={RecordedJointCount}; missingActorJoints={MissingJointCount}; seekPoseMs={lastSeekMilliseconds:F1}; paused={Paused}; buffering={Buffering}; preparingAudio={PreparingAudio}; preparingWorld={PreparingWorld}; cursor={Time:F3}; requested={RequestedTime:F3}; seekSequence={SeekSequence}; {timeline.LoadingDiagnostic}" +
        $"; ropes={currentFrame?.Ropes.Length ?? 0}; effects={currentFrame?.Effects.Length ?? 0}; renderedEffects={effects?.VisibleCount ?? 0}; incompleteEffects={effects?.MissingCount ?? 0}; laggingEffects={effects?.LaggingCount ?? 0}; gameSounds={currentFrame?.Audio.Length ?? 0}; spawnedTerrain={currentFrame?.Spawned.Length ?? 0}; balloons={currentFrame?.Balloons.Length ?? 0}; renderedBalloons={balloons?.VisibleCount ?? 0}; missingBalloons={balloons?.MissingCount ?? 0}; creatures={currentFrame?.Creatures.Length ?? 0}; renderedCreatures={creatures?.VisibleCount ?? 0}; missingCreatures={creatures?.MissingCount ?? 0}; interactions={currentFrame?.Interactions.Length ?? 0}; renderedInteractions={interactions?.VisibleCount ?? 0}; missingInteractions={interactions?.MissingCount ?? 0}; environmentKnown={currentFrame?.World.Environment != null}; windSources={currentFrame?.World.Environment?.Winds.Length ?? 0}; lavaSources={currentFrame?.World.Environment?.Lava.Length ?? 0}; {audio?.Diagnostic ?? "no-recorded-audio-track"}";
    private bool disposed;
    private IReplayPresentationExtension? presentationExtension;
    public Camera PresentationCamera => camera;
    public bool CameraReadsLookDelta => cursor.ReadLookDelta;
    public bool ShowNames { get; set; } = true;
    public string PresentationSegment => (currentFrame?.World.Segment ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
    // Read cached history only. Editing a camera key must not start IO or move
    // the replay cursor just to infer the segment at its proposed new time.
    public bool TryGetPresentationSegmentAt(double time, out string segment)
    {
        segment = "";
        if (double.IsNaN(time) || double.IsInfinity(time) || time < 0 || time > Duration) return false;
        if (!Buffering && currentFrame != null && Math.Abs(time - Time) < .0001)
        { segment = PresentationSegment; return true; }
        if (!timeline.TryHistory(time, out var sample)) return false;
        segment = sample.Left.World.Segment.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }
    private float yaw, pitch = 15, roll, distance = 5;
    public float FreeCameraSpeed { get; private set; } = 6;
    private static readonly FieldInfo[] InputFlags = typeof(CharacterInput).GetFields(BindingFlags.Public | BindingFlags.Instance)
        .Where(f => f.FieldType == typeof(bool) && (f.Name.EndsWith("Pressed", StringComparison.Ordinal) || f.Name.EndsWith("Released", StringComparison.Ordinal))).ToArray();
    private static readonly object ClearedFlag = false;

    public double Time { get; private set; }
    public double Duration => timeline.Duration;
    public bool Buffering { get; private set; }
    public double RequestedTime { get; private set; }
    public string BufferingLabel => Buffering ? PreparingWorld ? "正在重建目标关卡，播放时钟已暂停…" : PreparingAudio ? "正在准备声音资源，播放时钟已暂停…" : $"正在读取 {TimeSpan.FromSeconds(RequestedTime):hh\\:mm\\:ss} 附近的录像，播放时钟已暂停…" : "";
    public bool Paused { get; set; }
    public float Speed { get; set; } = 1;
    public bool FreeCamera { get; private set; }
    public bool CameraLooking => cursor.Looking;
    public string FocusId { get; private set; } = "";
    public ActorFrame? FocusState => actors.TryGetValue(FocusId, out var a) && a.Visible ? a.State : null;
    public int VisibleActors { get; private set; }
    public static bool IsSolo => !PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom.PlayerCount <= 1;

    public PlaybackSession(ReplayClip clip, Dictionary<string, VisualActor> actors, Action<Exception> reportRestoreError, Action<WorldFrame>? applyWorld = null, Action<string>? warning = null, WorldReplayPlayer? stagedWorld = null)
        : this(new MemoryReplayTimeline(clip), actors, reportRestoreError, applyWorld, warning, stagedWorld) { }

    public PlaybackSession(IReplayTimeline timeline, Dictionary<string, VisualActor> actors, Action<Exception> reportRestoreError, Action<WorldFrame>? applyWorld = null, Action<string>? warning = null, WorldReplayPlayer? stagedWorld = null)
    {
        this.timeline = timeline;
        this.actors = actors;
        this.reportRestoreError = reportRestoreError;
        this.applyWorld = applyWorld;
        this.stagedWorld = stagedWorld;
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
            environment = new EnvironmentReplayPlayer(Warn, workBudget);
            items = new ItemPlayback(Warn, actors, workBudget, ConstructionPriority);
            crates = new LuggagePlayback(Warn, workBudget);
            spawned = new SpawnedReplayPlayback(Warn, workBudget, ConstructionPriority);
            balloons = new BalloonReplayPlayback(actors, Warn, workBudget, ConstructionPriority);
            creatures = new CreatureReplayPlayback(Warn, workBudget, ConstructionPriority, includeExtended: timeline.Header.Schema >= 15, includeNativeBodies: timeline.Header.Schema >= 21);
            interactions = new InteractionReplayPlayback(Warn, workBudget);
            ropes = new RopeReplayPlayback(Warn, workBudget);
            effects = new EffectReplayPlayback(Warn, workBudget, includeSceneEvents: timeline.Header.Schema >= 22);
            effects.SetTimeline(t =>
            {
                // The birth resolver biases a boundary by a few microseconds;
                // never turn an already-loaded final frame into pending IO.
                t = Math.Max(0, Math.Min(this.timeline.Duration, t));
                if (this.timeline.TryHistory(t, out var history))
                    return new EffectReplayHistorySample(history.Scene.Left.Effects, history.Scene.Right.Effects, history.Scene.LeftTime, history.Scene.RightTime);
                this.timeline.RequestHistory(t);
                if (this.timeline.HistoryWarning.Length != 0 && historyWarning != this.timeline.HistoryWarning)
                { historyWarning = this.timeline.HistoryWarning; Warn(historyWarning); }
                return null;
            });
            audio = new AudioReplayPlayback(Warn, includeSceneEvents: timeline.Header.Schema >= 22);
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
        long tickStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        workBudget.BeginFrame(UnityEngine.Time.frameCount);
        timeline.SetLookAhead(!Paused, Speed);
        using var timing = ReplayPerformance.Measure(ReplayStage.Playback);
        if (!IsSolo) throw new InvalidOperationException("A player joined. Playback stopped and live controls restored.");
        if (SceneManager.GetActiveScene().handle != sceneHandle) throw new InvalidOperationException("Scene changed. Playback stopped.");
        if (!camera) throw new InvalidOperationException("Replay camera was removed.");
        UnityEngine.Time.timeScale = 0;
        foreach (var asset in assets) if (asset) asset.Disable();
        ClearInputs();
        foreach (var saved in renderers) if (saved.Renderer && saved.Renderer.enabled) saved.Renderer.enabled = false;
        view!.Enforce();
        crates?.Enforce();
        using (ReplayPerformance.Measure(ReplayStage.PlaybackRopes)) ropes?.Enforce();
        using (ReplayPerformance.Measure(ReplayStage.PlaybackAudio)) audio?.Enforce();
        environment?.Enforce();
        audio?.SetClock(AudioPaused, Speed);
        if (seekPending)
        {
            seekPending = false;
            ApplyTime(RequestedTime, true);
        }
        else if (Buffering) ApplyTime(RequestedTime, false);
        else if (!Paused)
        {
            ApplyTime(Time + delta * Speed, false);
            if (Time >= Duration) { Paused = true; audio?.SetClock(true, Speed); }
        }
        // Complete only the latest cursor, including while paused. A new seek
        // must replace pending work before any old cursor is warmed or allocated.
        using (ReplayPerformance.Measure(ReplayStage.PlaybackCamera)) ApplyCamera(delta);
        constructionPosition = camera.transform.position;
        constructionRotation = camera.transform.rotation;
        environment?.UpdateCamera(camera.transform.position);
        environment?.UpdateVisualClock(Time);
        for (int turn = 0; turn < 9; turn++)
            switch ((UnityEngine.Time.frameCount + turn) % 9)
            {
                case 0: using (ReplayPerformance.Measure(ReplayStage.PlaybackItems)) items?.Enforce(!Buffering); break;
                case 1: using (ReplayPerformance.Measure(ReplayStage.PlaybackBalloons)) balloons?.Enforce(!Buffering); break;
                case 2: using (ReplayPerformance.Measure(ReplayStage.PlaybackCreatures)) creatures?.Enforce(!Buffering); break;
                case 3: using (ReplayPerformance.Measure(ReplayStage.PlaybackSpawned)) spawned?.Enforce(!Buffering); break;
                case 4: using (ReplayPerformance.Measure(ReplayStage.PlaybackCrates)) crates?.Enforce(!Buffering); break;
                case 5: using (ReplayPerformance.Measure(ReplayStage.PlaybackRopes)) ropes?.Enforce(!Buffering); break;
                case 6: using (ReplayPerformance.Measure(ReplayStage.PlaybackEffects)) effects?.Enforce(!Buffering); break;
                case 7: using (ReplayPerformance.Measure(ReplayStage.PlaybackEnvironment)) environment?.EnforcePresentation(!Buffering); break;
                case 8: using (ReplayPerformance.Measure(ReplayStage.PlaybackInteractions)) interactions?.Enforce(!Buffering); break;
            }
        try { presentationExtension?.CameraApplied(); }
        catch (Exception error) { ReleaseFailedPresentation(error); }
        nameplates?.SetVisible(ShowNames);
        nameplates?.Apply(camera, actors);
        seekDiagnostics.Observe(UnityEngine.Time.realtimeSinceStartupAsDouble, Time, Buffering, Paused,
            (System.Diagnostics.Stopwatch.GetTimestamp() - tickStarted) * 1000d / System.Diagnostics.Stopwatch.Frequency,
            UnityEngine.Time.unscaledDeltaTime * 1000d);
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
        if (!ReplayRules.Finite(time)) return;
        // GUI dragging can alternate seek and normal ticks. Debounce sound so a
        // moving slider cannot restart the same short explosion every render frame.
        audioScrubMuteUntil = UnityEngine.Time.unscaledTimeAsDouble + .15;
        audio?.SetClock(true, Speed);
        RequestedTime = Math.Max(0, Math.Min(Duration, time));
        SeekSequence++;
        LastSeekRequestedAt = UnityEngine.Time.realtimeSinceStartupAsDouble;
        seekDiagnostics.Request(RequestedTime, Paused, LastSeekRequestedAt);
        stagedWorld?.Cancel();
        environment?.InvalidatePresentation();
        seekStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        historyWarning = "";
        Buffering = true; seekPending = true;
        PreparingAudio = false; PreparingWorld = false;
    }

    private void ApplyTime(double time, bool explicitSeek)
    {
        if (!ReplayRules.Finite(time)) return;
        double previousTime = Time;
        RequestedTime = Math.Max(0, Math.Min(Duration, time));
        if (explicitSeek) UiRevision++;
        ReplayTimelineSample sample;
        bool ready;
        using (ReplayPerformance.Measure(ReplayStage.PlaybackSample)) ready = timeline.TrySample(RequestedTime, out sample);
        if (!ready)
        {
            Buffering = true;
            PreparingAudio = false; PreparingWorld = false;
            audio?.SetClock(true, Speed);
            return;
        }
        bool wasBuffering = Buffering;
        if ((explicitSeek || wasBuffering) && audio != null &&
            !audio.PrepareSeek(sample.Scene.Left.Audio, sample.Scene.Right.Audio, RequestedTime,
                sample.Scene.LeftTime, sample.Scene.RightTime, SeekSequence))
        {
            Buffering = true; PreparingAudio = true; PreparingWorld = false;
            audio.SetClock(true, Speed);
            return;
        }
        PreparingAudio = false;
        if ((explicitSeek || wasBuffering) && stagedWorld != null)
        {
            var targetWorld = sample.Scene.Left.World;
            if (stagedWorld.NeedsSceneShield(targetWorld)) view!.CoverWorldPreparation(true);
            bool worldReady;
            using (ReplayPerformance.Measure(ReplayStage.PlaybackWorldPrepare))
                worldReady = stagedWorld.Prepare(targetWorld, SeekSequence, UnityEngine.Time.frameCount);
            if (!worldReady)
            {
                Buffering = true; PreparingWorld = true;
                audio?.SetClock(true, Speed);
                return;
            }
        }
        PreparingWorld = false;
        Buffering = false;
        Time = RequestedTime;
        if (currentFrame != null && Time == previousTime && !wasBuffering) return;
        bool discontinuity = currentFrame == null || explicitSeek || wasBuffering || Time < previousTime || Time - previousTime > .25;
        var left = sample.Left; var right = sample.Right; float mix = sample.Mix;
        var sceneLeft = sample.Scene.Left; var sceneRight = sample.Scene.Right; float sceneMix = sample.Scene.Mix;
        currentFrame = left;
        hudRightFrame = right; hudMix = mix;
        using (ReplayPerformance.Measure(ReplayStage.PlaybackWorld))
        {
            stagedWorld?.Apply(sceneLeft.World);
            applyWorld?.Invoke(sceneLeft.World);
        }
        using (ReplayPerformance.Measure(ReplayStage.PlaybackEnvironment))
            environment?.Apply(sample.Environment.Left, sample.Environment.Right, sample.Environment.Mix, Time, camera.transform.position);
        using (ReplayPerformance.Measure(ReplayStage.PlaybackAudio))
        {
            audio?.SetClock(AudioPaused, Speed);
            audio?.Apply(sceneLeft.Audio, sceneRight.Audio, sceneMix, Time, discontinuity, sample.Scene.LeftTime, sample.Scene.RightTime);
        }
        // Recorded local joints drive this visual hierarchy. Hide only actors
        // absent at the current recorded timestamp.
        using (ReplayPerformance.Measure(ReplayStage.PlaybackActors))
        {
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
        }
        // Rank construction using the destination camera, before allocating
        // objects at that cursor. Mouse input is still consumed once in Tick.
        constructionRotation = Quaternion.Euler(pitch, yaw, 0);
        constructionPosition = camera.transform.position;
        if (!FreeCamera && actors.TryGetValue(FocusId, out var focused) && focused.Visible)
            constructionPosition = focused.Position + Vector3.up * .65f + constructionRotation * Vector3.back * distance;
        environment?.UpdateCamera(constructionPosition);
        if (discontinuity)
        {
            double historyStart = Time;
            foreach (var effect in sceneLeft.Effects)
                if (effect.Kind == 0)
                {
                    double phase = EffectReplayRules.PhaseAt(effect, Time);
                    double startPhase = Math.Max(0, phase - 12 * Math.Max(effect.Rate, 1));
                    historyStart = Math.Min(historyStart, Math.Max(0, EffectReplayRules.TimeAtPhase(effect, startPhase)));
                }
            if (historyStart < Time) timeline.RequestHistory(historyStart);
        }
        // Slot attachments must follow the current actor pose, not last frame's
        // backpack transform or the original physics-driven item's world position.
        // All tracks that build or simulate share the same rotating admission
        // order here and in Tick, so later completion work cannot be starved by
        // the earlier Apply calls. Pose and world state above remain immediate.
        for (int turn = 0; turn < 9; turn++)
            switch ((UnityEngine.Time.frameCount + turn) % 9)
            {
                case 0: using (ReplayPerformance.Measure(ReplayStage.PlaybackItems)) items?.Apply(sceneLeft.Items, sceneRight.Items, sceneMix, discontinuity, Time, sample.Joints?.Items); break;
                case 1: using (ReplayPerformance.Measure(ReplayStage.PlaybackBalloons)) balloons?.Apply(sceneLeft.Balloons, sceneRight.Balloons, sceneMix, Time, discontinuity); break;
                case 2: using (ReplayPerformance.Measure(ReplayStage.PlaybackCreatures)) creatures?.Apply(sceneLeft.Creatures, sceneRight.Creatures, sceneMix, Time, discontinuity, sample.Joints?.Creatures); break;
                case 3: using (ReplayPerformance.Measure(ReplayStage.PlaybackSpawned)) spawned?.Apply(sceneLeft.Spawned, sceneRight.Spawned, sceneMix, Time, discontinuity); break;
                case 4: using (ReplayPerformance.Measure(ReplayStage.PlaybackCrates)) crates?.Apply(sceneLeft.Crates, sceneRight.Crates, sceneMix, Time, discontinuity); break;
                case 5: using (ReplayPerformance.Measure(ReplayStage.PlaybackRopes)) ropes?.Apply(sceneLeft.Ropes, sceneRight.Ropes, sceneMix, Time, discontinuity, sample.Joints?.Ropes); break;
                case 6: using (ReplayPerformance.Measure(ReplayStage.PlaybackEffects)) effects?.Apply(sceneLeft.Effects, sceneRight.Effects, sceneMix, Time, discontinuity, sample.Scene.LeftTime, sample.Scene.RightTime); break;
                case 7: using (ReplayPerformance.Measure(ReplayStage.PlaybackEnvironment)) environment?.EnforcePresentation(); break;
                case 8: using (ReplayPerformance.Measure(ReplayStage.PlaybackInteractions)) interactions?.Apply(sceneLeft.Interactions, sceneRight.Interactions, sceneMix, Time, discontinuity); break;
            }
        RefreshHeldItem();
        RefreshFocusedHud();
        view?.CoverWorldPreparation(false);
        if (seekStarted != 0)
        {
            lastSeekMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - seekStarted) * 1000d / System.Diagnostics.Stopwatch.Frequency;
            seekStarted = 0;
        }
    }

    private float ConstructionPriority(Vector3 position)
    {
        Vector3 offset = position - constructionPosition;
        float squared = offset.sqrMagnitude;
        if (squared < 4) return squared;
        // A conservative cone includes objects close to the screen edges.
        // Offscreen objects remain queued; camera motion reprioritizes them.
        float facing = Vector3.Dot(constructionRotation * Vector3.forward, offset.normalized);
        return squared + (facing < .25f ? 1000000 : 0);
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
        FocusHudState = ReplayHudState.InterpolateForPresentation(FocusState?.HudState, right, hudMix, presentationHud);
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
        SetFreeCamera(!FreeCamera);
    }

    public void SetFreeCamera(bool free)
    {
        FreeCamera = free;
        SyncCameraOrientation();
        UiRevision++;
    }

    public void SyncCameraOrientation()
    {
        yaw = camera.transform.eulerAngles.y;
        // Preserve an explicitly edited cinematic pose. Mouse look still bounds
        // pitch when the user takes over, without clipping a paused numeric edit.
        pitch = Mathf.DeltaAngle(0, camera.transform.eulerAngles.x);
        roll = camera.transform.eulerAngles.z;
    }

    public bool TryGetActorFocusPoint(string id, out Vector3 point)
    {
        if (actors.TryGetValue(id, out var actor) && actor.Visible)
        { point = actor.Position + Vector3.up * .65f; return true; }
        point = default; return false;
    }

    public void AttachPresentation(IReplayPresentationExtension extension)
    {
        if (disposed) throw new ObjectDisposedException(nameof(PlaybackSession));
        if (presentationExtension != null) throw new InvalidOperationException("A presentation extension already owns this replay.");
        presentationExtension = extension ?? throw new ArgumentNullException(nameof(extension));
    }

    public void DetachPresentation(IReplayPresentationExtension extension)
    {
        if (ReferenceEquals(presentationExtension, extension)) presentationExtension = null;
    }

    private void ReleaseFailedPresentation(Exception error)
    {
        var failed = presentationExtension; presentationExtension = null;
        try { failed?.Dispose(); }
        catch (Exception restore) { try { reportRestoreError(restore); } catch { } }
        try { reportRestoreError(error); } catch { }
    }

    private void ApplyCamera(float dt)
    {
        var mouse = Mouse.current;
        var keys = Keyboard.current;
        ApplyCursor(Application.isFocused, mouse?.rightButton.isPressed == true);
        bool looking = cursor.Looking;
        if (presentationExtension != null)
        {
            try { if (presentationExtension.ApplyCamera(dt)) return; }
            catch (Exception error) { ReleaseFailedPresentation(error); }
        }
        if (cursor.ReadLookDelta && mouse != null)
        {
            Vector2 motion = mouse.delta.ReadValue();
            yaw += motion.x * .12f;
            pitch = Mathf.Clamp(pitch - motion.y * .12f, -85, 85);
        }
        Quaternion rotation = Quaternion.Euler(pitch, yaw, FreeCamera ? roll : 0);
        float notches = 0;
        if (mouse != null && Application.isFocused && (looking ||
            new Rect(0, 0, Screen.width, Screen.height).Contains(mouse.position.ReadValue()) &&
            (!EventSystem.current || !EventSystem.current.IsPointerOverGameObject())))
        {
            // Unity's default is one unit per notch. The optional Windows native
            // range is 120. Read the setting without changing global game input.
            bool nativeWindowsRange = InputSystem.settings.scrollDeltaBehavior == InputSettings.ScrollDeltaBehavior.KeepPlatformSpecificInputRange &&
                Application.platform == RuntimePlatform.WindowsPlayer;
            notches = ReplayCameraMotion.WheelNotches(mouse.scroll.ReadValue().y, nativeWindowsRange ? 120 : 1);
        }
        if (FreeCamera)
        {
            camera.transform.rotation = rotation;
            FreeCameraSpeed = ReplayCameraMotion.FlySpeed(FreeCameraSpeed, notches);
            // Require RMB so typing/operating the timeline cannot accidentally move the camera.
            if (looking && keys != null)
            {
                float x = (keys.dKey.isPressed ? 1 : 0) - (keys.aKey.isPressed ? 1 : 0);
                float z = (keys.wKey.isPressed ? 1 : 0) - (keys.sKey.isPressed ? 1 : 0);
                float y = (keys.eKey.isPressed ? 1 : 0) - (keys.qKey.isPressed ? 1 : 0);
                // Normalize after combining view movement and world height:
                // the axes overlap when looking up/down, especially W + E.
                Vector3 direction = rotation * new Vector3(x, 0, z) + Vector3.up * y;
                var move = ReplayCameraMotion.Movement(direction.x, direction.y, direction.z, FreeCameraSpeed, dt,
                    keys.leftShiftKey.isPressed || keys.rightShiftKey.isPressed,
                    keys.leftCtrlKey.isPressed || keys.rightCtrlKey.isPressed);
                // WASD follows the view; Q/E always moves along world height.
                camera.transform.position += new Vector3(move.X, move.Y, move.Z);
            }
        }
        else if (actors.TryGetValue(FocusId, out var focused) && focused.Visible)
        {
            distance = ReplayCameraMotion.ZoomDistance(distance, notches);
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
        Restore(() => { var extension = presentationExtension; presentationExtension = null; extension?.Dispose(); });
        Restore(() => { UnityEngine.Time.timeScale = oldTimeScale; });
        foreach (var a in actors.Values) Restore(a.Hide);
        Restore(() => items?.Dispose());
        Restore(() => crates?.Dispose());
        Restore(() => ropes?.Dispose()); Restore(() => effects?.Dispose()); Restore(() => audio?.Dispose());
        Restore(() => spawned?.Dispose());
        Restore(() => balloons?.Dispose());
        Restore(() => creatures?.Dispose());
        Restore(() => interactions?.Dispose());
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
