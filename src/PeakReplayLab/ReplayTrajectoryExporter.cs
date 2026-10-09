using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace PeakReplayLab;

public sealed class TrajectoryExportResult
{
    public string Path { get; internal set; } = "";
    public string[] Paths { get; internal set; } = Array.Empty<string>();
    public string RecordingId { get; internal set; } = "";
    public long CompressedBytes { get; internal set; }
    public long DecodedBytes { get; internal set; }
    public long PointCount { get; internal set; }
    public int PlayerCount { get; internal set; }
    public long DurationMs { get; internal set; }
    public string[] PlayerNames { get; internal set; } = Array.Empty<string>();
    public string DifficultyLabel { get; internal set; } = "未知难度";
    public bool NativeEvidence { get; internal set; }
    public bool StageGatesKnown { get; internal set; }
    public int MapLandmarkCount { get; internal set; }
    public string Scene { get; internal set; } = "";
    public string[] Route { get; internal set; } = Array.Empty<string>();
}

// Separate upload whitelist: no ReplayFrame/ActorFrame, inventory, animation,
// audio, original player ID, game files, or JSON extension data is serialized.
public sealed class TrajectoryPackage
{
    public string Format { get; set; } = "trajectory-v1";
    public string RecordingId { get; set; } = "";
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string? RunKey { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public long? TimeOriginMs { get; set; }
    public string StartedUtc { get; set; } = "";
    public int DurationMs { get; set; }
    public int SampleHz { get; set; } = 10;
    public string CoordinateUnit { get; set; } = "cm";
    public TrajectoryMap Map { get; set; } = new();
    public TrajectoryDifficulty Difficulty { get; set; } = new();
    public List<TrajectoryPlayer> Players { get; set; } = new();
}

public sealed class TrajectoryMap
{
    public int BuildId { get; set; }
    public string Scene { get; set; } = "";
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? LevelIndex { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string? LayoutKey { get; set; }
    public string[] Route { get; set; } = Array.Empty<string>();
    public TrajectoryStage[] Stages { get; set; } = Array.Empty<TrajectoryStage>();
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public TrajectoryMapAlignment? Alignment { get; set; }
}

// Independent HTTP whitelist; replay metadata types and extension data never
// become the upload contract. All arrays are copied from recording-start proof.
public sealed class TrajectoryMapAlignment
{
    public int Version { get; set; } = 1;
    public string CoordinateSpace { get; set; } = "unity-world-cm";
    public List<TrajectoryMapLandmark> Landmarks { get; set; } = new();
}

public sealed class TrajectoryMapLandmark
{
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? StageIndex { get; set; }
    public string Name { get; set; } = "";
    public int[] PositionCm { get; set; } = new int[3];
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public float[]? Rotation { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public float[]? Scale { get; set; }
}

public sealed class TrajectoryStage
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? EnterZCm { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? ExitZCm { get; set; }
}

public sealed class TrajectoryDifficulty
{
    // Nulls are deliberately emitted: an old recording does not inherit today's
    // difficulty or become Ascent 0 by a serializer default.
    public int? Ascent { get; set; }
    public bool? Custom { get; set; }
    public bool? Mini { get; set; }
}

public sealed class TrajectoryPlayer
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Owner { get; set; }
    public string Evidence { get; set; } = "legacy-unknown";
    public List<int[]> Points { get; set; } = new();
    public List<TrajectoryEvent> Events { get; set; } = new();
}

public sealed class TrajectoryEvent
{
    public int TMs { get; set; }
    public string Kind { get; set; } = "";
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? StageIndex { get; set; }
}

public static class ReplayTrajectoryExporter
{
    public const long MaximumCompressedBytes = 12L * 1024 * 1024;
    public const long MaximumDecodedBytes = 64L * 1024 * 1024;
    private const long MaximumRetainedPoints = MaximumDecodedBytes / 72;
    private const int MaximumEvents = 100_000;
    private const int MaximumRetainedEvents = 200_000;
    private static readonly JsonSerializerSettings UploadJson = new()
    {
        TypeNameHandling = TypeNameHandling.None, MaxDepth = 12,
        ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
        Formatting = Formatting.None,
    };

