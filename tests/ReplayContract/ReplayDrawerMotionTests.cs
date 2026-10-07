using PeakReplayLab;

internal static class ReplayDrawerMotionTests
{
    public static void Run(Action<string, Action> test)
    {
        test("drawer starts closed and a click completes its unscaled animation", () =>
        {
            var drawer = new ReplayDrawerMotion();
            Check(drawer.Progress == 0 && !drawer.Open && !drawer.Dragging);
            drawer.Toggle();
            Check(drawer.Progress == 0 && drawer.Open);
            drawer.Tick(ReplayDrawerMotion.AnimationSeconds / 2);
            Near(drawer.Progress, .5f);
            drawer.Tick(ReplayDrawerMotion.AnimationSeconds / 2);
            Check(drawer.Progress == 1 && drawer.Open);
            drawer.Toggle(); drawer.Tick(10);
            Check(drawer.Progress == 0 && !drawer.Open);
        });
        test("drawer opening and closing remain monotonic bounded and finish exactly", () =>
        {
            var drawer = new ReplayDrawerMotion();
            foreach (bool open in new[] { true, false })
            {
                drawer.SetOpen(open);
                float previous = drawer.Progress;
                for (int i = 0; i < 100; i++)
                {
                    drawer.Tick(.01f);
                    Check(drawer.Progress >= 0 && drawer.Progress <= 1);
                    Check(open ? drawer.Progress >= previous : drawer.Progress <= previous);
                    previous = drawer.Progress;
                }
                Check(drawer.Progress == (open ? 1 : 0));
            }
        });
        test("drawer rapid reversal keeps its current position without an animation snap", () =>
        {
            var drawer = new ReplayDrawerMotion();
            for (int i = 0; i < 100; i++)
            {
                drawer.Toggle();
                float current = drawer.Progress;
                drawer.Tick(.035f);
                Check(drawer.Open ? drawer.Progress >= current : drawer.Progress <= current);
                current = drawer.Progress;
                drawer.Toggle();
                Near(drawer.Progress, current);
                drawer.Tick(.02f);
                Check(drawer.Open ? drawer.Progress >= current : drawer.Progress <= current);
            }
            drawer.SetOpen(false); drawer.Tick(1);
            Check(drawer.Progress == 0);
        });
        test("drawer repeated target commands do not restart an existing animation", () =>
        {
            var drawer = new ReplayDrawerMotion(); drawer.SetOpen(true);
            for (int i = 0; i < 5; i++) { drawer.Tick(.05f); drawer.SetOpen(true); }
            Check(drawer.Progress == 1 && drawer.Open);
        });
        test("drawer zero or invalid clock ticks preserve motion and the target", () =>
        {
            var drawer = new ReplayDrawerMotion(); drawer.Toggle(); drawer.Tick(.07f);
            float current = drawer.Progress;
            foreach (float delta in new[] { 0, -1, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                drawer.Tick(delta); Near(drawer.Progress, current); Check(drawer.Open);
            }
            drawer.Tick(1); Check(drawer.Progress == 1);
        });
        test("drawer drag interrupts animation and follows the pointer even with a zero clock", () =>
        {
            var drawer = new ReplayDrawerMotion(); drawer.Toggle(); drawer.Tick(.08f);
            float current = drawer.Progress;
            drawer.BeginDrag(); Near(drawer.Progress, current);
            drawer.Tick(1); Near(drawer.Progress, current);
            drawer.DragBy(.1f); Near(drawer.Progress, current + .1f);
            drawer.Tick(0); Near(drawer.Progress, current + .1f);
            Check(drawer.Dragging);
        });
        test("drawer dragging clamps both ends and ignores nonfinite pointer motion", () =>
        {
            var drawer = new ReplayDrawerMotion(); drawer.BeginDrag();
            drawer.DragBy(100); Check(drawer.Progress == 1);
            drawer.DragBy(-100); Check(drawer.Progress == 0);
            drawer.DragBy(.3f);
            foreach (float delta in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            { drawer.DragBy(delta); Near(drawer.Progress, .3f); }
        });
        test("drawer ordinary release chooses the nearest end around the halfway threshold", () =>
        {
            foreach (float progress in new[] { 0, .49f, .5f, .51f, 1 })
            {
                var drawer = At(progress); drawer.EndDrag(0);
                Check(drawer.Open == (progress >= .5f) && !drawer.Dragging);
                Near(drawer.Progress, progress);
                drawer.Tick(1); Check(drawer.Progress == (drawer.Open ? 1 : 0));
            }
        });
        test("drawer fast upward and downward releases override the position threshold", () =>
        {
            var drawer = At(.1f); drawer.EndDrag(ReplayDrawerMotion.FlingVelocity);
            Check(drawer.Open); drawer.Tick(1); Check(drawer.Progress == 1);
            drawer.BeginDrag(); drawer.DragBy(-.1f); drawer.EndDrag(-ReplayDrawerMotion.FlingVelocity);
            Check(!drawer.Open); drawer.Tick(1); Check(drawer.Progress == 0);
            drawer = At(.49f); drawer.EndDrag(ReplayDrawerMotion.FlingVelocity - .01f);
            Check(!drawer.Open);
            drawer = At(.51f); drawer.EndDrag(-ReplayDrawerMotion.FlingVelocity + .01f);
            Check(drawer.Open);
        });
        test("drawer unknown release velocity falls back to its actual dragged position", () =>
        {
            foreach (float velocity in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                var drawer = At(.2f); drawer.EndDrag(velocity); Check(!drawer.Open);
                drawer = At(.8f); drawer.EndDrag(velocity); Check(drawer.Open);
            }
        });
        test("drawer ending a drag can animate back to the same previously requested end", () =>
        {
            var drawer = At(.4f); drawer.EndDrag(0);
            Check(!drawer.Open && !drawer.Dragging); drawer.Tick(.1f);
            Check(drawer.Progress < .4f && drawer.Progress > 0);
            drawer.Tick(1); Check(drawer.Progress == 0);
            drawer.SetOpen(true); drawer.Tick(1); drawer.BeginDrag(); drawer.DragBy(-.4f);
            drawer.EndDrag(0); Check(drawer.Open); drawer.Tick(1); Check(drawer.Progress == 1);
        });
        test("drawer explicit commands cancel dragging and stray drag events do not move it", () =>
        {
            var drawer = At(.3f); drawer.SetOpen(true);
            Check(!drawer.Dragging && drawer.Open); Near(drawer.Progress, .3f);
            drawer.DragBy(.2f); drawer.EndDrag(-10); Near(drawer.Progress, .3f); Check(drawer.Open);
            drawer.Tick(1); Check(drawer.Progress == 1);
        });
    }

    private static ReplayDrawerMotion At(float progress)
    {
        var drawer = new ReplayDrawerMotion(); drawer.BeginDrag(); drawer.DragBy(progress); return drawer;
    }
    private static void Near(float actual, float expected) => Check(Math.Abs(actual - expected) < .00001f);
    private static void Check(bool value)
    {
        if (!value) throw new Exception("Replay drawer motion violated.");
    }
}
