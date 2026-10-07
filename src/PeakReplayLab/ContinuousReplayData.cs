using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace PeakReplayLab;

public sealed class ContinuousReplayOptions
{
    public double SegmentSeconds { get; set; } = 30;
    public int SegmentFrameLimit { get; set; } = 1801;
    public long SegmentDecodedByteLimit { get; set; } = 256L * 1024 * 1024;
    public long SegmentUncompressedByteLimit { get; set; } = 384L * 1024 * 1024;
    public int QueueFrameLimit { get; set; } = 360;
    public long QueueByteLimit { get; set; } = 128L * 1024 * 1024;
    public double MaxRunSeconds { get; set; } = 4 * 60 * 60;
    public long MaxRunBytes { get; set; } = 4L * 1024 * 1024 * 1024;
    public int FlushMilliseconds { get; set; } = 1000;

    internal ContinuousReplayOptions CopyChecked()
    {
        if (!ReplayRules.Finite(SegmentSeconds) || SegmentSeconds <= 0 || SegmentSeconds > 30 ||
            SegmentFrameLimit < 2 || SegmentFrameLimit > ReplayRules.MaxFrames ||
            SegmentDecodedByteLimit < 1024 || SegmentDecodedByteLimit > 384L * 1024 * 1024 ||
            SegmentUncompressedByteLimit < 1024 || SegmentUncompressedByteLimit > ReplayRules.MaxBytes ||
            QueueFrameLimit < 1 || QueueFrameLimit > ReplayRules.MaxFrames || QueueByteLimit < 1024 || QueueByteLimit > 512L * 1024 * 1024 ||
            !ReplayRules.Finite(MaxRunSeconds) || MaxRunSeconds <= 0 || MaxRunSeconds > 4 * 60 * 60 ||
            MaxRunBytes < 4096 || MaxRunBytes > 4L * 1024 * 1024 * 1024 || FlushMilliseconds < 50 || FlushMilliseconds > 5000)
            throw new ArgumentOutOfRangeException(nameof(ContinuousReplayOptions), "Invalid continuous replay safety limits.");
        return (ContinuousReplayOptions)MemberwiseClone();
    }
}

public sealed class ContinuousReplayStats
{
    public string State { get; internal set; } = "Recording";
    public long AcceptedFrames { get; internal set; }
    public long WrittenFrames { get; internal set; }
    public int QueuedFrames { get; internal set; }
    public long QueuedBytes { get; internal set; }
    public int CompletedSegments { get; internal set; }
    public long BytesWritten { get; internal set; }
    public long GapOver100ms { get; internal set; }
    public long GapOver500ms { get; internal set; }
    public double LargestGapSeconds { get; internal set; }
    public double NativeStart { get; internal set; }
    public double NativeEnd { get; internal set; }
}

public sealed class ContinuousReplayResult
{
    public string RunDirectory { get; internal set; } = "";
    public string Status { get; internal set; } = "completed";
    public string Reason { get; internal set; } = "manual-stop";
    public string? FaultCode { get; internal set; }
    public string? Fault { get; internal set; }
    public long AcceptedFrames { get; internal set; }
    public long WrittenFrames { get; internal set; }
    public long UnwrittenFrames { get; internal set; }
    public int CompletedSegments { get; internal set; }
    public long BytesWritten { get; internal set; }
    public long GapOver100ms { get; internal set; }
    public long GapOver500ms { get; internal set; }
    public double LargestGapSeconds { get; internal set; }
    public double NativeStart { get; internal set; }
    public double NativeEnd { get; internal set; }
}

