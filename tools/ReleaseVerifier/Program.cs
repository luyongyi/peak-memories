using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args is ["inspect", var dll])
            {
                Console.WriteLine(JsonSerializer.Serialize(Verifier.Inspect(File.ReadAllBytes(dll)), Verifier.Json));
                return 0;
            }
            if (args is ["verify", var zip, var repo, var tag, var commit])
            {
                Verifier.Verify(zip, repo, tag, commit);
                Console.WriteLine("Release package verified: " + tag);
                return 0;
            }
            if (args is ["self-test"]) { SelfTests.Run(); return 0; }
            Console.Error.WriteLine("Usage: ReleaseVerifier inspect <dll> | verify <zip> <repoRoot> <tag> <commit> | self-test");
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Release verification failed: " + error.Message);
            return 1;
        }
    }
}

internal sealed record DllMetadata(string AssemblyName, string AssemblyVersion, string FileVersion,
    string InformationalVersion, string Mvid, string[] EmbeddedResources);

internal static class Verifier
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 20,
    };
    public static readonly string[] Covers = new[] { "shore", "roots", "tropics", "alpine", "mesa", "volcano", "swamp", "kiln", "temple", "peak", "nadir" }
        .Select(value => "PeakReplayLab.CoverArt." + value + ".png").ToArray();
    public static readonly string[] Projects = { "ReplayContract", "NativeAppearanceContract", "NativeLoadingContract", "ReplayViewContract", "ReplayNameplateContract", "ReplayAudioSpatialContract", "TrajectoryContract", "TrajectoryUploadContract" };
    private static readonly Dictionary<string, long> Limits = new(StringComparer.Ordinal)
    {
        ["PeakReplayLab.dll"] = 128L * 1024 * 1024,
        ["README-INSTALL.md"] = 256L * 1024,
        ["SHA256SUMS.txt"] = 16L * 1024,
        ["build-manifest.json"] = 2L * 1024 * 1024,
    };
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool Hex(string? value, int count) => value != null && Regex.IsMatch(value, "\\A[0-9a-fA-F]{" + count + "}\\z");
    private static bool Equal(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool GuidText(string? value) => value != null && (Guid.TryParseExact(value, "N", out var n) && n != Guid.Empty || Guid.TryParseExact(value, "D", out var d) && d != Guid.Empty);
    private static string Text(byte[] bytes) => Utf8.GetString(bytes).TrimStart('\uFEFF');

    public static DllMetadata Inspect(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var pe = new PEReader(stream);
        Require(pe.HasMetadata, "DLL has no managed metadata.");
        var metadata = pe.GetMetadataReader();
        Require(metadata.IsAssembly, "DLL is not a managed assembly.");
        var definition = metadata.GetAssemblyDefinition();
        string fileVersion = "", information = "";
        foreach (var handle in definition.GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            string type = AttributeType(metadata, attribute.Constructor);
            if (type != "System.Reflection.AssemblyFileVersionAttribute" && type != "System.Reflection.AssemblyInformationalVersionAttribute") continue;
            var blob = metadata.GetBlobReader(attribute.Value);
            Require(blob.ReadUInt16() == 1, "Invalid assembly version attribute.");
            string value = blob.ReadSerializedString() ?? "";
            if (type.EndsWith("AssemblyFileVersionAttribute", StringComparison.Ordinal)) fileVersion = value; else information = value;
        }
        var resources = metadata.ManifestResources.Select(handle => metadata.GetManifestResource(handle))
            .Where(resource => resource.Implementation.IsNil).Select(resource => metadata.GetString(resource.Name)).ToArray();
        return new(metadata.GetString(definition.Name), definition.Version.ToString(), fileVersion, information,
            metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("N"), resources);
    }
    private static string AttributeType(MetadataReader metadata, EntityHandle constructor)
    {
        EntityHandle type = constructor.Kind switch
        {
            HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            _ => default,
        };
        if (type.Kind == HandleKind.TypeReference)
        { var value = metadata.GetTypeReference((TypeReferenceHandle)type); return metadata.GetString(value.Namespace) + "." + metadata.GetString(value.Name); }
        if (type.Kind == HandleKind.TypeDefinition)
        { var value = metadata.GetTypeDefinition((TypeDefinitionHandle)type); return metadata.GetString(value.Namespace) + "." + metadata.GetString(value.Name); }
        return "";
    }

    public static void Verify(string zip, string repo, string tag, string commit)
    {
        Require(Regex.IsMatch(tag, "\\Av(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\z"), "Tag must be vX.Y.Z without prerelease or leading zeroes.");
        Require(Hex(commit, 40), "Commit must contain exactly 40 hexadecimal characters.");
        string version = tag[1..];
        var source = Source(repo, version);
        Require(new FileInfo(zip).Length <= 160L * 1024 * 1024, "ZIP exceeds the package limit.");
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var entry in archive.Entries)
            {
                Require(Limits.TryGetValue(entry.FullName, out long limit), "Unexpected or non-flat ZIP entry: " + entry.FullName);
                Require(!entries.ContainsKey(entry.FullName), "Duplicate ZIP entry: " + entry.FullName);
                Require(entry.Length > 0 && entry.Length <= limit, "ZIP entry is empty or oversized: " + entry.FullName);
                using var input = entry.Open(); using var output = new MemoryStream();
                var buffer = new byte[64 * 1024]; int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                { Require(output.Length + read <= limit, "Decoded ZIP entry exceeds its limit: " + entry.FullName); output.Write(buffer, 0, read); }
                Require(output.Length == entry.Length, "ZIP entry length disagrees with decoded bytes: " + entry.FullName);
                entries.Add(entry.FullName, output.ToArray());
            }
        }
        Require(entries.Count == Limits.Count, "ZIP must contain exactly the four release files.");
        Checksums(entries);
        Require(Text(entries["README-INSTALL.md"]).Length >= 16, "Installation README is empty.");
        using var document = JsonDocument.Parse(entries["build-manifest.json"], new JsonDocumentOptions { MaxDepth = 20 });
        UniqueJson(document.RootElement);
        var manifest = document.Deserialize<Manifest>(Json) ?? throw new InvalidDataException("Missing build manifest.");
        Require(manifest.SchemaVersion == 1 && manifest.Tag == tag && manifest.Version == version && Equal(manifest.Commit, commit), "Manifest schema, tag, version or commit disagrees.");
        Require(manifest.Assembly != null && manifest.Replay != null && manifest.Game != null && manifest.Contracts != null, "Manifest sections must not be null.");
        byte[] dll = entries["PeakReplayLab.dll"];
        var actual = Inspect(dll); var assembly = manifest.Assembly!;
        Require(assembly.File == "PeakReplayLab.dll" && assembly.Name == "PeakReplayLab" && actual.AssemblyName == assembly.Name, "Wrong release assembly identity.");
        Require(assembly.Bytes == dll.LongLength && Hex(assembly.Sha256, 64) && Equal(assembly.Sha256, Hash(dll)), "DLL bytes or SHA256 disagree with manifest.");
        Require(assembly.Version == version + ".0" && assembly.Version == actual.AssemblyVersion &&
            assembly.FileVersion == version + ".0" && assembly.FileVersion == actual.FileVersion, "DLL assembly/file version disagrees with tag.");
        Require(assembly.InformationalVersion == actual.InformationalVersion && actual.InformationalVersion.StartsWith(version + "+" + commit, StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(actual.InformationalVersion, "\\A[0-9A-Za-z.+-]+\\z"), "DLL informational version does not identify this version and commit.");
        Require(GuidText(assembly.Mvid) && Equal(Guid.Parse(assembly.Mvid).ToString("N"), actual.Mvid), "DLL MVID disagrees with manifest.");
        SameSet(manifest.Covers, Covers, "Manifest covers"); SameSet(actual.EmbeddedResources, Covers, "Embedded DLL covers");
        Require(manifest.Replay!.CurrentSchema == source.CurrentSchema, "Manifest current replay schema disagrees with source.");
        SameSet(manifest.Replay.SupportedSchemas, source.SupportedSchemas, "Supported replay schemas");
        Require(manifest.Game!.SteamBuildId == null || manifest.Game.SteamBuildId > 0, "Invalid Steam build ID.");
        Require(manifest.Game.UnityVersion == null || Regex.IsMatch(manifest.Game.UnityVersion, "\\A[0-9A-Za-z][0-9A-Za-z._+ -]{0,127}\\z"), "Invalid Unity version or machine path in manifest.");
        Require(GuidText(manifest.Game.AssemblyCSharpMvid), "Invalid Assembly-CSharp MVID.");
        var references = manifest.Game.References;
        Require(references != null && references.Length is >= 3 and <= 1024, "Missing or excessive game reference metadata.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references!)
        {
            Require(reference != null && !string.IsNullOrWhiteSpace(reference.File) && reference.File.Length <= 192 &&
                reference.File.IndexOfAny(new[] { '/', '\\', ':', '\r', '\n', '\0' }) < 0 && reference.File.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(reference.File) == reference.File && names.Add(reference.File), "Reference filenames must be unique DLL basenames.");
            Require(reference!.Bytes > 0 && Hex(reference.Sha256, 64), "Invalid reference size or SHA256: " + reference.File);
        }
        Require(new[] { "Assembly-CSharp.dll", "BepInEx.dll", "0Harmony.dll" }.All(names.Contains), "Required compile reference metadata is missing.");
        Require(manifest.Contracts!.Passed, "Contract checks did not pass.");
        SameSet(manifest.Contracts.Projects, Projects, "Contract projects");
    }

    private static void Checksums(Dictionary<string, byte[]> entries)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in Text(entries["SHA256SUMS.txt"]).Split('\n'))
        {
            string value = line.TrimEnd('\r'); if (value.Length == 0) continue;
            var match = Regex.Match(value, "\\A([0-9A-Fa-f]{64})  (.+)\\z");
            Require(match.Success, "Invalid SHA256SUMS line."); string name = match.Groups[2].Value;
            Require(name != "SHA256SUMS.txt" && entries.ContainsKey(name) && found.Add(name), "Unexpected or duplicate checksum filename.");
            Require(Equal(match.Groups[1].Value, Hash(entries[name])), "Checksum mismatch: " + name);
        }
        Require(found.SetEquals(new[] { "PeakReplayLab.dll", "README-INSTALL.md", "build-manifest.json" }), "SHA256SUMS must cover DLL, README and manifest exactly.");
    }
    private static void UniqueJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { Require(names.Add(property.Name), "Duplicate JSON manifest property: " + property.Name); UniqueJson(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) UniqueJson(item);
    }
    private static void SameSet<T>(T[]? actual, T[] expected, string label) where T : notnull
    { Require(actual != null && actual.Length == expected.Length && actual.Distinct().Count() == actual.Length && actual.ToHashSet().SetEquals(expected), label + " are missing, duplicated or unexpected."); }
    private sealed record SourceRules(int CurrentSchema, int[] SupportedSchemas);
    private static SourceRules Source(string root, string version)
    {
        string Read(string relative)
        { var path = Path.Combine(root, relative); Require(File.Exists(path), "Required source missing: " + relative); return File.ReadAllText(path); }
        using var xml = XmlReader.Create(new StringReader(Read("src/PeakReplayLab/PeakReplayLab.csproj")), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var project = XDocument.Load(xml);
        var versions = project.Descendants().Where(value => value.Name.LocalName == "Version").Select(value => value.Value.Trim()).ToArray();
        var names = project.Descendants().Where(value => value.Name.LocalName == "AssemblyName").Select(value => value.Value.Trim()).ToArray();
        Require(versions is [var projectVersion] && projectVersion == version && names is ["PeakReplayLab"], "csproj version or assembly name disagrees with release.");
        string Match(string text, string pattern, string label)
        { var matches = Regex.Matches(text, pattern); Require(matches.Count == 1, "Missing or ambiguous source " + label); return matches[0].Groups[1].Value; }
        string plugin = Match(Read("src/PeakReplayLab/Plugin.cs"), "\\bBepInPlugin\\s*\\(\\s*\"cn\\.mylus\\.peakreplaylab\"\\s*,\\s*\"[^\"]*\"\\s*,\\s*\"([^\"]+)\"\\s*\\)", "Plugin version");
        string data = Read("src/PeakReplayLab/ReplayData.cs");
        string recorder = Match(data, "\\bRecorder\\s*\\{[^}]*\\}\\s*=\\s*\"PeakReplayLab/([^\"]+)\"", "Recorder version");
        Require(plugin == version && recorder == version, "Plugin or Recorder version disagrees with release.");
        int current = int.Parse(Match(data, "\\bconst\\s+int\\s+CurrentSchema\\s*=\\s*([0-9]+)\\s*;", "CurrentSchema"));
        string supported = Match(data, "\\bSupportedSchema\\s*\\(\\s*int\\s+schema\\s*\\)\\s*=>\\s*([^;]+);", "SupportedSchema");
        var schemas = supported.Split("||").Select(expression =>
        {
            var match = Regex.Match(expression.Trim(), "\\Aschema\\s*==\\s*(CurrentSchema|[0-9]+)\\z");
            Require(match.Success, "Unsupported SupportedSchema expression; update release verifier.");
            return match.Groups[1].Value == "CurrentSchema" ? current : int.Parse(match.Groups[1].Value);
        }).Distinct().ToArray();
        Require(current > 0 && schemas.Contains(current) && schemas.All(value => value > 0), "Invalid source replay schemas.");
        return new(current, schemas);
    }
}

