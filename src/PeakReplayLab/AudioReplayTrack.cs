using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

// Only audited game-owned fields are discovered. In particular CharacterVoiceHandler,
// Photon/Dissonance/Steam voice sources and microphone-created clips are never scanned.
internal static class AudioReplaySources
{
    internal sealed class Owner
    {
        public readonly Type Type;
        public readonly FieldInfo[] Fields;
        public readonly FieldInfo[] ClipFields;
        public readonly bool SoundChildren;
        public Owner(Type type)
        {
            Type = type;
            // These exact native prefabs contain playOnAwake sources as well as
            // pooled SFX (explosion debris, torch flame, cannon/rocket ignition).
            SoundChildren = type == typeof(SFX_PlayOneShot) || type == typeof(Lantern) ||
                type == typeof(ScoutCannon) || type == typeof(Peak.RocketFX);
            var fields = new List<FieldInfo>(); var clipFields = new List<FieldInfo>();
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (type == typeof(GUIManager) && field.Name != nameof(GUIManager.stingerSound)) continue;
                if (field.FieldType == typeof(AudioSource) || field.FieldType == typeof(AudioSource[])) fields.Add(field);
                if (field.FieldType == typeof(AudioClip) || field.FieldType == typeof(AudioClip[])) clipFields.Add(field);
            }
            Fields = fields.ToArray(); ClipFields = clipFields.ToArray();
        }
    }
    private static readonly Owner[] owners = BuildOwners();
    public static int Count => owners.Length;
    public static IEnumerable<MethodInfo> LifecycleMethods()
    {
        var known = new HashSet<MethodInfo>();
        foreach (var owner in owners)
            foreach (string name in new[] { "Awake", "OnEnable", "Start" })
            {
                var method = owner.Type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                    BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (method != null && known.Add(method)) yield return method;
            }
    }
    public static bool IsVoice(AudioSource source)
    {
        if (source.GetComponentInParent<CharacterVoiceHandler>()) return true;
        foreach (var component in source.GetComponentsInParent<MonoBehaviour>(true))
        {
            if (!component) continue;
            string name = component.GetType().FullName ?? "";
            if (name.StartsWith("Photon.Voice.", StringComparison.Ordinal) ||
                name.StartsWith("Dissonance.", StringComparison.Ordinal)) return true;
        }
        return false;
    }
    private static Owner[] BuildOwners()
    {
        string[] names = { "AudioLoop", "RopeAudio", "ItemAudioManager", "Campfire", "FallAudio", "FootStepPlayer",
            "AmbienceAudio", "BeeSwarm", "ItemVFX", "RescueHookSFX", "CoverageToVolume", "BugleSFX",
            "AntlionSandRumbleSFX", "BreakableBridge", "MyresAmbience", "MushroomZombie", "MandrakeScreamFX",
            "Peak.EnvironmentLoopHelper", "Peak.Jetpack", "Peak.Action_Antizooka", "Peak.TrickLuggage",
            "Peak.StatusFieldGloom", "Peak.ScoutmasterSoulPillar", "Peak.PeakGatePortal",
            "SFX_PlayOneShot", "Lantern", "ScoutCannon", "Peak.RocketFX", "Action_AskBingBong", "Peak.InfiniteStamSphere", "GUIManager" };
        var result = new List<Owner>();
        foreach (string name in names)
        {
            var type = typeof(SFX_Player).Assembly.GetType(name);
            if (type != null && typeof(MonoBehaviour).IsAssignableFrom(type)) result.Add(new Owner(type));
        }
        return result.ToArray();
    }
    public static void Discover(int index, Action<AudioSource> found)
    {
        if (owners.Length == 0) return;
        var owner = owners[index % owners.Length];
        foreach (var component in Object.FindObjectsByType(owner.Type, FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (!component) continue;
            Read(owner, component, found);
        }
    }
    private static void Read(Owner owner, Object component, Action<AudioSource> found)
    {
        if (component is Action_AskBingBong bing) AudioReplayHooks.AttachBingBong(bing);
        foreach (var field in owner.Fields)
        {
            object? value = field.GetValue(component);
            if (value is AudioSource source && source && !IsVoice(source)) found(source);
            else if (value is AudioSource[] sources)
                foreach (var entry in sources) if (entry && !IsVoice(entry)) found(entry);
        }
        if (owner.SoundChildren && component is Component root)
            foreach (var source in root.GetComponentsInChildren<AudioSource>(true))
                if (source && !IsVoice(source)) found(source);
    }
    public static void NativeOwner(Component component, Action<AudioSource> found)
    {
        if (!component) return;
        var type = component.GetType();
        foreach (var owner in owners) if (owner.Type == type) { Read(owner, component, found); return; }
    }
    public static void Pool(Action<AudioSource> found, Action<SFX_Player.SFX_Source>? pooled = null)
    {
        var player = SFX_Player.instance;
        if (!player || player.sources == null) return;
        foreach (var item in player.sources) if (item != null && item.source)
            { if (pooled != null) pooled(item); else found(item.source); }
    }
    public static void NativeClips(Action<AudioClip> found, Action<AudioSource>? foundSource = null,
        Action<AudioSource, AudioClip>? explicitSpatial = null)
    {
        // A broad AudioClip scan would also expose runtime microphone/voice buffers
        // to a forged replay resource key. Resolve ONLY audited native owners/SFX.
        foreach (var sfx in Resources.FindObjectsOfTypeAll<SFX_Instance>())
            if (sfx && sfx.clips != null) foreach (var clip in sfx.clips) if (clip) found(clip);
        // Hero-title stingers are native direct clips, not pooled SFX assets.
        // Read their exact authored fields once when building the replay catalog;
        // a historical region/title need not still be assigned to the GUI source.
        foreach (var progress in Resources.FindObjectsOfTypeAll<MountainProgressHandler>())
        {
            if (!progress) continue;
            if (progress.progressPoints != null)
                foreach (var point in progress.progressPoints) if (point != null && point.clip) found(point.clip);
            if (progress.tombProgressPoint != null && progress.tombProgressPoint.clip) found(progress.tombProgressPoint.clip);
        }
        foreach (var trigger in Resources.FindObjectsOfTypeAll<Peak.ForceHeroTextTrigger>())
            if (trigger && trigger.clip) found(trigger.clip);
        void Source(AudioSource source) { foundSource?.Invoke(source); if (source.clip) found(source.clip); }
        Pool(Source);
        for (int index = 0; index < owners.Length; index++)
        {
            var owner = owners[index];
            // Loaded prefab/inactive owners are needed when the historical item
            // does not exist in the newly loaded scene. This only reads assets;
            // live muting/recording continues to use scene discovery above.
            foreach (var component in Resources.FindObjectsOfTypeAll(owner.Type))
            {
                if (!component) continue;
                Read(owner, component, Source);
                if (component is Action_AskBingBong bing && bing.responses != null)
                    foreach (var response in bing.responses)
                        if (response?.sfx && response.sfx.clips != null)
                            foreach (var clip in response.sfx.clips)
                                if (clip) { found(clip); if (bing.source) explicitSpatial?.Invoke(bing.source, clip); }
                foreach (var field in owner.ClipFields)
                {
                    object? value = field.GetValue(component);
                    if (value is AudioClip clip && clip) found(clip);
                    else if (value is AudioClip[] entries) foreach (var entry in entries) if (entry) found(entry);
                }
            }
        }
    }
}