    // Invoke on a disk worker (Task.Run), never in the live capture loop.
    // Pages are released after each read. A metadata pass establishes one shared
    // clock; bounded whole-member batches are reread as needed. No member is
    // removed to satisfy a package size or an arbitrary team-size limit.
    public static TrajectoryExportResult Export(string recordingPath, string outputDirectory,
        CancellationToken cancellation = default, Action<double>? progress = null)
        => ExportCore(recordingPath, outputDirectory, MaximumCompressedBytes, MaximumDecodedBytes, cancellation, progress);

    internal static TrajectoryExportResult ExportWithSmallerLimits(string recordingPath, string outputDirectory,
        long compressedLimit, long decodedLimit, CancellationToken cancellation = default, Action<double>? progress = null)
    {
        if (compressedLimit < 1 || compressedLimit > MaximumCompressedBytes || decodedLimit < 1 || decodedLimit > MaximumDecodedBytes)
            throw new ArgumentOutOfRangeException(nameof(compressedLimit));
        return ExportCore(recordingPath, outputDirectory, compressedLimit, decodedLimit, cancellation, progress);
    }

    private static TrajectoryExportResult ExportCore(string recordingPath, string outputDirectory,
        long compressedLimit, long decodedLimit, CancellationToken cancellation, Action<double>? progress)
    {
        if (string.IsNullOrWhiteSpace(recordingPath) || !recordingPath.EndsWith(".peakrun", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("上传轨迹仅支持已封存的完整 .peakrun 文件，暂不支持 120 秒片段或 .partial。");
        if (string.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("Missing trajectory output directory.", nameof(outputDirectory));
        cancellation.ThrowIfCancellationRequested();
        var info = FullReplayArchive.ReadInfo(recordingPath, cancellation);
        if (!info.Complete || !string.IsNullOrEmpty(info.FaultCode) || !string.IsNullOrEmpty(info.Fault))
            throw new InvalidDataException("这份录像未正常封存，暂时不能上传轨迹。是否通关不影响正常封存录像上传。");
        void Read(Builder builder, bool metadata)
        {
            double previous = -1;
            for (int i = 0; i < info.Pages.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var page = FullReplayArchive.ReadPage(recordingPath, info, i, cancellation);
                foreach (var frame in page.Frames)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (frame.T <= previous) continue;
                    builder.Observe(frame); previous = frame.T;
                }
                if (metadata) progress?.Invoke((i + 1d) / info.Pages.Length * .45);
            }
        }
        var overview = new Builder(info.Header, info.Duration, retainData: false);
        Read(overview, true);
        var package = overview.Finish();
        string directory = System.IO.Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        long rawBytes = 0, compressedBytes = 0;
        int exportedMembers = 0;
        void Batch(string[] ids)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var builder = new Builder(info.Header, info.Duration, selectedIds: new HashSet<string>(ids, StringComparer.Ordinal));
                Read(builder, false);
                var batch = builder.Finish(package);
                var written = WritePackage(batch, directory, compressedLimit, decodedLimit, cancellation);
                paths.Add(written.Path); rawBytes += written.Decoded; compressedBytes += written.Compressed;
                exportedMembers += ids.Length;
                progress?.Invoke(.45 + .55 * exportedMembers / overview.ActorIds.Length);
            }
            catch (PackageBudgetException) when (ids.Length > 1)
            {
                // Whole-member subdivision preserves every individual's actual
                // route; time slicing would require inventing cross-upload joins.
                int middle = ids.Length / 2;
                Batch(ids.Take(middle).ToArray()); Batch(ids.Skip(middle).ToArray());
            }
        }
        try
        {
            Batch(overview.ActorIds);
            progress?.Invoke(1);
            return new TrajectoryExportResult
            {
                Path = paths[0], Paths = paths.ToArray(), RecordingId = package.RecordingId,
                CompressedBytes = compressedBytes, DecodedBytes = rawBytes,
                PointCount = overview.PointCount, PlayerCount = package.Players.Count,
                PlayerNames = package.Players.Select(p => p.Name).ToArray(), DurationMs = package.DurationMs,
                DifficultyLabel = package.Difficulty.Custom == true ? "自定义难度" : package.Difficulty.Ascent.HasValue
                    ? "Ascent " + package.Difficulty.Ascent.Value : "未知难度",
                NativeEvidence = package.Players.All(p => p.Evidence == "native-state"),
                StageGatesKnown = package.Map.Stages.Any(s => s.Name != "Void") &&
                    package.Map.Stages.Where(s => s.Name != "Void").All(s => s.EnterZCm.HasValue && s.ExitZCm.HasValue),
                MapLandmarkCount = package.Map.Alignment?.Landmarks.Count ?? 0,
                Scene = package.Map.Scene, Route = (string[])package.Map.Route.Clone(),
            };
        }
        catch (PackageBudgetException error)
        {
            foreach (string path in paths) if (File.Exists(path)) File.Delete(path);
            throw new InvalidDataException("单名队员的轨迹超过每包处理预算，未丢弃任何队员；请保留录像和导出记录供排查。", error);
        }
        catch
        {
            // Remove only files made by this invocation. An incomplete batch is
            // never offered as though every team member was exported.
            foreach (string path in paths) if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    private static (string Path, long Decoded, long Compressed) WritePackage(TrajectoryPackage package,
        string directory, long compressedLimit, long decodedLimit, CancellationToken cancellation)
    {
        string target = System.IO.Path.Combine(directory, package.RecordingId + "-" + Guid.NewGuid().ToString("N") + ".trajectory.json.gz");
        string temporary = target + ".partial";
        bool created = false;
        try
        {
            long rawBytes, compressedBytes;
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan))
            {
                created = true;
                using var compressed = new LimitedWriteStream(file, compressedLimit, cancellation);
                using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, true))
                using (var decoded = new LimitedWriteStream(gzip, decodedLimit, cancellation))
                using (var text = new StreamWriter(decoded, new UTF8Encoding(false), 8192, true))
                using (var json = new JsonTextWriter(text) { CloseOutput = false })
                {
                    JsonSerializer.Create(UploadJson).Serialize(json, package);
                    json.Flush(); text.Flush(); rawBytes = decoded.BytesWritten;
                }
                compressed.Flush(); compressedBytes = compressed.BytesWritten; file.Flush();
            }
            cancellation.ThrowIfCancellationRequested(); File.Move(temporary, target);
            return (target, rawBytes, compressedBytes);
        }
        catch { if (created && File.Exists(temporary)) File.Delete(temporary); throw; }
    }

