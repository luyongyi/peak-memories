using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PeakReplayLab;

// One file, with independently decodable internal pages. Opening the catalogue
// reads only bounded metadata; seeking decompresses only the requested page.
public static class FullReplayArchive
{
    public const long MaximumFileBytes = 4L * 1024 * 1024 * 1024;
    public const long MaximumPageRawBytes = 128L * 1024 * 1024;
    public const long MaximumPageDecodedBytes = 64L * 1024 * 1024;
    public const int MaximumHeaderBytes = 64 * 1024;
    public const int MaximumIndexBytes = 4 * 1024 * 1024;
    public const int MaximumPages = 8192;
    public const double MaximumDuration = 4 * 60 * 60;
    private const long MaximumFrames = 4 * 60 * 60 * 60 + 1;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PEAKRUN1");
    private static readonly byte[] Footer = Encoding.ASCII.GetBytes("PEAKEND1");

    public static FullReplayInfo ReadInfo(string path, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        string fullPath = System.IO.Path.GetFullPath(path);
        using var file = Open(fullPath);
        long length = file.Length;
        if (length < 32 || length > MaximumFileBytes) throw Invalid("Invalid full replay file size.");
        if (!ReadExactly(file, 8, cancellation).SequenceEqual(Magic)) throw Invalid("Invalid full replay signature.");
        int headerLength = Int32(ReadExactly(file, 4, cancellation), 0);
        if (headerLength < 2 || headerLength > MaximumHeaderBytes || headerLength > length - 28)
            throw Invalid("Invalid full replay header length.");
        JObject headerJson = Parse(ReadExactly(file, headerLength, cancellation));
        Require(headerJson, "Type", "Schema");
        var header = Convert<ReplayHeader>(headerJson);
        ValidateHeader(header);
        long headerEnd = file.Position;
        file.Position = length - 16;
        byte[] trailer = ReadExactly(file, 16, cancellation);
        if (!trailer.Skip(8).SequenceEqual(Footer)) throw Invalid("Full replay is unfinished or its footer is damaged.");
        long indexLength = Int64(trailer, 0);
        if (indexLength < 2 || indexLength > MaximumIndexBytes || indexLength > length - 16 - headerEnd)
            throw Invalid("Invalid full replay index length.");
        long indexOffset = length - 16 - indexLength;
        file.Position = indexOffset;
        JObject indexJson = Parse(ReadExactly(file, (int)indexLength, cancellation));
        Require(indexJson, "Version", "Header", "Duration", "FrameCount", "ActorIds", "Complete", "Pages");
        if (indexJson["Pages"] is not JArray pagesJson || pagesJson.Count > MaximumPages)
            throw Invalid("Invalid full replay page index.");
        foreach (JToken token in pagesJson)
        {
            if (token is not JObject pageJson) throw Invalid("Invalid full replay page index.");
            Require(pageJson, "Index", "Start", "End", "Offset", "Length", "RawBytes", "DecodedBytes", "FrameCount", "FirstFrameIndex", "Overlap");
        }
        var info = Convert<FullReplayInfo>(indexJson);
        info.FileLength = length; info.HeaderEnd = headerEnd; info.IndexOffset = indexOffset; info.Path = fullPath;
        ValidateIndex(info);
        if (!Equivalent(header, info.Header)) throw Invalid("Full replay headers disagree.");
        // A recovery/unfinished filename is never advertised as a complete run.
        if (fullPath.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) info.Complete = false;
        cancellation.ThrowIfCancellationRequested();
        return info;
    }

