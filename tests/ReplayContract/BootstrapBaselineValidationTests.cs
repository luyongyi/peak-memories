using PeakReplayLab;

internal static class BootstrapBaselineValidationTests
{
    internal static void Run(Action<string,Action> test)
    {
        test("bootstrap validates an actual empty-actor initial scene before native local spawn", () =>
        {
            var frame=new ReplayFrame { T=15, Actors=Array.Empty<ActorFrame>() };
            ReplayRules.ValidateCapture(frame,-1);
            if(frame.Actors.Length!=0 || frame.T!=15) throw new Exception("bootstrap validation changed captured time or actors");
        });
        test("bootstrap validation rejects future scene clocks and malformed native world values without backfill", () =>
        {
            foreach(var frame in new[]{ new ReplayFrame { T=15, SceneSampleTimeKnown=true,SceneSampleTime=16 },
                new ReplayFrame { T=15, World=new WorldFrame { TimeOfDay=float.NaN } } })
            {
                try { ReplayRules.ValidateCapture(frame,-1); throw new Exception("invalid initial scene accepted"); }
                catch(System.IO.InvalidDataException) { }
            }
        });
    }
}
