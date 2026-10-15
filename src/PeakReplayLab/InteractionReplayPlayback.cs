using System;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace PeakReplayLab;

internal sealed class InteractionReplayPlayback : IDisposable
{
    private sealed class Entry
    {
        public string Kind = "", Resource = "", Path = "";
        public GameObject Root = null!;
        public InteractionReplayResources.Source Source = null!;
        public VisualReplica? Visual;
        public NativeVisualAppearance.Playback? Appearance;
        public NativeLightAppearance.Playback? Lights;
        public LineRenderer? Line;
        public TextMeshPro? Text;
        public InteractionTextFrame? AppliedText;
        public Volume[]? Posts;
        public bool SharedPosts;
        public bool Ready;
        public bool Presented, RestartAppearance = true, RestartLights = true;
        private bool appearancePending, lightsPending;
        public bool AppearanceNeedsRefresh, LightsNeedRefresh;
        private NativeRendererFrame[]? appearanceTarget;
        private NativeLightFrame[]? lightsTarget;
        public bool ContinueBuild(PresentationWorkBudget budget)
        {
            if (Ready) return true;
            if (Visual == null) return Ready = true;
            if (!Visual.ContinueBuild(budget)) return false;
            if (Appearance == null) { if (!budget.Build()) return false; Appearance = new NativeVisualAppearance.Playback(Visual, Source.Root, true); }
            if (!Appearance.ContinueBuild(budget)) return false;
            if (Lights == null) { if (!budget.Build()) return false; Lights = new NativeLightAppearance.Playback(Visual, Source.Root, true); }
            return Ready = Lights.ContinueBuild(budget);
        }
        public void Hide()
        {
            Presented = false; RestartAppearance = RestartLights = true;
            if (Visual != null) { Visual.Hide(); Visual.InvalidatePose(); } else if (Root) Root.SetActive(false);
            if (SharedPosts && Posts != null) foreach (var post in Posts) if (post) post.enabled = false;
        }
        public void Defer() { if (!Presented) { if (Visual != null) Visual.Hide(); else if (Root) Root.SetActive(false); } }
        public bool ApplyAppearance(NativeRendererFrame[]? frames, PresentationWorkBudget budget)
        {
            if (!appearancePending || RestartAppearance) appearanceTarget = frames;
            appearancePending = !Appearance!.TryApply(frames, budget, restart: RestartAppearance);
            RestartAppearance = false;
            AppearanceNeedsRefresh = !appearancePending && !ReferenceEquals(appearanceTarget, frames);
            return !appearancePending;
        }
        public bool ApplyLights(NativeLightFrame[]? frames, PresentationWorkBudget budget)
        {
            if (!lightsPending || RestartLights) lightsTarget = frames;
            lightsPending = !Lights!.TryApply(frames, budget, restart: RestartLights);
            RestartLights = false;
            LightsNeedRefresh = !lightsPending && !ReferenceEquals(lightsTarget, frames);
            return !lightsPending;
        }
        public void Dispose()
        { Appearance?.Dispose(); Lights?.Dispose(); if (Visual != null) Visual.Retire(); else if (Root) Object.Destroy(Root); }
    }
    private readonly InteractionReplayResources resources = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InteractionReplayFrame> next = new(StringComparer.Ordinal);
    private readonly Dictionary<Renderer, bool> hidden = new();
    private readonly NativeLightAppearance.Originals originalLights = new();
    private readonly HashSet<string> wanted = new(StringComparer.Ordinal), warned = new(StringComparer.Ordinal);
    // A pair has the same ordinal string identity as the former newline-joined
    // key without allocating a new string for every visible world frame.
    private readonly HashSet<(string Kind, string Address)> claimed = new();
    private readonly Dictionary<int, InteractionReplayResources.Source> deferredSources = new();
    private readonly Dictionary<int, InteractionReplayResources.Source> postSources = new();
    private readonly HashSet<(string Kind, string Address)> postClaims = new();
    private readonly Dictionary<Volume, (bool Enabled, float Weight)> originalPosts = new();
    private readonly Dictionary<Volume, (bool Enabled, float Weight)> appliedPosts = new();
    private readonly Dictionary<string, double> retry = new(StringComparer.Ordinal);
    private readonly List<string> remove = new();
    private readonly InteractionReplayObserver observer;
    private readonly PresentationWorkBudget budget;
    private readonly FakeItemReplayPlayback fakeItems;
    private readonly Action<string>? warning;
    private InteractionReplayFrame[] lastLeft = Array.Empty<InteractionReplayFrame>(), lastRight = Array.Empty<InteractionReplayFrame>();
    private float lastMix, originalGloom;
    private double lastTime;
    private int appliedFrame = -1;
    private bool disposed, gloomOwned, ownsNative;
    public int VisibleCount { get; private set; }
    public int MissingCount { get; private set; }
    public bool Pending { get; private set; }
    public InteractionReplayPlayback(Action<string>? warning = null, PresentationWorkBudget? workBudget = null)
    {
        this.warning = warning; budget = workBudget ?? new(); resources.LoadTemplates();
        fakeItems = new FakeItemReplayPlayback(frame => resources.Resolve(frame)?.Root, budget, warning);
        observer = new InteractionReplayObserver(Changed, Spawned, warning);
        try { foreach (var source in resources.Initial()) Hide(source); }
        catch { Dispose(); throw; }
    }
    private void Changed(Component owner)
    {
        if (disposed || !owner) return;
        if (owner is Breakable breakable) { resources.AddBreakable(breakable); return; }
        if (owner is Item item)
        {
            foreach (var clone in item.GetComponentsInChildren<Peak.Action_CloneSelectedItem>(true)) Changed(clone);
            foreach (var bing in item.GetComponentsInChildren<Action_AskBingBong>(true)) Changed(bing);
            foreach (var warp in item.GetComponentsInChildren<WarpCompassVFX>(true)) Changed(warp);
            return;
        }
        var source = resources.FromOwner(owner); if (source != null) Hide(source);
    }
    private void Spawned(Object original, Object instance)
    { var source = resources.Spawn(original, instance); if (source != null) Hide(source); }
    private void Hide(InteractionReplayResources.Source source)
    {
        if (!source.Root) return;
        // Fake stand-ins have no visual ownership in old recordings. Their
        // explicit schema27 states acquire a separate passive visibility lease.
        if (source.Kind == "fake-item") return;
        if (source.Posts != null)
        {
            if (postSources.Count < InteractionReplayRules.MaximumEntities) postSources[source.Root.GetInstanceID()] = source;
            if (postClaims.Contains(PostKey(source.Kind, source.Path, source.Resource))) MutePosts(source);
            return;
        }
        if (!ownsNative) { if (deferredSources.Count < InteractionReplayRules.MaximumEntities) deferredSources[source.Root.GetInstanceID()] = source; return; }
        // Legacy recordings have no interaction snapshots. Their scenery must
        // remain visible until a recorded world entity explicitly claims it.
        if (InteractionReplayRules.World(source.Kind) && !claimed.Contains((source.Kind, source.Path))) return;
        HideRoot(source.Root);
    }
    private void HideRoot(GameObject source)
    {
        foreach (var renderer in source.GetComponentsInChildren<Renderer>(true))
            if (renderer && !(renderer is ParticleSystemRenderer) && !hidden.ContainsKey(renderer))
            { hidden.Add(renderer, renderer.forceRenderingOff); renderer.forceRenderingOff = true; }
        originalLights.Hide(source);
    }
    private static (string Kind, string Address) PostKey(string kind, string path, string resource) =>
        (kind, InteractionReplayRules.World(kind) ? path : resource);
    private void MutePosts(InteractionReplayResources.Source source)
    {
        if (source.Posts == null) return;
        foreach (var volume in source.Posts)
            if (volume && volume.gameObject.scene.IsValid())
            { if (!originalPosts.ContainsKey(volume)) originalPosts.Add(volume, (volume.enabled, volume.weight)); volume.enabled = false; }
    }
    public void Apply(InteractionReplayFrame[] left, InteractionReplayFrame[] right, float mix, double time, bool discontinuity)
    {
        if (disposed) return;
        budget.BeginFrame(Time.frameCount); VisualReplica.DrainRetired(budget);
        lastLeft = left; lastRight = right; lastMix = mix; lastTime = time; appliedFrame = Time.frameCount;
        if (!ownsNative && (left.Length != 0 || right.Length != 0))
        { ownsNative = true; foreach (var source in deferredSources.Values) Hide(source); deferredSources.Clear(); }
        Pending = false; VisibleCount = MissingCount = 0; wanted.Clear(); next.Clear(); appliedPosts.Clear();
        fakeItems.Begin(left, discontinuity);
        foreach (var frame in right) next[frame.Key] = frame;
        foreach (var frame in left) wanted.Add(frame.Key);
        int sleeping = 0;
        remove.Clear(); foreach (var pair in entries) if (!wanted.Contains(pair.Key))
        {
            pair.Value.Hide();
            if (pair.Value.Ready && sleeping++ < 32) continue;
            pair.Value.Dispose(); remove.Add(pair.Key);
        }
        foreach (var key in remove) { entries.Remove(key); retry.Remove(key); }
        remove.Clear(); foreach (var key in retry.Keys) if (!wanted.Contains(key)) remove.Add(key);
        foreach (var key in remove) retry.Remove(key);
        foreach (var frame in left)
        {
            if (frame.Kind == "fake-item") { fakeItems.Apply(frame, discontinuity); continue; }
            if (InteractionReplayRules.World(frame.Kind) && !InteractionReplayRules.PostKind(frame.Kind) && claimed.Add((frame.Kind, frame.SourcePath)))
            { var native = resources.Resolve(frame); if (native != null) HideRoot(native.Root); }
            if (InteractionReplayRules.PostKind(frame.Kind))
            {
                var claim = PostKey(frame.Kind, frame.SourcePath, frame.Resource);
                if (frame.PostWeights == null)
                {
                    foreach (var source in postSources.Values)
                        if (PostKey(source.Kind, source.Path, source.Resource) == claim && source.Posts != null)
                            foreach (var post in source.Posts) if (post && originalPosts.TryGetValue(post, out var saved)) appliedPosts[post] = saved;
                    if (entries.TryGetValue(frame.Key, out var unknown)) unknown.Hide();
                    continue;
                }
                if (postClaims.Add(claim)) foreach (var source in postSources.Values)
                    if (PostKey(source.Kind, source.Path, source.Resource) == claim) MutePosts(source);
            }
            if (frame.GloomAmount.HasValue)
            {
                if (!gloomOwned) { originalGloom = Shader.GetGlobalFloat("GloomSafeZoneAmount"); gloomOwned = true; }
                float amount = frame.GloomAmount.Value;
                if (next.TryGetValue(frame.Key, out var laterGloom) && laterGloom.GloomAmount.HasValue) amount = Mathf.Lerp(amount, laterGloom.GloomAmount.Value, mix);
                Shader.SetGlobalFloat("GloomSafeZoneAmount", amount);
            }
            bool visible = frame.Pose.Active && (frame.Line == null || frame.Line.Visible) && (frame.Text == null || frame.Text.Visible);
            entries.TryGetValue(frame.Key, out var entry);
            if (!visible) { entry?.Hide(); continue; }
            if (retry.TryGetValue(frame.Key, out double retryAt) && Time.realtimeSinceStartupAsDouble < retryAt) { MissingCount++; continue; }
            try
            {
                if (entry != null && (entry.Kind != frame.Kind || entry.Resource != frame.Resource || entry.Path != frame.SourcePath))
                { entry.Dispose(); entries.Remove(frame.Key); entry = null; }
                if (entry == null)
                {
                    var source = resources.Resolve(frame) ?? throw new InvalidOperationException("Native interaction resource not available: " + frame.Kind + " / " + frame.Resource);
                    if (!VisualReplica.CanStartBuild || !budget.Create()) { Pending = true; MissingCount++; continue; }
                    entry = Create(source, frame); entries.Add(frame.Key, entry);
                }
                if (discontinuity) entry.Hide();
                if (!entry.ContinueBuild(budget)) { entry.Defer(); Pending = true; MissingCount++; continue; }
                next.TryGetValue(frame.Key, out var rightFrame);
                if (rightFrame == null || !InteractionReplayRules.CanInterpolate(frame, rightFrame)) rightFrame = frame;
                if (entry.Visual != null)
                {
                    if (!entry.ApplyAppearance(frame.Visuals, budget) || !entry.ApplyLights(frame.Lights, budget))
                    {
                        entry.Defer(); Pending = true; MissingCount++;
                        if (entry.Presented) VisibleCount++;
                        continue;
                    }
                    entry.Visual.Apply(frame.Pose, rightFrame.Pose, mix); entry.Appearance!.ReapplyVisibility();
                }
                else
                {
                    ApplyRoot(entry.Root.transform, frame.Pose, rightFrame.Pose, mix);
                    if (entry.Line && frame.Line != null) ApplyLine(entry, frame.Line, rightFrame.Line ?? frame.Line, mix);
                    if (entry.Text && frame.Text != null) ApplyText(entry, frame.Text);
                    if (entry.Posts != null && frame.PostWeights != null)
                        for (int i = 0; i < entry.Posts.Length; i++)
                        {
                            var post = entry.Posts[i];
                            float weight = rightFrame.PostWeights != null ? Mathf.LerpUnclamped(frame.PostWeights[i], rightFrame.PostWeights[i], mix) : frame.PostWeights[i];
                            bool enabled = frame.PostEnabled![i]; post.weight = weight; post.enabled = enabled;
                            if (entry.SharedPosts) appliedPosts[post] = (enabled, weight);
                        }
                    entry.Root.SetActive(true);
                }
                entry.Presented = true; Pending |= entry.AppearanceNeedsRefresh || entry.LightsNeedRefresh;
                retry.Remove(frame.Key); VisibleCount++;
            }
            catch (Exception e)
            { entry?.Dispose(); entries.Remove(frame.Key); retry[frame.Key] = Time.realtimeSinceStartupAsDouble + 1; MissingCount++; if (warned.Count < 64 && warned.Add(frame.Kind + frame.Resource)) warning?.Invoke("互动视觉缺失：" + e.Message); }
        }
        fakeItems.End(); Pending |= fakeItems.Pending; MissingCount += fakeItems.MissingCount; VisibleCount += fakeItems.VisibleCount;
    }
    private static Entry Create(InteractionReplayResources.Source source, InteractionReplayFrame frame)
    {
        var entry = new Entry { Kind = frame.Kind, Resource = frame.Resource, Path = frame.SourcePath, Source = source };
        try
        {
            if (source.Posts != null)
            {
                entry.Root = new GameObject("PEAK Replay native post-processing"); entry.Root.SetActive(false);
                if (source.Kind == "peak-portal") { entry.Posts = source.Posts; entry.SharedPosts = true; }
                else
                {
                    entry.Posts = new Volume[2];
                    for (int i = 0; i < 2; i++)
                    {
                        var original = source.Posts[i];
                        if (!original || !original.isGlobal) throw new InvalidOperationException("The audited compass requires its native global volumes.");
                        var child = new GameObject("Native warp volume " + i); child.layer = original.gameObject.layer; child.transform.SetParent(entry.Root.transform, false);
                        var volume = child.AddComponent<Volume>(); volume.enabled = false;
                        volume.isGlobal = original.isGlobal; volume.priority = original.priority; volume.blendDistance = original.blendDistance;
                        volume.sharedProfile = original.sharedProfile; volume.weight = original.weight; entry.Posts[i] = volume;
                    }
                }
            }
            else if (source.Line)
            {
                entry.Root = new GameObject("PEAK Replay clone ray"); entry.Root.SetActive(false);
                var line = entry.Root.AddComponent<LineRenderer>(); entry.Line = line;
                line.sharedMaterials = source.Line!.sharedMaterials; line.useWorldSpace = true; line.alignment = source.Line.alignment;
                line.textureMode = source.Line.textureMode; line.numCapVertices = source.Line.numCapVertices; line.numCornerVertices = source.Line.numCornerVertices;
                line.shadowCastingMode = source.Line.shadowCastingMode; line.receiveShadows = source.Line.receiveShadows;
            }
            else if (source.Text)
            {
                entry.Root = new GameObject("PEAK Replay native BingBong subtitle"); entry.Root.SetActive(false);
                var text = entry.Root.AddComponent<TextMeshPro>(); entry.Text = text;
                var original = source.Text!;
                text.font = original.font; text.fontSharedMaterial = original.fontSharedMaterial;
                text.alignment = original.alignment; text.fontStyle = original.fontStyle; text.richText = original.richText;
                text.textWrappingMode = original.textWrappingMode; text.overflowMode = original.overflowMode;
                text.characterSpacing = original.characterSpacing; text.lineSpacing = original.lineSpacing;
                text.rectTransform.sizeDelta = original.rectTransform.sizeDelta; text.rectTransform.pivot = original.rectTransform.pivot;
                text.renderer.sortingLayerID = original.renderer.sortingLayerID; text.renderer.sortingOrder = original.renderer.sortingOrder;
            }
            else { entry.Visual = VisualReplica.BeginBuild(source.Root, true, InteractionReplayRules.MaximumNodes); entry.Root = entry.Visual.Root; entry.Root.name = "PEAK Replay Interaction - " + frame.Kind; }
            return entry;
        }
        catch { entry.Dispose(); throw; }
    }
    private static void ApplyRoot(Transform target, ObjectPose left, ObjectPose right, float mix)
    { target.SetPositionAndRotation(Vector3.Lerp(V(left.Position), V(right.Position), mix), Quaternion.Slerp(Q(left.Rotation), Q(right.Rotation), mix)); target.localScale = Vector3.Lerp(V(left.Scale), V(right.Scale), mix); }
    private static void ApplyLine(Entry entry, InteractionLineFrame left, InteractionLineFrame right, float mix)
    {
        var line = entry.Line!; int count = left.Widths.Length;
        if (right.Widths.Length != count) right = left;
        line.positionCount = count; line.widthMultiplier = 1;
        // Clone amulet native lines have two endpoints. No per-frame arrays or
        // GetComponents calls; only the authored endpoint widths are animated.
        for (int i = 0; i < count; i++) line.SetPosition(i, Vector3.Lerp(new Vector3(left.Points[i * 3], left.Points[i * 3 + 1], left.Points[i * 3 + 2]), new Vector3(right.Points[i * 3], right.Points[i * 3 + 1], right.Points[i * 3 + 2]), mix));
        if (count > 0) { line.startWidth = Mathf.Lerp(left.Widths[0], right.Widths[0], mix); line.endWidth = Mathf.Lerp(left.Widths[count - 1], right.Widths[count - 1], mix); }
        line.startColor = Color.Lerp(C(left.StartColor), C(right.StartColor), mix); line.endColor = Color.Lerp(C(left.EndColor), C(right.EndColor), mix);
        line.enabled = left.Visible;
    }
    private static void ApplyText(Entry entry, InteractionTextFrame frame)
    {
        if (ReferenceEquals(entry.AppliedText, frame)) return;
        var text = entry.Text!;
        if (text.text != frame.Content) text.text = frame.Content;
        text.color = C(frame.Color); text.fontSize = frame.FontSize; text.enabled = frame.Visible;
        entry.AppliedText = frame;
    }
    private static Vector3 V(float[] value) => new(value[0], value[1], value[2]);
    private static Quaternion Q(float[] value) => new Quaternion(value[0], value[1], value[2], value[3]).normalized;
    private static Color C(float[] value) => new(value[0], value[1], value[2], value[3]);
    public void Enforce(bool completePending = true)
    {
        if (disposed) return;
        foreach (var pair in hidden) if (pair.Key && !pair.Key.forceRenderingOff) pair.Key.forceRenderingOff = true;
        originalLights.Enforce();
        foreach (var pair in originalPosts) if (pair.Key)
        { if (appliedPosts.TryGetValue(pair.Key, out var applied)) { pair.Key.weight = applied.Weight; pair.Key.enabled = applied.Enabled; } else pair.Key.enabled = false; }
        if (completePending && Pending && appliedFrame != Time.frameCount) Apply(lastLeft, lastRight, lastMix, lastTime, false);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; observer.Dispose(); fakeItems.Dispose();
        foreach (var pair in hidden) if (pair.Key) pair.Key.forceRenderingOff = pair.Value;
        originalLights.Dispose(); foreach (var entry in entries.Values) entry.Dispose(); entries.Clear(); hidden.Clear();
        deferredSources.Clear();
        foreach (var pair in originalPosts) if (pair.Key) { pair.Key.weight = pair.Value.Weight; pair.Key.enabled = pair.Value.Enabled; }
        originalPosts.Clear(); appliedPosts.Clear(); postSources.Clear(); postClaims.Clear();
        if (gloomOwned) Shader.SetGlobalFloat("GloomSafeZoneAmount", originalGloom);
    }
}
