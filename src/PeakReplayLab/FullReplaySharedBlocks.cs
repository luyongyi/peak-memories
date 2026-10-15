using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace PeakReplayLab;

// At most 3 MiB of unparsed scratch survives between page reads. Oversized
// blocks may grow a private lease up to the existing record bound, but that
// storage is discarded on return. There are no cached decoded snapshots here.
internal sealed class FullReplaySharedScratchPool : IDisposable
{
    internal const int MaximumRetainedLength = 1024 * 1024;
    internal sealed class Scratch
    {
        internal byte[] Bytes = Array.Empty<byte>();
        internal char[] Characters = Array.Empty<char>();
    }
    private readonly object gate = new();
    private Scratch? spare;
    private bool disposed;
    internal Scratch Rent()
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(FullReplaySharedScratchPool));
            var result = spare ?? new Scratch(); spare = null; return result;
        }
    }
    internal void Return(Scratch scratch)
    {
        if (scratch.Bytes.Length > MaximumRetainedLength) scratch.Bytes = Array.Empty<byte>();
        if (scratch.Characters.Length > MaximumRetainedLength) scratch.Characters = Array.Empty<char>();
        lock (gate)
        {
            if (disposed) return;
            if (spare == null || (long)spare.Bytes.Length + spare.Characters.Length * 2 < (long)scratch.Bytes.Length + scratch.Characters.Length * 2)
                spare = scratch;
        }
    }
    public void Dispose() { lock (gate) { disposed = true; spare = null; } }
}

