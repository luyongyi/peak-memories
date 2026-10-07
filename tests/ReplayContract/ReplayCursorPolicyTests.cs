using PeakReplayLab;

internal static class ReplayCursorPolicyTests
{
    public static void Run(Action<string, Action> test)
    {
        test("replay cursor stays free without per-frame writes while operating controls", () =>
        {
            var cursor = new ReplayCursorPolicy();
            Check(cursor.Update(true, false) && !cursor.Looking && !cursor.ReadLookDelta);
            for (int i = 0; i < 5000; i++)
                Check(!cursor.Update(true, false) && !cursor.Looking && !cursor.ReadLookDelta);
        });
        test("right mouse captures once and rejects the initial UI or warp delta", () =>
        {
            var cursor = Ready();
            Check(cursor.Update(true, true) && cursor.Looking && !cursor.ReadLookDelta);
            Check(!cursor.Update(true, true) && cursor.Looking && cursor.ReadLookDelta);
        });
        test("camera rotation while right mouse is held never rewrites the cursor", () =>
        {
            var cursor = Ready(); cursor.Update(true, true);
            for (int i = 0; i < 5000; i++)
                Check(!cursor.Update(true, true) && cursor.Looking && cursor.ReadLookDelta);
        });
        test("right mouse release restores the UI pointer once and stops camera delta", () =>
        {
            var cursor = Ready(); cursor.Update(true, true); cursor.Update(true, true);
            Check(cursor.Update(true, false) && !cursor.Looking && !cursor.ReadLookDelta);
            Check(!cursor.Update(true, false) && !cursor.Looking && !cursor.ReadLookDelta);
            Check(cursor.Update(true, true) && cursor.Looking && !cursor.ReadLookDelta);
        });
        test("losing application focus releases camera capture and cannot recapture in background", () =>
        {
            var cursor = Ready(); cursor.Update(true, true); cursor.Update(true, true);
            Check(cursor.Update(false, true) && !cursor.Looking && !cursor.ReadLookDelta);
            for (int i = 0; i < 100; i++)
                Check(!cursor.Update(false, i % 2 == 0) && !cursor.Looking && !cursor.ReadLookDelta);
        });
        test("returning with right mouse held requires a release before camera recaptures", () =>
        {
            var cursor = Ready(); cursor.Update(true, true); cursor.Update(false, true);
            Check(cursor.Update(true, true) && !cursor.Looking && !cursor.ReadLookDelta);
            for (int i = 0; i < 100; i++)
                Check(!cursor.Update(true, true) && !cursor.Looking && !cursor.ReadLookDelta);
            Check(!cursor.Update(true, false) && !cursor.Looking && !cursor.ReadLookDelta);
            Check(cursor.Update(true, true) && cursor.Looking && !cursor.ReadLookDelta);
            Check(!cursor.Update(true, true) && cursor.ReadLookDelta);
        });
        test("returning with right mouse released keeps the pointer free and allows the next press", () =>
        {
            var cursor = Ready(); cursor.Update(false, false);
            Check(cursor.Update(true, false) && !cursor.Looking && !cursor.ReadLookDelta);
            Check(!cursor.Update(true, false));
            Check(cursor.Update(true, true) && cursor.Looking && !cursor.ReadLookDelta);
        });
        test("opening replay while right mouse is already held does not steal pointer capture", () =>
        {
            var cursor = new ReplayCursorPolicy();
            Check(cursor.Update(true, true) && !cursor.Looking && !cursor.ReadLookDelta);
            Check(!cursor.Update(true, true) && !cursor.Looking);
            Check(!cursor.Update(true, false));
            Check(cursor.Update(true, true) && cursor.Looking && !cursor.ReadLookDelta);
        });
        test("opening replay in the background never captures until a fresh focused press", () =>
        {
            var cursor = new ReplayCursorPolicy();
            Check(cursor.Update(false, false) && !cursor.Looking);
            Check(cursor.Update(true, true) && !cursor.Looking);
            Check(!cursor.Update(true, false));
            Check(cursor.Update(true, true) && cursor.Looking && !cursor.ReadLookDelta);
        });
    }

    private static ReplayCursorPolicy Ready()
    {
        var cursor = new ReplayCursorPolicy(); cursor.Update(true, false); return cursor;
    }
    private static void Check(bool value)
    {
        if (!value) throw new Exception("Replay cursor transition violated.");
    }
}
