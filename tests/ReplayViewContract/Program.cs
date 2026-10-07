using PeakReplayLab;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UObject = UnityEngine.Object;

var passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { Console.Error.WriteLine("FAIL " + name + ": " + ex); Environment.Exit(1); }
}
void ExpectInvalid(Action action)
{
    try { action(); }
    catch (InvalidOperationException) { return; }
    throw new Exception("Expected InvalidOperationException.");
}

Test("reuses the native camera, main references, URP data and camera stack", () =>
{
    var f = new Fixture();
    var count = UObject.Registry.Count;
    using var view = new ReplayView(f.Errors.Add);
    Check(ReferenceEquals(view.Camera, f.Camera), "Replay made a replacement camera.");
    Check(ReferenceEquals(Camera.main, f.Camera) && ReferenceEquals(MainCamera.instance, f.Main), "Native main camera references changed.");
    Check(ReferenceEquals(view.Camera.GetComponent<UniversalAdditionalCameraData>(), f.Urp), "URP component changed.");
    Check(f.Urp.enabled && f.Urp.renderType == CameraRenderType.Base && f.Urp.cameraStack.SequenceEqual(new[] { f.Overlay }), "URP data or camera stack changed.");
    Check(ReferenceEquals(f.Camera.targetTexture, f.Target), "Native render target changed.");
    Check(UObject.Registry.Count == count, "ReplayView allocated a replacement Unity object.");
});

Test("other cameras and environmental volumes retain their original enabled states", () =>
{
    var f = new Fixture();
    using var view = new ReplayView(f.Errors.Add);
    view.Enforce();
    Check(f.Reflection.enabled && f.Overlay.enabled && !f.DisabledCamera.enabled, "Another camera was altered.");
    Check(f.Environment.enabled && !f.DisabledEnvironment.enabled, "An environmental volume was altered.");
});

Test("local eye controller, material and full-screen feature are suppressed", () =>
{
    var f = new Fixture();
    using var view = new ReplayView(f.Errors.Add);
    Check(!f.Movement.enabled && !f.Eye.enabled, "Local control scripts still run.");
    Check(f.EyeMaterial.GetFloat("_EyeOpen") == 1 && !f.EyeFeature.isActive, "Eye Blink can still cover the view.");
    Check(f.UnrelatedFeature.isActive, "An unrelated URP feature was suppressed.");
    Check(f.RemoteEye.enabled && f.RemoteMaterial.GetFloat("_EyeOpen") == .25f && f.RemoteFeature.isActive, "A remote eye controller was changed.");
});

Test("Enforce repairs eye and status state if another script changes it while paused", () =>
{
    var f = new Fixture();
    using var view = new ReplayView(f.Errors.Add);
    f.Camera.enabled = false; f.Movement.enabled = true; f.Eye.enabled = true;
    f.EyeMaterial.SetFloat("_EyeOpen", 0); f.EyeFeature.SetActive(true);
    f.Pass.enabled = true; f.PassVolume.enabled = true; f.Fall.enabled = true; f.FallVolume.enabled = true;
    view.Enforce();
    Check(f.Camera.enabled && !f.Movement.enabled && !f.Eye.enabled, "Pause invariants were not repaired.");
    Check(f.EyeMaterial.GetFloat("_EyeOpen") == 1 && !f.EyeFeature.isActive, "Eye overlay returned during pause.");
    Check(!f.Pass.enabled && !f.PassVolume.enabled && !f.Fall.enabled && !f.FallVolume.enabled, "Status overlay returned during pause.");
});

Test("only volumes attached to PassOutPost and FallPost are suppressed", () =>
{
    var f = new Fixture();
    using var view = new ReplayView(f.Errors.Add);
    Check(!f.Pass.enabled && !f.PassVolume.enabled && !f.Fall.enabled && !f.FallVolume.enabled, "Status effects remain enabled.");
    Check(f.Environment.enabled && !f.DisabledEnvironment.enabled && f.UnrelatedFeature.isActive, "Environment was suppressed with status effects.");
});

