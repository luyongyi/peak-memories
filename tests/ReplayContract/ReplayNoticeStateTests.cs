using PeakReplayLab;

internal static class ReplayNoticeStateTests
{
    public static void Run(Action<string, Action> test)
    {
        test("recording failure survives ordinary toast expiry and time spent behind loading or menus", () =>
        {
            var state = new ReplayNoticeState(); const string failure = "完整录像已停止，未保存完整录像。";
            state.Show(failure, 0);
            var initial = state.Present(0, failure); Check(initial.Text == failure && initial.Error && initial.Alpha == 1);
            // There need not be a Tick while the native loading UI hides notes.
            var returned = state.Present(3600, failure);
            Check(returned.Text == failure && returned.Error && returned.Persistent && returned.Alpha == 1);
        });
        test("new ordinary notes remain visible without replacing a failed recorder", () =>
        {
            var state = new ReplayNoticeState(); const string failure = "录像已停止";
            state.Show("读取列表完成", 1);
            var combined = state.Present(2, failure);
            Check(combined.Text == failure + "\n读取列表完成" && combined.Error);
            state.Show("正在处理片段", 3);
            Check(state.Present(4, failure).Text == failure + "\n正在处理片段");
            Check(state.Present(20, failure).Text == failure);
        });
        test("recording failure and diagnostic text retain stable content without idle frame allocations", () =>
        {
            var state = new ReplayNoticeState(); state.Show("后台操作完成", 0);
            var initial = state.Present(1, "录制异常", "60 Hz");
            Check(initial.Text == "录制异常\n后台操作完成\n60 Hz" && initial.HasStats && initial.Alpha == 1);
            for (int i = 0; i < 100; i++) state.Present(1, "录制异常", "60 Hz");
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 6000; i++) Check(ReferenceEquals(initial.Text, state.Present(1, "录制异常", "60 Hz").Text));
            Check(GC.GetAllocatedBytesForCurrentThread() - before == 0);
        });
        test("explicit recorder restart clears its matching old toast without discarding an unrelated note", () =>
        {
            var state = new ReplayNoticeState(); state.Show("录制异常", 0);
            Check(state.Present(1, "录制异常").Visible);
            state.Dismiss("录制异常"); Check(!state.Present(2).Visible);
            state.Show("列表刷新完成", 3); state.Dismiss("录制异常");
            Check(state.Present(4).Text == "列表刷新完成" && !state.Present(4).Error);
        });
        test("ordinary notices still fade and expire without a permanent game banner", () =>
        {
            var state = new ReplayNoticeState(); Check(!state.Present(0).Visible);
            state.Show("保存完成", 10);
            Check(state.Present(16).Alpha == 1 && state.Present(16).Text == "保存完成");
            Check(Math.Abs(state.Present(16.75).Alpha - .5f) < 1e-6);
            Check(!state.Present(17).Visible && !state.Present(1000).Visible);
        });
    }
    private static void Check(bool value) { if (!value) throw new Exception("Recording notice presentation assertion failed."); }
}
