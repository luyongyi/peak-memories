using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace PeakReplayLab;

// Final visual poses with no forces or physics resimulation. Actor/object
// positions are Unity world metres; actor joint children use parent-local TRS.
public sealed class ReplayHeader
{
    internal ReplayRegionOutcome? CoverOutcome;
    public string Type { get; set; } = "header";
    public int Schema { get; set; } = ReplayRules.CurrentSchema;
    public string Recorder { get; set; } = "PeakReplayLab/0.8.25";
    public string Scene { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public int BuildId { get; set; }
    public string GameAssembly { get; set; } = "";
    public string Route { get; set; } = "";
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ReplayRouteContext? RouteContext { get; set; }
    public ReplayRegionSummary? RegionSummary { get; set; }
    public string StartedUtc { get; set; } = "";
    public string SavedUtc { get; set; } = "";
    public double Duration { get; set; }
    public int FrameCount { get; set; }
    public string[] Participants { get; set; } = Array.Empty<string>();
    public string[] MapObjects { get; set; } = Array.Empty<string>();
    public int SampleHz { get; set; } = 60;
    public string Fidelity { get; set; } = "local-joints-p0-60-p1-30-p2-10; position-grid-0.1mm; child-quaternion-48bit; root-quaternion-and-scales-f32; independent-joint-interpolation; native-hud-stamina-15-status-segments-petrify-shield-campfire-rainbow; observed-hud-morale; inventory-ui-fuel-cooking; inventory-items-ropes-placed-attachments-vfx-game-sfx; observed-wind-direction-event-clocks-storms-lava-height-fog; spider-zombie-native-poses-30hz; spore-trap-lifetime; native-web-wrap-clips; native-renderer-material-variants-probes; native-item-and-placement-lights; no-voice-chat; presentation-only; observed-clients";
}

public sealed class ReplayFrame
{
    // Capture-only flags become compact catalog metadata on the save worker.
    // They do not change the frame codec.
    [JsonIgnore] public bool CoverPeakExtraction { get; set; }
    [JsonIgnore] public bool CoverNadirEntered { get; set; }
    public string Type { get; set; } = "frame";
    public double T { get; set; }
    // Schema 22: every non-player track shares an actual 30 Hz observation
    // clock. A repeated 60 Hz player frame holds this immutable scene sample.
    public bool SceneSampleTimeKnown { get; set; }
    public double SceneSampleTime { get; set; }
    public WorldFrame World { get; set; } = new();
    public ActorFrame[] Actors { get; set; } = Array.Empty<ActorFrame>();
    public ItemFrame[] Items { get; set; } = Array.Empty<ItemFrame>();
    public CrateFrame[] Crates { get; set; } = Array.Empty<CrateFrame>();
    public ItemEvent[] Events { get; set; } = Array.Empty<ItemEvent>();
    public RopeReplayFrame[] Ropes { get; set; } = Array.Empty<RopeReplayFrame>();
    public EffectReplayFrame[] Effects { get; set; } = Array.Empty<EffectReplayFrame>();
    public AudioReplayFrame[] Audio { get; set; } = Array.Empty<AudioReplayFrame>();
    public SpawnedReplayFrame[] Spawned { get; set; } = Array.Empty<SpawnedReplayFrame>();
    public BalloonReplayFrame[] Balloons { get; set; } = Array.Empty<BalloonReplayFrame>();
    public CreatureReplayFrame[] Creatures { get; set; } = Array.Empty<CreatureReplayFrame>();
    public InteractionReplayFrame[] Interactions { get; set; } = Array.Empty<InteractionReplayFrame>();
}

public sealed class ActorFrame
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public float[] Position { get; set; } = new float[3]; // hip, not scene-root origin
    public float[] Rotation { get; set; } = new[] { 0f, 0f, 0f, 1f };
    public float[] HipRotation { get; set; } = new[] { 0f, 0f, 0f, 1f };
    public bool Limp { get; set; }
    public float[] Look { get; set; } = new float[2];
    public float[] Movement { get; set; } = new float[2];
    public string Action { get; set; } = "idle";
    public float Stamina { get; set; }
    public float ExtraStamina { get; set; }
    public ReplayHudState? HudState { get; set; }
    public ActorRouteState? RouteState { get; set; }
    public WebWrapReplayFrame? WebWrap { get; set; }
    public Appearance Appearance { get; set; } = new();
    public InventoryFrame[] Inventory { get; set; } = Array.Empty<InventoryFrame>();
    public bool InventoryKnown { get; set; }
    public int BackpackType { get; set; } = -1;
    public bool BackpackWorn { get; set; }
    public NodePose[] JointPose { get; set; } = Array.Empty<NodePose>();
    internal ActorFrame WithJoints(NodePose[] joints)
    { var copy = (ActorFrame)MemberwiseClone(); copy.JointPose = joints; return copy; }
    internal ActorFrame WithRouteState(ActorRouteState? state)
    { var copy = (ActorFrame)MemberwiseClone(); copy.RouteState = state; return copy; }
}

public sealed class Appearance : IEquatable<Appearance>
{
    public int Skin { get; set; }
    public int Eyes { get; set; }
    public int Mouth { get; set; }
    public int Accessory { get; set; }
    public int Outfit { get; set; }
    public int Hat { get; set; }
    public int Sash { get; set; }
    public int Medal { get; set; }
    public bool Skeleton { get; set; }
    public bool Mushroom { get; set; }
    public int EyeState { get; set; } // 0 normal / 1 passed out / 2 dead
    public bool Equals(Appearance? a) => a != null && Skin == a.Skin && Eyes == a.Eyes && Mouth == a.Mouth &&
        Accessory == a.Accessory && Outfit == a.Outfit && Hat == a.Hat && Sash == a.Sash && Medal == a.Medal &&
        Skeleton == a.Skeleton && Mushroom == a.Mushroom && EyeState == a.EyeState;
    public override bool Equals(object? obj) => obj is Appearance a && Equals(a);
    public override int GetHashCode() => Skin ^ Eyes << 3 ^ Mouth << 6 ^ Outfit << 9 ^ Hat << 12 ^ Sash << 15 ^ Accessory << 18 ^ EyeState << 21 ^ (Skeleton ? 1 << 25 : 0) ^ (Mushroom ? 1 << 26 : 0) ^ Medal;
}

public sealed class WorldFrame
{
    public int Segment { get; set; }
    public bool[] ActiveMapObjects { get; set; } = Array.Empty<bool>();
    public float TimeOfDay { get; set; } = 9;
    public int Day { get; set; } = 1;
    public EnvironmentReplayFrame? Environment { get; set; }
}

public sealed class ReplayClip
{
    public ReplayHeader Header { get; set; } = new();
    public List<ReplayFrame> Frames { get; } = new();
    public string FilePath { get; set; } = "";
    public bool Complete { get; set; }
    public double Duration => Frames.Count == 0 ? 0 : Frames[Frames.Count - 1].T;

