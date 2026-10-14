using System;

namespace PeakReplayLab;

// Translate immutable observation clocks while retaining all renderer/source
// arrays. Bounded hot caches avoid retaining a run's earlier pages or invoking
// Mono's ephemeron machinery from the save worker.
internal sealed class EnvironmentTimeRebaser
{
    private readonly double origin;
    private readonly BoundedReferenceCache<EnvironmentReplayFrame, EnvironmentReplayFrame> environments = new(256);
    private readonly BoundedReferenceCache<WorldFrame, WorldFrame> worlds = new(128);
    private readonly Func<WorldFrame, WorldFrame> worldFactory;
    private readonly Func<EnvironmentReplayFrame, EnvironmentReplayFrame> environmentFactory;
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
