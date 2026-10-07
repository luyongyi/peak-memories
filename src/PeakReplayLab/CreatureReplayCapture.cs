using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

internal static class WebWrapReplayCapture
{
    private static readonly ConditionalWeakTable<Character, Cache> caches = new();
    private sealed class Cache { public WebWrapReplayFrame? Last; }
    public static WebWrapReplayFrame? Sample(Character character)
    {
        var wrappers = character.refs?.afflictions?.webWraps;
        if (wrappers == null || wrappers.Count > WebWrapReplayRules.MaximumParts) return null;
        var cache = caches.GetValue(character, _ => new Cache());
        var previous = cache.Last;
        bool changed = previous == null || previous.Parts.Length != wrappers.Count;
        WebWrapReplayPart[]? parts = changed ? new WebWrapReplayPart[wrappers.Count] : null;
        for (int i = 0; i < wrappers.Count; i++)
        {
            var source = wrappers[i];
            if (!source || !source.transform.IsChildOf(character.transform)) return null;
            var material = source.sharedMaterial;
            if (!material || !material.HasProperty("_Clip")) return null;
            float clip = material.GetFloat("_Clip");
            var rotation = source.transform.localRotation;
            var scale = source.transform.parent.localScale;
            var old = previous != null && i < previous.Parts.Length ? previous.Parts[i] : null;
            bool active = source.gameObject.activeSelf && source.enabled;
            if (old != null && old.Active == active && old.Clip == clip && Quat(old.Rotation, rotation) && Vec(old.ParentScale, scale))
            { if (parts != null) parts[i] = old; continue; }
            if (parts == null) { parts = (WebWrapReplayPart[])previous!.Parts.Clone(); changed = true; }
            parts[i] = new WebWrapReplayPart { Path = ActorJointPaths.Path(source.transform, character.transform), Active = active,
                Clip = clip, Rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w }, ParentScale = new[] { scale.x, scale.y, scale.z } };
        }
        return changed ? cache.Last = new WebWrapReplayFrame { Parts = parts! } : previous;
    }
    private static bool Vec(float[] a, Vector3 p) => a[0] == p.x && a[1] == p.y && a[2] == p.z;
    private static bool Quat(float[] a, Quaternion p) => a[0] == p.x && a[1] == p.y && a[2] == p.z && a[3] == p.w;
}

