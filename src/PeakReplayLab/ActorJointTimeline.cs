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
        public ActorFrame Last;
        public Segment(double start, ActorFrame actor, bool earlierBaseline)
        {
            Start = start; Topology = actor.JointPose; Last = actor; this.earlierBaseline = earlierBaseline;
            Nodes = new List<Key>[Topology.Length];
            for (int i = 0; i < Nodes.Length; i++) Nodes[i] = new List<Key>();
        }
        public void Observe(ActorFrame actor, double frameTime)
        {
            for (int i = 0; i < Nodes.Length; i++)
            {
                var pose = actor.JointPose[i];
                if (!ReplayRules.Finite(pose.SampleTime) || pose.SampleTime > frameTime + .000001)
                    throw new InvalidDataException("人物关节采样时间无效。");
                // A page's first full baseline can predate its first frame. A
                // real cut, however, must not inherit a prior interval's clock.
                double time = earlierBaseline ? pose.SampleTime : Math.Max(Start, pose.SampleTime);
                var keys = Nodes[i];
                if (keys.Count != 0 && time <= keys[keys.Count - 1].Time) continue;
                keys.Add(new Key(time, pose));
            }
            Last = actor;
        }
    }

    private readonly Dictionary<string, List<Segment>> actors = new(StringComparer.Ordinal);
    public EnvironmentReplayTimeline Environment { get; }

    public ActorJointTimeline(IEnumerable<ReplayFrame> frames, bool knownEnd, CancellationToken cancellation = default)
    {
        var active = new Dictionary<string, Segment>(StringComparer.Ordinal);
        var present = new HashSet<string>(StringComparer.Ordinal);
        var absent = new List<string>();
        double previousTime = double.NegativeInfinity;
        bool first = true;
        var environment = new EnvironmentReplayTimeline.Builder();
        foreach (var frame in frames)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!ReplayRules.Finite(frame.T)) throw new InvalidDataException("人物关节帧时间无效。");
            // Adjacent seek pages may repeat their shared boundary frame.
            if (frame.T == previousTime) continue;
            if (frame.T < previousTime) throw new InvalidDataException("人物关节时间轴没有排序。");
            environment.Observe(frame);
            present.Clear();
            foreach (var actor in frame.Actors)
            {
                present.Add(actor.Id);
                bool continuation = active.TryGetValue(actor.Id, out var current) &&
                    frame.T - previousTime <= .5 &&
                    ActorJointReplayRules.SameTopology(current.Topology, actor.JointPose) &&
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
        if (!ReplayRules.Finite(time)) return false;
        if (!actors.TryGetValue(actorId, out var ranges) || ranges.Count == 0) return false;
        int lo = 0, hi = ranges.Count - 1;
        if (time < ranges[0].Start) return false;
        while (lo < hi)
        {
            int middle = (lo + hi + 1) / 2;
            if (ranges[middle].Start <= time) lo = middle; else hi = middle - 1;
        }
        var segment = ranges[lo];
        if (time >= segment.End) return false;
        for (int i = 0; i < segment.Nodes.Length; i++)
            if (!TryNode(segment, i, time, out _)) return false;
        sample = new ActorJointPoseSample(segment, time); return true;
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
