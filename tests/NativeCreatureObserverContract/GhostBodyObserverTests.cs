using System.Runtime.CompilerServices;
using PeakReplayLab;

internal static class GhostBodyObserverTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("ghost ball birth and destruction observe completed native initialization and preserve native cleanup", () =>
        {
            var source = new Peak.GhostBall(); var born = new List<UnityEngine.GameObject>();
            var died = new List<UnityEngine.GameObject>();
            using var observer = new CreatureReplayObserver(born.Add, died.Add);
            source.Awake();
            Check(source.Initialized && born.Count == 1 && ReferenceEquals(born[0], source.gameObject));
            source.OnDestroy();
            Check(source.CleanedUp && died.Count == 1 && ReferenceEquals(died[0], source.gameObject));
        });
        test("player ghost initialization wakes the admitted body after native customization and visibility", () =>
        {
            var source = new PlayerGhost(); int born = 0, changed = 0;
            using var observer = new CreatureReplayObserver(_ => born++, changed: go =>
            {
                Check(source.Initialized && source.AppearanceVersion == 7 && ReferenceEquals(go, source.gameObject));
                changed++;
            });
            source.Awake();
            Check(born == 1 && changed == 0);
            source.RPCA_InitGhost(new Photon.Pun.PhotonView(), new Photon.Pun.PhotonView());
            Check(born == 1 && changed == 1 && source.NativeInitCalls == 1);
        });
        test("throwing ghost body listeners cannot disrupt native birth initialization or cleanup", () =>
        {
            int healthyBirths = 0, healthyChanges = 0, healthyDeaths = 0;
            using var broken = new CreatureReplayObserver(_ => throw new Exception("observer"),
                _ => throw new Exception("observer"), _ => throw new Exception("observer"));
            using var healthy = new CreatureReplayObserver(_ => healthyBirths++, _ => healthyDeaths++, _ => healthyChanges++);
            var ghost = new PlayerGhost(); ghost.Awake(); ghost.RPCA_InitGhost(new Photon.Pun.PhotonView(), new Photon.Pun.PhotonView());
            var ball = new Peak.GhostBall(); ball.Awake(); ball.OnDestroy();
            Check(ghost.NativeInitCalls == 1 && ball.Initialized && ball.CleanedUp);
            Check(healthyBirths == 2 && healthyChanges == 1 && healthyDeaths == 1);
        });
        test("ghost body observers never patch AI update physics or replay any native action", () =>
        {
            using var observer = new CreatureReplayObserver(_ => { });
            foreach (Type type in new[] { typeof(PlayerGhost), typeof(Peak.GhostBall) })
                foreach (string name in new[] { "Update", "FixedUpdate" })
                {
                    var method = HarmonyLib.AccessTools.DeclaredMethod(type, name);
                    var patches = HarmonyLib.Harmony.GetPatchInfo(method);
                    Check(patches == null || patches.Owners.Count == 0);
                }
        });
    }
    private static void Check(bool value) { if (!value) throw new Exception("Ghost body lifecycle observation assertion failed."); }
}

// Native signatures/layout from this installation's Assembly-CSharp. These
// doubles test real Harmony invocation order; they do not simulate Unity AI.
public class PlayerGhost : UnityEngine.Component
{
    public bool Initialized;
    public int AppearanceVersion, NativeInitCalls;
    [MethodImpl(MethodImplOptions.NoInlining)] public void Awake() { }
    [MethodImpl(MethodImplOptions.NoInlining)] public void RPCA_InitGhost(Photon.Pun.PhotonView character, Photon.Pun.PhotonView target)
    { NativeInitCalls++; AppearanceVersion = 7; Initialized = true; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void Update() { }
    [MethodImpl(MethodImplOptions.NoInlining)] public void FixedUpdate() { }
}
namespace Peak
{
    public class GhostBall : UnityEngine.Component
    {
        public bool Initialized, CleanedUp;
        [MethodImpl(MethodImplOptions.NoInlining)] public void Awake() { Initialized = true; }
        [MethodImpl(MethodImplOptions.NoInlining)] public void OnDestroy() { CleanedUp = true; }
        [MethodImpl(MethodImplOptions.NoInlining)] public void Update() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void FixedUpdate() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void OnTargetIDChanged() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void Explode() { }
    }
}