// A segment owns weak translation caches, not a second 120-second recording
// buffer. Published capture frames remain immutable, including event sequences.
internal sealed class ContinuousReplayRebaser
{
    private sealed class Value<T> where T : class { public T Item = null!; }
    private sealed class Track<T> where T : class
    {
        private readonly ConditionalWeakTable<T, Value<T>> states = new();
        private readonly ConditionalWeakTable<T[], Value<T[]>> arrays = new();
        private readonly Func<T, double, T> rebase;
        private readonly double start;
        public Track(Func<T, double, T> rebase, double start) { this.rebase = rebase; this.start = start; }
        public T[] Apply(T[] source)
        {
            if (source.Length == 0) return source;
            return arrays.GetValue(source, values =>
            {
                var result = new T[values.Length];
                for (int i = 0; i < result.Length; i++)
                    result[i] = states.GetValue(values[i], value => new Value<T> { Item = rebase(value, start) }).Item;
                return new Value<T[]> { Item = result };
            }).Item;
        }
    }
    private readonly double origin;
    private readonly Track<CrateFrame> crates;
    private readonly Track<RopeReplayFrame> ropes;
    private readonly Track<EffectReplayFrame> effects;
    private readonly Track<AudioReplayFrame> audio;
    private readonly Track<SpawnedReplayFrame> spawned;
    private readonly Track<BalloonReplayFrame> balloons;
    private readonly ActorJointRebaser actors;
    private readonly EnvironmentTimeRebaser environment;
    public ContinuousReplayRebaser(double origin)
    {
        this.origin = origin;
        actors = new ActorJointRebaser(origin);
        environment = new EnvironmentTimeRebaser(origin);
        crates = new(CrateAnimationTimeline.Rebase, origin); ropes = new(RopeReplayRules.Rebase, origin);
        effects = new(EffectReplayRules.Rebase, origin); audio = new(AudioReplayRules.Rebase, origin);
        spawned = new(SpawnedReplayRules.Rebase, origin); balloons = new(BalloonReplayRules.Rebase, origin);
    }
    public ReplayFrame Apply(ReplayFrame source)
    {
        var events = new List<ItemEvent>();
        foreach (var value in source.Events)
            if (value.T >= origin) events.Add(new ItemEvent
            {
                Sequence = value.Sequence, T = value.T - origin, Kind = value.Kind, ActorId = value.ActorId,
                ItemKey = value.ItemKey, ItemId = value.ItemId, Name = value.Name,
            });
        return new ReplayFrame
        {
            Type = source.Type, T = source.T - origin, World = environment.Apply(source.World), Actors = actors.Apply(source.Actors), Items = source.Items,
            Crates = crates.Apply(source.Crates), Ropes = ropes.Apply(source.Ropes), Effects = effects.Apply(source.Effects),
            Audio = audio.Apply(source.Audio), Spawned = spawned.Apply(source.Spawned), Balloons = balloons.Apply(source.Balloons),
            Creatures = source.Creatures,
            Events = events.Count == 0 ? Array.Empty<ItemEvent>() : events.ToArray(),
        };
    }
}

// Mirrors the reader's conservative decoded-memory accounting. A removed then
// reintroduced object counts again: the decoder constructs another instance even
// if capture happens to reuse the old object reference. No actor interning/mutation.
internal sealed class ContinuousReplayBudget
{
    private sealed class Track<T> where T : class
    {
        private Dictionary<string, T> previous = new(StringComparer.Ordinal), next = new(StringComparer.Ordinal);
        private T[]? last;
        private readonly Func<T, string> key;
        private readonly Func<T, long> estimate;
        public Track(Func<T, string> key, Func<T, long> estimate) { this.key = key; this.estimate = estimate; }
        public long Additional(T[] values)
        {
            if (ReferenceEquals(values, last)) return 0;
            long result = 0;
            foreach (var value in values)
                if (!previous.TryGetValue(key(value), out var old) || !ReferenceEquals(old, value)) result = checked(result + estimate(value));
            return result;
        }
        public void Commit(T[] values)
        {
            if (ReferenceEquals(values, last)) return;
            next.Clear(); foreach (var value in values) next.Add(key(value), value);
            var spare = previous; previous = next; next = spare; last = values;
        }
    }
    private readonly Track<ItemFrame> items = new(x => x.Key, RollingBuffer.Estimate);
    private readonly Track<CrateFrame> crates = new(x => x.Key, RollingBuffer.Estimate);
    private readonly Track<RopeReplayFrame> ropes = new(x => x.Key, RopeReplayRules.Estimate);
    private readonly Track<EffectReplayFrame> effects = new(x => x.Key, EffectReplayRules.Estimate);
    private readonly Track<AudioReplayFrame> audio = new(x => x.Key, AudioReplayRules.Estimate);
    private readonly Track<SpawnedReplayFrame> spawned = new(x => x.Key, SpawnedReplayRules.Estimate);
    private readonly Track<BalloonReplayFrame> balloons = new(x => x.Key, BalloonReplayRules.Estimate);
    private readonly Track<CreatureReplayFrame> creatures = new(x => x.Key, CreatureReplayRules.Estimate);
    public long Bytes { get; private set; }
    public long Additional(ReplayFrame frame) => checked(RollingBuffer.EstimateFrame(frame) + items.Additional(frame.Items) +
        crates.Additional(frame.Crates) + ropes.Additional(frame.Ropes) + effects.Additional(frame.Effects) +
        audio.Additional(frame.Audio) + spawned.Additional(frame.Spawned) + balloons.Additional(frame.Balloons) + creatures.Additional(frame.Creatures));
    public void Commit(ReplayFrame frame, long additional)
    {
        items.Commit(frame.Items); crates.Commit(frame.Crates); ropes.Commit(frame.Ropes); effects.Commit(frame.Effects);
        audio.Commit(frame.Audio); spawned.Commit(frame.Spawned); balloons.Commit(frame.Balloons); creatures.Commit(frame.Creatures); Bytes = checked(Bytes + additional);
    }
}
