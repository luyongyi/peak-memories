using System.Security.Cryptography;
using Newtonsoft.Json;
using PeakReplayLab;

// Explicit offline comparison of a user-selected immutable recording. Native
// callbacks, climbing membership and spool ownership are absent from old files;
// this tool reports those assumptions rather than calling modeled reads CPU.
internal static class CaptureLifecycleInspection
{
    private const double RangeMetres = 40, AssumedDeploymentSeconds = 5.1;
    private sealed class Entry
    {
        internal RopeCaptureLifecycle Policy = null!;
        internal RopeReplayFrame? Candidate, Source;
        internal double Birth, NextManagedCheck;
        internal Counts Counts = new();
    }
    internal sealed class Counts
    {
        public long GeometryReads, PointReads, BoneReads, ChangedSnapshots, SleepingFrames, ManagedMotionChecks,
            MotionWakes, NativeStateEvents, HighRateReads, TailReads, NearReads, FarReads;
    }
    private sealed class CandidateModel
    {
        private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Counts> retired = new(StringComparer.Ordinal);
        private RopeReplayFrame[] previous = Array.Empty<RopeReplayFrame>();
        internal readonly Counts Total = new();
        internal long DiscreteStateMismatches;
        internal IEnumerable<(string Kind, Counts Counts)> ByKind => entries.Select(p => (p.Value.Source!.Kind, p.Value.Counts))
            .Concat(retired.Select(p => (p.Key.Split('\n')[0], p.Value)));
        internal ReplayFrame Apply(ReplayFrame source)
        {
            var wanted = new HashSet<string>(source.Ropes.Select(r => r.Key), StringComparer.Ordinal);
            foreach (string key in entries.Keys.Where(key => !wanted.Contains(key)).ToArray())
            { retired[entries[key].Source!.Kind + "\n" + key] = entries[key].Counts; entries.Remove(key); }
            var next = new RopeReplayFrame[source.Ropes.Length];
            for (int i = 0; i < source.Ropes.Length; i++)
            {
                var rope = source.Ropes[i]; double now = source.T;
                if (!entries.TryGetValue(rope.Key, out var entry))
                {
                    entry = new Entry { Birth = now, Policy = new RopeCaptureLifecycle(rope.Kind is "vine" or "anchor") };
                    entries.Add(rope.Key, entry); entry.Policy.Notify(now, RopeCaptureEvent.Baseline);
                }
                else if (entry.Source != null)
                {
                    var before = entry.Source;
                    if (before.Points.Length != rope.Points.Length || before.Bones.Length != rope.Bones.Length)
                        Notify(entry, now, RopeCaptureEvent.Topology);
                    if (before.Attachment != rope.Attachment || before.CollisionKind != rope.CollisionKind)
                        Notify(entry, now, RopeCaptureEvent.Attachment);
                    if (before.Visible != rope.Visible || before.Pose.Active != rope.Pose.Active)
                        Notify(entry, now, RopeCaptureEvent.Visibility);
                }
                // Approximate the production sleeping-only <=10 Hz wake check
                // using recorded Points. The game reuses native SegmentData,
                // which is not retained in this historical recording.
                if (rope.Kind == "rope" && entry.Policy.Sleeping && now + 1e-9 >= entry.NextManagedCheck)
                {
                    entry.NextManagedCheck = now + .1; entry.Counts.ManagedMotionChecks++; Total.ManagedMotionChecks++;
                    if (entry.Candidate == null || ManagedShapeChanged(entry.Candidate.Points, rope.Points))
                    { entry.Policy.Notify(now, RopeCaptureEvent.Motion); entry.Counts.MotionWakes++; Total.MotionWakes++; }
                }
                bool action = rope.Kind == "rope" && now < entry.Birth + AssumedDeploymentSeconds;
                bool near = InRange(entry.Candidate ?? rope, source.Actors);
                if (entry.Policy.ShouldRead(now, action, near))
                {
                    bool changed = entry.Candidate == null || !ShapeEqual(entry.Candidate, rope);
                    entry.Counts.GeometryReads++; Total.GeometryReads++;
                    entry.Counts.PointReads += rope.Points.Length; Total.PointReads += rope.Points.Length;
                    entry.Counts.BoneReads += rope.Bones.Length; Total.BoneReads += rope.Bones.Length;
                    if (changed)
                    { entry.Candidate = WithClock(rope, now); entry.Counts.ChangedSnapshots++; Total.ChangedSnapshots++; }
                    entry.Policy.Observed(now, changed, action, near);
                    if (entry.Policy.LastRateHz >= 60) { entry.Counts.HighRateReads++; Total.HighRateReads++; }
                    else if (entry.Policy.LastRateHz != 0) { entry.Counts.TailReads++; Total.TailReads++; }
                    if (near) { entry.Counts.NearReads++; Total.NearReads++; } else { entry.Counts.FarReads++; Total.FarReads++; }
                }
                if (entry.Policy.Sleeping) { entry.Counts.SleepingFrames++; Total.SleepingFrames++; }
                entry.Source = rope;
                next[i] = entry.Candidate ?? throw new InvalidOperationException("Candidate has no initial checkpoint.");
                if (!SameDiscrete(rope, next[i])) DiscreteStateMismatches++;
            }
            bool same = previous.Length == next.Length;
            if (same) for (int i = 0; i < next.Length; i++) if (!ReferenceEquals(previous[i], next[i])) { same = false; break; }
            if (!same) previous = next;
            return WithRopes(source, previous);
        }
        private void Notify(Entry entry, double now, RopeCaptureEvent kind)
        { entry.Policy.Notify(now, kind); entry.Counts.NativeStateEvents++; Total.NativeStateEvents++; }
    }
    private sealed class OldModel
    {
        private sealed class OldEntry
        {
            internal RopeReplayFrame? Last, Source;
            internal double HotUntil, NextProbe, NextHotSample;
        }
        private readonly Dictionary<string, OldEntry> entries = new(StringComparer.Ordinal);
        private readonly double spread;
        internal Counts Total = new();
        internal OldModel(double spread) => this.spread = spread;
        internal void Observe(ReplayFrame source)
        {
            foreach (var rope in source.Ropes)
            {
                double now = source.T;
                if (!entries.TryGetValue(rope.Key, out var entry))
                    entries.Add(rope.Key, entry = new OldEntry { HotUntil = now + 1 });
                bool dirty = entry.Source != null && !SameDiscrete(entry.Source, rope);
                entry.Source = rope;
                if (rope.SceneVine && entry.Last != null && !dirty && now < entry.NextHotSample) continue;
                if (entry.Last != null && !dirty && now > entry.HotUntil && now < entry.NextProbe) continue;
                Total.GeometryReads++; Total.PointReads += rope.Points.Length; Total.BoneReads += rope.Bones.Length;
                entry.NextProbe = now + (rope.SceneVine ? 1 : .5) + spread; entry.NextHotSample = now + 1d / 30;
                if (entry.Last == null || !ShapeEqual(entry.Last, rope))
                { Total.ChangedSnapshots++; entry.Last = rope; entry.HotUntil = Math.Max(entry.HotUntil, now + .5); }
            }
        }
    }
    private sealed class ErrorValues
    {
        internal readonly List<double> Position = new(), Rotation = new(), Scale = new();
        internal long VisibleBoneSamples, DiscreteMismatches, ZeroScaleMaskMismatches, MissingBrackets;
        internal object Summary() => new { VisibleBoneSamples, DiscreteMismatches, ZeroScaleMaskMismatches, MissingBrackets,
            PositionMetres = Distribution(Position), RotationDegrees = Distribution(Rotation), ScaleVectorNorm = Distribution(Scale) };
    }
    private sealed class JointNormalizationAudit
    {
        private readonly HashSet<(double Frame, int Actor, int Joint)> seen = new();
        private readonly HashSet<(int Actor, int Joint, double Clock)> samples = new();
        private readonly List<object> examples = new();
        internal long ChangedQuaternionObservations, ChangedScalarValues, SignOnlyObservations;
        internal double MaximumDegrees, MaximumSignAlignedScalarDelta;
        internal ReplayFrame Normalize(ReplayFrame source, ReplayFrame baseline)
        {
            Check(source.Actors.Length == baseline.Actors.Length, "Actor count changed during source rewrite.");
            ActorFrame[]? actors = null;
            for (int a = 0; a < source.Actors.Length; a++)
            {
                var original = source.Actors[a]; var rewritten = baseline.Actors[a];
                Check(original.JointPose.Length == rewritten.JointPose.Length, "Actor joint count changed during source rewrite.");
                NodePose[]? nodes = null, codecRoundTrip = null;
                for (int j = 0; j < original.JointPose.Length; j++)
                {
                    var before = original.JointPose[j]; var after = rewritten.JointPose[j];
                    if (ValuesEqual(before.Rotation, after.Rotation)) continue;
                    codecRoundTrip ??= ActorJointCodec.Decode(ActorJointCodec.Encode(original.JointPose, null), null);
                    Check(ValuesEqual(codecRoundTrip[j].Rotation, after.Rotation),
                        "Unexpected actor rotation change: not the production joint codec round trip at t=" + source.T + ", actor=" + a + ", joint=" + j);
                    double degrees = Angle(before.Rotation, after.Rotation);
                    Check(double.IsFinite(degrees) && degrees < .02,
                        "Joint codec orientation change exceeds explicit 0.02 degree diagnostic bound.");
                    if (seen.Add((source.T, a, j)))
                    {
                        ChangedQuaternionObservations++; samples.Add((a, j, before.SampleTime));
                        MaximumDegrees = Math.Max(MaximumDegrees, degrees);
                        double dot = 0; bool signOnly = true;
                        for (int k = 0; k < 4; k++) dot += (double)before.Rotation[k] * after.Rotation[k];
                        double sign = dot < 0 ? -1 : 1;
                        for (int k = 0; k < 4; k++)
                        {
                            if (BitConverter.SingleToInt32Bits(before.Rotation[k]) != BitConverter.SingleToInt32Bits(after.Rotation[k])) ChangedScalarValues++;
                            double delta = Math.Abs(before.Rotation[k] - sign * after.Rotation[k]);
                            MaximumSignAlignedScalarDelta = Math.Max(MaximumSignAlignedScalarDelta, delta);
                            if (delta != 0) signOnly = false;
                        }
                        if (signOnly) SignOnlyObservations++;
                        if (examples.Count < 12) examples.Add(new { FrameTime = source.T, ActorIndex = a, JointIndex = j,
                            JointSampleTime = before.SampleTime, Before = before.Rotation, After = after.Rotation, Degrees = degrees, SignOnly = signOnly });
                    }
                    nodes ??= (NodePose[])original.JointPose.Clone();
                    nodes[j] = new NodePose(before.Path, before.Position, after.Rotation, before.Scale, before.Active, before.Visible, before.SampleTime);
                }
                if (nodes == null) continue;
                actors ??= (ActorFrame[])source.Actors.Clone(); actors[a] = original.WithJoints(nodes);
            }
            if (actors == null) return source;
            var normalized = WithRopes(source, source.Ropes); normalized.Actors = actors; return normalized;
        }
        internal object Summary() => new { ChangedQuaternionObservations, ChangedScalarValues, DistinctJointClockSamples = samples.Count,
            SignOnlyObservations, MaximumDegrees, MaximumSignAlignedScalarDelta, ProductionCodecRoundTripVerified = true,
            ExplicitMaximumAcceptedDegrees = .02, Examples = examples.ToArray() };
    }
    internal static void Run(string sourcePath, string newOutput)
    {
        sourcePath = Path.GetFullPath(sourcePath); newOutput = Path.GetFullPath(newOutput);
        if (Directory.Exists(newOutput) || File.Exists(newOutput)) throw new IOException("Comparison destination must not exist.");
        string originalHash = Hash(sourcePath); var sourceInfo = FullReplayArchive.ReadInfo(sourcePath);
        if (!sourceInfo.Complete) throw new InvalidDataException("Comparison requires a complete source recording.");
        Directory.CreateDirectory(newOutput);
        var sourceLoader = FullReplayArchive.CreatePageLoader(sourcePath, sourceInfo);
        var header = JsonConvert.DeserializeObject<ReplayHeader>(JsonConvert.SerializeObject(sourceInfo.Header))!;
        header.Schema = ReplayRules.CurrentSchema; header.Recorder = "PeakReplayLab/offline-lifecycle-comparison";
        var baseline = new FullReplayWriter(Path.Combine(newOutput, "baseline"), header);
        var candidate = new FullReplayWriter(Path.Combine(newOutput, "candidate"), header);
        var model = new CandidateModel(); var oldLower = new OldModel(0); var oldUpper = new OldModel(.527);
        double last = double.NegativeInfinity; long unique = 0;
        try
        {
            for (int p = 0; p < sourceInfo.Pages.Length; p++)
            {
                var page = sourceLoader(p, CancellationToken.None);
                foreach (var frame in page.Frames)
                {
                    if (frame.T <= last) continue;
                    last = frame.T; unique++;
                    oldLower.Observe(frame); oldUpper.Observe(frame);
                    var modeled = model.Apply(frame);
                    Enqueue(baseline, frame); Enqueue(candidate, modeled);
                }
                if (p % 20 == 0) Console.WriteLine("CAPTURE-LIFECYCLE writingPage=" + p + " uniqueFrames=" + unique);
            }
            var baselineResult = baseline.CompleteAsync("offline-lifecycle-comparison").GetAwaiter().GetResult();
            var candidateResult = candidate.CompleteAsync("offline-lifecycle-comparison").GetAwaiter().GetResult();
            Complete(baselineResult, unique); Complete(candidateResult, unique);
            VerifyOutputs(sourcePath, newOutput, originalHash, sourceInfo, baselineResult.FilePath,
                candidateResult.FilePath, model, oldLower, oldUpper, unique, "comparison");
        }
        catch (Exception e)
        {
            if (!baseline.Completion.IsCompleted) baseline.InterruptAsync("offline-inspection-failed", e).GetAwaiter().GetResult();
            if (!candidate.Completion.IsCompleted) candidate.InterruptAsync("offline-inspection-failed", e).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(newOutput, "failure.json"), JsonConvert.SerializeObject(new { Error = e.ToString(), SourceUnmodified = Hash(sourcePath) == originalHash }, Formatting.Indented));
            throw;
        }
    }
    internal static void VerifyExisting(string sourcePath, string existingOutput)
    {
        sourcePath = Path.GetFullPath(sourcePath); existingOutput = Path.GetFullPath(existingOutput);
        string originalHash = Hash(sourcePath);
        var sourceInfo = FullReplayArchive.ReadInfo(sourcePath);
        Check(sourceInfo.Complete, "Comparison requires a complete source recording.");
        string baselinePath = Directory.GetFiles(Path.Combine(existingOutput, "baseline"), "*.peakrun").Single();
        string candidatePath = Directory.GetFiles(Path.Combine(existingOutput, "candidate"), "*.peakrun").Single();
        string baselineHash = Hash(baselinePath), candidateHash = Hash(candidatePath);
        var model = new CandidateModel(); var oldLower = new OldModel(0); var oldUpper = new OldModel(.527);
        double last = double.NegativeInfinity; long unique = 0;
        var loader = FullReplayArchive.CreatePageLoader(sourcePath, sourceInfo);
        for (int p = 0; p < sourceInfo.Pages.Length; p++)
        {
            foreach (var frame in loader(p, CancellationToken.None).Frames)
            {
                if (frame.T <= last) continue;
                last = frame.T; unique++; oldLower.Observe(frame); oldUpper.Observe(frame); model.Apply(frame);
            }
        }
        VerifyOutputs(sourcePath, existingOutput, originalHash, sourceInfo, baselinePath, candidatePath,
            model, oldLower, oldUpper, unique, "verification");
        Check(baselineHash == Hash(baselinePath) && candidateHash == Hash(candidatePath), "Read-only verification modified a comparison recording.");
    }
    private static void VerifyOutputs(string sourcePath, string newOutput, string originalHash, FullReplayInfo sourceInfo,
        string baselinePath, string candidatePath, CandidateModel model, OldModel oldLower, OldModel oldUpper, long unique, string reportStem)
    {
        var sourceLoader = FullReplayArchive.CreatePageLoader(sourcePath, sourceInfo);
    var baselineInfo = FullReplayArchive.ReadInfo(baselinePath);
        var candidateInfo = FullReplayArchive.ReadInfo(candidatePath);
        var baselineLoader = FullReplayArchive.CreatePageLoader(baselinePath, baselineInfo);
        var candidateLoader = FullReplayArchive.CreatePageLoader(candidatePath, candidateInfo);
        Check(sourceInfo.Pages.Length == baselineInfo.Pages.Length && baselineInfo.Pages.Length == candidateInfo.Pages.Length,
            "Comparison page boundaries changed; verification requires time-mapped page lookup.");
        var sourceEffects = FullReplayArchive.CreateEffectPageLoader(sourceInfo);
        var baselineEffects = FullReplayArchive.CreateEffectPageLoader(baselineInfo);
        var candidateEffects = FullReplayArchive.CreateEffectPageLoader(candidateInfo);
        var errors = new ErrorValues(); long compared = 0, historyCompared = 0;
        var normalization = new JointNormalizationAudit();
        var verifiedTimes = new HashSet<double>();
        for (int p = sourceInfo.Pages.Length - 1; p >= 0; p--)
        {
            var source = sourceLoader(p, CancellationToken.None); var old = baselineLoader(p, CancellationToken.None);
            var current = candidateLoader(p, CancellationToken.None);
            Check(source.Frames.Count == old.Frames.Count && old.Frames.Count == current.Frames.Count, "Page frame count changed.");
            var comparer = new ContinuousReplayInspection.ImmutableDataComparer();
            var builder = new RopeObservationTimeline.Builder();
            foreach (var f in current.Frames) builder.Observe(f);
            if (p + 1 < candidateInfo.Pages.Length)
                foreach (var f in candidateLoader(p + 1, CancellationToken.None).Frames) builder.Observe(f);
            var clock = builder.Build(p + 1 >= candidateInfo.Pages.Length);
            for (int i = 0; i < source.Frames.Count; i++)
            {
                var expected = source.Frames[i]; var a = old.Frames[i]; var b = current.Frames[i];
                Check(expected.T == a.T && a.T == b.T, "Frame timestamps changed.");
                var normalized = normalization.Normalize(expected, a);
                Check(comparer.Equal(normalized, a), "Source-to-baseline changed a field outside verified joint quaternion codec normalization at t=" + expected.T);
                Check(comparer.Equal(WithRopes(a, Array.Empty<RopeReplayFrame>()), WithRopes(b, Array.Empty<RopeReplayFrame>())),
                    "Non-rope data changed at t=" + expected.T);
                Check(a.Ropes.Length == b.Ropes.Length, "Rope presence changed.");
                if (verifiedTimes.Add(expected.T)) { compared++; Measure(expected, clock, errors); }
            }
            var sh = sourceEffects(p, CancellationToken.None); var ah = baselineEffects(p, CancellationToken.None); var bh = candidateEffects(p, CancellationToken.None);
            Check(sh.Frames.Count == ah.Frames.Count && ah.Frames.Count == bh.Frames.Count, "Independent effect history count changed.");
            for (int i = 0; i < sh.Frames.Count; i++)
            {
                Check(sh.Frames[i].T == ah.Frames[i].T && ah.Frames[i].T == bh.Frames[i].T &&
                    comparer.Equal(sh.Frames[i].Effects, ah.Frames[i].Effects) && comparer.Equal(sh.Frames[i].Effects, bh.Frames[i].Effects),
                    "Independent effect history changed."); historyCompared++;
            }
            if (p % 20 == 0) Console.WriteLine("CAPTURE-LIFECYCLE verifiedReversePage=" + p + " uniqueFrames=" + compared);
        }
        var random = new Random(181012); long randomSamples = 0;
        for (int n = 0; n < 32; n++)
        {
            int p = random.Next(candidateInfo.Pages.Length);
            var page = candidateLoader(p, CancellationToken.None); var expected = sourceLoader(p, CancellationToken.None);
            var b = new RopeObservationTimeline.Builder(); foreach (var f in page.Frames) b.Observe(f);
            if (p + 1 < candidateInfo.Pages.Length) foreach (var f in candidateLoader(p + 1, CancellationToken.None).Frames) b.Observe(f);
            var index = b.Build(p + 1 >= candidateInfo.Pages.Length);
            foreach (int i in new[] { 0, page.Frames.Count / 2, page.Frames.Count - 1 })
            {
                Check(index.CanSample(page.Frames[i].Ropes, expected.Frames[i].T), "Seek lacked a bounded successor bracket.");
                foreach (var rope in expected.Frames[i].Ropes)
                    Check(index.TrySample(rope.Key, expected.Frames[i].T, out _, out _, out _), "Seek required rope birth history.");
                randomSamples++;
            }
        }
        Check(compared == unique && unique == sourceInfo.FrameCount, "Some unique source frames were not verified.");
        string afterHash = Hash(sourcePath); Check(originalHash == afterHash, "Source recording was modified.");
        long oldBytes = new FileInfo(baselinePath).Length, newBytes = new FileInfo(candidatePath).Length;
        var byKind = model.ByKind.GroupBy(x => x.Kind).Select(g => new { Kind = g.Key, Counts = Add(g.Select(x => x.Counts)) }).ToArray();
        var result = new
        {
            SourceFile = Path.GetFileName(sourcePath), SourceBytes = new FileInfo(sourcePath).Length, SourceSha256Before = originalHash,
            SourceSha256After = afterHash, SourceUnmodified = true, SourceSchema = sourceInfo.Header.Schema, OutputSchema = ReplayRules.CurrentSchema,
            SourceSeconds = sourceInfo.Duration, SourceFrames = unique, SourcePages = sourceInfo.Pages.Length,
            BaselineFile = baselinePath, CandidateFile = candidatePath, BaselineBytes = oldBytes, CandidateBytes = newBytes,
            SavedBytes = oldBytes - newBytes, FileReductionPercent = 100d * (oldBytes - newBytes) / oldBytes,
            BaselineWritten = baselineInfo.FrameCount, CandidateWritten = candidateInfo.FrameCount,
            NonRopeFieldsExact = true, BaselineAllFieldsExact = normalization.ChangedQuaternionObservations == 0,
            BaselineOtherFieldsExact = true, SourceJointQuaternionNormalization = normalization.Summary(), VerifiedUniqueFrames = compared,
            ReverseIndependentPagesVerified = sourceInfo.Pages.Length, RandomIndependentPagesVerified = 32, RandomSeekSamplesVerified = randomSamples,
            IndependentEffectHistoryFramesVerified = historyCompared,
            SourceDiscreteStateMismatches = model.DiscreteStateMismatches,
            OldModeledNativeReadsSpreadZero = oldLower.Total, OldModeledNativeReadsSpreadPoint527 = oldUpper.Total,
            CandidateModeledNativeReads = model.Total, CandidateByKind = byKind, VisibleBoneError = errors.Summary(),
            Assumptions = new[]
            {
                "Offline resampling estimate of the recorded trace, not a new gameplay capture or Unity CPU benchmark.",
                "Both output files use the identical current production FullReplayWriter and all original unique frame times.",
                "Only candidate ropes change. Between the same-writer baseline and candidate, all non-rope tracks including actor joint clocks and rotations and independent effect histories are verified exactly.",
                "Source-to-baseline permits only rotations proven equal to the production ActorJointCodec round trip, with <0.02 degree orientation change; every other source field is exact. Quaternion sign canonicalization and repeated quantization are reported separately.",
                "Native callback times, charactersClimbing, spool ownership and antigravity mode were not recorded and cannot be recovered.",
                "For this trace only, each ordinary rope birth gets an assumed 5.1-second deployment action, conservatively covering observed ~5.002-second growth.",
                "Topology, attachment, visibility, active and collision-kind changes simulate native semantic events; pose changes do not reopen bursts.",
                "Distance is nearest cached candidate root/Points to recorded actor hip positions with assumed 40 m capture range.",
                "Static vine/anchor baselines sleep immediately. Other unrecorded native actions are not inferred from actor climbing strings.",
                "Sleeping ordinary ropes approximate native <=10 Hz managed SegmentData wake checks using recorded collider Points position/rotation thresholds.",
                "Old modeled reads reproduce birth 1 s heat, changed+0.5 s, cold .5 s (scene vines 1 s), with unknown identity spread modeled at 0 and .527 s.",
                "Written updates are not all native reads. Work counters are model counts, not measured main-thread milliseconds.",
                "Only rope scheduling savings are included; item/interaction lifecycle optimizations are deliberately excluded.",
                "Visible nonzero-scale bones are a relevance proxy, not mesh-weight or screen-space visual acceptance. Nonzero interpolation error prevents a no-motion-loss claim.",
            },
        };
        File.WriteAllText(Path.Combine(newOutput, reportStem + ".json"), JsonConvert.SerializeObject(result, Formatting.Indented));
        File.WriteAllText(Path.Combine(newOutput, reportStem + ".md"),
            "# 采集生命周期离线对照\n\n" +
            $"原录像 {unique} 帧、{sourceInfo.Duration:F3} 秒；同一当前 writer 重写基线 {oldBytes:N0} B，绳索新策略候选 {newBytes:N0} B，减少 {100d*(oldBytes-newBytes)/oldBytes:F2}%。\n\n" +
            $"候选模型完整绳几何读取 {model.Total.GeometryReads:N0} 次，点读取 {model.Total.PointReads:N0}，骨骼读取 {model.Total.BoneReads:N0}；旧模型未知实例错峰端点分别 {oldLower.Total.GeometryReads:N0} / {oldUpper.Total.GeometryReads:N0} 次。写入更新与几何读取分别统计。\n\n" +
            $"同 writer 基线/候选所有非绳轨道、人物骨骼及采样时钟逐位一致；源→基线除已验证的骨骼四元数 codec 规范化外全字段一致（{normalization.ChangedQuaternionObservations:N0} 个观测，最大朝向误差 {normalization.MaximumDegrees:F6}°）；{sourceInfo.Pages.Length} 页逆序独立读取及 32 个随机页共 {randomSamples} 次抽样通过；独立特效历史 {historyCompared:N0} 帧逐位一致。原录像 SHA-256 前后一致。\n\n" +
            $"离散段数/挂接/显示/碰撞状态差异 {model.DiscreteStateMismatches}；插值可见骨骼误差、零缩放状态差异详见 JSON。\n\n" +
            "这是缺少原生回调、攀爬成员及放绳持有状态的离线估算：普通绳出生前 5.1 秒保守视为部署动作，40 m 距离来自记录角色位置，睡眠唤醒由原始 Points 代理游戏已读 SegmentData；无法替代实机验收。仅计绳索收益，不代表其它物品或游戏 CPU 实测。固定降频不能据此保证不丢动作。\n");
        Console.WriteLine("CAPTURE-LIFECYCLE complete frames=" + unique + " baselineBytes=" + oldBytes + " candidateBytes=" + newBytes + " sourceUnmodified=true");
    }
    private static void Enqueue(FullReplayWriter writer, ReplayFrame frame)
    {
        long bytes = RollingBuffer.Estimate(frame); var deadline = DateTime.UtcNow.AddSeconds(60);
        while (writer.Stats.QueuedFrames >= 4 || writer.Stats.QueuedBytes + bytes > 128L * 1024 * 1024)
        {
            if (writer.Fault != null) throw writer.Fault;
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Offline writer stalled.");
            Thread.Sleep(1);
        }
        if (!writer.TryEnqueue(frame, bytes)) throw new IOException("Offline enqueue failed: " + writer.FailureCode, writer.Fault);
    }
    private static void Complete(FullReplayResult result, long frames) => Check(result.Status == "completed" && result.WrittenFrames == frames && result.UnwrittenFrames == 0,
        "Offline writer failed: " + result.FaultCode + " " + result.Fault);
    private static string Hash(string path) { using var file = File.OpenRead(path); using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(file)); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static ReplayFrame WithRopes(ReplayFrame f, RopeReplayFrame[] ropes) => new()
    { Type = f.Type, T = f.T, World = f.World, Actors = f.Actors, Items = f.Items, Crates = f.Crates, Events = f.Events, Ropes = ropes,
        Effects = f.Effects, Audio = f.Audio, Spawned = f.Spawned, Balloons = f.Balloons, Creatures = f.Creatures, Interactions = f.Interactions,
        CoverPeakExtraction = f.CoverPeakExtraction, CoverNadirEntered = f.CoverNadirEntered };
    private static RopeReplayFrame WithClock(RopeReplayFrame r, double now) => new()
    { SampleTimeKnown = true, SampleTime = now, Key = r.Key, Kind = r.Kind, Resource = r.Resource, SourcePath = r.SourcePath, Pose = r.Pose,
        Visible = r.Visible, Attachment = r.Attachment, CollisionKind = r.CollisionKind, Points = r.Points, Bones = r.Bones,
        Width = r.Width, Cutoff = r.Cutoff, Hang = r.Hang, LengthScale = r.LengthScale, Jitter = r.Jitter, BreakAmount = r.BreakAmount, SceneVine = r.SceneVine };
    private static bool SameDiscrete(RopeReplayFrame a, RopeReplayFrame b) => a.Key == b.Key && a.Kind == b.Kind && a.Visible == b.Visible &&
        a.Pose.Active == b.Pose.Active && a.Attachment == b.Attachment && a.CollisionKind == b.CollisionKind && a.Points.Length == b.Points.Length && a.Bones.Length == b.Bones.Length;
    private static bool ShapeEqual(RopeReplayFrame a, RopeReplayFrame b) => ReferenceEquals(a, b) || SameDiscrete(a,b) &&
        a.Resource == b.Resource && a.SourcePath == b.SourcePath && a.Width == b.Width && a.Cutoff == b.Cutoff && a.Hang == b.Hang &&
        a.LengthScale == b.LengthScale && a.Jitter == b.Jitter && a.BreakAmount == b.BreakAmount && a.SceneVine == b.SceneVine &&
        PoseEqual(a.Pose,b.Pose) && PointsEqual(a.Points,b.Points) && PointsEqual(a.Bones,b.Bones);
    private static bool PoseEqual(ObjectPose a, ObjectPose b)
    {
        if (ReferenceEquals(a,b)) return true;
        if (a.Active != b.Active || !ValuesEqual(a.Position,b.Position) || !ValuesEqual(a.Rotation,b.Rotation) || !ValuesEqual(a.Scale,b.Scale) || a.Nodes.Length != b.Nodes.Length) return false;
        for (int i=0; i<a.Nodes.Length; i++)
        { var x=a.Nodes[i]; var y=b.Nodes[i]; if (x.Path != y.Path || x.Active != y.Active || x.Visible != y.Visible || !ValuesEqual(x.Position,y.Position) || !ValuesEqual(x.Rotation,y.Rotation) || !ValuesEqual(x.Scale,y.Scale)) return false; }
        return true;
    }
    private static bool PointsEqual(RopeReplayPoint[] a, RopeReplayPoint[] b)
    {
        if (ReferenceEquals(a,b)) return true; if (a.Length != b.Length) return false;
        for (int i=0; i<a.Length; i++) if (!ReferenceEquals(a[i],b[i]) && (a[i].Radius != b[i].Radius || a[i].Height != b[i].Height ||
            !ValuesEqual(a[i].Position,b[i].Position) || !ValuesEqual(a[i].Rotation,b[i].Rotation) || !ValuesEqual(a[i].Scale,b[i].Scale))) return false;
        return true;
    }
    private static bool ValuesEqual(float[] a, float[] b)
    { if (ReferenceEquals(a,b)) return true; if(a.Length!=b.Length) return false; for(int i=0;i<a.Length;i++) if(BitConverter.SingleToInt32Bits(a[i])!=BitConverter.SingleToInt32Bits(b[i]))return false; return true; }
    private static bool ManagedShapeChanged(RopeReplayPoint[] a, RopeReplayPoint[] b)
    {
        if(a.Length!=b.Length)return true;
        for(int i=0;i<a.Length;i++)
        {
            for(int j=0;j<3;j++)if(Math.Abs(a[i].Position[j]-b[i].Position[j])>.001f)return true;
            double dot=0; for(int j=0;j<4;j++)dot+=(double)a[i].Rotation[j]*b[i].Rotation[j]; if(Math.Abs(dot)<=.999999)return true;
        }
        return false;
    }
    private static bool InRange(RopeReplayFrame rope, ActorFrame[] actors)
    {
        foreach(var actor in actors)
        { if(DistanceSquared(rope.Pose.Position,actor.Position)<=RangeMetres*RangeMetres)return true;
            foreach(var point in rope.Points)if(DistanceSquared(point.Position,actor.Position)<=RangeMetres*RangeMetres)return true; }
        return false;
    }
    private static double DistanceSquared(float[] a,float[] b) { double x=(double)a[0]-b[0],y=(double)a[1]-b[1],z=(double)a[2]-b[2]; return x*x+y*y+z*z; }
    private static Counts Add(IEnumerable<Counts> counts)
    {
        var r=new Counts(); foreach(var c in counts)
        { r.GeometryReads+=c.GeometryReads; r.PointReads+=c.PointReads; r.BoneReads+=c.BoneReads; r.ChangedSnapshots+=c.ChangedSnapshots;
            r.SleepingFrames+=c.SleepingFrames; r.ManagedMotionChecks+=c.ManagedMotionChecks; r.MotionWakes+=c.MotionWakes;
            r.NativeStateEvents+=c.NativeStateEvents; r.HighRateReads+=c.HighRateReads; r.TailReads+=c.TailReads; r.NearReads+=c.NearReads; r.FarReads+=c.FarReads; }
        return r;
    }
    private static bool Zero(float[] scale) => scale.All(x=>Math.Abs(x)<=.0001f);
    private static void Measure(ReplayFrame source,RopeObservationTimeline clock,ErrorValues errors)
    {
        foreach(var rope in source.Ropes)
        {
            if(!clock.TrySample(rope.Key,source.T,out var left,out var right,out float mix)) { errors.MissingBrackets++; continue; }
            if(!SameDiscrete(rope,left))errors.DiscreteMismatches++;
            if(rope.Kind!="rope" || !rope.Visible || rope.Bones.Length!=left.Bones.Length || left.Bones.Length!=right.Bones.Length)continue;
            for(int i=0;i<rope.Bones.Length;i++)
            {
                var actual=rope.Bones[i]; var a=left.Bones[i]; var b=right.Bones[i];
                var p=Lerp(a.Position,b.Position,mix);var s=Lerp(a.Scale,b.Scale,mix);
                if(Zero(actual.Scale)!=Zero(s))errors.ZeroScaleMaskMismatches++;
                if(Zero(actual.Scale))continue;
                errors.VisibleBoneSamples++; errors.Position.Add(Math.Sqrt(DistanceSquared(p,actual.Position)));
                errors.Scale.Add(Math.Sqrt(DistanceSquared(s,actual.Scale))); errors.Rotation.Add(Angle(Slerp(a.Rotation,b.Rotation,mix),actual.Rotation));
            }
        }
    }
    private static float[] Lerp(float[] a,float[] b,float t) => a.Select((x,i)=>x+(b[i]-x)*t).ToArray();
    private static float[] Slerp(float[] a,float[] b,float t)
    {
        double an=Math.Sqrt(a.Sum(x=>(double)x*x)),bn=Math.Sqrt(b.Sum(x=>(double)x*x));
        var x=a.Select(v=>v/an).ToArray();var y=b.Select(v=>v/bn).ToArray();double dot=x.Select((v,i)=>v*y[i]).Sum();
        if(dot<0){for(int i=0;i<4;i++)y[i]=-y[i];dot=-dot;}
        double wa=1-t,wb=t;
        if(dot<=.9995){double theta=Math.Acos(Math.Clamp(dot,-1,1)),sin=Math.Sin(theta);wa=Math.Sin((1-t)*theta)/sin;wb=Math.Sin(t*theta)/sin;}
        var q=x.Select((v,i)=>wa*v+wb*y[i]).ToArray();double n=Math.Sqrt(q.Sum(v=>v*v));return q.Select(v=>(float)(v/n)).ToArray();
    }
    private static double Angle(float[] a,float[] b)
    {
        double an=Math.Sqrt(a.Sum(x=>(double)x*x)),bn=Math.Sqrt(b.Sum(x=>(double)x*x)),minus=0,plus=0;
        for(int i=0;i<4;i++){double x=a[i]/an,y=b[i]/bn;minus+=(x-y)*(x-y);plus+=(x+y)*(x+y);}
        return 4*Math.Asin(Math.Min(1,Math.Sqrt(Math.Min(minus,plus))/2))*180/Math.PI;
    }
    private static object Distribution(List<double> values)
    {
        if(values.Count==0)return new { Count=0, Mean=0d, P95=0d, P99=0d, Max=0d };
        values.Sort();return new { Count=values.Count, Mean=values.Average(), P95=values[Math.Min(values.Count-1,(int)(values.Count*.95))],
            P99=values[Math.Min(values.Count-1,(int)(values.Count*.99))], Max=values[^1] };
    }
}
