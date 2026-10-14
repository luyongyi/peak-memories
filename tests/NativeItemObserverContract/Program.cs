using System.Reflection;
using HarmonyLib;
using PeakReplayLab;
using UnityEngine;

int passed = 0;
void Check(bool value, string message = "Native item observer assertion failed.") { if (!value) throw new Exception(message); }
void Test(string name, Action test)
{
    ItemCapture.Reset(); ItemEventCapture.Activate(false); ItemEventCapture.Activate(true); Time.timeAsDouble = 10;
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { Console.Error.WriteLine("FAIL " + name + ": " + e); Environment.Exit(1); }
    finally { ItemCapture.Reset(); ItemEventCapture.Activate(false); }
}
ItemEventCapture Observe()
{
    var warnings = new List<string>(); var observer = new ItemEventCapture(warnings.Add);
    Check(observer.HookFailures == 0 && warnings.Count == 0 && ItemCapture.EventHooksHealthy,
        "Hook installation failed: " + string.Join(" | ", warnings)); return observer;
}
bool Seen(string kind, Item? item) => ItemCapture.Notices.Any(n => n.Kind == kind && ReferenceEquals(n.Item, item));

Test("all 45 production patches install through real Harmony with matching native signatures", () =>
{
    using var observer = Observe();
    var methods = Harmony.GetAllPatchedMethods().Select(Harmony.GetPatchInfo).Where(p => p != null).ToArray();
    int count = methods.Sum(p => p.Prefixes.Concat(p.Postfixes).Count(patch => patch.owner.StartsWith("cn.mylus.peakreplaylab.items.", StringComparison.Ordinal)));
    Check(count == 45, "Expected 45 Harmony callbacks, observed " + count);
});
Test("native item lifecycles and optimizer callbacks update registry while destruction is distinct", () =>
{
    using var observer = Observe(); var item = new Item();
    item.InvokeAwake(); item.OnEnable(); item.OnDisable();
    Peak.ItemOptimizationManager.RegisterItem(item); Peak.ItemOptimizationManager.DeregisterItem(item);
    Check(ItemCapture.Notices.Count(n => n.Kind == "registry" && ReferenceEquals(n.Item, item)) == 5);
    item.InvokeDestroy(); Check(item.Destroyed && item.NativeCalls == 4 && Seen("destroy", item));
    Check(ItemCapture.Notices.Count(n => n.Kind == "registry") == 5);
    // Unity can retain a managed wrapper after its native side becomes null.
    var alreadyDestroyed = new Item { Destroyed = true }; alreadyDestroyed.InvokeDestroy();
    Check(Seen("destroy", alreadyDestroyed));
});
Test("kinematic transitions movement and forces notify after the native state has changed", () =>
{
    using var observer = Observe(); var item = new Item(); var observedCalls = new List<int>();
    ItemCapture.OnNotify = (kind, source) => { if (kind == "motion") observedCalls.Add(source!.NativeCalls); };
    item.SetKinematic(true); item.SetKinematic(false);
    item.SetKinematicRPC(true, new Vector3(1, 2, 3), default);
    item.SetKinematicAndResetSyncData(false, new Vector3(4, 5, 6), default);
    item.Move(new Vector3(7, 8, 9), default); item.ApplyForceRPC(default, default);
    Check(observedCalls.SequenceEqual(Enumerable.Range(1, 6)) && item.transform.position.x == 7 && !item.Kinematic);
});
Test("physics packets and getters preserve native result while ShouldSendData false stays silent", () =>
{
    using var observer = Observe(); var item = new Item(); var sync = new ItemPhysicsSyncer { ItemOwner = item };
    Check(!sync.ShouldSendData() && ItemCapture.Notices.Count == 0);
    sync.Send = true; Check(sync.ShouldSendData() && ItemCapture.Notices.Count == 0);
    sync.OnDataReceived(new ItemPhysicsSyncData { Stamp = 1234, position = new Vector3(1, 2, 3), rotation = new Quaternion { w = 1 } });
    Check(sync.GetDataToWrite().Stamp == 1234); sync.ResetRecievedData(); sync.ForceSyncForFrames(3);
    Check(sync.NativeCalls == 6 && sync.Data.Stamp == 0 && ItemCapture.Notices.Count(n => n.Kind == "motion") == 1);
    sync.GetDataToWrite(); // Empty/default native packet is not a zero-position teleport.
    Check(ItemCapture.Notices.Count(n => n.Kind == "motion") == 1);
    sync.OnDataReceived(new ItemPhysicsSyncData { position = new Vector3(2, 2, 3), rotation = new Quaternion { w = 1 } });
    Check(ItemCapture.Notices.Count(n => n.Kind == "motion") == 2);
});
Test("wind collision movement zones and launching explosions reach ground items without holders", () =>
{
    using var observer = Observe(); var direct = new Item(); var bodyItem = new Item();
    new WindChillZone().Wind(direct);
    new ItemImpactSFX { ItemOwner = direct }.Collide();
    var zone = new ItemMoveZone(); zone.Touch(new Collider { ItemOwner = direct });
    zone.Touch(new Collider { DirectItem = false, attachedRigidbody = new Rigidbody { ItemOwner = bodyItem } });
    Check(ItemCapture.Notices.Count(n => n.Kind == "motion" && ReferenceEquals(n.Item, direct)) == 3 && Seen("motion", bodyItem));
    var blast = new AOE { range = 9 }; blast.transform.position = new Vector3(1, 2, 3); blast.Explode();
    var area = ItemCapture.Notices.Single(n => n.Kind == "area"); Check(area.Radius == 10 && area.Position.y == 2);
    blast.canLaunchItems = false; blast.Explode(); Check(ItemCapture.Notices.Count(n => n.Kind == "area") == 1 && blast.NativeCalls == 2);
});
Test("cooking smoke and impact events notify the actual item after native behavior completes", () =>
{
    using var observer = Observe(); var item = new Item(); var cooking = new ItemCooking { ItemOwner = item };
    var smoke = new ItemParticles { ItemOwner = item }; var collision = new EventOnItemCollision { ItemOwner = item };
    ItemCapture.OnNotify = (_, _) => Check(cooking.State == ItemCooking.NetworkedCooking.Cooked);
    cooking.SyncCooking(ItemCooking.NetworkedCooking.Cooked); cooking.UpdateCookedBehavior(); smoke.EnableSmoke(true); collision.Trigger();
    Check(cooking.NativeCalls == 2 && smoke.Smoke && smoke.NativeCalls == 1 && collision.NativeCalls == 1);
    Check(ItemCapture.Notices.Count(n => n.Kind == "dirty" && ReferenceEquals(n.Item, item)) == 4);
});
Test("item state data ownership and inventory RPC hooks retain original out and return values", () =>
{
    using var observer = Observe(); var item = new Item(); var character = new Character(); var player = new Player();
    item.ChangeState(ItemState.Held, character); item.SetCookedAmountRPC(2); item.SetUseRemainingPercentage(.75f);
    item.SetItemInstanceDataRPC(new ItemInstanceData()); item.PutInBackpackRPC(1, default);
    Check(item.State == ItemState.Held && item.Cooked == 2 && ItemCapture.Notices.Count(n => n.Kind == "dirty") == 2);
    Check(ItemCapture.Notices.Count(n => n.Kind == "metadata" && ReferenceEquals(n.Item,item)) == 1);
    Check(ItemCapture.Notices.Count(n => n.Kind == "structure") == 2);
    Check(player.AddItem(1, new ItemInstanceData(), out var slot) && slot != null);
    player.EmptySlot(default); player.RPCRemoveItemFromSlot(1); player.RPC_SetInventory(new byte[] { 1 });
    Check(player.NativeCalls == 4 && ItemCapture.Notices.Count(n => n.Kind == "inventory") == 4);
    player.SyncInventoryRPC(Array.Empty<byte>(), false); Check(ItemEventCapture.InventoryKnown(player));
    player.InvokeDestroy(); Check(!ItemEventCapture.InventoryKnown(player));
});
Test("equipment records the prior stash and resulting equipped item at the correct hook phase", () =>
{
    using var observer = Observe(); var old = new Item(); var next = new Item(); var character = new Character(); character.data.currentItem = old;
    var inventory = new CharacterItems { CharacterOwner = character, EquipResult = next };
    inventory.EquipSlotRpc(0, 123); var events = ItemEventCapture.Drain(10);
    Check(events.Select(e => e.Kind).SequenceEqual(new[] { "stash", "equip" }));
    Check(events[0].ItemKey == ItemCapture.Key(old) && events[1].ItemKey == ItemCapture.Key(next));
    Check(events.All(e => e.ActorId == character.ActorId) && events[0].Sequence < events[1].Sequence);
    inventory.DropItemRpc(1, 0, default, default, default, new ItemInstanceData(), false);
    var drop = ItemEventCapture.Drain(10).Single(); Check(drop.Kind == "drop" && drop.ItemKey == ItemCapture.Key(next) && character.data.currentItem == null);
});
Test("use feed consume and charge callbacks preserve recorded action order and native state", () =>
{
    using var observer = Observe(); var item = new Item(); var character = new Character(); item.trueHolderCharacter = character;
    Photon.Pun.PhotonNetwork.Views[7] = new Photon.Pun.PhotonView { CharacterOwner = character, ViewID = 7 };
    item.StartUsePrimary(); item.StartUsePrimary(); item.CancelUsePrimary(); item.StartUsePrimary(); item.FinishPrimary();
    item.isUsingPrimary = false; item.finishedCast = false; item.StartUseSecondary(); item.CancelUseSecondary(); item.StartUseSecondary(); item.FinishCastSecondary();
    item.SendFeedDataRPC(7, 9, 1, 1); item.RemoveFeedDataRPC(7); item.Consume(7); new Action_ReduceUses { ItemOwner = item }.ReduceUsesRPC();
    var events = ItemEventCapture.Drain(10);
    Check(events.Select(e => e.Kind).SequenceEqual(new[] { "use-primary-start", "use-primary-cancel", "use-primary-start", "use-primary-finish",
        "use-secondary-start", "use-secondary-cancel", "use-secondary-start", "use-secondary-finish", "feed-start", "feed-end", "consume", "use-charge" }));
    Check(events.All(e => e.ActorId == character.ActorId && e.T == 10 && e.ItemKey == ItemCapture.Key(item)));
});
Test("replay safety suppresses lifecycle motion cooking inventory and action capture", () =>
{
    using var observer = Observe(); ReplaySafety.Active = true; var item = new Item();
    item.InvokeAwake(); item.OnEnable(); item.OnDisable(); item.SetKinematic(false); item.Move(default, default); item.SetCookedAmountRPC(1); item.StartUsePrimary();
    new ItemPhysicsSyncer { ItemOwner = item, Send = true }.ShouldSendData(); new WindChillZone().Wind(item);
    new ItemCooking { ItemOwner = item }.UpdateCookedBehavior(); new AOE().Explode();
    Peak.ItemOptimizationManager.RegisterItem(item); Peak.ItemOptimizationManager.DeregisterItem(item);
    var player = new Player(); player.SyncInventoryRPC(Array.Empty<byte>(), false); player.AddItem(1, new ItemInstanceData(), out _); item.InvokeDestroy();
    Check(ItemCapture.Notices.Count == 0 && ItemEventCapture.Drain(10).Length == 0 && !ItemEventCapture.InventoryKnown(player));
    Check(item.NativeCalls == 8 && item.Destroyed && player.NativeCalls == 2);
});
Test("failing observer callbacks cannot suppress native state results or lifecycle completion", () =>
{
    using var observer = Observe(); ItemCapture.OnNotify = (_, _) => throw new InvalidOperationException("injected recorder failure");
    var item = new Item(); item.InvokeAwake(); item.SetCookedAmountRPC(3); item.SetItemInstanceDataRPC(new ItemInstanceData());
    item.SetKinematic(true); item.StartUsePrimary(); item.InvokeDestroy(); Check(item.NativeCalls == 6 && item.Cooked == 3 && item.isUsingPrimary && item.Destroyed);
    var sync = new ItemPhysicsSyncer { ItemOwner = new Item(), Send = true, Data = new ItemPhysicsSyncData { Stamp = 77 } };
    Check(sync.ShouldSendData() && sync.GetDataToWrite().Stamp == 77);
    var blast = new AOE(); blast.Explode(); Check(blast.NativeCalls == 1);
    var player = new Player(); Check(player.AddItem(1, new ItemInstanceData(), out var slot) && slot != null && player.NativeCalls == 1);
});
Test("native exceptions remain native and do not cause false postfix observations", () =>
{
    using var observer = Observe(); var expected = new ApplicationException("native failure"); var item = new Item { NativeFailure = expected };
    try { item.SetCookedAmountRPC(7); throw new Exception("Native exception disappeared."); }
    catch (ApplicationException error) { Check(ReferenceEquals(error, expected)); }
    Check(item.Cooked == 0 && item.NativeCalls == 0 && ItemCapture.Notices.Count == 0);
});
Test("explicit item use permanently detaches origin before recorder notification", () =>
{
    using var observer = Observe(); var item = new Item(); bool detachedBeforeDirty = false;
    ItemCapture.OnNotify = (kind, source) => { if (kind == "dirty") detachedBeforeDirty = NativeSpawnOriginCapture.Detached.Contains(item.gameObject); };
    item.StartUsePrimary(); Check(detachedBeforeDirty && NativeSpawnOriginCapture.Detached.Count == 1 && item.isUsingPrimary);
});
Test("ground lifecycle, data changes, wind and native settling never detach origin", () =>
{
    using var observer = Observe(); var item = new Item(); item.InvokeAwake(); item.OnEnable(); item.ChangeState(ItemState.Ground, new Character());
    item.SetItemInstanceDataRPC(new ItemInstanceData()); item.SetKinematic(true); item.SetKinematicRPC(false, default, default); item.Move(default, default); item.ApplyForceRPC(default, default);
    new WindChillZone().Wind(item); new ItemPhysicsSyncer { ItemOwner = item }.OnDataReceived(new ItemPhysicsSyncData { position = new Vector3(1, 0, 0) });
    Check(NativeSpawnOriginCapture.Detached.Count == 0 && Seen("motion", item) && Seen("structure", item));
});
Test("replay and non-gameplay actions cannot alter live origin", () =>
{
    using var observer = Observe(); ReplaySafety.Active = true; new Item().StartUsePrimary(); ReplaySafety.Active = false;
    GameHandler.IsOnIslandAndInitialized = false; new Item().StartUsePrimary(); Check(NativeSpawnOriginCapture.Detached.Count == 0);
});
Test("disposing removes every owned Harmony callback and leaves normal native methods callable", () =>
{
    var observer = Observe(); observer.Dispose(); observer.Dispose(); ItemCapture.Notices.Clear();
    var item = new Item(); item.InvokeAwake(); item.SetKinematic(false); item.SetCookedAmountRPC(2); item.StartUsePrimary(); item.InvokeDestroy();
    Check(item.NativeCalls == 5 && item.Cooked == 2 && ItemCapture.Notices.Count == 0 && ItemEventCapture.Drain(10).Length == 0 && !ItemCapture.EventHooksHealthy);
    Check(!Harmony.GetAllPatchedMethods().Any(method => Harmony.GetPatchInfo(method)?.Owners.Any(owner => owner.StartsWith("cn.mylus.peakreplaylab.items.", StringComparison.Ordinal)) == true));
});
Test("network physics wind and zone delivery are continuous observations while collisions and explicit forces stay events",()=>
{
    using var observer=Observe();var item=new Item();
    var wind=new WindChillZone();var zone=new ItemMoveZone();var collider=new Collider{ItemOwner=item};
    wind.Wind(item);zone.Touch(collider);ItemCapture.Notices.Clear();
    new ItemPhysicsSyncer{ItemOwner=item}.OnDataReceived(new ItemPhysicsSyncData{position=new Vector3(1,0,0),rotation=new Quaternion{w=1}});
    wind.Wind(item);zone.Touch(collider);
    var continuous=ItemCapture.Notices.Where(n=>n.Kind=="motion").ToArray();Check(continuous.Length==3&&continuous.All(n=>n.Continuous));
    new ItemImpactSFX{ItemOwner=item}.Collide();item.ApplyForceRPC(default,default);
    var events=ItemCapture.Notices.Where(n=>n.Kind=="motion"&&!n.Continuous).ToArray();Check(events.Length==2&&events.All(n=>ReferenceEquals(n.Item,item)));
});
Test("remaining-fuel Update notifications are metadata only and preserve genuine cooking and state boundaries",()=>
{
    using var observer=Observe();var item=new Item();
    for(int i=0;i<30;i++)item.SetUseRemainingPercentage(1-i/100f);
    Check(ItemCapture.Notices.Count(n=>n.Kind=="metadata")==30&&!ItemCapture.Notices.Any(n=>n.Kind=="dirty"||n.Kind=="motion"||n.Kind=="structure"));
    item.SetCookedAmountRPC(1);item.ChangeState(ItemState.Held,new Character());
    Check(ItemCapture.Notices.Count(n=>n.Kind=="dirty")==2);
});
Test("native zero wind and zone forces stay silent while real force startup is one immediate event per source",()=>
{
    using var observer=Observe();var item=new Item();var wind=new WindChillZone{useIntensityCurve=true,windIntensity=.4f};
    var zone=new ItemMoveZone{forceMultiplier=0};var collider=new Collider{ItemOwner=item};
    for(int i=0;i<30;i++){wind.Wind(item);zone.Touch(collider);}Check(ItemCapture.Notices.Count==0);
    wind.windIntensity=.8f;zone.forceMultiplier=1;wind.Wind(item);zone.Touch(collider);
    Check(ItemCapture.Notices.Count==2&&ItemCapture.Notices.All(n=>n.Kind=="motion"&&!n.Continuous));
    wind.Wind(item);zone.Touch(collider);Check(ItemCapture.Notices.Count==4&&ItemCapture.Notices.Skip(2).All(n=>n.Continuous));
    // A zero-force second source must not reset the first source's state.
    var zero=new WindChillZone{windForce=0};zero.Wind(item);wind.Wind(item);
    Check(ItemCapture.Notices.Count==5&&ItemCapture.Notices.Last().Continuous);
});
Console.WriteLine($"TOTAL: {passed} native item observer checks passed using real HarmonyX injection. Unity physics, rendering and in-game FPS are not simulated.");
