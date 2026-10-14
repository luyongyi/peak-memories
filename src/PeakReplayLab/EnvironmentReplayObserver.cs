using System;
using HarmonyLib;
using UnityEngine;

namespace PeakReplayLab;

// Actual native lifecycle entry points, not Update polling. Every hook only
// queues the specific component; registration and Unity reads occur at Sample.
internal sealed class EnvironmentReplayObserver : IDisposable
{
    private static EnvironmentReplayObserver? current;
    private readonly Harmony harmony = new("cn.mylus.peakreplaylab.environment-directory");
    private readonly Action<Component> changed;
    private bool disposed;
    public EnvironmentReplayObserver(Action<Component> changed)
    {
        if (current != null) throw new InvalidOperationException("Environment lifecycle observer already exists.");
        this.changed = changed;
        try
        {
            Patch(typeof(WindChillZone), "Awake"); Patch(typeof(WindChillZone), "Start");
            Patch(typeof(StormVisual), "Start");
            Patch(typeof(LavaRising), "Awake"); Patch(typeof(LavaRising), "Start");
            Patch(typeof(MovingLava), "Start");
            Patch(typeof(OrbFogHandler), "Awake"); Patch(typeof(OrbFogHandler), "Start");
            Patch(typeof(FogSphere), "Start");
            current = this;
        }
        catch { harmony.UnpatchSelf(); throw; }
    }
    private void Patch(Type type, string method)
    {
        var target = AccessTools.DeclaredMethod(type, method) ?? throw new MissingMethodException(type.FullName, method);
        harmony.Patch(target, postfix: new HarmonyMethod(typeof(EnvironmentReplayObserver), nameof(Observed)));
    }
    private static void Observed(Component __instance)
    {
        // Diagnostic failure must not change native Awake/Start semantics.
        if (__instance) try { current?.changed(__instance); } catch { }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (ReferenceEquals(current, this)) current = null;
        harmony.UnpatchSelf();
    }
}
