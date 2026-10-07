using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace PeakReplayLab;

// Native non-legacy clips require an Animator. A controller-free Animator + manual
// PlayableGraph evaluates their curves without gameplay, state-machine behaviours or events.
internal sealed class CrateVisualAnimation : IDisposable
{
    private readonly GameObject root;
    private readonly Animator animator;
    private readonly Dictionary<string, AnimationClip?> clips = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (AnimationClipPlayable Playable, int Port)> players = new(StringComparer.Ordinal);
    private readonly List<AnimationClip> ownedClips = new();
    private PlayableGraph graph;
    private AnimationMixerPlayable mixer;
    private string previousClip = "";
    private double previousTime = double.NaN;
    private bool disposed;

    public CrateVisualAnimation(Luggage source, GameObject root)
    {
        this.root = root;
        animator = root.AddComponent<Animator>();
        animator.runtimeAnimatorController = null; animator.fireEvents = false;
        animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animator.updateMode = AnimatorUpdateMode.UnscaledTime;
        try
        {
            var original = source.GetComponent<Animator>();
            if (!original || !original.runtimeAnimatorController) throw new InvalidOperationException("箱子的原生动画控制器未就绪。");
            animator.avatar = original.avatar;
            foreach (var clip in original.runtimeAnimatorController.animationClips)
            {
                if (!clip) continue;
                string key = Capture.ClipKey(clip, original.runtimeAnimatorController);
                if (!clips.TryGetValue(key, out var existing)) clips.Add(key, clip);
                else if (existing != clip) clips[key] = null;
            }
            graph = PlayableGraph.Create("PeakReplayLab.CrateVisual");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            mixer = AnimationMixerPlayable.Create(graph, 0);
            AnimationPlayableOutput.Create(graph, "crate", animator).SetSourcePlayable(mixer);
            graph.Play();
        }
        catch { Dispose(); throw; }
    }

    public void Sample(CrateAnimationFrame value, CrateAnimationFrame? next, float mix, double replayTime, bool force)
    {
        if (disposed) throw new ObjectDisposedException(nameof(CrateVisualAnimation));
        if (!clips.TryGetValue(value.Clip, out var clip) || !clip)
            throw new InvalidOperationException("箱子原生动画资源不匹配：" + value.Clip);
        if (value.Anchored && Math.Abs(value.Duration - clip.length) > .01)
            throw new InvalidOperationException("箱子原生动画长度与录像不符：" + value.Clip);
        double cursor = CrateAnimationTimeline.Sample(value, replayTime, clip.length, clip.isLooping, next, mix);
        if (!force && previousClip == value.Clip && previousTime == cursor) return; // Settled chests do no animation work.
        if (!players.TryGetValue(value.Clip, out var player))
        {
            // Convert only a private copy if an older game asset was marked Legacy.
            // Never change the installed game's shared AnimationClip flags.
            if (clip.legacy)
            { clip = UnityEngine.Object.Instantiate(clip); clip.legacy = false; ownedClips.Add(clip); }
            var playable = AnimationClipPlayable.Create(graph, clip);
            playable.SetApplyFootIK(false); playable.SetApplyPlayableIK(false); playable.SetSpeed(0);
            int port = mixer.GetInputCount(); mixer.SetInputCount(port + 1);
            graph.Connect(playable, 0, mixer, port); players.Add(value.Clip, player = (playable, port));
        }
        foreach (var channel in players.Values) mixer.SetInputWeight(channel.Port, channel.Port == player.Port ? 1 : 0);
        player.Playable.SetTime(cursor);
        // World pose is authoritative; root motion in a curve must not move it a second time.
        var t = root.transform; Vector3 position = t.position, scale = t.localScale; Quaternion rotation = t.rotation;
        bool active = root.activeSelf;
        graph.Evaluate(0);
        t.SetPositionAndRotation(position, rotation); t.localScale = scale;
        if (root.activeSelf != active) root.SetActive(active);
        previousClip = value.Clip; previousTime = cursor;
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (graph.IsValid()) graph.Destroy();
        if (animator) { animator.enabled = false; UnityEngine.Object.Destroy(animator); }
        foreach (var clip in ownedClips) if (clip) UnityEngine.Object.Destroy(clip);
        ownedClips.Clear(); players.Clear(); clips.Clear();
    }
}
