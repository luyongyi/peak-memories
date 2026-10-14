using PeakReplayLab;
using UnityEngine;
int passed = 0;
void Check(bool value, string reason = "") { if (!value) throw new Exception("Static native event assertion failed: " + reason); }
void Test(string name, Action action)
{
    GameObject.All.Clear(); Time.timeAsDouble = 0; Time.frameCount = 0; ReplaySafety.Active = false;
    VisualReplica.Reads = NativeVisualAppearance.Reads = NativeLightAppearance.Reads = NativePlacementResources.ActivityReads = 0;
    GameObject.ActiveReads = GameObject.ComponentReads = GameObject.DirectoryReads = Transform.PoseReads = 0;
    Animator.StateReads = 0;
    action(); passed++; Console.WriteLine("PASS " + name);
}
GameObject Root(string name, GameObject? parent = null)
{ var source = new GameObject(name); source.transform.parent = parent?.transform; return source; }
TiedBalloon Balloon()
{
    var owner = Root("owner"); owner.AddComponent<Character>(); var balloons = owner.AddComponent<CharacterBalloons>();
    var source = Root("balloon").AddComponent<TiedBalloon>(); source.NativeOwner = balloons; return source;
}
Test("194 audited visible chests read one baseline and zero native state over six thousand idle observations", () =>
{
    for (int i = 0; i < 194; i++)
    {
        var source = Root("chest:" + i); source.AddComponent<Luggage>(); source.AddComponent<SpineCheck>(); source.AddComponent<SFX_PlayOneShot>();
        var animator = source.AddComponent<Animator>(); animator.Clip.length = 0;
    }
    using var capture = new LuggageCapture(); var first = capture.Capture(0);
    Check(first.Length == 194 && VisualReplica.Reads == 194);
    int active = GameObject.ActiveReads, pose = Transform.PoseReads, components = GameObject.ComponentReads, directories = GameObject.DirectoryReads, animatorReads = Animator.StateReads;
    for (int i = 1; i <= 6000; i++) Check(ReferenceEquals(first, capture.Capture(i / 30d)));
    Check(VisualReplica.Reads == 194 && NativeVisualAppearance.Reads == 194 && NativeLightAppearance.Reads == 194);
    Check(GameObject.ActiveReads == active && Transform.PoseReads == pose && GameObject.ComponentReads == components && GameObject.DirectoryReads == directories);
    Check(Animator.StateReads == animatorReads);
});
Test("a chest effect delayed well beyond its animation window records exact activation and retains earlier state", () =>
{
    var source = Root("chest").AddComponent<Luggage>(); var effect = Root("open-effect", source.gameObject); effect.SetActive(false); source.activateOnOpen = effect;
    using var capture = new LuggageCapture(); var initial = capture.Capture(0);
    source.IsOpen = true; LuggageEventObserver.Emit(source, LuggageEventKind.Open, 1); Time.frameCount = 1;
    capture.Capture(1.033); capture.Capture(2);
    int reads = VisualReplica.Reads; var opened = capture.Capture(8); Check(VisualReplica.Reads == reads && !opened[0].Pose.Effect);
    Time.timeAsDouble = 9; effect.SetActive(true); var completed = capture.Capture(9);
    Check(completed[0].Pose.Effect && completed[0].Open && !initial[0].Pose.Effect && !initial[0].Open && !opened[0].Pose.Effect);
    Check(VisualReplica.Reads == reads + 1 && ReferenceEquals(completed, capture.Capture(10000)));
});
Test("audited chest inherited activation is recorded once and lifecycle subscriptions retire with capture", () =>
{
    var parent = Root("parent"); Root("chest", parent).AddComponent<Luggage>();
    var capture = new LuggageCapture(); var initial = capture.Capture(0);
    parent.SetActive(false); var hidden = capture.Capture(1); Check(initial[0].Pose.Active && !hidden[0].Pose.Active);
    parent.SetActive(true); var restored = capture.Capture(2); Check(restored[0].Pose.Active && !hidden[0].Pose.Active);
    capture.Dispose(); parent.SetActive(false); parent.SetActive(true);
    using var next = new LuggageCapture(); Check(next.Capture(3).Length == 1);
});
Test("audited chest native animation remains observed through its full clip and its final edge then sleeps", () =>
{
    var source = Root("long-open-chest").AddComponent<Luggage>(); var animator = source.gameObject.AddComponent<Animator>();
    animator.Clip.length = 3; animator.State.normalizedTime = 1;
    using var capture = new LuggageCapture(); var initial = capture.Capture(0);
    source.IsOpen = true; animator.State.normalizedTime = .011f; LuggageEventObserver.Emit(source, LuggageEventKind.Open, 1); Time.frameCount = 1;
    for (int i = 1; i <= 100; i++) capture.Capture(1 + i / 30d);
    Check(NativeVisualAppearance.Reads > 85 && !initial[0].Open);
    int appearance = NativeVisualAppearance.Reads, light = NativeLightAppearance.Reads, root = Transform.PoseReads;
    var final = capture.Capture(5);
    for (int i = 0; i < 100; i++) Check(ReferenceEquals(final, capture.Capture(100 + i)));
    Check(NativeVisualAppearance.Reads == appearance && NativeLightAppearance.Reads == light && Transform.PoseReads == root);
});
Test("two hundred audited spawned roots with constant lights perform no idle property or activity polls", () =>
{
    for (int i = 0; i < 200; i++)
    {
        var source = Root("ShelfShroomSpawn"); source.AddComponent<Peak.PhotonCleanupHelper>(); source.AddComponent<Light>(); source.AddComponent<SFX_PlayOneShot>();
        var animator = Root("Model", source).AddComponent<Animator>(); animator.runtimeAnimatorController.name = "ShelfShroomSpawn";
        animator.runtimeAnimatorController.animationClips = new[] { animator.Clip };
    }
    using var capture = new SpawnedReplayCapture(); var first = capture.Capture(0);
    Check(first.Length == 200 && capture.GeometryReads == 200 && capture.LightReads == 200 && capture.AppearanceReads == 200);
    int active = GameObject.ActiveReads, components = GameObject.ComponentReads, directories = GameObject.DirectoryReads, animatorReads = Animator.StateReads;
    for (int i = 1; i <= 6000; i++) Check(ReferenceEquals(first, capture.Capture(i / 30d)));
    Check(capture.GeometryReads == 200 && capture.LightReads == 200 && capture.AppearanceReads == 200 && NativePlacementResources.ActivityReads == 0);
    Check(GameObject.ActiveReads == active && GameObject.ComponentReads == components && GameObject.DirectoryReads == directories);
    Check(Animator.StateReads == animatorReads);
});
Test("a real native bounce observes its finite result and returns every static channel to sleep", () =>
{
    var source = Root("BounceShroomSpawn"); source.AddComponent<Peak.PhotonCleanupHelper>(); var collision = source.AddComponent<CollisionModifier>();
    using var capture = new SpawnedReplayCapture(); capture.Capture(0); Time.timeAsDouble = 1; collision.TriggerCharacterBouncedEvents();
    for (int i = 0; i <= 40; i++) capture.Capture(1 + i / 30d);
    var final = capture.Capture(3); long geometry = capture.GeometryReads, lights = capture.LightReads, appearance = capture.AppearanceReads;
    Check(geometry > 30 && lights == geometry && appearance == geometry);
    for (int i = 1; i <= 6000; i++) Check(ReferenceEquals(final, capture.Capture(3 + i / 30d)));
    Check(capture.GeometryReads == geometry && capture.LightReads == lights && capture.AppearanceReads == appearance);
});
Test("native spawned finite clips already advancing at admission are preserved beyond the generic event window", () =>
{
    var source = Root("ShelfShroomSpawn"); source.AddComponent<Peak.PhotonCleanupHelper>();
    var animator = Root("Model", source).AddComponent<Animator>(); animator.runtimeAnimatorController.name = "ShelfShroomSpawn";
    animator.Clip.length = 3; animator.runtimeAnimatorController.animationClips = new[] { animator.Clip };
    animator.State.length = 3; animator.State.normalizedTime = .1f;
    using var capture = new SpawnedReplayCapture(); capture.Capture(0);
    for (int i = 1; i <= 110; i++) capture.Capture(i / 30d);
    Check(capture.GeometryReads > 90 && NativePlacementResources.ActivityReads == 0);
    long reads = capture.GeometryReads; var final = capture.Capture(10);
    Check(ReferenceEquals(final, capture.Capture(10000)) && capture.GeometryReads == reads);
});
Test("unknown native controller replacements and state behaviours retain the fallback", () =>
{
    var root = Root("native-chest"); var source = root.AddComponent<Luggage>(); var animator = root.AddComponent<Animator>();
    Check(NativeStaticVisualCapability.CrateEventOnly(source));
    animator.runtimeAnimatorController.name = "mod-controller"; Check(!NativeStaticVisualCapability.CrateEventOnly(source));
    animator.runtimeAnimatorController.name = "Luggage"; animator.Behaviours = new[] { new StateMachineBehaviour() };
    Check(!NativeStaticVisualCapability.CrateEventOnly(source));
    animator.Behaviours = Array.Empty<StateMachineBehaviour>(); animator.runtimeAnimatorController = new AnimatorOverrideController { name = "Luggage" };
    Check(!NativeStaticVisualCapability.CrateEventOnly(source));
});
Test("unproven spawned scripts retain independent light and material observation without repeated directory binding", () =>
{
    var source = Root("ShelfShroomSpawn"); source.AddComponent<Peak.PhotonCleanupHelper>(); source.AddComponent<UnknownVisualScript>(); source.AddComponent<Light>();
    using var capture = new SpawnedReplayCapture(); capture.Capture(0); int directories = GameObject.DirectoryReads;
    for (int i = 1; i <= 90; i++) capture.Capture(i / 30d);
    Check(capture.GeometryReads == 1 && capture.LightReads == 91 && capture.AppearanceReads == 91 && NativePlacementResources.ActivityReads == 91);
    Check(GameObject.DirectoryReads == directories);
});
Test("native balloon coroutine completion wakes a previously ownerless baseline after arbitrarily many yielded frames", () =>
{
    var source = Balloon(); using var capture = new BalloonReplayCapture(); Check(capture.Capture(0).Length == 0 && capture.InitialBaselineReady);
    source.RPC_Init(4); Check(capture.Capture(.01).Length == 0 && capture.InitialBaselineReady);
    int components = GameObject.ComponentReads, active = GameObject.ActiveReads;
    for (int i = 1; i <= 120; i++) { Check(source.Routine!.MoveNext()); Check(capture.Capture(i / 30d).Length == 0); }
    Check(GameObject.ComponentReads == components && GameObject.ActiveReads == active);
    source.CharacterReady = true; Time.timeAsDouble = 5; Check(!source.Routine!.MoveNext()); var attached = capture.Capture(5);
    Check(attached.Length == 1 && attached[0].ColorIndex == 4 && attached[0].Started == 5 && source.NativeOwner!.tiedBalloons.Contains(source));
    components = GameObject.ComponentReads; active = GameObject.ActiveReads;
    for (int i = 1; i <= 6000; i++) Check(ReferenceEquals(attached, capture.Capture(5 + i / 30d)));
    Check(GameObject.ComponentReads == components && GameObject.ActiveReads == active);
});
Test("balloon native reinitialization updates color and epoch only at completed assignment", () =>
{
    var source = Balloon(); source.CharacterReady = true; using var capture = new BalloonReplayCapture();
    Time.timeAsDouble = 1; source.RPC_Init(1); Check(!source.Routine!.MoveNext()); var first = capture.Capture(1);
    source.CharacterReady = false; source.RPC_Init(3); Check(source.Routine!.MoveNext()); Check(ReferenceEquals(first, capture.Capture(2)));
    source.CharacterReady = true; Time.timeAsDouble = 3; Check(!source.Routine.MoveNext()); var second = capture.Capture(3);
    Check(first[0].ColorIndex == 1 && first[0].Started == 1 && second[0].ColorIndex == 3 && second[0].Started == 3);
});
Test("native balloon destruction retires attachments while its prior checkpoint remains seekable", () =>
{
    var source = Balloon(); source.CharacterReady = true; using var capture = new BalloonReplayCapture();
    source.RPC_Init(2); Check(!source.Routine!.MoveNext()); var first = capture.Capture(0); Check(first.Length == 1);
    source.OnDestroy(); Check(capture.Capture(1).Length == 0 && first.Length == 1 && first[0].Active);
});
Test("balloon activation tracks its ancestor without resampling a settled attachment", () =>
{
    var source = Balloon(); var parent = Root("balloon-parent"); source.transform.parent = parent.transform; source.CharacterReady = true;
    using var capture = new BalloonReplayCapture(); source.RPC_Init(0); Check(!source.Routine!.MoveNext()); var initial = capture.Capture(0);
    parent.SetActive(false); var hidden = capture.Capture(1); Check(initial[0].Active && !hidden[0].Active);
    parent.SetActive(true); var restored = capture.Capture(2); Check(restored[0].Active && !hidden[0].Active);
});
Test("balloon completion listeners cannot interrupt native binding or suppress another observer", () =>
{
    var source = Balloon(); int completed = 0;
    using var broken = new BalloonReplayObserver(_ => throw new Exception("observer failure"), _ => { });
    using var healthy = new BalloonReplayObserver(value => { if (value.ObservedOwner != null) { Check(value.ObservedOwner.tiedBalloons.Contains(value)); completed++; } }, _ => { });
    source.RPC_Init(1); Check(completed == 0); source.CharacterReady = true; Check(!source.Routine!.MoveNext()); Check(completed == 1);
    Check(!source.Routine.MoveNext() && completed == 1);
});
Test("shared root lifecycle isolates failing observers and disposal releases only its own subscription", () =>
{
    var source = Root("shared-root"); int calls = 0;
    using var broken = CaptureRootLifecycleObserver.Bind(source, () => throw new Exception("observer failure"));
    var healthy = CaptureRootLifecycleObserver.Bind(source, () => calls++);
    source.SetActive(false); Check(calls == 1); healthy.Dispose(); healthy.Dispose(); source.SetActive(true); Check(calls == 1);
});
Test("recording root lifecycle observers ignore replay activation", () =>
{
    var source = Root("replay-hidden"); int calls = 0; using var observer = CaptureRootLifecycleObserver.Bind(source, () => calls++);
    ReplaySafety.Active = true; source.SetActive(false); source.SetActive(true); Check(calls == 0); ReplaySafety.Active = false;
    source.SetActive(false); Check(calls == 1);
});
CrateAppearanceCapturePolicyTests.Run(Test);
Console.WriteLine($"TOTAL: {passed} native static event checks passed. Actual capture/observer helpers use deterministic Unity doubles; no game is launched.");
