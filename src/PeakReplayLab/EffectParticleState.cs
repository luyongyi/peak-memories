using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace PeakReplayLab;

// Component/module handles are resolved at birth. The recording hot path never
// searches a hierarchy or reflects over Unity's module properties.
internal sealed class EffectParticleCapture
{
    private sealed class Binding
    {
        internal readonly ParticleSystem Particle;
        internal readonly Transform Transform;
        internal readonly ParticleSystemRenderer Renderer;
        internal readonly ParticleSystem.MainModule Main;
        internal readonly ParticleSystem.EmissionModule Emission;
        internal readonly ParticleSystem.ShapeModule Shape;
        internal readonly Transform? MeshSurface;
        internal Transform? CustomSpace;
        internal Binding(ParticleSystem particle, Transform root)
        {
            Particle = particle; Transform = particle.transform;
            Renderer = particle.GetComponent<ParticleSystemRenderer>();
            Main = particle.main; Emission = particle.emission; Shape = particle.shape;
            if (EffectParticleSurfaces.TryStaticSurface(particle, out var surface)) MeshSurface = surface.transform;
        }
    }
    private readonly Binding[] bindings;
    private readonly ParticleSystemForceField? force;
    private readonly Material? beeMaterial;
    private readonly EffectEmitterCapturePolicy moduleReads = new();
    private readonly Campfire? changingFire;
    private sealed class AnimatorBinding
    {
        internal readonly Animator Source;
        internal readonly EffectNativeModuleClock[] Layers;
        internal AnimatorBinding(Animator source)
        {
            Source = source; Layers = new EffectNativeModuleClock[Math.Min(32, source.layerCount)];
            for (int i = 0; i < Layers.Length; i++) Layers[i] = new EffectNativeModuleClock();
        }
    }
    private readonly AnimatorBinding[] animators;
    private readonly ParticleCountCuller?[] cullers;
    private readonly float[] attenuation;
    private readonly bool hasCullers;
    private float fireProgress = float.NaN;
    internal long BaselineModuleReads { get; private set; }
    internal long EventModuleReads { get; private set; }
    internal long AnimatedModuleReads { get; private set; }
    internal long DistanceModuleReads { get; private set; }
    internal long UnknownAnimationModuleReads { get; private set; }
    internal long CullerProbes { get; private set; }
    internal long NativeAnimationProbes { get; private set; }
    internal long TransformReads { get; private set; }
    internal long NativeParticleStateReads => 0;
    internal bool HasCheckpoints => false;
    private double next;
    internal EffectParticleCapture(ParticleSystem[] particles)
    {
        bindings = new Binding[particles.Length];
        for (int i = 0; i < particles.Length; i++) bindings[i] = new Binding(particles[i], particles[0].transform);
        cullers = new ParticleCountCuller?[particles.Length]; attenuation = new float[particles.Length];
        for (int i = 0; i < particles.Length; i++)
        { cullers[i] = particles[i].GetComponent<ParticleCountCuller>(); attenuation[i] = float.NaN; hasCullers |= cullers[i]; }
        var campfire = particles[0].GetComponentInParent<Campfire>(true);
        if (campfire && Array.IndexOf(particles, campfire.fireParticles) >= 0) changingFire = campfire;
        var controllers = new HashSet<Animator>(particles[0].GetComponentsInChildren<Animator>(true));
        foreach (var parent in particles[0].GetComponentsInParent<Animator>(true)) controllers.Add(parent);
        animators = new AnimatorBinding[controllers.Count]; int index = 0;
        foreach (var controller in controllers) animators[index++] = new AnimatorBinding(controller);
        var bees = particles[0].GetComponentInParent<BeeSwarm>(true);
        if (bees) beeMaterial = bees.bingBongMaterial;
        if (EffectParticleForces.TryAuditedBeeField(particles[0], out var field)) force = field;
    }
    internal void Invalidate(double now = 0, double animationSeconds = 0)
    { next = double.NegativeInfinity; moduleReads.Invalidate(now, animationSeconds); }
    internal void Sample(double time, EffectParticleFrame[]? previous, EffectForceFieldFrame? previousForce,
        out EffectParticleFrame[]? result, out EffectForceFieldFrame? resultForce, bool followSceneClock = false, double rootPhase = 0)
    {
        result = previous; resultForce = previousForce;
        if (previous != null && !followSceneClock && time < next) return;
        next = time + 1d / 30;
        float progress = changingFire ? changingFire!.LitProgress : fireProgress;
        bool cullingChanged = false;
        if (hasCullers && moduleReads.ProbeCulling(time))
            for (int i = 0; i < cullers.Length; i++) if (cullers[i])
            {
                CullerProbes++;
                float value = cullers[i]!.attenuationVal;
                if (value != attenuation[i]) { cullingChanged = true; attenuation[i] = value; }
            }
        bool nativeAnimation = NativeAnimation(out bool unknownAnimation);
        bool fireChanged = changingFire && progress != fireProgress;
        bool modules = moduleReads.Due(time, previous != null, fireChanged || nativeAnimation, cullingChanged, unknownAnimation);
        bool eventRead = moduleReads.EventPending;
        for (int i = 0; i < bindings.Length; i++)
        {
            var old = previous != null && i < previous.Length ? previous[i] : null;
            var binding = bindings[i];
            if (!binding.Particle)
            {
                if (old != null && !old.Pose.Active) continue;
                var inactive = old == null ? Inactive : new EffectParticleFrame(new NodePose("", old.Pose.Position, old.Pose.Rotation, old.Pose.Scale, false, false), old.Colors, old.Parameters)
                    { ColorMode = old.ColorMode, MaterialVariant = old.MaterialVariant };
                if (result == null) result = new EffectParticleFrame[bindings.Length];
                else if (ReferenceEquals(result, previous)) result = (EffectParticleFrame[])result.Clone();
                result[i] = inactive; continue;
            }
            var main = binding.Main; var emission = binding.Emission;
            bool readModules = modules || old == null || !old.Pose.Active && binding.Particle.gameObject.activeSelf;
            Span<float> parameters = stackalloc float[EffectReplayRules.ParticleParameterCount];
            Span<float> colors = stackalloc float[8];
            int colorMode = old?.ColorMode ?? 0, materialVariant = old?.MaterialVariant ?? 0;
            bool emissionEnabled = old?.EmissionEnabled ?? true;
            if (readModules)
            {
            if (old == null) BaselineModuleReads++;
            else if (eventRead) EventModuleReads++;
            else if (fireChanged || nativeAnimation || !modules) AnimatedModuleReads++;
            else if (cullingChanged) DistanceModuleReads++;
            else if (unknownAnimation) UnknownAnimationModuleReads++;
            else AnimatedModuleReads++;
            Curve(main.startSize, parameters.Slice(0, 4)); Curve(emission.rateOverTime, parameters.Slice(4, 4));
            Curve(emission.rateOverDistance, parameters.Slice(8, 4)); parameters[12] = binding.Shape.radius; parameters[13] = main.simulationSpeed;
            var color = main.startColor; colorMode = (int)color.mode; emissionEnabled = emission.enabled;
            // Unity does not initialize colour union slots that the selected
            // mode does not use (observed colorMin ~= -1e37 in Color mode).
            // Never read those native slots, even for comparison or diagnostics.
            if (color.mode == ParticleSystemGradientMode.Color) Color(color.color, colors.Slice(4));
            else if (color.mode == ParticleSystemGradientMode.TwoColors)
            { Color(color.colorMin, colors); Color(color.colorMax, colors.Slice(4)); }
            EffectReplayRules.CanonicalizeParticleColors((int)color.mode, colors);
            materialVariant = beeMaterial && binding.Renderer && binding.Renderer.sharedMaterial == beeMaterial ? 1 : 0;
            binding.CustomSpace = main.simulationSpace == ParticleSystemSimulationSpace.Custom ? main.customSimulationSpace : null;
            }
            if (i != 0) TransformReads++;
            var pose = Pose(old?.Pose, binding.Transform, i == 0,
                binding.Particle.gameObject.activeSelf, binding.Renderer && binding.Renderer.enabled);
            ObjectPose? space = binding.CustomSpace ? Space(old?.SimulationSpace, binding.CustomSpace!) : null;
            ObjectPose? meshShape = binding.MeshSurface ? Space(old?.MeshShapeSpace, binding.MeshSurface!) : null;
            if (binding.CustomSpace) TransformReads++;
            if (binding.MeshSurface) TransformReads++;
            bool emitting = binding.Particle.isEmitting, paused = binding.Particle.isPaused;
            bool sameParameters = !readModules || Same(old?.Parameters, parameters), sameColors = !readModules || Same(old?.Colors, colors);
            if (old != null && ReferenceEquals(pose, old.Pose) && ReferenceEquals(space, old.SimulationSpace) && ReferenceEquals(meshShape, old.MeshShapeSpace) &&
                old.Checkpoint == null && sameParameters && sameColors && old.Emitting == emitting && old.Paused == paused &&
                old.EmissionEnabled == emissionEnabled && old.ColorMode == colorMode && old.MaterialVariant == materialVariant) continue;
            if (result == null) result = new EffectParticleFrame[bindings.Length];
            else if (ReferenceEquals(result, previous)) result = (EffectParticleFrame[])result.Clone();
            result[i] = new EffectParticleFrame(pose, sameColors ? old!.Colors : colors.ToArray(), sameParameters ? old!.Parameters : parameters.ToArray())
                { SimulationSpace = space, MeshShapeSpace = meshShape, Emitting = emitting, Paused = paused, MaterialVariant = materialVariant,
                    EmissionEnabled = emissionEnabled, ColorMode = colorMode };
        }
        if (force)
        {
            Span<float> gravity = stackalloc float[4];
            bool readGravity = modules || previousForce == null;
            if (readGravity) Curve(force!.gravity, gravity);
            var pose = Space(previousForce?.Pose, force.transform);
            TransformReads++;
            bool enabled = force.enabled && force.gameObject.activeInHierarchy;
            bool sameGravity = !readGravity || Same(previousForce?.Gravity, gravity);
            if (previousForce == null || !ReferenceEquals(pose, previousForce.Pose) || enabled != previousForce.Enabled || !sameGravity)
                resultForce = new EffectForceFieldFrame(pose, sameGravity ? previousForce!.Gravity : gravity.ToArray()) { Enabled = enabled };
        }
        else resultForce = null;
        if (modules)
        {
            moduleReads.Complete(time); fireProgress = progress;
        }
    }
    private bool NativeAnimation(out bool unknown)
    {
        bool read = false; unknown = false;
        foreach (var binding in animators)
        {
            var source = binding.Source;
            bool enabled; int layerCount;
            try
            {
                enabled = source && source.isActiveAndEnabled && source.runtimeAnimatorController;
                layerCount = enabled ? source!.layerCount : 0;
            }
            catch { unknown = true; continue; }
            for (int layer = 0; layer < binding.Layers.Length; layer++)
            {
                try
                {
                    NativeAnimationProbes++;
                    bool evaluates = enabled && layer < layerCount && (layer == 0 || source!.GetLayerWeight(layer) > 0);
                    var state = evaluates ? source!.GetCurrentAnimatorStateInfo(layer) : default;
                    bool transition = evaluates && source!.IsInTransition(layer);
                    bool clips = evaluates && source!.GetCurrentAnimatorClipInfoCount(layer) != 0;
                    float rate = evaluates ? source!.speed * state.speed * state.speedMultiplier : 0;
                    if (evaluates && (float.IsNaN(state.normalizedTime) || float.IsInfinity(state.normalizedTime) ||
                        float.IsNaN(rate) || float.IsInfinity(rate))) unknown = true;
                    read |= binding.Layers[layer].Observe(evaluates, clips, state.fullPathHash, state.normalizedTime,
                        state.loop, transition, rate);
                }
                // A controller or layer can be replaced at runtime. Preserve a
                // bounded conservative module probe until native queries work.
                catch { unknown |= enabled; }
            }
            if (enabled && (layerCount == 0 || layerCount > binding.Layers.Length)) unknown = true;
        }
        return read;
    }
    private static readonly EffectParticleFrame Inactive = new(new NodePose { Active = false, Visible = false }, new float[8], new float[EffectReplayRules.ParticleParameterCount]);
    internal static void Curve(ParticleSystem.MinMaxCurve value, Span<float> output)
    {
        output[0] = (int)value.mode;
        if (value.mode == ParticleSystemCurveMode.Constant) output[2] = value.constant;
        else if (value.mode == ParticleSystemCurveMode.TwoConstants) { output[1] = value.constantMin; output[2] = value.constantMax; }
        else output[3] = value.curveMultiplier;
        EffectReplayRules.CanonicalizeParticleCurve((int)value.mode, output);
    }
    internal static ParticleSystem.MinMaxCurve ApplyCurve(ParticleSystem.MinMaxCurve value, float[] input, int offset)
    {
        value.mode = (ParticleSystemCurveMode)(int)input[offset];
        if (value.mode == ParticleSystemCurveMode.Constant) value.constant = input[offset + 2];
        else if (value.mode == ParticleSystemCurveMode.TwoConstants) { value.constantMin = input[offset + 1]; value.constantMax = input[offset + 2]; }
        else value.curveMultiplier = input[offset + 3];
        return value;
    }
    private static void Color(Color value, Span<float> output)
    { output[0] = value.r; output[1] = value.g; output[2] = value.b; output[3] = value.a; }
    internal static bool Same(float[]? previous, ReadOnlySpan<float> values)
    {
        if (previous == null || previous.Length != values.Length) return false;
        for (int i = 0; i < values.Length; i++) if (previous[i] != values[i]) return false;
        return true;
    }
    private static NodePose Pose(NodePose? old, Transform transform, bool root, bool active, bool visible)
    {
        Vector3 p; Quaternion r;
        if (root) { p = Vector3.zero; r = Quaternion.identity; }
        else transform.GetLocalPositionAndRotation(out p, out r);
        var s = root ? Vector3.one : transform.localScale;
        Span<float> position = stackalloc float[] { p.x, p.y, p.z };
        Span<float> rotation = stackalloc float[] { r.x, r.y, r.z, r.w };
        Span<float> scale = stackalloc float[] { s.x, s.y, s.z };
        bool sameP = Same(old?.Position, position), sameR = Same(old?.Rotation, rotation), sameS = Same(old?.Scale, scale);
        if (old != null && sameP && sameR && sameS && old.Active == active && old.Visible == visible) return old;
        return new NodePose("", sameP ? old!.Position : position.ToArray(), sameR ? old!.Rotation : rotation.ToArray(),
            sameS ? old!.Scale : scale.ToArray(), active, visible);
    }
    private static ObjectPose Space(ObjectPose? old, Transform transform)
    {
        transform.GetPositionAndRotation(out var p, out var r); var s = transform.lossyScale;
        Span<float> trs = stackalloc float[] { p.x, p.y, p.z, r.x, r.y, r.z, r.w, s.x, s.y, s.z };
        return CreatureDirectoryRules.StaticPose(old, transform.gameObject.activeInHierarchy, trs);
    }
}

