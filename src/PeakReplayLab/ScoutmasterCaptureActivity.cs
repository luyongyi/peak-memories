using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PeakReplayLab;

// The native Scoutmaster is an enabled ragdoll even while waiting at its spawn
// point or HideySpot. Only its encounter lifecycle should admit bone sampling.
internal sealed class ScoutmasterCaptureActivity : IDisposable
{
    private static readonly Dictionary<Character, List<ScoutmasterCaptureActivity>> registered = new();
    private static readonly AccessTools.FieldRef<Character, Vector3> lastWarpTarget =
        AccessTools.FieldRefAccess<Character, Vector3>("_lastWarpTarget");
    private const float ParkingToleranceSquared = 25;
    private readonly Scoutmaster source;
    private readonly Character? character;
    private Character? previousTarget, previousDiscovered;
    private bool initialized, engaged, suspended, warpObserved, pendingPark, parked, disposed;

    public ScoutmasterCaptureActivity(Scoutmaster source)
    {
        if (!source) throw new ArgumentNullException(nameof(source));
        this.source = source;
        character = source.GetComponent<Character>() ?? throw new InvalidOperationException("Native Scoutmaster has no Character component.");
        if (!registered.TryGetValue(character, out var listeners)) registered.Add(character, listeners = new List<ScoutmasterCaptureActivity>());
        listeners.Add(this);
        character.WarpCompleted += WarpCompleted;
    }

    public bool Active
    {
        get
        {
            if (disposed || ReplaySafety.Active || !source || !character) return false;
            if (!source.enabled || !source.gameObject.activeInHierarchy)
            {
                // A stale target must not immediately wake a parked object on
                // re-enable. Reconcile its real body once when it returns.
                Suspend();
                return false;
            }
            if (!initialized)
            {
                var data = character!.data;
                var ragdoll = character.refs?.ragdoll;
                if (!data || !ragdoll || !ragdoll!.partDict.TryGetValue(BodypartType.Torso, out var torso) || !torso) return false;
                var position = torso!.transform.position;
                // spawnPoint is assigned by the master after native Awake.
                // Remote clients retain the original Character root instead;
                // native warps move the ragdoll, never that original root.
                var spawn = data.spawnPoint ? data.spawnPoint.position : source.transform.position;
                bool atHide = Near(position, Character.ScoutmasterHideySpot);
                bool awayFromSpawn = !Near(position, spawn);
                if (!warpObserved || suspended)
                {
                    if (character.warping || character.timeLastWarped > 0)
                    {
                        // A parked native ragdoll keeps falling under gravity.
                        // Its completed destination is authoritative; distance
                        // from the parking point cannot identify an encounter.
                        bool returning = lastWarpTarget(character) == Character.ScoutmasterHideySpot;
                        warpObserved = true; pendingPark = returning && character.warping;
                        parked = returning && !character.warping;
                        engaged = !parked;
                    }
                    else
                    {
                        // A master records the spawnPoint even before the first
                        // warp. Falling away from it is still idle. A late-join
                        // client can lack both this anchor and warp history, so
                        // retain the conservative body fallback only there.
                        engaged = !atHide && (!data.spawnPoint && awayFromSpawn ||
                            !suspended && (source.currentTarget || source.discovered));
                    }
                }
                initialized = true; suspended = false; RememberSignals();
            }
            var target = source.currentTarget; var discovered = source.discovered;
            bool started = target && target != previousTarget || discovered && discovered != previousDiscovered;
            previousTarget = target; previousDiscovered = discovered;
            if (!pendingPark && started) { engaged = true; parked = false; }
            // Losing a target is not the end of an encounter: the native body
            // may still flee, throw a scout, or walk until its return warp.
            return engaged;
        }
    }

    internal static void ObserveWarp(Character character, Vector3 destination)
    {
        if (ReplaySafety.Active || !character || !registered.TryGetValue(character, out var listeners)) return;
        foreach (var activity in listeners)
        {
            activity.warpObserved = true;
            activity.pendingPark = destination == Character.ScoutmasterHideySpot;
            if (!activity.pendingPark) { activity.engaged = true; activity.parked = false; }
            // A return warp yields several fixed frames before moving the body.
            // Keep the visible departure until the native completion callback.
            activity.RememberSignals();
        }
    }

    internal static void ObserveDisabled(Scoutmaster source)
    {
        if (ReplaySafety.Active || !source) return;
        var character = source.GetComponent<Character>();
        if (!character || !registered.TryGetValue(character!, out var listeners)) return;
        foreach (var activity in listeners) activity.Suspend();
    }

    private void WarpCompleted(Character completed)
    {
        if (disposed || ReplaySafety.Active || completed != character) return;
        try
        {
            if (!warpObserved)
            {
                // Recording may start in the middle of an existing native warp.
                // Its destination event predates us, so reconcile the settled body.
                initialized = false; _ = Active; return;
            }
            if (!pendingPark) return;
            pendingPark = false; parked = true; engaged = false;
            RememberSignals();
        }
        catch { initialized = false; } // Never interrupt native or other listeners.
    }

    private void RememberSignals() { previousTarget = source.currentTarget; previousDiscovered = source.discovered; }
    private void Suspend() { engaged = false; initialized = false; suspended = true; RememberSignals(); }
    private static bool Near(Vector3 left, Vector3 right) => (left - right).sqrMagnitude <= ParkingToleranceSquared;
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (character is null) return;
        character.WarpCompleted -= WarpCompleted;
        if (registered.TryGetValue(character, out var listeners))
        { listeners.Remove(this); if (listeners.Count == 0) registered.Remove(character); }
    }
}