    public (ReplayFrame Left, ReplayFrame Right, float Mix) At(double time)
    {
        if (Frames.Count == 0) throw new InvalidOperationException("Replay has no frames.");
        if (!ReplayRules.Finite(time)) throw new ArgumentOutOfRangeException(nameof(time));
        time = Math.Max(Frames[0].T, Math.Min(Duration, time));
        int lo = 0, hi = Frames.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Frames[mid].T <= time) lo = mid; else hi = mid - 1;
        }
        ReplayFrame left = Frames[lo], right = Frames[Math.Min(lo + 1, Frames.Count - 1)];
        double gap = right.T - left.T;
        // Do not invent movement across a paused/stalled capture interval.
        float mix = gap > 0 && gap <= 0.5 ? (float)((time - left.T) / gap) : 0;
        return (left, right, mix);
    }
}

public static class ReplayRules
{
    public const int CurrentSchema = 25;
    public static bool SupportedSchema(int schema) => schema >= 10 && schema <= CurrentSchema;
    // Scoped to one immutable save/read operation, never trusts a previous operation.
    internal sealed class ValidationMemo
    {
        public readonly VersionValidationMemo Version = new();
        public readonly HashSet<ItemFrame[]> ItemArrays = new();
        public readonly HashSet<CrateFrame[]> CrateArrays = new();
        public readonly HashSet<ItemFrame> Items = new();
        public readonly HashSet<CrateFrame> Crates = new();
        public readonly ReplayTrackValidation<RopeReplayFrame> Ropes = RopeValidation();
        public readonly ReplayTrackValidation<EffectReplayFrame> Effects = EffectValidation();
        public readonly ConditionalWeakTable<EffectReplayFrame[], EffectObservationBound> EffectObservations = new();
        public readonly ReplayTrackValidation<AudioReplayFrame> Audio = AudioValidation();
        public readonly ReplayTrackValidation<SpawnedReplayFrame> Spawned = SpawnedValidation();
        public readonly ReplayTrackValidation<BalloonReplayFrame> Balloons = BalloonValidation();
        public readonly ReplayTrackValidation<CreatureReplayFrame> Creatures = CreatureValidation();
        public readonly ReplayTrackValidation<InteractionReplayFrame> Interactions = InteractionValidation();
    }
    // Immutable arrays are shared across frames. Older schemas only need each
    // track/appearance checked once within this save/read operation.
    internal sealed class VersionValidationMemo
    {
        public readonly HashSet<object> Tracks = new();
        public readonly HashSet<NativeRendererFrame[]> Visuals = new();
    }
    // Capture data is immutable after insertion. Weak keys avoid retaining evicted
    // world snapshots; disk reads/saves deliberately do not trust this cache.
    private static readonly ConditionalWeakTable<ItemFrame, object> capturedItems = new();
    private static readonly ConditionalWeakTable<CrateFrame, object> capturedCrates = new();
    private static readonly ConditionalWeakTable<ItemFrame[], object> capturedItemArrays = new();
    private static readonly ConditionalWeakTable<CrateFrame[], object> capturedCrateArrays = new();
    private static readonly object validated = new();
    private static readonly ReplayTrackValidation<RopeReplayFrame> capturedRopes = RopeValidation();
    private static readonly ReplayTrackValidation<EffectReplayFrame> capturedEffects = EffectValidation();
    internal sealed class EffectObservationBound { internal double Latest; }
    private static readonly ConditionalWeakTable<EffectReplayFrame[], EffectObservationBound> capturedEffectObservations = new();
    private static readonly ConditionalWeakTable<EffectReplayFrame[], EffectObservationBound>.CreateValueCallback effectObservationBound =
        values => new EffectObservationBound { Latest = LatestEffectObservation(values) };
    private static readonly ReplayTrackValidation<AudioReplayFrame> capturedAudio = AudioValidation();
    private static readonly ReplayTrackValidation<SpawnedReplayFrame> capturedSpawned = SpawnedValidation();
    private static readonly ReplayTrackValidation<BalloonReplayFrame> capturedBalloons = BalloonValidation();
    private static readonly ReplayTrackValidation<CreatureReplayFrame> capturedCreatures = CreatureValidation();
    private static readonly ReplayTrackValidation<InteractionReplayFrame> capturedInteractions = InteractionValidation();
    private static ReplayTrackValidation<InteractionReplayFrame> InteractionValidation() => new(x => x.Key, MaxInteractions, InteractionReplayRules.Validate);
    private static ReplayTrackValidation<RopeReplayFrame> RopeValidation() => new(x => x.Key, MaxRopes, RopeReplayRules.Validate, RopeReplayRules.ValidateCounts);
    private static ReplayTrackValidation<EffectReplayFrame> EffectValidation() => new(x => x.Key, MaxEffects, EffectReplayRules.Validate);
    private static ReplayTrackValidation<AudioReplayFrame> AudioValidation() => new(x => x.Key, MaxAudio, AudioReplayRules.Validate);
    private static ReplayTrackValidation<SpawnedReplayFrame> SpawnedValidation() => new(x => x.Key, MaxSpawned, SpawnedReplayRules.Validate);
    private static ReplayTrackValidation<BalloonReplayFrame> BalloonValidation() => new(x => x.Key, MaxBalloons, BalloonReplayRules.Validate);
    private static ReplayTrackValidation<CreatureReplayFrame> CreatureValidation() => new(x => x.Key, MaxCreatures, CreatureReplayRules.Validate, CreatureReplayRules.ValidateCounts);
    [ThreadStatic] private static HashSet<string>? objectKeys;
    [ThreadStatic] private static HashSet<string>? nodePaths;
    public const int MaxFrames = 7201; // 120 seconds at the maximum configurable 60 Hz
    public const long MaxBytes = 512L * 1024 * 1024;
    public const int MaxLineCharacters = 8 * 1024 * 1024;
    public const int MaxItems = 1024;
    public const int MaxCrates = 1024;
    public const int MaxNodes = 256;
    public const int MaxEvents = 4096;
    public const int MaxRopes = RopeReplayRules.MaximumTotalEntities;
    public const int MaxEffects = EffectReplayRules.Maximum;
    public const int MaxAudio = AudioReplayRules.MaxVoices;
    public const int MaxSpawned = SpawnedReplayRules.MaximumEntities;
    public const int MaxBalloons = BalloonReplayRules.MaximumEntities;
    public const int MaxCreatures = CreatureReplayRules.MaximumEntities;
    public const int MaxInteractions = InteractionReplayRules.MaximumEntities;
    public static bool Finite(double n) => !double.IsInfinity(n) && !double.IsNaN(n);
    private static bool Vector(float[]? value, int size)
    {
        if (value == null || value.Length != size) return false;
        foreach (float n in value) if (!Finite(n) || Math.Abs(n) >= 10_000_000) return false;
        return true;
    }
    private static bool Rotation(float[]? value)
    {
        if (!Vector(value, 4)) return false;
        double lengthSquared = 0;
        foreach (float n in value!) lengthSquared += (double)n * n;
        return lengthSquared >= .5 && lengthSquared <= 1.5;
    }
    public static bool Compatible(ReplayHeader recorded, ReplayHeader current) =>
        SupportedSchema(recorded.Schema) && SupportedSchema(current.Schema) &&
        recorded.Scene == current.Scene && recorded.GameVersion == current.GameVersion &&
        recorded.BuildId == current.BuildId && recorded.GameAssembly == current.GameAssembly && recorded.Route == current.Route;

