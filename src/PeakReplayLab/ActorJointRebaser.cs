using System;

namespace PeakReplayLab;

// Keep immutable, shared node samples shared while translating their time origin.
// Worker-owned bounded identity caches retain hot page-spanning samples without
// weak-table runtime lookups or retaining a multi-hour history.
internal sealed class ActorJointRebaser
{
    private readonly double origin;
    internal const int NodeCapacity = 32768, PoseCapacity = 512, ActorCapacity = 512, RouteCapacity = 512, SetCapacity = 128;
    private readonly BoundedReferenceCache<NodePose, NodePose> nodes = new(NodeCapacity);
    private readonly BoundedReferenceCache<NodePose[], NodePose[]> poses = new(PoseCapacity);
    private readonly BoundedReferenceCache<ActorFrame, ActorFrame> actors = new(ActorCapacity);
    private readonly BoundedReferenceCache<ActorRouteState, ActorRouteState> routeStates = new(RouteCapacity);
    private readonly BoundedReferenceCache<ActorFrame[], ActorFrame[]> sets = new(SetCapacity);
    private readonly Func<NodePose, NodePose> nodeFactory;
    private readonly Func<NodePose[], NodePose[]> poseFactory;
    private readonly Func<ActorFrame, ActorFrame> actorFactory;
    private readonly Func<ActorRouteState, ActorRouteState> routeFactory;
    private readonly Func<ActorFrame[], ActorFrame[]> setFactory;
    internal (int Nodes, int Poses, int Actors, int Routes, int Sets) CacheCounts => (nodes.Count, poses.Count, actors.Count, routeStates.Count, sets.Count);
    public ActorJointRebaser(double origin)
    {
        this.origin = origin; nodeFactory = ShiftNode; poseFactory = ShiftPose;
        actorFactory = ShiftActor; routeFactory = state => state.Rebase(origin); setFactory = ShiftSet;
    }

    public ActorFrame[] Apply(ActorFrame[] source) => source.Length == 0 || origin == 0 ? source : sets.GetValue(source, setFactory);
    private ActorFrame[] ShiftSet(ActorFrame[] values)
    {
        ActorFrame[]? result = null;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i].JointPose.Length == 0 && values[i].RouteState == null) continue;
            result ??= (ActorFrame[])values.Clone();
            result[i] = actors.GetValue(values[i], actorFactory);
        }
        return result ?? values;
    }
    private ActorFrame ShiftActor(ActorFrame actor)
    {
        var result = actor;
        if (actor.JointPose.Length != 0) result = actor.WithJoints(poses.GetValue(actor.JointPose, poseFactory));
        if (actor.RouteState != null) result = result.WithRouteState(routeStates.GetValue(actor.RouteState, routeFactory));
        return result;
    }
    private NodePose[] ShiftPose(NodePose[] joints)
    {
        var shifted = new NodePose[joints.Length];
        for (int i = 0; i < shifted.Length; i++) shifted[i] = nodes.GetValue(joints[i], nodeFactory);
        return shifted;
    }
    private NodePose ShiftNode(NodePose node) => new(node.Path, node.Position, node.Rotation, node.Scale, node.Active, node.Visible, node.SampleTime - origin);
}
