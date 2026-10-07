using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace PeakReplayLab;

// Copies authored native source settings, never audio samples. Custom rolloff is
// normalized to maxDistance in Unity; keeping only its enum loses the actual
// distance envelope (notably both BeeSwarm loops).
internal sealed class AudioReplaySpatialProfile
{
    private static readonly AudioSourceCurveType[] kinds = {
        AudioSourceCurveType.CustomRolloff, AudioSourceCurveType.SpatialBlend,
        AudioSourceCurveType.Spread, AudioSourceCurveType.ReverbZoneMix,
    };
    private sealed class Curve
    {
        private readonly Keyframe[] keys;
        private readonly WrapMode before, after;
        public Curve(AnimationCurve value)
        { keys = value.keys; before = value.preWrapMode; after = value.postWrapMode; }
        public AnimationCurve Copy() => new(keys) { preWrapMode = before, postWrapMode = after };
        public bool Same(Curve other)
        {
            if (before != other.before || after != other.after || keys.Length != other.keys.Length) return false;
            for (int i = 0; i < keys.Length; i++)
            {
                var a = keys[i]; var b = other.keys[i];
                if (a.time != b.time || a.value != b.value || a.inTangent != b.inTangent || a.outTangent != b.outTangent ||
                    a.inWeight != b.inWeight || a.outWeight != b.outWeight || a.weightedMode != b.weightedMode) return false;
            }
            return true;
        }
    }
    private readonly Curve[] curves = new Curve[kinds.Length];
    public readonly bool HasCustomRolloff;
    private readonly AudioMixerGroup? mixer;
    private readonly float pan, spread, reverb;
    private readonly bool spatialize, spatializePost, bypassEffects, bypassListenerEffects, bypassReverb;
    private AudioReplaySpatialProfile(AudioSource source)
    {
        for (int i = 0; i < kinds.Length; i++)
        {
            var curve = source.GetCustomCurve(kinds[i]);
            bool authored = curve != null && curve.length > 0;
            if (i == 0) HasCustomRolloff = authored;
            // Unity assets may omit optional curves. Explicit flat defaults
            // clear a pooled voice's previous curve without passing null to
            // Unity's native API. Log/Linear modes ignore the fallback rolloff.
            float flat = i == 1 ? source.spatialBlend : i == 2 ? source.spread : i == 3 ? source.reverbZoneMix : 1;
            curves[i] = new Curve(authored ? curve! : Flat(flat));
        }
        mixer = source.outputAudioMixerGroup; pan = source.panStereo; spread = source.spread; reverb = source.reverbZoneMix;
        spatialize = source.spatialize; spatializePost = source.spatializePostEffects;
        bypassEffects = source.bypassEffects; bypassListenerEffects = source.bypassListenerEffects; bypassReverb = source.bypassReverbZones;
    }
    public static AudioReplaySpatialProfile Capture(AudioSource source) => new(source);
    internal static AnimationCurve Flat(float value) => new(new Keyframe(0, value), new Keyframe(1, value))
        { preWrapMode = WrapMode.ClampForever, postWrapMode = WrapMode.ClampForever };
    public bool Same(AudioReplaySpatialProfile other)
    {
        if (mixer != other.mixer || pan != other.pan || spread != other.spread || reverb != other.reverb ||
            spatialize != other.spatialize || spatializePost != other.spatializePost || bypassEffects != other.bypassEffects ||
            bypassListenerEffects != other.bypassListenerEffects || bypassReverb != other.bypassReverb) return false;
        for (int i = 0; i < curves.Length; i++)
        {
            if (!curves[i].Same(other.curves[i])) return false;
        }
        return true;
    }
    public void Apply(AudioSource target, AudioMixerGroup? fallbackMixer)
    {
        target.outputAudioMixerGroup = mixer ? mixer : fallbackMixer;
        target.panStereo = pan; target.spread = spread; target.reverbZoneMix = reverb;
        target.spatialize = spatialize; target.spatializePostEffects = spatializePost;
        target.bypassEffects = bypassEffects; target.bypassListenerEffects = bypassListenerEffects; target.bypassReverbZones = bypassReverb;
        for (int i = 0; i < kinds.Length; i++) target.SetCustomCurve(kinds[i], curves[i].Copy());
    }
}

internal sealed class AudioReplaySpatialCatalog
{
    private readonly Dictionary<AudioReplaySpatialKey, AudioReplaySpatialProfile> profiles = new();
    private readonly HashSet<AudioReplaySpatialKey> ambiguous = new();
    private readonly Dictionary<AudioSource, AudioReplaySpatialKey> registered = new();
    private readonly HashSet<string> warned = new(StringComparer.Ordinal);
    private readonly Action<string, string>? warning;
    public AudioReplaySpatialCatalog(Action<string, string>? warning = null) { this.warning = warning; }
    public void Register(AudioSource source, string clipKey)
    {
        var key = new AudioReplaySpatialKey(clipKey, source.spatialBlend, source.minDistance, source.maxDistance, (int)source.rolloffMode);
        if (registered.TryGetValue(source, out var previous) && previous.Equals(key)) return;
        registered[source] = key;
        if (ambiguous.Contains(key)) return;
        var profile = AudioReplaySpatialProfile.Capture(source);
        if (key.RequiresNativeRolloff && !profile.HasCustomRolloff) return;
        if (profiles.TryGetValue(key, out var old))
        {
            if (old.Same(profile)) return;
            profiles.Remove(key); ambiguous.Add(key);
        }
        else profiles[key] = profile;
    }
    // Called once when a voice is assigned a new clip/spatial configuration,
    // never on every replay tick. No scene/resource scan happens here.
    public bool Apply(AudioSource target, AudioReplayFrame frame, AudioReplaySpatialProfile defaults, AudioMixerGroup? fallbackMixer)
    {
        var key = new AudioReplaySpatialKey(frame);
        bool matched = profiles.TryGetValue(key, out var profile);
        defaults.Apply(target, fallbackMixer);
        target.minDistance = frame.MinDistance; target.maxDistance = frame.MaxDistance;
        // Authored blend curves override the scalar at distance. Assign the
        // recorded scalar first, then restore the native curve when matched.
        target.spatialBlend = frame.SpatialBlend;
        if (matched) profile!.Apply(target, fallbackMixer);
        else target.SetCustomCurve(AudioSourceCurveType.SpatialBlend, AudioReplaySpatialProfile.Flat(frame.SpatialBlend));
        target.rolloffMode = (AudioRolloffMode)(matched ? frame.Rolloff : key.MissingCurveFallbackRolloff);
        target.ignoreListenerVolume = target.outputAudioMixerGroup != null;
        if (!matched && key.RequiresNativeRolloff && warned.Count < 128 && warned.Add(frame.Clip))
            warning?.Invoke(frame.Clip + ":native-curve", ambiguous.Contains(key)
                ? "A recorded game sound has ambiguous native distance curves; using recorded distances with linear falloff."
                : "A recorded game sound's native distance curve is unavailable in this game build; using recorded distances with linear falloff.");
        return matched;
    }
    public void Clear() { profiles.Clear(); ambiguous.Clear(); registered.Clear(); warned.Clear(); }
}
