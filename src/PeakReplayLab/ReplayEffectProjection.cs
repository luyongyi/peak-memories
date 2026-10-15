using System;

namespace PeakReplayLab;

// Page-local scratch for selective replay readers. This is only a lexical
// fast path: the consuming parser still validates identities, duplicates, schema
// and retained patch semantics. Any syntax outside this conservative JSON subset
// must be parsed from the untouched original record instead.
internal sealed class ReplayEffectProjection
{
    private const int MaximumDepth = 24;
    private enum Scope { Frame, World, Actor, Appearance }
    private readonly bool trajectoryOnly;
    private char[] source = Array.Empty<char>();
    private int position, sourceLength, written;
    internal char[] Buffer { get; private set; } = new char[4096];
    internal int Length { get; private set; }

    internal ReplayEffectProjection(bool trajectoryOnly = false) => this.trajectoryOnly = trajectoryOnly;

    internal bool TryProject(char[] input, int length)
    {
        Length = written = position = 0;
        if (input == null || length < 0 || length > input.Length ||
            length > ReplayRules.MaxLineCharacters || ReferenceEquals(input, Buffer)) return false;
        source = input; sourceLength = length;
        try
        {
            WhiteSpace();
            if (!ProjectObject(Scope.Frame, depth: 1)) return false;
            WhiteSpace();
            if (position != sourceLength) return false;
            Length = written; return true;
        }
        finally { source = Array.Empty<char>(); }
    }

    private bool ProjectObject(Scope scope, int depth)
    {
        if (depth > MaximumDepth || !Take('{') || !Write('{')) return false;
        WhiteSpace();
        if (Take('}')) return Write('}');
        while (true)
        {
            int nameStart = position;
            if (!String(fieldName: true)) return false;
            int nameEnd = position;
            WhiteSpace();
            if (!Take(':') || !Copy(nameStart, nameEnd - nameStart) || !Write(':')) return false;
            WhiteSpace();
            if (scope == Scope.Frame && NameIs(nameStart, nameEnd, "World"))
            {
                if (!ProjectObject(Scope.World, depth: depth + 1)) return false;
            }
            else if (trajectoryOnly && scope == Scope.Frame && NameIs(nameStart, nameEnd, "Actors"))
            {
                if (!ProjectActors(depth + 1)) return false;
            }
            else if (trajectoryOnly && scope == Scope.Actor && NameIs(nameStart, nameEnd, "Appearance"))
            {
                if (!ProjectObject(Scope.Appearance, depth: depth + 1)) return false;
            }
            else
            {
                bool keep = Keep(scope, nameStart, nameEnd);
                int valueStart = position;
                if (!Value(depth)) return false;
                if (keep ? !Copy(valueStart, position - valueStart) : !Null()) return false;
            }
            WhiteSpace();
            if (Take('}')) return Write('}');
            if (!Take(',') || !Write(',')) return false;
            WhiteSpace();
        }
    }

    private bool Keep(Scope scope, int start, int end)
    {
        if (scope == Scope.World)
            return NameIs(start, end, "Segment") || !trajectoryOnly && NameIs(start, end, "ActiveMapObjects");
        if (scope == Scope.Actor)
            return NameIs(start, end, "Id") || NameIs(start, end, "Name") || NameIs(start, end, "Full") ||
                NameIs(start, end, "Position") || NameIs(start, end, "RouteState");
        if (scope == Scope.Appearance) return NameIs(start, end, "EyeState");
        return NameIs(start, end, "Type") || NameIs(start, end, "T") || NameIs(start, end, "ObjectsFull") ||
            NameIs(start, end, "SceneSampleTimeKnown") || NameIs(start, end, "SceneSampleTime") ||
            (trajectoryOnly
                ? NameIs(start, end, "RemovedActors") || NameIs(start, end, "ActorOrder")
                : NameIs(start, end, "Effects") || NameIs(start, end, "RemovedEffects") || NameIs(start, end, "EffectOrder"));
    }

    private bool ProjectActors(int depth)
    {
        if (depth > MaximumDepth || !Take('[') || !Write('[')) return false;
        WhiteSpace();
        if (Take(']')) return Write(']');
        while (true)
        {
            if (!ProjectObject(Scope.Actor, depth + 1)) return false;
            WhiteSpace();
            if (Take(']')) return Write(']');
            if (!Take(',') || !Write(',')) return false;
            WhiteSpace();
        }
    }

    private bool NameIs(int start, int end, string expected)
    {
        if (end - start != expected.Length + 2) return false;
        for (int i = 0; i < expected.Length; i++) if (source[start + 1 + i] != expected[i]) return false;
        return true;
    }

    private bool Value(int parentDepth)
    {
        if (position >= sourceLength) return false;
        char value = source[position];
        if (value == '"') return String(fieldName: false);
        if (value == '{') return Object(parentDepth + 1);
        if (value == '[') return ScanArray(parentDepth + 1);
        if (value == 't') return Literal("true");
        if (value == 'f') return Literal("false");
        if (value == 'n') return Literal("null");
        return Number();
    }

