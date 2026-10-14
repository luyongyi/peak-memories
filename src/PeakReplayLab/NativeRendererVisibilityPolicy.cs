using System;
using System.Collections.Generic;

namespace PeakReplayLab;

// Pose may rewrite visibility before appearance runs. Resolve current-frame
// renderers first, then hide omissions, without toggling present meshes off/on.
internal sealed class NativeRendererVisibilityPolicy<T> where T : class
{
    private readonly HashSet<T> present = new();
    private readonly Func<T, bool> read;
    private readonly Action<T, bool> write;
    public NativeRendererVisibilityPolicy(Func<T, bool> read, Action<T, bool> write)
    { this.read = read; this.write = write; }
    public void Begin() => present.Clear();
    public void SetDesired(T renderer, bool desired)
    {
        present.Add(renderer);
        if (read(renderer) != desired) write(renderer, desired);
    }
    public void HideAbsent(T renderer)
    { if (!present.Contains(renderer) && read(renderer)) write(renderer, false); }
    public void Clear() => present.Clear();
}
