using System;
using System.Collections.Generic;
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
            "SFX_PlayOneShot", "Lantern", "ScoutCannon", "Peak.RocketFX" };
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
    public static void Pool(Action<AudioSource> found)
    {
        var player = SFX_Player.instance;
        if (!player || player.sources == null) return;
        foreach (var item in player.sources) if (item != null && item.source) found(item.source);
    }
    public static void NativeClips(Action<AudioClip> found, Action<AudioSource>? foundSource = null)
    {
        // A broad AudioClip scan would also expose runtime microphone/voice buffers
        // to a forged replay resource key. Resolve ONLY audited native owners/SFX.
        foreach (var sfx in Resources.FindObjectsOfTypeAll<SFX_Instance>())
            if (sfx && sfx.clips != null) foreach (var clip in sfx.clips) if (clip) found(clip);
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
    private static int users;
    internal static AudioReplayCapture? Capture;
    internal static AudioReplayPlayback? Playback;
    private static readonly Action<AudioSource> foundOwnerSource = ObserveOwnerSource;
    public static void Acquire(Action<string>? warning)
    {
        if (users++ != 0) return;
        harmony = new Harmony("cn.mylus.peakreplaylab.audio-observation");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(SFX_Player.SFX_Source), nameof(SFX_Player.SFX_Source.StartPlaying)),
                prefix: new HarmonyMethod(typeof(AudioReplayHooks), nameof(BeforeStart)),
                postfix: new HarmonyMethod(typeof(AudioReplayHooks), nameof(AfterStart)));
            harmony.Patch(AccessTools.Method(typeof(SFX_Instance), nameof(SFX_Instance.PlayFromSource)),
                postfix: new HarmonyMethod(typeof(AudioReplayHooks), nameof(AfterDirect)));
            harmony.Patch(AccessTools.Method(typeof(Peak.Action_Antizooka), "PlayChargeFX"),
                postfix: new HarmonyMethod(typeof(AudioReplayHooks), nameof(AfterCharge)));
            harmony.Patch(AccessTools.Method(typeof(Peak.Jetpack), nameof(Peak.Jetpack.ActivateJetpack)),
                prefix: new HarmonyMethod(typeof(AudioReplayHooks), nameof(BeforeJetpack)),
                postfix: new HarmonyMethod(typeof(AudioReplayHooks), nameof(AfterJetpack)));
            // Registration only: never run gameplay methods to reproduce sound.
            foreach (var entry in new[] { (typeof(Peak.RocketFX), "RocketLight"), (typeof(Peak.RocketFX), "RocketFire") })
                harmony.Patch(AccessTools.Method(entry.Item1, entry.Item2),
                    postfix: new HarmonyMethod(typeof(AudioReplayHooks), nameof(AfterOwner)));
            foreach (var method in AudioReplaySources.LifecycleMethods())
                try { harmony.Patch(method, postfix: new HarmonyMethod(typeof(AudioReplayHooks), nameof(AfterOwner))); }
                catch (Exception e) { warning?.Invoke("Native audio owner lifecycle unavailable: " + method.DeclaringType?.Name + "." + method.Name + " · " + e.GetType().Name); }
        }
        catch (Exception e)
        {
            harmony.UnpatchSelf();
            warning?.Invoke("Game sound start hooks unavailable; observed native sources remain best-effort: " + e.GetType().Name);
        }
    }
    private static void BeforeStart(SFX_Player.SFX_Source __instance, out bool __state) => __state = !__instance.isPlaying;
    private static void AfterStart(SFX_Player.SFX_Source __instance, bool __state)
    { if (__state && __instance.isPlaying && __instance.source) Observe(__instance.source); }
    private static void AfterDirect(AudioSource source) { if (source) Observe(source); }
    private static void AfterCharge(Peak.Action_Antizooka __instance) { if (__instance.chargeSFX) Observe(__instance.chargeSFX); }
    // Repeated held jetpack input refreshes its timer; it must not create a new
    // recorded voice unless native code actually executes AudioSource.Play().
    private static void BeforeJetpack(bool ____active, out bool __state) => __state = !____active;
    private static void AfterJetpack(Peak.Jetpack __instance, bool __state)
    { if (__state && __instance.jetpackLoop) Observe(__instance.jetpackLoop); }
    private static void AfterOwner(Component __instance)
    {
        try { AudioReplaySources.NativeOwner(__instance, foundOwnerSource); }
        catch { /* A native prefab may be in the middle of being destroyed. */ }
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
    }
}

