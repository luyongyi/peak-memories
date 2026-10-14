using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PeakReplayLab;

internal static partial class ReplayDeltaCodec
{
    // Schema 16 changes storage only. The save/read worker handles these exact
    // field deltas; capture cadence, topology and animation clocks stay intact.
    private static readonly int MaximumObjectNodes = Math.Max(ReplayRules.MaxNodes, CreatureReplayRules.MaximumNodes);

    internal static object NodeDelta(NodePose[] current, NodePose[] previous)
    {
        // Paths define the node index. Never apply an index delta across a
        // topology change, reorder, addition or removal.
        if (current.Length != previous.Length) return current;
        for (int i = 0; i < current.Length; i++)
            if (!string.Equals(current[i].Path, previous[i].Path, StringComparison.Ordinal)) return current;
        var changes = new List<object>();
        for (int i = 0; i < current.Length; i++)
        {
            var a = current[i]; var b = previous[i];
            if (ReferenceEquals(a, b)) continue;
            int mask = (!Same(a.Position, b.Position) ? 1 : 0) | (!Same(a.Rotation, b.Rotation) ? 2 : 0) |
                (!Same(a.Scale, b.Scale) ? 4 : 0) | (a.Active != b.Active ? 8 : 0) | (a.Visible != b.Visible ? 16 : 0);
            if (mask == 0) continue;
            var change = new List<object>(7) { i, mask };
            if ((mask & 1) != 0) change.Add(a.Position);
            if ((mask & 2) != 0) change.Add(a.Rotation);
            if ((mask & 4) != 0) change.Add(a.Scale);
            if ((mask & 8) != 0) change.Add(a.Active);
            if ((mask & 16) != 0) change.Add(a.Visible);
            changes.Add(change.ToArray());
        }
        return new Dictionary<string, object?> { ["Length"] = current.Length, ["Changes"] = changes.ToArray() };
    }

    private sealed class NodeArrayPatch
    {
        internal long Length;
        internal NodeChange[] Changes = Array.Empty<NodeChange>();
        internal NodePose[]? SharedBaseline;
    }
    private readonly struct NodeChange
    {
        internal readonly long Index;
        internal readonly int Mask;
        internal readonly float[]? Position, Rotation, Scale;
        internal readonly bool Active, Visible;
        internal NodeChange(long index, int mask, float[]? position, float[]? rotation, float[]? scale, bool active, bool visible)
        { Index = index; Mask = mask; Position = position; Rotation = rotation; Scale = scale; Active = active; Visible = visible; }
    }

    private static NodePose[] ApplyNodes(JObject patch, NodePose[]? previous)
    {
        if (patch.Count != 2 || patch["Length"]?.Type != JTokenType.Integer) throw Error("Invalid object node delta fields.");
        long length = (long)Value(patch["Length"], typeof(long))!;
        if (length < 0 || length > MaximumObjectNodes) throw Error("Invalid object node delta length.");
        var changes = ArrayToken(patch["Changes"], MaximumObjectNodes);
        var parsed = new NodeChange[changes.Count];
        for (int i = 0; i < parsed.Length; i++)
        {
            if (changes[i] is not JArray entry || entry.Count < 3 || entry.Count > 7) throw Error("Invalid object node delta tuple.");
            long index = (long)Value(entry[0], typeof(long))!;
            int mask = (int)Value(entry[1], typeof(int))!;
            if (mask < 1 || mask > 31) throw Error("Invalid object node field mask.");
            int expected = 2; for (int bit = 1; bit <= 16; bit <<= 1) if ((mask & bit) != 0) expected++;
            if (entry.Count != expected) throw Error("Incomplete object node tuple.");
            int cursor = 2;
            float[]? position = (mask & 1) != 0 ? NodeVector(entry[cursor++], 3) : null;
            float[]? rotation = (mask & 2) != 0 ? NodeVector(entry[cursor++], 4) : null;
            float[]? scale = (mask & 4) != 0 ? NodeVector(entry[cursor++], 3) : null;
            bool active = (mask & 8) != 0 && Bool(entry[cursor++]);
            bool visible = (mask & 16) != 0 && Bool(entry[cursor++]);
            parsed[i] = new NodeChange(index, mask, position, rotation, scale, active, visible);
        }
        return ApplyNodePatch(new NodeArrayPatch { Length = length, Changes = parsed }, previous);
    }

