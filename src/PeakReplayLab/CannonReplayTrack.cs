using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace PeakReplayLab;

// Observe native results. The stationary root/tube are read on events and a cold
// 2 Hz fallback; only a short active cannon cycle probes its one Animator at 20 Hz.
// No hierarchy walk, component lookup, physics or network call on the hot path.
internal sealed class CannonReplayCapture
{
    private readonly ScoutCannon source;
    private readonly Transform root, tube;
    private readonly Animator animator;
    private readonly string tubePath;
    private readonly List<AnimatorClipInfo> clips = new(2);
    private double nextPose, nextAnimation, watchUntil;
    private SpawnedReplayFrame? last;
    private CrateAnimationFrame? animation;

    public CannonReplayCapture(ScoutCannon source)
    {
        if (!source) throw new InvalidOperationException("Native placed cannon component is missing.");
        this.source = source; root = source.transform; tube = root.Find("Cannon"); animator = source.anim;
        if (!tube || !animator) throw new InvalidOperationException("Native placed cannon visuals are not ready.");
        tubePath = tube.GetSiblingIndex() + ":" + Uri.EscapeDataString(tube.name);
        watchUntil = Time.timeAsDouble + 1;
    }

    public void Changed()
    {
        nextPose = nextAnimation = 0;
        watchUntil = Time.timeAsDouble + Math.Max(1, Math.Min(60, source.fireTime + source.fallFor + 1));
    }

    public SpawnedReplayFrame Sample(string key, string resource, double now, bool dirty)
    {
        bool visible = root.gameObject.activeInHierarchy;
        ObjectPose? pose = last?.Pose;
        if (dirty || pose == null || pose.Active != visible || now >= nextPose)
        {
            Vector3 p = root.position, s = root.lossyScale, tp = tube.localPosition, ts = tube.localScale;
            Quaternion q = root.rotation, tq = tube.localRotation;
            var candidate = new ObjectPose { Position = V(p), Rotation = Q(q), Scale = V(s), Active = visible,
                Nodes = new[] { new NodePose { Path = tubePath, Position = V(tp), Rotation = Q(tq), Scale = V(ts),
                    Active = tube.gameObject.activeSelf, Visible = true } } };
            if (pose == null || !VisualReplica.Same(pose, candidate)) pose = candidate;
            nextPose = now + .5 + (source.GetInstanceID() & 3) * .03;
        }
        if (dirty || last == null || now >= nextAnimation)
        {
            animation = ReadAnimation(now, animation);
            nextAnimation = now + (source.lit || now <= watchUntil ? .05 : .5);
        }
        if (last != null && ReferenceEquals(pose, last.Pose) && ReferenceEquals(animation, last.Animation)) return last;
        return last = new SpawnedReplayFrame { Key = key, Resource = resource, Kind = "scout-cannon", Pose = pose!, Animation = animation };
    }

    private CrateAnimationFrame? ReadAnimation(double now, CrateAnimationFrame? previous)
    {
        clips.Clear(); animator.GetCurrentAnimatorClipInfo(0, clips);
        AnimatorClipInfo best = default;
        foreach (var clip in clips) if (!best.clip || clip.weight > best.weight) best = clip;
        if (!best.clip || best.clip.name != "CannonLight" && best.clip.name != "CannonFire") return null;
        var state = animator.GetCurrentAnimatorStateInfo(0);
        float duration = best.clip.length;
        float cursor = Mathf.Clamp(state.normalizedTime * duration, 0, duration);
        float rate = animator.enabled ? animator.speed * state.speed * state.speedMultiplier : 0;
        if (previous != null && previous.Clip == best.clip.name && previous.Duration == duration && previous.Rate == rate &&
            Math.Abs(CrateAnimationTimeline.Sample(previous, now, duration, false) - cursor) <= .12) return previous;
        return new CrateAnimationFrame { Clip = best.clip.name, Time = cursor, Anchored = true,
            AnchorTime = now, Rate = rate, Duration = duration, Loop = false };
    }

    private static float[] V(Vector3 p) => new[] { p.x, p.y, p.z };
    private static float[] Q(Quaternion q) => new[] { q.x, q.y, q.z, q.w };
}