internal static class AudioReplayClips
{
    // This is metadata only; no GetData, Microphone APIs, PCM buffers, or file export.
    public static string Key(AudioClip clip) => Uri.EscapeDataString(clip.name) + "|" +
        clip.samples.ToString(CultureInfo.InvariantCulture) + "|" + clip.frequency.ToString(CultureInfo.InvariantCulture) +
        "|" + clip.channels.ToString(CultureInfo.InvariantCulture);
}

internal sealed class AudioReplayCapture : IDisposable
{
    private sealed class Source
    {
        public readonly AudioSource Audio;
        public AudioClip? Clip;
        public string ClipKey = "", Key = "";
        public bool Playing;
        public double Started, NextRead;
        public AudioReplayFrame? Last;
        public Source(AudioSource audio) { Audio = audio; }
    }
    private const int MaxRegistered = 512;
    private readonly Dictionary<int, Source> sources = new();
    private readonly List<int> dead = new();
    private readonly List<AudioReplayFrame> current = new();
    private readonly List<AudioReplayFrame> pendingStarts = new();
    private readonly HashSet<string> captured = new(StringComparer.Ordinal);
    private readonly Action<string>? warning;
    private readonly Action<AudioSource> register;
    private AudioReplayFrame[] previous = Array.Empty<AudioReplayFrame>();
    private long sequence;
    private double nextPool;
    private bool disposed, warned;

