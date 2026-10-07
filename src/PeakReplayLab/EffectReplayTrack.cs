using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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
        for (var t = p.transform; t; t = t.parent)
        {
            if (CreatureDirectoryRules.IsSporeEffect(t.name)) return true;
            if (t.GetComponent<Item>() || t.GetComponent<Character>() || t.GetComponent<Spider>() || t.GetComponent<ScoutCannon>() || t.GetComponent<ExplosionEffect>() || t.GetComponent<Peak.AntiSphere>()) return true;
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
        if (key.Length > 2048 || entries.Count >= 4096 || Ours(source.transform)) return;
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
        public Animator? Animator;
        public MeshRenderer? Mesh;
        public readonly MaterialPropertyBlock Block = new();
        public string Resource = "", Key = "";
        public EffectReplayFrame? Last;
        public float LastPhase;
        public int Kind;
        public double NextRetry;
        public readonly List<AnimatorClipInfo> Clips = new(4);
    }
    private static EffectReplayCapture? current;
    private readonly Harmony harmony = new("cn.mylus.peakreplaylab.effects." + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<int, Entry> entries = new();
    private readonly List<int> removed = new();
    private readonly List<EffectReplayFrame> frames = new();
    private EffectReplayFrame[] previous = Array.Empty<EffectReplayFrame>();
    private readonly Action<string>? warning;
    private readonly HashSet<string> warned = new(StringComparer.Ordinal);
    private bool disposed;
    public EffectReplayCapture(Action<string>? warning = null)
    {
        this.warning = warning; current = this;
        Observe(typeof(Peak.RocketFX), "RocketLight"); Observe(typeof(Peak.RocketFX), "RocketFire"); Observe(typeof(Peak.RocketFX), "RocketExtinguish");
        Observe(typeof(Peak.Jetpack), "ActivateJetpack"); Observe(typeof(Peak.Jetpack), "ForceStopJetpack");
        Observe(typeof(JetpackItem), "RPCAddFuel"); Observe(typeof(ScoutCannon), "RPCA_Light");
        Observe(typeof(Dynamite), "SetFlareLitRPC");
        Observe(typeof(Peak.Action_Antizooka), "RPCA_Shoot");
        Observe(typeof(Item), "Awake"); Observe(typeof(Character), "Awake");
        Observe(typeof(ScoutCannon), "Awake");
        Observe(typeof(Spider), "Awake"); Observe(typeof(Spider), "BonkRPC");
        Observe(typeof(MushroomZombie), "Awake");
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
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(EffectReplayCapture), nameof(Changed))); }
        catch (Exception e) { Warn("hook:" + type.Name + name, "特效事件入口不可用：" + type.Name + "." + name + " · " + e.Message); }
    }
    private static void Changed(Component __instance)
    { try { if (!ReplaySafety.Active && current != null && __instance) current.Register(__instance.gameObject); } catch { } }
    private static void SphereSeen(Peak.AntiSphere __instance)
    {
        try { if (!ReplaySafety.Active && current != null && __instance &&
                NativePlacementResources.Match(__instance.gameObject)?.Kind != "anti-sphere" &&
                !current.entries.ContainsKey(__instance.gameObject.GetInstanceID()))
                current.Register(__instance.gameObject); } catch { }
    }
    private static IEnumerable<CodeInstruction> WatchSpawn(IEnumerable<CodeInstruction> instructions)
    {
        var observer = AccessTools.Method(typeof(EffectReplayCapture), nameof(Spawned));
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo method &&
                method.DeclaringType == typeof(Object) && method.Name == nameof(Object.Instantiate) && typeof(Object).IsAssignableFrom(method.ReturnType))
            { yield return new CodeInstruction(OpCodes.Dup); yield return new CodeInstruction(OpCodes.Call, observer); }
        }
    }
    private static void Spawned(Object value)
    {
        // Observe the result of the game's EXISTING Instantiate call. This method
        // neither creates a gameplay object nor invokes any game/RPC method.
        try { if (!ReplaySafety.Active && current != null) current.Register(value is GameObject go ? go : (value as Component)?.gameObject); } catch { }
    }
    internal static void ObserveNativeSpawn(GameObject value) => Spawned(value);
    private void Register(GameObject? go)
    {
        if (!go || !go!.scene.IsValid() || NativeEffectResources.Ours(go.transform)) return;
        // Schema 12's placed antizooka projectile records the complete passive
        // native model and appearance. Keep legacy sphere resources/playback,
        // but never record a second copy of that same mesh in the effect track.
        if (go.TryGetComponent<Peak.AntiSphere>(out _) && NativePlacementResources.Match(go)?.Kind != "anti-sphere" &&
            !entries.ContainsKey(go.GetInstanceID()) && entries.Count < 2048)
            entries[go.GetInstanceID()] = new Entry { Root = go, Kind = 2, Resource = NativeEffectResources.Sphere(go), Key = "sphere:" + go.GetInstanceID() };
        if (go.TryGetComponent<ExplosionEffect>(out var explosion) && explosion.explosionOrb) NativeEffectResources.Mesh(explosion.explosionOrb);
        foreach (var p in go.GetComponentsInChildren<ParticleSystem>(true))
        {
            if (!NativeEffectResources.TopParticle(p) || !NativeEffectResources.Relevant(p) || entries.ContainsKey(p.GetInstanceID())) continue;
            var systems = p.GetComponentsInChildren<ParticleSystem>(true);
            if (systems.Length > 64) { Warn("systems", "特效子粒子系统超过64个，未录制该组。"); continue; }
            if (entries.Count >= 2048) { Warn("directory", "特效登记达到2048个上限。"); return; }
            string resource = NativeEffectResources.Particle(p);
            if (resource.Length > 2048) { Warn("resource-path", "特效资源路径超过上限，该特效未录制。"); continue; }
            entries[p.GetInstanceID()] = new Entry { Root = p.gameObject, Particle = p, Systems = systems,
                Resource = resource, Key = "particle:" + p.GetInstanceID() };
        }
        var animator = go.GetComponentInChildren<Animator>(true);
        if (animator && go.GetComponentInChildren<MeshRenderer>(true) && !go.GetComponent<Item>() && !go.GetComponent<Character>())
        {
            string resource = NativeEffectResources.MeshKey(go);
            // Only explicitly catalogued explosion-orb prefabs; a RocketFX hook
            // must not accidentally record a second copy of the whole rocket.
            if (resource.Length != 0 && NativeEffectResources.Find(resource) != null && entries.Count < 2048)
                entries[go.GetInstanceID()] = new Entry { Root = go, Kind = 1, Animator = animator, Mesh = go.GetComponentInChildren<MeshRenderer>(true),
                    Resource = resource, Key = "mesh:" + go.GetInstanceID() };
        }
    }
    private void Scan()
    {
        foreach (var p in NativeEffectResources.Refresh())
            if (p && p.gameObject.scene.IsValid() && p.gameObject.scene.isLoaded && NativeEffectResources.Relevant(p)) Register(p.gameObject);
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
            if (!e.Root) { removed.Add(pair.Key); continue; }
            if (!e.Root.activeInHierarchy) { e.Last = null; continue; }
            if (now < e.NextRetry) continue;
            try
            {
                EffectReplayFrame? sample = Sample(e, now);
                if (sample != null)
                {
                    if (frames.Count >= EffectReplayRules.Maximum) { Warn("limit", "活跃特效超过512组；超出部分未录制。"); break; }
                    frames.Add(sample);
                }
            }
            catch (Exception error) { e.NextRetry = now + 1; Warn(e.Resource, "原生特效采样不完整：" + error.Message); }
        }
        foreach (int id in removed) entries.Remove(id);
        bool same = frames.Count == previous.Length;
        if (same) for (int i = 0; i < frames.Count; i++) if (!ReferenceEquals(frames[i], previous[i])) { same = false; break; }
        return same ? previous : previous = frames.ToArray();
    }
    private static EffectReplayFrame? Sample(Entry e, double now)
    {
        var old = e.Last; double phase; float rate; bool loop, emitting, paused; long seed = 0;
        string clip = ""; float shaderRandom = 0; long[] seeds = old?.Seeds ?? Array.Empty<long>();
        if (e.Particle)
        {
            var p = e.Particle!; if (!p.IsAlive(true) && !p.isPlaying) { e.Last = null; return null; }
            var main = p.main; phase = p.time; rate = main.simulationSpeed; loop = main.loop; paused = p.isPaused;
            emitting = p.isEmitting || paused && old?.Emitting == true; seed = p.randomSeed;
            // ParticleSystem.time may wrap a looping duration; keep the recorded
            // clock unwrapped while the same seeded emission run is active.
            if (old != null && (loop || !emitting) && old.Seed == seed && !(emitting && !old.Emitting)) phase = EffectReplayRules.PhaseAt(old, now);
            bool same = seeds.Length == e.Systems.Length;
            if (same) for (int i = 0; i < seeds.Length; i++) if (!e.Systems[i] || seeds[i] != e.Systems[i].randomSeed) { same = false; break; }
            if (!same) seeds = e.Systems.Select(particle => particle ? (long)particle.randomSeed : 0).ToArray();
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
        var t = e.Root.transform; Vector3 position = t.position, scale = t.lossyScale; Quaternion rotation = t.rotation;
        if (!restart && old != null && Vec(old.Position, position) && Quat(old.Rotation, rotation) && Vec(old.Scale, scale) && old.ShaderRandom == shaderRandom) return old;
        return e.Last = new EffectReplayFrame
        {
            Key = e.Key, Resource = e.Resource, Kind = e.Kind,
            Position = new[] { position.x, position.y, position.z }, Rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w }, Scale = new[] { scale.x, scale.y, scale.z },
            AnchorTime = restart ? now : old!.AnchorTime, Phase = restart ? phase : old!.Phase, Rate = rate, Loop = loop,
            Emitting = emitting, EmissionStopPhase = emitting ? -1 : old != null && !old.Emitting ? old.EmissionStopPhase : phase,
            Paused = paused, Seed = seed, Seeds = seeds, Clip = clip, ShaderRandom = shaderRandom,
        };
    }
    private static bool Vec(float[] values, Vector3 v) => values[0] == v.x && values[1] == v.y && values[2] == v.z;
    private static bool Quat(float[] values, Quaternion q) => values[0] == q.x && values[1] == q.y && values[2] == q.z && values[3] == q.w;
    private void Warn(string key, string message) { if (warned.Count < 256 && warned.Add(key)) warning?.Invoke(message); }
    public void Dispose() { if (disposed) return; disposed = true; if (ReferenceEquals(current, this)) current = null; harmony.UnpatchSelf(); entries.Clear(); }
}

