// Small storage doubles exercise the production spatial helper. These tests do
// not simulate Unity's audio engine, audible attenuation or mixer DSP.
namespace UnityEngine
{
    public class Object
    {
        public string name { get; set; } = "";
        public static implicit operator bool(Object? value) => value != null;
        public int GetInstanceID() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }
    public enum AudioRolloffMode { Logarithmic, Linear, Custom }
    public enum AudioSourceCurveType { CustomRolloff, SpatialBlend, ReverbZoneMix, Spread }
    public enum AudioVelocityUpdateMode { Auto, Fixed, Dynamic }
    public enum WrapMode { Default = 0, Once = 1, Clamp = 1, Loop = 2, PingPong = 4, ClampForever = 8 }
    [Flags] public enum WeightedMode { None = 0, In = 1, Out = 2, Both = 3 }
    public struct Keyframe
    {
        public float time, value, inTangent, outTangent, inWeight, outWeight;
        public WeightedMode weightedMode;
        public Keyframe(float time, float value, float inTangent = 0, float outTangent = 0)
        {
            this.time = time; this.value = value; this.inTangent = inTangent; this.outTangent = outTangent;
            inWeight = outWeight = 1f / 3; weightedMode = WeightedMode.None;
        }
        public Keyframe(float time, float value, float inTangent, float outTangent, float inWeight, float outWeight)
            : this(time, value, inTangent, outTangent)
        { this.inWeight = inWeight; this.outWeight = outWeight; weightedMode = WeightedMode.Both; }
    }
    public sealed class AnimationCurve
    {
        private Keyframe[] values;
        public AnimationCurve(params Keyframe[] keys) => values = keys.ToArray();
        public Keyframe[] keys { get => values.ToArray(); set => values = value.ToArray(); }
        public int length => values.Length;
        public WrapMode preWrapMode { get; set; }
        public WrapMode postWrapMode { get; set; }
        public Keyframe this[int index] => values[index];
        public static AnimationCurve Linear(float fromTime, float fromValue, float toTime, float toValue)
        {
            float slope = (toValue - fromValue) / (toTime - fromTime);
            return new(new Keyframe(fromTime, fromValue, slope, slope), new Keyframe(toTime, toValue, slope, slope));
        }
    }
    public sealed class AudioClip : Object { }
    public sealed class AudioSource : Object
    {
        private readonly Dictionary<AudioSourceCurveType, AnimationCurve?> curves = new();
        public AnimationCurve? GetCustomCurve(AudioSourceCurveType kind) => curves.TryGetValue(kind, out var curve) ? curve : null;
        public void SetCustomCurve(AudioSourceCurveType kind, AnimationCurve? curve) => curves[kind] = curve;
        public Audio.AudioMixerGroup? outputAudioMixerGroup { get; set; }
        public AudioClip? clip { get; set; }
        private float blend;
        public float spatialBlend
        {
            get => blend;
            set { blend = value; curves[AudioSourceCurveType.SpatialBlend] = new AnimationCurve(new Keyframe(0, value)); }
        }
        public float minDistance { get; set; } = 1;
        public float maxDistance { get; set; } = 500;
        public float dopplerLevel { get; set; } = 1;
        public AudioRolloffMode rolloffMode { get; set; }
        public float reverbZoneMix { get; set; } = 1;
        public float spread { get; set; }
        public float panStereo { get; set; }
        public int priority { get; set; } = 128;
        public bool mute { get; set; }
        public bool bypassEffects { get; set; }
        public bool bypassListenerEffects { get; set; }
        public bool bypassReverbZones { get; set; }
        public bool ignoreListenerVolume { get; set; }
        public bool ignoreListenerPause { get; set; }
        public bool spatialize { get; set; }
        public bool spatializePostEffects { get; set; }
        public AudioVelocityUpdateMode velocityUpdateMode { get; set; }
    }
}
namespace UnityEngine.Audio
{
    public sealed class AudioMixerGroup : UnityEngine.Object { }
}
