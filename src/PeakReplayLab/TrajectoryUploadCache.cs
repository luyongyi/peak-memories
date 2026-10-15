using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;

namespace PeakReplayLab;

// Private local acknowledgements, never an upload payload. The fingerprint
// reads only the bounded sealed header/index, not compressed replay pages.
// Moving/copying a recording to a new path deliberately misses this cache.
public static class TrajectoryUploadCache
{
    public static TrajectoryUploadCacheSession Open(string exportDirectory, string recordingPath,
        Uri endpoint, string exporterVersion, CancellationToken cancellation = default)
        => new(exportDirectory, recordingPath, endpoint, exporterVersion, cancellation);
}

public sealed class TrajectoryUploadCacheSession
{
    private const int MaximumCacheBytes = 1024 * 1024;
    private const int MaximumParts = 4096;
    private static readonly Regex PackageName = new(@"^[a-f0-9]{64}-[a-f0-9]{32}\.trajectory\.json\.gz$", RegexOptions.CultureInvariant);
    private readonly string exportDirectory, sourcePath, sourceIdentity, endpoint, exporterVersion, key, cacheDirectory, statePath;
    private readonly byte[] authenticationKey;
    private readonly bool persist;
    private CacheState state;
    public string SourceIdentity => sourceIdentity;
    public bool Persistent => persist;
    public int ConfirmedParts => state.Parts.Count(value => value.Receipt != null);

