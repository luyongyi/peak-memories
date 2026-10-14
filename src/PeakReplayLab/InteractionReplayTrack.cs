using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

internal sealed class InteractionReplayResources
{
    internal sealed class Source
    {
        public string Kind = "", Resource = "", Path = "";
        public GameObject Root = null!;
        public Component? Owner;
        public LineRenderer? Line;
        public TextMeshPro? Text;
        public Volume[]? Posts;
    }
    private readonly Dictionary<string, Source> scenes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Source> templates = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Source> spawned = new();
    private static readonly string[] breakItems = { "Snowball", "Antidote", "Cure-All", "NestEgg", "NestEgg_Raven", "Item_Coconut", "Beehive", "ScoutmasterSoul" };
    public static bool IsWorld(Component owner) => owner is Peak.ScoutStatue || owner is GhostFire || owner is Peak.ScoutmasterSoulPillar || owner is Campfire || owner is Peak.PeakGatePortal;
    public static string Kind(Component owner) => owner is Peak.ScoutStatue ? "scout-statue" : owner is GhostFire ? "ghost-fire" : owner is Peak.ScoutmasterSoulPillar ? "soul-pillar" : owner is Campfire ? "campfire" : owner is Peak.PeakGatePortal ? "peak-portal" : owner is WarpCompassVFX ? "warp-compass" : owner is Peak.Action_CloneSelectedItem ? "clone-line" : owner is Action_AskBingBong ? "bing-subtitle" : owner is ArrowShooter ? "arrow-line" : "";
    public void LoadTemplates()
    {
        foreach (string name in breakItems)
            if (ItemDatabase.TryGetItem(name, out var item) && item && item.TryGetComponent<Breakable>(out var breakable)) AddBreakable(breakable);
        var rocket = Resources.Load<GameObject>("InertRocket");
        if (rocket) AddTemplate(new Source { Kind = "inert-rocket", Resource = "InertRocket", Root = rocket });
        foreach (var character in Resources.FindObjectsOfTypeAll<Character>())
        {
            if (!character || character.refs == null) continue;
            if (character.refs.turtleShell) AddTemplate(new Source { Kind = "turtle-shell", Resource = "turtle-shell/" + character.refs.turtleShell.name, Root = character.refs.turtleShell.gameObject });
            if (character.refs.infiniteStamSphere) AddTemplate(new Source { Kind = "stamina-sphere", Resource = "stamina-sphere/" + character.refs.infiniteStamSphere.name, Root = character.refs.infiniteStamSphere.gameObject });
        }
    }
    public void AddBreakable(Breakable owner)
    {
        if (!owner || !owner.TryGetComponent<Item>(out var item) || !ItemDatabase.TryGetItem(item.itemID, out var canonical) || !canonical) return;
        if (Array.IndexOf(breakItems, canonical.name) < 0 || !canonical.TryGetComponent<Breakable>(out var prefab) || prefab.instantiateNonItemOnBreak == null) return;
        for (int i = 0; i < Math.Min(32, prefab.instantiateNonItemOnBreak.Count); i++)
        {
            var root = prefab.instantiateNonItemOnBreak[i]; if (!root || root.GetComponent<Item>()) continue;
            AddTemplate(new Source { Kind = "break-debris", Resource = "break/" + canonical.itemID + "/" + i, Root = root });
        }
    }
    private void AddTemplate(Source source)
    { templates[source.Resource] = source; spawned[source.Root.GetInstanceID()] = source; }
    public Source? Spawn(Object original, Object instance)
    {
        var template = original is Component originalComponent ? originalComponent.gameObject : original as GameObject;
        var root = instance is Component instanceComponent ? instanceComponent.gameObject : instance as GameObject;
        if (!template || !root || !spawned.TryGetValue(template.GetInstanceID(), out var known)) return null;
        return new Source { Kind = known.Kind, Resource = known.Resource, Root = root };
    }
    public IEnumerable<Source> Initial()
    {
        foreach (Type type in new[] { typeof(Peak.ScoutStatue), typeof(GhostFire), typeof(Peak.ScoutmasterSoulPillar), typeof(Campfire), typeof(Peak.PeakGatePortal), typeof(WarpCompassVFX), typeof(Peak.Action_CloneSelectedItem), typeof(Action_AskBingBong), typeof(ArrowShooter) })
            foreach (var found in Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (found is Component owner)
                {
                    var source = FromOwner(owner); if (source != null) yield return source;
                    if (owner is ArrowShooter arrow && arrow.arrowPrefab)
                        for (int i = 0; i < arrow.transform.childCount; i++)
                        {
                            var child = arrow.transform.GetChild(i);
                            if (child.name.Replace("(Clone)", "").Trim() == arrow.arrowPrefab.name)
                            { var placed = Spawn(arrow.arrowPrefab, child.gameObject); if (placed != null) yield return placed; }
                        }
                }
        foreach (var shell in Object.FindObjectsByType<Peak.TurtleShell>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (shell && templates.TryGetValue("turtle-shell/" + shell.name.Replace("(Clone)", "").Trim(), out var shellSource))
                yield return new Source { Kind = shellSource.Kind, Resource = shellSource.Resource, Root = shell.gameObject };
        foreach (var sphere in Object.FindObjectsByType<Peak.InfiniteStamSphere>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (sphere && templates.TryGetValue("stamina-sphere/" + sphere.name.Replace("(Clone)", "").Trim(), out var sphereSource))
                yield return new Source { Kind = sphereSource.Kind, Resource = sphereSource.Resource, Root = sphere.gameObject };
    }
    public Source? FromOwner(Component owner)
    {
        if (!owner || !owner.gameObject.scene.IsValid()) return null;
        string kind = Kind(owner); if (kind.Length == 0) return null;
        if (owner is ArrowShooter arrow)
        {
            string path = WorldTrack.Path(owner.transform);
            if (arrow.arrowPrefab) AddTemplate(new Source { Kind = "warning-arrow", Resource = "arrow/" + path, Root = arrow.arrowPrefab });
            if (!arrow.lineRenderer) return null;
            var value = new Source { Kind = kind, Resource = "arrow-line", Path = path, Root = arrow.lineRenderer.gameObject, Owner = owner, Line = arrow.lineRenderer };
            scenes[kind + "\n" + path] = value; return value;
        }
        if (IsWorld(owner))
        {
            // Portable campfires already belong to their Item/placed visual tree.
            if (owner is Campfire && (owner.GetComponentInParent<Item>() || NativePlacementResources.Match(owner.gameObject) != null)) return null;
            string path = WorldTrack.Path(owner.transform);
            var source = new Source { Kind = kind, Resource = kind, Path = path, Root = owner.gameObject, Owner = owner };
            if (owner is Peak.PeakGatePortal portal) source.Posts = new[] { portal.warpPost, portal.warpPost2 };
            scenes[kind + "\n" + path] = source; return source;
        }
        var item = owner.GetComponentInParent<Item>();
        if (!item || !ItemDatabase.TryGetItem(item.itemID, out var prefab) || !prefab) return null;
        string resource = "item/" + prefab.itemID.ToString(CultureInfo.InvariantCulture);
        if (owner is WarpCompassVFX warp)
            return new Source { Kind = kind, Resource = resource, Root = owner.gameObject, Owner = owner, Posts = new[] { warp.warpPost, warp.warpPost2 } };
        if (owner is Peak.Action_CloneSelectedItem clone && clone.lr)
            return new Source { Kind = kind, Resource = resource, Root = clone.lr.gameObject, Owner = owner, Line = clone.lr };
        if (owner is Action_AskBingBong bing && bing.subtitles)
            return new Source { Kind = kind, Resource = resource, Root = bing.subtitles.gameObject, Owner = owner, Text = bing.subtitles };
        return null;
    }
    public Source? Resolve(InteractionReplayFrame frame)
    {
        if (InteractionReplayRules.World(frame.Kind) || frame.Kind == "arrow-line")
            return scenes.TryGetValue(frame.Kind + "\n" + frame.SourcePath, out var scene) && scene.Root ? scene : null;
        if (frame.Kind == "break-debris" || frame.Kind == "inert-rocket" || frame.Kind == "turtle-shell" || frame.Kind == "stamina-sphere" || frame.Kind == "warning-arrow")
            return templates.TryGetValue(frame.Resource, out var template) && template.Kind == frame.Kind && template.Root ? template : null;
        string key = frame.Kind + "\n" + frame.Resource;
        if (templates.TryGetValue(key, out var existing)) return existing;
        if (!frame.Resource.StartsWith("item/", StringComparison.Ordinal) || !ushort.TryParse(frame.Resource.Substring(5), NumberStyles.None, CultureInfo.InvariantCulture, out ushort id) || !ItemDatabase.TryGetItem(id, out var item) || !item) return null;
        Source? source = null;
        if (frame.Kind == "clone-line")
        {
            var owner = item.GetComponentInChildren<Peak.Action_CloneSelectedItem>(true);
            if (owner && owner.lr) source = new Source { Kind = frame.Kind, Resource = frame.Resource, Root = owner.lr.gameObject, Line = owner.lr, Owner = owner };
        }
        else if (frame.Kind == "bing-subtitle")
        {
            var owner = item.GetComponentInChildren<Action_AskBingBong>(true);
            if (owner && owner.subtitles) source = new Source { Kind = frame.Kind, Resource = frame.Resource, Root = owner.subtitles.gameObject, Text = owner.subtitles, Owner = owner };
        }
        else if (frame.Kind == "warp-compass")
        {
            var owner = item.GetComponentInChildren<WarpCompassVFX>(true);
            if (owner) source = new Source { Kind = frame.Kind, Resource = frame.Resource, Root = owner.gameObject, Owner = owner, Posts = new[] { owner.warpPost, owner.warpPost2 } };
        }
        if (source != null) templates[key] = source;
        return source;
    }
}

// Each patch observes an already executed native action. The only Instantiate
// wrapper belongs to the native caller; presentation never invokes this code.
internal sealed class InteractionReplayObserver : IDisposable
{
    private static readonly List<InteractionReplayObserver> observers = new();
    private static Harmony? harmony;
    private static readonly HashSet<Type> unavailable = new();
    private readonly Action<Component, bool> changed;
    private readonly ConditionalWeakTable<Peak.ScoutmasterSoulPillar, SoulPillarCaptureEventPolicy> soulPillars = new();
    private sealed class LanternState { internal bool Known, Lit; }
    private readonly ConditionalWeakTable<Lantern, LanternState> lanterns = new();
    private static readonly Func<Lantern, bool>? lanternLit = ResolveLanternLit();
    private static Func<Lantern, bool>? ResolveLanternLit()
    { try { return AccessTools.MethodDelegate<Func<Lantern, bool>>(AccessTools.PropertyGetter(typeof(Lantern), "lit")); } catch { return null; } }
    private readonly Func<ArrowShooter, bool>? arrowLineUnchanged;
    private readonly Action<Object, Object> spawned;
    private bool disposed;
    public InteractionReplayObserver(Action<Component> changed, Action<Object, Object> spawned, Action<string>? warning)
        : this((owner, _) => changed(owner), spawned, warning) { }
    public InteractionReplayObserver(Action<Component, bool> changed, Action<Object, Object> spawned, Action<string>? warning,
        Func<ArrowShooter, bool>? arrowLineUnchanged = null)
    {
        this.changed = changed; this.spawned = spawned; this.arrowLineUnchanged = arrowLineUnchanged;
        observers.Add(this); if (observers.Count > 1) return;
        unavailable.Clear();
        harmony = new Harmony("cn.mylus.peakreplaylab.interaction-observation");
        void Patch(Type type, string name, bool prefix = false, bool action = true, string? callbackName = null)
        {
            try
            {
                var target = AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);
                var callback = new HarmonyMethod(typeof(InteractionReplayObserver), callbackName ?? (action ? nameof(Changed) : nameof(StateChanged)));
                harmony!.Patch(target, prefix: prefix ? callback : null, postfix: prefix ? null : callback);
            }
            catch (Exception e) { unavailable.Add(type); warning?.Invoke("Interaction observer unavailable: " + type.Name + "." + name + " · " + e.GetType().Name); }
        }
        Patch(typeof(Peak.ScoutStatue), "Awake", action: false); Patch(typeof(Peak.ScoutStatue), "OnAmuletsChanged", action: false); Patch(typeof(Peak.ScoutStatue), "RPC_InsertAmulet");
        Patch(typeof(GhostFire), "Start", action: false); Patch(typeof(GhostFire), "SetLit", action: false); Patch(typeof(GhostFire), "RPC_SetLit");
        // These are native visual setters, not ongoing behavior. Observe their
        // resulting state without renewing a whole-object action window.
        Patch(typeof(SetMaterialProperty), "SetVal", action: false);
        Patch(typeof(ShaderEffect), "SetEffect", action: false);
        Patch(typeof(ShaderEffect), "ClearEffect", action: false);
        Patch(typeof(Lantern), "LightLantern", action: false, callbackName: nameof(LanternChanged));
        Patch(typeof(Lantern), "OnInstanceDataSet", action: false, callbackName: nameof(LanternChanged));
        Patch(typeof(Lantern), "OnPhotonSerializeView", action: false, callbackName: nameof(LanternChanged));
        if (lanternLit == null) unavailable.Add(typeof(Lantern));
        Patch(typeof(Action_Torch), "RunAction", action: false);
        Patch(typeof(LanternLight), "Start", action: false);
        // LightCuller changes Light values inside its private native coroutine.
        // Intercept those assignments at their source, never global Light APIs.
        try
        {
            bool found = false;
            foreach (var type in typeof(LightCuller).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                if (type.Name.IndexOf("LightCullRoutine", StringComparison.Ordinal) >= 0 && AccessTools.DeclaredMethod(type, "MoveNext") is MethodInfo method)
                { harmony.Patch(method, transpiler: new HarmonyMethod(typeof(InteractionReplayObserver), nameof(LightWrites))); found = true; }
            if (!found) throw new MissingMethodException(typeof(LightCuller).FullName, "LightCullRoutine.MoveNext");
        }
        catch (Exception e) { unavailable.Add(typeof(LightCuller)); warning?.Invoke("Interaction light observer unavailable: " + e.GetType().Name); }
        Patch(typeof(Peak.ScoutmasterSoulPillar), "SetBroken", action: false, callbackName: nameof(SoulBrokenChanged));
        try
        {
            var method = AccessTools.DeclaredMethod(typeof(Peak.ScoutmasterSoulPillar), "RPC_Break") ??
                throw new MissingMethodException(typeof(Peak.ScoutmasterSoulPillar).FullName, "RPC_Break");
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(InteractionReplayObserver), nameof(SoulBreakBefore)),
                postfix: new HarmonyMethod(typeof(InteractionReplayObserver), nameof(SoulBreakAfter)));
        }
        catch (Exception e)
        {
            unavailable.Add(typeof(Peak.ScoutmasterSoulPillar));
            warning?.Invoke("Interaction observer unavailable: ScoutmasterSoulPillar.RPC_Break · " + e.GetType().Name);
        }
        Patch(typeof(Peak.Action_CloneSelectedItem), "RPCA_Shoot"); Patch(typeof(Action_AskBingBong), "Ask");
        Patch(typeof(Item), "OnEnable", action: false); Patch(typeof(Breakable), "RPC_BroadcastBreak", true);
        Patch(typeof(Campfire), "UpdateLit", action: false); Patch(typeof(Campfire), "HideLogs", action: false); Patch(typeof(WarpCompassVFX), "Start", action: false);
        try { harmony.Patch(AccessTools.DeclaredMethod(typeof(WarpCompassVFX), "OnUpdatedFeedData"),
            prefix: new HarmonyMethod(typeof(InteractionReplayObserver), nameof(WarpFeedBefore)),
            postfix: new HarmonyMethod(typeof(InteractionReplayObserver), nameof(WarpFeedAfter))); }
        catch (Exception e) { unavailable.Add(typeof(WarpCompassVFX)); warning?.Invoke("Interaction feed observer unavailable: " + e.GetType().Name); }
        foreach (string method in new[] { "StartUsePrimary", "FinishCastPrimary", "CancelUsePrimary" })
        {
            try { harmony.Patch(AccessTools.DeclaredMethod(typeof(Item), method),
                prefix: new HarmonyMethod(typeof(InteractionReplayObserver), nameof(ItemCastBefore)),
                postfix: new HarmonyMethod(typeof(InteractionReplayObserver), nameof(ItemCastAfter))); }
            catch (Exception e) { unavailable.Add(typeof(WarpCompassVFX)); warning?.Invoke("Interaction cast observer unavailable: Item." + method + " · " + e.GetType().Name); }
        }
        foreach (string method in new[] { "Interact", "CancelCast" })
        {
            try { harmony.Patch(AccessTools.DeclaredMethod(typeof(Peak.PeakGatePortal), method),
                prefix: new HarmonyMethod(typeof(InteractionReplayObserver), nameof(PortalBefore)),
                postfix: new HarmonyMethod(typeof(InteractionReplayObserver), nameof(PortalAfter))); }
            catch (Exception e) { unavailable.Add(typeof(Peak.PeakGatePortal)); warning?.Invoke("Interaction portal observer unavailable: " + method + " · " + e.GetType().Name); }
        }
        Patch(typeof(Peak.TurtleShell), "Initialize"); Patch(typeof(Peak.InfiniteStamSphere), "Initialize");
        Patch(typeof(ArrowShooter), "Awake", true, false); Patch(typeof(ArrowShooter), "Awake", action: false); Patch(typeof(ArrowShooter), "DrawWarningLine", action: false);
        try
        {
            harmony.Patch(AccessTools.DeclaredMethod(typeof(ArrowShooter), "UpdateLine"), postfix: new HarmonyMethod(typeof(InteractionReplayObserver), nameof(ArrowLineChanged)));
        }
        catch (Exception e) { unavailable.Add(typeof(ArrowShooter)); warning?.Invoke("Interaction observer unavailable: ArrowShooter.UpdateLine · " + e.GetType().Name); }
        try
        {
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Breakable), "RPC_BroadcastBreak"), transpiler: new HarmonyMethod(typeof(InteractionReplayObserver), nameof(Spawns)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(ArrowShooter), "SpawnWarningArrow"), transpiler: new HarmonyMethod(typeof(InteractionReplayObserver), nameof(Spawns)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Peak.Afflictions.Affliction_Invincibility), "PlayVFX"), transpiler: new HarmonyMethod(typeof(InteractionReplayObserver), nameof(Spawns)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Peak.Afflictions.Affliction_RadiateInfiniteStam), "OnApplied"), transpiler: new HarmonyMethod(typeof(InteractionReplayObserver), nameof(Spawns)));
            bool found = false;
            foreach (var type in typeof(CharacterMovement).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                if (type.Name.IndexOf("RocketRoutine", StringComparison.Ordinal) >= 0 && AccessTools.DeclaredMethod(type, "MoveNext") is MethodInfo method)
                { harmony.Patch(method, transpiler: new HarmonyMethod(typeof(InteractionReplayObserver), nameof(Spawns))); found = true; }
            if (!found) warning?.Invoke("InertRocket native coroutine was not found; its birth observer is unavailable.");
        }
        catch (Exception e)
        {
            unavailable.Add(typeof(Breakable)); unavailable.Add(typeof(ArrowShooter));
            unavailable.Add(typeof(Peak.TurtleShell)); unavailable.Add(typeof(Peak.InfiniteStamSphere));
            warning?.Invoke("Interaction spawn observer unavailable: " + e.GetType().Name);
        }
    }
    internal bool Healthy(string kind)
    {
        Type? type = kind switch
        {
            "scout-statue" => typeof(Peak.ScoutStatue), "ghost-fire" => typeof(GhostFire), "soul-pillar" => typeof(Peak.ScoutmasterSoulPillar),
            "campfire" => typeof(Campfire), "peak-portal" => typeof(Peak.PeakGatePortal), "warp-compass" => typeof(WarpCompassVFX),
            "clone-line" => typeof(Peak.Action_CloneSelectedItem), "bing-subtitle" => typeof(Action_AskBingBong),
            "arrow-line" or "warning-arrow" => typeof(ArrowShooter), "turtle-shell" => typeof(Peak.TurtleShell),
            "stamina-sphere" => typeof(Peak.InfiniteStamSphere), "break-debris" => typeof(Breakable), _ => null,
        };
        return type != null && !unavailable.Contains(type);
    }
    internal bool VisualHooksHealthy(MonoBehaviour component)
    {
        Type? type = component is SetMaterialProperty ? typeof(SetMaterialProperty) : component is ShaderEffect ? typeof(ShaderEffect) :
            component is Lantern ? typeof(Lantern) : component is Action_Torch ? typeof(Action_Torch) :
            component is LanternLight ? typeof(LanternLight) : component is LightCuller ? typeof(LightCuller) : null;
        return type == null || !unavailable.Contains(type);
    }
    internal static bool LanternIsLit(Lantern source) => lanternLit != null && lanternLit(source);
    private static void Changed(Component __instance)
    {
        if (!__instance) return;
        foreach (var observer in observers) try { observer.changed(__instance, true); } catch { }
    }
    private static void StateChanged(Component __instance)
    {
        if (!__instance) return;
        foreach (var observer in observers) try { observer.changed(__instance, false); } catch { }
    }
    private static void LanternChanged(Lantern __instance)
    {
        if (!__instance || lanternLit == null) return;
        bool lit = lanternLit(__instance);
        foreach (var observer in observers) try
        {
            var state = observer.lanterns.GetValue(__instance, static _ => new LanternState());
            if (state.Known && state.Lit == lit) continue;
            state.Known = true; state.Lit = lit; observer.changed(__instance, false);
        }
        catch { }
    }
    private static void WarpFeedBefore(WarpCompassVFX __instance, out float __state) => __state = __instance.timeStartedBeingUsedOnMe;
    private static void WarpFeedAfter(WarpCompassVFX __instance, float __state)
    { if (__state != __instance.timeStartedBeingUsedOnMe) StateChanged(__instance); }
    private readonly struct SoulBreakState
    {
        internal readonly bool Broken, Interacting;
        internal SoulBreakState(bool broken, bool interacting) { Broken = broken; Interacting = interacting; }
    }
    private static readonly int soulInteracting = Animator.StringToHash("Interacting");
    private static void SoulBrokenChanged(Peak.ScoutmasterSoulPillar __instance, bool ____broken)
    { NotifySoul(__instance, ____broken, false); }
    private static void SoulBreakBefore(Peak.ScoutmasterSoulPillar __instance, int type, bool ____broken,
        int ___charactersTryingToBreak, out SoulBreakState __state)
    {
        // One known Animator bool is read only during a native charge/cancel
        // callback. Its actual state also handles unbalanced native counters.
        bool interacting = ___charactersTryingToBreak > 0;
        try { if (type is 1 or 2 && __instance && __instance.sphereAnimator) interacting = __instance.sphereAnimator.GetBool(soulInteracting); }
        catch { }
        __state = new SoulBreakState(____broken, interacting);
    }
    private static void SoulBreakAfter(Peak.ScoutmasterSoulPillar __instance, int type, bool ____broken,
        int ___charactersTryingToBreak, SoulBreakState __state)
    {
        // SetBroken owns the actual false->true coroutine edge for both RPC
        // and late network synchronization; its nested RPC must not renew it.
        bool action = type != 0 && SoulPillarCaptureEventPolicy.ActionChanged(type, __state.Broken, ____broken, __state.Interacting, ___charactersTryingToBreak);
        NotifySoul(__instance, ____broken, action);
    }
    private static void NotifySoul(Peak.ScoutmasterSoulPillar owner, bool broken, bool action)
    {
        if (!owner) return;
        foreach (var observer in observers) try
        {
            bool stateChanged = observer.soulPillars.GetValue(owner, static _ => new SoulPillarCaptureEventPolicy()).ObserveBroken(broken);
            if (action || stateChanged) observer.changed(owner, action || stateChanged && broken);
        }
        catch { }
    }
    private static void PortalBefore(bool ___interacting, out bool __state) => __state = ___interacting;
    private static void PortalAfter(Peak.PeakGatePortal __instance, bool ___interacting, bool __state)
    { if (__state != ___interacting) Changed(__instance); }
    private readonly struct ItemCastState
    {
        internal readonly bool Using, Finished;
        internal readonly float Progress;
        internal ItemCastState(Item item) { Using = item.isUsingPrimary; Finished = item.finishedCast; Progress = item.castProgress; }
        internal bool Same(Item item) => Using == item.isUsingPrimary && Finished == item.finishedCast && Progress == item.castProgress;
    }
    private static void ItemCastBefore(Item __instance, out ItemCastState __state) => __state = new ItemCastState(__instance);
    private static void ItemCastAfter(Item __instance, ItemCastState __state)
    { if (!__state.Same(__instance)) StateChanged(__instance); }
    private static IEnumerable<CodeInstruction> LightWrites(IEnumerable<CodeInstruction> instructions)
    {
        int writes = 0;
        foreach (var code in instructions)
        {
            if (code.operand is MethodInfo method)
            {
                string? replacement = method.DeclaringType == typeof(Light) && method.Name == "set_range" ? nameof(SetLightRange) :
                    method.DeclaringType == typeof(Behaviour) && method.Name == "set_enabled" ? nameof(SetLightEnabled) : null;
                if (replacement != null)
                {
                    writes |= replacement == nameof(SetLightRange) ? 1 : 2;
                    yield return new CodeInstruction(code) { opcode = OpCodes.Call, operand = AccessTools.DeclaredMethod(typeof(InteractionReplayObserver), replacement) }; continue;
                }
            }
            yield return code;
        }
        if (writes != 3) throw new InvalidOperationException("Native LightCuller setter layout changed.");
    }
    private static void SetLightRange(Light source, float range) { source.range = range; StateChanged(source); }
    private static void SetLightEnabled(Behaviour source, bool enabled) { source.enabled = enabled; StateChanged(source); }
    private static void ArrowLineChanged(ArrowShooter __instance)
    {
        if (!__instance || !__instance.lineRenderer) return;
        foreach (var observer in observers) try
        {
            // The network repeatedly assigns the same ammo count. The native
            // setter calls UpdateLine even when it leaves enabled unchanged.
            if (observer.arrowLineUnchanged?.Invoke(__instance) == true) continue;
            observer.changed(__instance, false);
        }
        catch { }
    }
    private static IEnumerable<CodeInstruction> Spawns(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var code in instructions)
        {
            if (code.operand is MethodInfo method && method.DeclaringType == typeof(Object) && method.Name == "Instantiate" && method.IsGenericMethod)
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 3 && parameters[1].ParameterType == typeof(Vector3) && parameters[2].ParameterType == typeof(Quaternion))
                {
                    var replacement = new CodeInstruction(code) { opcode = OpCodes.Call, operand = AccessTools.DeclaredMethod(typeof(InteractionReplayObserver), nameof(Instantiate))!.MakeGenericMethod(method.GetGenericArguments()) };
                    yield return replacement; continue;
                }
            }
            yield return code;
        }
    }
    private static T Instantiate<T>(T original, Vector3 position, Quaternion rotation) where T : Object
    {
        T result = Object.Instantiate(original, position, rotation);
        foreach (var observer in observers) try { observer.spawned(original, result); } catch { }
        try { EffectReplayCapture.ObserveNativeSpawn(original, result); } catch { }
        return result;
    }
    public void Dispose()
    { if (disposed) return; disposed = true; observers.Remove(this); if (observers.Count == 0) { harmony?.UnpatchSelf(); harmony = null; } }
}