internal static class CreatureReplayResources
{
    private static GameObject? spider, zombie;
    private static readonly Dictionary<string, GameObject> traps = new(StringComparer.Ordinal);
    private sealed class Signature { public bool Valid; }
    private static readonly ConditionalWeakTable<Transform, Signature> signatures = new();
    internal static bool Ours(Transform source)
    { for (var t = source; t; t = t.parent) if (t.name.StartsWith("PEAK Replay Creature", StringComparison.Ordinal)) return true; return false; }
    internal static bool IsTrapName(string value)
    {
        return CreatureDirectoryRules.TrapEffect(value).Length != 0;
    }
    internal static Transform? Trap(Transform source)
    {
        for (var t = source; t; t = t.parent) if (IsTrapName(t.name) && VerifiedTrap(t)) return t;
        return null;
    }
    private static bool VerifiedTrap(Transform root) => signatures.GetValue(root, source =>
    {
        var trigger = source.GetComponent<TriggerEvent>();
        var spawn = source.GetComponent<SpawnGameObject>();
        bool invokesSpawn = false, deactivatesRoot = false;
        if (trigger && trigger.triggerEvent != null)
            for (int i = 0; i < trigger.triggerEvent.GetPersistentEventCount(); i++)
            {
                var target = trigger.triggerEvent.GetPersistentTarget(i);
                string method = trigger.triggerEvent.GetPersistentMethodName(i);
                invokesSpawn |= spawn && target == spawn && method == "Go";
                deactivatesRoot |= target == source.gameObject && method == "SetActive";
            }
        return new Signature { Valid = CreatureDirectoryRules.TrapSignature(source.name,
            spawn && spawn.toSpawn ? spawn.toSpawn.name : "", trigger && trigger.onlyOnce,
            invokesSpawn, deactivatesRoot, source.GetComponentInChildren<MeshRenderer>(true)) };
    }).Valid;
    internal static void Register(GameObject source, string kind)
    {
        if (!source || Ours(source.transform)) return;
        if (kind == "spider" && (!spider || spider!.scene.IsValid() && !source.scene.IsValid())) spider = source;
        else if (kind == "mushroom-zombie" && (!zombie || zombie!.scene.IsValid() && !source.scene.IsValid())) zombie = source;
        else if (kind == "spore-trap" && source.scene.handle == SceneManager.GetActiveScene().handle && VerifiedTrap(source.transform))
        {
            string path = WorldTrack.Path(source.transform);
            if (traps.ContainsKey(path) || traps.Count < CreatureReplayRules.MaximumStaticTraps) traps[path] = source;
        }
    }
    internal static GameObject? Find(string kind, string sourcePath)
    {
        if (kind == "spider") return spider;
        if (kind == "mushroom-zombie") return zombie;
        return kind == "spore-trap" && traps.TryGetValue(sourcePath, out var source) && source ? source : null;
    }
    internal static void Refresh()
    {
        // Scene trap templates are keyed by their hierarchy, so replace the
        // directory when a new scene is ready instead of retaining old wrappers.
        traps.Clear();
        foreach (var value in Resources.FindObjectsOfTypeAll<Spider>()) if (value) Register(value.gameObject, "spider");
        foreach (var value in Resources.FindObjectsOfTypeAll<MushroomZombieSpawner>())
            if (value && value.mushroomZombiePrefab) Register(value.mushroomZombiePrefab.gameObject, "mushroom-zombie");
        foreach (var value in Resources.FindObjectsOfTypeAll<MushroomZombie>()) if (value) Register(value.gameObject, "mushroom-zombie");
        foreach (var value in Resources.FindObjectsOfTypeAll<TriggerEvent>())
        { var trap = value ? Trap(value.transform) : null; if (trap) Register(trap!.gameObject, "spore-trap"); }
    }
}

