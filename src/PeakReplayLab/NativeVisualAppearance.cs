using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace PeakReplayLab;

internal static class NativeVisualAppearance
{
    private sealed class PropertySpec
    { public string Name = ""; public int Id, Kind; public bool Integer, Hdr; }
    private static readonly ConditionalWeakTable<Shader, PropertySpec[]> shaderProperties = new();
    private static readonly ConditionalWeakTable<Shader, Dictionary<string, PropertySpec>> playbackProperties = new();
    private static readonly ConditionalWeakTable<Shader, PropertySpec[]> nativeBlockProperties = new();
    private static PropertySpec[] Properties(Shader shader) => shaderProperties.GetValue(shader, source =>
    {
        var result = new List<PropertySpec>();
        foreach (var allowed in NativeVisualAppearanceRules.Properties)
        {
            int index = source.FindPropertyIndex(allowed.Name); if (index < 0) continue;
            var type = source.GetPropertyType(index);
            int kind = type == ShaderPropertyType.Color ? 1 : type == ShaderPropertyType.Vector ? 2 : type == ShaderPropertyType.Texture ? 3 : 0;
            if (!NativeVisualAppearanceRules.AllowedProperty(allowed.Name, kind)) continue;
            result.Add(new PropertySpec { Name = allowed.Name, Id = Shader.PropertyToID(allowed.Name), Kind = kind, Integer = type == ShaderPropertyType.Int,
                Hdr = type == ShaderPropertyType.Color && (source.GetPropertyFlags(index) & ShaderPropertyFlags.HDR) != 0 });
        }
        return result.ToArray();
    });
    // Native property blocks may also contain trusted shader controls outside
    // the recording allowlist. Snapshot those once at visual construction, then
    // rebuild them without Unity's hidden SetColor conversion metadata.
    private sealed class NativeBlockValue
    {
        public PropertySpec Property = null!;
        public Vector4 Value;
        public int Integer;
        public Texture? Texture;
    }
    private static NativeBlockValue[] NativeBlock(Material material, MaterialPropertyBlock block)
    {
        if (!material || !material.shader || block.isEmpty) return Array.Empty<NativeBlockValue>();
        var properties = nativeBlockProperties.GetValue(material.shader, shader =>
        {
            var result = new PropertySpec[shader.GetPropertyCount()];
            for (int i = 0; i < result.Length; i++)
            {
                var type = shader.GetPropertyType(i);
                result[i] = new PropertySpec { Name = shader.GetPropertyName(i), Id = shader.GetPropertyNameId(i),
                    Kind = type == ShaderPropertyType.Color ? 1 : type == ShaderPropertyType.Vector ? 2 : type == ShaderPropertyType.Texture ? 3 : 0,
                    Integer = type == ShaderPropertyType.Int };
            }
            return result;
        });
        List<NativeBlockValue>? values = null;
        foreach (var property in properties)
        {
            if (property.Name is "_VertexGhost" or "_Interactable") continue;
            if (!block.HasProperty(property.Id)) continue;
            var entry = new NativeBlockValue { Property = property };
            if (property.Kind == 3) entry.Texture = block.GetTexture(property.Id);
            else if (property.Kind == 0)
            { if (property.Integer) entry.Integer = block.GetInteger(property.Id); else entry.Value.x = block.GetFloat(property.Id); }
            else entry.Value = block.GetVector(property.Id); // Actual native shader value, including SetColor's conversion.
            (values ??= new List<NativeBlockValue>()).Add(entry);
        }
        return values?.ToArray() ?? Array.Empty<NativeBlockValue>();
    }
    private static void RestoreNativeBlock(MaterialPropertyBlock block, NativeBlockValue[] values, NativeShaderPropertyFrame[] recorded)
    {
        foreach (var entry in values)
        {
            var property = entry.Property;
            // In particular, an explicitly empty recorded texture must not
            // inherit a native MPB texture that masks its cleared material.
            if (NativePropertyBlockPolicy.RecordedOverrides(property.Name, property.Kind, recorded)) continue;
            if (property.Kind == 3) { if (entry.Texture) block.SetTexture(property.Id, entry.Texture); }
            else if (property.Kind == 0)
            { if (property.Integer) block.SetInteger(property.Id, entry.Integer); else block.SetFloat(property.Id, entry.Value.x); }
            else block.SetVector(property.Id, entry.Value);
        }
    }
    private sealed class SourceRenderer
    {
        public Renderer Renderer = null!;
        public MeshFilter? Filter;
        public SkinnedMeshRenderer? Skin;
        public int BlendShapeCount;
        public string Path = "", MeshIdentity = "";
        public int Index;
        public bool Animated, OptionalAnimatedMainTexture;
        public readonly List<Material> Materials = new();
    }
    private sealed class Probe
    {
        public readonly List<SourceRenderer> Renderers = new();
        public readonly Dictionary<Transform, string> Paths = new();
        public readonly MaterialPropertyBlock Global = new();
        public readonly MaterialPropertyBlock Slot = new();
        public readonly NativeCapturePolicy Schedule;
        public bool Animated, OptionalAnimatedMainTexture;
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
                    if (!node.GetComponent<TMPro.TMP_Text>() && (renderer is MeshRenderer || renderer is SkinnedMeshRenderer))
                    {
                        if (Renderers.Count >= NativeVisualAppearanceRules.MaximumRenderers || index >= NativeVisualAppearanceRules.MaximumMaterials)
                            throw new InvalidOperationException("Native appearance renderer count exceeds its recording limit.");
                        Renderers.Add(new SourceRenderer { Renderer = renderer, Filter = renderer.GetComponent<MeshFilter>(), Skin = renderer as SkinnedMeshRenderer, Path = path, Index = index++ });
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

    public static bool NeedsCaptureAtTime(Transform source, NativeRendererFrame[]? previous, double now, bool active = true, bool allowAnimated = false, bool animateMainTexture = false, bool followSceneClock = false)
    {
        if (previous == null) return false; // The owning track supplies initial/lifecycle reads.
        if (!probes.TryGetValue(source, out var probe)) return true;
        return probe.Schedule.NeedsRead(now, active, allowAnimated && (probe.Animated || animateMainTexture && probe.OptionalAnimatedMainTexture), followSceneClock);
    }

    // Full static material/renderer metadata is independent of root motion.
    // Continuous native controls such as flame clipping and emissive pulses
    // keep a separate 30 Hz path; native notifications force a full read now.
    public static NativeRendererFrame[] CaptureAtTime(Transform source, NativeRendererFrame[]? previous,
        double now, bool dirty = false, int maximumNodes = 256, bool allowAnimated = false, bool animateMainTexture = false, bool followSceneClock = false)
    {
        if (!source) throw new ArgumentNullException(nameof(source));
        var probe = GetProbe(source, maximumNodes);
        if (probe.Schedule.FullDue(now, previous != null, dirty))
        {
            var value = Capture(source, previous, maximumNodes, animateMainTexture: animateMainTexture);
            probe.Schedule.FullObserved(now); return value;
        }
        if (!allowAnimated || !(probe.Animated || animateMainTexture && probe.OptionalAnimatedMainTexture) || !source.gameObject.activeInHierarchy || !probe.Schedule.AnimatedDue(now, followSceneClock)) return previous!;
        var animated = Capture(source, previous, maximumNodes, animatedOnly: true, animateMainTexture: animateMainTexture);
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

    public static NativeRendererFrame[] Capture(Transform source, NativeRendererFrame[]? previous = null, int maximumNodes = 256, bool animatedOnly = false, bool animateMainTexture = false)
    {
        if (!source) throw new ArgumentNullException(nameof(source));
        var probe = GetProbe(source, maximumNodes);
        using var timing = ReplayPerformance.Measure(ReplayStage.Appearance);
        if (!animatedOnly) probe.Animated = probe.OptionalAnimatedMainTexture = false;
        NativeRendererFrame[]? changed = previous == null || previous.Length != probe.Renderers.Count ? new NativeRendererFrame[probe.Renderers.Count] : null;
        for (int i = 0; i < probe.Renderers.Count; i++)
        {
            var entry = probe.Renderers[i]; var renderer = entry.Renderer;
            if (animatedOnly && !(entry.Animated || animateMainTexture && entry.OptionalAnimatedMainTexture)) continue;
            if (!renderer) { probes.Remove(source); return previous ?? Array.Empty<NativeRendererFrame>(); }
            var old = previous != null && i < previous.Length ? previous[i] : null;
            var nativeMaterials = entry.Materials;
            if (!animatedOnly)
            {
                renderer.GetSharedMaterials(nativeMaterials); entry.Animated = entry.OptionalAnimatedMainTexture = false;
                var skinMesh = entry.Skin ? entry.Skin!.sharedMesh : null;
                entry.BlendShapeCount = skinMesh ? skinMesh.blendShapeCount : 0;
                if (entry.BlendShapeCount > NativeVisualAppearanceRules.MaximumBlendShapes) throw new InvalidOperationException("Native blend shape count exceeds its recording limit.");
                entry.Animated = entry.BlendShapeCount > 0;
            }
            if (nativeMaterials.Count > NativeVisualAppearanceRules.MaximumMaterials) throw new InvalidOperationException("Native material count exceeds its recording limit.");
            renderer.GetPropertyBlock(probe.Global);
            NativeMaterialFrame[]? materials = old == null || old.Materials.Length != nativeMaterials.Count ? new NativeMaterialFrame[nativeMaterials.Count] : null;
            for (int slot = 0; slot < nativeMaterials.Count; slot++)
            {
                var material = nativeMaterials[slot];
                var prior = old != null && slot < old.Materials.Length ? old.Materials[slot] : null;
                var shared = SharedMaterialRead(material);
                var shader = ReadShader(material, shared);
                if (!animatedOnly && shader)
                    foreach (var property in Properties(shader!))
                    {
                        bool regular = AnimatedProperty(property.Name);
                        entry.Animated |= regular;
                        entry.OptionalAnimatedMainTexture |= !regular && AnimatedProperty(property.Name, true);
                    }
                renderer.GetPropertyBlock(probe.Slot, slot);
                // Unity ignores the entire renderer-wide block when a per-slot block
                // exists, including properties that are absent from that slot block.
                var block = probe.Slot.isEmpty ? probe.Global : probe.Slot;
                var properties = ReadProperties(material, shader, shared, block, prior?.Properties, animatedOnly, animateMainTexture);
                var descriptor = animatedOnly && prior != null ? new MaterialDescriptor(prior.Name, prior.Shader, prior.RenderQueue) :
                    shared == null ? ReadDescriptor((material, shader)) : shared.Descriptor.Read((material, shader), captureEpoch, descriptorReader);
                var value = prior != null && descriptor.Name == prior.Name && descriptor.Shader == prior.Shader && descriptor.Queue == prior.RenderQueue && ReferenceEquals(properties, prior.Properties) ? prior :
                    new NativeMaterialFrame { Name = descriptor.Name, Shader = descriptor.Shader, RenderQueue = descriptor.Queue, Properties = properties };
                if (materials != null) materials[slot] = value;
                else if (!ReferenceEquals(value, prior)) { materials = (NativeMaterialFrame[])old!.Materials.Clone(); materials[slot] = value; }
            }
            if (!animatedOnly)
            {
                probe.Animated |= entry.Animated;
                probe.OptionalAnimatedMainTexture |= entry.OptionalAnimatedMainTexture;
            }
            var appearanceMaterials = materials ?? old!.Materials;
            var weights = ReadBlendShapes(entry.Skin, entry.BlendShapeCount, old?.BlendShapes);
            if (animatedOnly && old != null)
            {
                bool visible = renderer.enabled && !renderer.forceRenderingOff;
                var animated = old.Enabled == visible && ReferenceEquals(old.Materials, appearanceMaterials) && ReferenceEquals(old.BlendShapes, weights) ? old :
                    new NativeRendererFrame { Path = old.Path, Index = old.Index, Mesh = old.Mesh, Enabled = visible, Layer = old.Layer,
                        RenderingLayerMask = old.RenderingLayerMask, LightProbeUsage = old.LightProbeUsage, ReflectionProbeUsage = old.ReflectionProbeUsage,
                        ShadowCastingMode = old.ShadowCastingMode, ReceiveShadows = old.ReceiveShadows, SortingLayerId = old.SortingLayerId,
                        SortingOrder = old.SortingOrder, ProbeAnchor = old.ProbeAnchor, Materials = appearanceMaterials, BlendShapes = weights };
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
                old.SortingLayerId == renderer.sortingLayerID && old.SortingOrder == renderer.sortingOrder && old.ProbeAnchor == anchor && ReferenceEquals(old.Materials, appearanceMaterials) && ReferenceEquals(old.BlendShapes, weights) ? old :
                new NativeRendererFrame { Path = entry.Path, Index = entry.Index, Mesh = mesh, Enabled = enabled, Layer = renderer.gameObject.layer,
                    RenderingLayerMask = renderer.renderingLayerMask, LightProbeUsage = (int)renderer.lightProbeUsage, ReflectionProbeUsage = (int)renderer.reflectionProbeUsage,
                    ShadowCastingMode = (int)renderer.shadowCastingMode, ReceiveShadows = renderer.receiveShadows, SortingLayerId = renderer.sortingLayerID,
                    SortingOrder = renderer.sortingOrder, ProbeAnchor = anchor, Materials = appearanceMaterials, BlendShapes = weights };
            if (changed != null) changed[i] = frame;
            else if (!ReferenceEquals(frame, old)) { changed = (NativeRendererFrame[])previous!.Clone(); changed[i] = frame; }
        }
        return changed ?? previous!;
    }

    private static float[] ReadBlendShapes(SkinnedMeshRenderer? skin, int count, float[]? previous)
    {
        if (!skin || count == 0) return Array.Empty<float>();
        float[]? changed = previous == null || previous.Length != count ? new float[count] : null;
        for (int i = 0; i < count; i++)
        {
            float value = skin!.GetBlendShapeWeight(i);
            if (!Finite(value)) throw new InvalidOperationException("Invalid native blend shape weight.");
            if (changed != null) changed[i] = value;
            else if (previous![i] != value) { changed = (float[])previous.Clone(); changed[i] = value; }
        }
        return changed ?? previous!;
    }

    // A capture tick is synchronous on the Unity thread. A shared material cannot
    // change between its observers in that tick; per-renderer property blocks
    // remain independent. Weak keys release destroyed scene materials.
    private sealed class MaterialRead
    {
        public Shader? Shader;
        public CaptureEpochValue<Shader?> ShaderRead;
        public CaptureEpochValue<MaterialDescriptor> Descriptor;
        public readonly Dictionary<PropertySpec, PropertyRead> Properties = new();
    }
    private readonly struct MaterialDescriptor
    {
        public readonly string Name, Shader;
        public readonly int Queue;
        public MaterialDescriptor(string name, string shader, int queue) { Name = name; Shader = shader; Queue = queue; }
    }
    private sealed class PropertyRead
    {
        public long Epoch;
        public NativeShaderPropertyFrame? Value;
    }
    private static readonly ConditionalWeakTable<Material, MaterialRead> materialReads = new();
    private static readonly Func<Material, Shader?> shaderReader = static material => material.shader;
    private static readonly Func<(Material Material, Shader? Shader), MaterialDescriptor> descriptorReader = ReadDescriptor;
    private static long captureEpoch;
    private static int captureDepth;
    internal readonly struct CaptureScope : IDisposable
    {
        public void Dispose() { captureDepth--; }
    }
    internal static CaptureScope BeginCapture()
    {
        if (captureDepth++ == 0) captureEpoch++;
        return new CaptureScope();
    }

    private static NativeShaderPropertyFrame[] ReadProperties(Material material, MaterialPropertyBlock block, NativeShaderPropertyFrame[]? previous, bool animatedOnly, bool animateMainTexture = false)
    {
        var shared = SharedMaterialRead(material);
        return ReadProperties(material, ReadShader(material, shared), shared, block, previous, animatedOnly, animateMainTexture);
    }
    private static MaterialRead? SharedMaterialRead(Material material) =>
        material && captureDepth != 0 ? materialReads.GetValue(material, static _ => new MaterialRead()) : null;
    private static Shader? ReadShader(Material material, MaterialRead? shared)
    {
        if (!material) return null;
        var shader = shared == null ? material.shader : shared.ShaderRead.Read(material, captureEpoch, shaderReader);
        if (shared != null && shared.Shader != shader) { shared.Shader = shader; shared.Properties.Clear(); }
        return shader;
    }
    private static MaterialDescriptor ReadDescriptor((Material Material, Shader? Shader) source) => new(
        AssetName(source.Material), source.Shader ? AssetName(source.Shader) : "", source.Material ? source.Material.renderQueue : -1);
    private static NativeShaderPropertyFrame[] ReadProperties(Material material, Shader? shader, MaterialRead? shared, MaterialPropertyBlock block,
        NativeShaderPropertyFrame[]? previous, bool animatedOnly, bool animateMainTexture)
    {
        if (!material || !shader) return Array.Empty<NativeShaderPropertyFrame>();
        bool hasOverrides = !block.isEmpty;
        List<NativeShaderPropertyFrame>? changed = null;
        int index = 0;
        foreach (var property in Properties(shader))
        {
            var old = previous != null && index < previous.Length ? previous[index] : null;
            if (animatedOnly && !AnimatedProperty(property.Name, animateMainTexture) && old != null && old.Name == property.Name)
            { if (changed != null) changed.Add(old); index++; continue; }
            bool overridden = hasOverrides && block.HasProperty(property.Id);
            NativeShaderPropertyFrame? frame;
            if (shared != null && !overridden)
            {
                if (!shared.Properties.TryGetValue(property, out var read))
                { read = new PropertyRead(); shared.Properties.Add(property, read); }
                if (read.Epoch != captureEpoch)
                { read.Value = ReadProperty(material, block, property, false, read.Value); read.Epoch = captureEpoch; }
                frame = read.Value;
                if (frame != null && SameProperty(old, frame)) frame = old;
            }
            else frame = ReadProperty(material, block, property, overridden, old);
            if (frame == null) continue;
            if (changed != null) changed.Add(frame);
            else if (!ReferenceEquals(frame, old))
            { changed = previous == null ? new List<NativeShaderPropertyFrame>() : previous.Take(index).ToList(); changed.Add(frame); }
            index++;
        }
        if (changed != null) return changed.ToArray();
        return previous == null || previous.Length != index ? (previous ?? Array.Empty<NativeShaderPropertyFrame>()).Take(index).ToArray() : previous;
    }
    private static bool SameProperty(NativeShaderPropertyFrame? left, NativeShaderPropertyFrame right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || left.Name != right.Name || left.Kind != right.Kind || left.Texture != right.Texture || left.ColorEncoding != right.ColorEncoding || left.Values.Length != right.Values.Length) return false;
        for (int i = 0; i < left.Values.Length; i++) if (left.Values[i] != right.Values[i]) return false;
        return true;
    }
    private static NativeShaderPropertyFrame? ReadProperty(Material material, MaterialPropertyBlock block, PropertySpec property, bool overridden, NativeShaderPropertyFrame? old)
    {
        int id = property.Id;
        float x = 0, y = 0, z = 0, w = 0; string texture = "";
        if (property.Kind == 0) x = property.Integer ? overridden ? block.GetInteger(id) : material.GetInteger(id) : overridden ? block.GetFloat(id) : material.GetFloat(id);
        else if (property.Kind == 1 && property.Hdr)
        { var v = overridden ? block.GetVector(id) : material.GetVector(id); x = v.x; y = v.y; z = v.z; w = v.w; }
        else if (property.Kind == 1)
        { var c = overridden ? block.GetColor(id) : material.GetColor(id); x = c.r; y = c.g; z = c.b; w = c.a; }
        else if (property.Kind == 2)
        { var v = overridden ? block.GetVector(id) : material.GetVector(id); x = v.x; y = v.y; z = v.z; w = v.w; }
        else texture = AssetName(overridden ? block.GetTexture(id) : material.GetTexture(id));
        if (!Finite(x) || !Finite(y) || !Finite(z) || !Finite(w)) return null;
        int colorEncoding = property.Hdr ? 1 : 0;
        bool same = old != null && old.Name == property.Name && old.Kind == property.Kind && old.Texture == texture && old.ColorEncoding == colorEncoding &&
            (property.Kind == 3 ? old.Values.Length == 0 : property.Kind == 0 ? old.Values.Length == 1 && old.Values[0] == x :
                old.Values.Length == 4 && old.Values[0] == x && old.Values[1] == y && old.Values[2] == z && old.Values[3] == w);
        return same ? old! : new NativeShaderPropertyFrame { Name = property.Name, Kind = property.Kind, Texture = texture, ColorEncoding = colorEncoding,
            Values = property.Kind == 3 ? Array.Empty<float>() : property.Kind == 0 ? new[] { x } : new[] { x, y, z, w } };
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= NativeVisualAppearanceRules.MaximumValue;
    private sealed class AssetIdentity { public string Name = ""; }
    private static readonly ConditionalWeakTable<UnityEngine.Object, AssetIdentity> assetNames = new();
    private static string AssetName(UnityEngine.Object? value) => value ? assetNames.GetValue(value!, source =>
        new AssetIdentity { Name = source.name.Replace(" (Instance)", "").Replace("(Instance)", "").Trim() }).Name : "";
    private static bool AnimatedProperty(string name, bool animateMainTexture = false) => NativeAnimatedAppearancePolicy.AnimatedProperty(name, animateMainTexture);
    private static Mesh? SharedMesh(Renderer renderer) => renderer is SkinnedMeshRenderer skin ? skin.sharedMesh :
        renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
    private static readonly ConditionalWeakTable<Mesh, AssetIdentity> meshNames = new();
    private static string MeshIdentity(Mesh? mesh) => mesh ? meshNames.GetValue(mesh!, source => new AssetIdentity
        { Name = AssetName(source) + "\n" + source.vertexCount.ToString(CultureInfo.InvariantCulture) + ":" + source.subMeshCount.ToString(CultureInfo.InvariantCulture) }).Name : "";

    private sealed class ReplayMaterial { }
    private static readonly ConditionalWeakTable<Material, ReplayMaterial> replayMaterials = new();
    internal static void MarkReplayMaterial(Material material)
    { if (material) replayMaterials.GetValue(material, _ => new ReplayMaterial()); }
    private static bool NativeMaterial(Material material) => material && material.shader && !replayMaterials.TryGetValue(material, out _);
    private static string MaterialIdentity(Material material) => AssetName(material) + "\n" + material.shader.name;
    private static readonly NativeVisualResourceCatalog<Material> materialCatalog = new(NativeMaterial,
        (existing, candidate) => existing.name.Contains("(Instance)") && !candidate.name.Contains("(Instance)"));
    private static readonly NativeVisualResourceCatalog<Texture> textureCatalog = new(texture => texture);
    private static readonly NativeVisualResourceCatalog<Mesh> meshCatalog = new(mesh => mesh);
    private static int catalogOwners;
    internal static NativeVisualResourceCatalog<Texture>.Lease AcquireTextureCatalog(int scene) => textureCatalog.Acquire(scene);
    internal static string TextureIdentity(Texture texture) => AssetName(texture);
    private static void RetainCatalogs()
    {
        if (catalogOwners++ != 0) return;
        SceneManager.sceneLoaded += CatalogSceneLoaded;
        SceneManager.sceneUnloaded += CatalogSceneUnloaded;
    }
    private static void ReleaseCatalogs()
    {
        if (--catalogOwners != 0) return;
        SceneManager.sceneLoaded -= CatalogSceneLoaded;
        SceneManager.sceneUnloaded -= CatalogSceneUnloaded;
    }
    private static void CatalogSceneLoaded(Scene scene, LoadSceneMode mode)
    { materialCatalog.Refresh(); textureCatalog.Refresh(); meshCatalog.Refresh(); }
    private static void CatalogSceneUnloaded(Scene scene)
    {
        materialCatalog.Unload(scene.handle); textureCatalog.Unload(scene.handle); meshCatalog.Unload(scene.handle);
        materialCatalog.Refresh(); textureCatalog.Refresh(); meshCatalog.Refresh();
    }

    internal sealed class Playback : IDisposable
    {
        private readonly VisualReplica replica;
        private readonly Dictionary<string, Material> materials = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Texture> textures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Mesh> meshes = new(StringComparer.Ordinal);
        private readonly NativeVisualResourceCatalog<Material>.Lease sharedMaterials;
        private readonly NativeVisualResourceCatalog<Texture>.Lease sharedTextures;
        private readonly NativeVisualResourceCatalog<Mesh>.Lease sharedMeshes;
        private readonly Dictionary<(Renderer, int), (string Identity, Material Copy)> ownedSlotMaterials = new();
        private readonly Dictionary<Renderer, Material[]> authored = new();
        private readonly Dictionary<Renderer, NativeBlockValue[][]> authoredBlocks = new();
        private readonly Dictionary<Renderer, Mesh?> authoredMeshes = new();
        private readonly Dictionary<Renderer, float[]> authoredBlendShapes = new();
        private readonly Dictionary<Renderer, bool> authoredVisibility = new();
        private readonly NativeRendererFrame[] baseline;
        private readonly MaterialPropertyBlock block = new();
        private readonly NativeRendererVisibilityPolicy<Renderer> visibility = new(
            static renderer => renderer.enabled, static (renderer, enabled) => renderer.enabled = enabled);
        private readonly Dictionary<Renderer, NativeRendererFrame> appliedRenderers = new();
        private NativeRendererFrame[]? applied;
        private bool disposed;
        private NativeRendererFrame[]? preparing, requested;
        private Dictionary<Renderer, bool>? restoringVisibility;
        private int prepareRenderer, prepareAbsent;
        private bool preparingActive, prepareReveal, stateDirty;
        private int initializeRenderer, initializeNode;
        public bool IsReady { get; private set; }
        public Playback(VisualReplica replica, GameObject nativeSource, bool incremental = false)
        {
            if (!replica.IsReady) throw new InvalidOperationException("Renderer baselines require a completed visual hierarchy.");
            if (replica.RendererDirectory.Count > NativeVisualAppearanceRules.MaximumRenderers) throw new InvalidOperationException("Native appearance renderer count exceeds its recording limit.");
            this.replica = replica;
            baseline = new NativeRendererFrame[replica.RendererDirectory.Count];
            int scene = SceneManager.GetActiveScene().handle;
            sharedMaterials = materialCatalog.Acquire(scene); sharedTextures = textureCatalog.Acquire(scene); sharedMeshes = meshCatalog.Acquire(scene);
            RetainCatalogs();
            try
            {
                if (!incremental) while (!IsReady) InitializeNext();
            }
            catch { Dispose(); throw; }
        }
        public bool ContinueBuild(PresentationWorkBudget budget)
        {
            while (!IsReady && budget.Build()) InitializeNext();
            return IsReady;
        }
        private void InitializeNext()
        {
            if (initializeRenderer < replica.RendererDirectory.Count)
            {
                var entry = replica.RendererDirectory[initializeRenderer]; var renderer = entry.Clone;
                authored[renderer] = renderer.sharedMaterials; authoredMeshes[renderer] = SharedMesh(renderer); authoredVisibility[renderer] = renderer.enabled;
                var skin = renderer as SkinnedMeshRenderer;
                authoredBlendShapes[renderer] = ReadBlendShapes(skin, skin && skin.sharedMesh ? skin.sharedMesh.blendShapeCount : 0, null);
                Register(SharedMesh(entry.Source)); foreach (var material in entry.Source.sharedMaterials) Register(material);
                var nativeMaterials = renderer.sharedMaterials;
                if (nativeMaterials.Length > NativeVisualAppearanceRules.MaximumMaterials) throw new InvalidOperationException("Native material count exceeds its recording limit.");
                var materialFrames = new NativeMaterialFrame[nativeMaterials.Length]; var global = new MaterialPropertyBlock();
                var nativeBlocks = new NativeBlockValue[nativeMaterials.Length][];
                renderer.GetPropertyBlock(global);
                for (int i = 0; i < nativeMaterials.Length; i++)
                {
                    var material = nativeMaterials[i]; renderer.GetPropertyBlock(block, i);
                    nativeBlocks[i] = NativeBlock(material!, block.isEmpty ? global : block);
                    materialFrames[i] = new NativeMaterialFrame { Name = AssetName(material), Shader = material && material.shader ? AssetName(material.shader) : "",
                        RenderQueue = material ? material.renderQueue : -1, Properties = ReadProperties(material!, block.isEmpty ? global : block, null, false) };
                }
                authoredBlocks[renderer] = nativeBlocks;
                baseline[initializeRenderer++] = new NativeRendererFrame { Path = entry.Path, Index = entry.Index,
                    Mesh = MeshIdentity(SharedMesh(renderer)), Enabled = renderer.enabled && !renderer.forceRenderingOff,
                    Layer = renderer.gameObject.layer, RenderingLayerMask = renderer.renderingLayerMask,
                    LightProbeUsage = (int)renderer.lightProbeUsage, ReflectionProbeUsage = (int)renderer.reflectionProbeUsage,
                    ShadowCastingMode = (int)renderer.shadowCastingMode, ReceiveShadows = renderer.receiveShadows,
                    SortingLayerId = renderer.sortingLayerID, SortingOrder = renderer.sortingOrder,
                    ProbeAnchor = renderer.probeAnchor ? replica.ClonePath(renderer.probeAnchor) : "", Materials = materialFrames, BlendShapes = authoredBlendShapes[renderer] };
                return;
            }
            if (initializeNode < replica.SourceNodeCount)
            {
                foreach (var variant in replica.SourceTransform(initializeNode++).GetComponents<ColorblindVariant>()) Register(variant.colorblindMaterial);
                return;
            }
            IsReady = true;
        }
        private void Register(Material material)
        {
            if (!NativeMaterial(material)) return;
            string key = MaterialIdentity(material);
            if (!materials.TryGetValue(key, out var existing)) materials.Add(key, material);
            else if (existing.name.Contains("(Instance)") && !material.name.Contains("(Instance)")) materials[key] = material;
            sharedMaterials.Register(key, material);
            foreach (var property in NativeVisualAppearanceRules.Properties)
                if (property.Kind == 3 && material.HasProperty(property.Name)) Register(material.GetTexture(property.Name));
        }
        private void Register(Texture? texture)
        { if (texture) { string key = AssetName(texture); if (!textures.ContainsKey(key)) textures.Add(key, texture!); sharedTextures.Register(key, texture!); } }
        private void Register(Mesh? mesh)
        { if (mesh) { string key = MeshIdentity(mesh); if (!meshes.ContainsKey(key)) meshes.Add(key, mesh!); sharedMeshes.Register(key, mesh!); } }
        private Material? FindMaterial(string key)
        {
            if (materials.TryGetValue(key, out var local) && NativeMaterial(local)) return local;
            // Only native loaded resources, never recording-supplied paths. Each
            // resource type is scanned once per scene lifecycle, not per clone.
            return sharedMaterials.TryGet(key, out var value) ? value : null;
        }
        private Texture? FindTexture(string key)
        {
            if (textures.TryGetValue(key, out var local) && local) return local;
            return sharedTextures.TryGet(key, out var value) ? value : null;
        }
        private Mesh? FindMesh(string key)
        {
            if (meshes.TryGetValue(key, out var local) && local) return local;
            return sharedMeshes.TryGet(key, out var value) ? value : null;
        }
        private bool PrepareResources(NativeRendererFrame frame, PresentationWorkBudget budget)
        {
            if (!replica.TryCloneRenderer(frame.Path, frame.Index, out var renderer)) return true;
            if (frame.Mesh.Length != 0 && !FindMesh(frame.Mesh) &&
                !sharedMeshes.ExpandIncremental(() => Resources.FindObjectsOfTypeAll<Mesh>(), MeshIdentity, budget.Build)) return false;
            var defaults = authored[renderer];
            for (int i = 0; i < frame.Materials.Length; i++)
            {
                var material = frame.Materials[i]; var original = i < defaults.Length ? defaults[i] : null;
                bool authoredMatch = original && AssetName(original) == material.Name && original.shader && original.shader.name == material.Shader;
                if (!authoredMatch && material.Name.Length != 0 && material.Shader.Length != 0 && !FindMaterial(material.Name + "\n" + material.Shader) &&
                    !sharedMaterials.ExpandIncremental(() => Resources.FindObjectsOfTypeAll<Material>(), MaterialIdentity, budget.Build)) return false;
                foreach (var property in material.Properties)
                    if (property.Kind == 3 && property.Texture.Length != 0 && !FindTexture(property.Texture) &&
                        !sharedTextures.ExpandIncremental(() => Resources.FindObjectsOfTypeAll<Texture>(), AssetName, budget.Build)) return false;
            }
            return true;
        }

        // A seek may change hundreds of renderers at once. Admission of a model
        // alone does not budget its recorded mesh/material/property restoration.
        // Keep one immutable target until complete; only a new seek cancels it,
        // so animated snapshots arriving every frame cannot restart the work.
        public bool TryApply(NativeRendererFrame[]? frames, PresentationWorkBudget budget, bool revealAuthoredRenderers = false, bool restart = false)
        {
            if (!IsReady || disposed) throw new InvalidOperationException("An unfinished renderer baseline cannot be applied.");
            if (restart) { preparingActive = false; preparing = requested = null; restoringVisibility = null; }
            if (!preparingActive && !stateDirty && ReferenceEquals(applied, frames)) { Apply(frames, revealAuthoredRenderers); return true; }
            if (!preparingActive)
            {
                requested = frames; preparing = frames ?? baseline; preparingActive = true;
                stateDirty = true;
                prepareRenderer = prepareAbsent = 0; prepareReveal = revealAuthoredRenderers; visibility.Begin();
                if (frames == null)
                {
                    restoringVisibility = new Dictionary<Renderer, bool>();
                    foreach (var entry in authored) if (entry.Key) restoringVisibility[entry.Key] = entry.Key.enabled;
                }
            }
            while (prepareRenderer < preparing!.Length)
            {
                var frame = preparing[prepareRenderer];
                if (!PrepareResources(frame, budget) || !budget.Build()) return false;
                ApplyRenderer(frame, prepareReveal, true); prepareRenderer++;
            }
            while (prepareAbsent < baseline.Length)
            {
                if (!budget.Build()) return false;
                var frame = baseline[prepareAbsent++];
                if (replica.TryCloneRenderer(frame.Path, frame.Index, out var renderer)) visibility.HideAbsent(renderer);
            }
            if (restoringVisibility != null)
            {
                foreach (var entry in restoringVisibility) if (entry.Key && entry.Key.enabled != entry.Value) entry.Key.enabled = entry.Value;
                restoringVisibility = null;
            }
            applied = requested; preparing = requested = null; preparingActive = false; stateDirty = false;
            return true;
        }

        public void Apply(NativeRendererFrame[]? frames, bool revealAuthoredRenderers = false)
        {
            if (!IsReady || disposed) throw new InvalidOperationException("An unfinished renderer baseline cannot be applied.");
            // Visibility may have been reapplied by the hierarchy pose even when the
            // appearance snapshot itself did not change.
            if (frames == null)
            {
                if (applied != null || stateDirty)
                {
                    // Unknown older snapshots must not retain mesh/light/material state
                    // from a later recorded snapshot. Keep the hierarchy's current
                    // visibility while restoring all native authored renderer state.
                    var savedVisibility = new Dictionary<Renderer, bool>();
                    foreach (var entry in authored) if (entry.Key) savedVisibility[entry.Key] = entry.Key.enabled;
                    Apply(baseline);
                    foreach (var entry in savedVisibility) if (entry.Key && entry.Key.enabled != entry.Value) entry.Key.enabled = entry.Value;
                }
                applied = null; stateDirty = false; return;
            }
            bool changed = stateDirty || !ReferenceEquals(applied, frames);
            visibility.Begin();
            foreach (var frame in frames) ApplyRenderer(frame, revealAuthoredRenderers, changed);
            foreach (var authored in baseline)
                if (replica.TryCloneRenderer(authored.Path, authored.Index, out var previous)) visibility.HideAbsent(previous);
            applied = frames; stateDirty = false;
        }
        public void ReapplyVisibility(bool revealAuthoredRenderers = false)
        {
            if (applied == null) return;
            visibility.Begin();
            foreach (var frame in applied)
                if (replica.TryCloneRenderer(frame.Path, frame.Index, out var renderer))
                    visibility.SetDesired(renderer, revealAuthoredRenderers ? authoredVisibility[renderer] : frame.Enabled);
            foreach (var frame in baseline)
                if (replica.TryCloneRenderer(frame.Path, frame.Index, out var renderer)) visibility.HideAbsent(renderer);
        }
        private void ApplyRenderer(NativeRendererFrame frame, bool revealAuthoredRenderers, bool changed)
        {
            if (!replica.TryCloneRenderer(frame.Path, frame.Index, out var renderer)) return;
            visibility.SetDesired(renderer, revealAuthoredRenderers ? authoredVisibility[renderer] : frame.Enabled);
            if (!changed) return;
            appliedRenderers.TryGetValue(renderer, out var previous);
            if (ReferenceEquals(previous, frame)) return;
            renderer.gameObject.layer = frame.Layer; renderer.renderingLayerMask = frame.RenderingLayerMask;
            renderer.lightProbeUsage = (LightProbeUsage)frame.LightProbeUsage; renderer.reflectionProbeUsage = (ReflectionProbeUsage)frame.ReflectionProbeUsage;
            renderer.shadowCastingMode = (ShadowCastingMode)frame.ShadowCastingMode; renderer.receiveShadows = frame.ReceiveShadows;
            renderer.sortingLayerID = frame.SortingLayerId; renderer.sortingOrder = frame.SortingOrder;
            renderer.probeAnchor = replica.TryCloneTransform(frame.ProbeAnchor, out var anchor) ? anchor : null;
            if (frame.Mesh.Length != 0)
            {
                var mesh = FindMesh(frame.Mesh);
                if (!mesh) mesh = authoredMeshes[renderer];
                if (renderer is SkinnedMeshRenderer skin) skin.sharedMesh = mesh; else if (renderer.TryGetComponent<MeshFilter>(out var filter)) filter.sharedMesh = mesh;
            }
            else if (renderer is SkinnedMeshRenderer emptySkin) emptySkin.sharedMesh = null;
            else if (renderer.TryGetComponent<MeshFilter>(out var emptyFilter)) emptyFilter.sharedMesh = null;
            if (renderer is SkinnedMeshRenderer morph && morph.sharedMesh)
            {
                var weights = frame.BlendShapes ?? authoredBlendShapes[renderer];
                for (int i = 0; i < morph.sharedMesh.blendShapeCount; i++) morph.SetBlendShapeWeight(i, i < weights.Length ? weights[i] : 0);
            }
            // A changing candle morph or visibility must not allocate and restore
            // every material property when the immutable material snapshot is shared.
            if (previous != null && ReferenceEquals(previous.Materials, frame.Materials))
            { appliedRenderers[renderer] = frame; return; }
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
                    var material = FindMaterial(key);
                    if (material)
                    {
                        // A live template's materials may be destroyed or changed
                        // by native code later. The visual owns each changed slot;
                        // replacing it releases the old copy, bounding seek memory.
                        if (owned.Copy) UnityEngine.Object.Destroy(owned.Copy);
                        var copy = new Material(material) { renderQueue = recorded.RenderQueue };
                        MarkReplayMaterial(copy);
                        ownedSlotMaterials[slot] = (identity, copy); selected[i] = copy;
                    }
                    else selected[i] = original!;
                }
            }
            renderer.sharedMaterials = selected;
            bool replacedMaterials = false;
            for (int i = 0; i < selected.Length; i++)
            {
                // SetVector over an existing SetColor entry retains Unity's
                // color-conversion marker. Start empty and rebuild the native
                // baseline, including otherwise unrecorded lighting controls.
                block.Clear();
                var nativeBlocks = authoredBlocks[renderer];
                if (i < nativeBlocks.Length) RestoreNativeBlock(block, nativeBlocks[i], frame.Materials[i].Properties);
                block.SetFloat("_VertexGhost", 0); block.SetFloat("_Interactable", 0);
                if (i < frame.Materials.Length) foreach (var property in frame.Materials[i].Properties)
                {
                    if (!NativeVisualAppearanceRules.ValidProperty(property) || !selected[i] || !selected[i].shader) continue;
                    var specs = playbackProperties.GetValue(selected[i].shader, shader =>
                        Properties(shader).ToDictionary(spec => spec.Name, StringComparer.Ordinal));
                    if (!specs.TryGetValue(property.Name, out var spec) || spec.Kind != property.Kind) continue;
                    if (property.Kind == 0)
                    { if (spec.Integer) block.SetInteger(spec.Id, (int)property.Values[0]); else block.SetFloat(spec.Id, property.Values[0]); }
                    else if (property.Kind == 1)
                    {
                        if (NativeShaderColorPolicy.UsesRawVector(property.ColorEncoding, spec.Hdr, frame.Materials[i].Name, frame.Materials[i].Shader, property.Name))
                            block.SetVector(spec.Id, new Vector4(property.Values[0], property.Values[1], property.Values[2], property.Values[3]));
                        else block.SetColor(spec.Id, new Color(property.Values[0], property.Values[1], property.Values[2], property.Values[3]));
                    }
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
                                    MarkReplayMaterial(copy);
                                    var recorded = frame.Materials[i];
                                    ownedSlotMaterials[slot] = (recorded.Name + "\n" + recorded.Shader + "\n" + recorded.RenderQueue.ToString(CultureInfo.InvariantCulture), copy);
                                    selected[i] = copy; replacedMaterials = true;
                                }
                                selected[i].SetTexture(property.Name, null);
                            }
                        }
                        else { var texture = FindTexture(property.Texture); if (texture) block.SetTexture(property.Name, texture); }
                    }
                }
                renderer.SetPropertyBlock(block, i);
            }
            if (replacedMaterials) renderer.sharedMaterials = selected;
            appliedRenderers[renderer] = frame;
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            try { foreach (var material in ownedSlotMaterials.Values) if (material.Copy) UnityEngine.Object.Destroy(material.Copy); }
            finally
            {
                ownedSlotMaterials.Clear(); appliedRenderers.Clear(); materials.Clear(); textures.Clear(); meshes.Clear(); authored.Clear(); authoredBlocks.Clear(); authoredMeshes.Clear(); authoredBlendShapes.Clear(); authoredVisibility.Clear(); visibility.Clear(); applied = null; preparing = requested = null; restoringVisibility = null; preparingActive = false;
                sharedMaterials.Dispose(); sharedTextures.Dispose(); sharedMeshes.Dispose(); ReleaseCatalogs();
            }
        }
    }
}
