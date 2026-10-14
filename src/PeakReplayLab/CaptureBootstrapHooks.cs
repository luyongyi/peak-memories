using System;
using System.Collections;
using HarmonyLib;

namespace PeakReplayLab;

// Preserve the native loading and spawn implementation. The only skipped call
// is the host's duplicate PopulateMapAndPlayerStates after our successful early
// invocation; FinalizeRunSetup and all native spawn/RPC methods remain native.
internal sealed class CaptureBootstrapHooks : IDisposable
{
    private readonly Harmony harmony = new("cn.mylus.peakreplaylab.bootstrap." + Guid.NewGuid().ToString("N"));
    private readonly Func<bool> hostAllowed;
    private readonly Func<bool, bool> restoreAllowed;
    private readonly Func<IEnumerator> waitForBaseline;
    private readonly Action<Exception> failure;
    private static CaptureBootstrapHooks? current;
    private int nativeSpawnDepth;
    private bool disposed;
    internal CaptureBootstrapHooks(Func<bool> hostAllowed, Func<bool, bool> restoreAllowed,
        Func<IEnumerator> waitForBaseline, Action<Exception> failure)
    {
        if (current != null) throw new InvalidOperationException("A capture bootstrap observer is already installed.");
        this.hostAllowed = hostAllowed; this.restoreAllowed = restoreAllowed;
        this.waitForBaseline = waitForBaseline; this.failure = failure;
        current = this;
        try
        {
            harmony.Patch(AccessTools.DeclaredMethod(typeof(CharacterSpawner), "SpawnHostCharacter") ??
                throw new MissingMethodException(typeof(CharacterSpawner).FullName, "SpawnHostCharacter"),
                prefix: new HarmonyMethod(typeof(CaptureBootstrapHooks), nameof(HostPrefix)),
                postfix: new HarmonyMethod(typeof(CaptureBootstrapHooks), nameof(HostPostfix)),
                finalizer: new HarmonyMethod(typeof(CaptureBootstrapHooks), nameof(HostFinalizer)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Peak.Quicksave), "PopulateMapAndPlayerStates") ??
                throw new MissingMethodException(typeof(Peak.Quicksave).FullName, "PopulateMapAndPlayerStates"),
                prefix: new HarmonyMethod(typeof(CaptureBootstrapHooks), nameof(RestorePrefix)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(LoadingScreen), "LoadingRoutine") ??
                throw new MissingMethodException(typeof(LoadingScreen).FullName, "LoadingRoutine"),
                prefix: new HarmonyMethod(typeof(CaptureBootstrapHooks), nameof(LoadingPrefix)));
        }
        catch { Dispose(); throw; }
    }
    private static bool HostPrefix(ref bool __state)
    {
        var owner = current;
        if (owner == null || ReplaySafety.Active) return true;
        try
        {
            if (!owner.hostAllowed()) return false;
            owner.nativeSpawnDepth++; __state = true; return true;
        }
        catch (Exception error) { owner.Report(error); return true; }
    }
    private static void HostPostfix(ref bool __state) => EndHost(ref __state);
    private static Exception? HostFinalizer(Exception? __exception, ref bool __state)
    { EndHost(ref __state); return __exception; }
    private static void EndHost(ref bool entered)
    {
        if (!entered) return; entered = false;
        if (current != null && current.nativeSpawnDepth > 0) current.nativeSpawnDepth--;
    }
    private static bool RestorePrefix()
    {
        var owner = current;
        if (owner == null || ReplaySafety.Active) return true;
        try { return owner.restoreAllowed(owner.nativeSpawnDepth > 0); }
        catch (Exception error) { owner.Report(error); return true; }
    }
    private static void LoadingPrefix(ref IEnumerator[] processList)
    {
        var owner = current;
        if (owner == null || ReplaySafety.Active) return;
        try
        {
            int count = processList?.Length ?? 0;
            var next = new IEnumerator[count + 1];
            if (count != 0) Array.Copy(processList!, next, count);
            next[count] = owner.waitForBaseline(); processList = next;
        }
        catch (Exception error) { owner.Report(error); }
    }
    private void Report(Exception error) { try { failure(error); } catch { } }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (ReferenceEquals(current, this)) current = null;
        nativeSpawnDepth = 0; harmony.UnpatchSelf();
    }
}
