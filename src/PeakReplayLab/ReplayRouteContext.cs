using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace PeakReplayLab;

// Optional, immutable recording-start evidence. A missing context means unknown;
// it never borrows the map or difficulty active when an old recording is opened.
public sealed class ReplayRouteContext
{
    public int Version { get; set; } = 1;
    public string RecordingId { get; set; } = "";
    public string? RunKey { get; set; }
    public long? TimeOriginMs { get; set; }
    public int? LevelIndex { get; set; }
    public string? LayoutKey { get; set; }
    public int? Ascent { get; set; }
    public bool? Custom { get; set; }
    public bool? Mini { get; set; }
    public ReplayRouteStage[] Stages { get; set; } = Array.Empty<ReplayRouteStage>();
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ReplayMapAlignment? Alignment { get; set; }

    public ReplayRouteContext Copy() => new()
    {
        Version = Version, RecordingId = RecordingId, RunKey = RunKey, TimeOriginMs = TimeOriginMs,
        LevelIndex = LevelIndex, LayoutKey = LayoutKey, Ascent = Ascent, Custom = Custom, Mini = Mini,
        Stages = Stages.Select(s => new ReplayRouteStage
        { Index = s.Index, Name = s.Name, EnterZCm = s.EnterZCm, ExitZCm = s.ExitZCm }).ToArray(),
        Alignment = Alignment?.Copy(),
    };
}

// Recording-start evidence only. These are stable map object names and world
// transforms, never player positions or runtime instance IDs. Old headers omit
// this optional field; their missing evidence must not be inferred later.
public sealed class ReplayMapAlignment
{
    [JsonProperty(Required = Required.Always)]
    public int Version { get; set; } = 1;
    [JsonProperty(Required = Required.Always)]
    public string CoordinateSpace { get; set; } = "unity-world-cm";
    [JsonProperty(Required = Required.Always)]
    public ReplayMapLandmark[] Landmarks { get; set; } = Array.Empty<ReplayMapLandmark>();

    public ReplayMapAlignment Copy() => new()
    {
        Version = Version, CoordinateSpace = CoordinateSpace,
        Landmarks = Landmarks.Select(value => value.Copy()).ToArray(),
    };
}

public sealed class ReplayMapLandmark
{
    [JsonProperty(Required = Required.Always)]
    public string Key { get; set; } = "";
    [JsonProperty(Required = Required.Always)]
    public string Kind { get; set; } = "";
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? StageIndex { get; set; }
    [JsonProperty(Required = Required.Always)]
    public string Name { get; set; } = "";
    [JsonProperty(Required = Required.Always)]
    public int[] PositionCm { get; set; } = Array.Empty<int>();
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public float[]? Rotation { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public float[]? Scale { get; set; }

    public ReplayMapLandmark Copy() => new()
    {
        Key = Key, Kind = Kind, StageIndex = StageIndex, Name = Name,
        PositionCm = (int[])PositionCm.Clone(), Rotation = Rotation == null ? null : (float[])Rotation.Clone(),
        Scale = Scale == null ? null : (float[])Scale.Clone(),
    };
}

public sealed class ReplayRouteStage
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int? EnterZCm { get; set; }
    public int? ExitZCm { get; set; }
}

// Cached at 10 Hz, with immediate status-change samples. The center is the
// native torso position used by MountainProgressHandler, not the hip pose.
public sealed class ActorRouteState
{
    public double SampleTime { get; set; }
    public float[] Center { get; set; } = new float[3];
    public bool Alive { get; set; }
    public bool LocalOwner { get; set; }
    public bool Warping { get; set; }
    public int WarpSequence { get; set; }
    public bool Finished { get; set; }
    public bool FinishedNadir { get; set; }
    public long RunTimeMs { get; set; } = -1;
    public string SharedRunKey { get; set; } = "";

    internal ActorRouteState Rebase(double origin) => new()
    {
        SampleTime = SampleTime - origin, Center = Center, Alive = Alive, LocalOwner = LocalOwner,
        Warping = Warping, WarpSequence = WarpSequence, Finished = Finished,
        FinishedNadir = FinishedNadir, RunTimeMs = RunTimeMs, SharedRunKey = SharedRunKey,
    };
}

public static class ReplayRouteRules
{
    public static bool HashKey(string? key) => key != null && key.Length == 64 &&
        key.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');

