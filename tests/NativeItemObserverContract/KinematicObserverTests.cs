using PeakReplayLab;
using UnityEngine;

internal static class KinematicObserverTests
{
    private static void Check(bool value, string message = "Kinematic observer contract failed")
    { if (!value) throw new Exception(message); }
    internal static void Run(Action<string, Action> test, Func<ItemEventCapture> observe)
    {
        test("1800 native same false setters preserve WasActive without queues or pose reads", () =>
        {
            using var observer = observe(); var item = new Item();
            for (int i = 0; i < 1800; i++) item.SetKinematic(false);
            Check(item.NativeCalls == 1800 && item.WasActiveCalls == 1800 && ItemCapture.Notices.Count == 0);
            Check(item.rig.PositionReads == 0 && item.rig.RotationReads == 0 && item.rig.KinematicReads == 3600);
            Check(item.ComponentLookups == 0, "The hot setter must not resolve components or hierarchies.");
        });
        test("actual false true false boundaries wake once each and same true setters stay silent", () =>
        {
            using var observer = observe(); var item = new Item();
            item.SetKinematic(true);
            for (int i = 0; i < 1800; i++) item.SetKinematic(true);
            item.SetKinematic(false);
            Check(ItemCapture.Notices.Count == 2 && ItemCapture.Notices.All(x => x.Kind == "motion" && !x.Continuous));
            Check(!item.Kinematic && item.NativeCalls == 1802 && item.WasActiveCalls == 1);
        });
        test("nested native kinematic RPC transitions emit only one motion boundary", () =>
        {
            using var observer = observe(); var item = new Item();
            var identity = new Quaternion { w = 1 };
            item.SetKinematicRPC(true, new Vector3(1, 2, 3), identity);
            item.SetKinematicAndResetSyncData(false, new Vector3(4, 5, 6), identity);
            Check(item.NativeCalls == 4 && ItemCapture.Notices.Count == 2 && item.rig.position.x == 4 && !item.Kinematic);
            Check(ItemCapture.Notices.All(x => x.Kind == "motion" && ReferenceEquals(x.Item, item)));
        });
        test("same-value RPC and reset with unchanged native pose do not renew any boundary", () =>
        {
            using var observer = observe(); var item = new Item(); var identity = new Quaternion { w = 1 };
            for (int i = 0; i < 1800; i++)
            { item.SetKinematicRPC(false, default, identity); item.SetKinematicAndResetSyncData(false, default, identity); }
            Check(item.NativeCalls == 7200 && item.WasActiveCalls == 3600 && ItemCapture.Notices.Count == 0);
        });
        test("same kinematic true RPC pose changes remain visible without a physics packet", () =>
        {
            using var observer = observe(); var item = new Item(); var identity = new Quaternion { w = 1 };
            item.SetKinematic(true); ItemCapture.Notices.Clear();
            item.SetKinematicRPC(true, new Vector3(5, 0, 0), identity);
            item.SetKinematicRPC(true, new Vector3(5, 0, 0), new Quaternion { w = -1 });
            item.SetKinematicAndResetSyncData(true, new Vector3(6, 0, 0), identity);
            Check(item.rig.position.x == 6 && ItemCapture.Notices.Count == 2 && ItemCapture.Notices.All(x => !x.Continuous));
            item.SetKinematicRPC(true, new Vector3(6, 0, 0), new Quaternion { y = 1 });
            Check(ItemCapture.Notices.Count == 3, "Same kinematic state must not hide an actual rotation RPC.");
        });
        test("same-value native reset preserves real packet motion and native remote reset semantics", () =>
        {
            using var observer = observe(); var item = new Item(); var sync = new ItemPhysicsSyncer { ItemOwner = item };
            item.physicsSyncer = sync; var identity = new Quaternion { w = 1 };
            sync.OnDataReceived(new ItemPhysicsSyncData { position = default, rotation = identity });
            ItemCapture.Notices.Clear();
            item.SetKinematicAndResetSyncData(false, new Vector3(2, 0, 0), identity);
            Check(ItemCapture.Notices.Count == 1 && !ItemCapture.Notices[0].Continuous);
            sync.OnDataReceived(new ItemPhysicsSyncData { position = new Vector3(2, 0, 0), rotation = identity });
            sync.OnDataReceived(new ItemPhysicsSyncData { position = new Vector3(2, 0, 0), rotation = identity });
            Check(ItemCapture.Notices.Count == 2 && ItemCapture.Notices[1].Continuous, "True packet change still wakes, repeated packet remains deduplicated.");
            item.SetKinematic(true); item.rig.linearVelocity = new Vector3(3, 0, 0); item.rig.angularVelocity = new Vector3(0, 2, 0);
            ItemCapture.Notices.Clear(); int oldSyncCalls = sync.NativeCalls;
            item.SetKinematicAndResetSyncData(true, new Vector3(2, 0, 0), identity);
            Check(item.rig.linearVelocity.x == 0 && item.rig.angularVelocity.y == 0 && sync.NativeCalls == oldSyncCalls + 1 && ItemCapture.Notices.Count == 0);
        });
        test("continuous microscopic kinematic RPC pose changes cannot disappear without physics packets", () =>
        {
            using var observer = observe(); var item = new Item(); var rotation = new Quaternion { w = 1 };
            item.SetKinematic(true); ItemCapture.Notices.Clear();
            for (int i = 1; i <= 1800; i++) item.SetKinematicRPC(true, new Vector3(i * .000001f, 0, 0), rotation);
            Check(ItemCapture.Notices.Count == 1800 && ItemCapture.Notices.All(x => !x.Continuous));
            for (int i = 1; i <= 30; i++) item.SetKinematicAndResetSyncData(true, new Vector3(.0018f, 0, 0), new Quaternion { y = i * .0000001f, w = 1 });
            Check(ItemCapture.Notices.Count == 1830, "Tiny rotations must not require a dynamic packet to be recorded.");
        });
        test("kinematic observer presentation, recorder failures and native exceptions remain isolated", () =>
        {
            using var observer = observe(); var item = new Item(); ReplaySafety.Active = true;
            item.SetKinematicRPC(true, new Vector3(1, 0, 0), new Quaternion { w = 1 });
            Check(item.Kinematic && ItemCapture.Notices.Count == 0); ReplaySafety.Active = false;
            ItemCapture.OnNotify = (_, _) => throw new InvalidOperationException("injected recorder failure");
            item.SetKinematicRPC(false, new Vector3(2, 0, 0), new Quaternion { w = 1 });
            Check(!item.Kinematic && item.rig.position.x == 2 && ItemCapture.Notices.Count == 1);
            ItemCapture.OnNotify = null; var expected = new ApplicationException("native failure"); item.NativeFailure = expected;
            try { item.SetKinematic(true); throw new Exception("Native exception disappeared"); }
            catch (ApplicationException actual) { Check(ReferenceEquals(actual, expected)); }
            Check(!item.Kinematic && ItemCapture.Notices.Count == 1);
        });
    }
}
