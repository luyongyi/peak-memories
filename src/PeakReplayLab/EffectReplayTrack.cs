using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

internal static class NativeEffectResources
{
    internal sealed class Resource
    {
        public string Key = "";
        public GameObject Source = null!;
        public int Kind;
    }
    private static readonly Dictionary<string, Resource> entries = new(StringComparer.Ordinal);
    private static bool preloaded;
    private static readonly List<GameObject> preloadAssets = new();
    internal static bool Ours(Transform t)
    {
        for (var p = t; p; p = p.parent) if (p.name.StartsWith("PEAK Replay Effect", StringComparison.Ordinal)) return true;
        return false;
    }
    private static string Name(string name) => name.Replace("(Clone)", "").Trim();
    private static string Path(Transform child, Transform root)
    {
        var pieces = new Stack<string>();
        while (child != root && child.parent)
        { pieces.Push(child.GetSiblingIndex() + ":" + Uri.EscapeDataString(Name(child.name))); child = child.parent; }
        return string.Join("/", pieces);
    }
    internal static bool TopParticle(ParticleSystem p)
    {
        for (Transform t = p.transform.parent; t; t = t.parent) if (t.GetComponent<ParticleSystem>()) return false;
        return true;
    }
    internal static bool Relevant(ParticleSystem p)
    {
        if (Ours(p.transform) || !TopParticle(p)) return false;
        if (EffectNativeBirths.Known(p)) return true;
        for (var t = p.transform; t; t = t.parent)
        {
            if (CreatureDirectoryRules.IsSporeEffect(t.name)) return true;
            if (CreatureReplayResources.Kind(t.gameObject).Length != 0) return true;
            if (t.GetComponent<Item>() || t.GetComponent<Character>() || t.GetComponent<Spider>() || t.GetComponent<ScoutCannon>() || t.GetComponent<ExplosionEffect>() || t.GetComponent<Peak.AntiSphere>() ||
                t.GetComponent<BeeSwarm>() || t.GetComponent<ArrowShooter>() || t.GetComponent<Campfire>() || t.GetComponent<Peak.ScoutStatue>() || t.GetComponent<GhostFire>() ||
                t.GetComponent<Peak.ScoutmasterSoulPillar>() || t.GetComponent<Peak.GhostBall>() || t.GetComponent<Luggage>()) return true;
            string name = t.name.ToLowerInvariant();
            if (name.Contains("explos") || name.Contains("smoke") || name.Contains("fire") || name.Contains("rocket") ||
                name.Contains("jetpack") || name.Contains("spark") || name.Contains("cannon") || name.Contains("bomb") ||
                name.Contains("flare") || name.Contains("dynamite") || name.Contains("puffshroom") || name.Contains("spore") || name.Contains("fungus")) return true;
        }
        return false;
    }
    internal static string Particle(ParticleSystem source)
    {
        Transform root = source.transform.root;
        string prefix = "scene/" + Name(root.name);
        var item = source.GetComponentInParent<Item>(true);
        var character = source.GetComponentInParent<Character>(true);
        var cannon = source.GetComponentInParent<ScoutCannon>(true);
        if (item) { root = item.transform; prefix = "item/" + item.itemID; }
        else if (character) { root = character.transform; prefix = "character"; }
        else if (cannon) { root = cannon.transform; prefix = "cannon/" + Name(cannon.name); }
        string key = "particle/" + prefix + "/" + Path(source.transform, root);
        Add(key, source.gameObject, 0); return key;
    }
    internal static string Mesh(GameObject source)
    {
        string key = MeshKey(source);
        if (key.Length != 0) Add(key, source, 1); return key;
    }
    internal static string Sphere(GameObject source)
    {
        string key = "antigravity/" + Name(source.name);
        Add(key, source, 2); return key;
    }
    internal static string MeshKey(GameObject source)
    {
        var animator = source.GetComponentInChildren<Animator>(true);
        if (!animator || !animator.runtimeAnimatorController) return "";
        return "mesh/" + Name(source.name) + "/" + Name(animator.runtimeAnimatorController.name);
    }
    private static void Add(string key, GameObject source, int kind)
    {
        if (key.Length > 2048 || entries.Count >= 4096 && !entries.ContainsKey(key) || Ours(source.transform)) return;
        if (!entries.TryGetValue(key, out var old) || !old.Source ||
            old.Source.scene.IsValid() && !source.scene.IsValid())
            entries[key] = new Resource { Key = key, Source = source, Kind = kind };
    }
    internal static ParticleSystem[] Refresh()
    {
        if (!preloaded)
        {
            // Verified against this installation's globalgamemanagers ResourceManager
            // container (2026-09-25). Fixed local whitelist; never a path from a log.
            // Load dependencies only: no Instantiate / Awake / gameplay/RPC calls.
            foreach (string path in new[] { "0_items/antizooka", "0_items/dynamite", "0_items/jetpack", "0_items/rocketpack",
                "0_items/scoutcannonitem", "scoutcannon_placed", "antisphere_projectile",
                "antisphere_cookingexplosion variant", "antisphere_cookingexplosion_gem", "skeletonexplosion", "0_items/healingpuffshroomspawn",
                Action_RandomMushroomEffect.RESOURCE_PATH_EXPLOSION, Action_RandomMushroomEffect.RESOURCE_PATH_EXPLOSION_NO_KNOCKBACK })
            { var asset = Resources.Load<GameObject>(path); if (asset) preloadAssets.Add(asset); }
            preloaded = true;
        }
        foreach (string key in entries.Where(pair => !pair.Value.Source).Select(pair => pair.Key).ToArray()) entries.Remove(key);
        EffectNativeBirths.Refresh();
        var all = Resources.FindObjectsOfTypeAll<ParticleSystem>();
        foreach (var p in all) if (p && Relevant(p)) Particle(p);
        foreach (var explosion in Resources.FindObjectsOfTypeAll<ExplosionEffect>())
            if (explosion && explosion.explosionOrb && !Ours(explosion.transform)) Mesh(explosion.explosionOrb);
        foreach (var sphere in Resources.FindObjectsOfTypeAll<Peak.AntiSphere>())
            if (sphere && !Ours(sphere.transform)) Sphere(sphere.gameObject);
        // The antizooka's separate spawn action owns the canonical Photon prefab.
        // Reading that reference does not spawn it or run either action.
        foreach (var action in Resources.FindObjectsOfTypeAll<Peak.Action_RaycastSpawnSomething>())
            if (action && action.prefabToSpawn && action.prefabToSpawn.GetComponent<Peak.AntiSphere>()) Sphere(action.prefabToSpawn);
        // The audited spore mines reference their non-Photon VFX prefab through
        // SpawnGameObject.toSpawn. Keep that native asset available after the
        // observed explosion instance has expired, including on a later seek.
        foreach (var spawn in Resources.FindObjectsOfTypeAll<SpawnGameObject>())
            if (spawn && spawn.toSpawn)
                foreach (var particle in spawn.toSpawn.GetComponentsInChildren<ParticleSystem>(true))
                    if (Relevant(particle)) Particle(particle);
        return all;
    }
    internal static Resource? Find(string key) => entries.TryGetValue(key, out var value) && value.Source ? value : null;
}