internal static class AudioReplayHooks
{
    private static Harmony? harmony;
    private static AudioPoolNativeObserver? poolObserver;
    internal static bool PoolHooksReady => poolObserver != null;
    private static int users;
    internal static AudioReplayCapture? Capture;
    internal static AudioReplayPlayback? Playback;
    private static readonly Action<AudioSource> foundOwnerSource = ObserveOwnerSource;
    private static readonly Action<AudioSource> foundBaselineSource = ObserveBaselineSource;
    private static readonly Dictionary<Action_AskBingBong, Action_AskBingBong.AskEvent> bingBongs = new();
    private static readonly List<Action_AskBingBong> deadBingBongs = new();
    public static void Acquire(Action<string>? warning)
    {
        if (users++ != 0) return;
        harmony = new Harmony("cn.mylus.peakreplaylab.audio-observation");
        try
        {
            poolObserver = new AudioPoolNativeObserver(PoolCreated, PoolStarted, PoolStopped);
            harmony.Patch(AccessTools.Method(typeof(SFX_Instance), nameof(SFX_Instance.PlayFromSource)),
                postfix: new HarmonyMethod(typeof(AudioReplayHooks), nameof(AfterDirect)));
            harmony.Patch(AccessTools.Method(typeof(Peak.Action_Antizooka), "PlayChargeFX"),
                postfix: new HarmonyMethod(typeof(AudioReplayHooks), nameof(AfterCharge)));
            harmony.Patch(AccessTools.Method(typeof(Peak.Jetpack), nameof(Peak.Jetpack.ActivateJetpack)),
                prefix: new HarmonyMethod(typeof(AudioReplayHooks), nameof(BeforeJetpack)),
                postfix: new HarmonyMethod(typeof(AudioReplayHooks), nameof(AfterJetpack)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Item), "OnEnable"),
                postfix: new HarmonyMethod(typeof(AudioReplayHooks), nameof(AfterItem)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Action_AskBingBong), "Ask"),
                prefix: new HarmonyMethod(typeof(AudioReplayHooks), nameof(BeforeAsk)));
            foreach (string name in new[] { "ForcePlayStinger", "SetHeroTitle" })
                harmony.Patch(AccessTools.DeclaredMethod(typeof(GUIManager), name),
                    postfix: new HarmonyMethod(typeof(AudioReplayHooks), nameof(AfterStinger)));
            // Registration only: never run gameplay methods to reproduce sound.
            foreach (var entry in new[] { (typeof(Peak.RocketFX), "RocketLight"), (typeof(Peak.RocketFX), "RocketFire"),
                (typeof(Peak.InfiniteStamSphere), "Initialize") })
                harmony.Patch(AccessTools.Method(entry.Item1, entry.Item2),
                    postfix: new HarmonyMethod(typeof(AudioReplayHooks), nameof(AfterOwner)));
            foreach (var method in AudioReplaySources.LifecycleMethods())
                try { harmony.Patch(method, postfix: new HarmonyMethod(typeof(AudioReplayHooks), nameof(AfterOwnerBaseline))); }
                catch (Exception e) { warning?.Invoke("Native audio owner lifecycle unavailable: " + method.DeclaringType?.Name + "." + method.Name + " · " + e.GetType().Name); }
        }
        catch (Exception e)
        {
            poolObserver?.Dispose(); poolObserver = null;
            harmony.UnpatchSelf();
            warning?.Invoke("Game sound start hooks unavailable; observed native sources remain best-effort: " + e.GetType().Name);
        }
    }
    private static void PoolCreated(SFX_Player.SFX_Source source)
    {
        if (!source.source) return;
        if (ReplaySafety.Active) Playback?.MuteOriginal(source.source);
        else Capture?.RegisterPool(source);
    }
    private static void PoolStarted(SFX_Player.SFX_Source source)
    { PoolCreated(source); if (source.source) Observe(source.source); }
    private static void PoolStopped(SFX_Player.SFX_Source source)
    { if (!ReplaySafety.Active) Capture?.StoppedPool(source); }
    private static void AfterDirect(AudioSource source) { if (source) Observe(source); }
    private static void AfterItem(Item __instance)
    {
        if (!__instance) return;
        if (!ReplaySafety.Active && Capture != null && !Capture.AdmitOwner(__instance.gameObject)) return;
        foreach (var bing in __instance.GetComponentsInChildren<Action_AskBingBong>(true)) AttachBingBong(bing);
    }
    private static void BeforeAsk(Action_AskBingBong __instance) => AttachBingBong(__instance);
    private static void AfterStinger(GUIManager __instance, AudioClip stinger)
    { if (__instance && stinger && __instance.stingerSound && __instance.stingerSound.clip == stinger) Observe(__instance.stingerSound); }
    internal static void AttachBingBong(Action_AskBingBong owner)
    {
        if (!owner || !owner.gameObject.scene.IsValid() || bingBongs.ContainsKey(owner)) return;
        // The limit bounds live subscriptions, not every item that ever existed
        // in a long recording. Only a new registration at capacity does cleanup.
        if (bingBongs.Count >= 256)
        {
            deadBingBongs.Clear();
            foreach (var registered in bingBongs.Keys) if (!registered) deadBingBongs.Add(registered!);
            foreach (var dead in deadBingBongs) bingBongs.Remove(dead);
            deadBingBongs.Clear();
            if (bingBongs.Count >= 256) return;
        }
        Action_AskBingBong.AskEvent callback = clip =>
        {
            try
            {
                if (!owner || !owner.source || !clip || owner.responses == null) return;
                // OnAsk is emitted after the native PlayOneShot. Admit only its
                // authored response clips; never a voice-chat/microphone source.
                bool native = false;
                foreach (var response in owner.responses)
                    if (response?.sfx && response.sfx.clips != null)
                        foreach (var allowed in response.sfx.clips) if (allowed == clip) native = true;
                if (!native || AudioReplaySources.IsVoice(owner.source)) return;
                if (ReplaySafety.Active) Playback?.MuteOriginal(owner.source);
                else Capture?.StartedOneShot(owner.source, clip);
            }
            catch { }
        };
        owner.OnAsk += callback; bingBongs.Add(owner, callback);
    }
    private static void AfterCharge(Peak.Action_Antizooka __instance) { if (__instance.chargeSFX) Observe(__instance.chargeSFX); }
    // Repeated held jetpack input refreshes its timer; it must not create a new
    // recorded voice unless native code actually executes AudioSource.Play().
    private static void BeforeJetpack(bool ____active, out bool __state) => __state = !____active;
    private static void AfterJetpack(Peak.Jetpack __instance, bool __state)
    { if (__state && __instance.jetpackLoop) Observe(__instance.jetpackLoop); }
    private static void AfterOwner(Component __instance)
    {
        try
        {
            if (!__instance || !ReplaySafety.Active && Capture != null && !Capture.AdmitOwner(__instance.gameObject, true)) return;
            AudioReplaySources.NativeOwner(__instance, foundOwnerSource);
        }
        catch { /* A native prefab may be in the middle of being destroyed. */ }
    }
    private static void AfterOwnerBaseline(Component __instance)
    {
        try
        {
            if (!__instance || !ReplaySafety.Active && Capture != null && !Capture.AdmitOwner(__instance.gameObject)) return;
            AudioReplaySources.NativeOwner(__instance, foundBaselineSource);
        }
        catch { }
    }
    private static void ObserveBaselineSource(AudioSource source)
    {
        if (ReplaySafety.Active) Playback?.MuteOriginal(source);
        else Capture?.Discovered(source, false);
    }
    private static void ObserveOwnerSource(AudioSource source)
    {
        if (ReplaySafety.Active) Playback?.MuteOriginal(source);
        else Capture?.Discovered(source);
    }
    private static void Observe(AudioSource source)
    {
        // Observation must not throw into a game sound call or alter random choices.
        try
        {
            if (ReplaySafety.Active) Playback?.MuteOriginal(source);
            else Capture?.Started(source);
        }
        catch { /* Source may be destroyed by another component during this callback. */ }
    }
    public static void Release()
    {
        if (--users > 0) return;
        users = 0; harmony?.UnpatchSelf(); harmony = null;
        poolObserver?.Dispose(); poolObserver = null;
        foreach (var pair in bingBongs) if (pair.Key) pair.Key.OnAsk -= pair.Value;
        bingBongs.Clear(); deadBingBongs.Clear();
    }
}

