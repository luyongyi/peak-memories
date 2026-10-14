using PeakReplayLab;

internal static class FaunaEventObserverTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("raven semantic boundaries wake observation only after native action completion", () =>
        {
            var raven = new Peak.Island.VoiceThief(); int changed = 0;
            using (var observer = new CreatureReplayObserver(_ => { }, changed: go =>
            {
                Check(raven.NativeActionComplete && ReferenceEquals(go, raven.gameObject)); changed++;
            }))
            {
                Check(RavenAnimationCapture.EventHooksHealthy);
                raven.RPCA_Assign(); raven.NativeActionComplete = false;
                raven.RPCA_FlyAway(); raven.NativeActionComplete = false;
                raven.SetActivelyStealing();
                var random = new Peak.RandomizeAnimatorParameter { Parent = raven }; random.OnEnable();
                Check(changed == 4);
            }
            Check(!RavenAnimationCapture.EventHooksHealthy);
            raven.RPCA_FlyAway(); Check(changed == 4);
        });
        test("frog native action edges wake both item and tongue observers and unpatch cleanly", () =>
        {
            var frog = new FrogTongue(); int changed = 0; int before = FrogCaptureActivity.NativeEvents;
            using (var observer = new CreatureReplayObserver(_ => { }, changed: go =>
            { Check(frog.NativeActionComplete && ReferenceEquals(go, frog.gameObject)); changed++; }))
            {
                Check(FrogCaptureActivity.EventHooksHealthy);
                foreach (Action action in new Action[] { frog.Reposition, frog.Attack, frog.HopAway, frog.LetGo })
                { frog.NativeActionComplete = false; action(); }
                Check(changed == 4 && FrogCaptureActivity.NativeEvents - before == 4);
            }
            Check(!FrogCaptureActivity.EventHooksHealthy);
            frog.HopAway(); Check(changed == 4 && FrogCaptureActivity.NativeEvents - before == 4);
        });
        test("ignored frog floor collisions stay quiet while the actual native wall HopAway immediately cuts sparse interpolation",()=>
        {
            var frog=new FrogTongue();var cadence=new CreatureSamplingCadence();int changes=0;
            Check(cadence.Due(10,true,0));
            using var observer=new CreatureReplayObserver(_=>{},changed:_=>{changes++;cadence.NotifyNativeEvent();});
            frog.OnCollisionEnter();Check(changes==0&&!cadence.Due(10.01,true,0));
            frog.WallCollision=true;frog.OnCollisionEnter();
            Check(changes==1&&cadence.Due(10.02,true,0)&&cadence.LastWasBoundary);
            Check(cadence.Due(10.03,true,0)&&!cadence.LastWasBoundary);
        });
    }
    private static void Check(bool value) { if (!value) throw new Exception("Native fauna event observation failed."); }
}
