using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakReplayLab;

internal static class WorldTrack
{
    private static MapHandler? cachedMap;
    private static int cachedScene = -1;
    private static Transform[]? capturedNodes;
    private static WorldFrame? capturedWorld;
    private static double nextCapture;

    public static void Reset()
    {
        cachedMap = null; cachedScene = -1; capturedNodes = null; capturedWorld = null; nextCapture = 0;
        EnvironmentReplayCapture.Reset();
    }

    private static MapHandler Map()
    {
        int scene = SceneManager.GetActiveScene().handle;
        if (cachedScene != scene) Reset();
        if (!cachedMap)
        {
            cachedMap = UnityEngine.Object.FindFirstObjectByType<MapHandler>();
            cachedScene = scene;
        }
        return cachedMap ? cachedMap! : throw new InvalidOperationException("Map is not initialized.");
    }

    public static Transform[] MapObjects()
    {
        var map = Map();
        var nodes = new List<Transform>();
        void Add(GameObject go) { if (go && !nodes.Contains(go.transform)) nodes.Add(go.transform); }
        foreach (var segment in map.segments.Concat(map.variantSegments ?? Array.Empty<MapHandler.MapSegment>()))
        {
            Add(segment.segmentParent); Add(segment.segmentCampfire); Add(segment.wallNext); Add(segment.wallPrevious);
        }
        if (nodes.Count > 128) throw new InvalidOperationException("Map layout exceeds supported node count.");
        return nodes.ToArray();
    }

    // Names plus child indices prevent ambiguity. No filesystem paths are ever used here.
    public static string Path(Transform t)
    {
        var parts = new Stack<string>();
        while (t.parent) { parts.Push(t.GetSiblingIndex() + ":" + Uri.EscapeDataString(t.name)); t = t.parent; }
        parts.Push(Uri.EscapeDataString(t.name));
        return string.Join("/", parts);
    }

    public static WorldFrame Capture(Transform[] nodes)
    {
        var map = Map();
        int segment = (int)map.GetCurrentSegment();
        double now = Time.timeAsDouble;
        // Discrete wind/fog/lava edges are checked at recorder cadence. Numeric
        // environment observations use 20 Hz and their own replay time index.
        var environment = EnvironmentReplayCapture.Sample(force: capturedWorld != null && capturedWorld.Segment != segment);
        if (ReferenceEquals(nodes, capturedNodes) && capturedWorld != null &&
            capturedWorld.Segment == segment && now < nextCapture && now >= nextCapture - .1)
        {
            if (ReferenceEquals(capturedWorld.Environment, environment)) return capturedWorld;
            return capturedWorld = new WorldFrame { Segment = capturedWorld.Segment, ActiveMapObjects = capturedWorld.ActiveMapObjects,
                TimeOfDay = capturedWorld.TimeOfDay, Day = capturedWorld.Day, Environment = environment };
        }
        var light = DayNightManager.instance;
        var active = capturedWorld != null && capturedWorld.ActiveMapObjects.Length == nodes.Length ? capturedWorld.ActiveMapObjects : null;
        bool copied = active == null;
        if (copied) active = new bool[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            bool value = nodes[i] && nodes[i].gameObject.activeSelf;
            if (active![i] == value) continue;
            if (!copied) { active = (bool[])active.Clone(); copied = true; }
            active[i] = value;
        }
        capturedNodes = nodes;
        nextCapture = now + .1;
        float timeOfDay = light ? light.timeOfDay : 9;
        int day = light ? light.dayCount : 1;
        if (capturedWorld != null && capturedWorld.Segment == segment && ReferenceEquals(capturedWorld.ActiveMapObjects, active) &&
            capturedWorld.TimeOfDay == timeOfDay && capturedWorld.Day == day && ReferenceEquals(capturedWorld.Environment, environment)) return capturedWorld;
        return capturedWorld = new WorldFrame
        {
            Segment = segment, ActiveMapObjects = active!,
            TimeOfDay = timeOfDay, Day = day,
            Environment = environment,
        };
    }

    public static WorldReplayPlayer Player(ReplayHeader header)
    {
        // A file may only address the game's known map switches, never arbitrary scene
        // objects such as UI, network services or another mod's GameObject.
        var allowed = MapObjects().ToDictionary(Path, StringComparer.Ordinal);
        if (header.MapObjects.Length != allowed.Count || header.MapObjects.Any(p => !allowed.ContainsKey(p)))
            throw new InvalidOperationException("Recorded map hierarchy no longer matches this scene.");
        Transform[] nodes = header.MapObjects.Select(p => allowed[p]).ToArray();
        var map = Map();
        var segmentField = HarmonyLib.AccessTools.Field(typeof(MapHandler), "currentSegment")
            ?? throw new MissingFieldException("MapHandler.currentSegment");
        // Match the two presentation switches in MapHandler.JumpToSegmentLogic.
        // The ancestor comes only from the already validated native map, never
        // a path supplied by the replay. Do not invoke Activate/Deactivate or
        // JumpToSegment: those subscribe gameplay events, spawn items and warp scouts.
        var mountain = map.segments.Length > 3 && map.segments[3].segmentParent
            ? map.segments[3].segmentParent.transform.parent : null;
        var voidBiome = Peak.VoidBiome.instance;
        if (mountain && mountain.gameObject.scene.handle != map.gameObject.scene.handle)
            throw new InvalidOperationException("Mountain presentation root is outside the replay scene.");
        if (voidBiome && voidBiome.gameObject.scene.handle != map.gameObject.scene.handle)
            throw new InvalidOperationException("Nadir presentation root is outside the replay scene.");
        var voidActiveSetter = HarmonyLib.AccessTools.PropertySetter(typeof(Peak.VoidBiome), nameof(Peak.VoidBiome.isActive))
            ?? throw new MissingMemberException("VoidBiome.isActive");
        return new WorldReplayPlayer(map, nodes, segmentField, mountain, voidBiome, voidActiveSetter);
    }
}

