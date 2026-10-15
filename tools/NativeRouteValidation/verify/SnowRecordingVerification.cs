using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

// Independent archive decoding in reverse page order. Native samples are
// assertions to verify, never a source from which to construct replay state.
internal sealed class SnowRecordingVerification
{
    private readonly JObject evidence;
    private readonly JObject[] samples, boundaries;
    private readonly Dictionary<long, ReplayFrame> found = new();
    private readonly HashSet<long> wanted;
    private readonly Dictionary<long, JObject[]> sampleTimes;
    private int duplicateChecks, duplicateFailures;
    private readonly double origin;
    private readonly string fakePath;
    private SnowRecordingVerification(JObject evidence, double origin)
    {
        this.evidence = evidence; this.origin = origin;
        fakePath = (string?)evidence["Summary"]?["FakePath"] ?? "";
        samples = (evidence["Samples"] as JArray ?? new()).Cast<JObject>().ToArray();
        boundaries = (evidence["Events"] as JArray ?? new()).Cast<JObject>()
            .Where(x => (string?)x["Kind"] is "fake-visible-baseline" or "fake-hidden-real-held").ToArray();
        wanted = samples.Concat(boundaries).Select(x => Tick((double)x["NativeTime"]! - origin)).ToHashSet();
        sampleTimes = samples.GroupBy(x => Tick((double)x["NativeTime"]! - origin)).ToDictionary(g => g.Key, g => g.ToArray());
    }
    internal static SnowRecordingVerification? OpenIfAvailable(string summaryPath, double origin)
    {
        string path = Path.Combine(Path.GetDirectoryName(summaryPath)!, "snow-visual.json");
        return File.Exists(path) ? new(JObject.Parse(File.ReadAllText(path)), origin) : null;
    }
    internal void Observe(ReplayFrame frame)
    {
        long time = Tick(frame.T);
        if (!wanted.Contains(time)) return;
        if (!found.TryAdd(time, frame))
        {
            duplicateChecks++;
            if (frame.T != found[time].T || !JToken.DeepEquals(Witness(frame, time), Witness(found[time], time))) duplicateFailures++;
        }
    }
    internal object Complete(string output)
    {
        int missed = 0, failed = 0;
        var sampleChecks = samples.Select(sample =>
        {
            long time = Tick((double)sample["NativeTime"]! - origin);
            found.TryGetValue(time, out var frame);
            string key = (string)sample["Key"]!;
            var item = frame?.Items.SingleOrDefault(x => x.Key == key);
            var mesh = item?.Pose.Nodes.SingleOrDefault(x => x.Path == (string?)sample["MeshPath"]);
            var collider = item?.Pose.Nodes.SingleOrDefault(x => x.Path == (string?)sample["ColliderPath"]);
            var fake = frame?.Interactions.SingleOrDefault(x => x.Kind == "fake-item" && x.SourcePath == fakePath);
            bool exists = frame != null && item != null && mesh != null && collider != null;
            if (!exists) missed++;
            bool expected = exists && Math.Abs(frame!.T + origin - (double)sample["NativeTime"]!) <= .000002 && Same(mesh!.Scale, sample["RecordedMeshScale"]) &&
                Same(collider!.Scale, sample["RecordedColliderScale"]) && Same(item!.Pose.Scale, sample["RootScale"]) &&
                item.State == (int)sample["RecordedState"]! && item.SampleTimeKnown == (bool)sample["SampleTimeKnown"]! &&
                (!item.SampleTimeKnown || Math.Abs(item.SampleTime + origin - (double)sample["SampleTime"]!) <= .000002) &&
                item.ClockMode == (int)sample["ClockMode"]! && fake != null &&
                sample["FakeRecordedActive"]?.Type == JTokenType.Boolean && fake.Pose.Active == (bool)sample["FakeRecordedActive"]!;
            bool native = exists && Same(mesh!.Scale, sample["NativeAppliedMeshScale"], .0005f) &&
                Same(collider!.Scale, sample["NativeAppliedColliderScale"], .0005f) && Same(item!.Pose.Scale, sample["NativeRootScale"], .0005f) &&
                item.State == (int)sample["NativeStateCode"]!;
            bool nativeExpectation = native == (bool)sample["Matches"]!;
            bool nativeFinite = Finite(sample["NativeAppliedMeshScale"]) && Finite(sample["NativeAppliedColliderScale"]) && Finite(sample["NativeRootScale"]);
            bool passed = expected && nativeExpectation && nativeFinite;
            if (!passed) failed++;
            return new { NativeTime = (double)sample["NativeTime"]!, RecordingTime = time / 1e6,
                Stage = (string?)sample["Stage"], Key = key, CapturedFrameFound = frame != null,
                RecordedGeometryAndStateEqual = expected, NativeAppliedShapeEqual = native,
                NativeMatchAgreesWithLiveAssertion = nativeExpectation, NativeScalesFinite = nativeFinite, Passed = passed };
        }).ToArray();
        var boundaryChecks = boundaries.Select(boundary =>
        {
            found.TryGetValue(Tick((double)boundary["NativeTime"]! - origin), out var frame);
            bool expected = (string?)boundary["Kind"] == "fake-visible-baseline";
            var fake = frame?.Interactions.SingleOrDefault(x => x.Kind == "fake-item" && x.SourcePath == fakePath);
            bool passed = fake != null && fake.Pose.Active == expected;
            if (!passed) failed++;
            return new { Kind = (string?)boundary["Kind"], ExpectedActive = expected, RecordedActive = fake?.Pose.Active, Passed = passed };
        }).ToArray();
        bool nativeSuccess = (bool?)evidence["Summary"]?["Success"] == true;
        int GrowthCount(Func<string?, bool> stage)
        {
            int count = 0; float prior = float.NaN;
            foreach (var sample in samples.OrderBy(x => (double)x["NativeTime"]!))
            {
                if (!stage((string?)sample["Stage"]) || (bool?)sample["Matches"] != true || sample["RecordedMeshScale"] is not JArray scale) continue;
                float current = (float)scale[0];
                if (float.IsFinite(prior) && current > prior + .0005f) count++;
                prior = current;
            }
            return count;
        }
        int first = GrowthCount(stage => stage is "GroundBirth" or "FirstPush");
        int second = GrowthCount(stage => stage == "SecondPush");
        var report = new { Passed = nativeSuccess && missed == 0 && failed == 0 && duplicateFailures == 0 && samples.Length > 0 && boundaryChecks.Length == 2 && first >= 2 && second >= 2,
            NativeSummarySuccess = nativeSuccess, SampleCount = samples.Length, MissingSamples = missed, FailedChecks = failed,
            DuplicatePageCopiesChecked = duplicateChecks, DuplicatePageCopyFailures = duplicateFailures,
            FirstPushNativeMatches = first, SecondPushNativeMatches = second, BoundaryChecks = boundaryChecks, Samples = sampleChecks,
            Method = "Final archive was independently decoded in reverse page order. Exact accepted frame times, item identities, child/root scales, state, observation clocks and static pickup visibility are compared against live assertions. Native mismatches may exist between 5 Hz observations; they must agree with the recorded live mismatch result. This is archive evidence, not a screenshot or a CPU measurement." };
        File.WriteAllText(Path.Combine(output, "snow-recording-verification.json"), JsonConvert.SerializeObject(report, Formatting.Indented));
        return report;
    }
    private static long Tick(double time) => checked((long)Math.Round(time * 1e6));
    private JToken Witness(ReplayFrame frame, long time)
    {
        var keys = sampleTimes.TryGetValue(time, out var requested) ? requested.Select(x => (string)x["Key"]!).ToHashSet(StringComparer.Ordinal) : new HashSet<string>();
        return JToken.FromObject(new
        {
            Items = frame.Items.Where(x => keys.Contains(x.Key)).Select(x => new { x.Key, x.State, x.SampleTimeKnown, x.SampleTime, x.ClockMode, x.SampleDiscontinuity, x.Pose }),
            Fake = frame.Interactions.SingleOrDefault(x => x.Kind == "fake-item" && x.SourcePath == fakePath),
        });
    }
    private static bool Same(float[] value, JToken? expected, float tolerance = .000002f)
    {
        if (expected is not JArray array || array.Count != value.Length) return false;
        for (int i = 0; i < value.Length; i++) if (!float.IsFinite(value[i]) || !float.IsFinite((float)array[i]) || Math.Abs(value[i] - (float)array[i]) > tolerance) return false;
        return true;
    }
    private static bool Finite(JToken? value) => value is JArray array && array.Count == 3 && array.All(x => float.IsFinite((float)x));
}
