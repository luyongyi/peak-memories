using System;
using HarmonyLib;
using UnityEngine;

namespace PeakReplayLab;

// Managed action bits, cached at admission. They catch the end of short native
// coroutines (Antlion's attack bool lasts only .12 s), without inspecting an
// Animator, searching a hierarchy, or allocating a snapshot on every tick.
internal sealed class CreatureActionState
{
    private readonly Antlion? antlion;
    private readonly VenusFlyTrap? flyTrap;
    private readonly Peak.Island.VoiceThief? raven;
    private readonly Peak.GhostBall? ghostBall;
    private static readonly AccessTools.FieldRef<Antlion, bool>? attacking = Field<Antlion, bool>("attacking");
    private static readonly AccessTools.FieldRef<VenusFlyTrap, bool>? munching = Field<VenusFlyTrap, bool>("_isMunching");
    private static readonly AccessTools.FieldRef<Peak.GhostBall, bool>? burning = Field<Peak.GhostBall, bool>("burning");
    private static readonly AccessTools.FieldRef<Peak.GhostBall, bool>? exploding = Field<Peak.GhostBall, bool>("exploding");
    private static readonly AccessTools.FieldRef<Peak.GhostBall, bool>? ready = Field<Peak.GhostBall, bool>("_readyToExplode");
    private static readonly AccessTools.FieldRef<Peak.GhostBall, bool>? hasTarget = Field<Peak.GhostBall, bool>("hasTarget");
    private static readonly AccessTools.FieldRef<Peak.GhostBall, bool>? burned = Field<Peak.GhostBall, bool>("_playedBurnDeath");
    internal CreatureActionState(GameObject source, string kind)
    {
        if (kind == "antlion") antlion = source.GetComponent<Antlion>();
        if (kind == "venus-flytrap") flyTrap = source.GetComponent<VenusFlyTrap>();
        if (kind == "voice-thief") raven = source.GetComponent<Peak.Island.VoiceThief>();
        if (kind == "ghost-ball") ghostBall = source.GetComponent<Peak.GhostBall>();
    }
    internal int State => antlion && attacking != null ? (attacking(antlion!) ? 1 : 0) :
        flyTrap && munching != null ? (munching(flyTrap!) ? 1 : 0) : raven ? (raven!.IsLeaving ? 1 : 0) :
        ghostBall && GhostFieldsReady ?
            (burning!(ghostBall!) ? 1 : 0) | (exploding!(ghostBall!) ? 2 : 0) | (ready!(ghostBall!) ? 4 : 0) |
            (hasTarget!(ghostBall!) ? 8 : 0) | (burned!(ghostBall!) ? 16 : 0) : 0;
    internal bool Reliable => (!antlion || attacking != null) && (!flyTrap || munching != null) && (!ghostBall || GhostFieldsReady);
    private static bool GhostFieldsReady => burning != null && exploding != null && ready != null && hasTarget != null && burned != null;
    private static AccessTools.FieldRef<T, F>? Field<T, F>(string name) where T : class
    { try { return AccessTools.FieldRefAccess<T, F>(name); } catch { return null; } }
}
