using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PeakReplayLab;

// One selectable owns both the click and the drag. The input module sends
// pointerClick before endDrag, so drag must cancel the click at its beginning.
internal sealed class NativeReplayDrawerHandle : Selectable, IPointerClickHandler,
    IInitializePotentialDragHandler, IBeginDragHandler, IDragHandler, IEndDragHandler, ISubmitHandler
{
    private RectTransform? pointerSpace;
    private ReplayDrawerMotion? motion;
    private Func<float>? travel;
    private Func<bool>? allowed;
    private Action? changed;
    private bool didDrag, pointed, selected, pointerKnown;
    private int pointerId;
    private float lastY, velocity;
    private double lastMove;
    public bool Hovered => pointed || selected;
    public bool PointerHovered => pointed;

    public void Configure(RectTransform space, ReplayDrawerMotion drawer, Func<float> distance, Func<bool> canUse, Action onChange)
    { pointerSpace = space; motion = drawer; travel = distance; allowed = canUse; changed = onChange; }

    private bool CanUse => IsActive() && IsInteractable() && allowed?.Invoke() == true;
    private bool TryY(PointerEventData data, out float y)
    {
        y = 0;
        if (!pointerSpace || !RectTransformUtility.ScreenPointToLocalPointInRectangle(pointerSpace, data.position, data.pressEventCamera, out var point)) return false;
        y = point.y; return true;
    }

    public override void OnPointerDown(PointerEventData data)
    {
        base.OnPointerDown(data);
        if (data.button != PointerEventData.InputButton.Left || !CanUse) return;
        didDrag = false; pointerId = data.pointerId; velocity = 0;
        pointerKnown = TryY(data, out lastY); lastMove = Time.unscaledTimeAsDouble;
    }

    public void OnInitializePotentialDrag(PointerEventData data) => data.useDragThreshold = true;

    public void OnBeginDrag(PointerEventData data)
    {
        if (data.button != PointerEventData.InputButton.Left || !CanUse || data.pointerId != pointerId) return;
        didDrag = true; data.eligibleForClick = false;
        if (!pointerKnown) { pointerKnown = TryY(data, out lastY); lastMove = Time.unscaledTimeAsDouble; }
        motion!.BeginDrag(); changed?.Invoke();
    }

    public void OnDrag(PointerEventData data)
    {
        if (data.button != PointerEventData.InputButton.Left || data.pointerId != pointerId || motion?.Dragging != true) return;
        if (!CanUse) { CancelDrag(); return; }
        if (!TryY(data, out float y)) return;
        double now = Time.unscaledTimeAsDouble;
        float delta = pointerKnown ? (y - lastY) / Mathf.Max(1, travel!()) : 0;
        double elapsed = now - lastMove;
        if (elapsed > .001) velocity = Mathf.Lerp(velocity, delta / (float)elapsed, .65f);
        motion.DragBy(delta);
        lastY = y; lastMove = now; pointerKnown = true; changed?.Invoke();
    }

    public void OnEndDrag(PointerEventData data)
    {
        if (data.button != PointerEventData.InputButton.Left || data.pointerId != pointerId || motion?.Dragging != true) return;
        motion.EndDrag(CanUse && Time.unscaledTimeAsDouble - lastMove <= .12 ? velocity : 0);
        pointerKnown = false; changed?.Invoke();
    }

    public void OnPointerClick(PointerEventData data)
    {
        if (data.button != PointerEventData.InputButton.Left || didDrag || !CanUse) return;
        motion!.Toggle(); changed?.Invoke();
    }

    public void OnSubmit(BaseEventData data)
    {
        if (!CanUse) return;
        motion!.Toggle(); changed?.Invoke();
    }

    public override void OnPointerEnter(PointerEventData data) { base.OnPointerEnter(data); pointed = true; }
    public override void OnPointerExit(PointerEventData data) { base.OnPointerExit(data); pointed = false; }
    public override void OnSelect(BaseEventData data) { base.OnSelect(data); selected = true; }
    public override void OnDeselect(BaseEventData data) { base.OnDeselect(data); selected = false; }

    public void CancelDrag()
    {
        if (motion?.Dragging == true) { motion.EndDrag(0); changed?.Invoke(); }
        pointerKnown = false; velocity = 0;
    }

    protected override void OnDisable()
    {
        CancelDrag(); pointed = selected = false;
        base.OnDisable();
    }
}