    public static void Validate(ReplayHeader h)
    {
        if (!SupportedSchema(h.Schema))
            throw new InvalidDataException($"此版本支持格式 10 至 {CurrentSchema}，请使用支持的录像或重新录制。");
        if (h.Type != "header" || string.IsNullOrWhiteSpace(h.Scene) || h.Scene.Length > 200 ||
            h.Route == null || h.Route.Length > 4096 || h.GameVersion == null || h.GameVersion.Length > 100 ||
            h.GameAssembly == null || h.GameAssembly.Length > 64 || h.SampleHz < 1 || h.SampleHz > 60)
            throw new InvalidDataException("Unsupported or invalid replay header.");
        if (h.MapObjects == null || h.MapObjects.Length > 128 || h.MapObjects.Any(p => string.IsNullOrEmpty(p) || p.Length > 2048) ||
            h.MapObjects.Distinct().Count() != h.MapObjects.Length || h.Participants == null ||
            h.Participants.Any(n => n == null || n.Length > 256) || !Finite(h.Duration) || h.Duration < 0 || h.Duration > 121 ||
            h.FrameCount < 0 || h.FrameCount > MaxFrames || h.StartedUtc == null || h.StartedUtc.Length > 64 || h.SavedUtc == null || h.SavedUtc.Length > 64)
            throw new InvalidDataException("Invalid replay metadata.");
        if (!ReplayRegionCovers.Valid(h.RegionSummary)) throw new InvalidDataException("Invalid replay region summary.");
        ReplayRouteRules.Validate(h.RouteContext);
    }

    public static void Validate(ReplayFrame frame, double previous) => ValidateFrame(frame, previous, false);
    public static void ValidateCapture(ReplayFrame frame, double previous) => ValidateFrame(frame, previous, true);
    internal static void ValidateStored(ReplayFrame frame, double previous, ValidationMemo memo, double maximumStoredTime = 121) =>
        ValidateFrame(frame, previous, false, memo, maximumStoredTime);

    internal static void ValidateVersion(ReplayFrame frame, int schema, VersionValidationMemo? memo = null)
    {
        if (!SupportedSchema(schema)) throw new InvalidDataException("Unsupported replay format; record a new replay.");
        if (schema < 25 && (Array.Exists(frame.Items, item => item.SampleTimeKnown || item.SampleTime != 0 || item.ClockMode != CreatureReplayRules.ActiveClock || item.SampleDiscontinuity ||
                item.IdleAnimation is { } animation && (CreatureIdleAnimationRules.IsEarlyWormClip(animation.Clip) || animation.SpinRotation != null || animation.SpinSpeed != 0)) ||
            Array.Exists(frame.Creatures, creature => creature.SampleDiscontinuity || creature.IdleAnimation is { } animation && (animation.SpinRotation != null || animation.SpinSpeed != 0))))
            throw new InvalidDataException("Independent item observation clocks require schema 25.");
        if (schema < 24 && (Array.Exists(frame.Items, item => item.IdleAnimation != null) ||
            Array.Exists(frame.Creatures, creature => creature.IdleAnimation != null && creature.IdleAnimation.Clip != CreatureIdleAnimationRules.SpiderIdle)))
            throw new InvalidDataException("Native frog and raven animation clocks require schema 24.");
        if (schema < 23)
            foreach (var effect in frame.Effects)
                if (HasParticleCheckpoint(effect.Particles) || HasParticleCheckpoint(effect.InitialState?.Particles))
                    throw new InvalidDataException("Particle playback checkpoints require schema 23.");
        if (schema < 22 && (frame.SceneSampleTimeKnown || frame.SceneSampleTime != 0))
            throw new InvalidDataException("Scene observation clocks require schema 22.");
        if (schema < 22 && Array.Exists(frame.Effects, effect => effect.InitialState != null))
            throw new InvalidDataException("Effect initial observations require schema 22.");
        if (schema < 21 && Array.Exists(frame.Creatures, creature => creature.IdleAnimation != null || creature.Kind == "ghost-ball" || creature.Kind == "player-ghost"))
            throw new InvalidDataException("Native idle animation and ghost bodies require schema 21.");
        if (schema < 20 && Array.Exists(frame.Creatures, creature => creature.SampleTimeKnown || creature.SampleTime != 0 || creature.ClockMode != CreatureReplayRules.ActiveClock))
            throw new InvalidDataException("Creature observation clocks require schema 20.");
        if (schema < 19 && Array.Exists(frame.Ropes, rope => rope.SampleTimeKnown || rope.SampleTime != 0))
            throw new InvalidDataException("Rope observation clocks require schema 19.");
        if (schema < 18)
        {
            ValidateLegacyColorTrack(frame.Items, item => item.Visuals, memo);
            ValidateLegacyColorTrack(frame.Crates, crate => crate.Visuals, memo);
            ValidateLegacyColorTrack(frame.Creatures, creature => creature.Visuals, memo);
            ValidateLegacyColorTrack(frame.Spawned, spawned => spawned.Visuals, memo);
            ValidateLegacyColorTrack(frame.Interactions, interaction => interaction.Visuals, memo);
        }
        if (schema < 15) foreach (var audio in frame.Audio) AudioReplayRules.ValidateVersion(audio, schema);
        if (schema < 12 && (frame.World.Environment != null || frame.Creatures.Length != 0 ||
            Array.Exists(frame.Actors, a => a.WebWrap != null) || Array.Exists(frame.Items, i => i.Visuals != null || i.Lights != null) ||
            Array.Exists(frame.Spawned, s => s.Visuals != null || s.Lights != null || !SpawnedReplayRules.LegacyKind(s.Kind))))
            throw new InvalidDataException("Environment, creatures and native visual snapshots require schema 12.");
        if (schema < 13 && frame.World.Environment?.SampleTimeKnown == true)
            throw new InvalidDataException("Environment observation clocks require schema 13.");
        if (schema < 15 && (frame.Interactions.Length != 0 ||
            Array.Exists(frame.Crates, c => c.Visuals != null || c.Lights != null) ||
            Array.Exists(frame.Effects, e => e.Particles != null || e.ForceField != null) ||
            Array.Exists(frame.Ropes, r => r.SceneVine || r.BreakAmount != 0) ||
            Array.Exists(frame.Creatures, c => !CreatureDirectoryRules.IsLegacyKind(c.Kind) || c.Resource != c.Kind || HasBlendShapes(c.Visuals)) ||
            Array.Exists(frame.Items, i => HasBlendShapes(i.Visuals)) || Array.Exists(frame.Spawned, s => HasBlendShapes(s.Visuals))))
            throw new InvalidDataException("Extended native capture requires schema 15.");
        if (schema < 14 && Array.Exists(frame.Actors, a => a.RouteState != null))
            throw new InvalidDataException("Actor route evidence requires schema 14.");
        if (schema == 10)
            foreach (var actor in frame.Actors)
            {
                if (actor.HudState != null) throw new InvalidDataException("Replay HUD snapshots require schema 11.");
                foreach (var item in actor.Inventory)
                    if (item.UiFuel != -1 || item.Cooked != -1) throw new InvalidDataException("Inventory HUD snapshots require schema 11.");
            }
    }

