using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace PeakReplayLab;

// Observe normal game methods only. No replay event ever invokes these methods or RPCs.
internal sealed class ItemEventCapture : IDisposable
{
    private static readonly Queue<ItemEvent> pending = new();
    private static readonly HashSet<int> syncedInventory = new();
    private static bool active;
    private static long sequence;
    private static Action<string>? warn;
    private static bool overflowWarned;
    private readonly Harmony harmony = new("cn.mylus.peakreplaylab.items." + Guid.NewGuid().ToString("N"));
    private readonly Action<string>? warning;
    public int HookFailures { get; private set; }

    public ItemEventCapture(Action<string>? warning = null)
    {
        this.warning = warning;
        warn = warning;
        try
        {
            Patch(typeof(Item), nameof(Item.StartUsePrimary), nameof(PrimaryStart));
            Patch(typeof(Item), "FinishCastPrimary", nameof(PrimaryFinish));
            Patch(typeof(Item), nameof(Item.CancelUsePrimary), nameof(PrimaryCancel));
            Patch(typeof(Item), nameof(Item.StartUseSecondary), nameof(SecondaryStart));
            Patch(typeof(Item), nameof(Item.FinishCastSecondary), nameof(SecondaryFinish));
            Patch(typeof(Item), nameof(Item.CancelUseSecondary), nameof(SecondaryCancel));
            Patch(typeof(Item), nameof(Item.Consume), nameof(Consume));
            Patch(typeof(Item), "SendFeedDataRPC", nameof(Feed));
            Patch(typeof(Item), "RemoveFeedDataRPC", nameof(FeedEnd));
            Patch(typeof(CharacterItems), nameof(CharacterItems.EquipSlotRpc), nameof(Stash));
            Patch(typeof(CharacterItems), nameof(CharacterItems.EquipSlotRpc), nameof(Equip), postfix: true);
            Patch(typeof(CharacterItems), nameof(CharacterItems.DropItemRpc), nameof(Drop));
            Patch(typeof(Action_ReduceUses), nameof(Action_ReduceUses.ReduceUsesRPC), nameof(ReduceUses));
            Patch(typeof(Player), nameof(Player.SyncInventoryRPC), nameof(InventorySynced), postfix: true);
            Patch(typeof(Player), "OnDestroy", nameof(PlayerDestroyed));
            Patch(typeof(Item), "Awake", nameof(Lifecycle), postfix: true);
            Patch(typeof(Item), nameof(Item.OnEnable), nameof(Lifecycle), postfix: true);
            Patch(typeof(Item), nameof(Item.OnDisable), nameof(Lifecycle), postfix: true);
            Patch(typeof(Item), "OnDestroy", nameof(Lifecycle));
            Patch(typeof(Item), "SetState", nameof(Changed), postfix: true);
            Patch(typeof(Item), nameof(Item.SetItemInstanceDataRPC), nameof(StructureChanged), postfix: true);
            Patch(typeof(Item), nameof(Item.SetCookedAmountRPC), nameof(Changed), postfix: true);
            Patch(typeof(Item), nameof(Item.SetKinematicRPC), nameof(Motion), postfix: true);
            Patch(typeof(Item), nameof(Item.ApplyForceRPC), nameof(Motion), postfix: true);
            Patch(typeof(ItemPhysicsSyncer), nameof(ItemPhysicsSyncer.OnDataReceived), nameof(PhysicsPacket), postfix: true);
            Patch(typeof(Player), nameof(Player.AddItem), nameof(InventoryChanged), postfix: true);
            Patch(typeof(Player), nameof(Player.EmptySlot), nameof(InventoryChanged), postfix: true);
            Patch(typeof(Player), nameof(Player.RPCRemoveItemFromSlot), nameof(InventoryChanged), postfix: true);
            Patch(typeof(Player), nameof(Player.RPC_SetInventory), nameof(InventoryChanged), postfix: true);
        }
        catch { harmony.UnpatchSelf(); throw; }
    }

    private void Patch(Type type, string method, string observer, bool postfix = false)
    {
        try
        {
            var target = AccessTools.DeclaredMethod(type, method) ?? throw new MissingMethodException(type.Name, method);
            var patch = new HarmonyMethod(typeof(ItemEventCapture), observer);
            harmony.Patch(target, prefix: postfix ? null : patch, postfix: postfix ? patch : null);
        }
        catch (Exception e)
        {
            HookFailures++;
            try { warning?.Invoke("物品事件入口未安装，其他录制保持可用：" + type.Name + "." + method + " · " + e.Message); }
            catch { }
        }
    }

    public static void Activate(bool value)
    {
        if (active == value) return;
        active = value;
        pending.Clear();
        overflowWarned = false;
    }

    public static ItemEvent[] Drain(double through)
    {
        if (pending.Count == 0 || pending.Peek().T > through) return Array.Empty<ItemEvent>();
        var result = new List<ItemEvent>();
        while (pending.Count > 0 && pending.Peek().T <= through) result.Add(pending.Dequeue());
        return result.Count == 0 ? Array.Empty<ItemEvent>() : result.ToArray();
    }

