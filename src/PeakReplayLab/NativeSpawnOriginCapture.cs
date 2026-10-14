using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace PeakReplayLab;

// Observe native return references at spawn completion. A room item is normally
// unparented; its coordinates/name are never evidence of its biome of birth.
internal sealed class NativeSpawnOriginCapture : IDisposable
{
    private sealed class Origin
    {
        internal readonly int Id;
        internal readonly GameObject Source, Owner;
        internal readonly Item? Item;
        internal readonly int SourceScene, OwnerScene;
        internal Origin(GameObject source, GameObject owner, Item? item)
        { Id = source.GetInstanceID(); Source = source; Owner = owner; Item = item; SourceScene = source.scene.handle; OwnerScene = owner.scene.handle; }
    }
    private readonly struct RestoreScope
    {
        internal readonly NativeSpawnOriginCapture? Capture;
        internal readonly GameObject? Previous;
        internal readonly int Generation;
        internal RestoreScope(NativeSpawnOriginCapture capture) { Capture = capture; Previous = capture.restoringOwner; Generation = capture.generation; }
    }
    private const int MaximumOrigins = 32768;
    private static NativeSpawnOriginCapture? active;
    private static readonly object detachedValue = new();
    private readonly Harmony harmony = new("cn.mylus.peakreplaylab.spawn-origins." + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<int, Origin> origins = new();
    private ConditionalWeakTable<GameObject, Origin> bySource = new();
    private ConditionalWeakTable<Item, Origin> byItem = new();
    private ConditionalWeakTable<GameObject, object> detached = new();
    private readonly Action<string>? warning;
    private GameObject? restoringOwner;
    private int generation, failures;
    private bool disposed, overflowWarned;
    internal static event Action<GameObject>? OriginChanged;
    internal int Count => origins.Count;
    internal int HookFailures => failures;

    internal NativeSpawnOriginCapture(Action<string>? warning = null)
    {
        if (active != null) throw new InvalidOperationException("A native spawn-origin observer is already active.");
        this.warning = warning; active = this;
        try
        {
            var patched = new HashSet<MethodInfo>();
            foreach (var type in typeof(ISpawner).Assembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(Component).IsAssignableFrom(type) || !typeof(ISpawner).IsAssignableFrom(type)) continue;
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (method.ReturnType == typeof(List<PhotonView>) && method.GetMethodBody() != null &&
                        (method.Name == nameof(ISpawner.TrySpawnItems) || method.Name == "SpawnItems") && patched.Add(method))
                        Patch(method, postfix: nameof(SpawnedList));
            }
            Patch(AccessTools.DeclaredMethod(typeof(Peak.SpawnedItemTracker), "SpawnAndTrackFromItemHistory"), postfix: nameof(SpawnedList));
            Patch(AccessTools.DeclaredMethod(typeof(Peak.SpawnedItemTracker), "TrackSpawnedItems"), postfix: nameof(TrackedList));
            Patch(AccessTools.DeclaredMethod(typeof(Peak.SpawnedItemTracker.SpawnRecord), "Spawn"), postfix: nameof(RestoredSingle));
            Patch(AccessTools.DeclaredMethod(typeof(MapHandler), "RespawnSavedBaseCampItems"), prefix: nameof(BeginSavedRestore), finalizer: nameof(EndSavedRestore));
            // These observers are installed before island-ready/capture starts,
            // so a pickup during resume can never be reassigned by a later list.
            Patch(AccessTools.DeclaredMethod(typeof(Item), "SetState"), postfix: nameof(StateChanged));
            Patch(AccessTools.DeclaredMethod(typeof(Item), "PutInBackpackRPC"), postfix: nameof(StateChanged));
            Patch(AccessTools.DeclaredMethod(typeof(StickyItemComponent), "StickToCharacterLocal"), postfix: nameof(StickyChanged));
            Patch(AccessTools.DeclaredMethod(typeof(Item), "OnDestroy"), prefix: nameof(ItemDestroyed));
        }
        catch { Dispose(); throw; }
    }
    private void Patch(MethodInfo? target, string? prefix = null, string? postfix = null, string? finalizer = null)
    {
        try
        {
            if (target == null) throw new MissingMethodException("Native spawn-origin entry point is missing.");
            harmony.Patch(target, prefix: prefix == null ? null : new HarmonyMethod(typeof(NativeSpawnOriginCapture), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(NativeSpawnOriginCapture), postfix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(NativeSpawnOriginCapture), finalizer));
        }
        catch (Exception error) { failures++; Warn("入口不可用 " + target?.DeclaringType?.Name + "." + target?.Name, error); }
    }
    private void Warn(string operation, Exception? error = null)
    { try { warning?.Invoke("原生物品来源" + operation + (error == null ? "" : "：" + error.Message)); } catch { } }
    private static void Changed(GameObject source)
    {
        var listeners = OriginChanged;
        if (listeners == null) return;
        foreach (Action<GameObject> listener in listeners.GetInvocationList())
            try { listener(source); } catch { /* An observer cannot change native spawning. */ }
    }
    private void Record(GameObject source, GameObject owner)
    {
        if (disposed || ReplaySafety.Active || !source || !owner || detached.TryGetValue(source, out _)) return;
        int id = source.GetInstanceID();
        if (origins.TryGetValue(id, out var old))
        {
            if (ReferenceEquals(old.Source, source) && old.Owner) return; // Nested native wrappers repeat the same references.
            Remove(old, notify: !ReferenceEquals(old.Source, source));
        }
        var item = source.GetComponent<Item>();
        if (item && IsPlayerOwned(item!)) { Detach(source); return; }
        if (origins.Count >= MaximumOrigins)
        { if (!overflowWarned) { overflowWarned = true; Warn("目录达到32768个上限，额外对象保持未知归属"); } return; }
        var value = new Origin(source, owner, item); origins[id] = value;
        bySource.Remove(source); bySource.Add(source, value);
        if (item) { byItem.Remove(item!); byItem.Add(item!, value); }
        Changed(source);
    }
    private void Remove(Origin value, bool notify)
    {
        if (origins.TryGetValue(value.Id, out var found) && ReferenceEquals(found, value)) origins.Remove(value.Id);
        bySource.Remove(value.Source);
        if (!ReferenceEquals(value.Item, null)) byItem.Remove(value.Item!);
        if (notify) Changed(value.Source);
    }
    internal static GameObject? ResolveOwner(GameObject source)
    {
        var capture = active;
        if (capture == null || capture.disposed || !source || ReplaySafety.Active) return null;
        for (var node = source.transform; node; node = node.parent)
        {
            var go = node!.gameObject;
            if (capture.detached.TryGetValue(go, out _)) return null;
            if (!capture.bySource.TryGetValue(go, out var value)) continue;
            if (!value.Source || !value.Owner) { capture.Remove(value, notify: false); return null; }
            return value.Owner;
        }
        return null;
    }
    internal static void Detach(GameObject source)
    {
        var capture = active;
        if (capture == null || capture.disposed || !source || ReplaySafety.Active) return;
        if (capture.detached.TryGetValue(source, out _)) return;
        capture.detached.Add(source, detachedValue);
        if (capture.bySource.TryGetValue(source, out var value)) capture.Remove(value, notify: false);
        Changed(source);
    }
    internal static void Forget(GameObject source)
    {
        var capture = active;
        if (capture == null || ReferenceEquals(source, null)) return;
        if (capture.bySource.TryGetValue(source, out var value)) capture.Remove(value, notify: true);
        capture.detached.Remove(source);
    }
    private static void SpawnedList(Component __instance, List<PhotonView>? __result) => RecordList(__instance, __result);
    private static void TrackedList(Component __instance, List<PhotonView>? spawnedItems) => RecordList(__instance, spawnedItems);
    private static void RecordList(Component owner, List<PhotonView>? sources)
    {
        try
        {
            if (active == null || ReplaySafety.Active || !owner || sources == null) return;
            foreach (var view in sources) if (view) active.Record(view.gameObject, owner.gameObject);
        }
        catch (Exception error) { active?.Warn("返回列表观察失败", error); }
    }
    private static void BeginSavedRestore(Segment segment, out RestoreScope __state)
    {
        __state = default;
        var capture = active;
        if (capture == null || capture.disposed || ReplaySafety.Active) return;
        __state = new RestoreScope(capture);
        capture.restoringOwner = null;
        try { if ((int)segment >= 1 && (int)segment <= 4) capture.restoringOwner = MapHandler.GetCampfireRoot((int)segment - 1); }
        catch (Exception error) { capture.Warn("存档营地来源不可用", error); }
    }
    private static Exception? EndSavedRestore(Exception? __exception, RestoreScope __state)
    {
        var capture = __state.Capture;
        if (capture != null && !capture.disposed && capture.generation == __state.Generation) capture.restoringOwner = __state.Previous;
        return __exception;
    }
    private static void RestoredSingle(PhotonView? __result)
    {
        try { var capture = active; if (capture != null && capture.restoringOwner && __result) capture.Record(__result!.gameObject, capture.restoringOwner!); }
        catch (Exception error) { active?.Warn("存档物品来源观察失败", error); }
    }
    private static void StateChanged(Item __instance)
    { try { if (__instance && IsPlayerOwned(__instance)) Detach(__instance.gameObject); } catch { } }
    private static void StickyChanged(StickyItemComponent __instance)
    { try { if (__instance && __instance.item && __instance.stuckToCharacter && __instance.stuckToCharacter!.IsPlayerControlled) Detach(__instance.item.gameObject); } catch { } }
    private static bool IsPlayerOwned(Item item)
    {
        var holder = item.trueHolderCharacter;
        if (holder && holder!.IsPlayerControlled) return true;
        if (item.itemState != ItemState.InBackpack || !item.backpackReference.IsSome) return false;
        var reference = item.backpackReference.Value.Item2;
        return reference.type == BackpackReference.BackpackType.Equipped && reference.view;
    }
    private static void ItemDestroyed(Item __instance)
    {
        try
        {
            var capture = active;
            if (capture == null || ReferenceEquals(__instance, null) || !capture.byItem.TryGetValue(__instance, out var value)) return;
            capture.byItem.Remove(__instance); Forget(value.Source);
        }
        catch { }
    }
    internal void ClearScene(int sceneHandle)
    {
        var remove = new List<Origin>();
        foreach (var origin in origins.Values)
            if (origin.SourceScene == sceneHandle || origin.OwnerScene == sceneHandle) remove.Add(origin);
        foreach (var origin in remove) Remove(origin, notify: true);
        if (restoringOwner && restoringOwner!.scene.handle == sceneHandle) { restoringOwner = null; generation++; }
        overflowWarned = false;
    }
    private void ClearAll()
    {
        origins.Clear(); bySource = new(); byItem = new(); detached = new(); restoringOwner = null; generation++;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (ReferenceEquals(active, this)) active = null;
        harmony.UnpatchSelf(); ClearAll();
    }
}