// This is a full-run container feature, not a clip codec. A shared block is a
// complete immutable entity or leaf array and can never contain another reference.
// The bounded catalogue stores hashes/offsets, not the history's object trees.
internal sealed class FullReplaySharedWriter
{
    private readonly int schema;
    internal FullReplaySharedWriter(int schema = ReplayRules.CurrentSchema) { this.schema = schema; }
    private sealed class Cached { internal int Id; internal Cached(int id) { Id = id; } }
    private sealed class NodeTemplate
    {
        internal NodePose[] Nodes = Array.Empty<NodePose>(); internal int Id; internal long Bytes, Used;
    }
    internal const long MaximumNodeTemplateBytes = 8 * 1024 * 1024;
    internal const int MaximumNodeTemplates = 128;
    internal const int MaximumDeferredHashes = FullReplayArchive.MaximumSharedBlocks;
    // This is worker-owned. Do not depend on Mono ephemeron lookups during a
    // concurrent native GC; eviction only repeats content hashing, never changes
    // the on-disk catalogue or an already published shared reference.
    private readonly BoundedReferenceCache<object, Cached> identities = new(2048);
    internal const int MaximumCachedIdentityRawBytes = 4096;
    private readonly HashSet<string> entities = new(StringComparer.Ordinal);
    // Only hashes are retained while an active state may become static. On a
    // second identical page baseline, its current complete raw record can be
    // promoted to a whole-entity block without retaining historical trees/raw.
    private readonly HashSet<string> deferredHashes = new(StringComparer.Ordinal);
    private readonly Queue<string> deferredOrder = new();
    internal int DeferredHashCount => deferredHashes.Count;
    private readonly Dictionary<string, NodeTemplate> nodeTemplates = new(StringComparer.Ordinal);
    internal long NodeTemplateBytes { get; private set; }
    internal int NodeTemplateCount => nodeTemplates.Count;
    private long useClock;
    private readonly Dictionary<string, int> hashes = new(StringComparer.Ordinal);
    private readonly List<byte[]> pending = new();
    private readonly HashSet<int> pageReferences = new();
    private long pageRawBytes;
    internal readonly List<FullReplaySharedBlock> Blocks = new();
    private int flushed;
    internal long EncodeBaseline(object encoded, ReplayFrame source)
    {
        var record = (Dictionary<string, object?>)encoded;
        // Only a page's baseline emits shared references. Later frame deltas
        // keep those decoded objects alive and do not resolve them again.
        // Match SharedSnapshotReader: count each resolved block ID once for
        // this page, separately from the 8 MiB limit on any individual record.
        pageReferences.Clear(); pageRawBytes = 0;
        // A direct typed directory owns complete immutable records, never
        // references to entity/leaf blocks. Only large cold scene directories
        // use it; active native clocks remain small independent page patches.
        bool packedDirectory = schema >= 26 && EncodeStaticDirectory();
        Encode("Items", source.Items, 0); Encode("Crates", source.Crates, 1);
        Encode("Ropes", source.Ropes, 2);
        if (!packedDirectory) Encode("Creatures", source.Creatures, 3);
        Encode("Interactions", source.Interactions, 4); Encode("Spawned", source.Spawned, 5);
        foreach (string name in new[] { "ActorOrder", "ItemOrder", "CrateOrder", "RopeOrder", "EffectOrder", "AudioOrder", "SpawnedOrder", "BalloonOrder", "CreatureOrder", "InteractionOrder" })
            if (schema >= 26 && record.TryGetValue(name, out var order) && order is string[] keys &&
                keys.Length <= SharedOrderMaximum(schema) && (keys.Length >= 256 || keys.Sum(key => (long)key.Length) >= 8192) && TryLeaf(keys, 9, out int id))
            { record[name] = Reference(id); CountReference(id); }
        return pageRawBytes;

        bool EncodeStaticDirectory()
        {
            if (!record.TryGetValue("Creatures", out var field) || field is not object[] updates) return false;
            if (updates.Length != source.Creatures.Length) throw new InvalidDataException("Shared snapshots require a full page baseline.");
            int count = source.Creatures.Count(StaticDirectoryEntry);
            // Below this point, individual references have lower replacement
            // cost when one entity wakes or changes state.
            if (count < 256) return false;
            var cold = source.Creatures.Where(StaticDirectoryEntry).ToArray();
            if (cold.Sum(CreatureReplayRules.Estimate) > ReplayRules.MaxLineCharacters) return false;
            if (!TryLeaf(cold, 10, out int id)) return false;
            record["StaticCreatures"] = Reference(id); CountReference(id);
            var active = new List<object>();
            var activeValues = new List<CreatureReplayFrame>();
            for (int i = 0; i < updates.Length; i++)
                if (!StaticDirectoryEntry(source.Creatures[i])) { active.Add(updates[i]); activeValues.Add(source.Creatures[i]); }
            record["Creatures"] = active.ToArray();
            Encode("Creatures", activeValues.ToArray(), 3);
            if (active.Count == 0) record.Remove("Creatures");
            return true;
        }

        void Encode<T>(string track, T[] values, int kind) where T : class
        {
            if (!record.TryGetValue(track, out var field)) return;
            var updates = (object[])field!;
            if (updates.Length != values.Length) throw new InvalidDataException("Shared snapshots require a full page baseline.");
            for (int i = 0; i < updates.Length; i++)
            {
                // Audited native animation checkpoints keep immutable geometry
                // in direct leaves BEFORE any full entity JSON/hash work. A
                // phase anchor can never enter deferred whole-rig promotion.
                if (values[i] is CreatureReplayFrame { IdleAnimation: not null } || values[i] is ItemFrame { IdleAnimation: not null })
                {
                    var nativePatch = (Dictionary<string, object?>)updates[i];
                    EncodeLeaves(nativePatch, kind + ":" + (string)nativePatch["Key"]!);
                    continue;
                }
                int id;
                if (identities.TryGetValue(values[i], out var cached)) id = cached.Id;
                else
                {
                    byte[] raw = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(updates[i], ReplayFiles.Json));
                    // Compact traps and fake item sources are event-driven
                    // scene state. Share their small complete snapshots so
                    // independent pages do not repeat dormant source paths.
                    bool compactTrap = values[i] is CreatureReplayFrame creature && CreatureReplayRules.CompactStaticTrap(creature);
                    bool compactFake = schema >= 27 && values[i] is InteractionReplayFrame interaction && InteractionReplayRules.CompactFakeItem(interaction);
                    if (raw.Length < 512 && !compactTrap && !compactFake) continue;
                    if (raw.Length > ReplayRules.MaxLineCharacters) throw new InvalidDataException("Shared snapshot exceeds its raw byte limit.");
                    string hash = FullReplayArchive.SharedHash(kind, raw);
                    if (!hashes.TryGetValue(hash, out id))
                    {
                        var patch = (Dictionary<string, object?>)updates[i];
                        string key = kind + ":" + (string)patch["Key"]!;
                        // Keep unchanged entities as one tiny whole-record reference.
                        // Changed entities instead own their absolute page patch;
                        // their immutable leaves are shared directly, never via a
                        // shared entity block containing another reference.
                        // Rope Points are a large, mostly unchanged array that
                        // has no leaf representation. Keeping ropes whole avoids
                        // rewriting their entire geometry after a pull settles.
                        bool promote = deferredHashes.Contains(hash) && Blocks.Count < FullReplayArchive.MaximumSharedBlocks;
                        if (kind != 2 && entities.Contains(key) && !promote)
                        {
                            if (EncodeLeaves(patch, key))
                            {
                                RememberDeferred(hash);
                                continue;
                            }
                        }
                        // Exhausting the catalogue reduces compression only:
                        // new states stay inline, all old references stay valid.
                        if (Blocks.Count >= FullReplayArchive.MaximumSharedBlocks) continue;
                        id = AddBlock(kind, raw, hash);
                        deferredHashes.Remove(hash);
                        if (entities.Count < FullReplayArchive.MaximumSharedBlocks) entities.Add(key);
                    }
                    // Large snapshots remain deduplicated by content hash. Keep
                    // only small source trees in the bounded identity shortcut.
                    if (raw.Length <= MaximumCachedIdentityRawBytes)
                        identities.GetValue(values[i], _ => new Cached(id));
                }
                CountReference(id);
                updates[i] = new Dictionary<string, object?> { ["$Shared"] = id };
            }
        }
    }

    internal static bool StaticDirectoryEntry(CreatureReplayFrame frame) => CreatureReplayRules.CompactStaticTrap(frame) ||
        frame.Kind == "spider" && frame.ClockMode == CreatureReplayRules.IdleClock && frame.IdleAnimation != null;

    // A leaf is typed only as string[], so its scanner uses the largest track
    // allowance. The consuming track still enforces its own entity limit.
    internal static int SharedOrderMaximum(int schema) => schema >= 27
        ? Math.Max(ReplayRules.MaxCreatures, ReplayRules.MaxInteractions) : ReplayRules.MaxCreatures;

    private void CountReference(int id)
    {
        if (!pageReferences.Add(id)) return;
        pageRawBytes = checked(pageRawBytes + Blocks[id].RawBytes);
        if (pageRawBytes > FullReplayArchive.MaximumPageRawBytes)
            throw new InvalidDataException("Shared snapshot expansion exceeds the page budget.");
    }

    private void RememberDeferred(string hash)
    {
        if (!deferredHashes.Add(hash)) return;
        deferredOrder.Enqueue(hash);
        while (deferredOrder.Count > MaximumDeferredHashes) deferredHashes.Remove(deferredOrder.Dequeue());
    }

    private bool EncodeLeaves(Dictionary<string, object?> patch, string key)
    {
        bool shared = false;
        Leaf("Visuals", 6); Leaf("Lights", 7);
        if (patch.TryGetValue("Pose", out var poseValue) && poseValue is Dictionary<string, object?> pose &&
            pose.TryGetValue("Nodes", out var nodesValue) && nodesValue is NodePose[] nodes && nodes.Length != 0)
        {
            if (nodeTemplates.TryGetValue(key, out var template))
            {
                template.Used = ++useClock;
                object delta = ReplayDeltaCodec.NodeDelta(nodes, template.Nodes);
                if (delta is Dictionary<string, object?> changes)
                {
                    // These values are absolute against one immutable leaf, not
                    // cumulative changes against a preceding page.
                    if (changes["Changes"] is object[] empty && empty.Length == 0) pose["Nodes"] = Reference(template.Id);
                    else { changes.Add("$Shared", template.Id); pose["Nodes"] = changes; }
                    CountReference(template.Id); shared = true;
                }
                else UseNewNodes(); // Topology changed: complete independent leaf.
            }
            else UseNewNodes();

            void UseNewNodes()
            {
                if (!TryLeaf(nodes, 8, out int id)) return;
                pose["Nodes"] = Reference(id); CountReference(id); shared = true;
                RememberNodes(key, nodes, id);
            }
        }
        return shared;

        void Leaf(string name, int kind)
        {
            if (!patch.TryGetValue(name, out var value) || value is not Array array || array.Length == 0 || !TryLeaf(value, kind, out int id)) return;
            patch[name] = Reference(id); CountReference(id); shared = true;
        }
    }
    private static Dictionary<string, object?> Reference(int id) => new() { ["$Shared"] = id };
    private bool TryLeaf(object value, int kind, out int id)
    {
        if (identities.TryGetValue(value, out var cached)) { id = cached.Id; return true; }
        // The wrapper allows the existing canonical typed JSON scanner to
        // validate grammar once without constructing any JObject/JArray tree.
        byte[] raw = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new Dictionary<string, object?> { ["Value"] = value }, ReplayFiles.Json));
        id = -1;
        if (raw.Length < 256) return false;
        if (raw.Length > ReplayRules.MaxLineCharacters)
        {
            if (kind == 10) return false; // Grouping must never reduce valid page capacity.
            throw new InvalidDataException("Shared leaf exceeds its raw byte limit.");
        }
        string hash = FullReplayArchive.SharedHash(kind, raw);
        if (!hashes.TryGetValue(hash, out id))
        {
            if (Blocks.Count >= FullReplayArchive.MaximumSharedBlocks) return false;
            id = AddBlock(kind, raw, hash);
        }
        if (raw.Length <= MaximumCachedIdentityRawBytes)
        {
            int cachedId = id;
            identities.GetValue(value, _ => new Cached(cachedId));
        }
        return true;
    }
    private int AddBlock(int kind, byte[] raw, string hash)
    {
        int id = Blocks.Count;
        Blocks.Add(new FullReplaySharedBlock { Kind = kind, RawBytes = raw.Length, Hash = hash });
        hashes.Add(hash, id); pending.Add(raw); return id;
    }
    private void RememberNodes(string key, NodePose[] nodes, int id)
    {
        if (nodeTemplates.TryGetValue(key, out var old)) { NodeTemplateBytes -= old.Bytes; nodeTemplates.Remove(key); }
        long size = EstimateNodes(nodes) + 128 + key.Length * 2L;
        if (size > MaximumNodeTemplateBytes) return;
        while (nodeTemplates.Count >= MaximumNodeTemplates || NodeTemplateBytes + size > MaximumNodeTemplateBytes)
        {
            string? oldest = null; long stamp = long.MaxValue;
            foreach (var entry in nodeTemplates) if (entry.Value.Used < stamp) { oldest = entry.Key; stamp = entry.Value.Used; }
            if (oldest == null) break;
            NodeTemplateBytes -= nodeTemplates[oldest].Bytes; nodeTemplates.Remove(oldest);
        }
        nodeTemplates.Add(key, new NodeTemplate { Nodes = nodes, Id = id, Bytes = size, Used = ++useClock }); NodeTemplateBytes += size;
    }
    internal static long EstimateNodes(NodePose[] nodes)
    {
        long size = 32 + nodes.Length * 8L;
        foreach (var node in nodes) size += 248 + node.Path.Length * 2L;
        return size;
    }

    internal void FlushPending(Stream file)
    {
        foreach (byte[] raw in pending)
        {
            var block = Blocks[flushed++]; block.Offset = file.Position;
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest, true)) gzip.Write(raw, 0, raw.Length);
            block.Length = file.Position - block.Offset;
        }
        pending.Clear();
    }
}

