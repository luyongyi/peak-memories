using System;
using UnityEngine;

namespace PeakReplayLab;

// Attached only to an admitted native root. Unity supplies activation changes,
// including ancestor activation; there is deliberately no Update or tree scan.
internal sealed class CaptureRootLifecycleObserver : MonoBehaviour
{
    private event Action? changed;
    public static IDisposable Bind(GameObject root, Action callback)
    {
        var observer = root.GetComponent<CaptureRootLifecycleObserver>();
        if (!observer) observer = root.AddComponent<CaptureRootLifecycleObserver>();
        observer!.changed += callback;
        return new Subscription(observer, callback);
    }
    private void OnEnable() => Notify();
    private void OnDisable() => Notify();
    private void Notify()
    {
        // Recording observers must never interrupt native activation.
        if (ReplaySafety.Active) return;
        var listeners = changed;
        if (listeners == null) return;
        foreach (Action callback in listeners.GetInvocationList())
            try { callback(); } catch { }
    }
    private sealed class Subscription : IDisposable
    {
        private CaptureRootLifecycleObserver? observer;
        private readonly Action callback;
        public Subscription(CaptureRootLifecycleObserver observer, Action callback)
        { this.observer = observer; this.callback = callback; }
        public void Dispose()
        {
            // A destroyed Unity component still owns the managed delegate.
            // Always release it so a completed recorder cannot be retained.
            if (observer is null) return;
            observer.changed -= callback; observer = null;
        }
    }
}
