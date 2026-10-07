using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Reflection;
using Photon.Pun;
using UnityEngine;

namespace PeakReplayLab;

// The recording side only reads live objects. Never call GetData<T>: the game's getter
// creates missing entries, which would change the very inventory being observed.
internal sealed class ItemCapture : IDisposable
{
    private sealed class Identity
    {
        public Guid Guid;
        public string Key = "";
        public string Prefab = "";
        public string Name = "";
        public int ItemId = -1;
        public long Revision;
        public double MotionUntil;
        public PhotonView? BackpackView;
        public Character? BackpackOwner;
    }
    private sealed class Tracked
    {
        public Item Source = null!;
        public Transform Transform = null!;
        public Identity Identity = null!;
        public ItemSamplingPolicy Schedule = null!;
        public ItemFrame? Frame;
        public bool Attached;
        public bool DynamicChildren;
        public bool Mob;
        public long SeenRevision = -1;
        public BackpackPlacement? Backpack;
    }
    private sealed class BackpackPlacement
    {
        public Character Owner = null!;
        public PhotonView View = null!;
        public Transform SlotTransform = null!;
        public int Slot, ItemId, BackpackType;
        public Guid Instance, BackpackInstance;
        public InventoryFrame[] Inventory = Array.Empty<InventoryFrame>();
    }
    private sealed class InventoryCache
    {
        public InventoryFrame[] Frames = Array.Empty<InventoryFrame>();
        public bool Known;
        public long Revision = -1;
        public double NextRead;
        public int Selected = -2;
    }
    private static readonly ConditionalWeakTable<Item, Identity> identities = new();
    private static readonly ConditionalWeakTable<Character, InventoryCache> inventoryCache = new();
    private static readonly Dictionary<Type, bool> updatingTypes = new();
    private static long registryRevision;
    private static long inventoryRevision;
    internal const int MaximumItems = 1024;
    private readonly Action<string>? warning;
    private readonly HashSet<string> warned = new(StringComparer.Ordinal);
    private Dictionary<int, Tracked> tracked = new();
    private Tracked[] ordered = Array.Empty<Tracked>();
    private readonly List<ItemFrame> frames = new();
    private ItemFrame[] previousArray = Array.Empty<ItemFrame>();
    private long seenRegistry = -1;
    private double nextRegistry;

    public ItemCapture(Action<string>? warning = null) => this.warning = warning;

    public ItemFrame[] Capture()
    {
        double now = Time.timeAsDouble;
        if (seenRegistry != registryRevision || now >= nextRegistry) RefreshRegistry(now);
        frames.Clear();
        foreach (var entry in ordered)
        {
            // Stationary unowned objects perform only managed version/deadline checks here.
            // No Transform, name, Rigidbody, Renderer or scene getter on their cold path.
            var identity = entry.Identity;
            try
            {
                // These cheap lifecycle/attachment checks deliberately remain at the
                // capture rate. A destroyed/transferred/taken-out item must not wait
                // for the slow metadata probe, even if a native hook was missed.
                bool fixedPlacement = entry.Backpack != null && SameBackpackPlacement(entry.Source!, entry.Backpack);
                bool attachmentChanged = entry.Backpack != null && !fixedPlacement;
                // A few native luminous items flicker independently of their physical
                // motion. Keep those sampled at the capture rate; other cold items
                // still perform only managed scheduling checks.
                bool luminous = entry.Frame?.Lights?.Length > 0;
                bool appearanceDue = entry.Frame != null && NativeVisualAppearance.NeedsCaptureAtTime(entry.Transform, entry.Frame.Visuals, now, entry.Frame.Pose.Active, entry.DynamicChildren || entry.Mob);
                bool hot = entry.Attached || entry.Mob && entry.Frame?.Cooked < 1 || now < identity.MotionUntil || luminous;
                if (!luminous && !appearanceDue && !entry.Schedule.ShouldRead(now, identity.Revision, hot, fixedPlacement, attachmentChanged))
                { if (entry.Frame != null) frames.Add(entry.Frame); continue; }
                var item = entry.Source;
                if (!item || !item.gameObject.activeInHierarchy)
                { entry.Frame = null; entry.Attached = false; entry.Backpack = null; entry.Schedule.Observed(now, identity.Revision, false); continue; }
                string key = Key(item);
                bool attached = item.itemState != ItemState.Ground;
                var backpack = KnownBackpackPlacement(item, key, fixedPlacement ? entry.Backpack : null);
                bool keepPlacement = fixedPlacement && backpack != null;
                bool dirty = identity.Revision != entry.SeenRevision || attachmentChanged ||
                    (entry.Backpack == null) != (backpack == null);
                var old = entry.Frame;
                bool children = entry.Schedule.SampleChildren(now, dirty, backpack == null && (attached || entry.Mob), entry.DynamicChildren);
                var sample = Read(item, key, old, children, keepPlacement ? old?.Pose : null, now, dirty, entry.DynamicChildren || entry.Mob);
                bool moving = backpack == null && old != null && RootMoved(old.Pose, sample.Pose);
                if (backpack == null && item.rig && !item.rig.isKinematic)
                    moving |= item.rig.linearVelocity.sqrMagnitude > .0001f || item.rig.angularVelocity.sqrMagnitude > .0001f;
                entry.Frame = sample; entry.Attached = attached; entry.Backpack = backpack; entry.SeenRevision = identity.Revision;
                entry.Schedule.Observed(now, identity.Revision, moving, backpack != null);
                frames.Add(sample);
            }
            catch (Exception e)
            {
                entry.Schedule.Observed(now, identity.Revision, false);
                // A transient read failure is not a destruction event. Retain the
                // immutable checkpoint while the actual source remains alive.
                if (entry.Frame != null && entry.Source && entry.Source.gameObject.activeInHierarchy) frames.Add(entry.Frame);
                Warn("read", "有一个物品尚未准备好，保留上一有效状态：" + e.Message);
            }
        }
        bool same = frames.Count == previousArray.Length;
        if (same) for (int i = 0; i < frames.Count; i++) if (!ReferenceEquals(frames[i], previousArray[i])) { same = false; break; }
        if (!same) previousArray = frames.ToArray();
        return previousArray;
    }

