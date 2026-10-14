using PeakReplayLab;
using UnityEngine;

int passed = 0;
void Check(bool value) { if (!value) throw new Exception("Audited bee force replay contract failed"); }
void Test(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); Environment.Exit(1); } }
(ParticleSystem Particle, ParticleSystemForceField Field, BeeSwarm Bee) Native()
{
    var field = new ParticleSystemForceField(); var particle = new ParticleSystem();
    var bee = new BeeSwarm { beeForceField = field, beeParticles = particle }; particle.Owner = bee;
    particle.externalForces.Influences.Add(field); return (particle, field, bee);
}
Test("three native BEES instances resolve their own distinct private visual force field", () =>
{
    var instances = new[] { Native(), Native(), Native() };
    foreach (var instance in instances) Check(EffectParticleForces.TryAuditedBeeField(instance.Particle, out var result) && ReferenceEquals(result, instance.Field));
    Check(!ReferenceEquals(instances[0].Field, instances[1].Field));
});
Test("a global or mixed layer force filter cannot be declared completely reconstructed", () =>
{
    var instance = Native();
    foreach (var filter in new[] { ParticleSystemGameObjectFilter.LayerMask, ParticleSystemGameObjectFilter.LayerMaskAndList })
    { instance.Particle.externalForces.influenceFilter = filter; Check(!EffectParticleForces.TryAuditedBeeField(instance.Particle, out _)); }
});
Test("zero or multiple influence bindings retain the unsupported-native-force limitation", () =>
{
    var instance = Native(); instance.Particle.externalForces.Influences.Clear();
    Check(!EffectParticleForces.TryAuditedBeeField(instance.Particle, out _));
    instance.Particle.externalForces.Influences.Add(instance.Field); instance.Particle.externalForces.Influences.Add(new());
    Check(!EffectParticleForces.TryAuditedBeeField(instance.Particle, out _));
});
Test("a single external field belonging to a different swarm is not accepted", () =>
{
    var instance = Native(); instance.Particle.externalForces.Influences[0] = new();
    Check(!EffectParticleForces.TryAuditedBeeField(instance.Particle, out _));
});
Test("unrelated child particles and non-bee emitters cannot use the audited bee exception", () =>
{
    var instance = Native(); instance.Bee.beeParticles = new();
    Check(!EffectParticleForces.TryAuditedBeeField(instance.Particle, out _));
    instance.Particle.Owner = null; Check(!EffectParticleForces.TryAuditedBeeField(instance.Particle, out _));
});
Test("destroyed fields and inactive native external force modules do not advertise replay support", () =>
{
    var instance = Native(); instance.Field.Destroyed = true;
    Check(!EffectParticleForces.TryAuditedBeeField(instance.Particle, out _));
    instance.Field.Destroyed = false; instance.Particle.externalForces.enabled = false;
    Check(!EffectParticleForces.TryAuditedBeeField(instance.Particle, out _));
});
Console.WriteLine($"TOTAL: {passed} actual native force binding checks passed. Particle integration is not simulated.");