internal sealed class NativeParticleReplica : IDisposable
{
    public readonly GameObject Root;
    public readonly ParticleSystem[] Systems;
    public readonly List<string> Limitations = new();
    private readonly Dictionary<Transform, Transform> transforms = new();
    private readonly Dictionary<ParticleSystem, ParticleSystem> systems = new();
    private ParticleSystemRenderer[] renderers = Array.Empty<ParticleSystemRenderer>();
    private float[] relativeRates = Array.Empty<float>();
    private bool emitting;
    public NativeParticleReplica(GameObject source)
    {
        Root = new GameObject("PEAK Replay Effect - particles"); Root.SetActive(false);
        try
        {
            CopyTree(source.transform, Root.transform, 0);
            var original = source.GetComponentsInChildren<ParticleSystem>(true);
            if (original.Length == 0 || original.Length > 64) throw new InvalidOperationException("Unsupported particle hierarchy.");
            foreach (var p in original) systems.Add(p, transforms[p.transform].gameObject.AddComponent<ParticleSystem>());
            int budget = 0;
            foreach (var pair in systems)
            {
                CopyModules(pair.Key, pair.Value);
                var main = pair.Value.main; budget += main.maxParticles;
                if (budget > 16384) throw new InvalidOperationException("Native particle budget exceeds 16384 per group.");
                pair.Value.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                CopyRenderer(pair.Key.GetComponent<ParticleSystemRenderer>(), pair.Value.GetComponent<ParticleSystemRenderer>());
            }
            Systems = original.Select(p => systems[p]).ToArray();
            renderers = Systems.Select(p => p.GetComponent<ParticleSystemRenderer>()).ToArray();
            float rootRate = Math.Max(.0001f, original[0].main.simulationSpeed);
            relativeRates = original.Select(p => p.main.simulationSpeed / rootRate).ToArray();
            foreach (var pair in systems) BindSubEmitters(pair.Key, pair.Value);
            if (source.GetComponentInChildren<Animator>(true)) Limitations.Add("child particle Animator not replayed");
            foreach (var component in source.GetComponentsInChildren<MonoBehaviour>(true))
                if (component && component.GetType().Name != "TrackNetworkedObject")
                    Limitations.Add("custom particle behaviour not executed: " + component.GetType().Name);
        }
        catch { Dispose(); throw; }
    }
    private void CopyTree(Transform source, Transform target, int depth)
    {
        if (depth > 64 || transforms.Count >= 256) throw new InvalidOperationException("Particle hierarchy too large.");
        transforms.Add(source, target); target.localPosition = depth == 0 ? Vector3.zero : source.localPosition;
        target.localRotation = depth == 0 ? Quaternion.identity : source.localRotation; target.localScale = depth == 0 ? Vector3.one : source.localScale;
        if (depth != 0) target.gameObject.SetActive(source.gameObject.activeSelf);
        foreach (Transform child in source)
        { var go = new GameObject(child.name); go.transform.SetParent(target, false); CopyTree(child, go.transform, depth + 1); }
    }
    private void CopyModules(ParticleSystem source, ParticleSystem target)
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
            }
        }
        var main = target.main; main.playOnAwake = false; main.stopAction = ParticleSystemStopAction.None;
        main.useUnscaledTime = false; main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
        if (source.main.customSimulationSpace)
        {
            if (transforms.TryGetValue(source.main.customSimulationSpace, out var space)) main.customSimulationSpace = space;
            else { main.simulationSpace = ParticleSystemSimulationSpace.World; Limitations.Add("external custom simulation space"); }
        }
        var collision = target.collision; collision.enabled = false;
        var trigger = target.trigger; trigger.enabled = false;
        var external = target.externalForces; external.enabled = false;
        var lights = target.lights; lights.enabled = false;
        if (source.collision.enabled || source.trigger.enabled || source.externalForces.enabled || source.lights.enabled)
            Limitations.Add("physics/trigger/external force/light callbacks disabled");
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
            }
        var shape = target.shape;
        if (source.shape.meshRenderer || source.shape.skinnedMeshRenderer || source.shape.spriteRenderer)
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
        }
    }
    private void BindSubEmitters(ParticleSystem source, ParticleSystem target)
    {
        var output = target.subEmitters;
        for (int i = 0; i < source.subEmitters.subEmittersCount; i++)
        {
            var original = source.subEmitters.GetSubEmitterSystem(i);
            if (!original || !systems.TryGetValue(original, out var copy)) { Limitations.Add("external sub-emitter"); continue; }
            output.AddSubEmitter(copy, source.subEmitters.GetSubEmitterType(i), source.subEmitters.GetSubEmitterProperties(i), source.subEmitters.GetSubEmitterEmitProbability(i));
        }
    }
    private static void CopyRenderer(ParticleSystemRenderer original, ParticleSystemRenderer copy)
    {
        copy.sharedMaterials = original.sharedMaterials; copy.trailMaterial = original.trailMaterial;
        foreach (string name in new[] { "renderMode", "sortMode", "minParticleSize", "maxParticleSize", "cameraVelocityScale", "velocityScale", "lengthScale", "alignment", "pivot", "flip", "enableGPUInstancing", "normalDirection", "sortingFudge", "shadowCastingMode", "receiveShadows", "meshDistributionMode" })
        {
            var property = typeof(ParticleSystemRenderer).GetProperty(name);
            if (property?.CanWrite == true) property.SetValue(copy, property.GetValue(original));
        }
        var meshes = new Mesh[original.meshCount]; original.GetMeshes(meshes); if (meshes.Length > 0) copy.SetMeshes(meshes);
        var streams = new List<ParticleSystemVertexStream>(); original.GetActiveVertexStreams(streams); copy.SetActiveVertexStreams(streams);
        var block = new MaterialPropertyBlock(); original.GetPropertyBlock(block); copy.SetPropertyBlock(block);
        copy.enabled = original.enabled;
    }
    public void Restart(EffectReplayFrame state)
    {
        Root.SetActive(true);
        for (int i = 0; i < Systems.Length; i++)
        {
            var p = Systems[i]; p.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            p.useAutoRandomSeed = false; p.randomSeed = (uint)(i < state.Seeds.Length ? state.Seeds[i] : state.Seed);
            var main = p.main; main.simulationSpeed = relativeRates[i]; p.Play(false); p.Pause(false);
        }
        emitting = true;
    }
    public void Step(float delta, bool emitting)
    {
        if (this.emitting != emitting)
        {
            if (!emitting) Systems[0].Stop(true, ParticleSystemStopBehavior.StopEmitting);
            else Systems[0].Play(true);
            this.emitting = emitting;
        }
        Systems[0].Simulate(Math.Max(0, delta), true, false, false);
        Systems[0].Pause(true);
    }
    public void SetVisible(bool visible) { foreach (var renderer in renderers) if (renderer) renderer.forceRenderingOff = !visible; }
    public void Dispose() { if (Root) Object.Destroy(Root); }
}

