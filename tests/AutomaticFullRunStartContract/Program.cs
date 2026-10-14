using PeakReplayLab;

int passed = 0;
void Check(bool condition, string message = "Automatic recording start assertion failed") { if (!condition) throw new Exception(message); }
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); Environment.Exit(1); }
}

Test("initial capture failure does not claim the scene and its first accepted frame is written", () =>
{
    var run = new AcceptedFrameHarness();
    for (int i = 0; i < 3; i++)
    {
        try { run.Capture(i, valid: false); } catch (InvalidDataException) { }
        Check(run.Attempts == 0 && run.StoppedScene == int.MinValue && run.Writer == null && run.Buffer.Count == 0);
    }
    run.Capture(3); run.Capture(4);
    Check(run.Attempts == 1 && run.Writer!.SequenceEqual(new[] { 3, 4 }) && run.Buffer.SequenceEqual(new[] { 3, 4 }));
});
Test("automatic start includes the exact accepted first-frame instance and byte estimate once", () =>
{
    var run = new AcceptedFrameHarness(); var first = new Frame(17, 739);
    run.AcceptValidated(first);
    Check(ReferenceEquals(run.FrameWriter![0], first) && run.ByteWriter!.Single() == 739 && run.Attempts == 1);
    run.AcceptValidated(new Frame(18, 841));
    Check(run.Attempts == 1 && run.FrameWriter.Count == 2 && run.ByteWriter!.SequenceEqual(new long[] { 739, 841 }));
});
Test("failed file creation retains accepted rolling history but cannot retry on later frames", () =>
{
    var run = new AcceptedFrameHarness { FailCreation = true };
    try { run.Capture(1); } catch (IOException) { }
    Check(run.Attempts == 1 && run.StoppedScene == run.Scene && run.Buffer.SequenceEqual(new[] { 1 }));
    run.FailCreation = false;
    for (int i = 2; i <= 10000; i++) run.Capture(i);
    Check(run.Attempts == 1 && run.Writer == null && run.Buffer.Count == 10000);
});
Test("F4 stop prevents automatic restart while rolling samples continue", () =>
{
    var run = new AcceptedFrameHarness(); run.Capture(1); run.F4Stop();
    for (int i = 2; i <= 100; i++) run.Capture(i);
    Check(run.Attempts == 1 && run.Writer == null && !run.Continuous && run.Buffer.Count == 100);
});
Test("writer failure with continuous setting still enabled blocks automatic retry in that scene", () =>
{
    var run = new AcceptedFrameHarness(); run.Capture(1); run.WriterStop();
    run.Closing = false; run.Capture(2);
    Check(run.Continuous && run.Attempts == 1 && run.Writer == null && run.Buffer.Count == 2);
});
Test("closing the preceding writer defers a new scene start without consuming its attempt", () =>
{
    var run = new AcceptedFrameHarness { Closing = true };
    run.Capture(1); Check(run.Attempts == 0 && run.StoppedScene == int.MinValue);
    run.Closing = false; run.Capture(2);
    Check(run.Attempts == 1 && run.Writer!.SequenceEqual(new[] { 2 }) && run.Buffer.SequenceEqual(new[] { 1, 2 }));
});
Test("scene transition resets eligibility and sends the next scene's accepted frame", () =>
{
    var run = new AcceptedFrameHarness(); run.Capture(1); run.WriterStop(); run.Closing = false;
    run.Scene = 43; run.StoppedScene = int.MinValue; run.Capture(2);
    Check(run.Attempts == 2 && run.Writer!.SequenceEqual(new[] { 2 }) && run.StoppedScene == 43);
});
Test("explicit F4 or automation chapter start keeps its immediate writer and appends the next frame", () =>
{
    var run = new AcceptedFrameHarness(); run.ExplicitStart();
    Check(run.Writer != null && run.Writer.Count == 0 && run.Attempts == 1);
    run.Capture(1); Check(run.Attempts == 1 && run.Writer!.SequenceEqual(new[] { 1 }));
    run.WriterStop(); run.Closing = false; run.ExplicitStart(); run.Capture(2);
    Check(run.Attempts == 2 && run.Writer!.SequenceEqual(new[] { 2 }) && run.Buffer.SequenceEqual(new[] { 1, 2 }));
});
Test("rolling-only mode and unavailable capture cannot consume an automatic attempt", () =>
{
    int stopped = int.MinValue;
    Check(!AutomaticFullRunStartPolicy.TryClaim(false, false, false, true, 7, ref stopped) && stopped == int.MinValue);
    Check(!AutomaticFullRunStartPolicy.TryClaim(true, false, false, false, 7, ref stopped) && stopped == int.MinValue);
    Check(!AutomaticFullRunStartPolicy.TryClaim(true, true, false, true, 7, ref stopped) && stopped == int.MinValue);
    Check(AutomaticFullRunStartPolicy.TryClaim(true, false, false, true, 7, ref stopped) && stopped == 7);
    Check(!AutomaticFullRunStartPolicy.TryClaim(true, false, false, true, 7, ref stopped));
});
Test("warmed accepted policy performs no managed allocation or history-dependent work", () =>
{
    int stopped = int.MinValue;
    AutomaticFullRunStartPolicy.TryClaim(true, false, false, true, 7, ref stopped);
    for (int i = 0; i < 1000; i++) AutomaticFullRunStartPolicy.TryClaim(true, true, false, true, 7, ref stopped);
    long before = GC.GetAllocatedBytesForCurrentThread(); int starts = 0;
    for (int i = 0; i < 100000; i++) if (AutomaticFullRunStartPolicy.TryClaim(true, true, false, true, 7, ref stopped)) starts++;
    Check(starts == 0 && GC.GetAllocatedBytesForCurrentThread() == before);
});
Console.WriteLine($"TOTAL: {passed} automatic accepted-frame start policy contracts passed. Native scene loading and Unity capture are not simulated.");

internal sealed record Frame(int Value, long Bytes);

// Capture stages model the production boundary: validation and rolling-buffer
// admission precede the cached accepted-frame callback. The callback uses the
// real production claim policy; writer IO is deliberately represented by a
// bounded in-memory fake, so failure tests never create user recording files.
internal sealed class AcceptedFrameHarness
{
    internal bool Continuous = true, Closing, FailCreation;
    internal int Scene = 42, StoppedScene = int.MinValue, Attempts;
    internal List<int> Buffer = new();
    internal List<int>? Writer;
    internal List<Frame>? FrameWriter;
    internal List<long>? ByteWriter;
    internal void Capture(int value, bool valid = true)
    {
        if (!valid) throw new InvalidDataException("capture validation failed before acceptance");
        Buffer.Add(value);
        AcceptValidated(new Frame(value, value + 100));
    }
    internal void AcceptValidated(Frame frame)
    {
        if (AutomaticFullRunStartPolicy.TryClaim(Continuous, Writer != null, Closing, true, Scene, ref StoppedScene)) CreateWriter();
        if (Writer == null) return;
        Writer.Add(frame.Value); FrameWriter!.Add(frame); ByteWriter!.Add(frame.Bytes);
    }
    private void CreateWriter()
    {
        Attempts++;
        if (FailCreation) throw new IOException("controlled file creation failure");
        Writer = new(); FrameWriter = new(); ByteWriter = new();
    }
    internal void WriterStop() { StoppedScene = Scene; Writer = null; FrameWriter = null; ByteWriter = null; Closing = true; }
    internal void F4Stop() { Continuous = false; WriterStop(); }
    internal void ExplicitStart() { Continuous = true; StoppedScene = Scene; CreateWriter(); }
}
