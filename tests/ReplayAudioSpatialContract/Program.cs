using PeakReplayLab;
using UnityEngine;
using UnityEngine.Audio;

int passed = 0;
void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
void Check(bool value, string detail = "spatial audio contract") { if (!value) throw new Exception(detail); }
AnimationCurve Curve(float value, WrapMode before = WrapMode.Once, WrapMode after = WrapMode.ClampForever) => new(new Keyframe(0, value, -.75f, .5f, .125f, .875f) { weightedMode = WeightedMode.Both }) { preWrapMode = before, postWrapMode = after };
bool SameCurve(AnimationCurve? a, AnimationCurve? b)
{
    if (a == null || b == null) return a == b;
    if (a.preWrapMode != b.preWrapMode || a.postWrapMode != b.postWrapMode || a.length != b.length) return false;
    return a.keys.Zip(b.keys).All(pair => pair.First.time == pair.Second.time && pair.First.value == pair.Second.value &&
        pair.First.inTangent == pair.Second.inTangent && pair.First.outTangent == pair.Second.outTangent &&
        pair.First.inWeight == pair.Second.inWeight && pair.First.outWeight == pair.Second.outWeight && pair.First.weightedMode == pair.Second.weightedMode);
}
AudioSourceCurveType[] Kinds() => new[] { AudioSourceCurveType.CustomRolloff, AudioSourceCurveType.SpatialBlend, AudioSourceCurveType.Spread, AudioSourceCurveType.ReverbZoneMix };
// Exact keys/tangents/weights from installed BeeSwarm beeIdleLoop / beeAngryLoop
// in local/checks/replay-audio-0.7.3/bee-audio-assets.json. Alternate API wrap
// modes deliberately exercise preservation, independent of serialized infinity.
AnimationCurve BeeCurve()
{
    float[] times = { .005f, .01f, .02f, .04f, .08f, .16f, .32f, 1 };
    float[] values = { 1, .5f, .25f, .125f, .0625f, .03125f, .015625f, 0 };
    float[] slopes = { -200.07968139648438f, -50.019920349121094f, -12.504980087280273f, -3.1262450218200684f, -.7815612554550171f, -.19539031386375427f, -.04884757846593857f, -.005001993849873543f };
    var keys = times.Select((time, i) => new Keyframe(time, values[i], slopes[i], slopes[i]) { inWeight = 0, outWeight = 0, weightedMode = WeightedMode.None }).ToArray();
    return new AnimationCurve(keys) { preWrapMode = WrapMode.ClampForever, postWrapMode = WrapMode.Once };
}
var beeMixer = new AudioMixerGroup { name = "native bee mixer" };
AudioSource Bee()
{
    var source = new AudioSource { spatialBlend = 1, minDistance = 1, maxDistance = 250, rolloffMode = AudioRolloffMode.Custom,
        outputAudioMixerGroup = beeMixer, panStereo = -.25f, spread = 23, reverbZoneMix = .7f,
        spatialize = true, spatializePostEffects = true, bypassEffects = true, bypassListenerEffects = true, bypassReverbZones = true };
    source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, BeeCurve());
    source.SetCustomCurve(AudioSourceCurveType.SpatialBlend, Curve(.75f, WrapMode.Loop, WrapMode.PingPong));
    source.SetCustomCurve(AudioSourceCurveType.Spread, Curve(23));
    source.SetCustomCurve(AudioSourceCurveType.ReverbZoneMix, Curve(.7f));
    return source;
}
AudioSource Defaults()
{
    var source = new AudioSource();
    source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, AnimationCurve.Linear(0, 1, 1, 0));
    source.SetCustomCurve(AudioSourceCurveType.SpatialBlend, Curve(0));
    source.SetCustomCurve(AudioSourceCurveType.Spread, Curve(0));
    source.SetCustomCurve(AudioSourceCurveType.ReverbZoneMix, Curve(1));
    return source;
}
AudioReplayFrame Frame(string clip = "bee/native", AudioSource? source = null) => new()
{
    Clip = clip, SpatialBlend = source?.spatialBlend ?? 1, MinDistance = source?.minDistance ?? 1,
    MaxDistance = source?.maxDistance ?? 250, Rolloff = (int)(source?.rolloffMode ?? AudioRolloffMode.Custom),
};
void AssertNativeCurves(AudioSource source, AudioSource target)
{
    foreach (var kind in Kinds())
    {
        Check(SameCurve(source.GetCustomCurve(kind), target.GetCustomCurve(kind)), "Lost native curve fields: " + kind);
        if (source.GetCustomCurve(kind) != null) Check(!ReferenceEquals(source.GetCustomCurve(kind), target.GetCustomCurve(kind)), "Shared mutable native curve: " + kind);
    }
}

