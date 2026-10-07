using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PeakReplayLab;

// A queue retains an immutable object graph, not N independent deep copies of
// each frame. Child edges are walked only when their parent is first retained;
// release walks them only after its last live reference disappears. The worker
// keeps its lease through serialization. The newest accepted capture also has
// one explicit baseline reference: an idle worker must not destroy the shared
// scene directory and make the producer rediscover it on the next capture.
// Baseline bytes remain inside the same bound; only pending leases count as
// frames. File I/O never holds this short lock.
internal sealed class ReplayQueueBudget
{
    internal readonly struct Snapshot
    {
        public readonly long Bytes, PeakBytes;
        public readonly int Frames, PeakFrames;
        public Snapshot(long bytes, int frames, long peakBytes, int peakFrames)
        { Bytes = bytes; Frames = frames; PeakBytes = peakBytes; PeakFrames = peakFrames; }
    }
    internal sealed class Reservation : IDisposable
    {
        private readonly ReplayQueueBudget owner;
        private readonly ReplayFrame frame;
        private int released;
        internal Reservation(ReplayQueueBudget owner, ReplayFrame frame) { this.owner = owner; this.frame = frame; }
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) owner.Release(frame); }
    }
    private sealed class Identity : IEqualityComparer<object>
    {
        public static readonly Identity Instance = new();
        public new bool Equals(object? a, object? b) => ReferenceEquals(a, b);
        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }
    private sealed class Node
    {
        public int References = 1, RetainedChildren;
        public readonly long Bytes;
        public readonly object[] Children;
        public Node(long bytes, object[] children) { Bytes = bytes; Children = children; }
    }
    private sealed class ByteOverflow : Exception
    {
        public readonly long AttemptedBytes;
        public ByteOverflow(long attemptedBytes) => AttemptedBytes = attemptedBytes;
    }
    private sealed class Shape
    {
        public readonly PropertyInfo[] References;
        public readonly long OwnBytes;
        public Shape(Type type)
        {
            if (type.Assembly != typeof(ReplayFrame).Assembly || type.Namespace != typeof(ReplayFrame).Namespace)
                throw new InvalidDataException("Queue frame contains an unsupported reference type.");
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var references = new List<PropertyInfo>();
            foreach (var property in properties)
                if (property.CanRead && property.GetIndexParameters().Length == 0 && !property.PropertyType.IsValueType) references.Add(property);
            References = references.ToArray();
            // Eight bytes per scalar/reference is deliberately conservative.
            OwnBytes = checked(32 + properties.Length * 8L);
        }
    }
    private static readonly ConcurrentDictionary<Type, Shape> shapes = new();
    private readonly object gate = new();
    private readonly Dictionary<object, Node> nodes = new(Identity.Instance);
    private ReplayFrame? baseline;
    private long bytes, peakBytes;
    private int frames, peakFrames;
    public Snapshot Current { get { lock (gate) return new(bytes, frames, peakBytes, peakFrames); } }

    public bool TryReserve(ReplayFrame frame, long byteLimit, int frameLimit, out Reservation? reservation, out Snapshot attempted)
    {
        lock (gate)
        {
            reservation = null;
            int proposedFrames = checked(frames + 1);
            if (proposedFrames > frameLimit)
            { attempted = new(bytes, proposedFrames, peakBytes, peakFrames); return false; }
            try { RetainFrame(frame, byteLimit); }
            catch (ByteOverflow overflow)
            {
                // The old baseline is a cache, never a pending capture. Reclaim
                // it once before rejecting a genuinely new bounded snapshot.
                // Worker/queued leases retain their own references and cannot
                // be evicted or hidden by this retry.
                if (baseline == null)
                { attempted = new(overflow.AttemptedBytes, proposedFrames, peakBytes, peakFrames); return false; }
                ClearBaselineCore();
                try { RetainFrame(frame, byteLimit); }
                catch (ByteOverflow retry)
                { attempted = new(retry.AttemptedBytes, proposedFrames, peakBytes, peakFrames); return false; }
            }
            peakBytes = Math.Max(peakBytes, bytes);
            Retain(frame, byteLimit); // Existing root: acquire baseline without walking its children again.
            var oldBaseline = baseline; baseline = frame;
            if (oldBaseline != null) ReleaseNode(oldBaseline);
            frames = proposedFrames; peakFrames = Math.Max(peakFrames, frames);
            attempted = new(bytes, proposedFrames, peakBytes, peakFrames);
            reservation = new Reservation(this, frame); return true;
        }
    }
    // Writers call this only after captures have stopped and all worker/queue
    // leases have drained. It is safe and idempotent even after a write fault.
    public void ClearBaseline() { lock (gate) ClearBaselineCore(); }
    private void ClearBaselineCore()
    { var oldBaseline = baseline; baseline = null; if (oldBaseline != null) ReleaseNode(oldBaseline); }
    private void RetainFrame(ReplayFrame frame, long byteLimit)
    {
        Retain(frame, byteLimit);
        // Retaining an existing root can also cross a newly lowered limit.
        if (bytes > byteLimit) { long total = bytes; ReleaseNode(frame); throw new ByteOverflow(total); }
    }
    private void Release(ReplayFrame frame)
    { lock (gate) { ReleaseNode(frame); frames--; } }
    private void Retain(object value, long byteLimit)
    {
        if (nodes.TryGetValue(value, out var old)) { old.References = checked(old.References + 1); return; }
        var node = Describe(value);
        long total = checked(bytes + node.Bytes);
        // Stop discovering new edges as soon as the bound is crossed; rollback
        // unwinds only acquired edges. A bad hint cannot build an unbounded
        // temporary memoization graph before admission is finally rejected.
        if (total > byteLimit) throw new ByteOverflow(total);
        nodes.Add(value, node); bytes = total;
        try
        {
            for (int i = 0; i < node.Children.Length; i++)
            { var child = node.Children[i]; if (child != null) Retain(child, byteLimit); node.RetainedChildren = i + 1; }
        }
        catch { ReleaseNode(value); throw; }
    }
    private void ReleaseNode(object value)
    {
        var node = nodes[value];
        if (--node.References != 0) return;
        nodes.Remove(value); bytes -= node.Bytes;
        for (int i = 0; i < node.RetainedChildren; i++) if (node.Children[i] != null) ReleaseNode(node.Children[i]);
    }
    private static Node Describe(object value)
    {
        object[] children; long own;
        if (value is string text) { own = checked(32 + text.Length * 2L); children = Array.Empty<object>(); }
        else if (value is Array array)
        {
            var element = array.GetType().GetElementType()!;
            if (element.IsValueType)
            {
                int width = element == typeof(bool) || element == typeof(byte) || element == typeof(sbyte) ? 1 :
                    element == typeof(short) || element == typeof(ushort) || element == typeof(char) ? 2 :
                    element == typeof(float) || element == typeof(int) || element == typeof(uint) ? 4 : 8;
                own = checked(32 + array.LongLength * width); children = Array.Empty<object>();
            }
            else
            {
                // These source arrays are immutable. Reuse their child edges
                // directly instead of allocating another directory-sized copy.
                children = (object[])array;
                own = checked(32 + array.LongLength * 8);
            }
        }
        else
        {
            var shape = shapes.GetOrAdd(value.GetType(), type => new Shape(type));
            var references = new List<object>(shape.References.Length);
            foreach (var property in shape.References) { var child = property.GetValue(value); if (child != null) references.Add(child); }
            children = references.ToArray(); own = shape.OwnBytes;
        }
        // Include the reference table, node and cached child-edge array itself,
        // so memoization remains part of the bounded retained-memory estimate.
        return new Node(checked(own + 160 + children.Length * 8L), children);
    }
}
