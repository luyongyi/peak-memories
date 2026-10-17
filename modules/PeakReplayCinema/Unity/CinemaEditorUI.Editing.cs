using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PeakReplayLab.Cinema;

internal sealed partial class CinemaEditorUI
{
    // Both the key list and project list reuse four rows. The timeline uses a
    // fixed pool rather than allocating a button for every one of 2048 keys.
    private const int ListRows = 4, MarkerLimit = 160;
    private readonly List<TMP_InputField> inputs = new();
    private readonly List<CinemaKeyMarker> markers = new();
    private readonly List<Button> projectActions = new();
    private readonly List<TMP_InputField> poseInputs = new();
    private readonly List<Button> poseActions = new();
    private readonly Button[] keyRows = new Button[ListRows], projectRows = new Button[ListRows];
    private readonly TMP_Text[] keyRowLabels = new TMP_Text[ListRows], projectRowLabels = new TMP_Text[ListRows];
    private readonly int[] keyRowIndices = new int[ListRows], projectRowIndices = new int[ListRows];
    private Button undo = null!, redo = null!, keyPageBack = null!, keyPageNext = null!;
    private Button projectPageBack = null!, projectPageNext = null!, recovery = null!, confirmSave = null!;
    private TMP_InputField keyTime = null!, projectAlias = null!, projectTarget = null!;
    private TMP_Text keyPageLabel = null!, projectPageLabel = null!, formHint = null!, autosaveLabel = null!, confirmationText = null!;
    private RectTransform markerTrack = null!, confirmation = null!;
    private CinemaEditorState? lastState;
    private int keyPage, projectPage, lastFormRevision = int.MinValue, lastFormKey = int.MinValue;
    private int markerRevision = int.MinValue, markerSelection = int.MinValue;
    private int lastProjectSelection = int.MinValue;
    private float markerWidth = -1;
    private string localError = "";

    private void BuildKeyEditing(RectTransform left, RectTransform right)
    {
        var time = Row(left, "Exact key time", 0); Label(time, "时间 / 秒", 120);
        keyTime = Input(time, "key time", "秒（例如 12.5）", 230, project: false);
        poseActions.Add(Button(time, "移动到时间", () => ApplyKeyTime(CinemaEditorCommandKind.MoveKey), 180));
        poseActions.Add(Button(time, "复制到时间", () => ApplyKeyTime(CinemaEditorCommandKind.DuplicateKey), 180));
        BuildPoseRow(left, "位置", 52, new[] { "X", "Y", "Z" });
        BuildPoseRow(left, "角度", 104, new[] { "俯仰", "水平", "翻滚" });
        var lens = Row(left, "Exact key lens", 156);
        Label(lens, "视野 FOV", 120); poseInputs.Add(Input(lens, "key fov", "10–140°", 160, project: false));
        Label(lens, "对焦 / 米", 120); poseInputs.Add(Input(lens, "key focus", "0.05–100000", 160, project: false));
        var apply = Row(left, "Apply exact key pose", 208);
        poseActions.Add(Button(apply, "应用到选中关键帧", ApplyKeyPose, 280));
        formHint = Label(apply, "先选择关键帧，再输入数值。", 0, 18);

        for (int i = 0; i < ListRows; i++)
        {
            int row = i;
            var line = Row(right, "Keyframe list row " + i, i * 44);
            keyRows[i] = Button(line, "", () => Send(CinemaEditorCommandKind.SelectKey, keyRowIndices[row]), 0);
            Element(keyRows[i], 0, 1);
            keyRowLabels[i] = keyRows[i].GetComponentInChildren<TMP_Text>(true);
            keyRowLabels[i].alignment = TextAlignmentOptions.Left;
        }
        var browse = Row(right, "Keyframe list pages", 184);
        keyPageBack = Button(browse, "上一页", () => { keyPage = Math.Max(0, keyPage - 1); }, 145);
        keyPageLabel = Label(browse, "", 0, 20);
        keyPageNext = Button(browse, "下一页", () => keyPage++, 145);
        Label(Row(right, "Keyframe marker help", 228), "上方短线为关键帧：点击选择，拖动后松手修改时间。", 0, 18);
    }

    private void BuildPoseRow(RectTransform left, string label, float y, string[] axes)
    {
        var row = Row(left, "Exact key " + label, y); Label(row, label, 75);
        foreach (string axis in axes)
        {
            Label(row, axis, 52, 19);
            poseInputs.Add(Input(row, "key " + label + " " + axis, "数值", 150, project: false));
        }
    }