internal sealed class InteractionReplayCapture : IDisposable
{
    private static InteractionReplayCapture? current;
    // Particle birth observation supplies verified component-prefab references
    // for the two native affliction meshes; no global Object.Instantiate patch.
    internal static void ObserveNativeSpawn(Object original, Object result) => current?.Spawned(original, result);
    private sealed class LightDriver
    {
        internal MonoBehaviour Source = null!;
        internal Light? Light;
        internal bool Varying;
    }
    private sealed class Entry
    {
        public InteractionReplayResources.Source Source = null!;
        public Transform Root = null!;
        public string Key = "";
        public InteractionReplayFrame? Last;
        public Rigidbody? Body;
        public Rigidbody[] Bodies = Array.Empty<Rigidbody>();
        public Animator[] Animators = Array.Empty<Animator>();
        public AnimationCurve? LineWidth;
        public InteractionCaptureLifecycle Lifecycle = null!;
        public NativeCaptureAnimationActivity? AnimationProgress;
        public IDisposable? RootLifecycle;
        public double MotionUntil = double.NegativeInfinity, SubtitleUntil = double.NegativeInfinity;
        public InteractionContinuousState Continuous;
        public bool DynamicRoot, HasRootObservation;
        public Vector3 RootPosition, RootScale;
        public Quaternion RootRotation;
        public LightDriver[] LightDrivers = Array.Empty<LightDriver>();
        public readonly InteractionNativeFadeReadWindow LightReads = new();
        public bool MissingObserver;
        public double NextIndependent = double.NegativeInfinity;
        public bool Dirty = true, AppearanceDirty, LightDirty, Gloom, Active;
        public bool InitialUrgent;
        public Item? PostItem;
        public bool PostCasting;
        public readonly InteractionNativePostReadWindow PostReads = new();
        public float GloomValue;
        public float[]? PostWeights;
        public bool[]? PostEnabled;
    }
    private readonly Dictionary<int, Entry> entries = new();
    // Deferred/retired candidates retain only their native binding descriptor.
    // Rigs, geometry, appearance and the last frame live exclusively in entries.
    private readonly Dictionary<int, InteractionReplayResources.Source> candidates = new();
    private readonly List<int> dead = new();
    private readonly List<InteractionReplayFrame> frames = new();
    private readonly InteractionReplayResources resources = new();
    private readonly InteractionReplayObserver observer;
    private readonly SegmentCaptureClient? segments;
    private readonly Action<string>? warning;
    private readonly HashSet<string> warned = new(StringComparer.Ordinal);
    private InteractionReplayFrame[] previous = Array.Empty<InteractionReplayFrame>();
    private readonly int scene = SceneManager.GetActiveScene().handle;
    private long serial;
    private bool disposed, gloomAssigned;
    private readonly InteractionNativeFadeReadWindow gloomReads = new();
    private static readonly AccessTools.FieldRef<Peak.PeakGatePortal, bool>? portalInteracting = ResolvePortalInteracting();
    private static AccessTools.FieldRef<Peak.PeakGatePortal, bool>? ResolvePortalInteracting()
    { try { return AccessTools.FieldRefAccess<Peak.PeakGatePortal, bool>("interacting"); } catch { return null; } }
    public long ProbeCount { get; private set; }
    public long SharedSkipCount { get; private set; }
    public long RootReadCount { get; private set; }
    public long GeometryReadCount { get; private set; }
    public long AnimationReadCount { get; private set; }
    public long MotionReadCount { get; private set; }
    public long AppearanceReadCount { get; private set; }
    public long LightReadCount { get; private set; }
    public long PostReadCount { get; private set; }
    public long GloomReadCount { get; private set; }
    public long EventWakeCount { get; private set; }
    public long SleepingSkipCount { get; private set; }
    public long UnknownFallbackReadCount { get; private set; }
    public long EventBurstReadCount { get; private set; }
    public long ContinuousReadCount { get; private set; }
    internal bool InitialBaselineReady
    { get { foreach (var entry in entries.Values) if (entry.Source.Root && entry.Last == null) return false; return true; } }
    public InteractionReplayCapture(Action<string>? warning = null)
    {
        this.warning = warning; resources.LoadTemplates(); current = this;
        segments = CaptureSegmentScope.Active?.CreateClient(Admitted, Retire);
        try { observer = new InteractionReplayObserver(Changed, Spawned, warning, ArrowLineUnchanged); }
        catch { segments?.Dispose(); if (ReferenceEquals(current, this)) current = null; throw; }
        try { foreach (var source in resources.Initial()) Register(source, false); }
        catch { Dispose(); throw; }
    }
    private void Register(InteractionReplayResources.Source source, bool action = true)
    {
        if (disposed || ReplaySafety.Active || !source.Root || source.Root.scene.handle != scene) return;
        int id = source.Root.GetInstanceID();
        candidates[id] = source;
        if (!(segments?.Admit(source.Root, action) ?? true)) return;
        if (entries.TryGetValue(id, out var old) && old.Source.Root == source.Root)
        {
            old.Dirty = true;
            if (action) WakeAction(old, Time.timeAsDouble);
            else { old.Lifecycle.StateChanged(); old.AnimationProgress?.Wake(Time.timeAsDouble); }
            UpdatePostActivity(old, Time.timeAsDouble);
            if (source.Line) old.LineWidth = source.Line!.widthCurve;
            return;
        }
        if (entries.Count >= InteractionReplayRules.MaximumEntities) { Warn("limit", "互动实体目录达到768个上限，超出部分未录制。"); return; }
        bool gloom = !gloomAssigned && source.Kind == "ghost-fire"; gloomAssigned |= gloom;
        var components = source.Root.GetComponentsInChildren<MonoBehaviour>(true);
        bool missingObserver = !observer.Healthy(source.Kind) || source.Kind == "peak-portal" && portalInteracting == null;
        foreach (var component in components) if (component && !observer.VisualHooksHealthy(component)) missingObserver = true;
        var entry = new Entry { Source = source, Root = source.Root.transform, Key = "interaction:" + id + ":" + (++serial), Body = source.Root.GetComponent<Rigidbody>(),
            Lifecycle = new InteractionCaptureLifecycle(id, source.Kind == "break-debris" || source.Kind == "inert-rocket" || missingObserver, followSceneClock: true),
            Bodies = source.Root.GetComponentsInChildren<Rigidbody>(true), Animators = source.Root.GetComponentsInChildren<Animator>(true),
            LineWidth = source.Line ? source.Line!.widthCurve : null, Gloom = gloom, Active = source.Root.activeInHierarchy,
            PostItem = source.Owner is WarpCompassVFX ? source.Root.GetComponent<Item>() : null };
        entry.MissingObserver = missingObserver;
        var lightDrivers = new List<LightDriver>();
        foreach (var component in components)
        {
            Light? light; AnimationCurve? curve; float speed, amount;
            if (component is LanternLight flicker) { light = flicker.light; curve = flicker.flickerCurve; speed = flicker.flickerSpeed; amount = flicker.flickerAmount; }
            else if (component is Action_Torch torch) { light = torch.torchLight; curve = torch.lightCurve; speed = torch.lightSpeed; amount = torch.lightIntensity; }
            else if (component is Lantern lantern) { light = lantern.lanternLight; curve = lantern.lightCurve; speed = lantern.lightSpeed; amount = lantern.lightIntensity; }
            else continue;
            lightDrivers.Add(new LightDriver { Source = component, Light = light, Varying = InteractionLightDriverPolicy.Varying(CurveVaries(curve), speed, amount) });
        }
        entry.LightDrivers = lightDrivers.ToArray();
        bool nativeControllers = !missingObserver;
        foreach (var animator in entry.Animators)
            nativeControllers &= source.Owner is GhostFire fire && animator == fire.anim ||
                source.Owner is Peak.ScoutmasterSoulPillar pillar && (animator == pillar.sphereAnimator || animator == pillar.scoutmasterAnimator);
        entry.AnimationProgress = entry.Animators.Length == 0 ? null : new NativeCaptureAnimationActivity(entry.Animators, eventDriven: nativeControllers);
        entry.AnimationProgress?.Wake(Time.timeAsDouble);
        entry.DynamicRoot = !InteractionReplayResources.IsWorld(source.Owner!) && (entry.Bodies.Length != 0 || source.Kind == "turtle-shell" || source.Kind == "stamina-sphere" || source.Kind == "inert-rocket" || source.Kind == "break-debris");
        entries[id] = entry;
        entry.RootLifecycle = CaptureRootLifecycleObserver.Bind(source.Root, () =>
        {
            if (disposed) return;
            entry.Active = source.Root && source.Root.activeInHierarchy;
            entry.Dirty = true; entry.Lifecycle.StateChanged(); entry.AnimationProgress?.Wake(Time.timeAsDouble);
            UpdatePostActivity(entry, Time.timeAsDouble);
        });
        UpdatePostActivity(entry, Time.timeAsDouble);
        if (action) WakeAction(entry, Time.timeAsDouble);
    }
    private static void UpdatePostActivity(Entry entry, double now)
    {
        if (entry.Source.Posts == null) return;
        entry.PostCasting = entry.Source.Owner is WarpCompassVFX warp ? warp.isActiveAndEnabled &&
            (warp.timeStartedBeingUsedOnMe > 0 || entry.PostItem && entry.PostItem!.isUsingPrimary && !entry.PostItem.finishedCast) :
            entry.Source.Owner is Peak.PeakGatePortal portal && portal.isActiveAndEnabled && portalInteracting != null && portalInteracting(portal);
        entry.PostReads.StateChanged(now);
    }
    private void Admitted(GameObject source)
    {
        if (source && candidates.TryGetValue(source.GetInstanceID(), out var candidate)) Register(candidate, false);
    }
    private void Retire(int id)
    {
        if (candidates.TryGetValue(id, out var candidate) && !candidate.Root) candidates.Remove(id);
        if (!entries.TryGetValue(id, out var entry)) return;
        entry.Lifecycle.Retire();
        entry.RootLifecycle?.Dispose();
        if (entry.Gloom) { gloomAssigned = false; gloomReads.StateChanged(); }
        VisualReplica.ReleaseCapture(entry.Root);
        entries.Remove(id); previous = Array.Empty<InteractionReplayFrame>();
    }
    private void Changed(Component owner, bool action)
    {
        if (disposed || ReplaySafety.Active || !owner) return;
        if (owner is Breakable breakable) { resources.AddBreakable(breakable); return; }
        if (owner is Item item)
        {
            foreach (var clone in item.GetComponentsInChildren<Peak.Action_CloneSelectedItem>(true)) Changed(clone, action);
            foreach (var bing in item.GetComponentsInChildren<Action_AskBingBong>(true)) Changed(bing, action);
            foreach (var warp in item.GetComponentsInChildren<WarpCompassVFX>(true)) Changed(warp, action);
            return;
        }
        // Initialize follows the intercepted native Instantiate. Affliction
        // instances have no Item owner, so wake their already registered slot.
        if ((owner is Peak.TurtleShell || owner is Peak.InfiniteStamSphere) && entries.TryGetValue(owner.gameObject.GetInstanceID(), out var attached))
        { WakeAction(attached, Time.timeAsDouble); return; }
        var source = resources.FromOwner(owner); if (source != null) Register(source, action);
        else if (owner is SetMaterialProperty or ShaderEffect or Lantern or Action_Torch or LanternLight or LightCuller or Light)
        {
            // Setters are sparse; find their admitted visual root only on that
            // actual callback. No periodic component or hierarchy discovery.
            for (var parent = owner.transform; parent; parent = parent.parent)
                if (entries.TryGetValue(parent.gameObject.GetInstanceID(), out var visual))
                {
                    if (owner is SetMaterialProperty or ShaderEffect) visual.AppearanceDirty = true;
                    else
                    {
                        visual.LightDirty = true;
                        // Lantern.Update starts its one-second current intensity
                        // tween on the lit edge, even with a constant curve.
                        if (owner is Lantern lantern && InteractionReplayObserver.LanternIsLit(lantern)) visual.LightReads.NativeEvent(Time.timeAsDouble);
                    }
                    visual.InitialUrgent = true;
                    break;
                }
        }
        if (owner is GhostFire)
        {
            if (action) gloomReads.NativeEvent(Time.timeAsDouble); else gloomReads.StateChanged();
            foreach (var entry in entries.Values) if (entry.Gloom) { entry.Dirty = true; entry.Lifecycle.StateChanged(); }
        }
    }
    internal bool ArrowLineUnchanged(ArrowShooter owner)
    {
        var line = owner.lineRenderer;
        return entries.TryGetValue(line.gameObject.GetInstanceID(), out var entry) && entry.Last?.Line is InteractionLineFrame old &&
            old.Visible == (line.enabled && line.gameObject.activeInHierarchy && !line.forceRenderingOff);
    }
    private void WakeAction(Entry entry, double now)
    {
        double seconds = entry.Source.Kind is "clone-line" or "stamina-sphere" ? .65 : entry.Source.Kind == "warning-arrow" ? 0 :
            entry.Source.Kind == "turtle-shell" ? 2.1 : entry.Source.Kind == "ghost-fire" ? 2 : 8;
        // Native subtitle timeline: AskRoutine .5 + .5, SubtitleRoutine .19
        // then 1.8 / pitch. Its explicit lifetime can outlast the finite burst;
        // visibility alone never starts or renews that lifetime.
        if (entry.Source.Owner is Action_AskBingBong bing && bing.source)
            entry.SubtitleUntil = now + 1.3 + 1.8 / Math.Max(.001, Math.Abs(bing.source.pitch));
        entry.Dirty = true; entry.InitialUrgent = true; entry.Lifecycle.NativeEvent(now, seconds);
        entry.AnimationProgress?.Wake(now, seconds); EventWakeCount++;
    }
    private void Spawned(Object original, Object instance)
    { if (!disposed && !ReplaySafety.Active) { var source = resources.Spawn(original, instance); if (source != null) Register(source); } }
    public InteractionReplayFrame[] Capture(double now)
    {
        if (disposed) throw new ObjectDisposedException(nameof(InteractionReplayCapture));
        frames.Clear(); dead.Clear();
        foreach (var pair in entries)
        {
            var entry = pair.Value; var source = entry.Source;
            if (!source.Root) { entry.Lifecycle.Retire(); if (entry.Gloom) { gloomAssigned = false; gloomReads.StateChanged(); } dead.Add(pair.Key); continue; }
            if (CaptureBootstrapWorkBudget.InitialOnly && entry.Last != null && !entry.InitialUrgent && !entry.Dirty &&
                !entry.Lifecycle.EventActive(now))
            { frames.Add(entry.Last); continue; }
            if (entry.Last == null && !entry.InitialUrgent && !CaptureBootstrapWorkBudget.TryBeginRead()) continue;
            entry.InitialUrgent = false;
            // Capture is called only on admitted scene ticks. Fixed 30 Hz
            // fields share that clock rather than quantizing it a second time.
            if (source.Posts != null && (entry.Dirty || entry.PostReads.ShouldRead(now, entry.Active && entry.PostCasting) ||
                entry.MissingObserver && now + 1e-8 >= entry.Lifecycle.NextProbe))
            {
                PostReadCount++;
                var a = source.Posts[0]; var b = source.Posts[1];
                bool changed = a && b && (entry.PostWeights == null || entry.PostWeights[0] != a.weight || entry.PostWeights[1] != b.weight ||
                    entry.PostEnabled![0] != (a.enabled && a.gameObject.activeInHierarchy) || entry.PostEnabled[1] != (b.enabled && b.gameObject.activeInHierarchy));
                if (changed && a && b)
                {
                    entry.PostWeights = new[] { a.weight, b.weight }; entry.PostEnabled = new[] { a.enabled && a.gameObject.activeInHierarchy, b.enabled && b.gameObject.activeInHierarchy };
                    if (entry.Last != null) entry.Last = CopyState(entry.Last, entry.Last.GloomAmount, entry.PostWeights, entry.PostEnabled);
                }
                entry.PostReads.Observed(now, changed);
            }
            // The global native fade is independent of the chosen zone's
            // active tree. It runs only during the observed one-second action
            // and one final read, rather than every tick of the whole level.
            if (entry.Gloom && gloomReads.ShouldRead(now))
            {
                entry.GloomValue = Shader.GetGlobalFloat("GloomSafeZoneAmount");
                GloomReadCount++;
                gloomReads.Observed(now);
                if (entry.Last != null && entry.Last.GloomAmount != entry.GloomValue)
                {
                    var old = entry.Last;
                    entry.Last = CopyState(old, entry.GloomValue, old.PostWeights, old.PostEnabled);
                }
            }
            bool active = entry.Active;
            InteractionContinuousState continuous;
            try { continuous = ReadContinuous(entry, now, active); }
            catch (Exception e)
            {
                entry.Lifecycle.Failed(now); entry.Dirty = false;
                if (entry.Last != null) frames.Add(entry.Last);
                Warn(source.Kind, "互动活动状态保留上一有效状态：" + source.Kind + " · " + e.Message); continue;
            }
            if (!entry.Lifecycle.ShouldProbe(now, entry.Last != null, active, continuous))
            {
                try { ReadIndependent(entry, now, active); }
                catch (Exception e) { entry.NextIndependent = now + 1; Warn(source.Kind + ":appearance", "互动外观保留上一有效状态：" + source.Kind + " · " + e.Message); }
                if (entry.Last != null) frames.Add(entry.Last); SharedSkipCount++;
                if (!entry.Lifecycle.EventActive(now) && continuous == InteractionContinuousState.None) SleepingSkipCount++;
                continue;
            }
            try
            {
                var value = Sample(entry, now); ProbeCount++;
                if (entry.Lifecycle.LastProbeWasFallback) UnknownFallbackReadCount++;
                if (entry.Lifecycle.EventActive(now)) EventBurstReadCount++;
                if (continuous != InteractionContinuousState.None) ContinuousReadCount++;
                entry.Last = value; entry.Dirty = false;
                entry.Lifecycle.Observed(now, active, continuous);
                frames.Add(value);
            }
            catch (Exception e)
            { entry.Lifecycle.Failed(now); entry.Dirty = false; if (entry.Last != null) frames.Add(entry.Last); Warn(source.Kind, "互动采集保留上一有效状态：" + source.Kind + " · " + e.Message); }
        }
        foreach (int id in dead) { segments?.Forget(id); candidates.Remove(id); Retire(id); }
        if (!gloomAssigned)
            foreach (var entry in entries.Values) if (entry.Source.Kind == "ghost-fire") { entry.Gloom = gloomAssigned = true; break; }
        bool same = frames.Count == previous.Length;
        if (same) for (int i = 0; i < frames.Count; i++) if (!ReferenceEquals(frames[i], previous[i])) { same = false; break; }
        return same ? previous : previous = frames.ToArray();
    }
    private InteractionContinuousState ReadContinuous(Entry entry, double now, bool active)
    {
        if (!active) return entry.Continuous = InteractionContinuousState.None;
        var state = InteractionContinuousState.None;
        if (entry.DynamicRoot)
        {
            // Only dynamic/following families get this inexpensive root read.
            // Static statues, arrows and campfires do not walk their trees to
            // ask whether they might have moved.
            var root = entry.Source.Root.transform;
            root.GetPositionAndRotation(out var p, out var q); var scale = root.lossyScale; RootReadCount++; MotionReadCount++;
            bool changed = !entry.HasRootObservation || !RootNear(entry.RootPosition, p, .001f) ||
                Math.Abs(Quaternion.Dot(entry.RootRotation, q)) <= .999999f || !RootNear(entry.RootScale, scale, .00001f);
            if (changed)
            {
                entry.RootPosition = p; entry.RootRotation = q; entry.RootScale = scale;
                entry.MotionUntil = now + .1;
            }
            entry.HasRootObservation = true;
            if (now <= entry.MotionUntil) state |= InteractionContinuousState.AttachedMotion;
        }
        // World roots are stationary; only their native action can release
        // shards. Continue watching actual simulation until it settles, then
        // release body queries as well as the whole hierarchy.
        if (entry.DynamicRoot || entry.Dirty || entry.Lifecycle.EventActive(now) ||
            (entry.Continuous & InteractionContinuousState.Physics) != 0)
            foreach (var body in entry.Bodies)
                if (body && body.gameObject.activeInHierarchy && !body.isKinematic && !body.IsSleeping() &&
                    (body.linearVelocity.sqrMagnitude > .0001f || body.angularVelocity.sqrMagnitude > .0001f))
                { state |= InteractionContinuousState.Physics; break; }
        if (entry.AnimationProgress != null)
        {
            long before = entry.AnimationProgress.ProbeCount;
            if (entry.AnimationProgress.Read(now, entry.Dirty, followSceneClock: true)) state |= InteractionContinuousState.AdvancingAnimation;
            AnimationReadCount += entry.AnimationProgress.ProbeCount - before;
        }
        // This is the audited native subtitle coroutine's finite timeline,
        // not an arbitrary enabled TextMeshPro keeping an event alive.
        if (now <= entry.SubtitleUntil && entry.Source.Text && entry.Source.Text.gameObject.activeInHierarchy)
            state |= InteractionContinuousState.NativeTween;
        return entry.Continuous = state;
    }
    private static bool RootNear(Vector3 a, Vector3 b, float epsilon) =>
        Math.Abs(a.x - b.x) <= epsilon && Math.Abs(a.y - b.y) <= epsilon && Math.Abs(a.z - b.z) <= epsilon;
    private void ReadIndependent(Entry entry, double now, bool active)
    {
        var old = entry.Last;
        if (old == null || entry.Dirty || now + 1e-8 < entry.NextIndependent) return;
        // This deadline is a failure backoff only. Normal independent visual
        // reads already run at the scene clock's maximum of 30 observations/s.
        entry.NextIndependent = double.NegativeInfinity;
        // A constant material may expose _Opacity/_Glow/_EmissionColor. None
        // of those names makes a static object an animation. Native events and
        // Animator progress already take the full Sample path above.
        var visuals = old.Visuals;
        bool animation = (entry.Continuous & InteractionContinuousState.AdvancingAnimation) != 0;
        bool eventActive = entry.Lifecycle.EventActive(now);
        if (entry.AppearanceDirty || InteractionAppearanceReadPolicy.Periodic(active, false, animation, eventActive, false) &&
            NativeVisualAppearance.NeedsCaptureAtTime(entry.Source.Root.transform, visuals, now, active, true, followSceneClock: true))
        {
            visuals = NativeVisualAppearance.CaptureAtTime(entry.Source.Root.transform, visuals, now, entry.AppearanceDirty, InteractionReplayRules.MaximumNodes, true, followSceneClock: true);
            entry.AppearanceDirty = false;
            AppearanceReadCount++;
        }
        var lights = old.Lights;
        if (lights != null && lights.Length != 0 && (entry.LightDirty || entry.LightReads.ShouldRead(now) ||
            InteractionAppearanceReadPolicy.Periodic(active, LightDriven(entry), animation, eventActive, false)))
        { lights = NativeLightAppearance.Capture(entry.Source.Root.transform, lights, InteractionReplayRules.MaximumNodes); entry.LightDirty = false; entry.LightReads.Observed(now); LightReadCount++; }
        if (!ReferenceEquals(visuals, old.Visuals) || !ReferenceEquals(lights, old.Lights))
            entry.Last = new InteractionReplayFrame { Key = old.Key, Kind = old.Kind, Resource = old.Resource, SourcePath = old.SourcePath,
                Pose = old.Pose, Visuals = visuals, Lights = lights, Line = old.Line, Text = old.Text, GloomAmount = old.GloomAmount,
                PostWeights = old.PostWeights, PostEnabled = old.PostEnabled };
    }
    private InteractionReplayFrame Sample(Entry entry, double now)
    {
        var source = entry.Source; var old = entry.Last;
        bool special = source.Line || source.Text || source.Posts != null;
        var pose = VisualReplica.Capture(source.Root.transform, !special, InteractionReplayRules.MaximumNodes);
        RootReadCount++; if (!special) GeometryReadCount++;
        bool animation = (entry.Continuous & InteractionContinuousState.AdvancingAnimation) != 0;
        bool eventActive = entry.Lifecycle.EventActive(now);
        bool fullAppearance = entry.Dirty || entry.AppearanceDirty || old == null || entry.Lifecycle.LastProbeWasFallback;
        bool appearanceRead = !special && (fullAppearance || InteractionAppearanceReadPolicy.Periodic(entry.Active, false, animation, eventActive, false));
        var visuals = appearanceRead ? NativeVisualAppearance.CaptureAtTime(source.Root.transform, old?.Visuals, now, fullAppearance, InteractionReplayRules.MaximumNodes, true, followSceneClock: true) : old?.Visuals;
        if (appearanceRead) { AppearanceReadCount++; entry.AppearanceDirty = false; }
        bool lightRead = !special && (entry.Dirty || entry.LightDirty || entry.LightReads.ShouldRead(now) || old == null || entry.Lifecycle.LastProbeWasFallback ||
            InteractionAppearanceReadPolicy.Periodic(entry.Active, LightDriven(entry), animation, eventActive, false));
        var lights = lightRead ? NativeLightAppearance.Capture(source.Root.transform, old?.Lights, InteractionReplayRules.MaximumNodes) : old?.Lights;
        if (lightRead) { LightReadCount++; entry.LightDirty = false; entry.LightReads.Observed(now); }
        var line = source.Line ? SampleLine(source.Line!, entry.LineWidth!, old?.Line) : null;
        var text = source.Text ? SampleText(source.Text!, old?.Text) : null;
        float? gloom = entry.Gloom ? entry.GloomValue : null;
        if (old != null && ReferenceEquals(old.Pose, pose) && ReferenceEquals(old.Visuals, visuals) && ReferenceEquals(old.Lights, lights) && ReferenceEquals(old.Line, line) && ReferenceEquals(old.Text, text) && old.GloomAmount == gloom && ReferenceEquals(old.PostWeights, entry.PostWeights) && ReferenceEquals(old.PostEnabled, entry.PostEnabled)) return old;
        return new InteractionReplayFrame { Key = entry.Key, Kind = source.Kind, Resource = source.Resource, SourcePath = source.Path,
            Pose = pose, Visuals = visuals, Lights = lights, Line = line, Text = text, GloomAmount = gloom, PostWeights = entry.PostWeights, PostEnabled = entry.PostEnabled };
    }
    private static bool LightDriven(Entry entry)
    {
        foreach (var driver in entry.LightDrivers)
        {
            if (driver.Varying && driver.Source && driver.Source.isActiveAndEnabled && driver.Light && driver.Light!.isActiveAndEnabled) return true;
        }
        return false;
    }
    private static bool CurveVaries(AnimationCurve? curve)
    {
        if (curve == null || curve.length < 2) return false;
        float value = curve[0].value;
        for (int i = 0; i < curve.length; i++)
        {
            var key = curve[i];
            if (key.value != value || key.inTangent != 0 || key.outTangent != 0) return true;
        }
        return false;
    }
    private static InteractionReplayFrame CopyState(InteractionReplayFrame old, float? gloom, float[]? weights, bool[]? enabled) => new()
    { Key = old.Key, Kind = old.Kind, Resource = old.Resource, SourcePath = old.SourcePath, Pose = old.Pose, Visuals = old.Visuals, Lights = old.Lights,
        Line = old.Line, Text = old.Text, GloomAmount = gloom, PostWeights = weights, PostEnabled = enabled };
    private static InteractionLineFrame SampleLine(LineRenderer line, AnimationCurve curve, InteractionLineFrame? old)
    {
        bool visible = line.enabled && line.gameObject.activeInHierarchy && !line.forceRenderingOff;
        int count = line.positionCount;
        if (count > InteractionReplayRules.MaximumLinePoints) throw new InvalidOperationException("Clone ray point budget exceeded.");
        bool same = old != null && old.Visible == visible && old.Points.Length == count * 3 && ColorSame(old.StartColor, line.startColor) && ColorSame(old.EndColor, line.endColor);
        // The audited clone ray changes endpoints/colour, and ArrowShooter
        // changes endpoints/enabled. Neither rewrites the native width curve.
        // Cache its copied wrapper at birth/action events, not every sample.
        float[]? points = null, widths = null;
        for (int i = 0; i < count; i++)
        {
            var p = line.GetPosition(i); if (!line.useWorldSpace) p = line.transform.TransformPoint(p);
            float width = Math.Abs(line.widthMultiplier * curve.Evaluate(count < 2 ? 0 : (float)i / (count - 1)));
            if (same && old!.Points[i * 3] == p.x && old.Points[i * 3 + 1] == p.y && old.Points[i * 3 + 2] == p.z && old.Widths[i] == width) continue;
            if (points == null)
            { points = new float[count * 3]; widths = new float[count]; if (old != null && old.Points.Length == points.Length) { Array.Copy(old.Points, points, points.Length); Array.Copy(old.Widths, widths, widths.Length); } }
            same = false; points[i * 3] = p.x; points[i * 3 + 1] = p.y; points[i * 3 + 2] = p.z; widths![i] = width;
        }
        if (same) return old!;
        return new InteractionLineFrame { Visible = visible, Points = points ?? (old?.Points.Length == count * 3 ? old.Points : Array.Empty<float>()), Widths = widths ?? (old?.Widths.Length == count ? old.Widths : Array.Empty<float>()), StartColor = ColorValues(line.startColor), EndColor = ColorValues(line.endColor) };
    }
    private static InteractionTextFrame SampleText(TextMeshPro text, InteractionTextFrame? old)
    {
        bool visible = text.enabled && text.gameObject.activeInHierarchy;
        string content = text.text ?? ""; if (content.Length > 4096) throw new InvalidOperationException("Native subtitle length exceeds limit.");
        if (old != null && old.Visible == visible && old.Content == content && old.FontSize == text.fontSize && ColorSame(old.Color, text.color)) return old;
        return new InteractionTextFrame { Visible = visible, Content = content, FontSize = text.fontSize, Color = ColorValues(text.color) };
    }
    internal static float[] ColorValues(Color value) => new[] { value.r, value.g, value.b, value.a };
    private static bool ColorSame(float[] value, Color color) => value[0] == color.r && value[1] == color.g && value[2] == color.b && value[3] == color.a;
    private void Warn(string key, string message) { if (warned.Count < 64 && warned.Add(key)) warning?.Invoke(message); }
    public void Dispose()
    {
        if (disposed) return; disposed = true; if (ReferenceEquals(current, this)) current = null; observer.Dispose(); segments?.Dispose();
        foreach (var entry in entries.Values) { entry.RootLifecycle?.Dispose(); entry.Lifecycle.Retire(); VisualReplica.ReleaseCapture(entry.Root); }
        entries.Clear(); candidates.Clear(); frames.Clear(); dead.Clear(); previous = Array.Empty<InteractionReplayFrame>();
    }
}