    public static string Hash(string value)
    {
        using var hash = SHA256.Create();
        var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(value));
        var result = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes) result.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return result.ToString();
    }

    public static int Centimeters(float value)
    {
        if (!ReplayRules.Finite(value) || Math.Abs((double)value * 100) > int.MaxValue)
            throw new InvalidDataException("Trajectory coordinate is outside its centimeter range.");
        return checked((int)Math.Round((double)value * 100, MidpointRounding.AwayFromZero));
    }

    public static void Validate(ReplayRouteContext? context)
    {
        if (context == null) return;
        if (context.Version != 1 || !HashKey(context.RecordingId) ||
            context.RunKey != null && !HashKey(context.RunKey) ||
            context.LayoutKey != null && !HashKey(context.LayoutKey) ||
            context.LevelIndex < 0 || context.LevelIndex > 10_000_000 ||
            context.Ascent < -1 || context.Ascent > 100 ||
            context.TimeOriginMs < -60_000 || context.TimeOriginMs > 7L * 24 * 60 * 60 * 1000 ||
            context.Stages == null || context.Stages.Length > 7)
            throw new InvalidDataException("Invalid recording route context.");
        for (int i = 0; i < context.Stages.Length; i++)
        {
            var stage = context.Stages[i];
            if (stage == null || stage.Index != i || string.IsNullOrWhiteSpace(stage.Name) || stage.Name.Length > 100 ||
                stage.EnterZCm.HasValue != stage.ExitZCm.HasValue ||
                stage.EnterZCm.HasValue && stage.ExitZCm <= stage.EnterZCm)
                throw new InvalidDataException("Invalid recorded stage gates.");
        }
        Validate(context.Alignment);
        if (context.Alignment != null)
        {
            var landmarks = context.Alignment.Landmarks;
            if (landmarks.Any(value => value.StageIndex.HasValue && !context.Stages.Any(stage => stage.Index == value.StageIndex)))
                throw new InvalidDataException("Map landmark references an absent recorded stage.");
            foreach (var stage in context.Stages.Where(value => value.EnterZCm.HasValue && value.Name != "Void"))
            {
                var entry = landmarks.FirstOrDefault(value => value.Key == "progress-point:" + stage.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var next = context.Stages.FirstOrDefault(value => value.Index > stage.Index && value.Name != "Void");
                var exit = landmarks.FirstOrDefault(value => value.Key == (next == null ? "progress-point:peak" :
                    "progress-point:" + next.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                if (entry != null && entry.PositionCm[2] != stage.EnterZCm || exit != null && exit.PositionCm[2] != stage.ExitZCm)
                    throw new InvalidDataException("Recorded stage gates disagree with their world landmarks.");
            }
        }
    }

    public static void Validate(ReplayMapAlignment? alignment)
    {
        if (alignment == null) return;
        if (alignment.Version != 1 || alignment.CoordinateSpace != "unity-world-cm" ||
            alignment.Landmarks == null || alignment.Landmarks.Length < 1 || alignment.Landmarks.Length > 16)
            throw new InvalidDataException("Invalid recorded map alignment.");
        var keys = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (var landmark in alignment.Landmarks)
        {
            bool root = landmark?.Kind == "segment-root", gate = landmark?.Kind == "progress-point";
            bool peak = gate && landmark!.Key == "progress-point:peak";
            if (landmark == null || !keys.Add(landmark.Key) || (!root && !gate) ||
                (peak ? landmark.StageIndex.HasValue : !landmark.StageIndex.HasValue || landmark.StageIndex < 0 || landmark.StageIndex > 6) ||
                (!peak && landmark.Key != landmark.Kind + ":" + landmark.StageIndex!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) ||
                string.IsNullOrWhiteSpace(landmark.Name) || landmark.Name.Length > 120 || landmark.Name.Any(char.IsControl) ||
                landmark.PositionCm == null || landmark.PositionCm.Length != 3 || landmark.PositionCm.Any(value => Math.Abs((long)value) > 100_000_000))
                throw new InvalidDataException("Invalid recorded map landmark.");
            if (root)
            {
                if (landmark.Rotation == null || landmark.Rotation.Length != 4 || landmark.Rotation.Any(value => !ReplayRules.Finite(value)) ||
                    Math.Abs(landmark.Rotation.Sum(value => (double)value * value) - 1) > .002 ||
                    landmark.Scale == null || landmark.Scale.Length != 3 || landmark.Scale.Any(value => !ReplayRules.Finite(value) || Math.Abs(value) < .000001 || Math.Abs(value) > 10_000))
                    throw new InvalidDataException("Invalid recorded map root transform.");
            }
            else if (landmark.Rotation != null || landmark.Scale != null)
                throw new InvalidDataException("Progress-point landmarks only contain world positions.");
        }
    }

    public static void Validate(ActorRouteState? state, double frameTime)
    {
        if (state == null) return;
        if (!ReplayRules.Finite(state.SampleTime) || state.SampleTime < -1 || state.SampleTime > frameTime + .000001 ||
            frameTime - state.SampleTime > 1 || state.Center == null || state.Center.Length != 3 ||
            state.Center.Any(p => !ReplayRules.Finite(p) || Math.Abs(p) >= 10_000_000) ||
            state.WarpSequence < 0 || state.FinishedNadir && !state.Finished ||
            state.RunTimeMs < -1 || state.RunTimeMs > 7L * 24 * 60 * 60 * 1000 ||
            state.SharedRunKey == null || state.SharedRunKey.Length != 0 && !HashKey(state.SharedRunKey))
            throw new InvalidDataException("Invalid recorded actor route state.");
    }
}
