using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PeakReplayLab;

internal static partial class ReplayDeltaCodec
{
    // Reading a whole JObject/JProperty/JValue tree for every captured frame
    // creates several times the expanded page's memory in temporary objects.
    // This reader keeps only typed patch values, then shares unchanged immutable
    // state with the preceding frame. The JObject reader remains an independent
    // codec implementation for compatibility and equivalence checks.
    internal sealed partial class StreamReader
    {
        private readonly int schema;
        private readonly ReplayRules.VersionValidationMemo versionMemo = new();
        private readonly Func<Type, int, object>? sharedSnapshots;
        private const int MaximumRetainedPatches = 2048;
        private readonly Stack<ObjectPatch> patchPool = new();
        private readonly ReplayJsonBufferPool jsonBuffers = new();
        private readonly ReplayEffectProjection canonicalRecords = new();
        private readonly Stack<List<object?>> arrayScratch = new();
        private readonly Stack<List<float>> floatScratch = new();
        private readonly float[]?[] simpleVectors = new float[30][];
        // Direct-mapped tables bound both memory and lookup time. A collision
        // replaces a reference; previously decoded immutable values stay intact.
        private readonly float[]?[] shortVectors = new float[8160][];
        private readonly NodePose?[] sharedNodes = new NodePose[4096];
        private const int MaximumSharedStrings = 4096, MaximumSharedCharacters = 1024 * 1024;
        private readonly Dictionary<string, string> sharedStrings = new(StringComparer.Ordinal);
        private readonly Dictionary<int, List<string>> sharedStringBuckets = new();
        private int sharedCharacters;
        private WorldFrame? world;
        private readonly StreamSet<ActorFrame> actors = new(a => a.Id, nameof(ActorFrame.Id), null, 256);
        private readonly StreamSet<ItemFrame> items = new(a => a.Key, nameof(ItemFrame.Key), ReplayRules.MaxItems, 256);
        private readonly StreamSet<CrateFrame> crates = new(a => a.Key, nameof(CrateFrame.Key), ReplayRules.MaxCrates, 2048);
        private readonly StreamSet<RopeReplayFrame> ropes = new(a => a.Key, nameof(RopeReplayFrame.Key), ReplayRules.MaxRopes, 256);
        private readonly StreamSet<EffectReplayFrame> effects = new(a => a.Key, nameof(EffectReplayFrame.Key), ReplayRules.MaxEffects, 256);
        private readonly StreamSet<AudioReplayFrame> audio = new(a => a.Key, nameof(AudioReplayFrame.Key), ReplayRules.MaxAudio, 128);
        private readonly StreamSet<SpawnedReplayFrame> spawned = new(a => a.Key, nameof(SpawnedReplayFrame.Key), ReplayRules.MaxSpawned, 256);
        private readonly StreamSet<BalloonReplayFrame> balloons = new(a => a.Key, nameof(BalloonReplayFrame.Key), ReplayRules.MaxBalloons, 256);
        private readonly StreamSet<CreatureReplayFrame> creatures = new(a => a.Key, nameof(CreatureReplayFrame.Key), ReplayRules.MaxCreatures, 256);
        private readonly StreamSet<InteractionReplayFrame> interactions = new(a => a.Key, nameof(InteractionReplayFrame.Key), ReplayRules.MaxInteractions, 256);
        private static readonly string[] frameNames = {
            "Type", "T", "ObjectsFull", "World", "Actors", "Items", "Crates", "Events",
            "Ropes", "Effects", "Audio", "Spawned", "Balloons", "Creatures",
            "RemovedActors", "RemovedItems", "RemovedCrates", "RemovedRopes", "RemovedEffects", "RemovedAudio",
            "RemovedSpawned", "RemovedBalloons", "RemovedCreatures", "ActorOrder", "ItemOrder", "CrateOrder",
            "RopeOrder", "EffectOrder", "AudioOrder", "SpawnedOrder", "BalloonOrder", "CreatureOrder",
            "Interactions", "RemovedInteractions", "InteractionOrder", "SceneSampleTimeKnown", "SceneSampleTime"
        };
        private static readonly Dictionary<string, int> frameIndices = Indices(frameNames);
        private static readonly Dictionary<Type, Dictionary<string, int>> fieldIndices = FieldIndices();
        private static readonly DefaultJsonNameTable names = NameTable();
        private static readonly MethodInfo memberwiseClone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly Dictionary<Type, object> defaultModels = DefaultModels();
        private static readonly object boxedTrue = true, boxedFalse = false;
        private static readonly Dictionary<PropertyInfo, Action<object, object?>> fieldSetters = FieldSetters();
        private static readonly Func<object, object> cloneModel = ModelCloner();

        internal StreamReader(int schema, Func<Type, int, object>? sharedSnapshots = null)
        {
            if (!ReplayRules.SupportedSchema(schema)) throw new ArgumentOutOfRangeException(nameof(schema));
            this.schema = schema;
            this.sharedSnapshots = sharedSnapshots;
            actors.patchReader = this; items.patchReader = this; crates.patchReader = this;
            ropes.patchReader = this; effects.patchReader = this; audio.patchReader = this;
            spawned.patchReader = this; balloons.patchReader = this; creatures.patchReader = this; interactions.patchReader = this;
        }

