using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Photon.Pun;
using UnityEngine;
using Zorro.Core;

namespace LocalRouteAudit;

// Test-only native actions. The enclosing probe owns save/stat protections,
// opt-in invocation, recorder session lifetime, deadlines, and restoration.
// These helpers never change map selection, root activation, or AI state.
internal static class NativeRouteActions
{
    private static readonly FieldInfo CurrentIndexField = typeof(MapHandler).GetField("currentSegment",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(MapHandler).FullName, "currentSegment");

    internal static NativeSegmentInfo Current()
    {
        Require(MapHandler.ExistsAndInitialized, "Native map is not initialized.");
        var map = MapHandler.Instance;
        int index = (int)CurrentIndexField.GetValue(map)!;
        Require(map.segments != null && index >= 0 && index < map.segments.Length, "Native segment index is out of range.");
        var segment = map.segments![index];
        Require(segment != null, "Native segment descriptor is absent.");
        var root = segment!.segmentParent;
        var campRoot = segment.segmentCampfire;
        var campfire = campRoot && campRoot.activeInHierarchy ? campRoot.GetComponentInChildren<Campfire>() : null;
        var envelope = root ? root.GetComponentInParent<Biome>(includeInactive: true) : null;
        return new NativeSegmentInfo
        {
            Index = index,
            NativeSegment = map.GetCurrentSegment().ToString(),
            Biome = segment.biome.ToString(),
            BiomeId = (int)segment.biome,
            SelectedBiomes = map.biomes.Select(value => value.ToString()).ToArray(),
            RootName = root ? root.name : "",
            RootPath = root ? PathOf(root.transform) : "",
            RootActive = root && root.activeInHierarchy,
            CampfireRootName = campRoot ? campRoot.name : "",
            CampfireRootPath = campRoot ? PathOf(campRoot.transform) : "",
            CampfireRootActive = campRoot && campRoot.activeInHierarchy,
            CampfireName = campfire ? campfire.name : "",
            CampfireState = campfire ? campfire.state.ToString() : "absent",
            AdvanceToSegment = campfire ? campfire.advanceToSegment.ToString() : "absent",
            AdvanceToIndex = campfire ? (int)campfire.advanceToSegment : -1,
            Root = root,
            CampfireRoot = campRoot,
            Campfire = campfire,
            BiomeEnvelope = envelope
        };
    }

    internal static BakedSceneInfo[] ListBakedScenes()
    {
        var baker = SingletonAsset<MapBaker>.Instance;
        Require(baker, "Native MapBaker is unavailable.");
        int count = Math.Min(baker!.ScenePaths?.Length ?? 0, baker.selectedBiomes?.Count ?? 0);
        var output = new List<BakedSceneInfo>(count);
        for (int index = 0; index < count; index++)
        {
            var selection = baker.selectedBiomes![index];
            if (selection == null) continue;
            output.Add(new BakedSceneInfo
            {
                Index = index,
                Scene = baker.GetLevel(index),
                BiomeId = baker.GetBiomeID(index),
                Biomes = selection.biomeTypes.Select(value => value.ToString()).ToArray(),
                BiomeValues = selection.biomeTypes.Select(value => (int)value).ToArray(),
                VariantNames = selection.variantNames?.ToArray() ?? Array.Empty<string>()
            });
        }
        return output.ToArray();
    }

    internal static Vector3 TeleportToCampfire(NativeSegmentInfo segment)
    {
        RequireCurrentCampfire(segment);
        return TeleportTo(SafeGroundNear(segment.Campfire!.transform.position, 3.5f));
    }

    internal static Vector3 TeleportToBaseCamp()
    {
        Current();
        var spawn = MapHandler.CurrentBaseCampSpawnPoint;
        Require(spawn, "Native basecamp spawn is unavailable.");
        // CurrentBaseCampSpawnPoint is the lit campfire transform in later
        // biomes. Standing on its exact origin burns the stationary fixture.
        return TeleportTo(spawn!.GetComponent<Campfire>()
            ? SafeGroundNear(spawn.position, 3.5f) : spawn.position + Vector3.up);
    }

