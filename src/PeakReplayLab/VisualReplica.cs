using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

// An allowlist, not Instantiate(prefab) followed by disabling scripts: even an immediately
// disabled gameplay prefab has already run Awake and may have registered with Photon.
internal sealed class VisualReplica : IDisposable
{
    private const int DefaultMaximumNodes = 256;
    private sealed class SourceNode
    {
        public Transform Source = null!;
        public string Path = "";
        public Renderer[] Renderers = Array.Empty<Renderer>();
        public NodePose? Last;
    }
    private sealed class Probe
    {
        private readonly Transform source;
        private SourceNode[] nodes = Array.Empty<SourceNode>();
        private ObjectPose? last;
        private bool hierarchyDirty;
        private int maximumNodes;
        public Probe(Transform source, int maximumNodes) { this.source = source; this.maximumNodes = maximumNodes; Refresh(); }
        public void EnsureLimit(int limit) { if (limit > maximumNodes) { maximumNodes = limit; hierarchyDirty = true; } }

        private void Refresh()
        {
            var found = Directory(source, maximumNodes);
            var old = new Dictionary<Transform, SourceNode>();
            foreach (var entry in nodes) if (entry.Source) old[entry.Source] = entry;
            foreach (var entry in found)
                if (old.TryGetValue(entry.Source, out var previous) && previous.Path == entry.Path)
                    entry.Last = previous.Last;
            nodes = found; hierarchyDirty = false;
        }

        public void InvalidateHierarchy() => hierarchyDirty = true;
        public ObjectPose Sample(bool sampleChildren)
        {
            // Directory identity is event-driven. Reading every node's name/parent/sibling
            // sixty times a second to validate a cache was more expensive than the pose.
            if (hierarchyDirty) { Refresh(); sampleChildren = true; }
            bool childrenChanged = last == null || last.Nodes.Length != nodes.Length;
            for (int i = 0; (sampleChildren || childrenChanged) && i < nodes.Length; i++)
            {
                var node = nodes[i]; var t = node.Source;
                if (!t) { hierarchyDirty = true; return last ?? throw new InvalidOperationException("Visual tree was removed during capture."); }
                // Root TRS is exclusively in ObjectPose. Recording it again as a local pose
                // would apply the world transform twice when the original was parented.
                var p = i == 0 ? Vector3.zero : t.localPosition;
                var r = i == 0 ? Quaternion.identity : t.localRotation;
                var s = i == 0 ? Vector3.one : t.localScale;
                bool active = t.gameObject.activeSelf;
                bool visible = node.Renderers.Length == 0;
                foreach (var renderer in node.Renderers)
                    if (renderer && renderer.enabled && !renderer.forceRenderingOff) { visible = true; break; }
                var old = node.Last;
                if (old == null || old.Path != node.Path || old.Active != active || old.Visible != visible ||
                    !Equal(old.Position, p) || !Equal(old.Rotation, r) || !Equal(old.Scale, s))
                {
                    node.Last = new NodePose { Path = node.Path, Position = Values(p), Rotation = Values(r), Scale = Values(s), Active = active, Visible = visible };
                    childrenChanged = true;
                }
                else if (last != null && i < last.Nodes.Length && !ReferenceEquals(last.Nodes[i], old)) childrenChanged = true;
            }
            var worldPosition = source.position; var worldRotation = source.rotation; var worldScale = source.lossyScale;
            bool worldActive = source.gameObject.activeInHierarchy;
            if (!childrenChanged && last != null && last.Active == worldActive && Equal(last.Position, worldPosition) &&
                Equal(last.Rotation, worldRotation) && Equal(last.Scale, worldScale)) return last;
            NodePose[] poses;
            if (!childrenChanged && last != null) poses = last.Nodes;
            else { poses = new NodePose[nodes.Length]; for (int i = 0; i < poses.Length; i++) poses[i] = nodes[i].Last!; }
            return last = new ObjectPose { Position = Values(worldPosition), Rotation = Values(worldRotation), Scale = Values(worldScale), Active = worldActive, Nodes = poses };
        }
    }

