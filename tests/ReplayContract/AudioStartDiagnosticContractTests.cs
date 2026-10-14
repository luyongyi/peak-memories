using PeakReplayLab;

internal static class AudioStartDiagnosticContractTests
{
    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("audio start diagnostics retain exact native threshold, identities and outside-call metadata", () =>
        {
            var probe = new ReplayAudioStartDiagnostics(60000);
            var before = new ReplayAudioStartDiagnostics.ClipMetadata(1, 1, true, false);
            var after = new ReplayAudioStartDiagnostics.ClipMetadata(2, 1, true, false);
            string clip = new string('c', 20), voice = new string('v', 20);
            probe.Observe(clip, voice, false, 710, 60000, 60999, 100, 10, 10, 0, before, after);
            check(probe.Snapshot().Length == 0);
            probe.Observe(clip, voice, false, 710.25, 60000, 61000, 100, 10, 10, 2, before, after);
            var result = probe.Snapshot(); check(result.Length == 1);
            var value = result[0];
            check(ReferenceEquals(value.Clip, clip) && ReferenceEquals(value.VoiceKey, voice));
            check(value.Operation == "Play" && value.ReplaySeconds == 710.25 && value.StartFrame == 10 && value.EndFrame == 10);
            check(value.Milliseconds == ReplayAudioStartDiagnostics.MinimumMilliseconds && value.StartRealtime == 100 && value.EndRealtime == 100 + 1d / 60);
            check(value.GlobalCollections == 2 && value.ClipBefore.Available && value.ClipBefore.LoadState == 1 && value.ClipAfter.LoadState == 2);
            check(value.ClipAfter.LoadType == 1 && value.ClipAfter.LoadInBackground && !value.ClipAfter.PreloadAudioData);
        });
        test("audio start ring is allocation-free, bounded and snapshots are detached", () =>
        {
            var probe = new ReplayAudioStartDiagnostics(60000);
            var metadata = new ReplayAudioStartDiagnostics.ClipMetadata(2, 0, false, true);
            probe.Observe("clip", "voice", false, 0, 0, 1200, 0, 0, 0, 0, metadata, metadata);
            var first = probe.Snapshot();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++)
                probe.Observe("clip", "voice", (i & 1) != 0, i, i * 60000L, i * 60000L + 1200, i, i, i, 0, metadata, metadata);
            check(GC.GetAllocatedBytesForCurrentThread() - allocated == 0);
            var last = probe.Snapshot();
            check(last.Length == ReplayAudioStartDiagnostics.Capacity && last[0].ReplaySeconds == 9872 && last[^1].ReplaySeconds == 9999);
            check(last[0].Operation == "Play" && last[^1].Operation == "UnPause");
            check(first.Length == 1 && first[0].ReplaySeconds == 0);
            last[0].Clip = "changed"; last[0].StartRealtime = -99;
            check(probe.Snapshot()[0].Clip == "clip" && probe.Snapshot()[0].StartRealtime == 9872);
        });
        test("audio start metadata availability and invalid tick handling are explicit", () =>
        {
            var probe = new ReplayAudioStartDiagnostics(60000);
            probe.Observe("clip", "voice", true, 1, 2000, 1999, 4, 1, 1, 0, default, default);
            check(probe.Snapshot().Length == 0);
            probe.Observe("clip", "voice", true, 1, 2000, 3200, 4, 1, 2, -2, default, default);
            var value = probe.Snapshot()[0];
            check(value.Operation == "UnPause" && value.Milliseconds == 20 && value.EndRealtime == 4.02);
            check(!value.ClipBefore.Available && !value.ClipAfter.Available && value.GlobalCollections == 0 && value.EndFrame == 2);
            bool failed = false;
            try { _ = new ReplayAudioStartDiagnostics(0); } catch (ArgumentOutOfRangeException) { failed = true; }
            check(failed);
        });
    }
}
