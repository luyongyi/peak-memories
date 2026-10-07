using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

// Never clone a Character prefab: Awake/OnDestroy would register players and touch Photon.
// This hierarchy has ONLY Transforms, renderers and mesh filters. Recorded
// parent-local joint poses drive it; there is no animation or physics fallback.
internal sealed class VisualActor : IDisposable
{
    private readonly GameObject root = new("PEAK Replay Lab - visual only");
    private readonly Dictionary<Transform, Transform> transforms = new();
    private readonly Dictionary<Material, Material> materials = new();
    private readonly Dictionary<Renderer, Renderer> renderers = new();
    private readonly Dictionary<string, MeshRenderer> webWraps = new(StringComparer.Ordinal);
    private readonly List<Material> webMaterials = new();
    private CharacterCustomization customization = null!;
    private Appearance? appearance;
    private readonly Dictionary<string, Transform> joints = new(StringComparer.Ordinal);
    private NodePose[] boundJoints = Array.Empty<NodePose>();
    private Transform?[] jointBindings = Array.Empty<Transform?>();
    private readonly Dictionary<int, (Transform View, Renderer[] Renderers, Transform[] Parents, Transform?[] Slots)> backpacks = new();
    private int backpackType = int.MinValue;
    private bool backpackWorn, posed;
    private Transform hip = null!;
    private Transform? head;
    private Renderer[] nameHats = Array.Empty<Renderer>();
    public ActorFrame? State { get; private set; }
    public Vector3 Position => hip.position;
    public Vector3 HeadPosition => head ? head!.position : hip.position + Vector3.up * .8f;
    // Native Misc/PlayerName follows the head in FollowBodypart.LateUpdate.
    // It is not a recorded joint and its copied Transform has no follow script.
    public Vector3 NamePosition => ReplayNameplateAnchor.Position(HeadPosition, nameHats);
    public bool Visible => root && root.activeSelf;
    public int MissingJointCount { get; private set; }
    public int RendererCount { get; private set; }

    public VisualActor(Character source)
    {
        root.SetActive(false);
        try
        {
            var skip = new HashSet<Transform>();
            var hidden = source.refs.hideTheBody;
            if (hidden)
            {
                if (hidden.shadowCaster) skip.Add(hidden.shadowCaster.transform);
                if (hidden.shadowCasterHat) skip.Add(hidden.shadowCasterHat.transform);
            }
            CopyTree(source.transform, root.transform, skip, 0);
            foreach (var entry in transforms) joints.Add(ActorJointPaths.Path(entry.Key, source.transform), entry.Value);
            foreach (var entry in transforms) CopyRenderer(entry.Key, entry.Value);
            if (RendererCount == 0) throw new InvalidOperationException("Player has no renderable model.");
            hip = transforms[source.refs.hip.transform];
            if (source.refs.head && transforms.TryGetValue(source.refs.head.transform, out var headTransform)) head = headTransform;
            var bags = source.GetComponent<CharacterBackpackHandler>();
            if (bags)
            {
                RegisterBag(1, bags.backpackVisuals); RegisterBag(2, bags.fannypackVisuals);
                RegisterBag(3, bags.jetpackVisuals); RegisterBag(4, bags.rocketpackVisuals);
            }
            customization = source.refs.customization;
            nameHats = customization.refs.playerHats.Select(Render).Where(r => r).Cast<Renderer>().ToArray();
            var nativeWraps = source.refs.afflictions ? source.refs.afflictions.webWraps : null;
            if (nativeWraps != null)
                foreach (var original in nativeWraps)
                {
                    if (!original || !(Render(original) is MeshRenderer copied)) continue;
                    // Native tweening creates a material per wrap. The replay
                    // needs the same independence for unequal recorded phases.
                    if (copied.sharedMaterial)
                    { var material = new Material(copied.sharedMaterial); copied.sharedMaterial = material; webMaterials.Add(material); }
                    webWraps[ActorJointPaths.Path(original.transform, source.transform)] = copied;
                    copied.gameObject.SetActive(false);
                }
            VerifyVisualOnly();
        }
        catch { Dispose(); throw; }
    }

    private void CopyTree(Transform src, Transform parent, HashSet<Transform> skip, int depth)
    {
        if (skip.Contains(src) || src.GetComponent<Item>()) return; // ItemPlayback owns held/backpack props.
        if (depth > 100 || transforms.Count > 4096) throw new InvalidOperationException("Unexpected character hierarchy size.");
        var go = new GameObject(src.name) { layer = 0 };
        Transform dst = go.transform;
        dst.SetParent(parent, false);
        dst.localPosition = src.localPosition;
        dst.localRotation = src.localRotation;
        dst.localScale = src.localScale;
        go.SetActive(src.gameObject.activeSelf);
        transforms.Add(src, dst);
        foreach (Transform child in src) CopyTree(child, dst, skip, depth + 1);
    }