    private sealed class CloneNode
    {
        public Transform Transform = null!;
        public Renderer[] Renderers = Array.Empty<Renderer>();
        public bool[] AuthoredVisibility = Array.Empty<bool>();
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
        public bool Active;
        public bool Visible;
        public int AppliedSerial;
    }

    // Ephemeron keys do not keep destroyed scene Transform wrappers alive indefinitely.
    private static readonly ConditionalWeakTable<Transform, Probe> probes = new();
    private readonly Dictionary<string, CloneNode> nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<Transform, Transform> transforms = new();
    private readonly Dictionary<Renderer, Renderer> rendererClones = new();
    private readonly Dictionary<Material, Material> materials = new();
    private readonly List<Renderer> renderers = new();
    private readonly Dictionary<string, NodePose> following = new(StringComparer.Ordinal);
    private readonly bool shareImmutableMaterials;
    private bool disposed;
    private ObjectPose? appliedLeft;
    private ObjectPose? appliedRight;
    private NodePose[]? appliedLeftNodes;
    private NodePose[]? appliedRightNodes;
    private float appliedMix;
    private bool appliedReveal;
    private bool invalidated = true;
    private int applySerial;
    public GameObject Root { get; }
    public IReadOnlyList<Renderer> Renderers => renderers;

    public VisualReplica(GameObject source, bool shareImmutableMaterials = false, int maximumNodes = DefaultMaximumNodes)
    {
        if (!source) throw new ArgumentNullException(nameof(source));
        Root = new GameObject(source.name + " [Replay visual]") { layer = source.layer };
        this.shareImmutableMaterials = shareImmutableMaterials;
        Root.SetActive(false);
        try
        {
            var directory = Directory(source.transform, maximumNodes);
            foreach (var entry in directory)
            {
                Transform clone;
                if (entry.Source == source.transform) clone = Root.transform;
                else
                {
                    clone = new GameObject(entry.Source.name) { layer = entry.Source.gameObject.layer }.transform;
                    clone.SetParent(transforms[entry.Source.parent], false);
                    clone.localPosition = entry.Source.localPosition;
                    clone.localRotation = entry.Source.localRotation;
                    clone.localScale = entry.Source.localScale;
                    clone.gameObject.SetActive(entry.Source.gameObject.activeSelf);
                }
                transforms.Add(entry.Source, clone);
                nodes.Add(entry.Path, new CloneNode
                {
                    Transform = clone, Position = clone.localPosition, Rotation = clone.localRotation,
                    Scale = clone.localScale, Active = entry.Source.gameObject.activeSelf,
                });
            }
            int lightCount = 0;
            foreach (var entry in directory)
            {
                var node = nodes[entry.Path]; var copied = new List<Renderer>();
                foreach (var light in entry.Source.GetComponents<Light>())
                {
                    if (++lightCount > NativeLightRules.MaximumLights) throw new InvalidOperationException("Native replica light count exceeds its recording limit.");
                    NativeLightAppearance.Copy(light, node.Transform.gameObject.AddComponent<Light>());
                }
                foreach (var original in entry.Renderers)
                {
                    var renderer = CopyRenderer(original, node.Transform);
                    if (renderer != null) { copied.Add(renderer); renderers.Add(renderer); rendererClones.Add(original, renderer); }
                }
                node.Renderers = copied.ToArray(); node.AuthoredVisibility = copied.Select(r => r.enabled).ToArray();
                node.Visible = copied.Count == 0 || copied.Any(r => r.enabled);
            }
            if (renderers.Count == 0) throw new InvalidOperationException("Object has no supported visual mesh: " + source.name);
            foreach (var component in Root.GetComponentsInChildren<Component>(true))
                if (!(component is Transform || component is MeshFilter || component is MeshRenderer || component is SkinnedMeshRenderer || component is Light))
                    throw new InvalidOperationException("Gameplay component leaked into visual replica: " + component.GetType().Name);
        }
        catch { Dispose(); throw; }
    }

