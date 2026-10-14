using System;

namespace PeakReplayLab;

// Pure visual data. Nothing here instructs the game to use, spawn, damage or consume.
public sealed class NodePose
{
    // Actor joints use this clock for independent sampling. Object nodes do not.
    [Newtonsoft.Json.JsonIgnore] public double SampleTime;
    public string Path { get; set; }
    public float[] Position { get; set; }
    public float[] Rotation { get; set; }
    public float[] Scale { get; set; }
    public bool Active { get; set; }
    public bool Visible { get; set; }
    public NodePose() : this("", new float[3], new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f }) { }
    public NodePose(string path, float[] position, float[] rotation, float[] scale, bool active = true, bool visible = true, double sampleTime = 0)
    { Path = path; Position = position; Rotation = rotation; Scale = scale; Active = active; Visible = visible; SampleTime = sampleTime; }
}

public sealed class ObjectPose
{
    public ObjectPose() : this(new float[3], new[] { 0f, 0f, 0f, 1f }, new[] { 1f, 1f, 1f }) { }
    public ObjectPose(float[] position, float[] rotation, float[] scale, bool active = true, NodePose[]? nodes = null)
    { Position = position; Rotation = rotation; Scale = scale; Active = active; Nodes = nodes ?? Array.Empty<NodePose>(); }
    public float[] Position { get; set; }
    public float[] Rotation { get; set; }
    public float[] Scale { get; set; }
    public bool Active { get; set; }
    public NodePose[] Nodes { get; set; }
}

public sealed class ItemFrame
{
    // Schema 25: the last completed item read, independent of the 30 Hz scene
    // clock. Held 5 Hz observations keep this clock and their shared packet.
    public bool SampleTimeKnown { get; set; }
    public double SampleTime { get; set; }
    public int ClockMode { get; set; }
    public bool SampleDiscontinuity { get; set; }
    public bool ShouldSerializeSampleDiscontinuity() => SampleDiscontinuity;
    public bool ShouldSerializeSampleTimeKnown() => SampleTimeKnown || SampleTime != 0 || ClockMode != CreatureReplayRules.ActiveClock;
    public bool ShouldSerializeSampleTime() => ShouldSerializeSampleTimeKnown();
    public bool ShouldSerializeClockMode() => ClockMode != CreatureReplayRules.ActiveClock;
    public string Key { get; set; } = "";
    public int ItemId { get; set; }
    public string Prefab { get; set; } = "";
    public string Name { get; set; } = "";
    public string HolderId { get; set; } = "";
    public int State { get; set; }
    public ObjectPose Pose { get; set; } = new();
    // Null means an older recording did not capture native renderer appearance.
    public NativeRendererFrame[]? Visuals { get; set; }
    public NativeLightFrame[]? Lights { get; set; }
    // Schema 24: an audited native frog idle clip sampled on an inert clone.
    public CreatureIdleAnimation? IdleAnimation { get; set; }
    public bool Primary { get; set; }
    public bool Secondary { get; set; }
    public float Progress { get; set; }
    public bool ProgressKnown { get; set; }
    public int Uses { get; set; } = -1;
    public float Fuel { get; set; } = -1;
    public float Cooked { get; set; }
    public bool Used { get; set; }
    public bool Lit { get; set; }
    public bool RevealBackpackContents { get; set; }
    public float[] Color { get; set; } = new[] { 1f, 1f, 1f, 1f };
    internal ItemFrame WithIdleAnimation(CreatureIdleAnimation? animation)
    { var copy = (ItemFrame)MemberwiseClone(); copy.IdleAnimation = animation; return copy; }
    internal ItemFrame WithObservation(double time, int mode, bool discontinuity = false)
    { var copy = (ItemFrame)MemberwiseClone(); copy.SampleTimeKnown = true; copy.SampleTime = time; copy.ClockMode = mode; copy.SampleDiscontinuity = discontinuity; return copy; }
    internal ItemFrame RebaseObservation(double start)
    {
        if (start == 0 || !SampleTimeKnown && IdleAnimation == null) return this;
        var copy = (ItemFrame)MemberwiseClone();
        if (copy.SampleTimeKnown) copy.SampleTime -= start;
        copy.IdleAnimation = copy.IdleAnimation == null ? null : CreatureIdleAnimationRules.Rebase(copy.IdleAnimation, start);
        return copy;
    }
}

internal static class ItemAnimationTimeline
{
    internal static ItemFrame Rebase(ItemFrame frame, double start) => frame.RebaseObservation(start);
}

// Worker-owned, bounded reference tables avoid Mono's weak-key table path while
// preserving hot shared snapshots and unchanged legacy item array identity.
internal sealed class ItemAnimationRebaser
{
    internal const int MaximumCachedStates = 2048, MaximumCachedArrays = 512;
    private readonly BoundedReferenceCache<ItemFrame, ItemFrame> states = new(MaximumCachedStates);
    private readonly BoundedReferenceCache<ItemFrame[], ItemFrame[]> arrays = new(MaximumCachedArrays);
    private readonly double start;
    private readonly Func<ItemFrame, ItemFrame> stateFactory;
    private readonly Func<ItemFrame[], ItemFrame[]> arrayFactory;
    internal int CachedStates => states.Count;
    internal int CachedArrays => arrays.Count;
    internal ItemAnimationRebaser(double start)
    { this.start = start; stateFactory = ShiftState; arrayFactory = ShiftArray; }
    internal ItemFrame[] Apply(ItemFrame[] source)
    {
        if (start == 0 || source.Length == 0) return source;
        return arrays.GetValue(source, arrayFactory);
    }
    private ItemFrame ShiftState(ItemFrame source) => ItemAnimationTimeline.Rebase(source, start);
    private ItemFrame[] ShiftArray(ItemFrame[] values)
    {
        ItemFrame[]? translated = null;
        for (int i = 0; i < values.Length; i++)
        {
            var item = !values[i].SampleTimeKnown && values[i].IdleAnimation == null ? values[i] : states.GetValue(values[i], stateFactory);
            if (!ReferenceEquals(item, values[i]) && translated == null) translated = (ItemFrame[])values.Clone();
            if (translated != null) translated[i] = item;
        }
        return translated ?? values;
    }
}

public sealed class CrateFrame
{
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool Open { get; set; }
    public ObjectPose Pose { get; set; } = new();
    public CrateAnimationFrame? Animation { get; set; }
    // Schema 15: null preserves authored appearance for older recordings.
    public NativeRendererFrame[]? Visuals { get; set; }
    public NativeLightFrame[]? Lights { get; set; }
}

public sealed class InventoryFrame
{
    public bool Empty { get; set; } = true;
    public int Slot { get; set; }
    public int ItemId { get; set; }
    public string Instance { get; set; } = "";
    public bool Equipped { get; set; }
    public bool Backpack { get; set; }
    public int Uses { get; set; } = -1;
    public float Fuel { get; set; } = -1;
    // Original InventoryItemUI reads these data entries, not raw Fuel. -1 means
    // the item had no such entry or this older recording never captured it.
    public float UiFuel { get; set; } = -1;
    public int Cooked { get; set; } = -1;
}

public sealed class ItemEvent
{
    public long Sequence { get; set; }
    public double T { get; set; }
    public string Kind { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string ItemKey { get; set; } = "";
    public int ItemId { get; set; }
    public string Name { get; set; } = "";
}
