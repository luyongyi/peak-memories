using PeakReplayLab;
using UnityEngine;

int passed = 0;
void Check(bool value) { if (!value) throw new Exception("Crate playback contract failed."); }
void Test(string name, Action test)
{
    UnityEngine.Object.Scene = new[] { new Luggage() }; Time.frameCount = 0;
    VisualReplica.Created.Clear(); CrateVisualAnimation.Samples = 0; CrateVisualAnimation.FailPreparation = false;
    NativeVisualAppearance.Playback.Defer = NativeLightAppearance.Playback.Defer = false;
    NativeVisualAppearance.Playback.Applies = 0;
    test(); passed++; Console.WriteLine("PASS " + name);
}
void ExhaustAnimation(PresentationWorkBudget budget)
{ budget.BeginFrame(++Time.frameCount); for (int i = 0; i < PresentationWorkBudget.MaximumSteps; i++) Check(budget.Step()); }
void Apply(LuggagePlayback playback, CrateFrame frame, bool seek = false)
{ var frames = new[] { frame }; playback.Apply(frames, frames, 0, Time.frameCount / 60d, seek); }

Test("opening animation budget miss keeps the committed chest visible and its pose untouched", () =>
{
    var budget = new PresentationWorkBudget(() => 0); using var playback = new LuggagePlayback(workBudget: budget);
    var closed = new CrateFrame(); Apply(playback, closed); var replica = VisualReplica.Created.Single();
    Check(replica.Root.activeSelf && CrateVisualAnimation.Samples == 1);
    var opened = new CrateFrame { Pose = new() { Revision = 1 }, Animation = new() { Revision = 1 } };
    ExhaustAnimation(budget); Apply(playback, opened);
    Check(replica.Root.activeSelf && replica.Root.Hides == 0 && ReferenceEquals(replica.LastPose, closed.Pose));
    Check(replica.PoseApplies == 1 && CrateVisualAnimation.Samples == 1);
    Time.frameCount++; Apply(playback, opened);
    Check(replica.Root.activeSelf && ReferenceEquals(replica.LastPose, opened.Pose) && CrateVisualAnimation.Samples == 2);
});
Test("repeated opening budget misses never toggle the chest root", () =>
{
    var budget = new PresentationWorkBudget(() => 0); using var playback = new LuggagePlayback(workBudget: budget);
    var frame = new CrateFrame(); Apply(playback, frame); var replica = VisualReplica.Created.Single();
    for (int i = 0; i < 20; i++) { ExhaustAnimation(budget); Apply(playback, frame); Time.frameCount++; Apply(playback, frame); }
    Check(replica.Root.activeSelf && replica.Root.Hides == 0 && CrateVisualAnimation.Samples == 21);
});
Test("renderer restoration deferral retains a previously complete chest", () =>
{
    using var playback = new LuggagePlayback(workBudget: new(() => 0)); var frame = new CrateFrame(); Apply(playback, frame);
    var replica = VisualReplica.Created.Single(); NativeVisualAppearance.Playback.Defer = true;
    Time.frameCount++; Apply(playback, new CrateFrame()); Check(replica.Root.activeSelf && replica.Root.Hides == 0);
    NativeVisualAppearance.Playback.Defer = false; Time.frameCount++; Apply(playback, frame); Check(replica.Root.activeSelf);
});
Test("light restoration deferral retains a previously complete chest", () =>
{
    using var playback = new LuggagePlayback(workBudget: new(() => 0)); var frame = new CrateFrame(); Apply(playback, frame);
    var replica = VisualReplica.Created.Single(); NativeLightAppearance.Playback.Defer = true;
    Time.frameCount++; Apply(playback, new CrateFrame()); Check(replica.Root.activeSelf && replica.Root.Hides == 0);
});
Test("a seek hides stale presentation until the requested animation is admitted", () =>
{
    var budget = new PresentationWorkBudget(() => 0); using var playback = new LuggagePlayback(workBudget: budget);
    var frame = new CrateFrame(); Apply(playback, frame); var replica = VisualReplica.Created.Single();
    ExhaustAnimation(budget); Apply(playback, frame, true); Check(!replica.Root.activeSelf);
    ExhaustAnimation(budget); Apply(playback, frame); Check(!replica.Root.activeSelf);
    Time.frameCount++; Apply(playback, frame); Check(replica.Root.activeSelf);
});
Test("a seek remains hidden through deferred appearance after animation admission", () =>
{
    using var playback = new LuggagePlayback(workBudget: new(() => 0)); var frame = new CrateFrame(); Apply(playback, frame);
    var replica = VisualReplica.Created.Single(); NativeVisualAppearance.Playback.Defer = true;
    Time.frameCount++; Apply(playback, frame, true); Check(!replica.Root.activeSelf);
    Time.frameCount++; Apply(playback, frame); Check(!replica.Root.activeSelf);
    NativeVisualAppearance.Playback.Defer = false; Time.frameCount++; Apply(playback, frame); Check(replica.Root.activeSelf);
});
Test("initial reconstruction does not reveal uninitialized appearance", () =>
{
    using var playback = new LuggagePlayback(workBudget: new(() => 0)); var frame = new CrateFrame();
    NativeVisualAppearance.Playback.Defer = true; Apply(playback, frame); var replica = VisualReplica.Created.Single();
    Check(!replica.Root.activeSelf); NativeVisualAppearance.Playback.Defer = false;
    Time.frameCount++; playback.Enforce(true); Check(replica.Root.activeSelf);
});
Test("recorded inactive chest still disappears immediately under budget pressure", () =>
{
    var budget = new PresentationWorkBudget(() => 0); using var playback = new LuggagePlayback(workBudget: budget);
    var frame = new CrateFrame(); Apply(playback, frame); var replica = VisualReplica.Created.Single();
    ExhaustAnimation(budget); Apply(playback, new CrateFrame { Pose = new() { Active = false } }); Check(!replica.Root.activeSelf);
});
Test("absent chest is hidden and reintroduction cannot reveal incomplete appearance", () =>
{
    using var playback = new LuggagePlayback(workBudget: new(() => 0)); var frame = new CrateFrame(); Apply(playback, frame);
    var replica = VisualReplica.Created.Single(); Time.frameCount++; playback.Apply(Array.Empty<CrateFrame>(), Array.Empty<CrateFrame>(), 0);
    Check(!replica.Root.activeSelf); NativeVisualAppearance.Playback.Defer = true;
    Time.frameCount++; Apply(playback, frame); Check(!replica.Root.activeSelf);
});
Test("animation failure still presents the recorded checkpoint fallback", () =>
{
    var warnings = new List<string>(); using var playback = new LuggagePlayback(warnings.Add, new(() => 0));
    CrateVisualAnimation.FailPreparation = true; Apply(playback, new CrateFrame());
    Check(VisualReplica.Created.Single().Root.activeSelf && warnings.Count == 1 && CrateVisualAnimation.Samples == 0);
});
Test("a newer seek replaces deferred checkpoint state before anything is revealed", () =>
{
    var budget = new PresentationWorkBudget(() => 0); using var playback = new LuggagePlayback(workBudget: budget);
    Apply(playback, new CrateFrame()); var replica = VisualReplica.Created.Single();
    ExhaustAnimation(budget); Apply(playback, new CrateFrame { Pose = new() { Revision = 1 } }, true);
    var newest = new CrateFrame { Pose = new() { Revision = 2 } };
    ExhaustAnimation(budget); Apply(playback, newest, true); Check(!replica.Root.activeSelf);
    Time.frameCount++; playback.Enforce(true);
    Check(replica.Root.activeSelf && ReferenceEquals(replica.LastPose, newest.Pose));
});
Test("dispose after a deferred seek restores originals and cannot resume stale work", () =>
{
    var budget = new PresentationWorkBudget(() => 0); var playback = new LuggagePlayback(workBudget: budget);
    var frame = new CrateFrame(); Apply(playback, frame); var replica = VisualReplica.Created.Single();
    var original = UnityEngine.Object.Scene[0].GetComponentsInChildren<Renderer>(true).Single(); Check(original.forceRenderingOff);
    ExhaustAnimation(budget); Apply(playback, frame, true); playback.Dispose(); Time.frameCount++; playback.Enforce(true);
    Check(!replica.Root.activeSelf && !original.forceRenderingOff);
    bool rejected = false; try { Apply(playback, frame); } catch (ObjectDisposedException) { rejected = true; } Check(rejected);
});
Console.WriteLine($"Crate playback contracts passed: {passed}");
