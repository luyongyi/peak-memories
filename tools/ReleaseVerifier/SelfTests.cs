using System.IO.Compression;
using System.Text;
using System.Text.Json;

internal static class SelfTests
{
    private const string Commit = "1111111111111111111111111111111111111111";
    public static void Run()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PeakReplayLab.dll");
        Verifier.Require(File.Exists(fixture), "Synthetic fixture missing; build ReleaseVerifier first.");
        byte[] bytes = File.ReadAllBytes(fixture);
        var metadata = Verifier.Inspect(bytes);
        Verifier.Require(metadata.AssemblyName == "PeakReplayLab" && metadata.EmbeddedResources.Length == 11 &&
            metadata.InformationalVersion == "0.7.4+" + Commit, "Wrong synthetic fixture metadata.");
        string temp = Path.Combine(Path.GetTempPath(), "peak-release-verifier-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        int passed = 0;
        try
        {
            string repo = Path.Combine(temp, "repo"), zip = Path.Combine(temp, "test.zip");
            string source = Path.Combine(repo, "src", "PeakReplayLab"); Directory.CreateDirectory(source);
            void Sources(string projectVersion = "0.7.4", string pluginVersion = "0.7.4", string recorderVersion = "0.7.4")
            {
                File.WriteAllText(Path.Combine(source, "PeakReplayLab.csproj"), "<Project><PropertyGroup><Version>" + projectVersion + "</Version><AssemblyName>PeakReplayLab</AssemblyName></PropertyGroup></Project>");
                File.WriteAllText(Path.Combine(source, "Plugin.cs"), "[BepInPlugin(\"cn.mylus.peakreplaylab\", \"Synthetic fixture\", \"" + pluginVersion + "\")] class Plugin {}");
                File.WriteAllText(Path.Combine(source, "ReplayData.cs"), "class ReplayHeader { public string Recorder { get; set; } = \"PeakReplayLab/" + recorderVersion + "\"; } class ReplayRules { public const int CurrentSchema = 13; public static bool SupportedSchema(int schema) => schema == 10 || schema == 11 || schema == 12 || schema == CurrentSchema; }");
            }
            Manifest Fresh() => new()
            {
                SchemaVersion = 1, Tag = "v0.7.4", Version = "0.7.4", Commit = Commit,
                Assembly = new() { Name = metadata.AssemblyName, Version = metadata.AssemblyVersion, FileVersion = metadata.FileVersion,
                    InformationalVersion = metadata.InformationalVersion, Mvid = metadata.Mvid, File = "PeakReplayLab.dll", Bytes = bytes.LongLength, Sha256 = Verifier.Hash(bytes) },
                Replay = new() { CurrentSchema = 13, SupportedSchemas = new[] { 10, 11, 12, 13 } },
                Game = new() { SteamBuildId = null, UnityVersion = null, AssemblyCSharpMvid = "22222222222222222222222222222222",
                    References = new[] { "Assembly-CSharp.dll", "BepInEx.dll", "0Harmony.dll" }.Select(name => new ManifestReference
                        { File = name, Bytes = 1, Sha256 = Verifier.Hash(Encoding.UTF8.GetBytes("synthetic reference " + name)) }).ToArray() },
                Contracts = new() { Passed = true, Projects = Verifier.Projects.ToArray() }, Covers = Verifier.Covers.ToArray(),
            };
            Dictionary<string, byte[]> Entries(Manifest manifest) => new(StringComparer.Ordinal)
            {
                ["PeakReplayLab.dll"] = bytes,
                ["README-INSTALL.md"] = Encoding.UTF8.GetBytes("Synthetic test: install PeakReplayLab.dll into PEAK/BepInEx/plugins. BepInEx 5 is required."),
                ["build-manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, Verifier.Json),
            };
            void Checksum(Dictionary<string, byte[]> entries) => entries["SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(string.Join("\n",
                entries.Where(pair => pair.Key != "SHA256SUMS.txt").OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => Verifier.Hash(pair.Value) + "  " + pair.Key)) + "\n");
            void Archive(Dictionary<string, byte[]> entries, string? extraName = null)
            {
                File.Delete(zip); using var output = ZipFile.Open(zip, ZipArchiveMode.Create);
                foreach (var pair in entries) { using var stream = output.CreateEntry(pair.Key).Open(); stream.Write(pair.Value); }
                if (extraName != null) { using var stream = output.CreateEntry(extraName).Open(); stream.WriteByte(1); }
            }
            void Reject(string label, string expected, Action<Manifest>? changeManifest = null,
                Action<Dictionary<string, byte[]>>? changeEntries = null, string? extraName = null, Action? changeSource = null,
                string tag = "v0.7.4", string commit = Commit, bool recalculateChecksums = true)
            {
                Sources(); changeSource?.Invoke(); var manifest = Fresh(); changeManifest?.Invoke(manifest);
                var entries = Entries(manifest); Checksum(entries); changeEntries?.Invoke(entries); if (recalculateChecksums) Checksum(entries); Archive(entries, extraName);
                bool rejected = false;
                try { Verifier.Verify(zip, repo, tag, commit); }
                catch (Exception e) when (e is InvalidDataException or JsonException or BadImageFormatException)
                { Verifier.Require(e.Message.Contains(expected, StringComparison.OrdinalIgnoreCase), label + " failed for an unexpected reason: " + e.Message); rejected = true; }
                Verifier.Require(rejected, label + " was accepted."); passed++;
            }

            Sources(); var valid = Entries(Fresh()); Checksum(valid); Archive(valid); Verifier.Verify(zip, repo, "v0.7.4", Commit); passed++;
            Reject("tag format", "Tag must", tag: "v0.7.4-rc.1");
            Reject("full commit required", "Commit must", commit: "1111111");
            Reject("project version", "csproj version", changeSource: () => Sources(projectVersion: "0.7.3"));
            Reject("plugin version", "Plugin or Recorder", changeSource: () => Sources(pluginVersion: "0.7.3"));
            Reject("recorder version", "Plugin or Recorder", changeSource: () => Sources(recorderVersion: "0.7.3"));
            Reject("extra game DLL", "Unexpected", extraName: "Assembly-CSharp.dll");
            Reject("traversal", "Unexpected", extraName: "../PeakReplayLab.dll");
            Reject("duplicate DLL", "Duplicate ZIP", extraName: "PeakReplayLab.dll");
            Reject("oversized entry", "oversized", changeEntries: entries => entries["README-INSTALL.md"] = new byte[256 * 1024 + 1]);
            Reject("checksum tampering", "Checksum mismatch", changeEntries: entries => entries["README-INSTALL.md"][0] ^= 1, recalculateChecksums: false);
            Reject("missing README", "exactly the four", changeEntries: entries => entries.Remove("README-INSTALL.md"));
            Reject("native identity", "MVID disagrees", manifest => manifest.Assembly.Mvid = "33333333333333333333333333333333");
            Reject("DLL version", "assembly/file version", manifest => manifest.Assembly.FileVersion = "0.7.3.0");
            Reject("DLL bytes", "bytes or SHA256", manifest => manifest.Assembly.Bytes++);
            Reject("missing cover", "Manifest covers", manifest => manifest.Covers = manifest.Covers[1..]);
            Reject("schema drift", "current replay schema", manifest => manifest.Replay.CurrentSchema = 12);
            Reject("unsupported old schema", "Supported replay schemas", manifest => manifest.Replay.SupportedSchemas = new[] { 9, 10, 11, 12, 13 });
            Reject("failed contracts", "did not pass", manifest => manifest.Contracts.Passed = false);
            Reject("missing contract", "Contract projects", manifest => manifest.Contracts.Projects = manifest.Contracts.Projects[1..]);
            Reject("reference path", "basenames", manifest => manifest.Game.References[0].File = "private/Assembly-CSharp.dll");
            Reject("missing key reference", "Required compile reference", manifest => manifest.Game.References[0].File = "Other.dll");
            Reject("duplicate reference", "basenames", manifest => manifest.Game.References[1].File = "Assembly-CSharp.dll");
            Reject("machine path", "machine path", manifest => manifest.Game.UnityVersion = "C:/Users/private");
            Reject("information commit", "informational version", manifest => manifest.Commit = new string('3', 40), commit: new string('3', 40));
            Reject("duplicate JSON", "Duplicate JSON", changeEntries: entries =>
            {
                string json = Encoding.UTF8.GetString(entries["build-manifest.json"]);
                entries["build-manifest.json"] = Encoding.UTF8.GetBytes(json.Insert(1, "\"tag\":\"v0.7.4\","));
            });
            Reject("unmapped JSON path", "could not be mapped", changeEntries: entries =>
            {
                string json = Encoding.UTF8.GetString(entries["build-manifest.json"]);
                entries["build-manifest.json"] = Encoding.UTF8.GetBytes(json.Insert(1, "\"privatePath\":\"machine-local\","));
            });
            Console.WriteLine("Release verifier self-test: " + passed + " passed (synthetic managed fixture only).");
        }
        finally
        {
            string full = Path.GetFullPath(temp), parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("peak-release-verifier-test-", StringComparison.Ordinal)) Directory.Delete(full, true);
        }
    }
}
