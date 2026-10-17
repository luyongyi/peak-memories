using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Zorro.UI.Modal;
using Object = UnityEngine.Object;

namespace PeakReplayLab.Cinema;

// Presentation only: camera, playback and project changes go through the host.
// This canvas borrows the replay's EventSystem and never changes its lifetime.
internal sealed partial class CinemaEditorUI : IDisposable
{
    private sealed class ValueControl
    {
        public Slider Slider = null!;
        public TMP_Text Value = null!;
        public Func<CinemaEditorState, float> Read = null!;
        public Func<float, string> Format = null!;
        public Func<float, float> ToSlider = value => value;
    }

    private sealed class Choice
    {
        public Button Button = null!;
        public Func<CinemaEditorState, bool> Selected = null!;
    }

    private readonly ICinemaEditorHost host;
    private readonly List<ValueControl> values = new();
    private readonly List<Choice> choices = new();
    private readonly List<RectTransform> pages = new();
    private readonly List<Button> tabs = new();
    private readonly List<(RectTransform Left, RectTransform Right)> columns = new();
    private GameObject? root;
    private GameObject buttonPrefab = null!;
    private TMP_Text textTemplate = null!;
    private Sprite fillSprite = null!;
    private ColorBlock buttonColors;
    private RectTransform canvasRect = null!, panel = null!, grid = null!, frame = null!;
    private CanvasGroup group = null!;
    private TMP_Text title = null!, clock = null!, status = null!, warning = null!, playLabel = null!, collapseLabel = null!, keyLabel = null!;
    private Slider timeline = null!;
    private ValueControl amount = null!;
    private TMP_InputField projectName = null!;
    private Button overwrite = null!, delete = null!, previous = null!, next = null!, preview = null!, save = null!, load = null!;
    private bool expanded = true, disposed, refreshing;
    private int selectedTab;
    private float layoutWidth = -1, layoutHeight = -1;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly Color Cream = new(.8742f, .8567f, .7615f, 1);
    private static readonly Color Brown = new(.20f, .17f, .14f, .97f);

    public bool EditingText => !disposed && inputs.Exists(input => input && input.isFocused);