    private sealed class PackageBudgetException : IOException
    { public PackageBudgetException(string message) : base(message) { } }

    internal sealed class Builder
    {
        private sealed class Track
        {
            public readonly TrajectoryPlayer Value;
            public ActorRouteState? Native;
            public bool Present, EverMissingEvidence;
            public bool SummitRecorded;
            public bool? Dead;
            public double LastFrameTime = -1;
            public int LastPointMs = -100;
            public float[]? LastPosition;
            public int? GameStage;
            public int? LastFrameGameStage;
            public readonly HashSet<int> FinishedStages = new();
            public readonly HashSet<int> CheckpointStages = new();
            public Track(TrajectoryPlayer value) => Value = value;
        }
        private readonly Dictionary<string, Track> tracks = new(StringComparer.Ordinal);
        private readonly TrajectoryPackage package;
        private readonly bool retainData;
        private readonly HashSet<string>? selectedIds;
        private readonly string? headerRunKey;
        private string? lastRunKey;
        private long? runTimeOrigin;
        private long? headerTimeOrigin;
        private long points, events;
        public long PointCount => points;
        public string[] ActorIds => tracks.Keys.ToArray();

        public Builder(ReplayHeader header, double duration, bool retainData = true, HashSet<string>? selectedIds = null)
        {
            this.retainData = retainData; this.selectedIds = selectedIds;
            ReplayRouteRules.Validate(header.RouteContext);
            var context = header.RouteContext;
            string recordingId = context?.RecordingId ?? ReplayRouteRules.Hash("peak-memories/legacy/v1/" +
                header.Scene + "/" + header.BuildId + "/" + header.GameAssembly + "/" + header.Route + "/" + header.StartedUtc);
            headerRunKey = context?.RunKey;
            string[] route = header.Route.Split(',').Select(s => s.Trim()).Where(s => s.Length != 0).ToArray();
            var stages = context?.Stages.Select(s => new TrajectoryStage
            { Index = s.Index, Name = s.Name, EnterZCm = s.EnterZCm, ExitZCm = s.ExitZCm }).ToArray();
            if (stages != null && stages.Length != 0) route = stages.Select(s => s.Name).ToArray();
            if (stages == null || stages.Length == 0)
                stages = route.Select((name, index) => new TrajectoryStage { Index = index, Name = name }).ToArray();
            package = new TrajectoryPackage
            {
                RecordingId = recordingId, RunKey = context?.RunKey, TimeOriginMs = context?.TimeOriginMs,
                StartedUtc = header.StartedUtc, DurationMs = Milliseconds(duration),
                Map = new TrajectoryMap { BuildId = header.BuildId, Scene = header.Scene, LevelIndex = context?.LevelIndex,
                    LayoutKey = context?.LayoutKey, Route = route, Stages = stages,
                    Alignment = context?.Alignment == null ? null : new TrajectoryMapAlignment
                    {
                        Version = context.Alignment.Version, CoordinateSpace = context.Alignment.CoordinateSpace,
                        Landmarks = context.Alignment.Landmarks.Select(value => new TrajectoryMapLandmark
                        {
                            Key = value.Key, Kind = value.Kind, StageIndex = value.StageIndex, Name = value.Name,
                            PositionCm = (int[])value.PositionCm.Clone(),
                            Rotation = value.Rotation == null ? null : (float[])value.Rotation.Clone(),
                            Scale = value.Scale == null ? null : (float[])value.Scale.Clone(),
                        }).ToList(),
                    } },
                Difficulty = new TrajectoryDifficulty { Ascent = context?.Ascent, Custom = context?.Custom, Mini = context?.Mini },
            };
        }

