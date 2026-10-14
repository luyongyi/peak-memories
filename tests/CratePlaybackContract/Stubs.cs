namespace UnityEngine
{
    public class Object
    {
        public static Luggage[] Scene = Array.Empty<Luggage>();
        public static implicit operator bool(Object? value) => value != null;
        public static T[] FindObjectsByType<T>(FindObjectsInactive inactive, FindObjectsSortMode order) => Scene.OfType<T>().ToArray();
    }
    public enum FindObjectsInactive { Include }
    public enum FindObjectsSortMode { None }
    public class Renderer : Object { public bool forceRenderingOff; }
    public sealed class Transform : Object { public string Key = "crate"; }
    public sealed class GameObject : Object
    {
        public bool activeSelf;
        public int Hides;
        public readonly Transform transform = new();
        public SceneManagement.Scene scene;
        public void SetActive(bool value) { if (activeSelf && !value) Hides++; activeSelf = value; }
    }
    public static class Time { public static int frameCount; }
}
namespace UnityEngine.SceneManagement
{
    public struct Scene { public int handle; }
    public static class SceneManager { public static Scene GetActiveScene() => new(); }
}
public sealed class Luggage : UnityEngine.Object
{
    public readonly UnityEngine.GameObject gameObject = new();
    public UnityEngine.Transform transform => gameObject.transform;
    private readonly UnityEngine.Renderer renderer = new();
    public T[] GetComponentsInChildren<T>(bool inactive) => new object[] { renderer }.OfType<T>().ToArray();
}
namespace PeakReplayLab
{
    public sealed class ObjectPose { public bool Active = true; public int Revision; }
    public sealed class CrateAnimationFrame { public int Revision; }
    public sealed class NativeRendererFrame { }
    public sealed class NativeLightFrame { }
    public sealed class CrateFrame
    {
        public string Key = "crate", Kind = "Luggage";
        public ObjectPose Pose = new();
        public CrateAnimationFrame? Animation = new();
        public NativeRendererFrame[]? Visuals;
        public NativeLightFrame[]? Lights;
    }
    public static class WorldTrack { public static string Path(UnityEngine.Transform transform) => transform.Key; }
    public sealed class VisualReplica : IDisposable
    {
        public static readonly List<VisualReplica> Created = new();
        public static bool CanStartBuild => true;
        public readonly UnityEngine.GameObject Root = new();
        public bool IsReady { get; private set; }
        public ObjectPose? LastPose;
        public int PoseApplies;
        public static VisualReplica BeginBuild(UnityEngine.GameObject source) { var copy = new VisualReplica(); Created.Add(copy); return copy; }
        public bool ContinueBuild(PresentationWorkBudget budget) { if (!IsReady) IsReady = budget.Build(); return IsReady; }
        public void InvalidatePose() { }
        public void Apply(ObjectPose left, ObjectPose? right, float mix)
        { LastPose = mix >= 1 ? right ?? left : left; PoseApplies++; Root.SetActive(LastPose.Active); }
        public void Hide() => Root.SetActive(false);
        public void Retire() => Hide();
        public void Dispose() => Hide();
        public static void DrainRetired(PresentationWorkBudget budget) { }
        public static void FlushRetired() { }
    }
    public sealed class CrateVisualAnimation : IDisposable
    {
        public static int Samples;
        public static bool FailPreparation;
        public bool IsReady { get; private set; }
        public CrateVisualAnimation(Luggage source, UnityEngine.GameObject root) { }
        public bool ContinueBuild(PresentationWorkBudget budget) { if (!IsReady) IsReady = budget.Build(); return IsReady; }
        public bool TryPrepareSample(CrateAnimationFrame value, CrateAnimationFrame? next, float mix, double time, bool force, PresentationWorkBudget budget)
        { if (FailPreparation) throw new InvalidOperationException("bad clip"); return budget.Step(); }
        public void ApplyPreparedSample() { Samples++; }
        public void Dispose() { }
    }
    public static class NativeVisualAppearance
    {
        public sealed class Playback : IDisposable
        {
            public static bool Defer;
            public static int Applies;
            public bool IsReady { get; private set; }
            public Playback(VisualReplica replica, UnityEngine.GameObject source, bool deferred) { }
            public bool ContinueBuild(PresentationWorkBudget budget) { if (!IsReady) IsReady = budget.Build(); return IsReady; }
            public bool TryApply(NativeRendererFrame[]? frames, PresentationWorkBudget budget, bool restart = false) { Applies++; return !Defer; }
            public void Dispose() { }
        }
    }
    public static class NativeLightAppearance
    {
        public sealed class Originals : IDisposable
        {
            public void Hide(UnityEngine.GameObject source) { }
            public void Enforce() { }
            public void Dispose() { }
        }
        public sealed class Playback : IDisposable
        {
            public static bool Defer;
            public bool IsReady { get; private set; }
            public Playback(VisualReplica replica, UnityEngine.GameObject source, bool deferred) { }
            public bool ContinueBuild(PresentationWorkBudget budget) { if (!IsReady) IsReady = budget.Build(); return IsReady; }
            public bool TryApply(NativeLightFrame[]? frames, PresentationWorkBudget budget, bool restart = false) => !Defer;
            public void Dispose() { }
        }
    }
}