public static partial class FullReplayArchive
{
    // ~5 MiB of index metadata in the worst case, leaving room for 32k pages.
    public const int MaximumSharedBlocks = 32768;
    private static readonly Type[] SharedTypes = { typeof(ItemFrame), typeof(CrateFrame), typeof(RopeReplayFrame),
        typeof(CreatureReplayFrame), typeof(InteractionReplayFrame), typeof(SpawnedReplayFrame),
        typeof(NativeRendererFrame[]), typeof(NativeLightFrame[]), typeof(NodePose[]), typeof(string[]), typeof(CreatureReplayFrame[]) };

    internal static string SharedHash(int kind, byte[] raw, int length = -1)
    {
        using var sha = SHA256.Create();
        // Kind is part of the content identity: equal JSON in two tracks must
        // not silently resolve to an object of the wrong runtime type.
        byte[] prefix = { (byte)kind };
        sha.TransformBlock(prefix, 0, 1, prefix, 0);
        // TransformFinalBlock returns a copy of its input. Feed the payload as
        // a transform block so hashing does not allocate another raw snapshot.
        sha.TransformBlock(raw, 0, length < 0 ? raw.Length : length, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return BitConverter.ToString(sha.Hash!).Replace("-", "");
    }

    private static void ValidateSharedBlock(FullReplaySharedBlock block, long offset, long indexOffset)
    {
        if (block == null || block.Kind < 0 || block.Kind >= SharedTypes.Length || block.Offset != offset ||
            block.Length < 18 || block.Length > indexOffset - offset || block.RawBytes < 1 || block.RawBytes > ReplayRules.MaxLineCharacters ||
            block.Hash == null || block.Hash.Length != 64)
            throw Invalid("Invalid full replay shared snapshot bounds.");
        foreach (char c in block.Hash)
            if (!(c >= '0' && c <= '9' || c >= 'A' && c <= 'F')) throw Invalid("Invalid shared snapshot hash.");
    }

    // One resolver belongs to one page load. No second long-lived cache sits
    // outside the 1 GiB seek cache; requested typed objects become page state.
    // A separate file handle keeps random block reads out of the page inflater.
    private sealed class SharedSnapshotReader : IDisposable
    {
        private readonly FullReplayInfo info;
        private readonly int through;
        private readonly CancellationToken cancellation;
        private readonly FileStream file;
        private readonly ReplayDeltaCodec.StreamReader decoder;
        private readonly Dictionary<int, object> decoded = new();
        private readonly byte[] footer = new byte[8];
        // Reuse scratch across the hundreds of small immutable blocks in a
        // scene baseline; otherwise byte/string copies dominate seek GC.
        private readonly FullReplaySharedScratchPool? scratchPool;
        private readonly FullReplaySharedScratchPool.Scratch scratch;
        private bool disposed;
        private long rawBytes, decodedBytes;
        internal SharedSnapshotReader(string path, FullReplayInfo info, int through, CancellationToken cancellation, FullReplaySharedScratchPool? scratchPool)
        {
            this.info = info; this.through = through; this.cancellation = cancellation;
            file = Open(path);
            if (file.Length != info.FileLength) { file.Dispose(); throw Invalid("Full replay changed after its index was read."); }
            decoder = new ReplayDeltaCodec.StreamReader(info.Header.Schema);
            this.scratchPool = scratchPool;
            try { scratch = scratchPool?.Rent() ?? new FullReplaySharedScratchPool.Scratch(); }
            catch { file.Dispose(); throw; }
        }
        internal object Resolve(Type type, int id)
        {
            cancellation.ThrowIfCancellationRequested();
            if (id < 0 || id >= through || info.SharedBlocks == null || id >= info.SharedBlocks.Length)
                throw Invalid("Unknown or future full replay shared snapshot.");
            var block = info.SharedBlocks[id];
            if (SharedTypes[block.Kind] != type) throw Invalid("Shared snapshot has the wrong entity type.");
            if (decoded.TryGetValue(id, out var cached)) return cached;
            rawBytes = checked(rawBytes + block.RawBytes);
            if (rawBytes > MaximumPageRawBytes) throw Invalid("Shared snapshot expansion exceeds the page budget.");
            file.Position = block.Offset;
            // Capture the gzip trailer during the same forward read. Hundreds
            // of immutable blocks no longer each seek to their end and back.
            using var slice = new PageSlice(file, block.Length, cancellation, footer);
            using var gzip = new GZipStream(slice, CompressionMode.Decompress, true);
            using var raw = new CheckedRawStream(gzip, block.RawBytes, cancellation);
            if (scratch.Bytes.Length < block.RawBytes) scratch.Bytes = new byte[Math.Min(ReplayRules.MaxLineCharacters, Math.Max(block.RawBytes, scratch.Bytes.Length * 2))];
            byte[] rawBuffer = scratch.Bytes;
            int read = 0;
            while (read < block.RawBytes)
            {
                cancellation.ThrowIfCancellationRequested();
                int next = raw.Read(rawBuffer, read, block.RawBytes - read);
                if (next == 0) throw Invalid("Truncated shared snapshot.");
                read += next;
            }
            if (raw.ReadByte() != -1 || slice.Remaining != 0 || UInt32(footer, 4) != block.RawBytes ||
                raw.Crc != UInt32(footer, 0) || SharedHash(block.Kind, rawBuffer, block.RawBytes) != block.Hash)
                throw Invalid("Shared snapshot bytes do not match the index.");
            if (scratch.Characters.Length < block.RawBytes) scratch.Characters = new char[Math.Min(ReplayRules.MaxLineCharacters, Math.Max(block.RawBytes, scratch.Characters.Length * 2))];
            char[] textBuffer = scratch.Characters;
            int characters = Utf8.GetChars(rawBuffer, 0, block.RawBytes, textBuffer, 0);
            object value = block.Kind < 6 ? decoder.DecodeSharedRecord(textBuffer, characters, type, "Key") : decoder.DecodeSharedValue(textBuffer, characters, type);
            cancellation.ThrowIfCancellationRequested();
            decodedBytes = checked(decodedBytes + EstimateShared(value));
            if (decodedBytes > MaximumPageDecodedBytes) throw Invalid("Shared snapshots exceed the decoded page budget.");
            decoded.Add(id, value);
            return value;
        }
        private static long EstimateShared(object value) => value switch
        {
            ItemFrame x => RollingBuffer.Estimate(x), CrateFrame x => RollingBuffer.Estimate(x),
            RopeReplayFrame x => RopeReplayRules.Estimate(x), CreatureReplayFrame x => CreatureReplayRules.Estimate(x),
            InteractionReplayFrame x => InteractionReplayRules.Estimate(x), SpawnedReplayFrame x => SpawnedReplayRules.Estimate(x),
            NativeRendererFrame[] x => NativeVisualAppearanceRules.Estimate(x), NativeLightFrame[] x => NativeLightRules.Estimate(x),
            NodePose[] x => FullReplaySharedWriter.EstimateNodes(x),
            string[] x => 32 + x.Length * 8L + x.Sum(text => 24 + text.Length * 2L),
            CreatureReplayFrame[] x => 32 + x.Length * 8L + x.Sum(CreatureReplayRules.Estimate),
            _ => throw Invalid("Unsupported shared snapshot entity."),
        };
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            try { file.Dispose(); } finally { scratchPool?.Return(scratch); }
        }
    }
}
