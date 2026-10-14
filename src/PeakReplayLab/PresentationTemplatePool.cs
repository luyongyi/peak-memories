using System;
using System.Collections.Generic;

namespace PeakReplayLab;

// A cost bound prevents sixteen large skeletons from being treated as sixteen
// cheap props. The pool owns only completed, hidden, detached visual instances.
internal sealed class PresentationTemplatePool<T> where T : class
{
    private readonly List<(string Template, T Value, int Cost)> entries = new();
    private readonly int maximumCount, maximumCost;
    private readonly Action<T> retire;
    public int Count => entries.Count;
    public int Cost { get; private set; }
    public PresentationTemplatePool(int maximumCount, int maximumCost, Action<T> retire)
    {
        if (maximumCount < 0 || maximumCost < 0) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        this.maximumCount = maximumCount; this.maximumCost = maximumCost; this.retire = retire;
    }
    public T? Take(string template, Predicate<T>? preferred = null, Predicate<T>? reusable = null)
    {
        if (preferred != null) for (int i = entries.Count - 1; i >= 0; i--)
            if (entries[i].Template == template && preferred(entries[i].Value))
            { var entry = entries[i]; entries.RemoveAt(i); Cost -= entry.Cost; return entry.Value; }
        for (int i = entries.Count - 1; i >= 0; i--)
            if (entries[i].Template == template && (reusable == null || reusable(entries[i].Value)))
            { var entry = entries[i]; entries.RemoveAt(i); Cost -= entry.Cost; return entry.Value; }
        return null;
    }
    public void Return(string template, T value, int cost)
    {
        cost = Math.Max(1, cost);
        if (maximumCount == 0 || cost > maximumCost) { retire(value); return; }
        while (entries.Count > 0 && (entries.Count >= maximumCount || Cost > maximumCost - cost))
        { var old = entries[0]; entries.RemoveAt(0); Cost -= old.Cost; retire(old.Value); }
        entries.Add((template, value, cost)); Cost += cost;
    }
    public void Clear()
    { foreach (var entry in entries) retire(entry.Value); entries.Clear(); Cost = 0; }
}
