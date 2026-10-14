using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

// RPC_Init may start a coroutine before the local Character exists. Registration
// is immediate, but capture waits for the native private owner to be assigned.
internal sealed class BalloonReplayObserver : IDisposable
{
    private static readonly HashSet<BalloonReplayObserver> listeners = new();
    private static Harmony? harmony;
    private readonly Action<TiedBalloon> initialized, destroyed;
    private bool disposed;
    public BalloonReplayObserver(Action<TiedBalloon> initialized, Action<TiedBalloon> destroyed)
    {
        this.initialized = initialized; this.destroyed = destroyed;
        if (listeners.Count == 0)
        {
            var install = new Harmony("cn.mylus.peakreplaylab.balloons." + Guid.NewGuid().ToString("N"));
            try
            {
                install.Patch(AccessTools.DeclaredMethod(typeof(TiedBalloon), nameof(TiedBalloon.RPC_Init)),
                    postfix: new HarmonyMethod(typeof(BalloonReplayObserver), nameof(Initialized)));
                var routine = AccessTools.DeclaredMethod(typeof(TiedBalloon), "InitRoutine")
                    ?? throw new MissingMethodException(nameof(TiedBalloon), "InitRoutine");
                var moveNext = AccessTools.EnumeratorMoveNext(routine)
                    ?? throw new MissingMethodException(nameof(TiedBalloon), "InitRoutine.MoveNext");
                install.Patch(moveNext, transpiler: new HarmonyMethod(typeof(BalloonReplayObserver), nameof(ObserveInitialized)));
                install.Patch(AccessTools.DeclaredMethod(typeof(TiedBalloon), "OnDestroy"),
                    prefix: new HarmonyMethod(typeof(BalloonReplayObserver), nameof(Destroyed)));
                harmony = install;
            }
            catch { install.UnpatchSelf(); throw; }
        }
        listeners.Add(this);
    }
    private static void Initialized(TiedBalloon __instance)
    { if (__instance) foreach (var listener in listeners) try { listener.initialized(__instance); } catch { } }
    private static IEnumerable<CodeInstruction> ObserveInitialized(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var source = new List<CodeInstruction>(instructions);
        FieldInfo? owner = null;
        foreach (var field in __originalMethod.DeclaringType!.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (field.FieldType == typeof(TiedBalloon))
            {
                if (owner != null) throw new InvalidOperationException("Native balloon initialization has ambiguous owners.");
                owner = field;
            }
        var add = AccessTools.DeclaredMethod(typeof(List<TiedBalloon>), nameof(List<TiedBalloon>.Add));
        int matches = 0;
        foreach (var instruction in source)
            if (instruction.Calls(add)) matches++;
        if (owner == null || matches != 1)
            throw new InvalidOperationException("Native balloon initialization no longer matches its audited completion.");
        var notify = AccessTools.DeclaredMethod(typeof(BalloonReplayObserver), nameof(Initialized));
        foreach (var instruction in source)
        {
            yield return instruction;
            if (instruction.Calls(add))
            {
                // Observe the completed native assignment, even when RPC_Init
                // waited many frames for the local Character. Never run it again.
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldfld, owner);
                yield return new CodeInstruction(OpCodes.Call, notify);
            }
        }
    }
    private static void Destroyed(TiedBalloon __instance)
    { if (__instance) foreach (var listener in listeners) try { listener.destroyed(__instance); } catch { } }
    public void Dispose()
    {
        if (disposed) return; disposed = true; listeners.Remove(this);
        if (listeners.Count == 0) { harmony?.UnpatchSelf(); harmony = null; }
    }
}