    private bool Object(int depth)
    {
        if (depth > MaximumDepth || !Take('{')) return false;
        WhiteSpace();
        if (Take('}')) return true;
        while (true)
        {
            if (!String(fieldName: true)) return false;
            WhiteSpace(); if (!Take(':')) return false;
            WhiteSpace(); if (!Value(depth)) return false;
            WhiteSpace(); if (Take('}')) return true;
            if (!Take(',')) return false;
            WhiteSpace();
        }
    }

    private bool ScanArray(int depth)
    {
        if (depth > MaximumDepth || !Take('[')) return false;
        WhiteSpace();
        if (Take(']')) return true;
        while (true)
        {
            if (!Value(depth)) return false;
            WhiteSpace(); if (Take(']')) return true;
            if (!Take(',')) return false;
            WhiteSpace();
        }
    }

    private bool String(bool fieldName)
    {
        if (!Take('"')) return false;
        while (position < sourceLength)
        {
            char value = source[position++];
            if (value == '"') return true;
            if (value < ' ' || char.IsSurrogate(value)) return false;
            if (value != '\\') continue;
            // Escaped names are uncommon in recorder output. Let Newtonsoft
            // interpret their identity instead of comparing encoded spellings.
            if (fieldName || position >= sourceLength) return false;
            char escape = source[position++];
            if (escape is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't') continue;
            if (escape != 'u' || position > sourceLength - 4) return false;
            int scalar = 0;
            for (int i = 0; i < 4; i++)
            {
                int digit = Hex(source[position++]);
                if (digit < 0) return false;
                scalar = scalar * 16 + digit;
            }
            // Preserve Newtonsoft/Mono's handling of paired and unpaired
            // surrogate escapes by falling back for either kind.
            if (scalar >= 0xd800 && scalar <= 0xdfff) return false;
        }
        return false;
    }

    private bool Number()
    {
        int start = position;
        Take('-');
        int integerStart = position;
        if (Take('0')) { }
        else
        {
            if (position >= sourceLength || source[position] < '1' || source[position] > '9') return false;
            while (position < sourceLength && Digit(source[position])) position++;
        }
        int integerDigits = position - integerStart;
        if (integerDigits > 18) return false;
        int fractionStart = position, fractionDigits = 0;
        if (Take('.'))
        {
            fractionStart = position;
            while (position < sourceLength && Digit(source[position])) position++;
            fractionDigits = position - fractionStart;
            if (fractionDigits == 0) return false;
        }
        int exponent = 0;
        if (Take('e') || Take('E'))
        {
            bool negative = Take('-'); if (!negative) Take('+');
            int exponentStart = position;
            while (position < sourceLength && Digit(source[position]))
            {
                exponent = exponent * 10 + source[position++] - '0';
                if (exponent > 300) return false;
            }
            if (position == exponentStart) return false;
            if (negative) exponent = -exponent;
        }
        if (position - start > 24) return false;
        // Also bound the magnitude, not just the written exponent: an 18-digit
        // mantissa followed by e300 can overflow the old reader's double.
        int power = 0; bool nonzero = false;
        for (int i = 0; i < integerDigits; i++) if (source[integerStart + i] != '0')
        { power = integerDigits - i - 1; nonzero = true; break; }
        if (!nonzero) for (int i = 0; i < fractionDigits; i++) if (source[fractionStart + i] != '0')
        { power = -i - 1; nonzero = true; break; }
        return !nonzero || (power + exponent >= -300 && power + exponent <= 300);
    }

    private bool Literal(string value)
    {
        if (position > sourceLength - value.Length) return false;
        for (int i = 0; i < value.Length; i++) if (source[position + i] != value[i]) return false;
        position += value.Length; return true;
    }
    private void WhiteSpace()
    { while (position < sourceLength && source[position] is ' ' or '\t' or '\r' or '\n') position++; }
    private bool Take(char value)
    { if (position >= sourceLength || source[position] != value) return false; position++; return true; }
    private static bool Digit(char value) => value >= '0' && value <= '9';
    private static int Hex(char value) => value >= '0' && value <= '9' ? value - '0' :
        value >= 'a' && value <= 'f' ? value - 'a' + 10 : value >= 'A' && value <= 'F' ? value - 'A' + 10 : -1;
    private bool Null() => Write('n') && Write('u') && Write('l') && Write('l');
    private bool Write(char value)
    { if (!Reserve(1)) return false; Buffer[written++] = value; return true; }
    private bool Copy(int start, int count)
    { if (!Reserve(count)) return false; System.Array.Copy(source, start, Buffer, written, count); written += count; return true; }
    private bool Reserve(int count)
    {
        if (count < 0 || written > ReplayRules.MaxLineCharacters - count) return false;
        int needed = written + count;
        if (needed <= Buffer.Length) return true;
        int capacity = Buffer.Length;
        while (capacity < needed) capacity = Math.Min(ReplayRules.MaxLineCharacters, capacity * 2);
        var next = new char[capacity]; System.Array.Copy(Buffer, next, written); Buffer = next; return true;
    }
}