Test("Dispose restores camera transform, clipping, field of view, mask and all effect states", () =>
{
    var f = new Fixture();
    var view = new ReplayView(f.Errors.Add);
    Check(f.Camera.cullingMask == 9 && f.Camera.nearClipPlane == .05f, "Replay camera setup not applied.");
    f.Camera.transform.SetPositionAndRotation(new Vector3(99, 98, 97), new Quaternion(0, 1, 0, 0));
    f.Camera.fieldOfView = 110;
    view.Dispose();
    Check(f.Camera.transform.position == Fixture.Position && f.Camera.transform.rotation == Fixture.Rotation, "Camera transform was not restored.");
    Check(f.Camera.cullingMask == 8 && f.Camera.nearClipPlane == .3f && f.Camera.fieldOfView == 70 && f.Camera.enabled, "Camera properties were not restored.");
    Check(f.Movement.enabled && f.Eye.enabled && f.Pass.enabled && f.PassVolume.enabled && f.Fall.enabled && f.FallVolume.enabled, "Original enabled state lost.");
    Check(f.EyeMaterial.GetFloat("_EyeOpen") == 0 && f.EyeFeature.isActive, "Original eye state lost after OnDisable mutation.");
    Check(f.Errors.Count == 0, "Normal restoration reported an error.");
});

Test("originally disabled camera, controllers, volume and feature remain disabled after Dispose", () =>
{
    var f = new Fixture(initiallyEnabled: false);
    var view = new ReplayView(f.Errors.Add);
    Check(f.Camera.enabled && f.EyeMaterial.GetFloat("_EyeOpen") == 1, "Replay was not made visible.");
    view.Dispose();
    Check(!f.Camera.enabled && !f.Movement.enabled && !f.Eye.enabled && !f.Pass.enabled && !f.PassVolume.enabled && !f.Fall.enabled && !f.FallVolume.enabled, "A previously disabled component was enabled.");
    Check(!f.EyeFeature.isActive && f.EyeMaterial.GetFloat("_EyeOpen") == .4f, "Previously inactive eye state was not restored.");
});

Test("missing local character construction failure restores already suspended movement", () =>
{
    var f = new Fixture();
    Character.localCharacter = null!;
    ExpectInvalid(() => _ = new ReplayView(f.Errors.Add));
    Check(f.Movement.enabled, "Construction failure left camera movement suspended.");
    Check(f.Camera.cullingMask == 8 && f.Camera.nearClipPlane == .3f && f.Camera.transform.position == Fixture.Position, "Construction failure changed camera state.");
    Check(f.Eye.enabled && f.Pass.enabled && f.Environment.enabled, "Construction failure changed unrelated state.");
});

Test("failed construction also preserves originally disabled movement", () =>
{
    var f = new Fixture(initiallyEnabled: false);
    Character.localCharacter = null!;
    ExpectInvalid(() => _ = new ReplayView(f.Errors.Add));
    Check(!f.Movement.enabled && !f.Camera.enabled, "Failure enabled an originally disabled component.");
});

Test("duplicate Dispose and Enforce after Dispose do not change restored state", () =>
{
    var f = new Fixture();
    var view = new ReplayView(f.Errors.Add);
    view.Dispose();
    f.Camera.fieldOfView = 88; f.Camera.enabled = false; f.Movement.enabled = false;
    f.EyeMaterial.SetFloat("_EyeOpen", .7f);
    view.Dispose(); view.Enforce();
    Check(f.Camera.fieldOfView == 88 && !f.Camera.enabled && !f.Movement.enabled && f.EyeMaterial.GetFloat("_EyeOpen") == .7f, "Disposed object mutated live game state.");
});

Test("fallback uses Camera.main if MainCamera.instance is absent", () =>
{
    var f = new Fixture();
    MainCamera.instance = null!;
    using var view = new ReplayView(f.Errors.Add);
    Check(ReferenceEquals(view.Camera, f.Camera), "Camera.main fallback failed.");
});

Test("absent and inactive native cameras fail before modifying controllers", () =>
{
    var f = new Fixture();
    f.Camera.gameObject.activeSelf = false;
    ExpectInvalid(() => _ = new ReplayView(f.Errors.Add));
    Check(f.Movement.enabled && f.Eye.enabled, "Inactive camera failure changed controllers.");
    MainCamera.instance = null!; Camera.main = null!;
    ExpectInvalid(() => _ = new ReplayView(f.Errors.Add));
});

