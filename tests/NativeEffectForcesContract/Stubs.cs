namespace UnityEngine
{
    public class Object
    {
        public bool Destroyed;
        public static implicit operator bool([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Object? value) => value is not null && !value.Destroyed;
    }
    public enum ParticleSystemGameObjectFilter { LayerMask, List, LayerMaskAndList }
    public class ParticleSystemForceField : Object { }
    public class ParticleSystem : Object
    {
        public BeeSwarm? Owner;
        public int ParentReads;
        public ExternalForcesModule externalForces = new();
        public T? GetComponentInParent<T>(bool includeInactive) where T : class { ParentReads++; return Owner as T; }
        public class ExternalForcesModule
        {
            public bool enabled = true;
            public ParticleSystemGameObjectFilter influenceFilter = ParticleSystemGameObjectFilter.List;
            public List<ParticleSystemForceField> Influences = new();
            public int influenceCount => Influences.Count;
            public ParticleSystemForceField GetInfluence(int index) => Influences[index];
        }
    }
}
public class BeeSwarm : UnityEngine.Object
{
    public UnityEngine.ParticleSystem? beeParticles;
    public UnityEngine.ParticleSystemForceField? beeForceField;
}
