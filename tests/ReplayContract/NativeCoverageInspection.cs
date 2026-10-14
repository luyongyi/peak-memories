using Newtonsoft.Json;
using PeakReplayLab;

internal static class NativeCoverageInspection
{
    // Read-only inspection of explicitly selected files. No player identities,
    // recorded text, original file writes, or automatic seek-cache generation.
    public static void Run(string path, bool allPages)
    {
        var info = FullReplayArchive.ReadInfo(path);
        var load = FullReplayArchive.CreatePageLoader(path, info);
        var effects = FullReplayArchive.CreateEffectPageLoader(info);
        var chosen = allPages ? info.Pages : info.Pages.Where(p => p.Index == 0 || p.Index == info.Pages.Length - 1 || p.Start <= 740 && p.End >= 720).ToArray();
        var creatures = new Dictionary<string, HashSet<string>>();
        var activeCreatures = new Dictionary<string, HashSet<string>>();
        var interactions = new Dictionary<string, HashSet<string>>();
        var ropes = new Dictionary<string, HashSet<string>>();
        var effectKeys = new HashSet<string>();
        var cratesWithAppearance = new HashSet<string>();
        var postProcessingOwners = new HashSet<string>();
        var seen = new HashSet<long>();
        long particles = 0, fields = 0, blends = 0, crateAppearances = 0, postObservations = 0;
        long nativeParticleCheckpoints = 0, nativeParticleBytes = 0;
        float maximumAudioPitch = 0;
        foreach (var page in Enumerable.Reverse(chosen))
        {
            var clip = load(page.Index, default);
            var history = effects(page.Index, default);
            if (history.Frames.Count != clip.Frames.Count) throw new Exception("Effect history frame count mismatch.");
            for (int i = 0; i < clip.Frames.Count; i++)
            {
                var frame = clip.Frames[i];
                if (frame.T != history.Frames[i].T || !frame.Effects.Select(e => e.Key).SequenceEqual(history.Frames[i].Effects.Select(e => e.Key)))
                    throw new Exception("Effect history timeline mismatch.");
                if (!seen.Add(page.FirstFrameIndex + i)) continue;
                foreach (var creature in frame.Creatures)
                {
                    Add(creatures, creature.Kind, creature.Key);
                    if (creature.Pose.Active) Add(activeCreatures, creature.Kind, creature.Key);
                }
                foreach (var sound in frame.Audio) maximumAudioPitch = Math.Max(maximumAudioPitch, Math.Abs(sound.Pitch));
                foreach (var interaction in frame.Interactions)
                {
                    Add(interactions, interaction.Kind, interaction.Key);
                    if (interaction.PostWeights != null && interaction.PostEnabled != null)
                    { postProcessingOwners.Add(interaction.Key); postObservations++; }
                }
                foreach (var crate in frame.Crates)
                    if (crate.Visuals != null && crate.Lights != null)
                    { cratesWithAppearance.Add(crate.Key); crateAppearances++; }
                foreach (var rope in frame.Ropes) Add(ropes, rope.SceneVine ? "scene-vine" : rope.Kind, rope.Key);
                foreach (var effect in frame.Effects)
                {
                    effectKeys.Add(effect.Resource);
                    if (effect.Particles != null) particles++;
                    if (effect.Particles != null) foreach (var emitter in effect.Particles)
                        if (emitter.Checkpoint is { } checkpoint)
                        { nativeParticleCheckpoints++; nativeParticleBytes += checkpoint.Data.Length + checkpoint.State.Length; }
                    if (effect.ForceField != null) fields++;
                }
                blends += frame.Items.Sum(item => item.Visuals?.Count(v => v.BlendShapes is { Length: > 0 }) ?? 0);
            }
        }
        Console.WriteLine(JsonConvert.SerializeObject(new
        {
            File = Path.GetFileName(path), info.Header.Schema, info.Header.Recorder, info.Duration, info.Complete,
            TotalPages = info.Pages.Length, InspectedPages = chosen.Length, UniqueFrames = seen.Count,
            CreatureEntities = Counts(creatures), InteractionEntities = Counts(interactions), RopeEntities = Counts(ropes),
            ActiveCreatureEntities = Counts(activeCreatures), MaximumAbsoluteAudioPitch = maximumAudioPitch,
            EffectResources = effectKeys.OrderBy(s => s).ToArray(), ParticleStateObservations = particles,
            NativeParticleCheckpoints = nativeParticleCheckpoints, NativeParticleCheckpointBytes = nativeParticleBytes,
            ForceFieldObservations = fields, ItemBlendShapeObservations = blends,
            CratesWithRecordedAppearance = cratesWithAppearance.Count, CrateAppearanceObservations = crateAppearances,
            PostProcessingOwners = postProcessingOwners.Count, PostProcessingObservations = postObservations,
            ReversePageReads = true, EffectHistoryConsistent = true,
        }, Formatting.Indented));
    }
    private static Dictionary<string, int> Counts(Dictionary<string, HashSet<string>> values) => values.ToDictionary(p => p.Key, p => p.Value.Count);
    private static void Add(Dictionary<string, HashSet<string>> values, string kind, string key)
    { if (!values.TryGetValue(kind, out var keys)) values[kind] = keys = new(); keys.Add(key); }
}

