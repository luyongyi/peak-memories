using System;

namespace PeakReplayLab;

// Pure drawer motion. Call Tick with unscaled time so playback pause/seek never
// pauses the controls. Positive drag and velocity mean toward the open position.
internal sealed class ReplayDrawerMotion
{
    public const float AnimationSeconds = .25f;
    public const float FlingVelocity = 1.2f;
    private bool animating;
    private float start;
    private float elapsed;

    public float Progress { get; private set; }
    public bool Open { get; private set; }
    public bool Dragging { get; private set; }

    public void Toggle() => SetOpen(!Open);

    public void SetOpen(bool open)
    {
        if (!Dragging && Open == open) return;
        Open = open;
        Dragging = false;
        start = Progress;
        elapsed = 0;
        animating = Progress != (open ? 1 : 0);
    }

    public void BeginDrag()
    {
        Dragging = true;
        animating = false;
    }

    public void DragBy(float normalizedDelta)
    {
        if (!Dragging || !Finite(normalizedDelta)) return;
        Progress = Clamp(Progress + normalizedDelta);
    }

    public void EndDrag(float normalizedVelocity)
    {
        if (!Dragging) return;
        if (!Finite(normalizedVelocity)) normalizedVelocity = 0;
        bool open = normalizedVelocity >= FlingVelocity ||
            (normalizedVelocity > -FlingVelocity && Progress >= .5f);
        SetOpen(open);
    }

    public void Tick(float unscaledDelta)
    {
        if (Dragging || !animating || !Finite(unscaledDelta) || unscaledDelta <= 0) return;
        elapsed = Math.Min(AnimationSeconds, elapsed + unscaledDelta);
        if (elapsed >= AnimationSeconds)
        {
            Progress = Open ? 1 : 0;
            animating = false;
            return;
        }
        float t = elapsed / AnimationSeconds;
        float smooth = t * t * (3 - 2 * t);
        Progress = Clamp(start + ((Open ? 1 : 0) - start) * smooth);
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static float Clamp(float value) => Math.Max(0, Math.Min(1, value));
}

// The read notice has no drawer, focus, pause, or auto-hide timer dependency.
// Formatting happens only when the requested whole second actually changes.
internal sealed class ReplayBufferingNotice
{
    private long second = long.MinValue;
    private bool preparingAudio;
    private bool preparingWorld;
    public bool Visible { get; private set; }
    public string Text { get; private set; } = "";
    public bool Update(bool buffering, double requestedTime, bool preparingAudio = false, bool preparingWorld = false)
    {
        if (!buffering)
        {
            if (!Visible) return false;
            Visible = false; Text = ""; second = long.MinValue; return true;
        }
        long target = !double.IsNaN(requestedTime) && !double.IsInfinity(requestedTime) && requestedTime >= 0 &&
            requestedTime < TimeSpan.MaxValue.TotalSeconds ? (long)Math.Floor(requestedTime) : -1;
        if (Visible && second == target && this.preparingAudio == preparingAudio && this.preparingWorld == preparingWorld) return false;
        Visible = true; second = target; this.preparingAudio = preparingAudio; this.preparingWorld = preparingWorld;
        string time = target < 0 ? "--:--:--" : string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00}", target / 3600, target / 60 % 60, target % 60);
        Text = preparingWorld ? "正在重建目标关卡…" : preparingAudio ? "正在准备声音资源…" : "正在读取 " + time + " 附近的回放…";
        return true;
    }
}
