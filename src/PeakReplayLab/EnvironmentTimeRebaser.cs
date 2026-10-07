using System.Runtime.CompilerServices;

namespace PeakReplayLab;

// Translate immutable observation clocks while retaining all renderer/source
// arrays. Weak keys prevent a save worker from retaining a run's earlier pages.
internal sealed class EnvironmentTimeRebaser
{
    private readonly double origin;
    private readonly ConditionalWeakTable<EnvironmentReplayFrame, EnvironmentReplayFrame> environments = new();
    private readonly ConditionalWeakTable<WorldFrame, WorldFrame> worlds = new();
    private readonly ConditionalWeakTable<WorldFrame, WorldFrame>.CreateValueCallback worldFactory;
    private readonly ConditionalWeakTable<EnvironmentReplayFrame, EnvironmentReplayFrame>.CreateValueCallback environmentFactory;
    public EnvironmentTimeRebaser(double origin)
    { this.origin = origin; worldFactory = ShiftWorld; environmentFactory = ShiftEnvironment; }
    public WorldFrame Apply(WorldFrame source)
    {
        if (origin == 0 || source.Environment == null || !source.Environment.SampleTimeKnown) return source;
        return worlds.GetValue(source, worldFactory);
    }
    private WorldFrame ShiftWorld(WorldFrame value) => new()
    {
        Segment = value.Segment, ActiveMapObjects = value.ActiveMapObjects, TimeOfDay = value.TimeOfDay, Day = value.Day,
        Environment = environments.GetValue(value.Environment!, environmentFactory),
    };
    private EnvironmentReplayFrame ShiftEnvironment(EnvironmentReplayFrame environment) => new()
    {
        SampleTimeKnown = true, SampleTime = environment.SampleTime - origin,
        Winds = environment.Winds, Lava = environment.Lava, Fog = environment.Fog,
        WeatherBlend = environment.WeatherBlend, GlobalWind = environment.GlobalWind, RainFactor = environment.RainFactor,
        SnowFactor = environment.SnowFactor, HeightFogAmount = environment.HeightFogAmount, HeightFogSpirit = environment.HeightFogSpirit,
    };
}
