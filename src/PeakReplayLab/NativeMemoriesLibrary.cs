using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Zorro.Core;
using Zorro.UI.Modal;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

// The real game's Modal owns the backdrop, typography, fade and controller scope.
// Only its content is ours. Never clone a menu page (which can start matchmaking).
internal sealed class NativeMemoriesLibrary : IDisposable
{
    private enum Page { Library, Details, Routes, Modes, ConfirmMode, ConfirmDelete, Loading }
    internal sealed class State
    {
        public MemoriesLibraryItem[] Items = Array.Empty<MemoriesLibraryItem>();
        public bool FullRuns, Reading, CanSwitch;
        public ReplayRecordingMode Mode;
        public string ModeHelp = "", Status = "";
        public bool CanManage, Managing, DeletePreparing, DeleteMoving, DeleteReady;
        public string DeleteError = "";
        public MemoriesLibraryItem? DeleteItem;
        public long DeleteBytes;
        public bool CanUpload, RouteExporting, RouteSending, RouteReady, RouteFinished;
        public string RouteSummary = "", RouteError = "";
        public float RouteProgress;
    }
    private sealed class Content : ModalContentOption
    {
        private readonly NativeMemoriesLibrary owner;
        public Content(NativeMemoriesLibrary owner) => this.owner = owner;
        public override void Setup(Transform parent) => owner.Build(parent);
    }
    private sealed class Header : HeaderModalOption
    {
        private readonly NativeMemoriesLibrary owner;
        private readonly string title, subtitle;
        public Header(NativeMemoriesLibrary owner, string title, string subtitle) { this.owner = owner; this.title = title; this.subtitle = subtitle; }
        public override void Setup(Transform parent)
        {
            var root = new GameObject("Memories heading", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
            owner.headerRoot = root;
            Place(root, parent, new Vector2(0, 320), new Vector2(1100, 210));
            var column = root.GetComponent<VerticalLayoutGroup>();
            column.childControlWidth = true; column.childControlHeight = false;
            column.childForceExpandWidth = true; column.childForceExpandHeight = false;
            new DefaultHeaderModalOption(title, subtitle).Setup(root.transform);
        }
    }

    private readonly Action close, refresh, openFolder, cancelLoading;
    private readonly Action<bool> chooseCollection;
    private readonly Action<string> play;
    private readonly Action<ReplayRecordingMode> chooseMode;
    private readonly Func<string, bool> requestDelete;
    private readonly Action confirmDelete, cancelDelete;
    private readonly Func<string, bool> prepareRoute;
    private readonly Action uploadRoute, cancelRoute, openRoutes;
    private readonly List<Button[]> navigationRows = new();
    private readonly NativeMemoriesCoverArt covers = new();
    private State state = new();
    private Page page;
    private ReplayRecordingMode pendingMode;
    private MemoriesLibraryItem? selected;
    private int pageIndex;
    private bool dirty = true, silentClose, disposed, focusPending;
    private GameObject? contentRoot, headerRoot, previousSelection;
    private Modal? modal;
    private GameObject? buttonPrefab, textPrefab;
    private string loadingLabel = "";
    private TMP_Text? loadingText;
    private TMP_Text? routeText;
    private Button? firstButton;
    public bool IsVisible => OwnsModal && modal!.Visible;
    private bool OwnsModal => modal && headerRoot && modal!.headerParent.childCount == 1 &&
        modal.headerParent.GetChild(0) == headerRoot!.transform;

    public NativeMemoriesLibrary(Action close, Action refresh, Action openFolder, Action<bool> chooseCollection,
        Action<string> play, Action<ReplayRecordingMode> chooseMode, Action cancelLoading,
        Func<string, bool> requestDelete, Action confirmDelete, Action cancelDelete,
        Func<string, bool> prepareRoute, Action uploadRoute, Action cancelRoute, Action openRoutes)
    {
        this.close = close; this.refresh = refresh; this.openFolder = openFolder; this.chooseCollection = chooseCollection;
        this.play = play; this.chooseMode = chooseMode; this.cancelLoading = cancelLoading;
        this.requestDelete = requestDelete; this.confirmDelete = confirmDelete; this.cancelDelete = cancelDelete;
        this.prepareRoute = prepareRoute; this.uploadRoute = uploadRoute; this.cancelRoute = cancelRoute; this.openRoutes = openRoutes;
    }