    private static bool HasParticleCheckpoint(EffectParticleFrame[]? particles) => particles != null && Array.Exists(particles, particle => particle.Checkpoint != null);
    private static bool HasBlendShapes(NativeRendererFrame[]? values) => values != null && Array.Exists(values, v => v.BlendShapes != null);

    private static void ValidateLegacyColorTrack<T>(T[] track, Func<T, NativeRendererFrame[]?> visuals, VersionValidationMemo? memo)
    {
        if (memo != null && memo.Tracks.Contains(track)) return;
        foreach (var entry in track) ValidateLegacyColors(visuals(entry), memo);
        memo?.Tracks.Add(track);
    }

    private static void ValidateLegacyColors(NativeRendererFrame[]? visuals, VersionValidationMemo? memo)
    {
        if (visuals == null || memo != null && memo.Visuals.Contains(visuals)) return;
        foreach (var renderer in visuals)
            foreach (var material in renderer.Materials)
                foreach (var property in material.Properties)
                    if (property.ColorEncoding != 0)
                        throw new InvalidDataException("Raw native HDR colors require schema 18.");
        memo?.Visuals.Add(visuals);
    }

    private static void ValidateFrame(ReplayFrame frame, double previous, bool capture, ValidationMemo? memo = null, double maximumStoredTime = 121)
    {
        if (!Finite(maximumStoredTime) || maximumStoredTime < 1 || maximumStoredTime > 4 * 60 * 60)
            throw new ArgumentOutOfRangeException(nameof(maximumStoredTime));
        if (frame.Type != "frame" || !Finite(frame.T) || frame.T < 0 || frame.T <= previous || (!capture && frame.T > maximumStoredTime) ||
            frame.Actors == null)
            throw new InvalidDataException("Invalid replay time or actor count.");
        if (!Finite(frame.SceneSampleTime) || Math.Abs(frame.SceneSampleTime) > 1_000_000 ||
            (!frame.SceneSampleTimeKnown && frame.SceneSampleTime != 0) ||
            frame.SceneSampleTimeKnown && frame.SceneSampleTime > frame.T + .000001)
            throw new InvalidDataException("Invalid scene observation clock.");
        if (frame.World == null || frame.World.Segment < 0 || frame.World.Segment > 10 ||
            !Finite(frame.World.TimeOfDay) || frame.World.TimeOfDay < 0 || frame.World.TimeOfDay > 1000 ||
            frame.World.Day < 0 || frame.World.Day > 100000 || frame.World.ActiveMapObjects == null || frame.World.ActiveMapObjects.Length > 128)
            throw new InvalidDataException("Invalid world state.");
        EnvironmentReplayRules.Validate(frame.World.Environment);
        if (frame.World.Environment is { SampleTimeKnown: true } environment && environment.SampleTime > frame.T + .000001)
            throw new InvalidDataException("Environment sample time is outside its recording clock.");
        var actorIds = objectKeys ??= new HashSet<string>(StringComparer.Ordinal); actorIds.Clear();
        for (int actorIndex = 0; actorIndex < frame.Actors.Length; actorIndex++)
        {
            ActorFrame a = frame.Actors[actorIndex];
            if (a == null || string.IsNullOrWhiteSpace(a.Id) || a.Id.Length > 256 ||
                a.Name == null || a.Name.Length > 256 || a.Action == null || a.Action.Length > 64 ||
                !Vector(a.Position, 3) || !Rotation(a.Rotation) || !Rotation(a.HipRotation) || !Vector(a.Look, 2) || !Vector(a.Movement, 2) ||
                !Finite(a.Stamina) || !Finite(a.ExtraStamina))
                throw new InvalidDataException("Invalid replay actor.");
            ReplayHudState.Validate(a.HudState);
            ReplayRouteRules.Validate(a.RouteState, frame.T);
            WebWrapReplayRules.Validate(a.WebWrap);
            if (!actorIds.Add(a.Id)) throw new InvalidDataException("Duplicate replay actor.");
            Appearance look = a.Appearance;
            if (look == null || !Cosmetic(look.Skin) || !Cosmetic(look.Eyes) || !Cosmetic(look.Mouth) || !Cosmetic(look.Accessory) ||
                !Cosmetic(look.Outfit) || !Cosmetic(look.Hat) || !Cosmetic(look.Sash) || !Cosmetic(look.Medal) ||
                look.EyeState < 0 || look.EyeState > 2) throw new InvalidDataException("Invalid appearance.");
            if (a.Inventory == null || a.Inventory.Length > 64 || a.BackpackType < -1 || a.BackpackType > 4)
                throw new InvalidDataException("Invalid inventory state.");
            ulong playerSlots = 0, bagSlots = 0; bool playerTemp = false, bagTemp = false;
            foreach (var item in a.Inventory)
            {
                if (item == null || item.Slot < 0 || (item.Slot > 63 && item.Slot != 250) || !ValidItemId(item.ItemId) ||
                    !Text(item.Instance, 256) ||
                    !ValidItemUses(item.Uses) || !Finite(item.Fuel) || item.Fuel < -1 || item.Fuel > 1_000_000 ||
                    !Finite(item.UiFuel) || (item.UiFuel != -1 && (item.UiFuel < 0 || item.UiFuel > 1)) ||
                    item.Cooked < -1 || item.Cooked > 1_000_000)
                    throw new InvalidDataException("Invalid inventory item.");
                if (item.Slot == 250)
                {
                    if (item.Backpack ? bagTemp : playerTemp) throw new InvalidDataException("Duplicate inventory slot.");
                    if (item.Backpack) bagTemp = true; else playerTemp = true;
                }
                else
                {
                    ulong bit = 1UL << item.Slot;
                    if (((item.Backpack ? bagSlots : playerSlots) & bit) != 0) throw new InvalidDataException("Duplicate inventory slot.");
                    if (item.Backpack) bagSlots |= bit; else playerSlots |= bit;
                }
            }
            ActorJointReplayRules.Validate(a.JointPose);
            foreach (var joint in a.JointPose)
                if (joint.SampleTime > frame.T + .000001 || (!capture && joint.SampleTime < -1))
                    throw new InvalidDataException("Joint sample time is outside its recording clock.");

        }
        actorIds.Clear();
        ValidateObjects(frame, capture, memo);
        if (!capture) foreach (var item in frame.Items)
            if (item.SampleTimeKnown && item.SampleTime > frame.T + .000001)
                throw new InvalidDataException("Item observation is outside its recording clock.");
        (capture ? capturedRopes : memo?.Ropes ?? RopeValidation()).Validate(frame.Ropes);
        if (!capture) foreach (var rope in frame.Ropes)
            if (rope.SampleTimeKnown && rope.SampleTime > frame.T + .000001)
                throw new InvalidDataException("Rope observation is outside its recording clock.");
        (capture ? capturedEffects : memo?.Effects ?? EffectValidation()).Validate(frame.Effects);
        var effectTimes = capture ? capturedEffectObservations : memo?.EffectObservations;
        double latestEffect = effectTimes == null ? LatestEffectObservation(frame.Effects) :
            effectTimes.GetValue(frame.Effects, effectObservationBound).Latest;
        if (latestEffect > frame.T + .000001)
            throw new InvalidDataException("Effect observation is outside its recording clock.");
        (capture ? capturedAudio : memo?.Audio ?? AudioValidation()).Validate(frame.Audio);
        (capture ? capturedSpawned : memo?.Spawned ?? SpawnedValidation()).Validate(frame.Spawned);
        (capture ? capturedBalloons : memo?.Balloons ?? BalloonValidation()).Validate(frame.Balloons);
        (capture ? capturedCreatures : memo?.Creatures ?? CreatureValidation()).Validate(frame.Creatures);
        if (!capture) foreach (var creature in frame.Creatures)
            if (creature.SampleTimeKnown && creature.SampleTime > frame.T + .000001)
                throw new InvalidDataException("Creature observation is outside its recording clock.");
        (capture ? capturedInteractions : memo?.Interactions ?? InteractionValidation()).Validate(frame.Interactions);
        if (frame.Events == null || frame.Events.Length > MaxEvents) throw new InvalidDataException("Invalid item event count.");
        long lastSequence = -1;
        double lastEventTime = -1;
        foreach (var e in frame.Events)
        {
            if (e == null || e.Sequence < 0 || e.Sequence <= lastSequence || !Finite(e.T) || e.T < 0 ||
                e.T < lastEventTime || e.T > frame.T + .001 || !Text(e.Kind, 64, true) ||
                !Text(e.ActorId, 256) || !Text(e.ItemKey, 256) || !Text(e.Name, 256) || !ValidItemId(e.ItemId))
                throw new InvalidDataException("Invalid item event.");
            lastSequence = e.Sequence; lastEventTime = e.T;
        }
    }