internal sealed class EffectReplayCapture : IDisposable
{
    private sealed class Entry
    {
        public GameObject Root = null!;
        public ParticleSystem? Particle;
        public ParticleSystem[] Systems = Array.Empty<ParticleSystem>();
        public EffectParticleCapture? Parameters;
        public Animator? Animator;
        public MeshRenderer? Mesh;
        public readonly MaterialPropertyBlock Block = new();
        public string Resource = "", Key = "", SourceKey = "";
        public EffectReplayFrame? Last;
        public float LastPhase;
        public int Kind;
        public int ScopeId;
        public bool UrgentInitialRead;
        public bool ParameterEventPending;
        public double NextRetry;
        public double LastSampleAt = double.NegativeInfinity;
        public long WorldTransformReads;
        public EffectReplayFrame? LastSampleValue;
        public readonly EffectCaptureEventState Events = new();
        public readonly List<AnimatorClipInfo> Clips = new(4);
    }
    private static EffectReplayCapture? current;
    private readonly Harmony harmony = new("cn.mylus.peakreplaylab.effects." + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<int, Entry> entries = new();
    private readonly Dictionary<int, List<int>> scopeEntries = new();
    private readonly SegmentCaptureClient? segments;
    private readonly List<int> removed = new();
    private readonly List<EffectReplayFrame> frames = new();
    private readonly HashSet<int> pendingEvents = new();
    private readonly HashSet<int> startupRuns = new();
    private readonly Dictionary<string, Entry> activeRuns = new(StringComparer.Ordinal);
    private readonly List<int> eventWork = new();
    private readonly EffectCaptureEventBuffer<EffectReplayFrame> eventFrames = new(EffectReplayRules.Maximum);
    private static readonly Func<EffectReplayFrame, string> eventKey = frame => frame.Key;
    private EffectReplayFrame[] previous = Array.Empty<EffectReplayFrame>();
    private readonly Action<string>? warning;
    private readonly HashSet<string> warned = new(StringComparer.Ordinal);
    private bool disposed;
    private long runSerial;
    private readonly long[] retiredReads = new long[8];
    internal long BaselineModuleReads => ReadCount(0);
    internal long EventModuleReads => ReadCount(1);
    internal long AnimatedModuleReads => ReadCount(2);
    internal long DistanceModuleReads => ReadCount(3);
    internal long EmitterTransformReads => ReadCount(4);
    internal long NativeParticleStateReads => 0;
    internal long CullerProbes => ReadCount(5);
    internal long NativeAnimationProbes => ReadCount(6);
    internal long UnknownAnimationModuleReads => ReadCount(7);
    internal object Diagnostics => new
    {
        BaselineModuleReads, EventModuleReads, AnimatedModuleReads, DistanceModuleReads,
        UnknownAnimationModuleReads, EmitterTransformReads, CullerProbes, NativeAnimationProbes, NativeParticleStateReads,
        Measurement = "Cumulative observations across active and retired emitters since capture started. Module counters count native system baselines; transforms count cached emitter/space poses; culler attenuation probes run at 4 Hz. Default recording reads no particles or native playback state. Counts are work, not CPU milliseconds."
    };
    private long ReadCount(int field)
    {
        long total = retiredReads[field];
        foreach (var entry in entries.Values)
            total += field == 4 ? entry.WorldTransformReads + (entry.Parameters?.TransformReads ?? 0) : ModuleCount(entry.Parameters,field);
        return total;
    }
    private static long ModuleCount(EffectParticleCapture? value, int field) => value == null ? 0 : field switch
    {
        0 => value.BaselineModuleReads, 1 => value.EventModuleReads, 2 => value.AnimatedModuleReads, 3 => value.DistanceModuleReads,
        5 => value.CullerProbes, 6 => value.NativeAnimationProbes, 7 => value.UnknownAnimationModuleReads, _ => 0
    };
    private void RetainCounters(Entry entry)
    {
        for (int counter = 0; counter < retiredReads.Length; counter++)
            retiredReads[counter] += counter == 4 ? entry.WorldTransformReads + (entry.Parameters?.TransformReads ?? 0) : ModuleCount(entry.Parameters, counter);
    }
    internal bool InitialBaselineReady
    {
        get
        {
            foreach (var entry in entries.Values)
                if (entry.Root && entry.Root.activeInHierarchy && double.IsNegativeInfinity(entry.LastSampleAt)) return false;
            return true;
        }
    }
    public EffectReplayCapture(Action<string>? warning = null)
    {
        this.warning = warning; current = this;
        segments = CaptureSegmentScope.Active?.CreateClient(go => Register(go), Retire, go => go && go.activeInHierarchy);
        Observe(typeof(Peak.RocketFX), "RocketLight"); Observe(typeof(Peak.RocketFX), "RocketFire"); Observe(typeof(Peak.RocketFX), "RocketExtinguish");
        Observe(typeof(Peak.Jetpack), "ActivateJetpack"); Observe(typeof(Peak.Jetpack), "ForceStopJetpack");
        Observe(typeof(JetpackItem), "RPCAddFuel"); Observe(typeof(ScoutCannon), "RPCA_Light");
        Observe(typeof(Dynamite), "SetFlareLitRPC");
        Observe(typeof(Peak.Action_Antizooka), "RPCA_Shoot");
        Observe(typeof(Item), "Awake"); Observe(typeof(Character), "Awake");
        Observe(typeof(ScoutCannon), "Awake");
        Observe(typeof(Spider), "Awake"); Observe(typeof(Spider), "BonkRPC");
        Observe(typeof(MushroomZombie), "Awake");
        Observe(typeof(BeeSwarm), "Awake"); Observe(typeof(BeeSwarm), "Start"); Observe(typeof(BeeSwarm), "DisperseRPC");
        Observe(typeof(ArrowShooter), "Awake"); Observe(typeof(ArrowShooter), "FireArrowVisuals_RPC");
        Observe(typeof(Luggage), "Awake"); Observe(typeof(Luggage), "OpenLuggageRPC");
        Observe(typeof(Campfire), "UpdateLit");
        Observe(typeof(Peak.ScoutStatue), "Awake"); Observe(typeof(GhostFire), "Start"); Observe(typeof(GhostFire), "SetLit");
        Observe(typeof(Peak.ScoutmasterSoulPillar), "RPC_Break"); Observe(typeof(Peak.GhostBall), "Awake");
        ObserveReference(typeof(SwingingAxe), "Start", nameof(AxeBorn));
        ObserveReference(typeof(OrbThatMakesYouSleepy), "Start", nameof(SleepyOrbBorn));
        ObserveReference(typeof(ParticleManager), "Register", nameof(CullerRegistered));
        foreach (var method in typeof(WaterZone).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            if (method.Name is "Awake" or "PlaySplash")
                try { harmony.Patch(method, postfix: new HarmonyMethod(typeof(EffectReplayCapture), nameof(WaterChanged))); }
                catch (Exception e) { Warn("water-hook:" + method, "水花事件入口不可用：" + e.Message); }
        Watch(typeof(Flare), "EnableFlareVisuals"); Watch(typeof(ItemCooking), "CookingExplodeImpl");
        Watch(typeof(CharacterBalloons), "RemoveBalloon"); Watch(typeof(Action_RaycastDart), "RPC_DartImpact");
        Watch(typeof(Peak.Action_CloneSelectedItem), "RPCA_Shoot"); Watch(typeof(Action_Spawn), "RPCSpawn");
        Watch(typeof(ArrowShooter), "FireArrowVisuals_RPC"); Watch(typeof(LandImpactAudio), "Update");
        Watch(typeof(EruptionSpawner), "RPCA_SpawnEruption"); Watch(typeof(TrackableNetworkObject), "SetInstanceIDRPC");
        Watch(typeof(Peak.GhostBall), "Explode"); Watch(typeof(Character), "RPCA_Die");
        Watch(typeof(CharacterMovement), "RocketExplodeRPC");
        Watch(typeof(Peak.Afflictions.Affliction_MassSuperJump), "OnApplied");
        // The placed projectile owns its mesh in SpawnedReplayCapture; observe
        // its existing birth here once for independent particle emitters only.
        Observe(typeof(Peak.GrowOverTime), "Awake");
        try { harmony.Patch(AccessTools.DeclaredMethod(typeof(Peak.AntiSphere), "FixedUpdate"),
            postfix: new HarmonyMethod(typeof(EffectReplayCapture), nameof(SphereSeen))); }
        catch (Exception e) { Warn("sphere-hook", "悬浮球出生观察不可用：" + e.Message); }
        foreach (Type type in new[] { typeof(Dynamite), typeof(Rocketpack), typeof(ExplosionEffect), typeof(SpawnGameObject), typeof(GameUtils) })
            foreach (Type nested in Family(type))
                foreach (var method in nested.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (method.GetMethodBody() != null && (method.Name == "MoveNext" || method.Name == "RPC_Explode" || method.Name == "EnableFlareVisuals" ||
                        type == typeof(SpawnGameObject) && method.Name == "Go" || type == typeof(GameUtils) && method.Name == "RPC_SpawnResourceAtPosition"))
                        try { harmony.Patch(method, transpiler: new HarmonyMethod(typeof(EffectReplayCapture), nameof(WatchSpawn))); }
                        catch (Exception e) { Warn("hook:" + method.Name, "特效出生观察未安装：" + type.Name + "." + method.Name + " · " + e.Message); }
        // One scene-entry catalogue. Native births/actions maintain it afterward;
        // never enumerate every loaded particle hierarchy once a second.
        try { Scan(); }
        catch { Dispose(); throw; }
    }
    private static IEnumerable<Type> Family(Type type)
    { yield return type; foreach (Type child in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) foreach (Type descendant in Family(child)) yield return descendant; }
    private void Observe(Type type, string name)
    {
        try { var method = AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.Name, name);
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(EffectReplayCapture),
                name is "Awake" or "Start" or "OnEnable" ? nameof(Discovered) : nameof(Changed))); }
        catch (Exception e) { Warn("hook:" + type.Name + name, "特效事件入口不可用：" + type.Name + "." + name + " · " + e.Message); }
    }
    private static void Changed(Component __instance, MethodBase __originalMethod)
    {
        try
        {
            if (ReplaySafety.Active || current == null || !__instance) return;
            bool parameterEvent = __instance is BeeSwarm && __originalMethod.Name == "DisperseRPC" ||
                __instance is Campfire && __originalMethod.Name == "UpdateLit" ||
                __instance is GhostFire && __originalMethod.Name == "SetLit" ||
                __instance is Peak.ScoutmasterSoulPillar && __originalMethod.Name == "RPC_Break";
            current.Register(__instance.gameObject, urgent: true, parameterEvent: parameterEvent);
            // The audited native coroutine changes emission and gravity for
            // one second. A new event starts this lease; sampling cannot renew it.
            if (__instance is BeeSwarm bees && __originalMethod.Name == "DisperseRPC" && bees.beeParticles &&
                current.entries.TryGetValue(bees.beeParticles.GetInstanceID(), out var entry))
                entry.Parameters?.Invalidate(Time.timeAsDouble, 1.1);
        }
        catch { }
    }
    private static void Discovered(Component __instance)
    { try { if (!ReplaySafety.Active && current != null && __instance) current.Register(__instance.gameObject); } catch { } }
    private static void WaterChanged(WaterZone __instance, MethodBase __originalMethod)
    {
        try
        {
            if (ReplaySafety.Active || current == null || !__instance || !__instance.waterVFX) return;
            // A zone may contain a large level hierarchy. Register only its
            // native splash reference, once at birth and on its two Play calls.
            var particle = __instance.waterVFX;
            EffectNativeBirths.MarkReference(particle);
            bool urgent = __originalMethod.Name != "Awake";
            if (current.Admit(particle.gameObject, urgent))
                current.Register(particle.gameObject, EffectNativeBirths.Bind(particle, particle.gameObject), urgent);
        }
        catch (Exception e) { current?.Warn("water-capture", "原生水花出生记录失败：" + e.Message); }
    }
    private void ObserveReference(Type type, string method, string observer)
    {
        try { harmony.Patch(AccessTools.DeclaredMethod(type, method) ?? throw new MissingMethodException(type.Name, method),
            postfix: new HarmonyMethod(typeof(EffectReplayCapture), observer)); }
        catch (Exception e) { Warn("reference:" + type.Name + method, "原生粒子引用入口不可用：" + type.Name + "." + method + " · " + e.Message); }
    }
    private static void ReferenceBorn(ParticleSystem? particle)
    {
        if (ReplaySafety.Active || current == null || !particle) return;
        try
        {
            EffectNativeBirths.MarkReference(particle!);
            if (current.Admit(particle!.gameObject, false)) current.Register(particle.gameObject, EffectNativeBirths.Bind(particle, particle.gameObject));
        }
        catch (Exception e) { current.Warn("reference-capture", "原生粒子出生记录失败：" + e.Message); }
    }
    private static void AxeBorn(SwingingAxe __instance)
    { if (__instance && !ReplaySafety.Active && current != null) ReferenceBorn(EffectNativeBirths.AxeImpact(__instance)); }
    private static void SleepyOrbBorn(OrbThatMakesYouSleepy __instance)
    { if (__instance) { ReferenceBorn(__instance.particle); ReferenceBorn(__instance.ambientParticles); } }
    private static void CullerRegistered(ParticleCuller particle)
    {
        // The game's existing registration runs once per enabled lifecycle.
        // Capture each explicit reference; do not patch Update/Scan or traverse
        // the culler's potentially large scene hierarchy on every scan.
        if (!particle || particle.systems == null || ReplaySafety.Active || current == null) return;
        foreach (var system in particle.systems) ReferenceBorn(system);
    }
    private static void SphereSeen(Peak.AntiSphere __instance)
    {
        try { if (!ReplaySafety.Active && current != null && __instance &&
                current.Admit(__instance.gameObject, true) &&
                NativePlacementResources.Match(__instance.gameObject)?.Kind != "anti-sphere" &&
                !current.entries.ContainsKey(__instance.gameObject.GetInstanceID()))
                current.Register(__instance.gameObject, urgent: true); } catch { }
    }
    private static IEnumerable<CodeInstruction> WatchSpawn(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo method &&
                EffectNativeBirths.Wrapper(method) is MethodInfo wrapper)
            { instruction.opcode = OpCodes.Call; instruction.operand = wrapper; }
            yield return instruction;
        }
    }
    private void Watch(Type type, string name)
    {
        try { var method = AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.Name, name);
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(EffectReplayCapture), nameof(WatchSpawn))); }
        catch (Exception e) { Warn("birth:" + type.Name + name, "特效出生观察不可用：" + type.Name + "." + name + " · " + e.Message); }
    }
    private static void Spawned(Object value)
    {
        // Observe the result of the game's EXISTING Instantiate call. This method
        // neither creates a gameplay object nor invokes any game/RPC method.
        try { if (!ReplaySafety.Active && current != null) current.Register(value is GameObject go ? go : (value as Component)?.gameObject, urgent: true); } catch { }
    }
    internal static void ObserveNativeSpawn(GameObject value) => Spawned(value);
    internal static void ObserveNativeSpawn(Object original, Object result)
    {
        try
        {
            if (ReplaySafety.Active || current == null) return;
            var go = EffectNativeBirths.GameObject(result); if (!go) return;
            if (current.Admit(go!, true)) current.Register(go, EffectNativeBirths.Bind(original, go!), true);
        }
        catch (Exception e) { current?.Warn("native-birth", "原生特效出生记录失败：" + e.Message); }
    }
    private bool Admit(GameObject go, bool urgent) => segments?.Admit(go, urgent) ?? true;
    private void AddEntry(int id, Entry entry, bool urgent = false)
    {
        if (entries.TryGetValue(id, out var old)) { RetainCounters(old); activeRuns.Remove(old.Key); }
        entry.ScopeId = entry.Root.GetInstanceID();
        entry.UrgentInitialRead = urgent;
        entries[id] = entry;
        if (!scopeEntries.TryGetValue(entry.ScopeId, out var ids)) scopeEntries.Add(entry.ScopeId, ids = new List<int>(1));
        if (!ids.Contains(id)) ids.Add(id);
    }
    private void Retire(int scopeId)
    {
        if (!scopeEntries.TryGetValue(scopeId, out var ids)) return;
        double now = Time.timeAsDouble;
        foreach (int id in ids)
        {
            if (!entries.TryGetValue(id, out var entry)) continue;
            // End retained evidence without dropping a birth/stop that has not
            // reached its next scene sample. It contains no Unity bindings.
            if (segments?.OriginRejection == true) eventFrames.DiscardSource(entry.SourceKey);
            else eventFrames.End(entry.Key, now);
            activeRuns.Remove(entry.Key); pendingEvents.Remove(id); startupRuns.Remove(id);
            RetainCounters(entry);
            if (entry.Root) VisualReplica.ReleaseCapture(entry.Root.transform);
            entry.Parameters = null; entry.Systems = Array.Empty<ParticleSystem>();
            entry.Particle = null; entry.Animator = null; entry.Mesh = null;
            entry.Last = entry.LastSampleValue = null; entry.Clips.Clear();
            entries.Remove(id);
        }
        scopeEntries.Remove(scopeId);
    }
    private void Register(GameObject? go, Dictionary<int, string>? nativeResources = null, bool urgent = false, bool parameterEvent = false)
    {
        if (disposed || !go || !go!.scene.IsValid() || !Admit(go, urgent) || NativeEffectResources.Ours(go.transform)) return;
        // Schema 12's placed antizooka projectile records the complete passive
        // native model and appearance. Keep legacy sphere resources/playback,
        // but never record a second copy of that same mesh in the effect track.
        if (go.TryGetComponent<Peak.AntiSphere>(out _) && NativePlacementResources.Match(go)?.Kind != "anti-sphere" &&
            !entries.ContainsKey(go.GetInstanceID()) && entries.Count < 2048)
        {
            AddEntry(go.GetInstanceID(), new Entry { Root = go, Kind = 2, Resource = NativeEffectResources.Sphere(go), Key = "sphere:" + go.GetInstanceID(), SourceKey = "sphere:" + go.GetInstanceID() }, urgent);
            pendingEvents.Add(go.GetInstanceID());
        }
        if (go.TryGetComponent<ExplosionEffect>(out var explosion) && explosion.explosionOrb) NativeEffectResources.Mesh(explosion.explosionOrb);
        foreach (var p in go.GetComponentsInChildren<ParticleSystem>(true))
        {
            if (!Admit(p.gameObject, urgent)) continue;
            string? nativeResource = null; nativeResources?.TryGetValue(p.GetInstanceID(), out nativeResource);
            if (!NativeEffectResources.TopParticle(p) || nativeResource == null && !NativeEffectResources.Relevant(p)) continue;
            if (entries.TryGetValue(p.GetInstanceID(), out var known))
            {
                // Discovery and repeated Play/Stop callbacks only enqueue a
                // signature check. Native module reads follow the deduped edge.
                known.ParameterEventPending |= parameterEvent;
                known.UrgentInitialRead |= urgent;
                if (nativeResource != null && known.Resource != nativeResource)
                { known.Resource = nativeResource; known.Last = null; known.Events.Reset(); }
                pendingEvents.Add(p.GetInstanceID()); continue;
            }
            var systems = p.GetComponentsInChildren<ParticleSystem>(true);
            if (systems.Length > 64) { Warn("systems", "特效子粒子系统超过64个，未录制该组。"); continue; }
            if (entries.Count >= 2048) { Warn("directory", "特效登记达到2048个上限。"); return; }
            string resource = nativeResource ?? NativeEffectResources.Particle(p);
            if (resource.Length > 2048) { Warn("resource-path", "特效资源路径超过上限，该特效未录制。"); continue; }
            AddEntry(p.GetInstanceID(), new Entry { Root = p.gameObject, Particle = p, Systems = systems,
                Parameters = new EffectParticleCapture(systems), Resource = resource, Key = "particle:" + p.GetInstanceID(), SourceKey = "particle:" + p.GetInstanceID() }, urgent);
            pendingEvents.Add(p.GetInstanceID());
        }
        var animator = go.GetComponentInChildren<Animator>(true);
        if (animator && go.GetComponentInChildren<MeshRenderer>(true) && !go.GetComponent<Item>() && !go.GetComponent<Character>())
        {
            string resource = NativeEffectResources.MeshKey(go);
            // Only explicitly catalogued explosion-orb prefabs; a RocketFX hook
            // must not accidentally record a second copy of the whole rocket.
            if (resource.Length != 0 && NativeEffectResources.Find(resource) != null && entries.Count < 2048)
            {
                // Repeated action notifications must not replace the entry and
                // manufacture a first run on every admitted recorder tick.
                if (!entries.TryGetValue(go.GetInstanceID(), out var known) || known.Root != go || known.Resource != resource || known.Kind != 1)
                    AddEntry(go.GetInstanceID(), new Entry { Root = go, Kind = 1, Animator = animator, Mesh = go.GetComponentInChildren<MeshRenderer>(true),
                        Resource = resource, Key = "mesh:" + go.GetInstanceID(), SourceKey = "mesh:" + go.GetInstanceID() }, urgent);
                pendingEvents.Add(go.GetInstanceID());
            }
        }
    }

    // Call once per admitted GLOBAL recording tick, before the optional 30 Hz
    // scene read. Native hooks only enqueue IDs, so Instantiate callers have
    // finished assigning transforms/modules before this LateUpdate observation.
    public void ObservePendingEvents(double now)
    {
        if (disposed || ReplaySafety.Active || pendingEvents.Count == 0 && startupRuns.Count == 0) return;
        eventWork.Clear(); foreach (int id in pendingEvents) eventWork.Add(id);
        foreach (int id in startupRuns) if (!pendingEvents.Contains(id)) eventWork.Add(id);
        pendingEvents.Clear();
        foreach (int id in eventWork)
        {
            if (!entries.TryGetValue(id, out var entry)) continue;
            if (double.IsNegativeInfinity(entry.LastSampleAt) && !entry.UrgentInitialRead && !CaptureBootstrapWorkBudget.TryBeginRead())
            { pendingEvents.Add(id); continue; }
            try
            {
                var signature = EventSignature(entry);
                bool boundary = entry.Events.Observe(signature);
                bool parameterEvent = entry.ParameterEventPending; entry.ParameterEventPending = false;
                if (!signature.Alive) { eventFrames.End(entry.Key, now); startupRuns.Remove(id); continue; }
                if (!boundary && !parameterEvent) continue;
                entry.NextRetry = 0; // A real new run may retry; repeated notifications cannot.
                entry.Parameters?.Invalidate();
                // One event read, never a new observation lease. Preserve the
                // completed snapshot independently of this Unity instance.
                if (entry.Events.StartsRun)
                { eventFrames.End(entry.Key, now); StartRun(entry, signature); startupRuns.Add(id); }
                var frame = Sample(entry, now, signature, true);
                if (frame != null && !eventFrames.Save(frame.Key, frame, replace: !entry.Events.StartsRun, observedAt: now,
                    isInitialRun: entry.Events.StartsRun))
                    Warn("pending-events", "原生短特效事件超过512组；超额事件未保留。");
            }
            catch (Exception error)
            { entry.NextRetry = now + 1; Warn(entry.Resource, "原生特效事件初态暂不可用：" + error.Message); }
        }
        eventWork.Clear();
    }
    private static EffectCaptureRunSignature EventSignature(Entry entry)
    {
        if (!entry.Root || !entry.Root.activeInHierarchy) return default;
        if (entry.Particle)
        {
            var particle = entry.Particle!;
            bool alive = particle.IsAlive(true) || particle.isPlaying;
            if (!alive) return default;
            var main = particle.main; long seeds = 1469598103934665603L;
            foreach (var system in entry.Systems) seeds = unchecked((seeds ^ (system ? system.randomSeed : 0)) * 1099511628211L);
            bool paused = particle.isPaused;
            return new EffectCaptureRunSignature(true, particle.isEmitting || paused && entry.Last?.Emitting == true,
                paused, main.loop, seeds, particle.time, main.simulationSpeed, 0);
        }
        if (entry.Kind == 2) return new EffectCaptureRunSignature(true, true, true, false, 0, 0, 0, 0);
        var animator = entry.Animator;
        if (!animator || !animator.runtimeAnimatorController) return default;
        var state = animator.GetCurrentAnimatorStateInfo(0);
        float rate = animator.speed * state.speed * state.speedMultiplier;
        return new EffectCaptureRunSignature(true, true, !animator.enabled || rate == 0,
            state.loop, 0, state.normalizedTime, rate, state.fullPathHash);
    }
    private void Scan()
    {
        foreach (var p in NativeEffectResources.Refresh())
            if (p && p.gameObject.scene.IsValid() && p.gameObject.scene.isLoaded) Register(p.gameObject);
        foreach (var sphere in Resources.FindObjectsOfTypeAll<Peak.AntiSphere>())
            if (sphere && sphere.gameObject.scene.IsValid() && sphere.gameObject.scene.isLoaded) Register(sphere.gameObject);
    }
    public EffectReplayFrame[] Capture(double now)
    {
        if (disposed) throw new ObjectDisposedException(nameof(EffectReplayCapture));
        frames.Clear(); removed.Clear();
        foreach (var pair in entries)
        {
            var e = pair.Value;
            if (!e.Root) { eventFrames.End(e.Key, now); activeRuns.Remove(e.Key); removed.Add(pair.Key); continue; }
            if (!e.Root.activeInHierarchy) { eventFrames.End(e.Key, now); e.Last = null; e.Events.Observe(default); continue; }
            if (now < e.NextRetry) continue;
            try
            {
                EffectReplayFrame? sample = e.LastSampleAt == now ? e.LastSampleValue : Sample(e, now);
                if (sample != null)
                {
                    if (frames.Count >= EffectReplayRules.Maximum) { Warn("limit", "活跃特效超过512组；超出部分未录制。"); break; }
                    frames.Add(sample);
                }
                else eventFrames.End(e.Key, now);
            }
            catch (Exception error) { e.NextRetry = now + 1; Warn(e.Resource, "原生特效采样不完整：" + error.Message); }
        }
        foreach (int id in removed)
            if (entries.TryGetValue(id, out var entry)) { segments?.Forget(entry.ScopeId); Retire(entry.ScopeId); }
        if (eventFrames.AppendMissing(frames, eventKey, CompleteFirstSceneFrame, now) != 0)
            Warn("pending-delivery", "同时特效超过512组；超额短特效事件未交付。");
        startupRuns.Clear();
        bool same = frames.Count == previous.Length;
        if (same) for (int i = 0; i < frames.Count; i++) if (!ReferenceEquals(frames[i], previous[i])) { same = false; break; }
        return same ? previous : previous = frames.ToArray();
    }
    internal void InvalidateLiveBaselines()
    {
        foreach (var entry in entries.Values)
            if (entry.Parameters?.HasCheckpoints == true)
            { entry.Parameters.Invalidate(); entry.LastSampleAt = double.NegativeInfinity; }
    }
    private EffectReplayFrame CompleteFirstSceneFrame(EffectCapturePending<EffectReplayFrame> pendingFrame,
        EffectReplayFrame current, double observedAt)
    {
        var first = pendingFrame.First;
        if (!pendingFrame.IsInitialRun) return current;
        bool ended = !double.IsNaN(pendingFrame.EndedAt);
        bool changed = EffectSceneEventPlayback.Changed(first, current);
        if (!ended && !changed) return current;
        // Preserve the complete actually observed boundary, rather than using
        // a later scene's transform/modules retroactively at that boundary.
        if (!double.IsNaN(pendingFrame.BoundaryAt)) current = pendingFrame.Last;
        double until = !double.IsNaN(pendingFrame.BoundaryAt) ? pendingFrame.LastAt : ended ? pendingFrame.EndedAt : observedAt;
        if (double.IsNaN(pendingFrame.ObservedAt) || double.IsNaN(until) || until < pendingFrame.ObservedAt) return current;
        var evidence = EffectSceneEventPlayback.Evidence(first, pendingFrame.ObservedAt, until, ended);
        if (ended) evidence.EndTime = pendingFrame.EndedAt;
        var complete = EffectSceneEventPlayback.WithInitial(current, evidence);
        if (activeRuns.TryGetValue(current.Key, out var entry))
        { entry.Last = complete; entry.LastSampleValue = complete; }
        return complete;
    }
    private void StartRun(Entry entry, EffectCaptureRunSignature signature)
    {
        activeRuns.Remove(entry.Key);
        entry.Key = EffectCaptureRunKey.Create(entry.SourceKey, ++runSerial, signature.Rate);
        activeRuns[entry.Key] = entry;
        entry.Last = null; entry.LastPhase = 0;
        entry.Parameters?.Invalidate();
        entry.LastSampleAt = double.NegativeInfinity; entry.LastSampleValue = null;
    }
    private EffectReplayFrame? Sample(Entry e, double now, EffectCaptureRunSignature? signature = null, bool observed = false)
    {
        if (CaptureBootstrapWorkBudget.InitialOnly && !double.IsNegativeInfinity(e.LastSampleAt)) return e.LastSampleValue;
        if (double.IsNegativeInfinity(e.LastSampleAt) && !e.UrgentInitialRead && !CaptureBootstrapWorkBudget.TryBeginRead()) return null;
        if (!observed)
        {
            var run = signature ?? EventSignature(e);
            e.Events.Observe(run);
            if (e.Events.StartsRun) StartRun(e, run);
        }
        var frame = ReadSample(e, now, followSceneClock: !observed);
        e.LastSampleAt = now; e.LastSampleValue = frame; e.UrgentInitialRead = false; return frame;
    }
    private static EffectReplayFrame? ReadSample(Entry e, double now, bool followSceneClock)
    {
        var old = e.Last; double phase; float rate; bool loop, emitting, paused; long seed = 0;
        string clip = ""; float shaderRandom = 0; long[] seeds = old?.Seeds ?? Array.Empty<long>();
        EffectParticleFrame[]? parameters = old?.Particles; EffectForceFieldFrame? forceField = old?.ForceField;
        if (e.Particle)
        {
            var p = e.Particle!; if (!p.IsAlive(true) && !p.isPlaying) { e.Last = null; return null; }
            var main = p.main; phase = p.time; rate = main.simulationSpeed; loop = main.loop; paused = p.isPaused;
            emitting = p.isEmitting || paused && old?.Emitting == true; seed = p.randomSeed;
            // ParticleSystem.time may wrap a looping duration; keep the recorded
            // clock unwrapped while the same seeded emission run is active.
            if (old != null && (loop || !emitting) && old.Seed == seed && !(emitting && !old.Emitting)) phase = EffectReplayRules.PhaseAt(old, now);
            bool same = seeds.Length == e.Systems.Length;
            if (same) for (int i = 0; i < seeds.Length; i++) if (seeds[i] != (e.Systems[i] ? e.Systems[i].randomSeed : 0)) { same = false; break; }
            if (!same)
            { seeds = new long[e.Systems.Length]; for (int i = 0; i < seeds.Length; i++) seeds[i] = e.Systems[i] ? e.Systems[i].randomSeed : 0; }
            e.Parameters!.Sample(now, old?.Particles, old?.ForceField, out parameters, out forceField, followSceneClock, rootPhase: phase);
        }
        else if (e.Kind == 2)
        { phase = 0; rate = 0; loop = false; paused = true; emitting = true; }
        else
        {
            var animator = e.Animator; if (!animator || !animator.runtimeAnimatorController) return null;
            e.Clips.Clear(); animator.GetCurrentAnimatorClipInfo(0, e.Clips);
            AnimatorClipInfo best = default; foreach (var candidate in e.Clips) if (!best.clip || candidate.weight > best.weight) best = candidate;
            if (!best.clip) return null;
            var state = animator.GetCurrentAnimatorStateInfo(0); clip = best.clip.name;
            phase = state.normalizedTime * best.clip.length; rate = animator.speed * state.speed * state.speedMultiplier;
            loop = best.clip.isLooping; paused = !animator.enabled || rate == 0; emitting = true;
            if (old != null) shaderRandom = old.ShaderRandom;
            else if (e.Mesh) { e.Mesh!.GetPropertyBlock(e.Block); shaderRandom = e.Block.GetFloat("_Random"); }
        }
        bool restart = old == null || old.Resource != e.Resource || old.Seed != seed || !ReferenceEquals(old.Seeds, seeds) ||
            old.Clip != clip || old.Rate != rate || old.Emitting != emitting || old.Paused != paused || old.Loop != loop || !loop && phase + .05 < e.LastPhase;
        e.LastPhase = (float)phase;
        var t = e.Root.transform; e.WorldTransformReads++;
        t.GetPositionAndRotation(out var position, out var rotation); Vector3 scale = t.lossyScale;
        bool samePosition = old != null && Vec(old.Position, position), sameRotation = old != null && Quat(old.Rotation, rotation), sameScale = old != null && Vec(old.Scale, scale);
        if (!restart && old != null && samePosition && sameRotation && sameScale && old.ShaderRandom == shaderRandom &&
            ReferenceEquals(old.Particles, parameters) && ReferenceEquals(old.ForceField, forceField)) return old;
        return e.Last = new EffectReplayFrame(samePosition ? old!.Position : new[] { position.x, position.y, position.z },
            sameRotation ? old!.Rotation : new[] { rotation.x, rotation.y, rotation.z, rotation.w }, sameScale ? old!.Scale : new[] { scale.x, scale.y, scale.z })
        {
            Key = e.Key, Resource = e.Resource, Kind = e.Kind,
            AnchorTime = restart ? now : old!.AnchorTime, Phase = restart ? phase : old!.Phase, Rate = rate, Loop = loop,
            Emitting = emitting, EmissionStopPhase = emitting ? -1 : old != null && !old.Emitting ? old.EmissionStopPhase : phase,
            Paused = paused, Seed = seed, Seeds = seeds, Clip = clip, ShaderRandom = shaderRandom,
            Particles = parameters, ForceField = forceField, InitialState = old?.InitialState,
        };
    }
    private static bool Vec(float[] values, Vector3 v) => values[0] == v.x && values[1] == v.y && values[2] == v.z;
    private static bool Quat(float[] values, Quaternion q) => values[0] == q.x && values[1] == q.y && values[2] == q.z && values[3] == q.w;
    private void Warn(string key, string message) { if (warned.Count < 256 && warned.Add(key)) warning?.Invoke(message); }
    public void Dispose() { if (disposed) return; disposed = true; if (ReferenceEquals(current, this)) current = null; harmony.UnpatchSelf(); segments?.Dispose(); foreach (var entry in entries.Values) RetainCounters(entry); entries.Clear(); scopeEntries.Clear(); pendingEvents.Clear(); startupRuns.Clear(); activeRuns.Clear(); eventWork.Clear(); eventFrames.Clear(); previous = Array.Empty<EffectReplayFrame>(); }
}