internal sealed class AudioReplayCapture : IDisposable
{
    private sealed class Source
    {
        public readonly AudioSource Audio;
        public readonly GameObject Root;
        public readonly int ScopeId;
        public SFX_Player.SFX_Source? PoolOwner;
        public AudioClip? Clip;
        public AudioClip? ExplicitClip;
        public AudioReplayClipMetadata ClipMetadata;
        public string ClipKey = "", Key = "";
        public bool Playing, InitialObserved;
        public double Started;
        public AudioParameterSampleClock ReadClock;
        public AudioReplayFrame? Last;
        public Source(AudioSource audio) { Audio = audio; Root = audio.gameObject; ScopeId = Root.GetInstanceID(); }
    }
    private const int MaxRegistered = 512;
    private readonly Dictionary<int, Source> sources = new();
    private readonly Dictionary<int, List<int>> scopeSources = new();
    private readonly SegmentCaptureClient? segments;
    private readonly List<int> dead = new();
    private readonly List<AudioReplayFrame> current = new();
    private readonly List<AudioReplayFrame> pendingStarts = new();
    private readonly Dictionary<string, Source> pendingOwners = new(StringComparer.Ordinal);
    private readonly HashSet<string> captured = new(StringComparer.Ordinal);
    private readonly Action<string>? warning;
    private readonly Action<AudioSource> register;
    private readonly Action<SFX_Player.SFX_Source> registerPool;
    private SFX_Player? registeredPool;
    private int registeredPoolCount = -1;
    internal long PoolDiscoveryPasses { get; private set; }
    internal long IdlePoolSkips { get; private set; }
    internal long NativeLivenessReads { get; private set; }
    private AudioReplayFrame[] previous = Array.Empty<AudioReplayFrame>();
    private long sequence;
    private double nextPool;
    private bool disposed, warned;
    internal bool InitialBaselineReady
    {
        get
        {
            foreach (var source in sources.Values) if (source.Audio && !source.InitialObserved) return false;
            return true;
        }
    }
    private void Pending(Source source, AudioReplayFrame frame)
    {
        if (pendingStarts.Count >= 256) return;
        pendingStarts.Add(frame); pendingOwners[frame.Key] = source;
    }

