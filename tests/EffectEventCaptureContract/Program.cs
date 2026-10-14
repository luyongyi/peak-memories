using PeakReplayLab;

EffectCaptureEventBuffer<Snapshot> NewBuffer(int capacity) => new(capacity);
bool Save(EffectCaptureEventBuffer<Snapshot> buffer, Snapshot frame) => buffer.Save(frame.Key, frame);
int Deliver(EffectCaptureEventBuffer<Snapshot> buffer, List<Snapshot> frames) => buffer.AppendMissing(frames, frame => frame.Key);
EffectCaptureRunSignature Run(bool alive = true, bool emitting = true, bool paused = false, bool loop = false,
    long seed = 123, float phase = 0, float rate = 1, int state = 0) => new(alive, emitting, paused, loop, seed, phase, rate, state);
bool Observe(EffectCaptureEventState state, EffectCaptureRunSignature value) => state.Observe(value);
int passed = 0;
void Test(string name, Action run)
{
    try { run(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception error) { throw new Exception(name, error); }
}
void Check(bool value) { if (!value) throw new Exception("Effect event assertion failed."); }

Test("event initial snapshot survives source destruction before next scene tick", () =>
{
    var pending = NewBuffer(512);
    Source? source = new() { X = 4 };
    Snapshot completed = new("pop:1", .016, source.X);
    Check(Save(pending, completed));
    source = null;
    var scene = new List<Snapshot>();
    Check(Deliver(pending, scene) == 0 && scene.Count == 1 && ReferenceEquals(scene[0], completed));
    Check(scene[0].AnchorTime == .016 && scene[0].X == 4);
    scene.Clear(); Check(Deliver(pending, scene) == 0 && scene.Count == 0);
});
Test("event initial snapshot survives pooled source deactivation and later mutation", () =>
{
    var pending = NewBuffer(512);
    var source = new Source { X = 8 };
    Snapshot completed = new("impact:1", .016, source.X);
    Save(pending, completed);
    source.Active = false; source.X = -50;
    var scene = new List<Snapshot>(); Deliver(pending, scene);
    Check(scene.Count == 1 && scene[0].X == 8 && scene[0].AnchorTime == .016);
});
Test("same active run repeated hooks cannot request 60 Hz geometry reads", () =>
{
    var state = new EffectCaptureEventState();
    int geometryReads = 0;
    for (int i = 0; i < 120; i++) if (Observe(state, Run(phase: i / 60f))) geometryReads++;
    Check(geometryReads == 1);
    Check(Observe(state, Run(emitting: false, phase: 2)));
    Check(!Observe(state, Run(emitting: false, phase: 2.02f)));
    Check(Observe(state, Run(seed: 456, phase: .01f)));
    Check(!Observe(state, Run(seed: 456, phase: .02f)));
});
Test("finished dormant registrations and normal loop wraps do not create starts", () =>
{
    var dormant = new EffectCaptureEventState();
    for (int i = 0; i < 120; i++) Check(!Observe(dormant, Run(alive: false, emitting: false)));
    Check(Observe(dormant, Run()));
    var looping = new EffectCaptureEventState();
    Check(Observe(looping, Run(loop: true, phase: 9.99f)));
    Check(!Observe(looping, Run(loop: true, phase: .01f)));
    var burst = new EffectCaptureEventState();
    Check(Observe(burst, Run(phase: .8f)));
    Check(Observe(burst, Run(phase: .01f)));
});
Test("scene delivery never creates duplicate effect keys or replaces live output", () =>
{
    var pending = NewBuffer(512);
    var birth = new Snapshot("particle:1", .016, 1);
    var live = new Snapshot("particle:1", .016, 2);
    Save(pending, birth);
    var scene = new List<Snapshot> { live }; Deliver(pending, scene);
    Check(scene.Count == 1 && ReferenceEquals(scene[0], live));
    scene.Clear(); Deliver(pending, scene); Check(scene.Count == 0);
});
Test("live same-run stop keeps its phase anchor and emission cutoff", () =>
{
    var pending = NewBuffer(512);
    var birth = new Snapshot("particle:1", .016, 1);
    var stopped = new Snapshot("particle:1", .025, 2, Phase: .009, Rate: 1, EmissionStopPhase: .009);
    Save(pending, birth);
    var scene = new List<Snapshot> { stopped }; Deliver(pending, scene);
    Check(scene.Count == 1 && ReferenceEquals(scene[0], stopped));
    Check(Math.Abs(scene[0].AnchorTime - scene[0].Phase / scene[0].Rate - birth.AnchorTime) < 1e-12);
    Check(scene[0].EmissionStopPhase == .009);
    scene.Clear(); Deliver(pending, scene); Check(scene.Count == 0);
});
Test("bounded queue keeps the first immutable event until one delivery", () =>
{
    var pending = NewBuffer(2);
    var first = new Snapshot("a", .016, 1);
    Check(Save(pending, first));
    Check(Save(pending, new Snapshot("a", .02, 2)));
    Check(Save(pending, new Snapshot("b", .025, 3)));
    Check(!Save(pending, new Snapshot("c", .03, 4)));
    var scene = new List<Snapshot>(); Check(Deliver(pending, scene) == 0);
    Check(scene.Count == 2 && ReferenceEquals(scene.Single(f => f.Key == "a"), first));
    Save(pending, new Snapshot("c", .04, 4));
    Check(Deliver(pending, scene) == 1 && scene.Count == 2);
    scene.Clear(); Check(Deliver(pending, scene) == 0 && scene.Count == 0);
});
Test("same source genuine restarts before one scene tick retain two distinct runs", () =>
{
    var state = new EffectCaptureEventState(); var pending = NewBuffer(512); long serial = 0;
    string key = "";
    foreach (var observed in new[] { Run(phase: .006f), Run(seed: 456, phase: .004f) })
    {
        Check(state.Observe(observed) && state.StartsRun);
        key = EffectCaptureRunKey.Create("particle:19", ++serial, observed.Rate);
        pending.Save(key, new Snapshot(key, serial == 1 ? .016 : .033, serial));
    }
    var scene = new List<Snapshot>(); Deliver(pending, scene);
    Check(scene.Count == 2 && scene.Select(frame => frame.Key).Distinct().Count() == 2);
    Check(scene[0].AnchorTime == .016 && scene[1].AnchorTime == .033);
    scene.Clear(); Deliver(pending, scene); Check(scene.Count == 0);
});
Test("stop pause and normal advance preserve run identity while seed phase reset and reactivation create new runs", () =>
{
    var state = new EffectCaptureEventState();
    Check(state.Observe(Run(phase: .5f)) && state.StartsRun);
    Check(state.Observe(Run(emitting: false, phase: .6f)) && !state.StartsRun);
    Check(state.Observe(Run(emitting: false, paused: true, phase: .6f)) && !state.StartsRun);
    Check(state.Observe(Run(emitting: false, paused: false, phase: .6f)) && !state.StartsRun);
    Check(!state.Observe(Run(emitting: false, phase: .7f)) && !state.StartsRun);
    Check(state.Observe(Run(alive: false, emitting: false)) && !state.StartsRun);
    Check(state.Observe(Run(phase: .01f)) && state.StartsRun);
    Check(state.Observe(Run(phase: .8f, seed: 456)) && state.StartsRun);
    Check(state.Observe(Run(phase: .01f, seed: 456)) && state.StartsRun);
});
Test("same run event stop replaces its queued clock without inventing another run", () =>
{
    var pending = NewBuffer(512); string key = EffectCaptureRunKey.Create("particle:1", 1, 1);
    pending.Save(key, new Snapshot(key, .016, 1, Phase: .006));
    var stop = new Snapshot(key, .025, 1, Phase: .015, EmissionStopPhase: .015);
    Check(pending.Save(key, stop, replace: true));
    var scene = new List<Snapshot>(); Deliver(pending, scene);
    Check(scene.Count == 1 && ReferenceEquals(scene[0], stop) && scene[0].EmissionStopPhase == .015);
});
Test("a changed rate is a same-run clock boundary and repeated rate does not resample", () =>
{
    var state = new EffectCaptureEventState(); Check(state.Observe(Run()) && state.StartsRun);
    Check(state.Observe(Run(phase: .016f, rate: 2)) && !state.StartsRun);
    Check(!state.Observe(Run(phase: .032f, rate: 2)) && !state.StartsRun);
});
string eventKey = EffectCaptureRunKey.Create("particle:12", 1, 1);
bool Select(double time, out double birth, bool enabled = true, bool present = false, string? key = null,
    int kind = 0, bool loop = false, bool paused = false, double anchor = .016, double phase = .006,
    float rate = 1, double left = 0, double right = .033) => EffectSceneEventSelection.IncludeRight(enabled, present,
        key ?? eventKey, kind, loop, paused, anchor, phase, rate, left, right, time, out birth);
Test("right-only short birth is included during its true pre-observation lifetime", () =>
{
    Check(!Select(.009, out _));
    Check(Select(.012, out double birth)); Check(Math.Abs(birth - .010) < 1e-12);
    Check(Math.Abs(EffectSceneEventSelection.PhaseAtBirth(birth, 1, .012) - .002) < 1e-12);
    Check(Select(.016, out _) && Select(.025, out _) && Select(.033, out _));
});
Test("short birth selection is stable during paused seek and follows backwards seek", () =>
{
    for (int i = 0; i < 200; i++) Check(Select(.012, out double birth) && Math.Abs(birth - .01) < 1e-12);
    Check(!Select(.008, out _)); Check(Select(.02, out _)); Check(!Select(.008, out _));
});
Test("birth selection rejects old schema and keys plus existing left items", () =>
{
    Check(!Select(.012, out _, enabled: false)); Check(!Select(.012, out _, present: true));
    Check(!Select(.012, out _, key: "particle:12"));
    Check(!Select(.012, out _, key: EffectCaptureRunKey.Create("mesh:12", 1, 1)));
});
Test("only audited one-shot kind0 and kind1 with unchanged positive rate qualify", () =>
{
    Check(Select(.012, out _, key: EffectCaptureRunKey.Create("mesh:12", 1, 1), kind: 1));
    Check(!Select(.012, out _, kind: 2)); Check(!Select(.012, out _, loop: true));
    Check(!Select(.012, out _, paused: true)); Check(!Select(.012, out _, rate: 0));
    Check(!Select(.012, out _, rate: -1)); Check(!Select(.012, out _, rate: 2));
    Check(!Select(.012, out _, phase: -1)); Check(!Select(.012, out _, phase: double.NaN));
    Check(!Select(.012, out _, anchor: double.PositiveInfinity));
});
Test("right-birth does not fill unrelated missing history or pending scene clocks", () =>
{
    Check(!Select(.04, out _)); Check(!Select(-.01, out _));
    Check(!Select(.012, out _, left: .02)); Check(!Select(.012, out _, right: .005));
    Check(!Select(.012, out _, left: double.NaN)); Check(!Select(.012, out _, right: double.NaN));
    Check(!Select(.012, out _, right: 0)); Check(!Select(.012, out _, anchor: 1));
});
Test("relative run rate key survives highlight rebasing without absolute timestamps", () =>
{
    string key = EffectCaptureRunKey.Create("particle:12", 9876, .5f);
    Check(Select(.012, out double birth, key: key, anchor: .016, phase: .003, rate: .5f));
    Check(Math.Abs(birth - .010) < 1e-12);
    Check(Select(10.012, out double shifted, key: key, anchor: 10.016, phase: .003,
        rate: .5f, left: 10, right: 10.033));
    Check(Math.Abs(shifted - birth - 10) < 1e-12);
});
Test("root stop cutoff remains active before stop and turns off exactly at stop", () =>
{
    Check(!EffectSceneEventSelection.EmittingAtBirth(false, .009, .010, 1, .009));
    Check(EffectSceneEventSelection.EmittingAtBirth(false, .009, .010, 1, .012));
    Check(EffectSceneEventSelection.EmittingAtBirth(false, .009, .010, 1, .018));
    Check(!EffectSceneEventSelection.EmittingAtBirth(false, .009, .010, 1, .019));
    Check(!EffectSceneEventSelection.EmittingAtBirth(false, .009, .010, 1, .025));
    Check(!EffectSceneEventSelection.EmittingAtBirth(false, -1, .010, 1, .012));
});
Test("pending birth keeps independent terminal and observed end evidence until scene delivery", () =>
{
    var pending = NewBuffer(512); var first = new Snapshot("a", .016, 1, Phase: .006);
    var stopped = new Snapshot("a", .025, 9, Phase: .015, EmissionStopPhase: .015);
    pending.Save("a", first, observedAt: .016, isInitialRun: true);
    pending.Save("a", stopped, replace: true, observedAt: .025);
    pending.End("a", .027); pending.End("a", .033);
    var scene = new List<Snapshot>();
    pending.AppendMissing(scene, frame => frame.Key, (evidence, current, observedAt) =>
    {
        Check(ReferenceEquals(evidence.First, first) && ReferenceEquals(evidence.Last, stopped));
        Check(evidence.ObservedAt == .016 && evidence.BoundaryAt == .025 && evidence.LastAt == .025);
        Check(evidence.EndedAt == .027 && evidence.IsInitialRun); return current;
    }, .033);
    Check(scene.Count == 1 && ReferenceEquals(scene[0], stopped));
    scene.Clear(); Deliver(pending, scene); Check(scene.Count == 0);
});
Test("mature same-run action admission does not mark a fresh initial run", () =>
{
    var pending = NewBuffer(512); var stopped = new Snapshot("a", .025, 9);
    pending.Save("a", stopped, observedAt: .025); var scene = new List<Snapshot>();
    pending.AppendMissing(scene, frame => frame.Key, (evidence, current, observedAt) =>
    { Check(!evidence.IsInitialRun); return current; }, .033);
    Check(scene.Count == 1);
});
Test("default effect recording captures modules and clocks without native particle state", () =>
{
    Check(!EffectEmitterCapturePolicy.RecordsNativeParticles);
    string root = Directory.GetCurrentDirectory();
    while (!File.Exists(Path.Combine(root, "src/PeakReplayLab/EffectParticleState.cs")))
        root = Directory.GetParent(root)?.FullName ?? throw new Exception("Effect capture source was not found.");
    string source = File.ReadAllText(Path.Combine(root, "src/PeakReplayLab/EffectParticleState.cs"));
    string capture = source[..source.IndexOf("internal sealed class EffectParticlePlayback", StringComparison.Ordinal)];
    Check(capture.Contains("HasCheckpoints => false", StringComparison.Ordinal));
    foreach (string read in new[] { "GetParticles(", "GetPlaybackState(", ".particleCount", "EffectParticleCheckpointRuntime.Capture(", "EffectParticleCheckpointRuntime.Scratch(" })
        Check(!capture.Contains(read, StringComparison.Ordinal));
    string track = File.ReadAllText(Path.Combine(root, "src/PeakReplayLab/EffectReplayTrack.cs"));
    string recording = track[..track.IndexOf("internal sealed class NativeParticleReplica", StringComparison.Ordinal)];
    Check(!recording.Contains("EffectParticleCheckpointRuntime.Capture(", StringComparison.Ordinal));
    Check(recording.Contains("internal object Diagnostics", StringComparison.Ordinal));
});
Test("static native baselines retain one module read through repeated same-run action callbacks", () =>
{
    var events = new EffectCaptureEventState(); var modules = new EffectEmitterCapturePolicy(); int reads = 0;
    for (int i = 0; i < 6000; i++)
    {
        double now = i / 60d;
        if (events.Observe(Run(loop: true, phase: (float)now))) modules.Invalidate(now);
        if (modules.Due(now, reads != 0, false, false)) { reads++; modules.Complete(now); }
    }
    Check(reads == 1);
    Check(events.Observe(Run(loop: true, phase: 100, emitting: false)));
    modules.Invalidate(100); Check(modules.Due(100, true, false, false)); modules.Complete(100);
    Check(!modules.Due(100.016, true, false, false));
});
Test("culler attenuation is probed at 4Hz before changed values cause module reads", () =>
{
    var modules = new EffectEmitterCapturePolicy(); int probes = 0, reads = 0; float prior = float.NaN;
    for (int i = 0; i <= 3000; i++)
    {
        double now = i / 30d; bool changed = false;
        if (modules.ProbeCulling(now)) { probes++; float value = i < 1500 ? 1 : .2f; changed = prior != value; prior = value; }
        Check(!modules.ProbeCulling(now));
        if (modules.Due(now, reads != 0, false, changed)) { reads++; modules.Complete(now); }
    }
    Check(probes == 401 && reads == 2);
});
Test("distance probe phase survives animation reads jitter and long recording gaps", () =>
{
    var modules = new EffectEmitterCapturePolicy(); int probes = 0;
    for (int i = 0; i <= 3000; i++)
    {
        double now = i / 30d + (i % 3 - 1) * .001;
        if (modules.ProbeCulling(now)) probes++;
        Check(modules.Due(now, true, true, false)); modules.Complete(now);
    }
    Check(probes == 401);
    Check(modules.ProbeCulling(1000)); Check(!modules.ProbeCulling(1000));
    Check(!modules.ProbeCulling(1000.01)); Check(modules.ProbeCulling(1000.26));
});
Test("empty looping animator states never acquire continuous module work", () =>
{
    var clock = new EffectNativeModuleClock();
    for (int i = 0; i < 6000; i++) Check(!clock.Observe(true, false, 10, i / 30f, true, false, 1));
    Check(clock.Observe(true, true, 11, 0, false, false, 1));
    Check(clock.Observe(true, true, 11, .5f, false, false, 1));
    Check(clock.Observe(true, true, 11, 1, false, false, 1)); // final pose
    for (int i = 0; i < 6000; i++) Check(!clock.Observe(true, true, 11, 1 + i / 30f, false, false, 1));
});
Test("native clip clocks stop on pause and held clocks and resume on real motion or transition", () =>
{
    var clock = new EffectNativeModuleClock();
    Check(clock.Observe(true, true, 10, 0, true, false, 1));
    Check(clock.Observe(true, true, 10, .1f, true, false, 1));
    Check(clock.Observe(true, true, 10, .1f, true, false, 0));
    Check(!clock.Observe(true, true, 10, .1f, true, false, 0));
    Check(clock.Observe(true, true, 10, .1f, true, false, 1));
    Check(clock.Observe(true, true, 10, .1f, true, false, 1)); // final held pose
    Check(!clock.Observe(true, true, 10, .1f, true, false, 1));
    Check(clock.Observe(true, true, 10, .2f, true, false, 1));
    Check(clock.Observe(true, false, 12, 0, false, true, 1));
    Check(clock.Observe(false, false, 12, 0, false, false, 0));
    Check(!clock.Observe(false, false, 12, 0, false, false, 0));
});
Test("unreadable native animation uses a conservative bounded 4Hz module fallback", () =>
{
    var modules = new EffectEmitterCapturePolicy(); modules.Complete(0); int reads = 0;
    for (int i = 0; i <= 3000; i++)
    {
        double now = i / 30d;
        if (modules.Due(now, true, false, false, unknownNativeAnimation: true)) { reads++; modules.Complete(now); }
    }
    Check(reads == 401);
});
Test("bee disperse lease expires without being renewed by sampling while real fire changes remain observed", () =>
{
    var modules = new EffectEmitterCapturePolicy(); modules.Invalidate(10, 1.1); int reads = 0;
    for (int i = 0; i < 6000; i++)
    {
        double now = 10 + i / 30d;
        if (modules.Due(now, true, false, false)) { reads++; modules.Complete(now); }
    }
    Check(reads == 33);
    Check(modules.Due(211, true, true, false)); modules.Complete(211);
    Check(!modules.Due(211.033, true, false, false));
    modules.Invalidate(212); Check(modules.Due(212, true, false, false));
});
Test("old native smoke uses a finite lifetime warmup window with bounded loop clocks", () =>
{
    foreach (double phase in new[] { 0, .1, 10, 3600, 86400, 1e8 })
        foreach ((double life, double duration, double delay) in new[] { (2d, 5d, 1d), (300d, 5d, 1d), (double.PositiveInfinity, 5d, 0d), (double.NaN, -1d, 0d) })
        {
            double start = EffectNativeWarmupWindow.Start(phase, life, duration, delay);
            Check(double.IsFinite(start) && start >= 0 && start <= phase && phase - start <= 60);
            float clock = EffectNativeWarmupWindow.LoopTime(start, .5f, 3);
            Check(float.IsFinite(clock) && clock >= 0 && clock < 3);
        }
    Check(EffectNativeWarmupWindow.Start(3600, 2, 5, 1) == 3592);
    Check(EffectNativeWarmupWindow.Start(3600, double.PositiveInfinity, 5, 0) == 3540);
    Check(EffectNativeWarmupWindow.Start(double.NaN, 2, 5, 1) == 0);
    Check(EffectNativeWarmupWindow.LoopTime(double.PositiveInfinity, 1, 3) == 0);
    Check(EffectNativeWarmupWindow.LoopTime(3600, float.NaN, 3) == 0);
});
Console.WriteLine($"Effect event capture contracts: {passed} passed.");
sealed record Snapshot(string Key, double AnchorTime, float X, double Phase = 0, float Rate = 1, double EmissionStopPhase = -1);
sealed class Source { public bool Active = true; public float X; }
