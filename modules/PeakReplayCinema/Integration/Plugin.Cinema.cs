using System;
using PeakReplayLab.Cinema;
using UnityEngine.InputSystem;

namespace PeakReplayLab;

// The only plugin bridge. No recording callbacks, capture types, archive codecs
// or network services are visible to the cinema module.
public sealed partial class Plugin
{
    private CinemaController? cinema;

    partial void ConfigurePresentationEntry(ref Action? openPresentation) => openPresentation = ToggleCinema;

    partial void PresentationInput(ref bool ownsShortcuts)
    {
        if (cinema?.IsDisposed == true) cinema = null;
        if (Keyboard.current?.f8Key.wasPressedThisFrame == true)
        {
            ToggleCinema(); ownsShortcuts = true; return;
        }
        if (cinema == null) return;
        ownsShortcuts = true;
        try { cinema.HandleInput(); }
        catch (Exception error)
        {
            DisposePresentation(); ownsShortcuts = false;
            Logger.LogError(error); Note("电影模式已恢复普通回放：" + error.Message);
        }
    }

    private void ToggleCinema()
    {
        if (cinema != null)
        {
            cinema.RequestClose();
            if (cinema.IsDisposed) cinema = null;
            return;
        }
        var session = theatre?.Playback;
        if (session == null || replayConsole == null || replayHud == null) return;
        try
        {
            cinema = new CinemaController(session, theatre!.SourcePath,
                visible => replayConsole?.SetPresentationVisible(visible),
                visible => replayHud?.SetPresentationVisible(visible),
                message => Logger.LogWarning(message));
        }
        catch (Exception error)
        {
            Logger.LogError(error);
            Note("电影模式未能打开，普通回放仍可使用：" + error.Message);
        }
    }

    partial void DisposePresentation()
    {
        var current = cinema; cinema = null;
        try { current?.Dispose(); }
        catch (Exception error) { Logger.LogError(error); }
    }

    partial void RequestPresentationExit(Action continueExit, ref bool handled)
    {
        if (cinema == null || cinema.IsDisposed) return;
        handled = true;
        cinema.RequestClose(continueExit);
    }

    partial void CancelPresentationPrompt(ref bool handled)
    {
        if (cinema == null || !cinema.HasPendingAction) return;
        cinema.Execute(new CinemaEditorCommand(CinemaEditorCommandKind.CancelPending));
        handled = true;
    }
}
