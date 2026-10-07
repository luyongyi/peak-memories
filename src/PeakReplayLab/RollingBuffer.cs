using System;
using System.Collections.Generic;
using System.Linq;

namespace PeakReplayLab;

// Pure data only: no Unity objects, files or serialization in the capture path.
public sealed class RollingBuffer
{
    private readonly Queue<(ReplayFrame Frame, long Bytes)> frames = new();
    private readonly Dictionary<string, string> strings = new(StringComparer.Ordinal);
    private readonly Dictionary<Appearance, Appearance> appearances = new();
    // Object snapshots are immutable and shared until their state changes. Account
    // each live snapshot once, not once for every 60 Hz frame referring to it.
    private readonly Dictionary<ItemFrame, (int Count, long Bytes)> itemReferences = new();
    private readonly Dictionary<CrateFrame, (int Count, long Bytes)> crateReferences = new();
    private readonly Dictionary<ItemFrame[], (int Count, long Bytes)> itemArrays = new();
    private readonly Dictionary<CrateFrame[], (int Count, long Bytes)> crateArrays = new();
    private readonly ReplayTrackBudget<RopeReplayFrame> ropes = new(RopeReplayRules.Estimate);
    private readonly ReplayTrackBudget<EffectReplayFrame> effects = new(EffectReplayRules.Estimate);
    private readonly ReplayTrackBudget<AudioReplayFrame> audio = new(AudioReplayRules.Estimate);
    private readonly ReplayTrackBudget<SpawnedReplayFrame> spawned = new(SpawnedReplayRules.Estimate);
    private readonly ReplayTrackBudget<BalloonReplayFrame> balloons = new(BalloonReplayRules.Estimate);
    private readonly ReplayTrackBudget<CreatureReplayFrame> creatures = new(CreatureReplayRules.Estimate);
    public const double WindowSeconds = 120;
    public const long DefaultBudget = 256L * 1024 * 1024;
    private readonly long budget;
    private double previous = -1;
    public long EstimatedBytes { get; private set; }
    // Reuse already-accounted whole-frame size for a bounded debug writer queue.
    public long LastFrameEstimatedBytes { get; private set; }
    public int RetainedFrameLimit { get; }
    public int Count => frames.Count;
    public double Duration => Count < 2 ? 0 : previous - frames.Peek().Frame.T;
    public bool MemoryLimited { get; private set; }

    public RollingBuffer(long budget = DefaultBudget, int frameLimit = ReplayRules.MaxFrames)
    {
        if (budget < 1024) throw new ArgumentOutOfRangeException(nameof(budget));
        if (frameLimit < 2 || frameLimit > ReplayRules.MaxFrames) throw new ArgumentOutOfRangeException(nameof(frameLimit));
        this.budget = budget;
        RetainedFrameLimit = frameLimit;
    }

    public void Add(ReplayFrame frame)
    {
        // Capture time is a monotonically increasing clock, not a saved clip's bounded timeline.
        if (!ReplayRules.Finite(frame.T) || frame.T < 0 || frame.T <= previous) throw new ArgumentException("Capture time must increase.");
        foreach (var actor in frame.Actors)
        {
            actor.Id = Intern(actor.Id); actor.Name = Intern(actor.Name); actor.Action = Intern(actor.Action);
            if (appearances.TryGetValue(actor.Appearance, out var found)) actor.Appearance = found;
            else if (appearances.Count < 512) appearances.Add(actor.Appearance, actor.Appearance);
        }
        long bytes = EstimateFrame(frame);
        long singleFrame = bytes;
        singleFrame += Acquire(frame.Items) + Acquire(frame.Crates);
        long presentationBefore = PresentationBytes;
        singleFrame += ropes.Acquire(frame.Ropes) + effects.Acquire(frame.Effects) + audio.Acquire(frame.Audio) + spawned.Acquire(frame.Spawned) + balloons.Acquire(frame.Balloons) + creatures.Acquire(frame.Creatures);
        EstimatedBytes += PresentationBytes - presentationBefore;
        if (singleFrame > budget)
        {
            Release(frame.Items); Release(frame.Crates);
            ReleasePresentation(frame);
            throw new InvalidOperationException("One frame exceeds the replay memory budget.");
        }
        previous = frame.T;
        LastFrameEstimatedBytes = singleFrame;
        frames.Enqueue((frame, bytes));
        EstimatedBytes += bytes;
        while (Count > 0 && (previous - frames.Peek().Frame.T > WindowSeconds || Count > RetainedFrameLimit || EstimatedBytes > budget))
        {
            if (EstimatedBytes > budget) MemoryLimited = true;
            var removed = frames.Dequeue();
            EstimatedBytes -= removed.Bytes;
            Release(removed.Frame.Items); Release(removed.Frame.Crates);
            ReleasePresentation(removed.Frame);
        }
    }

