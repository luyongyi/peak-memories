using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PeakReplayLab;
using Photon.Pun;
using UnityEngine;

int passed = 0;
void Check(bool value) { if (!value) throw new Exception("Native origin contract assertion failed."); }
void Test(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); Environment.Exit(1); }
    finally { ReplaySafety.Active = false; MapHandler.Failure = null; MapHandler.DuringRestore = null; }
}
(Item item, PhotonView view) Born(int scene = 1, int id = 0)
{
    var source = new GameObject(scene, id); var item = new Item(); var view = new PhotonView(); source.Attach(item); source.Attach(view); return (item, view);
}
Test("actual concrete/inherited native return observers are installed", () =>
{
    using var capture = new NativeSpawnOriginCapture(); Check(capture.HookFailures == 0);
    var bush = new BerryBush(); var (item, view) = Born(); bush.Results.Add(view); int changed = 0;
    Action<GameObject> listener = _ => changed++; NativeSpawnOriginCapture.OriginChanged += listener;
    try { var result = bush.TrySpawnItems(); Check(ReferenceEquals(result, bush.Results) && bush.Calls == 2 && changed == 1 && capture.Count == 1); }
    finally { NativeSpawnOriginCapture.OriginChanged -= listener; }
    Check(ReferenceEquals(NativeSpawnOriginCapture.ResolveOwner(item.gameObject), bush.gameObject));
    Check(item.gameObject.ComponentReads == 1);
});
Test("single spawner uses exact references and child visuals inherit origin", () =>
{
    using var capture = new NativeSpawnOriginCapture(); var owner = new SingleItemSpawner(); var (item, view) = Born(); owner.Results.Add(view); owner.TrySpawnItems();
    var child = new GameObject(); child.transform.parent = item.transform;
    Check(ReferenceEquals(NativeSpawnOriginCapture.ResolveOwner(child), owner.gameObject));
    var unknown = new GameObject(); Check(NativeSpawnOriginCapture.ResolveOwner(unknown) == null && unknown.ComponentReads == 0);
});
Test("nested history tracking retains one origin without component rereads", () =>
{
    using var capture = new NativeSpawnOriginCapture(); var owner = new Peak.SpawnedItemTracker(); var (item, view) = Born(); owner.Results.Add(view);
    owner.SpawnAndTrackFromItemHistory(); owner.TrackSpawnedItems(owner.Results);
    Check(capture.Count == 1 && item.gameObject.ComponentReads == 1 && ReferenceEquals(NativeSpawnOriginCapture.ResolveOwner(item.gameObject), owner.gameObject));
});
Test("saved loose-item restore uses the actual previous campfire root", () =>
{
    using var capture = new NativeSpawnOriginCapture(); var (item, view) = Born();
    MapHandler.RespawnSavedBaseCampItems(Segment.Alpine, new(), new() { new() { Result = view } });
    Check(ReferenceEquals(NativeSpawnOriginCapture.ResolveOwner(item.gameObject), MapHandler.Camps[1]));
    var (outside, outsideView) = Born(); new Peak.SpawnedItemTracker.SpawnRecord { Result = outsideView }.Spawn(default);
    Check(NativeSpawnOriginCapture.ResolveOwner(outside.gameObject) == null);
});
Test("native restore exception survives and finalizer clears origin context", () =>
{
    using var capture = new NativeSpawnOriginCapture(); var expected = new ApplicationException("native restore"); MapHandler.Failure = expected;
    try { MapHandler.RespawnSavedBaseCampItems(Segment.Tropics, new(), new()); throw new Exception("exception lost"); }
    catch (ApplicationException error) { Check(ReferenceEquals(error, expected)); }
    var (item, view) = Born(); new Peak.SpawnedItemTracker.SpawnRecord { Result = view }.Spawn(default); Check(NativeSpawnOriginCapture.ResolveOwner(item.gameObject) == null);
});
Test("unknown restore segment never guesses a campfire origin", () =>
{
    using var capture = new NativeSpawnOriginCapture(); var (item, view) = Born();
    MapHandler.RespawnSavedBaseCampItems(Segment.Beach, new(), new() { new() { Result = view } }); Check(capture.Count == 0 && NativeSpawnOriginCapture.ResolveOwner(item.gameObject) == null);
});
Test("pickup permanently detaches even after drop and repeated native wrappers", () =>
{
    using var capture = new NativeSpawnOriginCapture(); var owner = new Spawner(); var (item, view) = Born(); owner.Results.Add(view); owner.TrySpawnItems();
    item.SetState(ItemState.Held, new Character()); item.SetState(ItemState.Ground); owner.TrySpawnItems();
    Check(capture.Count == 0 && NativeSpawnOriginCapture.ResolveOwner(item.gameObject) == null && item.NativeCalls == 2);
});
Test("pickup before the first native list returns cannot later bind origin", () =>
{
    using var capture = new NativeSpawnOriginCapture(); var owner = new Spawner(); var (item, view) = Born(); owner.Results.Add(view);
    owner.BeforeReturn = () => { item.SetState(ItemState.Held, new Character()); item.SetState(ItemState.Ground); };
    owner.TrySpawnItems(); Check(capture.Count == 0);
});
Test("equipped backpack detaches but unattended ground backpack retains birth", () =>
{
    using var capture = new NativeSpawnOriginCapture(); var owner = new Spawner(); var (carried, carriedView) = Born(); var (ground, groundView) = Born(); owner.Results.Add(carriedView); owner.Results.Add(groundView); owner.TrySpawnItems();
    ground.PutInBackpackRPC(0, new BackpackReference { type = BackpackReference.BackpackType.Item, view = new PhotonView() });
    carried.PutInBackpackRPC(0, new BackpackReference { type = BackpackReference.BackpackType.Equipped, view = new PhotonView() });
    Check(capture.Count == 1 && NativeSpawnOriginCapture.ResolveOwner(carried.gameObject) == null && ReferenceEquals(NativeSpawnOriginCapture.ResolveOwner(ground.gameObject), owner.gameObject));
});
Test("stick to a player permanently detaches while ground state does not", () =>
{
    using var capture = new NativeSpawnOriginCapture(); var owner = new Spawner(); var (item, view) = Born(); owner.Results.Add(view); owner.TrySpawnItems(); item.SetState(ItemState.Ground);
    Check(capture.Count == 1); var sticky = new StickyItemComponent { item = item }; sticky.StickToCharacterLocal(new Character(), Bodypart.Head, default); sticky.stuckToCharacter = null; owner.TrySpawnItems(); Check(capture.Count == 0);
});
Test("listener failures do not change native returns or suppress healthy listeners", () =>
{
    using var capture = new NativeSpawnOriginCapture(); var owner = new Spawner(); var (item, view) = Born(); owner.Results.Add(view); int changed = 0;
    Action<GameObject> broken = _ => throw new Exception("listener"); Action<GameObject> good = _ => changed++;
    NativeSpawnOriginCapture.OriginChanged += broken; NativeSpawnOriginCapture.OriginChanged += good;
    try { Check(ReferenceEquals(owner.TrySpawnItems(), owner.Results) && changed == 1 && capture.Count == 1); }
    finally { NativeSpawnOriginCapture.OriginChanged -= broken; NativeSpawnOriginCapture.OriginChanged -= good; }
});
Test("clearing an old scene preserves already generated new-scene references", () =>
{
    using var capture = new NativeSpawnOriginCapture(); var old = new Spawner(); var next = new Spawner(); next.gameObject.scene = new() { handle = 2, isLoaded = true };
    var (before, beforeView) = Born(); var (after, afterView) = Born(2); old.Results.Add(beforeView); next.Results.Add(afterView); old.TrySpawnItems(); next.TrySpawnItems();
    capture.ClearScene(1); Check(capture.Count == 1 && NativeSpawnOriginCapture.ResolveOwner(before.gameObject) == null && ReferenceEquals(NativeSpawnOriginCapture.ResolveOwner(after.gameObject), next.gameObject));
});
Test("carried weak detach marker survives old-scene cleanup", () =>
{
    using var capture = new NativeSpawnOriginCapture(); var owner = new Spawner(); var (item, view) = Born(); owner.Results.Add(view); owner.TrySpawnItems(); item.SetState(ItemState.Held, new Character());
    capture.ClearScene(1); item.gameObject.scene = new() { handle = 2, isLoaded = true }; item.SetState(ItemState.Ground); owner.TrySpawnItems(); Check(capture.Count == 0);
});
Test("destroy handles Unity fake-null and reused IDs by exact managed instance", () =>
{
    using var capture = new NativeSpawnOriginCapture(); var owner = new Spawner(); var (old, oldView) = Born(id: 123456); owner.Results.Add(oldView); owner.TrySpawnItems();
    var (fresh, freshView) = Born(id: 123456); var second = new Spawner(); second.Results.Add(freshView); second.TrySpawnItems(); old.DestroyNative(fakeNull: true);
    Check(capture.Count == 1 && ReferenceEquals(NativeSpawnOriginCapture.ResolveOwner(fresh.gameObject), second.gameObject)); fresh.DestroyNative(fakeNull: true); Check(capture.Count == 0);
});
Test("scene cleanup releases native origin strong references", () =>
{
    using var capture = new NativeSpawnOriginCapture(); var refs = ReferenceChecks.Make(capture, detach: false); capture.ClearScene(1); ReferenceChecks.Collect(); Check(!refs[0].IsAlive && !refs[1].IsAlive && capture.Count == 0);
});
Test("permanent detach releases the owner and source through cached Item lookup", () =>
{
    using var capture = new NativeSpawnOriginCapture(); var refs = ReferenceChecks.Make(capture, detach: true); ReferenceChecks.Collect(); Check(!refs[0].IsAlive && !refs[1].IsAlive && capture.Count == 0);
});
Test("replay reconstruction never records or detaches live origin", () =>
{
    using var capture = new NativeSpawnOriginCapture(); var owner = new Spawner(); var (item, view) = Born(); owner.Results.Add(view); owner.TrySpawnItems(); ReplaySafety.Active = true; item.SetState(ItemState.Held, new Character());
    Check(capture.Count == 1 && NativeSpawnOriginCapture.ResolveOwner(item.gameObject) == null); ReplaySafety.Active = false; Check(ReferenceEquals(NativeSpawnOriginCapture.ResolveOwner(item.gameObject), owner.gameObject));
});
Test("dispose removes observer patches and allows a fresh singleton", () =>
{
    var capture = new NativeSpawnOriginCapture(); capture.Dispose(); capture.Dispose();
    var patches = Harmony.GetPatchInfo(typeof(Spawner).GetMethod(nameof(Spawner.TrySpawnItems))); Check(patches == null || patches.Owners.Count == 0);
    using var replacement = new NativeSpawnOriginCapture(); Check(replacement.HookFailures == 0);
});
Console.WriteLine($"TOTAL: {passed} native spawn-origin contracts passed. Unity world/network simulation is not performed.");

static class ReferenceChecks
{
    [MethodImpl(MethodImplOptions.NoInlining)] internal static WeakReference[] Make(NativeSpawnOriginCapture capture, bool detach)
    {
        var owner = new Spawner(); var source = new GameObject(); var item = new Item(); var view = new PhotonView(); source.Attach(item); source.Attach(view); owner.Results.Add(view); owner.TrySpawnItems();
        if (detach) item.SetState(ItemState.Held, new Character()); return new[] { new WeakReference(source), new WeakReference(owner.gameObject) };
    }
    [MethodImpl(MethodImplOptions.NoInlining)] internal static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
}
