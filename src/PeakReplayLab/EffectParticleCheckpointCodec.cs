using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PeakReplayLab;

// Save/read workers only. Runtime capture and native restoration keep the exact
// immutable struct bytes; neither compression nor transposition runs in capture.
internal static class EffectParticleCheckpointCodec
{
    private const string Prefix = "PS23D1:";
    private const int Header = 48;
    private const int MaximumWireBytes = EffectParticleCheckpointRules.MaximumDataBytes + 65536;
    private sealed class Encoded
    {
        internal readonly int Count;
        internal readonly WeakReference<string> Text;
        internal Encoded(byte[] data, int count) { Count = count; Text = new WeakReference<string>(EncodeCore(data, count)); }
    }
    private static readonly ConditionalWeakTable<byte[], Encoded> encoded = new();
    internal static string Encode(byte[] data, int count)
    {
        ValidateShape(data.Length, count, count == 0 ? 0 : data.Length / count);
        if (data.Length == 0) return "";
        var cached = encoded.GetValue(data, bytes => new Encoded(bytes, count));
        if (cached.Count == count && cached.Text.TryGetTarget(out var text)) return text;
        text = EncodeCore(data, count);
        if (cached.Count == count) cached.Text.SetTarget(text);
        return text;
    }
    private static string EncodeCore(byte[] data, int count)
    {
        int stride = data.Length / count;
        var columns = new byte[data.Length];
        for (int column = 0; column < stride; column++)
            for (int particle = 0; particle < count; particle++)
                columns[column * count + particle] = data[particle * stride + column];
        using var output = new MemoryStream();
        using (var zip = new DeflateStream(output, CompressionLevel.Optimal, true)) zip.Write(columns, 0, columns.Length);
        byte[] compressed = output.ToArray();
        // Random bytes need not grow: ordinary base64 is also a valid schema23
        // representation and remains accepted by every read path.
        if (compressed.Length + Header + Prefix.Length >= data.Length) return Convert.ToBase64String(data);
        var envelope = new byte[Header + compressed.Length];
        WriteInt(envelope, 0, data.Length); WriteInt(envelope, 4, stride);
        WriteInt(envelope, 8, count); WriteInt(envelope, 12, compressed.Length);
        using (var hash = SHA256.Create()) hash.ComputeHash(compressed).CopyTo(envelope, 16);
        Buffer.BlockCopy(compressed, 0, envelope, Header, compressed.Length);
        return Prefix + Convert.ToBase64String(envelope);
    }
    internal static byte[] Decode(string text)
    {
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
            return Base64(text, EffectParticleCheckpointRules.MaximumDataBytes);
        byte[] envelope = Base64(text.Substring(Prefix.Length), MaximumWireBytes);
        if (envelope.Length < Header) throw Invalid();
        int length = ReadInt(envelope, 0), stride = ReadInt(envelope, 4), count = ReadInt(envelope, 8), compressed = ReadInt(envelope, 12);
        ValidateShape(length, count, stride);
        if (length == 0 || compressed <= 0 || compressed != envelope.Length - Header) throw Invalid();
        using (var hash = SHA256.Create())
        {
            var actual = hash.ComputeHash(envelope, Header, compressed);
            for (int i = 0; i < actual.Length; i++) if (actual[i] != envelope[16 + i]) throw Invalid();
        }
        var columns = new byte[length];
        using var input = new ExactInput(envelope, Header, compressed);
        using (var zip = new DeflateStream(input, CompressionMode.Decompress, true))
        {
            int read = 0;
            while (read < columns.Length)
            {
                int next = zip.Read(columns, read, columns.Length - read);
                if (next == 0) throw Invalid();
                read += next;
            }
            if (zip.ReadByte() != -1) throw Invalid();
        }
        if (input.Position != input.Length) throw Invalid();
        var data = new byte[length];
        for (int column = 0; column < stride; column++)
            for (int particle = 0; particle < count; particle++)
                data[particle * stride + column] = columns[column * count + particle];
        return data;
    }
    internal static byte[] Base64(string text, int maximum)
    {
        if (text == null || text.Length > ((maximum + 2L) / 3) * 4) throw Invalid();
        try { var bytes = Convert.FromBase64String(text); return bytes.Length <= maximum ? bytes : throw Invalid(); }
        catch (FormatException e) { throw new InvalidDataException("Invalid particle checkpoint binary encoding.", e); }
    }
    private static void ValidateShape(int length, int count, int stride)
    {
        if (length < 0 || length > EffectParticleCheckpointRules.MaximumDataBytes || count < 0 || count > EffectParticleCheckpointRules.MaximumParticles ||
            (count == 0 ? length != 0 || stride != 0 : stride < 1 || stride > EffectParticleCheckpointRules.MaximumParticleStride || (long)count * stride != length))
            throw Invalid();
    }
    private static int ReadInt(byte[] bytes, int at) => bytes[at] | bytes[at + 1] << 8 | bytes[at + 2] << 16 | bytes[at + 3] << 24;
    private static void WriteInt(byte[] bytes, int at, int value)
    { for (int i = 0; i < 4; i++) bytes[at + i] = (byte)(value >> (i * 8)); }
    private static InvalidDataException Invalid() => new("Invalid compressed particle checkpoint payload.");
    // DeflateStream buffers input. Keep the final eight bytes unbuffered so
    // no compressed suffix can be silently swallowed after the final block.
    private sealed class ExactInput : MemoryStream
    {
        internal ExactInput(byte[] bytes, int start, int count) : base(bytes, start, count, false) { }
        private int Limit(int count) => (int)Math.Min(count, Math.Max(1, Length - Position - 8));
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Limit(count));
        public override int Read(Span<byte> buffer) => base.Read(buffer.Slice(0, Limit(buffer.Length)));
    }
}

