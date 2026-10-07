using System;
using System.Runtime.CompilerServices;

namespace PeakReplayLab;

// Keep immutable, shared node samples shared while translating their time origin.
// Weak caches cannot retain the history of a multi-hour recording.
internal sealed class ActorJointRebaser
{
    private readonly double origin;
    private readonly ConditionalWeakTable<NodePose, NodePose> nodes = new();
    private readonly ConditionalWeakTable<NodePose[], NodePose[]> poses = new();
    private readonly ConditionalWeakTable<ActorFrame, ActorFrame> actors = new();
    private readonly ConditionalWeakTable<ActorFrame[], ActorFrame[]> sets = new();
    public ActorJointRebaser(double origin) => this.origin = origin;

    public ActorFrame[] Apply(ActorFrame[] source) => source.Length == 0 || origin == 0 ? source : sets.GetValue(source, values =>
    {
        ActorFrame[]? result = null;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i].JointPose.Length == 0) continue;
            result ??= (ActorFrame[])values.Clone();
            result[i] = actors.GetValue(values[i], actor =>
            actor.JointPose.Length == 0 ? actor : actor.WithJoints(poses.GetValue(actor.JointPose, joints =>
            {
                var shifted = new NodePose[joints.Length];
                for (int j = 0; j < shifted.Length; j++) shifted[j] = nodes.GetValue(joints[j], node =>
                    new NodePose(node.Path, node.Position, node.Rotation, node.Scale, node.Active, node.Visible, node.SampleTime - origin));
                return shifted;
            })));
        }
        return result ?? values;
    });
}