    private long PresentationBytes => ropes.Bytes + effects.Bytes + audio.Bytes + spawned.Bytes + balloons.Bytes + creatures.Bytes;
    private void ReleasePresentation(ReplayFrame frame)
    {
        long before = PresentationBytes;
        ropes.Release(frame.Ropes); effects.Release(frame.Effects); audio.Release(frame.Audio);
        spawned.Release(frame.Spawned);
        balloons.Release(frame.Balloons);
        creatures.Release(frame.Creatures);
        EstimatedBytes += PresentationBytes - before;
    }

    private long Acquire(ItemFrame[] items)
    {
        if (itemArrays.TryGetValue(items, out var old)) { itemArrays[items] = (old.Count + 1, old.Bytes); return old.Bytes; }
        long bytes = 0; foreach (var item in items) bytes += Acquire(item);
        itemArrays.Add(items, (1, bytes)); return bytes;
    }
    private long Acquire(CrateFrame[] crates)
    {
        if (crateArrays.TryGetValue(crates, out var old)) { crateArrays[crates] = (old.Count + 1, old.Bytes); return old.Bytes; }
        long bytes = 0; foreach (var crate in crates) bytes += Acquire(crate);
        crateArrays.Add(crates, (1, bytes)); return bytes;
    }
    private void Release(ItemFrame[] items)
    {
        var old = itemArrays[items];
        if (old.Count > 1) itemArrays[items] = (old.Count - 1, old.Bytes);
        else { itemArrays.Remove(items); foreach (var item in items) Release(item); }
    }
    private void Release(CrateFrame[] crates)
    {
        var old = crateArrays[crates];
        if (old.Count > 1) crateArrays[crates] = (old.Count - 1, old.Bytes);
        else { crateArrays.Remove(crates); foreach (var crate in crates) Release(crate); }
    }

    private long Acquire(ItemFrame item)
    {
        if (itemReferences.TryGetValue(item, out var old)) { itemReferences[item] = (old.Count + 1, old.Bytes); return old.Bytes; }
        long bytes = Estimate(item); itemReferences.Add(item, (1, bytes)); EstimatedBytes += bytes; return bytes;
    }
    private long Acquire(CrateFrame crate)
    {
        if (crateReferences.TryGetValue(crate, out var old)) { crateReferences[crate] = (old.Count + 1, old.Bytes); return old.Bytes; }
        long bytes = Estimate(crate); crateReferences.Add(crate, (1, bytes)); EstimatedBytes += bytes; return bytes;
    }
    private void Release(ItemFrame item)
    {
        var old = itemReferences[item];
        if (old.Count > 1) itemReferences[item] = (old.Count - 1, old.Bytes);
        else { itemReferences.Remove(item); EstimatedBytes -= old.Bytes; }
    }
    private void Release(CrateFrame crate)
    {
        var old = crateReferences[crate];
        if (old.Count > 1) crateReferences[crate] = (old.Count - 1, old.Bytes);
        else { crateReferences.Remove(crate); EstimatedBytes -= old.Bytes; }
    }

