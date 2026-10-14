using System;
using System.Collections.Generic;

namespace PeakReplayLab;

internal sealed class EffectCheckpointPlaybackPlan
{
    internal readonly EffectParticleFrame[] Particles;
    internal readonly double ObservedAt, Phase;
    internal EffectCheckpointPlaybackPlan(EffectParticleFrame[] particles, double observedAt, double phase)
    { Particles = particles; ObservedAt = observedAt; Phase = phase; }
    internal static bool LegacySupported(double phase) => !double.IsNaN(phase) && !double.IsInfinity(phase) && phase >= 0 &&
        phase <= EffectParticleCheckpointRules.MaximumLegacyPhase;
    internal static bool TryCreate(EffectParticleFrame[]? particles, string[] paths, double targetTime, double targetPhase,
        out EffectCheckpointPlaybackPlan? plan, bool boundedTail = true)
    {
        plan = null;
        if (particles == null || particles.Length == 0 || particles.Length != paths.Length) return false;
        var first = particles[0]?.Checkpoint;
        if (first == null || first.ObservedAt > targetTime + 1e-7 || first.RootPhase > targetPhase + 1e-7 ||
            boundedTail && targetPhase > first.RootPhase + 1e-7 && targetTime - first.ObservedAt > 1.00001) return false;
        int total = 0;
        foreach (var p in particles) if (p == null || p.Checkpoint == null) return false;
        for (int i = 0; i < particles.Length; i++)
        {
            var p = particles[i]; var cp = p.Checkpoint!;
            if (p.Pose.Path != paths[i] || cp.ObservedAt != first.ObservedAt || cp.RootPhase != first.RootPhase || cp.Layout != first.Layout)
                return false;
            EffectParticleCheckpointRules.Validate(cp); total += cp.Count;
            if (total > EffectParticleCheckpointRules.MaximumParticles) return false;
        }
        plan = new(particles, first.ObservedAt, first.RootPhase); return true;
    }
}

// Only completed native states enter this small cache. Entries retain immutable
// replay data, never gameplay objects or an unbounded timeline of particles.
internal sealed class EffectParticleWarmCache
{
    private sealed class Entry
    {
        internal object Owner = null!;
        internal EffectReplayFrame Frame = null!;
        internal long Bytes;
        internal bool Approximate;
    }
    private readonly LinkedList<Entry> entries = new();
    private readonly long limit;
    private readonly int perOwner;
    private long bytes;
    internal EffectParticleWarmCache(long limit = 16 * 1024 * 1024, int perOwner = 4)
    { this.limit = limit; this.perOwner = perOwner; }
    internal long Bytes => bytes;
    internal int Count => entries.Count;
    internal bool LastWasApproximate { get; private set; }
    internal void Add(object owner, EffectReplayFrame frame, bool approximate = false)
    {
        var ps = frame.Particles; if (ps == null || ps.Length != 1 || ps[0].Checkpoint == null) return;
        long size = 1024 + EffectParticleCheckpointRules.Estimate(ps[0].Checkpoint!);
        if (size > limit) return;
        int owned = 0;
        for (var node = entries.Last; node != null;)
        {
            var old = node; node = node.Previous;
            if (!ReferenceEquals(old.Value.Owner, owner)) continue;
            owned++;
            if (old.Value.Frame.Particles![0].Checkpoint!.ObservedAt == ps[0].Checkpoint!.ObservedAt || owned >= perOwner) Remove(old);
        }
        while (entries.Count != 0 && bytes + size > limit) Remove(entries.First!);
        entries.AddLast(new Entry { Owner = owner, Frame = frame, Bytes = size, Approximate = approximate }); bytes += size;
    }
    internal EffectReplayFrame? Find(object owner, EffectReplayFrame current, string[] paths, double time, double phase)
    {
        LinkedListNode<Entry>? best = null;
        LastWasApproximate = false;
        foreach (var entry in entries)
        {
            if (!ReferenceEquals(entry.Owner, owner) || entry.Frame.Key != current.Key || entry.Frame.Resource != current.Resource ||
                entry.Frame.Seed != current.Seed || !EffectReplayRules.SameSeeds(entry.Frame.Seeds, current.Seeds) ||
                !EffectCheckpointPlaybackPlan.TryCreate(entry.Frame.Particles, paths, time, phase, out var plan, false)) continue;
            if (best == null || plan!.Phase > best.Value.Frame.Particles![0].Checkpoint!.RootPhase)
                best = entries.Find(entry);
        }
        if (best == null) return null;
        LastWasApproximate = best.Value.Approximate;
        var value = best.Value.Frame; entries.Remove(best); entries.AddLast(best); return value;
    }
    private void Remove(LinkedListNode<Entry> node) { bytes -= node.Value.Bytes; entries.Remove(node); }
    internal void RemoveOwner(object owner)
    { for (var node = entries.First; node != null;) { var old = node; node = node.Next; if (ReferenceEquals(old.Value.Owner, owner)) Remove(old); } }
    internal void Clear() { entries.Clear(); bytes = 0; }
}
