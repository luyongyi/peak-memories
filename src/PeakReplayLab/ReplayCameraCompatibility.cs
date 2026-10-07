using System;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using UnityEngine;

namespace PeakReplayLab;

// Audited against the locally installed FreeCAM 0.0.2. Its manager retries a
// missing MainCameraMovement every Update, including during replay scene loads.
// Suspend only that exact optional camera manager, not its plugin, other mods,
// Photon, scene loaders, or any unknown future version's lifecycle.
internal sealed class ReplayCameraCompatibility : IDisposable
{
    private readonly List<(Behaviour Component, bool Enabled)> suspended = new();
    public int Count => suspended.Count;
    public ReplayCameraCompatibility()
    {
        if (!Chainloader.PluginInfos.TryGetValue("com.Bananegame.FreeCAM", out var info) ||
            info.Metadata.Version.ToString() != "0.0.2" || !info.Instance) return;
        var plugin = info.Instance;
        var assembly = plugin.GetType().Assembly;
        if (plugin.GetType().FullName != "FreeCAM.Plugin" || assembly.GetName().Name != "com.Bananegame.FreeCAM") return;
        var manager = assembly.GetType("FreeCAM.FreeCamManager", false);
        if (manager == null || !typeof(Behaviour).IsAssignableFrom(manager)) return;
        foreach (var component in plugin.GetComponents(manager))
        {
            if (component is not Behaviour behaviour || behaviour.GetType() != manager) continue;
            suspended.Add((behaviour, behaviour.enabled));
            behaviour.enabled = false;
        }
    }

    public void Dispose()
    {
        foreach (var entry in suspended)
        {
            try { if (entry.Component) entry.Component.enabled = entry.Enabled; }
            catch { /* A scene/object may already have been destroyed; restore the rest. */ }
        }
        suspended.Clear();
    }
}