// Birth observations update a bounded directory. Sampling reads completed native
// animation/physics results; it never calls Scan, Spawn, RPC, Trigger or damage.
internal sealed class CreatureReplayCapture : IDisposable
{
    private sealed class Entry
    {
        public GameObject Source = null!;
        public Spider? Spider;
        public MushroomZombie? Zombie;
        public string Key = "", Kind = "", Path = "";
        public CreatureReplayFrame? Last;
        public double NextSample, RetryAfter;
        public StaticCreatureCapturePolicy StaticSchedule = null!;
        public Vector3[] LineScratch = Array.Empty<Vector3>();
    }
    private readonly CreatureReplayObserver observer;
    private readonly Dictionary<int, Entry> entries = new();
    private readonly List<int> removed = new();
    private readonly List<CreatureReplayFrame> frames = new();
    private readonly HashSet<string> warned = new(StringComparer.Ordinal);
    private readonly Action<string>? warning;
    private readonly int scene = SceneManager.GetActiveScene().handle;
    private CreatureReplayFrame[] previous = Array.Empty<CreatureReplayFrame>();
    private long serial;
    private int dynamicCount, staticCount;
    private bool disposed;
    public CreatureReplayCapture(Action<string>? warning = null)
    {
        this.warning = warning;
        observer = new CreatureReplayObserver(Register, source => { if (source) Remove(source.GetInstanceID()); }, Changed);
        try
        {
            CreatureReplayResources.Refresh();
            foreach (var value in Object.FindObjectsByType<Spider>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Register(value.gameObject);
            foreach (var value in Object.FindObjectsByType<MushroomZombie>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Register(value.gameObject);
            foreach (var value in Object.FindObjectsByType<TriggerEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            { var trap = CreatureReplayResources.Trap(value.transform); if (trap) Register(trap!.gameObject); }
        }
        catch { Dispose(); throw; }
    }
    private void Register(GameObject source)
    {
        if (disposed || ReplaySafety.Active || !source || source.scene.handle != scene || CreatureReplayResources.Ours(source.transform)) return;
        var spider = source.GetComponent<Spider>(); var zombie = source.GetComponent<MushroomZombie>();
        string kind = spider ? "spider" : zombie ? "mushroom-zombie" : CreatureReplayResources.Trap(source.transform) == source.transform ? "spore-trap" : "";
        if (kind.Length == 0 || entries.ContainsKey(source.GetInstanceID())) return;
        bool isStatic = kind == "spore-trap";
        if (isStatic ? staticCount >= CreatureReplayRules.MaximumStaticTraps : dynamicCount >= CreatureReplayRules.MaximumDynamicEntities)
        { Warn(isStatic ? "static-limit" : "dynamic-limit", isStatic ? "静态孢子陷阱超过8192个，超出部分未录制。" : "动态生物超过256个，超出部分未录制。"); return; }
        CreatureReplayResources.Register(source, kind);
        entries[source.GetInstanceID()] = new Entry { Source = source, Spider = spider, Zombie = zombie, Kind = kind,
            Key = "creature:" + source.GetInstanceID() + ":" + ++serial, Path = isStatic ? WorldTrack.Path(source.transform) : "",
            LineScratch = spider ? new Vector3[CreatureReplayRules.MaximumLinePoints] : Array.Empty<Vector3>() };
        entries[source.GetInstanceID()].StaticSchedule = new StaticCreatureCapturePolicy(source.GetInstanceID());
        if (isStatic) staticCount++; else dynamicCount++;
    }
    private void Remove(int id)
    {
        if (!entries.TryGetValue(id, out var entry)) return;
        entries.Remove(id); if (entry.Kind == "spore-trap") staticCount--; else dynamicCount--;
    }
    private void Changed(GameObject source)
    {
        Register(source);
        if (source && entries.TryGetValue(source.GetInstanceID(), out var entry))
        { entry.StaticSchedule.Changed(); entry.RetryAfter = 0; }
    }
    public CreatureReplayFrame[] Capture(double now)
    {
        if (disposed) throw new ObjectDisposedException(nameof(CreatureReplayCapture));
        frames.Clear(); removed.Clear();
        foreach (var pair in entries)
        {
            var entry = pair.Value;
            if (!entry.Source) { removed.Add(pair.Key); continue; }
            if (now < entry.RetryAfter) { if (entry.Last != null) frames.Add(entry.Last); continue; }
            try
            {
                if (entry.Kind == "spore-trap")
                {
                    SampleStatic(entry, now); frames.Add(entry.Last!); continue;
                }
                // Dormant future-biome creatures retain their initial checkpoint.
                // Activity is cheap to observe every tick; activation/deactivation
                // bypasses the 30 Hz animation clock immediately.
                bool active = entry.Source.activeInHierarchy;
                bool activeChanged = entry.Last != null && entry.Last.Pose.Active != active;
                if (!StaticCreatureCapturePolicy.DynamicDue(entry.Last != null, active, entry.Last?.Pose.Active ?? false, now, entry.NextSample))
                { frames.Add(entry.Last!); continue; }
                using var timing = ReplayPerformance.Measure(ReplayStage.DynamicCreatures);
                var pose = VisualReplica.Capture(entry.Source.transform, true, CreatureReplayRules.MaximumNodes);
                var line = entry.Spider && entry.Spider!.line ? SampleLine(entry, entry.Spider.line) : null;
                var visuals = NativeVisualAppearance.CaptureAtTime(entry.Source.transform, entry.Last?.Visuals, now, activeChanged, CreatureReplayRules.MaximumNodes, allowAnimated: active);
                int state = entry.Spider ? (int)entry.Spider!.spiderState : entry.Zombie ? (int)entry.Zombie!.currentState : pose.Active ? 0 : 1;
                var old = entry.Last;
                if (old == null || !ReferenceEquals(old.Pose, pose) || !ReferenceEquals(old.Line, line) || !ReferenceEquals(old.Visuals, visuals) || old.State != state)
                    entry.Last = new CreatureReplayFrame { Key = entry.Key, Kind = entry.Kind, Resource = entry.Kind,
                        SourcePath = entry.Path, State = state, Pose = pose, Line = line, Visuals = visuals };
                entry.NextSample = now + 1d / 30;
                frames.Add(entry.Last!);
            }
            catch (Exception e)
            {
                if (entry.Last != null) frames.Add(entry.Last);
                entry.RetryAfter = now + 1; Warn(entry.Kind, "生物视觉采样暂不可用：" + e.Message);
            }
        }
        foreach (int id in removed) Remove(id);
        bool same = frames.Count == previous.Length;
        if (same) for (int i = 0; i < frames.Count; i++) if (!ReferenceEquals(frames[i], previous[i])) { same = false; break; }
        return same ? previous : previous = frames.ToArray();
    }
    private static void SampleStatic(Entry entry, double now)
    {
        // Native Trigger postfix dirties the already-registered entry after its
        // spawn/deactivation completes. A staggered one-second integrity check
        // covers scene edits that do not emit that event without polling every
        // static transform/active bit on every recording frame.
        if (!entry.StaticSchedule.Due(now, entry.Last != null)) return;
        using var timing = ReplayPerformance.Measure(ReplayStage.StaticTraps);
        bool active = entry.Source.activeSelf;
        var root = entry.Source.transform;
        var p = root.position; var r = root.rotation; var s = root.lossyScale;
        Span<float> trs = stackalloc float[] { p.x, p.y, p.z, r.x, r.y, r.z, r.w, s.x, s.y, s.z };
        var pose = CreatureDirectoryRules.StaticPose(entry.Last?.Pose, active, trs);
        entry.StaticSchedule.Observed(now);
        if (entry.Last != null && ReferenceEquals(pose, entry.Last.Pose)) return;
        entry.Last = new CreatureReplayFrame { Key = entry.Key, Kind = entry.Kind, Resource = entry.Kind,
            SourcePath = entry.Path, State = active ? 0 : 1, Pose = pose };
    }
    private static CreatureReplayLine SampleLine(Entry entry, LineRenderer source)
    {
        int count = source.positionCount;
        if (count > CreatureReplayRules.MaximumLinePoints) throw new InvalidOperationException("蜘蛛丝超过128个控制点");
        source.GetPositions(entry.LineScratch);
        var old = entry.Last?.Line;
        bool same = old != null && old.Enabled == source.enabled && old.WorldSpace == source.useWorldSpace && old.Width == source.widthMultiplier && old.Points.Length == count * 3;
        for (int i = 0; same && i < count; i++)
        { var p = entry.LineScratch[i]; same = old!.Points[i * 3] == p.x && old.Points[i * 3 + 1] == p.y && old.Points[i * 3 + 2] == p.z; }
        if (same) return old!;
        var points = new float[count * 3];
        for (int i = 0; i < count; i++) { var p = entry.LineScratch[i]; points[i * 3] = p.x; points[i * 3 + 1] = p.y; points[i * 3 + 2] = p.z; }
        return new CreatureReplayLine { Enabled = source.enabled, WorldSpace = source.useWorldSpace, Width = source.widthMultiplier, Points = points };
    }
    private void Warn(string key, string message) { if (warned.Count < 128 && warned.Add(key)) warning?.Invoke(message); }
    public void Dispose()
    {
        if (disposed) return; disposed = true; observer.Dispose();
        entries.Clear(); previous = Array.Empty<CreatureReplayFrame>();
    }
}
