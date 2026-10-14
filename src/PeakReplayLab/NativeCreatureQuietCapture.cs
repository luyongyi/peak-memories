using System;
using HarmonyLib;
using UnityEngine;

namespace PeakReplayLab;

// A held checkpoint is published only after a successful final read. Neither
// repeated sampling nor root movement renews the finite native-action tail.
internal sealed class NativeCreatureQuietPolicy
{
    private bool known, committedQuiet, dirty = true;
    private double awakeUntil = double.NegativeInfinity;
    internal bool Quiet { get; private set; }
    internal bool LastWasBoundary { get; private set; }
    internal int ClockMode { get; private set; } = CreatureReplayRules.ActiveClock;
    internal void NotifyNativeEvent(double now)
    {
        dirty = true;
        if (!double.IsNaN(now) && !double.IsInfinity(now)) awakeUntil = Math.Max(awakeUntil, now + 1);
    }
    internal bool Due(double now, bool eligible, bool forceRead)
    {
        bool quiet = eligible && !double.IsNaN(now) && !double.IsInfinity(now) && now >= awakeUntil;
        ClockMode = quiet ? CreatureReplayRules.SleepingClock : CreatureReplayRules.ActiveClock;
        LastWasBoundary = dirty || known && committedQuiet != quiet;
        Quiet = quiet && known && committedQuiet && !dirty && !forceRead;
        return !Quiet;
    }
    internal void CompleteRead(bool success)
    {
        if (!success) { dirty = true; Quiet = false; return; }
        known = true; committedQuiet = ClockMode == CreatureReplayRules.SleepingClock; dirty = false;
    }
}

// Only the audited, already-disabled native Venus Animator may be held. Its
// enabled 1.5-second idle has ten varying streamed channels and must continue
// actual pose sampling until a separate inert native-idle implementation exists.
internal sealed class NativeCreatureQuietCapture
{
    private const int VenusIdleState = 1432961145;
    private static readonly int Biting = Animator.StringToHash("Biting");
    private static readonly AccessTools.FieldRef<VenusFlyTrap, Character>? target = Resolve<Character>("_targetPlayer");
    private static readonly AccessTools.FieldRef<VenusFlyTrap, bool>? munching = Resolve<bool>("_isMunching");
    private readonly GameObject source;
    private readonly string kind, resource;
    private readonly VenusFlyTrap? venus;
    private readonly Animator? animator;
    private readonly RuntimeAnimatorController? controller;
    private readonly Transform? trapRoot;
    private readonly Animator[] animators;
    private readonly Animation[] legacyAnimations;
    private readonly Rigidbody[] bodies;
    private readonly MonoBehaviour[] scripts;
    private readonly WobbleSpinBounce[] wobblers;
    private readonly Renderer[] renderers;
    private readonly bool[] scriptEnabled, rendererEnabled, rendererActive;
    private readonly bool audited;
    private readonly NativeCreatureQuietPolicy policy = new();
    private Transform? anchorParent, trapParent;
    private Vector3 trapPosition, trapScale;
    private Quaternion trapRotation;
    private bool anchored;
    private bool safetyKnown, safetyEligible;
    private double nextSafetyAt = double.NegativeInfinity;
    private readonly double safetyInterval;
    internal bool Quiet => policy.Quiet;
    internal int ClockMode => policy.ClockMode;
    internal bool LastWasBoundary => policy.LastWasBoundary;
    internal bool RequiresAppearanceRead { get; private set; }
    internal long QuietSafetyChecks { get; private set; }