    private void BuildProjectEditing(RectTransform left, RectTransform right)
    {
        var current = Row(left, "Project management name", 0); Label(current, "方案名称", 120);
        projectAlias = Input(current, "management project name", "输入方案名称", 300);
        projectActions.Add(Button(current, "保存", () => ProjectAliasAction(CinemaEditorCommandKind.Save), 120));
        projectActions.Add(Button(current, "加载", () => ProjectAliasAction(CinemaEditorCommandKind.Load), 120));
        var target = Row(left, "Project copy or rename target", 52); Label(target, "新的名称", 120);
        projectTarget = Input(target, "new project name", "复制或重命名的目标名称", 420, project: false);
        var actions = Row(left, "Project management actions", 104);
        projectActions.Add(Button(actions, "复制为新方案", () => ProjectTargetAction(CinemaEditorCommandKind.CopyProject), 210));
        projectActions.Add(Button(actions, "重命名方案", () => ProjectTargetAction(CinemaEditorCommandKind.RenameProject), 210));
        projectActions.Add(Button(actions, "新建空方案", () => Send(CinemaEditorCommandKind.NewProject), 210));
        var automatic = Row(left, "Project recovery", 156);
        recovery = Button(automatic, "恢复自动副本", () => Send(CinemaEditorCommandKind.RestoreRecovery), 230);
        autosaveLabel = Label(automatic, "", 0, 18);
        Label(Row(left, "Project automatic recovery help", 208), "自动副本单独保存；退出或加载前可选择保存、放弃或取消。", 0, 18);

        for (int i = 0; i < ListRows; i++)
        {
            int row = i;
            var line = Row(right, "Project list row " + i, i * 44);
            projectRows[i] = Button(line, "", () => Send(CinemaEditorCommandKind.SelectProject, projectRowIndices[row]), 0);
            Element(projectRows[i], 0, 1);
            projectRowLabels[i] = projectRows[i].GetComponentInChildren<TMP_Text>(true);
            projectRowLabels[i].alignment = TextAlignmentOptions.Left;
        }
        var browse = Row(right, "Project list pages", 184);
        projectPageBack = Button(browse, "上一页", () => { projectPage = Math.Max(0, projectPage - 1); }, 120);
        projectPageLabel = Label(browse, "", 0, 19);
        projectPageNext = Button(browse, "下一页", () => projectPage++, 120);
        projectActions.Add(Button(browse, "刷新列表", () => Send(CinemaEditorCommandKind.RefreshProjects), 145));
        Label(Row(right, "Project selection help", 228), "点击列表选择名称，再点「加载」打开；每份录像各自管理方案。", 0, 18);
    }