    private static bool WornBackpack(Character owner, out BackpackSlot? backpack)
    {
        backpack = null;
        if (!owner || !owner.IsPlayerControlled || !owner.gameObject.activeInHierarchy || !owner.refs?.hip ||
            !owner.refs.animator || !owner.refs.items || !owner.player) return false;
        backpack = owner.player.backpackSlot;
        int type = backpack == null ? 0 : (int)backpack.backpackType;
        return backpack != null && !backpack.IsEmpty() && type >= 1 && type <= 4 &&
            (!owner.refs.items.currentSelectedSlot.IsSome || owner.refs.items.currentSelectedSlot.Value != 3);
    }

    private static bool SameBackpackPlacement(Item item, BackpackPlacement placement)
    {
        if (!item || !item.gameObject.activeInHierarchy || item.itemState != ItemState.InBackpack ||
            !item.backpackReference.IsSome || !item.backpackSlotTransform ||
            item.backpackSlotTransform != placement.SlotTransform || item.itemID != placement.ItemId ||
            item.data?.guid != placement.Instance) return false;
        var (slot, reference) = item.backpackReference.Value;
        if (slot != placement.Slot || reference.type != BackpackReference.BackpackType.Equipped ||
            !reference.view || reference.view != placement.View || !WornBackpack(placement.Owner, out var backpack) ||
            (int)backpack!.backpackType != placement.BackpackType || backpack.data?.guid != placement.BackpackInstance) return false;
        // Actor capture already refreshes this cache before item capture. A slot
        // inventory change is an immediate boundary, not a half-second delay.
        return inventoryCache.TryGetValue(placement.Owner, out var inventory) && inventory.Known &&
            ReferenceEquals(inventory.Frames, placement.Inventory);
    }