        public void Observe(ReplayFrame frame)
        {
            int frameMs = Milliseconds(frame.T);
            int? gameStage = MappedGameStage(frame.World?.Segment);
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var actor in frame.Actors)
            {
                if (selectedIds != null && !selectedIds.Contains(actor.Id)) continue;
                present.Add(actor.Id);
                if (!tracks.TryGetValue(actor.Id, out var track))
                {
                    var player = new TrajectoryPlayer { Name = actor.Name };
                    tracks.Add(actor.Id, track = new Track(player)); package.Players.Add(player);
                }
                track.Value.Name = actor.Name;
                var native = actor.RouteState;
                bool rejoining = !track.Present;
                if (rejoining) { Event(track, frameMs, "join"); track.LastPosition = null; }
                track.Present = true;
                if (native == null) track.EverMissingEvidence = true;
                track.Value.Owner |= native?.LocalOwner == true;
                bool dead = native != null ? !native.Alive : actor.Appearance.EyeState == 2;
                bool terminalDeath = dead && track.Dead == false && !rejoining;
                bool lifeChanged = track.Dead.HasValue && track.Dead != dead;
                if (track.Dead != dead)
                {
                    if (dead) Event(track, frameMs, "dead");
                    else if (track.Dead == true) Event(track, frameMs, "revive");
                    if (track.Dead == true) Event(track, frameMs, "break");
                    track.Dead = dead;
                }
                else if (dead && rejoining) Event(track, frameMs, "dead");
                var position = native?.Center ?? actor.Position;
                int observedMs = native == null ? frameMs : Math.Max(0, Milliseconds(native.SampleTime));
                // Negative prior baseline at a restarted recording is bounded
                // by archive validation and anchored to its first real frame.
                if (native != null && native.SampleTime >= 0)
                {
                    long? anchor = native.RunTimeMs >= 0 ? native.RunTimeMs - observedMs : null;
                    if (anchor < -60_000 || anchor > 7L * 24 * 60 * 60 * 1000) anchor = null;
                    if (native.SharedRunKey.Length != 0)
                    {
                        // The host assigns the actual RunId two seconds after
                        // StartRun and then resets its shared timer. Prefer the
                        // last observed identity; early empty/stale identities
                        // must not group this recording with the preceding run.
                        if (native.SharedRunKey != lastRunKey) { lastRunKey = native.SharedRunKey; runTimeOrigin = anchor; }
                        // Room-property identity and the timer-reset RPC may
                        // arrive on different frames. Keep the latest valid
                        // clock anchor even when the identity already matches.
                        else if (anchor.HasValue) runTimeOrigin = anchor;
                    }
                    else if (!headerTimeOrigin.HasValue && anchor.HasValue) headerTimeOrigin = anchor;
                }
                bool warp = native != null && track.Native != null &&
                    (native.WarpSequence != track.Native.WarpSequence || native.Warping && !track.Native.Warping);
                bool gap = track.LastFrameTime >= 0 && frame.T - track.LastFrameTime > 1
                    || native != null && track.Native != null && native.SampleTime - track.Native.SampleTime > 1;
                bool jump = false;
                if (track.LastPosition != null && observedMs > track.LastPointMs)
                {
                    double distance = Distance(position, track.LastPosition);
                    double elapsed = (observedMs - track.LastPointMs) / 1000d;
                    // High-speed legitimate movement is retained, but an
                    // implausible bridge never becomes a useful route segment.
                    jump = distance > Math.Max(10, elapsed * 50);
                }
                if (warp) Event(track, frameMs, "warp");
                if (warp || gap || jump || rejoining && track.LastFrameTime >= 0)
                { Event(track, frameMs, "break"); track.LastPosition = null; }
                // World.Segment is already recorded once per replay frame. It
                // proves the native map phase even while its campfire is before
                // the next title/progress Z plane. Keep this clock at frameMs,
                // independent of the cached actor sample's earlier timestamp.
                bool aliveNow = native?.Alive == true && !native.Warping;
                bool continuousStep = aliveNow && track.Native?.Alive == true && !track.Native.Warping
                    && !warp && !gap && !jump && !rejoining && !lifeChanged && track.LastFrameGameStage == track.GameStage;
                if (gameStage.HasValue)
                {
                    if (track.GameStage != gameStage)
                    {
                        int? previousStage = track.GameStage;
                        Event(track, frameMs, "game-stage", gameStage.Value);
                        // This is the player's actual campfire advancement,
                        // not a claim that the entire path is uninterrupted.
                        // Earlier breaks remain exported for server filtering.
                        if (previousStage.HasValue && continuousStep
                            && gameStage.Value == previousStage.Value + 1 && OrdinaryStage(previousStage.Value)
                            && OrdinaryStage(gameStage.Value) && track.CheckpointStages.Add(previousStage.Value))
                            Event(track, frameMs, "checkpoint", previousStage.Value);
                        track.GameStage = gameStage;
                    }
                }
                // Full memoir replay still retains ghost animation. The upload
                // whitelist ends each living segment at the actual death sample
                // and resumes only after an observed revival, never ghost motion.
                int pointMs = terminalDeath ? frameMs : observedMs;
                bool sameBucket = pointMs / 100 == track.LastPointMs / 100 && track.LastPointMs >= 0;
                bool sampled = terminalDeath || !dead && !sameBucket && (lifeChanged || observedMs >= track.LastPointMs + 100);
                if (sampled)
                {
                    // Preserve the exact terminal position without increasing
                    // the wire sampling rate: it replaces this bucket's prior
                    // living point. A same-bucket revival waits for a later one.
                    if (!sameBucket && ++points > MaximumRetainedPoints && retainData)
                        throw new PackageBudgetException("Trajectory point batch budget exceeded.");
                    int[] point = { pointMs, ReplayRouteRules.Centimeters(position[0]), ReplayRouteRules.Centimeters(position[1]), ReplayRouteRules.Centimeters(position[2]) };
                    // Emit finish evidence only for an alive, unbroken native
                    // crossing. The server still checks the stage's start and
                    // every required interval; finish alone never qualifies it.
                    if (native?.Alive == true && !native.Warping && track.LastPosition != null && !warp && !gap && !jump && !rejoining && track.Dead == false)
                        foreach (var stage in package.Map.Stages)
                            if (stage.ExitZCm.HasValue && ReplayRouteRules.Centimeters(track.LastPosition[2]) <= stage.ExitZCm && point[3] > stage.ExitZCm && track.FinishedStages.Add(stage.Index))
                                Event(track, observedMs, "finish", stage.Index);
                    if (retainData)
                    {
                        if (sameBucket && track.Value.Points.Count != 0)
                            track.Value.Points[track.Value.Points.Count - 1] = point;
                        else track.Value.Points.Add(point);
                    }
                    track.LastPointMs = pointMs; track.LastPosition = (float[])position.Clone();
                }
                if (native?.Finished == true && track.Native?.Finished == false)
                {
                    int last = native.FinishedNadir ? Array.FindIndex(package.Map.Stages, s => s.Name == "Void") :
                        Array.FindLastIndex(package.Map.Stages, s => s.Name != "Void");
                    if (last >= 0 && track.FinishedStages.Add(last)) Event(track, frameMs, "finish", last);
                }
                if (native?.Alive == true && native.Finished && !native.FinishedNadir && !track.SummitRecorded
                    && (track.Native == null || !track.Native.Finished))
                {
                    int last = Array.FindLastIndex(package.Map.Stages, s => s.Name != "Void");
                    if (last >= 0) { Event(track, frameMs, "summit", last); track.SummitRecorded = true; }
                }
                track.Native = native; track.LastFrameTime = frame.T; track.LastFrameGameStage = gameStage;
            }
            foreach (var pair in tracks)
                if (pair.Value.Present && !present.Contains(pair.Key))
                { Event(pair.Value, frameMs, "leave"); Event(pair.Value, frameMs, "break"); pair.Value.Present = false;
                    pair.Value.LastPosition = null; }
        }

