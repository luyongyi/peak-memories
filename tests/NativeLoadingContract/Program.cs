using System.Collections;
using PeakReplayLab;

int passed = 0;
void Check(bool condition, string message = "Native loading assertion failed.") { if (!condition) throw new Exception(message); }
void Test(string title, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + title); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + title + ": " + error); Environment.Exit(1); }
}
void Tick(LoadingScreenHandler handler, NativeReplayLoading load, int count = 1)
{ for (int i = 0; i < count; i++) { handler.Tick(); load.Check(); } }
void ExpectInvalid(Action action)
{
    try { action(); } catch (InvalidOperationException) { return; }
    throw new Exception("Expected controlled loader failure.");
}

Test("native plane and basic types are used without suspending the message queue", () =>
{
    foreach (var type in new[] { LoadingScreen.LoadingScreenType.Plane, LoadingScreen.LoadingScreenType.Basic })
    {
        var handler = LoadingScreenHandler.Reset();
        using var load = new NativeReplayLoading(type);
        Check(handler.LastType == type && !handler.DisabledQueue && load.Visible && LoadingScreenHandler.loading);
    }
});
Test("scene gate starts only after native fade-out and stays until explicitly released", () =>
{
    var handler = LoadingScreenHandler.Reset(); using var load = new NativeReplayLoading(LoadingScreen.LoadingScreenType.Plane);
    Check(!load.ProcessStarted && !load.Finished);
    Tick(handler, load, 3); Check(load.ProcessStarted && !load.Finished);
    Tick(handler, load, 100); Check(!load.Finished && LoadingScreenHandler.loading);
    load.Release(); Tick(handler, load); Check(!load.Finished, "Release skipped native fade-in.");
    Tick(handler, load, 5); Check(load.Finished && !LoadingScreenHandler.loading);
});
Test("completed native fade removes the delayed canvas before a frozen replay", () =>
{
    var handler = LoadingScreenHandler.Reset(); using var load = new NativeReplayLoading(LoadingScreen.LoadingScreenType.Plane);
    var screen = handler.Last!; load.Release(); Tick(handler, load, 10);
    Check(load.Finished && !load.Visible && !screen.canvas.enabled && screen.gameObject.Destroyed);
    Check(handler.Last == null && handler.StopAllCount == 0);
    Check(handler.Prefab.Mixer.Values["LoadingFade"] == 0 && handler.Prefab.Mixer.Values["UserVolume"] == -18);
});
Test("cancellation before the process gate still finishes the native fade", () =>
{
    var handler = LoadingScreenHandler.Reset(); using var load = new NativeReplayLoading(LoadingScreen.LoadingScreenType.Basic);
    load.Release(); Tick(handler, load, 10);
    Check(load.Finished && load.ProcessStarted && !LoadingScreenHandler.loading && !load.Visible);
});
Test("emergency disposal restores only the loading fade and its own loading flag", () =>
{
    var handler = LoadingScreenHandler.Reset(); var load = new NativeReplayLoading(LoadingScreen.LoadingScreenType.Plane);
    var screen = handler.Last!; Tick(handler, load, 3); load.Dispose(); load.Dispose();
    Check(!LoadingScreenHandler.loading && screen.gameObject.Destroyed && load.Finished);
    Check(handler.StopOneCount == 1 && handler.StopAllCount == 0);
    Check(handler.Prefab.Mixer.Values["LoadingFade"] == -3 && handler.Prefab.Mixer.Values["UserVolume"] == -18);
});
Test("disposal leaves unrelated handler coroutines running", () =>
{
    var handler = LoadingScreenHandler.Reset(); int ticks = 0;
    IEnumerator Other() { while (true) { ticks++; yield return null; } }
    handler.StartCoroutine(Other()); var load = new NativeReplayLoading(LoadingScreen.LoadingScreenType.Plane);
    load.Dispose(); int before = ticks; handler.Tick();
    Check(ticks == before + 1 && handler.StopAllCount == 0);
});
Test("a later foreign screen and its fade cannot be cleared by our disposal", () =>
{
    var handler = LoadingScreenHandler.Reset(); var load = new NativeReplayLoading(LoadingScreen.LoadingScreenType.Plane);
    var ours = handler.Last!; var foreign = new LoadingScreen { Mixer = handler.Prefab.Mixer };
    foreign.Mixer.SetFloat("LoadingFade", -47); handler.InstallForeign(foreign); load.Dispose();
    Check(LoadingScreenHandler.loading && ReferenceEquals(handler.Last, foreign) && foreign.canvas.enabled && !foreign.gameObject.Destroyed);
    Check(ours.gameObject.Destroyed && foreign.Mixer.Values["LoadingFade"] == -47);
});
Test("native failure after screen creation is cleaned up and reported only once", () =>
{
    var handler = LoadingScreenHandler.Reset(); handler.FailAfterScreen = true;
    var load = new NativeReplayLoading(LoadingScreen.LoadingScreenType.Plane); var screen = handler.Last!;
    handler.Tick(); handler.Tick(); ExpectInvalid(load.Check);
    load.Check(); load.Dispose(); Check(!LoadingScreenHandler.loading && screen.gameObject.Destroyed);
    Check(handler.Prefab.Mixer.Values["LoadingFade"] == -3);
});
Test("native failure before creating a screen does not strand the global flag", () =>
{
    var handler = LoadingScreenHandler.Reset(); handler.FailBeforeScreen = true;
    ExpectInvalid(() => new NativeReplayLoading(LoadingScreen.LoadingScreenType.Basic));
    Check(!LoadingScreenHandler.loading && handler.Last == null && handler.Prefab.Mixer.Values["LoadingFade"] == -3);
});
Test("existing game load is rejected without changing its screen or sound", () =>
{
    var handler = LoadingScreenHandler.Reset(); var foreign = new LoadingScreen(); handler.InstallForeign(foreign);
    ExpectInvalid(() => new NativeReplayLoading(LoadingScreen.LoadingScreenType.Basic));
    Check(LoadingScreenHandler.loading && ReferenceEquals(handler.Last, foreign) && handler.StopOneCount == 0);
});
Test("missing native animator is rejected before any loader state changes", () =>
{
    var handler = LoadingScreenHandler.Reset(); handler.Prefab.Animator = null;
    ExpectInvalid(() => new NativeReplayLoading(LoadingScreen.LoadingScreenType.Plane));
    Check(!LoadingScreenHandler.loading && handler.Last == null && handler.StopOneCount == 0);
});
Console.WriteLine($"TOTAL: {passed} native loading contract checks passed. No Unity rendering, audio or scene runtime is simulated.");