    private static BackpackPlacement? KnownBackpackPlacement(Item item, string key, BackpackPlacement? previous)
    {
        if (previous != null) return previous; // SameBackpackPlacement checked it this tick.
        if (item.itemState != ItemState.InBackpack || !item.backpackReference.IsSome || !item.backpackSlotTransform) return null;
        var (slot, reference) = item.backpackReference.Value;
        if (reference.type != BackpackReference.BackpackType.Equipped || !reference.view) return null;
        var identity = identities.GetValue(item, _ => new Identity());
        if (identity.BackpackView != reference.view || !identity.BackpackOwner)
        { identity.BackpackView = reference.view; identity.BackpackOwner = reference.view.GetComponent<Character>(); }
        var owner = identity.BackpackOwner;
        if (!owner || !WornBackpack(owner!, out var backpack) ||
            !inventoryCache.TryGetValue(owner!, out var inventory) ||
            !BackpackAttachmentPolicy.IsKnownEquippedSlot(key, item.itemID, slot, inventory.Known, inventory.Frames)) return null;
        return new BackpackPlacement
        {
            Owner = owner!, View = reference.view, Slot = slot, SlotTransform = item.backpackSlotTransform,
            ItemId = item.itemID, Instance = item.data.guid, BackpackType = (int)backpack!.backpackType,
            BackpackInstance = backpack.data?.guid ?? Guid.Empty, Inventory = inventory.Frames,
        };
    }

    private void RefreshRegistry(double now)
    {
        nextRegistry = now + 2; seenRegistry = registryRevision;
        var next = new Dictionary<int, Tracked>();
        // Keep sleeping/inactive entities in the registry, not only ALL_ACTIVE_ITEMS.
        foreach (var item in Item.ALL_ITEMS)
        {
            if (!item || !item.gameObject.scene.IsValid() || !item.gameObject.scene.isLoaded) continue;
            if (next.Count >= MaximumItems) { Warn("count", "物品轨道达到 1024 个实体上限；超出的物品未记录。"); break; }
            int id = item.GetInstanceID();
            if (!tracked.TryGetValue(id, out var entry) || entry.Source != item)
                entry = new Tracked { Source = item, Transform = item.transform, Identity = identities.GetValue(item, _ => new Identity()),
                    Schedule = new ItemSamplingPolicy(id), DynamicChildren = DynamicVisual(item), Mob = item is MobItem };
            next[id] = entry;
        }
        tracked = next; ordered = next.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
    }

