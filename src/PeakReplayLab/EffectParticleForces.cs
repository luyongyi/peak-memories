using UnityEngine;

namespace PeakReplayLab;

internal static class EffectParticleForces
{
    // Current BEES asset: one explicit List influence, the owning BeeSwarm's
    // private visual force field. No global force discovery or gameplay script
    // execution is needed. Keep every other native force configuration limited.
    internal static bool TryAuditedBeeField(ParticleSystem source, out ParticleSystemForceField field)
    {
        field = null!;
        var external = source.externalForces;
        if (!external.enabled || external.influenceFilter != ParticleSystemGameObjectFilter.List || external.influenceCount != 1) return false;
        var bee = source.GetComponentInParent<BeeSwarm>(true);
        if (!bee || bee.beeParticles != source || !bee.beeForceField || external.GetInfluence(0) != bee.beeForceField) return false;
        field = bee.beeForceField;
        return true;
    }
}
