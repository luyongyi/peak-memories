using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;

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
    private readonly Action<GameObject>? appearanceChanged;
    private bool disposed;
    public CreatureReplayObserver(Action<GameObject> birth, Action<GameObject>? destruction = null, Action<GameObject>? changed = null,
        Action<GameObject>? appearanceChanged = null)
    {
        this.birth = birth; this.destruction = destruction; this.changed = changed; this.appearanceChanged = appearanceChanged;
        if (listeners.Count == 0)
        {
            var patch = new Harmony("cn.mylus.peakreplaylab.creatures." + Guid.NewGuid().ToString("N"));
            try
            {
                patch.Patch(AccessTools.DeclaredMethod(typeof(Spider), "Awake"), postfix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(Born)));
                patch.Patch(AccessTools.DeclaredMethod(typeof(MushroomZombie), "Awake"), postfix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(Born)));
                patch.Patch(AccessTools.DeclaredMethod(typeof(Condor), "Start"), postfix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(Born)));
                patch.Patch(AccessTools.DeclaredMethod(typeof(FrogTongue), "OnEnable"), postfix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(Born)));
                // Independent mobs are not Item.ALL_ITEMS. Birth callbacks and
                // the single scene-entry pass maintain the bounded directory.
                // Observing Mob.Awake also sees Frogs; registration deduplicates
                // them and ignores item-only Scorpions (owned by ItemCapture).
                ObserveBirth(patch, typeof(Mob), "Awake");
                ObserveBirth(patch, typeof(Scoutmaster), "OnEnable");
                ObserveBirth(patch, typeof(Scoutmaster), "Start");
                patch.Patch(AccessTools.DeclaredMethod(typeof(Character), "WarpPlayer"),
                    postfix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(ScoutmasterWarp)));
                patch.Patch(AccessTools.DeclaredMethod(typeof(Scoutmaster), "OnDisable"),
                    postfix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(ScoutmasterDisabled)));
                ObserveBirth(patch, typeof(Antlion), "Start");
                ObserveBirth(patch, typeof(VenusFlyTrap), "Start");
                ObserveBirth(patch, typeof(TumbleWeed), "Start");
                ObserveBirth(patch, typeof(Peak.Island.VoiceThief), "Start");
                RavenAnimationCapture.EventHooksHealthy = InstallRavenHooks(patch);
                FrogCaptureActivity.EventHooksHealthy = InstallFrogHooks(patch);
                // These are real action boundaries, not Update/FixedUpdate
                // callbacks. They can bypass the low-rate observation clock
                // without repeatedly dirtying immutable renderer metadata.
                ObserveAction(patch, typeof(Condor), "RPCA_CondorAction");
                ObserveAction(patch, typeof(VenusFlyTrap), "RPC_BitePlayer");
                ObserveAction(patch, typeof(VenusFlyTrap), "RPC_LetGo");
                ObserveAction(patch, typeof(Antlion), "RPCA_Attack");
                ObserveAction(patch, typeof(Antlion), "RPCA_SetClosestTarget");
                ObserveAction(patch, typeof(TumbleWeed), "OnCollisionEnter");
                ObserveAction(patch, typeof(Peak.ScoutmasterGhostOrbiter), "RPC_SetOrbitCharacter");
                ObserveAction(patch, typeof(Peak.GhostBall), "OnTargetIDChanged");
                ObserveAction(patch, typeof(Peak.GhostBall), "Explode");
                ObserveBirth(patch, typeof(Peak.ScoutmasterGhostOrbiter), "Start");
                ObserveBirth(patch, typeof(Peak.GhostBall), "Awake");
                ObserveBirth(patch, typeof(PlayerGhost), "Awake");
                // Initialization copies the dead player's actual appearance
                // and renderer visibility after the normal native spawn.
                patch.Patch(AccessTools.DeclaredMethod(typeof(PlayerGhost), "RPCA_InitGhost"),
                    postfix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(NativeAppearanceChanged)));
                ObserveBirth(patch, typeof(Skelleton), "SpawnSkelly");
                ObserveBirth(patch, typeof(Skelleton), "SetCharacter");
                ObserveBirth(patch, typeof(Capybara), "OnEnable");
                ObserveBirth(patch, typeof(MirageLuggage), "OnEnable");
                patch.Patch(AccessTools.DeclaredMethod(typeof(MushroomZombie), "RPC_Arise"), postfix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(NativeAppearanceChanged)));
                patch.Patch(AccessTools.DeclaredMethod(typeof(MushroomZombie), "RPC_SetOutfit"), postfix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(NativeAppearanceChanged)));
                // Native methods complete normally; only admit the next observed
                // visual output. Bonk can change animation while State stays Idle.
                foreach (string action in new[] { "RPCA_DropSpider", "RPCA_GrabCharacter", "RPCA_LetGo", "BonkRPC" })
                    patch.Patch(AccessTools.DeclaredMethod(typeof(Spider), action) ?? throw new MissingMethodException(typeof(Spider).FullName, action),
                        postfix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(NativeChanged)));
                patch.Patch(AccessTools.DeclaredMethod(typeof(Mob), "UpdateSleeping"),
                    postfix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(NativeChanged)));
                patch.Patch(AccessTools.DeclaredMethod(typeof(Spider), "OnDestroy"), prefix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(Died)));
                patch.Patch(AccessTools.DeclaredMethod(typeof(MushroomZombie), "OnDestroy"), prefix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(Died)));
                patch.Patch(AccessTools.DeclaredMethod(typeof(Mob), "OnDestroy"), prefix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(Died)));
                patch.Patch(AccessTools.DeclaredMethod(typeof(Scoutmaster), "OnDestroy"), prefix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(Died)));
                patch.Patch(AccessTools.DeclaredMethod(typeof(Peak.Island.VoiceThief), "OnDestroy"), prefix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(Died)));
                patch.Patch(AccessTools.DeclaredMethod(typeof(Peak.GhostBall), "OnDestroy"), prefix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(Died)));
                // PlayerGhost declares no destruction callback. The bounded
                // capture directory retires its invalid Unity reference on the
                // next observation; do not invent an absent native method.
                // Trigger only starts a coroutine. The native UnityEvent runs
                // after WaitForEndOfFrame and may deactivate this trap then.
                // Mark its observation dirty only after that existing event
                // completes, or the next sample consumes the change too early.
                var routine = AccessTools.DeclaredMethod(typeof(TriggerEvent), "TriggerRoutine")
                    ?? throw new MissingMethodException(nameof(TriggerEvent), "TriggerRoutine");
                var moveNext = AccessTools.EnumeratorMoveNext(routine)
                    ?? throw new MissingMethodException(nameof(TriggerEvent), "TriggerRoutine.MoveNext");
                patch.Patch(moveNext, transpiler: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(ObserveTrapInvocation)));
                // A future area's first Awake may apply custom hazard settings
                // after its initial static checkpoint. Observe that native
                // transition once, without polling inactive map hierarchies.
                var settings = AccessTools.DeclaredMethod(typeof(DisableBasedOnRunSettings), "UpdateEnabled")
                    ?? throw new MissingMethodException(nameof(DisableBasedOnRunSettings), "UpdateEnabled");
                patch.Patch(settings,
                    prefix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(TrapSettingsChanging)),
                    postfix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(TrapSettingsChanged)));
                harmony = patch;
            }
            catch
            {
                RavenAnimationCapture.EventHooksHealthy = FrogCaptureActivity.EventHooksHealthy = false;
                patch.UnpatchSelf(); throw;
            }
        }
        listeners.Add(this);
    }
    private static void ObserveBirth(Harmony patch, Type type, string name)
    {
        var method = AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);
        patch.Patch(method, postfix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(Born)));
    }
    private static void ObserveAction(Harmony patch, Type type, string name)
    {
        var method = AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);
        patch.Patch(method, postfix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(NativeChanged)));
    }
    // Optional optimizations fail open to ordinary observed geometry if a game
    // update changes any native boundary. The shared creature observer remains
    // usable for all the other species and baseline recording.
    private static bool InstallRavenHooks(Harmony patch)
    {
        try
        {
            foreach (string action in new[] { "RPCA_Assign", "RPCA_FlyAway", "SetActivelyStealing" })
                patch.Patch(AccessTools.DeclaredMethod(typeof(Peak.Island.VoiceThief), action) ??
                    throw new MissingMethodException(typeof(Peak.Island.VoiceThief).FullName, action),
                    postfix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(NativeChanged)));
            patch.Patch(AccessTools.DeclaredMethod(typeof(Peak.RandomizeAnimatorParameter), "OnEnable") ??
                throw new MissingMethodException(typeof(Peak.RandomizeAnimatorParameter).FullName, "OnEnable"),
                postfix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(RavenRandomized)));
            return true;
        }
        catch { return false; }
    }
    private static bool InstallFrogHooks(Harmony patch)
    {
        try
        {
            // Native OnCollisionEnter ignores ordinary floor contacts and calls
            // HopAway for the wall-jump branch. Observe that actual action once.
            foreach (string action in new[] { "Reposition", "Attack", "HopAway", "LetGo" })
                patch.Patch(AccessTools.DeclaredMethod(typeof(FrogTongue), action) ?? throw new MissingMethodException(nameof(FrogTongue), action),
                    postfix: new HarmonyMethod(typeof(CreatureReplayObserver), nameof(NativeChanged)));
            return true;
        }
        catch { return false; }
    }
    private static void Born(Component __instance)
    {
        if (!__instance) return;
        foreach (var listener in listeners) try { listener.birth(__instance.gameObject); } catch { }
        // Reuse the native birth signal for a creature's child particle effects.
        // The effect observer owns its own ReplaySafety/capture activity guard.
        try { EffectReplayCapture.ObserveNativeSpawn(__instance.gameObject); } catch { }
    }
    private static void Died(Component __instance)
    { if (__instance) foreach (var listener in listeners) try { listener.destruction?.Invoke(__instance.gameObject); } catch { } }
    private static void ScoutmasterWarp(Character __instance, Vector3 position)
    { try { ScoutmasterCaptureActivity.ObserveWarp(__instance, position); } catch { } }
    private static void ScoutmasterDisabled(Scoutmaster __instance)
    { try { ScoutmasterCaptureActivity.ObserveDisabled(__instance); } catch { } }
    private static void NativeChanged(Component __instance)
    {
        if (!__instance) return;
        if (__instance is FrogTongue frog) try { FrogCaptureActivity.NotifyNativeEvent(frog); } catch { }
        foreach (var listener in listeners) try { (listener.changed ?? listener.birth)(__instance.gameObject); } catch { }
    }
    private static void NativeAppearanceChanged(Component __instance)
    {
        if (!__instance) return;
        foreach (var listener in listeners)
            try { (listener.appearanceChanged ?? listener.changed ?? listener.birth)(__instance.gameObject); } catch { }
    }
    private static void RavenRandomized(Peak.RandomizeAnimatorParameter __instance)
    {
        if (!__instance) return;
        var raven = __instance.GetComponentInParent<Peak.Island.VoiceThief>();
        if (raven) NativeChanged(raven!);
    }
    private static IEnumerable<CodeInstruction> ObserveTrapInvocation(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var source = new List<CodeInstruction>(instructions);
        var invoke = AccessTools.DeclaredMethod(typeof(UnityEvent), nameof(UnityEvent.Invoke));
        FieldInfo? owner = null;
        foreach (var field in __originalMethod.DeclaringType!.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (field.FieldType == typeof(TriggerEvent))
            {
                if (owner != null) throw new InvalidOperationException("Native trap coroutine has ambiguous owners.");
                owner = field;
            }
        int calls = 0;
        foreach (var instruction in source)
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && Equals(instruction.operand, invoke)) calls++;
        if (owner == null || calls != 1) throw new InvalidOperationException("Native trap completion callback no longer matches its audited coroutine.");
        var observer = AccessTools.DeclaredMethod(typeof(CreatureReplayObserver), nameof(TrapChanged));
        foreach (var instruction in source)
        {
            yield return instruction;
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && Equals(instruction.operand, invoke))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldfld, owner);
                yield return new CodeInstruction(OpCodes.Call, observer);
            }
        }
    }
    private static void TrapSettingsChanging(DisableBasedOnRunSettings __instance, out bool? __state)
    {
        __state = null;
        try { if (__instance) __state = __instance.gameObject.activeSelf; } catch { }
    }
    private static void TrapSettingsChanged(DisableBasedOnRunSettings __instance, bool? __state)
    {
        try
        {
            if (__state.HasValue && __instance && __state.Value != __instance.gameObject.activeSelf)
                ObserveTrapChange(__instance);
        }
        catch { } // Settings must complete even if observation is unavailable.
    }
    private static void TrapChanged(TriggerEvent __instance) => ObserveTrapChange(__instance);
    private static void ObserveTrapChange(Component source)
    {
        try
        {
            if (!source) return;
            var trap = CreatureReplayResources.Trap(source.transform); if (!trap) return;
            foreach (var listener in listeners) try { (listener.changed ?? listener.birth)(trap!.gameObject); } catch { }
        }
        catch { } // Observation must not prevent native activation bookkeeping.
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; listeners.Remove(this);
        if (listeners.Count == 0)
        {
            RavenAnimationCapture.EventHooksHealthy = FrogCaptureActivity.EventHooksHealthy = false;
            harmony?.UnpatchSelf(); harmony = null;
        }
    }
}
