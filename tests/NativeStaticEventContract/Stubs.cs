using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class NotNullWhenAttribute : Attribute { public NotNullWhenAttribute(bool value) { } }
}
namespace UnityEngine.SceneManagement
{
    public readonly struct Scene { public int handle => 1; }
    public static class SceneManager { public static Scene GetActiveScene() => new(); }
}
namespace UnityEngine
{
    public class Object
    {
        private static int serial;
        private readonly int id = ++serial;
        public bool Destroyed;
        public string name = "";
        public int GetInstanceID() => id;
        public static implicit operator bool([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Object? value) => value is not null && !value.Destroyed;
        public static T[] FindObjectsByType<T>(FindObjectsInactive inactive, FindObjectsSortMode sort) where T : Object =>
            GameObject.All.SelectMany(value => value.Components).OfType<T>().Where(value => value).ToArray();
    }
    public enum FindObjectsInactive { Include }
    public enum FindObjectsSortMode { None }
    public class Component : Object
    {
        public GameObject gameObject = null!;
        public Transform transform => gameObject.transform;
        public T? GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
    }
    public class MonoBehaviour : Component { }
    public class GameObject : Object
    {
        public static readonly List<GameObject> All = new();
        public readonly List<Component> Components = new();
        public readonly Transform transform;
        private bool active = true;
        public bool activeSelf => active;
        public static int ActiveReads, ComponentReads, DirectoryReads;
        public bool activeInHierarchy { get { ActiveReads++; return ActiveRaw; } }
        private bool ActiveRaw => active && !Destroyed && (transform.parent == null || transform.parent.gameObject.ActiveRaw);
        public SceneManagement.Scene scene => new();
        public GameObject(string name = "native")
        { this.name = name; transform = new Transform { gameObject = this }; Components.Add(transform); All.Add(this); }
        public T AddComponent<T>() where T : Component, new()
        { var value = new T { gameObject = this }; Components.Add(value); return value; }
        public T? GetComponent<T>() where T : Component { ComponentReads++; return Components.OfType<T>().FirstOrDefault(); }
        public bool TryGetComponent<T>(out T value) where T : Component { value = GetComponent<T>()!; return value != null; }
        public T[] GetComponentsInChildren<T>(bool inactive) where T : Component
        { DirectoryReads++; return All.Where(value => value == this || value.transform.Ancestor(this)).SelectMany(value => value.Components).OfType<T>().ToArray(); }
        public void SetActive(bool value)
        {
            var roots = All.Where(root => root == this || root.transform.Ancestor(this)).ToArray();
            var prior = roots.Select(root => root.ActiveRaw).ToArray(); active = value;
            for (int i = 0; i < roots.Length; i++) if (prior[i] != roots[i].ActiveRaw)
                foreach (var component in roots[i].Components.ToArray())
                    component.GetType().GetMethod(roots[i].ActiveRaw ? "OnEnable" : "OnDisable", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(component, null);
        }
    }
    public class Transform : Component
    {
        public Transform? parent;
        public bool Ancestor(GameObject root)
        { for (var node = parent; node != null; node = node.parent) if (node.gameObject == root) return true; return false; }
        public static int PoseReads;
        public Vector3 position { get { PoseReads++; return new(); } }
        public Quaternion rotation { get { PoseReads++; return new() { w = 1 }; } }
        public Vector3 lossyScale { get { PoseReads++; return new() { x = 1, y = 1, z = 1 }; } }
    }
    public struct Vector3 { public float x, y, z; }
    public struct Quaternion { public float x, y, z, w; }
    public class Animation : Component { }
    public class StateMachineBehaviour : Object { }
    public class RuntimeAnimatorController : Object { public AnimationClip[] animationClips = Array.Empty<AnimationClip>(); }
    public class AnimatorOverrideController : RuntimeAnimatorController { }
    public class Light : Component { }
    public class AnimationClip : Object { public bool humanMotion, isLooping; public float length = 1; }
    public struct AnimatorClipInfo { public AnimationClip clip; public float weight; }
    public struct AnimatorStateInfo
    {
        public float normalizedTime, speed, speedMultiplier, length;
        public bool loop;
        public string Name;
        public bool IsName(string value) => Name == value;
    }
    public class Animator : Component
    {
        public static int StateReads;
        public RuntimeAnimatorController runtimeAnimatorController = new() { name = "Luggage" };
        public int layerCount = 1;
        public float speed = 1;
        public bool isActiveAndEnabled => gameObject.activeInHierarchy;
        public StateMachineBehaviour[] Behaviours = Array.Empty<StateMachineBehaviour>();
        public T[] GetBehaviours<T>() where T : StateMachineBehaviour => Behaviours.OfType<T>().ToArray();
        public readonly AnimationClip Clip = new();
        public AnimatorStateInfo State = new() { Name = "Luggage_Open", speed = 1, speedMultiplier = 1 };
        public AnimatorStateInfo GetCurrentAnimatorStateInfo(int layer) { StateReads++; return State; }
        public AnimatorStateInfo GetNextAnimatorStateInfo(int layer) { StateReads++; return State; }
        public void GetCurrentAnimatorClipInfo(int layer, List<AnimatorClipInfo> clips) => clips.Add(new() { clip = Clip, weight = 1 });
        public void GetNextAnimatorClipInfo(int layer, List<AnimatorClipInfo> clips) => GetCurrentAnimatorClipInfo(layer, clips);
        public bool IsInTransition(int layer) => false;
    }
    public static class Time { public static double timeAsDouble, realtimeSinceStartupAsDouble; public static int frameCount; }
    public static class Mathf
    {
        public static float Clamp(float x, float min, float max) => Math.Max(min, Math.Min(max, x));
        public static float Repeat(float x, float length) => x - (float)Math.Floor(x / length) * length;
    }
    public static class Resources { public static T? Load<T>(string value) where T : Object => null; }
}
namespace Photon.Pun { public class PhotonView : UnityEngine.MonoBehaviour { } }
namespace Peak
{
    public class PhotonCleanupHelper : UnityEngine.MonoBehaviour
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public void OnPhotonInstantiate() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void OnDestroy() { }
    }
}
public class Item : UnityEngine.MonoBehaviour { }
public class AddScreenshake : UnityEngine.MonoBehaviour { }
public class SFX_PlayOneShot : UnityEngine.MonoBehaviour { }
public class SpineCheck : UnityEngine.MonoBehaviour { }
public class AnimationJuice : UnityEngine.MonoBehaviour { }
public class LazyGizmo : UnityEngine.MonoBehaviour { }
public class MushroomBounceBadgeTracker : UnityEngine.MonoBehaviour { }
public class ClimbHandle : UnityEngine.MonoBehaviour { }
public class CollisionModifier : UnityEngine.MonoBehaviour
{ [MethodImpl(MethodImplOptions.NoInlining)] public void TriggerCharacterBouncedEvents() { } }
public class ScoutCannon : UnityEngine.MonoBehaviour
{
    [MethodImpl(MethodImplOptions.NoInlining)] public void Awake() { }
    [MethodImpl(MethodImplOptions.NoInlining)] public void RPCA_Light() { }
}
public class Constructable
{ [MethodImpl(MethodImplOptions.NoInlining)] public static void AngleIt(Photon.Pun.PhotonView view) { } }
public class UnknownVisualScript : UnityEngine.MonoBehaviour { }
public class Luggage : UnityEngine.MonoBehaviour { public bool IsOpen; public UnityEngine.GameObject? activateOnOpen; }
public class RespawnChest : Luggage { public UnityEngine.GameObject? skeleton; }
public class MirageLuggage : UnityEngine.MonoBehaviour { }
public class Character : UnityEngine.MonoBehaviour { }
public class CharacterBalloons : UnityEngine.MonoBehaviour
{ public readonly List<TiedBalloon> tiedBalloons = new(); public float headOffset = .5f; }
public class TiedBalloon : UnityEngine.MonoBehaviour
{
    private CharacterBalloons? characterBalloons;
    private float initialTime;
    public CharacterBalloons? ObservedOwner => characterBalloons;
    public float Started => initialTime;
    public int colorIndex;
    public bool CharacterReady, OwnerAvailable = true;
    public CharacterBalloons? NativeOwner;
    public IEnumerator? Routine;
    public static int Initializations;
    [MethodImpl(MethodImplOptions.NoInlining)] public void RPC_Init(int color = 0) => Routine = InitRoutine(color);
    private IEnumerator InitRoutine(int color)
    {
        while (!CharacterReady) yield return null;
        if (!OwnerAvailable) yield break;
        colorIndex = color; initialTime = (float)UnityEngine.Time.timeAsDouble; characterBalloons = NativeOwner;
        characterBalloons!.tiedBalloons.Add(this); Initializations++;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] public void OnDestroy() { Destroyed = true; gameObject.Destroyed = true; }
}
namespace PeakReplayLab
{
    internal static class ReplaySafety { public static bool Active; }
    internal static class EffectReplayCapture { public static void ObserveNativeSpawn(UnityEngine.GameObject source) { } }
    internal sealed class SegmentCaptureClient : IDisposable
    { public bool Admit(UnityEngine.GameObject source, bool urgent = false) => true; public void Forget(int id) { } public void Dispose() { } }
    internal sealed class CaptureSegmentScope
    { public static CaptureSegmentScope? Active => null; public SegmentCaptureClient CreateClient(Action<UnityEngine.GameObject> birth, Action<int> retired) => new(); }
    public sealed class ObjectPose
    {
        public float[] Position = new float[3], Rotation = new[] { 0f, 0, 0, 1 }, Scale = new[] { 1f, 1, 1 };
        public bool Active; public object[] Nodes = Array.Empty<object>(); public bool Effect;
        public ObjectPose() { }
        public ObjectPose(float[] p, float[] q, float[] s, bool a, object[] n) { Position = p; Rotation = q; Scale = s; Active = a; Nodes = n; }
    }
    public sealed class CrateAnimationFrame
    { public string Clip = ""; public float Time, Rate, Duration; public double AnchorTime; public bool Anchored, Loop; }
    public sealed class NativeVisualFrame { }
    public sealed class NativeLightFrame { }
    public sealed class CrateFrame
    { public string Key = "", Kind = ""; public bool Open; public ObjectPose Pose = null!; public CrateAnimationFrame? Animation; public NativeVisualFrame[]? Visuals; public NativeLightFrame[]? Lights; }
    public sealed class BalloonReplayFrame
    { public string Key = "", OwnerId = ""; public int ColorIndex; public float HeadOffset; public double Started; public bool Active; }
    public static class BalloonReplayRules
    { public const int MaximumEntities = 128; public static void Validate(BalloonReplayFrame frame) { if (frame.OwnerId == "") throw new Exception("owner"); } }
    public sealed class SpawnedReplayFrame
    { public string Key = "", Resource = "", Kind = ""; public ObjectPose Pose = null!; public CrateAnimationFrame? Animation; public NativeVisualFrame[]? Visuals; public NativeLightFrame[]? Lights; }
    public static class SpawnedReplayRules { public const int MaximumEntities = 256; public const double AnimationWatchSeconds = 1.25; }
    internal static class WorldTrack { public static string Path(UnityEngine.Transform source) => source.gameObject.name; }
    internal static class Capture
    { public static string Id(Character source) => "actor:" + source.GetInstanceID(); public static string ClipKey(UnityEngine.AnimationClip clip, UnityEngine.Object controller) => "native-clip"; }
    internal static class VisualReplica
    {
        public static int Reads;
        public static ObjectPose Capture(UnityEngine.Transform source, bool children = false)
        {
            Reads++; var crate = source.gameObject.GetComponent<Luggage>();
            return new() { Active = source.gameObject.activeInHierarchy, Effect = crate && crate!.activateOnOpen && crate.activateOnOpen!.activeSelf };
        }
        public static void ReleaseCapture(UnityEngine.Transform source) { }
    }
    internal static class NativeVisualAppearance
    {
        public static int Reads;
        public static NativeVisualFrame[] Capture(UnityEngine.Transform source, NativeVisualFrame[]? old) { Reads++; return old ?? new[] { new NativeVisualFrame() }; }
        public static NativeVisualFrame[] CaptureAtTime(UnityEngine.Transform source, NativeVisualFrame[]? old, double now, bool dirty, bool allowAnimated, bool followSceneClock)
            => Capture(source, old);
        public static bool NeedsCaptureAtTime(UnityEngine.Transform source, NativeVisualFrame[]? old, double now, bool active, bool animated, bool followSceneClock) => true;
    }
    internal static class NativeLightAppearance
    { public static int Reads; public static NativeLightFrame[] Capture(UnityEngine.Transform source, NativeLightFrame[]? old) { Reads++; return old ?? new[] { new NativeLightFrame() }; } }
    internal enum LuggageEventKind { Discovered, Destroyed, Unclasp, Open, Reclasp, Appearance }
    internal readonly struct LuggageEvent
    {
        public readonly Luggage Source; public readonly int Id, Frame; public readonly LuggageEventKind Kind; public readonly double Time;
        public LuggageEvent(Luggage source, LuggageEventKind kind, double time)
        { Source = source; Id = source.GetInstanceID(); Kind = kind; Time = time; Frame = UnityEngine.Time.frameCount; }
    }
    internal sealed class LuggageEventObserver : IDisposable
    {
        private static Action<LuggageEvent>? listeners; private readonly Action<LuggageEvent> callback;
        public LuggageEventObserver(Action<LuggageEvent> callback) { this.callback = callback; listeners += callback; }
        public static void Emit(Luggage source, LuggageEventKind kind, double time) => listeners?.Invoke(new(source, kind, time));
        public void Dispose() => listeners -= callback;
    }
    internal static class NativePlacementResources
    {
        public static int ActivityReads;
        public static SpawnedReplayResources.Resource? Find(string path) => null;
        public static SpawnedReplayResources.Resource? Match(UnityEngine.GameObject source) => null;
        public static IEnumerable<UnityEngine.GameObject> InitialSources() => Array.Empty<UnityEngine.GameObject>();
        public static void InvalidateCapture(UnityEngine.GameObject source) { }
        public static bool IsAnimating(UnityEngine.GameObject source, double now, bool force, bool followSceneClock) { ActivityReads++; return false; }
    }
    internal sealed class NativePlacementObserver : IDisposable
    { public NativePlacementObserver(Action<UnityEngine.GameObject, bool> birth, Action<UnityEngine.GameObject> death, Action<UnityEngine.GameObject> changed) { } public void Dispose() { } }
    internal sealed class CannonReplayCapture
    {
        public CannonReplayCapture(ScoutCannon? source) { }
        public void Changed() { }
        public SpawnedReplayFrame Sample(string key, string path, double now, bool dirty) => new() { Key = key, Resource = path, Kind = "scout-cannon", Pose = new() };
    }
}
