using System.Reflection;
using HarmonyLib;
using PeakReplayLab;

internal static class CreatureDormancyActivityTests
{
    // Same cached private managed-field access used by the capture adapter. It
    // does not ask Unity to discover an Animator, component or live hierarchy.
    private static readonly AccessTools.FieldRef<Spider, float> stunnedTime =
        AccessTools.FieldRefAccess<Spider, float>("_stunnedTime");
    public static void Run(Action<string, Action> test)
    {
        test("native spider RPC changes are observed only after their state and action work complete", () =>
        {
            var source = new Spider(); var states = new List<Spider.SpiderState>(); int births = 0;
            using var observer = new CreatureReplayObserver(_ => births++, changed: go =>
            {
                Check(ReferenceEquals(go, source.gameObject) && source.NativeActionComplete);
                states.Add(source.spiderState);
            });
            var target = new Photon.Pun.PhotonView();
            source.RPCA_DropSpider(target, 12); source.RPCA_GrabCharacter(target); source.NativeLetGo(); source.NativeBonk();
            Check(births == 0 && source.NativeActionCalls == 4);
            Check(states.SequenceEqual(new[] { Spider.SpiderState.Dropped, Spider.SpiderState.Grabbing, Spider.SpiderState.LetGo, Spider.SpiderState.LetGo }));
            Check(stunnedTime(source) == 5);
        });
        test("native idle spider bonk wakes a throttled capture without needing a state transition", () =>
        {
            var source = new Spider(); var activity = new CreatureCaptureActivity(source, null); int changes = 0;
            using var observer = new CreatureReplayObserver(_ => { }, changed: go =>
            { Check(ReferenceEquals(go, source.gameObject) && source.NativeActionComplete); activity.NotifyNativeEvent(); changes++; });
            Check(activity.Due(0, source.gameObject.activeInHierarchy));
            Check(activity.Due(.25, true), "Initial native setup did not save its final Idle checkpoint.");
            Check(activity.IdleCandidate); activity.CompleteIdleVisual(.25, true);
            Check(!activity.Due(1, true) && !activity.Due(1.001, true));
            source.NativeBonk();
            Check(source.spiderState == Spider.SpiderState.Idle && changes == 1);
            Check(activity.Due(1.002, true));
            Check(activity.ClockMode == 0 && activity.Due(1.04, true));
            source.NativeStun(0);
            Check(activity.Due(6.041, true), "Native stun ending missed its final pose.");
        });
        test("unsupported native idle visuals keep capturing instead of silently freezing", () =>
        {
            var activity = new CreatureCaptureActivity(new Spider(), null);
            Check(activity.Due(0, true)); Check(activity.Due(.25, true));
            Check(activity.IdleCandidate && activity.ClockMode == CreatureReplayRules.ActiveClock);
            activity.CompleteIdleVisual(.25, false);
            Check(activity.Due(.29, true) && activity.ClockMode == CreatureReplayRules.ActiveClock);
            activity.CompleteIdleVisual(.29, true);
            Check(!activity.Due(1, true) && activity.ClockMode == CreatureReplayRules.IdleClock);
            activity.NotifyNativeEvent(); Check(activity.Due(1.001, true));
        });
        test("native creature activity follows admitted scene ticks while sleeping sources retain their checkpoint", () =>
        {
            var spider = new Spider { spiderState = Spider.SpiderState.Dropped };
            var action = new CreatureCaptureActivity(spider, null, followSceneClock: true);
            Check(action.Due(0, true) && action.Due(.02, true) && action.Due(.05, true));
            var beetle = new Beetle { sleeping = true };
            var asleep = new CreatureCaptureActivity(null, beetle, followSceneClock: true);
            Check(asleep.Due(0, true));
            Check(!asleep.Due(.02, true) && !asleep.Due(.05, true));
            beetle.sleeping = false;
            Check(asleep.Due(.06, true) && asleep.Due(.08, true));
        });
        test("native mob sleep and wake preserve completed child visibility with immediate capture edges", () =>
        {
            var source = new Beetle { sleeping = true }; source.UpdateSleeping();
            var activity = new CreatureCaptureActivity(null, source); int changed = 0;
            using var observer = new CreatureReplayObserver(_ => { }, changed: go =>
            { Check(ReferenceEquals(go, source.gameObject) && source.NativeSleepComplete); activity.NotifyNativeEvent(); changed++; });
            int componentReads = UnityEngine.Component.ComponentLookups, positionReads = UnityEngine.Transform.PositionReads;
            Check(activity.Due(0, source.gameObject.activeInHierarchy));
            Check(source.gameObject.activeSelf && !source.visuals.activeSelf && !source.AnimatorEnabled);
            for (int i = 1; i <= 3600; i++) Check(!activity.Due(i / 60d, source.gameObject.activeInHierarchy));
            Check(UnityEngine.Component.ComponentLookups == componentReads && UnityEngine.Transform.PositionReads == positionReads,
                "Managed sleep signals caused component or transform discovery.");
            source.NearCharacter = true; source.TestSleepMode();
            Check(source.visuals.activeSelf && source.AnimatorEnabled);
            Check(activity.Due(60.001, source.gameObject.activeInHierarchy));
            source.NearCharacter = false; source.TestSleepMode();
            Check(!source.visuals.activeSelf && activity.Due(60.002, true));
            Check(!activity.Due(60.003, true) && changed == 2);
        });
        test("native spider culling and return invalidate visibility even inside one active cadence", () =>
        {
            var source = new Spider { spiderState = Spider.SpiderState.Dropped };
            var activity = new CreatureCaptureActivity(source, null);
            Check(activity.Due(0, source.gameObject.activeInHierarchy));
            source.gameObject.SetActive(false);
            Check(activity.Due(.001, source.gameObject.activeInHierarchy));
            Check(!activity.Due(.002, false));
            source.gameObject.SetActive(true);
            Check(activity.Due(.003, source.gameObject.activeInHierarchy));
        });
        test("a failing spider event listener leaves native work and healthy capture observers intact", () =>
        {
            var source = new Spider(); int seen = 0;
            using var broken = new CreatureReplayObserver(_ => { }, changed: _ => throw new InvalidOperationException("observer only"));
            using var healthy = new CreatureReplayObserver(_ => { }, changed: _ => seen++);
            source.NativeBonk();
            Check(seen == 1 && source.NativeActionCalls == 1 && source.NativeActionComplete && stunnedTime(source) == 5);
        });
        test("disposing the last creature observer removes native spider action patches", () =>
        {
            int seen = 0; var observer = new CreatureReplayObserver(_ => { }, changed: _ => seen++);
            observer.Dispose(); observer.Dispose(); var source = new Spider(); source.NativeBonk();
            Check(seen == 0 && source.NativeActionCalls == 1 && source.NativeActionComplete);
            foreach (string name in new[] { "RPCA_DropSpider", "RPCA_GrabCharacter", "RPCA_LetGo", "BonkRPC" })
            {
                var method = typeof(Spider).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
                var patches = Harmony.GetPatchInfo(method); Check(patches == null || patches.Owners.Count == 0);
            }
        });
    }
    private static void Check(bool condition, string message = "")
    { if (!condition) throw new Exception("Native creature dormancy contract failed: " + message); }
}
