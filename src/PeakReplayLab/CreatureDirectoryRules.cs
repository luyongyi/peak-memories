using System;

namespace PeakReplayLab;

// Names and effect links audited from this installation's data.unity3d.
// A model called SporeShroom is a child mesh, never a trap root.
public static class CreatureDirectoryRules
{
    public static string TrapEffect(string rootName) => Normalize(rootName) switch
    {
        "Forest_SporeFungus" => "VFX_SporeExplo",
        "Jungle_SporeMushroom" => "VFX_PoisonExplo",
        "Jungle_SporeMushroomExplo" => "VFX_SporeExploExplo",
        _ => ""
    };
    public static bool IsSporeEffect(string name)
    {
        name = Normalize(name);
        return name == "VFX_SporeExplo" || name == "VFX_PoisonExplo" || name == "VFX_SporeExploExplo";
    }
    public static bool TrapSignature(string rootName, string effectName, bool onlyOnce,
        bool invokesRootSpawn, bool deactivatesRoot, bool hasModel) =>
        TrapEffect(rootName) is var expected && expected.Length != 0 && expected == Normalize(effectName) &&
        onlyOnce && invokesRootSpawn && deactivatesRoot && hasModel;
    private static string Normalize(string value)
    {
        value = value.Trim();
        if (value.EndsWith("(Clone)", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 7).TrimEnd();
        // Unity's editor instance suffix is accepted only alongside the verified
        // event/prefab signature; arbitrary similar names remain excluded.
        if (value.EndsWith(")", StringComparison.Ordinal))
        {
            int start = value.LastIndexOf(" (", StringComparison.Ordinal);
            if (start >= 0 && start + 2 < value.Length - 1)
            {
                bool digits = true;
                for (int i = start + 2; i < value.Length - 1; i++) if (value[i] < '0' || value[i] > '9') digits = false;
                if (digits) value = value.Substring(0, start);
            }
        }
        return value;
    }
    public static ObjectPose StaticPose(ObjectPose? previous, bool active, ReadOnlySpan<float> trs)
    {
        if (trs.Length != 10) throw new ArgumentException("Static root TRS must contain ten values.", nameof(trs));
        bool positionSame = Same(previous?.Position, trs.Slice(0, 3));
        bool rotationSame = Same(previous?.Rotation, trs.Slice(3, 4));
        bool scaleSame = Same(previous?.Scale, trs.Slice(7, 3));
        if (previous != null && previous.Active == active && positionSame && rotationSame && scaleSame) return previous;
        return new ObjectPose { Active = active, Position = positionSame ? previous!.Position : trs.Slice(0, 3).ToArray(),
            Rotation = rotationSame ? previous!.Rotation : trs.Slice(3, 4).ToArray(),
            Scale = scaleSame ? previous!.Scale : trs.Slice(7, 3).ToArray(), Nodes = Array.Empty<NodePose>() };
    }
    public static ObjectPose StaticActive(ObjectPose previous, bool active) => previous.Active == active ? previous :
        new ObjectPose { Active = active, Position = previous.Position, Rotation = previous.Rotation,
            Scale = previous.Scale, Nodes = previous.Nodes };
    private static bool Same(float[]? previous, ReadOnlySpan<float> values)
    {
        if (previous == null || previous.Length != values.Length) return false;
        for (int i = 0; i < values.Length; i++) if (previous[i] != values[i]) return false;
        return true;
    }
}
