using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Peak.Afflictions;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakReplayLab;

internal static class Capture
{
    public static Character[] Players() => Character.AllCharacters
        .Where(c => c && c.IsPlayerControlled && c.gameObject.activeInHierarchy && c.refs?.hip && c.refs.animator)
        .ToArray();

    public static void Players(List<Character> destination)
    {
        destination.Clear();
        foreach (var c in Character.AllCharacters)
        {
            if (!c || !c.IsPlayerControlled || !c.gameObject.activeInHierarchy || !c.refs?.hip || !c.refs.animator) continue;
            destination.Add(c);
        }
    }

    private sealed class ActorCache
    {
        public Appearance? Appearance;
        public double NextCosmeticRead;
        public readonly ActorJointCapture Joints = new();
        public ReplayHudState? HudState;
        public ActorRouteState? RouteState;
        public double NextRouteRead;
        public int WarpSequence;
        public bool WatchingWarp;
    }
    private static ConditionalWeakTable<Character, ActorCache> actors = new();
    // This native field is internal. Resolve its access once, so sampling uses
    // a typed delegate with no reflection, boxing, or source-character writes.
    private static readonly AccessTools.FieldRef<CharacterData, bool> invincible =
        AccessTools.FieldRefAccess<CharacterData, bool>("isInvincible");

    // Real participant identity when available; the fallback is scoped to this game's room.
    public static string Id(Character c)
    {
        var owner = c.photonView ? c.photonView.Owner : null;
        return !string.IsNullOrEmpty(owner?.UserId) ? owner!.UserId : owner != null
            ? "actor:" + owner.ActorNumber : "view:" + (c.photonView ? c.photonView.ViewID : c.GetInstanceID());
    }

    private static readonly Dictionary<int, Dictionary<int, string>> clipKeys = new();
    public static void ClearCatalog() { clipKeys.Clear(); actors = new(); }
    public static string ClipKey(AnimationClip clip, RuntimeAnimatorController controller)
    {
        if (!controller) throw new InvalidOperationException("Character has no animation controller.");
        if (!clipKeys.TryGetValue(controller.GetInstanceID(), out var keys))
        {
            keys = new Dictionary<int, string>();
            var list = controller.animationClips;
            for (int i = 0; i < list.Length; i++)
                if (list[i] && !keys.ContainsKey(list[i].GetInstanceID()))
                    // Names/metadata are stable across restarts; do not serialize runtime IDs or
                    // rely on a native clip enumeration order. VisualActor rejects ambiguous keys.
                    keys.Add(list[i].GetInstanceID(), controller.name + "/" + list[i].name + "|" +
                        list[i].length.ToString("R", CultureInfo.InvariantCulture) + "|" + list[i].frameRate.ToString("R", CultureInfo.InvariantCulture));
            clipKeys[controller.GetInstanceID()] = keys;
        }
        return keys.TryGetValue(clip.GetInstanceID(), out var key) ? key : "unresolved/" + clip.name;
    }

    public static ReplayHeader Header(int hz)
    {
        var map = UnityEngine.Object.FindFirstObjectByType<MapHandler>();
        string route = map && map.segments != null
            ? string.Join(",", map.segments.Select(s => s.biome.ToString())) : "no-map-handler";
        int build = 0;
        try { build = Steamworks.SteamApps.GetAppBuildId(); } catch { /* Non-Steam debug environment. */ }
        var header = new ReplayHeader
        {
            Scene = SceneManager.GetActiveScene().name, GameVersion = Application.version,
            BuildId = build, GameAssembly = typeof(Character).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
            Route = route, SampleHz = hz, StartedUtc = DateTime.UtcNow.ToString("O"),
        };
        if (map && map.segments != null) header.RouteContext = NativeReplayRouteCapture.Context(map, header);
        return header;
    }

