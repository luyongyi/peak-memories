using System;
using System.Collections.Generic;

namespace PeakReplayLab;

// Separate expensive metadata polling from motion and native shader animation.
// Object-specific deadlines spread steady-state fallback work across frames;
// observed native changes and first snapshots always bypass the deadline.
internal sealed class NativeCapturePolicy
{
    private readonly double spread;
    private double nextFull = double.NegativeInfinity, nextAnimated = double.NegativeInfinity;
    private double lastAnimated = double.NegativeInfinity;
    public NativeCapturePolicy(int identity) => spread = Math.Abs(identity % 29) / 29d * .25;
    public bool FullDue(double now, bool hasBaseline, bool dirty) => !hasBaseline || dirty || now >= nextFull;
    public bool AnimatedDue(double now, bool followSceneClock = false) =>
        followSceneClock ? now > lastAnimated + 1e-8 : now + 1e-8 >= nextAnimated;
    public bool NeedsRead(double now, bool active, bool animated, bool followSceneClock = false) =>
        FullDue(now, true, false) || active && animated && AnimatedDue(now, followSceneClock);
    public void FullObserved(double now) { nextFull = now + .5 + spread; AnimatedObserved(now); }
    public void AnimatedObserved(double now) { lastAnimated = now; nextAnimated = now + 1d / 30; }
}

internal static class StaticCreatureCapturePolicy
{
    public static bool DynamicDue(bool hasBaseline, bool active, bool wasActive, double now, double nextSample) =>
        !hasBaseline || active != wasActive || active && now + 1e-8 >= nextSample;
}

