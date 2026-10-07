using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace PeakReplayLab;

public static class ReplayArchive
{
    public static StreamReader OpenText(string path)
    {
        Stream source = File.OpenRead(path);
        try
        {
            if (source.Length > ReplayRules.MaxBytes) throw new InvalidDataException("Replay file is too large.");
            if (path.EndsWith(".peakreplay", StringComparison.OrdinalIgnoreCase)) source = new GZipStream(source, CompressionMode.Decompress);
            return new StreamReader(new BoundedInput(source), Encoding.UTF8, true);
        }
        catch { source.Dispose(); throw; }
    }

    // Called ONLY on explicit Save. The rolling buffer has no file handle and no shutdown save.
    public static string Save(string directory, ReplayClip clip)
    {
        ReplayRules.Validate(clip.Header);
        if (clip.Frames.Count < 2 || clip.Frames.Count > ReplayRules.MaxFrames) throw new InvalidDataException("Invalid highlight length.");
        if (clip.Header.FrameCount != clip.Frames.Count || Math.Abs(clip.Header.Duration - clip.Duration) > .001)
            throw new InvalidDataException("Highlight metadata does not match its frames.");
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, $"memory-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.peakreplay");
        string temp = target + ".partial";
        bool created = false;
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                using var compressed = new GZipStream(file, CompressionLevel.Fastest, true);
                using var writer = new StreamWriter(compressed, new UTF8Encoding(false));
                long bytes = 0;
                void Write(object item)
                {
                    string line = JsonConvert.SerializeObject(item, ReplayFiles.Json);
                    int lineBytes = Encoding.UTF8.GetByteCount(line);
                    if (lineBytes > ReplayRules.MaxLineCharacters) throw new InvalidDataException("Replay record too large.");
                    bytes += lineBytes + 2;
                    if (bytes > ReplayRules.MaxBytes) throw new InvalidDataException("Highlight exceeds 512 MiB uncompressed.");
                    writer.WriteLine(line);
                }
                Write(clip.Header);
                double previous = -1;
                var validation = new ReplayRules.ValidationMemo();
                var fieldDeltas = new ReplayDeltaCodec.Writer(clip.Header.Schema);
                long eventSequence = -1;
                double eventTime = -1;
                foreach (var frame in clip.Frames)
                {
                    ReplayRules.ValidateStored(frame, previous, validation);
                    ReplayRules.ValidateVersion(frame, clip.Header.Schema);
                    if (frame.World.ActiveMapObjects.Length != clip.Header.MapObjects.Length) throw new InvalidDataException("Invalid map state size.");
                    foreach (var e in frame.Events)
                    {
                        if (e.Sequence <= eventSequence || e.T < eventTime) throw new InvalidDataException("Item events are not ordered.");
                        eventSequence = e.Sequence; eventTime = e.T;
                    }
                    Write(fieldDeltas.Encode(frame));
                    previous = frame.T;
                }
                Write(new { Type = "end" });
            }
            File.Move(temp, target); // unique name; never replace an existing highlight
            return target;
        }
        catch
        {
            // Only clean up the exact temporary file successfully created by this invocation.
            if (created && File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }

    private sealed class BoundedInput : Stream
    {
        private readonly Stream inner;
        private long read;
        public BoundedInput(Stream inner) => this.inner = inner;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            read += n;
            if (read > ReplayRules.MaxBytes) throw new InvalidDataException("Expanded replay exceeds 512 MiB.");
            return n;
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => read; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
