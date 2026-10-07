using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

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

    public ReplayRouteContext Copy() => new()
    {
        Version = Version, RecordingId = RecordingId, RunKey = RunKey, TimeOriginMs = TimeOriginMs,
        LevelIndex = LevelIndex, LayoutKey = LayoutKey, Ascent = Ascent, Custom = Custom, Mini = Mini,
        Stages = Stages.Select(s => new ReplayRouteStage
        { Index = s.Index, Name = s.Name, EnterZCm = s.EnterZCm, ExitZCm = s.ExitZCm }).ToArray(),
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
