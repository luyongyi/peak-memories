using PeakReplayLab;

internal static class ItemOptimizationTests
{
    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("stationary item gate avoids 60 Hz probes", () =>
        {
            var policy = new ItemSamplingPolicy(0);
            check(policy.ShouldRead(0, 0, false)); policy.Observed(0, 0, false);
            for (int i = 1; i < 27; i++) check(!policy.ShouldRead(i / 60.0, 0, false));
            check(policy.ShouldRead(.5, 0, false));
        });
        test("item lifecycle revision bypasses cold deadline", () =>
        { var p = new ItemSamplingPolicy(1); p.Observed(10, 5, false); check(p.ShouldRead(10.001, 6, false)); });
        test("held or animated items keep every observed sample", () =>
        { var p = new ItemSamplingPolicy(1); p.Observed(10, 5, false); check(p.ShouldRead(10.001, 5, true)); });
        test("thrown items stay hot briefly after movement then cool", () =>
        { var p = new ItemSamplingPolicy(0); p.Observed(0, 0, true); check(p.ShouldRead(.1, 0, false));
          p.Observed(.1, 0, false); check(p.ShouldRead(.2, 0, false)); check(!p.ShouldRead(.3, 0, false)); });
        test("stationary fallback deadlines are spread across entities", () =>
        { var a = new ItemSamplingPolicy(0); var b = new ItemSamplingPolicy(16); a.Observed(0,0,false); b.Observed(0,0,false);
          check(a.ShouldRead(.5,0,false)); check(!b.ShouldRead(.5,0,false)); check(b.ShouldRead(.7,0,false)); });
        test("inventory stays cached until event selection or fallback", () =>
        { check(!ItemSamplingPolicy.NeedsInventory(1,2,5,5,1,1)); check(ItemSamplingPolicy.NeedsInventory(1,2,6,5,1,1));
          check(ItemSamplingPolicy.NeedsInventory(1,2,5,5,2,1)); check(ItemSamplingPolicy.NeedsInventory(2,2,5,5,1,1)); });
        test("plain static children only sample on lifecycle changes", () =>
        { var p = new ItemSamplingPolicy(0); check(p.SampleChildren(0,true,false,false));
          check(!p.SampleChildren(1,false,false,false)); check(p.SampleChildren(1.01,true,false,false)); });
        test("ground dynamic children use slow fallback even while root stays hot", () =>
        { var p = new ItemSamplingPolicy(0); check(p.SampleChildren(0,true,false,true));
          for (int i = 1; i < 27; i++) { p.Observed(i/60.0,0,true); check(!p.SampleChildren(i/60.0,false,false,true)); }
          check(p.SampleChildren(.5,false,false,true)); check(!p.SampleChildren(.51,false,false,true)); });
        test("attached dynamic children retain 60 Hz while simple held meshes do not rescan", () =>
        { var p = new ItemSamplingPolicy(0); p.SampleChildren(0,true,true,true);
          check(p.SampleChildren(1/60.0,false,true,true)); check(!p.SampleChildren(2/60.0,false,true,false)); });
        test("static shared pose ignores irrelevant interpolation changes", () =>
        { var p = new ObjectPose(); check(PoseVersionPolicy.CanSkip(p,p,.8f,p,p,.1f,false,false,false)); });
        test("dynamic pose pair never skips a changed cursor", () =>
        { var a = new ObjectPose(); var b = new ObjectPose(); check(!PoseVersionPolicy.CanSkip(a,b,.8f,a,b,.1f,false,false,false)); });
        test("seek force and backpack visibility invalidate pose cache", () =>
        { var p = new ObjectPose(); check(!PoseVersionPolicy.CanSkip(p,p,0,p,p,0,true,false,false));
          check(!PoseVersionPolicy.CanSkip(p,p,0,p,p,0,false,true,false)); });
        test("new immutable pose version is applied even at same time", () =>
        { var p = new ObjectPose(); var q = new ObjectPose(); check(!PoseVersionPolicy.CanSkip(q,q,0,p,p,0,false,false,false)); });
    }
}
