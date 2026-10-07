using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PeakReplayLab;

// Passive shared directory events for both recording and hiding live templates.
// Native Awake/destruction/trigger work completes as usual outside presentation.
internal sealed class CreatureReplayObserver : IDisposable
{
    private static readonly HashSet<CreatureReplayObserver> listeners = new();
    private static Harmony? harmony;
    private readonly Action<GameObject> birth;
    private readonly Action<GameObject>? destruction;
    private readonly Action<GameObject>? changed;
    private bool disposed;
    public CreatureReplayObserver(Action<GameObject> birth, Action<GameObject>? destruction = null, Action<GameObject>? changed = null)
    {
        this.birth = birth; this.destruction = destruction; this.changed = changed;
        if (listeners.Count == 0)
        {
            var patch = new Harmony("cn.mylus.peakreplaylab.creatures." + Guid.NewGuid().ToString("N"));
            try
            {
                patch.Patch(AccessTools.DeclaredMethod(typeof(Spider), "Awake"), postfix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(Born)));
                patch.Patch(AccessTools.DeclaredMethod(typeof(MushroomZombie), "Awake"), postfix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(Born)));
                patch.Patch(AccessTools.DeclaredMethod(typeof(Spider), "OnDestroy"), prefix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(Died)));
                patch.Patch(AccessTools.DeclaredMethod(typeof(MushroomZombie), "OnDestroy"), prefix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(Died)));
                patch.Patch(AccessTools.DeclaredMethod(typeof(TriggerEvent), "Trigger"), postfix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(TrapChanged)));
                harmony = patch;
            }
            catch { patch.UnpatchSelf(); throw; }
        }
        listeners.Add(this);
    }
    private static void Born(Component __instance)
    { if (__instance) foreach (var listener in listeners) try { listener.birth(__instance.gameObject); } catch { } }
    private static void Died(Component __instance)
    { if (__instance) foreach (var listener in listeners) try { listener.destruction?.Invoke(__instance.gameObject); } catch { } }
    private static void TrapChanged(TriggerEvent __instance)
    {
        if (!__instance) return;
        var trap = CreatureReplayResources.Trap(__instance.transform); if (!trap) return;
        foreach (var listener in listeners) try { (listener.changed ?? listener.birth)(trap!.gameObject); } catch { }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; listeners.Remove(this);
        if (listeners.Count == 0) { harmony?.UnpatchSelf(); harmony = null; }
    }
}