Test("native bee full rolloff keys tangents weights and four curve wraps survive catalog replay", () =>
{
    var native = Bee(); var target = new AudioSource(); var catalog = new AudioReplaySpatialCatalog(); catalog.Register(native, "bee/native");
    Check(catalog.Apply(target, Frame(), AudioReplaySpatialProfile.Capture(Defaults()), null));
    AssertNativeCurves(native, target); Check(target.GetCustomCurve(AudioSourceCurveType.CustomRolloff)!.length == 8);
    Check(target.outputAudioMixerGroup == beeMixer && target.spatialize && target.spatializePostEffects);
    Check(target.bypassEffects && target.bypassListenerEffects && target.bypassReverbZones);
    Check(target.panStereo == -.25f && target.spread == 23 && target.reverbZoneMix == .7f && target.rolloffMode == AudioRolloffMode.Custom);
});
Test("identical same clip spatial sources remain matchable without first source uniqueness", () =>
{
    var catalog = new AudioReplaySpatialCatalog(); catalog.Register(Bee(), "bee/native"); catalog.Register(Bee(), "bee/native");
    var target = new AudioSource(); Check(catalog.Apply(target, Frame(), AudioReplaySpatialProfile.Capture(Defaults()), null));
    Check(SameCurve(BeeCurve(), target.GetCustomCurve(AudioSourceCurveType.CustomRolloff)));
});
Test("conflicting native curve weight or mixer rejects the shared identity permanently", () =>
{
    foreach (bool mixerConflict in new[] { false, true })
    {
        var one = Bee(); var conflict = Bee();
        if (mixerConflict) conflict.outputAudioMixerGroup = new AudioMixerGroup();
        else
        {
            var curve = conflict.GetCustomCurve(AudioSourceCurveType.CustomRolloff)!; var keys = curve.keys;
            keys[2].inWeight = .625f; keys[2].weightedMode = WeightedMode.In; curve.keys = keys;
        }
        var messages = new List<string>(); var catalog = new AudioReplaySpatialCatalog((_, message) => messages.Add(message));
        catalog.Register(one, "bee/native"); catalog.Register(conflict, "bee/native"); catalog.Register(Bee(), "bee/native");
        var target = new AudioSource(); Check(!catalog.Apply(target, Frame(), AudioReplaySpatialProfile.Capture(Defaults()), null));
        Check(target.rolloffMode == AudioRolloffMode.Linear && messages.Count == 1 && messages[0].Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
    }
});
Test("pooled source reuse resets all four previous curves and mixer before the next voice", () =>
{
    var defaultsSource = Defaults(); var defaults = AudioReplaySpatialProfile.Capture(defaultsSource);
    var catalog = new AudioReplaySpatialCatalog(); catalog.Register(Bee(), "bee/native"); var target = new AudioSource();
    Check(catalog.Apply(target, Frame(), defaults, null));
    var fallbackMixer = new AudioMixerGroup(); var missing = Frame("missing/native");
    Check(!catalog.Apply(target, missing, defaults, fallbackMixer));
    Check(target.outputAudioMixerGroup == fallbackMixer && !target.spatialize && !target.spatializePostEffects);
    Check(!target.bypassEffects && !target.bypassListenerEffects && !target.bypassReverbZones);
    Check(target.GetCustomCurve(AudioSourceCurveType.CustomRolloff)!.length == 2);
    Check(SameCurve(defaultsSource.GetCustomCurve(AudioSourceCurveType.Spread), target.GetCustomCurve(AudioSourceCurveType.Spread)));
    Check(SameCurve(defaultsSource.GetCustomCurve(AudioSourceCurveType.ReverbZoneMix), target.GetCustomCurve(AudioSourceCurveType.ReverbZoneMix)));
    Check(target.minDistance == missing.MinDistance && target.maxDistance == missing.MaxDistance && target.spatialBlend == missing.SpatialBlend);
    catalog.Apply(target, missing, defaults, null); Check(target.outputAudioMixerGroup == null && !target.ignoreListenerVolume);
    // The next matched source lacks an optional curve; the old bee curve must
    // still disappear rather than survive because a profile skipped null.
    var next = Bee(); next.SetCustomCurve(AudioSourceCurveType.Spread, null);
    catalog.Register(next, "next/native"); catalog.Apply(target, Frame(), defaults, null);
    var oldSpread = target.GetCustomCurve(AudioSourceCurveType.Spread);
    Check(catalog.Apply(target, Frame("next/native"), defaults, null));
    Check(!SameCurve(oldSpread, target.GetCustomCurve(AudioSourceCurveType.Spread)), "Null native curve leaked pooled spread curve");
});
Test("missing custom curve uses recorded linear distances and bounded once per clip warnings", () =>
{
    var messages = new List<string>(); var catalog = new AudioReplaySpatialCatalog((key, _) => messages.Add(key));
    var defaults = AudioReplaySpatialProfile.Capture(Defaults()); var target = new AudioSource(); var frame = Frame();
    frame.MinDistance = 2.75f; frame.MaxDistance = 35;
    for (int i = 0; i < 10; i++) Check(!catalog.Apply(target, frame, defaults, null));
    Check(messages.Count == 1 && target.rolloffMode == AudioRolloffMode.Linear && target.minDistance == 2.75f && target.maxDistance == 35);
    for (int i = 0; i < 300; i++) catalog.Apply(target, Frame("missing/" + i), defaults, null);
    Check(messages.Count == 128 && messages.Distinct().Count() == 128);
    catalog.Clear(); catalog.Apply(target, frame, defaults, null); Check(messages.Count == 129);
});
Test("native source and captured catalog curves are unaffected by replay target mutation", () =>
{
    var native = Bee(); var saved = AudioReplaySpatialProfile.Capture(native); var catalog = new AudioReplaySpatialCatalog(); catalog.Register(native, "bee/native");
    var one = new AudioSource(); var two = new AudioSource(); var defaults = AudioReplaySpatialProfile.Capture(Defaults());
    catalog.Apply(one, Frame(), defaults, null);
    foreach (var kind in Kinds())
    {
        var targetCurve = one.GetCustomCurve(kind)!; var keys = targetCurve.keys; keys[0].value = -99; targetCurve.keys = keys; targetCurve.preWrapMode = WrapMode.PingPong;
    }
    one.outputAudioMixerGroup = null; one.panStereo = 1;
    Check(saved.Same(AudioReplaySpatialProfile.Capture(native)), "Playback mutated the native source");
    catalog.Apply(two, Frame(), defaults, null); AssertNativeCurves(native, two);
    // A live source can later change, but it must not mutate the already
    // captured profile backing arrays or target copies.
    var nativeCurve = native.GetCustomCurve(AudioSourceCurveType.CustomRolloff)!;
    var changed = nativeCurve.keys; changed[0].value = .125f; nativeCurve.keys = changed;
    catalog.Apply(two, Frame(), defaults, null); Check(SameCurve(BeeCurve(), two.GetCustomCurve(AudioSourceCurveType.CustomRolloff)));
});
Test("native curve matching rejects altered distance blend rolloff or clip metadata", () =>
{
    var catalog = new AudioReplaySpatialCatalog(); catalog.Register(Bee(), "bee/native"); var defaults = AudioReplaySpatialProfile.Capture(Defaults());
    foreach (Action<AudioReplayFrame> change in new Action<AudioReplayFrame>[] { f => f.MinDistance += .01f, f => f.MaxDistance += 1, f => f.SpatialBlend = .5f, f => f.Rolloff = 1, f => f.Clip = "other/native" })
    {
        var frame = Frame(); change(frame); var target = new AudioSource();
        Check(!catalog.Apply(target, frame, defaults, null));
        Check(target.minDistance == frame.MinDistance && target.maxDistance == frame.MaxDistance && target.spatialBlend == frame.SpatialBlend);
        Check(target.rolloffMode == (frame.Rolloff == 2 ? AudioRolloffMode.Linear : (AudioRolloffMode)frame.Rolloff));
    }
});
Test("normal logarithmic linear and two-dimensional settings keep their recorded rolloff", () =>
{
    int warnings = 0; var catalog = new AudioReplaySpatialCatalog((_, _) => warnings++); var defaults = AudioReplaySpatialProfile.Capture(Defaults());
    foreach (var mode in new[] { AudioRolloffMode.Logarithmic, AudioRolloffMode.Linear, AudioRolloffMode.Custom })
    {
        var frame = Frame("normal/native"); frame.Rolloff = (int)mode; frame.SpatialBlend = mode == AudioRolloffMode.Custom ? 0 : .625f; frame.MinDistance = 3; frame.MaxDistance = 37;
        var target = new AudioSource(); Check(!catalog.Apply(target, frame, defaults, null));
        Check(target.rolloffMode == mode && target.spatialBlend == frame.SpatialBlend && target.minDistance == 3 && target.maxDistance == 37);
    }
    Check(warnings == 0);
});
Console.WriteLine($"TOTAL: {passed} spatial audio contracts passed. Unity storage doubles do not measure audible rolloff or mixer DSP.");
