namespace UnityEngine
{
    public class Object
    {
        private static int serial;
        private readonly int id = ++serial;
        public static implicit operator bool([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Object? value) => value != null;
        public int GetInstanceID() => id;
        public static T[] FindObjectsByType<T>(FindObjectsInactive inactive, FindObjectsSortMode order) => Array.Empty<T>();
        public static void Destroy(Object value) { }
    }
    public enum FindObjectsInactive { Include }
    public enum FindObjectsSortMode { None }
    public class Component : Object
    {
        public GameObject gameObject = null!;
        public Transform transform => gameObject.transform;
        public T[] GetComponentsInChildren<T>(bool inactive) => gameObject.GetComponentsInChildren<T>(inactive);
    }
    public class Renderer : Component
    {
        public bool forceRenderingOff, enabled = true, receiveShadows;
        public object[] sharedMaterials = Array.Empty<object>();
        public int shadowCastingMode, sortingLayerID, sortingOrder;
    }
    public class MeshRenderer : Renderer { }
    public class SkinnedMeshRenderer : Renderer { }
    public class ParticleSystemRenderer : Renderer { }
    public sealed class LineRenderer : Renderer
    {
        public bool useWorldSpace, loop, generateLightingData;
        public int alignment, textureMode, numCapVertices, numCornerVertices, positionCount;
        public float widthMultiplier, startWidth, endWidth;
        public object? widthCurve, colorGradient;
        public Color startColor, endColor;
        public readonly Dictionary<int, Vector3> Positions = new();
        public int PositionWrites;
        public void SetPosition(int index, Vector3 point) { Positions[index] = point; PositionWrites++; }
    }
    public sealed class Transform : Object
    {
        public GameObject gameObject = null!;
        public Transform? parent;
        public Vector3 position, localPosition, localScale = Vector3.one, lossyScale = Vector3.one;
        public Quaternion rotation, localRotation;
        public void SetParent(Transform? value, bool world) => parent = value;
        public void SetPositionAndRotation(Vector3 position, Quaternion rotation) { this.position = position; this.rotation = rotation; }
    }
    public sealed class GameObject : Object
    {
        public string name;
        public int layer, Hides;
        public bool activeSelf = true;
        public bool activeInHierarchy => activeSelf && (transform.parent == null || transform.parent.gameObject.activeInHierarchy);
        public readonly Transform transform;
        public Scene scene;
        private readonly List<Component> components = new();
        public GameObject(string name = "native") { this.name = name; transform = new() { gameObject = this }; }
        public void SetActive(bool value) { if (activeSelf && !value) Hides++; activeSelf = value; }
        public T AddComponent<T>() where T : Component, new() { var value = new T { gameObject = this }; components.Add(value); return value; }
        public T? GetComponent<T>() where T : class => components.OfType<T>().FirstOrDefault();
        public T? GetComponentInChildren<T>(bool inactive) where T : class => GetComponent<T>();
        public T[] GetComponentsInChildren<T>(bool inactive) => components.OfType<T>().ToArray();
    }
    public struct Scene { public bool IsValid() => true; }
    public readonly record struct Vector3(float x, float y, float z)
    {
        public static Vector3 one => new(1, 1, 1);
        public static Vector3 Lerp(Vector3 a, Vector3 b, float f) => LerpUnclamped(a, b, f);
        public static Vector3 LerpUnclamped(Vector3 a, Vector3 b, float f) => new(a.x+(b.x-a.x)*f, a.y+(b.y-a.y)*f, a.z+(b.z-a.z)*f);
    }
    public readonly record struct Quaternion(float x, float y, float z, float w)
    {
        public Quaternion normalized => this;
        public static Quaternion Slerp(Quaternion a, Quaternion b, float f) => f >= 1 ? b : a;
        public static Quaternion SlerpUnclamped(Quaternion a, Quaternion b, float f) => Slerp(a, b, f);
    }
    public readonly record struct Color(float r, float g, float b, float a) { public static Color Lerp(Color a, Color b, float f) => f >= 1 ? b : a; }
    public static class Time { public static int frameCount; public static double realtimeSinceStartupAsDouble => frameCount / 60d; }
    public static class Mathf
    {
        public static float Lerp(float a, float b, float f) => a+(b-a)*f;
        public static float LerpUnclamped(float a, float b, float f) => Lerp(a,b,f);
        public static float Abs(float value) => Math.Abs(value);
    }
    public static class Shader { public static float GetGlobalFloat(string key) => 0; public static void SetGlobalFloat(string key, float value) { } }
}
namespace UnityEngine.Rendering
{
    public sealed class Volume : UnityEngine.Component
    { public bool enabled, isGlobal; public float priority, blendDistance, weight; public object? sharedProfile; }
}
namespace TMPro
{
    public sealed class TextMeshPro : UnityEngine.Component
    {
        public object? font, fontSharedMaterial;
        public int alignment, fontStyle, textWrappingMode, overflowMode;
        public bool richText, enabled;
        public float characterSpacing, lineSpacing, fontSize;
        public string text = "";
        public UnityEngine.Color color;
        public readonly Rect rectTransform = new();
        public readonly UnityEngine.Renderer renderer = new();
    }
    public sealed class Rect { public object? sizeDelta, pivot; }
}
public sealed class Breakable : UnityEngine.Component { }
public sealed class Item : UnityEngine.Component { }
public sealed class ScoutCannon : UnityEngine.Component { }
public sealed class Action_AskBingBong : UnityEngine.Component { }
public sealed class WarpCompassVFX : UnityEngine.Component { }
public sealed class WobbleSpinBounce : UnityEngine.Component { public bool enabled; }
public sealed class Spider : UnityEngine.Component { public UnityEngine.LineRenderer line = null!; }
namespace Peak
{
    public sealed class PhotonCleanupHelper : UnityEngine.Component { }
    public sealed class Action_CloneSelectedItem : UnityEngine.Component { }
}
namespace PeakReplayLab
{
    public sealed class ObjectPose
    {
        public bool Active = true;
        public int Revision;
        public float[] Position = new float[3], Rotation = new float[] { 0, 0, 0, 1 }, Scale = new float[] { 1, 1, 1 };
    }
    public sealed class NativeRendererFrame { public int Revision; }
    public sealed class NativeLightFrame { public int Revision; }
    public sealed class SpawnedReplayFrame
    {
        public string Key = "object", Resource = "resource", Kind = "static";
        public ObjectPose Pose = new();
        public NativeRendererFrame[]? Visuals;
        public NativeLightFrame[]? Lights;
    }
    public sealed class InteractionReplayFrame
    {
        public string Key = "object", Resource = "resource", Kind = "static", SourcePath = "path";
        public ObjectPose Pose = new();
        public NativeRendererFrame[]? Visuals;
        public NativeLightFrame[]? Lights;
        public InteractionLineFrame? Line;
        public InteractionTextFrame? Text;
        public float? GloomAmount;
        public float[]? PostWeights;
        public bool[]? PostEnabled;
    }
    public sealed class InteractionLineFrame { public bool Visible = true; public float[] Points = new float[6], Widths = new float[2], StartColor = new float[4], EndColor = new float[4]; }
    public sealed class InteractionTextFrame { public bool Visible = true; public string Content = ""; public float FontSize; public float[] Color = new float[4]; }
    public sealed class CreatureReplayFrame
    {
        public bool SampleTimeKnown;
        public int ClockMode, State;
        public CreatureIdleAnimation? IdleAnimation;
        public string Key = "object", Resource = "resource", Kind = "static", SourcePath = "path";
        public ObjectPose Pose = new();
        public NativeRendererFrame[]? Visuals;
        public CreatureReplayLine? Line;
    }
    public sealed class CreatureReplayLine { public bool Enabled, WorldSpace; public float Width; public float[] Points = Array.Empty<float>(); }
    // Controlled clock output only: real observation indexing/interpolation is
    // covered by ReplayContract. This isolates the production Apply call site.
    internal sealed class CreatureObservationTimeline
    {
        public string Key = "object";
        public CreatureReplayFrame Left = null!, Right = null!;
        public float Mix;
        public bool Available = true;
        public int Calls;
        public double LastRequestedTime;
        internal bool TrySample(string key, double time, out CreatureReplayFrame left, out CreatureReplayFrame right, out float mix)
        {
            Calls++; LastRequestedTime = time;
            left = Left; right = Right; mix = Mix;
            return Available && key == Key;
        }
    }
    public static class TestScene
    {
        public static UnityEngine.GameObject Native = null!;
        public static string Kind = "static";
        public static void Reset() { Kind = "static"; Native = new(); Native.AddComponent<UnityEngine.MeshRenderer>(); }
    }
    public static class SpawnedReplayResources
    {
        public sealed class Resource { public string Kind = "static", Name = "native"; public UnityEngine.GameObject Source => TestScene.Native; }
        public static Resource? Match(UnityEngine.GameObject source) => new();
        public static Resource? Find(string resource) => new();
    }
    public static class NativePlacementResources { public static IEnumerable<UnityEngine.GameObject> InitialSources() => new[] { TestScene.Native }; }
    public static class SpawnedReplayRules { public const int MaximumEntities = 256; public static bool CanInterpolate(SpawnedReplayFrame a, SpawnedReplayFrame b) => a.Kind == b.Kind; }
    public sealed class SpawnedReplayObserver : IDisposable
    { public SpawnedReplayObserver(Action<UnityEngine.GameObject, string> add, Action<UnityEngine.GameObject> change, Action<UnityEngine.GameObject> remove) { } public void Dispose() { } }
    public sealed class NativePlacementObserver : IDisposable
    { public NativePlacementObserver(Action<UnityEngine.GameObject, string> add, Action<UnityEngine.GameObject> change, Action<UnityEngine.GameObject> remove) { } public void Dispose() { } }
    public sealed class InteractionReplayResources
    {
        public sealed class Source
        { public string Kind = "static", Resource = "resource", Path = "path"; public UnityEngine.GameObject Root => TestScene.Native; public UnityEngine.LineRenderer? Line; public TMPro.TextMeshPro? Text; public UnityEngine.Rendering.Volume[]? Posts; }
        public void LoadTemplates() { }
        public IEnumerable<Source> Initial() => new[] { new Source() };
        public void AddBreakable(Breakable value) { }
        public Source? FromOwner(UnityEngine.Component value) => new();
        public Source? Spawn(UnityEngine.Object original, UnityEngine.Object instance) => new();
        public Source? Resolve(InteractionReplayFrame frame) => new();
    }
    public static class InteractionReplayRules
    { public const int MaximumEntities = 256, MaximumNodes = 128; public static bool World(string kind) => true; public static bool PostKind(string kind) => false; public static bool CanInterpolate(InteractionReplayFrame a, InteractionReplayFrame b) => a.Kind == b.Kind; }
    public sealed class InteractionReplayObserver : IDisposable
    { public InteractionReplayObserver(Action<UnityEngine.Component> changed, Action<UnityEngine.Object, UnityEngine.Object> spawned, Action<string>? warning) { } public void Dispose() { } }
    public static class CreatureReplayResources
    {
        public static void Refresh() { }
        public static void VisitScene(Action<UnityEngine.GameObject> visit) => visit(TestScene.Native);
        public static bool Ours(UnityEngine.Transform value) => false;
        public static string Kind(UnityEngine.GameObject value) => TestScene.Kind;
        public static void Register(UnityEngine.GameObject value, string kind) { }
        public static UnityEngine.GameObject? Find(string kind, string resource, string path = "") => TestScene.Native;
    }
    public static class CreatureDirectoryRules
    {
        public static bool IsLegacyKind(string kind) => !BodyRequiresSchema21(kind);
        public static bool BodyRequiresSchema21(string kind) => kind == "ghost-ball" || kind == "player-ghost";
    }
    public static class CreatureReplayRules
    {
        public const int MaximumDynamicEntities = 256, MaximumStaticTraps = 2048, MaximumNodes = 128;
        public const int ActiveClock = 0, IdleClock = 1, SleepingClock = 2;
        public static bool CompactStaticTrap(CreatureReplayFrame frame) => false;
        public static bool CanInterpolate(CreatureReplayFrame a, CreatureReplayFrame b) => a.Kind == b.Kind;
    }
    public sealed class CreatureReplayObserver : IDisposable { public CreatureReplayObserver(Action<UnityEngine.GameObject> add) { } public void Dispose() { } }
    public sealed class CannonReplayVisual : IDisposable
    {
        private readonly VisualReplica replica;
        public CannonReplayVisual(UnityEngine.GameObject source, VisualReplica replica) => this.replica = replica;
        public void Apply(SpawnedReplayFrame frame, SpawnedReplayFrame? next, float mix, double time, bool restart) => replica.Apply(frame.Pose, next?.Pose, mix);
        public void Dispose() { }
    }
    public sealed class VisualReplica : IDisposable
    {
        public static readonly List<VisualReplica> Created = new();
        public static bool CanStartBuild => true;
        public readonly UnityEngine.GameObject Root = new();
        public bool IsReady { get; private set; }
        public bool IsResetting { get; private set; }
        public int RetainedCost => 4;
        public ObjectPose? LastPose;
        public ObjectPose? LastLeftPose, LastRightPose;
        public float LastMix;
        public double BoneMarker;
        public int PoseApplies;
        private ObjectPose? appliedLeft, appliedRight;
        private float appliedMix;
        private bool poseInvalidated = true;
        public static VisualReplica BeginBuild(UnityEngine.GameObject source, bool clone = false, int nodes = 128)
        { var copy = new VisualReplica(); copy.Root.activeSelf = false; Created.Add(copy); return copy; }
        public bool ContinueBuild(PresentationWorkBudget budget) { if (!IsReady) IsReady = budget.Build(); return IsReady; }
        public void BeginReuse() { Hide(); IsResetting = true; appliedLeft = appliedRight = null; poseInvalidated = true; }
        public bool ContinueReuse(PresentationWorkBudget budget) { if (!budget.Build()) return false; IsResetting = false; return true; }
        public void InvalidatePose() => poseInvalidated = true;
        public void Apply(ObjectPose left, ObjectPose? right, float mix)
        {
            right ??= left;
            // Match VisualReplica's real PoseVersionPolicy. Native idle writes
            // bones outside this cache; the caller must invalidate on cuts.
            if (!poseInvalidated && ReferenceEquals(left,appliedLeft) && ReferenceEquals(right,appliedRight) &&
                (mix==appliedMix || ReferenceEquals(left,right))) return;
            LastLeftPose = left; LastRightPose = right; LastMix = mix; LastPose = mix >= 1 ? right : left;
            BoneMarker = LastPose.Revision; PoseApplies++; Root.SetActive(LastPose.Active);
            appliedLeft=left;appliedRight=right;appliedMix=mix;poseInvalidated=false;
        }
        public void Hide() => Root.SetActive(false);
        public void Sleep() => Hide();
        public void Retire() => Hide();
        public void Dispose() => Hide();
        public bool TryCloneTransform(UnityEngine.Transform original, out UnityEngine.Transform value) { value = Root.transform; return true; }
        public bool TryCloneRenderer(UnityEngine.Renderer original, out UnityEngine.Renderer value) { value = Root.AddComponent<UnityEngine.MeshRenderer>(); return true; }
        public static void DrainRetired(PresentationWorkBudget budget) { }
        public static void FlushRetired() { }
    }
    // Model native incremental restoration: once started, an older target is
    // completed before a newer argument is accepted unless restart is requested.
    public static class NativeVisualAppearance
    {
        public sealed class Playback : IDisposable
        {
            public static readonly List<Playback> Created = new();
            public static bool Defer;
            public static int Calls;
            private bool pending;
            private NativeRendererFrame[]? target;
            public NativeRendererFrame[]? Applied;
            public bool IsReady { get; private set; }
            public Playback(VisualReplica replica, UnityEngine.GameObject source, bool deferred) => Created.Add(this);
            public bool ContinueBuild(PresentationWorkBudget budget) { if (!IsReady) IsReady = budget.Build(); return IsReady; }
            public bool TryApply(NativeRendererFrame[]? frames, PresentationWorkBudget budget, bool restart = false)
            {
                Calls++; if (restart) pending = false;
                if (!pending && ReferenceEquals(Applied, frames)) return true;
                if (!pending) { pending = true; target = frames; }
                if (Defer || !budget.Step()) return false;
                Applied = target; pending = false; return true;
            }
            public void ReapplyVisibility() { }
            public void Dispose() { }
        }
    }
    public static class NativeLightAppearance
    {
        public sealed class Originals : IDisposable
        { public void Hide(UnityEngine.GameObject source) { } public void Enforce() { } public void Dispose() { } }
        public sealed class Playback : IDisposable
        {
            public static readonly List<Playback> Created = new();
            public static bool Defer;
            private bool pending;
            private NativeLightFrame[]? target;
            public NativeLightFrame[]? Applied;
            public bool IsReady { get; private set; }
            public Playback(VisualReplica replica, UnityEngine.GameObject source, bool deferred) => Created.Add(this);
            public bool ContinueBuild(PresentationWorkBudget budget) { if (!IsReady) IsReady = budget.Build(); return IsReady; }
            public bool TryApply(NativeLightFrame[]? frames, PresentationWorkBudget budget, bool restart = false)
            {
                if (restart) pending = false;
                if (!pending && ReferenceEquals(Applied, frames)) return true;
                if (!pending) { pending = true; target = frames; }
                if (Defer || !budget.Step()) return false;
                Applied = target; pending = false; return true;
            }
            public void CancelPending() => pending = false;
            public void Dispose() { }
        }
    }
}
