using System.Collections;

// State/ownership doubles only. These do not simulate Unity visuals, its real
// coroutine scheduler, audio curves or scene loading; those require game testing.
namespace UnityEngine
{
    public class Object
    {
        public bool Destroyed;
        protected virtual bool Alive => !Destroyed;
        public static implicit operator bool(Object? value) => value is not null && value.Alive;
        public static void Destroy(Object value) { value.Destroyed = true; }
    }
    public class GameObject : Object { public bool activeInHierarchy = true; }
    public class Component : Object
    {
        public GameObject gameObject = new();
        protected override bool Alive => !Destroyed && gameObject;
        public T? GetComponent<T>() where T : class => this is LoadingScreen screen ? screen.Animator as T : null;
    }
    public class Canvas : Component { public bool enabled = true; }
    public class CanvasGroup : Component { }
    public class Animator : Component { }
    public sealed class Coroutine
    {
        public IEnumerator Routine = null!;
        public bool Done;
        public void Tick() { if (!Done && !Routine.MoveNext()) Done = true; }
    }
    public class MonoBehaviour : Component
    {
        private readonly List<Coroutine> routines = new();
        public int StopAllCount, StopOneCount;
        public Coroutine StartCoroutine(IEnumerator routine)
        {
            var result = new Coroutine { Routine = routine }; routines.Add(result); result.Tick(); return result;
        }
        public void StopCoroutine(Coroutine routine) { routine.Done = true; StopOneCount++; }
        public void StopAllCoroutines() { StopAllCount++; foreach (var routine in routines) routine.Done = true; }
        public void Tick() { foreach (var routine in routines.ToArray()) routine.Tick(); }
    }
}
namespace UnityEngine.Audio
{
    public class AudioMixer : UnityEngine.Object
    {
        public readonly Dictionary<string, float> Values = new() { ["LoadingFade"] = -3, ["UserVolume"] = -18 };
        public bool GetFloat(string name, out float value) => Values.TryGetValue(name, out value);
        public bool SetFloat(string name, float value) { Values[name] = value; return true; }
    }
}

public sealed class LoadingScreen : UnityEngine.MonoBehaviour
{
    public enum LoadingScreenType { Basic, Plane, White, WhiteInstant, PhotonDriven }
    public UnityEngine.Canvas canvas = new();
    public UnityEngine.CanvasGroup group = new();
    public UnityEngine.Animator? Animator = new();
    public UnityEngine.Audio.AudioMixer Mixer = new();
}

public sealed class LoadingScreenHandler : UnityEngine.MonoBehaviour
{
    private static bool _loading;
    private LoadingScreen? _lastActiveLoadingScreen;
    public static LoadingScreenHandler Instance = new();
    public static bool loading => _loading;
    public LoadingScreen Prefab = new();
    public bool FailBeforeScreen, FailAfterScreen, DisabledQueue;
    public LoadingScreen.LoadingScreenType LastType;
    public LoadingScreen? Last => _lastActiveLoadingScreen;
    public LoadingScreen GetLoadingScreenPrefab(LoadingScreen.LoadingScreenType type) => Prefab;
    public static LoadingScreenHandler Reset() { _loading = false; return Instance = new(); }
    public void InstallForeign(LoadingScreen screen) { _lastActiveLoadingScreen = screen; _loading = true; }
    private IEnumerator LoadingRoutine(LoadingScreen.LoadingScreenType type, Action? after, bool disableMessageQueue, params IEnumerator[] processes)
    {
        LastType = type; DisabledQueue = disableMessageQueue; _loading = true;
        if (FailBeforeScreen) throw new InvalidOperationException("Synthetic creation failure.");
        _lastActiveLoadingScreen = new LoadingScreen { Mixer = Prefab.Mixer };
        Prefab.Mixer.SetFloat("LoadingFade", -80);
        yield return null; yield return null;
        if (FailAfterScreen) throw new InvalidOperationException("Synthetic native failure.");
        foreach (var process in processes) while (process.MoveNext()) yield return null;
        after?.Invoke();
        yield return null; yield return null;
        Prefab.Mixer.SetFloat("LoadingFade", 0); _loading = false;
        // Delayed destruction intentionally leaves a canvas until helper cleanup.
    }
}
public sealed class EndScreenStatus { }
public static class GameHandler { public static void ClearStatus<T>() { } }