Test("inactive local eye children are also suppressed and restored", () =>
{
    var f = new Fixture();
    var child = new GameObject("Inactive eye child") { activeSelf = false };
    Character.localCharacter.gameObject.AddChild(child);
    var eye = child.AddComponent<EyeBlinkController>();
    eye.eyeBlinkMaterial = new Material(); eye.eyeBlinkMaterial.SetFloat("_EyeOpen", .2f);
    eye.rend = new ScriptableRendererData();
    var feature = new ScriptableRendererFeature { name = "Eye Blink" }; eye.rend.rendererFeatures.Add(feature);
    var view = new ReplayView(f.Errors.Add);
    Check(!eye.enabled && eye.eyeBlinkMaterial.GetFloat("_EyeOpen") == 1 && !feature.isActive, "Inactive local eye was omitted.");
    view.Dispose();
    Check(eye.enabled && eye.eyeBlinkMaterial.GetFloat("_EyeOpen") == .2f && feature.isActive, "Inactive local eye state was lost.");
});

Test("shared eye material and feature are snapshotted only once", () =>
{
    var f = new Fixture();
    var second = Character.localCharacter.gameObject.AddComponent<EyeBlinkController>();
    second.eyeBlinkMaterial = f.EyeMaterial; second.rend = f.Eye.rend;
    var view = new ReplayView(f.Errors.Add);
    view.Dispose();
    Check(f.EyeMaterial.GetFloat("_EyeOpen") == 0 && f.EyeFeature.isActive && second.enabled, "Second controller overwrote pre-suspension shared state.");
});

Test("missing optional eye resources and status volumes are tolerated", () =>
{
    var f = new Fixture();
    f.Eye.eyeBlinkMaterial = new Material(); f.Eye.rend = null!;
    var post = new GameObject("No volume").AddComponent<PassOutPost>();
    using (var view = new ReplayView(f.Errors.Add))
    {
        Check(!post.enabled && !f.Eye.enabled, "Optional-resource case skipped controllers.");
        view.Enforce();
    }
    Check(post.enabled && f.Eye.enabled && f.Errors.Count == 0, "Optional-resource restoration failed.");
});

Test("one restore failure and a failing reporter do not stop subsequent restoration", () =>
{
    var f = new Fixture();
    var reports = 0;
    var view = new ReplayView(_ => { reports++; throw new InvalidOperationException("Synthetic reporter failure"); });
    f.Movement.ThrowOnNextEnabledSet = true;
    view.Dispose();
    Check(reports == 1 && f.Eye.enabled && f.Pass.enabled && f.PassVolume.enabled && f.Fall.enabled && f.FallVolume.enabled, "A failed restore blocked other components.");
    Check(f.EyeMaterial.GetFloat("_EyeOpen") == 0 && f.EyeFeature.isActive, "A failed restore blocked eye state restoration.");
});

Test("destroyed optional objects are skipped during Enforce and Dispose", () =>
{
    var f = new Fixture();
    var view = new ReplayView(f.Errors.Add);
    UObject.Destroy(f.Eye); UObject.Destroy(f.EyeMaterial); UObject.Destroy(f.EyeFeature); UObject.Destroy(f.PassVolume);
    view.Enforce(); view.Dispose();
    Check(f.Movement.enabled && f.Fall.enabled && f.Errors.Count == 0, "Destroyed optional components caused restoration failure.");
});

Test("destroyed camera is reported by Enforce but does not prevent effect restoration", () =>
{
    var f = new Fixture();
    var view = new ReplayView(f.Errors.Add);
    UObject.Destroy(f.Camera);
    ExpectInvalid(view.Enforce);
    view.Dispose();
    Check(f.Movement.enabled && f.Eye.enabled && f.EyeFeature.isActive && f.EyeMaterial.GetFloat("_EyeOpen") == 0, "Missing camera prevented other state restoration.");
});

Test("Describe identifies native render setup and suppressed eye pass", () =>
{
    var f = new Fixture();
    using var view = new ReplayView(f.Errors.Add);
    var description = view.Describe();
    Check(description.Contains("nativeCamera=Native Camera") && description.Contains("main=True") && description.Contains("urp=Base") && description.Contains("target=Native Target") && description.Contains("eyePassesSuppressed=1"), "Diagnostics omit the actual borrowed render setup.");
});