    public CinemaEditorUI(ICinemaEditorHost host)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        try { Build(); }
        catch { Dispose(); throw; }
    }

    private void Build()
    {
        var buttons = Resources.Load<GameObject>("ButtonsInputField");
        buttonPrefab = buttons ? buttons.GetComponent<ModalButtonsField>()?.m_buttonPrefab! : null!;
        var subheading = Resources.Load<GameObject>("SubheaderModalField");
        textTemplate = subheading ? subheading.GetComponentInChildren<TMP_Text>(true) : null!;
        if (!buttonPrefab || !textTemplate || !textTemplate.font)
            throw new InvalidOperationException("未找到游戏原生电影编辑按钮或字体。");
        foreach (var component in buttonPrefab.GetComponentsInChildren<Component>(true))
            if (component && !VerifiedButtonComponent(component.GetType()))
                throw new InvalidOperationException("原生按钮包含未验证行为，已停止创建电影编辑器。");
        var sourceButton = buttonPrefab.GetComponent<Button>();
        var sourceImage = buttonPrefab.GetComponent<Image>();
        if (!sourceButton || !sourceImage || !sourceImage.sprite)
            throw new InvalidOperationException("游戏原生电影编辑按钮资源不完整。");
        fillSprite = sourceImage.sprite; buttonColors = sourceButton.colors;

        root = new GameObject("PEAK Memories - cinema editor", typeof(RectTransform));
        root.SetActive(false); canvasRect = root.GetComponent<RectTransform>();
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 1020;
        canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 |
            AdditionalCanvasShaderChannels.TexCoord2 | AdditionalCanvasShaderChannels.TexCoord3;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
        root.AddComponent<GraphicRaycaster>();

        frame = Rect("Framing reference only", root.transform); Center(frame);
        grid = Rect("Rule of thirds reference", root.transform); Center(grid);
        Border(frame); Grid(grid);
        panel = Rect("Cinema editing drawer", root.transform);
        panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(.5f, 0);
        panel.anchoredPosition = new Vector2(0, 12);
        group = panel.gameObject.AddComponent<CanvasGroup>();
        Image(panel, Brown).raycastTarget = true;
        title = Text("Cinema project heading", panel, 27);
        clock = Text("Cinema playback clock", panel, 27, TextAlignmentOptions.Right);
        timeline = Slider(panel, "Recording time", 0, 1, value => Send(CinemaEditorCommandKind.Seek, value));

        var playback = Row(panel, "Recording transport", 88);
        var play = Button(playback, "播放 [空格]", () => Send(CinemaEditorCommandKind.TogglePlayback), 145);
        playLabel = play.GetComponentInChildren<TMP_Text>(true);
        foreach (float speed in new[] { .25f, .5f, 1f, 2f })
        {
            float speedValue = speed;
            var button = Button(playback, speed.ToString("0.##", Invariant) + "x",
                () => Send(CinemaEditorCommandKind.SetPlaybackSpeed, speedValue), 78);
            choices.Add(new Choice { Button = button, Selected = state => Mathf.Abs(state.Speed - speedValue) < .001f });
        }
        Button(playback, "纯画面 [H]", () => Send(CinemaEditorCommandKind.ToggleCleanPreview), 145);
        undo = Button(playback, "撤销", () => Send(CinemaEditorCommandKind.Undo), 90);
        redo = Button(playback, "重做", () => Send(CinemaEditorCommandKind.Redo), 90);
        Flex(playback);
        var collapse = Button(playback, "收起编辑器", () => { expanded = !expanded; layoutWidth = -1; }, 165);
        collapseLabel = collapse.GetComponentInChildren<TMP_Text>(true);
        Button(playback, "返回回放 [F8]", () => Send(CinemaEditorCommandKind.Close), 165);
        status = Text("Cinema status", panel, 20);
        warning = Text("Cinema warning", panel, 19); warning.color = new Color(1, .77f, .41f);

        var tabRow = Row(panel, "Cinema editing tabs", 195);
        string[] labels = { "运镜", "镜头", "景深", "调色", "关键帧", "方案" };
        for (int i = 0; i < labels.Length; i++)
        {
            int tab = i;
            tabs.Add(Button(tabRow, labels[i], () => { selectedTab = tab; RefreshTabs(); }, 110));
            var page = Rect("Cinema page " + labels[i], panel); pages.Add(page);
            var left = Rect("Primary settings", page); var right = Rect("Secondary settings", page);
            columns.Add((left, right));
        }
        var hint = Label(tabRow, "右键 + WASD / QE 飞行 · 滚轮调速 · H 纯画面", 0, 19);
        hint.alignment = TextAlignmentOptions.Right; Element(hint, 0, 1);
        BuildMotion(columns[0].Left, columns[0].Right);
        BuildLens(columns[1].Left, columns[1].Right);
        BuildDepth(columns[2].Left, columns[2].Right);
        BuildColor(columns[3].Left, columns[3].Right);
        BuildKeyEditing(columns[4].Left, columns[4].Right);
        BuildProjectEditing(columns[5].Left, columns[5].Right);
        BuildKeyMarkers();
        BuildConfirmation();
        root.SetActive(true); RefreshTabs(); Layout();
    }

    private void BuildMotion(RectTransform left, RectTransform right)
    {
        var presets = Row(left, "Motion presets", 0); Label(presets, "运镜预设", 120);
        EnumChoices(presets, CinemaEditorCommandKind.SetPreset,
            new[] { "推近 / 拉远", "左右横移", "人物环绕", "升空 / 下降" }, state => (int)state.Preset, 145);
        amount = Value(left, "幅度", 52, -100, 100, CinemaEditorCommandKind.SetPresetAmount,
            state => state.Amount, value => Signed(value, " m"));
        Value(left, "时长", 104, .1f, 120, CinemaEditorCommandKind.SetPresetDuration,
            state => state.ShotDuration, value => value.ToString("0.0", Invariant) + " s");
        var transition = Row(left, "Keyframe transition", 156); Label(transition, "关键帧过渡", 120);
        EnumChoices(transition, CinemaEditorCommandKind.SetTransition,
            new[] { "平滑", "直线", "硬切" }, state => (int)state.Transition, 145);
        var generate = Row(left, "Generate motion", 208);
        Button(generate, "从当前机位生成运镜", () => Send(CinemaEditorCommandKind.GeneratePreset), 270);
        Label(generate, "正值：前进 / 右移 / 环绕 / 升高", 0, 18);

        var keys = Row(right, "Keyframe editing", 0);
        Button(keys, "添加关键帧", () => Send(CinemaEditorCommandKind.AddKey), 170);
        overwrite = Button(keys, "覆盖当前帧", () => Send(CinemaEditorCommandKind.OverwriteKey), 170);
        delete = Button(keys, "删除当前帧", () => Send(CinemaEditorCommandKind.DeleteKey), 170);
        var browse = Row(right, "Keyframe navigation", 52);
        previous = Button(browse, "上一关键帧", () => Send(CinemaEditorCommandKind.PreviousKey), 170);
        next = Button(browse, "下一关键帧", () => Send(CinemaEditorCommandKind.NextKey), 170);
        keyLabel = Label(browse, "关键帧 0 / 0", 0, 21);
        var transport = Row(right, "Motion preview", 104);
        preview = Button(transport, "预览运镜", () => Send(CinemaEditorCommandKind.Preview), 170);
        Button(transport, "停止 / 手动接管", () => Send(CinemaEditorCommandKind.Stop), 210);
        Label(transport, "飞行或转向会接管镜头", 0, 18);
        var name = Row(right, "Project name", 156); Label(name, "方案名称", 120);
        projectName = Input(name);
        var storage = Row(right, "Project storage", 208);
        save = Button(storage, "保存方案", () => ProjectAction(CinemaEditorCommandKind.Save), 170);
        load = Button(storage, "加载方案", () => ProjectAction(CinemaEditorCommandKind.Load), 170);
        Label(storage, "镜头方案独立保存，不改动录像", 0, 18);
    }

    private void BuildLens(RectTransform left, RectTransform right)
    {
        Value(left, "视野角度", 0, 10, 140, CinemaEditorCommandKind.SetFov,
            state => state.Fov, value => value.ToString("0", Invariant) + "°");
        var fov = Row(left, "Lens presets", 52); Label(fov, "变焦预设", 120);
        Button(fov, "广角 90°", () => Send(CinemaEditorCommandKind.SetFov, 90), 150);
        Button(fov, "标准 60°", () => Send(CinemaEditorCommandKind.SetFov, 60), 150);
        Button(fov, "长焦 30°", () => Send(CinemaEditorCommandKind.SetFov, 30), 150);
        var aim = Row(left, "Camera orientation", 104);
        Button(aim, "恢复水平", () => Send(CinemaEditorCommandKind.ResetHorizon), 190);
        Button(aim, "对准当前玩家", () => Send(CinemaEditorCommandKind.FrameActor), 220);
        var aspect = Row(left, "Framing aspect", 156); Label(aspect, "画幅参考", 120);
        EnumChoices(aspect, CinemaEditorCommandKind.SetAspect,
            new[] { "原始比例", "16:9", "21:9" }, state => (int)state.Aspect, 150);
        Label(Row(left, "Framing help", 208), "参考框不改变相机画幅；视野和机位可写入关键帧。", 0, 19);

        var overlays = Row(right, "Overlay visibility", 0);
        Toggle(overlays, "构图网格", CinemaEditorCommandKind.ToggleGrid, state => state.Grid, 190);
        Toggle(overlays, "隐藏 HUD", CinemaEditorCommandKind.ToggleHud, state => state.HideHud, 190);
        Toggle(overlays, "隐藏姓名", CinemaEditorCommandKind.ToggleNames, state => state.HideNames, 190);
        var clean = Row(right, "Clean preview", 52);
        Toggle(clean, "纯画面预览 [H]", CinemaEditorCommandKind.ToggleCleanPreview, state => state.CleanPreview, 270);
        Label(Row(right, "Clean preview help", 104), "H 恢复编辑器；纯画面隐藏所有构图参考线。", 0, 19);
        Label(Row(right, "Camera help", 156), "右键转向 · WASD 平移 · Q / E 升降", 0, 20);
        Label(Row(right, "Speed help", 208), "滚轮调飞行速度 · Shift 加速 · Ctrl 微调", 0, 20);
    }

    private void BuildDepth(RectTransform left, RectTransform right)
    {
        var focus = Row(left, "Focus mode", 0); Label(focus, "对焦模式", 120);
        EnumChoices(focus, CinemaEditorCommandKind.SetFocusMode,
            new[] { "关闭景深", "手动距离", "锁定人物" }, state => (int)state.FocusMode, 170);
        Value(left, "对焦距离", 52, .05f, 100000, CinemaEditorCommandKind.SetFocusDistance,
            state => state.FocusDistance, value => value.ToString("0.##", Invariant) + " m", logarithmic: true);
        Value(left, "虚化强度", 104, 0, 1, CinemaEditorCommandKind.SetBlur,
            state => state.BlurStrength, value => value.ToString("P0", Invariant));
        var quality = Row(left, "Depth quality", 156); Label(quality, "质量档位", 120);
        EnumChoices(quality, CinemaEditorCommandKind.SetDofQuality,
            new[] { "低", "中", "高" }, state => (int)state.DofQuality, 170);
        Label(Row(left, "Depth help", 208), "先用轻度虚化取景，再按需要增加强度。", 0, 19);
        Label(Row(right, "Manual focus help", 0), "手动距离：控制离镜头多远的景物保持清晰。", 0, 20);
        Label(Row(right, "Actor focus help", 52), "点「锁定人物」绑定玩家；Tab 后可再次点选更换。", 0, 20);
        Label(Row(right, "Actor focus fallback help", 104), "人物不可见时暂时关闭景深，恢复可见后继续对焦。", 0, 19);
        Label(Row(right, "Quality help", 156), "较高质量需要更多渲染时间，可随时关闭景深。", 0, 19);
        Label(Row(right, "Depth timeline help", 208), "对焦距离随关键帧变化；景深模式保存在方案中。", 0, 19);
    }

    private void BuildColor(RectTransform left, RectTransform right)
    {
        var preset = Row(left, "Color presets", 0); Label(preset, "调色预设", 120);
        EnumChoices(preset, CinemaEditorCommandKind.SetColorPreset,
            new[] { "原片", "暖阳", "冷峻", "黑白" }, state => (int)state.ColorPreset, 125);
        Value(left, "调色强度", 52, 0, 1, CinemaEditorCommandKind.SetColorStrength,
            state => state.ColorStrength, value => value.ToString("P0", Invariant));
        Value(left, "曝光", 104, -5, 5, CinemaEditorCommandKind.SetExposure,
            state => state.Exposure, value => Signed(value, " EV"));
        Label(Row(left, "Color help", 156), "调色独立于游戏天气、环境光和雾。", 0, 20);
        Label(Row(left, "Color saving help", 208), "黑白强度 100% 完全去色；强度 0% 恢复原片。", 0, 19);
        Value(right, "对比度", 0, -50, 50, CinemaEditorCommandKind.SetContrast,
            state => state.Contrast, value => Signed(value, "%"));
        Value(right, "饱和度", 52, -100, 100, CinemaEditorCommandKind.SetSaturation,
            state => state.Saturation, value => Signed(value, "%"));
        Value(right, "色温", 104, -100, 100, CinemaEditorCommandKind.SetTemperature,
            state => state.Temperature, value => Signed(value, "%"));
        var reset = Row(right, "Reset color grading", 156);
        Button(reset, "一键还原调色", () => Send(CinemaEditorCommandKind.ResetGrade), 250);
        Label(Row(right, "Color restoration help", 208), "退出电影模式后恢复游戏原来的画面设置。", 0, 19);
    }

    public void Refresh(CinemaEditorState state, bool looking)
    {
        if (disposed || !root) return;
        bool committedText = false;
        // End text editing before giving the mouse to the camera. Commit through
        // the normal end-edit command while callbacks are still permitted.
        if (looking || state.CleanPreview || state.HasPendingAction)
        {
            foreach (var input in inputs)
                if (input && input.isFocused) { input.DeactivateInputField(); committedText = true; }
            var eventSystem = EventSystem.current;
            var selected = eventSystem ? eventSystem.currentSelectedGameObject : null;
            if (selected && selected.transform.IsChildOf(panel)) eventSystem!.SetSelectedGameObject(null);
        }
        // Changing slider bounds can clamp its old value and invoke Unity's
        // callback even when the following value assignment is notification-free.
        refreshing = true;
        try
        {
        Layout();
        group.alpha = state.CleanPreview ? 0 : state.HasPendingAction ? .45f : 1;
        group.interactable = !state.CleanPreview && !state.HasPendingAction && !looking && Application.isFocused;
        group.blocksRaycasts = group.interactable;
        SetText(title, "电影模式 · " + state.ActiveProjectName + (state.Dirty ? "  * 未保存" : ""));
        SetText(clock, FormatTime(state.DisplayTime) + " / " + FormatTime(state.Duration));
        SetText(playLabel, state.Paused ? "播放 [空格]" : "暂停 [空格]");
        SetText(collapseLabel, expanded ? "收起编辑器" : "展开编辑器");
        timeline.maxValue = Mathf.Max(.001f, (float)state.Duration);
        timeline.SetValueWithoutNotify((float)state.DisplayTime);
        string key = state.KeyIndex >= 0 ? (state.KeyIndex + 1).ToString(Invariant) : "—";
        SetText(keyLabel, "关键帧 " + key + " / " + state.KeyCount.ToString(Invariant));
        SetText(status, (state.Buffering ? "读取录像中 · " : state.Previewing ? "自动运镜 · " : "手动机位 · ") +
            "关键帧 " + key + "/" + state.KeyCount.ToString(Invariant) + " · " + state.Status);
        SetText(warning, string.IsNullOrEmpty(localError) ? state.Warning : localError);
        if (!committedText && !projectName.isFocused && projectName.text != state.ProjectName)
            projectName.SetTextWithoutNotify(state.ProjectName);

        bool orbit = state.Preset == CinemaPreset.Orbit;
        float bound = orbit ? 360 : 100;
        amount.Slider.minValue = -bound; amount.Slider.maxValue = bound;
        amount.Format = orbit ? value => Signed(value, "°") : value => Signed(value, " m");
        foreach (var control in values)
        {
            float value = control.Read(state);
            control.Slider.SetValueWithoutNotify(control.ToSlider(value));
            SetText(control.Value, control.Format(value));
        }
        foreach (var choice in choices) Selected(choice.Button, choice.Selected(state));
        bool hasKey = state.KeyIndex >= 0 && state.KeyIndex < state.KeyCount;
        overwrite.interactable = delete.interactable = hasKey;
        previous.interactable = next.interactable = state.KeyCount > 0;
        preview.interactable = state.KeyCount > 1;
        save.interactable = load.interactable = state.IdentityReady;
        RefreshEditing(state, committedText);
        frame.gameObject.SetActive(state.Aspect != CinemaFrameAspect.Native && !state.CleanPreview);
        grid.gameObject.SetActive(state.Grid && !state.CleanPreview);
        LayoutReferences(state.Aspect);
        }
        finally { refreshing = false; }
    }

    private void RefreshTabs()
    {
        for (int i = 0; i < pages.Count; i++)
        {
            pages[i].gameObject.SetActive(expanded && selectedTab == i);
            Selected(tabs[i], selectedTab == i);
        }
    }

    private void Layout()
    {
        float width = Mathf.Min(1792, Mathf.Max(760, canvasRect.rect.width - 48));
        float height = expanded ? 520 : 198;
        if (Mathf.Abs(layoutWidth - width) < .1f && Mathf.Abs(layoutHeight - height) < .1f) return;
        layoutWidth = width; layoutHeight = height;
        panel.sizeDelta = new Vector2(width, height);
        Place(title.rectTransform, 22, 12, width - 455, 30);
        Place(clock.rectTransform, width - 405, 12, 383, 30);
        Place((RectTransform)timeline.transform, 22, 44, width - 44, 26);
        if (markerTrack) Place(markerTrack, 22, 70, width - 44, 16);
        Place(status.rectTransform, 22, 132, width - 44, 30);
        Place(warning.rectTransform, 22, 163, width - 44, 28);
        foreach (var page in pages) Place(page, 22, 248, width - 44, 260);
        foreach (var pair in columns)
        {
            float inner = (width - 68) / 2;
            Place(pair.Left, 0, 0, inner, 260);
            Place(pair.Right, inner + 24, 0, inner, 260);
        }
        var tabRow = panel.Find("Cinema editing tabs");
        if (tabRow) tabRow.gameObject.SetActive(expanded);
        RefreshTabs();
    }

    private void LayoutReferences(CinemaFrameAspect aspect)
    {
        float width = canvasRect.rect.width, height = canvasRect.rect.height;
        float ratio = aspect == CinemaFrameAspect.Cinema21x9 ? 21f / 9 :
            aspect == CinemaFrameAspect.Widescreen16x9 ? 16f / 9 : width / Mathf.Max(1, height);
        if (width / Mathf.Max(1, height) > ratio) width = height * ratio;
        else height = width / ratio;
        frame.sizeDelta = grid.sizeDelta = new Vector2(width, height);
    }

    private ValueControl Value(Transform parent, string label, float y, float min, float max,
        CinemaEditorCommandKind command, Func<CinemaEditorState, float> read, Func<float, string> format, bool logarithmic = false)
    {
        var row = Row(parent, label + " control", y); Label(row, label, 120);
        var slider = Slider(row, label + " slider", logarithmic ? Mathf.Log10(min) : min,
            logarithmic ? Mathf.Log10(max) : max, value => Send(command, logarithmic ? Mathf.Pow(10, value) : value));
        Element(slider, 210, 1);
        var text = Label(row, "", 115, 20); text.alignment = TextAlignmentOptions.Right;
        var control = new ValueControl { Slider = slider, Value = text, Read = read, Format = format };
        if (logarithmic) control.ToSlider = value => Mathf.Log10(Mathf.Max(min, value));
        values.Add(control); return control;
    }

    private void EnumChoices(Transform parent, CinemaEditorCommandKind kind, string[] labels,
        Func<CinemaEditorState, int> read, float width)
    {
        for (int i = 0; i < labels.Length; i++)
        {
            int value = i;
            var button = Button(parent, labels[i], () => Send(kind, value), width);
            choices.Add(new Choice { Button = button, Selected = state => read(state) == value });
        }
    }

    private void Toggle(Transform parent, string label, CinemaEditorCommandKind kind, Func<CinemaEditorState, bool> read, float width)
    {
        var button = Button(parent, label, () => Send(kind), width);
        choices.Add(new Choice { Button = button, Selected = read });
    }

    private Button Button(Transform parent, string label, Action action, float width)
    {
        var go = Object.Instantiate(buttonPrefab, parent, false); go.name = "Cinema action " + label; go.SetActive(true);
        var button = go.GetComponent<Button>(); button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(() => { if (!disposed) action(); });
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        var text = button.GetComponentInChildren<TMP_Text>(true);
        text.text = label; text.richText = false; text.raycastTarget = false; text.enableAutoSizing = true;
        text.fontSizeMin = 16; text.fontSizeMax = 24; text.textWrappingMode = TextWrappingModes.NoWrap;
        Element(button, width); return button;
    }

    private TMP_InputField Input(Transform parent, string name = "project name", string prompt = "输入方案名称",
        float width = 300, Action<string>? commit = null, bool project = true)
    {
        var rect = Rect("Cinema " + name + " input", parent); Element(rect, width, 1);
        var image = Image(rect, new Color(.31f, .27f, .22f)); image.raycastTarget = true;
        var viewport = Rect("Project name text viewport", rect); Stretch(viewport);
        viewport.offsetMin = new Vector2(12, 4); viewport.offsetMax = new Vector2(-12, -4);
        viewport.gameObject.AddComponent<RectMask2D>();
        var text = Text("Project name text", viewport, 23); Stretch(text.rectTransform);
        var placeholder = Text("Project name placeholder", viewport, 22); Stretch(placeholder.rectTransform);
        placeholder.text = prompt; placeholder.color = new Color(Cream.r, Cream.g, Cream.b, .5f);
        var input = rect.gameObject.AddComponent<TMP_InputField>();
        input.textViewport = viewport; input.textComponent = text; input.placeholder = placeholder;
        input.targetGraphic = image; input.characterLimit = 40; input.lineType = TMP_InputField.LineType.SingleLine;
        input.customCaretColor = true; input.caretColor = Cream;
        input.selectionColor = new Color(.55f, .48f, .36f, .7f);
        input.navigation = new Navigation { mode = Navigation.Mode.None };
        input.onValueChanged.AddListener(_ => { if (!refreshing) localError = ""; });
        if (project) input.onEndEdit.AddListener(value => Send(CinemaEditorCommandKind.SetProjectName, text: value));
        else if (commit != null) input.onEndEdit.AddListener(value => { if (!refreshing) commit(value); });
        inputs.Add(input);
        return input;
    }

    private Slider Slider(Transform parent, string name, float min, float max, Action<float> changed)
    {
        var rect = Rect(name, parent); var slider = rect.gameObject.AddComponent<Slider>();
        slider.minValue = min; slider.maxValue = max; slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
        var hit = Image(rect, Color.clear); hit.raycastTarget = true;
        var track = Rect("Slider track", rect); Stretch(track);
        track.offsetMin = new Vector2(0, 12); track.offsetMax = new Vector2(0, -12);
        Image(track, new Color(.46f, .41f, .34f));
        var fillArea = Rect("Slider fill area", rect); Stretch(fillArea);
        fillArea.offsetMin = new Vector2(10, 12); fillArea.offsetMax = new Vector2(-10, -12);
        var fill = Rect("Slider fill", fillArea); Stretch(fill); Image(fill, Cream);
        var handleArea = Rect("Slider handle area", rect); Stretch(handleArea);
        handleArea.offsetMin = new Vector2(10, 0); handleArea.offsetMax = new Vector2(-10, 0);
        var handle = Rect("Slider handle", handleArea); handle.sizeDelta = new Vector2(20, 34);
        var graphic = Image(handle, buttonColors.pressedColor); graphic.raycastTarget = true;
        slider.fillRect = fill; slider.handleRect = handle; slider.targetGraphic = graphic; slider.colors = buttonColors;
        slider.navigation = new Navigation { mode = Navigation.Mode.None };
        slider.onValueChanged.AddListener(value => { if (!disposed) changed(value); });
        return slider;
    }

    private RectTransform Row(Transform parent, string name, float y)
    {
        var row = Rect(name, parent);
        row.anchorMin = new Vector2(0, 1); row.anchorMax = new Vector2(1, 1); row.pivot = new Vector2(.5f, 1);
        row.offsetMin = new Vector2(parent == panel ? 22 : 0, -y - 40);
        row.offsetMax = new Vector2(parent == panel ? -22 : 0, -y);
        var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 10; layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = false; layout.childForceExpandHeight = true;
        return row;
    }

    private TMP_Text Label(Transform parent, string label, float width, float size = 21)
    {
        var text = Text(label, parent, size); text.text = label; Element(text, width, width == 0 ? 1 : 0); return text;
    }

    private TMP_Text Text(string name, Transform parent, float size, TextAlignmentOptions align = TextAlignmentOptions.Left)
    {
        var rect = Rect(name, parent); var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        text.font = textTemplate.font; text.fontSharedMaterial = textTemplate.fontSharedMaterial;
        text.spriteAsset = textTemplate.spriteAsset; text.color = Cream; text.richText = false; text.raycastTarget = false;
        text.alignment = align; text.fontSize = size; text.enableAutoSizing = true;
        text.fontSizeMin = Mathf.Min(17, size); text.fontSizeMax = size;
        text.textWrappingMode = TextWrappingModes.NoWrap; text.overflowMode = TextOverflowModes.Ellipsis;
        return text;
    }

    private void Selected(Button button, bool selected)
    {
        var colors = buttonColors; if (selected) colors.normalColor = buttonColors.selectedColor;
        if (button.colors != colors) button.colors = colors;
    }

    private static void Element(Component component, float width, float flexible = 0)
    {
        var layout = component.GetComponent<LayoutElement>() ?? component.gameObject.AddComponent<LayoutElement>();
        layout.minWidth = width == 0 ? 0 : Mathf.Min(55, width);
        layout.preferredWidth = width; layout.flexibleWidth = flexible; layout.preferredHeight = 40;
    }

    private static void Flex(Transform parent) { Element(Rect("Flexible space", parent), 0, 1); }
    private void Send(CinemaEditorCommandKind kind, double number = 0, string text = "")
    { if (!disposed && !refreshing) host.Execute(new CinemaEditorCommand(kind, number, text)); }
    private void ProjectAction(CinemaEditorCommandKind action)
    {
        // Pointer selection normally commits onEndEdit before onClick. Also
        // submit explicitly so Save/Load always use the visible name.
        Send(CinemaEditorCommandKind.SetProjectName, text: projectName.text);
        Send(action);
    }
    private static void SetText(TMP_Text text, string value) { if (text.text != value) text.text = value; }
    private static string Signed(float value, string unit) => value.ToString("+0.##;-0.##;0", Invariant) + unit;
    private static string FormatTime(double time)
    {
        long seconds = (long)Math.Max(0, time);
        return seconds >= 3600 ? (seconds / 3600).ToString(Invariant) + ":" + (seconds / 60 % 60).ToString("00", Invariant) + ":" +
            (seconds % 60).ToString("00", Invariant) : (seconds / 60).ToString("00", Invariant) + ":" + (seconds % 60).ToString("00", Invariant);
    }

    private void Border(RectTransform parent)
    {
        Line(parent, "Top frame edge", new Vector2(0, 1), new Vector2(1, 1), true);
        Line(parent, "Bottom frame edge", Vector2.zero, new Vector2(1, 0), true);
        Line(parent, "Left frame edge", Vector2.zero, new Vector2(0, 1), false);
        Line(parent, "Right frame edge", new Vector2(1, 0), Vector2.one, false);
    }

    private void Grid(RectTransform parent)
    {
        for (int i = 1; i <= 2; i++)
        {
            float third = i / 3f;
            Line(parent, "Horizontal third " + i, new Vector2(0, third), new Vector2(1, third), true);
            Line(parent, "Vertical third " + i, new Vector2(third, 0), new Vector2(third, 1), false);
        }
    }

    private void Line(RectTransform parent, string name, Vector2 min, Vector2 max, bool horizontal)
    {
        var line = Rect(name, parent); line.anchorMin = min; line.anchorMax = max;
        line.anchoredPosition = Vector2.zero; line.sizeDelta = horizontal ? new Vector2(0, 2) : new Vector2(2, 0);
        var image = line.gameObject.AddComponent<Image>(); image.color = new Color(Cream.r, Cream.g, Cream.b, .45f);
        image.raycastTarget = false;
    }

    private static bool VerifiedButtonComponent(Type type) =>
        type == typeof(Transform) || type == typeof(RectTransform) || type == typeof(CanvasRenderer) ||
        type == typeof(Image) || type == typeof(RawImage) || type == typeof(TextMeshProUGUI) || type == typeof(UnityEngine.UI.Text) ||
        type == typeof(Button) || type == typeof(LayoutElement) || type == typeof(HorizontalLayoutGroup) ||
        type == typeof(VerticalLayoutGroup) || type == typeof(GridLayoutGroup) || type == typeof(ContentSizeFitter);
    private static RectTransform Rect(string name, Transform parent)
    { var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false); return go.GetComponent<RectTransform>(); }
    private Image Image(RectTransform rect, Color color)
    { var image = rect.gameObject.AddComponent<Image>(); image.sprite = fillSprite; image.type = UnityEngine.UI.Image.Type.Sliced; image.color = color; image.raycastTarget = false; return image; }
    private static void Stretch(RectTransform rect)
    { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero; }
    private static void Center(RectTransform rect)
    { rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f); rect.anchoredPosition = Vector2.zero; }
    private static void Place(RectTransform rect, float x, float y, float width, float height)
    { rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1); rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height); }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (EventSystem.current && root)
        {
            var selected = EventSystem.current.currentSelectedGameObject;
            if (selected && selected.transform.IsChildOf(root!.transform)) EventSystem.current.SetSelectedGameObject(null);
        }
        if (root) { root!.SetActive(false); Object.Destroy(root); root = null; }
        values.Clear(); choices.Clear(); tabs.Clear(); pages.Clear(); columns.Clear();
        inputs.Clear(); markers.Clear();
    }
}
