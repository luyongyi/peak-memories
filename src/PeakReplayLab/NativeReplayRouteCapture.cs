using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace PeakReplayLab;

// Unity reads happen once after the existing island-ready gate, on the capture
// thread. Export workers only see these immutable plain values.
internal static class NativeReplayRouteCapture
{
    private static Guid lastRunId;
    private static string lastRunKey = "";
    public static string SharedRunKey(RunManager? run)
    {
        if (!run || run!.RunId == Guid.Empty) return "";
        if (run.RunId != lastRunId)
        {
            lastRunId = run.RunId;
            lastRunKey = ReplayRouteRules.Hash("peak-memories/run/v1/" + lastRunId.ToString("D"));
        }
        return lastRunKey;
    }

    public static ReplayRouteContext Context(MapHandler map, ReplayHeader header)
    {
        var result = new ReplayRouteContext
        {
            RecordingId = ReplayRouteRules.Hash("peak-memories/recording/" + Guid.NewGuid().ToString("N")),
        };
        try
        {
            var run = RunManager.Instance;
            if (run && run.RunId != Guid.Empty)
                result.RunKey = SharedRunKey(run);
        }
        catch { /* Unknown shared run identity remains recording-scoped. */ }
        try { result.LevelIndex = GameHandler.GetService<NextLevelService>()?.NextLevelIndexOrFallback; }
        catch { }
        if (result.LevelIndex < 0) result.LevelIndex = null;
        try
        {
            result.Ascent = Ascents.currentAscent;
            result.Custom = RunSettings.IsCustomRun;
            result.Mini = RunSettings.isMiniRun;
        }
        catch { result.Ascent = null; result.Custom = result.Mini = null; }

        var stages = new List<ReplayRouteStage>();
        var progress = MountainProgressHandler.Instance;
        var available = progress ? progress.progressPoints : null;
        var used = new HashSet<MountainProgressHandler.ProgressPoint>();
        var entries = new List<MountainProgressHandler.ProgressPoint?>();
        int normalCount = Math.Min(5, map.segments?.Length ?? 0);
        for (int i = 0; i < normalCount; i++)
        {
            // Repeated final biomes still have separate native progress points
            // (Caldera/Kiln or Gloom/Citadel), in original progress-point order.
            var biome = map.segments![i].biome;
            var point = available?.FirstOrDefault(p => p != null && p.transform && p.biome == biome && !used.Contains(p));
            if (point != null) used.Add(point);
            entries.Add(point);
            string name = biome.ToString();
            // Use the actual resolved native progress title to distinguish the
            // repeated final biome. Never infer an ending from index alone.
            if (i == 4 && point?.title == "THE KILN") name = "Kiln";
            else if (i == 4 && point?.title == "THE CITADEL") name = "Temple";
            stages.Add(new ReplayRouteStage { Index = i, Name = name });
        }
        var peak = available?.FirstOrDefault(p => p != null && p.transform && p.biome == Biome.BiomeType.Peak);
        for (int i = 0; i < normalCount; i++)
        {
            var enter = entries[i]; var exit = i + 1 < normalCount ? entries[i + 1] : peak;
            if (enter?.transform && exit?.transform && enter.transform.position.z < exit.transform.position.z)
            {
                stages[i].EnterZCm = ReplayRouteRules.Centimeters(enter.transform.position.z);
                stages[i].ExitZCm = ReplayRouteRules.Centimeters(exit.transform.position.z);
            }
        }
        // Nadir is a separate realm, not another monotonically increasing Z
        // interval. It has no fabricated mountain gates; native per-player Win
        // evidence is captured separately.
        if (map.segments != null && map.segments.Skip(normalCount).Any(s => s.biome == Biome.BiomeType.Void))
            stages.Add(new ReplayRouteStage { Index = stages.Count, Name = "Void" });
        result.Stages = stages.ToArray();
        // Capture the actual island-ready world frame once. The recording and
        // upload keep these coordinates unchanged; the website may only align
        // them against matching source landmarks, never a player's first point.
        var landmarks = new List<ReplayMapLandmark>();
        for (int i = 0; i < result.Stages.Length && i < (map.segments?.Length ?? 0); i++)
        {
            var parent = map.segments![i].segmentParent;
            if (parent) landmarks.Add(Landmark(parent.transform, "segment-root", i));
        }
        for (int i = 0; i < entries.Count; i++)
        {
            var point = entries[i];
            if (point?.transform) landmarks.Add(Landmark(point.transform, "progress-point", i));
        }
        if (peak?.transform) landmarks.Add(Landmark(peak.transform, "progress-point", null));
        if (landmarks.Count != 0) result.Alignment = new ReplayMapAlignment { Landmarks = landmarks.ToArray() };
        if (result.Stages.Length > 0)
        {
            var roots = map.segments!.Select((s, i) => s.segmentParent
                ? i + ":" + WorldTrack.Path(s.segmentParent.transform) + ":" +
                  string.Join(",", new[] { s.segmentParent.transform.position.x, s.segmentParent.transform.position.y, s.segmentParent.transform.position.z }
                      .Select(p => ReplayRouteRules.Centimeters(p).ToString(CultureInfo.InvariantCulture)))
                : i + ":unknown");
            string gates = string.Join(";", stages.Select(s => s.Index + ":" + s.Name + ":" + s.EnterZCm + ":" + s.ExitZCm));
            result.LayoutKey = ReplayRouteRules.Hash("peak-memories/layout/v1/" + header.BuildId + "/" + header.GameAssembly + "/" +
                header.Scene + "/" + header.Route + "/" + string.Join(";", roots) + "/" + gates);
        }
        ReplayRouteRules.Validate(result);
        return result;
    }

    private static ReplayMapLandmark Landmark(Transform transform, string kind, int? stage)
    {
        var p = transform.position;
        var value = new ReplayMapLandmark
        {
            Key = kind + ":" + (stage.HasValue ? stage.Value.ToString(CultureInfo.InvariantCulture) : "peak"),
            Kind = kind, StageIndex = stage, Name = transform.name,
            PositionCm = new[] { ReplayRouteRules.Centimeters(p.x), ReplayRouteRules.Centimeters(p.y), ReplayRouteRules.Centimeters(p.z) },
        };
        if (kind == "segment-root")
        {
            var rotation = transform.rotation;
            var scale = transform.lossyScale;
            value.Rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w };
            value.Scale = new[] { scale.x, scale.y, scale.z };
        }
        return value;
    }
}