Test("native CanvasGroup copies all visual fields without player-incompatible JSON", () =>
{
    UObject.Reset(); JsonUtility.ResetCalls();
    var source = new GameObject("Native BarGroup").AddComponent<CanvasGroup>();
    source.alpha = .6f; source.interactable = false; source.blocksRaycasts = false; source.ignoreParentGroups = true;
    var copy = new GameObject("Recorded BarGroup").AddComponent<CanvasGroup>();
    NativeUiComponentCopy.Copy(source, copy);
    Check(copy.alpha == .6f && !copy.interactable && !copy.blocksRaycasts && copy.ignoreParentGroups && copy.enabled,
        "CanvasGroup's native visual properties were not copied.");
    Check(JsonUtility.ToJsonCalls == 0 && JsonUtility.OverwriteCalls == 0, "CanvasGroup still entered JsonUtility.");
    Check(source.alpha == .6f && !source.interactable && !source.blocksRaycasts && source.ignoreParentGroups,
        "Copying a native group changed the source HUD.");
});

Test("transparent disabled native groups retain their observed state during copying", () =>
{
    UObject.Reset(); JsonUtility.ResetCalls();
    var source = new GameObject("Native morale group").AddComponent<CanvasGroup>();
    source.alpha = 0; source.enabled = false; source.interactable = true; source.blocksRaycasts = false;
    var copy = new GameObject("Recorded morale group").AddComponent<CanvasGroup>();
    copy.ignoreParentGroups = true;
    NativeUiComponentCopy.Copy(source, copy);
    Check(copy.alpha == 0 && !copy.enabled && copy.interactable && !copy.blocksRaycasts && !copy.ignoreParentGroups,
        "Transparent or disabled CanvasGroup state was lost.");
    Check(JsonUtility.ToJsonCalls == 0 && JsonUtility.OverwriteCalls == 0, "Native group used JSON.");
});

Test("allowed MonoBehaviour visuals continue to copy serialized fields and enabled state", () =>
{
    UObject.Reset(); JsonUtility.ResetCalls();
    var source = new GameObject("Native script visual").AddComponent<CopyableVisual>();
    source.Label = "原版状态条"; source.Width = 600; source.enabled = false;
    var copy = new GameObject("Recorded script visual").AddComponent<CopyableVisual>();
    NativeUiComponentCopy.Copy(source, copy);
    Check(copy.Label == source.Label && copy.Width == 600 && !copy.enabled, "Script visual state did not copy.");
    Check(JsonUtility.ToJsonCalls == 1 && JsonUtility.OverwriteCalls == 1, "Script copy skipped its supported serializer.");
    Check(!ReferenceEquals(source.gameObject, copy.gameObject), "Copy replaced visual ownership.");
});

Test("another native engine component fails clearly before unsupported JSON", () =>
{
    UObject.Reset(); JsonUtility.ResetCalls();
    var source = new GameObject("Native engine component").AddComponent<Camera>();
    var copy = new GameObject("Recorded engine component").AddComponent<Camera>();
    ExpectInvalid(() => NativeUiComponentCopy.Copy(source, copy));
    Check(JsonUtility.ToJsonCalls == 0 && JsonUtility.OverwriteCalls == 0, "Unsupported engine component reached JsonUtility.");
});

Test("component copy rejects a type mismatch before mutating the target", () =>
{
    UObject.Reset(); JsonUtility.ResetCalls();
    var source = new GameObject("Native group").AddComponent<CanvasGroup>(); source.alpha = 0;
    var copy = new GameObject("Different copy").AddComponent<CopyableVisual>(); copy.Label = "unchanged";
    ExpectInvalid(() => NativeUiComponentCopy.Copy(source, copy));
    Check(copy.Label == "unchanged" && copy.enabled && JsonUtility.ToJsonCalls == 0, "Mismatched copy mutated a target.");
});

Test("destroyed visual copy fails before reading its properties", () =>
{
    UObject.Reset(); JsonUtility.ResetCalls();
    var source = new GameObject("Native group").AddComponent<CanvasGroup>();
    var copy = new GameObject("Destroyed copy").AddComponent<CanvasGroup>(); UObject.Destroy(copy);
    bool rejected = false;
    try { NativeUiComponentCopy.Copy(source, copy); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected && JsonUtility.ToJsonCalls == 0, "Destroyed visual was not rejected before copying.");
});

Test("serializer double reproduces the installed player's native CanvasGroup rejection", () =>
{
    UObject.Reset(); JsonUtility.ResetCalls();
    var group = new GameObject("Native BarGroup").AddComponent<CanvasGroup>();
    bool rejected = false;
    try { JsonUtility.ToJson(group); }
    catch (ArgumentException e) { rejected = e.Message == "JsonUtility.ToJson does not support engine types."; }
    Check(rejected, "Regression fixture silently allowed native engine JSON serialization.");
});

