using PeakReplayLab;

internal static class NativeMemoriesNoteLayoutTests
{
    public static void Run(Action<string, Action> test)
    {
        test("memoir cover catalogue is bounded to eleven embedded art keys", () =>
        {
            string[] all = { "shore", "roots", "tropics", "alpine", "mesa", "volcano", "swamp", "kiln", "temple", "peak", "nadir" };
            Check(all.Length == NativeMemoriesNoteLayout.MaximumCovers && all.All(NativeMemoriesNoteLayout.IsKnownCover));
            Check(!NativeMemoriesNoteLayout.IsKnownCover(null) && !NativeMemoriesNoteLayout.IsKnownCover("../peak") && !NativeMemoriesNoteLayout.IsKnownCover("Void"));
            Check(NativeMemoriesNoteLayout.VisibleKeys(new[] { "shore", "unknown", "shore", "roots" }, true).SequenceEqual(new[] { "shore", "roots" }));
            Check(NativeMemoriesNoteLayout.VisibleKeys(new[] { "shore", "nadir" }, false).SequenceEqual(new[] { "nadir" }));
            Check(NativeMemoriesNoteLayout.VisibleKeys(null, true).Length == 0);
            Check(NativeMemoriesNoteLayout.TextureWidth * NativeMemoriesNoteLayout.TextureHeight * 4 * all.Length == 4325376);
        });
        test("memoir note view never shows peak and nadir as two endings", () =>
        {
            foreach (bool full in new[] { true, false })
                foreach (var keys in new[] { new[] { "peak", "nadir" }, new[] { "nadir", "peak" }, new[] { "unknown", "peak", "nadir", "nadir", "bad" } })
                    Check(NativeMemoriesNoteLayout.VisibleKeys(keys, full).SequenceEqual(new[] { "nadir" }));
            Check(NativeMemoriesNoteLayout.VisibleKeys(new[] { "shore", "peak", "nadir" }, true).SequenceEqual(new[] { "shore", "nadir" }));
            Check(NativeMemoriesNoteLayout.VisibleKeys(new[] { "shore", "bad", "roots", "unknown" }, false).SequenceEqual(new[] { "roots" }));
            Check(NativeMemoriesNoteLayout.VisibleKeys(new[] { "unknown", "bad" }, false).Length == 0);
        });
        test("memoir diagonal panels keep straight note edges and no overlaps or reversed quads", () =>
        {
            Check(NativeMemoriesNoteLayout.Panels(0).Length == 0 && NativeMemoriesNoteLayout.Panels(12).Length == 0);
            for (int count = 1; count <= 11; count++)
            {
                var panels = NativeMemoriesNoteLayout.Panels(count);
                Check(panels.Length == count && panels[0].LeftBottom == 0 && panels[0].LeftTop == 0);
                Check(panels[^1].RightBottom == 1 && panels[^1].RightTop == 1);
                for (int i = 0; i < count; i++)
                {
                    var p = panels[i];
                    Check(p.LeftBottom >= 0 && p.RightTop <= 1 && p.LeftBottom <= p.LeftTop && p.RightBottom <= p.RightTop);
                    Check(p.LeftBottom < p.RightBottom && p.LeftTop < p.RightTop);
                    if (i == 0) continue;
                    Check(panels[i - 1].RightBottom < p.LeftBottom && panels[i - 1].RightTop < p.LeftTop);
                    Check(Math.Abs(p.LeftTop - p.LeftBottom - (panels[i - 1].RightTop - panels[i - 1].RightBottom)) < .000001f);
                }
            }
        });
        test("memoir texture crop preserves aspect and never samples outside the image", () =>
        {
            foreach (float imageAspect in new[] { 2f / 3, 1f, 2f })
                foreach (float panelAspect in new[] { .4f, .8f, 1f, 1.6f, 5f })
                {
                    var crop = NativeMemoriesNoteLayout.Crop(imageAspect, panelAspect);
                    Check(crop.Left >= 0 && crop.Bottom >= 0 && crop.Left + crop.Width <= 1.00001f && crop.Bottom + crop.Height <= 1.00001f);
                    Check(Math.Abs(imageAspect * crop.Width / crop.Height - panelAspect) < .00001f);
                }
            foreach (float value in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
            {
                var crop = NativeMemoriesNoteLayout.Crop(value, 1);
                Check(crop.Width == 1 && crop.Height == 1);
            }
            var gate = NativeMemoriesNoteLayout.Crop(2f / 3, 112f / 68, "nadir");
            Check(gate.Bottom <= .76f && gate.Bottom + gate.Height >= .76f && gate.Bottom > .5f);
        });
    }

    private static void Check(bool condition) { if (!condition) throw new Exception("Memoir note layout contract failed."); }
}