internal sealed class Manifest
{
    public required int SchemaVersion { get; set; }
    public required string Tag { get; set; }
    public required string Version { get; set; }
    public required string Commit { get; set; }
    public required ManifestAssembly Assembly { get; set; }
    public required ManifestReplay Replay { get; set; }
    public required ManifestGame Game { get; set; }
    public required ManifestContracts Contracts { get; set; }
    public required string[] Covers { get; set; }
}
internal sealed class ManifestAssembly
{
    public required string Name { get; set; }
    public required string Version { get; set; }
    public required string FileVersion { get; set; }
    public required string InformationalVersion { get; set; }
    public required string Mvid { get; set; }
    public required string File { get; set; }
    public required long Bytes { get; set; }
    public required string Sha256 { get; set; }
}
internal sealed class ManifestReplay
{
    public required int CurrentSchema { get; set; }
    public required int[] SupportedSchemas { get; set; }
}
internal sealed class ManifestGame
{
    public required int? SteamBuildId { get; set; }
    public required string? UnityVersion { get; set; }
    public required string AssemblyCSharpMvid { get; set; }
    public required ManifestReference[] References { get; set; }
}
internal sealed class ManifestReference
{
    public required string File { get; set; }
    public required long Bytes { get; set; }
    public required string Sha256 { get; set; }
}
internal sealed class ManifestContracts
{
    public required string[] Projects { get; set; }
    public required bool Passed { get; set; }
}
