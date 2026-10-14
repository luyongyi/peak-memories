using System.Runtime.InteropServices;
namespace UnityEngine
{
    public static class Application { public static string unityVersion = "native-contract-layout"; }
    public sealed class Transform
    { public Transform? parent; public int index; public int GetSiblingIndex()=>index; }
    public enum ParticleSystemCurveMode { Constant, Curve, TwoCurves, TwoConstants }
    public enum ParticleSystemCustomData { Custom1, Custom2 }
    public enum ParticleSystemCustomDataMode { Disabled, Vector, Color }
    public sealed class ParticleSystem
    {
        [StructLayout(LayoutKind.Sequential)] public struct PlaybackState { public float Time, Accumulator; public uint RngA, RngB; }
        [StructLayout(LayoutKind.Sequential)] public struct Particle { public float Position, Velocity, InitialVelocity, RemainingLifetime; public uint RandomSeed, ParentRandomSeed; }
        public struct MinMaxCurve { public ParticleSystemCurveMode mode; public float constant, constantMin, constantMax; }
        public sealed class MainModule { public int maxParticles = 32; public MinMaxCurve startLifetime = new() { constant = 40 }; }
        public sealed class TrailsModule { public bool enabled; }
        public sealed class CustomDataModule { public ParticleSystemCustomDataMode Mode; public ParticleSystemCustomDataMode GetMode(ParticleSystemCustomData _) => Mode; }
        public MainModule main = new(); public TrailsModule trails = new(); public CustomDataModule customData = new();
        public PlaybackState NativeState;
        public Particle[] NativeParticles = Array.Empty<Particle>();
        public int StateReads, ParticleReads, Pauses;
        public PlaybackState GetPlaybackState() { StateReads++; return NativeState; }
        public int GetParticles(Particle[] target) { ParticleReads++; NativeParticles.CopyTo(target, 0); return NativeParticles.Length; }
        public void SetPlaybackState(PlaybackState state) => NativeState = state;
        public void SetParticles(Particle[] data, int count) => NativeParticles = data.Take(count).ToArray();
        public void Pause(bool _) => Pauses++;
    }
}
namespace PeakReplayLab
{
    public sealed class NodePose { public string Path=""; }
    public sealed class EffectReplayFrame
    { public string Key="",Resource=""; public long Seed; public long[] Seeds=Array.Empty<long>(); public EffectParticleFrame[]? Particles; }
    public static class EffectReplayRules { public static bool SameSeeds(long[] a,long[] b)=>a.SequenceEqual(b); }
    public sealed class EffectParticleFrame
    {
        public EffectParticleFrame(NodePose pose, float[] colors, float[] parameters) { Pose = pose; Colors = colors; Parameters = parameters; }
        public NodePose Pose; public float[] Colors, Parameters;
        public bool Emitting, Paused, EmissionEnabled; public int ColorMode, MaterialVariant;
        public object? SimulationSpace, MeshShapeSpace;
        public EffectParticleCheckpoint? Checkpoint;
    }
}
