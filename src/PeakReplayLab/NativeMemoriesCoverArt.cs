using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

// Images are private embedded resources, not web URLs or user-controlled paths.
// Allocated only while building the library; never touched by the recorder loop.
internal sealed class NativeMemoriesCoverArt : IDisposable
{
    private readonly Dictionary<string, Texture2D?> textures = new(StringComparer.Ordinal);
    private bool disposed;

    public Texture2D? Get(string key)
    {
        if (disposed || !NativeMemoriesNoteLayout.IsKnownCover(key)) return null;
        if (textures.TryGetValue(key, out var cached)) return cached;
        Texture2D? source = null, result = null;
        RenderTexture? target = null;
        var previous = RenderTexture.active;
        try
        {
            using var stream = typeof(NativeMemoriesCoverArt).Assembly.GetManifestResourceStream("PeakReplayLab.CoverArt." + key + ".png");
            if (stream == null) throw new FileNotFoundException("Missing embedded cover: " + key);
            if (stream.Length <= 0 || stream.Length > 8 * 1024 * 1024) throw new InvalidDataException("Invalid embedded cover size: " + key);
            using var bytes = new MemoryStream((int)stream.Length);
            stream.CopyTo(bytes);
            source = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            if (!ImageConversion.LoadImage(source, bytes.ToArray(), true)) throw new InvalidDataException("Cannot decode embedded cover: " + key);
            source.filterMode = FilterMode.Bilinear;
            target = RenderTexture.GetTemporary(NativeMemoriesNoteLayout.TextureWidth, NativeMemoriesNoteLayout.TextureHeight,
                0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            Graphics.Blit(source, target);
            RenderTexture.active = target;
            result = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false, false)
            { name = "Memories cover " + key, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            result.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
            result.Apply(false, true); // No retained CPU pixel copy or mip pyramid.
        }
        catch (Exception ex)
        {
            if (result) Object.Destroy(result);
            result = null;
            Debug.LogWarning("[PeakReplayLab] Cover unavailable; keeping native paper: " + key + " — " + ex.Message);
        }
        finally
        {
            RenderTexture.active = previous;
            if (target) RenderTexture.ReleaseTemporary(target);
            if (source) Object.Destroy(source);
        }
        // A failed asset is cached too: rebuilding/paging never repeats decode errors.
        textures[key] = result;
        return result;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var texture in textures.Values) if (texture) Object.Destroy(texture);
        textures.Clear();
    }
}

// Each child draws one quadrilateral in a common strip, with plain vertical
// outer edges and diagonal internal seams. It is decoration, never a click target.
internal sealed class NativeMemoriesCoverPanel : MaskableGraphic
{
    private Texture? texture;
    private NoteCoverPanel panel;
    private string key = "";
    public override Texture mainTexture => texture ? texture! : s_WhiteTexture;

    public void Configure(Texture image, NoteCoverPanel shape, string coverKey)
    {
        texture = image; panel = shape; key = coverKey; raycastTarget = false;
        SetAllDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (!texture) return;
        var rect = GetPixelAdjustedRect();
        float left = Mathf.Min(panel.LeftBottom, panel.LeftTop), right = Mathf.Max(panel.RightBottom, panel.RightTop);
        float width = (right - left) * rect.width;
        if (width <= 0 || rect.height <= 0) return;
        var crop = NativeMemoriesNoteLayout.Crop((float)texture!.width / texture.height, width / rect.height, key);
        Add(vh, rect, panel.LeftBottom, 0, left, right, crop);
        Add(vh, rect, panel.LeftTop, 1, left, right, crop);
        Add(vh, rect, panel.RightTop, 1, left, right, crop);
        Add(vh, rect, panel.RightBottom, 0, left, right, crop);
        vh.AddTriangle(0, 1, 2); vh.AddTriangle(2, 3, 0);
    }

    private void Add(VertexHelper vh, Rect rect, float x, float y, float left, float right, NoteCoverCrop crop) =>
        vh.AddVert(new Vector3(rect.xMin + x * rect.width, rect.yMin + y * rect.height), color,
            new Vector2(crop.Left + (x - left) / (right - left) * crop.Width, crop.Bottom + y * crop.Height));
}