internal sealed class NativeAnimatedEffectReplica : IDisposable
{
    public readonly VisualReplica Visual;
    private readonly PlayableGraph graph;
    private readonly AnimationClipPlayable player;
    private readonly MaterialPropertyBlock block = new();
    public GameObject Root => Visual.Root;
    public NativeAnimatedEffectReplica(GameObject source, string clipName)
    {
        Visual = new VisualReplica(source); Visual.Root.name = "PEAK Replay Effect - animated mesh";
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
        }
        catch { Dispose(); throw; }
    }
    public void Apply(EffectReplayFrame state, double time)
    {
        Root.SetActive(true); player.SetTime(EffectReplayRules.PhaseAt(state, time)); graph.Evaluate(0);
        block.SetFloat("_Random", state.ShaderRandom);
        foreach (var renderer in Visual.Renderers) if (renderer) renderer.SetPropertyBlock(block);
    }
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
        public double Time = double.NaN, Phase;
        public long Seed;
        public long[] Seeds = Array.Empty<long>();
        public bool Emitting;
        public bool Warming, Approximate;
        public double WarmPhase;
        public int Seen;
        public GameObject Root => Particle != null ? Particle.Root : Mesh != null ? Mesh.Root : Sphere!.Root;
        public void Dispose() { Particle?.Dispose(); Mesh?.Dispose(); Sphere?.Dispose(); }
    }
    private readonly Dictionary<string, Replica> replicas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EffectReplayFrame> following = new(StringComparer.Ordinal);
    private readonly PresentationResourceFailures failures = new();
    private readonly HashSet<string> warned = new(StringComparer.Ordinal);
    private readonly Dictionary<Renderer, bool> hidden = new();
    private readonly Action<string>? warning;
    private Func<double, EffectReplayFrame[]>? timeline;
    private int serial;
    private EffectReplayWorkBudget budget = new();
    private int budgetFrame = -1;
    private readonly List<KeyValuePair<string, Replica>> sleeping = new();
    private EffectReplayFrame[] lastLeft = Array.Empty<EffectReplayFrame>(), lastRight = Array.Empty<EffectReplayFrame>();
    private double lastTime;
    private float lastMix;
    private bool pending;
    public int MissingCount { get; private set; }
    public int VisibleCount { get; private set; }
    public EffectReplayPlayback(Action<string>? warning = null) { this.warning = warning; RefreshCatalog(); }
    public void SetTimeline(Func<double, EffectReplayFrame[]> sample) => timeline = sample;
    public void Enforce()
    {
        foreach (var pair in hidden) if (pair.Key && !pair.Key.forceRenderingOff) pair.Key.forceRenderingOff = true;
        // Paused replay still calls Enforce. Finish a bounded seek warm-up without
        // advancing its replay clock, and without requiring the user to press Play.
        if (pending) Apply(lastLeft, lastRight, lastMix, lastTime, false);
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
    public void Apply(EffectReplayFrame[] left, EffectReplayFrame[] right, float mix, double time, bool discontinuity)
    {
        lastLeft = left; lastRight = right; lastTime = time; lastMix = mix; pending = false;
        if (budgetFrame != Time.frameCount) { budget = new EffectReplayWorkBudget(); budgetFrame = Time.frameCount; }
        serial++; MissingCount = 0; VisibleCount = 0; following.Clear();
        foreach (var frame in right) following[frame.Key] = frame;
        foreach (var frame in left)
        {
            string failureKey = frame.Resource + "|" + frame.Clip;
            if (!failures.Allows(failureKey))
            {
                if (replicas.TryGetValue(frame.Key, out var waiting)) { waiting.Seen = serial; waiting.Root.SetActive(false); }
                MissingCount++; continue;
            }
            if (replicas.TryGetValue(frame.Key, out var obsolete) && (obsolete.Resource != frame.Resource || obsolete.Clip != frame.Clip))
            { obsolete.Dispose(); replicas.Remove(frame.Key); }
            if (!replicas.TryGetValue(frame.Key, out var replica))
            {
                if (!budget.Create()) { pending = true; MissingCount++; continue; }
                try
                {
                    var resource = NativeEffectResources.Find(frame.Resource);
                    if (resource == null || resource.Kind != frame.Kind) throw new InvalidOperationException("Native effect resource unavailable: " + frame.Resource);
                    if (replicas.Count >= EffectReplayRules.Maximum + 32) throw new InvalidOperationException("Effect replica pool reached its bounded capacity.");
                    replica = new Replica { Resource = frame.Resource, Clip = frame.Clip };
                    if (frame.Kind == 0)
                    {
                        replica.Particle = new NativeParticleReplica(resource.Source);
                        if (replica.Particle.Limitations.Count > 0)
                            Warn(frame.Resource, "原生特效存在安全回放限制（非完整复制）: " + string.Join(", ", replica.Particle.Limitations.Distinct()));
                    }
                    else if (frame.Kind == 1) replica.Mesh = new NativeAnimatedEffectReplica(resource.Source, frame.Clip);
                    else { replica.Sphere = new VisualReplica(resource.Source); replica.Sphere.Root.name = "PEAK Replay Effect - antigravity sphere"; }
                    replicas.Add(frame.Key, replica);
                }
                catch (Exception error) { MissingCount++; failures.Reject(failureKey); Warn(frame.Resource, "特效资源已隔离（本次播放不重复构建）: " + error.Message); continue; }
            }
            replica.Seen = serial;
            try
            {
                following.TryGetValue(frame.Key, out var next);
                bool reset = discontinuity || !replica.Root.activeSelf || !replica.Warming && (double.IsNaN(replica.Time) || time < replica.Time || time - replica.Time > .25) || replica.Seed != frame.Seed ||
                    !ReferenceEquals(replica.Seeds, frame.Seeds) || frame.Emitting && !replica.Emitting || EffectReplayRules.PhaseAt(frame, time) + .05 < replica.Phase;
                if (replica.Particle != null)
                {
                    if (reset) StartPreroll(replica, frame, time);
                    if (replica.Warming && !ContinuePreroll(replica, frame, time))
                    {
                        pending = true; MissingCount++; replica.Time = time;
                        replica.Seed = frame.Seed; replica.Seeds = frame.Seeds; replica.Emitting = frame.Emitting;
                        Warn("seek-budget", "特效正在按全局预算分帧预滚；完成前暂不显示，避免拖动时间轴卡住。"); continue;
                    }
                    else if (!reset)
                    {
                        SetPose(replica.Root.transform, frame, next, mix);
                        double phase = EffectReplayRules.PhaseAt(frame, time);
                        replica.Particle.Step((float)Math.Max(0, phase - replica.Phase), frame.Emitting);
                        replica.Phase = phase;
                    }
                    if (replica.Particle.Limitations.Count > 0 || replica.Approximate) MissingCount++;
                }
                else { if (replica.Mesh != null) replica.Mesh.Apply(frame, time); else replica.Root.SetActive(true); SetPose(replica.Root.transform, frame, next, mix); }
                replica.Time = time; replica.Seed = frame.Seed; replica.Seeds = frame.Seeds; replica.Emitting = frame.Emitting; VisibleCount++;
            }
            catch (Exception error) { replica.Root.SetActive(false); MissingCount++; failures.Reject(failureKey); Warn(frame.Resource, "特效回放已隔离: " + error.Message); }
        }
        sleeping.Clear();
        foreach (var pair in replicas) if (pair.Value.Seen != serial) { pair.Value.Root.SetActive(false); sleeping.Add(pair); }
        foreach (var pair in sleeping.OrderBy(p => p.Value.Seen).Take(Math.Max(0, sleeping.Count - 32)))
        { pair.Value.Dispose(); replicas.Remove(pair.Key); }
    }
    private void StartPreroll(Replica replica, EffectReplayFrame current, double time)
    {
        var particle = replica.Particle!; SetPose(particle.Root.transform, current, null, 0); particle.Restart(current);
        double phase = EffectReplayRules.PhaseAt(current, time);
        double limit = 12 * Math.Max(current.Rate, 1);
        replica.WarmPhase = Math.Max(0, phase - limit); replica.Warming = true; particle.SetVisible(false);
        replica.Approximate = replica.WarmPhase > 0 || EffectReplayRules.TimeAtPhase(current, 0) < 0;
        if (replica.WarmPhase > 0) Warn("preroll:" + current.Resource, "长特效预滚限制为12秒；未记录逐粒子，长寿命烟迹可能近似。" );
        if (EffectReplayRules.TimeAtPhase(current, 0) < 0) Warn("baseline:" + current.Resource, "录像开始前已存在的特效按原生时钟重建；窗口前发射器轨迹无法恢复。" );
    }
    private bool ContinuePreroll(Replica replica, EffectReplayFrame current, double time)
    {
        var particle = replica.Particle!; double phase = EffectReplayRules.PhaseAt(current, time);
        int localSteps = 0;
        while (replica.WarmPhase < phase - .00001)
        {
            if (localSteps++ >= 64 || !budget.Step()) return false;
            double next = Math.Min(phase, replica.WarmPhase + Math.Max(current.Rate, 1) / 30.0);
            double sampleTime = EffectReplayRules.TimeAtPhase(current, replica.WarmPhase);
            var state = timeline?.Invoke(Math.Max(0, sampleTime)).FirstOrDefault(e => e.Key == current.Key) ?? current;
            SetPose(particle.Root.transform, state, null, 0);
            bool emitting = current.Emitting || current.EmissionStopPhase >= 0 && replica.WarmPhase < current.EmissionStopPhase;
            particle.Step((float)(next - replica.WarmPhase), emitting);
            replica.WarmPhase = next;
        }
        particle.Step(0, current.Emitting); particle.SetVisible(true);
        SetPose(particle.Root.transform, current, null, 0); replica.Phase = phase; replica.Warming = false; return true;
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
        hidden.Clear(); replicas.Clear();
        pending = false; lastLeft = lastRight = Array.Empty<EffectReplayFrame>(); timeline = null;
    }
}