        private bool OrdinaryStage(int index) => package.Map.Stages.Any(stage => stage.Index == index && stage.Name != "Void");

        private int? MappedGameStage(int? nativeSegment)
        {
            if (nativeSegment == 6)
            {
                var nadir = package.Map.Stages.FirstOrDefault(stage => stage.Name == "Void");
                return nadir?.Index;
            }
            int index = nativeSegment == 5 ? 4 : nativeSegment ?? -1;
            return index >= 0 && index <= 4 && OrdinaryStage(index) ? index : null;
        }

        public TrajectoryPackage Finish(TrajectoryPackage? sharedMetadata = null)
        {
            package.RunKey = lastRunKey ?? headerRunKey;
            package.TimeOriginMs = lastRunKey != null ? runTimeOrigin : headerTimeOrigin;
            if (package.RunKey == null || !package.TimeOriginMs.HasValue)
            { package.RunKey = null; package.TimeOriginMs = null; }
            if (sharedMetadata != null)
            { package.RunKey = sharedMetadata.RunKey; package.TimeOriginMs = sharedMetadata.TimeOriginMs; }
            string scope = package.RunKey ?? package.RecordingId;
            foreach (var pair in tracks)
            {
                var track = pair.Value;
                track.Value.Key = ReplayRouteRules.Hash("peak-memories/player/v1/" + scope + "/" + pair.Key);
                track.Value.Evidence = track.EverMissingEvidence ? "legacy-unknown" : "native-state";
                if (track.EverMissingEvidence)
                    track.Value.Events.RemoveAll(value => value.Kind == "summit" || value.Kind == "checkpoint");
                track.Value.Events = track.Value.Events.OrderBy(value => value.TMs).ToList();
            }
            if (package.Players.Count == 0) throw new InvalidDataException("录像中没有可导出的队员信息。");
            return package;
        }