internal sealed class EffectParticleCheckpointJsonConverter : JsonConverter<EffectParticleCheckpoint>
{
    public override void WriteJson(JsonWriter writer, EffectParticleCheckpoint? value, JsonSerializer serializer)
    {
        if (value == null) { writer.WriteNull(); return; }
        writer.WriteStartObject();
        writer.WritePropertyName(nameof(value.ObservedAt)); writer.WriteValue(value.ObservedAt);
        writer.WritePropertyName(nameof(value.RootPhase)); writer.WriteValue(value.RootPhase);
        writer.WritePropertyName(nameof(value.Layout)); writer.WriteValue(value.Layout);
        writer.WritePropertyName(nameof(value.Count)); writer.WriteValue(value.Count);
        writer.WritePropertyName(nameof(value.State)); writer.WriteValue(value.State);
        writer.WritePropertyName(nameof(value.Data)); writer.WriteValue(EffectParticleCheckpointCodec.Encode(value.Data, value.Count));
        writer.WritePropertyName(nameof(value.Digest)); writer.WriteValue(value.Digest);
        writer.WriteEndObject();
    }
    public override EffectParticleCheckpoint? ReadJson(JsonReader reader, Type objectType, EffectParticleCheckpoint? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null) return null;
        var json = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (json.Count != 7) throw new InvalidDataException("Incomplete particle checkpoint.");
        var result = new EffectParticleCheckpoint
        {
            ObservedAt = Required<double>(json, "ObservedAt", JTokenType.Float, JTokenType.Integer),
            RootPhase = Required<double>(json, "RootPhase", JTokenType.Float, JTokenType.Integer),
            Layout = Required<string>(json, "Layout", JTokenType.String), Count = Required<int>(json, "Count", JTokenType.Integer),
            State = Binary(json, "State", false), Data = Binary(json, "Data", true),
            Digest = Required<string>(json, "Digest", JTokenType.String),
        };
        EffectParticleCheckpointRules.Validate(result); return result;
    }
    private static byte[] Binary(JObject json, string name, bool data)
    {
        var token = json[name]; int maximum = data ? EffectParticleCheckpointRules.MaximumDataBytes : EffectParticleCheckpointRules.MaximumStateBytes;
        if (token?.Type == JTokenType.Bytes && token.Value<byte[]>() is byte[] bytes && bytes.Length <= maximum)
            return (byte[])bytes.Clone();
        string text = Required<string>(json, name, JTokenType.String);
        return data ? EffectParticleCheckpointCodec.Decode(text) : EffectParticleCheckpointCodec.Base64(text, maximum);
    }
    private static T Required<T>(JObject json, string name, params JTokenType[] types)
    {
        var token = json[name]; bool valid = false;
        foreach (var type in types) valid |= token?.Type == type;
        if (!valid) throw new InvalidDataException("Invalid particle checkpoint field: " + name);
        return token!.Value<T>()!;
    }
}
