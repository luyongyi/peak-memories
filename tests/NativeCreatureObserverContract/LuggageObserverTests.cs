using PeakReplayLab;

internal static class LuggageObserverTests
{
    public static void Run(Action<string, Action> test)
    {
        test("luggage mirage material changes are observed after native enable and disable", () =>
        {
            var chest = new Luggage(); var source = new MirageLuggage { Parent = chest }; var values = new List<float>();
            using var observer = new LuggageEventObserver(e => { Check(e.Kind == LuggageEventKind.Appearance && e.Source == chest); values.Add(source.Mirage); });
            source.OnEnable(); chest.gameObject.SetActive(false); source.OnDisable();
            Check(values.SequenceEqual(new[] { 1f, 0f }) && source.LastIncludeInactive);
        });
        test("creature mirage fake chests never enter the luggage catalogue", () =>
        {
            int count = 0; using var observer = new LuggageEventObserver(_ => count++);
            var source = new MirageLuggage { Parent = new Capybara() }; source.OnEnable(); source.OnDisable();
            Check(count == 0 && source.Mirage == 0);
        });
        test("respawn skeleton change is observed after native removal without starting gameplay", () =>
        {
            var chest = new RespawnChest(); int count = 0;
            using var observer = new LuggageEventObserver(e => { Check(!chest.Skeleton && !chest.IsOpen && e.Kind == LuggageEventKind.Appearance); count++; });
            chest.RemoveSkeletonRPC(); Check(count == 1);
        });
        test("luggage open notification retains remote delay and one-shot transition", () =>
        {
            UnityEngine.Time.timeAsDouble = 10; UnityEngine.Time.frameCount = 42;
            var events = new List<LuggageEvent>(); using var observer = new LuggageEventObserver(events.Add);
            var local = new Luggage(); local.OpenLuggageRPC(true); local.OpenLuggageRPC(true);
            var remote = new Luggage(); remote.OpenLuggageRPC(false);
            Check(events.Count == 2 && events[0].Time == 10 && events[1].Time == 12 && events.All(e => e.Frame == 42 && e.Kind == LuggageEventKind.Open));
        });
        test("replay and failing observers cannot alter native luggage appearance actions", () =>
        {
            int calls = 0; using var observer = new LuggageEventObserver(_ => { calls++; throw new Exception("listener"); });
            var chest = new RespawnChest(); var source = new MirageLuggage { Parent = chest };
            ReplaySafety.Active = true;
            try { source.OnEnable(); chest.RemoveSkeletonRPC(); Check(calls == 0 && source.Mirage == 1 && !chest.Skeleton); }
            finally { ReplaySafety.Active = false; }
            source.OnDisable(); Check(calls == 1 && source.Mirage == 0);
        });
        test("disposing luggage observer removes appearance callbacks and permits a replacement", () =>
        {
            int first = 0, second = 0; var chest = new RespawnChest();
            var observer = new LuggageEventObserver(_ => first++); observer.Dispose(); observer.Dispose(); chest.RemoveSkeletonRPC();
            using var replacement = new LuggageEventObserver(_ => second++); chest.RemoveSkeletonRPC();
            Check(first == 0 && second == 1);
        });
    }
    private static void Check(bool value) { if (!value) throw new Exception("Luggage native event observation assertion failed."); }
}
