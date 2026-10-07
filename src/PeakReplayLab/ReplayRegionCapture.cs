using System;
using HarmonyLib;
using System.Threading;
using UnityEngine;

namespace PeakReplayLab;

// Observe native outcomes once. No extra object search, stats scan, transform
// sampling or filesystem work is added to the 60 Hz recording loop.
internal sealed class ReplayRegionCapture : IDisposable
{
    private static ReplayRegionCapture? active;
    private readonly ReplayHeader header;
    private readonly Harmony harmony = new("cn.mylus.peakreplaylab.regions." + Guid.NewGuid().ToString("N"));
    private bool peakExtraction, nadirEntered;
    public ReplayRegionCapture(ReplayHeader header, Action<string>? warning)
    {
        this.header = header; active = this;
        try
        {
            var target = AccessTools.DeclaredMethod(typeof(CharacterStats), nameof(CharacterStats.Win))
                ?? throw new MissingMethodException("CharacterStats.Win");
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(ReplayRegionCapture), nameof(ObserveNativeWin)));
        }
        catch (Exception e)
        {
            harmony.UnpatchSelf();
            try { warning?.Invoke("登顶结局封面观测未安装，录像仍可使用：" + e.Message); } catch { }
        }
    }
    public void Apply(ReplayFrame frame)
    {
        nadirEntered |= frame.World.Segment == (int)Segment.Void;
        frame.CoverPeakExtraction = peakExtraction && !nadirEntered;
        frame.CoverNadirEntered = nadirEntered;
    }
    public void Dispose()
    {
        if (ReferenceEquals(active, this)) active = null;
        harmony.UnpatchSelf();
    }

    private static void ObserveNativeWin(CharacterStats __instance)
    {
        var capture = active;
        if (capture == null || ReplaySafety.Active || RunSettings.isMiniRun || !__instance || !__instance.won) return;
        // Win sets wonViaNadir before this postfix. FixLastEntry later writes
        // biome Peak for BOTH endings, so that stats timeline is not evidence.
        if (__instance.wonViaNadir) capture.nadirEntered = true;
        else capture.peakExtraction = true;
        Volatile.Write(ref capture.header.CoverOutcome, new ReplayRegionOutcome(Time.timeAsDouble, capture.nadirEntered));
    }
}
