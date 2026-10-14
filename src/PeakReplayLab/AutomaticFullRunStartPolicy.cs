namespace PeakReplayLab;

// Called from the accepted-frame sink, never from an unvalidated capture tick.
// Claim before file creation: a failed writer still requires an explicit retry,
// while a failed initial capture has not consumed any automatic start attempt.
internal static class AutomaticFullRunStartPolicy
{
    internal static bool TryClaim(bool continuous, bool writerActive, bool writerClosing,
        bool captureAvailable, int captureScene, ref int stoppedScene)
    {
        if (!continuous || writerActive || writerClosing || !captureAvailable || stoppedScene == captureScene) return false;
        stoppedScene = captureScene;
        return true;
    }
}
