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
        public GameObject GameObject = null!;
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
                Vector3 p = Vector3.zero; Quaternion r = Quaternion.identity;
                if (i != 0) t.GetLocalPositionAndRotation(out p, out r);
                var s = i == 0 ? Vector3.one : t.localScale;
                bool active = node.GameObject.activeSelf;
                bool visible = node.Renderers.Length == 0;
                foreach (var renderer in node.Renderers)
                    if (renderer && renderer.enabled && !renderer.forceRenderingOff) { visible = true; break; }
                var old = node.Last;
                if (old == null || old.Path != node.Path || old.Active != active || old.Visible != visible ||
                    !Equal(old.Position, p) || !Equal(old.Rotation, r) || !Equal(old.Scale, s))
                {
                    node.Last = new NodePose(node.Path, old != null && Equal(old.Position, p) ? old.Position : Values(p), old != null && Equal(old.Rotation, r) ? old.Rotation : Values(r), old != null && Equal(old.Scale, s) ? old.Scale : Values(s), active, visible);
                    childrenChanged = true;
                }
                else if (last != null && i < last.Nodes.Length && !ReferenceEquals(last.Nodes[i], old)) childrenChanged = true;
            }
            source.GetPositionAndRotation(out var worldPosition, out var worldRotation); var worldScale = source.lossyScale;
            bool worldActive = source.gameObject.activeInHierarchy;
            if (!childrenChanged && last != null && last.Active == worldActive && Equal(last.Position, worldPosition) &&
                Equal(last.Rotation, worldRotation) && Equal(last.Scale, worldScale)) return last;
            NodePose[] poses;
            if (!childrenChanged && last != null) poses = last.Nodes;
            else { poses = new NodePose[nodes.Length]; for (int i = 0; i < poses.Length; i++) poses[i] = nodes[i].Last!; }
            return last = new ObjectPose(last != null && Equal(last.Position, worldPosition) ? last.Position : Values(worldPosition), last != null && Equal(last.Rotation, worldRotation) ? last.Rotation : Values(worldRotation), last != null && Equal(last.Scale, worldScale) ? last.Scale : Values(worldScale), worldActive, poses);
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
    private sealed class BuildVisit
    {
        public Transform Source = null!;
        public string Path = "";
        public int Depth, Child;
        public bool Entered;
    }
    internal readonly struct RendererEntry
    {
        public readonly string Path;
        public readonly int Index;
        public readonly Renderer Source, Clone;
        public RendererEntry(string path, int index, Renderer source, Renderer clone)
        { Path = path; Index = index; Source = source; Clone = clone; }
    }
    internal readonly struct LightEntry
    {
        public readonly string Path;
        public readonly int Index;
        public readonly Light Source, Clone;
        public LightEntry(string path, int index, Light source, Light clone)
        { Path = path; Index = index; Source = source; Clone = clone; }
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
    private readonly GameObject source;
    private readonly int maximumNodes;
    private readonly Stack<BuildVisit> visits = new();
    private readonly List<SourceNode> directory = new();
    private readonly List<RendererEntry> rendererDirectory = new();
    private readonly List<LightEntry> lightDirectory = new();
    private readonly Dictionary<Transform, string> clonePaths = new();
    private readonly List<Renderer> buildingRenderers = new();
    private Light[] buildingLights = Array.Empty<Light>();
    private Transform[]? sourceBones, mappedBones;
    private int buildNode, buildRenderer, buildLight, buildBone;
    private SkinnedMeshRenderer? blendSource, blendTarget;
    private int blendShape, retainedSlots;
    private bool renderPhase, nodeStarted;
    private int resetNode = -1, resetRenderer;
    private readonly List<GameObject> ownedObjects = new();
    private static readonly Queue<VisualReplica> retirement = new();
    private static int retirementCost;
    // Active, pooled and retiring replicas share these credits. Consequently
    // cancellation cannot append an unbounded second scene to the retire queue.
    private const int MaximumRetainedRoots = 4096, MaximumRetainedNodes = 2097152;
    private static int retainedRoots, retainedNodes, drainFrame = -1, drainedThisFrame;
    private readonly bool tracked;
    private int retainedNodeCount;
    private int queuedCost;
    public static bool CanStartBuild => retirementCost < 8192 && retainedRoots < MaximumRetainedRoots && retainedNodes < MaximumRetainedNodes;
    private int retireObject = -1, retireMaterial;
    private Material[] retiredMaterials = Array.Empty<Material>();
    public bool IsReady { get; private set; }
    public bool IsResetting => resetNode >= 0;
    public int RetainedCost => Math.Max(1, directory.Count + renderers.Count * 4 + (materials.Count + retainedSlots) * 4);
    internal IReadOnlyList<RendererEntry> RendererDirectory => rendererDirectory;
    internal IReadOnlyList<LightEntry> LightDirectory => lightDirectory;
    internal int SourceNodeCount => directory.Count;
    internal Transform SourceTransform(int index) => directory[index].Source;
    internal string ClonePath(Transform transform) => clonePaths.TryGetValue(transform, out var path) ? path : "";
    public GameObject Root { get; }
    public IReadOnlyList<Renderer> Renderers => renderers;

    public VisualReplica(GameObject source, bool shareImmutableMaterials = false, int maximumNodes = DefaultMaximumNodes)
        : this(source, shareImmutableMaterials, maximumNodes, false) { }
    private VisualReplica(GameObject source, bool shareImmutableMaterials, int maximumNodes, bool incremental)
    {
        if (!source) throw new ArgumentNullException(nameof(source));
        if (maximumNodes < 1 || maximumNodes > 1024) throw new ArgumentOutOfRangeException(nameof(maximumNodes));
        this.source = source; this.maximumNodes = maximumNodes;
        tracked = incremental;
        Root = new GameObject(source.name + " [Replay visual]") { layer = source.layer };
        ownedObjects.Add(Root);
        if (tracked) { retainedRoots++; retainedNodes++; retainedNodeCount++; }
        this.shareImmutableMaterials = shareImmutableMaterials;
        Root.SetActive(false);
        visits.Push(new BuildVisit { Source = source.transform, Path = "." });
        if (incremental) return;
        try
        {
            while (!IsReady) BuildNext();
        }
        catch { Dispose(); throw; }
    }
    public static VisualReplica BeginBuild(GameObject source, bool shareImmutableMaterials = false, int maximumNodes = DefaultMaximumNodes)
        => new(source, shareImmutableMaterials, maximumNodes, true);
    public bool ContinueBuild(PresentationWorkBudget budget)
    {
        if (disposed) throw new ObjectDisposedException(nameof(VisualReplica));
        if (!IsReady && !CanStartBuild) return false;
        while (!IsReady && budget.Build()) BuildNext();
        return IsReady;
    }
    private void BuildNext()
    {
        if (!source) throw new InvalidOperationException("Native visual template was unloaded during construction.");
        if (!renderPhase)
        {
            if (visits.Count == 0) { renderPhase = true; return; }
            var visit = visits.Peek(); var t = visit.Source;
            if (!t) throw new InvalidOperationException("Native visual hierarchy changed during construction.");
            if (!visit.Entered)
            {
                if (t != source.transform && t.GetComponent<Item>()) { visits.Pop(); return; }
                if (visit.Depth > 64 || directory.Count >= maximumNodes)
                    throw new InvalidOperationException("Visual hierarchy exceeds the " + maximumNodes + "-node / 64-depth recording limit: " + source.name);
                if (tracked && t != source.transform && retainedNodes >= MaximumRetainedNodes) return;
                var entry = new SourceNode { Source = t, GameObject = t.gameObject, Path = visit.Path,
                    Renderers = t.GetComponent<TMPro.TMP_Text>() ? Array.Empty<Renderer>() : t.GetComponents<Renderer>().Where(r => r is MeshRenderer || r is SkinnedMeshRenderer).ToArray() };
                if (entry.Renderers.Length > NativeVisualAppearanceRules.MaximumMaterials) throw new InvalidOperationException("Native renderer index exceeds its recording limit.");
                directory.Add(entry);
                Transform clone = Root.transform;
                if (t != source.transform)
                {
                    clone = new GameObject(t.name) { layer = t.gameObject.layer }.transform;
                    if (tracked) { retainedNodes++; retainedNodeCount++; }
                    ownedObjects.Add(clone.gameObject); clone.SetParent(transforms[t.parent], false);
                    clone.SetLocalPositionAndRotation(t.localPosition, t.localRotation); clone.localScale = t.localScale;
                    clone.gameObject.SetActive(t.gameObject.activeSelf);
                }
                transforms.Add(t, clone); clonePaths.Add(clone, visit.Path);
                nodes.Add(visit.Path, new CloneNode { Transform = clone, Position = clone.localPosition,
                    Rotation = clone.localRotation, Scale = clone.localScale, Active = t.gameObject.activeSelf });
                visit.Entered = true; return;
            }
            if (visit.Child < t.childCount)
            {
                int sibling = visit.Child++; var child = t.GetChild(sibling);
                visits.Push(new BuildVisit { Source = child, Depth = visit.Depth + 1,
                    Path = (visit.Path == "." ? "" : visit.Path + "/") + sibling.ToString(CultureInfo.InvariantCulture) + ":" + Uri.EscapeDataString(child.name) });
            }
            else visits.Pop();
            return;
        }
        if (blendSource)
        { CopyBlendShapes(); return; }
        if (buildNode >= directory.Count)
        {
            if (renderers.Count == 0) throw new InvalidOperationException("Object has no supported visual mesh: " + source.name);
            IsReady = true; return;
        }
        var current = directory[buildNode]; var node = nodes[current.Path];
        if (!nodeStarted)
        { buildingLights = current.Source.GetComponents<Light>(); buildLight = buildRenderer = 0; buildingRenderers.Clear(); nodeStarted = true; return; }
        if (buildLight < buildingLights.Length)
        {
            if (lightDirectory.Count >= NativeLightRules.MaximumLights) throw new InvalidOperationException("Native replica light count exceeds its recording limit.");
            var original = buildingLights[buildLight]; var copy = node.Transform.gameObject.AddComponent<Light>();
            NativeLightAppearance.Copy(original, copy); lightDirectory.Add(new LightEntry(current.Path, buildLight++, original, copy)); return;
        }
        if (buildRenderer < current.Renderers.Length)
        {
            var original = current.Renderers[buildRenderer];
            if (original is SkinnedMeshRenderer skin)
            {
                if (sourceBones == null)
                { sourceBones = skin.bones; if (sourceBones.Length > 1024) throw new InvalidOperationException("Native skin bone count exceeds the visual hierarchy limit."); mappedBones = new Transform[sourceBones.Length]; buildBone = 0; return; }
                if (buildBone < sourceBones.Length)
                {
                    int end = Math.Min(buildBone + 16, sourceBones.Length);
                    for (; buildBone < end; buildBone++)
                    {
                        var bone = sourceBones[buildBone];
                        if (bone && !transforms.TryGetValue(bone, out mappedBones![buildBone]))
                            throw new InvalidOperationException("A replay mesh depends on bones outside its captured hierarchy: " + skin.name);
                    }
                    return;
                }
            }
            var renderer = CopyRenderer(original, node.Transform, mappedBones, false);
            if (renderer != null)
            {
                if (rendererDirectory.Count >= NativeVisualAppearanceRules.MaximumRenderers) throw new InvalidOperationException("Native renderer count exceeds its recording limit.");
                rendererDirectory.Add(new RendererEntry(current.Path, buildingRenderers.Count, original, renderer)); buildingRenderers.Add(renderer); renderers.Add(renderer); rendererClones.Add(original, renderer);
                retainedSlots += renderer.sharedMaterials.Length;
                if (original is SkinnedMeshRenderer blend && renderer is SkinnedMeshRenderer target) { blendSource = blend; blendTarget = target; blendShape = 0; }
            }
            buildRenderer++; sourceBones = mappedBones = null; return;
        }
        node.Renderers = buildingRenderers.ToArray(); node.AuthoredVisibility = buildingRenderers.Select(r => r.enabled).ToArray();
        node.Visible = buildingRenderers.Count == 0 || buildingRenderers.Any(r => r.enabled);
        foreach (var component in node.Transform.GetComponents<Component>())
            if (!(component is Transform || component is MeshFilter || component is MeshRenderer || component is SkinnedMeshRenderer || component is Light))
                throw new InvalidOperationException("Gameplay component leaked into visual replica: " + component.GetType().Name);
        buildNode++; nodeStarted = false;
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
    // Retiring a native map root releases only recorder-side directories. Pure
    // snapshots already handed to the writer/history remain immutable and live.
    public static void ReleaseCapture(Transform source)
    {
        if (!source) return;
        probes.Remove(source);
        NativeVisualAppearance.InvalidateCapture(source);
        NativeLightAppearance.InvalidateCapture(source);
    }
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
            var sourceRenderers = t.GetComponent<TMPro.TMP_Text>() ? Array.Empty<Renderer>() : t.GetComponents<Renderer>().Where(r => r is MeshRenderer || r is SkinnedMeshRenderer).ToArray();
            result.Add(new SourceNode { Source = t, GameObject = t.gameObject, Path = path, Renderers = sourceRenderers });
            for (int i = 0; i < t.childCount; i++)
            {
                Transform child = t.GetChild(i);
                string name = Uri.EscapeDataString(child.name);
                string childPath = (path == "." ? "" : path + "/") + i.ToString(CultureInfo.InvariantCulture) + ":" + name;
                Visit(child, childPath, depth + 1);
            }
        }
    }

    private Renderer? CopyRenderer(Renderer original, Transform clone, Transform[]? mapped = null, bool copyBlendShapes = true)
    {
        if (!original) return null;
        Renderer copy;
        if (original is SkinnedMeshRenderer skin)
        {
            var sourceBones = mapped == null ? skin.bones : Array.Empty<Transform>();
            if (mapped == null && sourceBones.Any(b => b && !transforms.ContainsKey(b)))
                throw new InvalidOperationException("A replay mesh depends on bones outside its captured hierarchy: " + skin.name);
            var target = clone.gameObject.AddComponent<SkinnedMeshRenderer>();
            target.sharedMesh = skin.sharedMesh;
            target.bones = mapped ?? sourceBones.Select(b => b ? transforms[b] : null!).ToArray();
            target.rootBone = skin.rootBone && transforms.TryGetValue(skin.rootBone, out var bone) ? bone : null;
            target.localBounds = skin.localBounds; target.quality = skin.quality; target.updateWhenOffscreen = false;
            if (copyBlendShapes && skin.sharedMesh) for (int i = 0; i < skin.sharedMesh.blendShapeCount; i++) target.SetBlendShapeWeight(i, skin.GetBlendShapeWeight(i));
            copy = target;
        }
        else if (original is MeshRenderer && original.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh)
        {
            clone.gameObject.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            copy = clone.gameObject.AddComponent<MeshRenderer>();
        }
        else return null;
        CopyRendererSettings(original, copy);
        return copy;
    }
    private void CopyRendererSettings(Renderer original, Renderer copy)
    {
        copy.gameObject.layer = original.gameObject.layer;
        int oldSlots = copy.sharedMaterials.Length;
        var block = new MaterialPropertyBlock();
        for (int i = 0; i < oldSlots; i++) copy.SetPropertyBlock(block, i);
        copy.sharedMaterials = original.sharedMaterials.Select(CloneMaterial).ToArray();
        original.GetPropertyBlock(block); block.SetFloat("_VertexGhost", 0); block.SetFloat("_Interactable", 0); copy.SetPropertyBlock(block);
        for (int i = 0; i < copy.sharedMaterials.Length; i++)
        {
            block.Clear(); original.GetPropertyBlock(block, i);
            if (!block.isEmpty) { block.SetFloat("_VertexGhost", 0); block.SetFloat("_Interactable", 0); }
            copy.SetPropertyBlock(block, i);
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
    }

    private Material CloneMaterial(Material source)
    {
        if (!source) return null!;
        // Only prefab-backed item tracks opt in; their tint/ghost changes use property
        // blocks. Animation-driven replicas retain owned material copies by default.
        if (shareImmutableMaterials) return source;
        if (materials.TryGetValue(source, out var existing)) return existing;
        var copy = new Material(source);
        NativeVisualAppearance.MarkReplayMaterial(copy);
        if (copy.HasProperty("_VertexGhost")) copy.SetFloat("_VertexGhost", 0);
        materials.Add(source, copy); return copy;
    }

    public void Apply(ObjectPose left, ObjectPose? right, float mix, bool revealAuthoredRenderers = false)
    {
        if (disposed) throw new ObjectDisposedException(nameof(VisualReplica));
        if (!IsReady || IsResetting) throw new InvalidOperationException("An unfinished visual cannot be presented.");
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
    // Rebinding a completed instance to a different recorded lifetime never
    // exposes the previous lifetime's hierarchy/material/property-block state.
    public void BeginReuse()
    {
        if (disposed || !IsReady || IsResetting) throw new InvalidOperationException("Only a completed visual can enter the template pool.");
        Hide(); Root.transform.SetParent(null, false); resetNode = 0; resetRenderer = 0;
        appliedLeft = appliedRight = null; appliedLeftNodes = appliedRightNodes = null; following.Clear(); invalidated = true;
    }
    public bool ContinueReuse(PresentationWorkBudget budget)
    {
        if (IsResetting && !CanStartBuild) return false;
        while (resetNode >= 0 && budget.Build())
        {
            if (blendSource) { CopyBlendShapes(); continue; }
            if (resetNode < directory.Count)
            {
                var entry = directory[resetNode++]; var node = nodes[entry.Path];
                node.Transform.gameObject.layer = entry.Source.gameObject.layer;
                if (entry.Path != ".")
                { node.Transform.SetLocalPositionAndRotation(node.Position, node.Rotation); node.Transform.localScale = node.Scale; node.Transform.gameObject.SetActive(node.Active); }
                continue;
            }
            if (resetRenderer < rendererDirectory.Count)
            {
                var entry = rendererDirectory[resetRenderer++];
                if (entry.Clone is SkinnedMeshRenderer skin && entry.Source is SkinnedMeshRenderer original)
                { skin.sharedMesh = original.sharedMesh; skin.localBounds = original.localBounds;
                    blendSource = original; blendTarget = skin; blendShape = 0; }
                else if (entry.Clone.TryGetComponent<MeshFilter>(out var target) && entry.Source.TryGetComponent<MeshFilter>(out var sourceFilter)) target.sharedMesh = sourceFilter.sharedMesh;
                CopyRendererSettings(entry.Source, entry.Clone); continue;
            }
            int light = resetRenderer++ - rendererDirectory.Count;
            if (light < lightDirectory.Count) { var entry = lightDirectory[light]; NativeLightAppearance.Copy(entry.Source, entry.Clone); continue; }
            resetNode = -1;
        }
        return resetNode < 0;
    }
    private void CopyBlendShapes()
    {
        int count = blendSource!.sharedMesh ? blendSource.sharedMesh.blendShapeCount : 0;
        int end = Math.Min(blendShape + 16, count);
        for (; blendShape < end; blendShape++) blendTarget!.SetBlendShapeWeight(blendShape, blendSource.GetBlendShapeWeight(blendShape));
        if (blendShape >= count) blendSource = blendTarget = null;
    }
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
    public void Sleep()
    { Hide(); if (Root && Root.transform.parent) { Root.transform.SetParent(null, true); invalidated = true; } }
    public void Retire()
    {
        if (disposed) return; disposed = true; Hide();
        queuedCost = RetainedCost; retirementCost += queuedCost;
        retireObject = ownedObjects.Count - 1; retiredMaterials = materials.Values.ToArray(); retirement.Enqueue(this);
    }
    public static void DrainRetired(PresentationWorkBudget budget, int maximum = 32)
    {
        if (drainFrame != Time.frameCount) { drainFrame = Time.frameCount; drainedThisFrame = 0; }
        maximum = Math.Min(maximum, 64 - drainedThisFrame);
        for (int i = 0; i < maximum && retirement.Count > 0 && budget.Build(); i++)
        {
            drainedThisFrame++;
            var visual = retirement.Peek();
            if (visual.retireObject >= 0)
            { var go = visual.ownedObjects[visual.retireObject--]; if (go) Object.Destroy(go);
                if (visual.tracked) { retainedNodes--; visual.retainedNodeCount--; } }
            else if (visual.retireMaterial < visual.retiredMaterials.Length)
            { var material = visual.retiredMaterials[visual.retireMaterial++]; if (material) Object.Destroy(material); }
            else { retirement.Dequeue(); retirementCost -= visual.queuedCost; visual.ClearReferences(); }
        }
    }
    public static void FlushRetired()
    {
        while (retirement.Count > 0)
        {
            var visual = retirement.Dequeue();
            retirementCost -= visual.queuedCost;
            if (visual.Root) Object.Destroy(visual.Root);
            foreach (var material in visual.retiredMaterials) if (material) Object.Destroy(material);
            visual.ClearReferences();
        }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (Root) { Root.SetActive(false); Object.Destroy(Root); }
        foreach (var material in materials.Values) if (material) Object.Destroy(material);
        ClearReferences();
    }
    private void ClearReferences()
    {
        if (tracked) { retainedRoots--; retainedNodes -= retainedNodeCount; retainedNodeCount = 0; }
        materials.Clear(); transforms.Clear(); rendererClones.Clear(); nodes.Clear(); renderers.Clear(); following.Clear();
        visits.Clear(); directory.Clear(); rendererDirectory.Clear(); lightDirectory.Clear(); clonePaths.Clear(); ownedObjects.Clear();
        sourceBones = mappedBones = null; retiredMaterials = Array.Empty<Material>(); buildingRenderers.Clear(); buildingLights = Array.Empty<Light>();
        blendSource = blendTarget = null;
    }
}