    private static bool Text(string? value, int maximum, bool required = false) => value != null && value.Length <= maximum && (!required || !string.IsNullOrWhiteSpace(value));
    private static double LatestEffectObservation(EffectReplayFrame[] effects)
    {
        double latest = double.NegativeInfinity;
        foreach (var effect in effects)
        {
            latest = LatestCheckpointObservation(effect.Particles, latest);
            if (effect.InitialState is not { } initial) continue;
            latest = Math.Max(latest, Math.Max(initial.ObservedAt, initial.UntilTime));
            if (initial.EndTime.HasValue) latest = Math.Max(latest, initial.EndTime.Value);
            latest = LatestCheckpointObservation(initial.Particles, latest);
        }
        return latest;
    }
    private static double LatestCheckpointObservation(EffectParticleFrame[]? particles, double latest)
    {
        if (particles != null) foreach (var particle in particles)
            if (particle.Checkpoint is { } checkpoint) latest = Math.Max(latest, checkpoint.ObservedAt);
        return latest;
    }
    private static bool Cosmetic(int value) => value >= 0 && value <= 1024;
    private static bool ValidItemId(int value) => value >= 0 && value <= ushort.MaxValue;
    // Native Item.Start copies its int totalUses without an upper cap, and
    // Action_ReduceUses decrements any positive value (Frog starts at 100M).
    // Preserve that fixed-size counter and the -1 unavailable/unlimited marker.
    private static bool ValidItemUses(int value) => value >= -1;
    public static void ValidateObjects(ReplayFrame frame) => ValidateObjects(frame, false);
    internal static void ValidateObjectsStored(ReplayFrame frame, ValidationMemo memo) => ValidateObjects(frame, false, memo);
    private static void ValidateObjects(ReplayFrame frame, bool capture, ValidationMemo? memo = null)
    {
        if (frame.Items == null || frame.Items.Length > MaxItems || frame.Crates == null || frame.Crates.Length > MaxCrates)
            throw new InvalidDataException("Invalid object count.");
        if (capture)
        {
            capturedItemArrays.GetValue(frame.Items, values => { ValidateItems(values, true); return validated; });
            capturedCrateArrays.GetValue(frame.Crates, values => { ValidateCrates(values, true); return validated; });
        }
        else
        {
            if (memo == null || memo.ItemArrays.Add(frame.Items)) ValidateItems(frame.Items, false, memo);
            if (memo == null || memo.CrateArrays.Add(frame.Crates)) ValidateCrates(frame.Crates, false, memo);
        }
    }
    private static void ValidateItems(ItemFrame[] items, bool capture, ValidationMemo? memo = null)
    {
        var keys = objectKeys ??= new HashSet<string>(StringComparer.Ordinal); keys.Clear();
        foreach (var item in items)
        {
            if (item == null || !Text(item.Key, 256, true) || !keys.Add(item.Key)) throw new InvalidDataException("Invalid replay item identity.");
            if (capture) capturedItems.GetValue(item, key => { ValidateItem(key); return validated; });
            else if (memo == null || memo.Items.Add(item)) ValidateItem(item);
        }
        keys.Clear();
    }
    private static void ValidateCrates(CrateFrame[] crates, bool capture, ValidationMemo? memo = null)
    {
        var keys = objectKeys ??= new HashSet<string>(StringComparer.Ordinal); keys.Clear();
        foreach (var crate in crates)
        {
            if (crate == null || !Text(crate.Key, 2048, true) || !keys.Add(crate.Key)) throw new InvalidDataException("Invalid replay crate identity.");
            if (capture) capturedCrates.GetValue(crate, key => { ValidateCrate(key); return validated; });
            else if (memo == null || memo.Crates.Add(crate)) ValidateCrate(crate);
        }
        keys.Clear();
    }

