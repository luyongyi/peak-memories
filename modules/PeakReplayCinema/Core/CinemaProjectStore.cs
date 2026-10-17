using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace PeakReplayLab.Cinema;

public static class CinemaProjectStore
{
    public const string Suffix = ".camera.json";
    public static string GetSidecarPath(string recordingPath)
    {
        if (string.IsNullOrWhiteSpace(recordingPath)) throw new ArgumentException("Missing recording path.", nameof(recordingPath));
        return recordingPath + Suffix;
    }
    private static string Sidecar(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Cinema projects must use a separate .camera.json sidecar.", nameof(path));
        return Path.GetFullPath(path);
    }
    private static JsonSerializer Serializer() => JsonSerializer.Create(new JsonSerializerSettings
    {
        TypeNameHandling = TypeNameHandling.None,
        MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
        MissingMemberHandling = MissingMemberHandling.Error,
        DateParseHandling = DateParseHandling.None,
        MaxDepth = 16,
        Converters = { new StringEnumConverter { AllowIntegerValues = false } }
    });
    public static CinemaProject Load(string sidecarPath, string expectedFingerprint)
    {
        CinemaLimits.Text(expectedFingerprint, 256, nameof(expectedFingerprint));
        string path = Sidecar(sidecarPath);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > CinemaLimits.MaxFileBytes) throw new InvalidDataException("Cinema project is too large.");
        // Cap the actual bytes read too: a file growing after the initial size check stays bounded.
        using var bytes = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = file.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (bytes.Length + read > CinemaLimits.MaxFileBytes) throw new InvalidDataException("Cinema project is too large.");
            bytes.Write(buffer, 0, read);
        }
        try
        {
            bytes.Position = 0;
            using var text = new StreamReader(bytes, new UTF8Encoding(false, true), true, 1024, true);
            using var json = new JsonTextReader(text) { MaxDepth = 16, DateParseHandling = DateParseHandling.None };
            var root = JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            while (json.Read()) if (json.TokenType != JsonToken.Comment) throw new InvalidDataException("Trailing cinema project data.");
            var project = root.ToObject<CinemaProject>(Serializer()) ?? throw new InvalidDataException("Missing cinema project.");
            project.Validate();
            if (!string.Equals(project.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("Cinema project belongs to another recording.");
            return project;
        }
        catch (Exception error) when (error is JsonException || error is ArgumentException || error is DecoderFallbackException)
        {
            throw new InvalidDataException("Invalid cinema project data.", error);
        }
    }
    public static void SaveAtomic(string sidecarPath, CinemaProject project) => SaveAtomic(sidecarPath, project, true);
    // File.Move commits the completed file only if the destination is still absent.
    // A prior existence check alone would allow a concurrent creator to be overwritten.
    public static void SaveNewAtomic(string sidecarPath, CinemaProject project) => SaveAtomic(sidecarPath, project, false);
    private static void SaveAtomic(string sidecarPath, CinemaProject project, bool overwrite)
    {
        string path = Sidecar(sidecarPath);
        if (project == null) throw new ArgumentNullException(nameof(project));
        project.Validate();
        string payload;
        using (var text = new StringWriter(System.Globalization.CultureInfo.InvariantCulture))
        using (var json = new JsonTextWriter(text) { Formatting = Formatting.Indented })
        {
            Serializer().Serialize(json, project); json.Flush(); payload = text.ToString();
        }
        byte[] bytes = new UTF8Encoding(false).GetBytes(payload);
        if (bytes.Length > CinemaLimits.MaxFileBytes) throw new InvalidDataException("Cinema project is too large.");
        string directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("Missing cinema project directory.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".cinema-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
            // Both operations keep the completed file in the same directory/filesystem.
            // Never delete the previous project as a fallback if atomic replacement fails.
            if (overwrite && File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
