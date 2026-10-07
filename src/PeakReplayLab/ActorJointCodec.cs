using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PeakReplayLab;

// Schema 10 stores local joint transforms in a bounded binary envelope. Each
// position is quantized absolutely, never as a rounded displacement from the
// last frame. Scales and root rotations stay exact so long parent-to-hip
// distances cannot magnify their rounding error.
internal static class ActorJointCodec
{
    internal const int MaximumPayloadBytes = 1024 * 1024;
    private const double Precision = 10000d;
    private const double QuaternionLimit = .7071067811865475244;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static string Encode(NodePose[] current, NodePose[]? previous)
    {
        ActorJointReplayRules.Validate(current);
        bool full = previous == null || !ActorJointReplayRules.SameTopology(current, previous);
        var indices = new List<int>(); var masks = new List<byte>();
        var times = new List<double>(); var timeIds = new Dictionary<double, byte>();
        for (int i = 0; i < current.Length; i++)
        {
            if (!full && ReferenceEquals(current[i], previous![i])) continue;
            byte mask = full ? (byte)63 : Mask(current[i], previous![i]);
            if (mask == 0) continue;
            if (!full && current[i].SampleTime <= previous![i].SampleTime)
                throw Invalid("Changed joints require a newer sample time.");
            indices.Add(i); masks.Add(mask);
            if ((mask & 32) != 0 && !timeIds.ContainsKey(current[i].SampleTime))
            {
                if (!Finite(current[i].SampleTime)) throw Invalid("Invalid sample time.");
                timeIds.Add(current[i].SampleTime, checked((byte)times.Count));
                times.Add(current[i].SampleTime);
            }
        }
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Utf8, true))
        {
            writer.Write((byte)0x4a); writer.Write((byte)1); writer.Write((byte)(full ? 0 : 1));
            writer.Write((ushort)current.Length); writer.Write(Topology(current));
            writer.Write((ushort)times.Count);
            foreach (double time in times) writer.Write(time);
            writer.Write((ushort)indices.Count);
            for (int n = 0; n < indices.Count; n++)
            {
                int index = indices[n]; var node = current[index]; byte mask = masks[n];
                writer.Write((byte)index); writer.Write(mask);
                if (full)
                {
                    byte[] path = Utf8.GetBytes(node.Path);
                    writer.Write(checked((ushort)path.Length)); writer.Write(path);
                }
                if ((mask & 1) != 0) WriteVector(writer, node.Position);
                if ((mask & 2) != 0)
                {
                    if (node.Path == ".") foreach (float component in node.Rotation) writer.Write(component);
                    else WriteRotation(writer, Rotation(node.Rotation));
                }
                if ((mask & 4) != 0) foreach (float component in node.Scale) writer.Write(component);
                if ((mask & 24) != 0) writer.Write((byte)((node.Active ? 1 : 0) | (node.Visible ? 2 : 0)));
                if ((mask & 32) != 0) writer.Write(timeIds[node.SampleTime]);
            }
        }
        if (stream.Length > MaximumPayloadBytes) throw Invalid("Joint payload exceeds limit.");
        return Convert.ToBase64String(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    public static bool Same(NodePose[] current, NodePose[] previous)
    {
        if (ReferenceEquals(current, previous)) return true;
        if (!ActorJointReplayRules.SameTopology(current, previous)) return false;
        for (int i = 0; i < current.Length; i++)
            if (!ReferenceEquals(current[i], previous[i]) && Mask(current[i], previous[i]) != 0) return false;
        return true;
    }

    public static NodePose[] Decode(string payload, NodePose[]? previous)
    {
        if (payload == null || payload.Length == 0 || payload.Length > ((MaximumPayloadBytes + 2) / 3) * 4)
            throw Invalid("Invalid joint payload size.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(payload); }
        catch (FormatException e) { throw new InvalidDataException("Invalid joint base64 payload.", e); }
        if (bytes.Length > MaximumPayloadBytes) throw Invalid("Joint payload exceeds limit.");
        try
        {
            using var stream = new MemoryStream(bytes, false);
            using var reader = new BinaryReader(stream, Utf8);
            if (reader.ReadByte() != 0x4a || reader.ReadByte() != 1) throw Invalid("Unknown joint encoding.");
            byte mode = reader.ReadByte(); if (mode > 1) throw Invalid("Invalid joint mode.");
            bool full = mode == 0;
            int count = reader.ReadUInt16(); ulong topology = reader.ReadUInt64();
            if (count > ActorJointReplayRules.MaximumJoints) throw Invalid("Joint count exceeds limit.");
            if (!full && (previous == null || previous.Length != count || Topology(previous) != topology))
                throw Invalid("Joint delta topology does not match its baseline.");
            int timeCount = reader.ReadUInt16(); if (timeCount > count) throw Invalid("Sample time count exceeds limit.");
            var times = new double[timeCount]; var uniqueTimes = new HashSet<double>();
            for (int i = 0; i < timeCount; i++)
            {
                times[i] = reader.ReadDouble();
                if (!Finite(times[i]) || !uniqueTimes.Add(times[i])) throw Invalid("Invalid or duplicate sample time.");
            }
            int changes = reader.ReadUInt16();
            if (changes > count || full && changes != count || timeCount > changes) throw Invalid("Invalid joint update count.");
            var result = full ? new NodePose[count] : (NodePose[])previous!.Clone();
            int lastIndex = -1; var usedTimes = new bool[timeCount];
            for (int n = 0; n < changes; n++)
            {
                int index = reader.ReadByte(); byte mask = reader.ReadByte();
                if (index >= count || index <= lastIndex || full && index != n) throw Invalid("Invalid or duplicate joint index.");
                if (mask == 0 || mask > 63 || full && mask != 63) throw Invalid("Invalid joint field mask.");
                lastIndex = index;
                var old = full ? null : previous![index]; string path;
                if (full)
                {
                    int length = reader.ReadUInt16();
                    if (length == 0 || length > ActorJointReplayRules.MaximumPathLength * 3 || length > stream.Length - stream.Position)
                        throw Invalid("Invalid joint path length.");
                    path = Utf8.GetString(reader.ReadBytes(length));
                    if (path.Length > ActorJointReplayRules.MaximumPathLength) throw Invalid("Joint path exceeds limit.");
                }
                else path = old!.Path;
                var position = (mask & 1) != 0 ? ReadVector(reader) : old!.Position;
                var rotation = (mask & 2) != 0 ? path == "."
                    ? new[] { reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() }
                    : ReadRotation(reader) : old!.Rotation;
                var scale = (mask & 4) != 0 ? new[] { reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() } : old!.Scale;
                bool active = old?.Active ?? true, visible = old?.Visible ?? true;
                if ((mask & 24) != 0)
                {
                    byte flags = reader.ReadByte(); if (flags > 3) throw Invalid("Invalid joint flags.");
                    // Both bits are encoded together; a single-bit delta must
                    // retain the value of the flag excluded from its mask.
                    if (old != null && ((mask & 8) == 0 && ((flags & 1) != 0) != old.Active ||
                        (mask & 16) == 0 && ((flags & 2) != 0) != old.Visible)) throw Invalid("Inconsistent joint flags.");
                    active = (flags & 1) != 0; visible = (flags & 2) != 0;
                }
                double sampleTime = old?.SampleTime ?? 0;
                if ((mask & 32) != 0)
                {
                    int timeIndex = reader.ReadByte(); if (timeIndex >= timeCount) throw Invalid("Invalid sample time index.");
                    sampleTime = times[timeIndex]; usedTimes[timeIndex] = true;
                }
                if (old != null && sampleTime <= old.SampleTime)
                    throw Invalid("Changed joints require a newer sample time.");
                result[index] = new NodePose(path, position, rotation, scale, active, visible, sampleTime);
            }
            foreach (bool used in usedTimes) if (!used) throw Invalid("Unused sample time entry.");
            if (stream.Position != stream.Length) throw Invalid("Trailing joint payload bytes.");
            ActorJointReplayRules.Validate(result);
            if (full && Topology(result) != topology) throw Invalid("Joint topology checksum mismatch.");
            return changes == 0 && !full ? previous! : result;
        }
        catch (EndOfStreamException e) { throw new InvalidDataException("Truncated joint payload.", e); }
        catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid UTF-8 joint path.", e); }
    }

    private static byte Mask(NodePose current, NodePose previous) => (byte)(
        (!SameVector(current.Position, previous.Position) ? 1 : 0) |
        (!SameRotation(current, previous) ? 2 : 0) |
        (!SameExactVector(current.Scale, previous.Scale) ? 4 : 0) |
        (current.Active != previous.Active ? 8 : 0) | (current.Visible != previous.Visible ? 16 : 0) |
        (current.SampleTime != previous.SampleTime ? 32 : 0));

    private static bool SameRotation(NodePose current, NodePose previous)
    {
        if (ReferenceEquals(current.Rotation, previous.Rotation)) return true;
        // The world root may be far from physical hip bones. Preserve its
        // rotation bits so its error cannot grow with the length of that arm.
        if (current.Path != ".") return Rotation(current.Rotation) == Rotation(previous.Rotation);
        return SameExactVector(current.Rotation, previous.Rotation);
    }

    private static bool SameExactVector(float[] current, float[] previous)
    {
        if (ReferenceEquals(current, previous)) return true;
        for (int i = 0; i < current.Length; i++)
            if (BitConverter.SingleToInt32Bits(current[i]) != BitConverter.SingleToInt32Bits(previous[i])) return false;
        return true;
    }

    private static bool SameVector(float[] current, float[] previous)
    {
        if (ReferenceEquals(current, previous)) return true;
        for (int i = 0; i < 3; i++)
        {
            int a = Quantized(current[i]), b = Quantized(previous[i]);
            if (a != b || a == int.MinValue && current[i] != previous[i]) return false;
        }
        return true;
    }

    private static int Quantized(float value)
    {
        double quantized = Math.Round(value * Precision, MidpointRounding.AwayFromZero);
        return quantized > int.MinValue && quantized <= int.MaxValue ? (int)quantized : int.MinValue;
    }
    private static void WriteVector(BinaryWriter writer, float[] values)
    {
        foreach (float value in values)
        {
            int quantized = Quantized(value);
            if (quantized > short.MinValue && quantized <= short.MaxValue) writer.Write((short)quantized);
            else
            {
                writer.Write(short.MinValue); writer.Write(quantized);
                if (quantized == int.MinValue) writer.Write(value);
            }
        }
    }
    private static float[] ReadVector(BinaryReader reader)
    {
        var values = new float[3];
        for (int i = 0; i < values.Length; i++)
        {
            int quantized = reader.ReadInt16();
            if (quantized == short.MinValue)
            {
                quantized = reader.ReadInt32();
                if (quantized == int.MinValue)
                {
                    values[i] = reader.ReadSingle();
                    if (!Finite(values[i])) throw Invalid("Invalid escaped vector value.");
                    continue;
                }
            }
            values[i] = (float)(quantized / Precision);
        }
        return values;
    }

    private static ulong Rotation(float[] quaternion)
    {
        double norm = 0; int largest = 0;
        for (int i = 0; i < 4; i++)
        {
            norm += quaternion[i] * (double)quaternion[i];
            if (Math.Abs(quaternion[i]) > Math.Abs(quaternion[largest])) largest = i;
        }
        double factor = (quaternion[largest] < 0 ? -1 : 1) / Math.Sqrt(norm);
        ulong packed = (ulong)largest; int shift = 2;
        for (int i = 0; i < 4; i++)
        {
            if (i == largest) continue;
            // Signed 15-bit components represent zero exactly. This matters
            // for identity structural parents: biased zeroes would add tiny
            // rotations repeatedly along every bone chain.
            double mapped = quaternion[i] * factor * (16383d / QuaternionLimit);
            int value = (int)Math.Round(mapped, MidpointRounding.AwayFromZero);
            if (value < -16383 || value > 16383) throw Invalid("Invalid normalized joint rotation.");
            packed |= (ulong)(value & 32767) << shift; shift += 15;
        }
        return packed;
    }
    private static void WriteRotation(BinaryWriter writer, ulong packed)
    { for (int i = 0; i < 6; i++) writer.Write((byte)(packed >> (i * 8))); }
    private static float[] ReadRotation(BinaryReader reader)
    {
        ulong packed = 0; for (int i = 0; i < 6; i++) packed |= (ulong)reader.ReadByte() << (i * 8);
        if ((packed >> 47) != 0) throw Invalid("Invalid quaternion padding.");
        int largest = (int)(packed & 3), shift = 2; double sum = 0, maximum = 0;
        var quaternion = new float[4];
        for (int i = 0; i < 4; i++)
        {
            if (i == largest) continue;
            int encoded = (int)((packed >> shift) & 32767);
            int signed = (encoded & 16384) == 0 ? encoded : encoded - 32768;
            if (signed == -16384) throw Invalid("Reserved quaternion component.");
            double value = signed * (QuaternionLimit / 16383d);
            quaternion[i] = (float)value; sum += value * value; maximum = Math.Max(maximum, Math.Abs(value)); shift += 15;
        }
        if (sum > 1) throw Invalid("Invalid compressed quaternion.");
        double restored = Math.Sqrt(1 - sum);
        if (restored + .0001 < maximum) throw Invalid("Invalid largest quaternion component.");
        quaternion[largest] = (float)restored; return quaternion;
    }

    private static ulong Topology(NodePose[] joints)
    {
        ulong hash = 14695981039346656037UL;
        unchecked
        {
            foreach (var joint in joints)
            {
                hash = (hash ^ (byte)joint.Path.Length) * 1099511628211UL;
                hash = (hash ^ (byte)(joint.Path.Length >> 8)) * 1099511628211UL;
                foreach (char value in joint.Path)
                {
                    hash = (hash ^ (byte)value) * 1099511628211UL;
                    hash = (hash ^ (byte)(value >> 8)) * 1099511628211UL;
                }
            }
        }
        return hash;
    }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && Math.Abs(value) <= 1_000_000_000;
    private static InvalidDataException Invalid(string message) => new("Invalid joint codec: " + message);
}
