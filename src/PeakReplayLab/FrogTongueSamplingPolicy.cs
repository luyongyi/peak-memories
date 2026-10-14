using System;

namespace PeakReplayLab;

// The body belongs to ItemCapture. A closed tongue has no world geometry to
// advance; keep the last checkpoint until native visibility/activity changes.
public sealed class FrogTongueSamplingPolicy
{
    private bool observed, pending, lastActive, lastVisible, lastPulling, lastGeometry;
    public int ClockMode { get; private set; }
    public bool LastWasBoundary { get; private set; }
    public bool Quiet { get; private set; }
    public void NotifyNativeEvent() => pending = true;

    public bool Due(bool active, bool visible, int pointCount, float length, bool pulling, bool reliable = true)
    {
        bool finite = !float.IsNaN(length) && !float.IsInfinity(length);
        bool geometry = visible && (pointCount > 0 || pulling || finite && length > 0);
        // An unfamiliar native source stays on the old bounded scene clock.
        bool conservative = !reliable || !finite || pointCount < 0;
        bool boundary = !observed || pending || active != lastActive || visible != lastVisible ||
            pulling != lastPulling || geometry != lastGeometry;
        Quiet = !conservative && (!active || !geometry);
        ClockMode = Quiet ? CreatureReplayRules.SleepingClock : CreatureReplayRules.ActiveClock;
        LastWasBoundary = boundary;
        lastActive = active; lastVisible = visible; lastPulling = pulling; lastGeometry = geometry;
        observed = true; pending = false;
        return boundary || !Quiet;
    }
}