internal sealed class NativeParticleReplica : IDisposable
{
    public readonly GameObject Root;
    public ParticleSystem[] Systems { get; private set; } = Array.Empty<ParticleSystem>();
    public readonly List<string> Limitations = new();
    private readonly Dictionary<Transform, Transform> transforms = new();
    private readonly Dictionary<ParticleSystem, ParticleSystem> systems = new();
    private readonly List<ParticleSystem> originals = new();
    private readonly List<GameObject> owned = new();
    private ParticleSystemRenderer[] renderers = Array.Empty<ParticleSystemRenderer>();
    private EffectParticlePlayback? parameters;
    private float[] relativeRates = Array.Empty<float>();
    private IEnumerator<int>? building;
    private bool emitting, disposed;
    private bool? renderingVisible;
    public bool Emitting => emitting;
    private int restartStep = -1;
    private double restartVisualPhase;
    private EffectReplayFrame? restarting;
    private EffectCheckpointPlaybackPlan? restoringCheckpoint;
    private EffectReplayFrame? checkpointState;
    private ParticleSystem.Particle[][]? checkpointScratch;
    private int checkpointStep;
    internal string[] SystemPaths { get; private set; } = Array.Empty<string>();
    private static readonly Queue<NativeParticleReplica> retired = new();
    private static int retiredNodes, drainedFrame = -1, drainedNodes;
    public bool IsReady { get; private set; }
    public static bool CanCreate => retiredNodes < 8192;
    public NativeParticleReplica(GameObject source)
    {
        Root = new GameObject("PEAK Replay Effect - particles"); Root.SetActive(false); owned.Add(Root);
        building = Build(source).GetEnumerator();
    }
    public bool ContinueBuild(PresentationWorkBudget budget)
    {
        if (disposed) throw new ObjectDisposedException(nameof(NativeParticleReplica));
        while (!IsReady && CanCreate && budget.Build())
            if (!building!.MoveNext()) { building.Dispose(); building = null; IsReady = true; }
        return IsReady;
    }
    private IEnumerable<int> Build(GameObject source)
    {
        foreach (int step in CopyTree(source.transform, Root.transform, 0)) yield return step;
        if (originals.Count == 0 || originals.Count > 64) throw new InvalidOperationException("Unsupported particle hierarchy.");
        foreach (var particle in originals)
        { systems.Add(particle, transforms[particle.transform].gameObject.AddComponent<ParticleSystem>()); yield return 0; }
        int particles = 0;
        foreach (var original in originals)
        {
            var copy = systems[original];
            foreach (int step in CopyModules(original, copy)) yield return step;
            particles += copy.main.maxParticles;
            if (particles > 16384) throw new InvalidOperationException("Native particle budget exceeds 16384 per group.");
            copy.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear); yield return 0;
            foreach (int step in CopyRenderer(original.GetComponent<ParticleSystemRenderer>(), copy.GetComponent<ParticleSystemRenderer>())) yield return step;
        }
        Systems = originals.Select(p => systems[p]).ToArray();
        SystemPaths = originals.Select(p => EffectParticleCheckpointRuntime.Path(p.transform, source.transform)).ToArray();
        renderers = Systems.Select(p => p.GetComponent<ParticleSystemRenderer>()).ToArray();
        float rootRate = Math.Max(.0001f, originals[0].main.simulationSpeed);
        relativeRates = originals.Select(p => p.main.simulationSpeed / rootRate).ToArray();
        yield return 0;
        foreach (var original in originals)
            foreach (int step in BindSubEmitters(original, systems[original])) yield return step;
        parameters = new EffectParticlePlayback(originals.ToArray(), Systems, Root.transform, owned);
        foreach (int step in parameters.Build()) yield return step;
        foreach (var original in transforms.Keys)
        {
            if (!original) throw new InvalidOperationException("Native particle template was unloaded during construction.");
            if (original.GetComponent<Animator>()) Limitations.Add("child particle Animator not replayed");
            foreach (var component in original.GetComponents<MonoBehaviour>())
                if (component && component.GetType().Name != "TrackNetworkedObject")
                    Limitations.Add("custom particle behaviour not executed: " + component.GetType().Name);
            yield return 0;
        }
    }
    private IEnumerable<int> CopyTree(Transform source, Transform target, int depth)
    {
        if (!source) throw new InvalidOperationException("Native particle template was unloaded during construction.");
        if (depth > 64 || transforms.Count >= 256) throw new InvalidOperationException("Particle hierarchy too large.");
        transforms.Add(source, target); target.localPosition = depth == 0 ? Vector3.zero : source.localPosition;
        target.localRotation = depth == 0 ? Quaternion.identity : source.localRotation; target.localScale = depth == 0 ? Vector3.one : source.localScale;
        if (depth != 0) target.gameObject.SetActive(source.gameObject.activeSelf);
        var particle = source.GetComponent<ParticleSystem>(); if (particle) originals.Add(particle);
        yield return 0;
        for (int i = 0; i < source.childCount; i++)
        {
            var child = source.GetChild(i); var go = new GameObject(child.name); owned.Add(go); go.transform.SetParent(target, false);
            foreach (int step in CopyTree(child, go.transform, depth + 1)) yield return step;
        }
    }
    private IEnumerable<int> CopyModules(ParticleSystem source, ParticleSystem target)
    {
        foreach (var property in typeof(ParticleSystem).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            Type module = property.PropertyType;
            if (!module.IsValueType || module.DeclaringType != typeof(ParticleSystem) || !module.Name.EndsWith("Module", StringComparison.Ordinal)) continue;
            object from = property.GetValue(source)!, to = property.GetValue(target)!;
            foreach (var setting in module.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!setting.CanRead || !setting.CanWrite || setting.GetIndexParameters().Length != 0 ||
                    setting.GetCustomAttribute<ObsoleteAttribute>() != null || typeof(Component).IsAssignableFrom(setting.PropertyType) ||
                    setting.Name == "enabled" && (module.Name == "CollisionModule" || module.Name == "TriggerModule" || module.Name == "ExternalForcesModule" || module.Name == "LightsModule")) continue;
                try { setting.SetValue(to, setting.GetValue(from)); }
                catch { Limitations.Add(module.Name + "." + setting.Name); }
                yield return 0;
            }
        }
        yield return 0;
        var main = target.main; main.playOnAwake = false; main.stopAction = ParticleSystemStopAction.None;
        main.useUnscaledTime = false; main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
        if (source.main.customSimulationSpace)
        {
            if (transforms.TryGetValue(source.main.customSimulationSpace, out var space)) main.customSimulationSpace = space;
            else main.simulationSpace = ParticleSystemSimulationSpace.World; // schema 15 binds an observed private space after construction
        }
        var collision = target.collision; collision.enabled = false;
        var trigger = target.trigger; trigger.enabled = false;
        var external = target.externalForces; external.enabled = false;
        var lights = target.lights; lights.enabled = false;
        if (source.collision.enabled || source.trigger.enabled || source.lights.enabled ||
            source.externalForces.enabled && !EffectParticleForces.TryAuditedBeeField(source, out _))
            Limitations.Add("physics/trigger/external force/light callbacks disabled");
        yield return 0;
        var bursts = new ParticleSystem.Burst[source.emission.burstCount]; source.emission.GetBursts(bursts); var emission = target.emission; emission.SetBursts(bursts);
        var sheet = target.textureSheetAnimation;
        // Unity serializes a null placeholder even for Grid/disabled sheets.
        // AddSprite(null) throws and used to discard every such particle group.
        bool spritesMode = source.textureSheetAnimation.mode == ParticleSystemAnimationMode.Sprites;
        if (spritesMode)
            for (int i = 0; i < source.textureSheetAnimation.spriteCount; i++)
            {
                var sprite = source.textureSheetAnimation.GetSprite(i);
                if (PresentationResourceFailures.CopySpriteSlot(spritesMode, sprite)) sheet.AddSprite(sprite);
                else if (source.textureSheetAnimation.enabled) Limitations.Add("empty sprite sheet slot");
                yield return 0;
            }
        var shape = target.shape;
        // Native unions retain stale renderer references even for Sphere (the
        // GhostBall FlameDie prefab does this). Inspect the selected mode only.
        if (EffectReplayRules.UsesRendererShape((int)source.shape.shapeType) && !EffectParticleSurfaces.TryStaticSurface(source, out _))
        { shape.enabled = false; Limitations.Add("external renderer emission shape"); }
        var data = target.customData;
        foreach (var stream in new[] { ParticleSystemCustomData.Custom1, ParticleSystemCustomData.Custom2 })
        {
            var mode = source.customData.GetMode(stream); data.SetMode(stream, mode);
            if (mode == ParticleSystemCustomDataMode.Color) data.SetColor(stream, source.customData.GetColor(stream));
            else if (mode == ParticleSystemCustomDataMode.Vector)
            {
                int count = source.customData.GetVectorComponentCount(stream); data.SetVectorComponentCount(stream, count);
                for (int i = 0; i < count; i++) data.SetVector(stream, i, source.customData.GetVector(stream, i));
            }
            yield return 0;
        }
    }
    private IEnumerable<int> BindSubEmitters(ParticleSystem source, ParticleSystem target)
    {
        var output = target.subEmitters;
        for (int i = 0; i < source.subEmitters.subEmittersCount; i++)
        {
            var original = source.subEmitters.GetSubEmitterSystem(i);
            if (!original || !systems.TryGetValue(original, out var copy)) { Limitations.Add("external sub-emitter"); continue; }
            output.AddSubEmitter(copy, source.subEmitters.GetSubEmitterType(i), source.subEmitters.GetSubEmitterProperties(i), source.subEmitters.GetSubEmitterEmitProbability(i));
            yield return 0;
        }
    }
    private static IEnumerable<int> CopyRenderer(ParticleSystemRenderer original, ParticleSystemRenderer copy)
    {
        copy.sharedMaterials = original.sharedMaterials; copy.trailMaterial = original.trailMaterial;
        foreach (string name in new[] { "renderMode", "sortMode", "minParticleSize", "maxParticleSize", "cameraVelocityScale", "velocityScale", "lengthScale", "alignment", "pivot", "flip", "enableGPUInstancing", "normalDirection", "sortingFudge", "shadowCastingMode", "receiveShadows", "meshDistributionMode" })
        {
            var property = typeof(ParticleSystemRenderer).GetProperty(name);
            if (property?.CanWrite == true) property.SetValue(copy, property.GetValue(original));
            yield return 0;
        }
        var meshes = new Mesh[original.meshCount]; original.GetMeshes(meshes); if (meshes.Length > 0) copy.SetMeshes(meshes);
        var streams = new List<ParticleSystemVertexStream>(); original.GetActiveVertexStreams(streams); copy.SetActiveVertexStreams(streams);
        var block = new MaterialPropertyBlock(); original.GetPropertyBlock(block); copy.SetPropertyBlock(block);
        copy.enabled = original.enabled;
    }
    public void BeginRestart(EffectReplayFrame state, double visualStartPhase = 0)
    {
        restoringCheckpoint = null; checkpointState = null;
        restarting = state; restartStep = 0; restartVisualPhase = visualStartPhase; SetVisible(false);
    }
    internal void BeginCheckpointRestore(EffectReplayFrame state, EffectCheckpointPlaybackPlan plan)
    {
        if (plan.Particles.Length != Systems.Length || !EffectParticleCheckpointRuntime.CanCheckpoint(Systems))
            throw new InvalidOperationException("Native particle group does not support a complete checkpoint.");
        restoringCheckpoint = plan; checkpointState = state; checkpointStep = 0;
        checkpointScratch ??= new ParticleSystem.Particle[Systems.Length][];
    }
    internal bool ContinueCheckpointRestore(PresentationWorkBudget budget)
    {
        if (restoringCheckpoint == null) return true;
        int count = Systems.Length;
        while (checkpointStep <= count * 2 && budget.Build())
        {
            if (checkpointStep < count)
            {
                var checkpoint = restoringCheckpoint.Particles[checkpointStep].Checkpoint!;
                EffectParticleCheckpointRuntime.Validate(checkpoint);
                if (checkpoint.Count > Systems[checkpointStep].main.maxParticles)
                    throw new InvalidOperationException("Recorded particles exceed the native group's capacity.");
                if (checkpointScratch![checkpointStep] == null || checkpointScratch[checkpointStep].Length < checkpoint.Count)
                    checkpointScratch[checkpointStep] = new ParticleSystem.Particle[Math.Max(1, checkpoint.Count)];
                if (checkpoint.Count > checkpointScratch[checkpointStep].Length)
                    throw new InvalidOperationException("Recorded particles exceed the native group's capacity.");
            }
            else if (checkpointStep == count)
            {
                if (parameters == null || !parameters.Apply(checkpointState!))
                    throw new InvalidOperationException("Checkpoint has no complete particle module directory.");
            }
            else
            {
                int i = checkpointStep - count - 1;
                EffectParticleCheckpointRuntime.Restore(Systems[i], restoringCheckpoint.Particles[i].Checkpoint!, checkpointScratch![i]);
            }
            checkpointStep++;
        }
        if (checkpointStep <= count * 2) return false;
        parameters!.RestoredEmission(checkpointState!); emitting = checkpointState!.Emitting;
        restoringCheckpoint = null; checkpointState = null; return true;
    }
    internal EffectReplayFrame? CaptureWarmCheckpoint(EffectReplayFrame state, double time, double phase)
    {
        // One atomic bounded system avoids collecting different replay times
        // across rendered frames or pausing visible animation to fill a cache.
        if (Systems.Length != 1 || Systems[0].main.maxParticles > 2048 || state.Particles?.Length != 1 ||
            !EffectParticleCheckpointRuntime.CanCheckpoint(Systems)) return null;
        checkpointScratch ??= new ParticleSystem.Particle[1][];
        if (checkpointScratch[0] == null || checkpointScratch[0].Length < Systems[0].main.maxParticles)
            checkpointScratch[0] = EffectParticleCheckpointRuntime.Scratch(Systems[0]);
        var old = state.Particles[0]; var pose = old.Pose;
        var particle = new EffectParticleFrame(new NodePose(SystemPaths[0], pose.Position, pose.Rotation, pose.Scale, pose.Active, pose.Visible), old.Colors, old.Parameters)
        { Emitting = old.Emitting, Paused = old.Paused, EmissionEnabled = old.EmissionEnabled, ColorMode = old.ColorMode,
            MaterialVariant = old.MaterialVariant, SimulationSpace = old.SimulationSpace, MeshShapeSpace = old.MeshShapeSpace,
            Checkpoint = EffectParticleCheckpointRuntime.Capture(Systems[0], checkpointScratch[0], time, phase) };
        var p = Root.transform.position; var q = Root.transform.rotation; var s = Root.transform.lossyScale;
        return new EffectReplayFrame(new[] { p.x,p.y,p.z },new[] { q.x,q.y,q.z,q.w },new[] { s.x,s.y,s.z })
        { Key=state.Key,Resource=state.Resource,Kind=state.Kind,AnchorTime=time,Phase=phase,Rate=state.Rate,Loop=state.Loop,
            Emitting=state.Emitting,EmissionStopPhase=state.EmissionStopPhase,Paused=state.Paused,Seed=state.Seed,Seeds=state.Seeds,
            Clip=state.Clip,ShaderRandom=state.ShaderRandom,Particles=new[]{particle},ForceField=state.ForceField };
    }
    public bool ContinueRestart(PresentationWorkBudget budget)
    {
        if (restartStep < 0) return true;
        while (restartStep <= Systems.Length * 3 && budget.Build())
        {
            if (restartStep == 0) Root.SetActive(true);
            else
            {
                int operation = restartStep - 1, index = operation / 3;
                var particle = Systems[index];
                switch (operation % 3)
                {
                    case 0: particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear); break;
                    case 1:
                        particle.useAutoRandomSeed = false;
                        particle.randomSeed = (uint)(index < restarting!.Seeds.Length ? restarting.Seeds[index] : restarting.Seed);
                        var main = particle.main; main.simulationSpeed = relativeRates[index]; break;
                    // Play and Pause are one operation; yielding between them
                    // would let Unity advance this child outside replay time.
                    case 2:
                        particle.Play(false); particle.Pause(false);
                        if (restartVisualPhase > 0 && particle.main.loop)
                            particle.time = EffectNativeWarmupWindow.LoopTime(restartVisualPhase, relativeRates[index], particle.main.duration);
                        break;
                }
            }
            restartStep++;
        }
        if (restartStep <= Systems.Length * 3) return false;
        restartStep = -1; restarting = null; emitting = true; parameters?.ResetEmission(true); return true;
    }
    public void Step(float delta, bool emitting, EffectReplayFrame? state = null)
    {
        if (state != null && parameters != null && parameters.Apply(state))
        { parameters.Step(delta, emitting); this.emitting = emitting; return; }
        if (this.emitting != emitting)
        {
            if (!emitting) Systems[0].Stop(true, ParticleSystemStopBehavior.StopEmitting);
            else Systems[0].Play(true);
            this.emitting = emitting;
            parameters?.ResetEmission(emitting);
        }
        Systems[0].Simulate(Math.Max(0, delta), true, false, false);
        Systems[0].Pause(true);
    }
    public void PresentWorldSpaces(EffectReplayFrame state) => parameters?.PresentWorldSpaces(state);
    internal double VisualWarmupStart(double phase)
    {
        double lifetime = 0, duration = 0, delay = 0;
        for (int i = 0; i < Systems.Length; i++)
        {
            var main = Systems[i].main; double rate = Math.Max(.0001f, relativeRates[i]);
            lifetime = Math.Max(lifetime, Upper(main.startLifetime) / rate);
            delay = Math.Max(delay, Upper(main.startDelay) / rate); duration = Math.Max(duration, main.duration / rate);
        }
        return EffectNativeWarmupWindow.Start(phase, lifetime, duration, delay);
    }
    private static double Upper(ParticleSystem.MinMaxCurve curve) => curve.mode == ParticleSystemCurveMode.Constant ? Math.Max(0,curve.constant) :
        curve.mode == ParticleSystemCurveMode.TwoConstants ? Math.Max(0,Math.Max(curve.constantMin,curve.constantMax)) : double.PositiveInfinity;
    public void SetVisible(bool visible)
    {
        if (renderingVisible == visible) return;
        renderingVisible = visible;
        foreach (var renderer in renderers) if (renderer) renderer.forceRenderingOff = !visible;
    }
    public void Retire()
    {
        if (disposed) return; disposed = true;
        building?.Dispose(); building = null; restarting = null;
        if (Root) Root.SetActive(false);
        retiredNodes += owned.Count; retired.Enqueue(this);
    }
    public static void DrainRetired(PresentationWorkBudget budget)
    {
        if (drainedFrame != Time.frameCount) { drainedFrame = Time.frameCount; drainedNodes = 0; }
        while (retired.Count != 0 && drainedNodes < 32 && budget.Build())
        {
            var replica = retired.Peek();
            if (replica.owned.Count != 0)
            {
                int last = replica.owned.Count - 1; var go = replica.owned[last]; replica.owned.RemoveAt(last);
                if (go) Object.Destroy(go); retiredNodes--; drainedNodes++;
            }
            else { retired.Dequeue(); replica.Clear(); }
        }
    }
    public static void FlushRetired()
    {
        while (retired.Count != 0)
        {
            var replica = retired.Dequeue(); retiredNodes -= replica.owned.Count;
            if (replica.Root) Object.Destroy(replica.Root); replica.Clear();
        }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        building?.Dispose(); building = null;
        if (Root) { Root.SetActive(false); Object.Destroy(Root); } Clear();
    }
    private void Clear()
    { owned.Clear(); transforms.Clear(); systems.Clear(); originals.Clear(); restarting = null; restoringCheckpoint = null; checkpointState = null;
        checkpointScratch = null; SystemPaths = Array.Empty<string>(); parameters = null; Systems = Array.Empty<ParticleSystem>(); renderers = Array.Empty<ParticleSystemRenderer>(); relativeRates = Array.Empty<float>(); }
}

