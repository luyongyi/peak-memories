using PeakReplayLab;

internal static class NativeEnvironmentObserverTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("environment lifecycle observers report each actual native family after initialization", () =>
        {
            var seen = new List<UnityEngine.Component>(); var wind = new WindChillZone();
            using var observer = new EnvironmentReplayObserver(source => { if (ReferenceEquals(source, wind)) Check(wind.NativeComplete); seen.Add(source); });
            wind.Awake(); wind.Start(); var storm = new StormVisual(); storm.Start();
            var rising = new LavaRising(); rising.Awake(); rising.Start(); var moving = new MovingLava(); moving.Start();
            var orb = new OrbFogHandler(); orb.Awake(); orb.Start(); var sphere = new FogSphere(); sphere.Start();
            Check(seen.Count == 9 && seen.Count(value => ReferenceEquals(value, wind)) == 2);
            foreach (var source in new UnityEngine.Component[] { wind, storm, rising, moving, orb, sphere }) Check(seen.Contains(source));
        });
        test("environment listener failures cannot prevent native initialization and disposing removes hooks", () =>
        {
            int calls = 0; var wind = new WindChillZone();
            using (var observer = new EnvironmentReplayObserver(_ => { calls++; throw new InvalidOperationException("diagnostic failure"); }))
            { wind.Awake(); Check(wind.NativeComplete && calls == 1); }
            wind.Start(); Check(calls == 1);
            using var restarted = new EnvironmentReplayObserver(_ => calls++);
            wind.Start(); Check(calls == 2);
        });
    }
    private static void Check(bool value) { if (!value) throw new Exception("Environment lifecycle contract failed."); }
}