    private string Intern(string text)
    {
        if (strings.TryGetValue(text, out var found)) return found;
        if (strings.Count < 4096) strings.Add(text, text); // bounded cache, not CLR-global String.Intern
        return text;
    }

    // Intentionally conservative accounting including strings/appearance per sample even when shared.
    public static long Estimate(ReplayFrame frame)
    {
        return EstimateFrame(frame) + frame.Items.Sum(Estimate) + frame.Crates.Sum(Estimate) +
            frame.Ropes.Sum(RopeReplayRules.Estimate) + frame.Effects.Sum(EffectReplayRules.Estimate) + frame.Audio.Sum(AudioReplayRules.Estimate) + frame.Spawned.Sum(SpawnedReplayRules.Estimate) + frame.Balloons.Sum(BalloonReplayRules.Estimate) + frame.Creatures.Sum(CreatureReplayRules.Estimate);
    }

    internal static long EstimateFrame(ReplayFrame frame)
    {
        long size = 456 + frame.World.ActiveMapObjects.Length +
            (frame.Actors.Length + frame.Items.Length + frame.Crates.Length + frame.Events.Length + frame.Ropes.Length + frame.Effects.Length + frame.Audio.Length + frame.Spawned.Length + frame.Balloons.Length + frame.Creatures.Length) * 8L;
        size += EnvironmentReplayRules.Estimate(frame.World.Environment);
        foreach (var a in frame.Actors)
        {
            size += 640 + (a.Id.Length + a.Name.Length + a.Action.Length) * 2L;
            size += a.Inventory.Length * 8L;
            // 96 includes the new eight bytes of HUD metadata and remains
            // conservative. Keep it stable for schema 10 stored page budgets.
            foreach (var item in a.Inventory) size += 96 + item.Instance.Length * 2L;
            size += ActorJointReplayRules.Estimate(a.JointPose);
            size += ReplayHudState.Estimate(a.HudState);
            // Shared 10 Hz evidence remains conservatively counted per frame.
            if (a.RouteState != null) size += 256;
            size += WebWrapReplayRules.Estimate(a.WebWrap);
        }
        foreach (var e in frame.Events) size += 128 + (e.Kind.Length + e.ActorId.Length + e.ItemKey.Length + e.Name.Length) * 2L;
        return size;
    }

    public static long Estimate(ItemFrame item) => 312 + (item.Key.Length + item.Prefab.Length + item.Name.Length + item.HolderId.Length) * 2L + Estimate(item.Pose) + NativeVisualAppearanceRules.Estimate(item.Visuals) + NativeLightRules.Estimate(item.Lights);
    public static long Estimate(CrateFrame crate) => 160 + (crate.Key.Length + crate.Kind.Length) * 2L + Estimate(crate.Pose) +
        (crate.Animation == null ? 0 : 96 + crate.Animation.Clip.Length * 2L);
    private static long Estimate(ObjectPose pose) => 232 + pose.Nodes.Length * 8L + pose.Nodes.Sum(Estimate);
    private static long Estimate(NodePose node) => 248 + node.Path.Length * 2L;

    public ReplayClip Snapshot(ReplayHeader source, DateTime savedUtc) => PrepareSnapshot(source, savedUtc)();

