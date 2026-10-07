using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

// Only presentation. Native UIPlayerNames also performs gameplay/customization
// actions, so reuse its typography, never its behaviour or Character references.
internal sealed class ReplayNameplates : IDisposable
{
    private readonly GameObject canvas = null!;
    private readonly Canvas overlay = null!;
    private readonly Dictionary<string, TextMeshProUGUI> labels = new(StringComparer.Ordinal);
    private readonly List<(GameObject Object, bool Active)> nativeNames = new();
    private readonly Action<Exception> reportRestoreError;
    private readonly TMP_FontAsset? font;
    private readonly Material? material;
    private readonly Color color = Color.white;
    private bool disposed;
    public ReplayNameplates(Action<Exception> reportRestoreError)
    {
        this.reportRestoreError = reportRestoreError;
        try
        {
            var native = Object.FindFirstObjectByType<UIPlayerNames>(FindObjectsInactive.Include);
            if (native && native.playerNameText != null)
                foreach (var entry in native.playerNameText)
                {
                    if (!entry) continue;
                    nativeNames.Add((entry.gameObject, entry.gameObject.activeSelf));
                    if (font || !entry.text) continue;
                    font = entry.text.font; material = entry.text.fontSharedMaterial; color = entry.text.color;
                }
            if (!font) font = TMP_Settings.defaultFontAsset;
            canvas = new GameObject("PEAK Replay - recorded player names", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            overlay = canvas.GetComponent<Canvas>();
            overlay.renderMode = RenderMode.ScreenSpaceOverlay;
            overlay.sortingOrder = 20;
            var scaler = canvas.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
            HideNativeNames();
        }
        catch { Dispose(); throw; }
    }
    private void HideNativeNames()
    {
        foreach (var saved in nativeNames)
            if (saved.Object && saved.Object.activeSelf) saved.Object.SetActive(false);
    }
    public void Apply(Camera camera, IReadOnlyDictionary<string, VisualActor> actors)
    {
        HideNativeNames();
        foreach (var pair in actors)
        {
            var actor = pair.Value;
            if (!actor.Visible || actor.State == null)
            { if (labels.TryGetValue(pair.Key, out var hidden)) hidden.gameObject.SetActive(false); continue; }
            Vector3 point = camera.WorldToScreenPoint(actor.NamePosition);
            bool visible = point.z > 0 && point.x >= 0 && point.x <= Screen.width && point.y >= 0 && point.y <= Screen.height;
            if (!labels.TryGetValue(pair.Key, out var label))
            {
                if (!visible) continue;
                var go = new GameObject("Recorded player name", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                go.transform.SetParent(canvas.transform, false);
                label = go.GetComponent<TextMeshProUGUI>(); labels.Add(pair.Key, label);
                if (font) label.font = font!;
                if (material) label.fontSharedMaterial = material!;
                label.fontSize = 24; label.alignment = TextAlignmentOptions.Bottom;
                label.richText = false; label.raycastTarget = false;
                label.textWrappingMode = TextWrappingModes.NoWrap; label.overflowMode = TextOverflowModes.Ellipsis;
                label.color = color;
                label.rectTransform.sizeDelta = new Vector2(340, 38);
                label.rectTransform.pivot = new Vector2(.5f, 0);
            }
            label.gameObject.SetActive(visible);
            if (!visible) continue;
            if (label.text != actor.State.Name) label.text = actor.State.Name;
            label.rectTransform.position = new Vector3(point.x, point.y + 6 * overlay.scaleFactor, 0);
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (canvas)
        {
            try { canvas.SetActive(false); Object.Destroy(canvas); }
            catch (Exception error) { reportRestoreError(error); }
        }
        labels.Clear();
        foreach (var saved in nativeNames)
            try { if (saved.Object) saved.Object.SetActive(saved.Active); }
            catch (Exception error) { reportRestoreError(error); }
        nativeNames.Clear();
    }
}
