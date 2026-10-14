using UnityEngine;

namespace PeakReplayLab;

internal static class EffectParticleSurfaces
{
    // Audited native resource: GhostBall/Flame -> Body/GhostBall MeshRenderer.
    // Its shared mesh is static; only the observed body TRS changes. Do not bake
    // skinned character/chicken meshes or copy arbitrary renderer callbacks.
    internal static bool TryStaticSurface(ParticleSystem source, out MeshRenderer surface)
    {
        surface = null!;
        if (source.shape.shapeType != ParticleSystemShapeType.MeshRenderer) return false;
        var ghost = source.GetComponentInParent<Peak.GhostBall>(true);
        var renderer = source.shape.meshRenderer;
        if (!ghost || ghost.flame != source || !renderer || !renderer.transform.IsChildOf(ghost.transform)) return false;
        var filter = renderer.GetComponent<MeshFilter>();
        if (!filter || !filter.sharedMesh) return false;
        surface = renderer; return true;
    }
}