    public bool CanOpen => !Modal.IsOpen || OwnsModal;

    public void Open()
    {
        if (!CanOpen) return;
        if (!IsVisible && EventSystem.current) previousSelection = EventSystem.current.currentSelectedGameObject;
        page = Page.Library; selected = null; dirty = true;
    }

    public void Back()
    {
        if (!OwnsModal) return;
        if (page == Page.Loading) { cancelLoading(); return; }
        if (page == Page.Routes) { cancelRoute(); Navigate(Page.Details); return; }
        if (page == Page.ConfirmDelete) { cancelDelete(); Navigate(Page.Details); }
        else if (page == Page.ConfirmMode) Navigate(Page.Modes);
        else if (page != Page.Library) Navigate(Page.Library);
        else { Hide(); close(); }
    }

    public void Tick(State next, bool showLibrary, bool loading, bool nativeLoading, string label)
    {
        if (disposed) return;
        if (!showLibrary && !loading) { Hide(); return; }
        if (nativeLoading) { Hide(); return; }
        if (contentRoot && !OwnsModal)
        {
            // Another game modal replaced ours. Do not close it or immediately steal it back.
            contentRoot = null; firstButton = null; close(); return;
        }
        if (Modal.IsOpen && !OwnsModal) { if (showLibrary) close(); return; }
        if (next.Items != state.Items || next.FullRuns != state.FullRuns || next.Reading != state.Reading ||
            next.Mode != state.Mode || next.CanSwitch != state.CanSwitch || next.ModeHelp != state.ModeHelp || next.Status != state.Status ||
            next.CanManage != state.CanManage || next.Managing != state.Managing || next.DeletePreparing != state.DeletePreparing ||
            next.DeleteMoving != state.DeleteMoving || next.DeleteReady != state.DeleteReady || next.DeleteError != state.DeleteError ||
            next.DeleteItem != state.DeleteItem || next.DeleteBytes != state.DeleteBytes ||
            next.CanUpload != state.CanUpload || next.RouteExporting != state.RouteExporting || next.RouteSending != state.RouteSending ||
            next.RouteReady != state.RouteReady || next.RouteFinished != state.RouteFinished || next.RouteError != state.RouteError)
            dirty = true;
        state = next;
        if (page == Page.Routes && routeText) routeText!.text = RouteDescription();
        if (page == Page.ConfirmDelete && next.DeleteItem != null) selected = next.DeleteItem;
        pageIndex = MemoriesLibraryModel.ClampPage(pageIndex, state.Items.Length);
        if (loading)
        {
            if (page != Page.Loading) { page = Page.Loading; dirty = true; }
            loadingLabel = label;
            if (loadingText) loadingText!.text = label + "\n\nEsc 可取消并返回";
        }
        else if (page == Page.Loading) { page = Page.Library; dirty = true; }
        if (dirty || !contentRoot) Render();
        if (focusPending && EventSystem.current && firstButton)
        {
            // Set selection after content is alive; the native modal handles controller priority.
            EventSystem.current.SetSelectedGameObject(firstButton!.gameObject);
            focusPending = false;
        }
    }

    private void Navigate(Page next) { page = next; dirty = true; }

