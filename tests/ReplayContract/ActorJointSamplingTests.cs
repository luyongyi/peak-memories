using PeakReplayLab;

internal static class ActorJointSamplingTests
{
    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("current native skeleton retains all 128 nodes in 49 9 70 priority tiers", () =>
        {
            var paths = NativeSkeleton.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            check(paths.Length == 128 && paths.Distinct(StringComparer.Ordinal).Count() == 128);
            check(paths.Count(p => ActorJointSamplingPolicy.Classify(Stable(p)) == ActorJointPriority.P0) == 49);
            check(paths.Count(p => ActorJointSamplingPolicy.Classify(Stable(p)) == ActorJointPriority.P1) == 9);
            check(paths.Count(p => ActorJointSamplingPolicy.Classify(Stable(p)) == ActorJointPriority.P2) == 70);
            foreach (string path in paths)
                check(ActorJointSamplingPolicy.Classify(path) == ActorJointSamplingPolicy.Classify(Stable(path)));
        });
        test("all 24 finger joints retain full-rate capture including both thumb name orders", () =>
        {
            foreach (string side in new[] { "L", "R" })
                foreach (int segment in new[] { 1, 2, 3 })
                {
                    check(ActorJointSamplingPolicy.Classify(Stable("Scout/Armature/Hip/Index_" + segment + "_" + side)) == ActorJointPriority.P0);
                    check(ActorJointSamplingPolicy.Classify(Stable("Scout/Armature/Hip/Middle_" + segment + "_" + side)) == ActorJointPriority.P0);
                    check(ActorJointSamplingPolicy.Classify(Stable("Scout/Armature/Hip/Pinky_" + segment + "_" + side)) == ActorJointPriority.P0);
                    string thumb = side == "L" ? "Thumb_" + segment + "_L" : "Thumb_R_" + segment;
                    check(ActorJointSamplingPolicy.Classify(Stable("Scout/Armature/Hip/" + thumb)) == ActorJointPriority.P0);
                }
        });
        test("auxiliary hip names require their exact known clothing ancestry", () =>
        {
            check(ActorJointSamplingPolicy.Classify(Stable("Scout/Armature/Hip/Hip_L.014")) == ActorJointPriority.P0);
            check(ActorJointSamplingPolicy.Classify(Stable("Scout/Armature/Hip/ShoeWeight_R/Hip_L.014")) == ActorJointPriority.P2);
            check(ActorJointSamplingPolicy.Classify(Stable("Scout/Armature/Hip/ShoeWeight_L/Hip_L.014")) == ActorJointPriority.P0);
            check(ActorJointSamplingPolicy.Classify(Stable("Scout/Armature/Hip/ShirtArm_L/Hip.002_R.023")) == ActorJointPriority.P2);
            check(ActorJointSamplingPolicy.Classify(Stable("Scout/Armature/Hip/ShirtArm_L/Hip.002_R.022")) == ActorJointPriority.P0);
        });
        test("unknown skeletons and future clothing bones default to full-rate capture", () =>
        {
            foreach (string path in new[]
            {
                "Scout/Armature/Hip/ShoeWeight_L/FutureBone",
                "Scout/Armature/Hip/ShoeWeight_L/Hip_R.999",
                "Scout/Armature/Hip/ShoeWeight_L/Hip_R.+18",
                "Scout/Armature/Hip/Head/Hat/FutureHat/Armature/Bone",
                "Scout/OtherArmature/Hip/Middle_1_R",
                "NewCharacter/Armature/Hip/Detail/Chest",
                "Scout/Armature/Hip/FutureBone",
            }) check(ActorJointSamplingPolicy.Classify(Stable(path)) == ActorJointPriority.P0);
        });
        test("30 and 10 Hz tiers keep their rate with a 58 fps rendered stream", () =>
        {
            foreach (int hz in new[] { 30, 10 })
                foreach (double origin in new[] { 0d, 1048576d })
                {
                    double last = double.NaN; int count = 0;
                    for (int i = 0; i <= 3480; i++)
                    {
                        double now = origin + i / 58d;
                        if (ActorJointSamplingPolicy.Due(hz, now, last, origin)) { last = now; count++; }
                    }
                    check(count == 60 * hz + 1);
                }
        });
        test("tier scheduling shares one game-time phase across uneven frames", () =>
        {
            double last = double.NaN; var captured = new List<double>();
            foreach (double time in new[] { 0d, .017, .034, .051, .068, .085, .102 })
                if (ActorJointSamplingPolicy.Due(30, time, last, 0)) { last = time; captured.Add(time); }
            check(captured.SequenceEqual(new[] { 0d, .034, .068, .102 }));
        });
        test("force samples refresh a transition but never duplicate the same timestamp", () =>
        {
            check(ActorJointSamplingPolicy.Due(10, .016, 0, 0, force: true));
            check(!ActorJointSamplingPolicy.Due(10, .016, .016, 0, force: true));
            check(!ActorJointSamplingPolicy.Due(10, .033, .016, 0));
            check(ActorJointSamplingPolicy.Due(10, .101, .016, 0));
            check(ActorJointSamplingPolicy.Due(10, 1, 2, 0));
        });
        test("P1 foot detail can temporarily sample every rendered transition frame", () =>
        {
            check(ActorJointSamplingPolicy.Classify(Stable("Scout/Armature/Hip/Foot_L/S_Toe_1_L")) == ActorJointPriority.P1);
            check(!ActorJointSamplingPolicy.Due(30, .016, 0, 0));
            check(ActorJointSamplingPolicy.Due(30, .016, 0, 0, force: true));
            check(ActorJointSamplingPolicy.Due(30, .024, .016, 0, force: true));
            check(!ActorJointSamplingPolicy.Due(30, .024, .024, 0, force: true));
            check(ActorJointSamplingPolicy.Due(30, .034, .024, 0));
        });
        test("a missing rendered interval does not invent catch-up observations", () =>
        {
            check(ActorJointSamplingPolicy.Due(30, 2, 0, 0));
            check(!ActorJointSamplingPolicy.Due(30, 2.001, 2, 0));
            check(ActorJointSamplingPolicy.Due(30, 2.034, 2, 0));
        });
        test("capture rates are bounded by actual rendered observations", () =>
        {
            foreach (int hz in new[] { 30, 10 })
            {
                double last = double.NaN; int count = 0;
                for (int i = 0; i <= 30; i++)
                    if (ActorJointSamplingPolicy.Due(hz, i / 5d, last, 0)) { last = i / 5d; count++; }
                check(count == 31);
            }
        });
    }

    private static string Stable(string path) => path == "." ? "." :
        "./" + string.Join("/", path.Split('/').Select(name => Uri.EscapeDataString(name) + "#0"));

    // Captured from the game's 2.5.a native body and propeller-hat skeleton.
    // This fixture is independent of the classifier's category lists.
    private const string NativeSkeleton = """
        .
        Scout
        Scout/Armature
        Scout/Armature/Hip
        Scout/Armature/Hip/Hip_R
        Scout/Armature/Hip/Hip_R/Leg_R
        Scout/Armature/Hip/Hip_R/Leg_R/Knee_R
        Scout/Armature/Hip/Hip_R/Leg_R/Knee_R/Foot_R
        Scout/Armature/Hip/Hip_R/Leg_R/Knee_R/Foot_R/S_Toe_1_R
        Scout/Armature/Hip/Hip_R/Leg_R/Knee_R/Foot_R/S_Toe_1_R/S_Toe_2_R
        Scout/Armature/Hip/Hip_R/Leg_R/Knee_R/Foot_R/S_Heel_R
        Scout/Armature/Hip/Hip_R/Leg_R/Knee_R/ShoeWeight_R
        Scout/Armature/Hip/Hip_R/Leg_R/Knee_R/ShoeWeight_R/Hip_L.014
        Scout/Armature/Hip/Hip_R/Leg_R/Knee_R/ShoeWeight_R/Hip_L.014/Hip_L.018
        Scout/Armature/Hip/Hip_R/Leg_R/Knee_R/ShoeWeight_R/Hip_L.015
        Scout/Armature/Hip/Hip_R/Leg_R/Knee_R/ShoeWeight_R/Hip_L.015/Hip_L.019
        Scout/Armature/Hip/Hip_R/Leg_R/Knee_R/ShoeWeight_R/Hip_L.016
        Scout/Armature/Hip/Hip_R/Leg_R/Knee_R/ShoeWeight_R/Hip_L.016/Hip_L.020
        Scout/Armature/Hip/Hip_R/Leg_R/Knee_R/ShoeWeight_R/Hip_L.017
        Scout/Armature/Hip/Hip_R/Leg_R/Knee_R/ShoeWeight_R/Hip_L.017/Hip_L.021
        Scout/Armature/Hip/Hip_R/Leg_R/PantWeight_R
        Scout/Armature/Hip/Hip_R/Leg_R/PantWeight_R/Hip_L.008
        Scout/Armature/Hip/Hip_R/Leg_R/PantWeight_R/Hip_L.009
        Scout/Armature/Hip/Hip_R/Leg_R/PantWeight_R/Hip_L.009/Hip_L.012
        Scout/Armature/Hip/Hip_R/Leg_R/PantWeight_R/Hip_L.010
        Scout/Armature/Hip/Hip_R/Leg_R/PantWeight_R/Hip_L.011
        Scout/Armature/Hip/Hip_L
        Scout/Armature/Hip/Hip_L/Leg_L
        Scout/Armature/Hip/Hip_L/Leg_L/Knee_L
        Scout/Armature/Hip/Hip_L/Leg_L/Knee_L/Foot_L
        Scout/Armature/Hip/Hip_L/Leg_L/Knee_L/Foot_L/S_Toe_1_L
        Scout/Armature/Hip/Hip_L/Leg_L/Knee_L/Foot_L/S_Toe_1_L/S_Toe_2_L
        Scout/Armature/Hip/Hip_L/Leg_L/Knee_L/Foot_L/S_Heel_L
        Scout/Armature/Hip/Hip_L/Leg_L/Knee_L/ShoeWeight_L
        Scout/Armature/Hip/Hip_L/Leg_L/Knee_L/ShoeWeight_L/Hip_R.014
        Scout/Armature/Hip/Hip_L/Leg_L/Knee_L/ShoeWeight_L/Hip_R.014/Hip_R.018
        Scout/Armature/Hip/Hip_L/Leg_L/Knee_L/ShoeWeight_L/Hip_R.015
        Scout/Armature/Hip/Hip_L/Leg_L/Knee_L/ShoeWeight_L/Hip_R.015/Hip_R.019
        Scout/Armature/Hip/Hip_L/Leg_L/Knee_L/ShoeWeight_L/Hip_R.016
        Scout/Armature/Hip/Hip_L/Leg_L/Knee_L/ShoeWeight_L/Hip_R.016/Hip_R.020
        Scout/Armature/Hip/Hip_L/Leg_L/Knee_L/ShoeWeight_L/Hip_R.017
        Scout/Armature/Hip/Hip_L/Leg_L/Knee_L/ShoeWeight_L/Hip_R.017/Hip_R.021
        Scout/Armature/Hip/Hip_L/Leg_L/PantWeight_L
        Scout/Armature/Hip/Hip_L/Leg_L/PantWeight_L/Hip_R.008
        Scout/Armature/Hip/Hip_L/Leg_L/PantWeight_L/Hip_R.009
        Scout/Armature/Hip/Hip_L/Leg_L/PantWeight_L/Hip_R.009/Hip_R.012
        Scout/Armature/Hip/Hip_L/Leg_L/PantWeight_L/Hip_R.010
        Scout/Armature/Hip/Hip_L/Leg_L/PantWeight_L/Hip_R.011
        Scout/Armature/Hip/Waist
        Scout/Armature/Hip/Waist/WaistR1
        Scout/Armature/Hip/Waist/WaistL1
        Scout/Armature/Hip/Waist/WaistF1
        Scout/Armature/Hip/Waist/WaistB1
        Scout/Armature/Hip/Mid
        Scout/Armature/Hip/Mid/Sash_Base
        Scout/Armature/Hip/Mid/Sash_Base/SashWeight
        Scout/Armature/Hip/Mid/AimJoint
        Scout/Armature/Hip/Mid/AimJoint/Torso
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/Elbow_R
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/Elbow_R/Hand_R
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/Elbow_R/Hand_R/Hand_Upper_R
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/Elbow_R/Hand_R/Hand_Upper_R/Pinky_1_R
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/Elbow_R/Hand_R/Hand_Upper_R/Pinky_1_R/Pinky_2_R
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/Elbow_R/Hand_R/Hand_Upper_R/Pinky_1_R/Pinky_2_R/Pinky_3_R
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/Elbow_R/Hand_R/Hand_Upper_R/Middle_1_R
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/Elbow_R/Hand_R/Hand_Upper_R/Middle_1_R/Middle_2_R
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/Elbow_R/Hand_R/Hand_Upper_R/Middle_1_R/Middle_2_R/Middle_3_R
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/Elbow_R/Hand_R/Hand_Upper_R/Index_1_R
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/Elbow_R/Hand_R/Hand_Upper_R/Index_1_R/Index_2_R
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/Elbow_R/Hand_R/Hand_Upper_R/Index_1_R/Index_2_R/Index_3_R
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/Elbow_R/Hand_R/Thumb_R_1
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/Elbow_R/Hand_R/Thumb_R_1/Thumb_R_2
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/Elbow_R/Hand_R/Thumb_R_1/Thumb_R_2/Thumb_R_3
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/ShirtArm_R
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/ShirtArm_R/Hip.002_L.018
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/ShirtArm_R/Hip.002_L.018/Hip.002_L.022
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/ShirtArm_R/Hip.002_L.019
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/ShirtArm_R/Hip.002_L.019/Hip.002_L.023
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/ShirtArm_R/Hip.002_L.020
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_R/Arm_R/ShirtArm_R/Hip.002_L.021
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/Elbow_L
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/Elbow_L/Hand_L
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/Elbow_L/Hand_L/Hand_Upper_L
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/Elbow_L/Hand_L/Hand_Upper_L/Pinky_1_L
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/Elbow_L/Hand_L/Hand_Upper_L/Pinky_1_L/Pinky_2_L
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/Elbow_L/Hand_L/Hand_Upper_L/Pinky_1_L/Pinky_2_L/Pinky_3_L
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/Elbow_L/Hand_L/Hand_Upper_L/Middle_1_L
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/Elbow_L/Hand_L/Hand_Upper_L/Middle_1_L/Middle_2_L
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/Elbow_L/Hand_L/Hand_Upper_L/Middle_1_L/Middle_2_L/Middle_3_L
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/Elbow_L/Hand_L/Hand_Upper_L/Index_1_L
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/Elbow_L/Hand_L/Hand_Upper_L/Index_1_L/Index_2_L
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/Elbow_L/Hand_L/Hand_Upper_L/Index_1_L/Index_2_L/Index_3_L
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/Elbow_L/Hand_L/Thumb_1_L
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/Elbow_L/Hand_L/Thumb_1_L/Thumb_2_L
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/Elbow_L/Hand_L/Thumb_1_L/Thumb_2_L/Thumb_3_L
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/ShirtArm_L
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/ShirtArm_L/Hip.002_R.018
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/ShirtArm_L/Hip.002_R.018/Hip.002_R.023
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/ShirtArm_L/Hip.002_R.019
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/ShirtArm_L/Hip.002_R.019/Hip.002_R.024
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/ShirtArm_L/Hip.002_R.020
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Arm_L/ShirtArm_L/Hip.002_R.021
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Sash_Shoulder
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Sash_Shoulder/Hip.002_R.025
        Scout/Armature/Hip/Mid/AimJoint/Torso/S_Shoulder_L/Sash_Shoulder/Hip.002_R.026
        Scout/Armature/Hip/Mid/AimJoint/Torso/Head
        Scout/Armature/Hip/Mid/AimJoint/Torso/Head/Face
        Scout/Armature/Hip/Mid/AimJoint/Torso/Detail
        Scout/Armature/Hip/Mid/AimJoint/Torso/Detail/Chest
        Scout/Armature/Hip/Mid/AimJoint/Torso/Detail/Chest/ChestF
        Scout/Armature/Hip/Mid/AimJoint/Torso/Detail/Chest/ChestB
        Scout/Armature/Hip/Mid/AimJoint/Torso/Detail/Chest/Collar
        Scout/Armature/Hip/Mid/AimJoint/Torso/Detail/Chest/Collar/CollarB
        Scout/Armature/Hip/Mid/AimJoint/Torso/Detail/Chest/Collar/Collar_R
        Scout/Armature/Hip/Mid/AimJoint/Torso/Detail/Chest/Collar/Collar_R/Collar_R.001
        Scout/Armature/Hip/Mid/AimJoint/Torso/Detail/Chest/Collar/Collar_L
        Scout/Armature/Hip/Mid/AimJoint/Torso/Detail/Chest/Collar/Collar_L/Collar_L.001
        Scout/Armature/Hip/Mid/AimJoint/Torso/Detail/Sash_F
        Scout/Armature/Hip/Mid/AimJoint/Torso/Detail/Sash_B
        Scout/Armature/Hip/Mid/AimJoint/Torso/Head/Hat
        Scout/Armature/Hip/Mid/AimJoint/Torso/Head/Hat/PropellerHat
        Scout/Armature/Hip/Mid/AimJoint/Torso/Head/Hat/PropellerHat/Armature
        Scout/Armature/Hip/Mid/AimJoint/Torso/Head/Hat/PropellerHat/Armature/Bone
        Scout/Armature/Hip/Mid/AimJoint/Torso/Head/Hat/PropellerHat/Armature/Bone/Bone.001
        """;
}
