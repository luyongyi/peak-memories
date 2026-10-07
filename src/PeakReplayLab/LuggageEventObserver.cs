using System;
using HarmonyLib;
using UnityEngine;

namespace PeakReplayLab;

internal enum LuggageEventKind { Discovered, Destroyed, Unclasp, Open, Reclasp }
internal readonly struct LuggageEvent
{
    public readonly Luggage Source;
    public readonly int Id;
    public readonly LuggageEventKind Kind;
    public readonly double Time;
    public readonly int Frame;
    public LuggageEvent(Luggage source, LuggageEventKind kind, double time)
    { Source = source; Id = source.GetInstanceID(); Kind = kind; Time = time; Frame = UnityEngine.Time.frameCount; }
}

// Observe real play only; never invoke interactions, RPCs or loot. Reclasp is interaction
// cleanup, not a player action to close an already-open chest.
internal sealed class LuggageEventObserver : IDisposable
{
    private static Action<LuggageEvent>? listener;
    private readonly Action<LuggageEvent> callback;
    private readonly Harmony harmony = new("cn.mylus.peakreplaylab.luggage-events");
    private bool disposed;
    public LuggageEventObserver(Action<LuggageEvent> callback)
    {
        this.callback = callback;
        if (listener != null) throw new InvalidOperationException("A luggage observer is already active.");
        listener = callback;
        try
        {
            Post("Awake", nameof(Discovered)); Post("OnDestroy", nameof(Destroyed));
            Post("Interact", nameof(Unclasp)); Post("CancelCast", nameof(Reclasp));
            var open = AccessTools.DeclaredMethod(typeof(Luggage), "OpenLuggageRPC") ?? throw new MissingMethodException("Luggage.OpenLuggageRPC");
            harmony.Patch(open, prefix: new HarmonyMethod(typeof(LuggageEventObserver), nameof(BeforeOpen)),
                postfix: new HarmonyMethod(typeof(LuggageEventObserver), nameof(Opened)));
        }
        catch { listener = null; harmony.UnpatchSelf(); throw; }
    }
    private void Post(string method, string observer)
    {
        var target = AccessTools.DeclaredMethod(typeof(Luggage), method) ?? throw new MissingMethodException("Luggage." + method);
        harmony.Patch(target, postfix: new HarmonyMethod(typeof(LuggageEventObserver), observer));
    }
    private static void Emit(Luggage source, LuggageEventKind kind, double delay = 0)
    {
        try { if (!ReplaySafety.Active && source) listener?.Invoke(new LuggageEvent(source, kind, Time.timeAsDouble + delay)); }
        catch { /* Observers never interrupt real gameplay. */ }
    }
    private static void Discovered(Luggage __instance) => Emit(__instance, LuggageEventKind.Discovered);
    private static void Destroyed(Luggage __instance) => Emit(__instance, LuggageEventKind.Destroyed);
    private static void Unclasp(Luggage __instance)
    { if (__instance && !__instance.IsOpen) Emit(__instance, LuggageEventKind.Unclasp); }
    private static void Reclasp(Luggage __instance)
    { if (__instance && !__instance.IsOpen) Emit(__instance, LuggageEventKind.Reclasp); }
    private static void BeforeOpen(Luggage __instance, ref bool __state) => __state = __instance && __instance.IsOpen;
    private static void Opened(Luggage __instance, bool spawnItems, bool __state)
    {
        if (!__state && __instance && __instance.IsOpen)
            Emit(__instance, LuggageEventKind.Open, spawnItems ? 0 : Math.Max(0, __instance.timeToOpen));
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (listener == callback) listener = null;
        harmony.UnpatchSelf();
    }
}
