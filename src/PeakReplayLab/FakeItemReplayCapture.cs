using System;
using System.Collections.Generic;
using UnityEngine;

namespace PeakReplayLab;

// FakeItem is native scene scenery, not an Item instance. Keep it out of the
// dynamic interaction loop: only admission and completed visibility callbacks
// may touch a native object. The immutable directory is a full seek baseline.
internal sealed class FakeItemReplayCapture : IDisposable
{
    private sealed class Binding
    {
        internal FakeItem Source = null!;
        internal GameObject Root = null!;
        internal string Key = "", Resource = "", Path = "";
        internal bool Active;
        internal IDisposable? Subscription;
    }
    private readonly StaticCaptureDirectory<Binding, InteractionReplayFrame> directory = new();
    private readonly Dictionary<int, Binding> bindings = new();
    private readonly Action<string>? warning;
    private readonly Func<Binding, InteractionReplayFrame?, InteractionReplayFrame?> read;
    private readonly Action<Binding, Exception> failed;
    private readonly Func<bool> beginRead = CaptureBootstrapWorkBudget.TryBeginRead;
    private long serial;
    private bool disposed;
    internal int Count => directory.Count;
    internal bool InitialBaselineReady => directory.InitialBaselineReady;
    internal long VisibilityReads { get; private set; }
    internal long RootReads { get; private set; }
    internal long EventWakes { get; private set; }
    internal FakeItemReplayCapture(Action<string>? warning = null)
    {
        this.warning = warning; read = Read;
        failed = (_, error) => this.warning?.Invoke("静态采集物初态尚未准备好，保留上一状态：" + error.Message);
    }
    internal bool Contains(int id) => bindings.ContainsKey(id);
    internal void Register(FakeItem source, string resource, string path, bool urgent = false)
    {
        if (disposed || ReplaySafety.Active || !source) return;
        var root = source.gameObject; int id = root.GetInstanceID();
        if (bindings.TryGetValue(id, out var previous))
        {
            if (previous.Source == source && previous.Resource == resource && previous.Path == path)
            { VisibilityChanged(id, previous, urgent); return; }
            Remove(id);
        }
        if (directory.Count >= InteractionReplayRules.MaximumStaticFakeItems)
        { warning?.Invoke("静态采集物目录超过8192个，超出部分未录制。"); return; }
        VisibilityReads++;
        var binding = new Binding { Source = source, Root = root, Resource = resource, Path = path,
            Key = "fake-item:" + id + ":" + ++serial, Active = root.activeSelf };
        bindings.Add(id, binding); directory.Register(id, binding, urgent);
        binding.Subscription = CaptureRootLifecycleObserver.Bind(root, () => VisibilityChanged(id, binding, true));
    }
    private void VisibilityChanged(int id, Binding binding, bool urgent)
    {
        if (disposed || ReplaySafety.Active || !bindings.TryGetValue(id, out var current) || !ReferenceEquals(binding, current)) return;
        if (!binding.Source || !binding.Root) { Remove(id); return; }
        VisibilityReads++;
        bool active = binding.Root.activeSelf;
        // Ancestor/biome activation also invokes OnEnable/Disable. Its own
        // activeSelf did not change, so it cannot dirty every static child.
        if (binding.Active == active) return;
        binding.Active = active; EventWakes++; directory.Changed(id, urgent);
    }
    private InteractionReplayFrame? Read(Binding binding, InteractionReplayFrame? previous)
    {
        if (!binding.Source || !binding.Root) return null;
        VisibilityReads++; bool active = binding.Active = binding.Root.activeSelf;
        if (previous != null)
        {
            if (previous.Pose.Active == active) return previous;
            return new InteractionReplayFrame { Key = previous.Key, Kind = previous.Kind, Resource = previous.Resource,
                SourcePath = previous.SourcePath, Pose = CreatureDirectoryRules.StaticActive(previous.Pose, active) };
        }
        // The audited native visibility methods never move this stationary
        // root. One initial TRS suffices; no renderer/material/bone discovery.
        RootReads++; var root = binding.Root.transform;
        root.GetPositionAndRotation(out var p, out var r); var s = root.lossyScale;
        return new InteractionReplayFrame { Key = binding.Key, Kind = "fake-item", Resource = binding.Resource, SourcePath = binding.Path,
            Pose = new ObjectPose(new[] { p.x, p.y, p.z }, new[] { r.x, r.y, r.z, r.w }, new[] { s.x, s.y, s.z }, active) };
    }
    internal InteractionReplayFrame[] Capture(double now) => directory.Capture(now, read, failed, beginRead);
    internal bool Remove(int id)
    {
        if (!bindings.TryGetValue(id, out var binding)) return false;
        binding.Subscription?.Dispose(); bindings.Remove(id); directory.Remove(id); return true;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var binding in bindings.Values) binding.Subscription?.Dispose();
        bindings.Clear(); directory.Clear();
    }
}
