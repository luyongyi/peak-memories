using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

// Text-only native UI. Never clone a loading screen, its scripts, or its animated TMP label.
// The loading hint is a child of the actual label, so position, rotation, fade and lifetime
// follow the game. It cannot leave a full-screen panel behind when a scene changes.
internal sealed class ReplayNotice : IDisposable
{
    private sealed class Label
    {
        public readonly GameObject Root;
        public readonly RectTransform Rect;
        private readonly TMP_Text? tmp;
        private readonly Text? plain;

        public Label(Transform parent, string name, Font fallback, TMP_Text? style, string content, int size)
        {
            Root = new GameObject(name, typeof(RectTransform));
            Rect = Root.GetComponent<RectTransform>();
            Rect.SetParent(parent, false);
            // HasCharacters is a read-only check: do not add glyphs or change the game's
            // shared font/fallback tables just to render this Mod's Chinese help text.
            if (style && style!.font && style.font.HasCharacters(content))
            {
                tmp = Root.AddComponent<TextMeshProUGUI>();
                tmp.font = style.font;
                tmp.fontSharedMaterial = style.fontSharedMaterial;
                tmp.fontStyle = FontStyles.Normal;
                tmp.fontSize = size;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.textWrappingMode = TextWrappingModes.Normal;
                tmp.overflowMode = TextOverflowModes.Overflow;
                tmp.raycastTarget = false;
                tmp.richText = false;
            }
            else
            {
                plain = Root.AddComponent<Text>();
                plain.font = fallback;
                plain.fontSize = size;
                plain.alignment = TextAnchor.MiddleCenter;
                plain.horizontalOverflow = HorizontalWrapMode.Wrap;
                plain.verticalOverflow = VerticalWrapMode.Overflow;
                plain.supportRichText = false;
                plain.raycastTarget = false;
                var shadow = Root.AddComponent<Shadow>();
                shadow.effectColor = new Color(.15f, .12f, .08f, .42f);
                shadow.effectDistance = new Vector2(0, -1.5f);
            }
            Set(content, new Color(1f, .97f, .88f));
        }

        public void Set(string content, Color color)
        {
            if (tmp) { if (tmp!.text != content) tmp.text = content; if (tmp.color != color) tmp.color = color; }
            if (plain) { if (plain!.text != content) plain.text = content; if (plain.color != color) plain.color = color; }
        }
    }

    private Font? fallbackFont;
    private TMP_Text? loadingAnchor;
    private CircularTextMeshPro? loadingCurve;
    private LoadingScreen? loadingScreen;
    private Label? loadingHint, toast;
    private GameObject? overlay;
    private CanvasGroup? overlayGroup;
    private double nextScan, toastUntil;
    private string message = "";
    private bool isError;

    public void Show(string text, double seconds = 7, bool error = false)
    {
        message = text ?? "";
        toastUntil = Time.realtimeSinceStartupAsDouble + Math.Max(1, seconds);
        isError = error;
    }

    // No permanent tutorial or recording banner on the title/game screen.
    // Instructions live in Memories; only explicit F9 diagnostics may persist.
    public void Tick(bool overlayOpen, bool playback = false, string? stats = null, string? replayLoading = null)
    {
        double now = Time.realtimeSinceStartupAsDouble;
        // Do not search the entire live island four times/second for a loading UI
        // that does not exist, or decorate normal game loading with a tutorial.
        if (replayLoading != null && LoadingScreenHandler.loading && now >= nextScan)
        {
            nextScan = now + .25;
            ScanLoadingLabel(replayLoading);
        }
        bool nativeLoading = LoadingScreenHandler.loading || VisibleLoadingScreen();
        if (loadingHint?.Root)
        {
            bool show = replayLoading != null && VisibleLoadingScreen() && !playback;
            loadingHint!.Root.SetActive(show);
            if (show)
            {
                loadingHint.Set("回忆录 · " + replayLoading + "\nEsc 取消并返回", new Color(1f, .97f, .88f));
                PositionLoadingHint();
            }
        }

        bool hasToast = now < toastUntil && message.Length != 0;
        bool hasStats = !string.IsNullOrWhiteSpace(stats);
        bool showOverlay = !nativeLoading && !overlayOpen && !playback && (hasToast || hasStats);
        if (!showOverlay) { if (overlay) overlay!.SetActive(false); return; }
        EnsureOverlay();
        overlay!.SetActive(true);
        toast!.Root.SetActive(hasToast || hasStats);
        if (hasToast || hasStats)
        {
            string text = hasToast ? message : "";
            if (hasStats) text += (text.Length > 0 ? "\n" : "") + stats;
            float alpha = hasStats ? 1 : Mathf.Clamp01((float)(toastUntil - now) / .5f);
            Color color = isError && hasToast ? new Color(1f, .8f, .65f, alpha) : new Color(1f, .97f, .88f, alpha);
            toast.Set(text, color);
            toast.Rect.sizeDelta = new Vector2(1100, hasStats ? 200 : 92);
        }
        overlayGroup!.alpha = 1;
    }

