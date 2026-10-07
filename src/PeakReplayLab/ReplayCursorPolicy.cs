namespace PeakReplayLab;

// The replay owns the cursor while presenting. Native CursorHandler is suspended
// during that interval so a LateUpdate cannot undo another owner's Update lock.
internal sealed class ReplayCursorPolicy
{
    private bool initialized;
    private bool wasFocused;
    private bool armed;
    public bool Looking { get; private set; }
    public bool ReadLookDelta { get; private set; }

    // Returns true only when a cursor write is necessary. Re-entering the app
    // while RMB is held requires a release before camera control can capture it.
    public bool Update(bool focused, bool rightPressed)
    {
        bool focusChanged = !initialized || wasFocused != focused;
        if (!focused || focusChanged) armed = false;
        if (focused && !rightPressed) armed = true;
        bool looking = focused && armed && rightPressed;
        bool writeCursor = !initialized || focusChanged || Looking != looking;
        // The first locking frame still contains the preceding UI mouse motion
        // (or the cursor warp). Only an already captured frame can turn the view.
        ReadLookDelta = looking && Looking && !focusChanged;
        Looking = looking;
        wasFocused = focused;
        initialized = true;
        return writeCursor;
    }
}