    public static ReplayClip ReadPage(string path, FullReplayInfo info, int pageIndex, CancellationToken cancellation = default)
    {
        if (info == null) throw new ArgumentNullException(nameof(info));
        cancellation.ThrowIfCancellationRequested();
        string fullPath = System.IO.Path.GetFullPath(path);
        if (!string.Equals(fullPath, info.Path, StringComparison.OrdinalIgnoreCase)) throw Invalid("Full replay index belongs to another file.");
        ValidateIndex(info);
        if (pageIndex < 0 || pageIndex >= info.Pages.Length) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        FullReplayPage page = info.Pages[pageIndex];
        using var file = Open(fullPath);
        if (file.Length != info.FileLength) throw Invalid("Full replay changed after its index was read.");
        // Check the member's own trailer too: this prevents accepting extra bytes
        // after a gzip member merely because the inflater buffered them ahead.
        file.Position = page.Offset + page.Length - 8;
        byte[] gzipFooter = ReadExactly(file, 8, cancellation);
        uint expectedCrc = UInt32(gzipFooter, 0), expectedSize = UInt32(gzipFooter, 4);
        if (expectedSize != page.RawBytes) throw Invalid("Full replay page raw length disagrees with gzip.");
        file.Position = page.Offset;
        using var slice = new PageSlice(file, page.Length, cancellation);
        using var gzip = new GZipStream(slice, CompressionMode.Decompress, true);
        using var raw = new CheckedRawStream(gzip, page.RawBytes, cancellation);
        using var reader = new StreamReader(raw, Utf8, false, 8192, true);
        var clip = ReplayFiles.Read(reader, cancellation, allowSingleFrame: true, decodedByteLimit: MaximumPageDecodedBytes,
            maximumStoredTime: MaximumDuration);
        if (raw.BytesRead != page.RawBytes || raw.Crc != expectedCrc || slice.Remaining != 0)
            throw Invalid("Full replay page bytes do not match its index.");
        if (!clip.Complete || clip.Frames.Count != page.FrameCount || Math.Abs(clip.Frames[0].T - page.Start) > .000001 ||
            Math.Abs(clip.Duration - page.End) > .000001)
            throw Invalid("Full replay page timeline does not match its index.");
        if (!Equivalent(clip.Header, info.Header)) throw Invalid("Full replay page header disagrees with the recording.");
        var actorIds = new HashSet<string>(info.ActorIds, StringComparer.Ordinal);
        var budget = new ContinuousReplayBudget();
        foreach (var frame in clip.Frames)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (var actor in frame.Actors)
                if (!actorIds.Contains(actor.Id)) throw Invalid("Full replay page contains an unindexed actor.");
            budget.Commit(frame, budget.Additional(frame));
            if (budget.Bytes > page.DecodedBytes || budget.Bytes > MaximumPageDecodedBytes)
                throw Invalid("Full replay page decoded memory exceeds its index.");
        }
        // Page records already use whole-run elapsed time, including events in
        // the interval preceding a non-overlapping page's first capture sample.
        clip.FilePath = fullPath;
        return clip;
    }

    private static void ValidateIndex(FullReplayInfo info)
    {
        if (!ReplayRegionCovers.Valid(info.RegionSummary)) throw Invalid("Invalid full replay region summary.");
        if (info.Version != 1 || info.Header == null || info.Pages == null || info.Pages.Length < 1 || info.Pages.Length > MaximumPages ||
            !ReplayRules.Finite(info.Duration) || info.Duration <= 0 || info.Duration > MaximumDuration ||
            info.FrameCount < 2 || info.FrameCount > MaximumFrames || info.ActorIds == null ||
            info.ActorIds.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 256) ||
            info.ActorIds.Distinct(StringComparer.Ordinal).Count() != info.ActorIds.Length ||
            info.Reason == null || info.Reason.Length > 256 || info.Fault?.Length > 4096 || info.FaultCode?.Length > 128 ||
            info.Complete && (!string.IsNullOrEmpty(info.Fault) || !string.IsNullOrEmpty(info.FaultCode)) ||
            info.FileLength < 32 || info.FileLength > MaximumFileBytes || info.HeaderEnd < 14 ||
            info.HeaderEnd > 12 + MaximumHeaderBytes || info.IndexOffset <= info.HeaderEnd || info.IndexOffset > info.FileLength - 18 ||
            info.GapOver100ms < 0 || info.GapOver100ms >= info.FrameCount || info.GapOver500ms < 0 ||
            info.GapOver500ms > info.GapOver100ms || !ReplayRules.Finite(info.LargestGapSeconds) ||
            info.LargestGapSeconds < 0 || info.LargestGapSeconds > info.Duration + .000001)
            throw Invalid("Invalid full replay index metadata.");
        ValidateHeader(info.Header);
        long expectedOffset = info.HeaderEnd, uniqueFrames = 0;
        double lastEnd = 0;
        for (int i = 0; i < info.Pages.Length; i++)
        {
            var page = info.Pages[i];
            if (page == null || page.Index != i || !ReplayRules.Finite(page.Start) || !ReplayRules.Finite(page.End) ||
                page.Start < 0 || page.End < page.Start || page.End > MaximumDuration || page.End - page.Start > 121 ||
                page.FrameCount < 1 || page.FrameCount > ReplayRules.MaxFrames ||
                page.FrameCount == 1 && page.End != page.Start || page.FrameCount > 1 && page.End <= page.Start ||
                page.Offset != expectedOffset || page.Length < 18 || page.Length > info.IndexOffset - page.Offset ||
                page.RawBytes < 1 || page.RawBytes > MaximumPageRawBytes ||
                page.DecodedBytes < 1 || page.DecodedBytes > MaximumPageDecodedBytes)
                throw Invalid("Invalid full replay page bounds.");
            if (i == 0)
            {
                if (page.Start != 0 || page.FirstFrameIndex != 0 || page.Overlap) throw Invalid("Invalid first full replay page.");
            }
            else if (page.Overlap)
            {
                if (Math.Abs(page.Start - lastEnd) > .000001 || page.FirstFrameIndex != uniqueFrames - 1)
                    throw Invalid("Invalid overlapping full replay page boundary.");
            }
            else if (page.Start <= lastEnd || page.FirstFrameIndex != uniqueFrames)
                throw Invalid("Invalid full replay page boundary.");
            uniqueFrames += page.FrameCount - (page.Overlap ? 1 : 0);
            if (uniqueFrames > info.FrameCount) throw Invalid("Full replay frame count exceeds its index.");
            expectedOffset += page.Length;
            lastEnd = page.End;
        }
        if (expectedOffset != info.IndexOffset || uniqueFrames != info.FrameCount || Math.Abs(lastEnd - info.Duration) > .000001)
            throw Invalid("Full replay index does not cover its entire recording.");
    }

    private static void ValidateHeader(ReplayHeader header)
    {
        ReplayRules.Validate(header);
        if (!ReplayRules.SupportedSchema(header.Schema) || header.FrameCount != 0 || header.Duration != 0 ||
            header.Recorder == null || header.Recorder.Length > 256 || header.Fidelity == null || header.Fidelity.Length > 4096)
            throw Invalid("Invalid full replay schema or header.");
    }

    private static bool Equivalent(ReplayHeader a, ReplayHeader b) => JToken.DeepEquals(JObject.FromObject(a), JObject.FromObject(b));
    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.RandomAccess);
    private static InvalidDataException Invalid(string message) => new(message);
    private static void Require(JObject value, params string[] names)
    {
        foreach (string name in names)
            if (value[name] == null || value[name]!.Type == JTokenType.Null) throw Invalid("Missing full replay index field.");
    }
    private static T Convert<T>(JObject value) where T : class => value.ToObject<T>(JsonSerializer.Create(ReplayFiles.Json)) ?? throw Invalid("Invalid full replay metadata.");
    private static JObject Parse(byte[] data)
    {
        using var text = new StringReader(Utf8.GetString(data));
        using var json = new JsonTextReader(text) { MaxDepth = 24, DateParseHandling = DateParseHandling.None };
        JObject value = JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (json.Read()) throw Invalid("Multiple JSON values in full replay metadata.");
        return value;
    }
    private static byte[] ReadExactly(Stream stream, int count, CancellationToken cancellation)
    {
        var data = new byte[count];
        int read = 0;
        while (read < count)
        {
            cancellation.ThrowIfCancellationRequested();
            int next = stream.Read(data, read, count - read);
            if (next == 0) throw Invalid("Truncated full replay.");
            read += next;
        }
        return data;
    }
    private static uint UInt32(byte[] bytes, int start) => (uint)(bytes[start] | bytes[start + 1] << 8 | bytes[start + 2] << 16 | bytes[start + 3] << 24);
    private static int Int32(byte[] bytes, int start) => unchecked((int)UInt32(bytes, start));
    private static long Int64(byte[] bytes, int start) => unchecked((long)((ulong)UInt32(bytes, start) | (ulong)UInt32(bytes, start + 4) << 32));

    private sealed class PageSlice : Stream
    {
        private readonly Stream source;
        private readonly CancellationToken cancellation;
        public long Remaining { get; private set; }
        public PageSlice(Stream source, long length, CancellationToken cancellation)
        { this.source = source; Remaining = length; this.cancellation = cancellation; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Remaining == 0 || count == 0) return 0;
            int read = source.Read(buffer, offset, (int)Math.Min(count, Remaining));
            if (read == 0) throw Invalid("Truncated full replay page.");
            Remaining -= read;
            return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CheckedRawStream : Stream
    {
        private static readonly uint[] CrcTable = MakeCrcTable();
        private readonly Stream source;
        private readonly long maximum;
        private readonly CancellationToken cancellation;
        private uint crc = uint.MaxValue;
        public long BytesRead { get; private set; }
        public uint Crc => ~crc;
        public CheckedRawStream(Stream source, long maximum, CancellationToken cancellation)
        { this.source = source; this.maximum = maximum; this.cancellation = cancellation; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            cancellation.ThrowIfCancellationRequested();
            int read = source.Read(buffer, offset, count);
            if (read > maximum - BytesRead) throw Invalid("Full replay page exceeds its raw byte limit.");
            for (int i = offset; i < offset + read; i++) crc = CrcTable[(crc ^ buffer[i]) & 255] ^ (crc >> 8);
            BytesRead += read;
            return read;
        }
        private static uint[] MakeCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint c = i;
                for (int bit = 0; bit < 8; bit++) c = (c & 1) != 0 ? 0xedb88320U ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
