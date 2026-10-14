using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace PeakReplayLab;

internal static class EffectParticleCheckpointRuntime
{
    private sealed class Validated { }
    private static readonly ConditionalWeakTable<EffectParticleCheckpoint, Validated> validated = new();
    internal static readonly int StateSize = Marshal.SizeOf<ParticleSystem.PlaybackState>();
    internal static readonly int ParticleSize = Marshal.SizeOf<ParticleSystem.Particle>();
    private static readonly int[] stateFloats = FloatOffsets(typeof(ParticleSystem.PlaybackState));
    private static readonly int[] particleFloats = FloatOffsets(typeof(ParticleSystem.Particle));
    internal static readonly string Layout = CreateLayout();
    internal static string Path(Transform node, Transform root)
    {
        var indices = new Stack<int>();
        for (Transform? current = node; !ReferenceEquals(current, root); current = current.parent)
        {
            if (current == null || indices.Count >= 64) throw new InvalidOperationException("Particle checkpoint is outside its native root.");
            indices.Push(current.GetSiblingIndex());
        }
        return string.Join("/", indices);
    }

    private static string CreateLayout()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<ParticleSystem.PlaybackState>() ||
            RuntimeHelpers.IsReferenceOrContainsReferences<ParticleSystem.Particle>() || StateSize > EffectParticleCheckpointRules.MaximumStateBytes ||
            ParticleSize > EffectParticleCheckpointRules.MaximumParticleStride)
            throw new InvalidOperationException("Unsupported native particle checkpoint layout.");
        var text = new StringBuilder();
        Describe(typeof(ParticleSystem.PlaybackState), text); Describe(typeof(ParticleSystem.Particle), text);
        using var hash = SHA256.Create();
        string signature = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
        return "PS23/" + Application.unityVersion + "/" + typeof(ParticleSystem).Assembly.ManifestModule.ModuleVersionId.ToString("N") +
            "/" + StateSize + "/" + ParticleSize + "/" + signature;
    }
    private static void Describe(Type type, StringBuilder output)
    {
        output.Append(type.FullName).Append(':').Append(Marshal.SizeOf(type)).Append(';');
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            output.Append(field.Name).Append('@').Append(Marshal.OffsetOf(type, field.Name).ToInt32()).Append(':').Append(field.FieldType.FullName).Append(';');
            if (!field.FieldType.IsPrimitive && !field.FieldType.IsEnum) Describe(field.FieldType, output);
        }
    }
    private static int[] FloatOffsets(Type type)
    {
        var offsets = new List<int>(); AddFloatOffsets(type, 0, offsets); return offsets.ToArray();
    }
    private static void AddFloatOffsets(Type type, int origin, List<int> output)
    {
        if (type == typeof(float)) { output.Add(origin); return; }
        if (type.IsPrimitive || type.IsEnum) return;
        if (!type.IsValueType) throw new InvalidOperationException("Particle checkpoints cannot contain managed references.");
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            AddFloatOffsets(field.FieldType, origin + Marshal.OffsetOf(type, field.Name).ToInt32(), output);
    }

    // Evaluated once while constructing the cached capture binding, never as a
    // per-frame catalogue walk. Short explosions do not GetParticles at all.
    internal static bool NeedsCheckpoint(ParticleSystem[] systems)
    {
        foreach (var source in systems)
        {
            var lifetime = source.main.startLifetime;
            bool longLife = lifetime.mode == ParticleSystemCurveMode.Constant ? lifetime.constant > 12 :
                lifetime.mode == ParticleSystemCurveMode.TwoConstants ? Math.Max(lifetime.constantMin, lifetime.constantMax) > 12 : true;
            if (longLife) return true;
        }
        return false;
    }
    internal static bool CanCheckpoint(ParticleSystem[] systems)
    {
        int total = 0;
        foreach (var source in systems)
        {
            total += source.main.maxParticles;
            if (total > EffectParticleCheckpointRules.MaximumParticles || source.trails.enabled ||
                source.customData.GetMode(ParticleSystemCustomData.Custom1) != ParticleSystemCustomDataMode.Disabled ||
                source.customData.GetMode(ParticleSystemCustomData.Custom2) != ParticleSystemCustomDataMode.Disabled) return false;
        }
        return true;
    }
    internal static ParticleSystem.Particle[] Scratch(ParticleSystem source) =>
        new ParticleSystem.Particle[Math.Max(1, Math.Min(EffectParticleCheckpointRules.MaximumParticles, source.main.maxParticles))];

    internal static EffectParticleCheckpoint Capture(ParticleSystem source, ParticleSystem.Particle[] scratch, double observedAt, double rootPhase)
    {
        var state = source.GetPlaybackState();
        int count = source.GetParticles(scratch);
        if (count < 0 || count > scratch.Length) throw new InvalidDataException("Native particle checkpoint count exceeds its bound.");
        var result = new EffectParticleCheckpoint
        {
            ObservedAt = observedAt, RootPhase = rootPhase, Layout = Layout, Count = count,
            State = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref state, 1)).ToArray(),
            Data = MemoryMarshal.AsBytes(scratch.AsSpan(0, count)).ToArray(),
        };
        result.Digest = EffectParticleCheckpointRules.Digest(result);
        Validate(result);
        return result;
    }
    internal static void Restore(ParticleSystem target, EffectParticleCheckpoint checkpoint, ParticleSystem.Particle[] scratch)
    {
        Validate(checkpoint);
        if (checkpoint.Count > scratch.Length || checkpoint.Count > target.main.maxParticles)
            throw new InvalidDataException("Recorded particle checkpoint exceeds its native template capacity.");
        var state = MemoryMarshal.Read<ParticleSystem.PlaybackState>(checkpoint.State.AsSpan());
        MemoryMarshal.Cast<byte, ParticleSystem.Particle>(checkpoint.Data.AsSpan()).CopyTo(scratch);
        target.SetPlaybackState(state);
        target.SetParticles(scratch, checkpoint.Count);
        target.Pause(false);
    }
    internal static void Validate(EffectParticleCheckpoint checkpoint) => _ = validated.GetValue(checkpoint, ValidateOnce);
    private static Validated ValidateOnce(EffectParticleCheckpoint checkpoint)
    {
        EffectParticleCheckpointRules.Validate(checkpoint);
        if (checkpoint.Layout != Layout || checkpoint.State.Length != StateSize || checkpoint.Data.Length != checkpoint.Count * ParticleSize)
            throw new InvalidDataException("Particle checkpoint belongs to another Unity native layout.");
        Floats(checkpoint.State, 0, stateFloats);
        for (int i = 0; i < checkpoint.Count; i++) Floats(checkpoint.Data, i * ParticleSize, particleFloats);
        return new Validated();
    }
    private static void Floats(byte[] data, int origin, int[] offsets)
    {
        foreach (int offset in offsets)
        {
            float value = BitConverter.ToSingle(data, origin + offset);
            if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) >= 1e7)
                throw new InvalidDataException("Invalid numeric native particle checkpoint state.");
        }
    }
}

internal sealed class EffectParticleCheckpointSchedule
{
    private double last = double.NaN, next = double.NegativeInfinity;
    internal bool Due(double time, bool force = false)
    {
        if (time == last) return false;
        if (!double.IsNaN(last) && time < last) next = double.NegativeInfinity;
        if (!force && time < next) return false;
        last = time; next = time + EffectParticleCheckpointRules.IntervalSeconds; return true;
    }
}
