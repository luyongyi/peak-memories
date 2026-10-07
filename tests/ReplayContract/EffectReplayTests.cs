using PeakReplayLab;

internal static class EffectReplayTests
{
    public static void Run(Action<string, Action> test)
    {
        test("native effect clock advances from native phase and rate", () =>
        { var f = Sample(); Check(EffectReplayRules.PhaseAt(f, 12) == 5); });
        test("native effect clock respects pause and delayed start", () =>
        { var f = Sample(); f.Paused = true; Check(EffectReplayRules.PhaseAt(f, 12) == 1);
          f.Paused = false; Check(EffectReplayRules.PhaseAt(f, 9) == 1); });
        test("effect snapshot crop preserves ongoing lifetime without mutating capture", () =>
        {
            var f = Sample(); var cropped = EffectReplayRules.Rebase(f, 12);
            Check(f.AnchorTime == 10 && cropped.AnchorTime == -2);
            Check(EffectReplayRules.PhaseAt(cropped, 0) == EffectReplayRules.PhaseAt(f, 12));
            Check(ReferenceEquals(cropped.Seeds, f.Seeds) && ReferenceEquals(cropped.Position, f.Position));
        });
        test("effect emission stop is a phase not an absolute timestamp", () =>
        {
            var f = Sample(); f.Emitting = false; f.EmissionStopPhase = 3;
            var cropped = EffectReplayRules.Rebase(f, 12); EffectReplayRules.Validate(cropped);
            Check(cropped.EmissionStopPhase == 3 && !cropped.Emitting && EffectReplayRules.PhaseAt(cropped, 1) == 7);
        });
        test("effect backward seek is stateless and repeatable", () =>
        { var f = Sample(); double before = EffectReplayRules.PhaseAt(f, 11); EffectReplayRules.PhaseAt(f, 13);
          Check(before == EffectReplayRules.PhaseAt(f, 11)); });
        test("effect seeds preserve full unsigned native range", () =>
        { var f = Sample(); f.Seed = uint.MaxValue; f.Seeds = new long[] { 0, uint.MaxValue }; EffectReplayRules.Validate(f); });
        test("effect invalid seed negative phase and clock are rejected", () =>
        {
            var f = Sample(); f.Seed = (long)uint.MaxValue + 1; Bad(f);
            f = Sample(); f.Seeds = new long[] { -1 }; Bad(f);
            f = Sample(); f.Phase = -.001; Bad(f);
            f = Sample(); f.AnchorTime = double.NaN; Bad(f);
        });
        test("effect invalid orientation and excessive child systems are rejected", () =>
        { var f = Sample(); f.Rotation = new float[4]; Bad(f); f = Sample(); f.Seeds = new long[65]; Bad(f); });
        test("native mesh effect requires actual animation resource identity", () =>
        { var f = Sample(); f.Kind = 1; Bad(f); f.Clip = "Explosion"; EffectReplayRules.Validate(f); });
        test("antigravity sphere uses native mesh identity and observed scale without a fake animation", () =>
        {
            var f = Sample(); f.Kind = 2; f.Resource = "antigravity/AntiSphere"; f.Clip = "";
            f.Scale = new[] { .2f, .2f, .2f }; f.Paused = true; f.Rate = 0;
            EffectReplayRules.Validate(f); var copy = EffectReplayRules.Rebase(f, 20);
            Check(copy.Kind == 2 && copy.Clip == "" && ReferenceEquals(f.Scale, copy.Scale));
            f.Kind = 3; Bad(f);
        });
        test("paused particle preroll traces its anchor instead of the later paused seek cursor", () =>
        {
            var f = Sample(); f.Paused = true;
            Check(EffectReplayRules.PhaseAt(f, 100) == 1 && EffectReplayRules.TimeAtPhase(f, 1) == 10);
            Check(EffectReplayRules.TimeAtPhase(f, 0) == 9.5);
            var cropped = EffectReplayRules.Rebase(f, 12);
            Check(EffectReplayRules.TimeAtPhase(cropped, 0) == -2.5);
        });
        test("effect preroll and creation use one finite global frame budget", () =>
        {
            var budget = new EffectReplayWorkBudget(); int steps = 0, creations = 0;
            for (int entity = 0; entity < 512; entity++)
            {
                if (budget.Create()) creations++;
                for (int step = 0; step < 360; step++) if (budget.Step()) steps++;
            }
            Check(steps == EffectReplayWorkBudget.MaximumSteps && creations == EffectReplayWorkBudget.MaximumCreations);
            Check(!budget.Step() && !budget.Create());
            Check(new EffectReplayWorkBudget().Step());
        });
        test("effect budget includes child seed array and resource identities", () =>
        { var f = Sample(); long first = EffectReplayRules.Estimate(f); f.Seeds = new long[64]; Check(EffectReplayRules.Estimate(f) >= first + 62 * 8); });
    }
    private static EffectReplayFrame Sample() => new()
    { Key = "native-effect-test", Resource = "particle/test", AnchorTime = 10, Phase = 1, Rate = 2, Seed = 123, Seeds = new long[] { 123, 456 } };
    private static void Check(bool condition) { if (!condition) throw new Exception("Effect replay assertion failed."); }
    private static void Bad(EffectReplayFrame value)
    { try { EffectReplayRules.Validate(value); } catch (InvalidDataException) { return; } throw new Exception("Invalid effect data was accepted."); }
}