    private void CopyRenderer(Transform src, Transform dst)
    {
        Renderer original = src.GetComponent<Renderer>();
        if (!original || original.shadowCastingMode == ShadowCastingMode.ShadowsOnly) return;
        Renderer copy;
        if (original is SkinnedMeshRenderer skin)
        {
            if (skin.bones.Any(b => b && !transforms.ContainsKey(b))) return;
            var newSkin = dst.gameObject.AddComponent<SkinnedMeshRenderer>();
            newSkin.sharedMesh = skin.sharedMesh;
            newSkin.bones = skin.bones.Select(b => b ? transforms[b] : null!).ToArray();
            newSkin.rootBone = skin.rootBone && transforms.TryGetValue(skin.rootBone, out var bone) ? bone : null;
            newSkin.localBounds = skin.localBounds;
            newSkin.updateWhenOffscreen = true;
            newSkin.quality = skin.quality;
            for (int i = 0; skin.sharedMesh && i < skin.sharedMesh.blendShapeCount; i++) newSkin.SetBlendShapeWeight(i, skin.GetBlendShapeWeight(i));
            copy = newSkin;
        }
        else if (original is MeshRenderer && src.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh)
        {
            dst.gameObject.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            copy = dst.gameObject.AddComponent<MeshRenderer>();
        }
        else return;
        copy.sharedMaterials = original.sharedMaterials.Select(CloneMaterial).ToArray();
        var block = new MaterialPropertyBlock();
        original.GetPropertyBlock(block);
        block.SetFloat("_VertexGhost", 0); // first-person body hiding must not hide the replay
        copy.SetPropertyBlock(block);
        for (int i = 0; i < copy.sharedMaterials.Length; i++)
        {
            block.Clear();
            original.GetPropertyBlock(block, i);
            if (!block.isEmpty) { block.SetFloat("_VertexGhost", 0); copy.SetPropertyBlock(block, i); }
        }
        copy.enabled = original.enabled;
        copy.shadowCastingMode = ShadowCastingMode.On;
        copy.receiveShadows = true;
        renderers.Add(original, copy);
        RendererCount++;
    }

    private Material CloneMaterial(Material source)
    {
        if (!source) return null!;
        if (materials.TryGetValue(source, out var cached)) return cached;
        var copy = new Material(source);
        if (copy.HasProperty("_VertexGhost")) copy.SetFloat("_VertexGhost", 0);
        materials.Add(source, copy);
        return copy;
    }

    public void VerifyVisualOnly()
    {
        foreach (var component in root.GetComponentsInChildren<Component>(true))
            if (!(component is Transform || component is MeshFilter || component is MeshRenderer ||
                  component is SkinnedMeshRenderer))
                throw new InvalidOperationException("Unexpected component in visual replay: " + component.GetType().Name);
    }

    public void Hide() { if (root && root.activeSelf) { root.SetActive(false); posed = false; } }

    private void RegisterBag(int type, BackpackOnBackVisuals source)
    {
        if (!source || !source.view || !transforms.TryGetValue(source.view.transform, out var view)) return;
        var parents = new List<Transform>();
        for (var parent = view.parent; parent && parent != root.transform; parent = parent.parent) parents.Add(parent);
        var slots = new Transform?[Math.Min(4, source.backpackSlots?.Length ?? 0)];
        for (int i = 0; i < slots.Length; i++)
            if (source.backpackSlots![i] && transforms.TryGetValue(source.backpackSlots[i], out var clone)) slots[i] = clone;
        backpacks[type] = (view, view.GetComponentsInChildren<Renderer>(true), parents.ToArray(), slots);
    }

    public Transform? BackpackSlot(int slot)
    {
        var state = State;
        if (!Visible || state == null || !state.BackpackWorn || !backpacks.TryGetValue(state.BackpackType, out var bag) ||
            slot < 0 || slot >= bag.Slots.Length) return null;
        return bag.Slots[slot];
    }

