using System.Collections;
using System.Runtime.CompilerServices;
namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class NotNullWhenAttribute : Attribute
    { public bool ReturnValue { get; } public NotNullWhenAttribute(bool returnValue) => ReturnValue = returnValue; }
}
namespace UnityEngine
{
    public class Object
    {
        private static int sequence;
        private readonly int identity = ++sequence;
        public int GetInstanceID() => identity;
        public static implicit operator bool([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Object? value) => value is not null;
    }
    public static class Time { public static double timeAsDouble; public static int frameCount; }
    public class GameObject : Object
    {
        public bool activeSelf = true;
        public bool VerifiedTrap = true;
        public bool ParentActive = true;
        public bool activeInHierarchy => activeSelf && ParentActive;
        public Transform transform;
        public GameObject() { transform = new Transform(this); }
        public void SetActive(bool value) => activeSelf = value;
    }
    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform => gameObject.transform;
        public Component() { gameObject = new GameObject(); }
        protected Component(GameObject owner) { gameObject = owner; }
        public Component? Parent;
        public Character? CharacterComponent;
        public static int ComponentLookups;
        public T? GetComponent<T>() where T : Component { ComponentLookups++; return CharacterComponent as T; }
        public bool LastIncludeInactive;
        public T? GetComponentInParent<T>(bool includeInactive = false) where T : Component
        { LastIncludeInactive = includeInactive; for (Component? node = this; node != null; node = node.Parent) if (node is T match) return match; return null; }
    }
    public class Transform : Component
    {
        internal Transform(GameObject owner) : base(owner) { }
        private Vector3 location;
        public static int PositionReads;
        public bool ThrowOnPositionRead;
        public Vector3 position { get { PositionReads++; if (ThrowOnPositionRead) throw new InvalidOperationException("native transform unavailable"); return location; } set => location = value; }
    }
    public struct Vector3 : IEquatable<Vector3>
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x=x; this.y=y; this.z=z; }
        public float sqrMagnitude => x*x+y*y+z*z;
        public static Vector3 operator -(Vector3 left, Vector3 right) => new(left.x-right.x,left.y-right.y,left.z-right.z);
        public static bool operator ==(Vector3 left, Vector3 right) => left.Equals(right);
        public static bool operator !=(Vector3 left, Vector3 right) => !left.Equals(right);
        public bool Equals(Vector3 other) => x==other.x && y==other.y && z==other.z;
        public override bool Equals(object? other) => other is Vector3 v && Equals(v);
        public override int GetHashCode() => x.GetHashCode() ^ y.GetHashCode() ^ z.GetHashCode();
    }
}
namespace UnityEngine.Events
{
    public class UnityEvent
    {
        public Action? Callback;
        [MethodImpl(MethodImplOptions.NoInlining)] public void Invoke() => Callback?.Invoke();
    }
}
public class Spider : UnityEngine.Component
{
    public enum SpiderState { Idle, Dropped, Grabbing, LetGo }
    public SpiderState spiderState;
    private float _stunnedTime;
    public bool NativeActionComplete;
    public int NativeActionCalls;
    public void NativeStun(float remaining) => _stunnedTime = remaining;
    public float NativeStunRemaining => _stunnedTime;
    [MethodImpl(MethodImplOptions.NoInlining)] public void RPCA_DropSpider(Photon.Pun.PhotonView target, float distance)
    { NativeActionComplete = false; spiderState = SpiderState.Dropped; NativeActionCalls++; NativeActionComplete = true; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void RPCA_GrabCharacter(Photon.Pun.PhotonView target)
    { NativeActionComplete = false; spiderState = SpiderState.Grabbing; NativeActionCalls++; NativeActionComplete = true; }
    [MethodImpl(MethodImplOptions.NoInlining)] private void RPCA_LetGo()
    { NativeActionComplete = false; spiderState = SpiderState.LetGo; NativeActionCalls++; NativeActionComplete = true; }
    [MethodImpl(MethodImplOptions.NoInlining)] private void BonkRPC()
    { NativeActionComplete = false; _stunnedTime = 5; NativeActionCalls++; NativeActionComplete = true; }
    public void NativeLetGo() => RPCA_LetGo();
    public void NativeBonk() => BonkRPC();
    [MethodImpl(MethodImplOptions.NoInlining)] private void Awake() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private void OnDestroy() { }
}
public class MushroomZombie : UnityEngine.Component
{
    [MethodImpl(MethodImplOptions.NoInlining)] private void Awake() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private void OnDestroy() { }
    [MethodImpl(MethodImplOptions.NoInlining)] public void RPC_Arise() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private void RPC_SetOutfit() { }
}
public class Condor : UnityEngine.Component
{
    public bool NativeActionComplete;
    [MethodImpl(MethodImplOptions.NoInlining)] private void Start() { }
    [MethodImpl(MethodImplOptions.NoInlining)] public void RPCA_CondorAction() { NativeActionComplete = true; }
}
public class FrogTongue : UnityEngine.Component
{
    public bool NativeActionComplete;
    public bool WallCollision;
    [MethodImpl(MethodImplOptions.NoInlining)] private void OnEnable() { }
    [MethodImpl(MethodImplOptions.NoInlining)] public void Reposition() { NativeActionComplete = true; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void Attack() { NativeActionComplete = true; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void HopAway() { NativeActionComplete = true; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void LetGo() { NativeActionComplete = true; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void OnCollisionEnter() { if(WallCollision)HopAway(); }
}
public class Mob : UnityEngine.Component
{
    public bool sleeping;
    public bool NearCharacter, EligibleForSleep = true, NativeSleepComplete;
    public readonly UnityEngine.GameObject visuals = new();
    public bool AnimatorEnabled = true;
    [MethodImpl(MethodImplOptions.NoInlining)] public void UpdateSleeping()
    { NativeSleepComplete = false; visuals.SetActive(!sleeping); AnimatorEnabled = !sleeping; NativeSleepComplete = true; }
    public void TestSleepMode()
    {
        if (sleeping) { if (NearCharacter) { sleeping = false; UpdateSleeping(); } }
        else if (EligibleForSleep && !NearCharacter) { sleeping = true; UpdateSleeping(); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] public void Awake() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private void OnDestroy() { }
}
public class Beetle : Mob { }
namespace Photon.Pun { public class PhotonView : UnityEngine.Component { } }
public class Scoutmaster : UnityEngine.Component
{
    public bool enabled = true;
    private Character? target;
    public bool ThrowOnSignalRead;
    public Character? currentTarget { get { if (ThrowOnSignalRead) throw new InvalidOperationException("native scout unavailable"); return target; } set => target = value; }
    public Character? discovered;
    [MethodImpl(MethodImplOptions.NoInlining)] public void OnEnable() { }
    [MethodImpl(MethodImplOptions.NoInlining)] public void Start() { }
    [MethodImpl(MethodImplOptions.NoInlining)] public void OnDisable() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private void OnDestroy() { }
}
public enum BodypartType { Torso }
public class Bodypart : UnityEngine.Component { }
public class CharacterRagdoll : UnityEngine.Component { public readonly Dictionary<BodypartType, Bodypart> partDict = new(); }
public class CharacterData : UnityEngine.Object { public UnityEngine.Transform? spawnPoint; }
public class Character : UnityEngine.Component
{
    public class CharacterRefs { public CharacterRagdoll? ragdoll = new(); }
    public static readonly UnityEngine.Vector3 ScoutmasterHideySpot = new(0, 0, 5000);
    public CharacterData? data = new();
    public CharacterRefs? refs = new();
    public event Action<Character>? WarpCompleted;
    public bool warping;
    private UnityEngine.Vector3 _lastWarpTarget;
    public UnityEngine.Vector3 WarpTarget => _lastWarpTarget;
    public float timeLastWarped { get; private set; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void WarpPlayer(UnityEngine.Vector3 position, bool poof = false)
    { warping = true; _lastWarpTarget = position; }
    public void CompleteWarp()
    {
        if (refs?.ragdoll != null && refs.ragdoll.partDict.TryGetValue(BodypartType.Torso, out var torso)) torso.transform.position = WarpTarget;
        timeLastWarped = 1; warping = false; WarpCompleted?.Invoke(this);
    }
}
public class Antlion : UnityEngine.Component
{
    public bool NativeActionComplete;
    [MethodImpl(MethodImplOptions.NoInlining)] public void Start() { }
    [MethodImpl(MethodImplOptions.NoInlining)] public void RPCA_Attack() { NativeActionComplete = true; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void RPCA_SetClosestTarget() { NativeActionComplete = true; }
}
public class VenusFlyTrap : UnityEngine.Component
{
    public bool NativeActionComplete;
    [MethodImpl(MethodImplOptions.NoInlining)] public void Start() { }
    [MethodImpl(MethodImplOptions.NoInlining)] public void RPC_BitePlayer() { NativeActionComplete = true; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void RPC_LetGo() { NativeActionComplete = true; }
}
public class TumbleWeed : UnityEngine.Component
{
    [MethodImpl(MethodImplOptions.NoInlining)] public void Start() { }
    [MethodImpl(MethodImplOptions.NoInlining)] public void OnCollisionEnter() { }
}
public class Skelleton : UnityEngine.Component
{
    public bool NativePoseCopied;
    [MethodImpl(MethodImplOptions.NoInlining)] public void SpawnSkelly() { NativePoseCopied = true; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void SetCharacter() { }
}
public class Capybara : UnityEngine.Component { [MethodImpl(MethodImplOptions.NoInlining)] public void OnEnable() { } }
public class MirageLuggage : UnityEngine.Component
{
    public float Mirage;
    [MethodImpl(MethodImplOptions.NoInlining)] public void OnEnable() { Mirage = 1; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void OnDisable() { Mirage = 0; }
}
public class Luggage : UnityEngine.Component
{
    public bool IsOpen;
    public float timeToOpen = 2;
    [MethodImpl(MethodImplOptions.NoInlining)] public void Awake() { }
    [MethodImpl(MethodImplOptions.NoInlining)] public void OnDestroy() { }
    [MethodImpl(MethodImplOptions.NoInlining)] public void Interact() { }
    [MethodImpl(MethodImplOptions.NoInlining)] public void CancelCast() { }
    [MethodImpl(MethodImplOptions.NoInlining)] public void OpenLuggageRPC(bool spawnItems) { IsOpen = true; }
}
public class RespawnChest : Luggage
{
    public bool Skeleton = true;
    [MethodImpl(MethodImplOptions.NoInlining)] public void RemoveSkeletonRPC() { Skeleton = false; }
}
public class WindChillZone : UnityEngine.Component
{
    public bool NativeComplete;
    [MethodImpl(MethodImplOptions.NoInlining)] public void Awake() { NativeComplete = true; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void Start() { }
}
public class StormVisual : UnityEngine.Component { [MethodImpl(MethodImplOptions.NoInlining)] public void Start() { } }
public class LavaRising : UnityEngine.Component
{
    [MethodImpl(MethodImplOptions.NoInlining)] public void Awake() { }
    [MethodImpl(MethodImplOptions.NoInlining)] public void Start() { }
}
public class MovingLava : UnityEngine.Component { [MethodImpl(MethodImplOptions.NoInlining)] public void Start() { } }
public class OrbFogHandler : UnityEngine.Component
{
    [MethodImpl(MethodImplOptions.NoInlining)] public void Awake() { }
    [MethodImpl(MethodImplOptions.NoInlining)] public void Start() { }
}
public class FogSphere : UnityEngine.Component { [MethodImpl(MethodImplOptions.NoInlining)] public void Start() { } }
namespace Peak.Island
{
    public class VoiceThief : UnityEngine.Component
    {
        public bool NativeActionComplete;
        [MethodImpl(MethodImplOptions.NoInlining)] public void Start() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void RPCA_Assign() { NativeActionComplete = true; }
        [MethodImpl(MethodImplOptions.NoInlining)] public void RPCA_FlyAway() { NativeActionComplete = true; }
        [MethodImpl(MethodImplOptions.NoInlining)] public void SetActivelyStealing() { NativeActionComplete = true; }
        [MethodImpl(MethodImplOptions.NoInlining)] private void OnDestroy() { }
    }
}
namespace Peak
{
    public class RandomizeAnimatorParameter : UnityEngine.Component
    { [MethodImpl(MethodImplOptions.NoInlining)] public void OnEnable() { } }
    public class ScoutmasterGhostOrbiter : UnityEngine.Component
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public void Start() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void RPC_SetOrbitCharacter() { }
    }
}
// This contract keeps the real game's observed coroutine order: a scheduling
// yield, native UnityEvent, then one-shot bookkeeping. It simulates no gameplay.
public class TriggerEvent : UnityEngine.Component
{
    public bool onlyOnce = true, waitForRenderFrame = true, hasActivated, hasTriggered;
    public UnityEngine.Events.UnityEvent triggerEvent = new();
    public IEnumerator StartObservedRoutine() => TriggerRoutine();
    private IEnumerator TriggerRoutine()
    {
        if (!onlyOnce || !hasTriggered)
        {
            if (waitForRenderFrame) yield return null;
            triggerEvent.Invoke();
            hasActivated = true;
            hasTriggered = true;
        }
    }
}
// Same observed native boundary as DisableBasedOnRunSettings: both Awake and
// settings updates call UpdateEnabled; only a custom disabled setting hides it.
public class DisableBasedOnRunSettings : UnityEngine.Component
{
    public bool IsCustomRun;
    public int SettingValue = 1;
    public void Awake() => UpdateEnabled();
    public void ApplySettings() => UpdateEnabled();
    [MethodImpl(MethodImplOptions.NoInlining)] private void UpdateEnabled()
    { if (IsCustomRun && SettingValue == 0) gameObject.SetActive(false); }
}
namespace PeakReplayLab
{
    internal static class RavenAnimationCapture { internal static bool EventHooksHealthy; }
    internal static class FrogCaptureActivity
    {
        internal static bool EventHooksHealthy;
        internal static int NativeEvents;
        internal static void NotifyNativeEvent(FrogTongue frog)
        { if (!frog.NativeActionComplete) throw new InvalidOperationException("Frog action observed before native completion"); NativeEvents++; }
    }
    internal static class CreatureReplayRules { public const int ActiveClock = 0, IdleClock = 1, SleepingClock = 2; }
    internal static class ReplaySafety { public static bool Active; }
    internal static class EffectReplayCapture { internal static void ObserveNativeSpawn(UnityEngine.GameObject source) { } }
    internal static class CreatureReplayResources
    {
        public static bool Fail;
        public static int TrapLookups;
        public static UnityEngine.Transform? Trap(UnityEngine.Transform source)
        { TrapLookups++; if (Fail) throw new InvalidOperationException("diagnostic lookup"); return source.gameObject.VerifiedTrap ? source : null; }
    }
}
