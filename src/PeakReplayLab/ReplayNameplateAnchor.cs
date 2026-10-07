using UnityEngine;

namespace PeakReplayLab;

internal static class ReplayNameplateAnchor
{
    public static Vector3 Position(Vector3 posedHead, Renderer[] hats)
    {
        Vector3 point = posedHead + Vector3.up * .55f;
        foreach (var hat in hats)
            if (hat && hat.enabled && hat.gameObject.activeInHierarchy)
                point.y = Mathf.Max(point.y, hat.bounds.max.y + .12f);
        return point;
    }
}