    private void BuildKeyMarkers()
    {
        markerTrack = Rect("Cinema keyframe timeline markers", panel);
        for (int i = 0; i < MarkerLimit; i++)
        {
            var rect = Rect("Cinema keyframe marker " + i, markerTrack);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, .5f);
            rect.sizeDelta = new Vector2(15, 16);
            var graphic = Image(rect, Cream); graphic.raycastTarget = true;
            var marker = rect.gameObject.AddComponent<CinemaKeyMarker>();
            marker.targetGraphic = graphic; marker.colors = buttonColors;
            marker.navigation = new Navigation { mode = Navigation.Mode.None };
            marker.Configure(markerTrack, MarkerAllowed, SelectMarker, EndMarkerDrag);
            markers.Add(marker); marker.gameObject.SetActive(false);
        }
    }

    private void BuildConfirmation()
    {
        confirmation = Rect("Cinema unsaved changes confirmation", root!.transform); Stretch(confirmation);
        Image(confirmation, new Color(.08f, .06f, .04f, .72f)).raycastTarget = true;
        var card = Rect("Unsaved changes dialog", confirmation); Center(card); card.sizeDelta = new Vector2(1050, 238);
        Image(card, Brown).raycastTarget = true;
        var heading = Text("Unsaved changes heading", card, 30); heading.text = "方案有未保存的更改";
        Place(heading.rectTransform, 30, 22, 990, 42);
        confirmationText = Text("Unsaved changes explanation", card, 23);
        Place(confirmationText.rectTransform, 30, 78, 990, 38);
        var actions = Row(card, "Unsaved changes decisions", 154);
        actions.offsetMin += new Vector2(30, 0); actions.offsetMax -= new Vector2(30, 0);
        confirmSave = Button(actions, "保存并继续", () => Send(CinemaEditorCommandKind.ConfirmSave), 310);
        Button(actions, "放弃更改", () => Send(CinemaEditorCommandKind.ConfirmDiscard), 310);
        Button(actions, "取消", () => Send(CinemaEditorCommandKind.CancelPending), 310);
        confirmation.gameObject.SetActive(false);
    }

    private void RefreshEditing(CinemaEditorState state, bool committedText)
    {
        lastState = state;
        if (!group.interactable)
            foreach (var marker in markers)
                if (marker.Dragging) { marker.CancelDrag(); markerRevision = int.MinValue; }
        undo.interactable = state.CanUndo; redo.interactable = state.CanRedo;
        bool hasKey = state.KeyIndex >= 0 && state.KeyIndex < state.Keys.Count;
        foreach (var action in poseActions) action.interactable = hasKey;
        foreach (var input in poseInputs) input.interactable = hasKey;
        keyTime.interactable = hasKey;
        // Leaving focus keeps the staged numbers intact. Only selection or a
        // document revision reloads the form, and never while a field is focused.
        bool formFocused = keyTime.isFocused || poseInputs.Exists(input => input.isFocused);
        if (!committedText && !formFocused && (lastFormRevision != state.EditRevision || lastFormKey != state.KeyIndex))
        {
            lastFormRevision = state.EditRevision; lastFormKey = state.KeyIndex;
            keyTime.SetTextWithoutNotify(state.EditTime.ToString("0.######", Invariant));
            var pose = state.EditPose;
            Vector3 euler = new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W).eulerAngles;
            float[] components = { pose.Position.X, pose.Position.Y, pose.Position.Z, euler.x, euler.y, euler.z, pose.FieldOfView, pose.FocusDistance };
            for (int i = 0; i < poseInputs.Count; i++) poseInputs[i].SetTextWithoutNotify(components[i].ToString("0.######", Invariant));
            SetText(formHint, hasKey ? "修改后点「应用」，写入选中的关键帧。" : "先点击关键帧标记或右侧列表。" );
            localError = "";
        }
        if (!committedText && !projectAlias.isFocused && projectAlias.text != state.ProjectName)
            projectAlias.SetTextWithoutNotify(state.ProjectName);
        foreach (var action in projectActions) action.interactable = state.IdentityReady;
        recovery.interactable = state.IdentityReady && state.RecoveryAvailable && !state.AutosaveBusy;
        SetText(autosaveLabel, state.AutosaveBusy ? "正在保存自动副本…" : state.AutosaveStatus);
        RefreshKeyList(state); RefreshProjectList(state); RefreshMarkers(state);
        bool pending = state.HasPendingAction;
        if (confirmation.gameObject.activeSelf != pending) confirmation.gameObject.SetActive(pending);
        if (pending)
        {
            string action = state.PendingAction == "Close" ? "返回普通回放" : state.PendingAction == "Load" ? "加载另一方案" :
                state.PendingAction == "NewProject" ? "新建空方案" : state.PendingAction == "RestoreRecovery" ? "恢复自动副本" : "继续操作";
            SetText(confirmationText, action + "前，先保存当前方案「" + state.ActiveProjectName + "」的编辑内容？");
            confirmSave.interactable = state.IdentityReady;
        }
    }

    private void RefreshKeyList(CinemaEditorState state)
    {
        int pagesCount = Math.Max(1, (state.Keys.Count + ListRows - 1) / ListRows);
        keyPage = Math.Max(0, Math.Min(keyPage, pagesCount - 1));
        // A selection from the marker or previous/next key brings its row into view.
        if (state.KeyIndex != markerSelection && state.KeyIndex >= 0) keyPage = state.KeyIndex / ListRows;
        SetText(keyPageLabel, (keyPage + 1).ToString(Invariant) + " / " + pagesCount.ToString(Invariant));
        keyPageBack.interactable = keyPage > 0; keyPageNext.interactable = keyPage + 1 < pagesCount;
        for (int row = 0; row < ListRows; row++)
        {
            int index = keyPage * ListRows + row; keyRowIndices[row] = index;
            bool exists = index < state.Keys.Count;
            keyRows[row].gameObject.SetActive(exists);
            if (!exists) continue;
            var key = state.Keys[index];
            SetText(keyRowLabels[row], "  " + (index + 1).ToString(Invariant) + "    " + key.Time.ToString("0.000", Invariant) + " s    " + TransitionName(key.Transition));
            Selected(keyRows[row], index == state.KeyIndex);
        }
    }

    private void RefreshProjectList(CinemaEditorState state)
    {
        int pagesCount = Math.Max(1, (state.Projects.Length + ListRows - 1) / ListRows);
        projectPage = Math.Max(0, Math.Min(projectPage, pagesCount - 1));
        if (state.ProjectIndex != lastProjectSelection && state.ProjectIndex >= 0) projectPage = state.ProjectIndex / ListRows;
        lastProjectSelection = state.ProjectIndex;
        SetText(projectPageLabel, state.Projects.Length == 0 ? "暂无已保存方案" :
            (projectPage + 1).ToString(Invariant) + " / " + pagesCount.ToString(Invariant));
        projectPageBack.interactable = projectPage > 0; projectPageNext.interactable = projectPage + 1 < pagesCount;
        for (int row = 0; row < ListRows; row++)
        {
            int index = projectPage * ListRows + row; projectRowIndices[row] = index;
            bool exists = index < state.Projects.Length;
            projectRows[row].gameObject.SetActive(exists);
            if (!exists) continue;
            bool hasRecovery = Array.IndexOf(state.RecoveryProjects, state.Projects[index]) >= 0;
            SetText(projectRowLabels[row], "  " + state.Projects[index] + (hasRecovery ? "  [恢复副本]" : ""));
            Selected(projectRows[row], index == state.ProjectIndex);
            projectRows[row].interactable = state.IdentityReady;
        }
    }

    private void RefreshMarkers(CinemaEditorState state)
    {
        float width = markerTrack.rect.width;
        if (markerRevision == state.TrackRevision && markerSelection == state.KeyIndex && Mathf.Abs(markerWidth - width) < .1f) return;
        // Do not move or recycle the object owning the active drag.
        if (markers.Exists(marker => marker.Dragging)) return;
        markerRevision = state.TrackRevision; markerSelection = state.KeyIndex; markerWidth = width;
        var visible = new List<int>(MarkerLimit);
        int bins = Math.Max(1, Math.Min(MarkerLimit - 1, (int)(width / 15)));
        int previousBin = -1;
        for (int i = 0; i < state.Keys.Count && visible.Count < MarkerLimit - 1; i++)
        {
            int bin = Math.Min(bins - 1, (int)(state.Keys[i].Time / Math.Max(.001, state.Duration) * bins));
            if (bin == previousBin) continue;
            visible.Add(i); previousBin = bin;
        }
        if (state.KeyIndex >= 0 && state.KeyIndex < state.Keys.Count && !visible.Contains(state.KeyIndex)) visible.Add(state.KeyIndex);
        for (int slot = 0; slot < markers.Count; slot++)
        {
            var marker = markers[slot];
            bool exists = slot < visible.Count; marker.gameObject.SetActive(exists);
            if (!exists) continue;
            int index = visible[slot]; bool selected = index == state.KeyIndex;
            marker.Bind(index, state.TrackRevision, state.Duration);
            var rect = (RectTransform)marker.transform;
            rect.anchoredPosition = new Vector2((float)(state.Keys[index].Time / Math.Max(.001, state.Duration)) * width, 0);
            rect.sizeDelta = new Vector2(selected ? 18 : 12, selected ? 18 : 13);
            marker.targetGraphic.color = selected ? new Color(1, .72f, .32f) : Cream;
            if (selected) rect.SetAsLastSibling();
        }
    }

    private bool MarkerAllowed(int index, int revision) => !disposed && Application.isFocused && group.interactable && lastState != null &&
        !lastState.HasPendingAction && lastState.TrackRevision == revision && index >= 0 && index < lastState.Keys.Count;

    private void SelectMarker(int index) => Send(CinemaEditorCommandKind.SelectKey, index);

    private void EndMarkerDrag(int index, int revision, double time)
    {
        markerRevision = int.MinValue; // Reset even when focus or a modal cancels the gesture.
        if (!MarkerAllowed(index, revision) || lastState!.KeyIndex != index) return;
        Send(CinemaEditorCommandKind.MoveKey, time);
    }

    private void ApplyKeyTime(CinemaEditorCommandKind command)
    {
        if (!TryFinite(keyTime.text, out double value)) { localError = "时间必须是有限数值，例如 12.5。"; return; }
        if (value < 0 || lastState == null || value > lastState.Duration) { localError = "时间必须位于这份录像的时长范围内。"; return; }
        Send(command, value);
    }

    private void ApplyKeyPose()
    {
        var values = new string[poseInputs.Count];
        for (int i = 0; i < poseInputs.Count; i++)
        {
            if (!TryFinite(poseInputs[i].text, out double number)) { localError = "位置、角度、视野和对焦距离必须是有限数值。"; return; }
            values[i] = number.ToString("R", Invariant);
        }
        Send(CinemaEditorCommandKind.EditKeyPose, text: string.Join(",", values));
    }

    private void ProjectAliasAction(CinemaEditorCommandKind command)
    {
        Send(CinemaEditorCommandKind.SetProjectName, text: projectAlias.text);
        Send(command);
    }

    private void ProjectTargetAction(CinemaEditorCommandKind command)
    {
        string name = projectTarget.text.Trim();
        if (name.Length == 0) { localError = "请先输入新的方案名称。"; return; }
        Send(command, text: name);
    }

    private static bool TryFinite(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, Invariant, out value) && CinemaLimits.Finite(value);

    private static string TransitionName(CinemaTransition transition) => transition == CinemaTransition.Smooth ? "平滑" :
        transition == CinemaTransition.Linear ? "直线" : "硬切";
}