    private void ApplyBackpack(ActorFrame state, bool force)
    {
        if (!force && state.BackpackType == backpackType && state.BackpackWorn == backpackWorn) return;
        backpackType = state.BackpackType; backpackWorn = state.BackpackWorn;
        // Native first-person rendering hides one's own pack; the replay is third-person.
        // Do not call CharacterBackpackHandler/RefreshVisuals, which would spawn network Items.
        foreach (var pair in backpacks)
        {
            bool active = state.BackpackWorn && state.BackpackType == pair.Key;
            if (pair.Value.View.gameObject.activeSelf != active) pair.Value.View.gameObject.SetActive(active);
            if (active)
            {
                foreach (var parent in pair.Value.Parents) if (!parent.gameObject.activeSelf) parent.gameObject.SetActive(true);
                foreach (var renderer in pair.Value.Renderers) if (renderer && !renderer.enabled) renderer.enabled = true;
            }
        }
    }

    public void Pose(ActorFrame state, ActorJointPoseSample pose)
    {
        if (pose.Count == 0) throw new InvalidOperationException("录像缺少人物关节数据。");
        if (!root.activeSelf) root.SetActive(true);
        State = state; // Discrete appearance/equipment follows the frame clock.
        bool appearanceChanged = !state.Appearance.Equals(appearance);
        ApplyAppearance(state.Appearance);
        root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        MissingJointCount = 0;
        if (!ActorJointReplayRules.SameTopology(boundJoints, pose.Topology))
        {
            jointBindings = new Transform?[pose.Count];
            for (int i = 0; i < jointBindings.Length; i++)
                if (joints.TryGetValue(pose.Topology[i].Path, out var joint)) jointBindings[i] = joint;
            boundJoints = pose.Topology;
        }
        for (int i = 0; i < pose.Count; i++)
        {
            var bone = jointBindings[i];
            if (!bone) { MissingJointCount++; continue; }
            var sample = pose.At(i); var a = sample.Left; var b = sample.Right;
            if (bone!.gameObject.activeSelf != a.Active) bone.gameObject.SetActive(a.Active);
            bone.localScale = Vector3.Lerp(Point(a.Scale), Point(b.Scale), sample.Mix);
            Vector3 position = Vector3.Lerp(Point(a.Position), Point(b.Position), sample.Mix);
            Quaternion rotation = Quaternion.Slerp(Quat(a.Rotation), Quat(b.Rotation), sample.Mix);
            if (a.Path == ".") bone.SetPositionAndRotation(position, rotation);
            else
            {
                // A low-rate sleeve/finger follows its freshly posed parent on
                // every rendered frame while its own local motion interpolates
                // between the two real observations on that node's clock.
                bone.localPosition = position;
                bone.localRotation = rotation;
            }
        }
        ApplyBackpack(state, appearanceChanged || !posed);
        ApplyWebWrap(state.WebWrap);
        posed = true;
    }

    private void ApplyWebWrap(WebWrapReplayFrame? frame)
    {
        foreach (var wrap in webWraps.Values) if (wrap && wrap.gameObject.activeSelf) wrap.gameObject.SetActive(false);
        if (frame == null) return;
        foreach (var part in frame.Parts)
        {
            if (!webWraps.TryGetValue(part.Path, out var wrap) || !wrap) continue;
            wrap.gameObject.SetActive(part.Active); wrap.enabled = part.Active;
            wrap.transform.localRotation = Quat(part.Rotation);
            // Scale only the audited VFX parent, never a body bone if a future
            // game places this renderer elsewhere in its character hierarchy.
            var parent = wrap.transform.parent;
            if (parent && parent.name.StartsWith("VFX_WebWrap", StringComparison.Ordinal)) parent.localScale = Point(part.ParentScale);
            if (wrap.sharedMaterial && wrap.sharedMaterial.HasProperty("_Clip")) wrap.sharedMaterial.SetFloat("_Clip", part.Clip);
        }
    }

    private static Vector3 Point(float[] a) => new(a[0], a[1], a[2]);
    private static Quaternion Quat(float[] q) => new Quaternion(q[0], q[1], q[2], q[3]).normalized;

    private Renderer? Render(Renderer source) => source && renderers.TryGetValue(source, out var result) ? result : null;
    private void Active(Renderer source, bool active)
    {
        var renderer = Render(source);
        if (renderer) { renderer!.gameObject.SetActive(active); renderer.enabled = active; }
    }
    private void Texture(Renderer source, UnityEngine.Texture texture)
    {
        var renderer = Render(source);
        if (renderer && renderer!.sharedMaterial && texture) renderer.sharedMaterial.SetTexture("_MainTex", texture);
    }
    private void Material(Renderer source, Material material)
    {
        var renderer = Render(source);
        if (renderer && material) renderer!.sharedMaterial = CloneMaterial(material);
    }

