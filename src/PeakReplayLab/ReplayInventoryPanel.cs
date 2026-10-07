using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace PeakReplayLab;

// IMGUI may repaint several times per game frame. Catalogue lookup, sorting and
// formatting are presentation work: cache them at 10 Hz, immediately on seek/focus.
internal static class ReplayInventoryPanel
{
    private static readonly ConditionalWeakTable<PlaybackSession, PanelState> panels = new();
    private static readonly GUILayoutOption[] RowHeight = { GUILayout.Height(20) };
    private static readonly GUILayoutOption[] UnknownWidth = { GUILayout.Width(180) };

    public static void Draw(PlaybackSession session, float width)
    {
        var panel = panels.GetValue(session, _ => new PanelState());
        panel.Refresh(session, width);
        GUILayout.BeginHorizontal();
        foreach (var slot in panel.Slots) DrawSlot(slot, panel);
        if (panel.Unknown) GUILayout.Label("库存尚未同步\n未知 ≠ 空背包", UnknownWidth);
        GUILayout.EndHorizontal();
        GUILayout.Label(panel.Held);
        GUILayout.Label(panel.Events);
        if (panel.Warning.Length != 0) GUILayout.Label(panel.Warning);
    }

    private static void DrawSlot(SlotView slot, PanelState panel)
    {
        Color previous = GUI.backgroundColor;
        if (slot.Equipped) GUI.backgroundColor = new Color(.65f, .9f, .5f);
        GUILayout.BeginVertical(GUI.skin.box, panel.CellOptions);
        GUI.backgroundColor = previous;
        GUILayout.Label(slot.Label, RowHeight);
        Rect image = GUILayoutUtility.GetRect(panel.CellWidth - 14, 42);
        if (slot.Icon) GUI.DrawTexture(image, slot.Icon!, ScaleMode.ScaleToFit, true);
        else GUI.Label(image, slot.Fallback);
        GUILayout.Label(slot.Title, RowHeight);
        GUILayout.EndVertical();
    }

    private sealed class SlotView
    {
        public bool Equipped;
        public Texture? Icon;
        public string Label = "", Fallback = "";
        public GUIContent Title = new();
    }

    private sealed class PanelState
    {
        private readonly List<InventoryFrame> sorted = new(8);
        private readonly List<SlotView> spare = new(8);
        private readonly Dictionary<int, (Texture? Icon, string Name)> catalogue = new();
        private readonly StringBuilder text = new();
        private string focus = "";
        private int revision = -1;
        private double nextRefresh;
        public readonly List<SlotView> Slots = new(8);
        public bool Unknown;
        public string Held = "", Events = "", Warning = "";
        public float CellWidth;
        public GUILayoutOption[] CellOptions = Array.Empty<GUILayoutOption>();

        public void Refresh(PlaybackSession session, float width)
        {
            float cellWidth = Mathf.Min(100, (width - 32) / 9);
            if (CellWidth != cellWidth)
            {
                CellWidth = cellWidth; CellOptions = new[] { GUILayout.Width(cellWidth), GUILayout.Height(100) };
            }
            double now = Time.unscaledTimeAsDouble;
            if (now < nextRefresh && revision == session.UiRevision && focus == session.FocusId) return;
            nextRefresh = now + .1; revision = session.UiRevision; focus = session.FocusId;
            var actor = session.FocusState;
            Unknown = actor != null && !actor.InventoryKnown;
            sorted.Clear();
            if (actor != null)
                foreach (var slot in actor.Inventory) if (slot.Slot != 250 || !slot.Empty) sorted.Add(slot);
            sorted.Sort(CompareSlots);
            foreach (var old in Slots) spare.Add(old);
            Slots.Clear();
            foreach (var slot in sorted)
            {
                SlotView view;
                if (spare.Count > 0) { view = spare[spare.Count - 1]; spare.RemoveAt(spare.Count - 1); }
                else view = new SlotView();
                var item = slot.Empty ? default : Item(slot.ItemId, "#" + slot.ItemId);
                view.Equipped = slot.Equipped; view.Icon = item.Icon;
                string label = slot.Backpack ? "包内 " + (slot.Slot + 1) : slot.Slot == 3 ? "背包" : slot.Slot == 250 ? "临时手持" : "栏位 " + (slot.Slot + 1);
                view.Label = (slot.Equipped ? "▸ " : "") + label;
                view.Fallback = slot.Empty ? "—" : "#" + slot.ItemId;
                string title = slot.Empty ? "空" : item.Name;
                if (slot.Uses >= 0 && !slot.Empty) title += " ×" + slot.Uses;
                view.Title.text = title; view.Title.tooltip = title; Slots.Add(view);
            }
            var held = session.HeldItem;
            string use = held == null ? "未持物" : "手持：" + Item(held.ItemId, held.Name).Name +
                (held.ProgressKnown ? (held.Primary || held.Secondary ? $" · 使用中 {held.Progress:P0}" : "") : " · 队友使用进度未同步");
            Held = use + $"    场景物品 {session.VisibleItemCount}/{session.RecordedItemCount} · 箱子 {session.RecordedCrateCount}";
            var events = session.RecentEvents(); text.Clear();
            for (int i = 0; i < events.Count; i++)
            {
                if (i > 0) text.Append("   ·   ");
                var e = events[i]; text.Append(e.T.ToString("F1")).Append("s ").Append(EventName(e.Kind)).Append(' ').Append(Item(e.ItemId, e.Name).Name);
            }
            Events = events.Count == 0 ? "物品事件：—" : text.ToString();
            Warning = string.IsNullOrEmpty(session.ObjectWarning) ? "" : "资源提示：" + session.ObjectWarning;
        }

        private static int CompareSlots(InventoryFrame a, InventoryFrame b)
        {
            int backpack = a.Backpack.CompareTo(b.Backpack); return backpack == 0 ? a.Slot.CompareTo(b.Slot) : backpack;
        }

        private (Texture? Icon, string Name) Item(int id, string fallback)
        {
            if (catalogue.TryGetValue(id, out var cached)) return cached;
            try
            {
                if (id >= 0 && id <= ushort.MaxValue && ItemDatabase.TryGetItem((ushort)id, out var item) && item)
                {
                    var result = ((Texture?)item.UIData?.icon, item.GetName());
                    if (catalogue.Count < 2048) catalogue[id] = result;
                    return result;
                }
            }
            catch { /* Catalogue/localization may still be loading. Retry only at UI cadence. */ }
            return (null, fallback);
        }
    }

    private static string EventName(string kind) => kind switch
    {
        "equip" => "拿出", "stash" => "收起", "drop" => "丢出", "consume" => "消耗",
        "use-primary-start" => "开始使用", "use-primary-finish" => "完成使用", "use-primary-cancel" => "中断使用",
        "use-secondary-start" => "开始副使用", "use-secondary-finish" => "完成副使用", "use-secondary-cancel" => "中断副使用",
        "use-charge" => "使用次数变化", "feed-start" => "开始喂食", "feed-end" => "结束喂食", _ => kind,
    };
}
