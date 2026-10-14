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
    private readonly WeakReference<T[]> lastValidated = new(null!);
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
        // A moving entity replaces its immutable state, not the other hundreds
        // of directory entries. Reuse their validated identities and states.
        // Keep only a weak predecessor so stopping capture retains no hierarchy.
        if (lastValidated.TryGetTarget(out var previous) && SameIdentities(previous, values))
        {
            for (int i = 0; i < values.Length; i++)
                if (!ReferenceEquals(previous[i], values[i])) states.GetValue(values[i], checkState);
            validateCollection?.Invoke(values);
            lastValidated.SetTarget(values); return Valid;
        }
        identities.Clear();
        foreach (var value in values)
        {
            if (value == null || !identities.Add(key(value))) throw new InvalidDataException("Duplicate or null presentation entity.");
            states.GetValue(value, checkState);
        }
        validateCollection?.Invoke(values);
        identities.Clear(); lastValidated.SetTarget(values); return Valid;
    }
    private bool SameIdentities(T[] previous, T[] values)
    {
        if (previous.Length != values.Length) return false;
        for (int i = 0; i < values.Length; i++)
            if (!ReferenceEquals(previous[i], values[i]) && (values[i] == null || key(previous[i]) != key(values[i]))) return false;
        return true;
    }
}

internal sealed class ReplayTrackBudget<T> where T : class
{
    private sealed class SharedIdentity : IEqualityComparer<object>
    {
        internal static readonly SharedIdentity Instance = new();
        public new bool Equals(object? a, object? b) => ReferenceEquals(a, b);
        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }
    private readonly Dictionary<T, (int Count, long Bytes, long Estimate)> states = new();
    private readonly Dictionary<T[], (int Count, long Bytes)> arrays = new();
    private readonly Func<T, long> estimate;
    private readonly Action<T, Action<object, long>>? visitShared;
    private readonly Dictionary<object, (int Count, long Bytes)>? shared;
    private readonly Action<object, long> retainShared, releaseShared;
    private long visitingBytes;
    public long Bytes { get; private set; }
    public ReplayTrackBudget(Func<T, long> estimate, Action<T, Action<object, long>>? visitShared = null)
    {
        this.estimate = estimate; this.visitShared = visitShared;
        if (visitShared != null) shared = new(SharedIdentity.Instance);
        retainShared = RetainShared; releaseShared = ReleaseShared;
    }
    public long Acquire(T[] values)
    {
        if (arrays.TryGetValue(values, out var array)) { arrays[values] = (array.Count + 1, array.Bytes); return array.Bytes; }
        long size = 0;
        foreach (var value in values)
        {
            if (states.TryGetValue(value, out var old)) { states[value] = (old.Count + 1, old.Bytes, old.Estimate); size += old.Estimate; }
            else
            {
                long bytes = estimate(value); visitingBytes = bytes;
                visitShared?.Invoke(value, retainShared);
                states.Add(value, (1, bytes, visitingBytes)); Bytes += bytes; size += visitingBytes;
            }
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
            if (old.Count > 1) states[value] = (old.Count - 1, old.Bytes, old.Estimate);
            else { states.Remove(value); Bytes -= old.Bytes; visitShared?.Invoke(value, releaseShared); }
        }
    }
    private void RetainShared(object value, long bytes)
    {
        visitingBytes += bytes;
        if (shared!.TryGetValue(value, out var old)) shared[value] = (old.Count + 1, old.Bytes);
        else { shared.Add(value, (1, bytes)); Bytes += bytes; }
    }
    private void ReleaseShared(object value, long bytes)
    {
        var old = shared![value];
        if (old.Count > 1) shared[value] = (old.Count - 1, old.Bytes);
        else { shared.Remove(value); Bytes -= old.Bytes; }
    }
}

internal sealed class ReplayTrackRebaser<T> where T : class
{
    private readonly BoundedReferenceCache<T, T> states = new(1024);
    private readonly BoundedReferenceCache<T[], T[]> arrays = new(128);
    private readonly Func<T, double, T> rebase;
    private readonly Func<T, T> stateFactory;
    private readonly Func<T[], T[]> arrayFactory;
    private readonly double start;
    public ReplayTrackRebaser(Func<T, double, T> rebase, double start)
    { this.rebase = rebase; this.start = start; stateFactory = ShiftState; arrayFactory = ShiftArray; }
    public T[] Apply(T[] values)
    {
        if (values.Length == 0) return values;
        return arrays.GetValue(values, arrayFactory);
    }
    private T ShiftState(T source) => rebase(source, start);
    private T[] ShiftArray(T[] values)
    {
        var result = new T[values.Length];
        for (int i = 0; i < values.Length; i++) result[i] = states.GetValue(values[i], stateFactory);
        return result;
    }
}
