using PeakReplayLab;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UObject = UnityEngine.Object;

int passed = 0;
void Test(string name, Action body)
{
    try { body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); Environment.Exit(1); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Close(float actual, float expected) => Check(Math.Abs(actual - expected) < .0001f, $"Expected {expected}; got {actual}.");
void Point(Vector3 actual, Vector3 expected) { Close(actual.x, expected.x); Close(actual.y, expected.y); Close(actual.z, expected.z); }

Test("recorded head movement and seek change every axis of the name anchor", () =>
{
    var f = new Fixture();
    Point(f.Actor.NamePosition, new Vector3(2, 3.55f, 10));
    f.Actor.HeadPosition = new Vector3(12, 7, 42);
    Point(f.Actor.NamePosition, new Vector3(12, 7.55f, 42));
    f.Actor.HeadPosition = new Vector3(-5, 1, 9);
    Point(f.Actor.NamePosition, new Vector3(-5, 1.55f, 9));
});
Test("name stays above visible hat while hidden disabled and destroyed hats cannot raise it", () =>
{
    var f = new Fixture();
    var worn = new GameObject("worn hat").AddComponent<Renderer>(); worn.bounds = new Bounds(new(99, 6, 99), new(2, 2, 2));
    var hidden = new GameObject("hidden hat").AddComponent<Renderer>(); hidden.bounds = new Bounds(new(0, 80, 0), new(1, 1, 1)); hidden.gameObject.SetActive(false);
    var disabled = new GameObject("disabled hat").AddComponent<Renderer>(); disabled.bounds = new Bounds(new(0, 90, 0), new(1, 1, 1)); disabled.enabled = false;
    var destroyed = new GameObject("destroyed hat").AddComponent<Renderer>(); destroyed.bounds = new Bounds(new(0, 100, 0), new(1, 1, 1)); UObject.Destroy(destroyed);
    f.Actor.Hats = new[] { worn, hidden, disabled, destroyed };
    Point(f.Actor.NamePosition, new Vector3(2, 7.12f, 10));
    worn.enabled = false;
    Point(f.Actor.NamePosition, new Vector3(2, 3.55f, 10));
});
Test("native names hide immediately and activeSelf restores exactly including inactive parents", () =>
{
    var f = new Fixture(); var names = new ReplayNameplates(f.Errors.Add);
    Check(f.Native.All(entry => !entry.gameObject.activeSelf), "Native name remained visible on entry.");
    names.Dispose();
    Check(f.Native[0].gameObject.activeSelf && !f.Native[1].gameObject.activeSelf && f.Native[2].gameObject.activeSelf,
        "Original activeSelf was not restored exactly.");
    Check(!f.Native[2].gameObject.activeInHierarchy, "Originally inactive parent was changed.");
    f.Native[0].gameObject.SetActive(false); names.Dispose();
    Check(!f.Native[0].gameObject.activeSelf && f.Errors.Count == 0, "Duplicate disposal changed restored live state.");
});
Test("Apply suppresses native labels reactivated by another live callback", () =>
{
    var f = new Fixture(); using var names = new ReplayNameplates(f.Errors.Add);
    f.Native[0].gameObject.SetActive(true); f.Native[1].gameObject.SetActive(true);
    names.Apply(f.Camera, f.Actors);
    Check(f.Native.All(entry => !entry.gameObject.activeSelf), "Native name returned during playback.");
    Check(f.Label().gameObject.activeSelf && f.Label().text == "PLAYER", "Recorded name was suppressed with native names.");
});
Test("paused free camera reprojects after head pose and keeps overlay pixel coordinates at nonunit scale", () =>
{
    var f = new Fixture(); using var names = new ReplayNameplates(f.Errors.Add);
    Time.timeScale = 0;
    f.Overlay().scaleFactor = 1.75f;
    names.Apply(f.Camera, f.Actors); var first = f.Label().rectTransform.position;
    f.Camera.transform.position = new Vector3(3, 1, 0);
    names.Apply(f.Camera, f.Actors); var second = f.Label().rectTransform.position;
    Close(second.x, first.x - 120); Close(second.y, first.y - 40);
    f.Actor.HeadPosition = new Vector3(7, 4, 10);
    names.Apply(f.Camera, f.Actors);
    var projected = f.Camera.WorldToScreenPoint(f.Actor.NamePosition);
    Point(f.Label().rectTransform.position, new Vector3(projected.x, projected.y + 6 * 1.75f, 0));
    Check(Time.timeScale == 0 && f.Camera.ProjectionCalls >= 4, "Pause prevented a fresh projection.");
});
Test("behind camera outside viewport and absent actor labels hide and reappear without duplicates", () =>
{
    var f = new Fixture(); using var names = new ReplayNameplates(f.Errors.Add);
    f.Actor.HeadPosition = new Vector3(2, 3, -1); names.Apply(f.Camera, f.Actors);
    Check(f.LabelCount() == 0, "Invisible initial actor allocated a label.");
    f.Actor.HeadPosition = new Vector3(2, 3, 10); names.Apply(f.Camera, f.Actors); var label = f.Label();
    foreach (var point in new[] { new Vector3(2, 3, -1), new Vector3(-100, 3, 10), new Vector3(100, 3, 10), new Vector3(2, -100, 10), new Vector3(2, 100, 10) })
    {
        f.Actor.HeadPosition = point; names.Apply(f.Camera, f.Actors);
        Check(!label.gameObject.activeSelf, "Offscreen name remained visible.");
    }
    f.Actor.HeadPosition = new Vector3(2, 3, 10); f.Actor.Visible = false; names.Apply(f.Camera, f.Actors);
    Check(!label.gameObject.activeSelf, "Hidden actor name remained visible.");
    f.Actor.Visible = true; f.Actor.State = null; names.Apply(f.Camera, f.Actors);
    Check(!label.gameObject.activeSelf, "Unknown actor name remained visible.");
    f.Actor.State = new ActorFrame { Name = "restored recorded name" }; names.Apply(f.Camera, f.Actors);
    Check(label.gameObject.activeSelf && label.text == f.Actor.State.Name && f.LabelCount() == 1, "Returning actor duplicated or retained a stale name.");
});
Test("production overlay reuses native typography and creates only passive presentation components", () =>
{
    var f = new Fixture(); int before = UObject.Registry.Count;
    using var names = new ReplayNameplates(f.Errors.Add); names.Apply(f.Camera, f.Actors);
    var label = f.Label();
    Check(ReferenceEquals(label.font, f.Font) && ReferenceEquals(label.fontSharedMaterial, f.Material) && label.color == f.NativeColor,
        "Native typography or color was not reused.");
    Check(!label.richText && !label.raycastTarget && label.text == "PLAYER", "Recorded label retained interaction or interpreted markup.");
    var allowed = new[] { typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(CanvasRenderer), typeof(TextMeshProUGUI) };
    Check(UObject.Registry.Skip(before).OfType<Component>().All(component => allowed.Contains(component.GetType())),
        "Replay created a native gameplay/UIPlayerNames/PlayerName behaviour.");
});
Test("construction failure while hiding native labels restores prior names and destroys partial overlay", () =>
{
    var f = new Fixture(); f.Native[2].gameObject.ThrowOnNextSetActive = false;
    try { _ = new ReplayNameplates(f.Errors.Add); throw new Exception("Expected construction failure."); }
    catch (InvalidOperationException) { }
    Check(f.Native[0].gameObject.activeSelf && !f.Native[1].gameObject.activeSelf && f.Native[2].gameObject.activeSelf,
        "Construction failure left native labels changed.");
    Check(!UObject.Registry.OfType<Canvas>().Any(canvas => canvas && canvas.gameObject.activeInHierarchy), "Failed constructor leaked an active overlay.");
    Check(f.Errors.Count == 0, "Ordinary failure cleanup raised an extra restoration error.");
});
Test("one native restore failure is reported while later labels still restore and disposal is idempotent", () =>
{
    var f = new Fixture(); var names = new ReplayNameplates(f.Errors.Add);
    f.Native[0].gameObject.ThrowOnNextSetActive = true;
    names.Dispose();
    Check(f.Errors.Count == 1 && !f.Native[1].gameObject.activeSelf && f.Native[2].gameObject.activeSelf,
        "A native visibility failure aborted later restoration.");
    names.Dispose(); Check(f.Errors.Count == 1, "Duplicate disposal replayed a failed restore.");
});
Console.WriteLine($"PASS {passed} recorded nameplate contracts (API doubles; no Unity visual validation)");

internal sealed class Fixture
{
    public readonly List<Exception> Errors = new();
    public readonly TMP_FontAsset Font;
    public readonly Material Material;
    public readonly Color NativeColor = new(.96f, .98f, 1, 1);
    public readonly PlayerName[] Native;
    public readonly Camera Camera;
    public readonly VisualActor Actor;
    public readonly Dictionary<string, VisualActor> Actors;
    public Fixture()
    {
        UObject.Reset(); Time.timeScale = 1; Screen.width = 1920; Screen.height = 1080;
        Font = new TMP_FontAsset(); Material = new Material(); TMP_Settings.defaultFontAsset = Font;
        var native = new GameObject("native player names").AddComponent<UIPlayerNames>();
        var inactiveParent = new GameObject("inactive native UI parent"); inactiveParent.SetActive(false);
        Native = Enumerable.Range(0, 3).Select(i =>
        {
            var go = new GameObject("native name " + i, typeof(RectTransform), typeof(PlayerName), typeof(TextMeshProUGUI));
            var entry = go.GetComponent<PlayerName>(); entry.text = go.GetComponent<TextMeshProUGUI>();
            entry.text.font = Font; entry.text.fontSharedMaterial = Material; entry.text.color = NativeColor;
            if (i == 1) go.SetActive(false);
            if (i == 2) go.transform.SetParent(inactiveParent.transform, false);
            return entry;
        }).ToArray();
        native.playerNameText = Native;
        Camera = new GameObject("native replay camera").AddComponent<Camera>();
        Actor = new VisualActor { HeadPosition = new Vector3(2, 3, 10), State = new ActorFrame { Name = "PLAYER" } };
        Actors = new Dictionary<string, VisualActor> { ["recorded-scout"] = Actor };
    }
    public TextMeshProUGUI Label() => UObject.Registry.OfType<TextMeshProUGUI>()
        .Single(label => label && label.gameObject.name == "Recorded player name");
    public int LabelCount() => UObject.Registry.OfType<TextMeshProUGUI>()
        .Count(label => label && label.gameObject.name == "Recorded player name");
    public Canvas Overlay() => UObject.Registry.OfType<Canvas>().Single(canvas => canvas);
}