    private static void ValidateItem(ItemFrame item)
    {
        if (!Finite(item.SampleTime) || Math.Abs(item.SampleTime) > 1_000_000 ||
            item.ClockMode < CreatureReplayRules.ActiveClock || item.ClockMode > CreatureReplayRules.SleepingClock ||
            !item.SampleTimeKnown && (item.SampleTime != 0 || item.ClockMode != CreatureReplayRules.ActiveClock || item.SampleDiscontinuity))
            throw new InvalidDataException("Invalid item observation clock.");
        if (!ValidItemId(item.ItemId) || !Text(item.Prefab, 256) || !Text(item.Name, 256) || !Text(item.HolderId, 256) || item.State < 0 || item.State > 64 ||
            (item.RevealBackpackContents && item.State != 2) ||
            !Finite(item.Progress) || item.Progress < -1 || item.Progress > 1_000_000 ||
            !ValidItemUses(item.Uses) || !Finite(item.Fuel) || item.Fuel < -1 || item.Fuel > 1_000_000 ||
            !Finite(item.Cooked) || Math.Abs(item.Cooked) > 1_000_000 || !Vector(item.Color, 4)) throw new InvalidDataException("Invalid replay item.");
        ValidatePose(item.Pose);
        if (item.IdleAnimation != null)
        {
            CreatureIdleAnimationRules.Validate(item.IdleAnimation);
            if (!(item.ItemId == 172 && item.IdleAnimation.Clip == CreatureIdleAnimationRules.FrogIdle ||
                item.ItemId == 184 && CreatureIdleAnimationRules.IsEarlyWormClip(item.IdleAnimation.Clip)))
                throw new InvalidDataException("Native item animation requires a matching audited checkpoint.");
        }
        if (!NativeVisualAppearanceRules.Validate(item.Visuals)) throw new InvalidDataException("Invalid recorded native renderer state.");
        if (!NativeLightRules.Validate(item.Lights)) throw new InvalidDataException("Invalid recorded native light state.");
    }

    private static void ValidateCrate(CrateFrame crate)
    {
        if (!Text(crate.Kind, 256, true)) throw new InvalidDataException("Invalid replay crate.");
        ValidatePose(crate.Pose);
        if (crate.Animation != null && !CrateAnimationTimeline.Valid(crate.Animation))
            throw new InvalidDataException("Invalid crate animation.");
        if (!NativeVisualAppearanceRules.Validate(crate.Visuals) || !NativeLightRules.Validate(crate.Lights))
            throw new InvalidDataException("Invalid recorded crate appearance.");
    }

    private static void ValidatePose(ObjectPose pose)
    {
        if (pose == null || !Vector(pose.Position, 3) || !Rotation(pose.Rotation) || !Vector(pose.Scale, 3) ||
            pose.Nodes == null || pose.Nodes.Length > MaxNodes) throw new InvalidDataException("Invalid object pose.");
        var paths = nodePaths ??= new HashSet<string>(StringComparer.Ordinal); paths.Clear();
        foreach (var node in pose.Nodes)
            if (node == null || !Text(node.Path, 2048, true) || !paths.Add(node.Path) || !Vector(node.Position, 3) ||
                !Rotation(node.Rotation) || !Vector(node.Scale, 3)) throw new InvalidDataException("Invalid object node pose.");
        paths.Clear();
    }
}

public static class ReplayFiles
{
    // Never enable polymorphic deserialization for user-selected replay files.
    public static readonly JsonSerializerSettings Json = new()
    {
        TypeNameHandling = TypeNameHandling.None, MaxDepth = 24,
        FloatParseHandling = FloatParseHandling.Double,
    };

    public static ReplayClip Read(string path, System.Threading.CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        if (info.Length > ReplayRules.MaxBytes) throw new InvalidDataException("Replay exceeds 512 MiB.");
        using var reader = ReplayArchive.OpenText(path);
        var replay = Read(reader, cancellation);
        replay.FilePath = path;
        return replay;
    }

    // The full-run container supplies a bounded, independently compressed page.
    // It owns the reader and may allow a singleton page at a large capture gap;
    // ordinary standalone recordings still require at least two frames.
    public static ReplayClip Read(TextReader reader, System.Threading.CancellationToken cancellation = default,
        bool allowSingleFrame = false, long decodedByteLimit = ReplayRules.MaxBytes, double maximumStoredTime = 121)
        => ReadCore(reader, cancellation, allowSingleFrame, decodedByteLimit, maximumStoredTime, effectsOnly: false);

    internal static ReplayClip ReadEffects(TextReader reader, System.Threading.CancellationToken cancellation = default,
        bool allowSingleFrame = false, long decodedByteLimit = ReplayRules.MaxBytes, double maximumStoredTime = 121)
        => ReadCore(reader, cancellation, allowSingleFrame, decodedByteLimit, maximumStoredTime, effectsOnly: true);

    internal static ReplayClip ReadBuffered(TextReader reader, ReplayLineBufferPool buffers,
        System.Threading.CancellationToken cancellation, bool effectsOnly, long decodedByteLimit, double maximumStoredTime,
        Func<Type, int, object>? sharedSnapshots = null)
        => ReadCore(reader, cancellation, true, decodedByteLimit, maximumStoredTime, effectsOnly, buffers, sharedSnapshots);