    private void Render()
    {
        string title = "回忆录", subtitle;
        switch (page)
        {
            case Page.Details: title = "这一段旅程"; subtitle = selected?.Title ?? "录像详情"; break;
            case Page.Routes: title = "上传轨迹"; subtitle = "完整录像 · 只上传筛选后的坐标与关键信息"; break;
            case Page.Modes: title = "录制方式"; subtitle = NativeMemoriesRecordingControls.Subtitle; break;
            case Page.ConfirmMode: title = NativeMemoriesRecordingControls.ConfirmTitle(pendingMode); subtitle = "片段缓存保留 · 已保存的回忆不会改变"; break;
            case Page.ConfirmDelete: title = "删除这份回忆？"; subtitle = "移入本地回收目录，不会永久删除"; break;
            case Page.Loading: title = "正在打开回忆"; subtitle = "自动还原当时的场景与装扮 · 只读离线重演"; break;
            default:
                subtitle = state.Reading ? "正在读取本地录像…" :
                    (state.FullRuns ? "完整录像" : "精彩片段") + " · " + state.Items.Length + " 份回忆 · " +
                    NativeMemoriesRecordingControls.Status(state.Mode);
                break;
        }
        if (!buttonPrefab)
        {
            var field = Resources.Load<GameObject>("ButtonsInputField");
            buttonPrefab = field ? field.GetComponent<ModalButtonsField>()?.m_buttonPrefab : null;
            textPrefab = Resources.Load<GameObject>("SubheaderModalField");
            if (!buttonPrefab || !textPrefab) throw new InvalidOperationException("未找到游戏原生回忆录界面资源，请检查游戏版本。");
        }
        modal = RetrievableResourceSingleton<Modal>.Instance;
        Modal.OpenModal(new Header(this, title, subtitle), new Content(this), OnClosed);
        foreach (var text in modal.headerParent.GetComponentsInChildren<TMP_Text>(true)) text.richText = false;
        dirty = false;
    }

