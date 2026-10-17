using System;
using System.Collections.Generic;
using System.IO;

namespace PeakReplayLab.Cinema;

// Sidecar management stays local to the source recording. Recovery data has a
// distinct reserved suffix and is never promoted over an ordinary saved plan.
internal static class CinemaProjectFiles
{
    private const string RecoverySuffix = ".recovery.camera.json";
    private const int MaxPlans = 256;
    private const int MaxEntries = 4096;

    public static string[] List(string source)
    {
        string recording = Source(source);
        string directory = Path.GetDirectoryName(recording)!;
        string prefix = Path.GetFileName(recording) + ".";
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        int visited = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (++visited > MaxEntries) break;
            string file = Path.GetFileName(entry);
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !file.EndsWith(CinemaProjectStore.Suffix, StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(RecoverySuffix, StringComparison.OrdinalIgnoreCase) ||
                file.Length <= prefix.Length + CinemaProjectStore.Suffix.Length) continue;
            string name = file.Substring(prefix.Length, file.Length - prefix.Length - CinemaProjectStore.Suffix.Length);
            try
            {
                string normal = Plan(recording, name);
                if (!string.Equals(normal, entry, StringComparison.OrdinalIgnoreCase)) continue;
                Regular(normal, true);
            }
            catch (Exception error) when (error is ArgumentException || error is IOException || error is UnauthorizedAccessException)
            { continue; }
            names.Add(name);
            if (names.Count >= MaxPlans) break;
        }
        var result = new string[names.Count]; names.CopyTo(result); return result;
    }

    public static string RecoveryPath(string source, string name) => Plan(Source(source), name) + RecoverySuffix;

    // Unsaved new plans may exist only as recovery data. Make those names
    // discoverable after a restart without presenting a recovery as a saved plan.
    public static string[] ListRecoveries(string source)
    {
        string recording = Source(source);
        string prefix = Path.GetFileName(recording) + ".";
        string ending = CinemaProjectStore.Suffix + RecoverySuffix;
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        int visited = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(recording)!))
        {
            if (++visited > MaxEntries) break;
            string file = Path.GetFileName(entry);
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !file.EndsWith(ending, StringComparison.OrdinalIgnoreCase) || file.Length <= prefix.Length + ending.Length) continue;
            string name = file.Substring(prefix.Length, file.Length - prefix.Length - ending.Length);
            try { if (!string.Equals(RecoveryPath(recording, name), entry, StringComparison.OrdinalIgnoreCase)) continue; Regular(entry, true); }
            catch (Exception error) when (error is ArgumentException || error is IOException || error is UnauthorizedAccessException) { continue; }
            names.Add(name);
            if (names.Count >= MaxPlans) break;
        }
        var result = new string[names.Count]; names.CopyTo(result); return result;
    }

    public static void Copy(string source, string from, string to, string fingerprint)
    {
        string recording = Source(source);
        string previous = Plan(recording, from), target = Plan(recording, to);
        Different(previous, target);
        Regular(previous, true); Absent(target);
        CinemaProject project = CinemaProjectStore.Load(previous, fingerprint);
        // Recheck link state after reading, then use a create-only atomic commit.
        Regular(previous, true); Absent(target);
        CinemaProjectStore.SaveNewAtomic(target, project);
    }

    public static void Rename(string source, string from, string to, string fingerprint)
    {
        string recording = Source(source);
        string previous = Plan(recording, from), target = Plan(recording, to);
        Different(previous, target);
        Regular(previous, true); Absent(target);
        CinemaProjectStore.Load(previous, fingerprint);
        Regular(previous, true); Absent(target);
        // The two-argument API never replaces another plan, including a target
        // created between the check and this operation.
        File.Move(previous, target);
    }

    public static void RemoveRecovery(string source, string name)
    {
        string path = RecoveryPath(source, name);
        if (Regular(path, false)) File.Delete(path);
    }

    public static void SaveRecovery(string source, string name, CinemaProject project)
    {
        string path = RecoveryPath(source, name);
        Regular(path, false);
        CinemaProjectStore.SaveAtomic(path, project);
    }

    public static CinemaProject LoadRecovery(string source, string name, string fingerprint)
    {
        string path = RecoveryPath(source, name);
        Regular(path, true);
        return CinemaProjectStore.Load(path, fingerprint);
    }

    private static string Source(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Missing recording path.", nameof(source));
        string path = Path.GetFullPath(source);
        Regular(path, true);
        return path;
    }

    private static string Plan(string source, string name)
    {
        string path = CinemaRecordingIdentity.Sidecar(source, name);
        if (name.Trim().EndsWith(".recovery", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("方案名称不能以 .recovery 结尾。", nameof(name));
        return path;
    }

    private static void Different(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            throw new IOException("目标方案名称与原方案相同。请选择新名称。");
    }

    private static void Absent(string path)
    {
        if (Regular(path, false)) throw new IOException("目标方案已存在，不能覆盖。");
    }

    // GetAttributes sees dangling links too, unlike File.Exists. Check every
    // containing directory so a junction cannot redirect a local sidecar write.
    private static bool Regular(string path, bool required)
    {
        string? parent = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(parent))
        {
            FileAttributes attributes = File.GetAttributes(parent);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                throw new IOException("Cinema paths cannot use symbolic links or junctions.");
            string? next = Path.GetDirectoryName(parent);
            if (string.Equals(next, parent, StringComparison.Ordinal)) break;
            parent = next;
        }
        FileAttributes file;
        try { file = File.GetAttributes(path); }
        catch (FileNotFoundException) when (!required) { return false; }
        catch (DirectoryNotFoundException) when (!required) { return false; }
        if ((file & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
            throw new IOException("Cinema projects must be regular files.");
        return true;
    }
}