    public static ActorFrame Actor(Character c, double time)
    {
        var cache = actors.GetValue(c, _ => new ActorCache());
        Vector3 p = c.refs.hip.transform.position;
        Quaternion hip = c.refs.hip.transform.rotation;
        var d = c.data;
        bool limp = d.dead || d.passedOut || d.fullyPassedOut || d.currentRagdollControll < .5f;
        Quaternion rotation = c.refs.rigCreator.transform.rotation;
        if (d.carrier && d.carrier.refs.carryPosRef) rotation = d.carrier.refs.carryPosRef.rotation;
        else if (d.isRopeClimbing && d.ropeClimbWorldNormal.sqrMagnitude > .001f)
            rotation = Quaternion.LookRotation(-d.ropeClimbWorldNormal, d.ropeClimbWorldUp);
        else if (d.isClimbing && d.climbNormal.sqrMagnitude > .001f) rotation = Quaternion.LookRotation(-d.climbNormal);
        else if (d.lookDirection_Flat.sqrMagnitude > .001f) rotation = Quaternion.LookRotation(d.lookDirection_Flat);
        InventoryFrame[] inventory = ItemCapture.Inventory(c, out bool inventoryKnown);
        int backpackType = c.player?.backpackSlot != null ? (int)c.player.backpackSlot.backpackType : -1;
        bool backpackHeld = c.refs.items && c.refs.items.currentSelectedSlot.IsSome && c.refs.items.currentSelectedSlot.Value == 3;
        return new ActorFrame
        {
            Id = Id(c), Name = c.photonView?.Owner?.NickName ?? c.name,
            Position = new[] { p.x, p.y, p.z }, Rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w },
            HipRotation = new[] { hip.x, hip.y, hip.z, hip.w }, Limp = limp,
            Look = new[] { d.lookValues.x, d.lookValues.y }, Movement = new[] { c.input.movementInput.x, c.input.movementInput.y },
            Stamina = d.currentStamina, ExtraStamina = d.extraStamina,
            HudState = HudStateOf(c, cache),
            RouteState = RouteStateOf(c, cache, time),
            WebWrap = WebWrapReplayCapture.Sample(c),
            Appearance = AppearanceOf(c),
            Inventory = inventory, InventoryKnown = inventoryKnown,
            BackpackType = backpackType, BackpackWorn = backpackType > 0 && !backpackHeld,
            JointPose = cache.Joints.Sample(c, p, time),
            Action = d.dead ? "dead (approx.)" : d.passedOut || d.fullyPassedOut ? "passed out (approx.)" : limp ? "tumbling (approx.)" :
                d.isClimbingAnything ? "climbing" : !d.isGrounded ? "airborne" : d.isSprinting ? "sprinting" :
                d.isCrouching ? "crouching" : c.input.movementInput.sqrMagnitude > .01f ? "walking" : "idle",
        };
    }

    private static ActorRouteState RouteStateOf(Character character, ActorCache cache, double time)
    {
        if (!cache.WatchingWarp)
        {
            cache.WatchingWarp = true;
            // The delegate references only this actor's weak-cache value; no
            // global character list, Unity search, I/O, or extra Update loop.
            character.WarpCompleted += _ => { if (cache.WarpSequence < int.MaxValue) cache.WarpSequence++; };
        }
        bool alive = !character.data.dead, owner = character.IsLocal, warping = character.warping;
        var stats = character.refs.stats;
        bool finished = stats && stats.won, nadir = finished && stats!.wonViaNadir;
        var old = cache.RouteState;
        bool statusChanged = old == null || old.Alive != alive || old.LocalOwner != owner || old.Warping != warping ||
            old.WarpSequence != cache.WarpSequence || old.Finished != finished || old.FinishedNadir != nadir;
        if (!statusChanged && time < cache.NextRouteRead && time >= cache.NextRouteRead - .2) return old!;
        var p = character.Center;
        var center = old != null && old.Center[0] == p.x && old.Center[1] == p.y && old.Center[2] == p.z
            ? old.Center : new[] { p.x, p.y, p.z };
        long runTime = -1;
        var run = RunManager.Instance;
        if (run && ReplayRules.Finite(run.TimeSinceRunStarted) && run.TimeSinceRunStarted >= 0)
            runTime = (long)Math.Round(run.TimeSinceRunStarted * 1000d);
        cache.NextRouteRead = time + .1;
        return cache.RouteState = new ActorRouteState
        {
            SampleTime = time, Center = center, Alive = alive, LocalOwner = owner, Warping = warping,
            WarpSequence = cache.WarpSequence, Finished = finished, FinishedNadir = nadir, RunTimeMs = runTime,
            SharedRunKey = NativeReplayRouteCapture.SharedRunKey(run),
        };
    }

    private static ReplayHudState? HudStateOf(Character character, ActorCache cache)
    {
        var afflictions = character.refs.afflictions;
        var statuses = afflictions ? afflictions.currentStatuses : null;
        if (statuses == null || statuses.Length != ReplayHudState.StatusCount)
            return cache.HudState = null;
        var data = character.data;
        float stamina = data.currentStamina, extra = data.extraStamina, maximum = character.GetMaxStamina();
        float petrify = data.petrifyAmount * .01f;
        bool shield = invincible(data), canGetHungry = afflictions!.canGetHungry;
        bool rainbow = afflictions.HasAfflictionType(Affliction.AfflictionType.InfiniteStamina, out _);
        var nativeBar = Character.observedCharacter == character && GUIManager.instance ? GUIManager.instance.bar : null;
        var moraleText = nativeBar ? nativeBar!.moraleBoostText : null;
        bool moraleKnown = moraleText;
        bool moraleBoost = moraleKnown && moraleText!.enabled && moraleText.gameObject.activeInHierarchy;
        var old = cache.HudState;
        bool sameSegments = old != null;
        if (sameSegments)
            for (int i = 0; i < statuses.Length; i++)
                if (old!.Afflictions[i] != statuses[i]) { sameSegments = false; break; }
        if (sameSegments && old!.Stamina == stamina && old.MaxStamina == maximum && old.ExtraStamina == extra &&
            old.Petrify == petrify && old.Invincible == shield && old.CanGetHungry == canGetHungry &&
            old.Rainbow == rainbow && old.MoraleKnown == moraleKnown && old.MoraleBoost == moraleBoost)
            return old;
        // Status segments are immutable after capture. Reuse them while only
        // stamina is changing; copy the small native array only on a change.
        return cache.HudState = new ReplayHudState(sameSegments ? old!.Afflictions : (float[])statuses.Clone())
        {
            Stamina = stamina, MaxStamina = maximum, ExtraStamina = extra,
            Petrify = petrify,
            Invincible = shield, CanGetHungry = canGetHungry, Rainbow = rainbow,
            MoraleKnown = moraleKnown, MoraleBoost = moraleBoost,
        };
    }

    public static Appearance AppearanceOf(Character c)
    {
        var cache = actors.GetValue(c, _ => new ActorCache());
        var old = cache.Appearance;
        var visual = c.refs.customization;
        var refs = visual.refs;
        int skin = old?.Skin ?? 0, eyes = old?.Eyes ?? 0, mouth = old?.Mouth ?? 0, accessory = old?.Accessory ?? 0,
            outfit = old?.Outfit ?? 0, hat = old?.Hat ?? 0, sash = old?.Sash ?? 0, medal = old?.Medal ?? 0;
        if (old == null || Time.unscaledTimeAsDouble >= cache.NextCosmeticRead)
        {
            var data = GameHandler.GetService<PersistentPlayerDataService>().GetPlayerData(c.photonView.Owner).customizationData;
            skin = visual.useDebugColor ? visual.debugColorIndex : data.currentSkin;
            eyes = data.currentEyes; mouth = data.currentMouth; accessory = data.currentAccessory;
            outfit = data.currentOutfit; hat = data.currentHat; sash = data.currentSash; medal = data.currentMedal;
            cache.NextCosmeticRead = Time.unscaledTimeAsDouble + .5;
        }
        bool skeleton = c.data.isSkeleton;
        bool mushroom = refs.skeletonRenderer.gameObject.activeSelf && refs.skeletonRenderer is SkinnedMeshRenderer mesh && mesh.sharedMesh == refs.mushroomManMesh;
        int eyeState = c.data.dead ? 2 : c.data.passedOut || c.data.fullyPassedOut ? 1 : 0;
        if (old != null && old.Skin == skin && old.Eyes == eyes && old.Mouth == mouth && old.Accessory == accessory &&
            old.Outfit == outfit && old.Hat == hat && old.Sash == sash && old.Medal == medal &&
            old.Skeleton == skeleton && old.Mushroom == mushroom && old.EyeState == eyeState) return old;
        return cache.Appearance = new Appearance
        {
            Skin = skin, Eyes = eyes, Mouth = mouth, Accessory = accessory, Outfit = outfit, Hat = hat, Sash = sash, Medal = medal,
            Skeleton = skeleton, Mushroom = mushroom, EyeState = eyeState,
        };
    }

}

