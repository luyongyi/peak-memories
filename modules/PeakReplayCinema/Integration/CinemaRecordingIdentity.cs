using System;
using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PeakReplayLab.Cinema;

// Bounded identity read, performed on a worker only on opening cinema. It does
// not decode recordings or read an entire multi-gigabyte run. Timestamp, length
// and both ends bind local sidecars and allow a recording to be moved unchanged.
internal static class CinemaRecordingIdentity
{
    public static string Read(string sourcePath)
    {
        var before = new FileInfo(sourcePath);
        long timestamp = before.LastWriteTimeUtc.Ticks;
        using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = stream.Length;
        using var hash = SHA256.Create();
        byte[] header = Encoding.UTF8.GetBytes("peak-camera-v1\n" + length.ToString(CultureInfo.InvariantCulture) + "\n" + timestamp.ToString(CultureInfo.InvariantCulture) + "\n");
        hash.TransformBlock(header, 0, header.Length, header, 0);
        byte[] bytes = new byte[65536];
        void ReadBlock(long start)
        {
            stream.Position = start;
            int count = (int)Math.Min(bytes.Length, length - start);
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(bytes, total, count - total);
                if (read == 0) throw new EndOfStreamException("Recording changed while reading its identity.");
                total += read;
            }
            if (total != 0) hash.TransformBlock(bytes, 0, total, bytes, 0);
        }
        ReadBlock(0);
        if (length > bytes.Length) ReadBlock(Math.Max(bytes.Length, length - bytes.Length));
        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var after = new FileInfo(sourcePath);
        if (after.Length != length || after.LastWriteTimeUtc.Ticks != timestamp)
            throw new IOException("Recording changed while reading its identity.");
        return BitConverter.ToString(hash.Hash!).Replace("-", "").ToLowerInvariant();
    }

    public static string Sidecar(string recording, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 40 || name.Contains("..") || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("方案名称须为 1–40 个有效文件名字符。");
        foreach (char value in name) if (char.IsControl(value)) throw new ArgumentException("Invalid camera plan name.");
        return Path.GetFullPath(recording) + "." + name + ".camera.json";
    }
}
