using PeakReplayLab;

int passed = 0;
void Check(bool condition) { if (!condition) throw new Exception("Audio seek preparation assertion failed."); }
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); Environment.Exit(1); }
}
AudioReplayFrame Voice(string clip, string key = "voice", double start = 0, bool transient = false) => new()
{ Key = key, Clip = clip, StartedAt = start, AnchorTime = start, Duration = 1, Transient = transient, EndTime = .03 };

Test("four unloaded target sounds load on four different frames and yield before world application", () =>
{
    var policy = new AudioSeekPreparationPolicy();
    var voices = Enumerable.Range(0, 4).Select(i => Voice("clip:" + i, "voice:" + i)).ToArray();
    var loaded = new HashSet<string>(); var calls = new List<string>();
    AudioSeekLoadState State(string key) => loaded.Contains(key) ? AudioSeekLoadState.Loaded : AudioSeekLoadState.Unloaded;
    void Load(string key) { loaded.Add(key); calls.Add(key); }
    for (int frame = 1; frame <= 4; frame++)
    {
        Check(!policy.TryPrepare(2, frame, frame * .02, voices, State, Load));
        Check(calls.Count == frame);
        Check(!policy.TryPrepare(2, frame, frame * .02, voices, State, Load));
        Check(calls.Count == frame);
    }
    Check(policy.TryPrepare(2, 5, .1, voices, State, Load));
});
Test("a superseding seek never spends a second native load in the same rendered frame", () =>
{
    var policy = new AudioSeekPreparationPolicy(); var calls = new List<string>();
    AudioSeekLoadState State(string _) => AudioSeekLoadState.Unloaded;
    Check(!policy.TryPrepare(1, 10, 0, new[] { Voice("old") }, State, calls.Add));
    Check(!policy.TryPrepare(2, 10, .001, new[] { Voice("new") }, State, calls.Add));
    Check(calls.SequenceEqual(new[] { "old" }));
    Check(!policy.TryPrepare(2, 11, .02, new[] { Voice("new") }, State, calls.Add));
    Check(calls.SequenceEqual(new[] { "old", "new" }));
    Check(policy.TryPrepare(2, 12, .04, new[] { Voice("new") }, State, calls.Add));
});
Test("duplicate voice clips load once and unrelated catalog clips are never requested", () =>
{
    var policy = new AudioSeekPreparationPolicy(); var calls = new List<string>();
    AudioSeekLoadState State(string _) => AudioSeekLoadState.Unloaded;
    var voices = new[] { Voice("shared", "one"), Voice("shared", "two") };
    Check(!policy.TryPrepare(1, 1, 0, voices, State, calls.Add));
    Check(policy.TryPrepare(1, 2, .02, voices, State, calls.Add));
    Check(calls.SequenceEqual(new[] { "shared" }));
});
Test("existing background loads hold the clock and timeout visibly without restarting or dropping a voice", () =>
{
    var policy = new AudioSeekPreparationPolicy(); var voice = Voice("loading");
    var voices = new[] { voice }; int calls = 0;
    AudioSeekLoadState State(string _) => AudioSeekLoadState.Loading;
    Check(!policy.TryPrepare(1, 1, 10, voices, State, _ => calls++));
    Check(!policy.TryPrepare(1, 2, 14.99, voices, State, _ => calls++));
    Check(policy.TryPrepare(1, 3, 15, voices, State, _ => calls++));
    Check(policy.TimedOutClip == "loading" && calls == 0 && ReferenceEquals(voices[0], voice));
});
Test("failed unavailable and already-loaded sounds retain normal native fallback without pending forever", () =>
{
    foreach (var value in new[] { AudioSeekLoadState.Failed, AudioSeekLoadState.Unavailable, AudioSeekLoadState.Loaded })
    {
        var policy = new AudioSeekPreparationPolicy(); int calls = 0;
        Check(policy.TryPrepare(1, 1, 0, new[] { Voice("native") }, _ => value, _ => calls++));
        Check(calls == 0);
    }
});
Test("an unsuccessful native load is attempted once before preserving the existing native start path", () =>
{
    var policy = new AudioSeekPreparationPolicy(); int calls = 0; var voices = new[] { Voice("failed") };
    AudioSeekLoadState State(string _) => AudioSeekLoadState.Unloaded;
    Check(!policy.TryPrepare(1, 1, 0, voices, State, _ => calls++));
    Check(policy.TryPrepare(1, 2, .02, voices, State, _ => calls++));
    Check(calls == 1);
});
Test("preparation does not admit future transient births or consume their playback eligibility", () =>
{
    var preparation = new AudioSeekPreparationPolicy(); var selection = new AudioSceneEventPolicy();
    var born = Voice("short", "birth", .02, true); var right = new[] { born }; int calls = 0;
    var before = selection.Select(Array.Empty<AudioReplayFrame>(), right, .01, 0, .033, true);
    Check(preparation.TryPrepare(1, 1, 0, before, _ => AudioSeekLoadState.Unloaded, _ => calls++));
    Check(calls == 0);
    var after = selection.Select(Array.Empty<AudioReplayFrame>(), right, .02, 0, .033, true);
    Check(after.Count == 1 && ReferenceEquals(after[0], born));
    Check(AudioReplayRules.ShouldSound(false, 1, false) && AudioReplayRules.MayStartTransient(true, false, false));
});
Test("audio preparation notice changes immediately at the same requested second", () =>
{
    var notice = new ReplayBufferingNotice();
    Check(notice.Update(true, 2300.2) && notice.Text.Contains("正在读取"));
    Check(notice.Update(true, 2300.2, true) && notice.Text == "正在准备声音资源…");
    Check(!notice.Update(true, 2300.3, true));
    Check(notice.Update(true, 2300.3, false) && notice.Text.Contains("正在读取"));
    Check(notice.Update(false, 2300.3) && !notice.Visible);
});
Test("seek preparation excludes suppressed transients future births and already-ended one-shots", () =>
{
    var transient = Voice("short", transient: true);
    var future = Voice("future", start: 10);
    var ended = Voice("ended");
    Check(!AudioSeekPreparationPolicy.ShouldPrepare(transient, .02));
    Check(!AudioSeekPreparationPolicy.ShouldPrepare(future, 9.99));
    Check(!AudioSeekPreparationPolicy.ShouldPrepare(ended, 1));
    Check(!AudioSeekPreparationPolicy.ShouldPrepare(ended, .999));
    Check(AudioSeekPreparationPolicy.ShouldPrepare(ended, .998));
    Check(AudioSeekPreparationPolicy.ShouldPrepare(future, 10));
    ended.Loop = true;
    Check(AudioSeekPreparationPolicy.ShouldPrepare(ended, 20));
});
Test("cropped active voices retain negative opening clocks and later transient birth eligibility", () =>
{
    var active = Voice("active", start: 100); active.AnchorTime = 100; active.Offset = 0;
    var cropped = AudioReplayRules.Rebase(active, 100.25);
    Check(cropped.StartedAt == -.25 && AudioSeekPreparationPolicy.ShouldPrepare(cropped, 0));
    var birth = Voice("birth", start: .01, transient: true);
    Check(!AudioSeekPreparationPolicy.ShouldPrepare(birth, .02));
    Check(AudioReplayRules.MayStartTransient(birth.Transient, false, false));
});
Console.WriteLine($"TOTAL: {passed} selective audio seek preparation checks passed. Native audio loading duration is not simulated.");
