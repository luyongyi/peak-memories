using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace PeakReplayLab;

// Schemas 10–25 are storage codecs, not simulation. All work
// happens on the save/read worker. Expanded frames remain immutable seek points.
internal static partial class ReplayDeltaCodec
{
    private static readonly Dictionary<Type, PropertyInfo[]> properties = new()
    {
        [typeof(ActorFrame)] = Fields<ActorFrame>(), [typeof(ItemFrame)] = Fields<ItemFrame>(),
        [typeof(CrateFrame)] = Fields<CrateFrame>(), [typeof(WorldFrame)] = Fields<WorldFrame>(),
        [typeof(ObjectPose)] = Fields<ObjectPose>(), [typeof(NodePose)] = Fields<NodePose>(),
        [typeof(Appearance)] = Fields<Appearance>(), [typeof(InventoryFrame)] = Fields<InventoryFrame>(),
        [typeof(CrateAnimationFrame)] = Fields<CrateAnimationFrame>(), [typeof(ItemEvent)] = Fields<ItemEvent>(),
        [typeof(RopeReplayFrame)] = Fields<RopeReplayFrame>(), [typeof(RopeReplayPoint)] = Fields<RopeReplayPoint>(),
        [typeof(EffectReplayFrame)] = Fields<EffectReplayFrame>(), [typeof(AudioReplayFrame)] = Fields<AudioReplayFrame>(),
        [typeof(SpawnedReplayFrame)] = Fields<SpawnedReplayFrame>(),
        [typeof(BalloonReplayFrame)] = Fields<BalloonReplayFrame>(),
        [typeof(ReplayHudState)] = Fields<ReplayHudState>(),
        [typeof(ActorRouteState)] = Fields<ActorRouteState>(),
        [typeof(EnvironmentReplayFrame)] = Fields<EnvironmentReplayFrame>(),
        [typeof(EnvironmentWindFrame)] = Fields<EnvironmentWindFrame>(),
        [typeof(EnvironmentStormFrame)] = Fields<EnvironmentStormFrame>(),
        [typeof(EnvironmentLavaFrame)] = Fields<EnvironmentLavaFrame>(),
        [typeof(EnvironmentFogFrame)] = Fields<EnvironmentFogFrame>(),
        [typeof(CreatureReplayFrame)] = Fields<CreatureReplayFrame>(),
        [typeof(CreatureIdleAnimation)] = Fields<CreatureIdleAnimation>(),
        [typeof(CreatureReplayLine)] = Fields<CreatureReplayLine>(),
        [typeof(WebWrapReplayFrame)] = Fields<WebWrapReplayFrame>(),
        [typeof(WebWrapReplayPart)] = Fields<WebWrapReplayPart>(),
        [typeof(NativeRendererFrame)] = Fields<NativeRendererFrame>(),
        [typeof(NativeMaterialFrame)] = Fields<NativeMaterialFrame>(),
        [typeof(NativeShaderPropertyFrame)] = Fields<NativeShaderPropertyFrame>(),
        [typeof(NativeLightFrame)] = Fields<NativeLightFrame>(),
        [typeof(EffectParticleFrame)] = Fields<EffectParticleFrame>(),
        [typeof(EffectParticleCheckpoint)] = Fields<EffectParticleCheckpoint>(),
        [typeof(EffectForceFieldFrame)] = Fields<EffectForceFieldFrame>(),
        [typeof(EffectReplayInitialState)] = Fields<EffectReplayInitialState>(),
        [typeof(InteractionReplayFrame)] = Fields<InteractionReplayFrame>(),
        [typeof(InteractionLineFrame)] = Fields<InteractionLineFrame>(),
        [typeof(InteractionTextFrame)] = Fields<InteractionTextFrame>(),
    };
    private static readonly Dictionary<Type, HashSet<string>> propertyNames = properties.ToDictionary(
        pair => pair.Key, pair => new HashSet<string>(pair.Value.Select(p => p.Name), StringComparer.Ordinal));
    private static PropertyInfo[] Fields<T>() => typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0).ToArray();
    private static bool SchemaField(Type type, string field, int schema) =>
        (schema >= 25 || !((type == typeof(CreatureReplayFrame) || type == typeof(ItemFrame)) && field == nameof(CreatureReplayFrame.SampleDiscontinuity))) &&
        (schema >= 25 || !(type == typeof(CreatureIdleAnimation) && (field == nameof(CreatureIdleAnimation.SpinRotation) || field == nameof(CreatureIdleAnimation.SpinSpeed)))) &&
        (schema >= 25 || !(type == typeof(ItemFrame) && (field == nameof(ItemFrame.SampleTimeKnown) || field == nameof(ItemFrame.SampleTime) || field == nameof(ItemFrame.ClockMode)))) &&
        (schema >= 24 || !(type == typeof(ItemFrame) && field == nameof(ItemFrame.IdleAnimation))) &&
        (schema >= 23 || !(type == typeof(EffectParticleFrame) && field == nameof(EffectParticleFrame.Checkpoint))) &&
        (schema >= 22 || !(type == typeof(EffectReplayFrame) && field == nameof(EffectReplayFrame.InitialState))) &&
        (schema >= 21 || !(type == typeof(CreatureReplayFrame) && field == nameof(CreatureReplayFrame.IdleAnimation))) &&
        (schema >= 20 || !(type == typeof(CreatureReplayFrame) && (field == nameof(CreatureReplayFrame.SampleTimeKnown) || field == nameof(CreatureReplayFrame.SampleTime) || field == nameof(CreatureReplayFrame.ClockMode)))) &&
        (schema >= 19 || !(type == typeof(RopeReplayFrame) && (field == nameof(RopeReplayFrame.SampleTimeKnown) || field == nameof(RopeReplayFrame.SampleTime)))) &&
        (schema >= 18 || !(type == typeof(NativeShaderPropertyFrame) && field == nameof(NativeShaderPropertyFrame.ColorEncoding))) &&
        (schema >= 15 || !(type == typeof(NativeRendererFrame) && field == nameof(NativeRendererFrame.BlendShapes) ||
          type == typeof(CrateFrame) && (field == nameof(CrateFrame.Visuals) || field == nameof(CrateFrame.Lights)) ||
          type == typeof(EffectReplayFrame) && (field == nameof(EffectReplayFrame.Particles) || field == nameof(EffectReplayFrame.ForceField)) ||
          type == typeof(RopeReplayFrame) && (field == nameof(RopeReplayFrame.SceneVine) || field == nameof(RopeReplayFrame.BreakAmount)))) &&
        (schema >= 14 || !(type == typeof(ActorFrame) && field == nameof(ActorFrame.RouteState))) &&
        (schema >= 13 || !(type == typeof(EnvironmentReplayFrame) && (field == nameof(EnvironmentReplayFrame.SampleTimeKnown) || field == nameof(EnvironmentReplayFrame.SampleTime)))) &&
        (schema != 10 || !(type == typeof(ActorFrame) && field == nameof(ActorFrame.HudState) ||
          type == typeof(InventoryFrame) && (field == nameof(InventoryFrame.UiFuel) || field == nameof(InventoryFrame.Cooked)))) &&
        (schema >= 12 || !(type == typeof(WorldFrame) && field == nameof(WorldFrame.Environment) ||
          type == typeof(ActorFrame) && field == nameof(ActorFrame.WebWrap) || (type == typeof(ItemFrame) || type == typeof(SpawnedReplayFrame)) && (field == "Visuals" || field == "Lights")));

    // Zero retains the older color representation and is deliberately omitted
    // from JSON. Every other admitted baseline field remains mandatory.
    private static bool OptionalBaselineField(Type type, string field) =>
        (type == typeof(CreatureReplayFrame) || type == typeof(ItemFrame)) && field == nameof(CreatureReplayFrame.SampleDiscontinuity) ||
        type == typeof(CreatureIdleAnimation) && (field == nameof(CreatureIdleAnimation.SpinRotation) || field == nameof(CreatureIdleAnimation.SpinSpeed)) ||
        type == typeof(ItemFrame) && (field == nameof(ItemFrame.SampleTimeKnown) || field == nameof(ItemFrame.SampleTime) || field == nameof(ItemFrame.ClockMode)) ||
        type == typeof(ItemFrame) && field == nameof(ItemFrame.IdleAnimation) ||
        type == typeof(EffectParticleFrame) && field == nameof(EffectParticleFrame.Checkpoint) ||
        type == typeof(EffectReplayFrame) && field == nameof(EffectReplayFrame.InitialState) ||
        type == typeof(EffectReplayInitialState) && (field == nameof(EffectReplayInitialState.Ended) || field == nameof(EffectReplayInitialState.EndTime)) ||
        type == typeof(CreatureReplayFrame) && field == nameof(CreatureReplayFrame.IdleAnimation) ||
        type == typeof(CreatureReplayFrame) && (field == nameof(CreatureReplayFrame.ClockMode) ||
            field == nameof(CreatureReplayFrame.SampleTimeKnown) || field == nameof(CreatureReplayFrame.SampleTime)) ||
        type == typeof(NativeShaderPropertyFrame) && field == nameof(NativeShaderPropertyFrame.ColorEncoding);

    private static void ValidateCreatureClockBaseline(bool knownSeen, bool timeSeen, bool modeSeen, bool discontinuitySeen = false)
    {
        // A completely absent clock is the legacy/static default. If supplied,
        // its knowledge and time must be complete even when the time is zero.
        if ((knownSeen || timeSeen || modeSeen || discontinuitySeen) && (!knownSeen || !timeSeen))
            throw Error("Incomplete creature observation clock.");
    }

    private static void ValidateColorEncoding(object value)
    {
        if (value is NativeShaderPropertyFrame property && property.ColorEncoding != 0 &&
            (property.ColorEncoding != 1 || property.Kind != 1))
            throw Error("Invalid native color encoding.");
    }

    private static bool NullableSnapshot(Type type) => type == typeof(ReplayHudState) ||
        type == typeof(EffectParticleCheckpoint) ||
        type == typeof(EffectReplayInitialState) ||
        type == typeof(CreatureIdleAnimation) ||
        type == typeof(EffectForceFieldFrame) || type == typeof(InteractionLineFrame) || type == typeof(InteractionTextFrame) ||
        type == typeof(ActorRouteState) ||
        type == typeof(EnvironmentReplayFrame) || type == typeof(EnvironmentFogFrame) || type == typeof(WebWrapReplayFrame) || type == typeof(CreatureReplayLine);

    private static bool NullableField(Type owner, string field) =>
        owner == typeof(CreatureIdleAnimation) && field == nameof(CreatureIdleAnimation.SpinRotation) ||
        owner == typeof(InteractionReplayFrame) && (field == nameof(InteractionReplayFrame.PostWeights) || field == nameof(InteractionReplayFrame.PostEnabled)) ||
        owner == typeof(EffectParticleFrame) && (field == nameof(EffectParticleFrame.SimulationSpace) || field == nameof(EffectParticleFrame.MeshShapeSpace)) ||
        (owner == typeof(EffectReplayFrame) || owner == typeof(EffectReplayInitialState)) && field == nameof(EffectReplayFrame.Particles) ||
        owner == typeof(NativeRendererFrame) && field == nameof(NativeRendererFrame.BlendShapes) ||
        (owner == typeof(ItemFrame) || owner == typeof(CrateFrame) || owner == typeof(CreatureReplayFrame) || owner == typeof(SpawnedReplayFrame) || owner == typeof(InteractionReplayFrame)) &&
        (field == "Visuals" || field == "Lights");

    private static int FloatArrayLimit(Type owner, string field) =>
        owner == typeof(InteractionReplayFrame) && field == nameof(InteractionReplayFrame.PostWeights) ? 2 :
        owner == typeof(NativeRendererFrame) && field == nameof(NativeRendererFrame.BlendShapes) ? NativeVisualAppearanceRules.MaximumBlendShapes :
        owner == typeof(EffectParticleFrame) && field == nameof(EffectParticleFrame.Colors) ? 8 :
        owner == typeof(EffectParticleFrame) && field == nameof(EffectParticleFrame.Parameters) ? EffectReplayRules.ParticleParameterCount :
        owner == typeof(InteractionLineFrame) && field == nameof(InteractionLineFrame.Points) ? InteractionReplayRules.MaximumLinePoints * 3 :
        owner == typeof(InteractionLineFrame) && field == nameof(InteractionLineFrame.Widths) ? InteractionReplayRules.MaximumLinePoints : 0;

    private static int ByteArrayLimit(Type owner, string field) =>
        owner == typeof(EffectParticleCheckpoint) && field == nameof(EffectParticleCheckpoint.State) ? EffectParticleCheckpointRules.MaximumStateBytes :
        owner == typeof(EffectParticleCheckpoint) && field == nameof(EffectParticleCheckpoint.Data) ? EffectParticleCheckpointRules.MaximumDataBytes : 0;
    private static byte[] Binary(string value, int maximum, bool particleData = false)
    {
        if (particleData) return EffectParticleCheckpointCodec.Decode(value);
        if (maximum <= 0 || value.Length > ((maximum + 2L) / 3) * 4) throw Error("Oversized particle checkpoint bytes.");
        try { var bytes = Convert.FromBase64String(value); return bytes.Length <= maximum ? bytes : throw Error("Oversized particle checkpoint bytes."); }
        catch (FormatException e) { throw new InvalidDataException("Invalid particle checkpoint encoding.", e); }
    }

    // Values are compared directly, never by serializing JSON. Shared capture
    // arrays exit immediately; non-joint world tracks keep lossless deltas.
    private static bool Same(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.GetType() != b.GetType()) return false;
        // Object-node deltas preserve recorded float bits, including signed zero.
        if (a is float af && b is float bf) return BitConverter.SingleToInt32Bits(af) == BitConverter.SingleToInt32Bits(bf);
        if (a is Array aa && b is Array bb)
        {
            if (aa.Length != bb.Length) return false;
            for (int i = 0; i < aa.Length; i++) if (!Same(aa.GetValue(i), bb.GetValue(i))) return false;
            return true;
        }
        if (properties.TryGetValue(a.GetType(), out var fields))
        {
            foreach (var field in fields) if (!Same(field.GetValue(a), field.GetValue(b))) return false;
            return true;
        }
        return a.Equals(b);
    }

    // Page boundaries decode a fresh full baseline, so equal observations can
    // have different array references. Held capture frames take only the fast
    // reference exits; the worker compares values only for independent copies.
    internal static bool SameScene(ReplayFrame a, ReplayFrame b) =>
        Same(a.World, b.World) && Same(a.Items, b.Items) && Same(a.Crates, b.Crates) &&
        Same(a.Ropes, b.Ropes) && Same(a.Effects, b.Effects) && Same(a.Audio, b.Audio) &&
        Same(a.Spawned, b.Spawned) && Same(a.Balloons, b.Balloons) &&
        Same(a.Creatures, b.Creatures) && Same(a.Interactions, b.Interactions);

    private static Dictionary<string, object?> Patch(object current, object? previous, string? identity = null, int schema = ReplayRules.CurrentSchema)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (identity != null)
        {
            result.Add(identity, properties[current.GetType()].First(p => p.Name == identity).GetValue(current));
            if (previous == null) result.Add("Full", true);
        }
        foreach (var field in properties[current.GetType()])
        {
            if (field.Name == identity) continue;
            if (!SchemaField(current.GetType(), field.Name, schema)) continue;
            if (previous == null && field.Name == nameof(CreatureReplayFrame.SampleDiscontinuity) &&
                (current is CreatureReplayFrame boundaryCreature && !boundaryCreature.SampleDiscontinuity ||
                 current is ItemFrame boundaryItem && !boundaryItem.SampleDiscontinuity)) continue;
            if (previous == null && current is CreatureIdleAnimation animation &&
                (field.Name == nameof(CreatureIdleAnimation.SpinRotation) && animation.SpinRotation == null ||
                 field.Name == nameof(CreatureIdleAnimation.SpinSpeed) && animation.SpinSpeed == 0)) continue;
            if (previous == null && current is ItemFrame observedItem &&
                (field.Name == nameof(ItemFrame.SampleTimeKnown) || field.Name == nameof(ItemFrame.SampleTime) || field.Name == nameof(ItemFrame.ClockMode)) &&
                !observedItem.SampleTimeKnown && observedItem.SampleTime == 0 && observedItem.ClockMode == CreatureReplayRules.ActiveClock) continue;
            if (previous == null && current is ItemFrame && field.Name == nameof(ItemFrame.ClockMode) &&
                ((ItemFrame)current).ClockMode == CreatureReplayRules.ActiveClock) continue;
            if (previous == null && current is ItemFrame idleItem && field.Name == nameof(ItemFrame.IdleAnimation) && idleItem.IdleAnimation == null) continue;
            if (previous == null && current is CreatureReplayFrame idleCreature && field.Name == nameof(CreatureReplayFrame.IdleAnimation) && idleCreature.IdleAnimation == null) continue;
            if (previous == null && current is EffectReplayFrame initialEffect && field.Name == nameof(EffectReplayFrame.InitialState) && initialEffect.InitialState == null) continue;
            if (previous == null && current is CreatureReplayFrame creature &&
                (field.Name == nameof(CreatureReplayFrame.SampleTimeKnown) || field.Name == nameof(CreatureReplayFrame.SampleTime) || field.Name == nameof(CreatureReplayFrame.ClockMode)) &&
                !creature.SampleTimeKnown && creature.SampleTime == 0 && creature.ClockMode == CreatureReplayRules.ActiveClock) continue;
            if (previous == null && current is CreatureReplayFrame && field.Name == nameof(CreatureReplayFrame.ClockMode) &&
                ((CreatureReplayFrame)current).ClockMode == CreatureReplayRules.ActiveClock) continue;
            object? value = field.GetValue(current), old = previous == null ? null : field.GetValue(previous);
            // SampleTime is deliberately a non-JSON field. Handle joints before
            // the reflection comparator so stationary resamples remain visible.
            if (current is ActorFrame && field.Name == nameof(ActorFrame.JointPose))
            {
                var joints = (NodePose[])value!; var priorJoints = old as NodePose[];
                if (priorJoints == null || !ActorJointCodec.Same(joints, priorJoints))
                    result.Add(field.Name, ActorJointCodec.Encode(joints, priorJoints, schema));
                continue;
            }
            if (previous != null && Same(value, old)) continue;
            if (current is EffectParticleCheckpoint checkpoint && field.Name == nameof(EffectParticleCheckpoint.Data))
            { result.Add(field.Name, EffectParticleCheckpointCodec.Encode(checkpoint.Data, checkpoint.Count)); continue; }
            if (schema >= 16 && current is ObjectPose && field.Name == nameof(ObjectPose.Nodes) &&
                value is NodePose[] nodes && old is NodePose[] previousNodes)
            {
                result.Add(field.Name, NodeDelta(nodes, previousNodes));
                continue;
            }
            if (schema == 10 && value is InventoryFrame[] inventory)
            {
                result.Add(field.Name, inventory.Select(item => Patch(item, null, schema: schema)).ToArray());
                continue;
            }
            if (schema < 15 && value is NativeRendererFrame[] renderers)
            {
                result.Add(field.Name, renderers.Select(renderer => Patch(renderer, null, schema: schema)).ToArray());
                continue;
            }
            result.Add(field.Name, (field.PropertyType == typeof(ObjectPose) || NullableSnapshot(field.PropertyType)) && value != null
                ? Patch(value, old, schema: schema) : value is RopeReplayPoint[] points && old is RopeReplayPoint[] prior && points.Length == prior.Length
                    ? PointDelta(points, prior, schema) : value);
        }
        return result;
    }

    // Lossless node/field deltas only; capture, physical shape and playback do not change.
    private static object PointDelta(RopeReplayPoint[] current, RopeReplayPoint[] previous, int schema)
    {
        var changes = new List<object>();
        for (int i = 0; i < current.Length; i++)
        {
            if (Same(current[i], previous[i])) continue;
            var a = current[i]; var b = previous[i];
            int mask = (!Same(a.Position, b.Position) ? 1 : 0) | (!Same(a.Rotation, b.Rotation) ? 2 : 0) |
                (!Same(a.Scale, b.Scale) ? 4 : 0) | (a.Radius != b.Radius ? 8 : 0) | (a.Height != b.Height ? 16 : 0);
            var change = new List<object> { i, mask };
            if ((mask & 1) != 0) change.Add(a.Position);
            if ((mask & 2) != 0) change.Add(a.Rotation);
            if ((mask & 4) != 0) change.Add(a.Scale);
            if ((mask & 8) != 0) change.Add(a.Radius);
            if ((mask & 16) != 0) change.Add(a.Height);
            changes.Add(change.ToArray());
        }
        return new Dictionary<string, object?> { ["Length"] = current.Length, ["Changes"] = changes.ToArray() };
    }

    private static RopeReplayPoint[] ApplyPoints(JObject patch, RopeReplayPoint[]? previous, int schema)
    {
        if (previous == null || patch.Count != 2 || patch["Length"]?.Type != JTokenType.Integer ||
            (long)Value(patch["Length"], typeof(long))! != previous.Length) throw Error("Invalid rope point delta baseline.");
        var changes = ArrayToken(patch["Changes"], RopeReplayRules.MaximumPoints);
        if (changes.Count == 0) return previous;
        var result = (RopeReplayPoint[])previous.Clone();
        var touched = new HashSet<int>();
        foreach (var value in changes)
        {
            if (value is not JArray entry || entry.Count < 3 || entry.Count > 7) throw Error("Invalid rope point delta tuple.");
            long index = (long)Value(entry[0], typeof(long))!;
            if (index < 0 || index >= previous.Length || !touched.Add((int)index)) throw Error("Invalid or duplicate rope point index.");
            int mask = (int)Value(entry[1], typeof(int))!;
            if (mask < 1 || mask > 31) throw Error("Invalid rope point field mask.");
            int expected = 2; for (int bit = 1; bit <= 16; bit <<= 1) if ((mask & bit) != 0) expected++;
            if (entry.Count != expected) throw Error("Incomplete rope point tuple.");
            var old = previous[index]; int cursor = 2;
            var position = (mask & 1) != 0 ? (float[])Value(entry[cursor++], typeof(float[]))! : old.Position;
            var rotation = (mask & 2) != 0 ? (float[])Value(entry[cursor++], typeof(float[]))! : old.Rotation;
            var scale = (mask & 4) != 0 ? (float[])Value(entry[cursor++], typeof(float[]))! : old.Scale;
            float radius = (mask & 8) != 0 ? (float)Value(entry[cursor++], typeof(float))! : old.Radius;
            float height = (mask & 16) != 0 ? (float)Value(entry[cursor++], typeof(float))! : old.Height;
            result[index] = new RopeReplayPoint(position, rotation, scale) { Radius = radius, Height = height };
        }
        return result;
    }

    private sealed class WriteSet<T> where T : class
    {
        private Dictionary<string, T> previous = new(StringComparer.Ordinal), next = new(StringComparer.Ordinal);
        private T[]? last;
        private readonly Func<T, string> key;
        private readonly string keyField;
        public WriteSet(Func<T, string> key, string keyField) { this.key = key; this.keyField = keyField; }
        public (object[] Updates, string[] Removed, string[]? Order) Encode(T[] current, int schema)
        {
            if (ReferenceEquals(last, current)) return (Array.Empty<object>(), Array.Empty<string>(), null);
            next.Clear(); var updates = new List<object>();
            foreach (var value in current)
            {
                string id = key(value); next.Add(id, value);
                previous.TryGetValue(id, out var old);
                if (ReferenceEquals(value, old)) continue;
                var patch = Patch(value, old, keyField, schema);
                if (old == null || patch.Count > 1) updates.Add(patch);
            }
            string[] removed = previous.Keys.Where(k => !next.ContainsKey(k)).ToArray();
            bool sameOrder = last != null && last.Length == current.Length;
            if (sameOrder) for (int i = 0; i < current.Length; i++)
                if (key(last![i]) != key(current[i])) { sameOrder = false; break; }
            string[]? order = sameOrder ? null : current.Select(key).ToArray();
            var spare = previous; previous = next; next = spare; last = current;
            return (updates.ToArray(), removed, order);
        }
    }

    internal sealed class Writer
    {
        private readonly int schema;
        private readonly ReplayRules.VersionValidationMemo versionMemo = new();
        public Writer(int schema = ReplayRules.CurrentSchema)
        { if (!ReplayRules.SupportedSchema(schema)) throw new ArgumentOutOfRangeException(nameof(schema)); this.schema = schema; }
        private readonly WriteSet<ActorFrame> actors = new(a => a.Id, nameof(ActorFrame.Id));
        private readonly WriteSet<ItemFrame> items = new(a => a.Key, nameof(ItemFrame.Key));
        private readonly WriteSet<CrateFrame> crates = new(a => a.Key, nameof(CrateFrame.Key));
        private readonly WriteSet<RopeReplayFrame> ropes = new(a => a.Key, nameof(RopeReplayFrame.Key));
        private readonly WriteSet<EffectReplayFrame> effects = new(a => a.Key, nameof(EffectReplayFrame.Key));
        private readonly WriteSet<AudioReplayFrame> audio = new(a => a.Key, nameof(AudioReplayFrame.Key));
        private readonly WriteSet<SpawnedReplayFrame> spawned = new(a => a.Key, nameof(SpawnedReplayFrame.Key));
        private readonly WriteSet<CreatureReplayFrame> creatures = new(a => a.Key, nameof(CreatureReplayFrame.Key));
        private readonly WriteSet<InteractionReplayFrame> interactions = new(a => a.Key, nameof(InteractionReplayFrame.Key));
        private readonly WriteSet<BalloonReplayFrame> balloons = new(a => a.Key, nameof(BalloonReplayFrame.Key));
        private WorldFrame? world;
        public object Encode(ReplayFrame frame)
        {
            ReplayRules.ValidateVersion(frame, schema, versionMemo);
            var a = actors.Encode(frame.Actors, schema); var i = items.Encode(frame.Items, schema); var c = crates.Encode(frame.Crates, schema);
            var record = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Type"] = frame.Type, ["T"] = frame.T, ["ObjectsFull"] = world == null,
                ["World"] = Patch(frame.World, world, schema: schema), ["Actors"] = a.Updates, ["Items"] = i.Updates,
                ["Crates"] = c.Updates, ["RemovedActors"] = a.Removed, ["RemovedItems"] = i.Removed,
                ["RemovedCrates"] = c.Removed, ["Events"] = frame.Events,
            };
            if (schema >= 22)
            {
                record.Add(nameof(ReplayFrame.SceneSampleTimeKnown), frame.SceneSampleTimeKnown);
                record.Add(nameof(ReplayFrame.SceneSampleTime), frame.SceneSampleTime);
            }
            if (a.Order != null) record.Add("ActorOrder", a.Order);
            if (i.Order != null) record.Add("ItemOrder", i.Order);
            if (c.Order != null) record.Add("CrateOrder", c.Order);
            {
                var r = ropes.Encode(frame.Ropes, schema); var e = effects.Encode(frame.Effects, schema); var s = audio.Encode(frame.Audio, schema);
                record.Add("Ropes", r.Updates); record.Add("RemovedRopes", r.Removed);
                record.Add("Effects", e.Updates); record.Add("RemovedEffects", e.Removed);
                record.Add("Audio", s.Updates); record.Add("RemovedAudio", s.Removed);
                if (r.Order != null) record.Add("RopeOrder", r.Order);
                if (e.Order != null) record.Add("EffectOrder", e.Order);
                if (s.Order != null) record.Add("AudioOrder", s.Order);
            }
            {
                var s = spawned.Encode(frame.Spawned, schema);
                record.Add("Spawned", s.Updates); record.Add("RemovedSpawned", s.Removed);
                if (s.Order != null) record.Add("SpawnedOrder", s.Order);
            }
            {
                var b = balloons.Encode(frame.Balloons, schema);
                record.Add("Balloons", b.Updates); record.Add("RemovedBalloons", b.Removed);
                if (b.Order != null) record.Add("BalloonOrder", b.Order);
            }
            if (schema >= 12)
            {
                var n = creatures.Encode(frame.Creatures, schema);
                record.Add("Creatures", n.Updates); record.Add("RemovedCreatures", n.Removed);
                if (n.Order != null) record.Add("CreatureOrder", n.Order);
            }
            if (schema >= 15)
            {
                var n = interactions.Encode(frame.Interactions, schema);
                record.Add("Interactions", n.Updates); record.Add("RemovedInteractions", n.Removed);
                if (n.Order != null) record.Add("InteractionOrder", n.Order);
            }
            world = frame.World; return record;
        }
    }

    private sealed class ReadSet<T> where T : class, new()
    {
        private readonly Dictionary<string, T> values = new(StringComparer.Ordinal);
        private T[] previous = Array.Empty<T>();
        private readonly Func<T, string> key;
        private readonly string keyField;
        private readonly int? maximum;
        private readonly int keyLength;
        public ReadSet(Func<T, string> key, string keyField, int? maximum, int keyLength)
        { this.key = key; this.keyField = keyField; this.maximum = maximum; this.keyLength = keyLength; }
        public T[] Decode(JToken? updatesToken, JToken? removedToken, JToken? orderToken, bool full, int schema)
        {
            JArray updates = ArrayToken(updatesToken, maximum), removed = ArrayToken(removedToken, maximum);
            if (full && removed.Count != 0) throw Error("A full baseline cannot remove entities.");
            if (full) { values.Clear(); previous = Array.Empty<T>(); }
            var touched = new HashSet<string>(StringComparer.Ordinal);
            var additions = new List<string>();
            foreach (JToken token in updates)
            {
                var patch = token as JObject ?? throw Error("An entity patch must be an object.");
                string id = Key(patch[keyField], keyLength);
                if (!touched.Add(id)) throw Error("Duplicate entity patch.");
                values.TryGetValue(id, out var old);
                bool baseline = false;
                if (patch.TryGetValue("Full", out var fullToken)) baseline = Bool(fullToken);
                if ((full || old == null) && !baseline) throw Error("Unknown entity delta or missing initial baseline.");
                if (old == null) additions.Add(id);
                values[id] = (T)Apply(patch, baseline ? null : old, typeof(T), keyField, schema);
            }
            var removedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken token in removed)
            {
                string id = Key(token, keyLength);
                if (!removedKeys.Add(id) || touched.Contains(id) || !values.Remove(id))
                    throw Error("Unknown, duplicate or conflicting entity removal.");
            }
            if (maximum.HasValue && values.Count > maximum.Value) throw Error("Expanded entity count exceeds limit.");
            if (orderToken != null)
            {
                JArray order = ArrayToken(orderToken, maximum);
                if (order.Count != values.Count) throw Error("Entity order is not a complete permutation.");
                var seen = new HashSet<string>(StringComparer.Ordinal); var result = new T[order.Count];
                for (int n = 0; n < order.Count; n++)
                {
                    string id = Key(order[n], keyLength);
                    if (!seen.Add(id) || !values.TryGetValue(id, out var value)) throw Error("Invalid entity order.");
                    result[n] = value;
                }
                return previous = result;
            }
            if (updates.Count == 0 && removed.Count == 0) return previous;
            var expanded = new List<T>(values.Count);
            foreach (var old in previous) if (values.TryGetValue(key(old), out var value)) expanded.Add(value);
            foreach (string id in additions) expanded.Add(values[id]);
            return previous = expanded.ToArray();
        }
    }

    internal sealed class Reader
    {
        private readonly int schema;
        private readonly ReplayRules.VersionValidationMemo versionMemo = new();
        public Reader(int schema = ReplayRules.CurrentSchema)
        { if (!ReplayRules.SupportedSchema(schema)) throw new ArgumentOutOfRangeException(nameof(schema)); this.schema = schema; }
        private readonly ReadSet<ActorFrame> actors = new(a => a.Id, nameof(ActorFrame.Id), null, 256);
        private readonly ReadSet<ItemFrame> items = new(a => a.Key, nameof(ItemFrame.Key), ReplayRules.MaxItems, 256);
        private readonly ReadSet<CrateFrame> crates = new(a => a.Key, nameof(CrateFrame.Key), ReplayRules.MaxCrates, 2048);
        private readonly ReadSet<RopeReplayFrame> ropes = new(a => a.Key, nameof(RopeReplayFrame.Key), ReplayRules.MaxRopes, 256);
        private readonly ReadSet<EffectReplayFrame> effects = new(a => a.Key, nameof(EffectReplayFrame.Key), ReplayRules.MaxEffects, 256);
        private readonly ReadSet<AudioReplayFrame> audio = new(a => a.Key, nameof(AudioReplayFrame.Key), ReplayRules.MaxAudio, 128);
        private readonly ReadSet<SpawnedReplayFrame> spawned = new(a => a.Key, nameof(SpawnedReplayFrame.Key), ReplayRules.MaxSpawned, 256);
        private readonly ReadSet<BalloonReplayFrame> balloons = new(a => a.Key, nameof(BalloonReplayFrame.Key), ReplayRules.MaxBalloons, 256);
        private readonly ReadSet<CreatureReplayFrame> creatures = new(a => a.Key, nameof(CreatureReplayFrame.Key), ReplayRules.MaxCreatures, 256);
        private readonly ReadSet<InteractionReplayFrame> interactions = new(a => a.Key, nameof(InteractionReplayFrame.Key), ReplayRules.MaxInteractions, 256);
        private WorldFrame? world;
        private static readonly HashSet<string> recordFields = new(StringComparer.Ordinal)
        { "Type", "T", "ObjectsFull", "World", "Actors", "Items", "Crates", "RemovedActors", "RemovedItems", "RemovedCrates", "Events", "ActorOrder", "ItemOrder", "CrateOrder" };
        private static readonly HashSet<string> presentationFields = new(StringComparer.Ordinal)
        { "Ropes", "Effects", "Audio", "RemovedRopes", "RemovedEffects", "RemovedAudio", "RopeOrder", "EffectOrder", "AudioOrder" };
        private static readonly HashSet<string> spawnedFields = new(StringComparer.Ordinal)
        { "Spawned", "RemovedSpawned", "SpawnedOrder" };
        private static readonly HashSet<string> attachmentFields = new(StringComparer.Ordinal)
        { "Balloons", "RemovedBalloons", "BalloonOrder" };
        private static readonly HashSet<string> creatureFields = new(StringComparer.Ordinal)
        { "Creatures", "RemovedCreatures", "CreatureOrder" };
        private static readonly HashSet<string> interactionFields = new(StringComparer.Ordinal) { "Interactions", "RemovedInteractions", "InteractionOrder" };
        internal static bool AllowsField(string name, int schema) => recordFields.Contains(name) || presentationFields.Contains(name) ||
            spawnedFields.Contains(name) || attachmentFields.Contains(name) || schema >= 12 && creatureFields.Contains(name) || schema >= 15 && interactionFields.Contains(name) ||
            schema >= 22 && (name == nameof(ReplayFrame.SceneSampleTimeKnown) || name == nameof(ReplayFrame.SceneSampleTime));
        public ReplayFrame Decode(JObject token)
        {
            foreach (var field in token.Properties())
                if (!AllowsField(field.Name, schema)) throw Error("Unknown frame field.");
            if (token["Type"]?.Type != JTokenType.String || (string?)token["Type"] != "frame") throw Error("Invalid frame type.");
            bool full = Bool(token["ObjectsFull"]);
            if (world == null && !full) throw Error("First frame requires a complete baseline.");
            var worldPatch = token["World"] as JObject ?? throw Error("Missing world patch.");
            world = (WorldFrame)Apply(worldPatch, full ? null : world, typeof(WorldFrame), schema: schema);
            var frame = new ReplayFrame
            {
                T = (double)Value(token["T"], typeof(double))!, World = world,
                SceneSampleTimeKnown = schema >= 22 && Bool(token[nameof(ReplayFrame.SceneSampleTimeKnown)]),
                SceneSampleTime = schema >= 22 ? (double)Value(token[nameof(ReplayFrame.SceneSampleTime)], typeof(double))! : 0,
                Actors = actors.Decode(token["Actors"], token["RemovedActors"], token["ActorOrder"], full, schema),
                Items = items.Decode(token["Items"], token["RemovedItems"], token["ItemOrder"], full, schema),
                Crates = crates.Decode(token["Crates"], token["RemovedCrates"], token["CrateOrder"], full, schema),
                Events = (ItemEvent[])Value(token["Events"], typeof(ItemEvent[]))!,
                Ropes = ropes.Decode(token["Ropes"], token["RemovedRopes"], token["RopeOrder"], full, schema),
                Effects = effects.Decode(token["Effects"], token["RemovedEffects"], token["EffectOrder"], full, schema),
                Audio = audio.Decode(token["Audio"], token["RemovedAudio"], token["AudioOrder"], full, schema),
                Spawned = spawned.Decode(token["Spawned"], token["RemovedSpawned"], token["SpawnedOrder"], full, schema),
                Balloons = balloons.Decode(token["Balloons"], token["RemovedBalloons"], token["BalloonOrder"], full, schema),
                Creatures = schema >= 12 ? creatures.Decode(token["Creatures"], token["RemovedCreatures"], token["CreatureOrder"], full, schema) : Array.Empty<CreatureReplayFrame>(),
                Interactions = schema >= 15 ? interactions.Decode(token["Interactions"], token["RemovedInteractions"], token["InteractionOrder"], full, schema) : Array.Empty<InteractionReplayFrame>(),
            };
            ReplayRules.ValidateVersion(frame, schema, versionMemo);
            return frame;
        }
    }

    // Optional particle-history reads keep the exact same delta identity/order
    // rules while never expanding unrelated scene state from a legacy page.
    internal sealed class EffectReader
    {
        private readonly int schema;
        private readonly ReadSet<EffectReplayFrame> effects = new(a => a.Key, nameof(EffectReplayFrame.Key), ReplayRules.MaxEffects, 256);
        private bool[]? mapObjects;
        private int segment;
        public EffectReader(int schema) { this.schema = schema; }
        internal static bool AllowsWorldField(string name, int schema) =>
            SchemaField(typeof(WorldFrame), name, schema) && propertyNames[typeof(WorldFrame)].Contains(name);
        public ReplayFrame Decode(JObject token)
        {
            foreach (var field in token.Properties()) if (!Reader.AllowsField(field.Name, schema)) throw Error("Unknown frame field.");
            if (token["Type"]?.Type != JTokenType.String || (string?)token["Type"] != "frame") throw Error("Invalid frame type.");
            bool full = Bool(token["ObjectsFull"]);
            if (mapObjects == null && !full) throw Error("First frame requires a complete baseline.");
            var world = token["World"] as JObject ?? throw Error("Missing world patch.");
            if (world.TryGetValue(nameof(WorldFrame.ActiveMapObjects), out var objects)) mapObjects = (bool[])Value(objects, typeof(bool[]))!;
            else if (full || mapObjects == null) throw Error("Incomplete baseline: WorldFrame.ActiveMapObjects");
            if (world.TryGetValue(nameof(WorldFrame.Segment), out var nextSegment)) segment = (int)Value(nextSegment, typeof(int))!;
            else if (full) segment = 0;
            return new ReplayFrame
            {
                T = (double)Value(token["T"], typeof(double))!, World = new WorldFrame { Segment = segment, ActiveMapObjects = mapObjects! },
                SceneSampleTimeKnown = schema >= 22 && Bool(token[nameof(ReplayFrame.SceneSampleTimeKnown)]),
                SceneSampleTime = schema >= 22 ? (double)Value(token[nameof(ReplayFrame.SceneSampleTime)], typeof(double))! : 0,
                Effects = effects.Decode(token["Effects"], token["RemovedEffects"], token["EffectOrder"], full, schema),
            };
        }
    }

    private static object Apply(JObject patch, object? old, Type type, string? identity = null, int schema = ReplayRules.CurrentSchema,
        int maximumLinePoints = CreatureReplayRules.MaximumLinePoints)
    {
        PropertyInfo[] fields = properties[type];
        var names = propertyNames[type];
        foreach (var field in patch.Properties())
            if (!SchemaField(type, field.Name, schema) ||
                (!(identity != null && field.Name == "Full") && !names.Contains(field.Name)))
                throw Error("Unknown delta field: " + field.Name);
        if (old != null && patch.Count == 0) return old;
        if (old == null && schema >= 20 && type == typeof(CreatureReplayFrame))
            ValidateCreatureClockBaseline(patch.Property(nameof(CreatureReplayFrame.SampleTimeKnown)) != null,
                patch.Property(nameof(CreatureReplayFrame.SampleTime)) != null, patch.Property(nameof(CreatureReplayFrame.ClockMode)) != null,
                patch.Property(nameof(CreatureReplayFrame.SampleDiscontinuity)) != null);
        if (old == null && schema >= 25 && type == typeof(ItemFrame))
            ValidateCreatureClockBaseline(patch.Property(nameof(ItemFrame.SampleTimeKnown)) != null,
                patch.Property(nameof(ItemFrame.SampleTime)) != null, patch.Property(nameof(ItemFrame.ClockMode)) != null,
                patch.Property(nameof(ItemFrame.SampleDiscontinuity)) != null);
        object result = Activator.CreateInstance(type)!;
        foreach (var field in fields)
        {
            // Schema 10 predates the HUD snapshot. Its absence must remain
            // unknown instead of taking a zero-filled schema 11 default.
            if (!SchemaField(type, field.Name, schema)) continue;
            bool present = patch.TryGetValue(field.Name, out var token);
            if (!present)
            {
                if (old == null && OptionalBaselineField(type, field.Name)) continue;
                if (old == null) throw Error("Incomplete baseline: " + type.Name + "." + field.Name);
                field.SetValue(result, field.GetValue(old)); continue;
            }
            object? value;
            if (NullableField(type, field.Name) && token?.Type == JTokenType.Null) value = null;
            else if (type == typeof(InteractionReplayFrame) && field.Name == nameof(InteractionReplayFrame.PostEnabled))
            {
                var array = ArrayToken(token, 2); var values = new bool[array.Count];
                for (int i = 0; i < values.Length; i++) values[i] = Bool(array[i]);
                value = values;
            }
            else if (ByteArrayLimit(type, field.Name) is int byteLimit && byteLimit > 0)
                value = token?.Type == JTokenType.Bytes
                    ? token.Value<byte[]>() is byte[] bytes && bytes.Length <= byteLimit ? (byte[])bytes.Clone() : throw Error("Oversized particle checkpoint bytes.")
                    : Binary((string)Value(token, typeof(string))!, byteLimit,
                        type == typeof(EffectParticleCheckpoint) && field.Name == nameof(EffectParticleCheckpoint.Data));
            else if (FloatArrayLimit(type, field.Name) is int arrayLimit && arrayLimit > 0)
            {
                var array = ArrayToken(token, arrayLimit); var values = new float[array.Count];
                for (int i = 0; i < values.Length; i++) values[i] = (float)Value(array[i], typeof(float))!;
                value = values;
            }
            else if (field.PropertyType == typeof(ObjectPose))
                value = Apply(token as JObject ?? throw Error("Invalid object pose patch."), old == null ? null : field.GetValue(old), typeof(ObjectPose), schema: schema);
            else if (NullableSnapshot(field.PropertyType))
            {
                // Kind can be inherited from the prior creature delta. Admit
                // the frog's native 200 points without widening spider silk.
                int lineLimit = maximumLinePoints;
                if (type == typeof(CreatureReplayFrame) && field.PropertyType == typeof(CreatureReplayLine))
                {
                    string kind = patch.TryGetValue(nameof(CreatureReplayFrame.Kind), out var kindToken)
                        ? (string)Value(kindToken, typeof(string))! : (old as CreatureReplayFrame)?.Kind ?? "";
                    lineLimit = kind == "frog-tongue" ? CreatureReplayRules.MaximumFrogLinePoints : CreatureReplayRules.MaximumLinePoints;
                }
                value = token?.Type == JTokenType.Null ? null :
                    Apply(token as JObject ?? throw Error("Invalid nullable snapshot patch."), old == null ? null : field.GetValue(old), field.PropertyType,
                        schema: schema, maximumLinePoints: lineLimit);
            }
            else if (type == typeof(ReplayHudState) && field.Name == nameof(ReplayHudState.Afflictions))
            {
                JArray statuses = ArrayToken(token, ReplayHudState.StatusCount);
                if (statuses.Count != ReplayHudState.StatusCount) throw Error("Incomplete HUD status segments.");
                var values = new float[statuses.Count];
                for (int i = 0; i < values.Length; i++) values[i] = (float)Value(statuses[i], typeof(float))!;
                value = values;
            }
            else if (type == typeof(ActorFrame) && field.Name == nameof(ActorFrame.JointPose))
                value = ActorJointCodec.Decode(token?.Type == JTokenType.String ? token.Value<string>()! : throw Error("Expected binary joint payload."),
                    old == null ? null : (NodePose[])field.GetValue(old)!, schema);
            else if (type == typeof(CreatureReplayLine) && field.Name == nameof(CreatureReplayLine.Points))
            {
                var points = ArrayToken(token, maximumLinePoints * 3);
                var values = new float[points.Count];
                for (int i = 0; i < values.Length; i++) values[i] = (float)Value(points[i], typeof(float))!;
                value = values;
            }
            else if (type == typeof(NativeLightFrame) && field.Name == nameof(NativeLightFrame.CookieSize2D))
            {
                var dimensions = ArrayToken(token, 2);
                if (dimensions.Count != 2) throw Error("Incomplete native light cookie dimensions.");
                value = new[] { (float)Value(dimensions[0], typeof(float))!, (float)Value(dimensions[1], typeof(float))! };
            }
            else if ((type == typeof(ItemFrame) || type == typeof(CreatureReplayFrame) || type == typeof(SpawnedReplayFrame)) && (field.Name == "Visuals" || field.Name == "Lights") && token?.Type == JTokenType.Null) value = null;
            else if (field.PropertyType == typeof(RopeReplayPoint[]) && token is JObject pointPatch)
                value = ApplyPoints(pointPatch, old == null ? null : (RopeReplayPoint[])field.GetValue(old)!, schema);
            else if (schema >= 16 && type == typeof(ObjectPose) && field.Name == nameof(ObjectPose.Nodes) && token is JObject nodePatch)
                value = ApplyNodes(nodePatch, old == null ? null : (NodePose[])field.GetValue(old)!);
            else value = Value(token, field.PropertyType,
                (type == typeof(CrateFrame) || type == typeof(SpawnedReplayFrame)) && field.Name == "Animation", schema);
            field.SetValue(result, value);
        }
        ValidateColorEncoding(result);
        if (result is CreatureReplayFrame creature && creature.Line != null)
        {
            // A delta may change Kind while inheriting the previous Line.
            int maximum = creature.Kind == "frog-tongue" ? CreatureReplayRules.MaximumFrogLinePoints : CreatureReplayRules.MaximumLinePoints;
            if (creature.Line.Points.Length > maximum * 3) throw Error("Native creature line exceeds its kind limit.");
        }
        return result;
    }

    // Explicit token/type checks prevent coercions such as null=>0, "false"=>false,
    // or incomplete nested objects silently acquiring CLR defaults. No $type/$ref.
    private static object? Value(JToken? token, Type type, bool nullable = false, int schema = ReplayRules.CurrentSchema)
    {
        if (type == typeof(float?)) return Value(token, typeof(float), true, schema);
        if (type == typeof(double?)) return Value(token, typeof(double), true, schema);
        if (token == null) throw Error("Missing required delta value.");
        if (token.Type == JTokenType.Null)
        { if (nullable) return null; throw Error("Null delta value is not allowed."); }
        if (type == typeof(string)) return token.Type == JTokenType.String ? (string?)token : throw Error("Expected text.");
        if (type == typeof(bool)) return Bool(token);
        if (type == typeof(int) || type == typeof(long) || type == typeof(uint))
        {
            if (token.Type != JTokenType.Integer) throw Error("Expected integer.");
            try { return type == typeof(uint) ? (object)token.Value<uint>() : type == typeof(int) ? (object)token.Value<int>() : token.Value<long>(); }
            catch (Exception e) { throw new InvalidDataException("Integer outside range.", e); }
        }
        if (type == typeof(float) || type == typeof(double))
        {
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) throw Error("Expected number.");
            try { return type == typeof(float) ? (object)token.Value<float>() : token.Value<double>(); }
            catch (Exception e) { throw new InvalidDataException("Number outside range.", e); }
        }
        if (type.IsArray)
        {
            Type element = type.GetElementType()!;
            int maximum = element == typeof(float) ? 4 : element == typeof(long) ? 64 : element == typeof(bool) ? 128 :
                element == typeof(NodePose) ? Math.Max(ReplayRules.MaxNodes, CreatureReplayRules.MaximumNodes) : element == typeof(InventoryFrame) ? 64 :
                element == typeof(EnvironmentWindFrame) ? EnvironmentReplayRules.MaximumWinds :
                element == typeof(EnvironmentLavaFrame) ? EnvironmentReplayRules.MaximumLava :
                element == typeof(EnvironmentStormFrame) ? EnvironmentReplayRules.MaximumStorms :
                element == typeof(WebWrapReplayPart) ? WebWrapReplayRules.MaximumParts :
                element == typeof(NativeRendererFrame) ? NativeVisualAppearanceRules.MaximumRenderers :
                element == typeof(NativeMaterialFrame) ? NativeVisualAppearanceRules.MaximumMaterials :
                element == typeof(NativeShaderPropertyFrame) ? NativeVisualAppearanceRules.MaximumProperties :
                element == typeof(NativeLightFrame) ? NativeLightRules.MaximumLights :
                element == typeof(EffectParticleFrame) ? 64 :
                element == typeof(RopeReplayPoint) ? RopeReplayRules.MaximumPoints :
                element == typeof(ItemEvent) ? ReplayRules.MaxEvents : throw Error("Unsupported array type.");
            JArray array = ArrayToken(token, maximum); var result = Array.CreateInstance(element, array.Count);
            for (int i = 0; i < array.Count; i++) result.SetValue(Value(array[i], element, schema: schema), i);
            return result;
        }
        if (!properties.ContainsKey(type)) throw Error("Unsupported replay data type.");
        return Apply(token as JObject ?? throw Error("Expected complete nested object."), null, type, schema: schema);
    }
    private static bool Bool(JToken? token) => token?.Type == JTokenType.Boolean ? token.Value<bool>() : throw Error("Expected boolean.");
    private static JArray ArrayToken(JToken? token, int? maximum)
    { if (token is not JArray array || maximum.HasValue && array.Count > maximum.Value) throw Error("Missing, invalid or oversized array."); return array; }
    private static string Key(JToken? token, int length)
    {
        if (token?.Type != JTokenType.String) throw Error("Missing entity identity.");
        string value = token.Value<string>()!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > length) throw Error("Invalid entity identity.");
        return value;
    }
    private static InvalidDataException Error(string message) => new("Invalid delta replay: " + message);
}
