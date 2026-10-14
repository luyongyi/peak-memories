using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PeakReplayLab;

// The immutable descriptor contains only real stored sample boundaries. A
// previously visited old page can use its small blocks on the next open; an
// uncached page remains an ordinary source page for the current session.
internal sealed class FullReplayPlaybackSource : IDisposable
{
    internal readonly struct Entry
    {
        internal readonly int OriginalIndex, ChildIndex;
        internal readonly FullReplayInfo? Cached;
        internal Entry(int originalIndex, FullReplayInfo? cached = null, int childIndex = 0)
        { OriginalIndex = originalIndex; Cached = cached; ChildIndex = childIndex; }
    }
    private readonly FullReplayInfo original;
    private readonly Entry[] entries;
    private readonly Func<int, CancellationToken, ReplayClip> originalLoad;
    private readonly Func<int, CancellationToken, ReplayClip> originalHistoryLoad;
    private readonly bool createSeekCaches;
    private readonly string fingerprint;
    private readonly long sourceWriteTicks;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly object writeGate = new();
    private readonly ReplayLineBufferPool readBuffers = new();
    private readonly FullReplaySharedScratchPool sharedBuffers = new();
    private int disposed;
    internal Task? CacheWrite { get; private set; }
    internal ReplayPageRange[] Ranges { get; }
    internal long[] DecodedPageBytes { get; }
    internal long[] HistoryDecodedPageBytes { get; }
    internal FullReplayPlaybackSource(FullReplayInfo original, Entry[] entries, string fingerprint, long sourceWriteTicks, bool createSeekCaches)
    {
        this.original = original; this.entries = entries; this.fingerprint = fingerprint; this.sourceWriteTicks = sourceWriteTicks;
        this.createSeekCaches = createSeekCaches;
        lifetimeToken = lifetime.Token;
        originalLoad = FullReplayArchive.CreatePageLoader(original.Path, original, buffers: readBuffers, sharedBuffers: sharedBuffers);
        originalHistoryLoad = FullReplayArchive.CreateEffectPageLoader(original, readBuffers);
        Ranges = entries.Select(e => e.Cached == null ? original.Pages[e.OriginalIndex] : e.Cached.Pages[e.ChildIndex])
            .Select(p => new ReplayPageRange(p.Start, p.End)).ToArray();
        DecodedPageBytes = entries.Select(e => e.Cached == null ? original.Pages[e.OriginalIndex].DecodedBytes : e.Cached.Pages[e.ChildIndex].DecodedBytes).ToArray();
        HistoryDecodedPageBytes = entries.Select(e =>
        {
            var page = e.Cached == null ? original.Pages[e.OriginalIndex] : e.Cached.Pages[e.ChildIndex];
            return page.EffectHistory?.DecodedBytes ?? page.DecodedBytes;
        }).ToArray();
    }
    internal ReplayClip Load(int index, CancellationToken cancellation)
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(FullReplayPlaybackSource));
        cancellation.ThrowIfCancellationRequested();
        FullReplayArchive.CheckSeekSource(original, sourceWriteTicks);
        if (index < 0 || index >= entries.Length) throw new ArgumentOutOfRangeException(nameof(index));
        var entry = entries[index];
        if (entry.Cached != null)
        {
            try { return FullReplayArchive.ReadSeekChild(original, entry.Cached, entry.ChildIndex, cancellation, readBuffers); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (FullReplayArchive.IsSeekCacheFailure(e))
            {
                // A discarded or damaged cache is optional. Keep this immutable
                // session's real child endpoints when falling back to the source.
                var source = originalLoad(entry.OriginalIndex, cancellation);
                return FullReplayArchive.SliceSeekChild(source, entry.Cached.Pages[entry.ChildIndex]);
            }
        }
        var clip = originalLoad(entry.OriginalIndex, cancellation);
        lock (writeGate)
            if (createSeekCaches && Volatile.Read(ref disposed) == 0 && original.Pages[entry.OriginalIndex].End - original.Pages[entry.OriginalIndex].Start > 1.000001 &&
                (CacheWrite == null || CacheWrite.IsCompleted))
                CacheWrite = FullReplayArchive.TryWriteSeekCache(original, entry.OriginalIndex, clip, fingerprint, sourceWriteTicks, lifetimeToken);
        return clip;
    }
    internal ReplayClip LoadHistory(int index, CancellationToken cancellation)
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(FullReplayPlaybackSource));
        cancellation.ThrowIfCancellationRequested(); FullReplayArchive.CheckSeekSource(original, sourceWriteTicks);
        if (index < 0 || index >= entries.Length) throw new ArgumentOutOfRangeException(nameof(index));
        var entry = entries[index];
        if (entry.Cached != null)
        {
            try { return FullReplayArchive.ReadEffectHistory(entry.Cached.Path, entry.Cached, entry.ChildIndex, cancellation, readBuffers); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (FullReplayArchive.IsSeekCacheFailure(e))
            { return FullReplayArchive.SliceSeekChild(originalHistoryLoad(entry.OriginalIndex, cancellation), entry.Cached.Pages[entry.ChildIndex]); }
        }
        if (original.Pages[entry.OriginalIndex].EffectHistory != null)
            return FullReplayArchive.ReadEffectHistory(original.Path, original, entry.OriginalIndex, cancellation, readBuffers);
        // Legacy effect history is a projection, not a second full scene decode.
        // It never schedules optional full-page cache serialization.
        return originalHistoryLoad(entry.OriginalIndex, cancellation);
    }
    public void Dispose()
    {
        Task? pending;
        lock (writeGate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel(); pending = CacheWrite;
        }
        readBuffers.Dispose(); sharedBuffers.Dispose();
        // Never wait for disk IO on the UI thread; keep the cancellation source
        // valid until the admitted cache writer has observed cancellation.
        if (pending == null || pending.IsCompleted) lifetime.Dispose();
        else _ = pending.ContinueWith(_ => lifetime.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

public static partial class FullReplayArchive
{
    private const int SeekCacheVersion = 2;
    internal const long MaximumSeekCacheBytes = 512L * 1024 * 1024;
    private const long MaximumSeekCacheFileBytes = 128L * 1024 * 1024;
    private const int MaximumSeekCacheIndexBytes = 128 * 1024;
    private const int MaximumSeekCacheOpenMetadataBytes = 8 * 1024 * 1024;
    private const int SeekHeaderBytes = 44, SeekFooterBytes = 20;
    private static readonly byte[] SeekMagic = Encoding.ASCII.GetBytes("PEAKSEK1");
    private static readonly byte[] SeekFooter = Encoding.ASCII.GetBytes("PEAKSKND");
    private static int seekWriterBusy;
    private sealed class SeekIndex
    {
        public int Version { get; set; } = SeekCacheVersion;
        public string Fingerprint { get; set; } = "";
        public FullReplayPage Source { get; set; } = new();
        public FullReplayPage[] Pages { get; set; } = Array.Empty<FullReplayPage>();
    }

    internal static FullReplayPlaybackSource OpenPlayback(string path, FullReplayInfo info, CancellationToken cancellation = default, bool createSeekCaches = false)
    {
        // Validate/snapshot metadata, but never scan an unvisited page payload.
        if (info == null) throw new ArgumentNullException(nameof(info));
        cancellation.ThrowIfCancellationRequested();
        string fullPath = System.IO.Path.GetFullPath(path);
        if (!string.Equals(fullPath, info.Path, StringComparison.OrdinalIgnoreCase)) throw Invalid("Full replay index belongs to another file.");
        var snapshot = Convert<FullReplayInfo>(JObject.FromObject(info));
        snapshot.FileLength = info.FileLength; snapshot.HeaderEnd = info.HeaderEnd; snapshot.IndexOffset = info.IndexOffset; snapshot.Path = fullPath;
        ValidateIndex(snapshot); cancellation.ThrowIfCancellationRequested();
        long ticks = new FileInfo(fullPath).LastWriteTimeUtc.Ticks;
        string fingerprint;
        using (var hash = SHA256.Create())
        {
            byte[] input = Utf8.GetBytes(fullPath.ToUpperInvariant() + "\n" + snapshot.FileLength + "\n" + ticks + "\n" +
                snapshot.IndexOffset + "\n" + JsonConvert.SerializeObject(snapshot, ReplayFiles.Json));
            fingerprint = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "");
        }
        var entries = new List<FullReplayPlaybackSource.Entry>(snapshot.Pages.Length);
        int metadataRemaining = MaximumSeekCacheOpenMetadataBytes;
        bool hasDirectory = Directory.Exists(SeekDirectory(fullPath));
        foreach (var page in snapshot.Pages)
        {
            cancellation.ThrowIfCancellationRequested();
            FullReplayInfo? cached = null;
            if (hasDirectory && page.End - page.Start > 1.000001 && metadataRemaining > SeekHeaderBytes + SeekFooterBytes)
            {
                try { cached = ReadSeekIndex(snapshot, page, fingerprint, ref metadataRemaining, cancellation); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) when (IsSeekCacheFailure(e)) { }
            }
            // Leave room for all remaining ordinary source pages before
            // accepting optional children. Never grow a huge intermediate list.
            if (cached != null && entries.Count + cached.Pages.Length + snapshot.Pages.Length - page.Index - 1 > 100000) cached = null;
            if (cached == null) entries.Add(new(page.Index));
            else for (int child = 0; child < cached.Pages.Length; child++) entries.Add(new(page.Index, cached, child));
        }
        if (entries.Count > 100000) throw Invalid("Full replay playback index exceeds its safety limit.");
        cancellation.ThrowIfCancellationRequested();
        // Prebuilding old-page caches is an explicit opt-in operation. Starting
        // a large JSON re-encode during interactive playback adds allocations
        // while the newly generated child pages cannot be used until reopening.
        return new FullReplayPlaybackSource(snapshot, entries.ToArray(), fingerprint, ticks, createSeekCaches);
    }

    internal static bool IsSeekCacheFailure(Exception error) => error is InvalidDataException || error is IOException || error is UnauthorizedAccessException ||
        error is JsonException || error is ArgumentException || error is NotSupportedException;
    internal static void CheckSeekSource(FullReplayInfo info, long ticks)
    {
        var file = new FileInfo(info.Path);
        if (!file.Exists || file.Length != info.FileLength || file.LastWriteTimeUtc.Ticks != ticks)
            throw Invalid("Full replay changed after its playback index was read.");
    }
    internal static ReplayClip ReadSeekChild(FullReplayInfo original, FullReplayInfo cached, int child, CancellationToken cancellation,
        ReplayLineBufferPool? buffers = null)
    {
        var clip = ReadPageCore(cached.Path, cached, JObject.FromObject(original.Header),
            new HashSet<string>(original.ActorIds, StringComparer.Ordinal), child, cancellation, buffers: buffers);
        clip.FilePath = original.Path; return clip;
    }
    internal static ReplayFrame EffectsOnlyFrame(ReplayFrame frame) => new()
    {
        T = frame.T, SceneSampleTimeKnown = frame.SceneSampleTimeKnown, SceneSampleTime = frame.SceneSampleTime,
        Effects = frame.Effects,
        World = new WorldFrame { Segment = frame.World.Segment, ActiveMapObjects = frame.World.ActiveMapObjects },
    };
    internal static ReplayClip EffectsOnly(ReplayClip original)
    {
        var result = new ReplayClip { Header = original.Header, Complete = original.Complete, FilePath = original.FilePath };
        foreach (var frame in original.Frames) result.Frames.Add(EffectsOnlyFrame(frame));
        return result;
    }
    internal static FullReplayEffectHistory WriteEffectHistory(Stream file, ReplayHeader header, List<ReplayFrame> frames, CancellationToken cancellation)
    {
        var clip = new ReplayClip { Header = header, Complete = true };
        clip.Frames.AddRange(frames);
        var page = WriteSeekChild(file, clip, 0, frames.Count - 1, cancellation);
        return new FullReplayEffectHistory { Offset = page.Offset, Length = page.Length, RawBytes = page.RawBytes, DecodedBytes = page.DecodedBytes };
    }
    internal static ReplayClip ReadEffectHistory(string path, FullReplayInfo original, int pageIndex, CancellationToken cancellation,
        ReplayLineBufferPool? buffers = null)
    {
        var source = original.Pages[pageIndex];
        var history = source.EffectHistory ?? throw Invalid("Missing full replay effect history.");
        var descriptor = new FullReplayPage { Index = 0, Start = source.Start, End = source.End, FrameCount = source.FrameCount,
            Offset = history.Offset, Length = history.Length, RawBytes = history.RawBytes, DecodedBytes = history.DecodedBytes };
        var info = new FullReplayInfo { FileLength = original.FileLength, Header = original.Header, ActorIds = original.ActorIds,
            Pages = new[] { descriptor } };
        var clip = ReadPageCore(path, info, JObject.FromObject(original.Header), new HashSet<string>(original.ActorIds, StringComparer.Ordinal), 0, cancellation, buffers: buffers);
        foreach (var frame in clip.Frames)
            if (frame.Actors.Length != 0 || frame.Items.Length != 0 || frame.Crates.Length != 0 || frame.Events.Length != 0 ||
                frame.Ropes.Length != 0 || frame.Audio.Length != 0 || frame.Spawned.Length != 0 || frame.Balloons.Length != 0 ||
                frame.Creatures.Length != 0 || frame.Interactions.Length != 0 || frame.World.Environment != null)
                throw Invalid("Full replay effect history contains unrelated presentation data.");
        return clip;
    }
    internal static ReplayClip SliceSeekChild(ReplayClip original, FullReplayPage page)
    {
        var clip = new ReplayClip { Header = original.Header, Complete = original.Complete, FilePath = original.FilePath };
        foreach (var frame in original.Frames) if (frame.T >= page.Start && frame.T <= page.End) clip.Frames.Add(frame);
        if (clip.Frames.Count != page.FrameCount || clip.Frames[0].T != page.Start || clip.Duration != page.End)
            throw Invalid("Discarded seek cache boundaries disagree with the source recording.");
        return clip;
    }
    private static string SeekDirectory(string path) => path + ".seekcache";
    private static string SeekPath(string path, int page) => System.IO.Path.Combine(SeekDirectory(path), "page-" + page.ToString("D5") + ".seekpage");

    private static FullReplayInfo? ReadSeekIndex(FullReplayInfo source, FullReplayPage page, string fingerprint,
        ref int metadataRemaining, CancellationToken cancellation)
    {
        string path = SeekPath(source.Path, page.Index);
        if (!File.Exists(path)) return null;
        using var file = Open(path);
        if (file.Length < SeekHeaderBytes + SeekFooterBytes + 2 || file.Length > MaximumSeekCacheFileBytes) throw Invalid("Invalid seek cache size.");
        byte[] prefix = ReadExactly(file, SeekHeaderBytes, cancellation);
        metadataRemaining -= SeekHeaderBytes;
        if (!prefix.Take(8).SequenceEqual(SeekMagic) || BitConverter.ToString(prefix.Skip(8).Take(32).ToArray()).Replace("-", "") != fingerprint ||
            Int32(prefix, 40) != page.Index) throw Invalid("Seek cache source fingerprint disagrees.");
        file.Position = file.Length - SeekFooterBytes;
        byte[] footer = ReadExactly(file, SeekFooterBytes, cancellation); metadataRemaining -= SeekFooterBytes;
        long length = Int64(footer, 4);
        if (!footer.Skip(12).SequenceEqual(SeekFooter) || length < 2 || length > MaximumSeekCacheIndexBytes ||
            length > metadataRemaining || length > file.Length - SeekHeaderBytes - SeekFooterBytes) throw Invalid("Invalid seek cache index length.");
        long indexOffset = file.Length - SeekFooterBytes - length;
        file.Position = indexOffset; byte[] encoded = ReadExactly(file, (int)length, cancellation); metadataRemaining -= (int)length;
        if (SeekCrc(encoded) != UInt32(footer, 0)) throw Invalid("Seek cache index CRC disagrees.");
        var index = Convert<SeekIndex>(Parse(encoded));
        if (index.Version != SeekCacheVersion || index.Fingerprint != fingerprint || index.Source == null || index.Pages == null ||
            index.Pages.Length < 2 || index.Pages.Length > 512 ||
            !JToken.DeepEquals(JObject.FromObject(index.Source), JObject.FromObject(page))) throw Invalid("Invalid seek cache source metadata.");
        long expectedOffset = SeekHeaderBytes, uniqueFrames = 0;
        double lastEnd = page.Start;
        for (int i = 0; i < index.Pages.Length; i++)
        {
            var child = index.Pages[i];
            if (child == null || child.Index != i || !ReplayRules.Finite(child.Start) || !ReplayRules.Finite(child.End) ||
                child.Start < page.Start || child.End > page.End || child.End < child.Start || child.End - child.Start > 1.000001 ||
                child.FrameCount < 1 || child.FrameCount > page.FrameCount || child.FrameCount == 1 && child.Start != child.End ||
                child.FrameCount > 1 && child.End <= child.Start || child.Offset != expectedOffset || child.Length < 18 ||
                child.Length > indexOffset - child.Offset || child.RawBytes < 1 || child.RawBytes > MaximumPageRawBytes ||
                child.DecodedBytes < 1 || child.DecodedBytes > page.DecodedBytes) throw Invalid("Invalid seek cache child bounds.");
            if (i == 0)
            {
                if (child.Start != page.Start || child.FirstFrameIndex != page.FirstFrameIndex || child.Overlap != page.Overlap)
                    throw Invalid("Invalid first seek cache child.");
            }
            else if (child.Overlap)
            {
                if (child.Start != lastEnd || child.FirstFrameIndex != page.FirstFrameIndex + uniqueFrames - 1)
                    throw Invalid("Invalid overlapping seek cache child.");
            }
            else if (child.Start <= lastEnd || child.FirstFrameIndex != page.FirstFrameIndex + uniqueFrames)
                throw Invalid("Invalid seek cache sample gap.");
            // The source page's overlap belongs to the whole run, not to this
            // local reconstruction's unique sample count.
            uniqueFrames += child.FrameCount - (i > 0 && child.Overlap ? 1 : 0);
            expectedOffset += child.Length;
            ValidateEffectHistory(child.EffectHistory, expectedOffset, indexOffset, child.DecodedBytes);
            expectedOffset += child.EffectHistory!.Length; lastEnd = child.End;
        }
        if (expectedOffset != indexOffset || uniqueFrames != page.FrameCount || lastEnd != page.End) throw Invalid("Incomplete seek cache index.");
        return new FullReplayInfo { Version = 2, Header = source.Header, ActorIds = source.ActorIds, Pages = index.Pages, Path = path,
            FileLength = file.Length, HeaderEnd = SeekHeaderBytes, IndexOffset = indexOffset };
    }

    internal static Task? TryWriteSeekCache(FullReplayInfo source, int pageIndex, ReplayClip clip, string fingerprint,
        long sourceWriteTicks, CancellationToken cancellation)
    {
        if (Interlocked.CompareExchange(ref seekWriterBusy, 1, 0) != 0) return null;
        // There is no queue: at most one already decoded <=64 MiB source page
        // is retained globally for optional cache creation. Busy writers skip.
        return Task.Run(() =>
        {
            try { WriteSeekCache(source, pageIndex, clip, fingerprint, sourceWriteTicks, cancellation); }
            catch (OperationCanceledException) { }
            catch (Exception e) when (IsSeekCacheFailure(e)) { }
            finally { Volatile.Write(ref seekWriterBusy, 0); }
        });
    }
    private static void WriteSeekCache(FullReplayInfo source, int pageIndex, ReplayClip clip, string fingerprint,
        long sourceWriteTicks, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); CheckSeekSource(source, sourceWriteTicks);
        int metadataRemaining = MaximumSeekCacheIndexBytes + SeekHeaderBytes + SeekFooterBytes;
        try { if (ReadSeekIndex(source, source.Pages[pageIndex], fingerprint, ref metadataRemaining, cancellation) != null) return; }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (IsSeekCacheFailure(e)) { }
        string directory = SeekDirectory(source.Path), target = SeekPath(source.Path, pageIndex);
        Directory.CreateDirectory(directory);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        bool created = false;
        try
        {
            var children = new List<FullReplayPage>();
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                using (var binary = new BinaryWriter(file, Encoding.UTF8, true))
                {
                    binary.Write(SeekMagic);
                    for (int i = 0; i < fingerprint.Length; i += 2) binary.Write(System.Convert.ToByte(fingerprint.Substring(i, 2), 16));
                    binary.Write(pageIndex);
                }
                int start = 0;
                while (start < clip.Frames.Count)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int end = start;
                    while (end + 1 < clip.Frames.Count && clip.Frames[end + 1].T - clip.Frames[start].T <= 1.000001) end++;
                    bool overlap = children.Count == 0 ? source.Pages[pageIndex].Overlap :
                        clip.Frames[start].T == children[children.Count - 1].End;
                    var child = WriteSeekChild(file, clip, start, end, cancellation);
                    child.EffectHistory = WriteEffectHistory(file, clip.Header,
                        clip.Frames.GetRange(start, end - start + 1).Select(EffectsOnlyFrame).ToList(), cancellation);
                    child.Index = children.Count; child.FirstFrameIndex = source.Pages[pageIndex].FirstFrameIndex + start; child.Overlap = overlap;
                    children.Add(child);
                    if (children.Count > 512 || file.Position > MaximumSeekCacheFileBytes - MaximumSeekCacheIndexBytes - SeekFooterBytes)
                        throw Invalid("Seek cache exceeds its optional disk limit.");
                    if (end == clip.Frames.Count - 1) break;
                    // Repeat a real boundary sample only when the next capture
                    // is close enough; preserve large capture gaps as gaps.
                    start = end > start && clip.Frames[end + 1].T - clip.Frames[end].T <= 1 ? end : end + 1;
                }
                if (children.Count < 2) return;
                byte[] encoded = Utf8.GetBytes(JsonConvert.SerializeObject(new SeekIndex
                    { Fingerprint = fingerprint, Source = source.Pages[pageIndex], Pages = children.ToArray() }, ReplayFiles.Json));
                if (encoded.Length > MaximumSeekCacheIndexBytes) throw Invalid("Seek cache index exceeds its optional limit.");
                using var footer = new BinaryWriter(file, Encoding.UTF8, true);
                footer.Write(encoded); footer.Write(SeekCrc(encoded)); footer.Write((long)encoded.Length); footer.Write(SeekFooter); footer.Flush();
                file.Flush();
            }
            cancellation.ThrowIfCancellationRequested(); CheckSeekSource(source, sourceWriteTicks);
            // Cache files are disposable and quota-controlled. Evict only this
            // cache directory's generated names; never touch source recordings.
            long needed = new FileInfo(temporary).Length;
            var existing = Directory.EnumerateFiles(directory, "page-*.seekpage", SearchOption.TopDirectoryOnly)
                .Take(MaximumPages + 1).Select(p => new FileInfo(p)).ToArray();
            if (existing.Length > MaximumPages) return;
            long bytes = existing.Sum(f => f.Length);
            foreach (var old in existing.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (bytes + needed <= MaximumSeekCacheBytes) break;
                cancellation.ThrowIfCancellationRequested();
                try { long size = old.Length; old.Delete(); bytes -= size; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            if (bytes + needed > MaximumSeekCacheBytes) return;
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
            created = false;
        }
        finally { if (created) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private static FullReplayPage WriteSeekChild(Stream file, ReplayClip clip, int first, int last, CancellationToken cancellation)
    {
        long offset = file.Position, rawBytes = 0;
        var budget = new ContinuousReplayBudget();
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest, true))
        using (var writer = new StreamWriter(gzip, new UTF8Encoding(false), 65536, true))
        {
            void Write(object value)
            {
                string line = JsonConvert.SerializeObject(value, ReplayFiles.Json);
                long size = Utf8.GetByteCount(line) + 1L;
                if (line.Length > ReplayRules.MaxLineCharacters || size > ReplayRules.MaxLineCharacters || rawBytes + size > MaximumPageRawBytes)
                    throw Invalid("Seek cache record exceeds its safety limit.");
                writer.Write(line); writer.Write('\n'); rawBytes += size;
            }
            Write(clip.Header); var codec = new ReplayDeltaCodec.Writer(clip.Header.Schema);
            for (int i = first; i <= last; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var frame = clip.Frames[i]; budget.Commit(frame, budget.Additional(frame)); Write(codec.Encode(frame));
                if (budget.Bytes > MaximumPageDecodedBytes) throw Invalid("Seek cache decoded memory exceeds its safety limit.");
            }
            Write(new { Type = "end" });
        }
        return new FullReplayPage { Start = clip.Frames[first].T, End = clip.Frames[last].T, Offset = offset,
            Length = file.Position - offset, RawBytes = rawBytes, DecodedBytes = budget.Bytes, FrameCount = last - first + 1 };
    }
    private static uint SeekCrc(byte[] bytes)
    {
        uint value = uint.MaxValue;
        foreach (byte b in bytes)
        {
            value ^= b;
            for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1;
        }
        return ~value;
    }
}