    public AudioReplayCapture(Action<string>? warning = null)
    {
        if (AudioReplayHooks.Capture != null) throw new InvalidOperationException("Only one sound capture may be active.");
        this.warning = warning; register = source => Register(source); registerPool = RegisterPool;
        segments = CaptureSegmentScope.Active?.CreateClient(RegisterObject, Retire, go => go && go.activeInHierarchy);
        AudioReplayHooks.Acquire(warning); AudioReplayHooks.Capture = this;
        try
        {
            for (int i = 0; i < AudioReplaySources.Count; i++) AudioReplaySources.Discover(i, register);
            RefreshPool();
        }
        catch { Dispose(); throw; }
    }
    private void Warn(string message) { if (!warned) { warned = true; warning?.Invoke(message); } }
    private void RefreshPool()
    {
        var pool = SFX_Player.instance;
        int count = pool && pool.sources != null ? pool.sources.Count : 0;
        if (AudioReplayHooks.PoolHooksReady && ReferenceEquals(registeredPool, pool) && registeredPoolCount == count) return;
        registeredPool = pool; registeredPoolCount = count; PoolDiscoveryPasses++;
        AudioReplaySources.Pool(register, registerPool);
    }
    internal void RegisterPool(SFX_Player.SFX_Source native)
    {
        if (disposed || !native.source) return;
        Register(native.source);
        if (!sources.TryGetValue(native.source.GetInstanceID(), out var source)) return;
        source.PoolOwner = AudioReplayHooks.PoolHooksReady ? native : null;
        // Only this exclusive native owner provides an authoritative idle state.
        if (source.PoolOwner != null && !native.isPlaying && !source.Playing) source.InitialObserved = true;
    }
    internal void StoppedPool(SFX_Player.SFX_Source native)
    {
        if (disposed || !native.source || !sources.TryGetValue(native.source.GetInstanceID(), out var source) ||
            !ReferenceEquals(source.PoolOwner, native)) return;
        if (source.Last is { Loop: false } last)
        {
            bool pending = false;
            for (int i = 0; i < pendingStarts.Count; i++)
                if (pendingStarts[i].Key == last.Key)
                { pendingStarts[i] = AudioReplayRules.CompletedBetweenSamples(pendingStarts[i], Time.timeAsDouble); pending = true; }
            if (!pending) Pending(source, AudioReplayRules.CompletedBetweenSamples(last, Time.timeAsDouble));
        }
        source.Playing = false; source.Last = null; source.ExplicitClip = null; source.InitialObserved = true;
    }
    internal bool AdmitOwner(GameObject source, bool urgent = false) => segments?.Admit(source, urgent) ?? true;
    private void RegisterObject(GameObject source)
    {
        if (disposed || !source || !AdmitOwner(source)) return;
        foreach (var component in source.GetComponents<Component>())
        {
            if (component is AudioSource audio) Register(audio);
            else AudioReplaySources.NativeOwner(component, register);
        }
    }
    private void Retire(int scopeId)
    {
        if (!scopeSources.TryGetValue(scopeId, out var ids)) return;
        while (ids.Count != 0) RemoveSource(ids[ids.Count - 1]);
    }
    private void RemoveSource(int id)
    {
        if (!sources.TryGetValue(id, out var source)) return;
        bool rejected = segments?.OriginRejection == true;
        if (rejected)
            for (int i = pendingStarts.Count - 1; i >= 0; i--)
                if (pendingOwners.TryGetValue(pendingStarts[i].Key, out var owner) && ReferenceEquals(owner, source))
                { pendingOwners.Remove(pendingStarts[i].Key); pendingStarts.RemoveAt(i); }
        double now = Time.timeAsDouble;
        // Pooled/global tails remain admitted independently. Direct sounds
        // stopped with this root retain their observed short-run end.
        if (!rejected && source.Last is { Loop: false } last)
        {
            bool pending = false;
            for (int i = 0; i < pendingStarts.Count; i++)
                if (pendingStarts[i].Key == last.Key)
                { pendingStarts[i] = AudioReplayRules.CompletedBetweenSamples(pendingStarts[i], now); pending = true; }
            if (!pending) Pending(source, AudioReplayRules.CompletedBetweenSamples(last, now));
        }
        source.Last = null; source.Clip = source.ExplicitClip = null; source.Playing = false;
        sources.Remove(id);
        if (scopeSources.TryGetValue(source.ScopeId, out var ids))
        { ids.Remove(id); if (ids.Count == 0) scopeSources.Remove(source.ScopeId); }
    }
    private void Register(AudioSource source, bool urgent = false)
    {
        if (disposed || !source || !AdmitOwner(source.gameObject, urgent)) return;
        int id = source.GetInstanceID(), scopeId = source.gameObject.GetInstanceID();
        if (sources.TryGetValue(id, out var known))
        {
            if (known.Audio && known.Audio == source) return;
            // A genuine Play can precede the next scene cleanup after Unity
            // reuses a destroyed component ID. Preserve that old run's ending.
            if (!known.Root && known.ScopeId != scopeId) segments?.Forget(known.ScopeId);
            RemoveSource(id);
        }
        if (AudioReplaySources.IsVoice(source)) return;
        if (sources.Count >= MaxRegistered) { Warn("Game sound source limit reached; extra sources are omitted."); return; }
        sources.Add(id, new Source(source));
        if (!scopeSources.TryGetValue(scopeId, out var ids)) scopeSources.Add(scopeId, ids = new List<int>(1));
        ids.Add(id);
    }
    internal void Started(AudioSource source)
    {
        if (disposed || ReplaySafety.Active) return;
        if (!source) return;
        Register(source, true);
        if (!sources.TryGetValue(source.GetInstanceID(), out var state)) return;
        state.ExplicitClip = null;
        state.Playing = false; // A pooled source playing the same clip is still a NEW voice.
        var frame = Observe(state, Time.timeAsDouble, true);
        if (frame != null && !frame.Loop) Pending(state, frame);
    }
    internal void StartedOneShot(AudioSource source, AudioClip clip)
    {
        if (disposed || ReplaySafety.Active || !clip) return;
        if (!source) return;
        Register(source, true);
        if (!sources.TryGetValue(source.GetInstanceID(), out var state)) return;
        state.Playing = false; state.ExplicitClip = clip;
        var frame = Observe(state, Time.timeAsDouble, true);
        if (frame != null) Pending(state, frame);
    }
    internal void Discovered(AudioSource source, bool urgent = true)
    {
        if (disposed || ReplaySafety.Active) return;
        if (!source) return;
        Register(source, urgent);
        if (!sources.TryGetValue(source.GetInstanceID(), out var state)) return;
        bool wasPlaying = state.Playing;
        var frame = Observe(state, Time.timeAsDouble, urgent);
        if (!wasPlaying && frame != null && !frame.Loop) Pending(state, frame);
    }
    private AudioReplayFrame? Observe(Source source, double now, bool force = false)
    {
        AudioSource audio = source.Audio;
        if (CaptureBootstrapWorkBudget.InitialOnly && source.InitialObserved && !force) return source.Last;
        if (!source.InitialObserved && !force && !CaptureBootstrapWorkBudget.TryBeginRead()) return null;
        var result = ObserveReady(source, audio, now, force);
        // A quiet/disabled source is a complete known baseline. An actively
        // playing source with invalid or still-loading clip metadata is not.
        source.InitialObserved = result != null || !source.Playing;
        return result;
    }
    private AudioReplayFrame? ObserveReady(Source source, AudioSource audio, double now, bool force)
    {
        NativeLivenessReads++;
        if (!audio || !audio.enabled || !audio.gameObject.activeInHierarchy || !audio.isPlaying)
        { source.Playing = false; source.Last = null; source.ExplicitClip = null; return null; }
        var clip = source.ExplicitClip ? source.ExplicitClip! : audio.clip;
        if (!clip)
        { source.Playing = false; source.Last = null; source.ExplicitClip = null; return null; }
        if (!source.ClipMetadata.TryRead(clip))
        { source.Playing = true; return null; }
        bool fresh = !source.Playing || source.Clip != clip;
        if (fresh)
        {
            source.Clip = clip; source.ClipKey = source.ClipMetadata.Key;
            source.Key = "sound:" + (++sequence).ToString(CultureInfo.InvariantCulture);
            source.Started = now; source.Playing = true; force = true;
        }
        // A genuine native Play/PlayOneShot starts an independent voice even
        // when another run began at this same game time. Ordinary observations
        // dedupe and keep the fixed 20 Hz phase under the shared Scene30 caller.
        if (!source.ReadClock.Take(now, force)) return source.Last;
        Vector3 p = audio.transform.position;
        // PlayOneShot does not assign AudioSource.clip/timeSamples. Its actual
        // OnAsk birth anchors the cursor; later observed pitch changes re-anchor.
        double offset = source.ExplicitClip
            ? fresh ? 0 : source.Last != null ? AudioReplayRules.Cursor(source.Last, now) : 0
            : Math.Max(0, (double)audio.timeSamples / source.ClipMetadata.Frequency);
        bool looping = !source.ExplicitClip && audio.loop;
        float duration = source.ClipMetadata.Duration;
        if (source.ExplicitClip && !fresh && offset >= duration)
        { source.Playing = false; source.Last = null; source.ExplicitClip = null; return null; }
        float volume = audio.mute ? 0 : audio.volume;
        float pitch = audio.pitch, blend = audio.spatialBlend, min = audio.minDistance,
            max = audio.maxDistance, doppler = audio.dopplerLevel;
        int rolloff = (int)audio.rolloffMode;
        var old = source.Last;
        if (!force && old != null && old.Position[0] == p.x && old.Position[1] == p.y && old.Position[2] == p.z &&
            old.Volume == volume && old.Pitch == pitch && old.Loop == looping &&
            old.SpatialBlend == blend && old.MinDistance == min && old.MaxDistance == max &&
            old.Doppler == doppler && old.Rolloff == rolloff &&
            Distance(AudioReplayRules.Cursor(old, now), offset, duration, looping) < .04) return old;
        var value = new AudioReplayFrame
        {
            Key = source.Key, Clip = source.ClipKey, StartedAt = source.Started, AnchorTime = now, Offset = offset,
            Duration = duration, Position = new[] { p.x, p.y, p.z }, Volume = volume, Pitch = pitch,
            Loop = looping, SpatialBlend = blend, MinDistance = min,
            MaxDistance = max, Doppler = doppler, Rolloff = rolloff,
        };
        try { AudioReplayRules.Validate(value); }
        catch (Exception e)
        {
            // Failure-only metadata from an admitted native source. No object
            // hierarchy, player identity, dialogue or recorded sound bytes.
            Warn(FormattableString.Invariant($"A native game sound had unsupported metadata and was omitted. {e.GetType().Name}; clip={value.Clip}; started={value.StartedAt:R}, anchor={value.AnchorTime:R}, offset={value.Offset:R}, duration={value.Duration:R}, position=[{p.x:R},{p.y:R},{p.z:R}], volume={value.Volume:R}, pitch={value.Pitch:R}, loop={value.Loop}, spatial={value.SpatialBlend:R}, min={value.MinDistance:R}, max={value.MaxDistance:R}, doppler={value.Doppler:R}, rolloff={value.Rolloff}"));
            return null;
        }
        return source.Last = value;
    }
    internal static double Distance(double a, double b, double duration, bool loop)
    { double difference = Math.Abs(a - b); return loop ? Math.Min(difference, Math.Abs(duration - difference)) : difference; }
    public AudioReplayFrame[] Capture(double now)
    {
        if (disposed || ReplaySafety.Active) return Array.Empty<AudioReplayFrame>();
        if (now >= nextPool) { nextPool = now + .25; RefreshPool(); }
        dead.Clear(); current.Clear(); captured.Clear();
        foreach (var pair in sources)
        {
            if (!pair.Value.Audio) { dead.Add(pair.Key); continue; }
            var state = pair.Value;
            if (AudioPoolCapturePolicy.CanSkipIdle(state.PoolOwner != null, state.PoolOwner?.isPlaying ?? false,
                state.InitialObserved, state.Playing)) { IdlePoolSkips++; continue; }
            AudioReplayFrame? value = Observe(pair.Value, now);
            if (value == null) continue;
            if (current.Count < AudioReplayRules.MaxVoices) { current.Add(value); captured.Add(value.Key); }
            else Warn("Replay contains more than 128 simultaneous game sounds; extra voices are omitted.");
        }
        foreach (int id in dead)
            if (sources.TryGetValue(id, out var source)) { if (!source.Root) segments?.Forget(source.ScopeId); RemoveSource(id); }
        foreach (var started in pendingStarts)
            if (!captured.Contains(started.Key) && current.Count < AudioReplayRules.MaxVoices)
            { current.Add(started.Transient ? started : AudioReplayRules.CompletedBetweenSamples(started, now)); captured.Add(started.Key); }
        pendingStarts.Clear(); pendingOwners.Clear();
        bool same = current.Count == previous.Length;
        if (same) for (int i = 0; i < current.Count; i++) if (!ReferenceEquals(current[i], previous[i])) { same = false; break; }
        return same ? previous : previous = current.ToArray();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (ReferenceEquals(AudioReplayHooks.Capture, this)) AudioReplayHooks.Capture = null;
        AudioReplayHooks.Release(); segments?.Dispose(); sources.Clear(); scopeSources.Clear(); pendingStarts.Clear(); pendingOwners.Clear(); previous = Array.Empty<AudioReplayFrame>();
    }
}