    internal NativeCreatureQuietCapture(GameObject source, string kind, string resource)
    {
        this.source = source; this.kind = kind; this.resource = resource;
        animators = source.GetComponentsInChildren<Animator>(true);
        legacyAnimations = source.GetComponentsInChildren<Animation>(true);
        bodies = source.GetComponentsInChildren<Rigidbody>(true);
        scripts = source.GetComponentsInChildren<MonoBehaviour>(true);
        wobblers = source.GetComponentsInChildren<WobbleSpinBounce>(true);
        renderers = source.GetComponentsInChildren<Renderer>(true);
        scriptEnabled = new bool[scripts.Length]; rendererEnabled = new bool[renderers.Length]; rendererActive = new bool[renderers.Length];
        safetyInterval = .75 + (uint)source.GetInstanceID() % 251 / 1000d;
        bool scriptSignature = true;
        foreach (var script in scripts) if (!script || !KnownScript(script.GetType(),kind)) scriptSignature = false;
        if (kind == "venus-flytrap" && resource == kind)
        {
            venus = source.GetComponent<VenusFlyTrap>(); animator = venus ? venus!.anim : null;
            trapRoot = venus ? venus!.trapRoot : null; controller = animator ? animator!.runtimeAnimatorController : null;
            audited = scriptSignature && venus && animator && trapRoot && trapRoot!.IsChildOf(source.transform) &&
                animators.Length == 1 && animators[0] == animator && legacyAnimations.Length == 0 && bodies.Length == 0 &&
                target != null && munching != null && AuditedController(controller) && animator!.GetBehaviours<StateMachineBehaviour>().Length == 0;
        }
        else if (kind == "capybara" && resource == kind)
            audited = scriptSignature && (source.GetComponent<Capybara>() || source.GetComponent<MirageLuggage>()) &&
                animators.Length == 0 && legacyAnimations.Length == 0 && bodies.Length == 0;
    }
    internal void NotifyNativeEvent(double now)
    { policy.NotifyNativeEvent(now); safetyKnown = false; nextSafetyAt = double.NegativeInfinity; }
    internal bool Due(double now, CreatureReplayFrame? previous, bool forceRead = false)
    {
        RequiresAppearanceRead = false;
        bool eligible = false;
        try
        {
            if (CheapEligible(previous))
            {
                if (!safetyKnown || now >= nextSafetyAt || forceRead)
                {
                    QuietSafetyChecks++;
                    safetyEligible = Eligible(previous!); safetyKnown = true; nextSafetyAt = now + safetyInterval;
                }
                eligible = safetyEligible;
            }
            else { safetyKnown = false; nextSafetyAt = double.NegativeInfinity; }
        }
        catch { eligible = false; }
        return policy.Due(now, eligible, forceRead || RequiresAppearanceRead);
    }
    private bool CheapEligible(CreatureReplayFrame? previous)
    {
        if (!audited || !source || previous == null || previous.Kind != kind || previous.Resource != resource ||
            !source.activeInHierarchy || !previous.Pose.Active || !anchored) return false;
        // Enabled Venus idle is known to animate: reject before any pose,
        // renderer directory, controller or Animator-state safety query.
        if (kind == "venus-flytrap" && (!venus || !animator || !trapRoot || venus!.anim != animator || venus.trapRoot != trapRoot ||
            target!(venus) || munching!(venus) || animator!.enabled)) return false;
        foreach (var wobble in wobblers) if (!wobble || wobble.enabled && wobble.gameObject.activeInHierarchy) return false;
        return true;
    }
    private bool Eligible(CreatureReplayFrame previous)
    {
        if (source.transform.parent != anchorParent || !SameRoot(source.transform,previous.Pose)) return false;
        for (int i = 0; i < scripts.Length; i++)
        {
            var script = scripts[i];
            if (!script) return false;
            if (script.enabled != scriptEnabled[i]) { RequiresAppearanceRead = true; return false; }
        }
        for (int i = 0; i < renderers.Length; i++)
        {
            var renderer = renderers[i];
            if (!renderer || renderer.enabled != rendererEnabled[i] || renderer.gameObject.activeInHierarchy != rendererActive[i])
            { RequiresAppearanceRead = true; return false; }
        }
        if (kind == "capybara") return true;
        if (animator!.runtimeAnimatorController != controller || !animator.isInitialized || animator.layerCount != 1 ||
            animator.IsInTransition(0) || animator.GetBool(Biting) || animator.GetCurrentAnimatorStateInfo(0).fullPathHash != VenusIdleState ||
            trapRoot!.parent != trapParent) return false;
        var trap = trapRoot!;
        var p = trap.position; var r = trap.rotation; var s = trap.lossyScale;
        return Same(p,trapPosition) && Same(r,trapRotation) && Same(s,trapScale);
    }
    // Call after assigning the newly completed immutable frame. Failed geometry
    // or appearance must never become permission to reuse an old checkpoint.
    internal void CompleteRead(CreatureReplayFrame? frame, bool visualComplete = true)
    {
        bool success = source && frame != null && visualComplete;
        policy.CompleteRead(success);
        safetyKnown = false; nextSafetyAt = double.NegativeInfinity;
        if (!success) return;
        // Active/unknown inputs already have a real geometry observation. Do
        // not reread their roots and renderer directory just to prepare an idle
        // optimization they cannot enter. The first eligible sparse observation
        // anchors them; a subsequent final sleeping observation commits the hold.
        if (!audited || !source!.activeInHierarchy || kind == "venus-flytrap" &&
            (!venus || !animator || animator!.enabled || target!(venus!) || munching!(venus!)))
        { anchored = false; return; }
        foreach (var wobble in wobblers)
            if (!wobble || wobble.enabled && wobble.gameObject.activeInHierarchy) { anchored = false; return; }
        anchorParent = source!.transform.parent;
        for (int i = 0; i < scripts.Length; i++) if (scripts[i]) scriptEnabled[i] = scripts[i].enabled;
        for (int i = 0; i < renderers.Length; i++) if (renderers[i])
        { rendererEnabled[i] = renderers[i].enabled; rendererActive[i] = renderers[i].gameObject.activeInHierarchy; }
        if (trapRoot)
        { trapParent = trapRoot!.parent; trapPosition = trapRoot.position; trapRotation = trapRoot.rotation; trapScale = trapRoot.lossyScale; }
        anchored = true;
    }
    private static bool AuditedController(RuntimeAnimatorController? value)
    {
        if (!value || value!.name != "VenusFlyTrap") return false;
        var clips = value.animationClips;
        if (clips == null || clips.Length != 4) return false;
        int seen = 0;
        foreach (var clip in clips)
        {
            if (!clip || clip.events.Length != 0) return false;
            int bit; float length;
            switch (clip.name)
            {
                case "VenusFlyTrap_Idle": bit = 1; length = 1.5f; break;
                case "VenusFlyTrap_Bite": bit = 2; length = 2f/3; break;
                case "VenusFlyTrap_Biting": bit = 4; length = 5f/12; break;
                case "VenusFlyTrap_LetGo": bit = 8; length = 1f/3; break;
                default: return false;
            }
            if ((seen & bit) != 0 || Math.Abs(clip.length-length) > .0001f) return false;
            seen |= bit;
        }
        return seen == 15;
    }
    private static bool KnownScript(Type type, string kind)
    {
        string name = type.FullName ?? "";
        if (kind == "capybara") return name == "Capybara" || name == "MirageLuggage" || name == "WobbleSpinBounce";
        return name == "VenusFlyTrap" || name == "Photon.Pun.PhotonView" || name == "Peak.GenericOptimizer" ||
            name == "DisableBasedOnRunSettings" || name == "CollisionModifier";
    }
    private static bool SameRoot(Transform value, ObjectPose pose)
    {
        var p = value.position; var r = value.rotation; var s = value.lossyScale;
        return pose.Position.Length == 3 && pose.Rotation.Length == 4 && pose.Scale.Length == 3 &&
            Near(p.x,pose.Position[0]) && Near(p.y,pose.Position[1]) && Near(p.z,pose.Position[2]) &&
            Same(r,new Quaternion(pose.Rotation[0],pose.Rotation[1],pose.Rotation[2],pose.Rotation[3])) &&
            Near(s.x,pose.Scale[0]) && Near(s.y,pose.Scale[1]) && Near(s.z,pose.Scale[2]);
    }
    private static bool Same(Vector3 a, Vector3 b) => Near(a.x,b.x) && Near(a.y,b.y) && Near(a.z,b.z);
    private static bool Same(Quaternion a, Quaternion b)
    { double dot = (double)a.x*b.x+(double)a.y*b.y+(double)a.z*b.z+(double)a.w*b.w; return Finite(dot) && Math.Abs(dot) > .999999; }
    private static bool Near(float a, float b) => Finite(a) && Finite(b) && Math.Abs(a-b) <= .0001f;
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static AccessTools.FieldRef<VenusFlyTrap,T>? Resolve<T>(string name)
    { try { return AccessTools.FieldRefAccess<VenusFlyTrap,T>(name); } catch { return null; } }
}
