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
    public float[] Position { get; set; } = new float[3];
    public float[] Rotation { get; set; } = new[] { 0f, 0f, 0f, 1f };
    public float[] Scale { get; set; } = new[] { 1f, 1f, 1f };
    public bool Active { get; set; } = true;
    public NodePose[] Nodes { get; set; } = Array.Empty<NodePose>();
}

public sealed class ItemFrame
{
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
}

public sealed class CrateFrame
{
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool Open { get; set; }
    public ObjectPose Pose { get; set; } = new();
    public CrateAnimationFrame? Animation { get; set; }
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
