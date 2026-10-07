using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PeakReplayLab;

// Immutable presentation data only. Path deliberately retains its exact file
// identity; the three displayed fields are bounded plain text, never TMP markup.
public sealed class MemoriesLibraryItem
{
    public readonly string Path, Title, Subtitle, Details;
    public readonly bool Playable;
    public readonly string[] CoverKeys;
    public readonly bool IsFullRun;
    internal MemoriesLibraryItem(string path, string title, string subtitle, string details, bool playable,
        string[]? coverKeys = null, bool isFullRun = false)
    {
        Path = path; Title = title; Subtitle = subtitle; Details = details; Playable = playable;
        CoverKeys = coverKeys ?? Array.Empty<string>(); IsFullRun = isFullRun;
    }

    public static MemoriesLibraryItem FromHighlight(string path, ReplayHeader? header, string error) =>
        MemoriesLibraryModel.FromHighlight(path, header, error);
    public static MemoriesLibraryItem FromFullRun(string path, FullReplayInfo? info, long bytes, string error) =>
        MemoriesLibraryModel.FromFullRun(path, info, bytes, error);
}

public static class MemoriesLibraryModel
{
    public const int PageSize = 4;
    public const int TitleLimit = 80, SubtitleLimit = 160, DetailsLimit = 2400;
    public static int PageCount(int count) => count <= 0 ? 1 : 1 + (count - 1) / PageSize;
    public static int ClampPage(int page, int count) => Math.Max(0, Math.Min(page, PageCount(count) - 1));

    public static string FormatDuration(double seconds)
    {
        if (!ReplayRules.Finite(seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds - 1) return "--:--";
        long whole = (long)Math.Floor(seconds);
        return whole >= 3600
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", whole / 3600, whole / 60 % 60, whole % 60)
            : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", whole / 60, whole % 60);
    }

    // A valid header enables the existing open operation. That operation still
    // validates the complete highlight/footer and current game compatibility.
    // This model never opens a file or claims to have checked its payload.
    public static MemoriesLibraryItem FromHighlight(string path, ReplayHeader? header, string error)
    {
        string problem = Problem(path, ".peakreplay", error);
        if (header == null) return Missing(path, "精彩片段", problem);
        string metadataProblem = HeaderProblem(header);
        if (problem.Length == 0) problem = metadataProblem;
        if (problem.Length == 0 && header.FrameCount == 1) problem = "片段只有一帧，无法播放。";
        if (problem.Length == 0 && header.FrameCount >= 2 && header.Duration <= 0) problem = "片段时长与采样帧数不一致。";
        string time = LocalDate(header.SavedUtc, header.StartedUtc);
        bool durationKnown = header.Duration > 0 || header.FrameCount >= 2;
        string duration = durationKnown ? FormatDuration(header.Duration) : "时长未标注";
        return Make(path, "精彩片段", time, duration, header, problem,
            "采样帧数：" + (header.FrameCount > 0 ? header.FrameCount.ToString(CultureInfo.InvariantCulture) : "未标注") +
            "\n播放前将校验文件完整性与当前场景兼容性。", null, header.RegionSummary, false);
    }

    public static MemoriesLibraryItem FromFullRun(string path, FullReplayInfo? info, long bytes, string error)
    {
        string problem = Problem(path, ".peakrun", error);
        if (info?.Header == null) return Missing(path, "完整录像", problem);
        var header = info.Header;
        string metadataProblem = HeaderProblem(header);
        if (problem.Length == 0) problem = metadataProblem;
        if (problem.Length == 0 && (!info.Complete || !string.IsNullOrEmpty(info.Fault) || !string.IsNullOrEmpty(info.FaultCode)))
            problem = "录像未完整结束，暂不可播放。";
        if (problem.Length == 0 && (info.Version != 1 || !ReplayRules.SupportedSchema(header.Schema) || header.Duration != 0 || header.FrameCount != 0 ||
            !ReplayRules.Finite(info.Duration) || info.Duration <= 0 || info.Duration > FullReplayArchive.MaximumDuration ||
            info.FrameCount < 2 || info.FrameCount > 4L * 60 * 60 * 60 + 1 ||
            info.Pages == null || info.Pages.Length == 0 || info.Pages.Length > FullReplayArchive.MaximumPages ||
            info.ActorIds == null || info.ActorIds.Length > ReplayRules.MaxActors ||
            info.ActorIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256) ||
            info.ActorIds.Distinct(StringComparer.Ordinal).Count() != info.ActorIds.Length ||
            bytes <= 0 || bytes > FullReplayArchive.MaximumFileBytes))
            problem = "完整录像元数据无效或超出支持范围。";
        string size = bytes >= 0 ? (bytes / 1048576d).ToString("F1", CultureInfo.InvariantCulture) + " MiB" : "大小未知";
        string extra = "采样帧数：" + info.FrameCount.ToString(CultureInfo.InvariantCulture) + " · 文件大小：" + size +
            "\n整局连续时间轴；仅按需读取，不需逐段打开。";
        if (!string.IsNullOrWhiteSpace(info.FaultCode) || !string.IsNullOrWhiteSpace(info.Fault))
            extra += "\n中断原因：" + Plain(info.FaultCode, 80) + " " + Plain(info.Fault, 240);
        if (info.GapOver100ms > 0)
            extra += "\n采样间隙：超过 100 ms 共 " + info.GapOver100ms.ToString(CultureInfo.InvariantCulture) + " 次；最大 " +
                (ReplayRules.Finite(info.LargestGapSeconds) && info.LargestGapSeconds >= 0
                    ? info.LargestGapSeconds.ToString("F2", CultureInfo.InvariantCulture) + " 秒" : "未知") + "。";
        return Make(path, "完整录像", LocalDate(header.StartedUtc, header.SavedUtc), FormatDuration(info.Duration),
            header, problem, extra, info.ActorIds?.Length, info.RegionSummary, true);
    }

