using System.Runtime.CompilerServices;

// Controlled managed fixtures with signatures audited against this installed
// game's Assembly-CSharp (item-hook-signatures.json). They exercise Harmony's
// real injection/binding, not Unity physics, rendering or the capture scheduler.
namespace UnityEngine
{
    public class Object
    {
        private static int next; private readonly int id = ++next;
        public bool Destroyed;
        public int GetInstanceID() => id;
        public static implicit operator bool([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Object? value) => value is not null && !value.Destroyed;
    }
    public class Component : Object
    {
        public readonly GameObject gameObject = new();
        public Item? ItemOwner;
        public Character? CharacterOwner;
        public readonly Transform transform = new();
        public int NativeCalls;
        public int ComponentLookups;
        public Exception? NativeFailure;
        public T? GetComponent<T>() where T : Component { ComponentLookups++; return this is T self ? self : ItemOwner is T item ? item : CharacterOwner as T; }
        protected void Native() { if (NativeFailure != null) throw NativeFailure; NativeCalls++; }
    }
    public sealed class GameObject : Object { }
    public sealed class Transform { public Vector3 position; public Vector3 localScale = new(1,1,1); }
    public class Rigidbody : Component
    {
        private bool kinematic; private Vector3 p; private Quaternion q = new() { w = 1 };
        public int KinematicReads, PositionReads, RotationReads;
        public bool isKinematic { get { KinematicReads++; return kinematic; } set => kinematic = value; }
        public Vector3 position { get { PositionReads++; return p; } set => p = value; }
        public Quaternion rotation { get { RotationReads++; return q; } set => q = value; }
        public Vector3 linearVelocity, angularVelocity;
    }
    public class Collider : Component { public Rigidbody? attachedRigidbody; public bool DirectItem = true; }
    public class Collision { }
    public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } }
    public struct Quaternion { public float x, y, z, w; }
    public static class Time { public static double timeAsDouble; }
}
namespace System.Diagnostics.CodeAnalysis
{
    // .NET Framework lacks this annotation. Match the fake-null fixture's
    // successful bool conversion without hiding nullable warnings globally.
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class NotNullWhenAttribute : Attribute
    { public bool ReturnValue { get; } public NotNullWhenAttribute(bool returnValue) { ReturnValue = returnValue; } }
}
namespace Photon.Pun
{
    public class PhotonView : UnityEngine.Component { public int ViewID; }
    public static class PhotonNetwork
    {
        public static readonly Dictionary<int, PhotonView> Views = new();
        public static PhotonView? GetPhotonView(int id) => Views.TryGetValue(id, out var view) ? view : null;
    }
}
namespace Zorro.Core { public struct Optionable<T> { public T Value; } }
public class ItemInstanceData { }
public class ItemSlot { }
public struct BackpackReference { }
public enum ItemState { Ground, Held, Backpack }
public struct ItemPhysicsSyncData
{
    public int Stamp;
    public UnityEngine.Vector3 position, linearVelocity, angularVelocity;
    public UnityEngine.Quaternion rotation;
}
public class CharacterData { public Item? currentItem; }
public class Character : UnityEngine.Component { public readonly CharacterData data = new(); public string ActorId = "scout"; }
public static class GameHandler { public static bool IsOnIslandAndInitialized = true; }
public class Item : UnityEngine.Component
{
    public Character? trueHolderCharacter;
    public Photon.Pun.PhotonView? photonView;
    public ushort itemID = 1;
    public bool isUsingPrimary, isUsingSecondary, finishedCast;
    public UnityEngine.Rigidbody rig = new();
    public ItemPhysicsSyncer? physicsSyncer;
    public bool Kinematic => rig.isKinematic;
    public int WasActiveCalls;
    public ItemState State;
    public float Cooked;
    [MethodImpl(MethodImplOptions.NoInlining)] public void StartUsePrimary() { Native(); isUsingPrimary = true; }
    [MethodImpl(MethodImplOptions.NoInlining)] private void FinishCastPrimary() { Native(); finishedCast = true; }
    public void FinishPrimary() => FinishCastPrimary();
    [MethodImpl(MethodImplOptions.NoInlining)] public void CancelUsePrimary() { Native(); isUsingPrimary = false; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void StartUseSecondary() { Native(); isUsingSecondary = true; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void FinishCastSecondary() { Native(); finishedCast = true; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void CancelUseSecondary() { Native(); isUsingSecondary = false; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void Consume(int consumerID) => Native();
    [MethodImpl(MethodImplOptions.NoInlining)] internal void SendFeedDataRPC(int giverID, int recieverID, int itemID, float totalUsingTime) => Native();
    [MethodImpl(MethodImplOptions.NoInlining)] internal void RemoveFeedDataRPC(int giverID) => Native();
    [MethodImpl(MethodImplOptions.NoInlining)] private void Awake() => Native();
    public void InvokeAwake() => Awake();
    [MethodImpl(MethodImplOptions.NoInlining)] public void OnEnable() => Native();
    [MethodImpl(MethodImplOptions.NoInlining)] public void OnDisable() => Native();
    [MethodImpl(MethodImplOptions.NoInlining)] private void OnDestroy() { Native(); Destroyed = true; }
    public void InvokeDestroy() => OnDestroy();
    [MethodImpl(MethodImplOptions.NoInlining)] private void SetState(ItemState state, Character holder) { Native(); State = state; trueHolderCharacter = holder; }
    public void ChangeState(ItemState state, Character holder) => SetState(state, holder);
    [MethodImpl(MethodImplOptions.NoInlining)] public void SetItemInstanceDataRPC(ItemInstanceData data) => Native();
    [MethodImpl(MethodImplOptions.NoInlining)] public void SetCookedAmountRPC(int cookedAmount) { Native(); Cooked = cookedAmount; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void SetKinematicRPC(bool value, UnityEngine.Vector3 position, UnityEngine.Quaternion rotation)
    { Native(); SetKinematic(value); rig.position = position; rig.rotation = rotation; transform.position = position; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void SetKinematic(bool value) { Native(); rig.isKinematic = value; if (!value) WasActiveCalls++; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void SetKinematicAndResetSyncData(bool value, UnityEngine.Vector3 position, UnityEngine.Quaternion rotation)
    { Native(); SetKinematic(value); rig.position = position; rig.rotation = rotation; transform.position = position;
      if (value) { rig.linearVelocity = default; rig.angularVelocity = default; physicsSyncer?.ResetRecievedData(); } }
    [MethodImpl(MethodImplOptions.NoInlining)] internal void Move(UnityEngine.Vector3 position, UnityEngine.Quaternion rotation) { Native(); transform.position = position; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void ApplyForceRPC(UnityEngine.Vector3 force, UnityEngine.Vector3 pos) => Native();
    [MethodImpl(MethodImplOptions.NoInlining)] public void PutInBackpackRPC(byte slotID, BackpackReference backpackReference) => Native();
    [MethodImpl(MethodImplOptions.NoInlining)] public void SetUseRemainingPercentage(float percentage) => Native();
    public static bool TryGetItemFromCollider(UnityEngine.Collider collider, out Item item) { item = collider.ItemOwner!; return collider.DirectItem && item; }
}
public class CharacterItems : UnityEngine.Component
{
    public Item? EquipResult;
    [MethodImpl(MethodImplOptions.NoInlining)] public void EquipSlotRpc(int slotID, int objectViewID) { Native(); CharacterOwner!.data.currentItem = EquipResult; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void DropItemRpc(float throwCharge, byte slotID, UnityEngine.Vector3 spawnPos, UnityEngine.Vector3 velocity, UnityEngine.Quaternion rotation, ItemInstanceData itemInstanceData, bool cacheToDroppedItems) { Native(); CharacterOwner!.data.currentItem = null; }
}
public class Action_ReduceUses : UnityEngine.Component { [MethodImpl(MethodImplOptions.NoInlining)] public void ReduceUsesRPC() => Native(); }
public class Player : UnityEngine.Component
{
    [MethodImpl(MethodImplOptions.NoInlining)] public void SyncInventoryRPC(byte[] data, bool forceSync) => Native();
    [MethodImpl(MethodImplOptions.NoInlining)] private void OnDestroy() { Native(); Destroyed = true; }
    public void InvokeDestroy() => OnDestroy();
    [MethodImpl(MethodImplOptions.NoInlining)] public bool AddItem(ushort itemID, ItemInstanceData instanceData, out ItemSlot slot) { Native(); slot = new ItemSlot(); return true; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void EmptySlot(Zorro.Core.Optionable<byte> slot, bool andBroadcast = true) => Native();
    [MethodImpl(MethodImplOptions.NoInlining)] public void RPCRemoveItemFromSlot(byte slot) => Native();
    [MethodImpl(MethodImplOptions.NoInlining)] public void RPC_SetInventory(byte[] data) => Native();
}
public class ItemPhysicsSyncer : UnityEngine.Component
{
    public bool Send;
    public ItemPhysicsSyncData Data;
    [MethodImpl(MethodImplOptions.NoInlining)] public void OnDataReceived(ItemPhysicsSyncData data) { Native(); Data = data; }
    [MethodImpl(MethodImplOptions.NoInlining)] public ItemPhysicsSyncData GetDataToWrite() { Native(); return Data; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void ResetRecievedData() { Native(); Data = default; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void ForceSyncForFrames(int frames = 10) => Native();
    [MethodImpl(MethodImplOptions.NoInlining)] public bool ShouldSendData() { Native(); return Send; }
}
public class ItemScaleSyncer : UnityEngine.Component
{
    public float currentScale = 1;
    public readonly UnityEngine.Transform Mesh = new();
    [MethodImpl(MethodImplOptions.NoInlining)] private void ApplyScale(float scale)
    { Native(); Mesh.localScale = new UnityEngine.Vector3(scale,scale,scale); }
    public void Update(bool inBackpack = false) => ApplyScale(currentScale * (inBackpack ? .5f : 1));
}
public class WindChillZone : UnityEngine.Component
{
    public float windForce=15,windItemFactor=1,windIntensity;public bool useIntensityCurve;
    [MethodImpl(MethodImplOptions.NoInlining)] private void AddWindForceToItem(Item item) => Native();
    public void Wind(Item item) => AddWindForceToItem(item);
}
public class ItemMoveZone : UnityEngine.Component
{
    public float forceMultiplier=1;
    [MethodImpl(MethodImplOptions.NoInlining)] private void OnTriggerStay(UnityEngine.Collider other) => Native();
    public void Touch(UnityEngine.Collider collider) => OnTriggerStay(collider);
}
public class ItemImpactSFX : UnityEngine.Component
{
    [MethodImpl(MethodImplOptions.NoInlining)] private void OnCollisionEnter(UnityEngine.Collision collision) => Native();
    public void Collide() => OnCollisionEnter(new UnityEngine.Collision());
}
public class AOE : UnityEngine.Component
{
    public bool canLaunchItems = true; public float range = 5;
    [MethodImpl(MethodImplOptions.NoInlining)] public void Explode() => Native();
}
public class ItemCooking : UnityEngine.Component
{
    public enum NetworkedCooking { None, Cooking, Cooked }
    public NetworkedCooking State;
    [MethodImpl(MethodImplOptions.NoInlining)] private void SyncCookingStateRPC(NetworkedCooking newState) { Native(); State = newState; }
    public void SyncCooking(NetworkedCooking state) => SyncCookingStateRPC(state);
    [MethodImpl(MethodImplOptions.NoInlining)] public void UpdateCookedBehavior() => Native();
}
public class ItemParticles : UnityEngine.Component
{
    public bool Smoke;
    [MethodImpl(MethodImplOptions.NoInlining)] public void EnableSmoke(bool active) { Native(); Smoke = active; }
}
public class EventOnItemCollision : UnityEngine.Component
{
    [MethodImpl(MethodImplOptions.NoInlining)] private void TriggerEvent() => Native();
    public void Trigger() => TriggerEvent();
}
namespace Peak
{
    public static class ItemOptimizationManager
    {
        public static int NativeCalls;
        [MethodImpl(MethodImplOptions.NoInlining)] public static void RegisterItem(Item item) => NativeCalls++;
        [MethodImpl(MethodImplOptions.NoInlining)] public static void DeregisterItem(Item item) => NativeCalls++;
    }
}
namespace PeakReplayLab
{
    internal static class NativeSpawnOriginCapture
    {
        internal static readonly List<UnityEngine.GameObject> Detached = new();
        internal static void Detach(UnityEngine.GameObject source) => Detached.Add(source);
    }
    internal static class ReplaySafety { public static bool Active; }
    internal static class Capture { public static string Id(Character value) => value.ActorId; }
    public class ItemEvent
    {
        public long Sequence; public double T; public string Kind = "", ActorId = "", ItemKey = "", Name = ""; public int ItemId;
    }
    internal sealed class Notice
    {
        public readonly string Kind; public readonly Item? Item; public readonly float Radius; public readonly UnityEngine.Vector3 Position;public readonly bool Continuous;
        public Notice(string kind, Item? item, float radius = 0, UnityEngine.Vector3 position = default,bool continuous=false) { Kind = kind; Item = item; Radius = radius; Position = position;Continuous=continuous; }
    }
    internal static class ItemCapture
    {
        public static bool EventHooksHealthy;
        public static readonly List<Notice> Notices = new();
        public static Action<string, Item?>? OnNotify;
        private static void Emit(string kind, Item? item) { Notices.Add(new Notice(kind, item)); OnNotify?.Invoke(kind, item); }
        public static void MarkDirty(Item item, bool hierarchyChanged = false) => Emit(hierarchyChanged ? "structure" : "dirty", item);
        public static void MarkMotion(Item item) => Emit("motion", item);
        public static void MarkMotionObservation(Item item,bool continuousObservation)
        {Notices.Add(new Notice("motion",item,continuous:continuousObservation));OnNotify?.Invoke("motion",item);}
        public static void MarkMetadataObservation(Item item)=>Emit("metadata",item);
        public static void MarkChildScaleObservation(Item item)=>Emit("child-scale",item);
        public static void RegistryChanged(Item item) => Emit("registry", item);
        public static void Destroying(Item item) => Emit("destroy", item);
        public static void MarkInventoryDirty() => Emit("inventory", null);
        public static void WakeArea(UnityEngine.Vector3 position, float radius) { Notices.Add(new Notice("area", null, radius, position)); OnNotify?.Invoke("area", null); }
        public static string Key(Item item) => "item-" + item.GetInstanceID();
        public static string Name(Item item) => "fixture";
        public static void Reset() { Notices.Clear(); NativeSpawnOriginCapture.Detached.Clear(); OnNotify = null; ReplaySafety.Active = false; GameHandler.IsOnIslandAndInitialized = true; }
    }
}
