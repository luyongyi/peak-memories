using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

internal static class NativePlacementResources
{
    // ResourceManager mappings verified against installed PEAK build 25667990.
    // These are placed entities without Item; preview/debug prefabs are excluded.
    internal static readonly SpawnedReplayResources.Resource[] All =
    {
        new() { Path = "0_items/climbingspikehammered", Name = "ClimbingSpikeHammered", Kind = "piton" },
        new() { Path = "0_items/climbingspikehammered_shitty", Name = "ClimbingSpikeHammered_Shitty", Kind = "fragile-piton" },
        new() { Path = "0_items/climbingspikehammered_shitty hand", Name = "ClimbingSpikeHammered_Shitty hand", Kind = "fragile-piton-hand" },
        new() { Path = "flag_planted_checkpoint", Name = "Flag_Planted_Checkpoint", Kind = "checkpoint-flag" },
        new() { Path = "portablestovetop_placed", Name = "PortableStovetop_Placed", Kind = "portable-stove" },
        new() { Path = "magicbeanvine", Name = "MagicBeanVine", Kind = "magic-bean-vine" },
        new() { Path = "0_items/cloudfungusplaced", Name = "CloudFungusPlaced", Kind = "cloud-fungus" },
        new() { Path = "antisphere_projectile", Name = "AntiSphere_Projectile", Kind = "anti-sphere" },
    };
    internal static SpawnedReplayResources.Resource? Find(string path)
    {
        foreach (var resource in All) if (resource.Path == path)
        {
            if (!resource.Source && Time.realtimeSinceStartupAsDouble >= resource.RetryAfter)
            { resource.RetryAfter = Time.realtimeSinceStartupAsDouble + 1; resource.Source = Resources.Load<GameObject>(resource.Path); }
            return resource;
        }
        return null;
    }
    internal static SpawnedReplayResources.Resource? Match(GameObject source)
    {
        if (!source || source.GetComponent<Item>()) return null;
        string name = source.name.Replace("(Clone)", "").Trim();
        foreach (var resource in All) if (resource.Name == name) return resource;
        return null;
    }
    internal static IEnumerable<GameObject> InitialSources()
    {
        foreach (var source in Object.FindObjectsByType<ClimbHandle>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (source) yield return source.gameObject;
        foreach (var source in Object.FindObjectsByType<CheckpointFlag>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (source) yield return source.gameObject;
        foreach (var source in Object.FindObjectsByType<Campfire>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (source) yield return source.gameObject;
        foreach (var source in Object.FindObjectsByType<MagicBeanVine>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (source) yield return source.gameObject;
        foreach (var source in Object.FindObjectsByType<AddScreenshake>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (source && Match(source.gameObject) != null) yield return source.gameObject;
        foreach (var source in Object.FindObjectsByType<Peak.AntiSphere>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (source) yield return source.gameObject;
    }
    private static readonly AccessTools.FieldRef<ShittyPiton, bool> pitonBreaking = AccessTools.FieldRefAccess<ShittyPiton, bool>("isBreaking");
    private sealed class AnimationComponents
    {
        public MagicBeanVine? Vine;
        public ShittyPiton? Piton;
        public Campfire? Stove;
        public CheckpointFlag? Flag;
        public bool AntiSphere;
        public AnimationComponents(GameObject source)
        {
            Vine = source.GetComponent<MagicBeanVine>(); Piton = source.GetComponent<ShittyPiton>();
            Stove = source.GetComponent<Campfire>(); Flag = source.GetComponent<CheckpointFlag>();
            AntiSphere = source.GetComponent<Peak.AntiSphere>();
        }
    }
    private static readonly ConditionalWeakTable<GameObject, AnimationComponents> animations = new();
    internal static void InvalidateCapture(GameObject source) { if (source) animations.Remove(source); }
    internal static bool IsAnimating(GameObject source)
    {
        if (!source || !source.activeInHierarchy) return false;
        var components = animations.GetValue(source, value => new AnimationComponents(value));
        if (components.Vine) return components.Vine!.vineOriginTransform && components.Vine.vineOriginTransform.localScale.y < components.Vine.maxLength;
        if (components.Piton) return pitonBreaking(components.Piton!);
        if (components.Stove) return components.Stove!.Lit;
        if (components.Flag) return components.Flag!.anim && components.Flag.anim.enabled;
        if (components.AntiSphere) return true; // Native growth/lifetime shrink, at most 12 seconds.
        return false;
    }
}

// Supplement PhotonCleanupHelper with native component events. In particular,
// Constructable may use Object.Instantiate when its target has no PhotonView.
// Observing the completed component's Awake/Start avoids intercepting all Unity
// allocations or scanning the scene on every construction/frame.
internal sealed class NativePlacementObserver : IDisposable
{
    private static readonly HashSet<NativePlacementObserver> listeners = new();
    private static Harmony? harmony;
    private readonly Action<GameObject, bool> birth;
    private readonly Action<GameObject> destruction;
    private readonly Action<GameObject> changed;
    private bool disposed;
    internal NativePlacementObserver(Action<GameObject, bool> birth, Action<GameObject> destruction, Action<GameObject> changed)
    {
        this.birth = birth; this.destruction = destruction; this.changed = changed;
        if (listeners.Count == 0)
        {
            var install = new Harmony("cn.mylus.peakreplaylab.placement." + Guid.NewGuid().ToString("N"));
            try
            {
                Patch(typeof(ClimbHandle), "Start", nameof(Born));
                Patch(typeof(Campfire), "Awake", nameof(Born));
                Patch(typeof(Campfire), "OnDestroy", nameof(Destroyed), true);
                Patch(typeof(AddScreenshake), "Start", nameof(Born));
                Patch(typeof(Peak.GrowOverTime), "Awake", nameof(Born));
                Patch(typeof(Peak.AntiSphere), "Die", nameof(Changed));
                Patch(typeof(CheckpointFlag), "Initialize", nameof(Changed));
                Patch(typeof(CheckpointFlag), "SetColor", nameof(Changed));
                Patch(typeof(MagicBeanVine), "RPC_GrowVine", nameof(Changed));
                Patch(typeof(ClimbHandle), "Break", nameof(Changed));
                Patch(typeof(ShittyPiton), "RPCA_StartBreaking", nameof(Changed));
                Patch(typeof(ShittyPiton), "RPCA_Break", nameof(HierarchyChanged));
                harmony = install;
                void Patch(Type type, string method, string callback, bool prefix = false)
                {
                    var original = AccessTools.DeclaredMethod(type, method) ?? throw new MissingMethodException(type.FullName, method);
                    var hook = new HarmonyMethod(typeof(NativePlacementObserver), callback);
                    install.Patch(original, prefix: prefix ? hook : null, postfix: prefix ? null : hook);
                }
            }
            catch { install.UnpatchSelf(); throw; }
        }
        listeners.Add(this);
    }
    private static void Born(Component __instance)
    {
        if (!__instance || NativePlacementResources.Match(__instance.gameObject) == null) return;
        foreach (var listener in listeners) try { listener.birth(__instance.gameObject, true); } catch { }
    }
    private static void Changed(Component __instance)
    {
        if (!__instance || NativePlacementResources.Match(__instance.gameObject) == null) return;
        foreach (var listener in listeners) try { listener.birth(__instance.gameObject, false); listener.changed(__instance.gameObject); } catch { }
    }
    private static void HierarchyChanged(Component __instance)
    {
        if (__instance) { VisualReplica.InvalidateCapture(__instance.transform, true); Changed(__instance); }
    }
    private static void Destroyed(Component __instance)
    {
        if (!__instance) return;
        foreach (var listener in listeners) try { listener.destruction(__instance.gameObject); } catch { }
    }
    public void Dispose()
    { if (disposed) return; disposed = true; listeners.Remove(this); if (listeners.Count == 0) { harmony?.UnpatchSelf(); harmony = null; } }
}