Console.WriteLine($"{passed} ReplayView and native UI copy contract tests passed (in-memory doubles; not a Unity rendering test).");

internal sealed class CopyableVisual : MonoBehaviour
{
    public string Label = "";
    public float Width;
}

internal sealed class Fixture
{
    public static readonly Vector3 Position = new(1, 2, 3);
    public static readonly Quaternion Rotation = new(0, 0, 0, 1);
    public readonly List<Exception> Errors = new();
    public readonly Camera Camera, Reflection, Overlay, DisabledCamera;
    public readonly MainCamera Main;
    public readonly MainCameraMovement Movement;
    public readonly UniversalAdditionalCameraData Urp;
    public readonly RenderTexture Target;
    public readonly EyeBlinkController Eye, RemoteEye;
    public readonly Material EyeMaterial, RemoteMaterial;
    public readonly ScriptableRendererFeature EyeFeature, UnrelatedFeature, RemoteFeature;
    public readonly PassOutPost Pass;
    public readonly FallPost Fall;
    public readonly Volume PassVolume, FallVolume, Environment, DisabledEnvironment;

    public Fixture(bool initiallyEnabled = true)
    {
        UObject.Reset();
        var root = new GameObject("Native Camera");
        Camera = root.AddComponent<Camera>(); UnityEngine.Camera.main = Camera;
        Camera.transform.SetPositionAndRotation(Position, Rotation);
        Camera.cullingMask = 8; Camera.nearClipPlane = .3f; Camera.fieldOfView = 70; Camera.enabled = initiallyEnabled;
        Target = new RenderTexture { name = "Native Target" }; Camera.targetTexture = Target;
        Main = root.AddComponent<MainCamera>(); MainCamera.instance = Main;
        Movement = root.AddComponent<MainCameraMovement>(); Movement.enabled = initiallyEnabled;
        Urp = root.AddComponent<UniversalAdditionalCameraData>(); Urp.renderType = CameraRenderType.Base;
        Reflection = new GameObject("Reflection Camera").AddComponent<Camera>();
        Overlay = new GameObject("Overlay Camera").AddComponent<Camera>(); Urp.cameraStack.Add(Overlay);
        DisabledCamera = new GameObject("Disabled Camera").AddComponent<Camera>(); DisabledCamera.enabled = false;
        var local = new GameObject("Local Character"); Character.localCharacter = local.AddComponent<Character>();
        Eye = local.AddComponent<EyeBlinkController>();
        EyeMaterial = new Material(); EyeMaterial.SetFloat("_EyeOpen", 0); Eye.eyeBlinkMaterial = EyeMaterial;
        Eye.rend = new ScriptableRendererData();
        EyeFeature = new ScriptableRendererFeature { name = "Eye Blink" };
        UnrelatedFeature = new ScriptableRendererFeature { name = "Environment Fog" };
        Eye.rend.rendererFeatures.AddRange(new[] { EyeFeature, UnrelatedFeature });
        Eye.enabled = initiallyEnabled; EyeMaterial.SetFloat("_EyeOpen", initiallyEnabled ? 0 : .4f); EyeFeature.SetActive(initiallyEnabled);
        RemoteEye = new GameObject("Remote Character").AddComponent<EyeBlinkController>();
        RemoteMaterial = new Material(); RemoteMaterial.SetFloat("_EyeOpen", .25f); RemoteEye.eyeBlinkMaterial = RemoteMaterial;
        RemoteEye.rend = new ScriptableRendererData(); RemoteFeature = new ScriptableRendererFeature { name = "Eye Blink" }; RemoteEye.rend.rendererFeatures.Add(RemoteFeature);
        var passObject = new GameObject("PassOut status"); Pass = passObject.AddComponent<PassOutPost>(); PassVolume = passObject.AddComponent<Volume>();
        var fallObject = new GameObject("Fall status"); Fall = fallObject.AddComponent<FallPost>(); FallVolume = fallObject.AddComponent<Volume>();
        Pass.enabled = PassVolume.enabled = Fall.enabled = FallVolume.enabled = initiallyEnabled;
        Environment = new GameObject("Environment fog volume").AddComponent<Volume>();
        DisabledEnvironment = new GameObject("Disabled environment volume").AddComponent<Volume>(); DisabledEnvironment.enabled = false;
    }
}
