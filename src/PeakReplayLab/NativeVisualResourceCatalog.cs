using System;
using System.Collections.Generic;

namespace PeakReplayLab;

// One fallback directory per loaded scene, shared by all visual owners in that
// scene. Direct prefab lookups stay local; only an unresolved asset expands this
// directory. The final lease releases all strong resource references.
internal sealed class NativeVisualResourceCatalog<T> where T : class
{
    internal sealed class Directory
    {
        public readonly Dictionary<string, T> Values = new(StringComparer.Ordinal);
        public int Owners;
        public bool Expanded, Valid = true;
        public T[]? Discovering;
        public int Discovered;
    }
    private readonly Dictionary<int, Directory> scenes = new();
    private readonly Func<T, bool> usable;
    private readonly Func<T, T, bool>? prefer;
    internal int SceneCount => scenes.Count;

    public NativeVisualResourceCatalog(Func<T, bool> usable, Func<T, T, bool>? prefer = null)
    { this.usable = usable; this.prefer = prefer; }

    public Lease Acquire(int scene)
    {
        if (!scenes.TryGetValue(scene, out var directory)) scenes.Add(scene, directory = new Directory());
        directory.Owners++;
        return new Lease(this, scene, directory);
    }

    // Loaded/unloaded additive scenes can change the set of native fallback
    // assets even when the active scene handle stays the same.
    public void Refresh()
    {
        foreach (var directory in scenes.Values)
        { directory.Values.Clear(); directory.Expanded = false; directory.Discovering = null; directory.Discovered = 0; }
    }

    public void Unload(int scene)
    {
        if (!scenes.TryGetValue(scene, out var directory)) return;
        scenes.Remove(scene); directory.Values.Clear(); directory.Discovering = null; directory.Valid = false;
    }

    internal sealed class Lease : IDisposable
    {
        private readonly NativeVisualResourceCatalog<T> owner;
        private readonly int scene;
        private readonly Directory directory;
        private bool disposed;
        internal int Count => directory.Values.Count;

        internal Lease(NativeVisualResourceCatalog<T> owner, int scene, Directory directory)
        { this.owner = owner; this.scene = scene; this.directory = directory; }

        public void Register(string key, T value)
        {
            if (disposed || !directory.Valid || !owner.usable(value)) return;
            if (!directory.Values.TryGetValue(key, out var existing) || !owner.usable(existing) || owner.prefer?.Invoke(existing, value) == true)
                directory.Values[key] = value;
        }

        public bool TryGet(string key, out T value)
        {
            if (!disposed && directory.Valid && directory.Values.TryGetValue(key, out var found))
            {
                // A native runtime asset can be destroyed after discovery. Never
                // hand its stale wrapper (or a subsequently marked copy) back.
                if (owner.usable(found)) { value = found; return true; }
                directory.Values.Remove(key);
            }
            value = null!; return false;
        }

        public void Expand(Action<Action<string, T>> discover)
        {
            if (disposed || !directory.Valid || directory.Expanded) return;
            directory.Discovering = null; directory.Discovered = 0;
            directory.Expanded = true;
            try { discover(Register); }
            catch { directory.Values.Clear(); directory.Expanded = false; throw; }
        }

        // Unity's resource snapshot itself cannot be interrupted, but processing
        // thousands of names/shaders afterwards must not be one seek-time step.
        // All owners continue the same cursor, including after the initiating
        // visual is pooled or its target is replaced by another seek.
        public bool ExpandIncremental(Func<T[]> discover, Func<T, string> identity, Func<bool> step)
        {
            if (disposed || !directory.Valid || directory.Expanded) return true;
            try
            {
                if (directory.Discovering == null)
                {
                    if (!step()) return false;
                    directory.Discovering = discover(); directory.Discovered = 0;
                    // Do not combine a native resource snapshot with all its
                    // managed indexing work in the same admission.
                    return false;
                }
                while (directory.Discovered < directory.Discovering.Length)
                {
                    if (!step()) return false;
                    var value = directory.Discovering[directory.Discovered++];
                    if (owner.usable(value)) Register(identity(value), value);
                }
                directory.Discovering = null; directory.Discovered = 0; directory.Expanded = true;
                return true;
            }
            catch
            {
                directory.Values.Clear(); directory.Discovering = null; directory.Discovered = 0; directory.Expanded = false;
                throw;
            }
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (--directory.Owners > 0) return;
            directory.Values.Clear(); directory.Discovering = null; directory.Valid = false;
            // An unloaded scene handle may already have a new directory. An old
            // lease must not remove a newer scene/session's resource ownership.
            if (owner.scenes.TryGetValue(scene, out var current) && ReferenceEquals(current, directory)) owner.scenes.Remove(scene);
        }
    }
}
