using PeakReplayLab;

internal static class ReplayHudLayoutTests
{
    public static void Run(Action<string, Action> test)
    {
        test("native stamina offset cannot produce negative geometry at depleted stamina", () =>
        {
            Near(ReplayHudLayout.NativeStaminaWidth(0, 400, -10), 0);
            Near(ReplayHudLayout.NativeStaminaWidth(.025f, 400, -10), 0);
            Near(ReplayHudLayout.NativeStaminaWidth(.05f, 400, -10), 10);
            Near(ReplayHudLayout.NativeStaminaWidth(1, 400, -10), 390);
            Near(ReplayHudLayout.NativeStaminaWidth(0, 400, 8), 8);
        });
        test("native affliction visibility threshold and minimum reserve legible icon space", () =>
        {
            Near(ReplayHudLayout.AfflictionWidth(0, 400, 60), 0);
            Near(ReplayHudLayout.AfflictionWidth(.01f, 400, 60), 0);
            Near(ReplayHudLayout.AfflictionWidth(.0101f, 400, 60), 60);
            Near(ReplayHudLayout.AfflictionWidth(.15f, 400, 60), 60);
            Near(ReplayHudLayout.AfflictionWidth(.2f, 400, 60), 80);
            Near(ReplayHudLayout.AfflictionWidth(1.5f, 400, 60), 600);
        });
        test("native main outline preserves ordinary capacity then expands for real status overflow", () =>
        {
            float baseWidth = ReplayHudLayout.MainOutlineWidth(0, 400);
            Near(baseWidth, 414);
            Near(ReplayHudLayout.MainOutlineWidth(.25f, 400), baseWidth);
            Near(ReplayHudLayout.MainOutlineWidth(1, 400), baseWidth);
            Near(ReplayHudLayout.MainOutlineWidth(1.25f, 400) - baseWidth, 100);
            Near(ReplayHudLayout.MainOutlineWidth(3, 400), 1214);
            // Icon minimums expand the actual segments, not the recorded
            // status total: four small icons do not fabricate an affliction.
            float icons = 4 * ReplayHudLayout.AfflictionWidth(.03f, 400, 60);
            Near(icons, 240);
            Near(ReplayHudLayout.MainOutlineWidth(4 * .03f, 400), baseWidth);
        });
        test("native bonus stamina frame has a minimum and petrify retains its full outline", () =>
        {
            Near(ReplayHudLayout.ExtraOutlineWidth(0, 400, false), 20);
            Near(ReplayHudLayout.ExtraOutlineWidth(.01f, 400, false), 20);
            Near(ReplayHudLayout.ExtraOutlineWidth(.5f, 400, false), 212);
            Near(ReplayHudLayout.ExtraOutlineWidth(1, 400, false), 412);
            Near(ReplayHudLayout.ExtraOutlineWidth(0, 400, true), 412);
            Near(ReplayHudLayout.ExtraOutlineWidth(.5f, 400, true), 412);
            Near(ReplayHudLayout.SelectedSlotMultiplier, 1.2f);
        });
    }

    private static void Near(float actual, float expected)
    {
        if (Math.Abs(actual - expected) > .00001f)
            throw new Exception($"Native HUD width expected {expected}, got {actual}.");
    }
}