    public AudioReplayCapture(Action<string>? warning = null)
    {
        if (AudioReplayHooks.Capture != null) throw new InvalidOperationException("Only one sound capture may be active.");
        this.warning = warning; register = Register;
        AudioReplayHooks.Acquire(warning); AudioReplayHooks.Capture = this;
        try
        {
            for (int i = 0; i < AudioReplaySources.Count; i++) AudioReplaySources.Discover(i, register);
            AudioReplaySources.Pool(register);
        }
        catch { Dispose(); throw; }
    }
    private void Warn(string message) { if (!warned) { warned = true; warning?.Invoke(message); } }
    private void Register(AudioSource source)
    {
        if (!source || sources.ContainsKey(source.GetInstanceID())) return;
        if (AudioReplaySources.IsVoice(source)) return;
        if (sources.Count >= MaxRegistered) { Warn("Game sound source limit reached; extra sources are omitted."); return; }
        sources.Add(source.GetInstanceID(), new Source(source));
    }
    internal void Started(AudioSource source)
    {
        if (disposed || ReplaySafety.Active) return;
        Register(source);
        if (!sources.TryGetValue(source.GetInstanceID(), out var state)) return;
        state.Playing = false; // A pooled source playing the same clip is still a NEW voice.
        var frame = Observe(state, Time.timeAsDouble, true);
        if (frame != null && !frame.Loop && pendingStarts.Count < 256) pendingStarts.Add(frame);
    }
    internal void Discovered(AudioSource source)
    {
        if (disposed || ReplaySafety.Active) return;
        Register(source);
        if (!sources.TryGetValue(source.GetInstanceID(), out var state)) return;
        bool wasPlaying = state.Playing;
        var frame = Observe(state, Time.timeAsDouble);
        if (!wasPlaying && frame != null && !frame.Loop && pendingStarts.Count < 256) pendingStarts.Add(frame);
    }
    private AudioReplayFrame? Observe(Source source, double now, bool force = false)
    {
        AudioSource audio = source.Audio;
        if (!audio || !audio.enabled || !audio.gameObject.activeInHierarchy || !audio.isPlaying || !audio.clip)
        { source.Playing = false; source.Last = null; return null; }
        var clip = audio.clip;
        if (clip.length <= 0 || clip.length > 86400 || clip.frequency <= 0) return null;
        bool fresh = !source.Playing || source.Clip != clip;
        if (fresh)
        {
            source.Clip = clip; source.ClipKey = AudioReplayClips.Key(clip);
            if (source.ClipKey.Length > 1024) return null;
            source.Key = "sound:" + (++sequence).ToString(CultureInfo.InvariantCulture);
            source.Started = now; source.Playing = true; force = true;
        }
        if (!force && now < source.NextRead) return source.Last;
        source.NextRead = now + .05; // Audio metadata/position at 20 Hz, not a whole-scene 60 Hz scan.
        Vector3 p = audio.transform.position;
        double offset = Math.Max(0, (double)audio.timeSamples / clip.frequency);
        float volume = audio.mute ? 0 : audio.volume;
        var old = source.Last;
        if (!force && old != null && old.Position[0] == p.x && old.Position[1] == p.y && old.Position[2] == p.z &&
            old.Volume == volume && old.Pitch == audio.pitch && old.Loop == audio.loop &&
            old.SpatialBlend == audio.spatialBlend && old.MinDistance == audio.minDistance && old.MaxDistance == audio.maxDistance &&
            old.Doppler == audio.dopplerLevel && old.Rolloff == (int)audio.rolloffMode &&
            Distance(AudioReplayRules.Cursor(old, now), offset, clip.length, audio.loop) < .04) return old;
        var value = new AudioReplayFrame
        {
            Key = source.Key, Clip = source.ClipKey, StartedAt = source.Started, AnchorTime = now, Offset = offset,
            Duration = clip.length, Position = new[] { p.x, p.y, p.z }, Volume = volume, Pitch = audio.pitch,
            Loop = audio.loop, SpatialBlend = audio.spatialBlend, MinDistance = audio.minDistance,
            MaxDistance = audio.maxDistance, Doppler = audio.dopplerLevel, Rolloff = (int)audio.rolloffMode,
        };
        try { AudioReplayRules.Validate(value); }
        catch { Warn("A native game sound had unsupported metadata and was omitted."); return null; }
        return source.Last = value;
    }
    internal static double Distance(double a, double b, double duration, bool loop)
    { double difference = Math.Abs(a - b); return loop ? Math.Min(difference, Math.Abs(duration - difference)) : difference; }
    public AudioReplayFrame[] Capture(double now)
    {
        if (disposed || ReplaySafety.Active) return Array.Empty<AudioReplayFrame>();
        if (now >= nextPool) { nextPool = now + .25; AudioReplaySources.Pool(register); }
        dead.Clear(); current.Clear(); captured.Clear();
        foreach (var pair in sources)
        {
            if (!pair.Value.Audio) { dead.Add(pair.Key); continue; }
            AudioReplayFrame? value = Observe(pair.Value, now);
            if (value == null) continue;
            if (current.Count < AudioReplayRules.MaxVoices) { current.Add(value); captured.Add(value.Key); }
            else Warn("Replay contains more than 128 simultaneous game sounds; extra voices are omitted.");
        }
        foreach (var started in pendingStarts)
            if (!captured.Contains(started.Key) && current.Count < AudioReplayRules.MaxVoices)
            { current.Add(AudioReplayRules.CompletedBetweenSamples(started, now)); captured.Add(started.Key); }
        pendingStarts.Clear();
        foreach (int id in dead) sources.Remove(id);
        bool same = current.Count == previous.Length;
        if (same) for (int i = 0; i < current.Count; i++) if (!ReferenceEquals(current[i], previous[i])) { same = false; break; }
        return same ? previous : previous = current.ToArray();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (ReferenceEquals(AudioReplayHooks.Capture, this)) AudioReplayHooks.Capture = null;
        AudioReplayHooks.Release(); sources.Clear(); pendingStarts.Clear(); previous = Array.Empty<AudioReplayFrame>();
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
        public void Stop() { if (Audio) { Audio.Stop(); Audio.clip = null; } Spatial = null; Started = Paused = Finished = SuppressedTransient = false; }
    }
    private readonly Dictionary<string, AudioClip> clips = new(StringComparer.Ordinal);
    private readonly HashSet<string> ambiguous = new(StringComparer.Ordinal), warned = new(StringComparer.Ordinal), wanted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Voice> voices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioReplayFrame> future = new(StringComparer.Ordinal);
    private readonly List<string> remove = new();
    private readonly Queue<Voice> pool = new();
    private readonly Dictionary<AudioSource, bool> muted = new();
    private readonly List<AudioSource> dead = new();
    private readonly Action<string>? warning;
    private readonly Action<AudioSource> mute;
    private readonly AudioReplaySpatialCatalog spatial;
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

