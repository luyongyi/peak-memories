using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace PeakReplayLab;

// Installed once; no-op during ordinary play. Viewing a memory must not start a new real
// run, overwrite/delete the user's quicksave, or award achievements from reenactments.
internal sealed class ReplaySafety : IDisposable
{
    private static bool active;
    private static Action? deferredUnpatch;
    private static ReplayCameraCompatibility? cameraCompatibility;
    private bool disposeRequested;
    public static bool Returning { get; set; }
    // Set only after native scene initialization, not while loading/spawning.
    public static bool Presenting { get; set; }
    public static int SuspendedCameraManagers => cameraCompatibility?.Count ?? 0;
    public static bool Active
    {
        get => active;
        set
        {
            if (active == value) return;
            active = value;
            if (value) cameraCompatibility = new ReplayCameraCompatibility();
            else
            {
                Returning = false; Presenting = false;
                cameraCompatibility?.Dispose(); cameraCompatibility = null;
                var pending = deferredUnpatch; deferredUnpatch = null; pending?.Invoke();
            }
        }
    }
    private readonly Harmony harmony = new("cn.mylus.peakreplaylab.theatre");
    public ReplaySafety()
    {
        try
        {
            Patch(typeof(RunManager), "Start", nameof(SkipRun));
            Patch(typeof(RunManager), "Update");
            Patch(typeof(MapHandler), "Update");
            Patch(typeof(Peak.Quicksave), "SaveNow");
            Patch(typeof(Peak.Quicksave), "DestroySaveData");
            Patch(typeof(Peak.Quicksave), "FinalizeRunSetup");
            Patch(typeof(AchievementManager), "SetSteamStatInternal");
            Patch(typeof(AchievementManager), "IncrementSteamStat");
            Patch(typeof(AchievementManager), "ThrowAchievement");
            Patch(typeof(NetworkConnector), "OnLeftRoom"); // theatre owns its explicit return-to-title transition
            // Disconnect clears PlayerHandler before the old scene finishes unloading.
            // Its Update/UI consumers must not dereference that vanished player meanwhile.
            Patch(typeof(Character), "Update", nameof(AllowSceneUpdate));
            Patch(typeof(CharacterBackpackHandler), "LateUpdate", nameof(AllowSceneUpdate));
            Patch(typeof(GUIManager), "LateUpdate", nameof(AllowSceneUpdate));
            Patch(typeof(GUIManager), "UpdateItems", nameof(AllowSceneUpdate));
            // Audited update callbacks of the hidden native scout only. Start,
            // OnEnable/Disable, OnDestroy and network callbacks remain untouched.
            Patch(typeof(CharacterMovement), "Update", nameof(AllowPresentationUpdate));
            Patch(typeof(CharacterAnimations), "Update", nameof(AllowPresentationUpdate));
            Patch(typeof(CharacterAnimations), "LateUpdate", nameof(AllowPresentationUpdate));
            Patch(typeof(CharacterCustomization), "Update", nameof(AllowPresentationUpdate));
            // Native names refer to the hidden live scout and cannot fade while
            // deltaTime is zero. UpdateName also changes gameplay customization;
            // the recorded presentation owns names until it restores the scene.
            Patch(typeof(IsLookedAt), "Update", nameof(AllowPresentationUpdate));
            Patch(typeof(UIPlayerNames), "UpdateName", nameof(AllowPresentationUpdate));
            Patch(typeof(DayNightManager), "Update", nameof(AllowPresentationUpdate));
            // Only this callback writes the normal game cursor lock. Leaving it
            // active would lock in Update and unlock in replay LateUpdate every
            // frame. Loading, menus, return transitions and normal input retain
            // the native handler; the replay alone owns it during presentation.
            Patch(typeof(CursorHandler), "Update", nameof(AllowPresentationUpdate));
            // Native ropes must not simulate a second, unrelated shape behind
            // recorded mesh/line replicas. Loading still runs all initialization.
            Patch(typeof(Rope), "Update", nameof(AllowPresentationUpdate));
            Patch(typeof(Rope), "FixedUpdate", nameof(AllowPresentationUpdate));
            Patch(typeof(RopeBoneVisualizer), "LateUpdate", nameof(AllowPresentationUpdate));
            Patch(typeof(RescueHook), "LateUpdate", nameof(AllowPresentationUpdate));
            Patch(typeof(RescueHook), "FixedUpdate", nameof(AllowPresentationUpdate));
            Patch(typeof(ScoutCannon), "FixedUpdate", nameof(AllowPresentationUpdate));
            Patch(typeof(TiedBalloon), "FixedUpdate", nameof(AllowPresentationUpdate));
            Patch(typeof(TiedBalloon), "LateUpdate", nameof(AllowPresentationUpdate));
            Patch(typeof(MushroomZombie), "Update", nameof(AllowPresentationUpdate));
            Patch(typeof(Spider), "LateUpdate", nameof(AllowPresentationUpdate));
            Patch(typeof(Spider), "FixedUpdate", nameof(AllowPresentationUpdate));
            Patch(typeof(SpiderManager), "Update", nameof(AllowPresentationUpdate));
            Patch(typeof(ZombieManager), "Update", nameof(AllowPresentationUpdate));
            // Placed native templates remain in the scene behind passive replay
            // meshes. Their updates can rotate without deltaTime, break/RPC a
            // piton, or change campfire afflictions even while timeScale is zero.
            Patch(typeof(MagicBeanVine), "Update", nameof(AllowPresentationUpdate));
            Patch(typeof(ShittyPiton), "Update", nameof(AllowPresentationUpdate));
            Patch(typeof(Campfire), "Update", nameof(AllowPresentationUpdate));
            Patch(typeof(Peak.GrowOverTime), "Update", nameof(AllowPresentationUpdate));
            Patch(typeof(Peak.AntiSphere), "FixedUpdate", nameof(AllowPresentationUpdate));
        }
        catch { harmony.UnpatchSelf(); throw; }
    }
    private void Patch(Type type, string method, string prefix = nameof(AllowLive))
    {
        MethodInfo target = AccessTools.DeclaredMethod(type, method) ?? throw new MissingMethodException(type.FullName, method);
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(ReplaySafety), prefix));
    }
    private static bool AllowLive() => !Active;
    private static bool AllowSceneUpdate() => !(Active && (Returning || Presenting));
    private static bool AllowPresentationUpdate() => !(Active && Presenting);
    private static bool SkipRun(ref IEnumerator __result)
    {
        if (!Active) return true;
        __result = Empty();
        return false;
    }
    private static IEnumerator Empty() { yield break; }
    public void Dispose()
    {
        if (disposeRequested) return;
        disposeRequested = true;
        if (Active) deferredUnpatch += harmony.UnpatchSelf;
        else harmony.UnpatchSelf();
    }
}