// Only the validated native map nodes are ever addressed. A seek first resolves
// its scene page, then applies at most one expensive native world switch per
// rendered frame while the replay clock and 3D scene remain covered. Ordinary
// playback and same-segment seeks retain the original synchronous path.
internal sealed class WorldReplayPlayer
{
    private readonly MapHandler map;
    private readonly Transform[] nodes;
    private readonly System.Reflection.FieldInfo segmentField;
    private readonly Transform? mountain;
    private readonly Peak.VoidBiome? voidBiome;
    private readonly System.Reflection.MethodInfo voidActiveSetter;
    private readonly bool initialMountainActive, initialVoidActive;
    private readonly WorldSeekPreparationBatch batch = new();
    private WorldFrame? previousWorld, preparedWorld;
    private int previousSegment = -1, previousDay = -1, preparedSequence = -1;
    private float previousTime = float.NaN;
    private bool dirty, preparing;

    public WorldReplayPlayer(MapHandler map, Transform[] nodes, System.Reflection.FieldInfo segmentField,
        Transform? mountain, Peak.VoidBiome? voidBiome, System.Reflection.MethodInfo voidActiveSetter)
    {
        this.map = map; this.nodes = nodes; this.segmentField = segmentField;
        this.mountain = mountain; this.voidBiome = voidBiome; this.voidActiveSetter = voidActiveSetter;
        initialMountainActive = mountain && mountain.gameObject.activeSelf;
        initialVoidActive = voidBiome && voidBiome.isActive;
    }

    private int Validate(WorldFrame frame)
    {
        int mapSegment = WorldSegmentIndex.Resolve(frame.Segment, map.segments.Length);
        if (frame.ActiveMapObjects.Length != nodes.Length)
            throw new InvalidOperationException("Recorded map state no longer matches this scene.");
        if (frame.Segment == (int)Segment.Void && (!mountain || !voidBiome || map.segments[mapSegment].biome != Biome.BiomeType.Void))
            throw new InvalidOperationException("Nadir presentation is not present in this scene.");
        return mapSegment;
    }

    private bool DesiredMountain(int segment) => segment == (int)Segment.Void ? false : initialMountainActive;
    private bool DesiredVoid(int segment) => segment == (int)Segment.Void || initialVoidActive;

    public bool NeedsSceneShield(WorldFrame frame)
    {
        Validate(frame);
        if (!dirty && frame.Segment == previousSegment) return false;
        if (mountain && mountain.gameObject.activeSelf != DesiredMountain(frame.Segment)) return true;
        if (voidBiome && voidBiome.isActive != DesiredVoid(frame.Segment)) return true;
        for (int i = 0; i < nodes.Length; i++)
            if (nodes[i] && nodes[i].gameObject.activeSelf != frame.ActiveMapObjects[i]) return true;
        return false;
    }

    public void Cancel()
    {
        // A superseding cursor must rebuild from actual native activeSelf values,
        // never resume a plan which may have switched only part of the map.
        if (preparing)
        {
            dirty = true;
            previousWorld = null;
            previousSegment = -1;
            previousTime = float.NaN;
            previousDay = -1;
        }
        preparing = false; preparedWorld = null; preparedSequence = -1;
        batch.Clear();
    }

    public bool Prepare(WorldFrame frame, int seekSequence, int renderedFrame)
    {
        // Same-segment cursor changes keep their immediate state semantics and
        // do not obscure the image. A partially switched prior seek is dirty.
        if (!dirty && !preparing && frame.Segment == previousSegment) return true;
        if (!preparing || preparedSequence != seekSequence || !SameTarget(preparedWorld, frame))
        {
            Cancel();
            BuildPlan(frame, seekSequence);
        }
        if (!preparing) return true;
        if (!batch.Advance(renderedFrame)) return false;
        Commit(frame);
        preparing = false; batch.Clear();
        // Even a synchronous last switch gets its own rendered frame before
        // actors, audio and effects for the destination are admitted.
        return false;
    }

    private static bool SameTarget(WorldFrame? left, WorldFrame right)
    {
        if (ReferenceEquals(left, right)) return true;
        return left != null && left.Segment == right.Segment && left.TimeOfDay == right.TimeOfDay && left.Day == right.Day &&
            left.ActiveMapObjects.SequenceEqual(right.ActiveMapObjects);
    }

