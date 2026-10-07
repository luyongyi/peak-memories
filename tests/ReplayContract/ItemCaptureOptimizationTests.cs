using PeakReplayLab;

internal static class ItemCaptureOptimizationTests
{
    private const string Instance = "0123456789abcdef0123456789abcdef";
    private static InventoryFrame[] Inventory(int slot = 1) => new[]
    {
        new InventoryFrame { Backpack = true, Empty = false, Slot = slot, ItemId = 42, Instance = Instance },
    };

    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("known fixed backpack ignores wearer motion and expired hand heat", () =>
        {
            var policy = new ItemSamplingPolicy(0);
            policy.Observed(0, 0, true);
            for (int i = 1; i < 27; i++) check(!policy.ShouldRead(i / 60.0, 0, true, fixedPlacement: true));
            check(policy.ShouldRead(.5, 0, true, fixedPlacement: true));
        });
        test("equipped backpack metadata has bounded staggered low-frequency probes", () =>
        {
            var first = new ItemSamplingPolicy(0); var last = new ItemSamplingPolicy(16);
            first.Observed(0, 0, false, true); last.Observed(0, 0, false, true);
            check(first.ShouldRead(.5, 0, true, true)); check(!last.ShouldRead(.5, 0, true, true));
            check(last.ShouldRead(.7, 0, true, true));
            int reads = 0; var repeated = new ItemSamplingPolicy(0); repeated.Observed(0, 0, false, true);
            for (int i = 1; i <= 600; i++)
                if (repeated.ShouldRead(i / 60.0, 0, true, true))
                { reads++; repeated.Observed(i / 60.0, 0, true, true); }
            check(reads >= 20 && reads <= 23);
        });
        test("backpack item events wake immediately before the probe deadline", () =>
        {
            var policy = new ItemSamplingPolicy(16); policy.Observed(10, 5, false, true);
            check(policy.ShouldRead(10.001, 6, true, true));
            check(policy.ShouldRead(10.001, 5, false, false, attachmentChanged: true));
        });
        test("destroy transfer and attachment-loss boundaries do not wait for cold sampling", () =>
        {
            var policy = new ItemSamplingPolicy(16); policy.Observed(10, 5, false, true);
            check(!policy.ShouldRead(10.001, 5, false, true));
            check(policy.ShouldRead(10.001, 5, false, false, attachmentChanged: true));
        });
        test("held thrown and unknown-container sampling stays unchanged", () =>
        {
            var policy = new ItemSamplingPolicy(0); policy.Observed(0, 0, false, true);
            check(policy.ShouldRead(.001, 0, true));
            policy.Observed(.001, 0, true);
            for (int i = 1; i < 15; i++) check(policy.ShouldRead(.001 + i / 60.0, 0, false));
            check(policy.ShouldRead(.1, 0, true));
        });
        test("stowing clears previous motion heat but taking out immediately resumes full sampling", () =>
        {
            var policy = new ItemSamplingPolicy(0); policy.Observed(0, 0, true);
            policy.Observed(.01, 1, false, true);
            check(!policy.ShouldRead(.02, 1, false));
            check(policy.ShouldRead(.02, 1, true));
        });
        test("fixed backpack dynamic children are slow-probed and event-woken", () =>
        {
            var policy = new ItemSamplingPolicy(0);
            check(policy.SampleChildren(0, true, false, true));
            for (int i = 1; i < 27; i++) check(!policy.SampleChildren(i / 60.0, false, false, true));
            check(policy.SampleChildren(.5, false, false, true));
            check(policy.SampleChildren(.501, true, false, true));
            check(!policy.SampleChildren(.6, false, false, true));
        });
        test("known equipped slot requires exact guid item id and native slot agreement", () =>
        {
            check(BackpackAttachmentPolicy.IsKnownEquippedSlot(Instance + "/100", 42, 1, true, Inventory()));
            check(!BackpackAttachmentPolicy.IsKnownEquippedSlot(Instance + "/100", 43, 1, true, Inventory()));
            check(!BackpackAttachmentPolicy.IsKnownEquippedSlot(Instance + "/100", 42, 2, true, Inventory()));
            check(!BackpackAttachmentPolicy.IsKnownEquippedSlot("1123456789abcdef0123456789abcdef/100", 42, 1, true, Inventory()));
        });
        test("unknown anonymous malformed and invalid-slot bags never freeze", () =>
        {
            check(!BackpackAttachmentPolicy.IsKnownEquippedSlot(Instance + "/100", 42, 1, false, Inventory()));
            foreach (string key in new[] { "anonymous/100", "invalid", new string('0', 32) + "/100" })
                check(!BackpackAttachmentPolicy.IsKnownEquippedSlot(key, 42, 1, true, Inventory()));
            check(!BackpackAttachmentPolicy.IsKnownEquippedSlot(Instance + "/100", 42, -1, true, Inventory()));
            check(!BackpackAttachmentPolicy.IsKnownEquippedSlot(Instance + "/100", 42, 4, true, Inventory()));
            check(!BackpackAttachmentPolicy.IsKnownEquippedSlot(Instance + "/100", 42, 1, true, Inventory(250)));
        });
        test("empty ordinary and duplicated inventory entries cannot authorize fixed placement", () =>
        {
            var inventory = Inventory(); inventory[0].Empty = true;
            check(!BackpackAttachmentPolicy.IsKnownEquippedSlot(Instance + "/100", 42, 1, true, inventory));
            inventory = Inventory(); inventory[0].Backpack = false;
            check(!BackpackAttachmentPolicy.IsKnownEquippedSlot(Instance + "/100", 42, 1, true, inventory));
            inventory = new[] { Inventory()[0], Inventory(2)[0] };
            check(!BackpackAttachmentPolicy.IsKnownEquippedSlot(Instance + "/100", 42, 1, true, inventory));
        });
        test("placement baseline survives wearer world translation rotation and scale jitter", () =>
        {
            var baseline = new ObjectPose { Position = new[] { 1f, 2f, 3f }, Scale = new[] { .5f, .5f, .5f } };
            var moved = new ObjectPose { Position = new[] { 50f, 20f, -3f }, Rotation = new[] { 0f, 1f, 0f, 0f }, Nodes = baseline.Nodes };
            check(ReferenceEquals(BackpackCapturePolicy.KeepPlacement(baseline, moved), baseline));
            check(moved.Position[0] == 50 && baseline.Position[0] == 1);
        });
        test("child visual changes update without replacing the fixed placement arrays", () =>
        {
            var baseline = new ObjectPose { Nodes = new[] { new NodePose { Path = "0", Active = true } } };
            var changed = new ObjectPose { Position = new[] { 80f, 0f, 0f }, Nodes = new[] { new NodePose { Path = "0", Active = false } } };
            var result = BackpackCapturePolicy.KeepPlacement(baseline, changed);
            check(!ReferenceEquals(result, baseline)); check(ReferenceEquals(result.Position, baseline.Position));
            check(ReferenceEquals(result.Rotation, baseline.Rotation)); check(ReferenceEquals(result.Scale, baseline.Scale));
            check(ReferenceEquals(result.Nodes, changed.Nodes)); check(baseline.Nodes[0].Active);
            check(ReferenceEquals(BackpackCapturePolicy.KeepPlacement(result, changed), result));
        });
        test("visibility checkpoints remain immutable and survive backward selection", () =>
        {
            var before = new ObjectPose { Active = true };
            var sampled = new ObjectPose { Active = false, Nodes = before.Nodes };
            var after = BackpackCapturePolicy.KeepPlacement(before, sampled);
            check(before.Active && !after.Active); check(ReferenceEquals(before.Position, after.Position));
            check(BackpackCapturePolicy.KeepPlacement(before, before).Active);
        });
    }
}