    internal TrajectoryUploadCacheSession(string directory, string recordingPath, Uri destination,
        string version, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(version) || version.Length > 128 || !Regex.IsMatch(version, @"^[a-zA-Z0-9/._-]+$"))
            throw new ArgumentException("Invalid trajectory export cache version.");
        exportDirectory = Path.GetFullPath(directory);
        sourcePath = Path.GetFullPath(recordingPath);
        endpoint = NormalizeEndpoint(destination);
        exporterVersion = version;
        sourceIdentity = Fingerprint(sourcePath, cancellation);
        key = Hash("peak-memories/upload-cache/v1\n" + sourceIdentity + "\n" + endpoint + "\n" + exporterVersion);
        cacheDirectory = Path.Combine(exportDirectory, ".upload-cache");
        statePath = Path.Combine(cacheDirectory, key + ".json");
        state = EmptyState();
        try
        {
            EnsureDirectory(cacheDirectory);
            authenticationKey = LoadKey(Path.Combine(cacheDirectory, "authentication.key"));
            persist = true;
            state = ReadState() ?? state;
        }
        catch (Exception error) when (CacheFailure(error))
        {
            // A broken/unwritable cache must not prevent a real export/upload.
            authenticationKey = RandomKey(); persist = false;
        }
    }

    public static string NormalizeEndpoint(Uri endpoint)
    {
        var checkedEndpoint = TrajectoryUploader.Endpoint(endpoint.AbsoluteUri);
        return checkedEndpoint.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
    }

    public TrajectoryUploadReceipt? CompletedReceipt(CancellationToken cancellation = default)
    {
        if (!state.Completed || state.Completion == null) return null;
        EnsureSourceCurrent(cancellation);
        var receipt = Clone(state.Completion);
        receipt.Duplicate = true; receipt.Cached = true;
        return receipt;
    }

    public TrajectoryExportResult? RestoreExport(CancellationToken cancellation = default)
    {
        if (state.Export == null || state.Parts.Count == 0) return null;
        // Successful server receipts survive deletion of disposable local
        // exports. Incomplete batches must still prove every retained payload.
        if (!state.Completed)
        {
            try
            {
                foreach (var part in state.Parts)
                    if (!Matches(part, cancellation)) return null;
            }
            catch (Exception error) when (CacheFailure(error)) { return null; }
        }
        return state.Export.Restore(state.Parts.Select(value => Path.Combine(exportDirectory, value.Name)).ToArray());
    }

    public void RememberExport(TrajectoryExportResult result, CancellationToken cancellation = default)
    {
        EnsureSourceCurrent(cancellation);
        if (!Hex(result.RecordingId) || result.Paths.Length == 0 || result.Paths.Length > MaximumParts)
            throw new InvalidDataException("Invalid trajectory export cache batch.");
        var confirmed = state.Parts.Where(value => value.Receipt != null).GroupBy(value => value.Hash)
            .ToDictionary(group => group.Key, group => group.First().Receipt!, StringComparer.Ordinal);
        var parts = new List<CachePart>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in result.Paths)
        {
            cancellation.ThrowIfCancellationRequested();
            var part = DescribePackage(path, cancellation);
            if (!part.Name.StartsWith(result.RecordingId + "-", StringComparison.Ordinal) || !names.Add(part.Name))
                throw new InvalidDataException("Invalid trajectory export cache filename.");
            if (confirmed.TryGetValue(part.Hash, out var receipt)) part.Receipt = Clone(receipt);
            parts.Add(part);
        }
        state.Export = CacheExport.From(result);
        state.Parts = parts; state.Completed = false; state.Completion = null;
        Save();
    }

    public void EnsureSourceCurrent(CancellationToken cancellation = default)
    {
        if (Fingerprint(sourcePath, cancellation) != sourceIdentity)
            throw new InvalidDataException("录像在轨迹筛选后发生变化，请重新选择录像。");
    }

    // Explicit user recovery only. This session's source/target/version key is
    // the sole file updated; prepared exports remain available for reuse.
    public void ForgetAcknowledgements(CancellationToken cancellation = default)
    {
        EnsureSourceCurrent(cancellation);
        foreach (var part in state.Parts) part.Receipt = null;
        state.Completed = false; state.Completion = null;
        Save();
    }

    public TrajectoryUploadReceipt? ConfirmedReceipt(string packagePath, CancellationToken cancellation = default)
    {
        var part = Find(packagePath);
        // This callback runs before HTTP for every part, including ones not yet
        // acknowledged. Never send a local file changed since export preflight.
        if (part == null || !Matches(part, cancellation)) throw new InvalidDataException("Trajectory package changed after export.");
        if (part.Receipt == null) return null;
        var receipt = Clone(part.Receipt); receipt.Cached = true; receipt.Duplicate = true;
        return receipt;
    }

    public void RecordConfirmed(string packagePath, TrajectoryUploadReceipt receipt, CancellationToken cancellation = default)
    {
        if (!ValidReceipt(receipt) || receipt.UploadIds.Length != 1) throw new InvalidDataException("Invalid upload cache acknowledgement.");
        var part = Find(packagePath);
        if (part == null || !Matches(part, cancellation)) throw new InvalidDataException("Trajectory package changed after export.");
        part.Receipt = Clone(receipt);
        state.Completed = false; state.Completion = null;
        Save();
    }

    public void MarkCompleted(TrajectoryUploadReceipt receipt, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        EnsureSourceCurrent(cancellation);
        if (!ValidReceipt(receipt) || state.Parts.Count == 0 || state.Parts.Any(part => part.Receipt == null)
            || !receipt.UploadIds.SequenceEqual(state.Parts.Select(part => part.Receipt!.UploadId)))
            throw new InvalidDataException("Only a fully confirmed trajectory batch can be cached as complete.");
        state.Completed = true; state.Completion = Clone(receipt);
        Save();
    }

    private CachePart? Find(string path)
    {
        string full = Path.GetFullPath(path);
        if (!PathEquals(Path.GetDirectoryName(full)!, exportDirectory)) return null;
        return state.Parts.FirstOrDefault(part => string.Equals(part.Name, Path.GetFileName(full), StringComparison.Ordinal));
    }

    private bool Matches(CachePart part, CancellationToken cancellation)
    {
        var current = DescribePackage(Path.Combine(exportDirectory, part.Name), cancellation);
        return current.Length == part.Length && current.Hash == part.Hash;
    }

    private CachePart DescribePackage(string path, CancellationToken cancellation)
    {
        string full = Path.GetFullPath(path), name = Path.GetFileName(full);
        if (!PathEquals(Path.GetDirectoryName(full)!, exportDirectory) || !PackageName.IsMatch(name))
            throw new InvalidDataException("Trajectory cache only accepts generated private export files.");
        AssertRealPath(full, false);
        using var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        if (file.Length <= 0 || file.Length > TrajectoryUploader.MaximumPackageBytes) throw new InvalidDataException("Invalid cached trajectory package size.");
        using var hash = SHA256.Create();
        var buffer = new byte[65536];
        int count;
        while ((count = file.Read(buffer, 0, buffer.Length)) != 0)
        { cancellation.ThrowIfCancellationRequested(); hash.TransformBlock(buffer, 0, count, null, 0); }
        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return new CachePart { Name = name, Length = file.Length, Hash = HexBytes(hash.Hash!) };
    }

    private CacheState EmptyState() => new() { Key = key, Endpoint = endpoint, ExporterVersion = exporterVersion };

    private CacheState? ReadState()
    {
        try
        {
            if (!File.Exists(statePath)) return null;
            AssertRealPath(statePath, false);
            var bytes = ReadBounded(statePath, MaximumCacheBytes);
            var envelope = JsonConvert.DeserializeObject<Envelope>(Encoding.UTF8.GetString(bytes), JsonSettings);
            if (envelope?.Version != 1 || envelope.Payload == null || !Hex(envelope.Mac)) return null;
            byte[] payload = Convert.FromBase64String(envelope.Payload);
            if (payload.Length > MaximumCacheBytes || !FixedEquals(envelope.Mac, Authenticate(payload))) return null;
            var loaded = JsonConvert.DeserializeObject<CacheState>(Encoding.UTF8.GetString(payload), JsonSettings);
            if (loaded == null || loaded.Key != key || loaded.Endpoint != endpoint || loaded.ExporterVersion != exporterVersion
                || loaded.Export == null || loaded.Parts == null || loaded.Parts.Count == 0 || loaded.Parts.Count > MaximumParts
                || !loaded.Export.Valid() || loaded.Parts.Any(value => value == null)
                || loaded.Parts.Select(value => value.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != loaded.Parts.Count)
                return null;
            foreach (var part in loaded.Parts)
                if (part.Name == null || !PackageName.IsMatch(part.Name) || !part.Name.StartsWith(loaded.Export.RecordingId + "-", StringComparison.Ordinal)
                    || !Hex(part.Hash) || part.Length <= 0 || part.Length > TrajectoryUploader.MaximumPackageBytes
                    || part.Receipt != null && (!ValidReceipt(part.Receipt) || part.Receipt.UploadIds.Length != 1)) return null;
            if (loaded.Completed && (loaded.Completion == null || !ValidReceipt(loaded.Completion)
                || loaded.Parts.Any(part => part.Receipt == null)
                || !loaded.Completion.UploadIds.SequenceEqual(loaded.Parts.Select(part => part.Receipt!.UploadId)))) return null;
            return loaded;
        }
        catch (Exception error) when (CacheFailure(error)) { return null; }
    }

    private void Save()
    {
        if (!persist) return;
        string temporary = Path.Combine(cacheDirectory, key + "-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            AssertRealPath(cacheDirectory, true);
            if (File.Exists(statePath)) AssertRealPath(statePath, false);
            byte[] payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(state, JsonSettings));
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new Envelope
                { Version = 1, Payload = Convert.ToBase64String(payload), Mac = Authenticate(payload) }, JsonSettings));
            if (bytes.Length > MaximumCacheBytes) return;
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
            if (File.Exists(statePath)) File.Replace(temporary, statePath, null);
            else File.Move(temporary, statePath);
        }
        catch (Exception error) when (CacheFailure(error)) { }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private string Authenticate(byte[] payload) { using var mac = new HMACSHA256(authenticationKey); return HexBytes(mac.ComputeHash(payload)); }
    private static readonly JsonSerializerSettings JsonSettings = new() { TypeNameHandling = TypeNameHandling.None, MaxDepth = 12 };
    private static bool CacheFailure(Exception error) => error is IOException || error is InvalidDataException || error is UnauthorizedAccessException
        || error is JsonException || error is FormatException || error is CryptographicException || error is ArgumentException;
    private static bool Hex(string? value) => value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
    private static string HexBytes(byte[] bytes) => string.Concat(bytes.Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
    private static string Hash(string value) { using var hash = SHA256.Create(); return HexBytes(hash.ComputeHash(Encoding.UTF8.GetBytes(value))); }
    private static bool FixedEquals(string left, string right) { if (left.Length != right.Length) return false; int difference = 0; for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i]; return difference == 0; }
    private static bool PathEquals(string left, string right) => string.Equals(left, right, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool ValidReceipt(TrajectoryUploadReceipt receipt) => Hex(receipt.UploadId)
        && receipt.UploadIds != null && receipt.UploadIds.Length > 0 && receipt.UploadIds.Length <= MaximumParts
        && receipt.UploadId == receipt.UploadIds[0] && receipt.UploadIds.All(Hex)
        && (receipt.ModerationStatus == "approved" || receipt.ModerationStatus == "pending" || receipt.ModerationStatus == "hidden" || receipt.ModerationStatus == "rejected")
        && (receipt.MapCompatibility == "matched" || receipt.MapCompatibility == "waiting-map");
    private static TrajectoryUploadReceipt Clone(TrajectoryUploadReceipt receipt) => new()
    { UploadId = receipt.UploadId, UploadIds = (string[])receipt.UploadIds.Clone(), ModerationStatus = receipt.ModerationStatus,
        MapCompatibility = receipt.MapCompatibility, Duplicate = receipt.Duplicate, Cached = receipt.Cached };

    private static string Fingerprint(string path, CancellationToken cancellation)
    {
        if (!path.EndsWith(".peakrun", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Only sealed complete recordings can use upload acknowledgements.");
        AssertRealPath(path, false);
        var before = new FileInfo(path); long length = before.Length, written = before.LastWriteTimeUtc.Ticks, created = before.CreationTimeUtc.Ticks;
        var info = FullReplayArchive.ReadInfo(path, cancellation);
        if (!info.Complete || !string.IsNullOrEmpty(info.FaultCode) || !string.IsNullOrEmpty(info.Fault))
            throw new InvalidDataException("Unfinished or faulty recording cannot use upload acknowledgements.");
        var after = new FileInfo(path);
        if (after.Length != length || after.LastWriteTimeUtc.Ticks != written || after.CreationTimeUtc.Ticks != created)
            throw new InvalidDataException("Recording changed while reading upload identity.");
        string canonicalPath = Path.DirectorySeparatorChar == '\\' ? path.ToUpperInvariant() : path;
        return Hash("peak-memories/sealed-source/v1\n" + canonicalPath + "\n" + length + "\n" + written + "\n" + created + "\n"
            + Hash(JsonConvert.SerializeObject(info, JsonSettings)));
    }

    private static byte[] RandomKey() { var bytes = new byte[32]; using var random = RandomNumberGenerator.Create(); random.GetBytes(bytes); return bytes; }
    private static byte[] LoadKey(string path)
    {
        if (File.Exists(path)) { AssertRealPath(path, false); byte[] existing = ReadBounded(path, 32); if (existing.Length != 32) throw new InvalidDataException("Invalid private upload cache key."); return existing; }
        byte[] key = RandomKey();
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(key, 0, key.Length); file.Flush(true); return key;
    }
    private static byte[] ReadBounded(string path, int maximum)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length <= 0 || file.Length > maximum) throw new InvalidDataException("Invalid private upload cache size.");
        var bytes = new byte[(int)file.Length]; int count = 0;
        while (count < bytes.Length) { int read = file.Read(bytes, count, bytes.Length - count); if (read == 0) throw new EndOfStreamException(); count += read; }
        return bytes;
    }
    private static void EnsureDirectory(string path)
    {
        string full = Path.GetFullPath(path), root = Path.GetPathRoot(full)!;
        string current = root;
        foreach (string part in full.Substring(root.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        { current = Path.Combine(current, part); if (!Directory.Exists(current)) Directory.CreateDirectory(current); AssertRealPath(current, true); }
    }
    private static void AssertRealPath(string path, bool directory)
    {
        string full = Path.GetFullPath(path), root = Path.GetPathRoot(full)!;
        string current = root;
        string[] parts = full.Substring(root.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]); var attributes = File.GetAttributes(current);
            bool shouldBeDirectory = index < parts.Length - 1 || directory;
            if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != shouldBeDirectory)
                throw new InvalidDataException("Private upload cache paths must be real files and directories.");
        }
    }

    private sealed class Envelope { public int Version { get; set; } public string? Payload { get; set; } public string Mac { get; set; } = ""; }
    private sealed class CacheState
    {
        public string Key { get; set; } = ""; public string Endpoint { get; set; } = ""; public string ExporterVersion { get; set; } = "";
        public CacheExport? Export { get; set; } public List<CachePart> Parts { get; set; } = new();
        public bool Completed { get; set; } public TrajectoryUploadReceipt? Completion { get; set; }
    }
    private sealed class CachePart
    {
        public string Name { get; set; } = ""; public string Hash { get; set; } = ""; public long Length { get; set; }
        public TrajectoryUploadReceipt? Receipt { get; set; }
    }
    private sealed class CacheExport
    {
        public string RecordingId { get; set; } = ""; public long CompressedBytes { get; set; } public long DecodedBytes { get; set; }
        public long PointCount { get; set; } public int PlayerCount { get; set; } public long DurationMs { get; set; }
        public string[] PlayerNames { get; set; } = Array.Empty<string>(); public string DifficultyLabel { get; set; } = "";
        public bool NativeEvidence { get; set; } public bool StageGatesKnown { get; set; } public int MapLandmarkCount { get; set; }
        public string Scene { get; set; } = ""; public string[] Route { get; set; } = Array.Empty<string>();
        public bool Valid() => Hex(RecordingId) && CompressedBytes > 0 && DecodedBytes > 0 && PointCount >= 0 && PlayerCount > 0
            && DurationMs >= 0 && PlayerNames != null && PlayerNames.Length <= 4096 && PlayerNames.All(name => name != null && name.Length <= 128)
            && DifficultyLabel != null && DifficultyLabel.Length <= 256 && Scene != null && Scene.Length <= 256
            && Route != null && Route.Length <= 128 && Route.All(value => value != null && value.Length <= 128);
        public static CacheExport From(TrajectoryExportResult value) => new()
        { RecordingId = value.RecordingId, CompressedBytes = value.CompressedBytes, DecodedBytes = value.DecodedBytes, PointCount = value.PointCount,
            PlayerCount = value.PlayerCount, DurationMs = value.DurationMs, PlayerNames = (string[])value.PlayerNames.Clone(), DifficultyLabel = value.DifficultyLabel,
            NativeEvidence = value.NativeEvidence, StageGatesKnown = value.StageGatesKnown, MapLandmarkCount = value.MapLandmarkCount, Scene = value.Scene, Route = (string[])value.Route.Clone() };
        public TrajectoryExportResult Restore(string[] paths) => new()
        { Path = paths[0], Paths = paths, RecordingId = RecordingId, CompressedBytes = CompressedBytes, DecodedBytes = DecodedBytes, PointCount = PointCount,
            PlayerCount = PlayerCount, DurationMs = DurationMs, PlayerNames = (string[])PlayerNames.Clone(), DifficultyLabel = DifficultyLabel,
            NativeEvidence = NativeEvidence, StageGatesKnown = StageGatesKnown, MapLandmarkCount = MapLandmarkCount, Scene = Scene, Route = (string[])Route.Clone() };
    }
}
