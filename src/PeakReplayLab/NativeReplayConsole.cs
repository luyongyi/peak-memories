using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using UnityEngine.UI.ProceduralImage;
using Zorro.UI.Modal;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

// A playback-only canvas. Borrow the game's button, sprites, type and language
// fallbacks; every action targets the recording, never the hidden live scout.
internal sealed class NativeReplayConsole : IDisposable
{
    private sealed class Cell
    {
        public GameObject Root = null!;
        public Image Frame = null!;
        public RawImage Icon = null!;
        public TMP_Text Slot = null!, Name = null!;
    }

    private readonly PlaybackSession session;
    private readonly Action close, toggleDiagnostics;
    private readonly Action<Exception> error;
    private readonly List<Cell> cells = new();
    private readonly List<InventoryFrame> ordered = new();
    private readonly Dictionary<int, (Texture? Icon, string Name)> catalogue = new();
    private readonly List<Selectable> navigation = new();
    private readonly List<(EventSystem System, bool Enabled)> previousSystems = new();
    private readonly List<InputActionReference> inputReferences = new();
    private readonly StringBuilder events = new();
    private readonly List<(Button Button, float Speed)> speeds = new();
    private readonly ReplayDrawerMotion drawer = new();
    private GameObject? root;
    private RectTransform canvasRect = null!, panel = null!, controls = null!, details = null!, viewport = null!, content = null!;
    private CanvasGroup group = null!, panelGroup = null!;
    private NativeReplayDrawerHandle drawerHandle = null!;
    private RectTransform handleRect = null!, handlePill = null!, handleShadow = null!;
    private TMP_Text handleHint = null!;
    private TMP_Text title = null!, clock = null!, status = null!, detailTitle = null!, held = null!, itemEvents = null!, diagnostics = null!;
    private Button play = null!, camera = null!, backpack = null!, diagnosticButton = null!, collapse = null!;
    private Slider timeline = null!;
    private Image panelImage = null!;
    private GameObject buttonPrefab = null!;
    private TMP_Text textTemplate = null!;
    private Sprite fillSprite = null!, outlineSprite = null!;
    private ColorBlock originalColors;
    private InputSystemUIInputModule? inputModule;
    private InputActionAsset? inputAsset;
    private GameObject? oldSelection;
    private bool detailsExpanded, diagnosticExpanded, disposed;
    private float width = -1, height = -1;
    private double nextDetailUpdate;
    private int detailRevision = -1;
    private string detailFocus = "";
    private static readonly Color Cream = new(.8742f, .8567f, .7615f, 1);
    private static readonly Color Brown = new(.20f, .17f, .14f, .97f);
    public bool IsExpanded => drawer.Open || drawer.Dragging || drawer.Progress > .01f;
    private float DrawerTravel => Mathf.Max(1, height + 12);

    public NativeReplayConsole(PlaybackSession session, Action close, Action toggleDiagnostics, Action<Exception> error)
    {
        this.session = session; this.close = close; this.toggleDiagnostics = toggleDiagnostics; this.error = error;
        try { Build(); }
        catch { Dispose(); throw; }
    }