    private void Build(Transform parent)
    {
        navigationRows.Clear(); firstButton = null; loadingText = null; routeText = null;
        contentRoot = new GameObject("PEAK Memories - native modal content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        var rect = contentRoot.GetComponent<RectTransform>();
        Place(contentRoot, parent, new Vector2(0, -120), new Vector2(1050, 600));
        var column = contentRoot.GetComponent<VerticalLayoutGroup>();
        column.spacing = 8; column.childControlWidth = true; column.childControlHeight = true;
        column.childForceExpandWidth = true; column.childForceExpandHeight = false;
        column.childAlignment = TextAnchor.UpperCenter;
        switch (page)
        {
            case Page.Library: BuildLibrary(rect); break;
            case Page.Details: BuildDetails(rect); break;
            case Page.Routes: BuildRoutes(rect); break;
            case Page.Modes: BuildModes(rect); break;
            case Page.ConfirmMode: BuildConfirmation(rect); break;
            case Page.ConfirmDelete: BuildDeleteConfirmation(rect); break;
            case Page.Loading:
                loadingText = Label(rect, loadingLabel + "\n\nEsc 可取消并返回", 200, 28);
                Row(rect, 58, ("取消并返回", cancelLoading, true));
                break;
        }
        ConnectNavigation();
        focusPending = true;
    }

    private void BuildLibrary(Transform parent)
    {
        Row(parent, 52,
            (state.FullRuns ? "精彩片段" : "· 精彩片段 ·", () => Collection(false), !state.Managing),
            (state.FullRuns ? "· 完整录像 ·" : "完整录像", () => Collection(true), !state.Managing));
        var list = Column(parent, 352);
        if (state.Items.Length == 0)
            Label(list, state.Reading ? "正在整理你的回忆…" : state.FullRuns ?
                "这里还没有完整录像\n\n在录制方式中开启完整录制，或按 F4 开启；F6 仍可保存片段。" :
                "这里还没有精彩片段\n\n正常游玩时按 F6 保存最近最多 120 秒，完整录制开关不影响片段保存。", 330, 26);
        int start = pageIndex * MemoriesLibraryModel.PageSize;
        for (int i = start; i < Math.Min(state.Items.Length, start + MemoriesLibraryModel.PageSize); i++)
        {
            var item = state.Items[i];
            Note(list, item);
        }
        int count = MemoriesLibraryModel.PageCount(state.Items.Length);
        Row(parent, 48,
            ("上一页", () => { pageIndex--; dirty = true; }, pageIndex > 0),
            ($"{pageIndex + 1} / {count}", () => { }, false),
            ("下一页", () => { pageIndex++; dirty = true; }, pageIndex + 1 < count));
        Row(parent, 58,
            ("录制方式", () => Navigate(Page.Modes), !state.Managing),
            (state.Reading ? "读取中…" : "刷新", refresh, !state.Reading && !state.Managing),
            ("打开文件夹", openFolder, true),
            ("返回主菜单", () => { Hide(); close(); }, true));
        string status = state.Status.Length > 150 ? state.Status.Substring(0, 150) + "…" : state.Status;
        Label(parent, status, 40, 21);
    }

    private void Collection(bool full)
    {
        if (full == state.FullRuns) return;
        pageIndex = 0; selected = null; chooseCollection(full); dirty = true;
    }

    private void Note(Transform parent, MemoriesLibraryItem item)
    {
        // The native paper image, selected tint, sound and Button remain one
        // clickable note. Decorative covers do not create nested Selectables.
        var go = Object.Instantiate(buttonPrefab!, parent, false);
        go.name = "Memories note"; go.SetActive(true); Size(go, 80).flexibleWidth = 1;
        var button = go.GetComponent<Button>();
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(() => { selected = item; Navigate(Page.Details); });
        button.interactable = !state.Managing;
        var text = go.GetComponentInChildren<TMP_Text>(true);
        text.transform.SetParent(go.transform, false);
        text.text = item.Title + "\n" + item.Subtitle;
        text.richText = false; text.raycastTarget = false;
        text.enableAutoSizing = true; text.fontSizeMin = 18; text.fontSizeMax = 25;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.alignment = TextAlignmentOptions.MidlineLeft;

        string[] keys = NativeMemoriesNoteLayout.VisibleKeys(item.CoverKeys, item.IsFullRun);
        float coverWidth = keys.Length == 0 ? 0 : item.IsFullRun && keys.Length > 1 ? 352 : 112;
        if (coverWidth > 0)
        {
            var strip = new GameObject("Visited regions - cover strip", typeof(RectTransform), typeof(RectMask2D));
            var stripRect = strip.GetComponent<RectTransform>();
            stripRect.SetParent(go.transform, false);
            stripRect.anchorMin = new Vector2(0, 0); stripRect.anchorMax = new Vector2(0, 1);
            stripRect.offsetMin = new Vector2(8, 6); stripRect.offsetMax = new Vector2(8 + coverWidth, -6);
            var panels = NativeMemoriesNoteLayout.Panels(keys.Length);
            for (int i = 0; i < keys.Length; i++)
            {
                var texture = covers.Get(keys[i]);
                if (!texture) continue;
                var art = new GameObject("Cover " + keys[i], typeof(RectTransform), typeof(CanvasRenderer), typeof(NativeMemoriesCoverPanel));
                var artRect = art.GetComponent<RectTransform>();
                artRect.SetParent(stripRect, false);
                artRect.anchorMin = Vector2.zero; artRect.anchorMax = Vector2.one;
                artRect.offsetMin = artRect.offsetMax = Vector2.zero;
                art.GetComponent<NativeMemoriesCoverPanel>().Configure(texture!, panels[i], keys[i]);
            }
        }
        var textRect = (RectTransform)text.transform;
        textRect.anchorMin = Vector2.zero; textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(coverWidth > 0 ? coverWidth + 26 : 24, 7);
        textRect.offsetMax = new Vector2(-24, -7);
        navigationRows.Add(new[] { button });
        if (!firstButton && button.interactable) firstButton = button;
    }

    private void BuildDetails(Transform parent)
    {
        var item = selected;
        ScrollDetails(parent, item?.Details ?? "这份录像已不可用，请刷新列表。");
        Label(parent, state.Status.Length > 150 ? state.Status.Substring(0, 150) + "…" : state.Status, 40, 21);
        var playback = (item?.Playable == true ? "进入回忆" : "这份录像暂不可播放", (Action)(() =>
        {
            if (item?.Playable != true) return;
            // Open first. If validation throws, the owned details stay available.
            play(item.Path);
        }), item?.Playable == true && !state.Managing);
        if (item?.IsFullRun == true)
            Row(parent, 62, playback, ("上传轨迹", () =>
            {
                if (item.Playable && state.CanUpload && prepareRoute(item.Path)) Navigate(Page.Routes);
            }, item.Playable && state.CanUpload));
        else Row(parent, 62, playback);
        Row(parent, 48, ("返回录像列表", () => Navigate(Page.Library), true),
            (state.Managing ? "正在处理文件…" : "删除录像", () =>
            {
                if (item != null && state.CanManage && requestDelete(item.Path)) Navigate(Page.ConfirmDelete);
            }, item != null && state.CanManage));
    }

    private string RouteDescription() => state.RouteExporting
        ? $"{state.RouteSummary}\n\n筛选进度：{Math.Max(0, Math.Min(100, state.RouteProgress * 100)):F0}%"
        : state.RouteSummary + (state.RouteSending ? "\n\n正在上传…" : "") +
            (state.RouteError.Length == 0 ? "" : "\n\n" + state.RouteError);

    private void BuildRoutes(Transform parent)
    {
        routeText = Label(parent, RouteDescription(), 400, 26);
        if (state.RouteFinished)
            Row(parent, 62, ("打开地图网站", openRoutes, true));
        else
            Row(parent, 62, (state.RouteExporting ? "正在筛选…" : state.RouteSending ? "正在上传…" :
                state.RouteError.Length > 0 && state.RouteReady ? "重试上传" : "确认上传到 Web", uploadRoute,
                state.RouteReady && !state.RouteSending && !state.RouteExporting));
        Row(parent, 48, (state.RouteExporting || state.RouteSending ? "取消并返回" : "返回录像详情", Back, true));
    }

    private void BuildDeleteConfirmation(Transform parent)
    {
        var item = state.DeleteItem ?? selected;
        string summary = item == null ? "未选择录像" : item.Title + "\n" + item.Subtitle +
            "\n文件：" + Path.GetFileName(item.Path);
        if (state.DeleteReady) summary += $"\n文件大小：{state.DeleteBytes / 1048576d:F1} MiB";
        string progress = state.DeleteMoving ? "正在移入回收目录…\n可以返回，已确认的操作会在后台完成。" :
            state.DeletePreparing ? "正在核对文件，请稍候…" : state.DeleteError;
        Label(parent, summary, 180, 26);
        Label(parent, "文件将移到 BepInEx / PeakReplayLab / Trash\n这是 Mod 的本地回收目录，不是 Windows 回收站。\n误删后可从该目录移回原录像目录；不会自动清空。", 130, 23);
        Label(parent, progress, 90, 23);
        // The safe action is first, so keyboard/controller focus never defaults
        // to deleting a recording. Keep it bound to the prepared file ticket.
        Row(parent, 60, (state.DeleteMoving ? "返回" : "取消，保留录像", Back, true),
            (state.DeleteMoving ? "处理中…" : "确认删除", confirmDelete, state.DeleteReady && !state.DeleteMoving));
    }

    public void DeletionFinished(string path)
    {
        if (selected == null || !string.Equals(selected.Path, path, StringComparison.OrdinalIgnoreCase)) return;
        selected = null;
        // Update our state only; never reopen a modal that the user left or the
        // game replaced while a confirmed file move was running.
        Navigate(Page.Library);
    }

    private void ScrollDetails(Transform parent, string text)
    {
        var viewport = new GameObject("Memories details viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(ScrollRect), typeof(LayoutElement));
        viewport.transform.SetParent(parent, false); Size(viewport, 360);
        viewport.GetComponent<Image>().color = Color.clear;
        var content = new GameObject("Memories details text", typeof(RectTransform));
        var contentRect = content.GetComponent<RectTransform>();
        contentRect.SetParent(viewport.transform, false);
        contentRect.anchorMin = new Vector2(0, 1); contentRect.anchorMax = Vector2.one;
        contentRect.pivot = new Vector2(.5f, 1);
        var label = Label(content.transform, text, 360, 24);
        label.enableAutoSizing = false; label.fontSize = 24;
        label.alignment = TextAlignmentOptions.TopLeft;
        float height = Mathf.Max(360, label.GetPreferredValues(text, 1000, 0).y + 16);
        contentRect.sizeDelta = new Vector2(0, height);
        var textRoot = (RectTransform)label.transform;
        textRoot.anchorMin = Vector2.zero; textRoot.anchorMax = Vector2.one;
        textRoot.offsetMin = new Vector2(24, 8); textRoot.offsetMax = new Vector2(-24, -8);
        var scroll = viewport.GetComponent<ScrollRect>();
        scroll.viewport = viewport.GetComponent<RectTransform>(); scroll.content = contentRect;
        scroll.horizontal = false; scroll.vertical = true; scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 36; scroll.verticalNormalizedPosition = 1;
    }

    private void BuildModes(Transform parent)
    {
        foreach (var choice in NativeMemoriesRecordingControls.Choices(state.Mode, state.CanSwitch))
            Row(parent, 80, (choice.Label, () => AskMode(choice.Mode), choice.Enabled));
        Label(parent, NativeMemoriesRecordingControls.Help + (state.CanSwitch ? "" :
            "\n正在处理录像，请等完成后再修改完整录制。"), 160, 24);
        Row(parent, 48, ("返回录像列表", () => Navigate(Page.Library), true));
    }

    private void AskMode(ReplayRecordingMode mode)
    {
        if (!NativeMemoriesRecordingControls.CanChoose(state.Mode, mode, state.CanSwitch)) return;
        pendingMode = mode; Navigate(Page.ConfirmMode);
    }

    private void BuildConfirmation(Transform parent)
    {
        Label(parent, NativeMemoriesRecordingControls.Confirmation(pendingMode), 240, 26);
        Row(parent, 60, (pendingMode == ReplayRecordingMode.Continuous ? "确认开启" : "确认关闭",
            () => { chooseMode(pendingMode); Navigate(Page.Modes); }, NativeMemoriesRecordingControls.CanChoose(state.Mode, pendingMode, state.CanSwitch)),
            ("保持当前设置", () => Navigate(Page.Modes), true));
    }

    private static Transform Column(Transform parent, float height)
    {
        var root = new GameObject("Record list", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        root.transform.SetParent(parent, false);
        var group = root.GetComponent<VerticalLayoutGroup>();
        group.spacing = 8; group.childControlWidth = group.childControlHeight = true;
        group.childForceExpandWidth = true; group.childForceExpandHeight = false;
        Size(root, height);
        return root.transform;
    }

    private TMP_Text Label(Transform parent, string text, float height, float size)
    {
        var go = Object.Instantiate(textPrefab!, parent, false);
        go.name = "Memories text"; Size(go, height);
        var label = go.GetComponentInChildren<TMP_Text>(true);
        label.text = text; label.richText = false; label.raycastTarget = false;
        label.alignment = TextAlignmentOptions.Center;
        label.textWrappingMode = TextWrappingModes.Normal;
        label.enableAutoSizing = true; label.fontSizeMin = 18; label.fontSizeMax = size;
        return label;
    }

    private void Row(Transform parent, float height, params (string Text, Action Click, bool Enabled)[] options)
    {
        var root = new GameObject("Memories actions", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        root.transform.SetParent(parent, false); Size(root, height);
        var group = root.GetComponent<HorizontalLayoutGroup>();
        group.spacing = 16; group.childControlWidth = group.childControlHeight = true;
        group.childForceExpandWidth = group.childForceExpandHeight = true;
        var buttons = new Button[options.Length];
        for (int i = 0; i < options.Length; i++)
        {
            var option = options[i];
            // Verified native prefab: Image + Button + TMP label; no gameplay scripts.
            var go = Object.Instantiate(buttonPrefab!, root.transform, false);
            go.name = "Memories action"; go.SetActive(true);
            Size(go, height).flexibleWidth = 1;
            var button = go.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(() => option.Click());
            button.interactable = option.Enabled;
            var text = go.GetComponentInChildren<TMP_Text>(true);
            text.text = option.Text; text.richText = false; text.raycastTarget = false;
            text.enableAutoSizing = true; text.fontSizeMin = 18; text.fontSizeMax = height <= 60 ? 26 : 30;
            text.textWrappingMode = TextWrappingModes.Normal;
            buttons[i] = button;
            if (!firstButton && button.interactable) firstButton = button;
        }
        navigationRows.Add(buttons);
    }

    private static LayoutElement Size(GameObject go, float height)
    {
        var layout = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        layout.minHeight = layout.preferredHeight = height; layout.flexibleHeight = 0;
        return layout;
    }

    private static void Place(GameObject root, Transform parent, Vector2 center, Vector2 size)
    {
        // The shared modal's parents keep their original layout for every other game dialog.
        // Position only our ignored-layout roots in its native 1920 x 1080 canvas space.
        var rect = root.GetComponent<RectTransform>();
        rect.SetParent(parent, false); rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
        rect.sizeDelta = size;
        root.GetComponent<LayoutElement>().ignoreLayout = true;
        var canvas = (RectTransform)parent.GetComponentInParent<Canvas>().rootCanvas.transform;
        rect.position = canvas.TransformPoint((Vector3)(canvas.rect.center + center));
    }

    private void ConnectNavigation()
    {
        for (int row = 0; row < navigationRows.Count; row++)
            for (int col = 0; col < navigationRows[row].Length; col++)
            {
                var button = navigationRows[row][col];
                button.navigation = new Navigation
                {
                    mode = Navigation.Mode.Explicit,
                    selectOnLeft = Neighbour(row, col, 0, -1), selectOnRight = Neighbour(row, col, 0, 1),
                    selectOnUp = Neighbour(row, col, -1, 0), selectOnDown = Neighbour(row, col, 1, 0),
                };
            }
    }

    private Button? Neighbour(int row, int col, int dr, int dc)
    {
        int r = row + dr, c = col + dc;
        while (r >= 0 && r < navigationRows.Count)
        {
            var buttons = navigationRows[r];
            int index = dr != 0 ? Math.Min(col, buttons.Length - 1) : c;
            if (index < 0 || index >= buttons.Length) return null;
            if (buttons[index].interactable) return buttons[index];
            if (dr != 0)
                foreach (var candidate in buttons) if (candidate.interactable) return candidate;
            r += dr; c += dc;
        }
        return null;
    }

    private void OnClosed()
    {
        if (silentClose || disposed) return;
        if (page == Page.Loading) cancelLoading();
        close(); RestoreSelection();
    }

    public void Hide()
    {
        if (OwnsModal && modal!.Visible)
        {
            silentClose = true;
            try { Modal.CloseModal(); }
            finally { silentClose = false; }
            RestoreSelection();
        }
        // Keep closed content alive for the game's unscaled fade-out. The next
        // native Open clears it; destroying it here would make labels pop off.
        dirty = true;
    }

    private void RestoreSelection()
    {
        if (EventSystem.current && previousSelection && previousSelection!.activeInHierarchy)
            EventSystem.current.SetSelectedGameObject(previousSelection);
        else if (EventSystem.current)
        {
            var main = Object.FindFirstObjectByType<MainMenuMainPage>();
            if (main && main.PlayButton) EventSystem.current.SetSelectedGameObject(main.PlayButton.gameObject);
        }
        previousSelection = null;
    }

    public void Dispose()
    {
        Hide(); disposed = true;
        if (contentRoot) Object.Destroy(contentRoot);
        if (headerRoot) Object.Destroy(headerRoot);
        contentRoot = headerRoot = previousSelection = null;
        covers.Dispose();
    }
}
