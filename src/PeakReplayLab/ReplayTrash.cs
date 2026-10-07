using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PeakReplayLab;

public sealed class ReplayTrashTicket
{
    public readonly string SourcePath, FileName, SourceRoot;
    public readonly long Length;
    public readonly DateTime LastWriteTimeUtc, CreationTimeUtc;
    internal readonly object Issuer;
    internal readonly uint VolumeSerial;
    internal readonly ulong FileIndex;
    internal ReplayTrashTicket(object issuer, string path, string root, long length, DateTime modified, DateTime created,
        uint volumeSerial, ulong fileIndex)
    {
        Issuer = issuer; SourcePath = path; SourceRoot = root; FileName = Path.GetFileName(path);
        Length = length; LastWriteTimeUtc = modified; CreationTimeUtc = created; VolumeSerial = volumeSerial; FileIndex = fileIndex;
    }
}

public sealed class ReplayTrashResult
{
    public readonly string SourcePath, DestinationPath, FileName;
    public readonly long Length;
    internal ReplayTrashResult(ReplayTrashTicket ticket, string destination)
    { SourcePath = ticket.SourcePath; DestinationPath = destination; FileName = ticket.FileName; Length = ticket.Length; }
}

// Recoverable, explicit one-file operations only. All methods that touch disk
// should be invoked by the UI's background task, never by its layout callback.
// There is intentionally no purge, overwrite, recursive traversal or delete API.
public sealed class ReplayTrash
{
    private readonly string memoriesRoot, recordingsRoot, modRoot;
    private readonly object issuer = new(), gate = new();
    private static readonly bool Windows = Path.DirectorySeparatorChar == '\\';
    private static readonly StringComparison PathComparison = Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public string TrashRoot { get; }

    // Constructor performs lexical validation only. The source and destination
    // ancestors are checked again on disk for each Prepare/Move invocation.
    public ReplayTrash(string memoriesRoot, string recordingsRoot, string trashRoot)
    {
        this.memoriesRoot = Normalize(memoriesRoot);
        this.recordingsRoot = Normalize(recordingsRoot);
        TrashRoot = Normalize(trashRoot);
        modRoot = Path.GetDirectoryName(this.memoriesRoot) ?? throw Invalid("缺少模组数据目录。");
        if (Same(modRoot, Path.GetPathRoot(modRoot) ?? "") ||
            !Same(Path.GetDirectoryName(this.recordingsRoot) ?? "", modRoot) ||
            !Same(Path.GetDirectoryName(TrashRoot) ?? "", modRoot) ||
            Same(this.memoriesRoot, this.recordingsRoot) || Same(this.memoriesRoot, TrashRoot) || Same(this.recordingsRoot, TrashRoot))
            throw Invalid("回忆、整局录像与回收目录必须是同一模组目录下三个独立的直接子目录。");
        if (!Same(Path.GetPathRoot(this.memoriesRoot) ?? "", Path.GetPathRoot(TrashRoot) ?? ""))
            throw Invalid("回收目录必须和录像在同一个本机卷，不能跨卷移动。");
    }

    public ReplayTrashTicket Prepare(string sourcePath)
    {
        string source = Normalize(sourcePath), sourceRoot = SourceRoot(source);
        EnsureLocalVolume();
        CheckSource(source, sourceRoot);
        using var held = OpenGuard(source);
        var snapshot = Snapshot(held, source, sourceRoot);
        return new ReplayTrashTicket(issuer, source, sourceRoot, snapshot.Length, snapshot.Modified, snapshot.Created,
            snapshot.VolumeSerial, snapshot.FileIndex);
    }

