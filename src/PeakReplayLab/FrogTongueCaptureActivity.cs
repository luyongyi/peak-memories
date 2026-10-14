using HarmonyLib;
using UnityEngine;

namespace PeakReplayLab;

internal sealed class FrogTongueCaptureActivity
{
    private readonly FrogTongue frog;
    private readonly LineRenderer? line;
    private readonly GameObject? lineObject;
    private readonly FrogTongueSamplingPolicy policy = new();
    private static readonly AccessTools.FieldRef<FrogTongue, float>? tongueLength = Resolve<float>("_tongueLength");
    private static readonly AccessTools.FieldRef<FrogTongue, bool>? pulling = Resolve<bool>("_isPulling");
    public FrogTongueCaptureActivity(FrogTongue frog, LineRenderer? line)
    { this.frog = frog; this.line = line; lineObject = line ? line!.gameObject : null; }
    public int ClockMode => policy.ClockMode;
    public bool LastWasBoundary => policy.LastWasBoundary;
    public bool Quiet => policy.Quiet;
    public void NotifyNativeEvent() => policy.NotifyNativeEvent();
    public bool Due(double now, bool active)
    {
        bool reliable = frog && line && lineObject && tongueLength != null && pulling != null;
        if (!reliable) return policy.Due(active, true, 0, 0, false, reliable: false);
        bool visible = active && line!.enabled && lineObject!.activeInHierarchy;
        // A disabled/hidden line needs no point count getter or point array.
        int count = visible ? line!.positionCount : 0;
        return policy.Due(active, visible, count, tongueLength!(frog!), pulling!(frog!));
    }
    private static AccessTools.FieldRef<FrogTongue, T>? Resolve<T>(string name)
    { try { return AccessTools.FieldRefAccess<FrogTongue, T>(name); } catch { return null; } }
}
