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
