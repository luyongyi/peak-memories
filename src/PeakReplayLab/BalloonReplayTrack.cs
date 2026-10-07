using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

// RPC_Init may start a coroutine before the local Character exists. Registration
// is immediate, but capture waits for the native private owner to be assigned.
internal sealed class BalloonReplayObserver : IDisposable
{
    private static readonly HashSet<BalloonReplayObserver> listeners = new();
    private static Harmony? harmony;
    private readonly Action<TiedBalloon> initialized, destroyed;
    private bool disposed;
    public BalloonReplayObserver(Action<TiedBalloon> initialized, Action<TiedBalloon> destroyed)
    {
        this.initialized = initialized; this.destroyed = destroyed;
        if (listeners.Count == 0)
        {
            var install = new Harmony("cn.mylus.peakreplaylab.balloons." + Guid.NewGuid().ToString("N"));
            try
            {
                install.Patch(AccessTools.DeclaredMethod(typeof(TiedBalloon), nameof(TiedBalloon.RPC_Init)),
                    postfix: new HarmonyMethod(typeof(BalloonReplayObserver), nameof(Initialized)));
                install.Patch(AccessTools.DeclaredMethod(typeof(TiedBalloon), "OnDestroy"),
                    prefix: new HarmonyMethod(typeof(BalloonReplayObserver), nameof(Destroyed)));
                harmony = install;
            }
            catch { install.UnpatchSelf(); throw; }
        }
        listeners.Add(this);
    }
    private static void Initialized(TiedBalloon __instance)
    { if (__instance) foreach (var listener in listeners) try { listener.initialized(__instance); } catch { } }
    private static void Destroyed(TiedBalloon __instance)
    { if (__instance) foreach (var listener in listeners) try { listener.destroyed(__instance); } catch { } }
    public void Dispose()
    {
        if (disposed) return; disposed = true; listeners.Remove(this);
        if (listeners.Count == 0) { harmony?.UnpatchSelf(); harmony = null; }
    }
}