internal sealed class EffectParticlePlayback
{
    private readonly ParticleSystem[] systems;
    private readonly ParticleSystem[] sources;
    private readonly ParticleSystemRenderer[] renderers;
    private readonly Material?[] defaultMaterials;
    private readonly Material? beeMaterial;
    private readonly ParticleSystemForceField? forceSource;
    private readonly Transform?[] spaces;
    private readonly Transform?[] meshSurfaces;
    private readonly List<int> worldBindings = new();
    private readonly Vector3[] meshPositions, meshScales;
    private readonly Quaternion[] meshRotations;
    private readonly bool[] emitting;
    private readonly List<GameObject> owned;
    private readonly Transform root;
    private EffectParticleFrame[]? applied;
    private EffectForceFieldFrame? appliedForce;
    private ParticleSystemForceField? force;
    private float rootRate = float.NaN;
    internal EffectParticlePlayback(ParticleSystem[] sources, ParticleSystem[] systems, Transform root, List<GameObject> owned)
    {
        this.sources = sources; this.systems = systems; this.root = root; this.owned = owned;
        spaces = new Transform?[systems.Length]; emitting = new bool[systems.Length];
        meshSurfaces = new Transform?[systems.Length]; meshPositions = new Vector3[systems.Length];
        meshScales = new Vector3[systems.Length]; meshRotations = new Quaternion[systems.Length];
        renderers = new ParticleSystemRenderer[systems.Length];
        defaultMaterials = new Material?[systems.Length];
        for (int i = 0; i < systems.Length; i++) { renderers[i] = systems[i].GetComponent<ParticleSystemRenderer>(); defaultMaterials[i] = renderers[i] ? renderers[i].sharedMaterial : null; }
        var bee = sources[0].GetComponentInParent<BeeSwarm>(true); if (bee) beeMaterial = bee.bingBongMaterial;
        if (EffectParticleForces.TryAuditedBeeField(sources[0], out var field)) forceSource = field;
    }
    internal IEnumerable<int> Build()
    {
        for (int i = 0; i < sources.Length; i++)
        {
            if (!EffectParticleSurfaces.TryStaticSurface(sources[i], out var source)) continue;
            var go = new GameObject("Recorded particle emission surface"); owned.Add(go);
            go.transform.SetParent(systems[i].transform, false); meshSurfaces[i] = go.transform;
            worldBindings.Add(i);
            var emitter = sources[i].transform;
            meshPositions[i] = emitter.InverseTransformPoint(source.transform.position);
            meshRotations[i] = Quaternion.Inverse(emitter.rotation) * source.transform.rotation;
            var parentScale = emitter.lossyScale; var sourceScale = source.transform.lossyScale;
            meshScales[i] = new Vector3(parentScale.x == 0 ? 0 : sourceScale.x / parentScale.x,
                parentScale.y == 0 ? 0 : sourceScale.y / parentScale.y, parentScale.z == 0 ? 0 : sourceScale.z / parentScale.z);
            RestoreMeshSurface(i); yield return 0;
            go.AddComponent<MeshFilter>().sharedMesh = source.GetComponent<MeshFilter>().sharedMesh; yield return 0;
            var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterials = source.sharedMaterials;
            renderer.forceRenderingOff = true; yield return 0;
            var shape = systems[i].shape; shape.meshRenderer = renderer; shape.enabled = sources[i].shape.enabled; yield return 0;
        }
        for (int i = 0; i < sources.Length; i++)
        {
            // A torch receives its custom Transform after cloning; its prefab
            // already selects Custom. Allocate only that family's passive space
            // during budgeted build and bind only for schema-15 observations.
            if (sources[i].main.simulationSpace != ParticleSystemSimulationSpace.Custom) continue;
            var go = new GameObject("Recorded particle space"); owned.Add(go); go.transform.SetParent(root, false); spaces[i] = go.transform;
            if (!worldBindings.Contains(i)) worldBindings.Add(i);
            yield return 0;
        }
        if (!forceSource) yield break;
        var fieldObject = new GameObject("Recorded bee force field"); owned.Add(fieldObject); fieldObject.transform.SetParent(root, false);
        force = fieldObject.AddComponent<ParticleSystemForceField>(); force.enabled = false; yield return 0;
        // Only a visual force field is copied. No BeeSwarm, rigidbody, collider,
        // status field or gameplay callback is created on the replay object.
        foreach (var property in typeof(ParticleSystemForceField).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Type t = property.PropertyType;
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0 || property.Name == "enabled" ||
                !(t == typeof(float) || t == typeof(int) || t == typeof(bool) || t.IsEnum || t == typeof(ParticleSystem.MinMaxCurve) || t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Texture3D))) continue;
            property.SetValue(force, property.GetValue(forceSource)); yield return 0;
        }
        for (int i = 0; i < systems.Length; i++)
        {
            if (!EffectParticleForces.TryAuditedBeeField(sources[i], out var field) || field != forceSource) continue;
            var particle = systems[i];
            var external = particle.externalForces; external.enabled = true;
            external.influenceFilter = ParticleSystemGameObjectFilter.List;
            external.RemoveAllInfluences(); external.AddInfluence(force); yield return 0;
        }
    }
    internal void ResetEmission(bool value)
    { for (int i = 0; i < emitting.Length; i++) emitting[i] = value; applied = null; appliedForce = null; rootRate = float.NaN; }
    internal void RestoredEmission(EffectReplayFrame state)
    {
        for (int i = 0; i < emitting.Length; i++)
            emitting[i] = state.Particles![i].Pose.Active && state.Particles[i].Emitting && (i != 0 || state.Emitting);
    }
    internal void PresentWorldSpaces(EffectReplayFrame frame)
    {
        // Final presentation may move the emitter after its last history slice.
        // These private child transforms represent recorded WORLD coordinates;
        // re-anchor only those cached bindings without advancing any particles.
        var states = frame.Particles;
        if (states == null || states.Length != systems.Length) return;
        foreach (int i in worldBindings)
        {
            var state = states[i];
            if (state.SimulationSpace != null && spaces[i]) SetSpace(spaces[i]!, state.SimulationSpace);
            if (state.MeshShapeSpace != null && meshSurfaces[i]) SetSpace(meshSurfaces[i]!, state.MeshShapeSpace);
        }
        if (force && frame.ForceField != null) SetSpace(force!.transform, frame.ForceField.Pose);
    }
    internal bool Apply(EffectReplayFrame frame)
    {
        var states = frame.Particles;
        if (states == null || states.Length != systems.Length) return false;
        for (int i = 0; i < systems.Length; i++)
        {
            var state = states[i]; var previous = applied != null ? applied[i] : null;
            if (!ReferenceEquals(state, previous))
            {
                var transform = systems[i].transform;
                if (i != 0)
                {
                    transform.localPosition = Point(state.Pose.Position); transform.localRotation = Rotation(state.Pose.Rotation);
                    transform.localScale = Point(state.Pose.Scale); transform.gameObject.SetActive(state.Pose.Active);
                }
                if (renderers[i]) renderers[i].enabled = state.Pose.Visible;
                if (renderers[i] && (previous == null || state.MaterialVariant != previous.MaterialVariant))
                    renderers[i].sharedMaterial = state.MaterialVariant == 1 && beeMaterial ? beeMaterial : defaultMaterials[i];
                var main = systems[i].main; var emission = systems[i].emission;
                if (previous == null || !ReferenceEquals(state.Colors, previous.Colors) || state.ColorMode != previous.ColorMode)
                {
                    var color = main.startColor; color.mode = (ParticleSystemGradientMode)state.ColorMode;
                    if (color.mode == ParticleSystemGradientMode.Color) color.color = Color(state.Colors, 4);
                    else if (color.mode == ParticleSystemGradientMode.TwoColors) { color.colorMin = Color(state.Colors, 0); color.colorMax = Color(state.Colors, 4); }
                    main.startColor = color;
                }
                if (previous == null || !ReferenceEquals(state.Parameters, previous.Parameters))
                {
                    main.startSize = EffectParticleCapture.ApplyCurve(main.startSize, state.Parameters, 0);
                    emission.rateOverTime = EffectParticleCapture.ApplyCurve(emission.rateOverTime, state.Parameters, 4);
                    emission.rateOverDistance = EffectParticleCapture.ApplyCurve(emission.rateOverDistance, state.Parameters, 8);
                    var shape = systems[i].shape; shape.radius = state.Parameters[12];
                }
                emission.enabled = state.EmissionEnabled;
            }
            if (previous == null || !ReferenceEquals(state.Parameters, previous.Parameters) || rootRate != frame.Rate)
            { var main = systems[i].main; main.simulationSpeed = frame.Rate > .000001f ? state.Parameters[13] / frame.Rate : 0; }
            if (state.SimulationSpace != null && spaces[i])
            {
                var main = systems[i].main; main.simulationSpace = ParticleSystemSimulationSpace.Custom; main.customSimulationSpace = spaces[i];
                SetSpace(spaces[i]!, state.SimulationSpace);
            }
            else if (previous?.SimulationSpace != null)
            { var main = systems[i].main; main.customSimulationSpace = null; main.simulationSpace = sources[i].main.simulationSpace == ParticleSystemSimulationSpace.Custom ? ParticleSystemSimulationSpace.World : sources[i].main.simulationSpace; }
            if (state.MeshShapeSpace != null && meshSurfaces[i]) SetSpace(meshSurfaces[i]!, state.MeshShapeSpace);
            else if (meshSurfaces[i] && (previous == null || previous.MeshShapeSpace != null)) RestoreMeshSurface(i);
        }
        if (force)
        {
            if (frame.ForceField != null)
            {
                SetSpace(force!.transform, frame.ForceField.Pose); force.enabled = frame.ForceField.Enabled && frame.ForceField.Pose.Active;
                if (!ReferenceEquals(frame.ForceField, appliedForce))
                    force.gravity = EffectParticleCapture.ApplyCurve(force.gravity, frame.ForceField.Gravity, 0);
            }
            else force!.enabled = false;
        }
        applied = states; appliedForce = frame.ForceField; rootRate = frame.Rate; return true;
    }
    internal void Step(float delta, bool rootEmitting)
    {
        for (int i = 0; i < systems.Length; i++)
        {
            var state = applied![i]; var particle = systems[i];
            bool allow = state.Pose.Active && state.Emitting && (i != 0 || rootEmitting);
            if (emitting[i] != allow)
            {
                if (allow) particle.Play(false); else particle.Stop(false, ParticleSystemStopBehavior.StopEmitting);
                emitting[i] = allow;
            }
            if (state.Pose.Active && !state.Paused) particle.Simulate(Math.Max(0, delta), false, false, false);
            particle.Pause(false);
        }
    }
    private static Vector3 Point(float[] p) => new(p[0], p[1], p[2]);
    private void RestoreMeshSurface(int index)
    {
        var surface = meshSurfaces[index]!;
        surface.SetLocalPositionAndRotation(meshPositions[index], meshRotations[index]); surface.localScale = meshScales[index];
    }
    private static Quaternion Rotation(float[] q) => new(q[0], q[1], q[2], q[3]);
    private static Color Color(float[] c, int at) => new(c[at], c[at + 1], c[at + 2], c[at + 3]);
    private static void SetSpace(Transform transform, ObjectPose pose)
    {
        transform.SetPositionAndRotation(Point(pose.Position), Rotation(pose.Rotation));
        var parent = transform.parent; var scale = Point(pose.Scale);
        if (parent)
        {
            var p = parent.lossyScale;
            scale = new Vector3(p.x == 0 ? 0 : scale.x / p.x, p.y == 0 ? 0 : scale.y / p.y, p.z == 0 ? 0 : scale.z / p.z);
        }
        transform.localScale = scale;
    }
}
