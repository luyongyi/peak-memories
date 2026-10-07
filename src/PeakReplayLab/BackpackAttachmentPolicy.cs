using System;

namespace PeakReplayLab;

// This resolves recorded identity, never nearest-position/name/item-type guesses.
// Schema 3+ already records the same instance GUID in inventory and ItemFrame.Key.
internal static class BackpackAttachmentPolicy
{
    public const int InBackpack = 2;

    public static int FindSlot(ItemFrame item, ActorFrame actor)
    {
        if (item.State != InBackpack || !actor.InventoryKnown || !actor.BackpackWorn ||
            actor.BackpackType < 1 || actor.BackpackType > 4 ||
            item.HolderId.Length != 0 && item.HolderId != actor.Id ||
            !TryInstance(item.Key, out var instance)) return -1;
        int match = -1;
        foreach (var entry in actor.Inventory)
        {
            if (entry.Empty || !entry.Backpack || entry.Slot < 0 || entry.Slot > 3 || entry.ItemId != item.ItemId ||
                !Guid.TryParseExact(entry.Instance.AsSpan(), "N".AsSpan(), out var candidate) || candidate != instance) continue;
            if (match >= 0) return -2; // Duplicate ownership is ambiguous, even for identical item types.
            match = entry.Slot;
        }
        return match;
    }

    // Capture may omit redundant world motion only after the native attachment
    // and the already-recorded inventory independently agree on the exact slot.
    // Unknown/anonymous or ambiguous inventories keep the original pose path.
    public static bool IsKnownEquippedSlot(string key, int itemId, int nativeSlot,
        bool inventoryKnown, InventoryFrame[] inventory)
    {
        if (!inventoryKnown || nativeSlot < 0 || nativeSlot > 3 || !TryInstance(key, out var instance)) return false;
        int match = -1;
        foreach (var entry in inventory)
        {
            if (entry.Empty || !entry.Backpack || entry.ItemId != itemId ||
                !Guid.TryParseExact(entry.Instance.AsSpan(), "N".AsSpan(), out var candidate) || candidate != instance) continue;
            if (match >= 0 || entry.Slot < 0 || entry.Slot > 3) return false;
            match = entry.Slot;
        }
        return match == nativeSlot;
    }

    public static bool TryInstance(string key, out Guid instance)
    {
        instance = Guid.Empty;
        int slash = key.IndexOf('/');
        return slash == 32 && Guid.TryParseExact(key.AsSpan(0, slash), "N".AsSpan(), out instance) && instance != Guid.Empty;
    }

    public static bool CanInterpolate(ItemFrame left, ItemFrame? right) => right != null &&
        left.State == right.State && left.HolderId == right.HolderId;

    // Native Item.Update uses slot.position - slot.rotation * centerOfMass * .5,
    // and does not parent the live item. Our visual-only parent must compensate
    // its world scale to preserve that same placement/size without a physics body.
    public static float LocalOffset(float centerOfMass, float parentScale) => -.5f * centerOfMass * InverseScale(parentScale);
    public static float InverseScale(float scale) => Math.Abs(scale) > .00001f ? 1f / scale : 1f;
}