internal sealed class NativeAnimatedEffectReplica : IDisposable
{
    public readonly VisualReplica Visual;
    private PlayableGraph graph;
    private AnimationClipPlayable player;
    private readonly GameObject source;
    private readonly string clipName;
    public bool IsReady { get; private set; }
    private readonly MaterialPropertyBlock block = new();
    public GameObject Root => Visual.Root;
    public NativeAnimatedEffectReplica(GameObject source, string clipName)
    {
        this.source = source; this.clipName = clipName;
        Visual = VisualReplica.BeginBuild(source, shareImmutableMaterials: true); Visual.Root.name = "PEAK Replay Effect - animated mesh";
    }
    public bool ContinueBuild(PresentationWorkBudget budget)
    {
        if (IsReady) return true;
        if (!Visual.ContinueBuild(budget) || !budget.Build()) return false;
        try
        {
            var original = source.GetComponentInChildren<Animator>(true);
            if (!original || !original.runtimeAnimatorController) throw new InvalidOperationException("Native explosion animator missing.");
            var clips = original.runtimeAnimatorController.animationClips.Where(c => c && c.name == clipName).Distinct().ToArray();
            if (clips.Length != 1) throw new InvalidOperationException("Explosion clip identity is missing or ambiguous.");
            var path = new Stack<int>(); for (var t = original.transform; t != source.transform; t = t.parent) path.Push(t.GetSiblingIndex());
            var target = Root.transform; while (path.Count > 0) target = target.GetChild(path.Pop());
            var animator = target.gameObject.AddComponent<Animator>(); animator.avatar = original.avatar;
            animator.runtimeAnimatorController = null; animator.fireEvents = false; animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            graph = PlayableGraph.Create("PeakReplayLab.Explosion"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            player = AnimationClipPlayable.Create(graph, clips[0]); player.SetSpeed(0); player.SetApplyFootIK(false); player.SetApplyPlayableIK(false);
            AnimationPlayableOutput.Create(graph, "effect", animator).SetSourcePlayable(player); graph.Play();
            IsReady = true; return true;
        }
        catch { Retire(); throw; }
    }
    public void Apply(EffectReplayFrame state, double time)
    {
        Root.SetActive(true); player.SetTime(EffectReplayRules.PhaseAt(state, time)); graph.Evaluate(0);
        block.SetFloat("_Random", state.ShaderRandom);
        foreach (var renderer in Visual.Renderers) if (renderer) renderer.SetPropertyBlock(block);
    }
    public void Retire() { if (graph.IsValid()) graph.Destroy(); Visual.Retire(); }
    public void Dispose() { if (graph.IsValid()) graph.Destroy(); Visual.Dispose(); }
}

internal sealed class EffectReplayPlayback : IDisposable
{
    private sealed class Replica : IDisposable
    {
        public NativeParticleReplica? Particle;
        public NativeAnimatedEffectReplica? Mesh;
        public VisualReplica? Sphere;
        public string Resource = "", Clip = "";
        public double Time = double.NaN;
        public readonly EffectReplayRecoveryBatch Recovery = new();
        public double Phase => Recovery.Phase;
        public bool Warming => Recovery.Pending;
        public EffectReplayFrame? TargetRight;
        public float TargetMix;
        public long Seed;
        public long[] Seeds = Array.Empty<long>();
        public bool Emitting;
        public bool Approximate, LimitationsReported;
        public bool Unsupported, CacheWanted, RestoreStarted;
        public EffectCheckpointPlaybackPlan? Checkpoint;
        public EffectReplayFrame? CheckpointModules;
        public int Seen;
        public GameObject Root => Particle != null ? Particle.Root : Mesh != null ? Mesh.Root : Sphere!.Root;
        public bool IsReady => Particle != null ? Particle.IsReady : Mesh != null ? Mesh.IsReady : Sphere!.IsReady;
        public bool ContinueBuild(PresentationWorkBudget budget) => Particle != null ? Particle.ContinueBuild(budget) : Mesh != null ? Mesh.ContinueBuild(budget) : Sphere!.ContinueBuild(budget);
        public void Retire() { Particle?.Retire(); Mesh?.Retire(); Sphere?.Retire(); }
        public void Dispose() { Particle?.Dispose(); Mesh?.Dispose(); Sphere?.Dispose(); }
    }
    private readonly Dictionary<string, Replica> replicas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EffectReplayFrame> following = new(StringComparer.Ordinal);
    private readonly bool includeSceneEvents;
    private readonly List<EffectReplayFrame> selected = new();
    private readonly HashSet<string> selectedKeys = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<EffectReplayFrame, EffectReplayFrame> birthClocks = new();
    private readonly ConditionalWeakTable<EffectReplayFrame, EffectReplayFrame> emittingBirthClocks = new();
    private readonly PresentationResourceFailures failures = new();
    private readonly EffectParticleWarmCache warmCache = new();
    private readonly HashSet<string> warned = new(StringComparer.Ordinal);
    private readonly Dictionary<Renderer, bool> hidden = new();
    private readonly Action<string>? warning;
    private Func<double, EffectReplayHistorySample?>? timeline;
    private int serial;
    private readonly PresentationWorkBudget budget;
    private readonly Func<bool> admitStep;
    private readonly List<KeyValuePair<string, Replica>> sleeping = new();
    private EffectReplayFrame[] lastLeft = Array.Empty<EffectReplayFrame>(), lastRight = Array.Empty<EffectReplayFrame>();
    private double lastTime, lastLeftTime = double.NaN, lastRightTime = double.NaN;
    private float lastMix;
    private bool pending;
    private int appliedFrame = -1;
    public int MissingCount { get; private set; }
    public int VisibleCount { get; private set; }
    public int LaggingCount { get; private set; }
    public EffectReplayPlayback(Action<string>? warning = null, PresentationWorkBudget? workBudget = null, bool includeSceneEvents = false)
    { this.warning = warning; this.includeSceneEvents = includeSceneEvents; budget = workBudget ?? new(); admitStep = budget.Step; RefreshCatalog(); }
    public void SetTimeline(Func<double, EffectReplayHistorySample?> sample) => timeline = sample;
    public void Enforce(bool completePending = true)
    {
        budget.BeginFrame(Time.frameCount); NativeParticleReplica.DrainRetired(budget); VisualReplica.DrainRetired(budget);
        foreach (var pair in hidden) if (pair.Key && !pair.Key.forceRenderingOff) pair.Key.forceRenderingOff = true;
        // Paused replay still calls Enforce. Finish a bounded seek warm-up without
        // advancing its replay clock, and without requiring the user to press Play.
        if (completePending && pending && appliedFrame != Time.frameCount) Apply(lastLeft, lastRight, lastMix, lastTime, false, lastLeftTime, lastRightTime);
    }
    private void RefreshCatalog()
    {
        foreach (var p in NativeEffectResources.Refresh())
            if (p && p.gameObject.scene.IsValid() && NativeEffectResources.Relevant(p))
                foreach (var renderer in p.GetComponentsInChildren<ParticleSystemRenderer>(true)) HideOriginal(renderer);
        foreach (var sphere in Resources.FindObjectsOfTypeAll<Peak.AntiSphere>())
            if (sphere && sphere.gameObject.scene.IsValid() && !NativeEffectResources.Ours(sphere.transform) &&
                NativePlacementResources.Match(sphere.gameObject)?.Kind != "anti-sphere")
                foreach (var renderer in sphere.GetComponentsInChildren<Renderer>(true)) HideOriginal(renderer);
    }
    private void HideOriginal(Renderer renderer)
    {
        if (!renderer || hidden.ContainsKey(renderer)) return;
        hidden.Add(renderer, renderer.forceRenderingOff); renderer.forceRenderingOff = true;
    }
    public void Apply(EffectReplayFrame[] left, EffectReplayFrame[] right, float mix, double time, bool discontinuity,
        double leftTime = double.NaN, double rightTime = double.NaN)
    {
        lastLeft = left; lastRight = right; lastTime = time; lastMix = mix;
        lastLeftTime = leftTime; lastRightTime = rightTime; pending = false;
        appliedFrame = Time.frameCount;
        budget.BeginFrame(Time.frameCount); NativeParticleReplica.DrainRetired(budget); VisualReplica.DrainRetired(budget);
        serial++; MissingCount = 0; VisibleCount = 0; LaggingCount = 0; following.Clear();
        foreach (var frame in right) following[frame.Key] = frame;
        // Reclaim old cursor entries before admission; a full old window must
        // never permanently reject valid resources at the new cursor.
        selected.Clear(); selectedKeys.Clear();
        foreach (var frame in left)
        {
            selectedKeys.Add(frame.Key);
            var state = EffectSceneEventPlayback.Select(frame, time, includeSceneEvents);
            if (state != null) selected.Add(BirthClock(state, time));
        }
        if (includeSceneEvents)
            foreach (var frame in right)
            {
                if (!EffectSceneEventPlayback.IncludeRight(frame, leftTime, rightTime, time, true, selectedKeys.Contains(frame.Key))) continue;
                selectedKeys.Add(frame.Key);
                var state = EffectSceneEventPlayback.Select(frame, time, true);
                if (state != null) selected.Add(BirthClock(state, time));
            }
        TrimSleeping(selected);
        // Rotate expensive work: one long emitter must not starve every other
        // effect while the replay is paused at its target cursor.
        for (int index = 0; index < selected.Count; index++)
        {
            var frame = selected[(index + serial % Math.Max(1, selected.Count)) % selected.Count];
            if (!failures.Allows(frame.Resource, frame.Clip))
            {
                if (replicas.TryGetValue(frame.Key, out var waiting)) { waiting.Seen = serial; waiting.Root.SetActive(false); }
                MissingCount++; continue;
            }
            if (replicas.TryGetValue(frame.Key, out var obsolete) && (obsolete.Resource != frame.Resource || obsolete.Clip != frame.Clip))
            { warmCache.RemoveOwner(obsolete); obsolete.Retire(); replicas.Remove(frame.Key); }
            if (!replicas.TryGetValue(frame.Key, out var replica))
            {
                if (!NativeParticleReplica.CanCreate || !VisualReplica.CanStartBuild || !budget.Create()) { pending = true; MissingCount++; continue; }
                Replica? creating = null;
                try
                {
                    var resource = NativeEffectResources.Find(frame.Resource);
                    if (resource == null || resource.Kind != frame.Kind) throw new InvalidOperationException("Native effect resource unavailable: " + frame.Resource);
                    if (replicas.Count >= EffectReplayRules.Maximum + 32) throw new InvalidOperationException("Effect replica pool reached its bounded capacity.");
                    creating = replica = new Replica { Resource = frame.Resource, Clip = frame.Clip };
                    if (frame.Kind == 0)
                    {
                        replica.Particle = new NativeParticleReplica(resource.Source);
                    }
                    else if (frame.Kind == 1) replica.Mesh = new NativeAnimatedEffectReplica(resource.Source, frame.Clip);
                    else { replica.Sphere = VisualReplica.BeginBuild(resource.Source, shareImmutableMaterials: true); replica.Sphere.Root.name = "PEAK Replay Effect - antigravity sphere"; }
                    replicas.Add(frame.Key, replica);
                }
                catch (Exception error) { creating?.Retire(); MissingCount++; failures.Reject(frame.Resource, frame.Clip); Warn(frame.Resource, "特效资源已隔离（本次播放不重复构建）: " + error.Message); continue; }
            }
            replica.Seen = serial;
            try
            {
                if (!replica.ContinueBuild(budget)) { pending = true; MissingCount++; continue; }
                following.TryGetValue(frame.Key, out var next);
                if (includeSceneEvents && frame.InitialState != null && time < frame.InitialState.UntilTime) next = null;
                bool reset = EffectReplayRules.RequiresRestart(frame, time, replica.Time, replica.Phase,
                    replica.Seed, replica.Seeds, replica.Emitting, replica.Root.activeSelf, replica.Warming, discontinuity);
                if (replica.Particle != null)
                {
                    if (reset) StartPreroll(replica, frame, next, mix, time);
                    if (replica.Unsupported)
                    { MissingCount++; replica.Time = time; continue; }
                    else if (!replica.Warming)
                    {
                        double phase = EffectReplayRules.PhaseAt(frame, time), delta = Math.Max(0, phase - replica.Phase);
                        if (EffectSceneEventPlayback.CrossesBoundary(frame, replica.Time, time, includeSceneEvents))
                        {
                            replica.Recovery.CatchUp(frame, time); replica.TargetRight = next; replica.TargetMix = mix;
                        }
                        else if (delta <= .00001 && replica.Particle.Emitting == frame.Emitting)
                        {
                            // Completed paused emitters do not consume the work
                            // needed by effects that are still recovering.
                            SetPose(replica.Root.transform, frame, next, mix); replica.Recovery.Present(frame, time);
                        }
                        else if (delta <= EffectReplayRules.MaximumSimulationPhaseStep && budget.Step())
                        {
                            // Ordinary playback already has the interpolated target
                            // pose; it does not need a history request per frame.
                            SetPose(replica.Root.transform, frame, next, mix);
                            replica.Particle.Step((float)delta, frame.Emitting, frame); replica.Recovery.Present(frame, time);
                        }
                        else
                        {
                            replica.Recovery.CatchUp(frame, time); replica.TargetRight = next; replica.TargetMix = mix;
                        }
                    }
                    if (replica.Warming && !ContinuePreroll(replica))
                    {
                        bool visible = replica.Recovery.HasPresented;
                        replica.Particle.SetVisible(visible);
                        pending = true; MissingCount++; replica.Time = time;
                        replica.Seed = frame.Seed; replica.Seeds = frame.Seeds; replica.Emitting = frame.Emitting;
                        if (visible)
                        {
                            VisibleCount++;
                            if (replica.Recovery.Lags(frame, time))
                            { LaggingCount++; Warn("effect-lag", "特效正在按预算追赶真实历史，暂时落后于时间轴。"); }
                        }
                        else Warn("seek-budget", "特效正在按全局预算分帧预滚；完成前暂不显示，避免拖动时间轴卡住。");
                        continue;
                    }
                    if (replica.Recovery.Lags(frame, time))
                    {
                        // Show the genuinely simulated historical state. Do not
                        // teleport a lagging local-space emitter to the new pose.
                        pending = true; MissingCount++; LaggingCount++;
                        Warn("effect-lag", "特效正在按预算追赶真实历史，暂时落后于时间轴。");
                    }
                    else
                    {
                        SetPose(replica.Root.transform, frame, next, mix);
                        replica.Particle.PresentWorldSpaces(frame);
                    }
                    if (!replica.LimitationsReported && replica.Particle.Limitations.Count > 0)
                    {
                        replica.LimitationsReported = true;
                        Warn(frame.Resource, "原生特效存在安全回放限制（非完整复制）: " + string.Join(", ", replica.Particle.Limitations.Distinct()));
                    }
                    if (replica.Particle.Limitations.Count > 0 || replica.Approximate) MissingCount++;
                }
                else { if (replica.Mesh != null) replica.Mesh.Apply(frame, time); else replica.Root.SetActive(true); SetPose(replica.Root.transform, frame, next, mix); }
                replica.Time = time; replica.Seed = frame.Seed; replica.Seeds = frame.Seeds; replica.Emitting = frame.Emitting; VisibleCount++;
            }
            catch (Exception error) { warmCache.RemoveOwner(replica); replica.Retire(); replicas.Remove(frame.Key); MissingCount++; failures.Reject(frame.Resource, frame.Clip); Warn(frame.Resource, "特效回放已隔离: " + error.Message); }
        }
    }
    private EffectReplayFrame BirthClock(EffectReplayFrame frame, double time)
    {
        if (frame.InitialState != null) return frame; // piecewise clock is handled by EffectReplayRules
        if (!EffectSceneEventSelection.TryBirth(includeSceneEvents, frame.Key, frame.Kind, frame.Loop, frame.Paused,
            frame.AnchorTime, frame.Phase, frame.Rate, out double birth)) return frame;
        // PhaseAt intentionally clamps backwards elapsed time for legacy frames.
        // A proved new one-shot uses phase zero at birth so a right-only event
        // advances correctly before its first immutable observation's anchor.
        var clock = birthClocks.GetValue(frame, original => new EffectReplayFrame(original.Position, original.Rotation, original.Scale)
        {
            Key = original.Key, Resource = original.Resource, Kind = original.Kind,
            AnchorTime = birth, Phase = 0, Rate = original.Rate, Loop = original.Loop,
            Emitting = original.Emitting, EmissionStopPhase = original.EmissionStopPhase, Paused = original.Paused,
            Seed = original.Seed, Seeds = original.Seeds, Clip = original.Clip, ShaderRandom = original.ShaderRandom,
            Particles = original.Particles, ForceField = original.ForceField, InitialState = original.InitialState,
        });
        if (clock.Emitting || !EffectSceneEventSelection.EmittingAtBirth(false, clock.EmissionStopPhase, birth, clock.Rate, time)) return clock;
        return emittingBirthClocks.GetValue(clock, original =>
        {
            var particles = original.Particles;
            if (particles != null && particles.Length != 0 && !particles[0].Emitting)
            {
                // The scalar cutoff proves only the root emitter's earlier
                // emission. Independent child state must not be invented.
                particles = (EffectParticleFrame[])particles.Clone(); var root = particles[0];
                particles[0] = new EffectParticleFrame(root.Pose, root.Colors, root.Parameters)
                {
                    Emitting = true, Paused = root.Paused, EmissionEnabled = root.EmissionEnabled,
                    ColorMode = root.ColorMode, MaterialVariant = root.MaterialVariant,
                    SimulationSpace = root.SimulationSpace, MeshShapeSpace = root.MeshShapeSpace,
                    Checkpoint = root.Checkpoint,
                };
            }
            return new EffectReplayFrame(original.Position, original.Rotation, original.Scale)
            {
                Key = original.Key, Resource = original.Resource, Kind = original.Kind,
                AnchorTime = original.AnchorTime, Phase = original.Phase, Rate = original.Rate, Loop = original.Loop,
                Emitting = true, EmissionStopPhase = original.EmissionStopPhase, Paused = original.Paused,
                Seed = original.Seed, Seeds = original.Seeds, Clip = original.Clip, ShaderRandom = original.ShaderRandom,
                Particles = particles, ForceField = original.ForceField, InitialState = original.InitialState,
            };
        });
    }
    private readonly HashSet<string> present = new(StringComparer.Ordinal);
    private void TrimSleeping(List<EffectReplayFrame> frames)
    {
        present.Clear(); foreach (var frame in frames) present.Add(frame.Key);
        sleeping.Clear();
        foreach (var pair in replicas)
            if (!present.Contains(pair.Key))
            {
                pair.Value.Root.SetActive(false);
                // A half-built or half-warmed target is obsolete immediately;
                // do not resume it after a later seek happens to reuse its key.
                if (!pair.Value.IsReady || pair.Value.Warming) { warmCache.RemoveOwner(pair.Value); pair.Value.Retire(); }
                sleeping.Add(pair);
            }
        for (int i = sleeping.Count - 1; i >= 0; i--)
            if (!sleeping[i].Value.IsReady || sleeping[i].Value.Warming)
            { replicas.Remove(sleeping[i].Key); sleeping.RemoveAt(i); }
        if (sleeping.Count <= 32) return;
        sleeping.Sort((a, b) => a.Value.Seen.CompareTo(b.Value.Seen));
        for (int i = 0; i < sleeping.Count - 32; i++)
        { var pair = sleeping[i]; warmCache.RemoveOwner(pair.Value); pair.Value.Retire(); replicas.Remove(pair.Key); }
    }
    private void StartPreroll(Replica replica, EffectReplayFrame current, EffectReplayFrame? next, float mix, double time)
    {
        // These identify the pending job, rather than only the last completed
        // frame. Rechecking the same seeded run must preserve warmup progress.
        replica.Seed = current.Seed; replica.Seeds = current.Seeds; replica.Emitting = current.Emitting;
        var particle = replica.Particle!; SetPose(particle.Root.transform, current, null, 0);
        double phase = EffectReplayRules.PhaseAt(current, time), start = 0;
        double visualStart = particle.VisualWarmupStart(phase); bool cachedApproximate = false;
        replica.Unsupported = false; replica.Checkpoint = null; replica.CheckpointModules = null; replica.RestoreStarted = false;
        bool recorded = EffectCheckpointPlaybackPlan.TryCreate(current.Particles, particle.SystemPaths, time, phase, out var plan);
        if (recorded && plan!.Particles[0].Checkpoint!.Layout == EffectParticleCheckpointRuntime.Layout &&
            (phase <= plan.Phase + .00001 || Math.Abs(EffectReplayRules.TimeAtPhase(current, plan.Phase) - plan.ObservedAt) <= .001))
        {
            replica.Checkpoint = plan; start = plan.Phase;
        }
        else
        {
            var cached = warmCache.Find(replica, current, particle.SystemPaths, time, phase);
            if (cached != null && EffectCheckpointPlaybackPlan.TryCreate(cached.Particles, particle.SystemPaths, time, phase, out plan, false) &&
                plan!.Phase >= visualStart - .00001 &&
                (phase <= plan!.Phase + .00001 || Math.Abs(EffectReplayRules.TimeAtPhase(current, plan!.Phase) - plan.ObservedAt) <= .001))
            { replica.Checkpoint = plan; replica.CheckpointModules = cached; start = plan!.Phase; cachedApproximate = warmCache.LastWasApproximate; }
            else start = visualStart;
        }
        particle.BeginRestart(current, replica.Checkpoint == null ? start : 0);
        replica.Recovery.Restart(current, time, start); replica.TargetRight = next; replica.TargetMix = mix;
        particle.SetVisible(false);
        replica.CacheWanted = replica.Checkpoint == null;
        replica.Approximate = replica.Checkpoint != null ? cachedApproximate || replica.Checkpoint.ObservedAt < 0 && phase > start + .00001 :
            start > .00001 || EffectReplayRules.TimeAtPhase(current, 0) < 0;
        if (replica.Checkpoint == null && start > .00001)
            Warn("visual-window:" + current.Resource, "特效使用原生模块按粒子生命周期恢复视觉窗口；逐粒子随机历史不作完全一致保证。");
        if (replica.Approximate) Warn("baseline:" + current.Resource, "录像窗口之前的发射器轨迹没有记录，特效历史存在已知限制。" );
    }
    private bool ContinuePreroll(Replica replica)
    {
        var particle = replica.Particle!; var batch = replica.Recovery; var anchor = batch.Anchor;
        if (!particle.ContinueRestart(budget)) return false;
        if (replica.Checkpoint != null)
        {
            if (!replica.RestoreStarted)
            {
                var state = replica.CheckpointModules;
                if (state == null && !EffectReplayRules.TryAdmittedBracketState(anchor, replica.Checkpoint.ObservedAt,
                    admitStep, timeline, out state, includeSceneEvents)) return false;
                if (state == null) throw new InvalidOperationException("Recorded particle checkpoint has no matching historical emitter state.");
                SetPose(particle.Root.transform,state,null,0);
                particle.BeginCheckpointRestore(state,replica.Checkpoint); replica.RestoreStarted = true;
            }
            if (!particle.ContinueCheckpointRestore(budget)) return false;
            replica.Checkpoint = null; replica.CheckpointModules = null; replica.RestoreStarted = false;
        }
        int localSteps = 0;
        while (batch.HasSlice)
        {
            if (localSteps++ >= 64 || !budget.Step()) return false;
            double next = EffectSceneEventPlayback.CutPhase(anchor, batch.Phase, batch.NextPhase, includeSceneEvents), sampleTime = batch.SampleTime;
            if (!EffectReplayRules.TryAdmittedBracketState(anchor, sampleTime, admitStep, timeline, out var state, includeSceneEvents))
                return false;
            if (state != null && EffectSceneEventSelection.TryBirth(includeSceneEvents, state.Key, state.Kind,
                state.Loop, state.Paused, state.AnchorTime, state.Phase, state.Rate, out double birth))
                state = sampleTime < birth ? null : BirthClock(state, sampleTime);
            if (state != null) SetPose(particle.Root.transform, state, null, 0);
            bool emitting = state != null && (state.Emitting || state.EmissionStopPhase >= 0 &&
                EffectReplayRules.PhaseAt(state, sampleTime) < state.EmissionStopPhase);
            particle.Step((float)(next - batch.Phase), emitting, state);
            batch.CommitSlice(next);
        }
        if (!budget.Step()) return false;
        SetPose(particle.Root.transform, anchor, replica.TargetRight, replica.TargetMix);
        particle.Step(0, anchor.Emitting, anchor);
        batch.Complete(); particle.SetVisible(true);
        if (replica.CacheWanted && budget.Build())
        {
            replica.CacheWanted = false;
            var checkpoint = particle.CaptureWarmCheckpoint(anchor,batch.TargetTime,batch.Phase);
            if (checkpoint != null) warmCache.Add(replica,checkpoint,replica.Approximate);
        }
        return true;
    }
    private static void SetPose(Transform target, EffectReplayFrame a, EffectReplayFrame? b, float mix)
    {
        b ??= a;
        Vector3 Point(float[] p) => new(p[0], p[1], p[2]);
        Quaternion Rotation(float[] q) => new(q[0], q[1], q[2], q[3]);
        target.SetPositionAndRotation(Vector3.Lerp(Point(a.Position), Point(b.Position), mix), Quaternion.Slerp(Rotation(a.Rotation), Rotation(b.Rotation), mix));
        target.localScale = Vector3.Lerp(Point(a.Scale), Point(b.Scale), mix);
    }
    private void Warn(string key, string message) { if (warned.Count < 256 && warned.Add(key)) warning?.Invoke(message); }
    public void Dispose()
    {
        foreach (var pair in hidden) try { if (pair.Key) pair.Key.forceRenderingOff = pair.Value; } catch { }
        foreach (var replica in replicas.Values) try { replica.Dispose(); } catch { }
        hidden.Clear(); replicas.Clear(); warmCache.Clear(); selected.Clear(); selectedKeys.Clear(); NativeParticleReplica.FlushRetired(); VisualReplica.FlushRetired();
        pending = false; lastLeft = lastRight = Array.Empty<EffectReplayFrame>(); timeline = null;
    }
}