    public static ObjectPose Capture(Transform source) => Capture(source, true);
    public static ObjectPose Capture(Transform source, bool sampleChildren, int maximumNodes = DefaultMaximumNodes)
    {
        if (!source) throw new ArgumentNullException(nameof(source));
        if (maximumNodes < 1 || maximumNodes > 1024) throw new ArgumentOutOfRangeException(nameof(maximumNodes));
        var probe = probes.GetValue(source, key => new Probe(key, maximumNodes));
        probe.EnsureLimit(maximumNodes);
        return probe.Sample(sampleChildren);
    }
    public static ObjectPose CaptureRoot(Transform source) => Capture(source, false);
    public static void InvalidateCapture(Transform source, bool hierarchyChanged = false)
    {
        if (!source || !hierarchyChanged) return;
        if (probes.TryGetValue(source, out var probe)) probe.InvalidateHierarchy();
        NativeVisualAppearance.InvalidateCapture(source);
        NativeLightAppearance.InvalidateCapture(source);
    }

    private static SourceNode[] Directory(Transform root, int maximumNodes)
    {
        if (maximumNodes < 1 || maximumNodes > 1024) throw new ArgumentOutOfRangeException(nameof(maximumNodes));
        var result = new List<SourceNode>();
        Visit(root, ".", 0);
        return result.ToArray();
        void Visit(Transform t, string path, int depth)
        {
            // A spawned drop/held/backpack entity has its own track, never duplicate it as
            // a chest's child. The root Item itself is intentionally NOT skipped.
            if (t != root && t.GetComponent<Item>()) return;
            if (depth > 64 || result.Count >= maximumNodes)
                throw new InvalidOperationException("Visual hierarchy exceeds the " + maximumNodes + "-node / 64-depth recording limit: " + root.name);
            var sourceRenderers = t.GetComponents<Renderer>().Where(r => r is MeshRenderer || r is SkinnedMeshRenderer).ToArray();
            result.Add(new SourceNode { Source = t, Path = path, Renderers = sourceRenderers });
            for (int i = 0; i < t.childCount; i++)
            {
                Transform child = t.GetChild(i);
                string name = Uri.EscapeDataString(child.name);
                string childPath = (path == "." ? "" : path + "/") + i.ToString(CultureInfo.InvariantCulture) + ":" + name;
                Visit(child, childPath, depth + 1);
            }
        }
    }

    private Renderer? CopyRenderer(Renderer original, Transform clone)
    {
        if (!original) return null;
        Renderer copy;
        if (original is SkinnedMeshRenderer skin)
        {
            var sourceBones = skin.bones;
            if (sourceBones.Any(b => b && !transforms.ContainsKey(b)))
                throw new InvalidOperationException("A replay mesh depends on bones outside its captured hierarchy: " + skin.name);
            var target = clone.gameObject.AddComponent<SkinnedMeshRenderer>();
            target.sharedMesh = skin.sharedMesh;
            target.bones = sourceBones.Select(b => b ? transforms[b] : null!).ToArray();
            target.rootBone = skin.rootBone && transforms.TryGetValue(skin.rootBone, out var bone) ? bone : null;
            target.localBounds = skin.localBounds; target.quality = skin.quality; target.updateWhenOffscreen = false;
            if (skin.sharedMesh) for (int i = 0; i < skin.sharedMesh.blendShapeCount; i++) target.SetBlendShapeWeight(i, skin.GetBlendShapeWeight(i));
            copy = target;
        }
        else if (original is MeshRenderer && original.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh)
        {
            clone.gameObject.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            copy = clone.gameObject.AddComponent<MeshRenderer>();
        }
        else return null;
        copy.sharedMaterials = original.sharedMaterials.Select(CloneMaterial).ToArray();
        var block = new MaterialPropertyBlock();
        original.GetPropertyBlock(block); block.SetFloat("_VertexGhost", 0); block.SetFloat("_Interactable", 0); copy.SetPropertyBlock(block);
        for (int i = 0; i < copy.sharedMaterials.Length; i++)
        {
            block.Clear(); original.GetPropertyBlock(block, i);
            if (!block.isEmpty) { block.SetFloat("_VertexGhost", 0); block.SetFloat("_Interactable", 0); copy.SetPropertyBlock(block, i); }
        }
        copy.enabled = original.enabled; copy.forceRenderingOff = false;
        copy.shadowCastingMode = original.shadowCastingMode; copy.receiveShadows = original.receiveShadows;
        // Native render layers and probes determine lighting as well as visibility.
        // Replacing them with Unity's defaults washes out emissive/unlit item variants.
        copy.renderingLayerMask = original.renderingLayerMask;
        copy.lightProbeUsage = original.lightProbeUsage;
        copy.reflectionProbeUsage = original.reflectionProbeUsage;
        copy.probeAnchor = original.probeAnchor && transforms.TryGetValue(original.probeAnchor, out var anchor) ? anchor : null;
        copy.lightProbeProxyVolumeOverride = original.lightProbeProxyVolumeOverride;
        copy.motionVectorGenerationMode = original.motionVectorGenerationMode;
        copy.allowOcclusionWhenDynamic = original.allowOcclusionWhenDynamic;
        copy.sortingLayerID = original.sortingLayerID; copy.sortingOrder = original.sortingOrder;
        copy.lightmapIndex = original.lightmapIndex; copy.lightmapScaleOffset = original.lightmapScaleOffset;
        copy.realtimeLightmapIndex = original.realtimeLightmapIndex; copy.realtimeLightmapScaleOffset = original.realtimeLightmapScaleOffset;
        return copy;
    }

