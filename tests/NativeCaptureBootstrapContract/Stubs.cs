using System;
using System.Collections;
using System.Runtime.CompilerServices;

namespace PeakReplayLab { internal static class ReplaySafety { internal static bool Active; } }
namespace Peak
{
    public static class Quicksave
    {
        public static int Restores;
        public static bool ShouldUseSaveData;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void PopulateMapAndPlayerStates() { Restores++; }
    }
}
public sealed class CharacterSpawner
{
    public int Spawned;
    public bool ThrowInside;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SpawnHostCharacter()
    {
        if (Peak.Quicksave.ShouldUseSaveData) Peak.Quicksave.PopulateMapAndPlayerStates();
        if (ThrowInside) throw new InvalidOperationException("native failure");
        Spawned++;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] public void Tick() => SpawnHostCharacter();
}
public class LoadingScreen
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual IEnumerator LoadingRoutine(Action runAfter, IEnumerator[] processList)
    {
        foreach (var process in processList) while (process.MoveNext()) yield return process.Current;
        runAfter?.Invoke();
    }
}