internal sealed class RollingCapture : IDisposable
{
    private readonly List<Character> players = new();
    private readonly Transform[] mapObjects;
    private readonly ItemCapture items;
    private readonly LuggageCapture crates;
    private readonly RopeReplayCapture ropes = null!;
    private readonly EffectReplayCapture effects = null!;
    private readonly AudioReplayCapture audio = null!;
    private readonly SpawnedReplayCapture spawned = null!;
    private readonly BalloonReplayCapture balloons = null!;
    private readonly CreatureReplayCapture creatures = null!;
    private readonly ReplayRegionCapture regions = null!;
    private readonly Action<string>? warning;
    private readonly SampleClock clock;
    private bool disposed;
    private double previous = -1;
    public RollingBuffer Buffer { get; }
    public ReplayHeader Header { get; }
    public double ObservedHz => clock.ObservedRate;
    public long SkippedSamples => clock.Skipped;
    public int ItemCount { get; private set; }
    public int CrateCount { get; private set; }
    public long EventCount { get; private set; }
    public int RopeCount { get; private set; }
    public int EffectCount { get; private set; }
    public int AudioCount { get; private set; }
    public int SpawnedCount { get; private set; }
    public int BalloonCount { get; private set; }
    public int CreatureCount { get; private set; }
    public int JointCount { get; private set; }

