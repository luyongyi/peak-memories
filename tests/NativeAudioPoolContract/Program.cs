using System.Runtime.CompilerServices;
using PeakReplayLab;

int passed = 0;
void Check(bool condition) { if (!condition) throw new Exception("Native audio pool assertion failed."); }
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); Environment.Exit(1); }
}
Test("real native pool creation, start and stop Harmony hooks observe each transition once", () =>
{
    var events = new List<string>(); var pool = new SFX_Player();
    using (var observer = new AudioPoolNativeObserver(_ => events.Add("create"), _ => events.Add("start"), _ => events.Add("stop")))
    {
        var voice = pool.Create(); voice.StartPlaying(); voice.StartPlaying(); voice.StopPlaying(); voice.StopPlaying();
        voice.StartPlaying(); voice.StopPlaying();
        Check(events.SequenceEqual(new[] { "create", "start", "stop", "start", "stop" }));
        Check(voice.NativeStarts == 2 && voice.NativeStops == 2);
    }
    pool.Create().StartPlaying(); Check(events.Count == 5);
});
Test("observer callback failures never prevent native sound playback or stop", () =>
{
    using var observer = new AudioPoolNativeObserver(_ => throw new Exception(), _ => throw new Exception(), _ => throw new Exception());
    var voice = new SFX_Player().Create(); voice.StartPlaying(); Check(voice.isPlaying); voice.StopPlaying(); Check(!voice.isPlaying);
});
Test("only an observed idle native exclusive owner can skip Unity liveness reads", () =>
{
    Check(AudioPoolCapturePolicy.CanSkipIdle(true, false, true, false));
    Check(!AudioPoolCapturePolicy.CanSkipIdle(false, false, true, false));
    Check(!AudioPoolCapturePolicy.CanSkipIdle(true, true, true, false));
    Check(!AudioPoolCapturePolicy.CanSkipIdle(true, false, false, false));
    Check(!AudioPoolCapturePolicy.CanSkipIdle(true, false, true, true));
});
Test("sub-snapshot native start and stop preserve the exact short voice end", () =>
{
    AudioReplayFrame? recorded = null;
    using var observer = new AudioPoolNativeObserver(_ => { }, _ => recorded = new AudioReplayFrame
    { Key = "short", Clip = "native", StartedAt = 10, AnchorTime = 10, Duration = .2f, Volume = 1, Pitch = 1 },
    _ => recorded = AudioReplayRules.CompletedBetweenSamples(recorded!, 10.005));
    var voice = new SFX_Player().Create(); voice.StartPlaying(); voice.StopPlaying();
    Check(recorded != null && recorded.Transient && Math.Abs(recorded.EndTime - 10.005) < .000001);
    AudioReplayRules.Validate(recorded!);
});
Console.WriteLine($"Passed {passed} native audio pool contracts.");

public sealed class SFX_Player
{
    [MethodImpl(MethodImplOptions.NoInlining)] private SFX_Source CreateNewSource() => new();
    [MethodImpl(MethodImplOptions.NoInlining)] public SFX_Source Create() => CreateNewSource();
    public sealed class SFX_Source
    {
        public bool isPlaying;
        public int NativeStarts, NativeStops;
        [MethodImpl(MethodImplOptions.NoInlining)] public void StartPlaying() { if (!isPlaying) { isPlaying = true; NativeStarts++; } }
        [MethodImpl(MethodImplOptions.NoInlining)] public void StopPlaying() { if (isPlaying) { isPlaying = false; NativeStops++; } }
    }
}
