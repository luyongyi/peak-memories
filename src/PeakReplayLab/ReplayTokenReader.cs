using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;

namespace PeakReplayLab;

// The patch decoder consumes typed token values instead of JsonReader.Value.
// This keeps numeric vectors out of object boxes. The ordinary JSON adapter is
// still the compatibility path for records outside the canonical subset.
internal abstract class ReplayTokenReader : IDisposable
{
    internal abstract JsonToken TokenType { get; }
    internal abstract bool Read();
    internal abstract string TextValue { get; }
    internal abstract bool BooleanValue { get; }
    internal abstract long IntegerValue { get; }
    internal abstract double NumberValue { get; }
    internal abstract float SingleValue { get; }
    public abstract void Dispose();
}

internal sealed class ReplayJsonTokenReader : ReplayTokenReader
{
    private readonly JsonTextReader reader;
    internal ReplayJsonTokenReader(JsonTextReader reader) => this.reader = reader;
    internal override JsonToken TokenType => reader.TokenType;
    internal override bool Read() => reader.Read();
    internal override string TextValue => (string)reader.Value!;
    internal override bool BooleanValue => (bool)reader.Value!;
    internal override long IntegerValue => Convert.ToInt64(reader.Value, CultureInfo.InvariantCulture);
    internal override double NumberValue => Convert.ToDouble(reader.Value, CultureInfo.InvariantCulture);
    internal override float SingleValue => Convert.ToSingle(reader.Value, CultureInfo.InvariantCulture);
    public override void Dispose() => ((IDisposable)reader).Dispose();
}

// Construct only after ReplayEffectProjection.TryProject accepted the untouched
// complete input. That scan validates JSON grammar, escaping, depth and numeric
// bounds before the patch decoder can change any inherited state. Semantic
// failures keep their ordinary rejection path; they never trigger a retry.
internal sealed class ReplayCanonicalTokenReader : ReplayTokenReader
{
    private char[] source;
    private readonly int length;
    private readonly JsonNameTable names;
    private readonly IArrayPool<char> buffers;
    private int position, valueStart, valueLength;
    private JsonToken token;
    private string? text;
    private bool escaped, parsedInteger, parsedNumber;
    private long integer;
    private double number;
    internal ReplayCanonicalTokenReader(char[] source, int length, JsonNameTable names, IArrayPool<char> buffers)
    { this.source = source; this.length = length; this.names = names; this.buffers = buffers; }
    internal override JsonToken TokenType => token;
    internal override bool Read()
    {
        text = null; escaped = parsedInteger = parsedNumber = false;
        while (position < length && (WhiteSpace(source[position]) || source[position] is ',' or ':')) position++;
        if (position == length) { token = JsonToken.None; return false; }
        char value = source[position++];
        switch (value)
        {
            case '{': token = JsonToken.StartObject; return true;
            case '}': token = JsonToken.EndObject; return true;
            case '[': token = JsonToken.StartArray; return true;
            case ']': token = JsonToken.EndArray; return true;
            case 't': position += 3; token = JsonToken.Boolean; integer = 1; return true;
            case 'f': position += 4; token = JsonToken.Boolean; integer = 0; return true;
            case 'n': position += 3; token = JsonToken.Null; return true;
            case '"':
                valueStart = position;
                while (position < length)
                {
                    char character = source[position++];
                    if (character == '\\') { escaped = true; position++; }
                    else if (character == '"')
                    {
                        valueLength = position - valueStart - 1;
                        int next = position;
                        while (next < length && WhiteSpace(source[next])) next++;
                        token = next < length && source[next] == ':' ? JsonToken.PropertyName : JsonToken.String;
                        if (token == JsonToken.PropertyName)
                            text = names.Get(source, valueStart, valueLength) ?? new string(source, valueStart, valueLength);
                        return true;
                    }
                }
                throw Invalid("Incomplete canonical string.");
            default:
                valueStart = position - 1; token = JsonToken.Integer;
                while (position < length)
                {
                    char character = source[position];
                    if (character is '.' or 'e' or 'E') token = JsonToken.Float;
                    else if (character != '+' && character != '-' && (character < '0' || character > '9')) break;
                    position++;
                }
                valueLength = position - valueStart;
                return true;
        }
    }
    internal bool TryGetTextSlice(out char[] buffer, out int start, out int count)
    {
        buffer = source; start = valueStart; count = valueLength;
        return token == JsonToken.String && !escaped;
    }
    internal override string TextValue
    {
        get
        {
            if (text != null) return text;
            if (!escaped) return text = new string(source, valueStart, valueLength);
            // Keep Newtonsoft's string escape semantics for uncommon values.
            // Only this token is read; numeric tokens stay on the raw path.
            using var input = new SliceTextReader(source, valueStart - 1, valueLength + 2);
            using var json = new JsonTextReader(input)
            { MaxDepth = 24, DateParseHandling = DateParseHandling.None, ArrayPool = buffers };
            if (!json.Read() || json.TokenType != JsonToken.String) throw Invalid("Invalid canonical text value.");
            text = (string)json.Value!;
            if (json.Read()) throw Invalid("Multiple canonical text values.");
            return text;
        }
    }
    internal override bool BooleanValue => integer != 0;
    internal override long IntegerValue
    {
        get
        {
            if (parsedInteger) return integer;
            // Canonical preflight limits integers to 18 decimal digits. Keep
            // the integer token separate: Int64 -> Single must not round
            // through Double, including values above Double's exact range.
            int end = valueStart + valueLength, start = valueStart;
            bool negative = source[start] == '-'; if (negative) start++;
            long result = 0;
            checked { for (int i = start; i < end; i++) result = result * 10 + source[i] - '0'; }
            integer = negative ? -result : result; parsedInteger = true; return integer;
        }
    }
    internal override double NumberValue => token == JsonToken.Integer ? Convert.ToDouble(IntegerValue) : DoubleValue;
    internal override float SingleValue => token == JsonToken.Integer ? Convert.ToSingle(IntegerValue) : Convert.ToSingle(DoubleValue);
    private double DoubleValue
    {
        get
        {
            if (parsedNumber) return number;
            if (!double.TryParse(new ReadOnlySpan<char>(source, valueStart, valueLength), NumberStyles.Float,
                CultureInfo.InvariantCulture, out number)) throw Invalid("Invalid canonical number.");
            parsedNumber = true; return number;
        }
    }
    public override void Dispose() => source = Array.Empty<char>();
    private static bool WhiteSpace(char value) => value is ' ' or '\t' or '\r' or '\n';
    private static InvalidDataException Invalid(string message) => new(message);
    private sealed class SliceTextReader : TextReader
    {
        private readonly char[] source;
        private readonly int end;
        private int position;
        internal SliceTextReader(char[] source, int start, int count)
        { this.source = source; position = start; end = start + count; }
        public override int Read() => position < end ? source[position++] : -1;
        public override int Read(char[] buffer, int index, int count)
        {
            int available = Math.Min(count, end - position);
            Array.Copy(source, position, buffer, index, available); position += available; return available;
        }
    }
}
