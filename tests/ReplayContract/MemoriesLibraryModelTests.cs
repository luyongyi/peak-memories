using System.Globalization;
using PeakReplayLab;

internal static class MemoriesLibraryModelTests
{
    public static void Run(Action<string, Action> test)
    {
        test("memories library paging keeps an empty page and clamps after library shrink", () =>
        {
            Check(MemoriesLibraryModel.PageSize == 4);
            Check(MemoriesLibraryModel.PageCount(0) == 1 && MemoriesLibraryModel.PageCount(-1) == 1);
            Check(MemoriesLibraryModel.PageCount(4) == 1 && MemoriesLibraryModel.PageCount(5) == 2);
            Check(MemoriesLibraryModel.PageCount(8) == 2 && MemoriesLibraryModel.PageCount(9) == 3);
            Check(MemoriesLibraryModel.ClampPage(4, 17) == 4 && MemoriesLibraryModel.ClampPage(4, 5) == 1);
            Check(MemoriesLibraryModel.ClampPage(4, 0) == 0 && MemoriesLibraryModel.ClampPage(-3, 90) == 0);
            Check(MemoriesLibraryModel.PageCount(int.MaxValue) == 536870912);
            Check(MemoriesLibraryModel.ClampPage(int.MaxValue, int.MaxValue) == 536870911);
            Check(MemoriesLibraryModel.ClampPage(int.MinValue, int.MaxValue) == 0);
        });
        test("memories library duration is stable at minute hour and invalid-number boundaries", () =>
        {
            Check(MemoriesLibraryModel.FormatDuration(0) == "00:00");
            Check(MemoriesLibraryModel.FormatDuration(59.99) == "00:59");
            Check(MemoriesLibraryModel.FormatDuration(60) == "01:00");
            Check(MemoriesLibraryModel.FormatDuration(3599.9) == "59:59");
            Check(MemoriesLibraryModel.FormatDuration(3600) == "1:00:00");
            Check(MemoriesLibraryModel.FormatDuration(14400) == "4:00:00");
            foreach (double value in new[] { -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue })
                Check(MemoriesLibraryModel.FormatDuration(value) == "--:--");
        });
        test("memories library highlight summarizes local time scene names route version and rate", () =>
        {
            var h = Header(); var item = MemoriesLibraryItem.FromHighlight("C:/memories/a.peakreplay", h, "");
            string local = DateTimeOffset.Parse(h.SavedUtc, CultureInfo.InvariantCulture).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            Check(item.Playable && item.Title.Contains(local) && item.Title.Contains("精彩片段"));
            Check(item.Subtitle.Contains("01:00") && item.Subtitle.Contains("Level_Test"));
            Check(item.Details.Contains("Alice · Bob") && item.Details.Contains("Shore,Roots,Alpine") && item.Details.Contains("1.2.3"));
            Check(item.Details.Contains("60 Hz") && item.Details.Contains("Schema " + ReplayRules.CurrentSchema) && item.Details.Contains("完整性"));
            Check(item.Path == "C:/memories/a.peakreplay");
        });
        test("memories library refuses formats before schema 10 and unknown formats", () =>
        {
            foreach (int schema in Enumerable.Range(2, 8).Append(99))
            {
                var h = Header(); h.Schema = schema;
                var item = MemoriesLibraryModel.FromHighlight("old.peakreplay", h, "");
                Check(!item.Playable && item.Details.Contains("格式不受支持") && item.Details.Contains("重新录制"));
            }
        });
        test("memories library schema 11 stays playable with an accurate missing environment notice", () =>
        {
            var h = Header(); h.Schema = 11;
            var item = MemoriesLibraryModel.FromHighlight("old.peakreplay", h, "");
            Check(item.Playable && !item.Details.Contains("格式不受支持") && item.Details.Contains("已记录体力与状态条"));
        });
        test("memories library partial missing error and wrong-type entries cannot offer playback", () =>
        {
            var h = Header();
            foreach (var item in new[]
            {
                MemoriesLibraryModel.FromHighlight("a.peakreplay.partial", h, ""),
                MemoriesLibraryModel.FromHighlight("a.peakreplay", null, "unreadable"),
                MemoriesLibraryModel.FromHighlight("a.peakreplay", h, "bad data"),
                MemoriesLibraryModel.FromHighlight("a.peakreplay", h, "\0\u202e"),
                MemoriesLibraryModel.FromHighlight("a.peakrun", h, ""),
                MemoriesLibraryModel.FromHighlight("bad\0.peakreplay", h, ""),
                MemoriesLibraryModel.FromHighlight("", h, ""),
            }) Check(!item.Playable && item.Details.Contains("不可播放"));
            Check(MemoriesLibraryModel.FromHighlight("a.PEAKREPLAY", h, "").Playable);
        });
        test("memories library invalid dates fall back without inventing dates or rejecting valid payload metadata", () =>
        {
            var h = Header(); h.SavedUtc = "not-a-date"; h.StartedUtc = "also-invalid";
            var unknown = MemoriesLibraryModel.FromHighlight("a.peakreplay", h, "");
            Check(unknown.Playable && unknown.Title.Contains("时间未知") && !unknown.Title.Contains("0001"));
            h.StartedUtc = "2026-09-26T09:00:00Z";
            var fallback = MemoriesLibraryModel.FromHighlight("a.peakreplay", h, "");
            Check(fallback.Playable && fallback.Title.Contains("2026-09-26"));
            var legacy = Header(); legacy.Duration = 0; legacy.FrameCount = 0;
            var missingDuration = MemoriesLibraryModel.FromHighlight("old.peakreplay", legacy, "");
            Check(missingDuration.Playable && missingDuration.Details.Contains("时长未标注"));
        });
        test("memories library invalid highlights cannot disguise zero nonfinite or excessive durations", () =>
        {
            foreach (double duration in new[] { double.NaN, double.PositiveInfinity, -1, 122, 0 })
            {
                var h = Header(); h.Duration = duration;
                Check(!MemoriesLibraryModel.FromHighlight("bad.peakreplay", h, "").Playable);
            }
            var one = Header(); one.FrameCount = 1;
            Check(!MemoriesLibraryModel.FromHighlight("one.peakreplay", one, "").Playable);
        });
        test("memories library full recordings use global duration and actor index without exposing internal pages", () =>
        {
            var info = Full(); var item = MemoriesLibraryItem.FromFullRun("run.peakrun", info, 5 * 1024 * 1024, "");
            Check(item.Playable && item.Title.Contains("完整录像") && item.Subtitle.Contains("1:02:03") && item.Subtitle.Contains("2 位参与者"));
            Check(item.Details.Contains("5.0 MiB") && item.Details.Contains("3601") && item.Details.Contains("整局连续时间轴"));
            Check(!item.Details.Contains("segment-") && !item.Details.Contains("内部页"));
            Check(item.Details.Contains("Alice · Bob") && item.Details.Contains("60 Hz"));
        });
        test("memories library incomplete invalid or faulted full recordings remain visibly unavailable", () =>
        {
            foreach (Action<FullReplayInfo> change in new Action<FullReplayInfo>[]
            {
                info => info.Complete = false, info => info.Fault = "disk error", info => info.FaultCode = "capture-error",
                info => info.Duration = double.NaN, info => info.Duration = 14401, info => info.Duration = 0,
                info => info.FrameCount = 1, info => info.ActorIds = new[] { "same", "same" },
                info => info.Pages = Array.Empty<FullReplayPage>(), info => info.Header.Schema = 7,
            })
            {
                var info = Full(); change(info);
                Check(!MemoriesLibraryModel.FromFullRun("run.peakrun", info, 1000, "").Playable);
            }
            Check(!MemoriesLibraryModel.FromFullRun("run.peakrun.partial", Full(), 1000, "").Playable);
            Check(!MemoriesLibraryModel.FromFullRun("run.peakrun", Full(), -1, "").Playable);
            Check(!MemoriesLibraryModel.FromFullRun("run.peakrun", null, 1000, "").Playable);
            Check(!MemoriesLibraryModel.FromFullRun("run.peakrun", Full(), 1000, "read error").Playable);
        });
        test("memories library neutralizes TMP markup control characters and bidirectional spoofing in every display field", () =>
        {
            var h = Header(); h.Scene = "<size=999>scene</size>\n\u202e"; h.Participants = new[] { "<b>Alice</b>\t\0Bob", "<sprite=1>" };
            h.Route = "<link=bad>route</link>\r\nmore"; h.GameVersion = "<color=red>version</color>";
            var item = MemoriesLibraryModel.FromHighlight("C:/<b>source</b>.peakreplay", h, "<sprite=3>failure\u202e\0");
            Check(!item.Playable && item.Path == "C:/<b>source</b>.peakreplay");
            foreach (string text in new[] { item.Title, item.Subtitle, item.Details })
            {
                Check(!text.Contains('<') && !text.Contains('>') && !text.Contains('\u202e') && !text.Contains('\0') && !text.Contains('\r') && !text.Contains('\t'));
                Check(text.All(c => !char.IsControl(c) || c == '\n'));
            }
            Check(item.Details.Contains("Alice") && item.Details.Contains("Bob") && item.Details.Contains("failure"));
        });
        test("memories library bounded labels preserve schema warnings and do not split emoji pairs", () =>
        {
            var h = Header(); h.Route = new string('r', 4096); h.Scene = new string('s', 200);
            h.Participants = Enumerable.Range(0, 16).Select(_ => string.Concat(Enumerable.Repeat("🧗", 100))).ToArray();
            var item = MemoriesLibraryModel.FromHighlight(new string('f', 1000) + ".peakreplay", h, "");
            Check(item.Playable && item.Title.Length <= MemoriesLibraryModel.TitleLimit && item.Subtitle.Length <= MemoriesLibraryModel.SubtitleLimit);
            Check(item.Details.Length <= MemoriesLibraryModel.DetailsLimit && item.Details.Contains("状态条"));
            foreach (string text in new[] { item.Title, item.Subtitle, item.Details }) Check(ValidUtf16(text));
        });
    }
    private static ReplayHeader Header() => new()
    {
        Scene = "Level_Test", Duration = 60, FrameCount = 3601, SampleHz = 60, Participants = new[] { "Alice", "Bob" },
        Route = "Shore,Roots,Alpine", GameVersion = "1.2.3", StartedUtc = "2026-09-26T01:02:03Z", SavedUtc = "2026-09-26T01:03:03Z",
    };
    private static FullReplayInfo Full()
    {
        var h = Header(); h.Duration = 0; h.FrameCount = 0;
        return new FullReplayInfo
        {
            Header = h, Duration = 3723, FrameCount = 3601, Complete = true, ActorIds = new[] { "actor-a", "actor-b" },
            Pages = new[] { new FullReplayPage { Start = 0, End = 10, FrameCount = 601 } },
        };
    }
    private static bool ValidUtf16(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i])) { if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false; }
            else if (char.IsLowSurrogate(text[i])) return false;
        }
        return true;
    }
    private static void Check(bool value) { if (!value) throw new Exception("Assertion failed."); }
}
