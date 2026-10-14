using PeakReplayLab;

int passed = 0;
void Check(bool condition) { if (!condition) throw new Exception("Scene voice birth assertion failed."); }
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); Environment.Exit(1); }
}
AudioReplayFrame Voice(string key = "birth", double start = .01, bool transient = false) => new()
{
    Key = key, Clip = "native|480|48000|1", StartedAt = start, AnchorTime = start,
    Offset = 0, Duration = .01f, Volume = .5f, Pitch = 1, Transient = transient, EndTime = 1d / 30,
};

Test("schema22 short native voice is admitted at its birth before the next scene snapshot", () =>
{
    var policy = new AudioSceneEventPolicy(); var born = Voice(transient: true); var right = new[] { born };
    Check(policy.Select(Array.Empty<AudioReplayFrame>(), right, .009, 0, 1d / 30, true).Count == 0);
    var selected = policy.Select(Array.Empty<AudioReplayFrame>(), right, .01, 0, 1d / 30, true);
    Check(selected.Count == 1 && ReferenceEquals(selected[0], born));
});
Test("an advancing global tick can sound a birth hidden in the 30 Hz scene interval", () =>
{
    var born = Voice(transient: true); var policy = new AudioSceneEventPolicy();
    var selected = policy.Select(Array.Empty<AudioReplayFrame>(), new[] { born }, 1d / 60, 0, 1d / 30, true);
    Check(selected.Count == 1 && AudioReplayRules.ShouldSound(false, 1, false));
    Check(AudioReplayRules.MayStartTransient(selected[0].Transient, false, false));
    Check(!AudioReplayRules.MayStartTransient(selected[0].Transient, false, true));
});
Test("legacy playback and absent scene clocks retain the original left directory", () =>
{
    var policy = new AudioSceneEventPolicy(); var left = Array.Empty<AudioReplayFrame>(); var right = new[] { Voice() };
    Check(ReferenceEquals(left, policy.Select(left, right, .02, 0, 1d / 30, false)));
    Check(ReferenceEquals(left, policy.Select(left, right, .02, double.NaN, double.NaN, true)));
});
Test("the left observation wins for an existing voice with later parameter changes", () =>
{
    var policy = new AudioSceneEventPolicy(); var original = Voice(); var changed = Voice(); changed.Pitch = 2;
    var selected = policy.Select(new[] { original }, new[] { changed }, .02, 0, 1d / 30, true);
    Check(selected.Count == 1 && ReferenceEquals(selected[0], original));
});
Test("same pooled native source with independent playback keys admits each real birth once", () =>
{
    var policy = new AudioSceneEventPolicy(); var first = Voice("sound:1", 0); var second = Voice("sound:2", .02, true);
    var selected = policy.Select(new[] { first }, new[] { second }, .025, 0, 1d / 30, true);
    Check(selected.Count == 2 && ReferenceEquals(selected[0], first) && ReferenceEquals(selected[1], second));
});
Test("late discovery cannot fabricate a voice start before the current scene bracket", () =>
{
    var policy = new AudioSceneEventPolicy();
    Check(policy.Select(Array.Empty<AudioReplayFrame>(), new[] { Voice(start: 1) }, 10.02, 10, 10.033, true).Count == 0);
});
Test("loop birth follows its native start while its cursor supports time before first observation", () =>
{
    var policy = new AudioSceneEventPolicy(); var born = Voice(); born.Loop = true; born.Duration = 5;
    born.AnchorTime = 1d / 30; born.Offset = born.AnchorTime - born.StartedAt;
    var selected = policy.Select(Array.Empty<AudioReplayFrame>(), new[] { born }, .02, 0, 1d / 30, true);
    Check(selected.Count == 1 && Math.Abs(AudioReplayRules.Cursor(born, .02) - .01) < .0000001);
});
Test("negative opening clocks remain valid after crop and are not rebased twice", () =>
{
    var policy = new AudioSceneEventPolicy(); var born = Voice(start: 100.01);
    var cropped = AudioReplayRules.Rebase(born, 100.02);
    var selected = policy.Select(Array.Empty<AudioReplayFrame>(), new[] { cropped }, 0, -.02, .013, true);
    Check(selected.Count == 1 && Math.Abs(cropped.StartedAt + .01) < .0000001);
});
Test("seek and pause remain silent even when a right-side birth is selected", () =>
{
    var born = Voice(transient: true); var policy = new AudioSceneEventPolicy();
    Check(policy.Select(Array.Empty<AudioReplayFrame>(), new[] { born }, .02, 0, 1d / 30, true).Count == 1);
    Check(!AudioReplayRules.ShouldSound(false, 1, true)); Check(!AudioReplayRules.ShouldSound(true, 1, false));
    Check(!AudioReplayRules.MayStartTransient(true, true, false));
});
Test("cut or unbounded scene intervals do not bridge unrelated future voices", () =>
{
    var policy = new AudioSceneEventPolicy(); var left = Array.Empty<AudioReplayFrame>(); var right = new[] { Voice() };
    foreach (var range in new[] { (0d, 0d), (1d, 0d), (0d, 1d), (double.NaN, .033), (0d, double.PositiveInfinity) })
        Check(ReferenceEquals(left, policy.Select(left, right, .02, range.Item1, range.Item2, true)));
    Check(ReferenceEquals(left, policy.Select(left, right, -.02, 0, .033, true)));
    Check(ReferenceEquals(left, policy.Select(left, right, .04, 0, .033, true)));
});
Test("voice admission respects the existing simultaneous voice bound", () =>
{
    var policy = new AudioSceneEventPolicy();
    var left = Enumerable.Range(0, AudioReplayRules.MaxVoices - 1).Select(i => Voice("left:" + i, 0)).ToArray();
    var right = new[] { Voice("new:1"), Voice("new:2") };
    Check(policy.Select(left, right, .02, 0, .033, true).Count == AudioReplayRules.MaxVoices);
});
Test("repeated scene birth selection allocates no per-frame arrays or lookup directories", () =>
{
    var policy = new AudioSceneEventPolicy(); var left = new[] { Voice("held", 0) }; var right = new[] { Voice() };
    for (int i = 0; i < 100; i++) policy.Select(left, right, .02, 0, .033, true);
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 6000; i++) policy.Select(left, right, .02, 0, .033, true);
    Check(GC.GetAllocatedBytesForCurrentThread() == before);
});
Console.WriteLine($"TOTAL: {passed} scene audio birth checks passed. Native audio playback is not simulated.");