    private bool VisibleLoadingScreen() => loadingScreen && loadingScreen!.isActiveAndEnabled &&
        loadingScreen.canvas && loadingScreen.canvas.enabled && loadingAnchor && loadingAnchor!.isActiveAndEnabled &&
        (!loadingScreen.group || loadingScreen.group.alpha > .01f);

    private void ScanLoadingLabel(string replayLoading)
    {
        if (VisibleLoadingScreen()) return;
        TMP_Text? found = null;
        LoadingScreen? owner = null;
        foreach (var animation in Object.FindObjectsByType<LoadingScreenAnimation>(FindObjectsSortMode.None))
        {
            var screen = animation.GetComponentInParent<LoadingScreen>();
            if (screen && screen.canvas && screen.canvas.enabled && animation.loadingText && animation.loadingText.isActiveAndEnabled)
            { found = animation.loadingText; owner = screen; break; }
        }
        if (!found)
            foreach (var animation in Object.FindObjectsByType<LoadingScreenAnimationSimple>(FindObjectsSortMode.None))
            {
                var screen = animation.GetComponentInParent<LoadingScreen>();
                if (screen && screen.canvas && screen.canvas.enabled && animation.loading && animation.loading.isActiveAndEnabled)
                { found = animation.loading; owner = screen; break; }
            }
        if (loadingAnchor == found && loadingHint?.Root) return;
        if (loadingHint?.Root) Object.Destroy(loadingHint!.Root);
        loadingHint = null; loadingAnchor = found; loadingScreen = owner;
        loadingCurve = found ? found!.GetComponent<CircularTextMeshPro>() : null;
        if (!found) return;
        EnsureFont();
        string content = "回忆录 · " + replayLoading + "\nEsc 取消并返回";
        int size = Mathf.Clamp(Mathf.RoundToInt(found!.fontSize * .32f), 22, 30);
        loadingHint = new Label(found.transform, "PEAK Memories - loading help", fallbackFont!, found, content, size);
        PositionLoadingHint();
    }

    private void PositionLoadingHint()
    {
        if (!loadingAnchor || loadingHint == null) return;
        RectTransform source = loadingAnchor!.rectTransform;
        RectTransform rect = loadingHint.Rect;
        // Anchor relative to the native label's pivot, not to a fixed screen corner.
        rect.anchorMin = rect.anchorMax = source.pivot;
        rect.pivot = new Vector2(.5f, 0);
        if (loadingCurve)
        {
            // Verified against the installed Plane prefab: its label pivot is at
            // y=-1090.62 and CircularTextMeshPro bends vertices around Radius=1200.
            // TMP layout bounds are NOT those post-curve vertices; using them would
            // place our unbent child off-screen. The curve's fixed apex is (0,Radius).
            // Reserve one full maximum line height above it, independent of substring
            // length or autosizing, so the hint neither follows the moving final glyph
            // nor jumps when "Loading..." gains a letter. Children stay unbent/readable.
            float lineHeight = loadingAnchor.enableAutoSizing ? loadingAnchor.fontSizeMax : loadingAnchor.fontSize;
            rect.anchoredPosition = new Vector2(0, loadingCurve!.Radius + Mathf.Clamp(lineHeight, 24, 120) + 14);
            rect.sizeDelta = new Vector2(980, 72);
        }
        else
        {
            // Ordinary Basic/White loading screens have no vertex curve. Their fixed
            // layout rectangle is preferable to textBounds, which changes with dots.
            rect.anchoredPosition = new Vector2(source.rect.center.x, source.rect.yMax + 18);
            rect.sizeDelta = new Vector2(Mathf.Clamp(source.rect.width, 780, 1100), 86);
        }
        rect.localScale = Vector3.one;
        rect.localRotation = Quaternion.identity;
    }

    private void EnsureFont()
    {
        if (!fallbackFont)
            fallbackFont = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "Noto Sans CJK SC", "Arial" }, 28);
    }

    private void EnsureOverlay()
    {
        if (overlay) return;
        EnsureFont();
        overlay = new GameObject("PEAK Memories - quiet notices", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
        Object.DontDestroyOnLoad(overlay);
        Canvas canvas = overlay.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32;
        CanvasScaler scaler = overlay.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = .5f;
        overlayGroup = overlay.GetComponent<CanvasGroup>();
        overlayGroup.interactable = false;
        overlayGroup.blocksRaycasts = false;
        // No Image, GraphicRaycaster or background panel: notices cannot block game input.
        toast = new Label(overlay.transform, "Save result", fallbackFont!, null, "", 26);
        toast.Rect.anchorMin = toast.Rect.anchorMax = new Vector2(.5f, 0);
        toast.Rect.pivot = new Vector2(.5f, 0);
        toast.Rect.anchoredPosition = new Vector2(0, 118);
        toast.Rect.sizeDelta = new Vector2(1100, 92);
    }

    public void Dispose()
    {
        if (loadingHint?.Root) Object.Destroy(loadingHint!.Root);
        if (overlay) Object.Destroy(overlay);
        if (fallbackFont) Object.Destroy(fallbackFont);
        loadingHint = toast = null;
        loadingAnchor = null; loadingCurve = null; loadingScreen = null; overlay = null; overlayGroup = null; fallbackFont = null;
        nextScan = 0;
    }
}