    private static MemoriesLibraryItem Make(string path, string kind, string time, string duration, ReplayHeader header,
        string problem, string extra, int? actualParticipants, ReplayRegionSummary? regions, bool fullRun)
    {
        string scene = Plain(header.Scene, 120);
        if (scene.Length == 0) scene = "场景未知";
        string participants = Participants(header.Participants);
        string count = actualParticipants.HasValue
            ? " · " + actualParticipants.Value.ToString(CultureInfo.InvariantCulture) + " 位参与者" : "";
        string title = Plain(time + " · " + kind, TitleLimit);
        string subtitle = Plain((problem.Length == 0 ? "" : "不可播放 · ") + duration + " · " + scene + count, SubtitleLimit);
        // Put warnings before potentially long user-supplied names/routes, so a
        // label budget can never erase the most important compatibility warning.
        string details = (problem.Length == 0 ? "" : "不可播放：" + Plain(problem, 320) + "\n") +
            SchemaNotice(header.Schema) + "\n" +
            "本地时间：" + time + "\n时长：" + duration + " · 采样：" + header.SampleHz.ToString(CultureInfo.InvariantCulture) + " Hz\n" +
            "场景：" + scene + "\n游戏版本：" + Fallback(Plain(header.GameVersion, 80), "未标注") +
            " · Schema " + header.Schema.ToString(CultureInfo.InvariantCulture) + "\n" +
            extra + "\n路线：" + Fallback(Plain(header.Route, 420), "未标注") +
            "\n参与者：" + participants + count + "\n文件：" + FileLabel(path);
        if (regions == null) details += "\n封面：录像缺少实际经过区域信息，使用中性便条。";
        return new MemoriesLibraryItem(path ?? "", title, subtitle, LimitLines(details, DetailsLimit), problem.Length == 0,
            ReplayRegionCovers.Keys(regions, fullRun), fullRun);
    }

