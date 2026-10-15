using PeakReplayLab;
using UnityEngine;

int passed = 0;
void Test(string name, Action run)
{
    try { ReplaySafety.Active = false; VisualReplica.Reset(); run(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); Environment.Exit(1); }
}
void Check(bool value, string message = "Fake item replay contract failed.") { if (!value) throw new Exception(message); }
void Reject(Action run) { try { run(); } catch (InvalidDataException) { return; } throw new Exception("Invalid fake payload was accepted."); }
(FakeItem Fake, Renderer Mesh, Renderer AuthoredOff, ParticleSystemRenderer Particle) Source(int id, bool active = true)
{
    var root = new GameObject("native fake source " + id); var fake = root.Add(new FakeItem());
    var mesh = root.Add(new Renderer()); var off = root.Add(new Renderer { forceRenderingOff = true }); var particle = root.Add(new ParticleSystemRenderer());
    root.transform.localPosition = new Vector3(id, 2, 3); if (!active) root.SetActive(false);
    return (fake, mesh, off, particle);
}
InteractionReplayFrame Frame(int id, bool active = true, string? key = null) => new()
{
    Key = key ?? "fake-" + id, Kind = "fake-item", Resource = "fake-item/172", SourcePath = "Map/Alpine/" + id + ":SnowPile",
    Pose = new ObjectPose(new[] { (float)id, 2f, 3f }, new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f }, active),
};
void Register(FakeItemReplayCapture capture, FakeItem fake, int id, bool urgent = false) => capture.Register(fake, "fake-item/172", Frame(id).SourcePath, urgent);
int Reads(GameObject root) => root.ActiveReads + root.PoseReads + root.HierarchyReads + root.ComponentReads;
void Present(FakeItemReplayPlayback playback, PresentationWorkBudget budget, int tick, InteractionReplayFrame[] frames, bool jump = false)
{ budget.BeginFrame(tick); playback.Begin(frames, jump); foreach (var frame in frames) playback.Apply(frame, jump); playback.End(); }