internal sealed class AudioReplayPlayback : IDisposable
{
    private sealed class Voice
    {
        public readonly GameObject Object;
        public readonly AudioSource Audio;
        public readonly AudioReplaySpatialProfile Defaults;
        public AudioReplaySpatialKey? Spatial;
        public bool Started, Paused, Finished, SuppressedTransient;
        public float Pitch = 1;
        public string ClipKey = "";
        public Voice()
        {
            Object = new GameObject("PEAK Replay - game sound only");
            Audio = Object.AddComponent<AudioSource>(); Audio.playOnAwake = false;
            Audio.ignoreListenerPause = false;
            Defaults = AudioReplaySpatialProfile.Capture(Audio);
        }
        public void Stop()
        {
            using var measurement = ReplayPerformance.Measure(ReplayStage.PlaybackAudioStop);
            if (Audio) { Audio.Stop(); Audio.clip = null; } Spatial = null; Started = Paused = Finished = SuppressedTransient = false;
        }
    }
    private readonly Dictionary<string, AudioClip> clips = new(StringComparer.Ordinal);
    private readonly HashSet<string> ambiguous = new(StringComparer.Ordinal), warned = new(StringComparer.Ordinal), wanted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Voice> voices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioReplayFrame> future = new(StringComparer.Ordinal);
    private readonly AudioSceneEventPolicy sceneEvents = new();
    private readonly AudioSeekPreparationPolicy seekPreparation = new();
    private readonly List<AudioReplayFrame> preparationVoices = new(AudioReplayRules.MaxVoices);
    private readonly Func<string, AudioSeekLoadState> preparationState;
    private readonly Action<string> preparationLoad;
    private readonly bool includeSceneEvents;
    private readonly List<string> remove = new();
    private readonly Queue<Voice> pool = new();
    private readonly Dictionary<AudioSource, bool> muted = new();
    private readonly List<AudioSource> dead = new();
    private readonly Action<string>? warning;
    private readonly Action<AudioSource> mute;
    private readonly AudioReplaySpatialCatalog spatial;
    private readonly ReplayAudioStartDiagnostics slowStarts = new(Stopwatch.Frequency);
    public ReplayAudioStartDiagnostics.Entry[] SlowStartSnapshot => slowStarts.Snapshot();
    private bool paused = true, disposed;
    private float speed = 1;
    private double previousTime = double.NaN;
    public string Diagnostic
    {
        get
        {
            var sfx = SFX_Player.instance;
            var mixer = sfx && sfx.defaultMixerGroup ? sfx.defaultMixerGroup.audioMixer : null;
            string loading = mixer && mixer.GetFloat("LoadingFade", out float value)
                ? value.ToString("F1", CultureInfo.InvariantCulture) : "unknown";
            return "audioVoices=" + voices.Count + "; nativeClips=" + clips.Count +
                "; listenerVolume=" + AudioListener.volume.ToString("F2", CultureInfo.InvariantCulture) +
                "; listenerPaused=" + AudioListener.pause + "; loadingFadeDb=" + loading;
        }
    }