    public AudioReplayPlayback(Action<string>? warning = null)
    {
        if (AudioReplayHooks.Playback != null) throw new InvalidOperationException("Only one sound replay may be active.");
        this.warning = warning; mute = MuteOriginal; spatial = new AudioReplaySpatialCatalog(Warn);
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
        });
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
        this.paused = paused || float.IsNaN(speed) || float.IsInfinity(speed) || speed <= 0;
        this.speed = this.paused ? 1 : Mathf.Clamp(speed, .01f, 8);
        foreach (var voice in voices.Values)
        {
            if (!voice.Audio) continue;
            voice.Audio.pitch = Mathf.Clamp(voice.Pitch * this.speed, -3, 3);
            if (this.paused && voice.Started && !voice.Paused) { voice.Audio.Pause(); voice.Paused = true; }
        }
    }
    public void Apply(AudioReplayFrame[] left, AudioReplayFrame[] right, float mix, double time, bool discontinuity)
    {
        if (disposed) return;
        bool advancing = double.IsNaN(previousTime) || time > previousTime + .000001;
        bool sound = AudioReplayRules.ShouldSound(paused, speed, discontinuity) && advancing;
        previousTime = time;
        future.Clear(); wanted.Clear(); remove.Clear();
        foreach (var frame in right) future[frame.Key] = frame;
        foreach (var frame in left)
        {
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
            voice.Pitch = pitch; source.pitch = Mathf.Clamp(pitch * speed, -3, 3);
            double cursor = AudioReplayRules.Cursor(frame, time);
            if (discontinuity)
            { source.Stop(); voice.Started = voice.Paused = voice.Finished = false; voice.SuppressedTransient = frame.Transient; }
            if (!sound)
            {
                if (voice.Started && !voice.Paused) { source.Pause(); voice.Paused = true; }
                Seek(source, cursor, frame.Clip); continue;
            }
            bool shortStart = AudioReplayRules.MayStartTransient(frame.Transient, voice.SuppressedTransient, voice.Started);
            if (frame.Transient && voice.SuppressedTransient) continue;
            if (!shortStart && !frame.Loop && (cursor >= frame.Duration - .001 || voice.Finished)) continue;
            if (voice.Started && !voice.Paused && !source.isPlaying && !frame.Loop)
            { voice.Finished = true; continue; }
            if (!voice.Started)
            {
                Seek(source, shortStart ? frame.Offset : cursor, frame.Clip); source.Play(); voice.Started = true;
                if (shortStart) source.SetScheduledEndTime(AudioSettings.dspTime + Math.Max(.001, frame.EndTime - frame.StartedAt) / speed);
            }
            else if (voice.Paused)
            { Seek(source, cursor, frame.Clip); source.UnPause(); voice.Paused = false; }
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
    private void Seek(AudioSource source, double seconds, string key)
    {
        try
        {
            if (source.clip) source.timeSamples = (int)Math.Min(source.clip.samples - 1, Math.Max(0, seconds * source.clip.frequency));
        }
        catch (Exception e) { Warn(key + ":seek", "A native game sound cannot seek precisely: " + e.GetType().Name); }
    }
    public void Enforce()
    {
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