    private static bool DynamicVisual(Item item)
    {
        if (item.GetComponentsInChildren<Animator>(true).Length > 0 || item.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length > 0) return true;
        foreach (var behaviour in item.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (!behaviour || behaviour is Item) continue;
            Type type = behaviour.GetType();
            if (!updatingTypes.TryGetValue(type, out bool updates))
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                updates = type.GetMethod("Update", flags) != null || type.GetMethod("LateUpdate", flags) != null;
                updatingTypes[type] = updates;
            }
            if (updates) return true;
        }
        return false;
    }

    private static bool RootMoved(ObjectPose a, ObjectPose b)
    {
        for (int i = 0; i < 3; i++) if (Math.Abs(a.Position[i] - b.Position[i]) > .0001f) return true;
        for (int i = 0; i < 4; i++) if (Math.Abs(a.Rotation[i] - b.Rotation[i]) > .0001f) return true;
        return false;
    }

    internal static void MarkDirty(Item item, bool hierarchyChanged = false)
    {
        if (!item) return;
        var identity = identities.GetValue(item, _ => new Identity());
        identity.Revision++; identity.MotionUntil = Time.timeAsDouble + .25;
        VisualReplica.InvalidateCapture(item.transform, hierarchyChanged);
        MarkInventoryDirty();
    }
    internal static void MarkMotion(Item item)
    { if (item) identities.GetValue(item, _ => new Identity()).MotionUntil = Time.timeAsDouble + .25; }
    internal static void RegistryChanged(Item item)
    { registryRevision++; if (item) MarkDirty(item, true); }
    internal static void MarkInventoryDirty() => inventoryRevision++;

    internal static string Key(Item item)
    {
        var identity = identities.GetValue(item, _ => new Identity());
        Guid guid = item.data?.guid ?? Guid.Empty;
        // Equipping/drop replaces a Photon entity; old/new can briefly coexist with the same
        // data GUID. The instance suffix is a recording identity, never a prefab lookup key.
        if (identity.Key.Length == 0 || identity.Guid != guid)
        {
            identity.Guid = guid;
            identity.Key = (guid == Guid.Empty ? "anonymous" : guid.ToString("N")) + "/" + item.GetInstanceID().ToString(CultureInfo.InvariantCulture);
        }
        return identity.Key;
    }

    internal static string Name(Item item) => item.UIData?.itemName ?? item.name ?? "";

    private static ItemFrame Read(Item item, string key, ItemFrame? old, bool children, ObjectPose? fixedPlacement, double now, bool dirty, bool animatedAppearance)
    {
        var holder = item.trueHolderCharacter;
        var identity = identities.GetValue(item, _ => new Identity());
        if (identity.ItemId != item.itemID)
        {
            identity.Prefab = item.name.Replace("(Clone)", "").Trim();
            if (ItemDatabase.TryGetItem(item.itemID, out var canonical) && canonical) identity.Prefab = canonical.name;
            identity.Name = Name(item); identity.ItemId = item.itemID;
        }
        // Native InBackpack explicitly clears trueHolderCharacter. Preserve the
        // equipped pack owner, not a stale hand owner, without invoking GetData.
        if (item.itemState == ItemState.InBackpack && item.backpackReference.IsSome)
        {
            var reference = item.backpackReference.Value.Item2;
            if (reference.type == BackpackReference.BackpackType.Equipped && reference.view)
            {
                if (identity.BackpackView != reference.view || !identity.BackpackOwner)
                { identity.BackpackView = reference.view; identity.BackpackOwner = reference.view.GetComponent<Character>(); }
                holder = identity.BackpackOwner;
            }
            else holder = null;
        }
        string holderId = holder ? global::PeakReplayLab.Capture.Id(holder) : "";
        var lights = NativeLightAppearance.Capture(item.transform, old?.Lights);
        if (!ReferenceEquals(old?.Lights, lights)) children = true;
        // Stable equipped contents need no Transform reads on their cold path.
        // A notified/slow child-visual update keeps the original placement while
        // preserving actual visibility and child changes for playback.
        var pose = fixedPlacement == null ? VisualReplica.Capture(item.transform, children) :
            children ? BackpackCapturePolicy.KeepPlacement(fixedPlacement, VisualReplica.Capture(item.transform, true)) : fixedPlacement;
        bool progressKnown = item.itemState == ItemState.Held && holder && holder.IsLocal;
        // Remote CharacterSyncData does not contain use-input/progress. A remote zero
        // must not masquerade as a known idle state; network events are recorded separately.
        bool primary = progressKnown && item.isUsingPrimary, secondary = progressKnown && item.isUsingSecondary;
        float progress = progressKnown ? Mathf.Clamp01(Finite(item.progress, 0)) : 0;
        int uses = Int(item.data, DataEntryKey.ItemUses, -1), cooked = Int(item.data, DataEntryKey.CookedAmount, 0);
        float fuel = Number(item.data, DataEntryKey.Fuel, -1);
        bool used = Flag(item.data, DataEntryKey.Used), lit = Flag(item.data, DataEntryKey.FlareActive) || Flag(item.data, DataEntryKey.PowerEnabled);
        bool revealBackpack = item.itemState == ItemState.InBackpack && item.backpackReference.IsSome &&
            item.backpackReference.Value.Item2.view && item.backpackReference.Value.Item2.IsOnMyBack();
        var color = Color.white;
        if (Entry(item.data, DataEntryKey.Color) is ColorItemData tint)
        {
            var c = tint.Value;
            color = new Color(Finite(c.r, 1), Finite(c.g, 1), Finite(c.b, 1), Finite(c.a, 1));
        }
        bool appearanceChanged = dirty || old == null || old.Cooked != cooked || old.Lit != lit || old.Used != used ||
            old.Color[0] != color.r || old.Color[1] != color.g || old.Color[2] != color.b || old.Color[3] != color.a;
        var visuals = NativeVisualAppearance.CaptureAtTime(item.transform, old?.Visuals, now, appearanceChanged, allowAnimated: animatedAppearance);
        if (old != null && old.ItemId == item.itemID && old.Prefab == identity.Prefab && old.Name == identity.Name &&
            old.HolderId == holderId && old.State == (int)item.itemState && old.Primary == primary && old.Secondary == secondary &&
            old.Progress == progress && old.ProgressKnown == progressKnown && old.Uses == uses && old.Fuel == fuel &&
            old.Cooked == cooked && old.Used == used && old.Lit == lit && old.RevealBackpackContents == revealBackpack && old.Color[0] == color.r && old.Color[1] == color.g &&
            old.Color[2] == color.b && old.Color[3] == color.a && ReferenceEquals(old.Visuals, visuals) && ReferenceEquals(old.Lights, lights) && VisualReplica.Same(old.Pose, pose)) return old;
        return new ItemFrame { Key = key, ItemId = item.itemID, Prefab = identity.Prefab, Name = identity.Name, HolderId = holderId,
            State = (int)item.itemState, Pose = pose, ProgressKnown = progressKnown, Primary = primary, Secondary = secondary,
            Progress = progress, Uses = uses, Fuel = fuel, Cooked = cooked, Used = used, Lit = lit, RevealBackpackContents = revealBackpack,
            Color = new[] { color.r, color.g, color.b, color.a }, Visuals = visuals, Lights = lights };
    }

    public static InventoryFrame[] Inventory(Character c, out bool known)
    {
        if (c == null) { known = false; return Array.Empty<InventoryFrame>(); }
        var cached = inventoryCache.GetValue(c, _ => new InventoryCache());
        var items = c.refs?.items;
        int selected = items != null && items.currentSelectedSlot.IsSome ? items.currentSelectedSlot.Value : -1;
        double now = Time.timeAsDouble;
        if (!ItemSamplingPolicy.NeedsInventory(now, cached.NextRead, inventoryRevision, cached.Revision, selected, cached.Selected))
        { known = cached.Known; return cached.Frames; }
        var values = ReadInventory(c, out known);
        bool same = values.Length == cached.Frames.Length;
        if (same) for (int i = 0; i < values.Length; i++) if (!SameSlot(values[i], cached.Frames[i])) { same = false; break; }
        if (!same) cached.Frames = values;
        cached.Known = known; cached.Revision = inventoryRevision; cached.Selected = selected; cached.NextRead = now + .5;
        return cached.Frames;
    }

    private static bool SameSlot(InventoryFrame a, InventoryFrame b) => a.Empty == b.Empty && a.Slot == b.Slot && a.ItemId == b.ItemId &&
        a.Instance == b.Instance && a.Equipped == b.Equipped && a.Backpack == b.Backpack && a.Uses == b.Uses && a.Fuel == b.Fuel &&
        a.UiFuel == b.UiFuel && a.Cooked == b.Cooked;

    private static InventoryFrame[] ReadInventory(Character c, out bool known)
    {
        known = false;
        try
        {
            if (c == null) return Array.Empty<InventoryFrame>();
            var player = c.player;
            if (player == null || player.itemSlots == null || player.itemSlots.Length < 3 || player.backpackSlot == null || player.tempFullSlot == null)
                return Array.Empty<InventoryFrame>();
            known = PhotonNetwork.IsMasterClient || ItemEventCapture.InventoryKnown(player);
            if (!known) return Array.Empty<InventoryFrame>();
            var items = c.refs?.items;
            int selected = items != null && items.currentSelectedSlot.IsSome ? items.currentSelectedSlot.Value : -1;
            var values = new List<InventoryFrame>(9);
            for (int i = 0; i < 3; i++) values.Add(Slot(player.itemSlots[i], i, false, selected));
            values.Add(Slot(player.backpackSlot, 3, false, selected));
            values.Add(Slot(player.tempFullSlot, 250, false, selected));
            if (!player.backpackSlot.IsEmpty())
            {
                var backpackVisuals = c.refs?.backpackHandler?.activeBackpackVisuals;
                int capacity = backpackVisuals != null ? Math.Max(0, Math.Min(4, backpackVisuals.slotCount)) :
                    player.backpackSlot.prefab is Backpack prefabBackpack ? Math.Max(0, Math.Min(4, prefabBackpack.slotCount)) : -1;
                // Some wearable variants have no storage. Their absent BackpackData is
                // not an unknown inventory; use the actual game's slot count, not names.
                if (capacity == 0) return values.ToArray();
                if (!(Entry(player.backpackSlot.data, DataEntryKey.BackpackData) is BackpackData bag) || bag.itemSlots == null)
                { known = false; return values.ToArray(); }
                int count = Math.Min(capacity < 0 ? 4 : capacity, bag.itemSlots.Length);
                for (int i = 0; i < count; i++) values.Add(Slot(bag.itemSlots[i], i, true, -1));
            }
            return values.ToArray();
        }
        catch { known = false; return Array.Empty<InventoryFrame>(); }
    }

    private static InventoryFrame Slot(ItemSlot? slot, int id, bool backpack, int selected)
    {
        var result = new InventoryFrame { Slot = id, Backpack = backpack, Equipped = !backpack && selected == id };
        if (slot == null || slot.IsEmpty()) return result;
        Item? prefab = slot.prefab;
        if (!prefab && slot is BackpackSlot bag) ItemDatabase.TryGetItem(bag.GetPrefabName(), out prefab);
        if (prefab == null) throw new InvalidOperationException("Nonempty inventory slot has no known prefab.");
        result.Empty = false;
        result.ItemId = prefab.itemID;
        result.Instance = slot.data != null && slot.data.guid != Guid.Empty ? slot.data.guid.ToString("N") : "";
        result.Uses = Int(slot.data, DataEntryKey.ItemUses, -1);
        result.Fuel = Number(slot.data, DataEntryKey.Fuel, -1);
        result.UiFuel = Entry(slot.data, DataEntryKey.UseRemainingPercentage) is FloatItemData uiFuel ? Finite(uiFuel.Value, -1) : -1;
        result.Cooked = Entry(slot.data, DataEntryKey.CookedAmount) is IntItemData cooked ? cooked.Value : -1;
        return result;
    }

    private static DataEntryValue? Entry(ItemInstanceData? data, DataEntryKey key) =>
        data?.data != null && data.data.TryGetValue(key, out var value) ? value : null;
    private static int Int(ItemInstanceData? data, DataEntryKey key, int fallback) => Entry(data, key) switch
    {
        IntItemData value => value.Value,
        OptionableIntItemData value when value.HasData => value.Value,
        _ => fallback,
    };
    private static float Number(ItemInstanceData? data, DataEntryKey key, float fallback) => Entry(data, key) switch
    {
        FloatItemData value => Finite(value.Value, fallback),
        IntItemData value => value.Value,
        _ => fallback,
    };
    private static bool Flag(ItemInstanceData? data, DataEntryKey key) => Entry(data, key) switch
    {
        BoolItemData value => value.Value,
        OptionableBoolItemData value => value.HasData && value.Value,
        _ => false,
    };
    private static float Finite(float value, float fallback) => float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
    private void Warn(string key, string message) { if (warned.Add(key)) warning?.Invoke(message); }
    public void Dispose() { tracked.Clear(); ordered = Array.Empty<Tracked>(); frames.Clear(); previousArray = Array.Empty<ItemFrame>(); warned.Clear(); }
}

