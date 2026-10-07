using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.Audio;

namespace PeakReplayLab;

// Runs PEAK's real loader, not a reproduction of its visuals. The public Load
// methods discard the coroutine handle; retaining that exact routine lets error
// recovery stop only our load, never all coroutines on the shared game handler.
internal sealed class NativeReplayLoading : IDisposable
{
    private static readonly MethodInfo? NativeRoutine = typeof(LoadingScreenHandler).GetMethod("LoadingRoutine", BindingFlags.Instance | BindingFlags.NonPublic,
        null, new[] { typeof(LoadingScreen.LoadingScreenType), typeof(Action), typeof(bool), typeof(IEnumerator[]) }, null);
    private static readonly FieldInfo? LastScreen = typeof(LoadingScreenHandler).GetField("_lastActiveLoadingScreen", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? LoadingFlag = typeof(LoadingScreenHandler).GetField("_loading", BindingFlags.Static | BindingFlags.NonPublic);
    private readonly LoadingScreenHandler handler;
    private readonly LoadingScreen? previousScreen;
    private readonly AudioMixer? mixer;
    private readonly bool hadFade;
    private readonly float previousFade;
    private Coroutine? coroutine;
    private LoadingScreen? screen;
    private Exception? failure;
    private bool released, finished, disposed;
    public bool ProcessStarted { get; private set; }
    public bool Finished => finished;
    public bool Visible => screen && screen!.canvas && screen.canvas.enabled && screen.gameObject.activeInHierarchy;

    public NativeReplayLoading(LoadingScreen.LoadingScreenType type)
    {
        if (NativeRoutine == null || LastScreen == null || LoadingFlag == null)
            throw new MissingMemberException("本机游戏的原生加载接口已变化，已取消自动回放，避免遗留加载遮罩。");
        if (LoadingScreenHandler.loading) throw new InvalidOperationException("游戏仍在加载，请稍后打开回忆录。");
        handler = LoadingScreenHandler.Instance;
        if (!handler) throw new InvalidOperationException("游戏原生加载界面尚未准备完成。");
        var prefab = handler.GetLoadingScreenPrefab(type);
        if (!prefab || !prefab.canvas || !prefab.group || !prefab.GetComponent<Animator>())
            throw new InvalidOperationException("缺少游戏原生加载界面资源，已取消自动回放。");
        previousScreen = LastScreen.GetValue(handler) as LoadingScreen;
        mixer = prefab.Mixer;
        hadFade = mixer && mixer.GetFloat("LoadingFade", out previousFade);
        try
        {
            // Matches LoadWithoutDisablingQueue: offline disconnect/spawn needs
            // its existing message queue, so this loader must not suspend it.
            GameHandler.ClearStatus<EndScreenStatus>();
            var native = (IEnumerator)NativeRoutine.Invoke(handler, new object?[] { type, null, false, new IEnumerator[] { WaitForReplay() } })!;
            coroutine = handler.StartCoroutine(Observe(native));
            var current = LastScreen.GetValue(handler) as LoadingScreen;
            if (current && current != previousScreen) screen = current;
            Check();
        }
        catch { Dispose(); throw; }
    }

    private IEnumerator WaitForReplay()
    {
        ProcessStarted = true;
        while (!released) yield return null;
    }

    private IEnumerator Observe(IEnumerator native)
    {
        while (true)
        {
            object? yielded;
            try
            {
                if (!native.MoveNext()) { finished = true; yield break; }
                yielded = native.Current;
            }
            catch (Exception e) { failure = e; yield break; }
            yield return yielded;
        }
    }

    public void Release() => released = true;

    public void Check()
    {
        if (disposed) return;
        if (failure != null)
        {
            var error = failure; Dispose();
            throw new InvalidOperationException("游戏原生加载界面运行失败，正在安全返回主菜单。", error);
        }
        if (finished) RemoveOwnedScreen();
    }

    private void RemoveOwnedScreen()
    {
        if (!screen) return;
        // Native Destroy(screen, 6) uses scaled time. A replay freezes time only
        // after the full native audio fade, but must not freeze this stale canvas.
        if (screen!.canvas) screen.canvas.enabled = false;
        screen.StopAllCoroutines(); // Only this owned screen, never the game handler.
        UnityEngine.Object.Destroy(screen.gameObject);
        if (handler && ReferenceEquals(LastScreen!.GetValue(handler), screen)) LastScreen.SetValue(handler, null);
        screen = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; released = true;
        if (handler && coroutine != null) handler.StopCoroutine(coroutine);
        coroutine = null;
        var current = handler ? LastScreen?.GetValue(handler) as LoadingScreen : null;
        // A different screen belongs to someone else. Do not reset its global
        // loading flag or its audio fade, even while recovering our own scene.
        bool ownsCurrent = ReferenceEquals(current, screen) && !ReferenceEquals(screen, null);
        bool failedBeforeScreen = !finished && ReferenceEquals(screen, null) && ReferenceEquals(current, previousScreen);
        RemoveOwnedScreen();
        if (!finished && (ownsCurrent || failedBeforeScreen))
        {
            LoadingFlag?.SetValue(null, false);
            if (hadFade && mixer) mixer!.SetFloat("LoadingFade", previousFade);
        }
        finished = true;
    }
}