// Select once at pointer-down, update only a visual during dragging, and commit
// one MoveKey command on release. This gives one undo step for a whole gesture.
internal sealed class CinemaKeyMarker : Selectable, IPointerClickHandler, IInitializePotentialDragHandler,
    IBeginDragHandler, IDragHandler, IEndDragHandler
{
    private RectTransform? space;
    private Func<int, int, bool>? allowed;
    private Action<int>? select;
    private Action<int, int, double>? commit;
    private int index, revision, pointerId;
    private double duration, dragTime;
    private bool didDrag;
    public bool Dragging { get; private set; }

    public void Configure(RectTransform pointerSpace, Func<int, int, bool> canUse, Action<int> onSelect, Action<int, int, double> onCommit)
    { space = pointerSpace; allowed = canUse; select = onSelect; commit = onCommit; }
    public void Bind(int keyIndex, int trackRevision, double recordingDuration)
    { index = keyIndex; revision = trackRevision; duration = recordingDuration; }
    private bool CanUse => IsActive() && IsInteractable() && allowed?.Invoke(index, revision) == true;
    public override void OnPointerDown(PointerEventData data)
    {
        base.OnPointerDown(data);
        if (data.button != PointerEventData.InputButton.Left || !CanUse) return;
        pointerId = data.pointerId; didDrag = false;
        select?.Invoke(index);
    }
    public void OnInitializePotentialDrag(PointerEventData data) => data.useDragThreshold = true;
    public void OnBeginDrag(PointerEventData data)
    {
        if (data.button != PointerEventData.InputButton.Left || data.pointerId != pointerId || !CanUse) return;
        Dragging = didDrag = true; data.eligibleForClick = false; MoveVisual(data);
    }
    public void OnDrag(PointerEventData data)
    { if (Dragging && data.pointerId == pointerId && CanUse) MoveVisual(data); }
    private void MoveVisual(PointerEventData data)
    {
        if (!space || !RectTransformUtility.ScreenPointToLocalPointInRectangle(space, data.position, data.pressEventCamera, out var point)) return;
        float fraction = Mathf.Clamp01((point.x - space.rect.xMin) / Mathf.Max(1, space.rect.width));
        dragTime = fraction * duration;
        ((RectTransform)transform).anchoredPosition = new Vector2(fraction * space.rect.width, 0);
    }
    public void OnEndDrag(PointerEventData data)
    {
        if (!Dragging || data.pointerId != pointerId) return;
        Dragging = false;
        commit?.Invoke(index, revision, dragTime); // The owner validates and always resets the visual.
    }
    public void OnPointerClick(PointerEventData data)
    {
        // Selection happens at pointer-down so that the selected key is already
        // unambiguous when a drag begins. Drag cancels the following click.
        if (didDrag) data.eligibleForClick = false;
    }
    public void CancelDrag() { Dragging = false; didDrag = true; }
    protected override void OnDisable() { Dragging = didDrag = false; base.OnDisable(); }
}