    private void Build()
    {
        var resource = Resources.Load<GameObject>("ButtonsInputField");
        buttonPrefab = resource ? resource.GetComponent<ModalButtonsField>()?.m_buttonPrefab! : null!;
        var textResource = Resources.Load<GameObject>("SubheaderModalField");
        textTemplate = textResource ? textResource.GetComponentInChildren<TMP_Text>(true) : null!;
        if (!buttonPrefab || !textTemplate || !textTemplate.font)
            throw new InvalidOperationException("未找到游戏原生回放按钮或字体，请检查游戏版本。");
        // Refuse a future prefab with gameplay/animation callbacks before Instantiate.
        foreach (var component in buttonPrefab.GetComponentsInChildren<Component>(true))
            if (component && !VerifiedButtonComponent(component.GetType()))
                throw new InvalidOperationException("原生按钮包含未验证的行为，已停止创建回放控制台。");
        var sourceButton = buttonPrefab.GetComponent<Button>();
        var sourceImage = buttonPrefab.GetComponent<Image>();
        if (!sourceButton || !sourceImage || !sourceImage.sprite)
            throw new InvalidOperationException("原生回放按钮资源不完整。");
        originalColors = sourceButton.colors;
        fillSprite = sourceImage.sprite;
        var gui = GUIManager.instance;
        outlineSprite = gui && gui.backpack && gui.backpack.outline ? gui.backpack.outline.sprite : null!;
        if (!outlineSprite) outlineSprite = fillSprite;

        root = new GameObject("PEAK Memories - native playback controls", typeof(RectTransform));
        root.SetActive(false);
        canvasRect = root.GetComponent<RectTransform>();
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 1010;
        canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 |
            AdditionalCanvasShaderChannels.TexCoord2 | AdditionalCanvasShaderChannels.TexCoord3;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
        root.AddComponent<GraphicRaycaster>();
        group = root.AddComponent<CanvasGroup>();

        panel = Rect("Playback control panel", root.transform);
        panel.anchorMin = panel.anchorMax = new Vector2(.5f, 0); panel.pivot = new Vector2(.5f, 0);
        panel.anchoredPosition = Vector2.zero;
        panelGroup = panel.gameObject.AddComponent<CanvasGroup>();
        panelImage = Image(panel, fillSprite, Brown);
        var border = Rect("Native outline", panel);
        Stretch(border); Image(border, outlineSprite, new Color(Cream.r, Cream.g, Cream.b, .78f));

        title = Text("Memory heading", panel, 29, TextAlignmentOptions.Left);
        clock = Text("Playback clock", panel, 31, TextAlignmentOptions.Right);
        timeline = MakeTimeline(panel);
        controls = Rect("Playback actions", panel);
        play = Button("播放 [空格]", controls, () => session.Paused = !session.Paused);
        SetSelected(play, true);
        foreach (float speed in new[] { .25f, .5f, 1f, 2f })
        {
            float value = speed;
            var button = Button(speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "x", controls, () => session.Speed = value);
            speeds.Add((button, speed));
        }
        Button("切换玩家 [Tab]", controls, session.CycleFocus);
        camera = Button("跟随镜头", controls, session.ToggleCamera);
        backpack = Button("背包详情", controls, () => { detailsExpanded = !detailsExpanded; nextDetailUpdate = 0; width = -1; });
        Button("返回 [Esc]", controls, close);
        status = Text("Camera and loading hints", panel, 20, TextAlignmentOptions.Left);
        diagnosticButton = Button("详细信息 [F9]", panel, toggleDiagnostics);

        details = Rect("Recorded inventory details", panel);
        detailTitle = Text("Inventory heading", details, 23, TextAlignmentOptions.Left);
        viewport = Rect("Inventory viewport", details); viewport.gameObject.AddComponent<RectMask2D>();
        content = Rect("Recorded inventory cells", viewport);
        content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1); content.pivot = new Vector2(.5f, 1);
        var scroll = viewport.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport; scroll.content = content; scroll.horizontal = false; scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 35;
        held = Text("Recorded held item", details, 20, TextAlignmentOptions.Left);
        itemEvents = Text("Recent item events", details, 19, TextAlignmentOptions.Left);
        diagnostics = Text("Playback diagnostics", panel, 19, TextAlignmentOptions.Left);
        diagnostics.textWrappingMode = TextWrappingModes.Normal;
        collapse = Button("收起 [H]", panel, Hide);
        details.gameObject.SetActive(false); diagnostics.gameObject.SetActive(false);
        BuildDrawerHandle();
        ConnectNavigation();
        BuildInput();
        root.SetActive(true);
        // Do not preselect a control: Space has its one explicit playback action.
        EventSystem.current?.SetSelectedGameObject(null);
        Tick(false, "", "");
    }

    private void BuildInput()
    {
        foreach (var system in Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None))
        {
            previousSystems.Add((system, system.enabled));
            if (system == EventSystem.current) oldSelection = system.currentSelectedGameObject;
            system.enabled = false;
        }
        var inputRoot = new GameObject("Memories UI input"); inputRoot.SetActive(false); inputRoot.transform.SetParent(root!.transform, false);
        inputRoot.AddComponent<EventSystem>();
        inputModule = inputRoot.AddComponent<InputSystemUIInputModule>();
        // AssignDefaultActions shares a static asset with native menus. This asset
        // is instead wholly owned here, so playback cannot disable live UI actions.
        inputAsset = ScriptableObject.CreateInstance<InputActionAsset>(); inputAsset.name = "Memories playback UI only";
        var map = inputAsset.AddActionMap("MemoriesUI");
        InputActionReference Action(string name, InputActionType type, string control, params string[] bindings)
        {
            var action = map.AddAction(name, type); action.expectedControlType = control;
            foreach (string binding in bindings) action.AddBinding(binding);
            var reference = InputActionReference.Create(action); inputReferences.Add(reference); return reference;
        }
        inputModule.actionsAsset = inputAsset;
        inputModule.point = Action("Point", InputActionType.PassThrough, "Vector2", "<Mouse>/position", "<Touchscreen>/primaryTouch/position");
        inputModule.leftClick = Action("Click", InputActionType.PassThrough, "Button", "<Mouse>/leftButton", "<Touchscreen>/primaryTouch/press");
        inputModule.rightClick = Action("RightClick", InputActionType.PassThrough, "Button", "<Mouse>/rightButton");
        inputModule.scrollWheel = Action("Scroll", InputActionType.PassThrough, "Vector2", "<Mouse>/scroll");
        inputModule.submit = Action("Submit", InputActionType.Button, "Button", "<Keyboard>/enter", "<Gamepad>/buttonSouth");
        inputModule.cancel = Action("Cancel", InputActionType.Button, "Button", "<Gamepad>/buttonEast");
        inputModule.move = Action("Move", InputActionType.PassThrough, "Vector2", "<Gamepad>/leftStick", "<Gamepad>/dpad");
        inputModule.move.action.AddCompositeBinding("2DVector").With("Up", "<Keyboard>/upArrow").With("Down", "<Keyboard>/downArrow")
            .With("Left", "<Keyboard>/leftArrow").With("Right", "<Keyboard>/rightArrow");
        inputRoot.SetActive(true);
    }

    public void Tick(bool showDiagnostics, string performance, string hudWarning)
    {
        if (disposed || !root) return;
        bool changed = diagnosticExpanded != showDiagnostics;
        diagnosticExpanded = showDiagnostics;
        group.interactable = Application.isFocused && !session.CameraLooking;
        group.blocksRaycasts = group.interactable;
        title.text = "这一段回忆  ·  " + session.VisibleActors + " 位参与者";
        double displayed = session.Buffering ? session.RequestedTime : session.Time;
        clock.text = MemoriesLibraryModel.FormatDuration(displayed) + " / " + MemoriesLibraryModel.FormatDuration(session.Duration);
        timeline.maxValue = Math.Max(.001f, (float)session.Duration);
        timeline.SetValueWithoutNotify((float)displayed);
        play.GetComponentInChildren<TMP_Text>(true).text = session.Paused ? "播放 [空格]" : "暂停 [空格]";
        camera.GetComponentInChildren<TMP_Text>(true).text = session.FreeCamera ? "自由镜头" : "跟随镜头";
        SetSelected(camera, session.FreeCamera); SetSelected(backpack, detailsExpanded);
        foreach (var speed in speeds) SetSelected(speed.Button, speed.Speed == session.Speed);
        SetSelected(diagnosticButton, diagnosticExpanded);
        string hint = session.FreeCamera ? "右键 + WASD / Q E 移动 · Shift 加速" : "按住右键转向 · 滚轮调整跟随距离";
        if (session.Buffering) hint = session.BufferingLabel;
        else if (!string.IsNullOrEmpty(hudWarning)) hint = hudWarning;
        else if (!string.IsNullOrEmpty(session.ObjectWarning)) hint = session.ObjectWarning;
        else if (session.MissingJointCount > 0) hint = session.MissingJointCount + " 个人物关节未匹配 · F9 查看诊断";
        status.text = hint;
        diagnostics.text = performance + "\n场景物品 " + session.VisibleItemCount + "/" + session.RecordedItemCount +
            " · 箱子 " + session.RecordedCrateCount + " · 关节 " + session.RecordedJointCount + " · 录制采样 " + session.SampleHz + " Hz" +
            (string.IsNullOrEmpty(session.ObjectWarning) ? "" : "\n" + session.ObjectWarning) + "\nEnd 紧急返回 · F7 退出回忆";
        if (detailsExpanded && IsExpanded && (Time.unscaledTimeAsDouble >= nextDetailUpdate || detailRevision != session.UiRevision || detailFocus != session.FocusId))
        {
            nextDetailUpdate = Time.unscaledTimeAsDouble + .1; detailRevision = session.UiRevision; detailFocus = session.FocusId;
            RefreshInventory(); changed = true;
        }
        float desiredWidth = Mathf.Min(1500, canvasRect.rect.width - 64);
        float desiredHeight = 184 + (detailsExpanded ? 240 : 0) + (diagnosticExpanded ? 108 : 0);
        if (changed || Math.Abs(width - desiredWidth) > .5f || height != desiredHeight) Layout(desiredWidth, desiredHeight);
        TickDrawer();
        if (Gamepad.current != null && Gamepad.current.dpad.ReadValue() != Vector2.zero && EventSystem.current && !EventSystem.current.currentSelectedGameObject)
            EventSystem.current.SetSelectedGameObject(panelGroup.interactable ? play.gameObject : drawerHandle.gameObject);
    }

    private void Layout(float desiredWidth, float desiredHeight)
    {
        width = Mathf.Max(600, desiredWidth); height = desiredHeight;
        panel.sizeDelta = new Vector2(width, height);
        float inner = width - 40, y = 16;
        float heading = inner - 176;
        Place(title.rectTransform, 20, y, heading * .60f, 32);
        Place(clock.rectTransform, 20 + heading * .60f, y, heading * .40f, 32);
        Place((RectTransform)collapse.transform, width - 178, y - 2, 156, 35); y += 42;
        Place((RectTransform)timeline.transform, 24, y, inner - 8, 26); y += 36;
        Place(controls, 20, y, inner, 48);
        float[] sizes = { 180, 92, 82, 70, 70, 210, 168, 160, 145 };
        float total = sizes.Sum() + 10 * (sizes.Length - 1);
        float factor = Mathf.Min(1, inner / total), x = 0;
        for (int i = 0; i < sizes.Length; i++)
        {
            var child = (RectTransform)controls.GetChild(i);
            float size = sizes[i] * factor;
            Place(child, x, 0, size, 48); x += size + 10 * factor;
            if (i == 4) x += Mathf.Max(0, inner - total);
        }
        y += 61;
        Place(status.rectTransform, 20, y, inner - 230, 26);
        Place((RectTransform)diagnosticButton.transform, width - 240, y - 2, 218, 31); y += 35;
        details.gameObject.SetActive(detailsExpanded);
        if (detailsExpanded)
        {
            Place(details, 20, y, inner, 228);
            Place(detailTitle.rectTransform, 0, 0, inner, 28);
            Place(viewport, 0, 35, inner, 130);
            Place(held.rectTransform, 0, 173, inner, 23);
            Place(itemEvents.rectTransform, 0, 201, inner, 23);
            LayoutCells(inner); y += 240;
        }
        diagnostics.gameObject.SetActive(diagnosticExpanded);
        if (diagnosticExpanded) Place(diagnostics.rectTransform, 20, y, inner, 100);
    }

    private void BuildDrawerHandle()
    {
        handleRect = Rect("Bottom memory drawer handle", root!.transform);
        handleRect.anchorMin = handleRect.anchorMax = new Vector2(.5f, 0);
        handleRect.pivot = new Vector2(.5f, .5f); handleRect.sizeDelta = new Vector2(240, 44);
        var hit = handleRect.gameObject.AddComponent<Image>(); hit.color = Color.clear; hit.raycastTarget = true;
        handleShadow = Capsule("Warm handle shadow", handleRect, new Vector2(132, 10), new Color(.12f, .10f, .08f, .45f));
        handleShadow.anchoredPosition = new Vector2(0, -1);
        handlePill = Capsule("Native cream grab bar", handleRect, new Vector2(124, 6), Cream);
        drawerHandle = handleRect.gameObject.AddComponent<NativeReplayDrawerHandle>();
        drawerHandle.targetGraphic = handlePill.GetComponent<ProceduralImage>();
        var colors = originalColors; colors.normalColor = Color.white;
        colors.highlightedColor = colors.selectedColor = originalColors.pressedColor;
        drawerHandle.colors = colors;
        drawerHandle.Configure(canvasRect, drawer, () => DrawerTravel,
            () => !disposed && Application.isFocused && !session.CameraLooking, DrawerChanged);
        handleHint = Text("Memory drawer hover hint", handleRect, 18, TextAlignmentOptions.Center);
        handleHint.rectTransform.anchorMin = handleHint.rectTransform.anchorMax = new Vector2(.5f, .5f);
        handleHint.rectTransform.pivot = new Vector2(.5f, .5f);
        handleHint.rectTransform.anchoredPosition = new Vector2(0, 34);
        handleHint.rectTransform.sizeDelta = new Vector2(380, 28);
        handleHint.text = "上拉 / 点击展开回放控制 · H";
        handleHint.gameObject.SetActive(false);
        navigation.Add(drawerHandle);
    }

    private static RectTransform Capsule(string name, Transform parent, Vector2 size, Color color)
    {
        var rect = Rect(name, parent);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f); rect.sizeDelta = size;
        rect.gameObject.AddComponent<RoundModifier>();
        var graphic = rect.gameObject.AddComponent<ProceduralImage>();
        graphic.color = color; graphic.raycastTarget = false;
        return rect;
    }

    public void ToggleDrawer()
    { if (disposed) return; drawer.Toggle(); DrawerChanged(); }
    public void Show()
    { if (disposed) return; drawer.SetOpen(true); DrawerChanged(); }
    public void Hide()
    { if (disposed) return; drawer.SetOpen(false); DrawerChanged(); }

    private void DrawerChanged()
    {
        if (disposed) return;
        if (!drawer.Open || drawer.Dragging)
        {
            panelGroup.interactable = panelGroup.blocksRaycasts = false;
            MoveSelectionToHandle();
        }
    }

    private void MoveSelectionToHandle()
    {
        var system = EventSystem.current;
        if (system && system.currentSelectedGameObject && system.currentSelectedGameObject.transform.IsChildOf(panel))
            system.SetSelectedGameObject(drawerHandle.gameObject);
    }

    private void TickDrawer()
    {
        if (!group.interactable) drawerHandle.CancelDrag();
        drawer.Tick(Time.unscaledDeltaTime);
        bool visible = drawer.Open || drawer.Dragging || drawer.Progress > 0;
        if (panel.gameObject.activeSelf != visible) panel.gameObject.SetActive(visible);
        panel.anchoredPosition = new Vector2(0, -height + drawer.Progress * DrawerTravel);
        panelGroup.alpha = Mathf.Clamp01(drawer.Progress * 4);
        panelGroup.interactable = group.interactable && drawer.Open && !drawer.Dragging && drawer.Progress >= .999f;
        panelGroup.blocksRaycasts = panelGroup.interactable;
        if (!panelGroup.interactable) MoveSelectionToHandle();
        handleRect.anchoredPosition = new Vector2(0, 26 + drawer.Progress * DrawerTravel);
        float target = drawerHandle.Hovered || drawer.Dragging ? 154 : 124;
        float barWidth = Mathf.Lerp(handlePill.sizeDelta.x, target, Mathf.Min(1, Time.unscaledDeltaTime * 12));
        handlePill.sizeDelta = new Vector2(barWidth, 6); handleShadow.sizeDelta = new Vector2(barWidth + 8, 10);
        bool hint = group.interactable && drawer.Progress < .01f && !drawer.Open && drawerHandle.PointerHovered;
        if (handleHint.gameObject.activeSelf != hint) handleHint.gameObject.SetActive(hint);
        drawerHandle.navigation = panelGroup.interactable
            ? new Navigation { mode = Navigation.Mode.Explicit, selectOnUp = play, selectOnDown = collapse }
            : new Navigation { mode = Navigation.Mode.None };
    }

    private void RefreshInventory()
    {
        ordered.Clear();
        var actor = session.FocusState;
        if (actor == null) detailTitle.text = "当前时刻没有可观察的玩家";
        else if (!actor.InventoryKnown) detailTitle.text = actor.Name + " · 库存尚未同步，未知 ≠ 空背包";
        else
        {
            var state = session.FocusHudState;
            detailTitle.text = actor.Name + " 的背包 · " + actor.Action + " · 体力 " + (state?.Stamina ?? actor.Stamina).ToString("P0") + " + " + (state?.ExtraStamina ?? actor.ExtraStamina).ToString("P0");
            foreach (var slot in actor.Inventory) if (slot.Slot != 250 || !slot.Empty) ordered.Add(slot);
            ordered.Sort((a, b) => { int groupOrder = a.Backpack.CompareTo(b.Backpack); return groupOrder != 0 ? groupOrder : a.Slot.CompareTo(b.Slot); });
        }
        while (cells.Count < ordered.Count) cells.Add(MakeCell());
        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i]; cell.Root.SetActive(i < ordered.Count);
            if (i >= ordered.Count) continue;
            var slot = ordered[i];
            var item = slot.Empty ? (Icon: (Texture?)null, Name: "空") : Item(slot.ItemId);
            cell.Slot.text = slot.Backpack ? "包内 " + (slot.Slot + 1) : slot.Slot == 250 ? "临时手持" : slot.Slot == 3 ? "背包" : "栏位 " + (slot.Slot + 1);
            if (slot.Equipped) cell.Slot.text = "手持 · " + cell.Slot.text;
            cell.Name.text = NativeReplayHud.ItemLabel(item.Name, slot.Cooked) + (slot.Uses >= 0 && !slot.Empty ? " × " + slot.Uses : "");
            cell.Icon.texture = item.Icon; cell.Icon.enabled = item.Icon;
            cell.Icon.color = slot.Cooked >= 0 ? ItemCooking.GetCookColor((int)slot.Cooked) : Color.white;
            cell.Frame.color = slot.Equipped ? Cream : new Color(Cream.r, Cream.g, Cream.b, .58f);
        }
        var heldItem = session.HeldItem;
        held.text = heldItem == null ? "当前未持物" : "手持：" + Item(heldItem.ItemId).Name +
            (!heldItem.ProgressKnown ? " · 使用进度未同步" : heldItem.Primary || heldItem.Secondary ? " · 使用中 " + heldItem.Progress.ToString("P0") : "");
        events.Clear(); events.Append("物品事件：");
        var recent = session.RecentEvents();
        for (int i = 0; i < recent.Count; i++)
        {
            var entry = recent[i]; if (i > 0) events.Append("  ·  ");
            events.Append(MemoriesLibraryModel.FormatDuration(entry.T)).Append(' ').Append(EventName(entry.Kind)).Append(' ').Append(Item(entry.ItemId).Name);
        }
        if (recent.Count == 0) events.Append('—');
        itemEvents.text = events.ToString();
    }

    private Cell MakeCell()
    {
        var rect = Rect("Recorded inventory slot", content);
        var cell = new Cell { Root = rect.gameObject, Frame = Image(rect, outlineSprite, Cream) };
        cell.Slot = Text("Slot label", rect, 19, TextAlignmentOptions.Center);
        cell.Name = Text("Recorded item name", rect, 19, TextAlignmentOptions.Center);
        var icon = Rect("Original item icon", rect); cell.Icon = icon.gameObject.AddComponent<RawImage>(); cell.Icon.raycastTarget = false;
        return cell;
    }

    private void LayoutCells(float inner)
    {
        int columns = Mathf.Clamp(Mathf.FloorToInt(inner / 135), 4, 10);
        float size = (inner - (columns - 1) * 12) / columns;
        int rows = Mathf.CeilToInt((float)ordered.Count / columns);
        content.sizeDelta = new Vector2(0, Mathf.Max(130, rows * 130));
        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i]; Place((RectTransform)cell.Root.transform, i % columns * (size + 12), i / columns * 130, size, 118);
            Place(cell.Slot.rectTransform, 4, 5, size - 8, 24);
            Place((RectTransform)cell.Icon.transform, (size - 60) / 2, 32, 60, 54);
            Place(cell.Name.rectTransform, 4, 90, size - 8, 24);
        }
    }

    private (Texture? Icon, string Name) Item(int id)
    {
        if (catalogue.TryGetValue(id, out var cached)) return cached;
        if (id >= 0 && id <= ushort.MaxValue && ItemDatabase.TryGetItem((ushort)id, out var item) && item)
        {
            var value = ((Texture?)item.UIData?.GetIcon(), item.GetName());
            if (catalogue.Count < 2048) catalogue[id] = value; return value;
        }
        return (null, "#" + id);
    }

    private Slider MakeTimeline(Transform parent)
    {
        var rect = Rect("Recorded time slider", parent);
        var slider = rect.gameObject.AddComponent<Slider>();
        slider.minValue = 0; slider.maxValue = Math.Max(.001f, (float)session.Duration);
        var track = Rect("Native time track", rect); Stretch(track); track.offsetMin = new Vector2(0, 9); track.offsetMax = new Vector2(0, -9);
        Image(track, fillSprite, new Color(.46f, .41f, .34f));
        var fillArea = Rect("Recorded time fill area", rect); Stretch(fillArea); fillArea.offsetMin = new Vector2(10, 9); fillArea.offsetMax = new Vector2(-10, -9);
        var fill = Rect("Recorded time fill", fillArea); Stretch(fill); Image(fill, fillSprite, Cream);
        var handleArea = Rect("Recorded time handle area", rect); Stretch(handleArea); handleArea.offsetMin = new Vector2(10, 0); handleArea.offsetMax = new Vector2(-10, 0);
        var handle = Rect("Native time handle", handleArea); handle.sizeDelta = new Vector2(22, 32);
        var graphic = Image(handle, fillSprite, originalColors.pressedColor);
        graphic.raycastTarget = true;
        // A transparent hit area makes the entire track draggable, not just its handle.
        var hit = rect.gameObject.AddComponent<Image>(); hit.color = Color.clear; hit.raycastTarget = true;
        slider.fillRect = fill; slider.handleRect = handle; slider.targetGraphic = graphic;
        slider.direction = Slider.Direction.LeftToRight; slider.colors = originalColors;
        slider.onValueChanged.AddListener(value => Run(() => session.Seek(value)));
        navigation.Add(slider); return slider;
    }

    private Button Button(string label, Transform parent, Action action)
    {
        var go = Object.Instantiate(buttonPrefab, parent, false); go.name = "Memories action " + label; go.SetActive(true);
        var button = go.GetComponent<Button>(); button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(() => Run(action));
        var text = button.GetComponentInChildren<TMP_Text>(true);
        text.text = label; text.richText = false; text.raycastTarget = false; text.enableAutoSizing = true;
        text.fontSizeMin = 18; text.fontSizeMax = 28; text.textWrappingMode = TextWrappingModes.NoWrap;
        navigation.Add(button); return button;
    }

    private TMP_Text Text(string name, Transform parent, float size, TextAlignmentOptions alignment)
    {
        var rect = Rect(name, parent); var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        text.font = textTemplate.font; text.fontSharedMaterial = textTemplate.fontSharedMaterial;
        text.spriteAsset = textTemplate.spriteAsset; text.color = Cream; text.richText = false; text.raycastTarget = false;
        text.alignment = alignment; text.fontSize = size; text.enableAutoSizing = true;
        text.fontSizeMin = Mathf.Min(18, size); text.fontSizeMax = size;
        text.textWrappingMode = TextWrappingModes.NoWrap; text.overflowMode = TextOverflowModes.Ellipsis;
        return text;
    }

    private void SetSelected(Button button, bool selected)
    {
        var colors = originalColors; if (selected) colors.normalColor = originalColors.selectedColor;
        if (button.colors != colors) button.colors = colors;
    }

    private void ConnectNavigation()
    {
        for (int i = 0; i < navigation.Count; i++)
            navigation[i].navigation = new Navigation { mode = Navigation.Mode.Explicit,
                selectOnLeft = navigation[Math.Max(0, i - 1)], selectOnRight = navigation[Math.Min(navigation.Count - 1, i + 1)],
                selectOnUp = i == 0 ? navigation[1] : navigation[0], selectOnDown = navigation[navigation.Count - 1] };
    }

    private void Run(Action action)
    { if (disposed) return; try { action(); } catch (Exception exception) { error(exception); } }
    private static bool VerifiedButtonComponent(Type type) =>
        type == typeof(Transform) || type == typeof(RectTransform) || type == typeof(CanvasRenderer) ||
        type == typeof(Image) || type == typeof(RawImage) || type == typeof(TextMeshProUGUI) || type == typeof(Text) ||
        type == typeof(Button) || type == typeof(LayoutElement) || type == typeof(HorizontalLayoutGroup) ||
        type == typeof(VerticalLayoutGroup) || type == typeof(GridLayoutGroup) || type == typeof(ContentSizeFitter);
    private static RectTransform Rect(string name, Transform parent)
    { var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false); return go.GetComponent<RectTransform>(); }
    private static Image Image(RectTransform rect, Sprite sprite, Color color)
    { var image = rect.gameObject.AddComponent<Image>(); image.sprite = sprite; image.type = UnityEngine.UI.Image.Type.Sliced; image.color = color; image.raycastTarget = false; return image; }
    private static void Stretch(RectTransform rect)
    { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero; }
    private static void Place(RectTransform rect, float x, float y, float width, float height)
    { rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1); rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height); }
    private static string EventName(string kind) => kind switch
    {
        "equip" => "拿出", "stash" => "收起", "drop" => "丢出", "consume" => "消耗",
        "use-primary-start" => "开始使用", "use-primary-finish" => "完成使用", "use-primary-cancel" => "中断使用",
        "use-secondary-start" => "开始副使用", "use-secondary-finish" => "完成副使用", "use-secondary-cancel" => "中断副使用",
        "use-charge" => "次数变化", "feed-start" => "开始喂食", "feed-end" => "结束喂食", _ => kind,
    };

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (root) root!.SetActive(false);
        if (inputModule) inputModule!.actionsAsset = null;
        if (inputAsset) { inputAsset!.Disable(); Object.Destroy(inputAsset); }
        foreach (var reference in inputReferences) if (reference) Object.Destroy(reference);
        if (root) Object.Destroy(root); root = null;
        foreach (var saved in previousSystems) if (saved.System) saved.System.enabled = saved.Enabled;
        if (EventSystem.current && oldSelection && oldSelection!.activeInHierarchy) EventSystem.current.SetSelectedGameObject(oldSelection);
        cells.Clear(); catalogue.Clear(); inputReferences.Clear(); previousSystems.Clear();
    }
}