    // Only the bounded array of references and small header are copied on the Unity
    // thread. Rebasing/participants/statistics run after this in the save worker.
    public Func<ReplayClip> PrepareSnapshot(ReplayHeader source, DateTime savedUtc)
    {
        if (Count < 2) throw new InvalidOperationException("Not enough buffered gameplay yet.");
        var items = frames.Select(f => f.Frame).ToArray();
        double start = items[0].T;
        var h = new ReplayHeader
        {
            Schema = source.Schema, Recorder = source.Recorder, Fidelity = source.Fidelity,
            Scene = source.Scene, Route = source.Route, GameVersion = source.GameVersion, BuildId = source.BuildId, GameAssembly = source.GameAssembly,
            RouteContext = source.RouteContext?.Copy(),
            StartedUtc = savedUtc.AddSeconds(-Duration).ToUniversalTime().ToString("O"), SavedUtc = savedUtc.ToUniversalTime().ToString("O"),
            SampleHz = source.SampleHz, Duration = Duration, FrameCount = items.Length, MapObjects = source.MapObjects.ToArray(),
        };
        var outcome = System.Threading.Volatile.Read(ref source.CoverOutcome);
        return () => BuildSnapshot(h, items, start, outcome);
    }

    private static ReplayClip BuildSnapshot(ReplayHeader h, ReplayFrame[] items, double start, ReplayRegionOutcome? outcome)
    {
        h.Participants = items.SelectMany(f => f.Actors).GroupBy(a => a.Id).Select(g => g.Last().Name).Take(ReplayRules.MaxActors).ToArray();
        var regions = new ReplayRegionAccumulator(h);
        foreach (var frame in items) regions.Observe(frame);
        regions.ObserveOutcome(outcome, start, items[items.Length - 1].T);
        h.RegionSummary = regions.Snapshot();
        var result = new ReplayClip { Header = h };
        var crateStates = new Dictionary<CrateFrame, CrateFrame>();
        var crateSets = new Dictionary<CrateFrame[], CrateFrame[]>();
        var ropeRebaser = new ReplayTrackRebaser<RopeReplayFrame>(RopeReplayRules.Rebase, start);
        var effectRebaser = new ReplayTrackRebaser<EffectReplayFrame>(EffectReplayRules.Rebase, start);
        var audioRebaser = new ReplayTrackRebaser<AudioReplayFrame>(AudioReplayRules.Rebase, start);
        var spawnedRebaser = new ReplayTrackRebaser<SpawnedReplayFrame>(SpawnedReplayRules.Rebase, start);
        var balloonRebaser = new ReplayTrackRebaser<BalloonReplayFrame>(BalloonReplayRules.Rebase, start);
        var actorRebaser = new ActorJointRebaser(start);
        var environmentRebaser = new EnvironmentTimeRebaser(start);
        CrateFrame[] Rebase(CrateFrame[] source)
        {
            if (source.Length == 0) return source;
            if (crateSets.TryGetValue(source, out var found)) return found;
            var rebased = new CrateFrame[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                if (!crateStates.TryGetValue(source[i], out var state))
                { state = CrateAnimationTimeline.Rebase(source[i], start); crateStates.Add(source[i], state); }
                rebased[i] = state;
            }
            crateSets.Add(source, rebased); return rebased;
        }
        // Copy only frame wrappers; all nested samples are immutable after insertion. Saving does
        // not freeze capture or deep-copy 120 seconds of actor data. At most one save may run.
        foreach (var f in items) result.Frames.Add(new ReplayFrame
        {
            T = f.T - start, Actors = actorRebaser.Apply(f.Actors), World = environmentRebaser.Apply(f.World), Items = f.Items, Crates = Rebase(f.Crates),
            Ropes = ropeRebaser.Apply(f.Ropes), Effects = effectRebaser.Apply(f.Effects), Audio = audioRebaser.Apply(f.Audio),
            Spawned = spawnedRebaser.Apply(f.Spawned),
            Balloons = balloonRebaser.Apply(f.Balloons),
            Creatures = f.Creatures,
            Events = f.Events.Where(e => e.T >= start).Select(e => new ItemEvent
            {
                Sequence = e.Sequence, T = e.T - start, Kind = e.Kind, ActorId = e.ActorId,
                ItemKey = e.ItemKey, ItemId = e.ItemId, Name = e.Name,
            }).ToArray(),
        });
        return result;
    }
}
