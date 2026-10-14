using PeakReplayLab;

internal static class LowRateActionObserverTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("short native attacks and release RPCs bypass sparse sampling after completed native work", () =>
        {
            var condor = new Condor(); var trap = new VenusFlyTrap(); var antlion = new Antlion(); int changes = 0;
            using var observer = new CreatureReplayObserver(_ => { }, changed: go =>
            {
                Check(ReferenceEquals(go, condor.gameObject) && condor.NativeActionComplete ||
                    ReferenceEquals(go, trap.gameObject) && trap.NativeActionComplete ||
                    ReferenceEquals(go, antlion.gameObject) && antlion.NativeActionComplete);
                changes++;
            });
            condor.RPCA_CondorAction(); trap.RPC_BitePlayer(); trap.RPC_LetGo();
            antlion.RPCA_Attack(); antlion.RPCA_SetClosestTarget();
            Check(changes == 5);
        });
        test("native appearance changes use the appearance callback and ordinary actions do not dirty static metadata", () =>
        {
            int actions = 0, appearances = 0;
            using var observer = new CreatureReplayObserver(_ => { }, changed: _ => actions++, appearanceChanged: _ => appearances++);
            new MushroomZombie().RPC_Arise();
            new PlayerGhost().RPCA_InitGhost(new Photon.Pun.PhotonView(), new Photon.Pun.PhotonView());
            new VenusFlyTrap().RPC_BitePlayer(); new Condor().RPCA_CondorAction();
            Check(actions == 2 && appearances == 2);
        });
        test("low-rate action event patches are removed with the last observer", () =>
        {
            int seen = 0; var observer = new CreatureReplayObserver(_ => { }, changed: _ => seen++);
            new Antlion().RPCA_Attack(); Check(seen == 1); observer.Dispose();
            new Antlion().RPCA_Attack(); new VenusFlyTrap().RPC_BitePlayer(); Check(seen == 1);
        });
    }
    private static void Check(bool value) { if (!value) throw new Exception("Low-rate native action observation failed."); }
}
