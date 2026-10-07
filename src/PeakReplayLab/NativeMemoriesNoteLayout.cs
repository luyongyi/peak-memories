using System;
using System.Collections.Generic;

namespace PeakReplayLab;

// Geometry is expressed in the whole cover strip's normalized coordinates.
// First/last edges stay vertical so all panels belong to one rectangular note.
internal static class NativeMemoriesNoteLayout
{
    public const int TextureWidth = 256, TextureHeight = 384;
    public const int MaximumCovers = 11;
    public static bool IsKnownCover(string? key) => key is "shore" or "roots" or "tropics" or "alpine" or "mesa" or
        "volcano" or "swamp" or "kiln" or "temple" or "peak" or "nadir";

    public static string[] VisibleKeys(string[]? keys, bool fullRun)
    {
        if (keys == null || keys.Length == 0) return Array.Empty<string>();
        var visible = new List<string>();
        foreach (string key in keys)
        {
            if (!IsKnownCover(key) || visible.Contains(key)) continue;
            visible.Add(key);
            if (visible.Count == MaximumCovers) break;
        }
        // Retain the exclusive ending invariant even if malformed or older
        // metadata ever hands the view both alternatives in either order.
        if (visible.Contains("nadir")) visible.Remove("peak");
        if (!fullRun && visible.Count > 1) return new[] { visible[visible.Count - 1] };
        return visible.ToArray();
    }

    public static NoteCoverPanel[] Panels(int count)
    {
        if (count <= 0 || count > MaximumCovers) return Array.Empty<NoteCoverPanel>();
        var panels = new NoteCoverPanel[count];
        float slant = Math.Min(.035f, .4f / count), gap = count > 1 ? .004f : 0;
        for (int i = 0; i < count; i++)
        {
            float left = (float)i / count, right = (float)(i + 1) / count;
            panels[i] = new NoteCoverPanel(
                i == 0 ? 0 : left - slant / 2 + gap / 2,
                i == 0 ? 0 : left + slant / 2 + gap / 2,
                i == count - 1 ? 1 : right - slant / 2 - gap / 2,
                i == count - 1 ? 1 : right + slant / 2 - gap / 2);
        }
        return panels;
    }

    // Center crop, slightly above the middle for the landscape/gate subjects.
    // Texture UVs stay in [0,1]; a slanted polygon must not stretch the artwork.
    public static NoteCoverCrop Crop(float imageAspect, float panelAspect, string? key = null)
    {
        if (!(imageAspect > 0) || !(panelAspect > 0) || float.IsInfinity(imageAspect) || float.IsInfinity(panelAspect))
            return new NoteCoverCrop(0, 0, 1, 1);
        float width = Math.Min(1, panelAspect / imageAspect), height = Math.Min(1, imageAspect / panelAspect);
        // Unity's UV origin is at the bottom. Nadir's gate is in the upper
        // quarter and slightly right of center, unlike the other illustrations.
        float focusX = key == "nadir" ? .6f : .5f, focusY = key == "nadir" ? .76f : .58f;
        float left = Math.Min(1 - width, Math.Max(0, focusX - width / 2));
        float bottom = Math.Min(1 - height, Math.Max(0, focusY - height / 2));
        return new NoteCoverCrop(left, bottom, width, height);
    }
}

internal readonly struct NoteCoverPanel
{
    public readonly float LeftBottom, LeftTop, RightBottom, RightTop;
    public NoteCoverPanel(float leftBottom, float leftTop, float rightBottom, float rightTop)
    { LeftBottom = leftBottom; LeftTop = leftTop; RightBottom = rightBottom; RightTop = rightTop; }
}

internal readonly struct NoteCoverCrop
{
    public readonly float Left, Bottom, Width, Height;
    public NoteCoverCrop(float left, float bottom, float width, float height)
    { Left = left; Bottom = bottom; Width = width; Height = height; }
}