    private static Vector3 SafeGroundNear(Vector3 center, float radius)
    {
        for (int step = 0; step < 16; step++)
        {
            float angle = step * Mathf.PI / 4;
            float r = radius + (step >= 8 ? 1.5f : 0);
            Vector3 offset = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * r;
            if (!Physics.Raycast(center + offset + Vector3.up * 6, Vector3.down, out var hit, 12,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) || hit.normal.y < .6f || hit.point.y < center.y - 3)
                continue;
            if (hit.collider.GetComponentInParent<Character>() || hit.collider.GetComponentInParent<Item>() ||
                hit.collider.GetComponentInParent<Luggage>()) continue;
            Vector3 target = hit.point + Vector3.up * 1.2f;
            if (Physics.CheckSphere(target, .35f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) continue;
            return target;
        }
        throw new InvalidOperationException("No bounded safe floor near this native campfire; refusing to place the idle fixture on its flame or a cliff.");
    }

    internal static Vector3 TeleportTo(Vector3 position)
    {
        var local = RequireOfflineLocal(allowWarping: false);
        Require(Finite(position), "Teleport target is not finite.");
        local.photonView.RPC("WarpPlayerRPC", RpcTarget.All, position, false);
        return position;
    }

    // Return the native hold duration; caller schedules Finish after that delay
    // and must not finish twice. Rendering and recorder continue during the wait.
    internal static float BeginCampfire(NativeSegmentInfo segment)
    {
        var local = RequireOfflineLocal(allowWarping: false);
        var campfire = RequireCurrentCampfire(segment);
        Require(segment.Index < 4 && (int)campfire.advanceToSegment == segment.Index + 1,
            "This is not the next ordinary full-run campfire transition.");
        Require(campfire.state == Campfire.FireState.Off && !campfire.Lit, "Native campfire is already lit or spent.");
        Require(campfire.IsInteractible(local), "Native campfire rejects interaction.");
        Require(campfire.EveryoneInRange(campfire.moraleBoostRadius), "Not every living player is in native campfire range.");
        campfire.Interact(local);
        return Mathf.Max(.1f, campfire.GetInteractTime(local));
    }

    internal static void FinishCampfire(NativeSegmentInfo segment)
    {
        var local = RequireOfflineLocal(allowWarping: false);
        var campfire = RequireCurrentCampfire(segment);
        Require(campfire.state == Campfire.FireState.Off && !campfire.Lit, "Native campfire changed before cast completion.");
        Require(campfire.EveryoneInRange(campfire.moraleBoostRadius), "Player left native campfire range during cast.");
        campfire.Interact_CastFinished(local);
    }

    internal static float BeginLuggage(NativeSegmentInfo segment, Luggage luggage)
    {
        var local = RequireOfflineLocal(allowWarping: false);
        RequireNormalLuggage(segment, luggage, local);
        luggage.Interact(local);
        return Mathf.Max(.1f, luggage.GetInteractTime(local));
    }

    internal static void FinishLuggage(NativeSegmentInfo segment, Luggage luggage)
    {
        var local = RequireOfflineLocal(allowWarping: false);
        RequireNormalLuggage(segment, luggage, local);
        luggage.Interact_CastFinished(local);
    }

    internal static bool IsWithinCurrentScope(NativeSegmentInfo segment, Component candidate) =>
        candidate && IsWithinCurrentScope(segment, candidate.transform);

    internal static bool IsWithinCurrentScope(NativeSegmentInfo segment, GameObject candidate) =>
        candidate && IsWithinCurrentScope(segment, candidate.transform);

    internal static bool IsWithinCurrentScope(NativeSegmentInfo segment, Transform candidate)
    {
        if (!candidate || !candidate.gameObject.activeInHierarchy || !segment.Root || !segment.Root!.activeInHierarchy)
            return false;
        if (candidate.gameObject.scene != segment.Root!.scene) return false;
        var source = candidate;
        if (Within(source, segment.Root.transform)) return true;
        if (segment.CampfireRoot && segment.CampfireRoot!.activeInHierarchy && Within(source, segment.CampfireRoot.transform)) return true;
        // Authored Gloom/temple siblings can sit outside segmentParent. Match the
        // native Biome envelope, as production segment scope does, not distance.
        return segment.BiomeEnvelope && segment.BiomeEnvelope!.gameObject.activeInHierarchy &&
            Within(source, segment.BiomeEnvelope.transform);
    }

    internal static T[] FindCurrentCandidates<T>(NativeSegmentInfo segment, Func<T, bool>? predicate = null)
        where T : Component
    {
        Require(segment.Root && segment.Root!.activeInHierarchy, "Current native root is inactive.");
        var sources = new List<GameObject> { segment.Root! };
        if (segment.BiomeEnvelope) sources.Add(segment.BiomeEnvelope!.gameObject);
        if (segment.CampfireRoot && segment.CampfireRoot!.activeInHierarchy) sources.Add(segment.CampfireRoot);
        var ids = new HashSet<int>();
        var output = new List<T>();
        foreach (var source in sources)
            foreach (var candidate in source.GetComponentsInChildren<T>(includeInactive: false))
                if (candidate && ids.Add(candidate.GetInstanceID()) && IsWithinCurrentScope(segment, candidate) &&
                    (predicate == null || predicate(candidate))) output.Add(candidate);
        return output.ToArray();
    }

    internal static string PathOf(Transform transform)
    {
        var parts = new List<string>();
        for (var source = transform; source; source = source.parent)
            parts.Add(source.parent ? source.GetSiblingIndex() + ":" + Uri.EscapeDataString(source.name) : Uri.EscapeDataString(source.name));
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static Character RequireOfflineLocal(bool allowWarping)
    {
        Require(PhotonNetwork.OfflineMode && PhotonNetwork.InRoom && PhotonNetwork.IsMasterClient,
            "Native route actions require an isolated offline master session.");
        Require(!RunSettings.isMiniRun, "Native route actions require a full run.");
        var local = Character.localCharacter;
        Require(local && local.refs != null && local.refs.hip, "Native local character is not ready.");
        Require(!local!.data.dead && !local.data.fullyPassedOut && !local.gameOver, "Native local character cannot interact.");
        Require(allowWarping || !local.warping, "Native warp has not settled.");
        Require(PlayerHandler.GetAllPlayerCharacters().Count == 1, "Native route actions require a single-player fixture.");
        return local;
    }

    private static Campfire RequireCurrentCampfire(NativeSegmentInfo expected)
    {
        var actual = Current();
        Require(actual.Index == expected.Index && actual.Root == expected.Root && actual.Campfire == expected.Campfire,
            "Native segment or campfire changed after route planning.");
        Require(expected.Campfire && expected.Campfire!.gameObject.activeInHierarchy, "No active native current campfire.");
        return expected.Campfire!;
    }

    private static void RequireNormalLuggage(NativeSegmentInfo segment, Luggage luggage, Character local)
    {
        Require(luggage && luggage.GetType() == typeof(Luggage) && !luggage.CompareTag("Trap"),
            "Generic box scenario requires an ordinary native Luggage; special/trap variants need dedicated scenarios.");
        Require(IsWithinCurrentScope(segment, luggage!), "Native box is not in the current active biome scope.");
        Require(!luggage!.IsOpen && luggage.IsInteractible(local), "Native box is not closed/interactible.");
        Require(Vector3.Distance(local.Center, luggage.Center()) <= 5f, "Player is outside the bounded box test proximity.");
    }

    private static bool Within(Transform source, Transform root) => source == root || source.IsChildOf(root);
    private static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

internal sealed class NativeSegmentInfo
{
    public int Index, BiomeId, AdvanceToIndex;
    public string NativeSegment = "", Biome = "", RootName = "", RootPath = "", CampfireRootName = "", CampfireRootPath = "";
    public string CampfireName = "", CampfireState = "", AdvanceToSegment = "";
    public string[] SelectedBiomes = Array.Empty<string>();
    public bool RootActive, CampfireRootActive;
    [JsonIgnore] public GameObject? Root, CampfireRoot;
    [JsonIgnore] public Campfire? Campfire;
    [JsonIgnore] public Biome? BiomeEnvelope;
}

internal sealed class BakedSceneInfo
{
    public int Index;
    public string Scene = "", BiomeId = "";
    public string[] Biomes = Array.Empty<string>(), VariantNames = Array.Empty<string>();
    public int[] BiomeValues = Array.Empty<int>();
}
