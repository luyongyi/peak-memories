using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

internal sealed class CreatureReplayPlayback : IDisposable
{
    private sealed class Entry : IDisposable
    {
        public string Kind = "", Path = "";
        public VisualReplica Visual = null!;
        public LineRenderer? Line;
        public NativeVisualAppearance.Playback? Appearance;
        public int Seen;
        public void Dispose() { Appearance?.Dispose(); Visual.Dispose(); }
    }
    private sealed class StaticEntry
    {
        public Transform Root = null!;
        public Renderer[] Renderers = Array.Empty<Renderer>();
        public string Path = "";
        public Vector3 OriginalPosition, OriginalScale;
        public Quaternion OriginalRotation;
        public bool Visible, FallbackVisible;
        public ObjectPose? AppliedPose, AppliedNext;
        public float AppliedMix;
        public int Seen;
        public VisualReplica? Fallback;
        public void Restore()
        {
            Fallback?.Dispose(); Fallback = null;
            if (!Root) return;
            Root.localPosition = OriginalPosition; Root.localRotation = OriginalRotation; Root.localScale = OriginalScale;
        }
    }
    private readonly struct RenderState
    {
        public readonly bool ForceOff, Enabled;
        public RenderState(Renderer source) { ForceOff = source.forceRenderingOff; Enabled = source.enabled; }
    }
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StaticEntry> statics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CreatureReplayFrame> following = new(StringComparer.Ordinal);
    private readonly Dictionary<Renderer, RenderState> hidden = new();
    private readonly Dictionary<Renderer, StaticEntry> staticOwners = new();
    private readonly Dictionary<string, double> retry = new(StringComparer.Ordinal);
    private readonly HashSet<string> current = new(StringComparer.Ordinal), warned = new(StringComparer.Ordinal);
    private readonly List<KeyValuePair<string, Entry>> sleeping = new();
    private readonly List<string> removed = new();
    private readonly List<StaticEntry> fallbackSleeping = new();
    private readonly Action<string>? warning;
    private readonly CreatureReplayObserver observer;
    private int serial, enforcedFrame = -1, fallbackCount, fallbackUsed;
    private bool disposed;
    public int MissingCount { get; private set; }
    public int VisibleCount { get; private set; }
    public CreatureReplayPlayback(Action<string>? warning = null)
    {
        this.warning = warning;
        observer = new CreatureReplayObserver(Hide);
        try
        {
            CreatureReplayResources.Refresh();
            foreach (var source in Object.FindObjectsByType<Spider>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Hide(source.gameObject);
            foreach (var source in Object.FindObjectsByType<MushroomZombie>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Hide(source.gameObject);
            foreach (var source in Object.FindObjectsByType<TriggerEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            { var trap = CreatureReplayResources.Trap(source.transform); if (trap) Hide(trap!.gameObject); }
        }
        catch { Dispose(); throw; }
    }
    private void Hide(GameObject source)
    {
        if (!source || CreatureReplayResources.Ours(source.transform) || !source.GetComponent<Spider>() && !source.GetComponent<MushroomZombie>() && CreatureReplayResources.Trap(source.transform) != source.transform) return;
        foreach (var renderer in source.GetComponentsInChildren<Renderer>(true))
        {
            if (!(renderer is MeshRenderer || renderer is SkinnedMeshRenderer || renderer is LineRenderer) || hidden.ContainsKey(renderer)) continue;
            hidden.Add(renderer, new RenderState(renderer)); renderer.forceRenderingOff = true;
        }
    }
    public void Enforce()
    {
        if (disposed || enforcedFrame == Time.frameCount) return;
        enforcedFrame = Time.frameCount;
        foreach (var pair in hidden)
            if (pair.Key)
            {
                bool desired = pair.Value.ForceOff || !staticOwners.TryGetValue(pair.Key, out var owner) || !owner.Visible;
                if (pair.Key.forceRenderingOff != desired) pair.Key.forceRenderingOff = desired;
            }
    }
    public void Apply(CreatureReplayFrame[] left, CreatureReplayFrame[] right, float mix, double time, bool discontinuity)
    {
        if (disposed) throw new ObjectDisposedException(nameof(CreatureReplayPlayback));
        serial++; MissingCount = VisibleCount = fallbackUsed = 0; following.Clear(); current.Clear();
        foreach (var entry in statics.Values) entry.Visible = entry.FallbackVisible = false;
        foreach (var frame in right) following[frame.Key] = frame;
        foreach (var frame in left) current.Add(frame.Key);
        Reclaim();
        double now = Time.realtimeSinceStartupAsDouble;
        foreach (var frame in left)
        {
            if (retry.TryGetValue(frame.Key, out double nextRetry) && now < nextRetry) { MissingCount++; continue; }
            entries.TryGetValue(frame.Key, out var entry);
            try
            {
                if (CreatureReplayRules.CompactStaticTrap(frame))
                {
                    if (entry != null) { entry.Dispose(); entries.Remove(frame.Key); entry = null; }
                    ApplyStatic(frame, mix); retry.Remove(frame.Key); continue;
                }
                if (entry != null && (entry.Kind != frame.Kind || entry.Path != frame.SourcePath))
                { entry.Dispose(); entries.Remove(frame.Key); entry = null; }
                if (entry == null)
                {
                    var native = CreatureReplayResources.Find(frame.Kind, frame.SourcePath);
                    if (!native) throw new InvalidOperationException("原版生物模板不可用：" + frame.Kind);
                    if (entries.Count >= CreatureReplayRules.MaximumDynamicEntities * 2 + 16) throw new InvalidOperationException("生物视觉池已满");
                    entry = new Entry { Kind = frame.Kind, Path = frame.SourcePath,
                        Visual = new VisualReplica(native!, false, CreatureReplayRules.MaximumNodes) };
                    try
                    {
                        entry.Visual.Root.name = "PEAK Replay Creature - " + frame.Kind;
                        entry.Appearance = new NativeVisualAppearance.Playback(entry.Visual, native!);
                        var spider = native!.GetComponent<Spider>();
                        if (spider && spider.line && entry.Visual.TryCloneTransform(spider.line.transform, out var target))
                            entry.Line = CloneLine(spider.line, target);
                        entries.Add(frame.Key, entry);
                    }
                    catch { entry.Dispose(); entry = null; throw; }
                }
                following.TryGetValue(frame.Key, out var next);
                bool interpolate = next != null && CreatureReplayRules.CanInterpolate(frame, next);
                if (discontinuity) entry.Visual.InvalidatePose();
                entry.Visual.Apply(frame.Pose, interpolate ? next!.Pose : null, mix);
                entry.Appearance?.Apply(frame.Visuals);
                ApplyLine(entry.Line, frame.Line, interpolate ? next!.Line : null, mix);
                entry.Seen = serial; retry.Remove(frame.Key);
                if (frame.Pose.Active) VisibleCount++;
            }
            catch (Exception e)
            {
                entry?.Visual.Hide(); MissingCount++; retry[frame.Key] = now + 1;
                if (warned.Count < 128 && warned.Add(frame.Kind)) warning?.Invoke("生物视觉缺失：" + e.Message);
            }
        }
        ReclaimStaticFallbacks();
        foreach (var entry in statics.Values) ApplyStaticVisibility(entry);
        Reclaim();
        removed.Clear(); foreach (var pair in retry) if (!current.Contains(pair.Key)) removed.Add(pair.Key);
        foreach (var key in removed) retry.Remove(key);
    }
    private void ApplyStatic(CreatureReplayFrame frame, float mix)
    {
        if (!statics.TryGetValue(frame.Key, out var entry) || !entry.Root || entry.Path != frame.SourcePath)
        {
            if (entry != null)
            {
                foreach (var renderer in entry.Renderers) if (renderer) staticOwners.Remove(renderer);
                if (entry.Fallback != null) fallbackCount--; entry.Restore(); statics.Remove(frame.Key);
            }
            var native = CreatureReplayResources.Find("spore-trap", frame.SourcePath);
            if (!native) throw new InvalidOperationException("原版静态孢子陷阱不可用：" + frame.SourcePath);
            if (statics.Count >= CreatureReplayRules.MaximumStaticTraps) throw new InvalidOperationException("静态孢子陷阱目录已满");
            Hide(native!);
            var root = native!.transform;
            entry = new StaticEntry { Root = root, Path = frame.SourcePath,
                OriginalPosition = root.localPosition, OriginalRotation = root.localRotation, OriginalScale = root.localScale,
                Renderers = native.GetComponentsInChildren<Renderer>(true) };
            statics.Add(frame.Key, entry);
            foreach (var renderer in entry.Renderers) if (renderer && hidden.ContainsKey(renderer)) staticOwners[renderer] = entry;
        }
        following.TryGetValue(frame.Key, out var next);
        bool blend = next != null && CreatureReplayRules.CompactStaticTrap(next) && CreatureReplayRules.CanInterpolate(frame, next);
        var pose = frame.Pose;
        var nextPose = blend ? next!.Pose : pose;
        // The static model has no animated children. Most samples retain the
        // exact same root snapshot, so avoid rewriting thousands of native TRS.
        if (!ReferenceEquals(entry.AppliedPose, pose) || !ReferenceEquals(entry.AppliedNext, nextPose) ||
            !ReferenceEquals(pose, nextPose) && entry.AppliedMix != mix)
        {
            var position = V(pose.Position); var rotation = Q(pose.Rotation); var scale = V(pose.Scale);
            if (blend)
            {
                position = Vector3.LerpUnclamped(position, V(nextPose.Position), mix);
                rotation = Quaternion.SlerpUnclamped(rotation, Q(nextPose.Rotation), mix);
                scale = Vector3.LerpUnclamped(scale, V(nextPose.Scale), mix);
            }
            // Preserve the exact authored local scale when the scene already
            // has this pose (lossyScale can include a rotated nonuniform parent).
            if (entry.Root.position != position || entry.Root.rotation != rotation || entry.Root.lossyScale != scale)
            {
                entry.Root.SetPositionAndRotation(position, rotation);
                var parentScale = entry.Root.parent ? entry.Root.parent.lossyScale : Vector3.one;
                entry.Root.localScale = new Vector3(Divide(scale.x, parentScale.x), Divide(scale.y, parentScale.y), Divide(scale.z, parentScale.z));
            }
            entry.AppliedPose = pose; entry.AppliedNext = nextPose; entry.AppliedMix = mix;
        }
        entry.Seen = serial;
        // Never activate the native gameplay root. Usually fresh scene traps
        // are activeSelf=true and their original meshes can be reused directly.
        bool parentActive = !entry.Root.parent || entry.Root.parent.gameObject.activeInHierarchy;
        entry.Visible = pose.Active && entry.Root.gameObject.activeSelf;
        if (pose.Active && parentActive && !entry.Root.gameObject.activeSelf)
        {
            if (fallbackUsed >= CreatureReplayRules.MaximumDynamicEntities)
                throw new InvalidOperationException("已消耗陷阱的回拖视觉超过256个");
            if (entry.Fallback == null)
            {
                MakeFallbackRoom();
                entry.Fallback = new VisualReplica(entry.Root.gameObject, true, CreatureReplayRules.MaximumNodes);
                entry.Fallback.Root.name = "PEAK Replay Creature - static trap";
                foreach (var renderer in entry.Renderers)
                    if (renderer && hidden.TryGetValue(renderer, out var original) && entry.Fallback.TryCloneRenderer(renderer, out var copy))
                        copy.forceRenderingOff = original.ForceOff;
                // The parent's recorded biome activation still governs the inert model.
                entry.Fallback.Root.transform.SetParent(entry.Root.parent, true);
                fallbackCount++;
            }
            entry.Fallback.Apply(pose, blend ? next!.Pose : null, mix);
            // VisualReplica normally has no parent and interprets Scale as a
            // world scale. This inert fallback follows the recorded biome.
            entry.Fallback.Root.transform.localScale = entry.Root.localScale;
            entry.FallbackVisible = true; fallbackUsed++;
        }
        ApplyStaticVisibility(entry);
        if (pose.Active && parentActive) VisibleCount++;
    }
    private void ApplyStaticVisibility(StaticEntry entry)
    {
        foreach (var renderer in entry.Renderers)
            if (renderer && hidden.TryGetValue(renderer, out var original))
            {
                bool desired = original.ForceOff || !entry.Visible;
                if (renderer.forceRenderingOff != desired) renderer.forceRenderingOff = desired;
            }
        if (!entry.FallbackVisible) entry.Fallback?.Hide();
    }
    private void MakeFallbackRoom()
    {
        if (fallbackCount < CreatureReplayRules.MaximumDynamicEntities + 16) return;
        StaticEntry? oldest = null;
        foreach (var candidate in statics.Values)
            if (candidate.Fallback != null && !candidate.FallbackVisible && (oldest == null || candidate.Seen < oldest.Seen)) oldest = candidate;
        if (oldest == null) throw new InvalidOperationException("静态陷阱回拖视觉池已满");
        oldest.Fallback!.Dispose(); oldest.Fallback = null; fallbackCount--;
    }
    private void ReclaimStaticFallbacks()
    {
        fallbackSleeping.Clear();
        foreach (var entry in statics.Values) if (entry.Fallback != null && !entry.FallbackVisible) fallbackSleeping.Add(entry);
        fallbackSleeping.Sort((a, b) => a.Seen.CompareTo(b.Seen));
        for (int i = 0; i < fallbackSleeping.Count - 16; i++)
        { fallbackSleeping[i].Fallback!.Dispose(); fallbackSleeping[i].Fallback = null; fallbackCount--; }
    }
    private static Vector3 V(float[] values) => new(values[0], values[1], values[2]);
    private static Quaternion Q(float[] values) => new(values[0], values[1], values[2], values[3]);
    private static float Divide(float value, float denominator) => Mathf.Abs(denominator) > .000001f ? value / denominator : value;
    private void Reclaim()
    {
        sleeping.Clear();
        foreach (var pair in entries) if (!current.Contains(pair.Key)) { pair.Value.Visual.Hide(); sleeping.Add(pair); }
        sleeping.Sort((a, b) => a.Value.Seen.CompareTo(b.Value.Seen));
        for (int i = 0; i < sleeping.Count - 16; i++) { sleeping[i].Value.Dispose(); entries.Remove(sleeping[i].Key); }
    }
    private static LineRenderer CloneLine(LineRenderer source, Transform target)
    {
        var line = target.gameObject.AddComponent<LineRenderer>();
        line.enabled = false; line.sharedMaterials = source.sharedMaterials;
        line.useWorldSpace = source.useWorldSpace; line.widthCurve = source.widthCurve; line.widthMultiplier = source.widthMultiplier;
        line.colorGradient = source.colorGradient; line.numCapVertices = source.numCapVertices; line.numCornerVertices = source.numCornerVertices;
        line.textureMode = source.textureMode; line.alignment = source.alignment; line.loop = source.loop;
        line.generateLightingData = source.generateLightingData; line.shadowCastingMode = source.shadowCastingMode; line.receiveShadows = source.receiveShadows;
        return line;
    }
    private static void ApplyLine(LineRenderer? target, CreatureReplayLine? left, CreatureReplayLine? right, float mix)
    {
        if (!target) return;
        if (left == null) { target!.enabled = false; return; }
        bool blend = right != null && left.WorldSpace == right.WorldSpace && left.Points.Length == right.Points.Length && left.Enabled && right.Enabled;
        target!.useWorldSpace = left.WorldSpace; target.enabled = left.Enabled;
        target.widthMultiplier = blend ? Mathf.LerpUnclamped(left.Width, right!.Width, mix) : left.Width;
        target.positionCount = left.Points.Length / 3;
        for (int i = 0; i < target.positionCount; i++)
        {
            int at = i * 3;
            var a = new Vector3(left.Points[at], left.Points[at + 1], left.Points[at + 2]);
            var b = blend ? new Vector3(right!.Points[at], right.Points[at + 1], right.Points[at + 2]) : a;
            target.SetPosition(i, Vector3.LerpUnclamped(a, b, mix));
        }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; observer.Dispose();
        foreach (var entry in entries.Values) entry.Dispose(); entries.Clear();
        foreach (var entry in statics.Values) entry.Restore(); statics.Clear();
        foreach (var pair in hidden) if (pair.Key) { pair.Key.forceRenderingOff = pair.Value.ForceOff; pair.Key.enabled = pair.Value.Enabled; }
        hidden.Clear(); staticOwners.Clear(); retry.Clear();
    }
}