internal sealed class ItemPlayback : IDisposable
{
    private sealed class Visual : IDisposable
    {
        public readonly VisualReplica Replica;
        private readonly List<(Renderer Renderer, int Slot, Color Tint)> tints = new();
        private readonly MaterialPropertyBlock block = new();
        private readonly Vector3 centerOfMass;
        private readonly NativeVisualAppearance.Playback appearance;
        private readonly NativeLightAppearance.Playback lighting;
        private readonly bool supportsCooking;
        private bool hadRecordedAppearance;
        private float lastCooked = float.NaN;
        public long LastSeen;
        public Visual(Item prefab)
        {
            Replica = new VisualReplica(prefab.gameObject, shareImmutableMaterials: true);
            appearance = new NativeVisualAppearance.Playback(Replica, prefab.gameObject);
            lighting = new NativeLightAppearance.Playback(Replica, prefab.gameObject);
            supportsCooking = prefab.GetComponent<ItemCooking>();
            var body = prefab.GetComponent<Rigidbody>();
            centerOfMass = body ? body.centerOfMass : prefab.centerOfMass;
            foreach (var renderer in Replica.Renderers)
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                    if (materials[i] && materials[i].HasProperty("_Tint")) tints.Add((renderer, i, materials[i].GetColor("_Tint")));
            }
        }
        public void Apply(ItemFrame a, ItemFrame? b, float mix, Transform? backpackSlot)
        {
            b = BackpackAttachmentPolicy.CanInterpolate(a, b) ? b : null;
            var root = Replica.Root.transform;
            // Detach before the world-pose path resumes; no bag/hand interpolation
            // is allowed to leak across the recorded state boundary.
            if (!backpackSlot && root.parent) { root.SetParent(null, true); Replica.InvalidatePose(); }
            Replica.Apply(a.Pose, b?.Pose, mix, a.RevealBackpackContents);
            if (backpackSlot)
            {
                if (root.parent != backpackSlot) root.SetParent(backpackSlot, false);
                Vector3 parentScale = backpackSlot!.lossyScale;
                root.SetLocalPositionAndRotation(new Vector3(
                    BackpackAttachmentPolicy.LocalOffset(centerOfMass.x, parentScale.x),
                    BackpackAttachmentPolicy.LocalOffset(centerOfMass.y, parentScale.y),
                    BackpackAttachmentPolicy.LocalOffset(centerOfMass.z, parentScale.z)), Quaternion.identity);
                Vector3 aScale = new(a.Pose.Scale[0], a.Pose.Scale[1], a.Pose.Scale[2]);
                Vector3 bScale = b == null ? aScale : new Vector3(b.Pose.Scale[0], b.Pose.Scale[1], b.Pose.Scale[2]);
                root.localScale = Vector3.Scale(Vector3.Lerp(aScale, bScale, mix), new Vector3(
                    BackpackAttachmentPolicy.InverseScale(parentScale.x), BackpackAttachmentPolicy.InverseScale(parentScale.y),
                    BackpackAttachmentPolicy.InverseScale(parentScale.z)));
            }
            if (a.Visuals == null)
            {
                appearance.Apply(null);
                if (hadRecordedAppearance) lastCooked = float.NaN;
            }
            if (a.Visuals == null && supportsCooking && lastCooked != a.Cooked)
            {
                lastCooked = a.Cooked;
                Color cooked = ItemCooking.GetCookColor((int)a.Cooked);
                foreach (var entry in tints)
                {
                    // This fallback is for older recordings. Current recordings carry
                    // the native effective tint, which already includes cooking.
                    entry.Renderer.GetPropertyBlock(block, entry.Slot);
                    block.SetColor("_Tint", entry.Tint * cooked);
                    entry.Renderer.SetPropertyBlock(block, entry.Slot);
                }
            }
            if (a.Visuals != null) appearance.Apply(a.Visuals, a.RevealBackpackContents);
            lighting.Apply(a.Lights);
            hadRecordedAppearance = a.Visuals != null;
        }
        public void Dispose() { lighting.Dispose(); appearance.Dispose(); Replica.Dispose(); }
    }

    private readonly Dictionary<string, Visual> visuals = new(StringComparer.Ordinal);
    private readonly Dictionary<Renderer, bool> originals = new();
    private readonly NativeLightAppearance.Originals originalLights = new();
    private readonly HashSet<string> warned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> retryAt = new(StringComparer.Ordinal);
    private readonly HashSet<string> present = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ItemFrame> next = new(StringComparer.Ordinal);
    private readonly HashSet<int> originalItems = new();
    private readonly List<string> removals = new();
    private readonly List<KeyValuePair<string, Visual>> hidden = new();
    private ItemFrame[]? previousLeft;
    private ItemFrame[]? previousRight;
    private float previousMix;
    private long serial;
    private readonly Action<string>? warning;
    private readonly VisualActor[] actors;
    private float nextOriginalRefresh;
    private bool warningLimit;
    private int missingCount;
    private int visibleCount;
    private bool hasBackpackItems;
    public int VisibleCount => visibleCount;
    public int MissingCount => missingCount;
    public ItemPlayback(Action<string>? warning = null, IReadOnlyDictionary<string, VisualActor>? actors = null)
    { this.warning = warning; this.actors = actors?.Values.ToArray() ?? Array.Empty<VisualActor>(); }

    private Transform? BackpackSlot(ItemFrame item)
    {
        if (item.State != BackpackAttachmentPolicy.InBackpack || actors.Length == 0) return null;
        Transform? match = null;
        foreach (var actor in actors)
        {
            if (!actor.Visible || actor.State == null) continue;
            int slot = BackpackAttachmentPolicy.FindSlot(item, actor.State);
            if (slot == -2) return null;
            if (slot < 0) continue;
            var anchor = actor.BackpackSlot(slot);
            if (!anchor) continue;
            if (match) return null; // A stale/ambiguous inventory must not bind to a guessed actor.
            match = anchor;
        }
        return match;
    }

    public void Apply(ItemFrame[] left, ItemFrame[] right, float mix)
    {
        if (!hasBackpackItems && missingCount == 0 && ReferenceEquals(left, previousLeft) && ReferenceEquals(right, previousRight) &&
            (mix == previousMix || ReferenceEquals(left, right))) return;
        previousLeft = left; previousRight = right; previousMix = mix;
        missingCount = 0;
        visibleCount = 0; hasBackpackItems = false; serial++;
        present.Clear(); next.Clear();
        foreach (var item in right) next[item.Key] = item;
        foreach (var item in left)
        {
            hasBackpackItems |= item.State == BackpackAttachmentPolicy.InBackpack;
            present.Add(item.Key);
            if (!visuals.TryGetValue(item.Key, out var visual))
            {
                if (retryAt.TryGetValue(item.Key, out var retry) && Time.unscaledTime < retry) { missingCount++; continue; }
                try
                {
                    Item? prefab = null;
                    if (item.ItemId >= 0 && item.ItemId <= ushort.MaxValue) ItemDatabase.TryGetItem((ushort)item.ItemId, out prefab);
                    if ((!prefab || prefab.name != item.Prefab) && !ItemDatabase.TryGetItem(item.Prefab, out prefab))
                        throw new InvalidOperationException("找不到物品资源 " + item.Prefab);
                    if (prefab == null) throw new InvalidOperationException("物品资源为空 " + item.Prefab);
                    visual = new Visual(prefab); visuals.Add(item.Key, visual); retryAt.Remove(item.Key);
                }
                catch (Exception e)
                {
                    missingCount++;
                    retryAt[item.Key] = Time.unscaledTime + 1;
                    if (warned.Count < ItemCapture.MaximumItems)
                    { if (warned.Add(item.Key)) warning?.Invoke("无法呈现物品：" + e.Message); }
                    else if (!warningLimit) { warningLimit = true; warning?.Invoke("物品缺失报告已达上限，后续同类提示已合并。"); }
                    continue;
                }
            }
            next.TryGetValue(item.Key, out var future);
            visual.LastSeen = serial;
            visual.Apply(item, future, mix, BackpackSlot(item));
            if (item.Pose.Active) visibleCount++;
        }
        // Retain a small LRU of hidden entities for backward seeks instead of rebuilding
        // their meshes/materials each time an item is consumed, stashed or revisited.
        hidden.Clear();
        foreach (var entry in visuals)
            if (!present.Contains(entry.Key)) { entry.Value.Replica.Hide(); hidden.Add(entry); }
        if (hidden.Count > 128)
        {
            hidden.Sort((a, b) => a.Value.LastSeen.CompareTo(b.Value.LastSeen));
            for (int i = 0; i < hidden.Count - 128; i++)
            { var entry = hidden[i]; entry.Value.Dispose(); visuals.Remove(entry.Key); }
        }
        removals.Clear(); foreach (var entry in retryAt) if (!present.Contains(entry.Key)) removals.Add(entry.Key);
        foreach (var key in removals) retryAt.Remove(key);
    }

    private void HideOriginalItems()
    {
        if (Time.unscaledTime >= nextOriginalRefresh)
        {
            nextOriginalRefresh = Time.unscaledTime + .5f;
            foreach (var item in Item.ALL_ITEMS)
            {
                if (!item || !item.gameObject.scene.IsValid()) continue;
                if (!originalItems.Add(item.GetInstanceID())) continue;
                originalLights.Hide(item.gameObject);
                foreach (var renderer in item.GetComponentsInChildren<Renderer>(true))
                {
                    if (!renderer) continue;
                    if (!originals.ContainsKey(renderer)) originals.Add(renderer, renderer.forceRenderingOff);
                }
            }
        }
        // A native LateUpdate can reset the flag even while playback time is paused.
        foreach (var original in originals) if (original.Key && !original.Key.forceRenderingOff) original.Key.forceRenderingOff = true;
        originalLights.Enforce();
    }

    public void Enforce() => HideOriginalItems();

    public void Dispose()
    {
        try { originalLights.Dispose(); } catch (Exception e) { ReportCleanup("恢复物品灯光失败：" + e.Message); }
        foreach (var entry in originals)
            try { if (entry.Key) entry.Key.forceRenderingOff = entry.Value; }
            catch (Exception e) { ReportCleanup("恢复物品渲染器失败：" + e.Message); }
        originals.Clear();
        foreach (var value in visuals.Values)
            try { value.Dispose(); }
            catch (Exception e) { ReportCleanup("清理物品视觉副本失败：" + e.Message); }
        visuals.Clear(); warned.Clear(); retryAt.Clear(); present.Clear(); next.Clear(); hidden.Clear(); removals.Clear(); originalItems.Clear();
    }

    private void ReportCleanup(string message) { try { warning?.Invoke(message); } catch { } }
}