    private static MemoriesLibraryItem Missing(string path, string kind, string problem)
    {
        if (problem.Length == 0) problem = "无法读取录像元数据，或格式不兼容。";
        return new MemoriesLibraryItem(path ?? "", Plain(FileLabel(path), TitleLimit), kind + " · 不可播放",
            "不可播放：" + Plain(problem, 320) + "\n文件：" + FileLabel(path), false, isFullRun: kind == "完整录像");
    }
    private static string HeaderProblem(ReplayHeader header)
    {
        try { ReplayRules.Validate(header); return ""; }
        catch (InvalidDataException) { return "录像元数据无效或版本不兼容。"; }
        catch (ArgumentException) { return "录像元数据无效或版本不兼容。"; }
    }
    private static string Problem(string path, string extension, string error)
    {
        if (!string.IsNullOrWhiteSpace(path) && path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            return "尚未封存：录像仍在写入或曾异常中断，暂不可播放。" + (string.IsNullOrWhiteSpace(error) ? "" : " " + Plain(error, 240));
        if (!string.IsNullOrWhiteSpace(error)) return Fallback(Plain(error, 320), "无法读取录像数据。");
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) || path.Any(char.IsControl))
            return "录像文件名或文件类型无效。";
        return "";
    }
    private static string SchemaNotice(int schema) => schema == ReplayRules.CurrentSchema
        ? "人物、状态条、天气、生物和物品随录像同步；特效与游戏声音保留。"
        : schema == 11
            ? "兼容旧版录像，已记录体力与状态条；未记录的天气、生物及放置物需新版重新录制。"
            : schema == 10
                ? "兼容旧版录像；未记录的完整状态条、天气、生物及放置物需新版重新录制。"
                : "格式不受支持：请使用受支持的录像或重新录制。";
    private static string Participants(string[]? values)
    {
        if (values == null || values.Length == 0) return "未标注姓名";
        return Fallback(Plain(string.Join(" · ", values.Take(ReplayRules.MaxActors).Select(value => Plain(value, 36))), 620), "未标注姓名");
    }
    private static string LocalDate(string? preferred, string? fallback)
    {
        foreach (string? value in new[] { preferred, fallback })
            if (!string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
                DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var time))
                return time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return "时间未知";
    }
    private static string FileLabel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "未命名文件";
        // Recognize both separators without touching the filesystem, including
        // tests opened on another OS and filenames supplied in failure messages.
        int separator = Math.Max(path!.LastIndexOf('/'), path.LastIndexOf('\\'));
        return Fallback(Plain(path.Substring(separator + 1), 140), "未命名文件");
    }
    private static string Fallback(string value, string fallback) => value.Length == 0 ? fallback : value;

    // TMP tags require literal ASCII angle brackets. Display them as harmless
    // visible characters rather than parsing or trusting user-controlled tags.
    // Remove directional/invisible controls; preserve valid emoji surrogate pairs.
    private static string Plain(string? value, int maximum)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var result = new StringBuilder(Math.Min(maximum + 1, value!.Length));
        bool space = false;
        for (int i = 0; i < value.Length && i < 16384; i++)
        {
            char c = value[i];
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = result.Length > 0; continue; }
            if (char.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
            if (space) { result.Append(' '); space = false; }
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { result.Append(c); result.Append(value[++i]); }
                else continue;
            }
            else if (char.IsLowSurrogate(c)) continue;
            else result.Append(c == '<' ? '‹' : c == '>' ? '›' : c);
            if (result.Length > maximum) break;
        }
        string text = result.ToString().TrimEnd();
        if (text.Length <= maximum) return text;
        int keep = maximum - 1;
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1])) keep--;
        return text.Substring(0, keep).TrimEnd() + "…";
    }
    private static string LimitLines(string text, int maximum)
    {
        if (text.Length <= maximum) return text;
        int keep = maximum - 1;
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1])) keep--;
        return text.Substring(0, keep).TrimEnd() + "…";
    }
}
