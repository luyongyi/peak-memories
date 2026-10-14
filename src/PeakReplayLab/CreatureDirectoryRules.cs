using System;

namespace PeakReplayLab;

// Names and effect links audited from this installation's data.unity3d.
// A model called SporeShroom is a child mesh, never a trap root.
public static class CreatureDirectoryRules
{
    // This is a protocol/resource whitelist, never a Resources.Load path from
    // a recording. Scene variants resolve only inside the native typed directory.
    public static bool IsKnownKind(string kind) => kind == "spider" || kind == "mushroom-zombie" ||
        kind == "condor" || kind == "frog-tongue" || kind == "spore-trap" ||
        kind == "beetle" || kind == "scoutmaster" || kind == "antlion" || kind == "venus-flytrap" ||
        kind == "tumbleweed" || kind == "voice-thief" || kind == "skeleton" || kind == "scoutmaster-ghost" || kind == "capybara" ||
        kind == "ghost-ball" || kind == "player-ghost";
    public static bool IsLegacyKind(string kind) => kind == "spider" || kind == "mushroom-zombie" ||
        kind == "condor" || kind == "frog-tongue" || kind == "spore-trap";
    // Before schema 21 these independent bodies were absent from the recorder.
    // Their particle/actor tracks did not own the corresponding native mesh.
    public static bool BodyRequiresSchema21(string kind) => kind == "ghost-ball" || kind == "player-ghost";
    public static bool ValidResource(string kind, string resource) => IsKnownKind(kind) &&
        (resource == kind || kind == "spider" && (resource == "spider-normal" || resource == "spider-web") ||
         kind == "mushroom-zombie" && (resource == "mushroom-zombie-npc" || resource == "mushroom-zombie-player"));
    public static bool RequiresScenePath(string kind) => kind == "spore-trap" || kind == "antlion" ||
        kind == "venus-flytrap" || kind == "capybara";
    public static bool IsCapybaraName(string name)
    { name = Normalize(name); return name == "Capybara" || name == "Capybara Void"; }
    public static bool IsWebSpiderName(string name) => Normalize(name) == "SpiderDropper Web Variant";
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
        return new ObjectPose(positionSame ? previous!.Position : trs.Slice(0, 3).ToArray(),
            rotationSame ? previous!.Rotation : trs.Slice(3, 4).ToArray(),
            scaleSame ? previous!.Scale : trs.Slice(7, 3).ToArray(), active);
    }
    public static ObjectPose StaticActive(ObjectPose previous, bool active) => previous.Active == active ? previous :
        new ObjectPose(previous.Position, previous.Rotation, previous.Scale, active, previous.Nodes);
    private static bool Same(float[]? previous, ReadOnlySpan<float> values)
    {
        if (previous == null || previous.Length != values.Length) return false;
        for (int i = 0; i < values.Length; i++) if (previous[i] != values[i]) return false;
        return true;
    }
}
