using System;
using System.Collections.Generic;
using UnityEngine;

namespace PeakReplayLab;

// Static fake Items normally reuse the native scene mesh. The lease changes
// only renderer visibility and passive TRS; it never invokes pickup, networking,
// pickedUp, or activation callbacks. A consumed/inactive source gets an inert
// VisualReplica only when a backward seek actually needs its earlier model.
internal sealed class FakeItemReplayPlayback : IDisposable
{
    private sealed class Entry
    {
        internal Transform Root = null!;
        internal Renderer[] Renderers = Array.Empty<Renderer>();
        internal bool[] ForceOff = Array.Empty<bool>();
        internal string Path = "", Resource = "";
        internal Vector3 OriginalPosition, OriginalScale;
        internal Quaternion OriginalRotation;
        internal InteractionReplayFrame? Last;
        internal VisualReplica? Fallback;
        internal bool Missing, Wanted, FallbackVisible, NativeVisible;
        internal int Seen;
        internal void Restore()
        {
            if (Root) { Root.localPosition = OriginalPosition; Root.localRotation = OriginalRotation; Root.localScale = OriginalScale; }
            for (int i = 0; i < Renderers.Length; i++) if (Renderers[i]) Renderers[i].forceRenderingOff = ForceOff[i];
            Fallback?.Retire(); Fallback = null;
        }
    }
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly List<Entry> sleeping = new();
    private readonly Func<InteractionReplayFrame, GameObject?> resolve;
    private readonly PresentationWorkBudget budget;
    private readonly Action<string>? warning;
    private readonly HashSet<string> warned = new(StringComparer.Ordinal);
    private int serial, fallbackCount, fallbackUsed;
    private InteractionReplayFrame[]? lastSnapshot;
    private bool skip;
    internal bool Pending { get; private set; }
    internal int MissingCount { get; private set; }
    internal int VisibleCount { get; private set; }
    internal FakeItemReplayPlayback(Func<InteractionReplayFrame, GameObject?> resolve, PresentationWorkBudget budget, Action<string>? warning)
    { this.resolve = resolve; this.budget = budget; this.warning = warning; }
    internal void Begin(InteractionReplayFrame[]? snapshot = null, bool discontinuity = false)
    {
        skip = snapshot != null && ReferenceEquals(snapshot, lastSnapshot) && !discontinuity && !Pending && MissingCount == 0;
        if (skip) return;
        lastSnapshot = snapshot;
        serial++; fallbackUsed = 0; Pending = false; MissingCount = VisibleCount = 0;
        foreach (var entry in entries.Values) entry.Wanted = entry.FallbackVisible = false;
    }
    internal void Apply(InteractionReplayFrame frame, bool discontinuity)
    {
        if (skip) return;
        try
        {
            // A retired region can later be re-admitted with another recording
            // key. Its scene path still owns exactly one original visibility
            // lease; taking a second lease would snapshot our own hidden flag.
            if (!entries.TryGetValue(frame.SourcePath, out var entry))
            {
                if (entries.Count >= InteractionReplayRules.MaximumStaticFakeItems) throw new InvalidOperationException("静态采集物回放目录已满。");
                var native = resolve(frame);
                if (!native) throw new InvalidOperationException("原生静态采集物与录像不匹配：" + frame.SourcePath);
                var root = native!.transform; var renderers = native.GetComponentsInChildren<Renderer>(true);
                var values = new List<Renderer>(); var flags = new List<bool>();
                foreach (var renderer in renderers) if (renderer && !(renderer is ParticleSystemRenderer))
                { values.Add(renderer); flags.Add(renderer.forceRenderingOff); }
                entry = new Entry { Root = root, Path = frame.SourcePath, Resource = frame.Resource,
                    Renderers = values.ToArray(), ForceOff = flags.ToArray(), OriginalPosition = root.localPosition,
                    OriginalRotation = root.localRotation, OriginalScale = root.localScale };
                entries.Add(frame.SourcePath, entry);
            }
            entry.Wanted = true; entry.Seen = serial;
            if (entry.Path != frame.SourcePath || entry.Resource != frame.Resource)
                throw new InvalidOperationException("静态采集物身份改变。");
            bool changed = !ReferenceEquals(entry.Last, frame) || discontinuity || entry.Missing;
            if (!changed)
            {
                bool fallback = entry.Fallback != null && !entry.NativeVisible && frame.Pose.Active;
                // The cap counts all simultaneously presented fallback meshes,
                // including immutable states built on earlier render frames.
                if (fallback && ++fallbackUsed > InteractionReplayRules.MaximumDynamicEntities)
                    throw new InvalidOperationException("静态采集物回拖视觉超过768个。");
                entry.FallbackVisible = fallback;
                if (frame.Pose.Active) VisibleCount++;
                return;
            }
            entry.Last = frame; entry.Missing = false;
            if (!entry.Root) throw new InvalidOperationException("原生静态采集物已销毁。");
            var pose = frame.Pose;
            entry.NativeVisible = pose.Active && entry.Root.gameObject.activeSelf;
            SetVisibility(entry, entry.NativeVisible);
            if (!pose.Active) { entry.Fallback?.Hide(); return; }
            var position = V(pose.Position); var rotation = Q(pose.Rotation); var scale = V(pose.Scale);
            if (entry.Root.position != position || entry.Root.rotation != rotation || entry.Root.lossyScale != scale)
            {
                entry.Root.SetPositionAndRotation(position, rotation);
                var parentScale = entry.Root.parent ? entry.Root.parent.lossyScale : Vector3.one;
                entry.Root.localScale = new Vector3(Divide(scale.x, parentScale.x), Divide(scale.y, parentScale.y), Divide(scale.z, parentScale.z));
            }
            if (!entry.NativeVisible)
            {
                if (++fallbackUsed > InteractionReplayRules.MaximumDynamicEntities) throw new InvalidOperationException("静态采集物回拖视觉超过768个。");
                if (entry.Fallback == null)
                {
                    if (!VisualReplica.CanStartBuild || !budget.Create()) { entry.Missing = true; Pending = true; MissingCount++; return; }
                    MakeRoom(); entry.Fallback = VisualReplica.BeginBuild(entry.Root.gameObject, true, InteractionReplayRules.MaximumNodes); fallbackCount++;
                    entry.Fallback.Root.name = "PEAK Replay static pickup";
                }
                if (!entry.Fallback.ContinueBuild(budget)) { entry.Missing = true; Pending = true; MissingCount++; return; }
                if (!entry.Fallback.Root.transform.parent)
                {
                    entry.Fallback.Root.transform.SetParent(entry.Root.parent, true);
                    for (int i = 0; i < entry.Renderers.Length; i++)
                        if (entry.Renderers[i] && entry.Fallback.TryCloneRenderer(entry.Renderers[i], out var copy)) copy.forceRenderingOff = entry.ForceOff[i];
                }
                entry.Fallback.Apply(pose, null, 0); entry.Fallback.Root.transform.localScale = entry.Root.localScale;
                entry.FallbackVisible = true;
            }
            else entry.Fallback?.Hide();
            VisibleCount++;
        }
        catch (Exception error)
        {
            MissingCount++;
            if (entries.TryGetValue(frame.SourcePath, out var entry)) { entry.Missing = true; entry.Fallback?.Hide(); }
            if (warned.Count < 32 && warned.Add(frame.SourcePath)) warning?.Invoke("静态采集物视觉缺失：" + error.Message);
        }
    }
    internal void End()
    {
        if (skip) return;
        sleeping.Clear();
        foreach (var entry in entries.Values)
        {
            if (!entry.Wanted && entry.Last != null)
            {
                // A seek before this track's first admitted state, or into
                // another biome, releases its claim. Never infer old history.
                SetVisibility(entry, true); entry.Fallback?.Hide(); entry.Last = null;
                if (entry.Root) { entry.Root.localPosition = entry.OriginalPosition; entry.Root.localRotation = entry.OriginalRotation; entry.Root.localScale = entry.OriginalScale; }
            }
            if (entry.Fallback != null && !entry.FallbackVisible)
            {
                if (entry.Missing && entry.Wanted) continue;
                if (!entry.Fallback.IsReady) { entry.Fallback.Retire(); entry.Fallback = null; fallbackCount--; }
                else sleeping.Add(entry);
            }
        }
        sleeping.Sort((a, b) => a.Seen.CompareTo(b.Seen));
        for (int i = 0; i < sleeping.Count - 16; i++) { sleeping[i].Fallback!.Retire(); sleeping[i].Fallback = null; fallbackCount--; }
    }
    private void MakeRoom()
    {
        if (fallbackCount < InteractionReplayRules.MaximumDynamicEntities + 16) return;
        Entry? oldest = null;
        foreach (var entry in entries.Values)
            if (entry.Fallback != null && !entry.Wanted && (oldest == null || entry.Seen < oldest.Seen)) oldest = entry;
        if (oldest == null) throw new InvalidOperationException("静态采集物回拖视觉池已满。");
        oldest.Fallback!.Retire(); oldest.Fallback = null; fallbackCount--;
    }
    private static void SetVisibility(Entry entry, bool visible)
    {
        for (int i = 0; i < entry.Renderers.Length; i++) if (entry.Renderers[i])
        { bool hidden = entry.ForceOff[i] || !visible; if (entry.Renderers[i].forceRenderingOff != hidden) entry.Renderers[i].forceRenderingOff = hidden; }
    }
    private static Vector3 V(float[] value) => new(value[0], value[1], value[2]);
    private static Quaternion Q(float[] value) => new(value[0], value[1], value[2], value[3]);
    private static float Divide(float value, float by) => Math.Abs(by) < .0000001f ? value : value / by;
    public void Dispose() { foreach (var entry in entries.Values) entry.Restore(); entries.Clear(); sleeping.Clear(); }
}