Test("1000 static native sources have one baseline and 1800 captures make zero native queries or new snapshots", () =>
{
    using var capture = new FakeItemReplayCapture(); var sources = Enumerable.Range(0, 1000).Select(i => Source(i)).ToArray();
    for (int i = 0; i < sources.Length; i++) Register(capture, sources[i].Fake, i);
    var first = capture.Capture(0); Check(first.Length == 1000 && capture.InitialBaselineReady && capture.RootReads == 1000);
    int reads = sources.Sum(value => Reads(value.Fake.gameObject)); long visibility = capture.VisibilityReads, truth = UnityEngine.Object.TruthChecks;
    for (int i = 1; i <= 1800; i++) Check(ReferenceEquals(first, capture.Capture(i / 30d)));
    Check(sources.Sum(value => Reads(value.Fake.gameObject)) == reads && capture.VisibilityReads == visibility && capture.RootReads == 1000 && UnityEngine.Object.TruthChecks == truth);
});
Test("completed native activation records hide reveal and coalesced edges without rereading static TRS", () =>
{
    var source = Source(1); using var capture = new FakeItemReplayCapture(); Register(capture, source.Fake, 1);
    var initial = capture.Capture(0)[0]; source.Fake.gameObject.SetActive(false);
    var hidden = capture.Capture(1)[0]; Check(!hidden.Pose.Active && initial.Pose.Active && ReferenceEquals(hidden.Pose.Position, initial.Pose.Position));
    source.Fake.gameObject.SetActive(true); var shown = capture.Capture(2)[0];
    Check(shown.Pose.Active && !hidden.Pose.Active && capture.RootReads == 1 && capture.EventWakes == 2);
    source.Fake.gameObject.SetActive(false); source.Fake.gameObject.SetActive(true); source.Fake.gameObject.SetActive(false);
    Check(!capture.Capture(3)[0].Pose.Active && capture.RootReads == 1 && capture.EventWakes == 5);
});
Test("ancestor lifecycle callbacks preserve unchanged activeSelf and do not dirty the static directory", () =>
{
    var parent = new GameObject("biome parent"); var source = Source(2); source.Fake.transform.SetParent(parent.transform, true);
    using var capture = new FakeItemReplayCapture(); Register(capture, source.Fake, 2); var first = capture.Capture(0);
    parent.SetActive(false); parent.SetActive(true); source.Fake.gameObject.NotifyLifecycle();
    Check(ReferenceEquals(first, capture.Capture(1)) && capture.RootReads == 1 && capture.EventWakes == 0 && first[0].Pose.Active);
});
Test("initial capture budget defers static reads while a real activation event can bypass it", () =>
{
    var first = Source(1); var second = Source(2); using var capture = new FakeItemReplayCapture();
    Register(capture, first.Fake, 1); Register(capture, second.Fake, 2);
    long ticks = 0; using (CaptureBootstrapWorkBudget.Begin(1, () => ticks, 1000))
    {
        ticks = 2; Check(capture.Capture(0).Length == 0 && !capture.InitialBaselineReady && capture.RootReads == 0);
        second.Fake.gameObject.SetActive(false);
        var frames = capture.Capture(.1); Check(frames.Length == 1 && !frames[0].Pose.Active && capture.RootReads == 1 && !capture.InitialBaselineReady);
    }
    Check(capture.Capture(.2).Length == 2 && capture.InitialBaselineReady && capture.RootReads == 2);
});
Test("native read failures retry with bounded deadlines and a fresh real event restores admission", () =>
{
    var source = Source(3); source.Fake.transform.FailReads = 4; int warnings = 0;
    using var capture = new FakeItemReplayCapture(_ => warnings++); Register(capture, source.Fake, 3);
    Check(capture.Capture(0).Length == 0); capture.Capture(.1); Check(warnings == 1);
    capture.Capture(.25); capture.Capture(1.25); capture.Capture(5.25); Check(warnings == 4 && !capture.InitialBaselineReady);
    capture.Capture(100); Check(warnings == 4);
    source.Fake.gameObject.SetActive(false); var result = capture.Capture(101);
    Check(result.Length == 1 && !result[0].Pose.Active && capture.InitialBaselineReady);
});
Test("retirement rejects stale queued generations and disposal releases lifecycle subscriptions", () =>
{
    var source = Source(4); var capture = new FakeItemReplayCapture(); Register(capture, source.Fake, 4);
    int id = source.Fake.gameObject.GetInstanceID(); Check(capture.Remove(id) && !capture.Contains(id)); Register(capture, source.Fake, 4);
    var first = capture.Capture(0); Check(first.Length == 1 && first[0].Key.EndsWith(":2", StringComparison.Ordinal));
    capture.Dispose(); long wakes = capture.EventWakes, reads = capture.VisibilityReads;
    source.Fake.gameObject.SetActive(false); source.Fake.gameObject.SetActive(true);
    Check(capture.Count == 0 && capture.EventWakes == wakes && capture.VisibilityReads == reads && capture.Capture(1).Length == 0);
});
Test("presentation callbacks never enter recording static bindings or native pickup methods", () =>
{
    var source = Source(5); using var capture = new FakeItemReplayCapture(); Register(capture, source.Fake, 5); var first = capture.Capture(0);
    ReplaySafety.Active = true; source.Fake.gameObject.SetActive(false); Check(ReferenceEquals(first, capture.Capture(1)) && capture.EventWakes == 0);
    var another = Source(6); Register(capture, another.Fake, 6); Check(capture.Count == 1);
    ReplaySafety.Active = false; source.Fake.gameObject.SetActive(true); source.Fake.gameObject.SetActive(false);
    Check(!capture.Capture(2)[0].Pose.Active && source.Fake.Pickups == 0 && source.Fake.NetworkCalls == 0 && !source.Fake.pickedUp);
});
Test("capture enforces 8192 static sources and warns without reading or subscribing overflow", () =>
{
    int warnings = 0; using var capture = new FakeItemReplayCapture(_ => warnings++);
    for (int i = 0; i < InteractionReplayRules.MaximumStaticFakeItems; i++) Register(capture, Source(i).Fake, i);
    var overflow = Source(9000); Register(capture, overflow.Fake, 9000);
    Check(capture.Count == 8192 && warnings == 1 && overflow.Fake.gameObject.ComponentReads == 0 && capture.RootReads == 0);
});
Test("real fake DTO rules reject executable or detailed payloads malformed resources and duplicate scene aliases", () =>
{
    foreach (string resource in new[] { "fake-item/0172", "fake-item/-1", "fake-item/65536", "item/172", "fake-item/+1" })
    { var value = Frame(1); value.Resource = resource; Reject(() => InteractionReplayRules.Validate(value)); }
    foreach (Action<InteractionReplayFrame> change in new Action<InteractionReplayFrame>[] { value => value.SourcePath = "", value => value.SourcePath = new string('x', 2049),
        value => value.Visuals = Array.Empty<NativeRendererFrame>(), value => value.Lights = Array.Empty<NativeLightFrame>(), value => value.Pose.Nodes = new[] { new NodePose() },
        value => value.Text = new InteractionTextFrame(), value => value.Line = new InteractionLineFrame(), value => value.GloomAmount = 1, value => value.Pose.Position[0] = float.NaN })
    { var value = Frame(1); change(value); Reject(() => InteractionReplayRules.Validate(value)); }
    var one = Frame(1); InteractionReplayRules.Validate(one);
    Check(!InteractionReplayRules.CanInterpolate(one, Frame(1)));
    Reject(() => InteractionReplayRules.ValidateCounts(new[] { one, Frame(1, key: "alias") }));
});
Test("same playback snapshot for 1800 frames has zero native getters writes resolver work or replica builds", () =>
{
    var source = Source(7); int resolves = 0; var budget = new PresentationWorkBudget(() => 0);
    using var playback = new FakeItemReplayPlayback(_ => { resolves++; return source.Fake.gameObject; }, budget, null);
    var frames = new[] { Frame(7) }; Present(playback, budget, 0, frames);
    int reads = Reads(source.Fake.gameObject), writes = source.Fake.gameObject.PoseWrites, flags = source.Mesh.FlagWrites;
    long truth = UnityEngine.Object.TruthChecks;
    for (int i = 1; i <= 1800; i++) Present(playback, budget, i, frames);
    Check(Reads(source.Fake.gameObject) == reads && source.Fake.gameObject.PoseWrites == writes && source.Mesh.FlagWrites == flags && UnityEngine.Object.TruthChecks == truth);
    Check(resolves == 1 && VisualReplica.Created == 0 && playback.VisibleCount == 1 && !playback.Pending && playback.MissingCount == 0);
    Present(playback, budget, 1801, frames, true); Check(Reads(source.Fake.gameObject) > reads && resolves == 1);
});
Test("new frame arrays containing the same immutable fake DTO do not touch native sources", () =>
{
    var source = Source(8); var budget = new PresentationWorkBudget(() => 0); using var playback = new FakeItemReplayPlayback(_ => source.Fake.gameObject, budget, null);
    var frame = Frame(8); Present(playback, budget, 0, new[] { frame }); int reads = Reads(source.Fake.gameObject); long truth = UnityEngine.Object.TruthChecks;
    for (int i = 1; i <= 300; i++) Present(playback, budget, i, new[] { frame });
    Check(Reads(source.Fake.gameObject) == reads && UnityEngine.Object.TruthChecks == truth && playback.VisibleCount == 1);
});
Test("forward hide backward reveal and absent-page release restore renderer leases without native activation", () =>
{
    var source = Source(9); var root = source.Fake.gameObject; var budget = new PresentationWorkBudget(() => 0);
    var original = root.transform.localPosition; int activations = root.ActivationWrites, callbacks = source.Fake.NativeActivations;
    var playback = new FakeItemReplayPlayback(_ => root, budget, null);
    Present(playback, budget, 0, new[] { Frame(9, false) }); Check(source.Mesh.forceRenderingOff && source.AuthoredOff.forceRenderingOff && !source.Particle.forceRenderingOff);
    Present(playback, budget, 1, new[] { Frame(9) }, true); Check(!source.Mesh.forceRenderingOff && source.AuthoredOff.forceRenderingOff);
    var moved = Frame(9); moved.Pose.Position = new[] { 91f, 92f, 93f }; Present(playback, budget, 2, new[] { moved }); Check(root.transform.position == new Vector3(91, 92, 93));
    Present(playback, budget, 3, Array.Empty<InteractionReplayFrame>(), true); Check(root.transform.localPosition == original && !source.Mesh.forceRenderingOff);
    Present(playback, budget, 4, new[] { Frame(9, false) }); playback.Dispose(); playback.Dispose();
    Check(root.transform.localPosition == original && !source.Mesh.forceRenderingOff && source.AuthoredOff.forceRenderingOff && root.activeSelf);
    Check(root.ActivationWrites == activations && source.Fake.NativeActivations == callbacks && source.Fake.Pickups == 0 && source.Fake.NetworkCalls == 0 && !source.Fake.pickedUp);
});
Test("multiple recording keys for one SourcePath share exactly one original visibility and TRS lease", () =>
{
    var source = Source(10); var root = source.Fake.gameObject; int resolves = 0; var budget = new PresentationWorkBudget(() => 0);
    var playback = new FakeItemReplayPlayback(_ => { resolves++; return root; }, budget, null);
    Present(playback, budget, 0, new[] { Frame(10, false, "first-region-key") }); Check(source.Mesh.forceRenderingOff);
    Present(playback, budget, 1, new[] { Frame(10, true, "readmitted-key") }, true);
    Check(!source.Mesh.forceRenderingOff && resolves == 1);
    Present(playback, budget, 2, new[] { Frame(10, false, "first-region-key") }, true); Check(source.Mesh.forceRenderingOff);
    playback.Dispose(); Check(!source.Mesh.forceRenderingOff && source.AuthoredOff.forceRenderingOff && root.activeSelf);
});
Test("historical visible frame over an inactive consumed source uses only an inert fallback and keeps native scripts asleep", () =>
{
    var parent = new GameObject("scaled biome"); parent.transform.localScale = new Vector3(2, 3, 4);
    var source = Source(11, false); var root = source.Fake.gameObject; root.transform.SetParent(parent.transform, true);
    int activations = root.ActivationWrites, callbacks = source.Fake.NativeActivations; var originalScale = root.transform.localScale;
    var budget = new PresentationWorkBudget(() => 0); var playback = new FakeItemReplayPlayback(_ => root, budget, null);
    var earlier = new[] { Frame(11) }; Present(playback, budget, 0, earlier, true);
    var replica = VisualReplica.Instances.Single();
    Check(replica.Root.activeSelf && !root.activeSelf && replica.Root.transform.parent == parent.transform && replica.Root.transform.lossyScale == new Vector3(1, 1, 1));
    Check(!replica.Root.GetComponentsInChildren<Renderer>(true)[0].forceRenderingOff && playback.VisibleCount == 1 && playback.MissingCount == 0);
    int built = VisualReplica.Created; Present(playback, budget, 1, new[] { Frame(11, false) }); Check(!replica.Root.activeSelf);
    Present(playback, budget, 2, earlier, true); Check(replica.Root.activeSelf && VisualReplica.Created == built);
    playback.Dispose(); Check(replica.Root.Destroyed && !root.activeSelf && root.transform.localScale == originalScale);
    Check(root.ActivationWrites == activations && source.Fake.NativeActivations == callbacks && source.Fake.Pickups == 0 && source.Fake.NetworkCalls == 0);
});
Test("four creation budget admits inactive history gradually and identical snapshots retry pending work", () =>
{
    var sources = Enumerable.Range(0, 10).Select(i => Source(i, false)).ToArray(); var frames = Enumerable.Range(0, 10).Select(i => Frame(i)).ToArray();
    var budget = new PresentationWorkBudget(() => 0); using var playback = new FakeItemReplayPlayback(frame => sources[int.Parse(frame.Key.Substring(5))].Fake.gameObject, budget, null);
    Present(playback, budget, 0, frames); Check(playback.Pending && playback.MissingCount == 6 && VisualReplica.Created == 4);
    Present(playback, budget, 1, frames); Check(playback.Pending && playback.MissingCount == 2 && VisualReplica.Created == 8);
    Present(playback, budget, 2, frames); Check(!playback.Pending && playback.MissingCount == 0 && playback.VisibleCount == 10 && VisualReplica.Created == 10);
    Check(sources.All(value => !value.Fake.gameObject.activeSelf && value.Fake.NetworkCalls == 0));
});
Test("unfinished incremental replica survives a build budget boundary then finishes without allocating a second replica", () =>
{
    var source = Source(12, false); var budget = new PresentationWorkBudget(() => 0); using var playback = new FakeItemReplayPlayback(_ => source.Fake.gameObject, budget, null);
    VisualReplica.StepsForNextBuild = PresentationWorkBudget.MaximumBuildSteps + 1; var frames = new[] { Frame(12) };
    Present(playback, budget, 0, frames); Check(playback.Pending && playback.MissingCount == 1 && !VisualReplica.Instances.Single().IsReady);
    Present(playback, budget, 1, frames); Check(!playback.Pending && playback.MissingCount == 0 && VisualReplica.Created == 1 && VisualReplica.Instances.Single().Root.activeSelf);
});
Test("unavailable native resources and replica build exceptions remain visible failures and retry safely", () =>
{
    var source = Source(13, false); int resolves = 0, warnings = 0; var budget = new PresentationWorkBudget(() => 0);
    using var playback = new FakeItemReplayPlayback(_ => ++resolves == 1 ? null : source.Fake.gameObject, budget, _ => warnings++);
    var frames = new[] { Frame(13) }; Present(playback, budget, 0, frames); Check(playback.MissingCount == 1 && warnings == 1 && VisualReplica.Created == 0);
    VisualReplica.FailBuilds = 1; Present(playback, budget, 1, frames); Check(playback.MissingCount == 1 && VisualReplica.Created == 1);
    Present(playback, budget, 2, frames); Check(playback.MissingCount == 0 && playback.VisibleCount == 1 && VisualReplica.Created == 1 && warnings == 1);
});
Test("disabled build admission retries and disposing an unfinished fallback leaves no owned visual or live activation", () =>
{
    var source = Source(14, false); var budget = new PresentationWorkBudget(() => 0); var playback = new FakeItemReplayPlayback(_ => source.Fake.gameObject, budget, null);
    var frames = new[] { Frame(14) }; VisualReplica.CanStartBuild = false; Present(playback, budget, 0, frames);
    Check(playback.Pending && VisualReplica.Created == 0); VisualReplica.CanStartBuild = true;
    VisualReplica.StepsForNextBuild = 1000; Present(playback, budget, 1, frames); Check(playback.Pending && VisualReplica.Created == 1);
    playback.Dispose(); Check(VisualReplica.Retired == 1 && VisualReplica.Instances.Single().Root.Destroyed && !source.Fake.gameObject.activeSelf);
});
Test("completed inactive fallback snapshots stay static and absence retains at most sixteen sleeping replicas", () =>
{
    var sources = Enumerable.Range(0, 20).Select(i => Source(i, false)).ToArray(); var frames = Enumerable.Range(0, 20).Select(i => Frame(i)).ToArray();
    var budget = new PresentationWorkBudget(() => 0); var playback = new FakeItemReplayPlayback(frame => sources[int.Parse(frame.Key.Substring(5))].Fake.gameObject, budget, null);
    for (int i = 0; i < 5; i++) Present(playback, budget, i, frames);
    Check(!playback.Pending && playback.VisibleCount == 20 && VisualReplica.Created == 20);
    int reads = sources.Sum(value => Reads(value.Fake.gameObject)), builds = VisualReplica.BuildCalls, applies = VisualReplica.ApplyCalls;
    long truth = UnityEngine.Object.TruthChecks;
    for (int i = 5; i < 1805; i++) Present(playback, budget, i, frames);
    Check(sources.Sum(value => Reads(value.Fake.gameObject)) == reads && VisualReplica.BuildCalls == builds && VisualReplica.ApplyCalls == applies && UnityEngine.Object.TruthChecks == truth);
    Present(playback, budget, 1805, Array.Empty<InteractionReplayFrame>(), true);
    Check(VisualReplica.Retired == 4 && VisualReplica.Instances.Count(value => !value.Root.Destroyed) == 16);
    playback.Dispose(); Check(VisualReplica.Retired == 20 && sources.All(value => !value.Fake.gameObject.activeSelf && value.Fake.Pickups == 0));
});
Test("destroyed native source is detected at a discontinuity and incompatible resource cannot take another lease", () =>
{
    var source = Source(15); var budget = new PresentationWorkBudget(() => 0); int resolves = 0;
    using var playback = new FakeItemReplayPlayback(_ => { resolves++; return source.Fake.gameObject; }, budget, null);
    var frames = new[] { Frame(15) }; Present(playback, budget, 0, frames);
    var wrong = Frame(15); wrong.Resource = "fake-item/173"; Present(playback, budget, 1, new[] { wrong }, true);
    Check(playback.MissingCount == 1 && resolves == 1);
    Present(playback, budget, 2, frames, true); Check(playback.MissingCount == 0);
    source.Fake.transform.Destroyed = true; Present(playback, budget, 3, frames, true);
    Check(playback.MissingCount == 1 && resolves == 1 && source.Fake.Pickups == 0);
});
Test("gradual build admission cannot bypass the 768 concurrently visible historical fallback limit", () =>
{
    int limit = InteractionReplayRules.MaximumDynamicEntities;
    var sources = Enumerable.Range(0, limit + 1).Select(i => Source(i, false)).ToArray();
    var frames = Enumerable.Range(0, sources.Length).Select(i => Frame(i)).ToArray();
    var budget = new PresentationWorkBudget(() => 0); using var playback = new FakeItemReplayPlayback(frame => sources[int.Parse(frame.Key.Substring(5))].Fake.gameObject, budget, null);
    for (int tick = 0; tick < limit / PresentationWorkBudget.MaximumCreations + 5; tick++) Present(playback, budget, tick, frames);
    Check(VisualReplica.Created == limit && playback.VisibleCount == limit && playback.MissingCount == 1,
        $"Gradual admission exceeded the fallback limit: created={VisualReplica.Created}, visible={playback.VisibleCount}, missing={playback.MissingCount}.");
});
Console.WriteLine($"TOTAL: {passed} contracts execute actual fake capture/playback. Unity objects and inert VisualReplica construction are controlled adapters, not engine or native-game verification.");
