using PeakReplayLab;
using UnityEngine;

namespace PeakReplayLab;

// This suite isolates the ordinary static interaction renderer. The real fake
// pickup capture/playback implementation is exercised by FakeItemReplayContract.
internal sealed class FakeItemReplayPlayback : IDisposable
{
    internal FakeItemReplayPlayback(Func<InteractionReplayFrame, GameObject?> resolve, PresentationWorkBudget budget, Action<string>? warning) { }
    internal bool Pending => false;
    internal int MissingCount => 0;
    internal int VisibleCount => 0;
    internal void Begin(InteractionReplayFrame[]? snapshot = null, bool discontinuity = false) { }
    internal void Apply(InteractionReplayFrame frame, bool discontinuity) => throw new InvalidOperationException("Use FakeItemReplayContract for fake pickup frames.");
    internal void End() { }
    public void Dispose() { }
}
