using System;

namespace PeakReplayLab;

// The persisted enum keeps its existing values. Rolling120 now means that
// automatic full recording is off; both choices retain the rolling clip cache.
internal static class NativeMemoriesRecordingControls
{
    internal readonly struct Choice
    {
        public readonly ReplayRecordingMode Mode;
        public readonly string Label;
        public readonly bool Enabled;
        public Choice(ReplayRecordingMode mode, string label, bool enabled)
        { Mode = mode; Label = label; Enabled = enabled; }
    }

    public const string Subtitle = "F6 随时保存 120 秒片段 · 完整录制独立开关";
    public const string Help = "F6 随时保存最近最多 120 秒。\n完整录制可独立开启或关闭，F4 可切换完整录制。\n切换不会清空片段缓存，观看回放时不录制。";

    public static Choice[] Choices(ReplayRecordingMode current, bool canSwitch) => new[]
    {
        new Choice(ReplayRecordingMode.Continuous,
            (current == ReplayRecordingMode.Continuous ? "· 已开启 · " : "") + "开启完整录制\n进岛自动记录整局，F6 仍可保存片段",
            CanChoose(current, ReplayRecordingMode.Continuous, canSwitch)),
        new Choice(ReplayRecordingMode.Rolling120,
            (current == ReplayRecordingMode.Rolling120 ? "· 已关闭 · " : "") + "关闭完整录制\n不自动写整局，F6 片段保存仍然可用",
            CanChoose(current, ReplayRecordingMode.Rolling120, canSwitch)),
    };

    public static bool CanChoose(ReplayRecordingMode current, ReplayRecordingMode desired, bool canSwitch) => canSwitch &&
        Known(current) && Known(desired) && current != desired;

    public static string Status(ReplayRecordingMode mode) => mode == ReplayRecordingMode.Continuous
        ? "120 秒片段缓存 · 完整录制已开启" : "120 秒片段缓存 · 完整录制已关闭";

    public static string ConfirmTitle(ReplayRecordingMode desired) => desired switch
    {
        ReplayRecordingMode.Continuous => "开启完整录制？",
        ReplayRecordingMode.Rolling120 => "关闭完整录制？",
        _ => throw new ArgumentOutOfRangeException(nameof(desired)),
    };

    public static string Confirmation(ReplayRecordingMode desired) => desired switch
    {
        ReplayRecordingMode.Continuous => "开启后，进岛会自动记录完整录像。\n\nF6 仍可随时保存最近最多 120 秒，现有片段缓存会保留。\n此设置会保存，已存录像不受影响。",
        ReplayRecordingMode.Rolling120 => "关闭后，不再自动录制完整录像。\n\nF6 仍可随时保存最近最多 120 秒，现有片段缓存会保留。\n此设置会保存，已存录像不受影响。",
        _ => throw new ArgumentOutOfRangeException(nameof(desired)),
    };

    private static bool Known(ReplayRecordingMode mode) => mode == ReplayRecordingMode.Rolling120 || mode == ReplayRecordingMode.Continuous;
}
