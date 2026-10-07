using PeakReplayLab;

internal static class ReplayTrashTests
{
    public static void Run(Action<string, Action> test)
    {
        test("replay trash constructor performs no filesystem creation and rejects broad or overlapping roots", () => Fixture(f =>
        {
            string missing = Path.Combine(f.Base, "not-created");
            _ = new ReplayTrash(Path.Combine(missing, "Memories"), Path.Combine(missing, "Recordings"), Path.Combine(missing, "Trash"));
            Check(!Directory.Exists(missing));
            Reject(() => new ReplayTrash(f.Memories, f.Recordings, f.Memories));
            Reject(() => new ReplayTrash(f.Memories, f.Recordings, Path.Combine(f.Recordings, "Trash")));
            string drive = Path.GetPathRoot(f.Base)!;
            Reject(() => new ReplayTrash(Path.Combine(drive, "Memories"), Path.Combine(drive, "Recordings"), Path.Combine(drive, "Trash")));
            Check(!Directory.Exists(f.Trash));
        }));
        test("replay trash rejects outside traversal alternate streams network and device paths before filesystem access", () => Fixture(f =>
        {
            string foreign = Path.Combine(f.Base, "unrelated.txt"); File.WriteAllText(foreign, "untouched");
            foreach (string path in new[]
            {
                foreign, f.Memories, f.Recordings, f.Trash, Path.Combine(f.Memories, "nested", "a.peakreplay"),
                Path.Combine(f.Memories, "..", "Recordings", "a.peakrun"), Path.Combine(f.Memories, ".", "a.peakreplay"),
                Path.Combine(f.Memories, "a.peakreplay") + ":alternate", "\\\\server\\share\\a.peakrun", "//server/share/a.peakrun",
                "\\\\?\\C:\\a.peakrun", "relative.peakreplay", Path.Combine(f.Memories, "NUL.peakreplay"),
                Path.Combine(f.Memories, "trailing. ", "a.peakreplay"), Path.Combine(f.Memories, "a.peakreplay") + " ",
                Path.Combine(f.Memories, "wrong.json"), Path.Combine(f.Memories, "a.peakreplay.partial"),
                Path.Combine(f.Memories, "a.peakrun"), Path.Combine(f.Recordings, "a.peakreplay"),
            }) Reject(() => f.Service.Prepare(path));
            Check(File.ReadAllText(foreign) == "untouched" && !Directory.Exists(f.Trash));
        }));
        if (Path.DirectorySeparatorChar != '\\')
        {
            test("replay trash unsupported platform explicitly refuses a valid move without copy or deletion", () => Fixture(f =>
            {
                string source = f.Highlight("valid.peakreplay", "keep");
                Reject<PlatformNotSupportedException>(() => f.Service.Prepare(source));
                Check(File.ReadAllText(source) == "keep" && !Directory.Exists(f.Trash));
            }));
            return;
        }
        test("replay trash preparation is readonly and an explicit move preserves one recoverable original file", () => Fixture(f =>
        {
            string source = f.Highlight("saved memory.peakreplay", "exact original bytes");
            var ticket = f.Service.Prepare(source);
            Check(ticket.SourcePath == Path.GetFullPath(source) && ticket.FileName == "saved memory.peakreplay");
            Check(ticket.Length == new FileInfo(source).Length && ticket.LastWriteTimeUtc == File.GetLastWriteTimeUtc(source));
            Check(File.Exists(source) && !Directory.Exists(f.Trash));
            var result = f.Service.MoveToTrash(ticket);
            Check(!File.Exists(source) && result.SourcePath == ticket.SourcePath && result.FileName == ticket.FileName && result.Length == ticket.Length);
            Check(Path.GetDirectoryName(Path.GetDirectoryName(result.DestinationPath)) == f.Trash);
            Check(Path.GetFileName(result.DestinationPath) == ticket.FileName && File.ReadAllText(result.DestinationPath) == "exact original bytes");
            Check(Directory.GetFiles(Path.GetDirectoryName(result.DestinationPath)!).Length == 1);
            // Explicit manual restore in this generated fixture; backend never purges.
            File.Move(result.DestinationPath, source);
            Check(File.ReadAllText(source) == "exact original bytes" && !File.Exists(result.DestinationPath));
        }));
        test("replay trash unique destinations never overwrite earlier files with the same original name", () => Fixture(f =>
        {
            string source = f.Highlight("same.peakreplay", "first");
            var first = f.Service.MoveToTrash(f.Service.Prepare(source));
            File.WriteAllText(source, "second"); var second = f.Service.MoveToTrash(f.Service.Prepare(source));
            Check(first.DestinationPath != second.DestinationPath && File.ReadAllText(first.DestinationPath) == "first");
            Check(File.ReadAllText(second.DestinationPath) == "second" && Directory.GetDirectories(f.Trash).Length == 2);
        }));
        test("replay trash accepts complete and interrupted full recording extensions without touching neighbors", () => Fixture(f =>
        {
            string neighbor = Path.Combine(f.Recordings, "private-notes.txt"); File.WriteAllText(neighbor, "keep neighbor");
            foreach (string name in new[] { "complete.peakrun", "stopped.PeAkRuN.PaRtIaL" })
            {
                string path = Path.Combine(f.Recordings, name); File.WriteAllText(path, name);
                var result = f.Service.MoveToTrash(f.Service.Prepare(path));
                Check(File.ReadAllText(result.DestinationPath) == name && !File.Exists(path));
            }
            Check(File.ReadAllText(neighbor) == "keep neighbor");
        }));
        test("replay trash confirmation rejects changed length modification time and replaced file identity", () => Fixture(f =>
        {
            string source = f.Highlight("changed.peakreplay", "original"); var ticket = f.Service.Prepare(source);
            File.AppendAllText(source, "more"); Reject<IOException>(() => f.Service.MoveToTrash(ticket));
            ticket = f.Service.Prepare(source); File.SetLastWriteTimeUtc(source, ticket.LastWriteTimeUtc.AddSeconds(5));
            Reject<IOException>(() => f.Service.MoveToTrash(ticket));
            ticket = f.Service.Prepare(source);
            string original = Path.Combine(f.Base, "preserved-original.txt"); File.Move(source, original);
            File.WriteAllBytes(source, new byte[ticket.Length]); File.SetCreationTimeUtc(source, ticket.CreationTimeUtc); File.SetLastWriteTimeUtc(source, ticket.LastWriteTimeUtc);
            Reject<IOException>(() => f.Service.MoveToTrash(ticket));
            Check(File.Exists(source) && File.ReadAllText(original) == "originalmore" && !Directory.Exists(f.Trash));
        }));
        test("replay trash rejects reused or foreign-service tickets without moving a new file", () => Fixture(f =>
        {
            string source = f.Highlight("once.peakreplay", "original"); var ticket = f.Service.Prepare(source);
            var other = new ReplayTrash(f.Memories, f.Recordings, f.Trash);
            Reject<ArgumentException>(() => other.MoveToTrash(ticket)); Check(File.Exists(source));
            var moved = f.Service.MoveToTrash(ticket); Reject<IOException>(() => f.Service.MoveToTrash(ticket));
            File.WriteAllText(source, "new file"); Reject<IOException>(() => f.Service.MoveToTrash(ticket));
            Check(File.ReadAllText(moved.DestinationPath) == "original" && File.ReadAllText(source) == "new file");
        }));
        test("replay trash rejects directory and readonly selections without modifying attributes", () => Fixture(f =>
        {
            string directory = Path.Combine(f.Memories, "not-a-file.peakreplay"); Directory.CreateDirectory(directory);
            Reject<IOException>(() => f.Service.Prepare(directory)); Check(Directory.Exists(directory));
            string source = f.Highlight("readonly.peakreplay", "protected"); var before = File.GetAttributes(source);
            try
            {
                File.SetAttributes(source, before | FileAttributes.ReadOnly);
                Reject<UnauthorizedAccessException>(() => f.Service.Prepare(source));
                Check((File.GetAttributes(source) & FileAttributes.ReadOnly) != 0 && !Directory.Exists(f.Trash));
            }
            finally { File.SetAttributes(source, before); }
        }));
        test("replay trash refuses an active writer or reader and never retries with looser sharing", () => Fixture(f =>
        {
            string source = f.Highlight("busy.peakreplay", "busy");
            using (var activeWriter = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.Read))
                Reject<IOException>(() => f.Service.Prepare(source));
            var ticket = f.Service.Prepare(source);
            using (var activeReader = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject<IOException>(() => f.Service.MoveToTrash(ticket));
            Check(File.ReadAllText(source) == "busy" && !Directory.Exists(f.Trash));
        }));
        test("replay trash refuses symbolic source files and a linked trash target when supported", () => Fixture(f =>
        {
            string foreign = Path.Combine(f.Base, "foreign.txt"); File.WriteAllText(foreign, "do not move");
            string link = Path.Combine(f.Memories, "linked.peakreplay");
            if (!TryFileLink(link, foreign)) return;
            try { Reject<IOException>(() => f.Service.Prepare(link)); Check(File.ReadAllText(foreign) == "do not move"); }
            finally { File.Delete(link); }
            string source = f.Highlight("good.peakreplay", "good"); var ticket = f.Service.Prepare(source);
            string foreignDirectory = Path.Combine(f.Base, "foreign-trash"); Directory.CreateDirectory(foreignDirectory);
            if (!TryDirectoryLink(f.Trash, foreignDirectory)) return;
            try
            {
                Reject<IOException>(() => f.Service.MoveToTrash(ticket));
                Check(File.ReadAllText(source) == "good" && Directory.GetFileSystemEntries(foreignDirectory).Length == 0);
            }
            finally { Directory.Delete(f.Trash); }
        }));
        test("replay trash rejects reparse ancestors rather than following linked recording roots when supported", () => Fixture(f =>
        {
            string source = f.Highlight("original.peakreplay", "keep");
            string linked = Path.Combine(f.Base, "alias"); if (!TryDirectoryLink(linked, f.Mod)) return;
            try
            {
                var service = new ReplayTrash(Path.Combine(linked, "Memories"), Path.Combine(linked, "Recordings"), Path.Combine(linked, "Trash"));
                Reject<IOException>(() => service.Prepare(Path.Combine(linked, "Memories", "original.peakreplay")));
                Check(File.ReadAllText(source) == "keep" && !Directory.Exists(f.Trash));
            }
            finally { Directory.Delete(linked); }
        }));
    }
    private sealed class Scope
    {
        public readonly string Base, Mod, Memories, Recordings, Trash;
        public readonly ReplayTrash Service;
        public Scope(string root)
        {
            Base = root; Mod = Path.Combine(root, "PeakReplayLab"); Memories = Path.Combine(Mod, "Memories");
            Recordings = Path.Combine(Mod, "Recordings"); Trash = Path.Combine(Mod, "Trash");
            Directory.CreateDirectory(Memories); Directory.CreateDirectory(Recordings); Service = new ReplayTrash(Memories, Recordings, Trash);
        }
        public string Highlight(string name, string content)
        { string path = Path.Combine(Memories, name); File.WriteAllText(path, content); return path; }
    }
    private static void Fixture(Action<Scope> action)
    {
        // Only this new, generated fixture is ever cleaned up. No game/replay
        // discovery, user-selected directory, or real recording enters a test.
        string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string generatedName = "peak-trash-fixture-" + Guid.NewGuid().ToString("N");
        string root = Path.GetFullPath(Path.Combine(temporaryRoot, generatedName));
        ValidateFixtureTarget(root, temporaryRoot, generatedName);
        Directory.CreateDirectory(root);
        try { action(new Scope(root)); }
        finally
        {
            // Resolve and verify the exact recursive cleanup target immediately
            // before cleanup; never trust a broad or redirected computed path.
            ValidateFixtureTarget(root, temporaryRoot, generatedName);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to recursively clean up a redirected fixture root.");
            Directory.Delete(root, recursive: true);
        }
    }
    private static void ValidateFixtureTarget(string root, string temporaryRoot, string generatedName)
    {
        string resolved = Path.GetFullPath(root);
        string? parent = Path.GetDirectoryName(resolved);
        var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (parent == null || !string.Equals(Path.TrimEndingDirectorySeparator(parent), temporaryRoot, comparison) ||
            !string.Equals(Path.GetFileName(resolved), generatedName, StringComparison.Ordinal) ||
            !generatedName.StartsWith("peak-trash-fixture-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(generatedName.Substring("peak-trash-fixture-".Length), "N", out _))
            throw new IOException("Fixture cleanup target is not the exact generated temporary directory.");
    }
    private static bool TryFileLink(string path, string target)
    {
        try { File.CreateSymbolicLink(path, target); return true; }
        catch (Exception e) when (e is UnauthorizedAccessException || e is IOException || e is PlatformNotSupportedException)
        { Console.WriteLine("SKIP symbolic-file fixture: OS privilege/support unavailable (" + e.GetType().Name + ")."); return false; }
    }
    private static bool TryDirectoryLink(string path, string target)
    {
        try { Directory.CreateSymbolicLink(path, target); return true; }
        catch (Exception e) when (e is UnauthorizedAccessException || e is IOException || e is PlatformNotSupportedException)
        { Console.WriteLine("SKIP symbolic-directory fixture: OS privilege/support unavailable (" + e.GetType().Name + ")."); return false; }
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException) { return; }
        throw new Exception("Out-of-scope target was accepted.");
    }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Unsafe operation was accepted; expected " + typeof(T).Name); }
    private static void Check(bool value) { if (!value) throw new Exception("Assertion failed."); }
}