    private void BuildPlan(WorldFrame frame, int sequence)
    {
        int mapSegment = Validate(frame);
        bool segmentChanged = frame.Segment != previousSegment || dirty;
        preparedWorld = frame; preparedSequence = sequence; preparing = true;
        if (segmentChanged) batch.Add(() => segmentField.SetValue(map, mapSegment), false);
        bool voidActive = DesiredVoid(frame.Segment);
        if (voidBiome && voidBiome.isActive != voidActive)
            batch.Add(() => { if (voidBiome && voidBiome.isActive != voidActive) voidActiveSetter.Invoke(voidBiome, new object[] { voidActive }); }, true);
        bool mountainActive = DesiredMountain(frame.Segment);
        if (mountain && mountain.gameObject.activeSelf != mountainActive)
            batch.Add(() => { if (mountain && mountain.gameObject.activeSelf != mountainActive) mountain.gameObject.SetActive(mountainActive); }, true);
        for (int i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i]; bool active = frame.ActiveMapObjects[i];
            if (node && node.gameObject.activeSelf != active)
                batch.Add(() => { if (node && node.gameObject.activeSelf != active) node.gameObject.SetActive(active); }, true);
        }
        var light = DayNightManager.instance;
        if (light && (segmentChanged || frame.TimeOfDay != previousTime || frame.Day != previousDay))
        {
            var profile = segmentChanged ? map.segments[mapSegment].dayNightProfile : null;
            batch.Add(() =>
            {
                if (!light) return;
                light.timeOfDay = frame.TimeOfDay; light.dayCount = frame.Day;
                if (!profile) return;
                light.StopAllCoroutines(); light.clearAllShaderParams();
                light.currentProfile = profile; light.newProfile = profile;
            }, true);
            if (profile)
            {
                foreach (var parameter in profile.globalShaderFloats ?? Array.Empty<ShaderParameters>())
                { var p = parameter; batch.Add(() => { if (light) Shader.SetGlobalFloat(light.getShaderValue(p.parameter), p.paramValue); }, false); }
                foreach (var parameter in profile.globalStaticShaderFloats ?? Array.Empty<ShaderParameters>())
                { var p = parameter; batch.Add(() => { if (light) Shader.SetGlobalFloat(light.getShaderValue(p.parameter), p.paramValue); }, false); }
                foreach (var parameter in profile.globalStaticShaderColors ?? Array.Empty<ShaderParametersColors>())
                { var p = parameter; batch.Add(() => { if (light) Shader.SetGlobalColor(light.getShaderValue(p.parameter), p.paramValue); }, false); }
            }
            batch.Add(() => { if (light) light.UpdateCycle(); }, true);
        }
        if (batch.Count == 0)
        { Commit(frame); preparing = false; }
    }

    private void Commit(WorldFrame frame)
    {
        previousWorld = frame; previousSegment = frame.Segment;
        previousTime = frame.TimeOfDay; previousDay = frame.Day;
        dirty = false;
    }

    public void Apply(WorldFrame frame)
    {
        if (ReferenceEquals(previousWorld, frame) && !dirty) return;
        if (preparing) Cancel();
        int mapSegment = Validate(frame);
        bool segmentChanged = frame.Segment != previousSegment || dirty;
        if (segmentChanged) segmentField.SetValue(map, mapSegment);
        bool voidActive = DesiredVoid(frame.Segment);
        if (voidBiome && voidBiome.isActive != voidActive) voidActiveSetter.Invoke(voidBiome, new object[] { voidActive });
        bool mountainActive = DesiredMountain(frame.Segment);
        if (mountain && mountain.gameObject.activeSelf != mountainActive) mountain.gameObject.SetActive(mountainActive);
        for (int i = 0; i < nodes.Length; i++)
            if (nodes[i] && nodes[i].gameObject.activeSelf != frame.ActiveMapObjects[i]) nodes[i].gameObject.SetActive(frame.ActiveMapObjects[i]);
        var light = DayNightManager.instance;
        if (light && (segmentChanged || frame.TimeOfDay != previousTime || frame.Day != previousDay))
        {
            light.timeOfDay = frame.TimeOfDay; light.dayCount = frame.Day;
            if (segmentChanged)
            {
                var profile = map.segments[mapSegment].dayNightProfile;
                if (profile)
                {
                    light.StopAllCoroutines(); light.clearAllShaderParams();
                    light.currentProfile = profile; light.newProfile = profile;
                    foreach (var p in profile.globalShaderFloats ?? Array.Empty<ShaderParameters>()) Shader.SetGlobalFloat(light.getShaderValue(p.parameter), p.paramValue);
                    foreach (var p in profile.globalStaticShaderFloats ?? Array.Empty<ShaderParameters>()) Shader.SetGlobalFloat(light.getShaderValue(p.parameter), p.paramValue);
                    foreach (var p in profile.globalStaticShaderColors ?? Array.Empty<ShaderParametersColors>()) Shader.SetGlobalColor(light.getShaderValue(p.parameter), p.paramValue);
                }
            }
            light.UpdateCycle();
        }
        Commit(frame);
    }
}
