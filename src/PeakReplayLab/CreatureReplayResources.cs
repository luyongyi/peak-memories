using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakReplayLab;

// Fixed native types and prefab references only. Recorded strings select this
// directory; they never execute gameplay, load an arbitrary asset or find an
// arbitrary scene transform. Discovery runs once when a capture/player opens.
internal static class CreatureReplayResources
{
    private static readonly Dictionary<string, GameObject> prefabs = new(StringComparer.Ordinal);
    private static readonly Dictionary<(string Kind, string Path), GameObject> sceneSources = new();
    private sealed class Signature { public bool Valid; }
    private static readonly ConditionalWeakTable<Transform, Signature> signatures = new();
    private static readonly string[] preloadNames =
    { "Beetle", "Character_Scoutmaster", "tumbleweed", "RavenVoiceThief", "Skeleton", "ScoutmasterGhostOrbiter",
      "MushroomZombie", "MushroomZombie_Player", "Frog", "GhostBall", "PlayerGhost" };
    private static bool preloaded;

    internal static bool Ours(Transform source)
    { for (var t = source; t; t = t.parent) if (t.name.StartsWith("PEAK Replay Creature", StringComparison.Ordinal)) return true; return false; }
    internal static bool IsTrapName(string value) => CreatureDirectoryRules.TrapEffect(value).Length != 0;
    internal static Transform? Trap(Transform source)
    {
        for (var t = source; t; t = t.parent) if (IsTrapName(t.name) && VerifiedTrap(t)) return t;
        return null;
    }
    private static bool VerifiedTrap(Transform root) => signatures.GetValue(root, source =>
    {
        var trigger = source.GetComponent<TriggerEvent>(); var spawn = source.GetComponent<SpawnGameObject>();
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

    // Registration is a lifecycle operation, never a per-frame type search.
    internal static string Kind(GameObject source)
    {
        if (!source || Ours(source.transform)) return "";
        if (source.GetComponent<Spider>()) return "spider";
        if (source.GetComponent<MushroomZombie>()) return "mushroom-zombie";
        if (source.GetComponent<Condor>()) return "condor";
        if (source.GetComponent<FrogTongue>()) return "frog-tongue";
        if (source.GetComponent<Beetle>()) return "beetle";
        if (source.GetComponent<Scoutmaster>()) return "scoutmaster";
        if (source.GetComponent<Antlion>()) return "antlion";
        if (source.GetComponent<VenusFlyTrap>()) return "venus-flytrap";
        if (source.GetComponent<TumbleWeed>()) return "tumbleweed";
        if (source.GetComponent<Peak.Island.VoiceThief>()) return "voice-thief";
        if (source.GetComponent<Skelleton>()) return "skeleton";
        if (source.GetComponent<Peak.ScoutmasterGhostOrbiter>()) return "scoutmaster-ghost";
        if (source.GetComponent<Peak.GhostBall>()) return "ghost-ball";
        if (source.GetComponent<PlayerGhost>()) return "player-ghost";
        if (source.GetComponent<Capybara>() || source.GetComponent<MirageLuggage>() && CreatureDirectoryRules.IsCapybaraName(source.name)) return "capybara";
        return Trap(source.transform) == source.transform ? "spore-trap" : "";
    }
    internal static string Resource(GameObject source, string kind, MushroomZombie? zombie = null)
    {
        if (kind == "spider") return CreatureDirectoryRules.IsWebSpiderName(source.name) ? "spider-web" : "spider-normal";
        if (kind == "mushroom-zombie")
        {
            if (!zombie) zombie = source.GetComponent<MushroomZombie>();
            return zombie && !zombie!.isNPCZombie ? "mushroom-zombie-player" : "mushroom-zombie-npc";
        }
        return kind;
    }
    internal static string SourcePath(GameObject source, string kind) =>
        CreatureDirectoryRules.RequiresScenePath(kind) ? WorldTrack.Path(source.transform) : "";

    internal static void Register(GameObject source, string kind, string? resource = null)
    {
        if (!source || Ours(source.transform) || !CreatureDirectoryRules.IsKnownKind(kind)) return;
        resource ??= Resource(source, kind);
        if (!CreatureDirectoryRules.ValidResource(kind, resource)) return;
        if (CreatureDirectoryRules.RequiresScenePath(kind))
        {
            if (source.scene.handle != SceneManager.GetActiveScene().handle || kind == "spore-trap" && !VerifiedTrap(source.transform)) return;
            var key = (kind, WorldTrack.Path(source.transform));
            if (sceneSources.ContainsKey(key) || sceneSources.Count < CreatureReplayRules.MaximumStaticTraps + CreatureReplayRules.MaximumDynamicEntities)
                sceneSources[key] = source;
            return;
        }
        Add(resource, source);
        // Legacy files did not distinguish zombie variants or web-spider models.
        // Keep their historical resource valid without using that fallback for
        // new records, whose explicit Resource participates in pool identity.
        if (resource != kind && (!prefabs.TryGetValue(kind, out var legacy) || !legacy)) Add(kind, source);
    }
    private static void Add(string resource, GameObject source)
    {
        if (!prefabs.TryGetValue(resource, out var old) || !old || old.scene.IsValid() && !source.scene.IsValid()) prefabs[resource] = source;
    }
    internal static GameObject? Find(string kind, string sourcePath) => Find(kind, kind, sourcePath);
    internal static GameObject? Find(string kind, string resource, string sourcePath)
    {
        if (!CreatureDirectoryRules.ValidResource(kind, resource)) return null;
        if (CreatureDirectoryRules.RequiresScenePath(kind))
            return sceneSources.TryGetValue((kind, sourcePath), out var scene) && scene ? scene : null;
        return prefabs.TryGetValue(resource, out var source) && source ? source : null;
    }
    internal static void Refresh()
    {
        sceneSources.Clear();
        if (!preloaded)
        {
            // Names audited from Resources prefabs/native Instantiate callers.
            // Load dependencies only: never Instantiate/Awake/RPC during replay.
            foreach (string name in preloadNames)
            {
                var source = Resources.Load<GameObject>(name); if (!source) continue;
                string kind = Kind(source); if (kind.Length == 0) continue;
                string? resource = name == "MushroomZombie_Player" ? "mushroom-zombie-player" :
                    name == "MushroomZombie" ? "mushroom-zombie-npc" : null;
                Register(source, kind, resource);
            }
            preloaded = true;
        }
        VisitNative(RegisterObserved, true);
        foreach (var spawner in Resources.FindObjectsOfTypeAll<MushroomZombieSpawner>())
            if (spawner && spawner.mushroomZombiePrefab) Register(spawner.mushroomZombiePrefab.gameObject, "mushroom-zombie", "mushroom-zombie-npc");
        // Current GhostBallSpawner uses this typed reference's prefab name in
        // PhotonNetwork.Instantiate. Keep the actual reference as a fallback
        // to the independently verified fixed Resources directory entry.
        foreach (var spawner in Resources.FindObjectsOfTypeAll<Peak.GhostBallSpawner>())
            if (spawner && spawner.ghostBallPrefab) Register(spawner.ghostBallPrefab.gameObject, "ghost-ball");
    }
    private static void RegisterObserved(GameObject source)
    { string kind = Kind(source); if (kind.Length != 0) Register(source, kind); }
    internal static void VisitScene(Action<GameObject> visit) => VisitNative(visit, false);
    private static void VisitNative(Action<GameObject> visit, bool assets)
    {
        Visit<Spider>(visit, assets); Visit<MushroomZombie>(visit, assets); Visit<Condor>(visit, assets); Visit<FrogTongue>(visit, assets);
        Visit<Beetle>(visit, assets); Visit<Scoutmaster>(visit, assets); Visit<Antlion>(visit, assets); Visit<VenusFlyTrap>(visit, assets);
        Visit<TumbleWeed>(visit, assets); Visit<Peak.Island.VoiceThief>(visit, assets); Visit<Skelleton>(visit, assets);
        Visit<Peak.ScoutmasterGhostOrbiter>(visit, assets); Visit<Capybara>(visit, assets); Visit<MirageLuggage>(visit, assets);
        Visit<Peak.GhostBall>(visit, assets); Visit<PlayerGhost>(visit, assets);
        // TriggerEvent is a scene-level fixed catalogue; births after this pass
        // are also registered when the existing native event actually completes.
        foreach (var value in assets ? Resources.FindObjectsOfTypeAll<TriggerEvent>() :
            UnityEngine.Object.FindObjectsByType<TriggerEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        { var trap = value ? Trap(value.transform) : null; if (trap) visit(trap!.gameObject); }
    }
    private static void Visit<T>(Action<GameObject> visit, bool assets) where T : Component
    {
        foreach (var value in assets ? Resources.FindObjectsOfTypeAll<T>() :
            UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (value) visit(value.gameObject);
    }
}
