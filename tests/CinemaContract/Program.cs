using System.Globalization;
using Newtonsoft.Json.Linq;
using PeakReplayLab.Cinema;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var output = new List<string>(); int passed = 0, failed = 0, assertions = 0;
string root = Directory.GetCurrentDirectory();
while (!Directory.Exists(Path.Combine(root, "modules", "PeakReplayCinema", "Core")))
    root = Directory.GetParent(root)?.FullName ?? throw new InvalidOperationException("Run inside the repository.");
string checks = Path.Combine(root, "local", "checks"); Directory.CreateDirectory(checks);
string fixtures = Path.Combine(checks, "cinema-fixtures-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixtures);

void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
void Near(double actual, double expected, string message, double tolerance = 0.0001) => Check(Math.Abs(actual - expected) <= tolerance, message + $" ({actual} != {expected})");
void Throws<T>(Action action, string message) where T : Exception
{
    try { action(); } catch (T) { assertions++; return; }
    throw new Exception(message);
}
CinemaPose Pose(float x = 0, float y = 0, float z = 0, float fov = 60, float focus = 10, CinemaQuaternion? rotation = null) =>
    new(new CinemaVector3(x, y, z), rotation ?? CinemaQuaternion.Identity, fov, focus);
CinemaKeyframe Key(double time, float x = 0, string segment = "A", CinemaTransition transition = CinemaTransition.Linear) => new(time, segment, Pose(x), transition);
CinemaProject Project() { var p = new CinemaProject("recording-sha256-example"); p.Track.Upsert(new CinemaKeyframe(0, "A", Pose(1, 2, 3, 47, 8))); return p; }
void Run(string name, Action test)
{
    try { test(); passed++; output.Add("PASS " + name); } catch (Exception e) { failed++; output.Add("FAIL " + name + ": " + e); }
}
void SampleEquals(CinemaPose a, CinemaPose b, string message)
{
    Near(a.Position.X, b.Position.X, message + " x"); Near(a.Position.Y, b.Position.Y, message + " y"); Near(a.Position.Z, b.Position.Z, message + " z");
    Near(a.Rotation.X, b.Rotation.X, message + " qx"); Near(a.Rotation.Y, b.Rotation.Y, message + " qy");
    Near(a.Rotation.Z, b.Rotation.Z, message + " qz"); Near(a.Rotation.W, b.Rotation.W, message + " qw");
    Near(a.FieldOfView, b.FieldOfView, message + " fov"); Near(a.FocusDistance, b.FocusDistance, message + " focus");
}

try
{
    Run("absolute replay time is independent of seek/pause call order", () =>
    {
        var track = new CinemaTrack(new List<CinemaKeyframe> { Key(10, 10), Key(0), Key(20, 20) });
        var expected = new Dictionary<double, CinemaPose>();
        foreach (double time in new[] { 0d, 2.5, 5, 10, 17.5, 20 }) { Check(track.TrySample(time, "A", out var p), "sample exists"); expected[time] = p; }
        foreach (double time in new[] { 17.5, 0, 10, 2.5, 20, 5, 5, 5, 2.5 }) { track.TrySample(time, "A", out var p); SampleEquals(p, expected[time], "seek deterministic"); }
        track.TrySample(2.5, "A", out var linear); Near(linear.Position.X, 2.5, "linear time");
    });
    Run("smooth, cut, exact key, and endpoint clamping", () =>
    {
        var track = new CinemaTrack(new List<CinemaKeyframe> { Key(2, 0, transition: CinemaTransition.Smooth), Key(10, 8) });
        track.TrySample(4, out var smooth); Near(smooth.Position.X, 1.25, "smoothstep at quarter");
        track.TrySample(-100, out var before); Near(before.Position.X, 0, "clamp before start");
        track.TrySample(100, out var after); Near(after.Position.X, 8, "clamp after end");
        track.Upsert(Key(2, 0, transition: CinemaTransition.Cut)); track.TrySample(9.99, out var hold); Near(hold.Position.X, 0, "cut holds");
        track.TrySample(10, out var exact); Near(exact.Position.X, 8, "cut arrives exactly");
        Check(!track.TrySample(double.NaN, out _), "NaN sampling rejected"); Check(!track.TrySample(double.PositiveInfinity, out _), "infinite sampling rejected");
        Check(!new CinemaTrack().TrySample(0, out _), "empty track has no pose");
    });
    Run("segment changes cannot interpolate or reuse another segment", () =>
    {
        var track = new CinemaTrack(new List<CinemaKeyframe> { Key(0, 0, "A"), Key(10, 100, "B"), Key(20, 200, "B"), Key(30, 300, "A") });
        Check(track.TrySample(5, "A", out var p), "A exists"); Near(p.Position.X, 0, "A holds across boundary");
        Check(!track.TrySample(5, "B", out _), "future B cannot override A");
        Check(track.TrySample(10, "B", out p), "B begins exactly"); Near(p.Position.X, 100, "B exact");
        Check(!track.TrySample(10, "A", out _), "old segment cannot leak");
        track.TrySample(15, "B", out p); Near(p.Position.X, 150, "within B interpolation");
        track.TrySample(25, "B", out p); Near(p.Position.X, 200, "B holds until A cut");
        Check(!track.TrySample(25, "A", out _), "repeated A cannot bridge B");
    });
    Run("quaternions normalize and take the shortest path", () =>
    {
        var q = new CinemaQuaternion(0, 2, 0, 2); Near(q.Y, Math.Sqrt(0.5), "normalize");
        var negative = new CinemaQuaternion(-q.X, -q.Y, -q.Z, -q.W);
        var track = new CinemaTrack(new List<CinemaKeyframe> { new(0, "A", Pose(rotation: q)), new(10, "A", Pose(rotation: negative)) });
        track.TrySample(5, out var p); Near(Math.Abs(p.Rotation.Y), Math.Sqrt(0.5), "no full spin"); Near(Math.Abs(p.Rotation.W), Math.Sqrt(0.5), "shortest rotation");
        Throws<ArgumentException>(() => new CinemaQuaternion(0, 0, 0, 0), "zero rotation rejected");
    });
    Run("add overwrite delete and transactionally enforce key limits", () =>
    {
        var track = new CinemaTrack();
        for (int i = 0; i < CinemaLimits.MaxKeyframes; i++) track.Upsert(Key(i, i));
        Check(track.Count == CinemaLimits.MaxKeyframes, "limit accepted");
        Check(ReferenceEquals(track.Keyframes, track.Keyframes), "read-only view cached");
        track.Upsert(Key(0, 42)); Check(track.Count == CinemaLimits.MaxKeyframes, "overwrite at limit allowed");
        Throws<InvalidOperationException>(() => track.Upsert(Key(CinemaLimits.MaxKeyframes, 1)), "excess insertion rejected");
        Throws<InvalidOperationException>(() => track.UpsertRange(new[] { Key(0, 99), Key(CinemaLimits.MaxKeyframes, 1) }), "preset atomic limit");
        Near(track.Keyframes[0].Pose.Position.X, 42, "failed batch leaves old values");
        Check(track.RemoveAt(0), "delete existing"); Check(!track.RemoveAt(0), "delete absent");
        track.Upsert(Key(CinemaLimits.MaxKeyframes, 1)); Check(track.Count == CinemaLimits.MaxKeyframes, "deleted capacity reusable");
        Throws<ArgumentException>(() => new CinemaTrack(new List<CinemaKeyframe> { Key(0), Key(0) }), "duplicate constructor times rejected");
        Throws<ArgumentException>(() => new CinemaTrack(Enumerable.Range(0, CinemaLimits.MaxKeyframes + 1).Select(i => Key(i)).ToList()), "oversize constructor rejected");
        track.Clear(); Check(track.Count == 0, "clear");
    });
    Run("model rejects nonfinite and out of range data", () =>
    {
        Throws<ArgumentOutOfRangeException>(() => new CinemaVector3(float.NaN, 0, 0), "NaN position");
        Throws<ArgumentOutOfRangeException>(() => new CinemaVector3(float.PositiveInfinity, 0, 0), "infinite position");
        Throws<ArgumentOutOfRangeException>(() => Pose(fov: 0), "FOV bounded");
        Throws<ArgumentOutOfRangeException>(() => Pose(focus: 0), "focus bounded");
        Throws<ArgumentOutOfRangeException>(() => Key(-1), "negative key time");
        Throws<ArgumentOutOfRangeException>(() => Key(double.NaN), "nonfinite key time");
        Throws<ArgumentOutOfRangeException>(() => Key(CinemaLimits.MaxTime + 1), "time bounded");
        Throws<ArgumentException>(() => Key(0, segment: ""), "segment required");
        Throws<ArgumentException>(() => new CinemaKeyframe(0, "A", default), "default pose invalid");
        Throws<ArgumentOutOfRangeException>(() => Key(0, transition: (CinemaTransition)999), "undefined transition");
    });
    Run("four presets preserve start and have bounded ordered paths", () =>
    {
        var start = Pose(); var center = new CinemaVector3(0, 0, 10);
        foreach (CinemaPreset kind in Enum.GetValues<CinemaPreset>())
        {
            var keys = CinemaPresets.Create(kind, 10, 18, "A", start, center, kind == CinemaPreset.Orbit ? 180 : 4);
            Check(keys.Count >= 2 && keys.Count <= 73, "bounded generated keys"); SampleEquals(keys[0].Pose, start, "preset exact start");
            Near(keys[0].Time, 10, "preset start time"); Near(keys[^1].Time, 28, "preset end time");
            for (int i = 1; i < keys.Count; i++) { Check(keys[i].Time > keys[i - 1].Time, "ordered preset times"); keys[i].Pose.Validate(); }
            var end = keys[^1].Pose;
            if (kind == CinemaPreset.PushPull) Near(end.Position.Z, 4, "push endpoint");
            if (kind == CinemaPreset.Slide) Near(end.Position.X, 4, "slide endpoint");
            if (kind == CinemaPreset.Rise) { Near(end.Position.Y, 4, "rise endpoint"); Near(end.Position.Z, -1.4, "rise reveals"); }
            if (kind == CinemaPreset.Orbit)
            {
                Near(end.Position.Z, 20, "orbit endpoint"); Near(Math.Abs(end.Rotation.Y), 1, "orbit faces subject");
                foreach (var key in keys) Near(Math.Sqrt(key.Pose.Position.X * key.Pose.Position.X + Math.Pow(key.Pose.Position.Z - 10, 2)), 10, "orbit radius");
            }
        }
        var pull = CinemaPresets.Create(CinemaPreset.PushPull, 0, 1, "A", start, null, -5); Near(pull[^1].Pose.Position.Z, -5, "pull direction");
        foreach (float degrees in new[] { 180f, 360f, -360f })
        {
            var orbit = CinemaPresets.Create(CinemaPreset.Orbit, 0, 20, "A", start, center, degrees);
            foreach (var key in orbit)
            {
                var q = key.Pose.Rotation; var p = key.Pose.Position;
                double fx = 2 * (q.X * q.Z + q.W * q.Y), fy = 2 * (q.Y * q.Z - q.W * q.X), fz = 1 - 2 * (q.X * q.X + q.Y * q.Y);
                double dx = center.X - p.X, dy = center.Y - p.Y, dz = center.Z - p.Z;
                Near((fx * dx + fy * dy + fz * dz) / Math.Sqrt(dx * dx + dy * dy + dz * dz), 1, "orbit keeps subject centered through full rotation");
            }
        }
        var clamp = CinemaPresets.Create(CinemaPreset.PushPull, 0, 1, "A", start, center, 100); Near(clamp[^1].Pose.Position.Z, 9.75, "push cannot pass through subject");
        Throws<ArgumentException>(() => CinemaPresets.Create(CinemaPreset.Orbit, 0, 1, "A", start, start.Position, 90), "orbit zero radius");
        Throws<ArgumentOutOfRangeException>(() => CinemaPresets.Create(CinemaPreset.Slide, 0, 0, "A", start), "zero duration");
        Throws<ArgumentOutOfRangeException>(() => CinemaPresets.Create(CinemaPreset.Slide, CinemaLimits.MaxTime, 1, "A", start), "end time overflow");
    });
    Run("sidecar roundtrip keeps settings, binding, and primitive JSON", () =>
    {
        var project = Project();
        project.Settings = new CinemaSettings { ColorPreset = CinemaColorPreset.Cool, ColorStrength = 0.8f, Exposure = -1,
            Contrast = 1.1f, Saturation = 0.7f, Temperature = -0.3f, DofMode = CinemaDofMode.Actor, Blur = 0.3f,
            Quality = CinemaDofQuality.High, FocusActorId = "actor-2", FrameAspect = CinemaFrameAspect.Cinema21x9, Grid = true, HideHud = false, HideNames = false };
        string path = Path.Combine(fixtures, "run.peak.camera.json"); CinemaProjectStore.SaveAtomic(path, project);
        var loaded = CinemaProjectStore.Load(path, project.Fingerprint);
        Check(loaded.Track.Count == 1, "one key roundtrip"); SampleEquals(loaded.Track.Keyframes[0].Pose, project.Track.Keyframes[0].Pose, "pose roundtrip");
        var s = loaded.Settings; Check(s.ColorPreset == CinemaColorPreset.Cool && s.DofMode == CinemaDofMode.Actor && s.Quality == CinemaDofQuality.High, "enum settings");
        Check(s.FrameAspect == CinemaFrameAspect.Cinema21x9 && s.Grid && !s.HideHud && !s.HideNames && s.FocusActorId == "actor-2", "framing settings");
        Near(s.ColorStrength, 0.8, "strength"); Near(s.Exposure, -1, "exposure"); Near(s.Contrast, 1.1, "contrast");
        Near(s.Saturation, 0.7, "saturation"); Near(s.Temperature, -0.3, "temperature"); Near(s.Blur, 0.3, "blur");
        string json = File.ReadAllText(path); Check(!json.Contains("length", StringComparison.OrdinalIgnoreCase) && !json.Contains("Keyframes", StringComparison.Ordinal), "no derived fields");
        Throws<InvalidDataException>(() => CinemaProjectStore.Load(path, "different-recording"), "fingerprint mismatch rejected");
        project.Track.Upsert(Key(1, 50)); CinemaProjectStore.SaveAtomic(path, project);
        Check(CinemaProjectStore.Load(path, project.Fingerprint).Track.Count == 2, "atomic replacement roundtrip");
        Check(!Directory.GetFiles(fixtures, "*.tmp").Any(), "no abandoned temporary files");
    });
    Run("bad JSON schema, nonfinite, duplicate, and oversized files are rejected", () =>
    {
        string goodPath = Path.Combine(fixtures, "valid.camera.json"); var project = Project(); CinemaProjectStore.SaveAtomic(goodPath, project);
        var good = JObject.Parse(File.ReadAllText(goodPath)); string badPath = Path.Combine(fixtures, "bad.camera.json");
        void Reject(Action<JObject> mutate, string message)
        {
            var bad = (JObject)good.DeepClone(); mutate(bad); File.WriteAllText(badPath, bad.ToString());
            Throws<InvalidDataException>(() => CinemaProjectStore.Load(badPath, project.Fingerprint), message);
        }
        Reject(j => j["version"] = 2, "unknown version");
        Reject(j => j["version"] = null, "null version");
        Reject(j => j.Remove("version"), "missing version");
        Reject(j => j["unexpected"] = true, "unknown member");
        Reject(j => j["track"]!["keyframes"]!.Value<JArray>()!.Add(j["track"]!["keyframes"]![0]!.DeepClone()), "duplicate key time");
        Reject(j => j["track"]!["keyframes"]![0]!["pose"]!["fov"] = double.NaN, "NaN fov");
        Reject(j => j["track"]!["keyframes"]![0]!["pose"]!["rotation"] = JObject.Parse("{\"x\":0,\"y\":0,\"z\":0,\"w\":0}"), "zero quaternion");
        Reject(j => ((JObject)j["track"]!["keyframes"]![0]!["pose"]!).Remove("fov"), "missing fov");
        Reject(j => j["settings"]!["blur"] = 2, "settings out of range");
        Reject(j => j["settings"]!["dofMode"] = 999, "numeric enum rejected");
        Reject(j => j["settings"]!["frameAspect"] = "Bogus", "unknown enum rejected");
        Reject(j => { var array = (JArray)j["track"]!["keyframes"]!; var template = array[0]!; array.Clear();
            for (int i = 0; i <= CinemaLimits.MaxKeyframes; i++) { var key = template.DeepClone(); key["time"] = i; array.Add(key); } }, "key count rejected on load");
        File.WriteAllText(badPath, good + "{}"); Throws<InvalidDataException>(() => CinemaProjectStore.Load(badPath, project.Fingerprint), "trailing object");
        File.WriteAllText(badPath, "{\"version\":1,\"version\":1}"); Throws<InvalidDataException>(() => CinemaProjectStore.Load(badPath, project.Fingerprint), "duplicate JSON property");
        File.WriteAllBytes(badPath, new byte[CinemaLimits.MaxFileBytes + 1]); Throws<InvalidDataException>(() => CinemaProjectStore.Load(badPath, project.Fingerprint), "file size rejected");
    });
    Run("invalid save preserves previous project and never accepts original replay paths", () =>
    {
        var project = Project(); string path = Path.Combine(fixtures, "preserve.camera.json"); CinemaProjectStore.SaveAtomic(path, project);
        byte[] original = File.ReadAllBytes(path); project.Settings.Blur = float.NaN;
        Throws<ArgumentOutOfRangeException>(() => CinemaProjectStore.SaveAtomic(path, project), "invalid save refused");
        Check(original.SequenceEqual(File.ReadAllBytes(path)), "old sidecar preserved");
        string replay = Path.Combine(fixtures, "run.peak"); File.WriteAllText(replay, "original recording bytes");
        Throws<ArgumentException>(() => CinemaProjectStore.SaveAtomic(replay, Project()), "recording path cannot be saved");
        Throws<ArgumentException>(() => CinemaProjectStore.Load(replay, project.Fingerprint), "recording path cannot be loaded");
        Check(File.ReadAllText(replay) == "original recording bytes", "recording preserved");
        Check(CinemaProjectStore.GetSidecarPath(replay) == replay + ".camera.json", "sidecar suffix appended");
    });
    Run("catalog only lists bounded named regular plans for the exact recording", () =>
    {
        string directory = Path.Combine(fixtures, "catalog"); Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "run.peakrun"); File.WriteAllText(source, "recording");
        var project = Project();
        foreach (string name in new[] { "z-last", "A-first", "中文方案" }) CinemaProjectStore.SaveAtomic(CinemaRecordingIdentity.Sidecar(source, name), project);
        CinemaProjectFiles.SaveRecovery(source, "z-last", project);
        File.WriteAllText(source + ".camera.json", "legacy unnamed sidecar");
        File.WriteAllText(source + ".bad..name.camera.json", "invalid name");
        File.WriteAllText(source + ".reserved.recovery.camera.json", "reserved recovery");
        File.WriteAllText(source + "other.camera.json", "another recording");
        Directory.CreateDirectory(source + ".directory.camera.json");
        Check(CinemaProjectFiles.List(source).SequenceEqual(new[] { "A-first", "z-last", "中文方案" }), "catalog matches named regular files only");
        for (int i = 0; i < 270; i++) File.WriteAllText(CinemaRecordingIdentity.Sidecar(source, "bounded-" + i.ToString("D3")), "{}");
        Check(CinemaProjectFiles.List(source).Length == 256, "catalog name count capped");
        Check(File.ReadAllText(source) == "recording", "listing preserves recording");
    });
    Run("copy and rename validate binding and refuse every target collision", () =>
    {
        string source = Path.Combine(fixtures, "copy.peakrun"); File.WriteAllText(source, "recording");
        var project = Project(); project.Settings.ColorPreset = CinemaColorPreset.Warm;
        string initial = CinemaRecordingIdentity.Sidecar(source, "original"); CinemaProjectStore.SaveAtomic(initial, project);
        CinemaProjectFiles.Copy(source, "original", "copied", project.Fingerprint);
        string copied = CinemaRecordingIdentity.Sidecar(source, "copied");
        var loaded = CinemaProjectStore.Load(copied, project.Fingerprint);
        Check(loaded.Track.Count == 1 && loaded.Settings.ColorPreset == CinemaColorPreset.Warm && File.Exists(initial), "copy preserves source and complete project");
        byte[] bytes = File.ReadAllBytes(copied);
        Throws<IOException>(() => CinemaProjectFiles.Copy(source, "original", "copied", project.Fingerprint), "copy collision rejected");
        Throws<IOException>(() => CinemaProjectFiles.Rename(source, "original", "copied", project.Fingerprint), "rename collision rejected");
        Check(bytes.SequenceEqual(File.ReadAllBytes(copied)) && File.Exists(initial), "collision preserves both projects");
        Throws<InvalidDataException>(() => CinemaProjectFiles.Copy(source, "original", "bad-copy", "wrong-recording"), "copy verifies fingerprint");
        Throws<InvalidDataException>(() => CinemaProjectFiles.Rename(source, "original", "bad-rename", "wrong-recording"), "rename verifies fingerprint");
        Check(!File.Exists(CinemaRecordingIdentity.Sidecar(source, "bad-copy")) && !File.Exists(CinemaRecordingIdentity.Sidecar(source, "bad-rename")), "failed validation writes no destination");
        Throws<IOException>(() => CinemaProjectFiles.Rename(source, "original", "ORIGINAL", project.Fingerprint), "case-only rename refused");
        CinemaProjectFiles.Rename(source, "copied", "renamed", project.Fingerprint);
        string renamed = CinemaRecordingIdentity.Sidecar(source, "renamed");
        Check(!File.Exists(copied) && bytes.SequenceEqual(File.ReadAllBytes(renamed)), "rename moves exact bytes");
        Directory.CreateDirectory(CinemaRecordingIdentity.Sidecar(source, "directory"));
        Throws<IOException>(() => CinemaProjectFiles.Copy(source, "original", "directory", project.Fingerprint), "directory target refused");
        Throws<ArgumentException>(() => CinemaProjectFiles.Copy(source, "original", "../escape", project.Fingerprint), "traversal refused");
        Throws<ArgumentException>(() => CinemaProjectFiles.Copy(source, "original", "reserved.recovery", project.Fingerprint), "recovery namespace reserved");
        Check(File.ReadAllText(source) == "recording", "management preserves recording");
    });
    Run("create-only atomic saves retain existing targets and remove temporary files", () =>
    {
        string path = Path.Combine(fixtures, "new-only.camera.json"); var project = Project();
        CinemaProjectStore.SaveNewAtomic(path, project); byte[] original = File.ReadAllBytes(path);
        project.Track.Upsert(Key(1, 99));
        Throws<IOException>(() => CinemaProjectStore.SaveNewAtomic(path, project), "create-only collision rejected");
        Check(original.SequenceEqual(File.ReadAllBytes(path)), "create-only collision cannot overwrite");
        Check(!Directory.GetFiles(fixtures, ".cinema-*.tmp").Any(), "failed create-only commit cleans temporary");
        string blocked = Path.Combine(fixtures, "directory-blocked.camera.json"); Directory.CreateDirectory(blocked);
        Throws<IOException>(() => CinemaProjectStore.SaveNewAtomic(blocked, project), "directory collision rejected");
        Check(Directory.Exists(blocked) && !Directory.GetFiles(fixtures, ".cinema-*.tmp").Any(), "directory preserved and temporary cleaned");
    });
    Run("recovery is independently atomic and cannot overwrite ordinary plans", () =>
    {
        string source = Path.Combine(fixtures, "recovery.peakrun"); File.WriteAllText(source, "recording");
        var project = Project(); string normal = CinemaRecordingIdentity.Sidecar(source, "plan");
        CinemaProjectStore.SaveAtomic(normal, project); byte[] normalBytes = File.ReadAllBytes(normal);
        project.Track.Upsert(Key(3, 33)); CinemaProjectFiles.SaveRecovery(source, "plan", project);
        string recovery = CinemaProjectFiles.RecoveryPath(source, "plan");
        Check(recovery == normal + ".recovery.camera.json", "recovery uses exact distinct path");
        Check(CinemaProjectFiles.LoadRecovery(source, "plan", project.Fingerprint).Track.Count == 2, "recovery stores current project");
        Check(CinemaProjectStore.Load(normal, project.Fingerprint).Track.Count == 1 && normalBytes.SequenceEqual(File.ReadAllBytes(normal)), "recovery preserves ordinary saved plan");
        Check(CinemaProjectFiles.List(source).SequenceEqual(new[] { "plan" }), "recovery excluded from plan catalog");
        Check(CinemaProjectFiles.ListRecoveries(source).SequenceEqual(new[] { "plan" }), "recovery catalog discovers saved plan drafts");
        CinemaProjectFiles.SaveRecovery(source, "never-saved", project);
        Check(CinemaProjectFiles.ListRecoveries(source).SequenceEqual(new[] { "never-saved", "plan" }), "unsaved new plan draft is discoverable after reopen");
        Check(CinemaProjectFiles.List(source).SequenceEqual(new[] { "plan" }), "recovery-only plan cannot masquerade as a saved plan");
        CinemaProjectFiles.RemoveRecovery(source, "never-saved");
        Throws<InvalidDataException>(() => CinemaProjectFiles.LoadRecovery(source, "plan", "different-recording"), "recovery validates fingerprint");
        byte[] goodRecovery = File.ReadAllBytes(recovery); project.Settings.Blur = float.NaN;
        Throws<ArgumentOutOfRangeException>(() => CinemaProjectFiles.SaveRecovery(source, "plan", project), "invalid recovery refused");
        Check(goodRecovery.SequenceEqual(File.ReadAllBytes(recovery)), "failed recovery save keeps previous recovery");
        CinemaProjectFiles.RemoveRecovery(source, "plan"); CinemaProjectFiles.RemoveRecovery(source, "plan");
        Check(!File.Exists(recovery) && normalBytes.SequenceEqual(File.ReadAllBytes(normal)), "remove is idempotent and deletes only recovery");
    });
    Run("catalog and recovery refuse symbolic-link files and containing directories", () =>
    {
        string directory = Path.Combine(fixtures, "links"); Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "run.peakrun"); File.WriteAllText(source, "recording");
        var project = Project(); string target = CinemaRecordingIdentity.Sidecar(source, "real"); CinemaProjectStore.SaveAtomic(target, project);
        string alias = CinemaRecordingIdentity.Sidecar(source, "linked");
        bool linksAvailable = true;
        try { File.CreateSymbolicLink(alias, target); }
        catch (Exception error) when (error is UnauthorizedAccessException || error is PlatformNotSupportedException ||
                                      error is IOException io && (io.HResult & 0xffff) == 1314)
        { linksAvailable = false; output.Add("SKIP symbolic-link fixtures: " + error.GetType().Name); }
        if (!linksAvailable) return;
        string recovery = CinemaProjectFiles.RecoveryPath(source, "unsafe");
        string linkedDirectory = Path.Combine(fixtures, "linked-directory");
        try
        {
            Check(CinemaProjectFiles.List(source).SequenceEqual(new[] { "real" }), "catalog excludes link");
            Throws<IOException>(() => CinemaProjectFiles.Copy(source, "linked", "copy", project.Fingerprint), "link source refused");
            Throws<IOException>(() => CinemaProjectFiles.Rename(source, "real", "linked", project.Fingerprint), "link target refused");
            File.CreateSymbolicLink(recovery, target);
            Throws<IOException>(() => CinemaProjectFiles.SaveRecovery(source, "unsafe", project), "recovery link write refused");
            Throws<IOException>(() => CinemaProjectFiles.LoadRecovery(source, "unsafe", project.Fingerprint), "recovery link read refused");
            Throws<IOException>(() => CinemaProjectFiles.RemoveRecovery(source, "unsafe"), "recovery link delete refused");
            Check(CinemaProjectStore.Load(target, project.Fingerprint).Track.Count == 1 && File.Exists(recovery), "linked target and alias untouched");
            Directory.CreateSymbolicLink(linkedDirectory, directory);
            Throws<IOException>(() => CinemaProjectFiles.List(Path.Combine(linkedDirectory, "run.peakrun")), "directory link refused");
        }
        finally
        {
            File.Delete(alias); if (File.Exists(recovery)) File.Delete(recovery);
            if (Directory.Exists(linkedDirectory)) Directory.Delete(linkedDirectory);
        }
    });
}
finally
{
    Directory.Delete(fixtures, true);
    output.Add($"CinemaContract: {passed} passed, {failed} failed, {assertions} assertions.");
    File.WriteAllLines(Path.Combine(checks, "cinema-core.log"), output);
    foreach (string line in output) Console.WriteLine(line);
}
return failed == 0 ? 0 : 1;
