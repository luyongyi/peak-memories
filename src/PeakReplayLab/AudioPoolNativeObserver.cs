using System;
using HarmonyLib;

namespace PeakReplayLab;

// Only the native SFX_Player owns these sources. Its managed starts/stops are
// authoritative; ordinary AudioSource owners keep their existing fallback.
internal sealed class AudioPoolNativeObserver : IDisposable
{
    private static AudioPoolNativeObserver? current;
    private readonly Harmony harmony = new("cn.mylus.peakreplaylab.audio-pool." + Guid.NewGuid().ToString("N"));
    private readonly Action<SFX_Player.SFX_Source> created, started, stopped;
    private bool disposed;
    internal AudioPoolNativeObserver(Action<SFX_Player.SFX_Source> created, Action<SFX_Player.SFX_Source> started,
        Action<SFX_Player.SFX_Source> stopped)
    {
        if (current != null) throw new InvalidOperationException("A native sound pool observer is already installed.");
        this.created = created; this.started = started; this.stopped = stopped; current = this;
        try
        {
            harmony.Patch(AccessTools.DeclaredMethod(typeof(SFX_Player), "CreateNewSource") ?? throw new MissingMethodException("SFX_Player.CreateNewSource"),
                postfix: new HarmonyMethod(typeof(AudioPoolNativeObserver), nameof(Created)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(SFX_Player.SFX_Source), nameof(SFX_Player.SFX_Source.StartPlaying)),
                prefix: new HarmonyMethod(typeof(AudioPoolNativeObserver), nameof(BeforeStart)),
                postfix: new HarmonyMethod(typeof(AudioPoolNativeObserver), nameof(Started)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(SFX_Player.SFX_Source), nameof(SFX_Player.SFX_Source.StopPlaying)),
                prefix: new HarmonyMethod(typeof(AudioPoolNativeObserver), nameof(BeforeStop)),
                postfix: new HarmonyMethod(typeof(AudioPoolNativeObserver), nameof(Stopped)));
        }
        catch { Dispose(); throw; }
    }
    private static void Created(SFX_Player.SFX_Source __result) { try { if (__result != null) current?.created(__result); } catch { } }
    private static void BeforeStart(SFX_Player.SFX_Source __instance, out bool __state) => __state = !__instance.isPlaying;
    private static void Started(SFX_Player.SFX_Source __instance, bool __state) { try { if (__state && __instance.isPlaying) current?.started(__instance); } catch { } }
    private static void BeforeStop(SFX_Player.SFX_Source __instance, out bool __state) => __state = __instance.isPlaying;
    private static void Stopped(SFX_Player.SFX_Source __instance, bool __state) { try { if (__state && !__instance.isPlaying) current?.stopped(__instance); } catch { } }
    public void Dispose()
    { if (disposed) return; disposed = true; if (ReferenceEquals(current, this)) current = null; harmony.UnpatchSelf(); }
}

internal static class AudioPoolCapturePolicy
{
    internal static bool CanSkipIdle(bool reliableOwner, bool nativePlaying, bool observed, bool recorderPlaying) =>
        reliableOwner && !nativePlaying && observed && !recorderPlaying;
}
