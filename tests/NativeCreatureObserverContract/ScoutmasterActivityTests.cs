using PeakReplayLab;
using UnityEngine;

internal static class ScoutmasterActivityTests
{
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    private static (Scoutmaster Source, Character Character, Bodypart Torso) Native(bool spawnPoint = true)
    {
        var source = new Scoutmaster(); source.transform.position = new(16, 1023, 1968);
        var character = new Character { gameObject = source.gameObject };
        var torso = new Bodypart(); torso.transform.position = new(16, 1024, 1968);
        character.refs!.ragdoll!.partDict.Add(BodypartType.Torso, torso);
        if (spawnPoint) character.data!.spawnPoint = source.transform;
        source.CharacterComponent = character;
        return (source, character, torso);
    }
    public static void Run(Action<string, Action> test)
    {
        test("Scoutmaster idle spawn uses no per-tick hierarchy or position reads", () =>
        {
            var native = Native(); using var activity = new ScoutmasterCaptureActivity(native.Source);
            Check(!activity.Active, "native initial spawn mistaken for an encounter");
            int lookups = Component.ComponentLookups, positions = Transform.PositionReads;
            for (int i = 0; i < 1000; i++) Check(!activity.Active, "idle scoutmaster woke up");
            AppDomain.MonitoringIsEnabled = true;
            var domain = AppDomain.CurrentDomain;
            long controlBefore = domain.MonitoringTotalAllocatedMemorySize;
            var control = new byte[100000]; GC.KeepAlive(control);
            Check(domain.MonitoringTotalAllocatedMemorySize > controlBefore, "allocation counter positive control failed");
            long before = domain.MonitoringTotalAllocatedMemorySize;
            for (int i = 0; i < 6000; i++) Check(!activity.Active, "idle scoutmaster woke up");
            long allocated = domain.MonitoringTotalAllocatedMemorySize - before;
            Check(Component.ComponentLookups == lookups && Transform.PositionReads == positions,
                "steady idle gate re-read component/pose data");
            // Framework 4.7.2 only exposes an AppDomain-wide counter, so report
            // background-thread allocations without making them a flaky gate.
            Console.WriteLine("OBSERVED Scoutmaster 6000 idle checks / AppDomain allocated bytes: " + allocated);
        });
        test("Scoutmaster initialization waits for native data and torso readiness", () =>
        {
            var native = Native(); var data = native.Character.data; native.Character.data = null;
            using var activity = new ScoutmasterCaptureActivity(native.Source);
            Check(!activity.Active, "uninitialized data woke the gate"); native.Character.data = data;
            native.Character.refs!.ragdoll!.partDict.Clear(); Check(!activity.Active, "uninitialized torso woke the gate");
            native.Character.refs.ragdoll.partDict.Add(BodypartType.Torso, native.Torso);
            Check(!activity.Active, "completed initial spawn mistaken for activity");
        });
        test("Scoutmaster client without master-only spawnPoint recognizes its initial root", () =>
        {
            var native = Native(spawnPoint: false); using var activity = new ScoutmasterCaptureActivity(native.Source);
            Check(!activity.Active, "client lacks spawnPoint and captured idle bones");
        });
        test("Scoutmaster late-join client without history uses actual torso not stationary root", () =>
        {
            var native = Native(spawnPoint: false); native.Torso.transform.position = new(0, 10, 20);
            using var activity = new ScoutmasterCaptureActivity(native.Source);
            Check(activity.Active, "mid-encounter body missed because its original root stayed at spawn");
        });
        test("Scoutmaster newly recording a fallen initial master body remains idle without a native encounter", () =>
        {
            var native = Native(); native.Torso.transform.position = new(16, 824, 1968);
            using var activity = new ScoutmasterCaptureActivity(native.Source);
            Check(!activity.Active, "gravity at the spawn point mistaken for activation");
            native.Source.currentTarget = new Character(); Check(activity.Active, "real target failed to wake fallen idle body");
        });
        test("Scoutmaster newly recording a fallen body after completed native return stays parked", () =>
        {
            var native = Native(); native.Character.WarpPlayer(Character.ScoutmasterHideySpot); native.Character.CompleteWarp();
            native.Torso.transform.position = new(0, -86, 5000); native.Source.currentTarget = new Character();
            using var activity = new ScoutmasterCaptureActivity(native.Source);
            Check(!activity.Active && !activity.Active, "gravity or residual target overrode completed return history");
        });
        test("Scoutmaster native arrival history preserves an existing encounter without a target", () =>
        {
            var native = Native(); native.Character.WarpPlayer(new(0, 10, 20)); native.Character.CompleteWarp();
            using var activity = new ScoutmasterCaptureActivity(native.Source);
            Check(activity.Active, "completed native arrival was mistaken for master idle spawn");
        });
        test("Scoutmaster target activation is latched across loss of target", () =>
        {
            var native = Native(); using var activity = new ScoutmasterCaptureActivity(native.Source); Check(!activity.Active, "initial gate");
            native.Source.currentTarget = new Character(); Check(activity.Active, "target did not activate");
            native.Source.currentTarget = null; Check(activity.Active, "losing target cut off fleeing or throwing motion");
        });
        test("Scoutmaster discovery starts recording and remains active while fleeing", () =>
        {
            var native = Native(); using var activity = new ScoutmasterCaptureActivity(native.Source); Check(!activity.Active, "initial gate");
            native.Source.discovered = new Character(); Check(activity.Active, "discovery not captured");
            native.Source.discovered = null; Check(activity.Active, "discovery cleared before actual return");
        });
        test("Scoutmaster parked initialization ignores stale target at native HideySpot", () =>
        {
            var native = Native(); native.Torso.transform.position = Character.ScoutmasterHideySpot; native.Source.currentTarget = new Character();
            using var activity = new ScoutmasterCaptureActivity(native.Source); Check(!activity.Active && !activity.Active, "stale parked target woke the gate");
        });
        test("Scoutmaster native warp activates before completion and return waits for actual completion", () =>
        {
            using var observer = new CreatureReplayObserver(_ => { }); var native = Native();
            using var activity = new ScoutmasterCaptureActivity(native.Source); Check(!activity.Active, "initial gate");
            native.Character.WarpPlayer(new(0, 10, 20)); Check(activity.Active && native.Character.warping, "native departure not observed");
            native.Character.CompleteWarp(); Check(activity.Active, "arrival incorrectly stopped capture");
            native.Source.currentTarget = new Character(); Check(activity.Active, "encounter");
            native.Character.WarpPlayer(Character.ScoutmasterHideySpot); Check(activity.Active, "return invocation hid visible last frames");
            native.Character.CompleteWarp(); Check(!activity.Active && !activity.Active, "return completion did not park or stale target reactivated");
            native.Source.currentTarget = null; Check(!activity.Active, "target loss woke parked scoutmaster");
            native.Source.currentTarget = new Character(); Check(activity.Active, "new encounter after parking was missed");
        });
        test("Scoutmaster replacement warp cancels pending return instead of stopping next encounter", () =>
        {
            using var observer = new CreatureReplayObserver(_ => { }); var native = Native(); using var activity = new ScoutmasterCaptureActivity(native.Source);
            native.Character.WarpPlayer(new(0, 10, 20)); Check(activity.Active, "departure");
            native.Character.WarpPlayer(Character.ScoutmasterHideySpot); native.Character.WarpPlayer(new(50, 10, 20));
            native.Character.CompleteWarp(); Check(activity.Active, "superseded return stopped replacement encounter");
        });
        test("Scoutmaster capture started during an unobserved return parks on completion", () =>
        {
            var native = Native(); native.Torso.transform.position = new(0, 10, 20); native.Character.WarpPlayer(Character.ScoutmasterHideySpot);
            using var activity = new ScoutmasterCaptureActivity(native.Source); Check(activity.Active, "existing visible departure missing");
            native.Character.CompleteWarp(); Check(!activity.Active, "unobserved return kept parked bones active forever");
        });
        test("Scoutmaster capture started during an unobserved arrival admits its known native destination", () =>
        {
            var native = Native(); native.Torso.transform.position = Character.ScoutmasterHideySpot; native.Character.WarpPlayer(new(0, 10, 20));
            using var activity = new ScoutmasterCaptureActivity(native.Source); Check(activity.Active, "native pending arrival history was ignored");
            native.Character.CompleteWarp(); Check(activity.Active, "unobserved arrival failed to activate");
        });
        test("Scoutmaster disabled then enabled at spawn does not revive a stale target", () =>
        {
            var native = Native(); using var activity = new ScoutmasterCaptureActivity(native.Source); Check(!activity.Active, "initial gate");
            native.Source.currentTarget = new Character(); Check(activity.Active, "target activation");
            native.Source.enabled = false; Check(!activity.Active, "disabled body captured");
            native.Source.enabled = true; Check(!activity.Active && !activity.Active, "stale target survived disable");
        });
        test("Scoutmaster native disable event is observed even between two capture ticks", () =>
        {
            using var observer = new CreatureReplayObserver(_ => { }); var native = Native(); using var activity = new ScoutmasterCaptureActivity(native.Source);
            Check(!activity.Active, "initial gate"); native.Source.currentTarget = new Character(); Check(activity.Active, "target activation");
            native.Source.OnDisable(); native.Source.OnEnable(); Check(!activity.Active, "between-tick disable lost reset");
        });
        test("Scoutmaster re-enabled in the actual world resumes an existing encounter", () =>
        {
            var native = Native(); native.Character.WarpPlayer(new(0, 10, 20)); native.Character.CompleteWarp(); using var activity = new ScoutmasterCaptureActivity(native.Source);
            Check(activity.Active, "mid-encounter initialization"); native.Source.gameObject.ParentActive = false; Check(!activity.Active, "inactive hierarchy captured");
            native.Source.gameObject.ParentActive = true; Check(activity.Active, "existing encounter lost on re-enable");
        });
        test("Scoutmaster ReplaySafety ignores native-looking playback warps and completion", () =>
        {
            using var observer = new CreatureReplayObserver(_ => { }); var native = Native(); using var activity = new ScoutmasterCaptureActivity(native.Source);
            Check(!activity.Active, "initial gate"); ReplaySafety.Active = true;
            try { native.Character.WarpPlayer(new(0, 10, 20)); native.Character.CompleteWarp(); Check(!activity.Active, "replay admitted capture"); }
            finally { ReplaySafety.Active = false; }
            Check(!activity.Active, "replay changed real capture state");
        });
        test("Scoutmaster multiple captures each receive native activity and disposal preserves survivors", () =>
        {
            using var observer = new CreatureReplayObserver(_ => { }); var native = Native();
            var first = new ScoutmasterCaptureActivity(native.Source); using var second = new ScoutmasterCaptureActivity(native.Source);
            Check(!first.Active && !second.Active, "initial gate"); native.Character.WarpPlayer(new(0, 10, 20));
            Check(first.Active && second.Active, "registration overwrote another capture");
            second.Dispose(); native.Character.CompleteWarp(); native.Character.WarpPlayer(Character.ScoutmasterHideySpot); native.Character.CompleteWarp();
            Check(!first.Active && !second.Active, "remaining observer lost return or disposed observer stayed live");
            native.Character.WarpPlayer(new(0, 10, 20)); Check(first.Active && !second.Active, "surviving capture lost next warp");
            first.Dispose(); first.Dispose(); native.Character.CompleteWarp(); Check(!first.Active, "disposed capture resumed");
        });
        test("Scoutmaster completion observation cannot interrupt another native listener", () =>
        {
            var native = Native(); native.Torso.transform.position = new(0, 10, 20); native.Character.WarpPlayer(Character.ScoutmasterHideySpot);
            using var activity = new ScoutmasterCaptureActivity(native.Source); Check(activity.Active, "existing encounter");
            int completed = 0; native.Character.WarpCompleted += _ => completed++;
            native.Source.ThrowOnSignalRead = true; native.Character.CompleteWarp();
            Check(completed == 1, "capture observation exception interrupted native completion event");
            native.Source.ThrowOnSignalRead = false; Check(!activity.Active, "failed observation did not retry settled pose");
        });
        test("Scoutmaster missing Character is rejected explicitly instead of silently dropping encounters", () =>
        {
            bool rejected = false;
            try { using var activity = new ScoutmasterCaptureActivity(new Scoutmaster()); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "incompatible native source silently disabled capture");
        });
    }
}