    private static ReplayClip ReadCore(TextReader reader, System.Threading.CancellationToken cancellation,
        bool allowSingleFrame, long decodedByteLimit, double maximumStoredTime, bool effectsOnly, ReplayLineBufferPool? buffers = null,
        Func<Type, int, object>? sharedSnapshots = null)
    {
        if (reader == null) throw new ArgumentNullException(nameof(reader));
        if (decodedByteLimit < 1 || decodedByteLimit > ReplayRules.MaxBytes)
            throw new ArgumentOutOfRangeException(nameof(decodedByteLimit));
        if (!ReplayRules.Finite(maximumStoredTime) || maximumStoredTime < 1 || maximumStoredTime > 4 * 60 * 60)
            throw new ArgumentOutOfRangeException(nameof(maximumStoredTime));
        cancellation.ThrowIfCancellationRequested();
        var replay = new ReplayClip();
        using var lineBuffer = new BoundedReplayLineBuffer(buffers);
        string headerLine = ReadBoundedLine(reader, lineBuffer) ?? throw new InvalidDataException("Missing header.");
        replay.Header = ReadHeaderRecord(headerLine);
        ReplayRules.Validate(replay.Header);
        double previous = -1;
        long previousEventSequence = -1;
        double previousEventTime = -1;
        long decodedBytes = 0;
        var decodedItems = new HashSet<ItemFrame>();
        var decodedCrates = new HashSet<CrateFrame>();
        var decodedRopes = new HashSet<RopeReplayFrame>();
        var decodedEffects = new HashSet<EffectReplayFrame>();
        var decodedAudio = new HashSet<AudioReplayFrame>();
        var decodedSpawned = new HashSet<SpawnedReplayFrame>();
        var decodedBalloons = new HashSet<BalloonReplayFrame>();
        var decodedCreatures = new HashSet<CreatureReplayFrame>();
        var decodedInteractions = new HashSet<InteractionReplayFrame>();
        var validation = new ReplayRules.ValidationMemo();
        var fieldDeltas = effectsOnly ? null : new ReplayDeltaCodec.StreamReader(replay.Header.Schema, sharedSnapshots);
        var effectDeltas = effectsOnly ? new ReplayDeltaCodec.EffectReader(replay.Header.Schema) : null;
        var effectJsonBuffers = effectsOnly ? new ReplayJsonBufferPool() : null;
        var effectProjection = effectsOnly ? new ReplayEffectProjection() : null;
        while (lineBuffer.Read(reader))
        {
            cancellation.ThrowIfCancellationRequested();
            if (lineBuffer.IsWhiteSpace) continue;
            if (replay.Complete) throw new InvalidDataException("Records found after the replay footer.");
            ReplayFrame? frame;
            if (effectsOnly)
            {
                char[] input = lineBuffer.Buffer; int inputLength = lineBuffer.Length;
                // The conservative fast path verifies canonical JSON while
                // replacing discarded values. Unfamiliar syntax retains the
                // original parser and its exact validation behavior.
                if (effectProjection!.TryProject(input, inputLength))
                { input = effectProjection.Buffer; inputLength = effectProjection.Length; }
                var token = ParseEffectObject(input, inputLength, replay.Header.Schema, effectJsonBuffers!);
                frame = (string?)token["Type"] == "end" ? null : effectDeltas!.Decode(token);
            }
            else
            {
                try { frame = fieldDeltas!.DecodeRecord(lineBuffer.Buffer, lineBuffer.Length); }
                catch (OverflowException e) { throw new InvalidDataException("Delta number outside range.", e); }
            }
            if (frame == null) { replay.Complete = true; continue; }
            ReplayRules.ValidateStored(frame, previous, validation, maximumStoredTime);
            ReplayRules.ValidateVersion(frame, replay.Header.Schema, validation.Version);
            decodedBytes += RollingBuffer.EstimateFrame(frame);
            foreach (var item in frame.Items) if (decodedItems.Add(item)) decodedBytes += RollingBuffer.Estimate(item);
            foreach (var crate in frame.Crates) if (decodedCrates.Add(crate)) decodedBytes += RollingBuffer.Estimate(crate);
            foreach (var rope in frame.Ropes) if (decodedRopes.Add(rope)) decodedBytes += RopeReplayRules.Estimate(rope);
            foreach (var effect in frame.Effects) if (decodedEffects.Add(effect)) decodedBytes += EffectReplayRules.Estimate(effect);
            foreach (var sound in frame.Audio) if (decodedAudio.Add(sound)) decodedBytes += AudioReplayRules.Estimate(sound);
            foreach (var spawn in frame.Spawned) if (decodedSpawned.Add(spawn)) decodedBytes += SpawnedReplayRules.Estimate(spawn);
            foreach (var balloon in frame.Balloons) if (decodedBalloons.Add(balloon)) decodedBytes += BalloonReplayRules.Estimate(balloon);
            foreach (var creature in frame.Creatures) if (decodedCreatures.Add(creature)) decodedBytes += CreatureReplayRules.Estimate(creature);
            foreach (var interaction in frame.Interactions) if (decodedInteractions.Add(interaction)) decodedBytes += InteractionReplayRules.Estimate(interaction);
            if (decodedBytes > decodedByteLimit) throw new InvalidDataException("Decoded replay exceeds its conservative memory limit.");
            foreach (var e in frame.Events)
            {
                if (e.Sequence <= previousEventSequence || e.T < previousEventTime) throw new InvalidDataException("Item events are not ordered.");
                previousEventSequence = e.Sequence; previousEventTime = e.T;
            }
            if (frame.World.ActiveMapObjects.Length != replay.Header.MapObjects.Length) throw new InvalidDataException("Map state shape changed.");
            if (replay.Frames.Count >= ReplayRules.MaxFrames) throw new InvalidDataException("Too many replay frames.");
            replay.Frames.Add(frame);
            previous = frame.T;
        }
        if (replay.Frames.Count < (allowSingleFrame ? 1 : 2)) throw new InvalidDataException("Record at least two frames before playback.");
        if (replay.Header.FrameCount != 0 && (replay.Header.FrameCount != replay.Frames.Count || Math.Abs(replay.Header.Duration - replay.Duration) > .001))
            throw new InvalidDataException("Replay metadata does not match its frames.");
        return replay;
    }

    public static ReplayHeader ReadHeader(string path)
    {
        using var reader = ReplayArchive.OpenText(path);
        string line = ReadBoundedLine(reader) ?? throw new InvalidDataException("Missing header.");
        var h = ReadHeaderRecord(line);
        ReplayRules.Validate(h);
        return h;
    }

    private static ReplayHeader ReadHeaderRecord(string line)
    {
        var token = ParseObject(line);
        if (token["Schema"]?.Type != Newtonsoft.Json.Linq.JTokenType.Integer || !ReplayRules.SupportedSchema((int)token["Schema"]!))
            throw new InvalidDataException($"此版本支持格式 10 至 {ReplayRules.CurrentSchema}，请使用支持的录像或重新录制。");
        var h = JsonConvert.DeserializeObject<ReplayHeader>(line, Json) ?? throw new InvalidDataException("Missing header.");
        return h;
    }

