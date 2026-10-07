namespace PeakReplayLab;

// These two native switches are not in the historical map-node header. Their
// presentation state is determined by the recorded Segment enum, not a new sample.
internal sealed class WorldEnvironmentState
{
    private readonly bool initialMountainActive;
    private readonly bool initialVoidActive;
    private bool? previousNadir;

    public WorldEnvironmentState(bool mountainActive, bool voidActive)
    {
        initialMountainActive = mountainActive;
        initialVoidActive = voidActive;
    }

    public bool Transition(int recordedSegment, out bool mountainActive, out bool voidActive)
    {
        bool nadir = recordedSegment == 6;
        mountainActive = nadir ? false : initialMountainActive;
        voidActive = nadir ? true : initialVoidActive;
        if (previousNadir == nadir) return false;
        previousNadir = nadir;
        return true;
    }
}
