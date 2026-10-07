using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace PeakReplayLab;

internal static class NativeVisualAppearance
{
    private sealed class PropertySpec
    { public string Name = ""; public int Id, Kind; public bool Integer; }
    private static readonly ConditionalWeakTable<Shader, PropertySpec[]> shaderProperties = new();
    private static PropertySpec[] Properties(Shader shader) => shaderProperties.GetValue(shader, source =>
    {
        var result = new List<PropertySpec>();
        foreach (var allowed in NativeVisualAppearanceRules.Properties)
        {
            int index = source.FindPropertyIndex(allowed.Name); if (index < 0) continue;
            var type = source.GetPropertyType(index);
            int kind = type == ShaderPropertyType.Color ? 1 : type == ShaderPropertyType.Vector ? 2 : type == ShaderPropertyType.Texture ? 3 : 0;
            if (!NativeVisualAppearanceRules.AllowedProperty(allowed.Name, kind)) continue;
            result.Add(new PropertySpec { Name = allowed.Name, Id = Shader.PropertyToID(allowed.Name), Kind = kind, Integer = type == ShaderPropertyType.Int });
        }
        return result.ToArray();
    });
    private sealed class SourceRenderer
    {
        public Renderer Renderer = null!;
        public MeshFilter? Filter;
        public string Path = "", MeshIdentity = "";
        public int Index;
        public bool Animated;
        public readonly List<Material> Materials = new();
    }
    private sealed class Probe
    {
        public readonly List<SourceRenderer> Renderers = new();
        public readonly Dictionary<Transform, string> Paths = new();
        public readonly MaterialPropertyBlock Global = new();
        public readonly MaterialPropertyBlock Slot = new();
        public readonly NativeCapturePolicy Schedule;
        public bool Animated;
        public int MaximumNodes;
        public Probe(Transform root, int maximumNodes)
        {
            MaximumNodes = maximumNodes; Schedule = new NativeCapturePolicy(root.GetInstanceID());
            Visit(root, ".", 0);
            void Visit(Transform node, string path, int depth)
            {
                if (node != root && node.GetComponent<Item>()) return;
                if (depth > 64 || Paths.Count >= maximumNodes) throw new InvalidOperationException("Native appearance hierarchy exceeds its recording limit.");
                Paths.Add(node, path);
                int index = 0;
                foreach (var renderer in node.GetComponents<Renderer>())
                    if (renderer is MeshRenderer || renderer is SkinnedMeshRenderer)
                    {
                        if (Renderers.Count >= NativeVisualAppearanceRules.MaximumRenderers || index >= NativeVisualAppearanceRules.MaximumMaterials)
                            throw new InvalidOperationException("Native appearance renderer count exceeds its recording limit.");
                        Renderers.Add(new SourceRenderer { Renderer = renderer, Filter = renderer.GetComponent<MeshFilter>(), Path = path, Index = index++ });
                    }
                for (int i = 0; i < node.childCount; i++)
                {
                    var child = node.GetChild(i);
                    Visit(child, (path == "." ? "" : path + "/") + i.ToString(CultureInfo.InvariantCulture) + ":" + Uri.EscapeDataString(child.name), depth + 1);
                }
            }
        }
    }
    private static readonly ConditionalWeakTable<Transform, Probe> probes = new();

    public static void InvalidateCapture(Transform root) { if (root) probes.Remove(root); }

    public static bool NeedsCaptureAtTime(Transform source, NativeRendererFrame[]? previous, double now, bool active = true, bool allowAnimated = false)
    {
        if (previous == null) return false; // The owning track supplies initial/lifecycle reads.
        if (!probes.TryGetValue(source, out var probe)) return true;
        return probe.Schedule.NeedsRead(now, active, allowAnimated && probe.Animated);
    }

    // Full static material/renderer metadata is independent of root motion.
    // Continuous native controls such as flame clipping and emissive pulses
    // keep a separate 30 Hz path; native notifications force a full read now.
    public static NativeRendererFrame[] CaptureAtTime(Transform source, NativeRendererFrame[]? previous,
        double now, bool dirty = false, int maximumNodes = 256, bool allowAnimated = false)
    {
        if (!source) throw new ArgumentNullException(nameof(source));
        var probe = GetProbe(source, maximumNodes);
        if (probe.Schedule.FullDue(now, previous != null, dirty))
        {
            var value = Capture(source, previous, maximumNodes);
            probe.Schedule.FullObserved(now); return value;
        }
        if (!allowAnimated || !probe.Animated || !source.gameObject.activeInHierarchy || !probe.Schedule.AnimatedDue(now)) return previous!;
        var animated = Capture(source, previous, maximumNodes, animatedOnly: true);
        probe.Schedule.AnimatedObserved(now); return animated;
    }

