using PeakReplayLab;

internal static class CreatureDirectoryTests
{
    public static void Run(Action<string, Action> test)
    {
        test("audited trap roots require their own one-shot spawn and deactivate model signature", () =>
        {
            foreach (var pair in new[] { ("Forest_SporeFungus", "VFX_SporeExplo"),
                ("Jungle_SporeMushroom", "VFX_PoisonExplo"), ("Jungle_SporeMushroomExplo", "VFX_SporeExploExplo") })
            {
                Check(CreatureDirectoryRules.TrapSignature(pair.Item1, pair.Item2, true, true, true, true));
                Check(CreatureDirectoryRules.TrapSignature(pair.Item1 + " (1)(Clone)", pair.Item2 + "(Clone)", true, true, true, true));
                Check(!CreatureDirectoryRules.TrapSignature(pair.Item1, "VFX_Unrelated", true, true, true, true));
                Check(!CreatureDirectoryRules.TrapSignature(pair.Item1, pair.Item2, true, false, true, true));
                Check(!CreatureDirectoryRules.TrapSignature(pair.Item1, pair.Item2, true, true, false, true));
                Check(!CreatureDirectoryRules.TrapSignature(pair.Item1, pair.Item2, false, true, true, true));
                Check(!CreatureDirectoryRules.TrapSignature(pair.Item1, pair.Item2, true, true, true, false));
            }
            foreach (string leaf in new[] { "SporeShroom", "SomeTriggerEvent", "Forest_SporeFungus Extra", "Forest_SporeFungus (fake)" })
                Check(!CreatureDirectoryRules.TrapSignature(leaf, "VFX_SporeExplo", true, true, true, true));
        });
        test("poison spore effect is admitted by exact resource without admitting other poison effects", () =>
        {
            Check(CreatureDirectoryRules.IsSporeEffect("VFX_PoisonExplo(Clone)"));
            Check(CreatureDirectoryRules.IsSporeEffect("VFX_SporeExplo"));
            Check(CreatureDirectoryRules.IsSporeEffect("VFX_SporeExploExplo"));
            foreach (string other in new[] { "PoisonZone", "VFX_Poison", "VFX_PoisonExploOther", "SporeShroom" })
                Check(!CreatureDirectoryRules.IsSporeEffect(other));
        });
        test("full static trap directory leaves independent native creature capacity and bounds detailed legacy traps", () =>
        {
            var trap = Trap(); var creature = new CreatureReplayFrame { Key = "spider", Kind = "spider", Resource = "spider" };
            var frames = new CreatureReplayFrame[CreatureReplayRules.MaximumEntities];
            Array.Fill(frames, trap, 0, CreatureReplayRules.MaximumStaticTraps);
            Array.Fill(frames, creature, CreatureReplayRules.MaximumStaticTraps, CreatureReplayRules.MaximumDynamicEntities);
            CreatureReplayRules.ValidateCounts(frames);
            frames[0] = creature; Reject(() => CreatureReplayRules.ValidateCounts(frames));
            var detailed = Trap(); detailed.Pose.Nodes = new[] { new NodePose { Path = "mesh" } };
            var legacy = new CreatureReplayFrame[CreatureReplayRules.MaximumDynamicEntities + 1]; Array.Fill(legacy, detailed);
            Reject(() => CreatureReplayRules.ValidateCounts(legacy));
        });
        test("static trap activation observes every tick and does not mutate prior explosion or backward states", () =>
        {
            float[] trs = { 1, 2, 3, 0, 0, 0, 1, 1, 1, 1 };
            var before = CreatureDirectoryRules.StaticPose(null, true, trs);
            for (int i = 0; i < 1000; i++) Check(ReferenceEquals(before, CreatureDirectoryRules.StaticPose(before, true, trs)));
            var exploded = CreatureDirectoryRules.StaticActive(before, false);
            Check(before.Active && !exploded.Active && before.Nodes.Length == 0);
            Check(ReferenceEquals(before.Position, exploded.Position) && ReferenceEquals(before.Rotation, exploded.Rotation) && ReferenceEquals(before.Scale, exploded.Scale));
            var restored = CreatureDirectoryRules.StaticActive(exploded, true);
            Check(restored.Active && !exploded.Active && ReferenceEquals(restored.Position, before.Position));
            trs[0] = 4;
            var moved = CreatureDirectoryRules.StaticPose(restored, true, trs);
            Check(moved.Position[0] == 4 && before.Position[0] == 1 && ReferenceEquals(moved.Rotation, before.Rotation) && ReferenceEquals(moved.Scale, before.Scale));
            RejectArgument(() => CreatureDirectoryRules.StaticPose(null, true, new float[9]));
        });
    }
    private static CreatureReplayFrame Trap() => new() { Key = "trap", Kind = "spore-trap", Resource = "spore-trap", SourcePath = "Map/trap" };
    private static void Check(bool condition) { if (!condition) throw new Exception("Creature directory assertion failed."); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Unbounded creature directory accepted."); }
    private static void RejectArgument(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Malformed static root accepted."); }
}
