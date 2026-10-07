using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace PeakReplayLab;

// Shared immutable presentation tracks. Capture uses weak validation caches;
// every save/read owns fresh caches so untrusted files cannot reuse capture trust.
internal sealed class ReplayTrackValidation<T> where T : class
{
    private readonly ConditionalWeakTable<T, object> states = new();
    private readonly ConditionalWeakTable<T[], object> arrays = new();
    private readonly HashSet<string> identities = new(StringComparer.Ordinal);
    private readonly Func<T, string> key;
    private readonly Action<T> validate;
    private readonly Action<T[]>? validateCollection;
    private readonly int maximum;
    private readonly ConditionalWeakTable<T, object>.CreateValueCallback checkState;
    private readonly ConditionalWeakTable<T[], object>.CreateValueCallback checkArray;
    private static readonly object Valid = new();
    public ReplayTrackValidation(Func<T, string> key, int maximum, Action<T> validate, Action<T[]>? validateCollection = null)
    { this.key = key; this.maximum = maximum; this.validate = validate; this.validateCollection = validateCollection; checkState = State; checkArray = Array; }
    public void Validate(T[]? values)
    {
        if (values == null || values.Length > maximum) throw new InvalidDataException("Invalid presentation track count.");
        arrays.GetValue(values, checkArray);
    }
    private object State(T value) { validate(value); return Valid; }
    private object Array(T[] values)
    {
        identities.Clear();
        foreach (var value in values)
        {
            if (value == null || !identities.Add(key(value))) throw new InvalidDataException("Duplicate or null presentation entity.");
            states.GetValue(value, checkState);
        }
        validateCollection?.Invoke(values);
        identities.Clear(); return Valid;
    }
}

internal sealed class ReplayTrackBudget<T> where T : class
{
    private readonly Dictionary<T, (int Count, long Bytes)> states = new();
    private readonly Dictionary<T[], (int Count, long Bytes)> arrays = new();
    private readonly Func<T, long> estimate;
    public long Bytes { get; private set; }
    public ReplayTrackBudget(Func<T, long> estimate) => this.estimate = estimate;
    public long Acquire(T[] values)
    {
        if (arrays.TryGetValue(values, out var array)) { arrays[values] = (array.Count + 1, array.Bytes); return array.Bytes; }
        long size = 0;
        foreach (var value in values)
        {
            if (states.TryGetValue(value, out var old)) { states[value] = (old.Count + 1, old.Bytes); size += old.Bytes; }
            else { long bytes = estimate(value); states.Add(value, (1, bytes)); Bytes += bytes; size += bytes; }
        }
        arrays.Add(values, (1, size)); return size;
    }
    public void Release(T[] values)
    {
        var array = arrays[values];
        if (array.Count > 1) { arrays[values] = (array.Count - 1, array.Bytes); return; }
        arrays.Remove(values);
        foreach (var value in values)
        {
            var old = states[value];
            if (old.Count > 1) states[value] = (old.Count - 1, old.Bytes);
            else { states.Remove(value); Bytes -= old.Bytes; }
        }
    }
}

internal sealed class ReplayTrackRebaser<T> where T : class
{
    private readonly Dictionary<T, T> states = new();
    private readonly Dictionary<T[], T[]> arrays = new();
    private readonly Func<T, double, T> rebase;
    private readonly double start;
    public ReplayTrackRebaser(Func<T, double, T> rebase, double start) { this.rebase = rebase; this.start = start; }
    public T[] Apply(T[] values)
    {
        if (values.Length == 0) return values;
        if (arrays.TryGetValue(values, out var found)) return found;
        var result = new T[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            if (!states.TryGetValue(values[i], out var value)) { value = rebase(values[i], start); states.Add(values[i], value); }
            result[i] = value;
        }
        arrays.Add(values, result); return result;
    }
}
