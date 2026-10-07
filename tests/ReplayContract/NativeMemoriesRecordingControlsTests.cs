using PeakReplayLab;

internal static class NativeMemoriesRecordingControlsTests
{
    public static void Run(Action<string, Action> test)
    {
        test("native full-recording buttons preserve persisted enum meanings and never disable F6 capture", () =>
        {
            foreach (var mode in new[] { ReplayRecordingMode.Rolling120, ReplayRecordingMode.Continuous })
            {
                var choices = NativeMemoriesRecordingControls.Choices(mode, canSwitch: true);
                Check(choices.Length == 2 && choices.Select(choice => choice.Mode).Distinct().Count() == 2, "Full-recording options are duplicated or missing.");
                Check(choices.Single(choice => choice.Mode == ReplayRecordingMode.Continuous).Label.Contains("开启完整录制"), "Enable button does not select persistent Continuous.");
                Check(choices.Single(choice => choice.Mode == ReplayRecordingMode.Rolling120).Label.Contains("关闭完整录制"), "Disable button does not select persistent Rolling120.");
                Check(choices.All(choice => choice.Label.Contains("F6")), "A full-recording choice implies clip saving is unavailable.");
                Check(!choices.Single(choice => choice.Mode == mode).Enabled && choices.Single(choice => choice.Mode != mode).Enabled, "Current choice or desired opposite has the wrong enabled state.");
                Check(NativeMemoriesRecordingControls.Status(mode).Contains("120 秒片段缓存"), "Library status hides the shared clip cache.");
            }
        });
        
        test("busy native recording controls cannot change settings or accept an invalid enum", () =>
        {
            foreach (var mode in new[] { ReplayRecordingMode.Rolling120, ReplayRecordingMode.Continuous })
            {
                Check(NativeMemoriesRecordingControls.Choices(mode, canSwitch: false).All(choice => !choice.Enabled), "Busy UI exposes a setting mutation.");
                Check(!NativeMemoriesRecordingControls.CanChoose(mode, mode, true), "Current setting is not a no-op.");
                Check(!NativeMemoriesRecordingControls.CanChoose(mode, (ReplayRecordingMode)99, true), "An unknown requested setting can be persisted.");
                Check(!NativeMemoriesRecordingControls.CanChoose((ReplayRecordingMode)99, mode, true), "An unknown current setting bypasses validation.");
            }
        });
        
        test("native confirmation retains clip history and names only the independent full-recording action", () =>
        {
            foreach (var mode in new[] { ReplayRecordingMode.Rolling120, ReplayRecordingMode.Continuous })
            {
                var text = NativeMemoriesRecordingControls.Confirmation(mode);
                Check(text.Contains("F6 仍可随时保存最近最多 120 秒") && text.Contains("现有片段缓存会保留"), "Confirmation does not retain concurrent clip capture.");
                Check(text.Contains("此设置会保存") && text.Contains("已存录像不受影响"), "Confirmation omits persistence or saved files.");
                Check(!text.Contains("清空") && !text.Contains("不再同时") && !text.Contains("互斥"), "Old mutually exclusive mode instructions returned.");
            }
            Check(NativeMemoriesRecordingControls.ConfirmTitle(ReplayRecordingMode.Continuous) == "开启完整录制？", "Enable confirmation names the wrong action.");
            Check(NativeMemoriesRecordingControls.ConfirmTitle(ReplayRecordingMode.Rolling120) == "关闭完整录制？", "Disable confirmation names the wrong action.");
        });
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