    private static float[] NodeVector(JToken? token, int size)
    {
        JArray array = ArrayToken(token, size);
        if (array.Count != size) throw Error("Invalid object node vector length.");
        var values = new float[size];
        for (int i = 0; i < size; i++) values[i] = (float)Value(array[i], typeof(float))!;
        return values;
    }

    private static NodePose[] ApplyNodePatch(NodeArrayPatch patch, NodePose[]? previous)
    {
        previous = patch.SharedBaseline ?? previous;
        if (previous == null || patch.Length != previous.Length || patch.Length > MaximumObjectNodes)
            throw Error("Invalid object node delta baseline.");
        if (patch.Changes.Length == 0) return previous;
        var result = (NodePose[])previous.Clone(); var seen = new HashSet<long>();
        foreach (var change in patch.Changes)
        {
            if (change.Index < 0 || change.Index >= previous.Length || !seen.Add(change.Index)) throw Error("Invalid or duplicate object node index.");
            var old = previous[change.Index];
            result[change.Index] = new NodePose(old.Path, change.Position ?? old.Position, change.Rotation ?? old.Rotation,
                change.Scale ?? old.Scale, (change.Mask & 8) != 0 ? change.Active : old.Active, (change.Mask & 16) != 0 ? change.Visible : old.Visible);
        }
        return result;
    }

    internal sealed partial class StreamReader
    {
        private NodeArrayPatch ReadNodePatch(ReplayTokenReader json)
        {
            Require(json, JsonToken.StartObject);
            var result = new NodeArrayPatch(); bool lengthSeen = false, changesSeen = false, sharedSeen = false;
            while (NextProperty(json, out string name))
            {
                if (name == "$Shared" && !sharedSeen)
                {
                    sharedSeen = true; result.SharedBaseline = (NodePose[])ResolveSharedValue(json, typeof(NodePose[]));
                }
                else if (name == "Length" && !lengthSeen)
                {
                    lengthSeen = true; result.Length = Integer(json);
                    if (result.Length < 0 || result.Length > MaximumObjectNodes) throw Error("Invalid object node delta length.");
                }
                else if (name == "Changes" && !changesSeen)
                {
                    changesSeen = true; Require(json, JsonToken.StartArray); var changes = new List<NodeChange>();
                    while (NextElement(json))
                    {
                        if (changes.Count >= MaximumObjectNodes) throw Error("Oversized object node delta.");
                        Require(json, JsonToken.StartArray); Read(json); long index = Integer(json); Read(json); int mask = checked((int)Integer(json));
                        if (mask < 1 || mask > 31) throw Error("Invalid object node field mask.");
                        float[]? position = null, rotation = null, scale = null; bool active = false, visible = false;
                        if ((mask & 1) != 0) { Read(json); position = ReadNodeVector(json, 3); }
                        if ((mask & 2) != 0) { Read(json); rotation = ReadNodeVector(json, 4); }
                        if ((mask & 4) != 0) { Read(json); scale = ReadNodeVector(json, 3); }
                        if ((mask & 8) != 0) { Read(json); active = Boolean(json); }
                        if ((mask & 16) != 0) { Read(json); visible = Boolean(json); }
                        Read(json); Require(json, JsonToken.EndArray);
                        changes.Add(new NodeChange(index, mask, position, rotation, scale, active, visible));
                    }
                    result.Changes = changes.ToArray();
                }
                else throw Error("Invalid or duplicate object node delta field.");
            }
            if (!lengthSeen && !changesSeen && sharedSeen) result.Length = result.SharedBaseline!.Length;
            else if (!lengthSeen || !changesSeen) throw Error("Incomplete object node delta.");
            return result;
        }
        private float[] ReadNodeVector(ReplayTokenReader json, int size)
        {
            float[] values = ReadFloats(json, size);
            if (values.Length != size) throw Error("Invalid object node vector length.");
            return values;
        }
    }
}