    private Material CloneMaterial(Material source)
    {
        if (!source) return null!;
        // Only prefab-backed item tracks opt in; their tint/ghost changes use property
        // blocks. Animation-driven replicas retain owned material copies by default.
        if (shareImmutableMaterials) return source;
        if (materials.TryGetValue(source, out var existing)) return existing;
        var copy = new Material(source);
        if (copy.HasProperty("_VertexGhost")) copy.SetFloat("_VertexGhost", 0);
        materials.Add(source, copy); return copy;
    }

    public void Apply(ObjectPose left, ObjectPose? right, float mix, bool revealAuthoredRenderers = false)
    {
        if (disposed) throw new ObjectDisposedException(nameof(VisualReplica));
        mix = Mathf.Clamp01(mix); right ??= left;
        if (PoseVersionPolicy.CanSkip(left, right, mix, appliedLeft, appliedRight, appliedMix,
            invalidated, revealAuthoredRenderers, appliedReveal)) return;
        bool applyChildren = invalidated || appliedReveal != revealAuthoredRenderers ||
            !ReferenceEquals(appliedLeftNodes, left.Nodes) || !ReferenceEquals(appliedRightNodes, right.Nodes) ||
            !ReferenceEquals(left.Nodes, right.Nodes) && mix != appliedMix;
        bool boundary = mix >= 1;
        ObjectPose state = boundary ? right : left;
        Vector3 a = Point(left.Position), b = Point(right.Position);
        bool teleport = (b - a).sqrMagnitude > 400;
        Root.transform.SetPositionAndRotation(teleport ? (boundary ? b : a) : Vector3.LerpUnclamped(a, b, mix),
            Quaternion.SlerpUnclamped(Quat(left.Rotation), Quat(right.Rotation), mix));
        Root.transform.localScale = Vector3.LerpUnclamped(Point(left.Scale), Point(right.Scale), mix);
        if (applyChildren)
        {
            if (!ReferenceEquals(appliedRightNodes, right.Nodes) || invalidated)
            { following.Clear(); foreach (var pose in right.Nodes) following[pose.Path] = pose; }
            int serial = ++applySerial, applied = 0;
            foreach (var pose in left.Nodes)
            {
                if (!nodes.TryGetValue(pose.Path, out var node)) continue;
                node.AppliedSerial = serial; applied++;
                NodePose next = following.TryGetValue(pose.Path, out var match) ? match : pose;
                NodePose visibility = boundary ? next : pose;
                if (pose.Path != ".")
                {
                    node.Transform.SetLocalPositionAndRotation(Vector3.LerpUnclamped(Point(pose.Position), Point(next.Position), mix),
                        Quaternion.SlerpUnclamped(Quat(pose.Rotation), Quat(next.Rotation), mix));
                    node.Transform.localScale = Vector3.LerpUnclamped(Point(pose.Scale), Point(next.Scale), mix);
                    if (node.Transform.gameObject.activeSelf != visibility.Active) node.Transform.gameObject.SetActive(visibility.Active);
                }
                for (int i = 0; i < node.Renderers.Length; i++)
                    node.Renderers[i].enabled = revealAuthoredRenderers ? node.AuthoredVisibility[i] : visibility.Visible;
            }
            // Only omissions need restoring; never reset the entire tree then overwrite it.
            if (applied != nodes.Count) foreach (var entry in nodes)
            {
                var node = entry.Value; if (node.AppliedSerial == serial) continue;
                if (entry.Key != ".")
                { node.Transform.SetLocalPositionAndRotation(node.Position, node.Rotation); node.Transform.localScale = node.Scale; node.Transform.gameObject.SetActive(node.Active); }
                for (int i = 0; i < node.Renderers.Length; i++) node.Renderers[i].enabled = node.AuthoredVisibility[i];
            }
        }
        if (Root.activeSelf != state.Active) Root.SetActive(state.Active);
        appliedLeft = left; appliedRight = right; appliedMix = mix; appliedReveal = revealAuthoredRenderers;
        appliedLeftNodes = left.Nodes; appliedRightNodes = right.Nodes; invalidated = false;
    }

