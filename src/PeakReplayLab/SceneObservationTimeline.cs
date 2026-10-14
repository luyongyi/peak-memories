using System;
using System.Collections.Generic;
using System.IO;

namespace PeakReplayLab;

// A 60 Hz player frame can reuse the last 30 Hz scene arrays. Interpolate the
// actual scene observations, not the repeated player-frame wrappers. This
// immutable worker index has no seek history or scene/Unity object reads.
internal sealed class SceneObservationTimeline
{
    private readonly struct Key
    {
        public readonly double Time;
        public readonly ReplayFrame Frame;
        public readonly bool Cut;
        public Key(double time, ReplayFrame frame, bool cut) { Time = time; Frame = frame; Cut = cut; }
    }
    internal sealed class Builder
    {
        private readonly List<Key> keys = new();
        private double previousFrame = double.NegativeInfinity;
        private bool cut;
        public void Observe(ReplayFrame frame)
        {
            if (!ReplayRules.Finite(frame.T) || frame.T < previousFrame)
                throw new InvalidDataException("Scene observation frames are not ordered.");
            if (!frame.SceneSampleTimeKnown) { cut = true; previousFrame = frame.T; return; }
            double time = frame.SceneSampleTime;
            if (!ReplayRules.Finite(time) || Math.Abs(time) > 1_000_000 || time > frame.T + .000001)
                throw new InvalidDataException("Invalid scene observation time.");
            if (keys.Count != 0)
            {
                var previous = keys[keys.Count - 1];
                if (time < previous.Time) throw new InvalidDataException("Scene observation clocks moved backwards.");
                if (time == previous.Time)
                {
                    if (!ReplayDeltaCodec.SameScene(previous.Frame, frame))
                        throw new InvalidDataException("A held scene observation changed without a new clock.");
                    previousFrame = frame.T; return;
                }
                cut |= frame.T - previousFrame > .5 || WorldCut(previous.Frame.World, frame.World);
            }
            keys.Add(new Key(time, frame, cut)); cut = false; previousFrame = frame.T;
        }
        public SceneObservationTimeline Build(bool knownEnd) => new(keys.ToArray(), knownEnd);
        internal static bool WorldCut(WorldFrame a, WorldFrame b)
        {
            if (a.Segment != b.Segment) return true;
            if (ReferenceEquals(a.ActiveMapObjects,b.ActiveMapObjects)) return false;
            if (a.ActiveMapObjects.Length != b.ActiveMapObjects.Length) return true;
            for (int i=0;i<a.ActiveMapObjects.Length;i++) if (a.ActiveMapObjects[i] != b.ActiveMapObjects[i]) return true;
            return false;
        }
    }
    private readonly Key[] keys;
    private readonly bool knownEnd;
    internal long EstimatedIndexBytes => keys.Length == 0 ? 0 : 96L + keys.Length * 32L;
    internal int ObservationCount => keys.Length;
    private SceneObservationTimeline(Key[] keys, bool knownEnd) { this.keys = keys; this.knownEnd = knownEnd; }
    internal bool CanSample(ReplayFrame globalLeft, double time) => !globalLeft.SceneSampleTimeKnown || TrySample(time,out _);
    internal bool TrySample(double time, out SceneReplayBracket sample)
    {
        sample = default;
        if (!ReplayRules.Finite(time) || keys.Length == 0) return false;
        int lo=0, hi=keys.Length-1;
        if (time < keys[0].Time) return false;
        while(lo<hi)
        {
            int middle=(lo+hi+1)/2;
            if (keys[middle].Time<=time) lo=middle; else hi=middle-1;
        }
        var left=keys[lo];
        if(lo==keys.Length-1)
        {
            if(!knownEnd && time>left.Time+.000001) return false;
            sample=new SceneReplayBracket(left.Frame,left.Frame,left.Time,left.Time,0);return true;
        }
        var right=keys[lo+1];double gap=right.Time-left.Time;
        bool blend=!right.Cut && gap>0 && gap<=.5;
        float mix=blend ? (float)Math.Max(0,Math.Min(1,(time-left.Time)/gap)) : 0;
        sample=new SceneReplayBracket(left.Frame,blend?right.Frame:left.Frame,left.Time,blend?right.Time:left.Time,mix);return true;
    }
    // Effect-only history has separate page indexes. Reuse the successor's
    // full opening baseline or its first observation without a new IO/index
    // request on the display thread.
    internal bool TrySampleWithSuccessor(SceneObservationTimeline next, double time, out SceneReplayBracket sample)
    {
        if(TrySample(time,out sample) || next.TrySample(time,out sample)) return true;
        if(keys.Length==0 || next.keys.Length==0) return false;
        var left=keys[keys.Length-1];var right=next.keys[0];
        if(time<left.Time || time>=right.Time) return false;
        double gap=right.Time-left.Time;
        bool blend=gap>0 && gap<=.5 && !right.Cut && !Builder.WorldCut(left.Frame.World,right.Frame.World);
        sample=new SceneReplayBracket(left.Frame,blend?right.Frame:left.Frame,left.Time,blend?right.Time:left.Time,
            blend?(float)Math.Max(0,Math.Min(1,(time-left.Time)/gap)):0);return true;
    }
}

internal readonly struct SceneReplayBracket
{
    public readonly ReplayFrame Left, Right;
    public readonly double LeftTime, RightTime;
    public readonly float Mix;
    internal SceneReplayBracket(ReplayFrame left, ReplayFrame right, double leftTime, double rightTime, float mix)
    { Left=left;Right=right;LeftTime=leftTime;RightTime=rightTime;Mix=mix; }
    internal static SceneReplayBracket Legacy(ReplayFrame left, ReplayFrame right, float mix) => new(left,right,left.T,right.T,mix);
}
