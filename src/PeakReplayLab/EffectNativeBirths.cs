using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

// Only native references from audited game entry points enter this directory.
// Recording files cannot cause arbitrary Resource.Load or gameplay Instantiate.
internal static class EffectNativeBirths
{
    private sealed class Group
    {
        internal int[] Path = Array.Empty<int>();
        internal string Resource = "";
    }
    private sealed class Template
    {
        internal GameObject Source = null!;
        internal Group[] Groups = Array.Empty<Group>();
    }
    private static readonly Dictionary<int, Template> templates = new();
    private static readonly Dictionary<int, ParticleSystem> observed = new();
    private static readonly AccessTools.FieldRef<SwingingAxe, ParticleSystem> axeImpact =
        AccessTools.FieldRefAccess<SwingingAxe, ParticleSystem>("impactVfx");
    internal static ParticleSystem AxeImpact(SwingingAxe owner) => axeImpact(owner);
    internal static bool Known(ParticleSystem particle) => observed.TryGetValue(particle.GetInstanceID(), out var value) && value == particle;
    internal static void MarkReference(ParticleSystem particle) => Remember(particle);
    private static void Remember(ParticleSystem particle)
    {
        if (observed.Count >= 4096)
            foreach (int id in observed.Where(pair => !pair.Value).Select(pair => pair.Key).ToArray()) observed.Remove(id);
        if (observed.Count < 4096) observed[particle.GetInstanceID()] = particle;
    }
    internal static GameObject? GameObject(Object? value) => value is GameObject go ? go : (value as Component)?.gameObject;
    private static int[] Path(Transform child, Transform root)
    {
        var path = new Stack<int>();
        while (child && child != root) { path.Push(child.GetSiblingIndex()); child = child.parent; }
        return path.ToArray();
    }
    private static Template? Catalogue(Object? value)
    {
        var root = GameObject(value); if (!root || NativeEffectResources.Ours(root!.transform)) return null;
        int id = root.GetInstanceID();
        if (templates.TryGetValue(id, out var known) && known.Source == root) return known;
        if (templates.Count >= 4096) return null;
        var groups = new List<Group>();
        foreach (var p in root.GetComponentsInChildren<ParticleSystem>(true))
        {
            bool top = true;
            for (var t = p.transform.parent; t && t.IsChildOf(root.transform); t = t.parent)
                if (t.GetComponent<ParticleSystem>()) { top = false; break; }
            if (top) { Remember(p); groups.Add(new Group { Path = Path(p.transform, root.transform), Resource = NativeEffectResources.Particle(p) }); }
        }
        var result = new Template { Source = root, Groups = groups.ToArray() }; templates[id] = result; return result;
    }
    internal static Dictionary<int, string>? Bind(Object original, GameObject instance)
    {
        var template = Catalogue(original); if (template == null || template.Groups.Length == 0) return null;
        var result = new Dictionary<int, string>();
        foreach (var group in template.Groups)
        {
            Transform? node = instance.transform;
            foreach (int index in group.Path)
            { if (!node || index >= node!.childCount) { node = null; break; } node = node.GetChild(index); }
            if (node && node!.TryGetComponent<ParticleSystem>(out var particle)) { Remember(particle); result[particle.GetInstanceID()] = group.Resource; }
        }
        return result;
    }
    internal static void Refresh()
    {
        // One scene-entry pass. Keep referenced prefabs after short lived instances
        // die; drop destroyed scene templates before cataloguing the next scene.
        foreach (int id in templates.Where(pair => !pair.Value.Source).Select(pair => pair.Key).ToArray()) templates.Remove(id);
        foreach (int id in observed.Where(pair => !pair.Value).Select(pair => pair.Key).ToArray()) observed.Remove(id);
        References<Flare>(x => x.flareVFXPrefab);
        References<ItemCooking>(x => x.explosionPrefab);
        References<CharacterBalloons>(x => x.popParticle);
        References<Action_RaycastDart>(x => x.dartVFX);
        References<Peak.Action_CloneSelectedItem>(x => x.particle, x => x.particleFail);
        References<Action_Spawn>(x => x.objectToSpawn);
        References<ArrowShooter>(x => x.trailParticles, x => x.firedParticles, x => x.emptyParticles);
        References<Beehive>(x => x.beeSwarmPrefab);
        References<WaterZone>(x => x.waterVFX);
        References<SwingingAxe>(x => AxeImpact(x));
        References<OrbThatMakesYouSleepy>(x => x.particle, x => x.ambientParticles);
        foreach (var owner in Resources.FindObjectsOfTypeAll<ParticleCuller>())
            if (owner && !NativeEffectResources.Ours(owner.transform) && owner.systems != null)
                foreach (var particle in owner.systems) Catalogue(particle);
        References<LandImpactAudio>(x => x.impactGiant);
        References<EruptionSpawner>(x => x.eruption);
        References<TrackableNetworkObject>(x => x.trackerToSpawn);
        References<SpawnGameObject>(x => x.toSpawn);
        References<Dynamite>(x => x.explosionPrefab, x => x.smokeVFXPrefab);
        References<Rocketpack>(x => x.explosionPrefab);
        References<Peak.GhostBall>(x => x.explosionPrefab);
        References<CharacterMovement>(x => x.explosionPrefab);
        References<Peak.Action_SuperJumpAmulet>(x => x.vfxPrefab);
    }
    private static void References<T>(params Func<T, Object?>[] references) where T : Component
    {
        foreach (var owner in Resources.FindObjectsOfTypeAll<T>())
            if (owner && !NativeEffectResources.Ours(owner.transform)) foreach (var reference in references) Catalogue(reference(owner));
    }
    internal static MethodInfo? Wrapper(MethodInfo original)
    {
        if (original.DeclaringType != typeof(Object) || original.Name != nameof(Object.Instantiate) || !typeof(Object).IsAssignableFrom(original.ReturnType)) return null;
        var p = original.GetParameters(); string? name = null;
        if (p.Length == 1) name = nameof(Clone);
        else if (p.Length == 2 && p[1].ParameterType == typeof(Transform)) name = nameof(Parent);
        else if (p.Length == 3 && p[1].ParameterType == typeof(Transform) && p[2].ParameterType == typeof(bool)) name = nameof(ParentWorld);
        else if (p.Length == 3 && p[1].ParameterType == typeof(Vector3) && p[2].ParameterType == typeof(Quaternion)) name = nameof(Position);
        else if (p.Length == 4 && p[1].ParameterType == typeof(Vector3) && p[2].ParameterType == typeof(Quaternion) && p[3].ParameterType == typeof(Transform)) name = nameof(PositionParent);
        return name == null ? null : AccessTools.DeclaredMethod(typeof(EffectNativeBirths), name).MakeGenericMethod(original.ReturnType);
    }
    private static T Observe<T>(T original, T result) where T : Object
    { EffectReplayCapture.ObserveNativeSpawn(original, result); return result; }
    private static T Clone<T>(T original) where T : Object => Observe(original, Object.Instantiate(original));
    private static T Parent<T>(T original, Transform parent) where T : Object => Observe(original, Object.Instantiate(original, parent));
    private static T ParentWorld<T>(T original, Transform parent, bool worldPositionStays) where T : Object => Observe(original, Object.Instantiate(original, parent, worldPositionStays));
    private static T Position<T>(T original, Vector3 position, Quaternion rotation) where T : Object => Observe(original, Object.Instantiate(original, position, rotation));
    private static T PositionParent<T>(T original, Vector3 position, Quaternion rotation, Transform parent) where T : Object => Observe(original, Object.Instantiate(original, position, rotation, parent));
}