    private static Newtonsoft.Json.Linq.JObject ParseEffectObject(char[] buffer, int length, int schema, ReplayJsonBufferPool buffers)
    {
        using var text = new ReplayRecordTextReader(buffer, length);
        using var json = new JsonTextReader(text) { MaxDepth = 24, DateParseHandling = DateParseHandling.None, ArrayPool = buffers };
        var settings = new Newtonsoft.Json.Linq.JsonLoadSettings
        { DuplicatePropertyNameHandling = Newtonsoft.Json.Linq.DuplicatePropertyNameHandling.Error };
        var result = new Newtonsoft.Json.Linq.JObject();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!json.Read() || json.TokenType != JsonToken.StartObject) throw new InvalidDataException("Expected a replay object.");
        while (json.Read() && json.TokenType != JsonToken.EndObject)
        {
            if (json.TokenType != JsonToken.PropertyName || json.Value is not string name || !seen.Add(name))
                throw new InvalidDataException("Invalid or duplicate replay field.");
            if (!json.Read()) throw new InvalidDataException("Incomplete replay field.");
            if (name == "World")
            {
                if (json.TokenType != JsonToken.StartObject) throw new InvalidDataException("Missing world patch.");
                var world = new Newtonsoft.Json.Linq.JObject(); var worldNames = new HashSet<string>(StringComparer.Ordinal);
                while (json.Read() && json.TokenType != JsonToken.EndObject)
                {
                    if (json.TokenType != JsonToken.PropertyName || json.Value is not string field || !worldNames.Add(field) ||
                        !ReplayDeltaCodec.EffectReader.AllowsWorldField(field, schema)) throw new InvalidDataException("Invalid or duplicate world field.");
                    if (!json.Read()) throw new InvalidDataException("Incomplete world field.");
                    if (field is nameof(WorldFrame.ActiveMapObjects) or nameof(WorldFrame.Segment)) world.Add(field, Newtonsoft.Json.Linq.JToken.ReadFrom(json, settings));
                    else json.Skip();
                }
                if (json.TokenType != JsonToken.EndObject) throw new InvalidDataException("Incomplete world patch.");
                result.Add(name, world);
            }
            else if (name is "Type" or "T" or "SceneSampleTimeKnown" or "SceneSampleTime" or "ObjectsFull" or "Effects" or "RemovedEffects" or "EffectOrder")
                result.Add(name, Newtonsoft.Json.Linq.JToken.ReadFrom(json, settings));
            else
            {
                // Preserve unknown top-level identities for the ordinary frame
                // rejection, but do not construct actor/item/geometry JTokens.
                if (!ReplayDeltaCodec.Reader.AllowsField(name, schema)) result.Add(name, Newtonsoft.Json.Linq.JValue.CreateNull());
                json.Skip();
            }
        }
        if (json.TokenType != JsonToken.EndObject) throw new InvalidDataException("Incomplete replay object.");
        if (json.Read()) throw new InvalidDataException("Multiple JSON values in one record.");
        return result;
    }

    private static Newtonsoft.Json.Linq.JObject ParseObject(string line)
    {
        using var text = new StringReader(line);
        using var json = new JsonTextReader(text) { MaxDepth = 24, DateParseHandling = DateParseHandling.None };
        var token = Newtonsoft.Json.Linq.JObject.Load(json, new Newtonsoft.Json.Linq.JsonLoadSettings
        { DuplicatePropertyNameHandling = Newtonsoft.Json.Linq.DuplicatePropertyNameHandling.Error });
        if (json.Read()) throw new InvalidDataException("Multiple JSON values in one record.");
        return token;
    }

    private static string? ReadBoundedLine(TextReader reader, BoundedReplayLineBuffer? reusable = null)
    {
        var line = reusable ?? new BoundedReplayLineBuffer();
        return line.Read(reader) ? line.Text() : null;
    }
    private sealed class BoundedReplayLineBuffer : IDisposable
    {
        private readonly ReplayLineBufferPool? pool;
        internal char[] Buffer { get; private set; }
        internal int Length { get; private set; }
        internal BoundedReplayLineBuffer(ReplayLineBufferPool? pool = null)
        { this.pool = pool; Buffer = pool?.Rent() ?? new char[4096]; }
        internal bool IsWhiteSpace
        {
            get { for (int i = 0; i < Length; i++) if (!char.IsWhiteSpace(Buffer[i])) return false; return true; }
        }
        internal bool Read(TextReader reader)
        {
            Length = 0; int next;
            while ((next = reader.Read()) != -1 && next != '\n')
            {
                if (Length >= ReplayRules.MaxLineCharacters) throw new InvalidDataException("Replay record exceeds 8 MiB.");
                if (Length == Buffer.Length)
                {
                    var grown = new char[Math.Min(ReplayRules.MaxLineCharacters, Buffer.Length * 2)];
                    Array.Copy(Buffer, grown, Length); Buffer = grown;
                }
                Buffer[Length++] = (char)next;
            }
            if (next == -1 && Length == 0) return false;
            while (Length > 0 && Buffer[Length - 1] == '\r') Length--;
            if (System.Text.Encoding.UTF8.GetByteCount(Buffer, 0, Length) > ReplayRules.MaxLineCharacters)
                throw new InvalidDataException("Replay record exceeds 8 MiB UTF-8.");
            return true;
        }
        internal string Text() => new(Buffer, 0, Length);
        public void Dispose()
        {
            var released = Buffer; Buffer = Array.Empty<char>(); Length = 0;
            if (released.Length != 0) pool?.Return(released);
        }
    }
}

// A recording owns one reusable text buffer, leased exclusively to a page read.
// Large baseline lines otherwise grow 4 Ki -> 8 Mi characters afresh on every
// page (about 32 MiB of temporary arrays). Keep at most one on-demand buffer,
// <=16 MiB, without retaining parsed frames or reusing delta/validation state.
// Concurrent reads get separate storage; cancellation/errors return in finally.
internal sealed class ReplayLineBufferPool : IDisposable
{
    private readonly object gate = new();
    private char[]? spare;
    private bool disposed;
    internal char[] Rent()
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(ReplayLineBufferPool));
            var result = spare; spare = null;
            return result ?? new char[4096];
        }
    }
    internal void Return(char[] buffer)
    {
        if (buffer.Length < 4096 || buffer.Length > ReplayRules.MaxLineCharacters) return;
        lock (gate)
            if (!disposed && (spare == null || spare.Length < buffer.Length)) spare = buffer;
    }
    public void Dispose()
    { lock (gate) { disposed = true; spare = null; } }
}

// Both full and projected readers consume the same bounded line storage.
// A reader never outlives its record; expanded models own their strings/arrays.
internal sealed class ReplayRecordTextReader : TextReader
{
    private readonly char[] buffer;
    private readonly int length;
    private int position;
    internal ReplayRecordTextReader(char[] buffer, int length)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (length < 0 || length > buffer.Length) throw new ArgumentOutOfRangeException(nameof(length));
        this.buffer = buffer; this.length = length;
    }
    public override int Read() => position < length ? buffer[position++] : -1;
    public override int Peek() => position < length ? buffer[position] : -1;
    public override int Read(char[] target, int index, int count)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (index < 0 || count < 0 || index > target.Length - count) throw new ArgumentOutOfRangeException(nameof(index));
        int available = Math.Min(count, length - position);
        Array.Copy(buffer, position, target, index, available); position += available; return available;
    }
}

// JsonTextReader also grows private character buffers for long strings (joint
// blobs in particular). Reuse scratch within one page reader, never globally.
// At most four arrays of 128 Ki characters survive a record: <= 1 MiB total.
internal sealed class ReplayJsonBufferPool : Newtonsoft.Json.IArrayPool<char>
{
    internal const int Capacity = 4, MaximumRetainedLength = 128 * 1024;
    private readonly char[]?[] slots = new char[Capacity][];
    public char[] Rent(int minimumLength)
    {
        int best = -1;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] is char[] candidate && candidate.Length >= minimumLength &&
                (best < 0 || candidate.Length < slots[best]!.Length)) best = i;
        if (best < 0) return new char[minimumLength];
        var result = slots[best]!; slots[best] = null; return result;
    }
    public void Return(char[]? array)
    {
        if (array == null || array.Length > MaximumRetainedLength) return;
        for (int i = 0; i < slots.Length; i++) if (ReferenceEquals(slots[i], array)) return;
        int smallest = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] == null) { slots[i] = array; return; }
            if (slots[i]!.Length < slots[smallest]!.Length) smallest = i;
        }
        if (array.Length > slots[smallest]!.Length) slots[smallest] = array;
    }
}