    public static bool Same(ObjectPose a, ObjectPose b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Active != b.Active || !Same(a.Position, b.Position) || !Same(a.Rotation, b.Rotation) || !Same(a.Scale, b.Scale) || a.Nodes.Length != b.Nodes.Length) return false;
        for (int i = 0; i < a.Nodes.Length; i++)
        {
            var x = a.Nodes[i]; var y = b.Nodes[i];
            if (!ReferenceEquals(x, y) && (x.Path != y.Path || x.Active != y.Active || x.Visible != y.Visible ||
                !Same(x.Position, y.Position) || !Same(x.Rotation, y.Rotation) || !Same(x.Scale, y.Scale))) return false;
        }
        return true;
    }

    private static bool Same(float[] a, float[] b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    private static bool Equal(float[] p, Vector3 v) => p.Length == 3 && p[0] == v.x && p[1] == v.y && p[2] == v.z;
    private static bool Equal(float[] p, Quaternion v) => p.Length == 4 && p[0] == v.x && p[1] == v.y && p[2] == v.z && p[3] == v.w;
    private static float[] Values(Vector3 v) => new[] { v.x, v.y, v.z };
    private static float[] Values(Quaternion q) => new[] { q.x, q.y, q.z, q.w };
    private static Vector3 Point(float[] p) => new(p[0], p[1], p[2]);
    private static Quaternion Quat(float[] q) => new Quaternion(q[0], q[1], q[2], q[3]).normalized;
    public void InvalidatePose() => invalidated = true;
    public bool TryCloneTransform(Transform source, out Transform target) => transforms.TryGetValue(source, out target!);
    public bool TryCloneRenderer(Renderer source, out Renderer target) => rendererClones.TryGetValue(source, out target!);
    public bool TryCloneTransform(string path, out Transform target)
    { if (nodes.TryGetValue(path, out var node)) { target = node.Transform; return true; } target = null!; return false; }
    public bool TryCloneRenderer(string path, int index, out Renderer target)
    { if (nodes.TryGetValue(path, out var node) && index >= 0 && index < node.Renderers.Length) { target = node.Renderers[index]; return true; } target = null!; return false; }
    public bool TryCloneLight(string path, int index, out Light target)
    {
        if (nodes.TryGetValue(path, out var node))
        { var lights = node.Transform.GetComponents<Light>(); if (index >= 0 && index < lights.Length) { target = lights[index]; return true; } }
        target = null!; return false;
    }
    public void Hide() { if (Root && Root.activeSelf) { Root.SetActive(false); invalidated = true; } }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (Root) { Root.SetActive(false); Object.Destroy(Root); }
        foreach (var material in materials.Values) if (material) Object.Destroy(material);
        materials.Clear(); transforms.Clear(); rendererClones.Clear(); nodes.Clear(); renderers.Clear(); following.Clear();
    }
}
