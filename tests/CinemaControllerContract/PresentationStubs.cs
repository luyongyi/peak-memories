using UnityEngine;

namespace PeakReplayLab
{
    internal sealed class PlaybackSession
    {
        public Camera PresentationCamera { get; } = new();
        public bool FreeCamera { get; private set; }
        public bool Paused { get; set; }
        public bool ShowNames { get; set; } = true;
        public bool CameraLooking { get; set; }
        public bool CameraReadsLookDelta { get; set; }
        public bool Buffering { get; set; }
        public string PresentationSegment { get; set; } = "A";
        public string FocusId { get; set; } = "actor-A";
        public double Time { get; set; }
        public double Duration { get; set; } = 10000;
        public string BufferingLabel => "loading target";
        public float Speed { get; set; } = 1;
        public int SyncCalls, SetFreeCalls, FocusCycles, SeekCalls;
        public double RequestedTime;
        public IReplayPresentationExtension? Extension;
        public readonly Dictionary<string, Vector3> FocusPoints = new();
        public void SetFreeCamera(bool free) { SetFreeCalls++; FreeCamera = free; SyncCameraOrientation(); }
        public void SyncCameraOrientation() => SyncCalls++;
        public void CycleFocus() => FocusCycles++;
        public bool TryGetActorFocusPoint(string id, out Vector3 point) => FocusPoints.TryGetValue(id, out point);
        public readonly SortedDictionary<double, string> Segments = new() { [0] = "A" };
        public bool TryGetPresentationSegmentAt(double time, out string segment)
        {
            segment = "";
            if (!PeakReplayLab.Cinema.CinemaLimits.Finite(time) || time < 0 || time > Duration) return false;
            foreach (var entry in Segments) { if (entry.Key > time) break; segment = entry.Value; }
            return segment.Length != 0;
        }
        public void Seek(double time) { RequestedTime = time; Buffering = true; SeekCalls++; }
        public void Commit(double time, string? segment = null)
        { Time = time; Buffering = false; if (segment != null) PresentationSegment = segment; }
        public void AttachPresentation(IReplayPresentationExtension extension)
        { if (Extension != null) throw new InvalidOperationException("already attached"); Extension = extension; }
        public void DetachPresentation(IReplayPresentationExtension extension)
        { if (ReferenceEquals(Extension, extension)) Extension = null; }
    }
}

namespace PeakReplayLab.Cinema
{
    internal sealed class CinemaEditorUI : IDisposable
    {
        public static CinemaEditorUI? Latest;
        public static bool ThrowOnConstruction;
        public static bool ThrowOnFirstRefresh;
        public static int Created;
        public bool EditingText { get; set; }
        public bool ThrowOnDispose, ThrowOnRefresh;
        public int Disposals, Refreshes;
        public CinemaEditorState? LastState;
        public readonly ICinemaEditorHost Host;
        public CinemaEditorUI(ICinemaEditorHost host)
        {
            Created++;
            if (ThrowOnConstruction) throw new InvalidOperationException("injected UI constructor failure");
            Host = host; Latest = this; ThrowOnRefresh = ThrowOnFirstRefresh;
        }
        public void Refresh(CinemaEditorState state, bool looking)
        { if (ThrowOnRefresh) throw new InvalidOperationException("injected refresh failure"); Refreshes++; LastState = state; }
        public void Dispose() { Disposals++; if (ThrowOnDispose) throw new InvalidOperationException("injected UI dispose failure"); }
        public static void Reset() { Latest = null; ThrowOnConstruction = false; ThrowOnFirstRefresh = false; Created = 0; }
    }
    internal enum CinemaEffectPreset { Original, Warm, Cool, Mono }
    internal enum CinemaEffectFocusMode { Off, Manual, Actor }
    internal enum CinemaEffectQuality { Low, Medium, High }
    internal sealed class CinemaEffectSettings
    {
        public CinemaEffectPreset Preset;
        public float Intensity, Exposure, Contrast, Saturation, Temperature, BlurStrength;
        public CinemaEffectFocusMode DofMode;
        public CinemaEffectQuality DofQuality;
    }
    internal sealed class CinemaEffects : IDisposable
    {
        public static CinemaEffects? Latest;
        public static int Created;
        public bool ThrowOnDispose;
        public int Disposals;
        public float Focus;
        public CinemaEffectSettings? LastSettings;
        public string Diagnostic => "stub state only";
        public string Warning => "";
        public CinemaEffects(Camera camera) { Latest = this; Created++; }
        public void Apply(CinemaEffectSettings settings, float focus) { LastSettings = settings; Focus = focus; }
        public void Dispose() { Disposals++; if (ThrowOnDispose) throw new InvalidOperationException("injected effects dispose failure"); }
        public static void Reset() { Latest = null; Created = 0; }
    }
}