        private void Event(Track track, int time, string kind, int? stage = null)
        {
            if (!retainData) return;
            var last = track.Value.Events.LastOrDefault();
            if (last != null && last.TMs == time && last.Kind == kind && last.StageIndex == stage) return;
            if (++events > MaximumRetainedEvents || track.Value.Events.Count >= MaximumEvents)
                throw new PackageBudgetException("Trajectory event batch budget exceeded.");
            track.Value.Events.Add(new TrajectoryEvent { TMs = time, Kind = kind, StageIndex = stage });
        }
        private static double Distance(float[] a, float[] b)
        { double x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2]; return Math.Sqrt(x * x + y * y + z * z); }
    }

    private static int Milliseconds(double seconds)
    {
        if (!ReplayRules.Finite(seconds) || seconds < -1 || seconds > FullReplayArchive.MaximumDuration)
            throw new InvalidDataException("Trajectory time exceeds its recording bounds.");
        return checked((int)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero));
    }

    private sealed class LimitedWriteStream : Stream
    {
        private readonly Stream inner;
        private readonly long maximum;
        private readonly CancellationToken cancellation;
        public long BytesWritten { get; private set; }
        public LimitedWriteStream(Stream inner, long maximum, CancellationToken cancellation)
        { this.inner = inner; this.maximum = maximum; this.cancellation = cancellation; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            cancellation.ThrowIfCancellationRequested();
            if (count > maximum - BytesWritten) throw new PackageBudgetException("Trajectory packet byte budget exceeded.");
            inner.Write(buffer, offset, count); BytesWritten += count;
        }
        public override void Flush() { cancellation.ThrowIfCancellationRequested(); inner.Flush(); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
