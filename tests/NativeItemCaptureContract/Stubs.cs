using System.Diagnostics.CodeAnalysis;
namespace UnityEngine
{
    public class Object
    {
        private static int sequence; private readonly int id = ++sequence;
        public string name = "berry"; public bool Destroyed;
        public int GetInstanceID() => id;
        public static implicit operator bool([NotNullWhen(true)] Object? value) => value is not null && !value.Destroyed;
    }
    public struct Scene { public int handle; public bool isLoaded, Valid; public bool IsValid() => Valid; }
    public class GameObject : Object
    {
        public Scene scene = new() { handle = 1, isLoaded = true, Valid = true };
        public bool activeInHierarchy = true;
        public readonly Transform transform;
        public readonly List<Component> Components = new(); public int ComponentReads, DirectoryFailures;
        public GameObject() { transform = new(this); }
        public void Attach(Component source) { Components.Add(source); source.gameObject = this; }
        public T? GetComponent<T>() where T : Component { ComponentReads++; return Components.OfType<T>().FirstOrDefault(); }
        public T[] GetComponentsInChildren<T>(bool inactive) where T : Component { ComponentReads++; if(DirectoryFailures-- > 0)throw new InvalidOperationException("directory not ready");return Components.OfType<T>().ToArray(); }
    }
    public class Transform : Object
    {
        public readonly GameObject gameObject; public Vector3 position,lossyScale=new(1,1,1);public Quaternion rotation=new(){w=1}; public int Failures;
        public int ChildRevision,ObservedChildRevision;
        public object[] ObservedChildren=Array.Empty<object>();
        public Transform(GameObject gameObject) { this.gameObject = gameObject; }
    }
    public class Component : Object
    {
        public GameObject gameObject = null!; public Transform transform => gameObject.transform;
        public Component() { new GameObject().Attach(this); }
        public T? GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
        public T? GetComponentInChildren<T>(bool inactive) where T : Component => gameObject.GetComponent<T>();
        public T[] GetComponentsInChildren<T>(bool inactive) where T : Component => gameObject.GetComponentsInChildren<T>(inactive);
    }
    public class MonoBehaviour : Component { }
    public class Animator : Component { }
    public class SkinnedMeshRenderer : Component { }
    public class Light : Component { }
    public class Rigidbody : Component { public bool isKinematic = true; public Vector3 linearVelocity, angularVelocity; }
    public struct Vector3 { public float x,y,z; public float sqrMagnitude => x*x+y*y+z*z; public Vector3(float x,float y,float z) { this.x=x;this.y=y;this.z=z; } }
    public struct Quaternion {public float x,y,z,w;}
    public struct Color { public float r,g,b,a; public static Color white => new(1,1,1,1); public Color(float r,float g,float b,float a) {this.r=r;this.g=g;this.b=b;this.a=a;} }
    public static class Time { public static double timeAsDouble; }
    public static class Mathf { public static float Clamp01(float value) => Math.Clamp(value,0,1); }
}
namespace UnityEngine.SceneManagement { public static class SceneManager { public static UnityEngine.Scene GetActiveScene() => new() { handle=1,Valid=true,isLoaded=true }; } }
namespace Photon.Pun
{
    public class PhotonView : UnityEngine.MonoBehaviour { }
    public static class PhotonNetwork { public static bool IsMasterClient=true; }
}
public struct Option<T> { public bool IsSome; public T Value; }
public enum ItemState { Ground,Held,InBackpack }
public struct BackpackReference
{
    public enum BackpackType { Item,Equipped }
    public BackpackType type; public Photon.Pun.PhotonView? view;
    public bool IsOnMyBack() => false;
}
public class Item : UnityEngine.MonoBehaviour
{
    public static readonly List<Item> ALL_ITEMS = new();
    [Flags] public enum ItemTags { Bird=1 }
    public ItemTags itemTags; public ItemState itemState; public ushort itemID=1;
    public Character? trueHolderCharacter; public Option<(byte,BackpackReference)> backpackReference;
    public UnityEngine.Transform? backpackSlotTransform; public ItemInstanceData data = null!; public UIData? UIData;
    public UnityEngine.Rigidbody? rig = new(); public bool isUsingPrimary,isUsingSecondary; public float progress;
}
public class UIData { public string itemName="berry"; }
public class MobItem : Item { }
public class Backpack : Item { public int slotCount; }
public class Character : UnityEngine.MonoBehaviour
{ public bool IsPlayerControlled=true,IsLocal=true; public CharacterRefs? refs; public Player? player; }
public class CharacterRefs { public UnityEngine.Transform? hip; public UnityEngine.Animator? animator; public CharacterItems? items; public BackpackHandler? backpackHandler; }
public class CharacterItems : UnityEngine.MonoBehaviour { public Option<byte> currentSelectedSlot; }
public class BackpackHandler { public BackpackVisuals? activeBackpackVisuals; }
public class BackpackVisuals { public int slotCount; }
public class Player : UnityEngine.MonoBehaviour { public ItemSlot[]? itemSlots; public BackpackSlot? backpackSlot; public ItemSlot? tempFullSlot; }
public class ItemSlot { public Item? prefab; public ItemInstanceData? data; public bool IsEmpty() => prefab is null; }
public class BackpackSlot : ItemSlot { public int backpackType; public string GetPrefabName() => "bag"; }
public class ItemInstanceData { public Guid guid; public Dictionary<DataEntryKey,DataEntryValue>? data; }
public enum DataEntryKey { Color,ItemUses,CookedAmount,Fuel,Used,FlareActive,PowerEnabled,BackpackData,UseRemainingPercentage }
public class DataEntryValue { }
public class IntItemData : DataEntryValue { public int Value; }
public class OptionableIntItemData : DataEntryValue { public int Value; public bool HasData; }
public class FloatItemData : DataEntryValue { public float Value; }
public class BoolItemData : DataEntryValue { public bool Value; }
public class OptionableBoolItemData : DataEntryValue { public bool Value,HasData; }
public class ColorItemData : DataEntryValue { public UnityEngine.Color Value; }
public class BackpackData : DataEntryValue { public ItemSlot[]? itemSlots; }
public static class ItemDatabase
{
    public static bool TryGetItem(ushort id,out Item? item) { item=null;return false; }
    public static bool TryGetItem(string id,out Item? item) { item=null;return false; }
}
public class CompassPointer : UnityEngine.MonoBehaviour { public enum CompassType { Normal,Warp } public CompassType compassType;public bool isActiveAndEnabled;public float warpSpeed,speedMultiplier; }
public class StickyItemComponent : UnityEngine.MonoBehaviour { public Character? stuckToCharacter; }
public class ItemCooking : UnityEngine.MonoBehaviour { }
public class ItemParticles : UnityEngine.MonoBehaviour { }
public class ItemUseFeedback : UnityEngine.MonoBehaviour { }
public class ItemImpactSFX : UnityEngine.MonoBehaviour { }
public class ItemVFX : UnityEngine.MonoBehaviour { }
public class ItemTorch : UnityEngine.MonoBehaviour { }
public class ItemPhysicsSyncer : UnityEngine.MonoBehaviour { }
public class LootData : UnityEngine.MonoBehaviour { }
public class ColorblindVariant : UnityEngine.MonoBehaviour { }
public class CenterOfMass : UnityEngine.MonoBehaviour { }
public class ChessStabilizer : UnityEngine.MonoBehaviour { }
public class EventOnItemCollision : UnityEngine.MonoBehaviour { }
public class HandVisual : UnityEngine.MonoBehaviour { }
public class Bonkable : UnityEngine.MonoBehaviour { }
public class Breakable : UnityEngine.MonoBehaviour { }
public class ItemActionBase : UnityEngine.MonoBehaviour { }
public class Mob : UnityEngine.MonoBehaviour { }
public class TumbleWeed : UnityEngine.MonoBehaviour { }
public class Mandrake : UnityEngine.MonoBehaviour { }
public class BeeSwarm : UnityEngine.MonoBehaviour { }
public class SlipperyJellyfish : UnityEngine.MonoBehaviour { }
public class VenusFlyTrap : UnityEngine.MonoBehaviour { }
public class Spider : UnityEngine.MonoBehaviour { }
public class MushroomZombie : UnityEngine.MonoBehaviour { }
namespace Peak { public class EarlyWorm : UnityEngine.MonoBehaviour { } public class ItemOptimizer : UnityEngine.MonoBehaviour { } public class PhotonCleanupHelper : UnityEngine.MonoBehaviour { } }
namespace PeakReplayLab
{
    internal sealed class ActorFrame { public string Id="";public float[] Position=Array.Empty<float>(); }
    internal sealed class ObjectPose { public float[] Position=new float[3], Rotation=new float[]{0,0,0,1}, Scale=new float[]{1,1,1};public bool[] Active=Array.Empty<bool>();public object[] Nodes=Array.Empty<object>(); }
    internal sealed class NativeRendererFrame { }
    internal sealed class NativeLightFrame { public bool Enabled { get; set; } }
    internal sealed class ItemFrame
    {
        public string Key="",Prefab="",Name="",HolderId="";public int ItemId,State,Uses,Cooked;public bool ProgressKnown,Primary,Secondary,Used,Lit,RevealBackpackContents;
        public float Progress,Fuel;public float[] Color=Array.Empty<float>();public ObjectPose Pose=new();public NativeRendererFrame[]? Visuals;public NativeLightFrame[]? Lights;
        public CreatureIdleAnimation? IdleAnimation;
        public bool SampleTimeKnown, SampleDiscontinuity;public double SampleTime;public int ClockMode;
        internal ItemFrame WithIdleAnimation(CreatureIdleAnimation? animation) {var copy=(ItemFrame)MemberwiseClone();copy.IdleAnimation=animation;return copy;}
        internal ItemFrame WithObservation(double now,int mode,bool discontinuity=false) {var copy=(ItemFrame)MemberwiseClone();copy.SampleTimeKnown=true;copy.SampleTime=now;copy.ClockMode=mode;copy.SampleDiscontinuity=discontinuity;return copy;}
    }
    internal sealed class InventoryFrame { public bool Empty=true,Equipped,Backpack;public int Slot,ItemId,Uses,Cooked;public string Instance="";public float Fuel,UiFuel; }
    internal static class Capture { internal static string Id(Character character) => "player"; }
    internal static class CreatureReplayRules {public const int ActiveClock=0,IdleClock=1,SleepingClock=2;}
    internal static class ItemEventCapture { internal static bool InventoryKnown(Player player) => true; }
    internal static class NativeSpawnOriginCapture { internal static void Detach(UnityEngine.GameObject source) { } }
    internal sealed class NativeCaptureAnimationActivity
    {
        public NativeCaptureAnimationActivity(UnityEngine.Animator[] sources) { }
        public static int TotalReads;
        public long ProbeCount { get; set; } public long Revision { get; set; } public bool Advancing { get; set; }=true;
        public void Read(double now,bool followSceneClock=false) {TotalReads++;ProbeCount++;}
        public void Invalidate() { }
    }
    internal static class NativeVisualAppearance
    {
        internal static int Reads;
        internal static bool NeedsCaptureAtTime(UnityEngine.Transform source,NativeRendererFrame[]? old,double now,bool[] active,bool allowAnimated,bool followSceneClock=false) => false;
        internal static NativeRendererFrame[]? CaptureAtTime(UnityEngine.Transform source,NativeRendererFrame[]? old,double now,bool force=false,bool allowAnimated=false,bool followSceneClock=false) {Reads++;return old ?? Array.Empty<NativeRendererFrame>();}
    }
    internal static class NativeLightAppearance { internal static int Reads;internal static NativeLightFrame[] Capture(UnityEngine.Transform source,NativeLightFrame[]? old) {Reads++;return old ?? Array.Empty<NativeLightFrame>();} }
    internal static class VisualReplica
    {
        internal static int Reads,Releases; internal static long Clock,ReadCost;
        internal static ObjectPose Capture(UnityEngine.Transform source,bool children)
        {
            Reads++;Clock+=ReadCost;if(source.Failures-- > 0)throw new InvalidOperationException("not ready");
            if(children&&source.ChildRevision!=source.ObservedChildRevision){source.ObservedChildRevision=source.ChildRevision;source.ObservedChildren=new object[]{source.ChildRevision};}
            return new(){Position=new[]{source.position.x,source.position.y,source.position.z},Nodes=source.ObservedChildren};
        }
        internal static bool Same(ObjectPose a,ObjectPose b) => ReferenceEquals(a,b)||a.Position.SequenceEqual(b.Position)&&a.Rotation.SequenceEqual(b.Rotation)&&a.Scale.SequenceEqual(b.Scale)&&ReferenceEquals(a.Active,b.Active)&&ReferenceEquals(a.Nodes,b.Nodes);
        internal static void ReleaseCapture(UnityEngine.Transform source) { Releases++; }
        internal static void InvalidateCapture(UnityEngine.Transform source,bool hierarchyChanged) { }
    }
    internal static class BackpackAttachmentPolicy { internal static bool IsKnownEquippedSlot(string key,int itemId,byte slot,bool known,InventoryFrame[] inventory) => false; }
    internal sealed class CaptureSegmentScope
    {
        internal static CaptureSegmentScope? Active { get; set; }
        internal SegmentCaptureClient CreateClient(Action<UnityEngine.GameObject> admit,Action<int> retire,Func<UnityEngine.GameObject,bool> preserve) => new();
    }
    internal sealed class SegmentCaptureClient : IDisposable
    { internal bool Admit(UnityEngine.GameObject source,bool urgent=false) => true;internal void Forget(int id) { } public void Dispose() { } }
}