internal sealed class BalloonReplayCapture : IDisposable
{
    private static readonly FieldInfo Owner = AccessTools.Field(typeof(TiedBalloon), "characterBalloons");
    private static readonly FieldInfo Started = AccessTools.Field(typeof(TiedBalloon), "initialTime");
    private sealed class Entry
    {
        public TiedBalloon Source = null!;
        public CharacterBalloons? Owner;
        public string Key = "", OwnerId = "";
        public double Started, RetryAfter;
        public bool Dirty = true;
        public bool InitialUrgent, OwnerlessObserved;
        public BalloonReplayFrame? Last;
        public IDisposable? RootLifecycle;
    }
    private readonly Dictionary<int, Entry> entries = new();
    private readonly List<int> removed = new();
    private readonly List<BalloonReplayFrame> frames = new();
    private BalloonReplayFrame[] previous = Array.Empty<BalloonReplayFrame>();
    private readonly HashSet<string> warned = new(StringComparer.Ordinal);
    private readonly BalloonReplayObserver observer;
    private readonly SegmentCaptureClient? segments;
    private readonly Action<string>? warning;
    private readonly int scene = SceneManager.GetActiveScene().handle;
    private long serial;
    private bool disposed;
    internal bool InitialBaselineReady
    { get { foreach (var entry in entries.Values) if (entry.Source && entry.Last == null && !entry.OwnerlessObserved) return false; return true; } }
    public BalloonReplayCapture(Action<string>? warning = null)
    {
        this.warning = warning;
        segments = CaptureSegmentScope.Active?.CreateClient(Admitted, Retire);
        try { observer = new BalloonReplayObserver(source => Register(source, true), Remove); }
        catch { segments?.Dispose(); throw; }
        try
        {
            if (Owner == null || Started == null) throw new InvalidOperationException("Native tied balloon attachment fields changed.");
            foreach (var source in Object.FindObjectsByType<TiedBalloon>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Register(source);
        }
        catch { Dispose(); throw; }
    }
    private void Register(TiedBalloon source, bool urgent = false)
    {
        if (disposed || ReplaySafety.Active || !source || source.gameObject.scene.handle != scene) return;
        if (!(segments?.Admit(source.gameObject, urgent) ?? true)) return;
        int id = source.gameObject.GetInstanceID();
        if (entries.TryGetValue(id, out var old))
        {
            if (old.Source == source) { old.Dirty = true; old.InitialUrgent |= urgent; old.OwnerlessObserved = false; old.RetryAfter = 0; return; }
            old.RootLifecycle?.Dispose(); entries.Remove(id);
        }
        if (entries.Count >= BalloonReplayRules.MaximumEntities) { Warn("limit", "绑头气球超过128个，超出部分未记录。"); return; }
        var entry = new Entry { Source = source, Key = "balloon:" + id + ":" + (++serial), InitialUrgent = urgent };
        entries.Add(id, entry);
        entry.RootLifecycle = CaptureRootLifecycleObserver.Bind(source.gameObject, () => Register(source, true));
    }
    private void Admitted(GameObject source)
    { if (source && source.TryGetComponent<TiedBalloon>(out var balloon)) Register(balloon); }
    private void Retire(int id)
    {
        if (!entries.TryGetValue(id, out var entry)) return;
        entry.RootLifecycle?.Dispose(); entries.Remove(id); previous = Array.Empty<BalloonReplayFrame>();
    }
    private void Remove(TiedBalloon source)
    { if (source) { int id = source.gameObject.GetInstanceID(); segments?.Forget(id); Retire(id); } }
    public BalloonReplayFrame[] Capture(double now)
    {
        if (disposed) throw new ObjectDisposedException(nameof(BalloonReplayCapture));
        frames.Clear(); removed.Clear();
        foreach (var pair in entries)
        {
            var entry = pair.Value;
            if (!entry.Source) { removed.Add(pair.Key); continue; }
            if (now < entry.RetryAfter)
            { if (entry.Last != null) frames.Add(entry.Last); continue; }
            // Owner/color/start/offset are assigned by native initialization;
            // root activation and native completion wake this immutable state.
            if ((entry.Last != null || entry.OwnerlessObserved) && !entry.InitialUrgent && !entry.Dirty)
            { if (entry.Last != null) frames.Add(entry.Last); continue; }
            if (entry.Last == null && !entry.OwnerlessObserved && !entry.InitialUrgent && !CaptureBootstrapWorkBudget.TryBeginRead()) continue;
            entry.InitialUrgent = false;
            try
            {
                if (entry.Dirty || !entry.Owner)
                {
                    var owner = Owner.GetValue(entry.Source) as CharacterBalloons;
                    // A reinitialization/temporary observation failure must not
                    // masquerade as destruction of an already observed balloon.
                    if (!owner)
                    {
                        // Native RPC_Init can await the local character before
                        // assigning this owner. This is an observed unattached
                        // state, not a failed read or a fabricated attachment.
                        entry.OwnerlessObserved = true; entry.Dirty = false;
                        if (entry.Last != null) frames.Add(entry.Last); continue;
                    }
                    var character = owner!.GetComponent<Character>();
                    if (!character) throw new InvalidOperationException("Native balloon owner Character is unavailable.");
                    entry.Owner = owner; entry.OwnerId = PeakReplayLab.Capture.Id(character);
                    entry.OwnerlessObserved = false;
                    entry.Started = Convert.ToDouble(Started.GetValue(entry.Source)); entry.Dirty = false;
                }
                bool active = entry.Source.gameObject.activeInHierarchy;
                int color = entry.Source.colorIndex; float offset = entry.Owner!.headOffset;
                var old = entry.Last;
                if (old == null || old.OwnerId != entry.OwnerId || old.ColorIndex != color || old.HeadOffset != offset ||
                    old.Started != entry.Started || old.Active != active)
                {
                    var next = new BalloonReplayFrame { Key = entry.Key, OwnerId = entry.OwnerId, ColorIndex = color,
                        HeadOffset = offset, Started = entry.Started, Active = active };
                    BalloonReplayRules.Validate(next); entry.Last = next;
                }
                frames.Add(entry.Last!);
            }
            catch (Exception e)
            {
                entry.RetryAfter = now + 2;
                if (entry.Last != null) frames.Add(entry.Last);
                Warn("capture", "绑头气球采集暂不可用：" + e.Message);
            }
        }
        foreach (int id in removed) { segments?.Forget(id); Retire(id); }
        bool same = frames.Count == previous.Length;
        if (same) for (int i = 0; i < frames.Count; i++) if (!ReferenceEquals(frames[i], previous[i])) { same = false; break; }
        return same ? previous : previous = frames.ToArray();
    }
    private void Warn(string key, string value) { if (warned.Add(key)) try { warning?.Invoke(value); } catch { } }
    public void Dispose()
    {
        if (disposed) return; disposed = true; observer.Dispose(); segments?.Dispose();
        foreach (var entry in entries.Values) entry.RootLifecycle?.Dispose();
        entries.Clear(); frames.Clear(); removed.Clear();
        previous = Array.Empty<BalloonReplayFrame>();
    }
}