    public ReplayTrashResult MoveToTrash(ReplayTrashTicket ticket)
    {
        if (ticket == null) throw new ArgumentNullException(nameof(ticket));
        if (!ReferenceEquals(ticket.Issuer, issuer)) throw Invalid("删除确认不属于当前录像库，请重新选择录像。");
        lock (gate)
        {
            string source = Normalize(ticket.SourcePath), root = SourceRoot(source);
            if (!Same(root, ticket.SourceRoot) || !string.Equals(Path.GetFileName(source), ticket.FileName, StringComparison.Ordinal))
                throw Invalid("录像路径与确认时不一致，请刷新后重新确认。");
            EnsureLocalVolume();
            CheckSource(source, root);
            // FileShare.Delete is necessary to rename on Windows while this
            // guard denies concurrent readers/writers. Active recorder/playback
            // handles also prevent opening this guard; no permissive retry.
            using var held = OpenGuard(source);
            var snapshot = Snapshot(held, source, root);
            if (snapshot.Length != ticket.Length || snapshot.Modified != ticket.LastWriteTimeUtc ||
                snapshot.Created != ticket.CreationTimeUtc || snapshot.VolumeSerial != ticket.VolumeSerial || snapshot.FileIndex != ticket.FileIndex)
                throw new IOException("录像在确认后发生变化，未移动任何文件；请刷新后重新确认。");

            CheckDirectoryChain(modRoot);
            CreateCheckedChild(modRoot, TrashRoot, mustBeNew: false);
            string directory = Path.Combine(TrashRoot, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
            CreateCheckedChild(TrashRoot, directory, mustBeNew: true);
            string destination = Path.Combine(directory, ticket.FileName);
            if (!Same(Path.GetDirectoryName(destination) ?? "", directory) || Exists(destination))
                throw new IOException("回收目标已存在或不在预期目录内，未覆盖任何文件。");

            // Recheck both exact path chains after creating the destination.
            // File identity also rejects replacement by a same-size/timestamp
            // file between the confirmation dialog and this operation.
            CheckSource(source, root); CheckDirectoryChain(directory);
            var beforeMove = Snapshot(held, source, root);
            if (!snapshot.SameFile(beforeMove)) throw new IOException("录像在移动前发生变化，请重新确认。");
            // Unlike a general File.Move implementation, zero Win32 flags do
            // NOT permit COPY_ALLOWED or REPLACE_EXISTING. A cross-device move
            // fails rather than turning into copy-and-delete.
            if (!MoveFileEx(source, destination, 0)) throw NativeFailure(Marshal.GetLastWin32Error());
            return new ReplayTrashResult(ticket, destination);
        }
    }

    private string SourceRoot(string source)
    {
        string parent = Path.GetDirectoryName(source) ?? "";
        string name = Path.GetFileName(source);
        if (Same(parent, memoriesRoot) && name.EndsWith(".peakreplay", StringComparison.OrdinalIgnoreCase)) return memoriesRoot;
        if (Same(parent, recordingsRoot) && (name.EndsWith(".peakrun", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".peakrun.partial", StringComparison.OrdinalIgnoreCase))) return recordingsRoot;
        throw Invalid("只能移走当前回忆或整局录像目录中的直接子录像文件，不能选择其他文件或目录。");
    }
    private void EnsureLocalVolume()
    {
        if (!Windows) throw new PlatformNotSupportedException("当前系统尚未提供经过验证的同卷原子回收操作；未移动或删除文件。");
        var drive = new DriveInfo(Path.GetPathRoot(modRoot)!);
        if (!drive.IsReady || (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable))
            throw new IOException("仅支持就绪的本机固定或可移动磁盘，不支持网络位置或此文件系统。");
    }
    private static FileStream OpenGuard(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete); }
        catch (IOException e) { throw new IOException("无法锁定录像：文件可能正在录制、回放、被占用或已经移走；请停止使用并刷新后重试。", e); }
        catch (UnauthorizedAccessException e) { throw new UnauthorizedAccessException("没有访问录像的权限，未移动文件。", e); }
    }
    private static void CheckSource(string path, string root)
    {
        if (!Same(Path.GetDirectoryName(path) ?? "", root)) throw Invalid("录像不在允许的直接子文件范围内。");
        CheckDirectoryChain(root);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("不能移走目录、符号链接或重解析点。");
        if ((attributes & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException("录像为只读文件；不会擅自修改属性，请先自行取消只读后重试。");
    }
    private static void CheckDirectoryChain(string directory)
    {
        var chain = new List<string>();
        for (string? current = directory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)) chain.Add(current!);
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            var attributes = File.GetAttributes(chain[i]);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("路径含符号链接、挂载点或重解析目录，已拒绝跟随。");
            if ((attributes & FileAttributes.Directory) == 0) throw new IOException("录像目录路径中包含普通文件。");
        }
    }
    private static void CreateCheckedChild(string parent, string child, bool mustBeNew)
    {
        if (!Same(Path.GetDirectoryName(child) ?? "", parent)) throw Invalid("回收目标不在指定目录内。");
        CheckDirectoryChain(parent);
        if (Exists(child))
        {
            if (mustBeNew) throw new IOException("唯一回收目录已存在，请重新尝试；没有覆盖任何文件。");
            CheckDirectoryChain(child); return;
        }
        Directory.CreateDirectory(child);
        CheckDirectoryChain(child);
    }
    private static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !string.Equals(path, path.Trim(), StringComparison.Ordinal))
            throw Invalid("录像路径为空或含首尾空白。");
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw Invalid("不支持 UNC、设备或网络路径。");
        if (!Path.IsPathFullyQualified(path)) throw Invalid("必须使用录像的完整本机绝对路径。");
        int allowedColon = Windows && path.Length > 1 && path[1] == ':' ? 1 : -1;
        for (int i = 0; i < path.Length; i++)
            if (char.IsControl(path[i]) || path[i] == ':' && i != allowedColon)
                throw Invalid("录像路径含控制字符或备用数据流，已拒绝。");
        var components = path.Replace('\\', '/').Split('/');
        foreach (string component in components)
        {
            if (component.Length == 0 || component.Length == 2 && component[1] == ':' && char.IsLetter(component[0])) continue;
            if (component == "." || component == "..") throw Invalid("录像路径不能包含原始点号或上级目录跳转。");
            if (component.EndsWith(".", StringComparison.Ordinal) || component.EndsWith(" ", StringComparison.Ordinal) ||
                component.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*' }) >= 0 || DeviceName(component))
                throw Invalid("录像路径含不支持的 Windows 文件名或设备别名。");
        }
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? "";
        while (full.Length > root.Length && (full[full.Length - 1] == Path.DirectorySeparatorChar || full[full.Length - 1] == Path.AltDirectorySeparatorChar))
            full = full.Substring(0, full.Length - 1);
        return full;
    }
    private static bool DeviceName(string value)
    {
        string stem = value.Split('.')[0].ToUpperInvariant();
        if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL") return true;
        return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '1' && stem[3] <= '9';
    }
    private static bool Same(string left, string right) => string.Equals(left, right, PathComparison);
    private static ArgumentException Invalid(string message) => new(message);

    private readonly struct FileSnapshot
    {
        public readonly long Length;
        public readonly DateTime Modified, Created;
        public readonly uint VolumeSerial;
        public readonly ulong FileIndex;
        public FileSnapshot(NativeInfo value)
        {
            Length = checked((long)((ulong)value.SizeHigh << 32 | value.SizeLow));
            Modified = DateTime.FromFileTimeUtc((long)((ulong)value.WriteHigh << 32 | value.WriteLow));
            Created = DateTime.FromFileTimeUtc((long)((ulong)value.CreationHigh << 32 | value.CreationLow));
            VolumeSerial = value.VolumeSerial; FileIndex = (ulong)value.IndexHigh << 32 | value.IndexLow;
        }
        public bool SameFile(FileSnapshot other) => Length == other.Length && Modified == other.Modified && Created == other.Created &&
            VolumeSerial == other.VolumeSerial && FileIndex == other.FileIndex;
    }
    private static FileSnapshot Snapshot(FileStream held, string path, string root)
    {
        if (!GetFileInformationByHandle(held.SafeFileHandle, out var native)) throw NativeFailure(Marshal.GetLastWin32Error());
        if ((native.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0)
            throw new IOException("所选对象为目录、链接或只读文件，未移动。");
        var snapshot = new FileSnapshot(native);
        CheckSource(path, root);
        var info = new FileInfo(path); info.Refresh();
        if (info.Length != snapshot.Length || info.LastWriteTimeUtc != snapshot.Modified || info.CreationTimeUtc != snapshot.Created)
            throw new IOException("录像路径在操作期间发生变化，未移动文件。");
        return snapshot;
    }
    private static IOException NativeFailure(int code)
    {
        string message = code == 17 ? "回收操作不能跨卷；不会执行复制后删除。"
            : code == 80 || code == 183 ? "回收目标已经存在，未覆盖任何文件。"
            : code == 32 || code == 33 ? "录像正在使用，未移动；请停止录制或回放后重试。"
            : code == 5 ? "没有移动录像的权限，或文件为只读；未执行删除。"
            : "本机文件系统无法完成回收移动；未执行复制或永久删除。";
        return new IOException(message, new Win32Exception(code));
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInfo
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "MoveFileExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out NativeInfo information);
}
