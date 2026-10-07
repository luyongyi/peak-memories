using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UI.ProceduralImage;
using Zorro.ControllerSupport;
using Zorro.Core;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

// A passive view of recorded data. In particular, StaminaBar.Update and
// InventoryItemUI.SetItem/SetSelected must never run here: those methods read
// Character.observedCharacter and can play sounds or alter the live HUD.
internal sealed class NativeReplayHud : IDisposable
{
    private readonly GameObject root;
    private readonly Action<string>? reportWarning;
    private readonly Dictionary<int, (Texture? Icon, string Name, bool HideFuel)> catalogue = new();
    private readonly SlotView?[] slots = new SlotView?[3];
    private readonly SlotView?[] contents = new SlotView?[4];
    private readonly RectTransform inventoryRoot, contentsRoot;
    private BarView? bar;
    private SlotView? backpack, temporary;
    private TMP_Text? observedName, stateNotice, inventoryNotice;
    private string resourcesWarning = "";
    private InputScheme? inputScheme;
    private bool disposed;
    public bool BackpackExpanded { get; set; }
    public string Warning { get; private set; } = "";
    public string Diagnostic => "native-hud=" + root.activeInHierarchy + "; " + (bar?.Diagnostic ?? "bar-not-created") + "; " + Warning;

    public NativeReplayHud(Action<string>? reportWarning = null)
    {
        this.reportWarning = reportWarning;
        root = new GameObject("PEAK Memories - recorded native HUD", typeof(RectTransform));
        root.SetActive(false);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000;
        canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 |
            AdditionalCanvasShaderChannels.TexCoord2 | AdditionalCanvasShaderChannels.TexCoord3;
        var scaler = root.AddComponent<CanvasScaler>();
        var native = GUIManager.instance;
        var nativeScaler = native && native.hudCanvas ? native.hudCanvas.GetComponent<CanvasScaler>() : null;
        if (nativeScaler) JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(nativeScaler), scaler);
        else
        {
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = .5f;
        }
        inventoryRoot = Place("Inventory", root.transform, new Vector2(1, 0), new Vector2(-75, 107), new Vector2(636, 100));
        inventoryRoot.pivot = new Vector2(1, .5f);
        contentsRoot = Place("Recorded backpack contents", root.transform, new Vector2(1, 0), new Vector2(-75, 263), new Vector2(340, 90));
        contentsRoot.pivot = new Vector2(1, .5f);
        try
        {
            if (!native) throw new InvalidOperationException("游戏原版 HUD 尚未就绪");
            if (native.bar)
            {
                try { bar = new BarView(native.bar, root.transform); }
                catch (Exception e) { ResourceFailure("原版状态条", e); }
            }
            else ResourceFailure("原版状态条", null);
            for (int i = 0; i < slots.Length; i++)
            {
                if (native.items == null || i >= native.items.Length || !native.items[i]) { ResourceFailure("原版物品栏", null); break; }
                try
                {
                    slots[i] = new SlotView(native.items[i], inventoryRoot, new Vector2(-240 + i * 80, 0), new Vector2(70, 70));
                    slots[i]!.BindInput((InputSpriteData.InputAction)((int)InputSpriteData.InputAction.Slot1 + i));
                }
                catch (Exception e) { ResourceFailure("原版物品栏", e); }
            }
            if (native.backpack)
            {
                try
                {
                    backpack = new SlotView(native.backpack, inventoryRoot, new Vector2(-10, 0), new Vector2(100, 100));
                    backpack.BindInput(InputSpriteData.InputAction.Slot4);
                }
                catch (Exception e) { ResourceFailure("原版背包", e); }
            }
            if (native.temporaryItem)
            {
                try { temporary = new SlotView(native.temporaryItem, inventoryRoot, new Vector2(-355, 0), new Vector2(70, 70)); }
                catch (Exception e) { ResourceFailure("原版临时手持栏", e); }
            }
            if (native.items != null && native.items.Length > 0 && native.items[0])
            {
                for (int i = 0; i < contents.Length; i++)
                {
                    try
                    {
                        contents[i] = new SlotView(native.items[0], contentsRoot, new Vector2(-280 + i * 80, 0), new Vector2(70, 70));
                        contents[i]!.HideInput();
                    }
                    catch (Exception e) { ResourceFailure("原版包内物品栏", e); break; }
                }
            }
            if (native.spectatingNameText)
            {
                observedName = NativeLabel(native.spectatingNameText, root.transform, "Observed player", new Vector2(.5f, 1), new Vector2(0, -75), new Vector2(600, 100));
                observedName.rectTransform.localScale = Vector3.one * .6f;
                observedName.color = native.spectatingNameColor;
                stateNotice = NativeLabel(native.spectatingNameText, root.transform, "Unknown recorded status", new Vector2(0, 0), new Vector2(70, 160), new Vector2(570, 80));
                stateNotice.rectTransform.pivot = new Vector2(0, .5f);
                stateNotice.fontSize = 22;
                stateNotice.alignment = TextAlignmentOptions.Left;
                inventoryNotice = NativeLabel(native.spectatingNameText, root.transform, "Unknown recorded inventory", new Vector2(1, 0), new Vector2(-75, 107), new Vector2(600, 80));
                inventoryNotice.rectTransform.pivot = new Vector2(1, .5f);
                inventoryNotice.fontSize = 22;
                inventoryNotice.alignment = TextAlignmentOptions.Right;
            }
            else ResourceFailure("原版观察名称字体", null);
        }
        catch (Exception e) { ResourceFailure("原版 HUD", e); }
        // This root contains only visual UI components. No gameplay scripts,
        // Animators, audio sources, localization or input subscriptions exist.
        root.SetActive(true);
        contentsRoot.gameObject.SetActive(false);
        Warning = resourcesWarning;
    }

    public void Tick(PlaybackSession session)
    {
        if (disposed) return;
        var scheme = InputHandler.GetCurrentUsedInputScheme();
        if (inputScheme != scheme)
        {
            inputScheme = scheme;
            for (int i = 0; i < slots.Length; i++) slots[i]?.BindInput((InputSpriteData.InputAction)((int)InputSpriteData.InputAction.Slot1 + i), scheme);
            backpack?.BindInput(InputSpriteData.InputAction.Slot4, scheme);
        }
        var actor = session.FocusState;
        if (observedName)
        {
            observedName!.gameObject.SetActive(actor != null);
            observedName.text = actor?.Name ?? "";
        }
        bool stateKnown = session.FocusHudState != null;
        bar?.Apply(session.FocusHudState);
        string stateMessage = actor == null ? "" : stateKnown ? (bar == null ? "原版状态条资源不可用" : "") : "此录像未记录完整状态条";
        SetNotice(stateNotice, stateMessage);
        bool inventoryKnown = actor != null && actor.InventoryKnown;
        inventoryRoot.gameObject.SetActive(inventoryKnown);
        SetNotice(inventoryNotice, actor != null && !inventoryKnown ? "库存尚未同步" : "");
        if (inventoryKnown)
        {
            int filled = 0;
            for (int i = 0; i < slots.Length; i++) ApplySlot(slots[i], Find(actor!, i, false), session.HeldItem, 0);
            foreach (var item in actor!.Inventory) if (item.Backpack && !item.Empty) filled++;
            ApplySlot(backpack, Find(actor, 3, false), session.HeldItem, filled);
            var temp = Find(actor, 250, false);
            if (temporary != null)
            {
                temporary.Root.gameObject.SetActive(temp != null && !temp.Empty);
                ApplySlot(temporary, temp, session.HeldItem, 0, true);
            }
            contentsRoot.gameObject.SetActive(BackpackExpanded);
            if (BackpackExpanded)
            {
                for (int i = 0; i < contents.Length; i++)
                {
                    var entry = Find(actor, i, true);
                    if (contents[i] == null) continue;
                    contents[i]!.Root.gameObject.SetActive(entry != null);
                    ApplySlot(contents[i], entry, session.HeldItem, 0);
                }
            }
        }
        else contentsRoot.gameObject.SetActive(false);
        Warning = resourcesWarning;
        if (actor != null && !stateKnown) Warning = Append(Warning, "此录像未记录完整状态条；不补画满体力或零寒冷");
        if (actor != null && !inventoryKnown) Warning = Append(Warning, "库存尚未同步；未知不会显示成空背包");
    }

    private void ApplySlot(SlotView? view, InventoryFrame? entry, ItemFrame? held, int backpackCount, bool temporarySlot = false)
    {
        if (view == null) return;
        var item = entry != null && !entry.Empty ? Item(entry.ItemId) : default;
        bool matchesHeld = entry != null && held != null && !entry.Empty && held.ItemId == entry.ItemId &&
            (entry.Equipped || temporarySlot);
        view.Apply(entry, item.Icon, item.Name, item.HideFuel, matchesHeld ? held : null, backpackCount, temporarySlot);
    }

    private (Texture? Icon, string Name, bool HideFuel) Item(int id)
    {
        if (catalogue.TryGetValue(id, out var cached)) return cached;
        try
        {
            if (id >= 0 && id <= ushort.MaxValue && ItemDatabase.TryGetItem((ushort)id, out var prefab) && prefab)
            {
                Texture? icon = prefab.UIData?.GetIcon();
                var value = (icon, prefab.GetName(), prefab.UIData?.hideFuel ?? true);
                if (catalogue.Count < 2048) catalogue[id] = value;
                return value;
            }
        }
        catch { /* Native catalog or localization may finish loading later. */ }
        return (null, "#" + id, true);
    }

    private static InventoryFrame? Find(ActorFrame actor, int slot, bool backpackSlot)
    {
        foreach (var item in actor.Inventory) if (item.Slot == slot && item.Backpack == backpackSlot) return item;
        return null;
    }

    private void ResourceFailure(string control, Exception? error)
    {
        string message = control + "资源不可用";
        if (error != null) message += "（" + error.Message + "）";
        if (!resourcesWarning.Contains(message))
        {
            resourcesWarning = Append(resourcesWarning, message);
            reportWarning?.Invoke("Replay HUD: " + message + (error == null ? "" : "\n" + error));
        }
    }

    private static string Append(string current, string next) => current.Length == 0 ? next : current + "；" + next;
    internal static string ItemLabel(string name, int cooked)
    {
        string key = cooked >= 4 ? "COOKED_INCINERATED" : cooked == 3 ? "COOKED_BURNT" : cooked == 2 ? "COOKED_WELLDONE" : cooked == 1 ? "COOKED_COOKED" : "";
        return key.Length == 0 ? name : LocalizedText.GetText(key).Replace("#", name);
    }
    private static void SetNotice(TMP_Text? text, string message)
    {
        if (!text) return;
        text!.text = message;
        text.gameObject.SetActive(message.Length != 0);
    }

    private static TMP_Text NativeLabel(TMP_Text source, Transform parent, string name, Vector2 anchor, Vector2 position, Vector2 size)
    {
        var tree = new VisualTree(source.rectTransform, parent);
        var label = tree.Get(source);
        label.gameObject.name = name;
        SetRect(label.rectTransform, anchor, position, size);
        label.gameObject.SetActive(true);
        label.enabled = true;
        label.richText = false;
        return label;
    }

    private static RectTransform Place(string name, Transform parent, Vector2 anchor, Vector2 position, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>();
        SetRect(rect, anchor, position, size);
        return rect;
    }

    private static void SetRect(RectTransform rect, Vector2 anchor, Vector2 position, Vector2 size)
    {
        rect.anchorMin = rect.anchorMax = anchor;
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        rect.localScale = Vector3.one;
        rect.localRotation = Quaternion.identity;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (root) { root.SetActive(false); Object.Destroy(root); }
        catalogue.Clear();
    }

    private sealed class BarView
    {
        private readonly RectTransform root, full, stamina, max, outline, overflow, extra, extraStamina, extraOutline;
        private readonly Image? backing, staminaGlow, extraGlow, extraIcon, rainbow;
        private readonly GameObject? shield, campfire;
        private readonly TMP_Text? morale;
        private readonly List<(RectTransform Rect, GameObject Root, Image? Icon, int Status, bool Petrify)> afflictions = new();
        private readonly float offset, minWidth, minAffliction;
        private readonly Color defaultBacking, depletedBacking;
        public string Diagnostic => $"bar-active={root.gameObject.activeInHierarchy}; full={full.sizeDelta}; full-active={full.gameObject.activeInHierarchy}; stamina={stamina.sizeDelta}; stamina-active={stamina.gameObject.activeInHierarchy}; outline={outline.sizeDelta}; outline-active={outline.gameObject.activeInHierarchy}";

        public BarView(StaminaBar source, Transform parent)
        {
            var sourceRoot = source.transform.parent as RectTransform;
            if (!sourceRoot) throw new InvalidOperationException("未找到 BarGroup");
            var tree = new VisualTree(sourceRoot!, parent);
            root = tree.Get(sourceRoot!);
            root.name = "Recorded native BarGroup";
            SetRect(root, Vector2.zero, new Vector2(70, 160), new Vector2(600, 200));
            root.pivot = new Vector2(0, .5f);
            full = tree.Get(source.fullBar);
            stamina = tree.Get(source.staminaBar); max = tree.Get(source.maxStaminaBar);
            outline = tree.Get(source.staminaBarOutline); overflow = tree.Get(source.staminaBarOutlineOverflowBar);
            extra = tree.Get(source.extraBar); extraStamina = tree.Get(source.extraBarStamina); extraOutline = tree.Get(source.extraBarOutline);
            backing = tree.TryGet(source.backing); staminaGlow = tree.TryGet(source.staminaGlow); extraGlow = tree.TryGet(source.extraStaminaGlow);
            extraIcon = tree.TryGet(source.extraStaminaIcon); rainbow = tree.TryGet(source.rainbowStamina);
            shield = tree.TryGet(source.shield); campfire = tree.TryGet(source.campfire); morale = tree.TryGet(source.moraleBoostText);
            if (morale) morale!.text = LocalizedText.GetText("MORALEBOOST");
            offset = source.staminaBarOffset; minWidth = source.minStaminaBarWidth; minAffliction = source.minAfflictionWidth;
            defaultBacking = source.defaultBackingColor; depletedBacking = source.outOfStaminaBackingColor;
            foreach (var status in sourceRoot!.GetComponentsInChildren<BarAffliction>(true))
                afflictions.Add((tree.Get(status.rtf), tree.Get(status.gameObject), tree.TryGet(status.icon), (int)status.afflictionType, status.isPetrify));
            foreach (var group in root.GetComponentsInChildren<CanvasGroup>(true)) { group.alpha = 1; group.interactable = false; group.blocksRaycasts = false; }
        }

        public void Apply(ReplayHudState? state)
        {
            root.gameObject.SetActive(state != null);
            if (state == null) return;
            float width = full.sizeDelta.x;
            SetWidth(stamina, ReplayHudLayout.NativeStaminaWidth(state.Stamina, width, offset));
            SetWidth(max, ReplayHudLayout.NativeStaminaWidth(state.MaxStamina, width, offset));
            stamina.gameObject.SetActive(stamina.sizeDelta.x > minWidth);
            max.gameObject.SetActive(max.sizeDelta.x > minWidth);
            SetWidth(outline, ReplayHudLayout.MainOutlineWidth(state.StatusSum, width));
            overflow.gameObject.SetActive(state.StatusSum > 1.005f);
            if (backing) backing!.color = state.Stamina <= .005f ? depletedBacking : defaultBacking;
            HideGlow(staminaGlow); HideGlow(extraGlow);
            bool petrify = false;
            foreach (var affliction in afflictions)
            {
                float amount = affliction.Petrify ? state.Petrify : affliction.Status >= 0 && affliction.Status < state.Afflictions.Length ? state.Afflictions[affliction.Status] : 0;
                float desired = ReplayHudLayout.AfflictionWidth(amount, width, minAffliction);
                SetWidth(affliction.Rect, desired);
                affliction.Root.SetActive(desired > 0);
                if (affliction.Icon) affliction.Icon!.transform.localScale = Vector3.one;
                if (affliction.Petrify && desired > 0) petrify = true;
            }
            bool showExtra = state.ExtraStamina > 0 || petrify;
            extra.gameObject.SetActive(showExtra);
            extra.sizeDelta = new Vector2(45, 45);
            SetWidth(extraStamina, Mathf.Max(6, state.ExtraStamina * width));
            extraStamina.gameObject.SetActive(extraStamina.sizeDelta.x > 6.1f);
            SetWidth(extraOutline, ReplayHudLayout.ExtraOutlineWidth(state.ExtraStamina, width, petrify));
            if (extraIcon) extraIcon!.gameObject.SetActive(extraStamina.gameObject.activeSelf);
            if (rainbow) { rainbow!.enabled = state.Rainbow; var color = rainbow.color; color.a = state.Rainbow ? 1 : 0; rainbow.color = color; }
            if (shield) shield!.SetActive(state.Invincible);
            if (campfire) campfire!.SetActive(!state.CanGetHungry);
            if (morale) morale!.enabled = state.MoraleKnown && state.MoraleBoost;
        }

        private static void SetWidth(RectTransform rect, float width) => rect.sizeDelta = new Vector2(width, rect.sizeDelta.y);
        private static void HideGlow(Image? image) { if (!image) return; var color = image!.color; color.a = 0; image.color = color; }
    }

    private sealed class SlotView
    {
        public readonly RectTransform Root;
        private readonly RectTransform selectedRect;
        private readonly RawImage icon;
        private readonly Image fill, outline;
        private readonly TMP_Text title;
        private readonly GameObject? counter, fuel;
        private readonly Image? fuelFill;
        private readonly TMP_Text? count;
        private readonly Vector2 size;
        private readonly Color fillColor, outlineColor;
        private readonly List<(TMP_Text Text, InputIcon Source)> inputs = new();
        private static readonly FieldInfo? DefaultColor = typeof(InventoryItemUI).GetField("defaultElementColor", BindingFlags.Instance | BindingFlags.NonPublic);

        public SlotView(InventoryItemUI source, Transform parent, Vector2 position, Vector2 size)
        {
            var sourceRoot = source.transform as RectTransform ?? throw new InvalidOperationException("缺少物品 UI 的 RectTransform");
            var tree = new VisualTree(sourceRoot, parent);
            Root = tree.Get(sourceRoot);
            selectedRect = tree.Get(source.rectTransform);
            SetRect(Root, new Vector2(1, .5f), position, size);
            icon = tree.Get(source.icon); fill = tree.Get(source.fill); outline = tree.Get(source.outline); title = tree.Get(source.nameText);
            counter = tree.TryGet(source.backpackFilledSlotsObject); count = tree.TryGet(source.backpackFilledSlotsAmountText); fuel = tree.TryGet(source.fuelBar);
            fuelFill = tree.TryGet(source.fuelBarFill);
            this.size = size;
            fillColor = DefaultColor?.GetValue(source) is Color original ? original : source.fill.color;
            outlineColor = fillColor;
            title.richText = false;
            foreach (var input in source.GetComponentsInChildren<InputIcon>(true))
            {
                var text = tree.TryGet(input.GetComponent<TMP_Text>());
                if (text) inputs.Add((text!, input));
                var hold = tree.TryGet(input.hold); if (hold) hold!.SetActive(false);
            }
            foreach (var group in Root.GetComponentsInChildren<CanvasGroup>(true)) { group.alpha = 1; group.interactable = false; group.blocksRaycasts = false; }
            Root.gameObject.SetActive(true);
        }

        public void BindInput(InputSpriteData.InputAction action, InputScheme scheme = InputScheme.KeyboardMouse)
        {
            foreach (var input in inputs)
            {
                try
                {
                    var assets = SingletonAsset<InputSpriteData>.Instance;
                    var mode = GameHandler.Instance.SettingsHandler.GetSetting<ControllerIconSetting>().Value;
                    TMP_SpriteAsset sprite;
                    if (scheme == InputScheme.KeyboardMouse || mode == ControllerIconSetting.IconMode.KBM)
                        sprite = input.Source.keyboardSprites ? input.Source.keyboardSprites : assets.keyboardSprites;
                    else if (mode == ControllerIconSetting.IconMode.Style2 || mode == ControllerIconSetting.IconMode.Auto &&
                        (InputHandler.GetGamepadType() == GamepadType.Dualshock || InputHandler.GetGamepadType() == GamepadType.Dualsense))
                        sprite = input.Source.ps5Sprites ? input.Source.ps5Sprites : assets.ps5Sprites;
                    else sprite = input.Source.xboxSprites ? input.Source.xboxSprites : assets.xboxSprites;
                    input.Text.spriteAsset = sprite;
                    input.Text.richText = true;
                    input.Text.text = assets.GetSpriteTag(action, scheme);
                    input.Text.enabled = scheme == InputScheme.Gamepad ? !input.Source.disableIfController : !input.Source.disableIfKeyboard;
                    input.Text.gameObject.SetActive(true);
                }
                catch { input.Text.enabled = false; }
            }
        }

        public void HideInput() { foreach (var input in inputs) input.Text.gameObject.SetActive(false); }

        public void Apply(InventoryFrame? state, Texture? texture, string? name, bool hideFuel, ItemFrame? held, int filledCount, bool temporarySlot)
        {
            bool nonempty = state != null && !state.Empty;
            bool selected = nonempty && (state!.Equipped || temporarySlot);
            selectedRect.sizeDelta = size * (selected ? ReplayHudLayout.SelectedSlotMultiplier : 1);
            fill.enabled = selected;
            fill.color = fillColor;
            fill.transform.localScale = Vector3.one;
            outline.color = outlineColor;
            icon.texture = texture;
            icon.enabled = nonempty && texture;
            icon.transform.localScale = Vector3.one;
            int cooked = state != null && state.Cooked >= 0 ? state.Cooked : held != null ? Mathf.RoundToInt(held.Cooked) : -1;
            icon.color = cooked >= 0 ? ItemCooking.GetCookColor(cooked) : Color.white;
            title.text = nonempty ? ItemLabel(name ?? "#" + state!.ItemId, cooked) : "";
            title.enabled = selected;
            title.gameObject.SetActive(true);
            if (counter) counter!.SetActive(nonempty && filledCount > 0);
            if (count) count!.text = filledCount.ToString();
            // PEAK's native gauge reads UseRemainingPercentage, not raw Fuel.
            // Older recordings retain -1, so no full-fuel state is fabricated.
            bool showFuel = nonempty && !hideFuel && state!.UiFuel >= 0 && state.UiFuel <= 1 && fuelFill;
            if (fuel) fuel!.SetActive(showFuel);
            if (fuelFill) fuelFill!.fillAmount = showFuel ? state!.UiFuel : 0;
        }
    }

    // Rebuild the native hierarchy from its visual components instead of
    // Instantiate(source): even an inactive prefab could gain new native Awake
    // behavior in a future game build. No game MonoBehaviour is ever created.
    private sealed class VisualTree
    {
        private readonly Dictionary<Object, Object> copies = new();
        private readonly List<(Component Source, Component Copy)> components = new();
        private RectTransform? buildRoot;
        private int nodes;

        public VisualTree(RectTransform source, Transform parent)
        {
            RectTransform? copiedRoot = null;
            try
            {
                copiedRoot = BuildNodes(source, parent);
                foreach (var pair in components)
                {
                    NativeUiComponentCopy.Copy(pair.Source, pair.Copy);
                    if (pair.Copy is Graphic graphic) graphic.raycastTarget = false;
                }
                foreach (var pair in components) RemapReferences(pair.Source, pair.Copy);
                // Activation belongs to the successfully bound view. If its
                // native reference shape changed, a partial clone stays hidden.
                copiedRoot.gameObject.SetActive(false);
            }
            catch
            {
                if (buildRoot) { buildRoot!.gameObject.SetActive(false); Object.Destroy(buildRoot.gameObject); }
                throw;
            }
        }

        private RectTransform BuildNodes(RectTransform source, Transform parent)
        {
            if (++nodes > 256) throw new InvalidOperationException("原版视觉树超出安全范围");
            var go = new GameObject(source.gameObject.name, typeof(RectTransform));
            go.SetActive(false);
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            if (!buildRoot) buildRoot = rect;
            else go.SetActive(source.gameObject.activeSelf);
            copies[source] = rect; copies[source.gameObject] = go;
            rect.anchorMin = source.anchorMin; rect.anchorMax = source.anchorMax; rect.pivot = source.pivot;
            rect.sizeDelta = source.sizeDelta; rect.anchoredPosition3D = source.anchoredPosition3D;
            rect.localRotation = source.localRotation; rect.localScale = source.localScale;
            foreach (var component in source.GetComponents<Component>())
            {
                if (!IsVisual(component))
                {
                    if (component is Graphic) throw new InvalidOperationException("未验证的原版图形控件：" + component.GetType().Name);
                    continue;
                }
                var cloned = go.AddComponent(component.GetType());
                copies[component] = cloned; components.Add((component, cloned));
            }
            foreach (Transform child in source)
                if (child is RectTransform childRect && !child.GetComponent<TMP_SubMeshUI>()) BuildNodes(childRect, rect);
            return rect;
        }

        private static bool IsVisual(Component component)
        {
            if (!component) return false;
            Type type = component.GetType();
            // Exact types keep a future gameplay subclass of Image or TMP from
            // quietly gaining Awake/OnEnable privileges in the replay overlay.
            return type == typeof(Image) || type == typeof(ProceduralImage) || type == typeof(RawImage) ||
                type == typeof(TextMeshProUGUI) || type == typeof(CanvasGroup) || type == typeof(Mask) || type == typeof(RectMask2D) ||
                type == typeof(HorizontalLayoutGroup) || type == typeof(VerticalLayoutGroup) || type == typeof(GridLayoutGroup) ||
                type == typeof(LayoutElement) || type == typeof(ContentSizeFitter) || type == typeof(AspectRatioFitter) ||
                type == typeof(FreeModifier) || type == typeof(UniformModifier) || type == typeof(RoundModifier) || type == typeof(OnlyOneEdgeModifier);
        }

        private void RemapReferences(Component source, Component copy)
        {
            for (Type? type = source.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.IsInitOnly || field.IsNotSerialized || (!field.IsPublic && !field.IsDefined(typeof(SerializeField), false))) continue;
                    if (!typeof(Object).IsAssignableFrom(field.FieldType)) continue;
                    if (!(field.GetValue(source) is Object original)) continue;
                    if (copies.TryGetValue(original, out var mapped)) field.SetValue(copy, mapped);
                    else if (original is Component || original is GameObject) field.SetValue(copy, null);
                }
        }

        public T Get<T>(T source) where T : Object => TryGet(source) ?? throw new InvalidOperationException("原版视觉控件引用无法映射：" + (source ? source.name : typeof(T).Name));
        public T? TryGet<T>(T? source) where T : Object => source && copies.TryGetValue(source!, out var copied) ? copied as T : null;
    }
}
