using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

internal sealed class BalloonReplayPlayback : IDisposable
{
    private sealed class Visual : IDisposable
    {
        public readonly VisualReplica Body;
        private LineRenderer line = null!;
        private Transform knot = null!;
        private readonly TiedBalloon source;
        private bool initialized;
        private readonly Quaternion authoredRotation;
        private readonly float height;
        private int color = -1;
        public int Seen;
        public string Key = "";
        public bool Ready => Body.IsReady && !Body.IsResetting && initialized;
        public Visual(TiedBalloon source)
        {
            if (!source.balloonRenderer || !source.start || !source.anchor || !source.lr)
                throw new InvalidOperationException("Native tied balloon visual references are missing.");
            this.source = source;
            Body = VisualReplica.BeginBuild(source.balloonRenderer.gameObject, true);
            authoredRotation = source.balloonRenderer.transform.rotation;
            height = Vector3.Distance(source.balloonRenderer.transform.position, source.anchor.position);
            if (height < .1f || height > 10) { Body.Retire(); throw new InvalidOperationException("Invalid native tied balloon height."); }
        }
        public bool ContinueBuild(PresentationWorkBudget budget)
        {
            if (Body.IsResetting && !Body.ContinueReuse(budget)) return false;
            if (!Body.ContinueBuild(budget)) return false;
            if (initialized) return true;
            if (!budget.Build()) return false;
            try
            {
                var root = Body.Root.transform;
                root.localScale = source.balloonRenderer.transform.lossyScale;
                var knotObject = new GameObject("Replay balloon knot"); knot = knotObject.transform;
                knot.SetParent(root, false);
                knot.localPosition = source.balloonRenderer.transform.InverseTransformPoint(source.start.position);
                line = Body.Root.AddComponent<LineRenderer>();
                line.sharedMaterials = source.lr.sharedMaterials;
                line.widthCurve = source.lr.widthCurve; line.widthMultiplier = source.lr.widthMultiplier;
                line.colorGradient = source.lr.colorGradient; line.alignment = source.lr.alignment;
                line.textureMode = source.lr.textureMode; line.numCapVertices = source.lr.numCapVertices;
                line.numCornerVertices = source.lr.numCornerVertices; line.shadowCastingMode = source.lr.shadowCastingMode;
                line.receiveShadows = source.lr.receiveShadows; line.generateLightingData = source.lr.generateLightingData;
                line.useWorldSpace = true; line.loop = false; line.positionCount = 2;
                foreach (var component in Body.Root.GetComponentsInChildren<Component>(true))
                    if (!(component is Transform || component is MeshFilter || component is MeshRenderer ||
                        component is SkinnedMeshRenderer || component is LineRenderer))
                        throw new InvalidOperationException("Gameplay component leaked into balloon replay.");
                initialized = true;
            }
            catch { Body.Retire(); throw; }
            return true;
        }
        public void BeginReuse() { Body.BeginReuse(); color = -1; line.enabled = false; line.positionCount = 2; }
        public void Apply(BalloonReplayFrame state, VisualActor actor, double time, Material[] colors, int slot, int count)
        {
            if (state.ColorIndex < 0 || state.ColorIndex >= colors.Length || !colors[state.ColorIndex])
                throw new InvalidOperationException("Native balloon color is unavailable.");
            if (color != state.ColorIndex)
            {
                foreach (var renderer in Body.Renderers) renderer.sharedMaterial = colors[state.ColorIndex];
                color = state.ColorIndex;
            }
            var drift = BalloonReplayRules.Sway(state.Key, time - state.Started, slot, count);
            Vector3 anchor = actor.HeadPosition + Vector3.up * state.HeadOffset;
            Vector3 direction = new(drift.X, height + drift.Y, drift.Z);
            Body.Root.transform.SetPositionAndRotation(anchor + direction,
                Quaternion.FromToRotation(Vector3.up, direction.normalized) * authoredRotation);
            if (!Body.Root.activeSelf) Body.Root.SetActive(true);
            line.SetPosition(0, knot.position); line.SetPosition(1, anchor);
            line.enabled = true;
        }
        public void Dispose() => Body.Dispose();
        public void Retire() => Body.Retire();
    }
    private readonly IReadOnlyDictionary<string, VisualActor> actors;
    private readonly Dictionary<string, Visual> visuals = new(StringComparer.Ordinal);
    private readonly Dictionary<Renderer, bool> hidden = new();
    private readonly HashSet<string> failed = new(StringComparer.Ordinal);
    private readonly HashSet<string> current = new(StringComparer.Ordinal);
    private readonly List<string> removed = new();
    private readonly Action<string>? warning;
    private readonly BalloonReplayObserver observer;
    private readonly TiedBalloon? resource;
    private readonly Material[] colors;
    private int serial, enforcedFrame = -1;
    private int appliedFrame = -1;
    private readonly PresentationWorkBudget workBudget;
    private readonly Func<Vector3, float>? priorityScore;
    private readonly List<int> ordered = new();
    private readonly PresentationTemplatePool<Visual> pool = new(16, 2048, static visual => visual.Retire());
    private BalloonReplayFrame[] requested = Array.Empty<BalloonReplayFrame>();
    private double requestedTime;
    private bool pendingBuild;
    private bool disposed;
    public int VisibleCount { get; private set; }
    public int MissingCount { get; private set; }
    public BalloonReplayPlayback(IReadOnlyDictionary<string, VisualActor> actors, Action<string>? warning = null, PresentationWorkBudget? workBudget = null, Func<Vector3, float>? priorityScore = null)
    {
        this.actors = actors; this.warning = warning;
        this.workBudget = workBudget ?? new();
        this.priorityScore = priorityScore;
        // Verified ResourceManager paths: tiedballoon -> GO10516, character -> GO8158.
        // The CharacterBalloons palette contains the six native balloon materials.
        var prefab = Resources.Load<GameObject>("tiedballoon");
        resource = prefab ? prefab.GetComponent<TiedBalloon>() : null;
        var character = Resources.Load<GameObject>("character");
        var palette = character ? character.GetComponent<CharacterBalloons>() : null;
        colors = palette ? palette.balloonColors : Array.Empty<Material>();
        observer = new BalloonReplayObserver(Hide, _ => { });
        try
        {
            foreach (var source in Object.FindObjectsByType<TiedBalloon>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Hide(source);
        }
        catch { Dispose(); throw; }
    }
    private void Hide(TiedBalloon source)
    {
        if (!source) return;
        foreach (var renderer in source.GetComponentsInChildren<Renderer>(true))
        {
            if (hidden.ContainsKey(renderer)) continue;
            hidden.Add(renderer, renderer.forceRenderingOff); renderer.forceRenderingOff = true;
        }
    }
    public void Enforce(bool completePending = true)
    {
        if (disposed || enforcedFrame == Time.frameCount) return; enforcedFrame = Time.frameCount;
        workBudget.BeginFrame(Time.frameCount); VisualReplica.DrainRetired(workBudget);
        foreach (var pair in hidden) if (pair.Key && !pair.Key.forceRenderingOff) pair.Key.forceRenderingOff = true;
        // ApplyTime legitimately skips a paused/unchanged cursor. Finish the
        // latest requested snapshot over render frames without advancing time.
        if (completePending && pendingBuild && appliedFrame != Time.frameCount) ApplyRequested();
    }
    public void Apply(BalloonReplayFrame[] left, BalloonReplayFrame[] right, float mix, double time, bool discontinuity)
    {
        if (disposed) throw new ObjectDisposedException(nameof(BalloonReplayPlayback));
        // Replace, never append, a seek request. Enforce must not finish creating
        // objects from a cursor that the user has already left.
        requested = left; requestedTime = time; pendingBuild = false;
        ApplyRequested();
    }
    private void ApplyRequested()
    {
        var left = requested; double time = requestedTime;
        pendingBuild = false;
        // Enforce, normal playback and repeated GUI seeks share one render-frame
        // budget. Calling Apply twice cannot double the expensive clone work.
        appliedFrame = Time.frameCount; workBudget.BeginFrame(Time.frameCount);
        VisualReplica.DrainRetired(workBudget);
        serial++; VisibleCount = MissingCount = 0;
        bool prioritize = false;
        current.Clear(); foreach (var state in left) if (state.Active && actors.TryGetValue(state.OwnerId, out var owner) && owner.Visible)
        { current.Add(state.Key); if (!visuals.TryGetValue(state.Key, out var existing) || !existing.Ready) prioritize = true; }
        removed.Clear(); foreach (var pair in visuals) if (!current.Contains(pair.Key)) removed.Add(pair.Key);
        foreach (string key in removed)
        { var visual = visuals[key]; visual.Body.Sleep(); if (visual.Ready) pool.Return("balloon", visual, visual.Body.RetainedCost); else visual.Retire(); visuals.Remove(key); }
        ordered.Clear(); for (int i = 0; i < left.Length; i++) ordered.Add(i);
        if (prioritize && priorityScore != null) Prioritize(left);
        // Discrete attachments use left only. Actor.HeadPosition already follows
        // interpolated actor pose; future births/colors must never leak backward.
        foreach (int stateIndex in ordered)
        {
            var state = left[stateIndex];
            if (!state.Active) continue;
            if (!actors.TryGetValue(state.OwnerId, out var actor) || !actor.Visible) { MissingCount++; continue; }
            if (failed.Contains(state.Key) || failed.Count >= 4096) { MissingCount++; continue; }
            visuals.TryGetValue(state.Key, out var visual);
            try
            {
                if (visual == null)
                {
                    if (!resource) throw new InvalidOperationException("Native tiedballoon prefab is unavailable.");
                    visual = TakePooledVisual(state.Key);
                    if (visual != null) { if (visual.Key != state.Key) visual.BeginReuse(); }
                    else { if (!VisualReplica.CanStartBuild || !workBudget.Create()) { pendingBuild = true; MissingCount++; continue; } visual = new Visual(resource!); }
                    visual.Key = state.Key; visuals.Add(state.Key, visual);
                }
                if (!visual.ContinueBuild(workBudget)) { pendingBuild = true; MissingCount++; continue; }
                int slot = 0, count = 0;
                for (int i = 0; i < left.Length; i++) if (left[i].Active && left[i].OwnerId == state.OwnerId)
                { if (i < stateIndex) slot++; count++; }
                visual.Apply(state, actor, time, colors, slot, count); visual.Seen = serial; VisibleCount++;
            }
            catch (Exception e)
            {
                visual?.Retire(); visuals.Remove(state.Key); MissingCount++;
                if (failed.Count < 4096 && failed.Add(state.Key)) try { warning?.Invoke("绑头气球缺失：" + e.Message); } catch { }
            }
        }
        // Absent completed bodies are retained by the bounded template pool;
        // absent unfinished bodies are cancelled and retired in small batches.
    }
    // Both closures belong to cold creation/sorting paths. Keeping them inside
    // ApplyRequested would allocate on every frame, including an empty snapshot.
    private Visual? TakePooledVisual(string key) =>
        pool.Take("balloon", candidate => candidate.Key == key, candidate => !current.Contains(candidate.Key));

    private void Prioritize(BalloonReplayFrame[] left) => ordered.Sort((a, b) =>
    {
        float sa = actors.TryGetValue(left[a].OwnerId, out var aa) ? priorityScore!(aa.HeadPosition) : float.MaxValue;
        float sb = actors.TryGetValue(left[b].OwnerId, out var bb) ? priorityScore!(bb.HeadPosition) : float.MaxValue;
        int score = sa.CompareTo(sb); return score != 0 ? score : a.CompareTo(b);
    });

    public void Dispose()
    {
        if (disposed) return; disposed = true; observer.Dispose();
        pendingBuild = false; requested = Array.Empty<BalloonReplayFrame>();
        foreach (var pair in hidden) try { if (pair.Key) pair.Key.forceRenderingOff = pair.Value; } catch { }
        foreach (var visual in visuals.Values) try { visual.Dispose(); } catch { }
        hidden.Clear(); visuals.Clear(); failed.Clear();
        pool.Clear(); VisualReplica.FlushRetired();
    }
}
