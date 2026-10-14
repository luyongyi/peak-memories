using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PeakReplayLab;

// Schema 10 joints carry their own observation clock. Expanding a held low-rate
// pose into 60 Hz frames must never turn its motion into hold-then-fast-jump.
// This index is immutable and independent of playback/seek history.
internal sealed class ActorJointTimeline
{
    internal readonly struct Key
    {
        public readonly double Time;
        public readonly NodePose Pose;
        public Key(double time, NodePose pose) { Time = time; Pose = pose; }
    }

    internal sealed class Segment
    {
        public readonly double Start;
        public double End = double.PositiveInfinity;
        public bool Closed;
        public readonly NodePose[] Topology;
        public readonly List<Key>[] Nodes;
        private readonly bool earlierBaseline;
        private bool observed;
        private double latestCommonSample = double.PositiveInfinity;
        private double predecessorUntil = double.NegativeInfinity;
        public ActorFrame Last;
        public Segment(double start, ActorFrame actor, bool earlierBaseline)
        {
            Start = start; Topology = actor.JointPose; Last = actor; this.earlierBaseline = earlierBaseline;
            Nodes = new List<Key>[Topology.Length];
            for (int i = 0; i < Nodes.Length; i++) Nodes[i] = new List<Key>();
        }
        public void Observe(ActorFrame actor, double frameTime)
        {
            // The decoder shares immutable held poses between main frames.
            // Their clocks and node topology were checked on first sight.
            if (observed && ReferenceEquals(Last.JointPose, actor.JointPose)) { Last = actor; return; }
            latestCommonSample = double.PositiveInfinity;
            for (int i = 0; i < Nodes.Length; i++)
            {
                var pose = actor.JointPose[i];
                if (!ReplayRules.Finite(pose.SampleTime) || pose.SampleTime > frameTime + .000001)
                    throw new InvalidDataException("人物关节采样时间无效。");
                // A page's first full baseline can predate its first frame. A
                // real cut, however, must not inherit a prior interval's clock.
                double time = earlierBaseline ? pose.SampleTime : Math.Max(Start, pose.SampleTime);
                var keys = Nodes[i];
                if (keys.Count == 0 || time > keys[keys.Count - 1].Time) keys.Add(new Key(time, pose));
                latestCommonSample = Math.Min(latestCommonSample, keys[keys.Count - 1].Time);
            }
            Last = actor; observed = true;
        }
        // TryNode only buffers beyond a node's last real observation in an
        // open segment. Store that shared bound once instead of repeating a
        // binary search for every node just to ask whether the pose is ready.
        public bool CanSample(double time) => Closed || time <= latestCommonSample + .000001;
        public bool RequiresPredecessor(double time)
            => earlierBaseline && time < predecessorUntil;
        public long Seal()
        {
            long bytes = 256 + Nodes.Length * 64L;
            for (int i = 0; i < Nodes.Length; i++)
            {
                var keys = Nodes[i];
                bytes += keys.Capacity * 16L;
                if (!earlierBaseline || keys.Count == 0 || keys[0].Time >= Start - .000001) continue;
                double until = double.PositiveInfinity;
                foreach (var key in keys)
                    if (key.Time >= Start - .000001) { until = key.Time; break; }
                predecessorUntil = Math.Max(predecessorUntil, until);
            }
            return bytes;
        }
    }

    private readonly Dictionary<string, List<Segment>> actors = new(StringComparer.Ordinal);
    public EnvironmentReplayTimeline Environment { get; }
    internal RopeObservationTimeline Ropes { get; }
    internal CreatureObservationTimeline Creatures { get; }
    internal ItemObservationTimeline Items { get; }
    internal SceneObservationTimeline Scene { get; }
    internal long EstimatedIndexBytes { get; }