        internal ReplayFrame? DecodeRecord(string line)
        {
            try { if (line == null) throw new ArgumentNullException(nameof(line)); char[] buffer = line.ToCharArray(); return DecodeRecordCore(null, buffer, buffer.Length); }
            catch (OverflowException e) { throw new InvalidDataException("Delta number outside range.", e); }
        }
        internal ReplayFrame? DecodeRecord(char[] buffer, int length)
        {
            try { return DecodeRecordCore(null, buffer, length); }
            catch (OverflowException e) { throw new InvalidDataException("Delta number outside range.", e); }
        }
        // A shared block is one complete immutable entity, decoded on demand by
        // the container. It cannot resolve other blocks or build a reference chain.
        internal object DecodeSharedRecord(string record, Type type, string identity)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            char[] buffer = record.ToCharArray();
            return DecodeSharedRecord(buffer, buffer.Length, type, identity);
        }
        // The container owns reusable UTF-8/character scratch. Parse directly
        // from that slice so every static crate need not allocate a JSON string,
        // another character array, or boxed numbers before a seek can finish.
        internal object DecodeSharedRecord(char[] buffer, int length, Type type, string identity)
        {
            if (schema < 16 || sharedSnapshots != null || !propertyNames.TryGetValue(type, out var fields) || !fields.Contains(identity))
                throw Error("Invalid shared snapshot reader context.");
            if (buffer == null || length < 0 || length > buffer.Length || length > ReplayRules.MaxLineCharacters)
                throw Error("Invalid shared snapshot input bounds.");
            try
            {
                using var input = new ReplayRecordTextReader(buffer, length);
                // TryProject validates the full object's original grammar before
                // the canonical tokenizer is used; its projected output is not
                // consumed here. Uncommon syntax follows the same JSON adapter
                // as ordinary records, without a semantic-failure retry.
                using ReplayTokenReader json = canonicalRecords.TryProject(buffer, length)
                    ? new ReplayCanonicalTokenReader(buffer, length, names, jsonBuffers)
                    : new ReplayJsonTokenReader(new JsonTextReader(input)
                    { MaxDepth = 24, DateParseHandling = DateParseHandling.None, PropertyNameTable = names, ArrayPool = jsonBuffers });
                Read(json); Require(json, JsonToken.StartObject);
                var patch = ReadPatch(json, type, identity);
                if (json.Read() || !patch.Full || patch.Key == null) throw Error("Shared snapshot requires one complete entity baseline.");
                return ApplyPatch(patch, null);
            }
            catch (OverflowException e) { throw new InvalidDataException("Shared snapshot number outside range.", e); }
        }
        internal object DecodeSharedValue(char[] buffer, int length, Type type)
        {
            if (schema < 17 || sharedSnapshots != null || !SharedValueType(type) || buffer == null ||
                length < 0 || length > buffer.Length || length > ReplayRules.MaxLineCharacters)
                throw Error("Invalid shared leaf reader context or bounds.");
            try
            {
                using var input = new ReplayRecordTextReader(buffer, length);
                using ReplayTokenReader json = canonicalRecords.TryProject(buffer, length)
                    ? new ReplayCanonicalTokenReader(buffer, length, names, jsonBuffers)
                    : new ReplayJsonTokenReader(new JsonTextReader(input)
                    { MaxDepth = 24, DateParseHandling = DateParseHandling.None, PropertyNameTable = names, ArrayPool = jsonBuffers });
                Read(json); Require(json, JsonToken.StartObject);
                if (!NextProperty(json, out string name) || name != "Value") throw Error("Invalid shared leaf wrapper.");
                object value = ReadValue(json, type)!;
                if (NextProperty(json, out _) || json.Read()) throw Error("Shared leaf requires one complete array.");
                return value;
            }
            catch (OverflowException e) { throw new InvalidDataException("Shared leaf number outside range.", e); }
        }
        private static bool SharedValueType(Type type) => type == typeof(NativeRendererFrame[]) || type == typeof(NativeLightFrame[]) || type == typeof(NodePose[]);
        private object ResolveSharedValue(ReplayTokenReader json, Type type)
        {
            if (schema < 17 || sharedSnapshots == null || !SharedValueType(type)) throw Error("Invalid shared leaf reference context.");
            int id = checked((int)Integer(json));
            if (id < 0) throw Error("Invalid shared leaf index.");
            object value = sharedSnapshots(type, id);
            if (value == null || value.GetType() != type) throw Error("Shared leaf type mismatch.");
            return value;
        }
        private object ReadSharedValue(ReplayTokenReader json, Type type)
        {
            Require(json, JsonToken.StartObject);
            if (!NextProperty(json, out string name) || name != "$Shared") throw Error("Invalid shared leaf reference.");
            object value = ResolveSharedValue(json, type);
            if (NextProperty(json, out _)) throw Error("Shared leaf reference cannot contain other fields.");
            return value;
        }
        private static TextReader RecordInput(string? line, char[]? buffer, int length) =>
            line != null ? new StringReader(line) : new ReplayRecordTextReader(buffer!, length);
        private ReplayFrame? DecodeRecordCore(string? line, char[]? buffer, int length)
        {
            using var input = RecordInput(line, buffer, length);
            using var json = new ReplayJsonTokenReader(new JsonTextReader(input) { MaxDepth = 24, DateParseHandling = DateParseHandling.None, PropertyNameTable = names, ArrayPool = jsonBuffers });
            Read(json); Require(json, JsonToken.StartObject);
            // Footer records have historically been opaque apart from Type.
            // Keep their existing strict JSON syntax/duplicate checks, including
            // uncommon records whose Type property is not written first.
            Read(json);
            if (json.TokenType != JsonToken.PropertyName || json.TextValue != "Type")
            {
                using var fallbackInput = RecordInput(line, buffer, length);
                using var fallback = new JsonTextReader(fallbackInput) { MaxDepth = 24, DateParseHandling = DateParseHandling.None, ArrayPool = jsonBuffers };
                var token = JObject.Load(fallback, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (fallback.Read()) throw Error("Multiple JSON values in one record.");
                if ((string?)token["Type"] == "end") return null;
            }
            else
            {
                Read(json);
                if (json.TokenType == JsonToken.String && json.TextValue == "end")
                {
                    using var footerInput = RecordInput(line, buffer, length);
                    using var footer = new JsonTextReader(footerInput) { MaxDepth = 24, DateParseHandling = DateParseHandling.None, ArrayPool = jsonBuffers };
                    JObject.Load(footer, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (footer.Read()) throw Error("Multiple JSON values in one record.");
                    return null;
                }
            }
            // Restarting a lightweight text reader also supports arbitrary JSON
            // property order without applying any patch before it is complete.
            using var frameInput = RecordInput(line, buffer, length);
            using ReplayTokenReader frameJson = buffer != null && canonicalRecords.TryProject(buffer, length)
                ? new ReplayCanonicalTokenReader(buffer, length, names, jsonBuffers)
                : new ReplayJsonTokenReader(new JsonTextReader(frameInput)
                { MaxDepth = 24, DateParseHandling = DateParseHandling.None, PropertyNameTable = names, ArrayPool = jsonBuffers });
            Read(frameJson); Require(frameJson, JsonToken.StartObject);
            var values = new object?[frameNames.Length]; ulong seen = 0;
            while (NextProperty(frameJson, out string name))
            {
                if (!Reader.AllowsField(name, schema) || !frameIndices.TryGetValue(name, out int index)) throw Error("Unknown frame field.");
                ulong bit = 1UL << index;
                if ((seen & bit) != 0) throw Error("Duplicate frame field.");
                seen |= bit;
                values[index] = index switch
                {
                    0 => Text(frameJson), 1 => Number(frameJson), 2 => Boolean(frameJson),
                    3 => ReadPatch(frameJson, typeof(WorldFrame)),
                    4 => ReadPatches(frameJson, typeof(ActorFrame), nameof(ActorFrame.Id), null),
                    5 => ReadPatches(frameJson, typeof(ItemFrame), nameof(ItemFrame.Key), ReplayRules.MaxItems),
                    6 => ReadPatches(frameJson, typeof(CrateFrame), nameof(CrateFrame.Key), ReplayRules.MaxCrates),
                    7 => ReadArray(frameJson, typeof(ItemEvent), ReplayRules.MaxEvents),
                    8 => ReadPatches(frameJson, typeof(RopeReplayFrame), nameof(RopeReplayFrame.Key), ReplayRules.MaxRopes),
                    9 => ReadPatches(frameJson, typeof(EffectReplayFrame), nameof(EffectReplayFrame.Key), ReplayRules.MaxEffects),
                    10 => ReadPatches(frameJson, typeof(AudioReplayFrame), nameof(AudioReplayFrame.Key), ReplayRules.MaxAudio),
                    11 => ReadPatches(frameJson, typeof(SpawnedReplayFrame), nameof(SpawnedReplayFrame.Key), ReplayRules.MaxSpawned),
                    12 => ReadPatches(frameJson, typeof(BalloonReplayFrame), nameof(BalloonReplayFrame.Key), ReplayRules.MaxBalloons),
                    13 => ReadPatches(frameJson, typeof(CreatureReplayFrame), nameof(CreatureReplayFrame.Key), ReplayRules.MaxCreatures),
                    32 => ReadPatches(frameJson, typeof(InteractionReplayFrame), nameof(InteractionReplayFrame.Key), ReplayRules.MaxInteractions),
                    35 => Boolean(frameJson), 36 => Number(frameJson),
                    _ => ReadStrings(frameJson, MaximumEntities(index)),
                };
            }
            if (frameJson.Read()) throw Error("Multiple JSON values in one record.");
            if (values[0] is not string type || type != "frame") throw Error("Invalid frame type.");
            if (values[1] is not double time || values[2] is not bool full) throw Error("Missing frame clock or baseline marker.");
            if (world == null && !full) throw Error("First frame requires a complete baseline.");
            world = (WorldFrame)ApplyPatch(values[3] as ObjectPatch ?? throw Error("Missing world patch."), full ? null : world);
            var result = new ReplayFrame
            {
                T = time, World = world,
                SceneSampleTimeKnown = schema >= 22 ? values[35] is bool known ? known : throw Error("Missing scene clock knowledge.") : false,
                SceneSampleTime = schema >= 22 ? values[36] is double sceneTime ? sceneTime : throw Error("Missing scene observation clock.") : 0,
                Actors = actors.Decode(values[4], values[14], values[23], full),
                Items = items.Decode(values[5], values[15], values[24], full),
                Crates = crates.Decode(values[6], values[16], values[25], full),
                Events = values[7] as ItemEvent[] ?? throw Error("Missing events array."),
                Ropes = ropes.Decode(values[8], values[17], values[26], full),
                Effects = effects.Decode(values[9], values[18], values[27], full),
                Audio = audio.Decode(values[10], values[19], values[28], full),
                Spawned = spawned.Decode(values[11], values[20], values[29], full),
                Balloons = balloons.Decode(values[12], values[21], values[30], full),
                Creatures = schema >= 12 ? creatures.Decode(values[13], values[22], values[31], full) : Array.Empty<CreatureReplayFrame>(),
                Interactions = schema >= 15 ? interactions.Decode(values[32], values[33], values[34], full) : Array.Empty<InteractionReplayFrame>(),
            };
            ReplayRules.ValidateVersion(result, schema, versionMemo);
            return result;
        }

        private sealed class ObjectPatch
        {
            internal Type Type;
            internal readonly List<FieldValue> Fields = new(4);
            internal ulong Seen;
            internal bool Full;
            internal string? Key;
            internal object? ResolvedSnapshot;
            internal ObjectPatch(Type type) { Type = type; }
        }
        private readonly struct FieldValue
        {
            internal readonly PropertyInfo Field;
            internal readonly object? Value;
            internal FieldValue(PropertyInfo field, object? value) { Field = field; Value = value; }
        }
        private ObjectPatch ReadPatch(ReplayTokenReader json, Type type, string? identity = null)
        {
            Require(json, JsonToken.StartObject);
            var result = patchPool.Count == 0 ? new ObjectPatch(type) : patchPool.Pop();
            result.Type = type; bool fullSeen = false, sharedSeen = false; int sharedId = -1;
            var fields = properties[type]; var indices = fieldIndices[type];
            while (NextProperty(json, out string name))
            {
                if (name == "$Shared")
                {
                    if (schema < 16 || identity == null || sharedSnapshots == null || sharedSeen)
                        throw Error("Invalid shared snapshot reference.");
                    sharedSeen = true; sharedId = checked((int)Integer(json));
                    if (sharedId < 0) throw Error("Invalid shared snapshot index.");
                    continue;
                }
                if (name == "Full" && identity != null)
                {
                    if (fullSeen) throw Error("Duplicate delta field.");
                    fullSeen = true; result.Full = Boolean(json); continue;
                }
                if (!SchemaField(type, name, schema) || !indices.TryGetValue(name, out int index)) throw Error("Unknown delta field: " + name);
                ulong bit = 1UL << index;
                if ((result.Seen & bit) != 0) throw Error("Duplicate delta field.");
                result.Seen |= bit;
                PropertyInfo field = fields[index];
                object? value = ReadField(json, type, field);
                if (name == identity) result.Key = (string?)value;
                result.Fields.Add(new FieldValue(field, value));
            }
            if (sharedSeen)
            {
                if (result.Fields.Count != 0 || fullSeen) throw Error("Shared snapshot reference cannot contain entity fields.");
                object snapshot = sharedSnapshots!(type, sharedId);
                if (snapshot == null || snapshot.GetType() != type) throw Error("Shared snapshot type mismatch.");
                result.ResolvedSnapshot = snapshot; result.Full = true;
                result.Key = (string?)fields[indices[identity!]].GetValue(snapshot);
            }
            return result;
        }
        private ObjectPatch[] ReadPatches(ReplayTokenReader json, Type type, string identity, int? maximum)
        {
            Require(json, JsonToken.StartArray); var result = new List<ObjectPatch>();
            while (NextElement(json))
            {
                if (maximum.HasValue && result.Count >= maximum.Value) throw Error("Oversized entity array.");
                result.Add(ReadPatch(json, type, identity));
            }
            return result.ToArray();
        }
        private object? ReadField(ReplayTokenReader json, Type owner, PropertyInfo field)
        {
            Type type = field.PropertyType;
            if (NullableField(owner, field.Name) && json.TokenType == JsonToken.Null) return null;
            if (owner == typeof(InteractionReplayFrame) && field.Name == nameof(InteractionReplayFrame.PostEnabled))
                return ReadArray(json, typeof(bool), 2);
            int byteLimit = ByteArrayLimit(owner, field.Name);
            if (byteLimit > 0) return Binary(Text(json), byteLimit,
                owner == typeof(EffectParticleCheckpoint) && field.Name == nameof(EffectParticleCheckpoint.Data));
            int arrayLimit = FloatArrayLimit(owner, field.Name);
            if (arrayLimit > 0) return ReadFloats(json, arrayLimit);
            if (type == typeof(string))
            {
                if (json is ReplayCanonicalTokenReader canonical && canonical.TryGetTextSlice(out char[] characters, out int start, out int count))
                    return SharedText(characters, start, count);
                return SharedText(Text(json));
            }
            if (type == typeof(ObjectPose) || NullableSnapshot(type))
            {
                if (NullableSnapshot(type) && json.TokenType == JsonToken.Null) return null;
                return ReadPatch(json, type);
            }
            if (owner == typeof(ActorFrame) && field.Name == nameof(ActorFrame.JointPose)) return Text(json);
            if (owner == typeof(ReplayHudState) && field.Name == nameof(ReplayHudState.Afflictions))
            {
                float[] values = ReadFloats(json, ReplayHudState.StatusCount);
                if (values.Length != ReplayHudState.StatusCount) throw Error("Incomplete HUD status segments.");
                return values;
            }
            if (owner == typeof(CreatureReplayLine) && field.Name == nameof(CreatureReplayLine.Points))
                return ReadFloats(json, CreatureReplayRules.MaximumFrogLinePoints * 3);
            if (owner == typeof(NativeLightFrame) && field.Name == nameof(NativeLightFrame.CookieSize2D))
            {
                float[] values = ReadFloats(json, 2);
                if (values.Length != 2) throw Error("Incomplete native light cookie dimensions.");
                return values;
            }
            if ((owner == typeof(ItemFrame) || owner == typeof(CreatureReplayFrame) || owner == typeof(SpawnedReplayFrame)) &&
                (field.Name == "Visuals" || field.Name == "Lights") && json.TokenType == JsonToken.Null) return null;
            if ((type == typeof(NativeRendererFrame[]) || type == typeof(NativeLightFrame[])) && json.TokenType == JsonToken.StartObject)
                return ReadSharedValue(json, type);
            if (type == typeof(RopeReplayPoint[]) && json.TokenType == JsonToken.StartObject) return ReadPointPatch(json);
            if (schema >= 16 && owner == typeof(ObjectPose) && field.Name == nameof(ObjectPose.Nodes) && json.TokenType == JsonToken.StartObject)
                return ReadNodePatch(json);
            bool nullable = (owner == typeof(CrateFrame) || owner == typeof(SpawnedReplayFrame)) && field.Name == "Animation";
            return ReadValue(json, type, nullable);
        }
        private object? ReadValue(ReplayTokenReader json, Type type, bool nullable = false)
        {
            if (type == typeof(float?)) return ReadValue(json, typeof(float), true);
            if (type == typeof(double?)) return ReadValue(json, typeof(double), true);
            if (json.TokenType == JsonToken.Null)
            { if (nullable) return null; throw Error("Null delta value is not allowed."); }
            if (type == typeof(string)) return Text(json);
            if (type == typeof(bool)) return Boolean(json) ? boxedTrue : boxedFalse;
            if (type == typeof(float)) return Single(json);
            if (type == typeof(double)) return Number(json);
            if (type == typeof(int)) return checked((int)Integer(json));
            if (type == typeof(uint)) return checked((uint)Integer(json));
            if (type == typeof(long)) return Integer(json);
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
                    element == typeof(RopeReplayPoint) ? RopeReplayRules.MaximumPoints : throw Error("Unsupported array type.");
                if (element == typeof(float)) return ReadFloats(json, maximum);
                return ReadArray(json, element, maximum);
            }
            if (!properties.ContainsKey(type)) throw Error("Unsupported replay data type.");
            return ApplyPatch(ReadPatch(json, type), null);
        }
        private Array ReadArray(ReplayTokenReader json, Type element, int maximum)
        {
            Require(json, JsonToken.StartArray);
            var result = arrayScratch.Count == 0 ? new List<object?>() : arrayScratch.Pop();
            try
            {
                while (NextElement(json))
                {
                    if (result.Count >= maximum) throw Error("Oversized delta array.");
                    result.Add(ReadValue(json, element));
                }
                Array array = Array.CreateInstance(element, result.Count);
                for (int i = 0; i < result.Count; i++) array.SetValue(result[i], i);
                return array;
            }
            finally
            {
                result.Clear();
                if (result.Capacity <= 1024 && arrayScratch.Count < 4) arrayScratch.Push(result);
            }
        }
        private float[] ReadFloats(ReplayTokenReader json, int maximum)
        {
            Require(json, JsonToken.StartArray);
            if (maximum <= 4)
            {
                float a = 0, b = 0, c = 0, d = 0; int count = 0;
                while (NextElement(json))
                {
                    if (count >= maximum) throw Error("Oversized numeric array.");
                    float value = Single(json);
                    switch (count++) { case 0: a = value; break; case 1: b = value; break; case 2: c = value; break; default: d = value; break; }
                }
                return SmallFloats(count, a, b, c, d);
            }
            var result = floatScratch.Count == 0 ? new List<float>() : floatScratch.Pop();
            try
            {
                while (NextElement(json))
                {
                    if (result.Count >= maximum) throw Error("Oversized numeric array.");
                    result.Add(Single(json));
                }
                return result.ToArray();
            }
            finally
            {
                result.Clear();
                if (result.Capacity <= 1024 && floatScratch.Count < 4) floatScratch.Push(result);
            }
        }
        private float[] SmallFloats(int count, float a, float b, float c, float d)
        {
            if (count == 0) return Array.Empty<float>();
            int mask = 0;
            for (int i = 0; i < count; i++)
            {
                float value = i == 0 ? a : i == 1 ? b : i == 2 ? c : d;
                int bits = BitConverter.SingleToInt32Bits(value);
                // Do not collapse negative zero or any other bit pattern.
                if (bits != 0 && bits != 0x3f800000) return SharedSmallFloats(count, a, b, c, d);
                if (bits != 0) mask |= 1 << i;
            }
            // Immutable decoded frames already share inherited arrays. Limit
            // common 0/1 vectors to the 30 possible length-1..4 combinations,
            // owned by this page reader rather than a process-wide intern pool.
            int index = (1 << count) - 2 + mask;
            return simpleVectors[index] ??= NewSmallFloats(count, a, b, c, d);
        }
        private float[] SharedSmallFloats(int count, float a, float b, float c, float d)
        {
            int aBits = BitConverter.SingleToInt32Bits(a), bBits = BitConverter.SingleToInt32Bits(b);
            int cBits = BitConverter.SingleToInt32Bits(c), dBits = BitConverter.SingleToInt32Bits(d);
            uint hash = (uint)count;
            unchecked
            {
                hash = (hash ^ (uint)aBits) * 16777619;
                if (count > 1) hash = (hash ^ (uint)bBits) * 16777619;
                if (count > 2) hash = (hash ^ (uint)cBits) * 16777619;
                if (count > 3) hash = (hash ^ (uint)dBits) * 16777619;
            }
            int index = (int)(ValueHash(hash) % (uint)shortVectors.Length);
            var old = shortVectors[index];
            if (old != null && old.Length == count && BitConverter.SingleToInt32Bits(old[0]) == aBits &&
                (count < 2 || BitConverter.SingleToInt32Bits(old[1]) == bBits) &&
                (count < 3 || BitConverter.SingleToInt32Bits(old[2]) == cBits) &&
                (count < 4 || BitConverter.SingleToInt32Bits(old[3]) == dBits)) return old;
            return shortVectors[index] = NewSmallFloats(count, a, b, c, d);
        }
        private static uint ValueHash(uint hash)
        {
            unchecked
            {
                hash ^= hash >> 16; hash *= 0x85ebca6b;
                hash ^= hash >> 13; hash *= 0xc2b2ae35;
                return hash ^ (hash >> 16);
            }
        }
        private static float[] NewSmallFloats(int count, float a, float b, float c, float d) =>
            count switch { 1 => new[] { a }, 2 => new[] { a, b }, 3 => new[] { a, b, c }, _ => new[] { a, b, c, d } };
        private string SharedText(string value)
        {
            if (value.Length == 0) return "";
            if (sharedStrings.TryGetValue(value, out string? shared)) return shared;
            // Repeated node paths/material identities otherwise remain as a
            // different string in every complete pose array. New identities
            // bypass a full pool; existing entries can still be reused.
            if (sharedStrings.Count < MaximumSharedStrings && value.Length <= MaximumSharedCharacters - sharedCharacters)
            {
                sharedStrings.Add(value, value); sharedCharacters += value.Length;
                int hash = TextHash(value);
                if (!sharedStringBuckets.TryGetValue(hash, out var bucket)) sharedStringBuckets.Add(hash, bucket = new List<string>(1));
                bucket.Add(value);
            }
            return value;
        }
        private string SharedText(char[] buffer, int start, int count)
        {
            if (count == 0) return "";
            int hash = unchecked((int)2166136261);
            unchecked { for (int i = start; i < start + count; i++) hash = (hash ^ buffer[i]) * 16777619; }
            if (sharedStringBuckets.TryGetValue(hash, out var bucket))
                foreach (string candidate in bucket)
                {
                    if (candidate.Length != count) continue;
                    int i = 0; while (i < count && candidate[i] == buffer[start + i]) i++;
                    if (i == count) return candidate;
                }
            return SharedText(new string(buffer, start, count));
        }
        private static int TextHash(string value)
        {
            int hash = unchecked((int)2166136261);
            unchecked { for (int i = 0; i < value.Length; i++) hash = (hash ^ value[i]) * 16777619; }
            return hash;
        }
        private static string[] ReadStrings(ReplayTokenReader json, int? maximum)
        {
            Require(json, JsonToken.StartArray); var result = new List<string>();
            while (NextElement(json))
            {
                if (maximum.HasValue && result.Count >= maximum.Value) throw Error("Oversized entity array.");
                result.Add(Text(json));
            }
            return result.ToArray();
        }

        private object ApplyPatch(ObjectPatch patch, object? old)
        {
            try { return ApplyPatchCore(patch, old); }
            finally
            {
                // Nothing in an expanded frame refers to a patch. Clear all
                // object/array references before retaining bounded scratch space.
                patch.Fields.Clear(); patch.Seen = 0; patch.Full = false; patch.Key = null; patch.ResolvedSnapshot = null;
                if (patchPool.Count < MaximumRetainedPatches) patchPool.Push(patch);
            }
        }
        private object ApplyPatchCore(ObjectPatch patch, object? old)
        {
            Type type = patch.Type;
            if (patch.ResolvedSnapshot != null) return patch.ResolvedSnapshot;
            if (old != null && patch.Fields.Count == 0) return old;
            if (old == null && schema >= 20 && type == typeof(CreatureReplayFrame))
            {
                var indices = fieldIndices[type];
                ValidateCreatureClockBaseline((patch.Seen & (1UL << indices[nameof(CreatureReplayFrame.SampleTimeKnown)])) != 0,
                    (patch.Seen & (1UL << indices[nameof(CreatureReplayFrame.SampleTime)])) != 0,
                    (patch.Seen & (1UL << indices[nameof(CreatureReplayFrame.ClockMode)])) != 0,
                    (patch.Seen & (1UL << indices[nameof(CreatureReplayFrame.SampleDiscontinuity)])) != 0);
            }
            if (old == null && schema >= 25 && type == typeof(ItemFrame))
            {
                var indices = fieldIndices[type];
                ValidateCreatureClockBaseline((patch.Seen & (1UL << indices[nameof(ItemFrame.SampleTimeKnown)])) != 0,
                    (patch.Seen & (1UL << indices[nameof(ItemFrame.SampleTime)])) != 0,
                    (patch.Seen & (1UL << indices[nameof(ItemFrame.ClockMode)])) != 0,
                    (patch.Seen & (1UL << indices[nameof(ItemFrame.SampleDiscontinuity)])) != 0);
            }
            if (old == null)
                for (int i = 0; i < properties[type].Length; i++)
                    if (SchemaField(type, properties[type][i].Name, schema) &&
                        !OptionalBaselineField(type, properties[type][i].Name) && (patch.Seen & (1UL << i)) == 0)
                        throw Error("Incomplete baseline: " + type.Name + "." + properties[type][i].Name);
            // Only complete JSON nodes are shared. Their non-JSON observation
            // clock is +0; ActorJointCodec nodes with recorded clocks bypass this.
            // A future additional node property falls back to the generic path.
            if (type == typeof(NodePose) && old == null && patch.Fields.Count == 6) return SharedNode(patch);
            // Full patches were checked above before borrowing model defaults.
            // Only schema-forbidden and documented optional fields keep defaults.
            // Prototype arrays are never mutated or exposed.
            object result = cloneModel(old ?? defaultModels[type]);
            foreach (var entry in patch.Fields)
            {
                var field = entry.Field; object? value = entry.Value;
                if (value is ObjectPatch nested)
                    value = ApplyPatch(nested, old == null ? null : field.GetValue(old));
                else if (type == typeof(ActorFrame) && field.Name == nameof(ActorFrame.JointPose))
                    value = ActorJointCodec.Decode((string)value!, old == null ? null : (NodePose[])field.GetValue(old)!, schema);
                else if (value is PointPatch points)
                    value = ApplyPointPatch(points, old == null ? null : (RopeReplayPoint[])field.GetValue(old)!);
                else if (value is NodeArrayPatch nodes)
                    value = ApplyNodePatch(nodes, old == null ? null : (NodePose[])field.GetValue(old)!);
                fieldSetters[field](result, value);
            }
            ValidateColorEncoding(result);
            if (result is CreatureReplayFrame creature && creature.Line != null)
            {
                int maximum = creature.Kind == "frog-tongue" ? CreatureReplayRules.MaximumFrogLinePoints : CreatureReplayRules.MaximumLinePoints;
                if (creature.Line.Points.Length > maximum * 3) throw Error("Native creature line exceeds its kind limit.");
            }
            return result;
        }

        private NodePose SharedNode(ObjectPatch patch)
        {
            string path = "";
            float[] position = Array.Empty<float>(), rotation = Array.Empty<float>(), scale = Array.Empty<float>();
            bool active = false, visible = false;
            foreach (var entry in patch.Fields)
                switch (entry.Field.Name)
                {
                    case nameof(NodePose.Path): path = (string)entry.Value!; break;
                    case nameof(NodePose.Position): position = (float[])entry.Value!; break;
                    case nameof(NodePose.Rotation): rotation = (float[])entry.Value!; break;
                    case nameof(NodePose.Scale): scale = (float[])entry.Value!; break;
                    case nameof(NodePose.Active): active = (bool)entry.Value!; break;
                    case nameof(NodePose.Visible): visible = (bool)entry.Value!; break;
                }
            uint hash = (uint)TextHash(path);
            hash = NodeVectorHash(hash, position); hash = NodeVectorHash(hash, rotation); hash = NodeVectorHash(hash, scale);
            unchecked
            {
                hash = (hash ^ (active ? 1U : 0U)) * 16777619;
                hash = (hash ^ (visible ? 1U : 0U)) * 16777619;
            }
            int index = (int)(ValueHash(hash) % (uint)sharedNodes.Length);
            var old = sharedNodes[index];
            if (old != null && old.Path == path && old.Active == active && old.Visible == visible &&
                BitConverter.DoubleToInt64Bits(old.SampleTime) == 0 &&
                NodeVectorsSame(old.Position, position) && NodeVectorsSame(old.Rotation, rotation) && NodeVectorsSame(old.Scale, scale)) return old;
            return sharedNodes[index] = new NodePose(path, position, rotation, scale, active, visible);
        }
        private static uint NodeVectorHash(uint hash, float[] values)
        {
            unchecked
            {
                hash = (hash ^ (uint)values.Length) * 16777619;
                foreach (float value in values) hash = (hash ^ (uint)BitConverter.SingleToInt32Bits(value)) * 16777619;
                return hash;
            }
        }
        private static bool NodeVectorsSame(float[] left, float[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (BitConverter.SingleToInt32Bits(left[i]) != BitConverter.SingleToInt32Bits(right[i])) return false;
            return true;
        }

        private sealed class StreamSet<T> where T : class
        {
            private readonly Dictionary<string, T> values = new(StringComparer.Ordinal);
            private readonly HashSet<string> touched = new(StringComparer.Ordinal), removedKeys = new(StringComparer.Ordinal), seen = new(StringComparer.Ordinal);
            private readonly List<string> additions = new();
            private T[] previous = Array.Empty<T>();
            private readonly Func<T, string> key;
            private readonly string keyField;
            private readonly int? maximum;
            private readonly int keyLength;
            internal StreamSet(Func<T, string> key, string keyField, int? maximum, int keyLength)
            { this.key = key; this.keyField = keyField; this.maximum = maximum; this.keyLength = keyLength; }
            internal T[] Decode(object? updateValues, object? removedValues, object? orderValues, bool full)
            {
                var updates = updateValues as ObjectPatch[] ?? throw Error("Missing entity updates.");
                var removed = removedValues as string[] ?? throw Error("Missing entity removals.");
                if (full && removed.Length != 0) throw Error("A full baseline cannot remove entities.");
                if (full) { values.Clear(); previous = Array.Empty<T>(); }
                touched.Clear(); removedKeys.Clear(); additions.Clear();
                foreach (var patch in updates)
                {
                    string id = ValidKey(patch.Key, keyLength);
                    values.TryGetValue(id, out var old);
                    if (old != null)
                    {
                        // Keep the existing identity string in immutable frames.
                        // The entity dictionary bounds its lifetime naturally.
                        id = key(old); patch.Key = id;
                        for (int i = 0; i < patch.Fields.Count; i++)
                            if (patch.Fields[i].Field.Name == keyField)
                                patch.Fields[i] = new FieldValue(patch.Fields[i].Field, id);
                    }
                    if (!touched.Add(id)) throw Error("Duplicate entity patch.");
                    if ((full || old == null) && !patch.Full) throw Error("Unknown entity delta or missing initial baseline.");
                    if (old == null) additions.Add(id);
                    values[id] = (T)patchReader!.ApplyPatch(patch, patch.Full ? null : old);
                }
                foreach (string removedKey in removed)
                {
                    string id = ValidKey(removedKey, keyLength);
                    if (!removedKeys.Add(id) || touched.Contains(id) || !values.Remove(id)) throw Error("Unknown, duplicate or conflicting entity removal.");
                }
                if (maximum.HasValue && values.Count > maximum.Value) throw Error("Expanded entity count exceeds limit.");
                if (orderValues != null)
                {
                    var order = (string[])orderValues;
                    if (order.Length != values.Count) throw Error("Entity order is not a complete permutation.");
                    seen.Clear(); var result = new T[order.Length];
                    for (int n = 0; n < order.Length; n++)
                    {
                        string id = ValidKey(order[n], keyLength);
                        if (!seen.Add(id) || !values.TryGetValue(id, out var value)) throw Error("Invalid entity order.");
                        result[n] = value;
                    }
                    return previous = result;
                }
                if (updates.Length == 0 && removed.Length == 0) return previous;
                var expanded = new T[values.Count]; int index = 0;
                foreach (var old in previous) if (values.TryGetValue(key(old), out var value)) expanded[index++] = value;
                foreach (string id in additions) expanded[index++] = values[id];
                return previous = expanded;
            }
            // Assigned once by the containing reader, never a global context.
            internal StreamReader? patchReader;
        }

        private sealed class PointPatch
        {
            internal long Length;
            internal PointChange[] Changes = Array.Empty<PointChange>();
        }
        private readonly struct PointChange
        {
            internal readonly long Index;
            internal readonly int Mask;
            internal readonly float[]? Position, Rotation, Scale;
            internal readonly float Radius, Height;
            internal PointChange(long index, int mask, float[]? position, float[]? rotation, float[]? scale, float radius, float height)
            { Index = index; Mask = mask; Position = position; Rotation = rotation; Scale = scale; Radius = radius; Height = height; }
        }
        private PointPatch ReadPointPatch(ReplayTokenReader json)
        {
            var result = new PointPatch(); bool lengthSeen = false, changesSeen = false;
            while (NextProperty(json, out string name))
            {
                if (name == "Length" && !lengthSeen) { lengthSeen = true; result.Length = Integer(json); }
                else if (name == "Changes" && !changesSeen)
                {
                    changesSeen = true; Require(json, JsonToken.StartArray); var changes = new List<PointChange>();
                    while (NextElement(json))
                    {
                        if (changes.Count >= RopeReplayRules.MaximumPoints) throw Error("Oversized rope point delta.");
                        Require(json, JsonToken.StartArray); Read(json); long index = Integer(json); Read(json); int mask = checked((int)Integer(json));
                        if (mask < 1 || mask > 31) throw Error("Invalid rope point field mask.");
                        float[]? position = null, rotation = null, scale = null; float radius = 0, height = 0;
                        if ((mask & 1) != 0) { Read(json); position = ReadFloats(json, 4); }
                        if ((mask & 2) != 0) { Read(json); rotation = ReadFloats(json, 4); }
                        if ((mask & 4) != 0) { Read(json); scale = ReadFloats(json, 4); }
                        if ((mask & 8) != 0) { Read(json); radius = Single(json); }
                        if ((mask & 16) != 0) { Read(json); height = Single(json); }
                        Read(json); Require(json, JsonToken.EndArray);
                        changes.Add(new PointChange(index, mask, position, rotation, scale, radius, height));
                    }
                    result.Changes = changes.ToArray();
                }
                else throw Error("Invalid rope point delta field.");
            }
            if (!lengthSeen || !changesSeen) throw Error("Incomplete rope point delta.");
            return result;
        }
        private static RopeReplayPoint[] ApplyPointPatch(PointPatch patch, RopeReplayPoint[]? previous)
        {
            if (previous == null || patch.Length != previous.Length) throw Error("Invalid rope point delta baseline.");
            if (patch.Changes.Length == 0) return previous;
            var result = (RopeReplayPoint[])previous.Clone(); var seen = new HashSet<long>();
            foreach (var change in patch.Changes)
            {
                if (change.Index < 0 || change.Index >= previous.Length || !seen.Add(change.Index)) throw Error("Invalid or duplicate rope point index.");
                var old = previous[change.Index];
                result[change.Index] = new RopeReplayPoint(change.Position ?? old.Position, change.Rotation ?? old.Rotation, change.Scale ?? old.Scale)
                { Radius = (change.Mask & 8) != 0 ? change.Radius : old.Radius, Height = (change.Mask & 16) != 0 ? change.Height : old.Height };
            }
            return result;
        }

        private static int? MaximumEntities(int index) => index switch
        {
            14 or 23 => null, 15 or 24 => ReplayRules.MaxItems, 16 or 25 => ReplayRules.MaxCrates,
            17 or 26 => ReplayRules.MaxRopes, 18 or 27 => ReplayRules.MaxEffects, 19 or 28 => ReplayRules.MaxAudio,
            20 or 29 => ReplayRules.MaxSpawned, 21 or 30 => ReplayRules.MaxBalloons, 33 or 34 => ReplayRules.MaxInteractions, _ => ReplayRules.MaxCreatures
        };
        private static Dictionary<string, int> Indices(string[] fields)
        { var result = new Dictionary<string, int>(StringComparer.Ordinal); for (int i = 0; i < fields.Length; i++) result.Add(fields[i], i); return result; }
        private static Dictionary<Type, Dictionary<string, int>> FieldIndices()
        {
            var result = new Dictionary<Type, Dictionary<string, int>>();
            foreach (var pair in properties)
            {
                if (pair.Value.Length > 64) throw new InvalidOperationException("Delta field mask exceeds 64 fields.");
                var fields = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < pair.Value.Length; i++) fields.Add(pair.Value[i].Name, i);
                result.Add(pair.Key, fields);
            }
            return result;
        }
        private static Dictionary<Type, object> DefaultModels()
        {
            var result = new Dictionary<Type, object>();
            foreach (Type type in properties.Keys) result.Add(type, Activator.CreateInstance(type)!);
            return result;
        }
        private static Dictionary<PropertyInfo, Action<object, object?>> FieldSetters()
        {
            var result = new Dictionary<PropertyInfo, Action<object, object?>>();
            foreach (var pair in properties)
                foreach (var field in pair.Value) result.Add(field, FieldSetter(field));
            return result;
        }
        private static Action<object, object?> FieldSetter(PropertyInfo field)
        {
            try
            {
                var target = Expression.Parameter(typeof(object), "target");
                var value = Expression.Parameter(typeof(object), "value");
                var call = Expression.Call(Expression.Convert(target, field.DeclaringType!), field.SetMethod!, Expression.Convert(value, field.PropertyType));
                return Expression.Lambda<Action<object, object?>>(call, target, value).Compile();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Some runtimes cannot emit dynamic code. Keep the existing
                // typed/validated reflection path on those runtimes.
                return field.SetValue;
            }
        }
        private static Func<object, object> ModelCloner()
        {
            try
            {
                var source = Expression.Parameter(typeof(object), "source");
                return Expression.Lambda<Func<object, object>>(Expression.Call(source, memberwiseClone), source).Compile();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { return source => memberwiseClone.Invoke(source, null)!; }
        }
        private static DefaultJsonNameTable NameTable()
        {
            var result = new DefaultJsonNameTable();
            foreach (var name in frameNames) result.Add(name);
            foreach (var pair in properties) foreach (var field in pair.Value) result.Add(field.Name);
            result.Add("Full"); result.Add("Length"); result.Add("Changes");
            return result;
        }
        private static string ValidKey(string? value, int maximum)
        { if (string.IsNullOrWhiteSpace(value) || value!.Length > maximum) throw Error("Invalid entity identity."); return value; }
        private static void Require(ReplayTokenReader json, JsonToken token)
        { if (json.TokenType != token) throw Error("Unexpected JSON token."); }
        private static void Read(ReplayTokenReader json)
        { do { if (!json.Read()) throw Error("Incomplete delta record."); } while (json.TokenType == JsonToken.Comment); }
        private static bool NextProperty(ReplayTokenReader json, out string name)
        {
            Read(json); if (json.TokenType == JsonToken.EndObject) { name = ""; return false; }
            Require(json, JsonToken.PropertyName); name = json.TextValue; Read(json); return true;
        }
        private static bool NextElement(ReplayTokenReader json)
        { Read(json); return json.TokenType != JsonToken.EndArray; }
        private static string Text(ReplayTokenReader json)
        { Require(json, JsonToken.String); return json.TextValue; }
        private static bool Boolean(ReplayTokenReader json)
        { Require(json, JsonToken.Boolean); return json.BooleanValue; }
        private static long Integer(ReplayTokenReader json)
        { Require(json, JsonToken.Integer); try { return json.IntegerValue; } catch (Exception e) { throw new InvalidDataException("Integer outside range.", e); } }
        private static double Number(ReplayTokenReader json)
        {
            if (json.TokenType != JsonToken.Integer && json.TokenType != JsonToken.Float) throw Error("Expected number.");
            try { return json.NumberValue; } catch (Exception e) { throw new InvalidDataException("Number outside range.", e); }
        }
        private static float Single(ReplayTokenReader json)
        {
            if (json.TokenType != JsonToken.Integer && json.TokenType != JsonToken.Float) throw Error("Expected number.");
            try { return json.SingleValue; } catch (Exception e) { throw new InvalidDataException("Number outside range.", e); }
        }
    }
}