    public RollingCapture(int hz, long budget, Action<string>? warning = null)
    {
        this.warning = warning;
        Header = Capture.Header(hz);
        mapObjects = WorldTrack.MapObjects();
        Header.MapObjects = mapObjects.Select(WorldTrack.Path).ToArray();
        ReplayRules.Validate(Header);
        // F6 is always available. The optional full-run sink receives these same
        // immutable snapshots; it does not create a second capture or cache.
        Buffer = new RollingBuffer(budget, frameLimit: ReplayRules.MaxFrames);
        clock = new SampleClock(hz);
        items = new ItemCapture(warning);
        crates = new LuggageCapture(warning);
        try
        {
            regions = new ReplayRegionCapture(Header, warning);
            ropes = new RopeReplayCapture(warning);
            spawned = new SpawnedReplayCapture(warning);
            balloons = new BalloonReplayCapture(warning);
            creatures = new CreatureReplayCapture(warning);
            effects = new EffectReplayCapture(warning);
            audio = new AudioReplayCapture(warning);
        }
        catch { Dispose(); throw; }
    }

    public void Tick(Action<ReplayFrame, long>? accepted = null)
    {
        double time = Time.timeAsDouble;
        if (disposed || !clock.ShouldCapture(time)) return;
        using var timing = ReplayPerformance.Measure(ReplayStage.Capture);
        Capture.Players(players);
        if (players.Count == 0) return;
        var frame = new ReplayFrame { T = time, Actors = new ActorFrame[players.Count] };
        using (ReplayPerformance.Measure(ReplayStage.Actors))
            for (int i = 0; i < players.Count; i++) frame.Actors[i] = Capture.Actor(players[i], time);
        JointCount = frame.Actors.Sum(actor => actor.JointPose.Length);
        using (ReplayPerformance.Measure(ReplayStage.World)) frame.World = WorldTrack.Capture(mapObjects);
        regions.Apply(frame);
        using (ReplayPerformance.Measure(ReplayStage.Items)) frame.Items = items.Capture();
        using (ReplayPerformance.Measure(ReplayStage.Crates)) frame.Crates = crates.Capture(time);
        using (ReplayPerformance.Measure(ReplayStage.Ropes)) frame.Ropes = ropes.Capture(time);
        using (ReplayPerformance.Measure(ReplayStage.Effects)) frame.Effects = effects.Capture(time);
        using (ReplayPerformance.Measure(ReplayStage.Audio)) frame.Audio = audio.Capture(time);
        using (ReplayPerformance.Measure(ReplayStage.Spawned)) frame.Spawned = spawned.Capture(time);
        using (ReplayPerformance.Measure(ReplayStage.Balloons)) frame.Balloons = balloons.Capture(time);
        using (ReplayPerformance.Measure(ReplayStage.Creatures)) frame.Creatures = creatures.Capture(time);
        frame.Events = ItemEventCapture.Drain(time);
        using (ReplayPerformance.Measure(ReplayStage.Validate)) ReplayRules.ValidateCapture(frame, previous);
        using (ReplayPerformance.Measure(ReplayStage.Buffer)) Buffer.Add(frame);
        ItemCount = frame.Items.Length; CrateCount = frame.Crates.Length; EventCount += frame.Events.Length;
        RopeCount = frame.Ropes.Length; EffectCount = frame.Effects.Length; AudioCount = frame.Audio.Length;
        SpawnedCount = frame.Spawned.Length;
        BalloonCount = frame.Balloons.Length;
        CreatureCount = frame.Creatures.Length;
        previous = time;
        // Immutable frame tee; no file access or JSON work on the game thread.
        // The rolling buffer may evict old frames independently of full-run capture.
        using (ReplayPerformance.Measure(ReplayStage.Enqueue)) accepted?.Invoke(frame, Buffer.LastFrameEstimatedBytes);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        void Release(IDisposable? value)
        { try { value?.Dispose(); } catch (Exception e) { try { warning?.Invoke("回放采集清理：" + e.Message); } catch { } } }
        Release(items); Release(crates); Release(ropes); Release(effects); Release(audio);
        Release(spawned);
        Release(balloons);
        Release(creatures);
        Release(regions);
        // The pure rolling buffer remains available for F6 after returning to the menu.
    }
}
