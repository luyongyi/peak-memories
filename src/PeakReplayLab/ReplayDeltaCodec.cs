using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace PeakReplayLab;

// Schemas 10–14 are storage codecs, not simulation. All work
// happens on the save/read worker. Expanded frames remain immutable seek points.
internal static class ReplayDeltaCodec
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
        [typeof(CreatureReplayLine)] = Fields<CreatureReplayLine>(),
        [typeof(WebWrapReplayFrame)] = Fields<WebWrapReplayFrame>(),
        [typeof(WebWrapReplayPart)] = Fields<WebWrapReplayPart>(),
        [typeof(NativeRendererFrame)] = Fields<NativeRendererFrame>(),
        [typeof(NativeMaterialFrame)] = Fields<NativeMaterialFrame>(),
        [typeof(NativeShaderPropertyFrame)] = Fields<NativeShaderPropertyFrame>(),
        [typeof(NativeLightFrame)] = Fields<NativeLightFrame>(),
    };
    private static PropertyInfo[] Fields<T>() => typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0).ToArray();
    private static bool SchemaField(Type type, string field, int schema) =>
        (schema >= 14 || !(type == typeof(ActorFrame) && field == nameof(ActorFrame.RouteState))) &&
        (schema >= 13 || !(type == typeof(EnvironmentReplayFrame) && (field == nameof(EnvironmentReplayFrame.SampleTimeKnown) || field == nameof(EnvironmentReplayFrame.SampleTime)))) &&
        (schema != 10 || !(type == typeof(ActorFrame) && field == nameof(ActorFrame.HudState) ||
          type == typeof(InventoryFrame) && (field == nameof(InventoryFrame.UiFuel) || field == nameof(InventoryFrame.Cooked)))) &&
        (schema >= 12 || !(type == typeof(WorldFrame) && field == nameof(WorldFrame.Environment) ||
          type == typeof(ActorFrame) && field == nameof(ActorFrame.WebWrap) || (type == typeof(ItemFrame) || type == typeof(SpawnedReplayFrame)) && (field == "Visuals" || field == "Lights")));

    private static bool NullableSnapshot(Type type) => type == typeof(ReplayHudState) ||
        type == typeof(ActorRouteState) ||
        type == typeof(EnvironmentReplayFrame) || type == typeof(EnvironmentFogFrame) || type == typeof(WebWrapReplayFrame) || type == typeof(CreatureReplayLine);

    // Values are compared directly, never by serializing JSON. Shared capture
    // arrays exit immediately; non-joint world tracks keep lossless deltas.
    private static bool Same(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.GetType() != b.GetType()) return false;
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
            object? value = field.GetValue(current), old = previous == null ? null : field.GetValue(previous);
            // SampleTime is deliberately a non-JSON field. Handle joints before
            // the reflection comparator so stationary resamples remain visible.
            if (current is ActorFrame && field.Name == nameof(ActorFrame.JointPose))
            {
                var joints = (NodePose[])value!; var priorJoints = old as NodePose[];
                if (priorJoints == null || !ActorJointCodec.Same(joints, priorJoints))
                    result.Add(field.Name, ActorJointCodec.Encode(joints, priorJoints));
                continue;
            }
            if (previous != null && Same(value, old)) continue;
            if (schema == 10 && value is InventoryFrame[] inventory)
            {
                result.Add(field.Name, inventory.Select(item => Patch(item, null, schema: schema)).ToArray());
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
        private readonly WriteSet<BalloonReplayFrame> balloons = new(a => a.Key, nameof(BalloonReplayFrame.Key));
        private WorldFrame? world;
        public object Encode(ReplayFrame frame)
        {
            ReplayRules.ValidateVersion(frame, schema);
            var a = actors.Encode(frame.Actors, schema); var i = items.Encode(frame.Items, schema); var c = crates.Encode(frame.Crates, schema);
            var record = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Type"] = frame.Type, ["T"] = frame.T, ["ObjectsFull"] = world == null,
                ["World"] = Patch(frame.World, world, schema: schema), ["Actors"] = a.Updates, ["Items"] = i.Updates,
                ["Crates"] = c.Updates, ["RemovedActors"] = a.Removed, ["RemovedItems"] = i.Removed,
                ["RemovedCrates"] = c.Removed, ["Events"] = frame.Events,
            };
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
        public ReplayFrame Decode(JObject token)
        {
            foreach (var field in token.Properties())
                if (!recordFields.Contains(field.Name) && !presentationFields.Contains(field.Name) &&
                    !spawnedFields.Contains(field.Name) && !attachmentFields.Contains(field.Name) &&
                    !(schema >= 12 && creatureFields.Contains(field.Name))) throw Error("Unknown frame field.");
            if (token["Type"]?.Type != JTokenType.String || (string?)token["Type"] != "frame") throw Error("Invalid frame type.");
            bool full = Bool(token["ObjectsFull"]);
            if (world == null && !full) throw Error("First frame requires a complete baseline.");
            var worldPatch = token["World"] as JObject ?? throw Error("Missing world patch.");
            world = (WorldFrame)Apply(worldPatch, full ? null : world, typeof(WorldFrame), schema: schema);
            var frame = new ReplayFrame
            {
                T = (double)Value(token["T"], typeof(double))!, World = world,
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
            };
            ReplayRules.ValidateVersion(frame, schema);
            return frame;
        }
    }

    private static object Apply(JObject patch, object? old, Type type, string? identity = null, int schema = ReplayRules.CurrentSchema)
    {
        PropertyInfo[] fields = properties[type];
        foreach (var field in patch.Properties())
            if (!SchemaField(type, field.Name, schema) ||
                (!(identity != null && field.Name == "Full") && !fields.Any(p => p.Name == field.Name)))
                throw Error("Unknown delta field: " + field.Name);
        if (old != null && patch.Count == 0) return old;
        object result = Activator.CreateInstance(type)!;
        foreach (var field in fields)
        {
            // Schema 10 predates the HUD snapshot. Its absence must remain
            // unknown instead of taking a zero-filled schema 11 default.
            if (!SchemaField(type, field.Name, schema)) continue;
            bool present = patch.TryGetValue(field.Name, out var token);
            if (!present)
            {
                if (old == null) throw Error("Incomplete baseline: " + type.Name + "." + field.Name);
                field.SetValue(result, field.GetValue(old)); continue;
            }
            object? value;
            if (field.PropertyType == typeof(ObjectPose))
                value = Apply(token as JObject ?? throw Error("Invalid object pose patch."), old == null ? null : field.GetValue(old), typeof(ObjectPose), schema: schema);
            else if (NullableSnapshot(field.PropertyType))
                value = token?.Type == JTokenType.Null ? null :
                    Apply(token as JObject ?? throw Error("Invalid nullable snapshot patch."), old == null ? null : field.GetValue(old), field.PropertyType, schema: schema);
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
                    old == null ? null : (NodePose[])field.GetValue(old)!);
            else if (type == typeof(CreatureReplayLine) && field.Name == nameof(CreatureReplayLine.Points))
            {
                var points = ArrayToken(token, CreatureReplayRules.MaximumLinePoints * 3);
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
            else value = Value(token, field.PropertyType,
                (type == typeof(CrateFrame) || type == typeof(SpawnedReplayFrame)) && field.Name == "Animation", schema);
            field.SetValue(result, value);
        }
        return result;
    }

    // Explicit token/type checks prevent coercions such as null=>0, "false"=>false,
    // or incomplete nested objects silently acquiring CLR defaults. No $type/$ref.
    private static object? Value(JToken? token, Type type, bool nullable = false, int schema = ReplayRules.CurrentSchema)
    {
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
