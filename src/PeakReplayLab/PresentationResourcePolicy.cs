using System;
using System.Collections.Generic;

namespace PeakReplayLab;

// A native resource incompatibility is not transient. Retrying the same failed
// particle prefab every second created a periodic main-thread allocation spike.
public sealed class PresentationResourceFailures
{
    private readonly HashSet<string> failed = new(StringComparer.Ordinal);
    private readonly int maximum;
    public PresentationResourceFailures(int maximum = 4096)
    { if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum)); this.maximum = maximum; }
    public bool Allows(string key) => failed.Count < maximum && !failed.Contains(key);
    public void Reject(string key) { if (failed.Count < maximum) failed.Add(key); }
    public static bool CopySpriteSlot(bool spritesMode, bool hasSprite) => spritesMode && hasSprite;
}
