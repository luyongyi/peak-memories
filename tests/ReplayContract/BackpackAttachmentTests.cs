using PeakReplayLab;

internal static class BackpackAttachmentTests
{
    private const string Instance = "0123456789abcdef0123456789abcdef";
    private static ItemFrame Item() => new() { Key = Instance + "/100", State = 2, ItemId = 42 };
    private static InventoryFrame Slot(int index = 1) => new()
    { Empty = false, Backpack = true, Slot = index, Instance = Instance, ItemId = 42 };
    private static ActorFrame Actor() => new()
    { Id = "actor", InventoryKnown = true, BackpackWorn = true, BackpackType = 1, Inventory = new[] { Slot() } };

    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("legacy empty holder resolves exact backpack instance and slot", () =>
        { check(BackpackAttachmentPolicy.FindSlot(Item(), Actor()) == 1); });
        test("equipped backpack owner must match when recorded", () =>
        {
            var item = Item(); item.HolderId = "someone-else";
            check(BackpackAttachmentPolicy.FindSlot(item, Actor()) == -1);
            item.HolderId = "actor"; check(BackpackAttachmentPolicy.FindSlot(item, Actor()) == 1);
        });
        test("same item type is not a substitute for instance identity", () =>
        {
            var actor = Actor(); actor.Inventory[0].Instance = "1123456789abcdef0123456789abcdef";
            check(BackpackAttachmentPolicy.FindSlot(Item(), actor) == -1);
            actor = Actor(); actor.Inventory[0].ItemId = 43;
            check(BackpackAttachmentPolicy.FindSlot(Item(), actor) == -1);
        });
        test("held ground and unknown inventory never bind to a backpack", () =>
        {
            var item = Item(); item.State = 0; check(BackpackAttachmentPolicy.FindSlot(item, Actor()) == -1);
            item.State = 1; check(BackpackAttachmentPolicy.FindSlot(item, Actor()) == -1);
            item.State = 2; var actor = Actor(); actor.InventoryKnown = false;
            check(BackpackAttachmentPolicy.FindSlot(item, actor) == -1);
        });
        test("pack must actually be worn not held dropped or absent", () =>
        {
            var actor = Actor(); actor.BackpackWorn = false;
            check(BackpackAttachmentPolicy.FindSlot(Item(), actor) == -1);
            actor.BackpackWorn = true; actor.BackpackType = 0;
            check(BackpackAttachmentPolicy.FindSlot(Item(), actor) == -1);
        });
        test("empty ordinary and invalid slots are not visible backpack contents", () =>
        {
            var actor = Actor(); actor.Inventory[0].Empty = true;
            check(BackpackAttachmentPolicy.FindSlot(Item(), actor) == -1);
            actor = Actor(); actor.Inventory[0].Backpack = false;
            check(BackpackAttachmentPolicy.FindSlot(Item(), actor) == -1);
            actor = Actor(); actor.Inventory[0].Slot = 250;
            check(BackpackAttachmentPolicy.FindSlot(Item(), actor) == -1);
        });
        test("duplicate inventory ownership is explicitly ambiguous", () =>
        {
            var actor = Actor(); actor.Inventory = new[] { Slot(0), Slot(1) };
            check(BackpackAttachmentPolicy.FindSlot(Item(), actor) == -2);
        });
        test("anonymous empty and malformed entity keys cannot guess a pack", () =>
        {
            foreach (string key in new[] { "anonymous/100", "/100", Instance, new string('0', 32) + "/100", "wrong/100" })
            { var item = Item(); item.Key = key; check(BackpackAttachmentPolicy.FindSlot(item, Actor()) == -1); }
        });
        test("world position rotation jitter does not affect stable slot resolution", () =>
        {
            var item = Item(); var actor = Actor();
            for (int i = 0; i < 120; i++)
            {
                item.Pose.Position[0] = i * .1f; item.Pose.Position[1] = i % 3;
                item.Pose.Rotation[1] = i * .01f;
                check(BackpackAttachmentPolicy.FindSlot(item, actor) == 1);
            }
        });
        test("slot changes and reverse seeks use the selected frame not cached history", () =>
        {
            var before = Actor(); var after = Actor(); after.Inventory = new[] { Slot(3) };
            check(BackpackAttachmentPolicy.FindSlot(Item(), before) == 1);
            check(BackpackAttachmentPolicy.FindSlot(Item(), after) == 3);
            check(BackpackAttachmentPolicy.FindSlot(Item(), before) == 1);
        });
        test("native half center offset compensates scaled slot parents", () =>
        {
            check(BackpackAttachmentPolicy.LocalOffset(2, 1) == -1);
            check(BackpackAttachmentPolicy.LocalOffset(2, 2) * 2 == -1);
            check(BackpackAttachmentPolicy.LocalOffset(2, -2) * -2 == -1);
            check(float.IsFinite(BackpackAttachmentPolicy.LocalOffset(2, 0)));
        });
        test("taking out stashing and transfer boundaries never blend attachment spaces", () =>
        {
            var a = Item(); var b = Item(); check(BackpackAttachmentPolicy.CanInterpolate(a, b));
            b.State = 1; check(!BackpackAttachmentPolicy.CanInterpolate(a, b));
            b.State = 0; check(!BackpackAttachmentPolicy.CanInterpolate(a, b));
            b.State = 2; b.HolderId = "other"; check(!BackpackAttachmentPolicy.CanInterpolate(a, b));
            check(!BackpackAttachmentPolicy.CanInterpolate(a, null));
        });
    }
}