    private void ApplyAppearance(Appearance next)
    {
        if (next.Equals(appearance)) return;
        var options = Zorro.Core.Singleton<Customization>.Instance;
        if (!options) throw new InvalidOperationException("Game appearance catalogue is not loaded.");
        var r = customization.refs;
        // Reject stale/unknown indices; never silently dress everyone as the current local player.
        if (next.Skin >= options.skins.Length || next.Eyes >= options.eyes.Length || next.Mouth >= options.mouths.Length ||
            next.Accessory >= options.accessories.Length || next.Outfit >= options.fits.Length || next.Hat >= r.playerHats.Length ||
            next.Sash >= r.sashAscentMaterials.Length) throw new InvalidOperationException("Recorded cosmetics do not match this game version.");
        foreach (var original in r.AllRenderers) { var renderer = Render(original); if (renderer) renderer!.enabled = true; }
        var fit = options.fits[next.Outfit];
        var body = Render(r.mainRenderer) as SkinnedMeshRenderer;
        if (!body || !fit.fitMesh) throw new InvalidOperationException("Missing recorded outfit mesh.");
        body.sharedMesh = fit.fitMesh;
        body.sharedMaterials = new[] { body.sharedMaterials[0], CloneMaterial(fit.fitMaterial), CloneMaterial(fit.fitMaterialShoes) };
        Active(r.skirt, !fit.noPants && fit.isSkirt);
        Active(r.shorts, !fit.noPants && !fit.isSkirt);
        Material(r.skirt, fit.fitPantsMaterial); Material(r.shorts, fit.fitPantsMaterial);
        int hat = fit.overrideHat ? fit.overrideHatIndex : next.Hat;
        for (int i = 0; i < r.playerHats.Length; i++)
        {
            Active(r.playerHats[i], i == hat);
            if (i < 2) Material(r.playerHats[i], fit.fitHatMaterial);
        }
        foreach (var original in r.EyeRenderers)
            Texture(original, next.EyeState == 2 ? customization.deadEyes : next.EyeState == 1 ? customization.passedOutEyes : options.eyes[next.Eyes].texture);
        Texture(r.mouthRenderer, options.mouths[next.Mouth].texture);
        var accessory = options.accessories[next.Accessory];
        Texture(r.accessoryRenderer, accessory.texture);
        var accessoryRenderer = Render(r.accessoryRenderer);
        if (accessoryRenderer && accessoryRenderer!.sharedMaterial) accessoryRenderer.sharedMaterial.renderQueue = accessory.drawUnderEye ? 3007 : 3009;
        Active(r.accessoryRenderer, !accessory.isThirdEye);
        if (r.thirdEye && transforms.TryGetValue(r.thirdEye.transform, out var thirdEye)) thirdEye.gameObject.SetActive(accessory.isThirdEye);
        var sash = Render(r.sashRenderer);
        if (sash) sash!.sharedMaterials = new[] { sash.sharedMaterials[0], CloneMaterial(r.sashAscentMaterials[next.Sash]) };
        Active(r.medalRenderer, next.Medal == 1);
        foreach (var original in r.PlayerRenderers.Concat(r.EyeRenderers))
        {
            var renderer = Render(original);
            if (!renderer) continue;
            foreach (var mat in renderer!.sharedMaterials)
                if (mat && mat.HasProperty("_SkinColor")) mat.SetColor("_SkinColor", options.skins[next.Skin].color);
        }
        bool transformed = next.Skeleton || next.Mushroom;
        Active(r.skeletonRenderer, transformed);
        if (transformed)
        {
            foreach (var original in r.AllRenderers) { var renderer = Render(original); if (renderer) renderer!.enabled = false; }
            var skeleton = Render(r.skeletonRenderer) as SkinnedMeshRenderer;
            if (!skeleton) throw new InvalidOperationException("Missing transformed character renderer.");
            skeleton.gameObject.SetActive(true); skeleton.enabled = true;
            skeleton.sharedMesh = next.Mushroom ? r.mushroomManMesh : r.skeletonThirdPerson;
            skeleton.sharedMaterial = CloneMaterial(next.Mushroom ? r.mushroomManMaterial : r.skeletonThirdPersonMat);
            Active(r.sashRenderer, !next.Mushroom);
            Active(r.medalRenderer, !next.Mushroom && next.Medal == 1);
            Active(r.accessoryRenderer, false);
            if (r.thirdEye && transforms.TryGetValue(r.thirdEye.transform, out var eye)) eye.gameObject.SetActive(false);
        }
        appearance = next;
    }

    public void Dispose()
    {
        if (root) { root.SetActive(false); Object.Destroy(root); }
        foreach (var material in materials.Values) if (material) Object.Destroy(material);
        foreach (var material in webMaterials) if (material) Object.Destroy(material);
        webMaterials.Clear(); webWraps.Clear();
        materials.Clear();
    }
}
