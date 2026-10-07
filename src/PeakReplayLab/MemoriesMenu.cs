using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Zorro.ControllerSupport;
using Zorro.UI.Modal;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

// A real main-menu entry using the game's artwork and fonts, not a persistent debug window.
internal sealed class MemoriesMenu : IDisposable
{
    private GameObject? canvas;
    private CanvasGroup? group;
    private Canvas? nativeCanvas;
    private Button? entry, linkedPlay;
    private Selectable? predecessor, savedPlayUp, savedPredecessorDown;
    private bool addedGroup, blocked, oldInteractable, oldRaycasts;
    private readonly Action open;
    private MainMenuMainPage? page;
    public MemoriesMenu(Action open) => this.open = open;
    public void Tick(bool overlay)
    {
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Title")
        { RestoreNavigation(); if (canvas && canvas.activeSelf) canvas.SetActive(false); Unblock(); return; }
        if (!page) page = Object.FindFirstObjectByType<MainMenuMainPage>();
        if (!page || !page.PlayButton || !page.isActiveAndEnabled)
        { RestoreNavigation(); if (canvas) canvas!.SetActive(false); Unblock(); return; }
        if (!canvas)
        {
            nativeCanvas = page.GetComponentInParent<Canvas>()?.rootCanvas;
            if (!nativeCanvas) return;
            canvas = new GameObject("PEAK Memories - menu entry", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster), typeof(LayoutElement));
            // Native controller navigation accepts only descendants of the current
            // page. A separate root canvas made this button an invalid selection.
            canvas.SetActive(false);
            canvas.transform.SetParent(page.transform, false);
            canvas.layer = page.gameObject.layer;
            canvas.GetComponent<LayoutElement>().ignoreLayout = true;
            var ownCanvas = canvas.GetComponent<Canvas>();
            ownCanvas.overrideSorting = true; ownCanvas.sortingOrder = 30;
            var go = Object.Instantiate(page.PlayButton.gameObject, canvas.transform, false);
            go.name = "MemoriesButton";
            // Prevent copied localization/click scripts from re-adding Play listeners in Start.
            // The staging parent is inactive, so copied OnEnable/Start cannot run first.
            foreach (var script in go.GetComponentsInChildren<MonoBehaviour>(true)) if (!(script is UIBehaviour)) script.enabled = false;
            entry = go.GetComponent<Button>();
            entry.onClick = new Button.ButtonClickedEvent();
            entry.onClick.AddListener(() => open()); entry.interactable = true;
            entry.navigation = new Navigation { mode = Navigation.Mode.Explicit };
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1, 0);
            rect.anchoredPosition = new Vector2(-50, 150); rect.sizeDelta = new Vector2(300, 66);
            rect.localScale = Vector3.one; rect.localRotation = Quaternion.identity;
            foreach (var label in go.GetComponentsInChildren<TMP_Text>(true))
            { label.text = "回忆录 · MEMORIES"; label.enableAutoSizing = true; label.fontSizeMin = 16; label.fontSizeMax = 30; }
            foreach (var label in go.GetComponentsInChildren<Text>(true)) label.text = "回忆录 · MEMORIES";
            go.SetActive(true);
        }
        PositionCanvas();
        canvas!.SetActive(!overlay);
        if (overlay) RestoreNavigation();
        if (overlay && !blocked)
        {
            group = page.GetComponent<CanvasGroup>(); addedGroup = !group;
            if (!group) group = page.gameObject.AddComponent<CanvasGroup>();
            oldInteractable = group!.interactable; oldRaycasts = group.blocksRaycasts;
            group.interactable = false; group.blocksRaycasts = false; blocked = true;
        }
        if (!overlay) { Unblock(); ConnectNavigation(); }
    }

    private void PositionCanvas()
    {
        if (!canvas || !page || !nativeCanvas) return;
        // Preserve the old overlay's matchWidthOrHeight=.5 screen coordinates,
        // while inheriting the native page's visibility and navigation scope.
        float scale = Mathf.Sqrt(Mathf.Max(1, Screen.width) / 1920f * (Mathf.Max(1, Screen.height) / 1080f));
        var rect = canvas!.GetComponent<RectTransform>();
        var root = (RectTransform)nativeCanvas!.transform;
        Vector3 parentScale = page!.transform.lossyScale;
        Vector3 worldScale = root.lossyScale * (scale / Mathf.Max(.0001f, nativeCanvas.scaleFactor));
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
        rect.sizeDelta = new Vector2(Screen.width / scale, Screen.height / scale);
        rect.position = root.TransformPoint(root.rect.center);
        rect.rotation = root.rotation;
        rect.localScale = new Vector3(worldScale.x / Mathf.Max(.0001f, Mathf.Abs(parentScale.x)),
            worldScale.y / Mathf.Max(.0001f, Mathf.Abs(parentScale.y)), 1);
    }

    private void ConnectNavigation()
    {
        if (!entry || !page || !page!.PlayButton) return;
        var play = page.PlayButton;
        // SetUpContinueButton finishes asynchronously and rewrites Play.up /
        // Quit.down. Reconnect from its new topology rather than a stale snapshot.
        if (linkedPlay != play || play.navigation.selectOnUp != entry)
        {
            RestoreNavigation();
            linkedPlay = play;
            var navigation = play.navigation;
            savedPlayUp = predecessor = navigation.selectOnUp;
            navigation.selectOnUp = entry; play.navigation = navigation;
        }
        if (predecessor)
        {
            var navigation = predecessor!.navigation;
            if (navigation.selectOnDown != entry)
            {
                savedPredecessorDown = navigation.selectOnDown;
                navigation.selectOnDown = entry; predecessor.navigation = navigation;
            }
        }
        entry!.navigation = new Navigation
        {
            mode = Navigation.Mode.Explicit,
            selectOnUp = predecessor && predecessor!.isActiveAndEnabled ? predecessor : play,
            selectOnDown = play,
        };
    }

    private void RestoreNavigation()
    {
        // Restore only edges that we still own: native continue/save logic or
        // another mod may have deliberately changed unrelated navigation.
        if (entry && linkedPlay)
        {
            var navigation = linkedPlay!.navigation;
            if (navigation.selectOnUp == entry) { navigation.selectOnUp = savedPlayUp; linkedPlay.navigation = navigation; }
        }
        if (entry && predecessor)
        {
            var navigation = predecessor!.navigation;
            if (navigation.selectOnDown == entry) { navigation.selectOnDown = savedPredecessorDown; predecessor.navigation = navigation; }
        }
        linkedPlay = null; predecessor = savedPlayUp = savedPredecessorDown = null;
    }
    private void Unblock()
    {
        bool wasBlocked = blocked;
        if (blocked && group)
        {
            group!.interactable = oldInteractable; group.blocksRaycasts = oldRaycasts;
            if (addedGroup) Object.Destroy(group);
        }
        group = null; blocked = false;
        if (wasBlocked && page && page!.isActiveAndEnabled && page.PlayButton && !Modal.IsOpen &&
            InputHandler.GetCurrentUsedInputScheme() != InputScheme.KeyboardMouse && EventSystem.current)
        {
            var selected = EventSystem.current.currentSelectedGameObject;
            if (!selected || !selected.activeInHierarchy || !selected.transform.IsChildOf(page.transform))
                EventSystem.current.SetSelectedGameObject(page.PlayButton.gameObject);
        }
    }
    public void Dispose()
    {
        RestoreNavigation();
        if (canvas) canvas!.SetActive(false);
        Unblock();
        if (canvas) Object.Destroy(canvas);
        canvas = null; entry = null; nativeCanvas = null; page = null;
    }
}