    private static Probe GetProbe(Transform source, int maximumNodes)
    {
        if (maximumNodes < 1 || maximumNodes > 1024) throw new ArgumentOutOfRangeException(nameof(maximumNodes));
        if (!probes.TryGetValue(source, out var probe))
        { probe = new Probe(source, maximumNodes); probes.Add(source, probe); }
        else if (maximumNodes > probe.MaximumNodes)
        { probes.Remove(source); probe = new Probe(source, maximumNodes); probes.Add(source, probe); }
        return probe;
    }

    public static NativeRendererFrame[] Capture(Transform source, NativeRendererFrame[]? previous = null, int maximumNodes = 256, bool animatedOnly = false)
    {
        if (!source) throw new ArgumentNullException(nameof(source));
        var probe = GetProbe(source, maximumNodes);
        using var timing = ReplayPerformance.Measure(ReplayStage.Appearance);
        if (!animatedOnly) probe.Animated = false;
        NativeRendererFrame[]? changed = previous == null || previous.Length != probe.Renderers.Count ? new NativeRendererFrame[probe.Renderers.Count] : null;
        for (int i = 0; i < probe.Renderers.Count; i++)
        {
            var entry = probe.Renderers[i]; var renderer = entry.Renderer;
            if (animatedOnly && !entry.Animated) continue;
            if (!renderer) { probes.Remove(source); return previous ?? Array.Empty<NativeRendererFrame>(); }
            var old = previous != null && i < previous.Length ? previous[i] : null;
            var nativeMaterials = entry.Materials;
            if (!animatedOnly)
            {
                renderer.GetSharedMaterials(nativeMaterials); entry.Animated = false;
                foreach (var material in nativeMaterials)
                    if (material && material.shader)
                        foreach (var property in Properties(material.shader)) entry.Animated |= AnimatedProperty(property.Name);
                probe.Animated |= entry.Animated;
            }
            if (nativeMaterials.Count > NativeVisualAppearanceRules.MaximumMaterials) throw new InvalidOperationException("Native material count exceeds its recording limit.");
            renderer.GetPropertyBlock(probe.Global);
            NativeMaterialFrame[]? materials = old == null || old.Materials.Length != nativeMaterials.Count ? new NativeMaterialFrame[nativeMaterials.Count] : null;
            for (int slot = 0; slot < nativeMaterials.Count; slot++)
            {
                var material = nativeMaterials[slot];
                var prior = old != null && slot < old.Materials.Length ? old.Materials[slot] : null;
                renderer.GetPropertyBlock(probe.Slot, slot);
                // Unity ignores the entire renderer-wide block when a per-slot block
                // exists, including properties that are absent from that slot block.
                var block = probe.Slot.isEmpty ? probe.Global : probe.Slot;
                var properties = ReadProperties(material, block, prior?.Properties, animatedOnly);
                string name = animatedOnly && prior != null ? prior.Name : AssetName(material), shader = animatedOnly && prior != null ? prior.Shader : material && material.shader ? AssetName(material.shader) : "";
                int queue = animatedOnly && prior != null ? prior.RenderQueue : material ? material.renderQueue : -1;
                var value = prior != null && name == prior.Name && shader == prior.Shader && queue == prior.RenderQueue && ReferenceEquals(properties, prior.Properties) ? prior :
                    new NativeMaterialFrame { Name = name, Shader = shader, RenderQueue = queue, Properties = properties };
                if (materials != null) materials[slot] = value;
                else if (!ReferenceEquals(value, prior)) { materials = (NativeMaterialFrame[])old!.Materials.Clone(); materials[slot] = value; }
            }
            var appearanceMaterials = materials ?? old!.Materials;
            if (animatedOnly && old != null)
            {
                bool visible = renderer.enabled && !renderer.forceRenderingOff;
                var animated = old.Enabled == visible && ReferenceEquals(old.Materials, appearanceMaterials) ? old :
                    new NativeRendererFrame { Path = old.Path, Index = old.Index, Mesh = old.Mesh, Enabled = visible, Layer = old.Layer,
                        RenderingLayerMask = old.RenderingLayerMask, LightProbeUsage = old.LightProbeUsage, ReflectionProbeUsage = old.ReflectionProbeUsage,
                        ShadowCastingMode = old.ShadowCastingMode, ReceiveShadows = old.ReceiveShadows, SortingLayerId = old.SortingLayerId,
                        SortingOrder = old.SortingOrder, ProbeAnchor = old.ProbeAnchor, Materials = appearanceMaterials };
                if (changed != null) changed[i] = animated;
                else if (!ReferenceEquals(animated, old)) { changed = (NativeRendererFrame[])previous!.Clone(); changed[i] = animated; }
                continue;
            }
            string anchor = renderer.probeAnchor && probe.Paths.TryGetValue(renderer.probeAnchor, out var path) ? path : "";
            bool enabled = renderer.enabled && !renderer.forceRenderingOff;
            if (!animatedOnly) entry.MeshIdentity = MeshIdentity(renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : entry.Filter ? entry.Filter!.sharedMesh : null);
            string mesh = entry.MeshIdentity;
            var frame = old != null && old.Path == entry.Path && old.Index == entry.Index && old.Mesh == mesh && old.Enabled == enabled &&
                old.Layer == renderer.gameObject.layer && old.RenderingLayerMask == renderer.renderingLayerMask && old.LightProbeUsage == (int)renderer.lightProbeUsage &&
                old.ReflectionProbeUsage == (int)renderer.reflectionProbeUsage && old.ShadowCastingMode == (int)renderer.shadowCastingMode && old.ReceiveShadows == renderer.receiveShadows &&
                old.SortingLayerId == renderer.sortingLayerID && old.SortingOrder == renderer.sortingOrder && old.ProbeAnchor == anchor && ReferenceEquals(old.Materials, appearanceMaterials) ? old :
                new NativeRendererFrame { Path = entry.Path, Index = entry.Index, Mesh = mesh, Enabled = enabled, Layer = renderer.gameObject.layer,
                    RenderingLayerMask = renderer.renderingLayerMask, LightProbeUsage = (int)renderer.lightProbeUsage, ReflectionProbeUsage = (int)renderer.reflectionProbeUsage,
                    ShadowCastingMode = (int)renderer.shadowCastingMode, ReceiveShadows = renderer.receiveShadows, SortingLayerId = renderer.sortingLayerID,
                    SortingOrder = renderer.sortingOrder, ProbeAnchor = anchor, Materials = appearanceMaterials };
            if (changed != null) changed[i] = frame;
            else if (!ReferenceEquals(frame, old)) { changed = (NativeRendererFrame[])previous!.Clone(); changed[i] = frame; }
        }
        return changed ?? previous!;
    }