internal sealed class BalloonReplayCapture : IDisposable
{
    private static readonly FieldInfo Owner = AccessTools.Field(typeof(TiedBalloon), "characterBalloons");
    private static readonly FieldInfo Started = AccessTools.Field(typeof(TiedBalloon), "initialTime");
    private sealed class Entry
    {
        public TiedBalloon Source = null!;
        public CharacterBalloons? Owner;
        public string Key = "", OwnerId = "";
        public double Started, RetryAfter;
        public bool Dirty = true;
        public BalloonReplayFrame? Last;
    }
    private readonly Dictionary<int, Entry> entries = new();
    private readonly List<int> removed = new();
    private readonly List<BalloonReplayFrame> frames = new();
    private BalloonReplayFrame[] previous = Array.Empty<BalloonReplayFrame>();
    private readonly HashSet<string> warned = new(StringComparer.Ordinal);
    private readonly BalloonReplayObserver observer;
    private readonly Action<string>? warning;
    private readonly int scene = SceneManager.GetActiveScene().handle;
    private long serial;
    private bool disposed;
    public BalloonReplayCapture(Action<string>? warning = null)
    {
        this.warning = warning;
        observer = new BalloonReplayObserver(Register, Remove);
        try
        {
            if (Owner == null || Started == null) throw new InvalidOperationException("Native tied balloon attachment fields changed.");
            foreach (var source in Object.FindObjectsByType<TiedBalloon>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Register(source);
        }
        catch { observer.Dispose(); throw; }
    }
    private void Register(TiedBalloon source)
    {
        if (disposed || ReplaySafety.Active || !source || source.gameObject.scene.handle != scene) return;
        int id = source.GetInstanceID();
        if (entries.TryGetValue(id, out var old))
        {
            if (old.Source == source) { old.Dirty = true; old.RetryAfter = 0; return; }
            entries.Remove(id);
        }
        if (entries.Count >= BalloonReplayRules.MaximumEntities) { Warn("limit", "绑头气球超过128个，超出部分未记录。"); return; }
        entries.Add(id, new Entry { Source = source, Key = "balloon:" + id + ":" + (++serial) });
    }
    private void Remove(TiedBalloon source) { if (source) entries.Remove(source.GetInstanceID()); }
    public BalloonReplayFrame[] Capture(double now)
    {
        if (disposed) throw new ObjectDisposedException(nameof(BalloonReplayCapture));
        frames.Clear(); removed.Clear();
        foreach (var pair in entries)
        {
            var entry = pair.Value;
            if (!entry.Source) { removed.Add(pair.Key); continue; }
            if (now < entry.RetryAfter)
            { if (entry.Last != null) frames.Add(entry.Last); continue; }
            try
            {
                if (entry.Dirty || !entry.Owner)
                {
                    var owner = Owner.GetValue(entry.Source) as CharacterBalloons;
                    // A reinitialization/temporary observation failure must not
                    // masquerade as destruction of an already observed balloon.
                    if (!owner) { if (entry.Last != null) frames.Add(entry.Last); continue; }
                    var character = owner!.GetComponent<Character>();
                    if (!character) { if (entry.Last != null) frames.Add(entry.Last); continue; }
                    entry.Owner = owner; entry.OwnerId = PeakReplayLab.Capture.Id(character);
                    entry.Started = Convert.ToDouble(Started.GetValue(entry.Source)); entry.Dirty = false;
                }
                bool active = entry.Source.gameObject.activeInHierarchy;
                int color = entry.Source.colorIndex; float offset = entry.Owner!.headOffset;
                var old = entry.Last;
                if (old == null || old.OwnerId != entry.OwnerId || old.ColorIndex != color || old.HeadOffset != offset ||
                    old.Started != entry.Started || old.Active != active)
                {
                    var next = new BalloonReplayFrame { Key = entry.Key, OwnerId = entry.OwnerId, ColorIndex = color,
                        HeadOffset = offset, Started = entry.Started, Active = active };
                    BalloonReplayRules.Validate(next); entry.Last = next;
                }
                frames.Add(entry.Last!);
            }
            catch (Exception e)
            {
                entry.RetryAfter = now + 2;
                if (entry.Last != null) frames.Add(entry.Last);
                Warn("capture", "绑头气球采集暂不可用：" + e.Message);
            }
        }
        foreach (int id in removed) entries.Remove(id);
        bool same = frames.Count == previous.Length;
        if (same) for (int i = 0; i < frames.Count; i++) if (!ReferenceEquals(frames[i], previous[i])) { same = false; break; }
        return same ? previous : previous = frames.ToArray();
    }
    private void Warn(string key, string value) { if (warned.Add(key)) try { warning?.Invoke(value); } catch { } }
    public void Dispose()
    {
        if (disposed) return; disposed = true; observer.Dispose(); entries.Clear(); frames.Clear(); removed.Clear();
        previous = Array.Empty<BalloonReplayFrame>();
    }
}

internal sealed class BalloonReplayPlayback : IDisposable
{
    private sealed class Visual : IDisposable
    {
        public readonly VisualReplica Body;
        private readonly LineRenderer line;
        private readonly Transform knot;
        private readonly Quaternion authoredRotation;
        private readonly float height;
        private int color = -1;
        public int Seen;
        public Visual(TiedBalloon source)
        {
            if (!source.balloonRenderer || !source.start || !source.anchor || !source.lr)
                throw new InvalidOperationException("Native tied balloon visual references are missing.");
            Body = new VisualReplica(source.balloonRenderer.gameObject, true);
            try
            {
                var root = Body.Root.transform;
                root.localScale = source.balloonRenderer.transform.lossyScale;
                authoredRotation = source.balloonRenderer.transform.rotation;
                height = Vector3.Distance(source.balloonRenderer.transform.position, source.anchor.position);
                if (height < .1f || height > 10) throw new InvalidOperationException("Invalid native tied balloon height.");
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
            }
            catch { Body.Dispose(); throw; }
        }
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
        }
        public void Dispose() => Body.Dispose();
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
    private int creationFrame = -1, createdThisFrame;
    private BalloonReplayFrame[] requested = Array.Empty<BalloonReplayFrame>();
    private double requestedTime;
    private bool pendingBuild;
    private bool disposed;
    public int VisibleCount { get; private set; }
    public int MissingCount { get; private set; }
    public BalloonReplayPlayback(IReadOnlyDictionary<string, VisualActor> actors, Action<string>? warning = null)
    {
        this.actors = actors; this.warning = warning;
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
    public void Enforce()
    {
        if (disposed || enforcedFrame == Time.frameCount) return; enforcedFrame = Time.frameCount;
        foreach (var pair in hidden) if (pair.Key && !pair.Key.forceRenderingOff) pair.Key.forceRenderingOff = true;
        // ApplyTime legitimately skips a paused/unchanged cursor. Finish the
        // latest requested snapshot over render frames without advancing time.
        if (pendingBuild) ApplyRequested();
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
        if (creationFrame != Time.frameCount) { creationFrame = Time.frameCount; createdThisFrame = 0; }
        serial++; VisibleCount = MissingCount = 0;
        current.Clear(); foreach (var state in left) if (state.Active) current.Add(state.Key);
        removed.Clear(); foreach (var pair in visuals) if (!current.Contains(pair.Key)) removed.Add(pair.Key);
        foreach (string key in removed) { visuals[key].Dispose(); visuals.Remove(key); }
        // Discrete attachments use left only. Actor.HeadPosition already follows
        // interpolated actor pose; future births/colors must never leak backward.
        for (int stateIndex = 0; stateIndex < left.Length; stateIndex++)
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
                    if (createdThisFrame >= 4) { pendingBuild = true; MissingCount++; continue; }
                    createdThisFrame++;
                    if (!resource) throw new InvalidOperationException("Native tiedballoon prefab is unavailable.");
                    visual = new Visual(resource!); visuals.Add(state.Key, visual);
                }
                int slot = 0, count = 0;
                for (int i = 0; i < left.Length; i++) if (left[i].Active && left[i].OwnerId == state.OwnerId)
                { if (i < stateIndex) slot++; count++; }
                visual.Apply(state, actor, time, colors, slot, count); visual.Seen = serial; VisibleCount++;
            }
            catch (Exception e)
            {
                visual?.Body.Hide(); MissingCount++;
                if (failed.Count < 4096 && failed.Add(state.Key)) try { warning?.Invoke("绑头气球缺失：" + e.Message); } catch { }
            }
        }
        // Destroy absent lifetimes immediately; at most 128 live clones, and a
        // backwards seek recreates from immutable native resources without RPCs.
        removed.Clear(); foreach (var pair in visuals) if (pair.Value.Seen != serial) removed.Add(pair.Key);
        foreach (string key in removed) { visuals[key].Dispose(); visuals.Remove(key); }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; observer.Dispose();
        pendingBuild = false; requested = Array.Empty<BalloonReplayFrame>();
        foreach (var pair in hidden) try { if (pair.Key) pair.Key.forceRenderingOff = pair.Value; } catch { }
        foreach (var visual in visuals.Values) try { visual.Dispose(); } catch { }
        hidden.Clear(); visuals.Clear(); failed.Clear();
    }
}
