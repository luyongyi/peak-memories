using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace PeakReplayLab;

internal static class ActorJointPaths
{
    // Equal-named sibling ordinals survive insertion of unrelated props. The
    // player root's name is absent so paths bind to the offline template too.
    public static string Path(Transform node, Transform root)
    {
        var parts = new Stack<string>();
        while (node != root)
        {
            var parent = node.parent;
            if (!parent) throw new InvalidOperationException("Character joint is outside its visual hierarchy.");
            int ordinal = 0;
            for (int i = 0; i < node.GetSiblingIndex(); i++) if (parent.GetChild(i).name == node.name) ordinal++;
            parts.Push(Uri.EscapeDataString(node.name) + "#" + ordinal.ToString(CultureInfo.InvariantCulture));
            node = parent;
        }
        return parts.Count == 0 ? "." : "./" + string.Join("/", parts);
    }
}

internal sealed class ActorJointCapture
{
    private Transform? root, rig;
    private Transform[] nodes = Array.Empty<Transform>();
    private string[] paths = Array.Empty<string>();
    private ActorJointPriority[] priorities = Array.Empty<ActorJointPriority>();
    private NodePose[] previous = Array.Empty<NodePose>();
    private double nextCatalogue, origin, lastTime = double.NaN, lastDetail = double.NaN, lastAuxiliary = double.NaN, handDetailUntil;
    private Vector3 lastHip;
    private int lastState, lastItem, lastCarrier;

    public NodePose[] Sample(Character character, Vector3 hip, double time)
    {
        if (double.IsNaN(time) || double.IsInfinity(time) || time < 0) throw new ArgumentOutOfRangeException(nameof(time));
        // A paused game or a second observer in the same frame must not create
        // fresh timestamps for a pose which has not actually been evaluated.
        if (time == lastTime) return previous;
        bool reset = double.IsNaN(lastTime) || time < lastTime;
        if (reset)
        {
            origin = time; lastDetail = lastAuxiliary = double.NaN; handDetailUntil = time;
        }
        var data = character.data;
        int state = (data.dead ? 1 : 0) | (data.passedOut || data.fullyPassedOut ? 2 : 0) |
            (data.currentRagdollControll < .5f ? 4 : 0) | (data.isClimbingAnything ? 8 : 0) |
            (data.isGrounded ? 16 : 0) | (data.isSkeleton ? 32 : 0);
        int item = data.currentItem ? data.currentItem.GetInstanceID() : 0;
        int carrier = data.carrier ? data.carrier.GetInstanceID() : 0;
        bool transition = !reset && (state != lastState || item != lastItem || carrier != lastCarrier);
        bool force = reset || previous.Length == 0 || transition || time - lastTime >= ActorJointSamplingPolicy.LongGapSeconds ||
            (hip - lastHip).sqrMagnitude > 4;
        if (transition) handDetailUntil = time + ActorJointSamplingPolicy.HandTransitionSeconds;
        bool rebuild = root != character.transform || rig != character.refs.rigCreator.transform;
        if (rebuild || Time.unscaledTimeAsDouble >= nextCatalogue) force |= Rebuild(character);
        bool detailDue = ActorJointSamplingPolicy.Due(ActorJointSamplingPolicy.DetailHz, time, lastDetail, origin,
            force || time < handDetailUntil);
        bool auxiliaryDue = ActorJointSamplingPolicy.Due(ActorJointSamplingPolicy.AuxiliaryHz, time, lastAuxiliary, origin, force);
        // Check native objects only on their sampling tier. A missing scheduled
        // transform rebuilds the whole catalogue before publishing this frame.
        for (int i = 0; i < nodes.Length; i++)
            if (Scheduled(priorities[i], detailDue, auxiliaryDue) && !nodes[i])
            {
                Rebuild(character); detailDue = auxiliaryDue = true; break;
            }
        bool sameCatalogue = previous.Length == nodes.Length;
        var result = sameCatalogue ? (NodePose[])previous.Clone() : new NodePose[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            if (sameCatalogue && !Scheduled(priorities[i], detailDue, auxiliaryDue)) continue;
            var node = nodes[i];
            // Child bones follow their parent every playback render, even when
            // their own clothing/finger articulation is sampled less often.
            bool isRoot = node == root;
            Vector3 position = isRoot ? node.position : node.localPosition, scale = node.localScale;
            Quaternion rotation = isRoot ? node.rotation : node.localRotation;
            var old = sameCatalogue ? previous[i] : null;
            result[i] = new NodePose(paths[i],
                old != null && Equal(old.Position, position) ? old.Position : new[] { position.x, position.y, position.z },
                old != null && Equal(old.Rotation, rotation) ? old.Rotation : new[] { rotation.x, rotation.y, rotation.z, rotation.w },
                old != null && Equal(old.Scale, scale) ? old.Scale : new[] { scale.x, scale.y, scale.z },
                active: node.gameObject.activeSelf, sampleTime: time);
        }
        if (detailDue) lastDetail = time;
        if (auxiliaryDue) lastAuxiliary = time;
        lastTime = time; lastHip = hip; lastState = state; lastItem = item; lastCarrier = carrier;
        return previous = result;
    }

    private bool Rebuild(Character character)
    {
        var characterRoot = character.transform;
        var selected = new HashSet<Transform>();
        void Include(Transform? node)
        {
            if (!node || node != characterRoot && !node!.IsChildOf(characterRoot)) return;
            for (var current = node; current; current = current!.parent)
            {
                selected.Add(current!);
                if (current == characterRoot) break;
            }
        }
        Include(characterRoot); Include(character.refs.hip.transform); Include(character.refs.rigCreator.transform);
        foreach (var part in character.refs.rigCreator.parts) Include(part.transform);
        foreach (var renderer in character.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer.GetComponentInParent<Item>()) continue; // ItemPlayback owns props.
            foreach (var bone in renderer.bones) Include(bone);
            Include(renderer.rootBone);
        }
        var catalogue = selected.Select(node => (Node: node, Path: ActorJointPaths.Path(node, characterRoot)))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
        if (catalogue.Length > ActorJointReplayRules.MaximumJoints || catalogue.Any(entry => entry.Path.Length > ActorJointReplayRules.MaximumPathLength))
            throw new InvalidOperationException("人物骨骼超出支持范围，未截断关节数据。");
        bool changed = nodes.Length != catalogue.Length;
        if (!changed) for (int i = 0; i < nodes.Length; i++)
            if (nodes[i] != catalogue[i].Node || paths[i] != catalogue[i].Path) { changed = true; break; }
        if (changed)
        {
            nodes = catalogue.Select(entry => entry.Node).ToArray();
            paths = catalogue.Select(entry => entry.Path).ToArray();
            priorities = paths.Select(ActorJointSamplingPolicy.Classify).ToArray();
            previous = Array.Empty<NodePose>();
        }
        root = characterRoot; rig = character.refs.rigCreator.transform;
        nextCatalogue = Time.unscaledTimeAsDouble + 1;
        return changed;
    }

    private static bool Scheduled(ActorJointPriority priority, bool detail, bool auxiliary) =>
        priority == ActorJointPriority.P0 || priority == ActorJointPriority.P1 && detail || priority == ActorJointPriority.P2 && auxiliary;
    private static bool Equal(float[] value, Vector3 point) => value[0] == point.x && value[1] == point.y && value[2] == point.z;
    private static bool Equal(float[] value, Quaternion rotation) => value[0] == rotation.x && value[1] == rotation.y &&
        value[2] == rotation.z && value[3] == rotation.w;
}
