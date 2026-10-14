using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace PeakReplayLab;

// One save/read worker owns each instance. Explicit LRU bounds avoid both
// multi-hour strong retention and weak-table runtime lookups.
// Identity comparison is intentional: captured samples are immutable versions,
// and value-equal samples can still carry distinct observation clocks.
internal sealed class BoundedReferenceCache<TKey, TValue> where TKey : class where TValue : class
{
    private sealed class Identity : IEqualityComparer<TKey>
    {
        internal static readonly Identity Instance = new();
        public bool Equals(TKey? a, TKey? b) => ReferenceEquals(a, b);
        public int GetHashCode(TKey value) => RuntimeHelpers.GetHashCode(value);
    }
    private readonly struct Entry
    {
        internal readonly TKey Key;
        internal readonly TValue Value;
        internal Entry(TKey key, TValue value) { Key = key; Value = value; }
    }
    private readonly Dictionary<TKey, LinkedListNode<Entry>> entries;
    private readonly LinkedList<Entry> order = new();
    internal int Capacity { get; }
    internal int Count => entries.Count;
    internal BoundedReferenceCache(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        entries = new Dictionary<TKey, LinkedListNode<Entry>>(Math.Min(capacity, 256), Identity.Instance);
    }
    internal bool TryGetValue(TKey key, out TValue value)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (entries.TryGetValue(key, out var node))
        {
            if (!ReferenceEquals(node, order.Last)) { order.Remove(node); order.AddLast(node); }
            value = node.Value.Value; return true;
        }
        value = null!; return false;
    }
    internal TValue GetValue(TKey key, Func<TKey, TValue> factory)
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        if (TryGetValue(key, out var present)) return present;
        // Compute before eviction. A failed conversion leaves every admitted
        // immutable value available for the next frame/retry.
        var value = factory(key) ?? throw new InvalidOperationException("Reference cache factory returned null.");
        if (TryGetValue(key, out present)) return present; // bounded reentrant factory
        Insert(key, value); return value;
    }
    internal void Add(TKey key, TValue value)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (value == null) throw new ArgumentNullException(nameof(value));
        if (entries.ContainsKey(key)) throw new ArgumentException("Reference cache already contains this exact key.", nameof(key));
        Insert(key, value);
    }
    private void Insert(TKey key, TValue value)
    {
        LinkedListNode<Entry> node;
        if (entries.Count == Capacity)
        {
            node = order.First!; order.RemoveFirst(); entries.Remove(node.Value.Key);
            // Reuse LRU bookkeeping after warm-up; only the translated model
            // allocates when a new observation enters the bounded cache.
            node.Value = new Entry(key, value);
        }
        else node = new LinkedListNode<Entry>(new Entry(key, value));
        entries.Add(key, node); order.AddLast(node);
    }
    internal void Clear() { entries.Clear(); order.Clear(); }
}