    public ActorJointTimeline(IEnumerable<ReplayFrame> frames, bool knownEnd, CancellationToken cancellation = default)
    {
        var active = new Dictionary<string, Segment>(StringComparer.Ordinal);
        var present = new HashSet<string>(StringComparer.Ordinal);
        var absent = new List<string>();
        double previousTime = double.NegativeInfinity;
        bool first = true;
        var environment = new EnvironmentReplayTimeline.Builder();
        var ropes = new RopeObservationTimeline.Builder();
        var creatures = new CreatureObservationTimeline.Builder();
        var items = new ItemObservationTimeline.Builder();
        var scene = new SceneObservationTimeline.Builder();
        foreach (var frame in frames)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!ReplayRules.Finite(frame.T)) throw new InvalidDataException("人物关节帧时间无效。");
            // Adjacent seek pages may repeat their shared boundary frame.
            if (frame.T == previousTime) continue;
            if (frame.T < previousTime) throw new InvalidDataException("人物关节时间轴没有排序。");
            environment.Observe(frame);
            ropes.Observe(frame);
            creatures.Observe(frame);
            items.Observe(frame);
            scene.Observe(frame);
            present.Clear();
            foreach (var actor in frame.Actors)
            {
                present.Add(actor.Id);
                bool continuation = active.TryGetValue(actor.Id, out var current) &&
                    frame.T - previousTime <= .5 &&
                    (ReferenceEquals(current.Last.JointPose, actor.JointPose) || ActorJointReplayRules.SameTopology(current.Topology, actor.JointPose)) &&
                    !Teleported(current.Last, actor);
                if (!continuation)
                {
                    if (current != null) { current.End = frame.T; current.Closed = true; }
                    current = new Segment(frame.T, actor, first);
                    if (!actors.TryGetValue(actor.Id, out var ranges)) actors.Add(actor.Id, ranges = new List<Segment>());
                    ranges.Add(current); active[actor.Id] = current;
                }
                current!.Observe(actor, frame.T);
            }
            absent.Clear();
            foreach (var pair in active)
                if (!present.Contains(pair.Key)) { pair.Value.End = frame.T; pair.Value.Closed = true; absent.Add(pair.Key); }
            foreach (string id in absent) active.Remove(id);
            first = false; previousTime = frame.T;
        }
        if (knownEnd) foreach (var segment in active.Values) segment.Closed = true;
        Environment = environment.Build(knownEnd);
        Ropes = ropes.Build(knownEnd);
        Creatures = creatures.Build(knownEnd);
        Items = items.Build(knownEnd);
        Scene = scene.Build(knownEnd);
        long bytes = 1024 + Environment.EstimatedIndexBytes + Ropes.EstimatedIndexBytes + Creatures.EstimatedIndexBytes + Items.EstimatedIndexBytes + Scene.EstimatedIndexBytes;
        foreach (var ranges in actors.Values) foreach (var segment in ranges) bytes += segment.Seal();
        EstimatedIndexBytes = bytes;
    }

    private static bool Teleported(ActorFrame left, ActorFrame right)
    {
        double squared = 0;
        for (int i = 0; i < 3; i++) { double delta = right.Position[i] - left.Position[i]; squared += delta * delta; }
        return squared > 400;
    }

    public bool TryActor(string actorId, double time, out ActorJointPoseSample sample)
    {
        sample = default;
        if (!TrySegment(actorId, time, out var segment) || !segment.CanSample(time)) return false;
        sample = new ActorJointPoseSample(segment, time); return true;
    }

    private bool TrySegment(string actorId, double time, out Segment segment)
    {
        segment = null!;
        if (!ReplayRules.Finite(time)) return false;
        if (!actors.TryGetValue(actorId, out var ranges) || ranges.Count == 0) return false;
        int lo = 0, hi = ranges.Count - 1;
        if (time < ranges[0].Start) return false;
        while (lo < hi)
        {
            int middle = (lo + hi + 1) / 2;
            if (ranges[middle].Start <= time) lo = middle; else hi = middle - 1;
        }
        segment = ranges[lo];
        if (time >= segment.End) return false;
        return true;
    }

    internal bool RequiresPredecessor(ActorFrame[] visibleActors, double time)
    {
        if (Environment.RequiresPredecessor(time)) return true;
        foreach (var actor in visibleActors)
            if (TrySegment(actor.Id, time, out var segment) && segment.RequiresPredecessor(time)) return true;
        return false;
    }

    public bool CanSample(ActorFrame[] visibleActors, double time)
    {
        foreach (var actor in visibleActors) if (!TryActor(actor.Id, time, out _)) return false;
        return true;
    }

    internal static bool TryNode(Segment segment, int index, double time, out ActorJointBracket sample)
    {
        sample = default;
        var keys = segment.Nodes[index];
        if (keys.Count == 0) return false;
        int lo = 0, hi = keys.Count - 1;
        if (time < keys[0].Time) { sample = new ActorJointBracket(keys[0], keys[0], 0); return true; }
        while (lo < hi)
        {
            int middle = (lo + hi + 1) / 2;
            if (keys[middle].Time <= time) lo = middle; else hi = middle - 1;
        }
        var left = keys[lo];
        if (lo == keys.Count - 1)
        {
            if (!segment.Closed && time > left.Time + .000001) return false;
            sample = new ActorJointBracket(left, left, 0); return true;
        }
        var right = keys[lo + 1];
        double gap = right.Time - left.Time;
        bool blend = gap > 0 && gap <= .5 && left.Pose.Active == right.Pose.Active && left.Pose.Visible == right.Pose.Visible;
        float mix = blend ? (float)Math.Max(0, Math.Min(1, (time - left.Time) / gap)) : 0;
        sample = new ActorJointBracket(left, blend ? right : left, mix); return true;
    }
}

internal readonly struct ActorJointPoseSample
{
    private readonly ActorJointTimeline.Segment segment;
    private readonly double time;
    public NodePose[] Topology => segment.Topology;
    public int Count => segment.Nodes.Length;
    internal ActorJointPoseSample(ActorJointTimeline.Segment segment, double time) { this.segment = segment; this.time = time; }
    public ActorJointBracket At(int index)
    {
        if (!ActorJointTimeline.TryNode(segment, index, time, out var sample))
            throw new InvalidOperationException("缺少人物关节的下一次真实采样。");
        return sample;
    }
}

internal readonly struct ActorJointBracket
{
    public readonly NodePose Left, Right;
    public readonly double LeftTime, RightTime;
    public readonly float Mix;
    internal ActorJointBracket(ActorJointTimeline.Key left, ActorJointTimeline.Key right, float mix)
    { Left = left.Pose; Right = right.Pose; LeftTime = left.Time; RightTime = right.Time; Mix = mix; }
}