// A controller-free native animation graph on a mesh-only clone. In particular,
// RPCA_Light/FireTargets/Constructable are never invoked by playback.
internal sealed class CannonReplayVisual : IDisposable
{
    private readonly VisualReplica visual;
    private readonly Animator animator;
    private readonly Transform tube;
    private readonly Dictionary<string, AnimationClip> clips = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (AnimationClipPlayable Clip, int Port)> players = new(StringComparer.Ordinal);
    private PlayableGraph graph;
    private AnimationMixerPlayable mixer;
    private string previousClip = "";
    private double previousTime = double.NaN;
    private NodePose[]? previousNodes;
    private bool disposed;

    public CannonReplayVisual(GameObject source, VisualReplica visual)
    {
        this.visual = visual;
        tube = visual.Root.transform.Find("Cannon");
        var original = source.GetComponent<ScoutCannon>();
        if (!tube || !original || !original.anim || !original.anim.runtimeAnimatorController)
            throw new InvalidOperationException("Native placed cannon animation resource is missing.");
        animator = visual.Root.AddComponent<Animator>();
        try
        {
            animator.avatar = original.anim.avatar; animator.runtimeAnimatorController = null;
            animator.applyRootMotion = false; animator.fireEvents = false;
            animator.updateMode = AnimatorUpdateMode.UnscaledTime; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            foreach (var clip in original.anim.runtimeAnimatorController.animationClips)
                if (clip && (clip.name == "CannonLight" || clip.name == "CannonFire"))
                {
                    if (clips.TryGetValue(clip.name, out var previous) && previous != clip)
                        throw new InvalidOperationException("Ambiguous native cannon animation: " + clip.name);
                    clips[clip.name] = clip;
                }
            if (clips.Count != 2) throw new InvalidOperationException("Native cannon Light/Fire clips are unavailable.");
            graph = PlayableGraph.Create("PeakReplayLab.CannonVisual"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            mixer = AnimationMixerPlayable.Create(graph, 0);
            AnimationPlayableOutput.Create(graph, "cannon", animator).SetSourcePlayable(mixer); graph.Play();
        }
        catch { Dispose(); throw; }
    }

    public void Apply(SpawnedReplayFrame frame, SpawnedReplayFrame? next, float mix, double replayTime, bool discontinuity)
    {
        if (disposed) throw new ObjectDisposedException(nameof(CannonReplayVisual));
        var value = frame.Animation;
        string name = value?.Clip ?? "";
        bool reset = discontinuity || previousClip != name;
        bool poseChanged = !ReferenceEquals(previousNodes, frame.Pose.Nodes);
        if (reset) visual.InvalidatePose(); // Reset old animated bindings before seeking/switching to idle.
        visual.Apply(frame.Pose, next != null && SpawnedReplayRules.CanInterpolate(frame, next) ? next.Pose : null, mix);
        previousNodes = frame.Pose.Nodes;
        if (value == null)
        {
            foreach (var player in players.Values) mixer.SetInputWeight(player.Port, 0);
            previousClip = ""; previousTime = double.NaN; return;
        }
        if (!clips.TryGetValue(value.Clip, out var clip) || Math.Abs(value.Duration - clip.length) > .01)
            throw new InvalidOperationException("Recorded cannon animation does not match the installed game.");
        double cursor = CrateAnimationTimeline.Sample(value, replayTime, clip.length, false);
        if (!reset && !poseChanged && previousTime == cursor) return;
        if (!players.TryGetValue(name, out var selected))
        {
            var player = AnimationClipPlayable.Create(graph, clip); player.SetSpeed(0);
            player.SetApplyFootIK(false); player.SetApplyPlayableIK(false);
            int port = mixer.GetInputCount(); mixer.SetInputCount(port + 1); graph.Connect(player, 0, mixer, port);
            players.Add(name, selected = (player, port));
        }
        foreach (var player in players.Values) mixer.SetInputWeight(player.Port, player.Port == selected.Port ? 1 : 0);
        selected.Clip.SetTime(cursor);
        var t = visual.Root.transform;
        Vector3 position = t.position, scale = t.localScale; Quaternion rotation = t.rotation, tubeRotation = tube.localRotation;
        bool active = visual.Root.activeSelf;
        graph.Evaluate(0);
        t.SetPositionAndRotation(position, rotation); t.localScale = scale; tube.localRotation = tubeRotation;
        if (visual.Root.activeSelf != active) visual.Root.SetActive(active);
        previousClip = name; previousTime = cursor;
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (graph.IsValid()) graph.Destroy();
        if (animator) { animator.enabled = false; UnityEngine.Object.Destroy(animator); }
        clips.Clear(); players.Clear();
    }
}
