using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace PeakReplayLab;

internal sealed class ItemPlayback : IDisposable
{
    private sealed class Visual : IDisposable
    {
        public readonly VisualReplica Replica;
        private readonly List<(Renderer Renderer, int Slot, Color Tint)> tints = new();
        private readonly MaterialPropertyBlock block = new();
        private readonly Vector3 centerOfMass;
        private NativeVisualAppearance.Playback? appearance;
        private NativeLightAppearance.Playback? lighting;
        private FrogIdleAnimationRuntime? idleRuntime;
        private EarlyWormAnimationRuntime? wormRuntime;
        private CreatureIdleAnimation? idleFrame;
        private ObjectPose? idlePose;
        private bool idlePresented;
        private readonly Item prefab;
        private int tintRenderer;
        private readonly bool supportsCooking;
        private bool hadRecordedAppearance;
        private bool presented, restartAppearance, restartLights;
        private bool preparingAppearance, preparingLights, preparingReveal;
        private NativeRendererFrame[]? preparingVisuals;
        private NativeLightFrame[]? preparingLightFrames;
        private float lastCooked = float.NaN;
        public long LastSeen;
        public string Key = "";
        public string Template => prefab.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture);
        public bool Visible => presented && Replica.Root && Replica.Root.activeSelf;
        public bool NeedsRefresh { get; private set; }
        public bool NativeIdle => idlePresented;
        public bool Ready => Replica.IsReady && !Replica.IsResetting && appearance?.IsReady == true && lighting?.IsReady == true && tintRenderer >= Replica.Renderers.Count;
        public Visual(Item prefab)
        {
            this.prefab = prefab;
            Replica = VisualReplica.BeginBuild(prefab.gameObject, shareImmutableMaterials: true);
            supportsCooking = prefab.GetComponent<ItemCooking>();
            var body = prefab.GetComponent<Rigidbody>();
            centerOfMass = body ? body.centerOfMass : prefab.centerOfMass;
        }
        public bool ContinueBuild(PresentationWorkBudget budget)
        {
            if (Replica.IsResetting && !Replica.ContinueReuse(budget)) return false;
            if (!Replica.ContinueBuild(budget)) return false;
            if (appearance == null) { if (!budget.Build()) return false; appearance = new NativeVisualAppearance.Playback(Replica, prefab.gameObject, true); }
            if (!appearance.ContinueBuild(budget)) return false;
            if (lighting == null) { if (!budget.Build()) return false; lighting = new NativeLightAppearance.Playback(Replica, prefab.gameObject, true); }
            if (!lighting.ContinueBuild(budget)) return false;
            while (tintRenderer < Replica.Renderers.Count && budget.Build())
            {
                var renderer = Replica.Renderers[tintRenderer++];
                {
                    var materials = renderer.sharedMaterials;
                    for (int i = 0; i < materials.Length; i++)
                        if (materials[i] && materials[i].HasProperty("_Tint")) tints.Add((renderer, i, materials[i].GetColor("_Tint")));
                }
            }
            return Ready;
        }
        public void BeginReuse()
        { RestartPresentation(); idleRuntime?.Dispose(); idleRuntime = null; wormRuntime?.Dispose(); wormRuntime = null; lighting?.Dispose(); appearance?.Dispose(); lighting = null; appearance = null; tints.Clear(); tintRenderer = 0; lastCooked = float.NaN; hadRecordedAppearance = false; Replica.BeginReuse(); }
        public void RestartPresentation()
        {
            presented = false;
            restartAppearance = restartLights = true;
            preparingAppearance = preparingLights = false;
            NeedsRefresh = false;
            preparingVisuals = null; preparingLightFrames = null;
            idleRuntime?.Invalidate(); idlePresented = false; idleFrame = null; idlePose = null;
            wormRuntime?.Invalidate();
            Replica.Hide(); Replica.InvalidatePose();
        }
        public void Sleep() { RestartPresentation(); Replica.Sleep(); }
        public bool Prepare(ItemFrame frame, PresentationWorkBudget budget)
        {
            // A deferred native pass finishes the snapshot it originally began,
            // even if playback has since moved to a new page. Do not mistake
            // that completion for the latest request and take the static fast path.
            if (!preparingAppearance)
            { preparingVisuals = frame.Visuals; preparingReveal = frame.RevealBackpackContents; }
            NeedsRefresh = false;
            bool appearanceReady = appearance!.TryApply(frame.Visuals, budget, frame.RevealBackpackContents, restartAppearance);
            restartAppearance = false;
            preparingAppearance = !appearanceReady;
            if (!appearanceReady) return false;
            NeedsRefresh = !ReferenceEquals(preparingVisuals, frame.Visuals) || preparingReveal != frame.RevealBackpackContents;
            if (!preparingLights) preparingLightFrames = frame.Lights;
            bool lightsReady = lighting!.TryApply(frame.Lights, budget, restartLights);
            restartLights = false;
            preparingLights = !lightsReady;
            NeedsRefresh |= lightsReady && !ReferenceEquals(preparingLightFrames, frame.Lights);
            if (lightsReady && frame.IdleAnimation != null &&
                (frame.ItemId == 184 ? wormRuntime == null : idleRuntime == null))
            {
                if (!budget.Build()) return false;
                if (frame.ItemId == 184)
                {
                    if (!EarlyWormAnimationRuntime.TryCreate(Replica, prefab, out wormRuntime))
                        throw new InvalidOperationException("原生 EarlyWorm 动画资源不可用：" + frame.Prefab);
                }
                else if (!FrogIdleAnimationRuntime.TryCreate(Replica, prefab, out idleRuntime))
                    throw new InvalidOperationException("青蛙闲置动画资源不可用：" + frame.Prefab);
            }
            return lightsReady;
        }
        public void Apply(ItemFrame a, ItemFrame? b, float mix, Transform? backpackSlot, double replayTime,
            ObjectPose? motionLeft = null, ObjectPose? motionRight = null)
        {
            b = BackpackAttachmentPolicy.CanInterpolate(a, b) ? b : null;
            var leftPose = motionLeft ?? a.Pose;
            var rightPose = motionLeft == null ? b?.Pose : motionRight;
            var root = Replica.Root.transform;
            // Detach before the world-pose path resumes; no bag/hand interpolation
            // is allowed to leak across the recorded state boundary.
            if (!backpackSlot && root.parent) { root.SetParent(null, true); Replica.InvalidatePose(); }
            bool nativeIdle = a.IdleAnimation != null;
            bool resetIdle = !idlePresented || !ReferenceEquals(idlePose, a.Pose) || !ReferenceEquals(idleFrame, a.IdleAnimation);
            // A clip overwrites clone bones. Returning to a held geometry packet
            // must restore it even if its Pose happens to be the same reference.
            if (nativeIdle ? resetIdle : idlePresented) Replica.InvalidatePose();
            if (resetIdle) idleRuntime?.Invalidate();
            if (resetIdle) wormRuntime?.Invalidate();
            Replica.Apply(leftPose, rightPose, mix, a.RevealBackpackContents);
            if (backpackSlot)
            {
                if (root.parent != backpackSlot) root.SetParent(backpackSlot, false);
                Vector3 parentScale = backpackSlot!.lossyScale;
                root.SetLocalPositionAndRotation(new Vector3(
                    BackpackAttachmentPolicy.LocalOffset(centerOfMass.x, parentScale.x),
                    BackpackAttachmentPolicy.LocalOffset(centerOfMass.y, parentScale.y),
                    BackpackAttachmentPolicy.LocalOffset(centerOfMass.z, parentScale.z)), Quaternion.identity);
                Vector3 aScale = new(leftPose.Scale[0], leftPose.Scale[1], leftPose.Scale[2]);
                Vector3 bScale = rightPose == null ? aScale : new Vector3(rightPose.Scale[0], rightPose.Scale[1], rightPose.Scale[2]);
                root.localScale = Vector3.Scale(Vector3.Lerp(aScale, bScale, mix), new Vector3(
                    BackpackAttachmentPolicy.InverseScale(parentScale.x), BackpackAttachmentPolicy.InverseScale(parentScale.y),
                    BackpackAttachmentPolicy.InverseScale(parentScale.z)));
            }
            if (nativeIdle) ApplyNative(a.IdleAnimation!, replayTime);
            if (a.Visuals == null)
            {
                if (hadRecordedAppearance) lastCooked = float.NaN;
            }
            if (a.Visuals == null && supportsCooking && lastCooked != a.Cooked)
            {
                lastCooked = a.Cooked;
                Color cooked = ItemCooking.GetCookColor((int)a.Cooked);
                foreach (var entry in tints)
                {
                    // This fallback is for older recordings. Current recordings carry
                    // the native effective tint, which already includes cooking.
                    entry.Renderer.GetPropertyBlock(block, entry.Slot);
                    block.SetColor("_Tint", entry.Tint * cooked);
                    entry.Renderer.SetPropertyBlock(block, entry.Slot);
                }
            }
            appearance!.ReapplyVisibility(a.RevealBackpackContents);
            hadRecordedAppearance = a.Visuals != null;
            idlePresented = nativeIdle; idleFrame = a.IdleAnimation; idlePose = a.Pose;
            presented = true;
        }
        private void ApplyNative(CreatureIdleAnimation frame, double time)
        { if (prefab.itemID == 184) wormRuntime?.Apply(frame, time); else idleRuntime?.Apply(frame, time); }
        public void AdvanceIdle(double replayTime) { if (idlePresented && idleFrame != null) ApplyNative(idleFrame, replayTime); }
        public void Retire() { presented = false; idleRuntime?.Dispose(); wormRuntime?.Dispose(); lighting?.Dispose(); appearance?.Dispose(); Replica.Retire(); }
        public void Dispose() { presented = false; idleRuntime?.Dispose(); wormRuntime?.Dispose(); lighting?.Dispose(); appearance?.Dispose(); Replica.Dispose(); }
    }

    private readonly Dictionary<string, Visual> visuals = new(StringComparer.Ordinal);
    private readonly Dictionary<Renderer, bool> originals = new();
    private readonly NativeLightAppearance.Originals originalLights = new();
    private readonly HashSet<string> warned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> retryAt = new(StringComparer.Ordinal);
    private readonly HashSet<string> present = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ItemFrame> next = new(StringComparer.Ordinal);
    private readonly HashSet<int> originalItems = new();
    private readonly List<string> removals = new();
    private readonly List<KeyValuePair<string, Visual>> hidden = new();
    private ItemFrame[]? previousLeft;
    private ItemFrame[]? previousRight;
    private float previousMix;
    private double previousReplayTime;
    private ItemObservationTimeline? previousObservations;
    private bool hasSparseItems;
    private readonly List<Visual> idleVisuals = new();
    private long serial;
    private readonly Action<string>? warning;
    private readonly VisualActor[] actors;
    private float nextOriginalRefresh;
    private bool warningLimit;
    private int missingCount;
    private int visibleCount;
    private bool hasBackpackItems;
    private readonly PresentationWorkBudget workBudget;
    private readonly Func<Vector3, float>? priorityScore;
    private readonly List<ItemFrame> ordered = new();
    private readonly PresentationTemplatePool<Visual> pool = new(128, 8192, static visual => visual.Retire());
    private bool pending;
    private bool disposed;
    private int appliedFrame = -1;
    public int VisibleCount => visibleCount;
    public int MissingCount => missingCount;
    public ItemPlayback(Action<string>? warning = null, IReadOnlyDictionary<string, VisualActor>? actors = null, PresentationWorkBudget? workBudget = null, Func<Vector3, float>? priorityScore = null)
    { this.warning = warning; this.actors = actors?.Values.ToArray() ?? Array.Empty<VisualActor>(); this.workBudget = workBudget ?? new(); this.priorityScore = priorityScore; }

    private Transform? BackpackSlot(ItemFrame item)
    {
        if (item.State != BackpackAttachmentPolicy.InBackpack || actors.Length == 0) return null;
        Transform? match = null;
        foreach (var actor in actors)
        {
            if (!actor.Visible || actor.State == null) continue;
            int slot = BackpackAttachmentPolicy.FindSlot(item, actor.State);
            if (slot == -2) return null;
            if (slot < 0) continue;
            var anchor = actor.BackpackSlot(slot);
            if (!anchor) continue;
            if (match) return null; // A stale/ambiguous inventory must not bind to a guessed actor.
            match = anchor;
        }
        return match;
    }

    public void Apply(ItemFrame[] left, ItemFrame[] right, float mix, bool discontinuity = false, double replayTime = 0, ItemObservationTimeline? observations = null)
    {
        if (disposed) throw new ObjectDisposedException(nameof(ItemPlayback));
        workBudget.BeginFrame(Time.frameCount);
        VisualReplica.DrainRetired(workBudget);
        appliedFrame = Time.frameCount;
        if (!discontinuity && !pending && !hasBackpackItems && (!hasSparseItems || replayTime == previousReplayTime) &&
            ReferenceEquals(observations, previousObservations) && missingCount == 0 && ReferenceEquals(left, previousLeft) && ReferenceEquals(right, previousRight) &&
            (mix == previousMix || ReferenceEquals(left, right)))
        {
            if (replayTime != previousReplayTime) foreach (var visual in idleVisuals) visual.AdvanceIdle(replayTime);
            previousReplayTime = replayTime; return;
        }
        previousLeft = left; previousRight = right; previousMix = mix; previousReplayTime = replayTime;
        previousObservations = observations;
        idleVisuals.Clear();
        pending = false;
        missingCount = 0;
        visibleCount = 0; hasBackpackItems = hasSparseItems = false; serial++;
        present.Clear(); next.Clear();
        foreach (var item in right) next[item.Key] = item;
        bool prioritize = false;
        foreach (var item in left) if (item.Pose.Active)
        { present.Add(item.Key); if (!visuals.TryGetValue(item.Key, out var existing) || !existing.Ready) prioritize = true; }
        removals.Clear();
        foreach (var entry in visuals) if (!present.Contains(entry.Key))
        {
            entry.Value.Sleep(); removals.Add(entry.Key);
            if (entry.Value.Ready) pool.Return(entry.Value.Template, entry.Value, entry.Value.Replica.RetainedCost);
            else entry.Value.Retire();
        }
        foreach (var key in removals) visuals.Remove(key);
        ordered.Clear(); ordered.AddRange(left);
        if (prioritize && priorityScore != null)
        {
            ordered.Sort((a, b) =>
            {
                int attached = (b.HolderId.Length != 0).CompareTo(a.HolderId.Length != 0); if (attached != 0) return attached;
                int distance = priorityScore(new Vector3(a.Pose.Position[0], a.Pose.Position[1], a.Pose.Position[2])).CompareTo(priorityScore(new Vector3(b.Pose.Position[0], b.Pose.Position[1], b.Pose.Position[2])));
                return distance != 0 ? distance : string.CompareOrdinal(a.Key, b.Key);
            });
        }
        foreach (var stored in ordered)
        {
            var item = stored;
            next.TryGetValue(item.Key, out var future);
            float itemMix = mix;
            ObjectPose? motionLeft = null, motionRight = null;
            if (observations != null && observations.TrySample(item.Key, replayTime, out var observed, out var observedNext, out float observedMix))
            { motionLeft = observed.Pose; motionRight = observedNext.Pose; itemMix = observedMix; }
            hasSparseItems |= item.SampleTimeKnown && item.ClockMode == CreatureReplayRules.ActiveClock;
            if (!item.Pose.Active) continue;
            hasBackpackItems |= item.State == BackpackAttachmentPolicy.InBackpack;
            if (!visuals.TryGetValue(item.Key, out var visual))
            {
                if (retryAt.TryGetValue(item.Key, out var retry) && Time.unscaledTime < retry) { missingCount++; continue; }
                try
                {
                    Item? prefab = null;
                    if (item.ItemId >= 0 && item.ItemId <= ushort.MaxValue) ItemDatabase.TryGetItem((ushort)item.ItemId, out prefab);
                    if ((!prefab || prefab.name != item.Prefab) && !ItemDatabase.TryGetItem(item.Prefab, out prefab))
                        throw new InvalidOperationException("找不到物品资源 " + item.Prefab);
                    if (prefab == null) throw new InvalidOperationException("物品资源为空 " + item.Prefab);
                    string template = prefab.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    visual = TakePooledVisual(template, item.Key);
                    if (visual != null) { if (visual.Key != item.Key) visual.BeginReuse(); }
                    else { if (!VisualReplica.CanStartBuild || !workBudget.Create()) { pending = true; missingCount++; continue; } visual = new Visual(prefab); }
                    visual.Key = item.Key; visuals.Add(item.Key, visual); retryAt.Remove(item.Key);
                }
                catch (Exception e)
                {
                    visual?.Retire(); visuals.Remove(item.Key);
                    missingCount++;
                    retryAt[item.Key] = Time.unscaledTime + 1;
                    if (warned.Count < ItemCapture.MaximumItems)
                    { if (warned.Add(item.Key)) warning?.Invoke("无法呈现物品：" + e.Message); }
                    else if (!warningLimit) { warningLimit = true; warning?.Invoke("物品缺失报告已达上限，后续同类提示已合并。"); }
                    continue;
                }
            }
            try
            {
                // A seek must hide stale geometry before any build stage can
                // yield. Its reset remains pending until each native pass runs.
                if (discontinuity) visual.RestartPresentation();
                if (!visual.ContinueBuild(workBudget) || !visual.Prepare(item, workBudget))
                {
                    pending = true;
                    // Keep the last complete presentation during ordinary
                    // playback. New, sleeping and seeking replicas stay hidden.
                    if (visual.Visible) visibleCount++; else missingCount++;
                    continue;
                }
                visual.LastSeen = serial;
                visual.Apply(item, future, itemMix, BackpackSlot(item), replayTime, motionLeft, motionRight);
                if (visual.NativeIdle) idleVisuals.Add(visual);
                // Finishing an older immutable snapshot may still present this
                // moving item; revisit the latest target even if it is static.
                pending |= visual.NeedsRefresh;
                if (item.Pose.Active) visibleCount++;
            }
            catch (Exception e)
            { visual.Retire(); visuals.Remove(item.Key); missingCount++; retryAt[item.Key] = Time.unscaledTime + 1; if (warned.Count < ItemCapture.MaximumItems && warned.Add(item.Key)) warning?.Invoke("无法呈现物品：" + e.Message); }
        }
        // Retain a small LRU of hidden entities for backward seeks instead of rebuilding
        // their meshes/materials each time an item is consumed, stashed or revisited.
        removals.Clear(); foreach (var entry in retryAt) if (!present.Contains(entry.Key)) removals.Add(entry.Key);
        foreach (var key in removals) retryAt.Remove(key);
    }

    // Keep captured pool predicates on the creation path: a lambda capturing the
    // foreach item would otherwise allocate a closure for every live item/frame.
    private Visual? TakePooledVisual(string template, string key) =>
        pool.Take(template, candidate => candidate.Key == key, candidate => !present.Contains(candidate.Key));

    private void HideOriginalItems()
    {
        if (Time.unscaledTime >= nextOriginalRefresh)
        {
            nextOriginalRefresh = Time.unscaledTime + .5f;
            foreach (var item in Item.ALL_ITEMS)
            {
                if (!item || !item.gameObject.scene.IsValid()) continue;
                if (!originalItems.Add(item.GetInstanceID())) continue;
                originalLights.Hide(item.gameObject);
                foreach (var renderer in item.GetComponentsInChildren<Renderer>(true))
                {
                    if (!renderer) continue;
                    if (!originals.ContainsKey(renderer)) originals.Add(renderer, renderer.forceRenderingOff);
                }
            }
        }
        // A native LateUpdate can reset the flag even while playback time is paused.
        foreach (var original in originals) if (original.Key && !original.Key.forceRenderingOff) original.Key.forceRenderingOff = true;
        originalLights.Enforce();
    }

    public void Enforce(bool completePending = true)
    {
        if (disposed) return;
        workBudget.BeginFrame(Time.frameCount); VisualReplica.DrainRetired(workBudget);
        HideOriginalItems();
        if (completePending && pending && appliedFrame != Time.frameCount && previousLeft != null && previousRight != null)
            Apply(previousLeft, previousRight, previousMix, replayTime: previousReplayTime, observations: previousObservations);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; pending = false; previousLeft = previousRight = null;
        visibleCount = missingCount = 0; hasBackpackItems = false;
        try { originalLights.Dispose(); } catch (Exception e) { ReportCleanup("恢复物品灯光失败：" + e.Message); }
        foreach (var entry in originals)
            try { if (entry.Key) entry.Key.forceRenderingOff = entry.Value; }
            catch (Exception e) { ReportCleanup("恢复物品渲染器失败：" + e.Message); }
        originals.Clear();
        foreach (var value in visuals.Values)
            try { value.Dispose(); }
            catch (Exception e) { ReportCleanup("清理物品视觉副本失败：" + e.Message); }
        visuals.Clear(); idleVisuals.Clear(); warned.Clear(); retryAt.Clear(); present.Clear(); next.Clear(); hidden.Clear(); removals.Clear(); originalItems.Clear();
        pool.Clear(); VisualReplica.FlushRetired();
    }

    private void ReportCleanup(string message) { try { warning?.Invoke(message); } catch { } }
}
