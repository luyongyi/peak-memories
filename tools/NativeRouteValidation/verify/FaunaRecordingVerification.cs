using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class FaunaRecordingVerification
{
    internal sealed class Result
    {
        public bool Passed { get; init; }
        public bool FlightDescriptorPassed { get; init; }
        public bool FeatherEventPassed { get; init; }
        public bool WormInteractionRequired { get; init; }
        public bool? WormInteractionPassed { get; init; }
        public bool SpiderIdleRequired { get; init; }
        public bool? SpiderIdlePassed { get; init; }
        public int AudioCandidateCount { get; init; }
        public string File { get; init; } = "fauna-recording-verification.json";
    }
    private sealed class Effect
    {
        internal string Key = "", Resource = "";
        internal double FirstFrame, Anchor, Phase, Birth;
        internal bool HasBirthClock;
    }
    private sealed class Flight
    {
        internal string Key = "";
        internal double FirstFrame, SampleTime, Anchor, ClipTime;
        internal int Frames;
    }
    private const double MaximumWormStateErrorSeconds = .35;
    private sealed class WormClip
    {
        internal string Role = "", Clip = "", Key = "", ItemSuffix = "";
        internal int ItemInstanceId, NativeRootId, Frames;
        internal double? NativeComparisonTime;
        internal double FirstFrame = double.NaN, MatchedFrame = double.NaN, MatchedObservation = double.NaN;
        internal double MatchedDistance = double.PositiveInfinity, MatchedCooked;
        internal int MatchedState, MatchedClockMode;
        internal bool MatchedClockKnown;
    }
    private sealed class WormAppearance
    {
        internal string Key = "", LastMaterialHash = "", LastTintHash = "";
        internal NativeRendererFrame[]? LastVisuals;
        internal int MaterialPropertyCount, TintCount;
        internal string UncookedMaterialHash = "", UncookedTintHash = "";
        internal int UncookedProperties, UncookedTintCount;
        internal int CookedProperties;
        internal double UncookedFrame = double.NaN, FirstChangedCookedFrame = double.NaN, ChangedCookedValue;
        internal string CookedMaterialHash = "", CookedTintHash = "";
        internal bool TintChanged;
    }
    private sealed class SpiderIdle
    {
        internal int NativeRootId, Frames, MatchedClockMode;
        internal string Prefix = "", Key = "";
        internal double? NativeComparisonTime;
        internal double FirstFrame = double.NaN, MatchedFrame = double.NaN, MatchedObservation = double.NaN,
            MatchedAnchor = double.NaN, MatchedClipTime = double.NaN, MatchedDistance = double.PositiveInfinity;
        internal bool MatchedClockKnown;
    }
    internal static Result? RunIfAvailable(string summaryPath, JObject summary, string recording, FullReplayInfo info, string output)
    {
        string visualPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(summaryPath))!, "fauna-visual.json");
        if (!File.Exists(visualPath)) return null;
        var native = JObject.Parse(File.ReadAllText(visualPath));
        double origin = (double)summary["FirstCapture"]!;
        var wormStimulus = native["WormInteractionStimulus"] as JObject;
        int originalItemId = (int?)wormStimulus?["OriginalItemId"] ?? 0, heldItemId = (int?)wormStimulus?["HeldItemId"] ?? 0;
        int originalRootId = (int?)wormStimulus?["OriginalRootId"] ?? 0, heldRootId = (int?)wormStimulus?["HeldRootId"] ?? 0;
        double pickupRequested = (double?)wormStimulus?["PickupRequestedAt"] ?? -1;
        double finishCookingRequested = (double?)wormStimulus?["FinishCookingRequestedAt"] ?? -1;
        double cookedObserved = (double?)wormStimulus?["CookedObservedAt"] ?? -1;
        string originalSuffix = ItemSuffix(originalItemId), heldSuffix = ItemSuffix(heldItemId);
        var wormClips = wormStimulus == null ? Array.Empty<WormClip>() : new[]
        {
            new WormClip { Role = "original-ground", Clip = CreatureIdleAnimationRules.EarlyWormWalk, ItemInstanceId = originalItemId, NativeRootId = originalRootId },
            new WormClip { Role = "held-uncooked", Clip = CreatureIdleAnimationRules.EarlyWormInHand, ItemInstanceId = heldItemId, NativeRootId = heldRootId },
            new WormClip { Role = "held-cooked", Clip = CreatureIdleAnimationRules.EarlyWormCooked, ItemInstanceId = heldItemId, NativeRootId = heldRootId },
        };
        var nativeComparisons = (native["Comparisons"] as JArray ?? new()).Cast<JObject>().ToArray();
        bool spiderRequired = (bool?)native["Summary"]?["SpiderIdleRequired"] == true;
        var spiderStimulus = native["SpiderIdleStimulus"] as JObject;
        int spiderRootId = (int?)spiderStimulus?["RootId"] ?? 0;
        var spider = spiderRequired || spiderStimulus != null ? new SpiderIdle
        {
            NativeRootId = spiderRootId,
            Prefix = spiderRootId == 0 ? "unavailable:" : "creature:" + spiderRootId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":",
            NativeComparisonTime = nativeComparisons.Where(row => (int?)row["Id"] == spiderRootId &&
                    (string?)row["Kind"] == "spider" && (string?)row["Clip"] == CreatureIdleAnimationRules.SpiderIdle &&
                    (bool?)row["Passed"] == true && (bool?)row["Baseline"] == false)
                .Select(row => (double?)row["NativeTime"]).Where(time => time is >= 0 && double.IsFinite(time.Value)).OrderBy(time => time).FirstOrDefault(),
        } : null;
        foreach (var target in wormClips)
        {
            target.ItemSuffix = ItemSuffix(target.ItemInstanceId);
            target.NativeComparisonTime = nativeComparisons.Where(row => (int?)row["Id"] == target.NativeRootId &&
                    (string?)row["Kind"] == "worm" && (string?)row["Clip"] == target.Clip &&
                    (bool?)row["Passed"] == true && (bool?)row["Baseline"] == false)
                .Select(row => (double?)row["NativeTime"]).Where(time => time is >= 0 && double.IsFinite(time.Value)).OrderBy(time => time).FirstOrDefault();
        }
        var wormAppearance = new Dictionary<string, WormAppearance>(StringComparer.Ordinal);
        bool wormAppearanceTruncated = false;
        int? ravenId = (int?)native["FlightStimulus"]?["RavenId"];
        double? observedFlight = (double?)native["FlightStimulus"]?["FlightObservedAt"];
        var nativeEvents = (native["NativeFlightEffects"] as JArray ?? new()).Cast<JObject>().Where(row => (int?)row["RavenId"] == ravenId).ToArray();
        var featherBirth = nativeEvents.Where(row => (string?)row["Kind"] == "particle-system" &&
                (bool?)row["BirthObserved"] == true && ((bool?)row["IsPlaying"] == true || (int?)row["ParticleCount"] > 0) &&
                ((string?)row["Name"] ?? "").Contains("Feather", StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => (double)row["NativeTime"]!).FirstOrDefault();
        int? particleId = (int?)featherBirth?["Id"];
        double? nativeBirth = (double?)featherBirth?["NativeTime"];
        string? expectedResource = featherBirth == null ? null : ParticleResource((string)featherBirth["Path"]!);
        var sfx = nativeEvents.Where(row => (string?)row["Kind"] == "native-sfx-enable" && (bool?)row["EnableObserved"] == true &&
                ((string?)row["Name"] ?? "").Contains("AngyFlyAway", StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => (double)row["NativeTime"]!).FirstOrDefault();
        double? nativeAudioEnable = (double?)sfx?["NativeTime"];
        double audioNativeTime = nativeAudioEnable ?? nativeBirth ?? observedFlight ?? double.NaN;
        var nativePositionSample = (native["Comparisons"] as JArray ?? new()).Cast<JObject>()
            .Where(row => (int?)row["Id"] == ravenId && row["RootNativePosition"] is JArray)
            .OrderBy(row => Math.Abs((double)row["NativeTime"]! - audioNativeTime)).FirstOrDefault();
        float[]? nativePosition = nativePositionSample?["RootNativePosition"]?.ToObject<float[]>();
        string ravenPrefix = ravenId.HasValue ? "creature:" + ravenId.Value + ":" : "unavailable:";
        string particlePrefix = particleId.HasValue ? "event-particle:" + particleId.Value + ":" : "unavailable:";
        var flights = new Dictionary<string, Flight>(StringComparer.Ordinal);
        var effects = new Dictionary<string, Effect>(StringComparer.Ordinal);
        var audio = new Dictionary<string, object>(StringComparer.Ordinal);
        int audioCandidates = 0; long scannedFrames = 0;
        double previous = double.NegativeInfinity;
        // A second, chronological, bounded pass is intentional. It finds the
        // first observed state rather than treating a reverse page baseline as
        // a birth. Only selected entity/event summaries survive page eviction.
        foreach (var page in info.Pages.OrderBy(page => page.Index))
        {
            var clip = FullReplayArchive.ReadPage(recording, info, page.Index);
            foreach (var frame in clip.Frames)
            {
                if (frame.T <= previous) continue; // overlapping real page boundary
                previous = frame.T; scannedFrames++;
                if (wormStimulus != null)
                    foreach (var item in frame.Items)
                    {
                        if (item.ItemId != 184) continue;
                        bool original = MatchesItemInstance(item.Key, originalSuffix), held = MatchesItemInstance(item.Key, heldSuffix);
                        if (!original && !held) continue;
                        foreach (var target in wormClips)
                        {
                            if (!MatchesItemInstance(item.Key, target.ItemSuffix) || item.IdleAnimation?.Clip != target.Clip) continue;
                            bool semanticMatch = target.Role == "original-ground" ? item.State == 0 && item.Cooked <= 0 :
                                item.State == 1 && (target.Role == "held-cooked" ? item.Cooked > 0 : item.Cooked <= 0);
                            if (!semanticMatch || !item.SampleTimeKnown || item.ClockMode == CreatureReplayRules.SleepingClock) continue;
                            double nativeFrame = frame.T + origin;
                            bool withinAction = target.Role == "original-ground" ? pickupRequested >= 0 && nativeFrame <= pickupRequested + MaximumWormStateErrorSeconds :
                                target.Role == "held-uncooked" ? pickupRequested >= 0 && finishCookingRequested >= pickupRequested &&
                                    nativeFrame >= pickupRequested - MaximumWormStateErrorSeconds && nativeFrame <= finishCookingRequested + MaximumWormStateErrorSeconds :
                                finishCookingRequested >= 0 && cookedObserved >= finishCookingRequested &&
                                    nativeFrame >= finishCookingRequested - MaximumWormStateErrorSeconds && nativeFrame <= cookedObserved + MaximumWormStateErrorSeconds;
                            if (!withinAction) continue;
                            if (target.Frames++ == 0) { target.FirstFrame = frame.T; target.Key = item.Key; }
                            double distance = target.NativeComparisonTime.HasValue ? Math.Abs(nativeFrame - target.NativeComparisonTime.Value) : double.PositiveInfinity;
                            if (distance >= target.MatchedDistance) continue;
                            target.MatchedDistance = distance; target.MatchedFrame = frame.T; target.MatchedObservation = item.SampleTime;
                            target.MatchedState = item.State; target.MatchedCooked = item.Cooked; target.MatchedClockMode = item.ClockMode;
                            target.MatchedClockKnown = item.SampleTimeKnown; target.Key = item.Key;
                        }
                        if (!held || item.State != 1) continue;
                        if (!wormAppearance.TryGetValue(item.Key, out var appearance))
                        {
                            if (wormAppearance.Count >= 16) { wormAppearanceTruncated = true; continue; }
                            wormAppearance.Add(item.Key, appearance = new WormAppearance { Key = item.Key });
                        }
                        if (!ReferenceEquals(appearance.LastVisuals, item.Visuals)) UpdateAppearance(appearance, item.Visuals);
                        if (appearance.LastMaterialHash.Length == 0) continue;
                        if (item.Cooked <= 0 && item.IdleAnimation?.Clip == CreatureIdleAnimationRules.EarlyWormInHand &&
                            frame.T + origin <= finishCookingRequested + MaximumWormStateErrorSeconds)
                        {
                            appearance.UncookedMaterialHash = appearance.LastMaterialHash; appearance.UncookedTintHash = appearance.LastTintHash;
                            appearance.UncookedFrame = frame.T; appearance.UncookedProperties = appearance.MaterialPropertyCount;
                            appearance.UncookedTintCount = appearance.TintCount;
                        }
                        else if (item.Cooked > 0 && item.IdleAnimation?.Clip == CreatureIdleAnimationRules.EarlyWormCooked &&
                            appearance.UncookedMaterialHash.Length != 0 && appearance.LastMaterialHash != appearance.UncookedMaterialHash &&
                            double.IsNaN(appearance.FirstChangedCookedFrame) && frame.T + origin >= finishCookingRequested - MaximumWormStateErrorSeconds &&
                            frame.T + origin <= cookedObserved + MaximumWormStateErrorSeconds)
                        {
                            appearance.FirstChangedCookedFrame = frame.T; appearance.CookedMaterialHash = appearance.LastMaterialHash;
                            appearance.CookedTintHash = appearance.LastTintHash; appearance.ChangedCookedValue = item.Cooked;
                            appearance.CookedProperties = appearance.MaterialPropertyCount;
                            appearance.TintChanged = appearance.UncookedTintCount > 0 && appearance.TintCount > 0 && appearance.LastTintHash != appearance.UncookedTintHash;
                        }
                    }
                foreach (var creature in frame.Creatures)
                {
                    if (spider != null && creature.Key.StartsWith(spider.Prefix, StringComparison.Ordinal) && creature.Kind == "spider" &&
                        creature.Pose.Active && creature.IdleAnimation?.Clip == CreatureIdleAnimationRules.SpiderIdle && creature.SampleTimeKnown &&
                        creature.ClockMode != CreatureReplayRules.SleepingClock)
                    {
                        if (spider.Frames++ == 0) spider.FirstFrame = frame.T;
                        double distance = spider.NativeComparisonTime.HasValue ? Math.Abs(frame.T + origin - spider.NativeComparisonTime.Value) : double.PositiveInfinity;
                        if (distance < spider.MatchedDistance)
                        {
                            spider.Key = creature.Key; spider.MatchedDistance = distance; spider.MatchedFrame = frame.T;
                            spider.MatchedObservation = creature.SampleTime; spider.MatchedClockMode = creature.ClockMode;
                            spider.MatchedClockKnown = creature.SampleTimeKnown;
                            spider.MatchedAnchor = creature.IdleAnimation.AnchorTime; spider.MatchedClipTime = creature.IdleAnimation.ClipTime;
                        }
                    }
                    if (!creature.Key.StartsWith(ravenPrefix, StringComparison.Ordinal) || creature.Kind != "voice-thief" ||
                        creature.State != 1 || creature.IdleAnimation?.Clip != "raven-fly-away-v1" || !creature.SampleTimeKnown ||
                        creature.IdleAnimation.AnchorTime != creature.SampleTime) continue;
                    if (!flights.TryGetValue(creature.Key, out var flight))
                    {
                        flight = new Flight { Key = creature.Key, FirstFrame = frame.T, SampleTime = creature.SampleTime,
                            Anchor = creature.IdleAnimation.AnchorTime, ClipTime = creature.IdleAnimation.ClipTime };
                        flights.Add(creature.Key, flight);
                    }
                    flight.Frames++;
                }
                foreach (var effect in frame.Effects)
                {
                    if (!effect.Key.StartsWith(particlePrefix, StringComparison.Ordinal) || effects.ContainsKey(effect.Key)) continue;
                    double anchor = effect.InitialState?.AnchorTime ?? effect.AnchorTime;
                    double phase = effect.InitialState?.Phase ?? effect.Phase;
                    float rate = effect.InitialState?.Rate ?? effect.Rate;
                    bool loop = effect.InitialState?.Loop ?? effect.Loop, paused = effect.InitialState?.Paused ?? effect.Paused;
                    bool birthClock = !loop && !paused && rate > 0 && double.IsFinite(anchor) && double.IsFinite(phase) && phase >= 0;
                    effects.Add(effect.Key, new Effect { Key = effect.Key, Resource = effect.Resource, FirstFrame = frame.T,
                        Anchor = anchor, Phase = phase, HasBirthClock = birthClock, Birth = birthClock ? anchor - phase / rate : double.NaN });
                }
                foreach (var voice in frame.Audio)
                {
                    if (!double.IsFinite(audioNativeTime) || nativePosition == null || voice.Loop ||
                        Math.Abs(voice.StartedAt + origin - audioNativeTime) > 1 || Distance(voice.Position, nativePosition) > 10 || audio.ContainsKey(voice.Key)) continue;
                    audioCandidates++;
                    if (audio.Count >= 128) continue;
                    bool suggestive = new[] { "raven", "crow", "wing", "flap", "fly" }.Any(name => voice.Clip.Contains(name, StringComparison.OrdinalIgnoreCase));
                    audio.Add(voice.Key, new { voice.Key, voice.Clip, StartedAt = voice.StartedAt, NativeStartedAt = voice.StartedAt + origin,
                        DeltaFromNativeEnableSeconds = voice.StartedAt + origin - audioNativeTime, DistanceFromNativeRavenMetres = Distance(voice.Position, nativePosition),
                        voice.Position, voice.Pitch, voice.Volume, voice.SpatialBlend, NameSuggestsBirdOrFlight = suggestive,
                        ExactNativeSourceAssociation = false });
                }
            }
        }
        bool nativeFlightPresent = ravenId.HasValue && observedFlight is >= 0 && (int?)native["FlightStimulus"]?["FlightSamples"] > 0;
        var flightChecks = flights.Values.Select(flight => new
        {
            flight.Key, NativeRavenGameObjectId = ravenId, FirstCapturedFrame = flight.FirstFrame,
            FirstCapturedNativeFrame = flight.FirstFrame + origin, NativeObservedFlightTime = observedFlight,
            ObservationTime = flight.SampleTime, flight.Anchor, flight.ClipTime, flight.Frames,
            DeltaFromNativeObservationSeconds = observedFlight.HasValue ? (double?)(flight.FirstFrame + origin - observedFlight.Value) : null,
            Passed = nativeFlightPresent && Math.Abs(flight.FirstFrame + origin - observedFlight!.Value) <= .25,
        }).ToArray();
        var effectChecks = effects.Values.Select(effect => new
        {
            effect.Key, NativeParticleInstanceId = particleId, effect.Resource, ExpectedNativeResource = expectedResource,
            ResourceMatches = effect.Resource == expectedResource, effect.FirstFrame, FirstNativeFrame = effect.FirstFrame + origin,
            effect.Anchor, effect.Phase, effect.HasBirthClock, CalculatedRecordingBirth = effect.HasBirthClock ? (double?)effect.Birth : null,
            CalculatedNativeBirth = effect.HasBirthClock ? (double?)(effect.Birth + origin) : null, NativeBirthObservedAt = nativeBirth,
            DeltaFromNativeBirthSeconds = nativeBirth.HasValue && effect.HasBirthClock ? (double?)(effect.Birth + origin - nativeBirth.Value) : null,
            Passed = nativeBirth.HasValue && effect.Resource == expectedResource && effect.HasBirthClock &&
                Math.Abs(effect.Birth + origin - nativeBirth.Value) <= .25 && Math.Abs(effect.FirstFrame + origin - nativeBirth.Value) <= .25,
        }).ToArray();
        bool flightPassed = flightChecks.Any(check => check.Passed), featherPassed = effectChecks.Any(check => check.Passed);
        var wormChecks = wormClips.Select(target => new
        {
            target.Role, target.Clip, target.Key, NativeItemInstanceId = target.ItemInstanceId, NativeRootGameObjectId = target.NativeRootId,
            ExactItemInstanceSuffix = target.ItemSuffix,
            target.Frames, FirstCapturedFrame = FiniteOrNull(target.FirstFrame), FirstCapturedNativeFrame = FiniteOrNull(target.FirstFrame + origin),
            NativeVerifiedComparisonTime = target.NativeComparisonTime, MatchedCapturedFrame = FiniteOrNull(target.MatchedFrame),
            MatchedCapturedNativeFrame = FiniteOrNull(target.MatchedFrame + origin), MatchedObservationTime = FiniteOrNull(target.MatchedObservation),
            MatchedObservationNativeTime = FiniteOrNull(target.MatchedObservation + origin),
            DeltaFromIndependentNativeComparisonSeconds = FiniteOrNull(target.MatchedFrame + origin - (target.NativeComparisonTime ?? double.NaN)),
            RecordedState = target.MatchedState, RecordedCooked = target.MatchedCooked, target.MatchedClockMode, target.MatchedClockKnown,
            Passed = target.ItemInstanceId != 0 && target.NativeRootId != 0 && target.Frames > 0 && target.NativeComparisonTime.HasValue &&
                target.MatchedDistance <= MaximumWormStateErrorSeconds && target.MatchedClockKnown,
        }).ToArray();
        var cookedTarget = wormClips.FirstOrDefault(target => target.Role == "held-cooked");
        var appearanceChecks = wormAppearance.Values.Select(appearance => new
        {
            appearance.Key, NativeHeldItemInstanceId = heldItemId,
            UncookedFrame = FiniteOrNull(appearance.UncookedFrame), UncookedNativeFrame = FiniteOrNull(appearance.UncookedFrame + origin),
            FirstChangedCookedFrame = FiniteOrNull(appearance.FirstChangedCookedFrame), FirstChangedCookedNativeFrame = FiniteOrNull(appearance.FirstChangedCookedFrame + origin),
            NativeFinishCookingRequestedAt = finishCookingRequested, NativeVerifiedCookedComparisonTime = cookedTarget?.NativeComparisonTime,
            DeltaFromNativeCookedComparisonSeconds = FiniteOrNull(appearance.FirstChangedCookedFrame + origin - (cookedTarget?.NativeComparisonTime ?? double.NaN)),
            RecordedCooked = appearance.ChangedCookedValue, appearance.UncookedProperties, CookedMaterialPropertyCount = appearance.CookedProperties,
            appearance.UncookedMaterialHash, appearance.CookedMaterialHash, appearance.UncookedTintHash, appearance.CookedTintHash,
            RecordedMaterialShaderPropertiesChanged = !double.IsNaN(appearance.FirstChangedCookedFrame), RecordedTintChanged = appearance.TintChanged,
            ExactNativeMaterialBoundaryAssociation = false,
            Passed = appearance.UncookedProperties > 0 && appearance.CookedProperties > 0 && !double.IsNaN(appearance.FirstChangedCookedFrame) &&
                cookedTarget?.NativeComparisonTime is double nativeCooked && Math.Abs(appearance.FirstChangedCookedFrame + origin - nativeCooked) <= MaximumWormStateErrorSeconds,
        }).ToArray();
        bool wormNativePassed = wormStimulus != null && (bool?)wormStimulus["Completed"] == true && string.IsNullOrEmpty((string?)wormStimulus["Error"]) &&
            originalItemId != 0 && heldItemId != 0 && originalItemId != heldItemId && originalRootId != 0 && heldRootId != 0;
        bool wormPassed = wormNativePassed && !wormAppearanceTruncated && wormChecks.Length == 3 && wormChecks.All(check => check.Passed) && appearanceChecks.Any(check => check.Passed);
        bool spiderNativePassed = spider != null && spider.NativeRootId != 0 && spider.NativeComparisonTime.HasValue && spiderStimulus != null &&
            (bool?)spiderStimulus["ReturnedToBaseCamp"] == true && string.IsNullOrEmpty((string?)spiderStimulus["Error"]) &&
            (bool?)native["Summary"]?["SpiderIdleVerified"] == true;
        bool spiderPassed = spiderNativePassed && spider!.Frames > 0 && spider.MatchedClockKnown && spider.MatchedDistance <= MaximumWormStateErrorSeconds;
        var spiderCheck = spider == null ? null : new
        {
            NativeRootGameObjectId = spider.NativeRootId, ExactCreatureKeyPrefix = spider.Prefix, spider.Key,
            Kind = "spider", Clip = CreatureIdleAnimationRules.SpiderIdle, spider.Frames,
            FirstCapturedFrame = FiniteOrNull(spider.FirstFrame), FirstCapturedNativeFrame = FiniteOrNull(spider.FirstFrame + origin),
            NativeFirstVerifiedNonBaselineComparisonTime = spider.NativeComparisonTime,
            MatchedCapturedFrame = FiniteOrNull(spider.MatchedFrame), MatchedCapturedNativeFrame = FiniteOrNull(spider.MatchedFrame + origin),
            DeltaFromIndependentNativeComparisonSeconds = FiniteOrNull(spider.MatchedFrame + origin - (spider.NativeComparisonTime ?? double.NaN)),
            MatchedObservationTime = FiniteOrNull(spider.MatchedObservation), MatchedObservationNativeTime = FiniteOrNull(spider.MatchedObservation + origin),
            MatchedAnimationAnchor = FiniteOrNull(spider.MatchedAnchor), MatchedClipTime = FiniteOrNull(spider.MatchedClipTime),
            spider.MatchedClockMode, spider.MatchedClockKnown, Passed = spiderPassed,
        };
        var result = new Result { Passed = (bool?)native["Summary"]?["Passed"] == true && flightPassed && featherPassed && (wormStimulus == null || wormPassed) &&
                (!spiderRequired || spiderPassed),
            FlightDescriptorPassed = flightPassed, FeatherEventPassed = featherPassed, AudioCandidateCount = audio.Count,
            WormInteractionRequired = wormStimulus != null,
            WormInteractionPassed = wormStimulus == null ? null : wormPassed,
            SpiderIdleRequired = spiderRequired, SpiderIdlePassed = spiderRequired ? spiderPassed : null };
        File.WriteAllText(Path.Combine(output, result.File), JsonConvert.SerializeObject(new
        {
            result.Passed, result.FlightDescriptorPassed, result.FeatherEventPassed, result.WormInteractionRequired, result.WormInteractionPassed,
            result.SpiderIdleRequired, result.SpiderIdlePassed,
            VisualProbePassed = (bool?)native["Summary"]?["Passed"], NativeRavenGameObjectId = ravenId, NativeParticleInstanceId = particleId,
            Schema = info.Header.Schema, ContainerVersion = info.Version, Origin = origin, PagesScannedChronologically = info.Pages.Length, FramesScannedChronologically = scannedFrames,
            MaximumBirthAndFlightTimingErrorSeconds = .25, FlightChecks = flightChecks, FeatherEventChecks = effectChecks,
            NativeFlightSfxEnable = sfx, AudioCandidateCount = audio.Count, AudioCandidatesTruncated = audioCandidates > audio.Count,
            AudioNativePosition = nativePosition, AudioNativePositionSampleTime = (double?)nativePositionSample?["NativeTime"],
            AudioCandidates = audio.Values.ToArray(), AudioExactNativeSourceAssociation = false,
            WormNativeStimulus = wormStimulus, WormNativeStimulusPassed = wormStimulus == null ? (bool?)null : wormNativePassed,
            MaximumWormStateTimingErrorSeconds = MaximumWormStateErrorSeconds, WormStateChecks = wormChecks, WormAppearanceChecks = appearanceChecks,
            WormAppearanceIdentitiesTruncated = wormAppearanceTruncated,
            WormExactNativeMaterialBoundaryAssociation = false,
            SpiderNativeStimulus = spiderStimulus, SpiderNativeStimulusPassed = spiderRequired ? (bool?)spiderNativePassed : null,
            MaximumSpiderStateTimingErrorSeconds = MaximumWormStateErrorSeconds, SpiderIdleStateCheck = spiderCheck,
            SpiderLimits = "Spider recording association is required only when the independent visual summary sets SpiderIdleRequired=true. Its native GameObject RootId must match the exact creature:<RootId>: key prefix, Kind=spider and spider-idle-v1 descriptor on an active, known-clock, non-sleeping recorded state. The closest recorded frame must be within 0.35 seconds of the first independently passed non-baseline native comparison for that same root and clip. A held descriptor may retain its earlier observation/anchor while its absolute animation clock advances. Stimulus must return normally with no error and native SpiderIdleVerified must be true. Older 0.8.29 summaries without Spider fields leave this requirement optional. Only this one nearest entity/state summary is retained during the existing chronological page pass; no recording or page collection is cached.",
            WormLimits = "When a WormInteractionStimulus is present, all three production descriptors are mandatory on ItemId 184 and exact /native-Item-component-ID key suffixes: original Ground Walk, replacement Held InHand with Cooked<=0, and the same Held Cooked with Cooked>0. Their recorded frame clocks must lie within 0.35 seconds of an independently verified non-baseline native comparison for that exact root ID and clip, and within the normal pickup/cooking action interval. A held native animation reference may legitimately retain an old observation/anchor; its absolute clock is not mistaken for late state delivery. Cooking appearance is compared only within the same Held item identity using recorded renderer material shader-property fingerprints, excluding blend shapes and material instance names. A recorded change near the native Cooked comparison is required; the native visual probe did not export material-boundary values, so exact native material correspondence or exact native material-change latency is not established. InHandObservedAt and CookedObservedAt are validation-completion times after repeated samples, not the first native state-change times. Missing stimulus in older runs leaves these additional checks optional. No complete recording or page collection is retained by this chronological pass.",
            Limits = "Raven identity uses the native GameObject ID in the exact creature key prefix. Feather identity uses the native ParticleSystem instance ID in the exact event-particle key prefix, an independently reconstructed resource path, a native activation/isPlaying birth edge, and its first captured state plus native-clock birth within 0.25 seconds. Audio candidates are non-looping voices starting within one second of the native SFX enable and within ten metres of the independently observed raven root. SFX_PlayOneShot.enabled alone does not establish that a sound played or identify a specific pooled AudioSource; clip names and time/position proximity are candidate evidence only. No microphone, PCM, particle array or native gameplay state is read by this offline verifier. Only this observed raven flight is established, not all creature/event coverage.",
        }, Formatting.Indented));
        return result;
    }
    private static double? FiniteOrNull(double value) => double.IsFinite(value) ? value : null;
    private static string ItemSuffix(int id) => id == 0 ? "" : "/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static bool MatchesItemInstance(string key, string suffix) => suffix.Length != 0 && key.EndsWith(suffix, StringComparison.Ordinal);
    private static void UpdateAppearance(WormAppearance appearance, NativeRendererFrame[]? visuals)
    {
        appearance.LastVisuals = visuals; appearance.LastMaterialHash = appearance.LastTintHash = "";
        appearance.MaterialPropertyCount = appearance.TintCount = 0;
        if (visuals == null) return;
        var properties = new JArray(); var tints = new JArray();
        foreach (var renderer in visuals)
            for (int slot = 0; slot < renderer.Materials.Length; slot++)
                foreach (var property in renderer.Materials[slot].Properties)
                {
                    var value = JObject.FromObject(new { renderer.Path, renderer.Index, Slot = slot, Shader = renderer.Materials[slot].Shader,
                        property.Name, property.Kind, property.Values, property.Texture, property.ColorEncoding });
                    properties.Add(value); appearance.MaterialPropertyCount++;
                    if (property.Name == "_Tint") { tints.Add(value.DeepClone()); appearance.TintCount++; }
                }
        if (appearance.MaterialPropertyCount > 0) appearance.LastMaterialHash = Fingerprint(properties);
        if (appearance.TintCount > 0) appearance.LastTintHash = Fingerprint(tints);
    }
    private static string Fingerprint(JArray values) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(values.ToString(Formatting.None))));
    private static double Distance(float[] a, float[] b) => Math.Sqrt(a.Zip(b, (x, y) => (x - (double)y) * (x - (double)y)).Sum());
    private static string ParticleResource(string nativePath)
    {
        var parts = nativePath.Split('/');
        string root = Uri.UnescapeDataString(parts[0]).Replace("(Clone)", "", StringComparison.Ordinal).Trim();
        string suffix = string.Join("/", parts.Skip(1).Select(part =>
        {
            int separator = part.IndexOf(':');
            if (separator < 0) throw new InvalidDataException("Native particle path omitted sibling identity.");
            return part[..(separator + 1)] + Uri.EscapeDataString(Uri.UnescapeDataString(part[(separator + 1)..]).Replace("(Clone)", "", StringComparison.Ordinal).Trim());
        }));
        return "particle/scene/" + root + "/" + suffix;
    }
}