    internal static bool InventoryKnown(Player player) => player && syncedInventory.Contains(player.GetInstanceID());
    private static bool Gameplay => !ReplaySafety.Active && GameHandler.IsOnIslandAndInitialized;
    private static void InventorySynced(Player __instance)
    {
        try
        {
            // Initial inventory packets can arrive before MapHandler's initialized flag.
            if (ReplaySafety.Active || !__instance) return;
            if (syncedInventory.Count >= 256) syncedInventory.Clear();
            syncedInventory.Add(__instance.GetInstanceID());
            ItemCapture.MarkInventoryDirty();
        }
        catch { /* An observer must not disrupt incoming inventory RPCs. */ }
    }

    private static void PlayerDestroyed(Player __instance)
    {
        if (__instance) syncedInventory.Remove(__instance.GetInstanceID());
    }

    private static void Lifecycle(Item __instance)
    { try { if (!ReplaySafety.Active) ItemCapture.RegistryChanged(__instance); } catch { } }
    private static void Changed(Item __instance)
    { try { if (!ReplaySafety.Active) ItemCapture.MarkDirty(__instance); } catch { } }
    private static void StructureChanged(Item __instance)
    { try { if (!ReplaySafety.Active) ItemCapture.MarkDirty(__instance, true); } catch { } }
    private static void Motion(Item __instance)
    { try { if (!ReplaySafety.Active) ItemCapture.MarkMotion(__instance); } catch { } }
    private static void PhysicsPacket(ItemPhysicsSyncer __instance)
    { try { if (!ReplaySafety.Active) ItemCapture.MarkMotion(__instance.GetComponent<Item>()); } catch { } }
    private static void InventoryChanged()
    { if (!ReplaySafety.Active) ItemCapture.MarkInventoryDirty(); }

    private static void Emit(string kind, Item? item, Character? actor = null)
    {
        try
        {
            if (!active || !Gameplay || item == null) return;
            ItemCapture.MarkDirty(item);
            actor = actor != null ? actor : item.trueHolderCharacter;
            var value = new ItemEvent
            {
                Sequence = ++sequence, T = Time.timeAsDouble, Kind = kind,
                ActorId = actor != null ? Capture.Id(actor) : "", ItemKey = ItemCapture.Key(item),
                ItemId = item.itemID, Name = ItemCapture.Name(item),
            };
            if (pending.Count >= 4096)
            {
                pending.Dequeue();
                if (!overflowWarned) { overflowWarned = true; warn?.Invoke("物品事件队列已满，最早的未采样事件已丢弃。"); }
            }
            pending.Enqueue(value);
        }
        catch { /* Optional recording must never change the outcome of gameplay. */ }
    }

    private static void PrimaryStart(Item __instance) { if (!__instance.isUsingPrimary) Emit("use-primary-start", __instance); }
    private static void PrimaryFinish(Item __instance) => Emit("use-primary-finish", __instance);
    private static void PrimaryCancel(Item __instance) { if (__instance.isUsingPrimary && !__instance.finishedCast) Emit("use-primary-cancel", __instance); }
    private static void SecondaryStart(Item __instance) { if (!__instance.isUsingPrimary && !__instance.isUsingSecondary) Emit("use-secondary-start", __instance); }
    private static void SecondaryFinish(Item __instance) => Emit("use-secondary-finish", __instance);
    private static void SecondaryCancel(Item __instance) { if (__instance.isUsingSecondary && !__instance.finishedCast) Emit("use-secondary-cancel", __instance); }
    private static void Consume(Item __instance, int consumerID)
    {
        try
        {
            var view = PhotonNetwork.GetPhotonView(consumerID);
            Emit("consume", __instance, view ? view.GetComponent<Character>() : null);
        }
        catch { Emit("consume", __instance); }
    }
    private static void Equip(CharacterItems __instance, int slotID, int objectViewID)
    {
        try
        {
            if (slotID < 0 || objectViewID < 0) return;
            var c = __instance.GetComponent<Character>();
            Emit("equip", c ? c.data?.currentItem : null, c);
        }
        catch { }
    }
    private static void Stash(CharacterItems __instance, int objectViewID)
    {
        try
        {
            var c = __instance.GetComponent<Character>();
            var old = c ? c.data?.currentItem : null;
            if (old && (!old.photonView || old.photonView.ViewID != objectViewID)) Emit("stash", old, c);
        }
        catch { }
    }
    private static void Feed(Item __instance, int giverID)
    {
        try { var view = PhotonNetwork.GetPhotonView(giverID); Emit("feed-start", __instance, view ? view.GetComponent<Character>() : null); }
        catch { }
    }
    private static void FeedEnd(Item __instance, int giverID)
    {
        try { var view = PhotonNetwork.GetPhotonView(giverID); Emit("feed-end", __instance, view ? view.GetComponent<Character>() : null); }
        catch { }
    }
    private static void Drop(CharacterItems __instance)
    {
        try { var c = __instance.GetComponent<Character>(); Emit("drop", c ? c.data?.currentItem : null, c); }
        catch { }
    }
    private static void ReduceUses(Action_ReduceUses __instance)
    {
        try { Emit("use-charge", __instance.GetComponent<Item>()); }
        catch { }
    }

    public void Dispose()
    {
        Activate(false); pending.Clear(); syncedInventory.Clear(); warn = null; harmony.UnpatchSelf();
    }
}