    private static NativeShaderPropertyFrame[] ReadProperties(Material material, MaterialPropertyBlock block, NativeShaderPropertyFrame[]? previous, bool animatedOnly)
    {
        if (!material || !material.shader) return Array.Empty<NativeShaderPropertyFrame>();
        List<NativeShaderPropertyFrame>? changed = null;
        int index = 0;
        foreach (var property in Properties(material.shader))
        {
            int id = property.Id;
            bool overridden = block.HasProperty(id);
            var old = previous != null && index < previous.Length ? previous[index] : null;
            if (animatedOnly && !AnimatedProperty(property.Name) && old != null && old.Name == property.Name)
            { if (changed != null) changed.Add(old); index++; continue; }
            float x = 0, y = 0, z = 0, w = 0; string texture = "";
            if (property.Kind == 0) x = property.Integer ? overridden ? block.GetInteger(id) : material.GetInteger(id) : overridden ? block.GetFloat(id) : material.GetFloat(id);
            else if (property.Kind == 1)
            { var c = overridden ? block.GetColor(id) : material.GetColor(id); x = c.r; y = c.g; z = c.b; w = c.a; }
            else if (property.Kind == 2)
            { var v = overridden ? block.GetVector(id) : material.GetVector(id); x = v.x; y = v.y; z = v.z; w = v.w; }
            else texture = AssetName(overridden ? block.GetTexture(id) : material.GetTexture(id));
            if (!Finite(x) || !Finite(y) || !Finite(z) || !Finite(w)) continue;
            bool same = old != null && old.Name == property.Name && old.Kind == property.Kind && old.Texture == texture &&
                (property.Kind == 3 ? old.Values.Length == 0 : property.Kind == 0 ? old.Values.Length == 1 && old.Values[0] == x :
                    old.Values.Length == 4 && old.Values[0] == x && old.Values[1] == y && old.Values[2] == z && old.Values[3] == w);
            var frame = same ? old! : new NativeShaderPropertyFrame { Name = property.Name, Kind = property.Kind, Texture = texture,
                Values = property.Kind == 3 ? Array.Empty<float>() : property.Kind == 0 ? new[] { x } : new[] { x, y, z, w } };
            if (changed != null) changed.Add(frame);
            else if (!same) { changed = previous == null ? new List<NativeShaderPropertyFrame>() : previous.Take(index).ToList(); changed.Add(frame); }
            index++;
        }
        if (changed != null) return changed.ToArray();
        return previous == null || previous.Length != index ? (previous ?? Array.Empty<NativeShaderPropertyFrame>()).Take(index).ToArray() : previous;
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= NativeVisualAppearanceRules.MaximumValue;
    private sealed class AssetIdentity { public string Name = ""; }
    private static readonly ConditionalWeakTable<UnityEngine.Object, AssetIdentity> assetNames = new();
    private static string AssetName(UnityEngine.Object? value) => value ? assetNames.GetValue(value!, source =>
        new AssetIdentity { Name = source.name.Replace(" (Instance)", "").Replace("(Instance)", "").Trim() }).Name : "";
    private static bool AnimatedProperty(string name) => name is "_Clip" or "_Spin" or "_RopeCutoff" or "_Alpha" or "_Opacity" or
        "_BreakAmount" or "_JitterAmount" or "_Glow" or "_GlowColor" or "_Emission" or "_EmissionStrength" or "_EmissionColor" or "_StatusGlow";
    private static Mesh? SharedMesh(Renderer renderer) => renderer is SkinnedMeshRenderer skin ? skin.sharedMesh :
        renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
    private static readonly ConditionalWeakTable<Mesh, AssetIdentity> meshNames = new();
    private static string MeshIdentity(Mesh? mesh) => mesh ? meshNames.GetValue(mesh!, source => new AssetIdentity
        { Name = AssetName(source) + "\n" + source.vertexCount.ToString(CultureInfo.InvariantCulture) + ":" + source.subMeshCount.ToString(CultureInfo.InvariantCulture) }).Name : "";

    internal sealed class Playback : IDisposable
    {
        private readonly VisualReplica replica;
        private readonly Dictionary<string, Material> materials = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Texture> textures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Mesh> meshes = new(StringComparer.Ordinal);
        private readonly Dictionary<(Renderer, int), (string Identity, Material Copy)> ownedSlotMaterials = new();
        private readonly Dictionary<Renderer, Material[]> authored = new();
        private readonly Dictionary<Renderer, Mesh?> authoredMeshes = new();
        private readonly Dictionary<Renderer, bool> authoredVisibility = new();
        private readonly NativeRendererFrame[] baseline;
        private readonly MaterialPropertyBlock block = new();
        private NativeRendererFrame[]? applied;
        private bool catalogExpanded;
        public Playback(VisualReplica replica, GameObject nativeSource)
        {
            this.replica = replica;
            foreach (var renderer in replica.Renderers)
            { authored[renderer] = renderer.sharedMaterials; authoredMeshes[renderer] = SharedMesh(renderer); authoredVisibility[renderer] = renderer.enabled; }
            foreach (var renderer in nativeSource.GetComponentsInChildren<Renderer>(true))
            { Register(SharedMesh(renderer)); foreach (var material in renderer.sharedMaterials) Register(material); }
            foreach (var variant in nativeSource.GetComponentsInChildren<ColorblindVariant>(true)) Register(variant.colorblindMaterial);
            baseline = Capture(replica.Root.transform, maximumNodes: 1024);
        }
        private void Register(Material material)
        {
            if (!material || !material.shader) return;
            string key = AssetName(material) + "\n" + material.shader.name;
            if (!materials.TryGetValue(key, out var existing)) materials.Add(key, material);
            else if (existing.name.Contains("(Instance)") && !material.name.Contains("(Instance)")) materials[key] = material;
            foreach (var property in NativeVisualAppearanceRules.Properties)
                if (property.Kind == 3 && material.HasProperty(property.Name)) Register(material.GetTexture(property.Name));
        }
        private void Register(Texture? texture)
        { if (texture && !textures.ContainsKey(AssetName(texture))) textures.Add(AssetName(texture), texture!); }
        private void Register(Mesh? mesh)
        { if (mesh && !meshes.ContainsKey(MeshIdentity(mesh))) meshes.Add(MeshIdentity(mesh), mesh!); }
        private void ExpandCatalog()
        {
            if (catalogExpanded) return; catalogExpanded = true;
            // Loaded native assets only. A recording cannot request shader creation,
            // file access or Resources.Load on an arbitrary supplied string.
            foreach (var material in Resources.FindObjectsOfTypeAll<Material>()) Register(material);
            foreach (var texture in Resources.FindObjectsOfTypeAll<Texture>()) Register(texture);
            foreach (var mesh in Resources.FindObjectsOfTypeAll<Mesh>()) Register(mesh);
        }
        public void Apply(NativeRendererFrame[]? frames, bool revealAuthoredRenderers = false)
        {
            // Visibility may have been reapplied by the hierarchy pose even when the
            // appearance snapshot itself did not change.
            if (frames == null)
            {
                if (applied != null)
                {
                    // Unknown older snapshots must not retain mesh/light/material state
                    // from a later recorded snapshot. Keep the hierarchy's current
                    // visibility while restoring all native authored renderer state.
                    var visibility = new Dictionary<Renderer, bool>();
                    foreach (var entry in authored) if (entry.Key) visibility[entry.Key] = entry.Key.enabled;
                    Apply(baseline);
                    foreach (var entry in visibility) if (entry.Key) entry.Key.enabled = entry.Value;
                }
                applied = null; return;
            }
            bool changed = !ReferenceEquals(applied, frames);
            foreach (var authored in baseline)
                if (replica.TryCloneRenderer(authored.Path, authored.Index, out var previous)) previous.enabled = false;
            foreach (var frame in frames)
            {
                if (!replica.TryCloneRenderer(frame.Path, frame.Index, out var renderer)) continue;
                renderer.enabled = revealAuthoredRenderers ? authoredVisibility[renderer] : frame.Enabled;
                if (!changed) continue;
                renderer.gameObject.layer = frame.Layer; renderer.renderingLayerMask = frame.RenderingLayerMask;
                renderer.lightProbeUsage = (LightProbeUsage)frame.LightProbeUsage; renderer.reflectionProbeUsage = (ReflectionProbeUsage)frame.ReflectionProbeUsage;
                renderer.shadowCastingMode = (ShadowCastingMode)frame.ShadowCastingMode; renderer.receiveShadows = frame.ReceiveShadows;
                renderer.sortingLayerID = frame.SortingLayerId; renderer.sortingOrder = frame.SortingOrder;
                renderer.probeAnchor = replica.TryCloneTransform(frame.ProbeAnchor, out var anchor) ? anchor : null;
                if (frame.Mesh.Length != 0)
                {
                    if (!meshes.TryGetValue(frame.Mesh, out var mesh)) { ExpandCatalog(); meshes.TryGetValue(frame.Mesh, out mesh); }
                    if (!mesh) mesh = authoredMeshes[renderer];
                    if (renderer is SkinnedMeshRenderer skin) skin.sharedMesh = mesh; else if (renderer.TryGetComponent<MeshFilter>(out var filter)) filter.sharedMesh = mesh;
                }
                else if (renderer is SkinnedMeshRenderer emptySkin) emptySkin.sharedMesh = null;
                else if (renderer.TryGetComponent<MeshFilter>(out var emptyFilter)) emptyFilter.sharedMesh = null;
                var defaults = authored[renderer]; var selected = new Material[frame.Materials.Length];
                for (int i = 0; i < selected.Length; i++)
                {
                    var recorded = frame.Materials[i]; string key = recorded.Name + "\n" + recorded.Shader;
                    var original = i < defaults.Length ? defaults[i] : null;
                    var slot = (renderer, i);
                    string identity = key + "\n" + recorded.RenderQueue.ToString(CultureInfo.InvariantCulture);
                    if (recorded.Name.Length == 0 && recorded.Shader.Length == 0) selected[i] = null!;
                    else if (ownedSlotMaterials.TryGetValue(slot, out var owned) && owned.Identity == identity && owned.Copy) selected[i] = owned.Copy;
                    else if (original && AssetName(original) == recorded.Name && original.shader && original.shader.name == recorded.Shader && original.renderQueue == recorded.RenderQueue)
                    {
                        selected[i] = original;
                        if (ownedSlotMaterials.TryGetValue(slot, out var obsolete)) { UnityEngine.Object.Destroy(obsolete.Copy); ownedSlotMaterials.Remove(slot); }
                    }
                    else
                    {
                        if (!materials.TryGetValue(key, out var material) || !material) { ExpandCatalog(); materials.TryGetValue(key, out material); }
                        if (material)
                        {
                            // A live template's materials may be destroyed or changed
                            // by native code later. The visual owns each changed slot;
                            // replacing it releases the old copy, bounding seek memory.
                            if (owned.Copy) UnityEngine.Object.Destroy(owned.Copy);
                            var copy = new Material(material) { renderQueue = recorded.RenderQueue };
                            ownedSlotMaterials[slot] = (identity, copy); selected[i] = copy;
                        }
                        else selected[i] = original!;
                    }
                }
                renderer.sharedMaterials = selected;
                bool replacedMaterials = false;
                for (int i = 0; i < selected.Length; i++)
                {
                    block.Clear(); block.SetFloat("_VertexGhost", 0); block.SetFloat("_Interactable", 0);
                    if (i < frame.Materials.Length) foreach (var property in frame.Materials[i].Properties)
                    {
                        if (!NativeVisualAppearanceRules.ValidProperty(property) || !selected[i] || !selected[i].HasProperty(property.Name)) continue;
                        int propertyIndex = selected[i].shader.FindPropertyIndex(property.Name);
                        var type = propertyIndex < 0 ? (ShaderPropertyType)(-1) : selected[i].shader.GetPropertyType(propertyIndex);
                        int actualKind = type == ShaderPropertyType.Color ? 1 : type == ShaderPropertyType.Vector ? 2 : type == ShaderPropertyType.Texture ? 3 : 0;
                        if (actualKind != property.Kind) continue;
                        if (property.Kind == 0)
                        { if (type == ShaderPropertyType.Int) block.SetInteger(property.Name, (int)property.Values[0]); else block.SetFloat(property.Name, property.Values[0]); }
                        else if (property.Kind == 1) block.SetColor(property.Name, new Color(property.Values[0], property.Values[1], property.Values[2], property.Values[3]));
                        else if (property.Kind == 2) block.SetVector(property.Name, new Vector4(property.Values[0], property.Values[1], property.Values[2], property.Values[3]));
                        else
                        {
                            if (property.Texture.Length == 0)
                            {
                                // Unity 6's MaterialPropertyBlock.SetTexture rejects
                                // null. Clear an actual authored texture on an owned
                                // material copy instead, and leave the block unset.
                                if (selected[i].GetTexture(property.Name))
                                {
                                    var slot = (renderer, i);
                                    if (!ownedSlotMaterials.TryGetValue(slot, out var owned) || owned.Copy != selected[i])
                                    {
                                        if (owned.Copy) UnityEngine.Object.Destroy(owned.Copy);
                                        var copy = new Material(selected[i]);
                                        var recorded = frame.Materials[i];
                                        ownedSlotMaterials[slot] = (recorded.Name + "\n" + recorded.Shader + "\n" + recorded.RenderQueue.ToString(CultureInfo.InvariantCulture), copy);
                                        selected[i] = copy; replacedMaterials = true;
                                    }
                                    selected[i].SetTexture(property.Name, null);
                                }
                            }
                            else { if (!textures.TryGetValue(property.Texture, out var texture)) { ExpandCatalog(); textures.TryGetValue(property.Texture, out texture); } if (texture) block.SetTexture(property.Name, texture); }
                        }
                    }
                    renderer.SetPropertyBlock(block, i);
                }
                if (replacedMaterials) renderer.sharedMaterials = selected;
            }
            applied = frames;
        }
        public void Dispose()
        {
            foreach (var material in ownedSlotMaterials.Values) if (material.Copy) UnityEngine.Object.Destroy(material.Copy);
            ownedSlotMaterials.Clear(); materials.Clear(); textures.Clear(); meshes.Clear(); authored.Clear(); authoredMeshes.Clear(); authoredVisibility.Clear(); applied = null;
        }
    }
}
