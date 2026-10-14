using HarmonyLib;
using PeakReplayLab;

internal static class TrapSettingsObserverTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("future biome first Awake records custom hazard deactivation after the native setting completes", () =>
        {
            var source = new DisableBasedOnRunSettings { IsCustomRun = true, SettingValue = 0 };
            source.gameObject.ParentActive = false;
            int changed = 0;
            using var observer = new CreatureReplayObserver(_ => throw new Exception("unexpected birth"), changed: go =>
            { Check(ReferenceEquals(go, source.gameObject) && !go.activeSelf); changed++; });
            Check(source.gameObject.activeSelf && !source.gameObject.activeInHierarchy);
            source.Awake(); Check(!source.gameObject.activeSelf && changed == 1);
            source.gameObject.ParentActive = true;
            source.ApplySettings(); Check(changed == 1);
        });
        test("ordinary unchanged run settings never inspect trap signatures or enqueue static observations", () =>
        {
            var source = new DisableBasedOnRunSettings { SettingValue = 0 };
            int changed = 0;
            using var observer = new CreatureReplayObserver(_ => changed++, changed: _ => changed++);
            int before = CreatureReplayResources.TrapLookups;
            source.Awake();
            for (int i = 0; i < 6000; i++) source.ApplySettings();
            source.IsCustomRun = true; source.SettingValue = 1; source.ApplySettings();
            Check(source.gameObject.activeSelf && changed == 0 && CreatureReplayResources.TrapLookups == before);
        });
        test("one actual custom setting transition emits once and repeated disabled updates stay cold", () =>
        {
            var source = new DisableBasedOnRunSettings { IsCustomRun = true };
            int changed = 0;
            using var observer = new CreatureReplayObserver(_ => { }, changed: _ => changed++);
            source.Awake(); source.SettingValue = 0; source.ApplySettings();
            int lookups = CreatureReplayResources.TrapLookups;
            for (int i = 0; i < 100; i++) source.ApplySettings();
            Check(!source.gameObject.activeSelf && changed == 1 && CreatureReplayResources.TrapLookups == lookups);
        });
        test("native settings changes on objects outside the verified trap directory do not dirty creatures", () =>
        {
            var source = new DisableBasedOnRunSettings { IsCustomRun = true, SettingValue = 0 };
            source.gameObject.VerifiedTrap = false; int changed = 0;
            using var observer = new CreatureReplayObserver(_ => changed++, changed: _ => changed++);
            source.Awake(); Check(!source.gameObject.activeSelf && changed == 0);
        });
        test("trap settings observation failure cannot prevent native disable or another listener", () =>
        {
            var source = new DisableBasedOnRunSettings { IsCustomRun = true, SettingValue = 0 };
            int changed = 0;
            using var broken = new CreatureReplayObserver(_ => { }, changed: _ => throw new Exception("diagnostic callback"));
            using var healthy = new CreatureReplayObserver(_ => { }, changed: _ => changed++);
            source.Awake(); Check(!source.gameObject.activeSelf && changed == 1);
            source.gameObject.SetActive(true); CreatureReplayResources.Fail = true;
            try { source.ApplySettings(); Check(!source.gameObject.activeSelf && changed == 1); }
            finally { CreatureReplayResources.Fail = false; }
        });
        test("disposing last observer removes native run settings hooks and future native calls remain intact", () =>
        {
            var source = new DisableBasedOnRunSettings { IsCustomRun = true, SettingValue = 0 };
            int changed = 0;
            var observer = new CreatureReplayObserver(_ => { }, changed: _ => changed++);
            observer.Dispose(); observer.Dispose(); source.Awake();
            Check(!source.gameObject.activeSelf && changed == 0);
            var method = AccessTools.DeclaredMethod(typeof(DisableBasedOnRunSettings), "UpdateEnabled");
            Check(Harmony.GetPatchInfo(method) == null || Harmony.GetPatchInfo(method).Owners.Count == 0);
        });
    }
    private static void Check(bool value) { if (!value) throw new Exception("Native trap settings observation assertion failed."); }
}