    public AudioReplayPlayback(Action<string>? warning = null, bool includeSceneEvents = false)
    {
        if (AudioReplayHooks.Playback != null) throw new InvalidOperationException("Only one sound replay may be active.");
        this.warning = warning; this.includeSceneEvents = includeSceneEvents;
        preparationState = PreparationState; preparationLoad = PrepareClip;
        mute = MuteOriginal; spatial = new AudioReplaySpatialCatalog(Warn);
        AudioReplayHooks.Acquire(warning); AudioReplayHooks.Playback = this;
        try
        {
            RefreshCatalog(); AudioReplaySources.Pool(mute);
            for (int i = 0; i < AudioReplaySources.Count; i++) AudioReplaySources.Discover(i, mute);
        }
        catch { Dispose(); throw; }
    }
    private void Warn(string key, string message)
    { if (warned.Count < 128 && warned.Add(key)) warning?.Invoke(message); }
    private void RefreshCatalog()
    {
        clips.Clear(); ambiguous.Clear(); spatial.Clear();
        AudioReplaySources.NativeClips(clip =>
        {
            if (!clip || clip.length <= 0) return;
            string key = AudioReplayClips.Key(clip);
            if (clips.TryGetValue(key, out var old) && old != clip) { ambiguous.Add(key); clips.Remove(key); }
            else if (!ambiguous.Contains(key)) clips[key] = clip;
        }, source =>
        {
            if (source.clip) spatial.Register(source, AudioReplayClips.Key(source.clip));
        }, (source, clip) => spatial.Register(source, AudioReplayClips.Key(clip)));
    }
    internal void MuteOriginal(AudioSource source)
    {
        if (!source || disposed) return;
        if (!muted.ContainsKey(source))
        {
            if (AudioReplaySources.IsVoice(source)) return;
            muted.Add(source, source.mute);
        }
        source.mute = true; // Restore only this flag; never stop/restart original gameplay audio.
    }
    public void SetClock(bool paused, float speed)
    {
        using var measurement = ReplayPerformance.Measure(ReplayStage.PlaybackAudioClock);
        this.paused = paused || float.IsNaN(speed) || float.IsInfinity(speed) || speed <= 0;
        this.speed = this.paused ? 1 : Mathf.Clamp(speed, .01f, 8);
        foreach (var voice in voices.Values)
        {
            if (!voice.Audio) continue;
            voice.Audio.pitch = AudioReplayRules.PlaybackPitch(voice.Pitch, this.speed);
            if (this.paused && voice.Started && !voice.Paused) { voice.Audio.Pause(); voice.Paused = true; }
        }
    }
    public bool PrepareSeek(AudioReplayFrame[] left, AudioReplayFrame[] right, double time,
        double sceneLeftTime, double sceneRightTime, int seekSequence)
    {
        using var measurement = ReplayPerformance.Measure(ReplayStage.PlaybackAudioPrepare);
        if (disposed) return true;
        var admitted = sceneEvents.Select(left, right, time, sceneLeftTime, sceneRightTime, includeSceneEvents);
        preparationVoices.Clear();
        foreach (var voice in admitted)
            if (AudioSeekPreparationPolicy.ShouldPrepare(voice, time)) preparationVoices.Add(voice);
        bool ready = seekPreparation.TryPrepare(seekSequence, Time.frameCount, Time.realtimeSinceStartupAsDouble,
            preparationVoices, preparationState, preparationLoad);
        if (seekPreparation.TimedOutClip.Length != 0)
            Warn(seekPreparation.TimedOutClip + ":prepare-timeout", "A native game sound is still loading after five seconds; playback will use its native start behavior.");
        return ready;
    }
    private AudioSeekLoadState PreparationState(string key)
    {
        if (!clips.TryGetValue(key, out var clip) || !clip || ambiguous.Contains(key)) return AudioSeekLoadState.Unavailable;
        try { return (AudioSeekLoadState)(int)clip.loadState; }
        catch (Exception e)
        { Warn(key + ":prepare", "A native game sound cannot report preparation state: " + e.GetType().Name); return AudioSeekLoadState.Unavailable; }
    }
    private void PrepareClip(string key)
    {
        if (!clips.TryGetValue(key, out var clip) || !clip || ambiguous.Contains(key)) return;
        // Keep Unity APIs on the main thread. Do not change the shared native
        // clip's import flags or preload the unrelated catalog of game sounds.
        try
        {
            if (!clip.LoadAudioData())
                Warn(key + ":prepare", "A native game sound could not be prepared; playback will use its native start behavior.");
        }
        catch (Exception e)
        { Warn(key + ":prepare", "A native game sound could not be prepared: " + e.GetType().Name); }
    }
    public void Apply(AudioReplayFrame[] left, AudioReplayFrame[] right, float mix, double time, bool discontinuity,
        double sceneLeftTime = double.NaN, double sceneRightTime = double.NaN)
    {
        using var measurement = ReplayPerformance.Measure(ReplayStage.PlaybackAudioApply);
        if (disposed) return;
        bool advancing = double.IsNaN(previousTime) || time > previousTime + .000001;
        bool sound = AudioReplayRules.ShouldSound(paused, speed, discontinuity) && advancing;
        previousTime = time;
        future.Clear(); wanted.Clear(); remove.Clear();
        foreach (var frame in right) future[frame.Key] = frame;
        var admitted = sceneEvents.Select(left, right, time, sceneLeftTime, sceneRightTime, includeSceneEvents);
        for (int voiceIndex = 0; voiceIndex < admitted.Count; voiceIndex++)
        {
            var frame = admitted[voiceIndex];
            wanted.Add(frame.Key);
            if (voices.TryGetValue(frame.Key, out var changed) && changed.ClipKey != frame.Clip)
            { voices.Remove(frame.Key); changed.Stop(); pool.Enqueue(changed); }
            if (!voices.TryGetValue(frame.Key, out var voice))
            {
                if (voices.Count >= AudioReplayRules.MaxVoices) break;
                if (!clips.TryGetValue(frame.Clip, out var native) || !native || ambiguous.Contains(frame.Clip))
                { Warn(frame.Clip, "A recorded game sound is missing or ambiguous in this game build; omitted."); continue; }
                voice = pool.Count > 0 ? pool.Dequeue() : new Voice();
                voice.Stop(); voice.Audio.clip = native; voice.ClipKey = frame.Clip;
                voices.Add(frame.Key, voice);
            }
            if (!voice.Audio) continue;
            var source = voice.Audio;
            var position = new Vector3(frame.Position[0], frame.Position[1], frame.Position[2]);
            float volume = frame.Volume, pitch = frame.Pitch;
            if (future.TryGetValue(frame.Key, out var next) && next.Clip == frame.Clip)
            {
                position = Vector3.Lerp(position, new Vector3(next.Position[0], next.Position[1], next.Position[2]), mix);
                volume = Mathf.Lerp(volume, next.Volume, mix); pitch = Mathf.Lerp(pitch, next.Pitch, mix);
            }
            var spatialKey = new AudioReplaySpatialKey(frame);
            if (!voice.Spatial.HasValue || !voice.Spatial.Value.Equals(spatialKey))
            {
                var sfx = SFX_Player.instance;
                // Preserve native Master/SFX mixer settings. Listener.volume is
                // the scaled-time cutscene fade; bypass only that frozen fade,
                // while Listener.pause remains respected by the voice.
                spatial.Apply(source, frame, voice.Defaults, sfx ? sfx.defaultMixerGroup : null);
                voice.Spatial = spatialKey;
            }
            source.transform.position = position; source.volume = volume;
            source.loop = frame.Loop; source.dopplerLevel = frame.Doppler;
            voice.Pitch = pitch; source.pitch = AudioReplayRules.PlaybackPitch(pitch, speed);
            double cursor = AudioReplayRules.Cursor(frame, time);
            if (discontinuity)
            { source.Stop(); voice.Started = voice.Paused = voice.Finished = false; voice.SuppressedTransient = frame.Transient; }
            if (!sound)
            {
                if (voice.Started && !voice.Paused) { source.Pause(); voice.Paused = true; }
                // A discontinuity still keeps a voice for transient suppression,
                // but its native clip must not be sought when it cannot sound at
                // this cursor. Avoid touching unloaded clips for future births
                // and already-ended one-shots as well.
                if (!(frame.Transient && voice.SuppressedTransient) &&
                    time >= frame.StartedAt &&
                    (frame.Loop || cursor < frame.Duration - .001))
                    Seek(source, cursor, frame.Clip);
                continue;
            }
            bool shortStart = AudioReplayRules.MayStartTransient(frame.Transient, voice.SuppressedTransient, voice.Started);
            if (frame.Transient && voice.SuppressedTransient) continue;
            if (!shortStart && !frame.Loop && (cursor >= frame.Duration - .001 || voice.Finished)) continue;
            if (voice.Started && !voice.Paused && !source.isPlaying && !frame.Loop)
            { voice.Finished = true; continue; }
            if (!voice.Started)
            {
                Seek(source, shortStart ? frame.Offset : cursor, frame.Clip);
                StartNativeAudio(source, frame, time, false);
                voice.Started = true;
                if (shortStart) source.SetScheduledEndTime(AudioSettings.dspTime + Math.Max(.001, frame.EndTime - frame.StartedAt) / speed);
            }
            else if (voice.Paused)
            {
                Seek(source, cursor, frame.Clip);
                StartNativeAudio(source, frame, time, true);
                voice.Paused = false;
            }
            else if (AudioReplayCapture.Distance((double)source.timeSamples / source.clip.frequency, cursor, frame.Duration, frame.Loop) > .15)
                Seek(source, cursor, frame.Clip);
        }
        foreach (var pair in voices) if (!wanted.Contains(pair.Key)) remove.Add(pair.Key);
        foreach (string key in remove)
        {
            var voice = voices[key]; voices.Remove(key); voice.Stop();
            if (pool.Count < AudioReplayRules.MaxVoices) pool.Enqueue(voice); else Object.Destroy(voice.Object);
        }
    }
    private void StartNativeAudio(AudioSource source, AudioReplayFrame frame, double replaySeconds, bool unPause)
    {
        if (!ReplayPerformance.Enabled)
        { if (unPause) source.UnPause(); else source.Play(); return; }

        AudioClip? clip = null;
        ReplayAudioStartDiagnostics.ClipMetadata before = default;
        int startFrame = -1;
        double calibratedRealtime = 0;
        long calibratedTicks = 0;
        bool observe = false;
        try
        {
            clip = source.clip;
            before = ReadClipMetadata(clip);
            startFrame = Time.frameCount;
            calibratedRealtime = Time.realtimeSinceStartupAsDouble;
            calibratedTicks = Stopwatch.GetTimestamp();
            observe = true;
        }
        catch (Exception) { } // Diagnostics must not prevent the native call.
        long startedTicks = 0, endedTicks = 0;
        int globalCollections = 0;
        try
        {
            using var measurement = ReplayPerformance.Measure(ReplayStage.PlaybackAudioStart);
            int collections = 0;
            if (observe)
            {
                try { collections = GC.CollectionCount(GC.MaxGeneration); startedTicks = Stopwatch.GetTimestamp(); }
                catch (Exception) { observe = false; }
            }
            try
            { if (unPause) source.UnPause(); else source.Play(); }
            finally
            {
                if (observe)
                {
                    try
                    {
                        endedTicks = Stopwatch.GetTimestamp();
                        globalCollections = GC.CollectionCount(GC.MaxGeneration) - collections;
                    }
                    catch (Exception) { observe = false; }
                }
            }
        }
        finally
        {
            if (observe)
            {
                try
                {
                    int endFrame = Time.frameCount;
                    var after = ReadClipMetadata(clip);
                    if (ReplayPerformance.Enabled)
                        slowStarts.Observe(frame.Clip, frame.Key, unPause, replaySeconds, startedTicks, endedTicks,
                            calibratedRealtime + (startedTicks - calibratedTicks) / (double)Stopwatch.Frequency,
                            startFrame, endFrame, globalCollections, before, after);
                }
                catch (Exception) { } // Preserve a native exception, if any.
            }
        }
    }
    private static ReplayAudioStartDiagnostics.ClipMetadata ReadClipMetadata(AudioClip? clip)
    {
        // A diagnostic getter must never suppress or replace the native call.
        // Destroyed/unavailable clips are reported explicitly without logging.
        try
        {
            if (clip is null || !clip) return default;
            return new ReplayAudioStartDiagnostics.ClipMetadata((int)clip.loadState, (int)clip.loadType,
                clip.loadInBackground, clip.preloadAudioData);
        }
        catch (Exception) { return default; }
    }
    private void Seek(AudioSource source, double seconds, string key)
    {
        using var measurement = ReplayPerformance.Measure(ReplayStage.PlaybackAudioSeek);
        try
        {
            if (source.clip) source.timeSamples = (int)Math.Min(source.clip.samples - 1, Math.Max(0, seconds * source.clip.frequency));
        }
        catch (Exception e) { Warn(key + ":seek", "A native game sound cannot seek precisely: " + e.GetType().Name); }
    }
    public void Enforce()
    {
        using var measurement = ReplayPerformance.Measure(ReplayStage.PlaybackAudioEnforce);
        if (disposed) return;
        AudioReplaySources.Pool(mute);
        dead.Clear();
        foreach (var pair in muted)
        {
            if (pair.Key) { if (!pair.Key.mute) pair.Key.mute = true; }
            // Dictionary keys cannot be CLR-null; Unity's destroyed-object false
            // still has the original managed key needed to remove this entry.
            else dead.Add(pair.Key!);
        }
        foreach (var source in dead) muted.Remove(source);
        if (paused) foreach (var voice in voices.Values)
            if (voice.Audio && voice.Started && !voice.Paused) { voice.Audio.Pause(); voice.Paused = true; }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (ReferenceEquals(AudioReplayHooks.Playback, this)) AudioReplayHooks.Playback = null;
        AudioReplayHooks.Release();
        foreach (var voice in voices.Values) if (voice.Object) Object.Destroy(voice.Object);
        foreach (var voice in pool) if (voice.Object) Object.Destroy(voice.Object);
        foreach (var pair in muted) if (pair.Key) pair.Key.mute = pair.Value;
        voices.Clear(); pool.Clear(); muted.Clear(); clips.Clear(); spatial.Clear();
    }
}
