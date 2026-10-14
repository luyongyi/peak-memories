using PeakReplayLab;

internal static class NativeRendererVisibilityTests
{
    private sealed class Renderer
    {
        public bool Enabled;
        public int Writes;
    }
    private static NativeRendererVisibilityPolicy<Renderer> Policy() => new(
        static renderer => renderer.Enabled,
        static (renderer, enabled) => { renderer.Enabled = enabled; renderer.Writes++; });

    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("unchanged large native appearance does not toggle present renderers", () =>
        {
            var policy = Policy(); var renderers = Enumerable.Range(0, 295).Select(_ => new Renderer { Enabled = true }).ToArray();
            for (int frame = 0; frame < 120; frame++)
            {
                policy.Begin();
                foreach (var renderer in renderers) policy.SetDesired(renderer, true);
                foreach (var renderer in renderers) policy.HideAbsent(renderer);
            }
            check(renderers.All(renderer => renderer.Enabled && renderer.Writes == 0));
        });
        test("appearance visibility boundaries write only actual changes", () =>
        {
            var policy = Policy(); var visible = new Renderer(); var hidden = new Renderer { Enabled = true };
            for (int frame = 0; frame < 20; frame++)
            {
                policy.Begin(); policy.SetDesired(visible, true); policy.SetDesired(hidden, false);
                policy.HideAbsent(visible); policy.HideAbsent(hidden);
            }
            check(visible.Enabled && !hidden.Enabled && visible.Writes == 1 && hidden.Writes == 1);
        });
        test("native lamps retain prior illumination across a deferred update", () =>
        {
            var policy = Policy(); var first = new Renderer { Enabled = true }; var next = new Renderer { Enabled = true }; var removed = new Renderer { Enabled = true };
            policy.Begin();
            // First budget slice applies one present lamp; the rest must retain
            // their last completed lighting while the update waits for a frame.
            policy.SetDesired(first, true);
            check(first.Enabled && next.Enabled && removed.Enabled && first.Writes + next.Writes + removed.Writes == 0);
            policy.SetDesired(next, true);
            policy.HideAbsent(first); policy.HideAbsent(next); policy.HideAbsent(removed);
            check(first.Enabled && next.Enabled && !removed.Enabled && first.Writes == 0 && next.Writes == 0 && removed.Writes == 1);
            // A cancelled/reversed seek starts a new target set, without leaking
            // an earlier partial update's membership into absence decisions.
            policy.Begin(); policy.SetDesired(removed, true);
            policy.HideAbsent(first); policy.HideAbsent(next); policy.HideAbsent(removed);
            check(!first.Enabled && !next.Enabled && removed.Enabled && removed.Writes == 2);
        });
        test("known partial and empty appearance hides omitted authored meshes", () =>
        {
            var policy = Policy(); var retained = new Renderer { Enabled = true }; var omitted = new Renderer { Enabled = true };
            policy.Begin(); policy.SetDesired(retained, true); policy.HideAbsent(retained); policy.HideAbsent(omitted);
            check(retained.Enabled && retained.Writes == 0 && !omitted.Enabled && omitted.Writes == 1);
            policy.Begin(); policy.HideAbsent(retained); policy.HideAbsent(omitted);
            check(!retained.Enabled && retained.Writes == 1 && omitted.Writes == 1);
        });
        test("unchanged appearance corrects hierarchy pose visibility and reveal requests", () =>
        {
            var policy = Policy(); var renderer = new Renderer();
            policy.Begin(); policy.SetDesired(renderer, true); policy.HideAbsent(renderer);
            // Pose can independently hide this node before the identical appearance
            // snapshot runs again; checking frame identity alone cannot skip it.
            renderer.Enabled = false;
            policy.Begin(); policy.SetDesired(renderer, true); policy.HideAbsent(renderer);
            check(renderer.Enabled && renderer.Writes == 2);
            // The caller supplies authored visibility when reveal is requested.
            policy.Begin(); policy.SetDesired(renderer, false); policy.HideAbsent(renderer);
            check(!renderer.Enabled && renderer.Writes == 3);
        });
    }
}