// Static native objects are read only at birth or after a real native event.
// The cached immutable array is also the seek/page checkpoint; polling a live
// Unity hierarchy is never necessary merely because recording time advanced.
internal sealed class StaticCaptureDirectory<TSource, TFrame> where TSource : class where TFrame : class
{
    private sealed class Entry
    {
        public int Key;
        public long Generation;
        public long WorkVersion;
        public TSource Source = null!;
        public TFrame? Frame;
        public bool Pending, Urgent;
        public int Failures;
        public double RetryAt = double.NegativeInfinity;
    }
    private readonly Dictionary<int, Entry> entries = new();
    // Queued work must not keep retired native bindings/frames alive. Generation
    // stamps also reject an old queue item after Unity reuses its instance ID.
    private readonly struct Work
    {
        public readonly int Key;
        public readonly long Generation;
        public readonly long Version;
        public Work(Entry entry) { Key = entry.Key; Generation = entry.Generation; Version = entry.WorkVersion; }
    }
    private readonly Queue<Work> pending = new();
    private readonly Queue<Work> urgent = new();
    private readonly SortedList<double, List<Work>> retries = new();
    private readonly List<TFrame> frames = new();
    private TFrame[] snapshot = Array.Empty<TFrame>();
    private bool snapshotDirty;
    private long generation;
    private int initialPending;
    public int Count => entries.Count;
    public bool InitialBaselineReady => initialPending == 0;
    public bool Contains(int key) => entries.ContainsKey(key);
    public bool Register(int key, TSource source, bool urgent = false)
    {
        if (entries.ContainsKey(key)) return false;
        var entry = new Entry { Key = key, Generation = ++generation, WorkVersion = 1, Source = source, Pending = true, Urgent = urgent };
        entries.Add(key, entry); initialPending++;
        if (urgent) this.urgent.Enqueue(new Work(entry)); else pending.Enqueue(new Work(entry));
        return true;
    }
    public bool Changed(int key, bool urgent = false)
    {
        if (!entries.TryGetValue(key, out var entry)) return false;
        entry.Failures = 0; entry.RetryAt = double.NegativeInfinity;
        bool promote = urgent && !entry.Urgent; entry.Urgent |= urgent;
        if (promote || !entry.Pending) entry.WorkVersion++;
        if (promote) this.urgent.Enqueue(new Work(entry));
        if (!entry.Pending) { entry.Pending = true; if (!urgent) pending.Enqueue(new Work(entry)); }
        return true;
    }
    public bool Remove(int key)
    {
        if (!entries.TryGetValue(key, out var entry)) return false;
        entries.Remove(key);
        if (entry.Frame == null) initialPending--;
        if (entry.Frame != null)
        {
            snapshotDirty = true; snapshot = Array.Empty<TFrame>(); frames.Clear();
        }
        return true;
    }
    private bool Current(Entry entry) => entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(entry, current);
    private Entry? Current(Work work) => entries.TryGetValue(work.Key, out var entry) && entry.Generation == work.Generation && entry.WorkVersion == work.Version ? entry : null;
    public TFrame[] Capture(double now, Func<TSource, TFrame?, TFrame?> read, Action<TSource, Exception>? error = null,
        Func<bool>? beginInitialRead = null)
    {
        // Failed native initialization gets three spaced retries. These checks
        // inspect only managed deadlines; no static object is touched while cold.
        while (retries.Count != 0 && retries.Keys[0] <= now)
        {
            double due = retries.Keys[0]; var dueEntries = retries.Values[0]; retries.RemoveAt(0);
            foreach (var work in dueEntries)
            {
                var entry = Current(work);
                if (entry != null && entry.RetryAt == due && !entry.Pending)
                { entry.Pending = true; pending.Enqueue(work); }
            }
        }
        // A real explosion must not sit behind a deferred initial checkpoint
        // when an earlier track used the shared loading budget.
        int count = pending.Count, urgentCount = urgent.Count;
        while (urgentCount-- > 0)
        {
            var entry = Current(urgent.Dequeue());
            if (entry != null && entry.Pending && entry.Urgent) ReadEntry(entry, now, read, error);
        }
        while (count-- > 0)
        {
            var entry = Current(pending.Dequeue());
            if (entry == null || !entry.Pending) continue;
            if (entry.Frame == null && !entry.Urgent && beginInitialRead != null && !beginInitialRead())
            { pending.Enqueue(new Work(entry)); break; }
            ReadEntry(entry, now, read, error);
        }
        if (!snapshotDirty) return snapshot;
        frames.Clear(); foreach (var entry in entries.Values) if (entry.Frame != null) frames.Add(entry.Frame);
        snapshotDirty = false; return snapshot = frames.ToArray();
    }
    private void ReadEntry(Entry entry, double now, Func<TSource, TFrame?, TFrame?> read, Action<TSource, Exception>? error)
    {
            // Remove the flag before reading: a trigger during that read must
            // survive as work for the following Capture, not be acknowledged away.
            entry.Pending = false; entry.Urgent = false; entry.RetryAt = double.NegativeInfinity;
            try
            {
                var value = read(entry.Source, entry.Frame);
                if (!Current(entry)) return;
                // A queued source can die before its native read. Null retires
                // its directory slot; a consumed trap instead has Active=false.
                if (value == null) { Remove(entry.Key); return; }
                entry.Failures = 0;
                if (ReferenceEquals(value, entry.Frame)) return;
                if (entry.Frame == null) initialPending--;
                entry.Frame = value; snapshotDirty = true;
            }
            catch (Exception exception)
            {
                if (Current(entry) && !entry.Pending && ++entry.Failures <= 3)
                {
                    double due = now + (entry.Failures == 1 ? .25 : entry.Failures == 2 ? 1 : 4);
                    entry.RetryAt = due;
                    if (!retries.TryGetValue(due, out var list)) retries.Add(due, list = new List<Work>());
                    list.Add(new Work(entry));
                }
                try { error?.Invoke(entry.Source, exception); } catch { }
            }
    }
    public void Clear()
    { entries.Clear(); pending.Clear(); urgent.Clear(); retries.Clear(); frames.Clear(); snapshot = Array.Empty<TFrame>(); snapshotDirty = false; initialPending = 0; }
}
