using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

int passed = 0, skipped = 0;
void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
async Task Test(string name, Func<Fixture, Task> body)
{
    using var fixture = new Fixture();
    try { await body(fixture); passed++; Console.WriteLine("PASS " + name); }
    catch (SkippedException error) { skipped++; Console.WriteLine("SKIP " + name + ": " + error.Message); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); Environment.Exit(1); }
}
async Task Reject<T>(Func<Task> body) where T : Exception
{ try { await body(); } catch (T) { return; } throw new Exception("Expected rejection " + typeof(T).Name); }

await Test("fully acknowledged unchanged recording skips export and HTTP even after disposable exports are deleted", async f =>
{
    var cache = f.Open(); var exported = f.Export(); cache.RememberExport(exported);
    var handler = new Handler((call, _, _) => Task.FromResult(Fixture.Response(call)));
    var receipt = await f.Send(cache, exported, handler); cache.MarkCompleted(receipt);
    foreach (string path in exported.Paths) File.Delete(path);
    int exports = 0;
    var reopened = f.Open();
    var known = reopened.CompletedReceipt();
    if (known == null) { exports++; await f.Send(reopened, f.Export(), handler); }
    Check(known?.Cached == true && known.Duplicate && exports == 0 && handler.Calls == 1, "completed hit did work again");
    Check(reopened.RestoreExport()?.PointCount == exported.PointCount, "lost receipt UI metadata");
});

await Test("partial failure persists first acknowledgement and restart sends only the missing player part", async f =>
{
    var cache = f.Open(); var parts = f.SplitExport(); Check(parts.Paths.Length == 2, "fixture did not split"); cache.RememberExport(parts);
    var failing = new Handler((call, _, _) => Task.FromResult(Fixture.Response(call, call == 2 ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.Created)));
    await Reject<InvalidDataException>(() => f.Send(cache, parts, failing));
    Check(cache.CompletedReceipt() == null && cache.ConfirmedParts == 1, "partial success became complete");
    var resumed = f.Open(); var retained = resumed.RestoreExport();
    Check(retained != null && resumed.ConfirmedParts == 1, "partial export/ack not restored");
    var remaining = new Handler((_, _, _) => Task.FromResult(Fixture.Response(2)));
    var receipt = await f.Send(resumed, retained!, remaining); resumed.MarkCompleted(receipt);
    Check(remaining.Calls == 1 && receipt.UploadIds.SequenceEqual(new[] { Fixture.Id(1), Fixture.Id(2) }), "acknowledged part resent or order changed");
    Check(f.Open().CompletedReceipt() != null, "completed batch not persisted");
});

await Test("cancel between parts retains acknowledgement but never marks the batch completed", async f =>
{
    var cache = f.Open(); var parts = f.SplitExport(); cache.RememberExport(parts);
    using var cancellation = new CancellationTokenSource();
    var handler = new Handler((call, _, _) => Task.FromResult(Fixture.Response(call)));
    await Reject<OperationCanceledException>(() => f.Send(cache, parts, handler, cancellation.Token,
        (done, _) => { if (done == 1) cancellation.Cancel(); }));
    Check(handler.Calls == 1 && f.Open().ConfirmedParts == 1 && f.Open().CompletedReceipt() == null, "cancellation persisted incorrect completion");
});

await Test("target normalization shares only the same complete endpoint and protocol version", async f =>
{
    var cache = f.Open(); var exported = f.Export(); cache.RememberExport(exported);
    var receipt = await f.Send(cache, exported, new Handler((call, _, _) => Task.FromResult(Fixture.Response(call)))); cache.MarkCompleted(receipt);
    Check(f.Open(new Uri("https://PEAK.EXAMPLE:443/api/route-uploads")).CompletedReceipt() != null, "normalized identical endpoint missed");
    foreach (var endpoint in new[] { "https://other.example/api/route-uploads", "https://peak.example:444/api/route-uploads", "https://peak.example/prefix/api/route-uploads", "http://localhost/api/route-uploads" })
        Check(f.Open(new Uri(endpoint)).CompletedReceipt() == null, "different endpoint inherited acknowledgement");
    Check(f.Open(version: "trajectory-v1/export-1").CompletedReceipt() == null, "old protocol inherited acknowledgement");
    await Reject<ArgumentException>(() => Task.FromResult(f.Open(new Uri("https://peak.example/api/route-uploads?token=private"))));
});

await Test("source changes, copies and moves cannot inherit a prior completion", async f =>
{
    var cache = f.Open(); var exported = f.Export(); cache.RememberExport(exported);
    var receipt = await f.Send(cache, exported, new Handler((call, _, _) => Task.FromResult(Fixture.Response(call)))); cache.MarkCompleted(receipt);
    File.SetLastWriteTimeUtc(f.Recording, File.GetLastWriteTimeUtc(f.Recording).AddSeconds(5));
    Check(f.Open().CompletedReceipt() == null, "mtime change inherited completion");
    string moved = Path.Combine(f.Root, "moved.peakrun"); File.Copy(f.Recording, moved);
    Check(TrajectoryUploadCache.Open(f.Output, moved, f.Endpoint, Fixture.Version).CompletedReceipt() == null, "copied path inherited completion");
    using (var file = new FileStream(f.Recording, FileMode.Append)) file.WriteByte(1);
    await Reject<InvalidDataException>(() => Task.FromResult(f.Open()));
});

await Test("unfinished and faulted recordings are never eligible for cached completion", async f =>
{
    f.RewriteIndex(index => index["Complete"] = false);
    await Reject<InvalidDataException>(() => Task.FromResult(f.Open()));
    f.RewriteIndex(index => { index["Complete"] = true; index["FaultCode"] = "test-fault"; });
    await Reject<InvalidDataException>(() => Task.FromResult(f.Open()));
    File.Move(f.Recording, f.Recording + ".partial");
    await Reject<InvalidDataException>(() => Task.FromResult(TrajectoryUploadCache.Open(f.Output, f.Recording + ".partial", f.Endpoint, Fixture.Version)));
});

await Test("header/index fingerprint does not decode replay page data", f =>
{
    f.MakeMetadataOnlyBody();
    var cache = f.Open();
    Check(cache.SourceIdentity.Length == 64 && cache.CompletedReceipt() == null, "bounded metadata identity failed");
    var info = FullReplayArchive.ReadInfo(f.Recording);
    try { FullReplayArchive.ReadPage(f.Recording, info, 0); throw new Exception("garbage body should not decode"); }
    catch (InvalidDataException) { }
    return Task.CompletedTask;
});

await Test("corrupt or unsigned cache receipts and legacy receipt notes never count as success", async f =>
{
    var cache = f.Open(); var exported = f.Export(); cache.RememberExport(exported);
    string state = f.StatePath();
    File.WriteAllText(state, "{\"Completed\":true,\"UploadId\":\"" + Fixture.Id(1) + "\"}");
    File.WriteAllText(exported.Path + ".receipt.json", "{\"UploadId\":\"" + Fixture.Id(1) + "\"}");
    Check(f.Open().CompletedReceipt() == null, "unsigned/legacy note was trusted");
    cache.RememberExport(exported);
    var envelope = JObject.Parse(File.ReadAllText(state));
    byte[] payload = Convert.FromBase64String(envelope.Value<string>("Payload")!); payload[0] ^= 1;
    envelope["Payload"] = Convert.ToBase64String(payload); File.WriteAllText(state, envelope.ToString());
    Check(f.Open().CompletedReceipt() == null && f.Open().RestoreExport() == null, "HMAC mismatch was trusted");
    await Task.CompletedTask;
});

await Test("signed unsafe package paths are rejected without touching external files", async f =>
{
    var cache = f.Open(); var exported = f.Export(); cache.RememberExport(exported);
    string victim = Path.Combine(f.Root, "victim.txt"); File.WriteAllText(victim, "do not touch");
    f.RewriteSignedState(state => state["Parts"]![0]!["Name"] = "../../victim.txt");
    var reopened = f.Open();
    Check(reopened.CompletedReceipt() == null && reopened.RestoreExport() == null, "traversal receipt was accepted");
    Check(File.ReadAllText(victim) == "do not touch", "external file changed");
    await Reject<InvalidDataException>(() => Task.Run(() => reopened.RememberExport(new TrajectoryExportResult
        { RecordingId = exported.RecordingId, Paths = new[] { victim } })));
});

await Test("cached parts must retain their exact bytes before they can skip a request", async f =>
{
    var cache = f.Open(); var parts = f.SplitExport(); cache.RememberExport(parts);
    cache.RecordConfirmed(parts.Paths[0], Fixture.Receipt(1));
    var bytes = File.ReadAllBytes(parts.Paths[0]); bytes[^1] ^= 1; File.WriteAllBytes(parts.Paths[0], bytes);
    var reopened = f.Open();
    Check(reopened.RestoreExport() == null, "changed package inherited export");
    await Reject<InvalidDataException>(() => Task.FromResult(reopened.ConfirmedReceipt(parts.Paths[0])));
    await Reject<InvalidDataException>(() => Task.Run(() => reopened.RecordConfirmed(parts.Paths[0], Fixture.Receipt(1))));
    Check(reopened.CompletedReceipt() == null, "changed partial export became complete");
});

await Test("re-exported identical parts retain their confirmed content identity despite new filenames", async f =>
{
    var cache = f.Open(); var parts = f.SplitExport(); cache.RememberExport(parts);
    cache.RecordConfirmed(parts.Paths[0], Fixture.Receipt(1));
    var again = f.SplitExport(); cache.RememberExport(again);
    Check(again.Paths[0] != parts.Paths[0] && cache.ConfirmedReceipt(again.Paths[0]) != null, "equivalent part identity depended on GUID filename");
    var handler = new Handler((_, _, _) => Task.FromResult(Fixture.Response(2)));
    var receipt = await f.Send(cache, again, handler); cache.MarkCompleted(receipt);
    Check(handler.Calls == 1, "re-export resent already acknowledged content");
});

await Test("source change after export blocks completion even if the server accepted a part", async f =>
{
    var cache = f.Open(); var exported = f.Export(); cache.RememberExport(exported);
    var receipt = await f.Send(cache, exported, new Handler((call, _, _) => Task.FromResult(Fixture.Response(call))));
    File.SetLastWriteTimeUtc(f.Recording, File.GetLastWriteTimeUtc(f.Recording).AddSeconds(5));
    await Reject<InvalidDataException>(() => Task.Run(() => cache.MarkCompleted(receipt)));
    Check(f.Open().CompletedReceipt() == null, "changed source was marked complete");
});

await Test("completion requires all individual acknowledgements and matching ordered server IDs", async f =>
{
    var cache = f.Open(); var parts = f.SplitExport(); cache.RememberExport(parts);
    cache.RecordConfirmed(parts.Paths[0], Fixture.Receipt(1));
    await Reject<InvalidDataException>(() => Task.Run(() => cache.MarkCompleted(Fixture.Receipt(1))));
    var invalid = Fixture.Receipt(2); invalid.UploadId = "../../bad";
    await Reject<InvalidDataException>(() => Task.Run(() => cache.RecordConfirmed(parts.Paths[1], invalid)));
    cache.RecordConfirmed(parts.Paths[1], Fixture.Receipt(2));
    await Reject<InvalidDataException>(() => Task.Run(() => cache.MarkCompleted(Fixture.Receipt(1))));
    Check(f.Open().CompletedReceipt() == null, "incomplete/mismatched receipt accepted");
});

await Test("cache survives missing disposable exports but not missing or corrupt authentication key", async f =>
{
    var cache = f.Open(); var exported = f.Export(); cache.RememberExport(exported);
    var receipt = await f.Send(cache, exported, new Handler((call, _, _) => Task.FromResult(Fixture.Response(call)))); cache.MarkCompleted(receipt);
    string key = Path.Combine(f.Output, ".upload-cache", "authentication.key");
    File.WriteAllBytes(key, new byte[1]);
    var reopened = f.Open();
    Check(!reopened.Persistent && reopened.CompletedReceipt() == null, "invalid private key inherited success");
    File.Delete(key);
    Check(f.Open().CompletedReceipt() == null, "a replacement private key authenticated old receipts");
});

await Test("a completed session rechecks the source before returning its cached receipt", async f =>
{
    var cache = f.Open(); var exported = f.Export(); cache.RememberExport(exported);
    var receipt = await f.Send(cache, exported, new Handler((call, _, _) => Task.FromResult(Fixture.Response(call)))); cache.MarkCompleted(receipt);
    File.SetLastWriteTimeUtc(f.Recording, File.GetLastWriteTimeUtc(f.Recording).AddSeconds(5));
    await Reject<InvalidDataException>(() => Task.FromResult(cache.CompletedReceipt()));
});

await Test("cache state symlinks cannot overwrite external files", async f =>
{
    var cache = f.Open(); var exported = f.Export(); cache.RememberExport(exported);
    string state = f.StatePath(), victim = Path.Combine(f.Root, "victim.json");
    File.WriteAllText(victim, "keep external data"); File.Delete(state);
    try { File.CreateSymbolicLink(state, victim); }
    catch (UnauthorizedAccessException) { throw new SkippedException("Host cannot create file symlinks"); }
    catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 1314)
    { throw new SkippedException("Windows account lacks file-symlink privilege"); }
    cache.RecordConfirmed(exported.Path, Fixture.Receipt(1));
    Check(File.ReadAllText(victim) == "keep external data", "cache replaced external symlink target");
    Check(f.Open().CompletedReceipt() == null, "cache symlink inherited success");
    await Task.CompletedTask;
});

await Test("signed malformed acknowledgement data cannot produce completion", async f =>
{
    var cache = f.Open(); var exported = f.Export(); cache.RememberExport(exported);
    var receipt = await f.Send(cache, exported, new Handler((call, _, _) => Task.FromResult(Fixture.Response(call)))); cache.MarkCompleted(receipt);
    f.RewriteSignedState(state => state["Parts"]![0]!["Receipt"]!["UploadIds"] = new JArray(Fixture.Id(999)));
    Check(f.Open().CompletedReceipt() == null, "inconsistent per-part acknowledgement was trusted");
    cache.RememberExport(exported);
    f.RewriteSignedState(state => state["Parts"]![0] = JValue.CreateNull());
    Check(f.Open().RestoreExport() == null, "null cache part did not fail closed");
});

await Test("untrusted uploader lookup callbacks cannot bypass receipt validation", async f =>
{
    var exported = f.Export(); var malformed = Fixture.Receipt(1); malformed.UploadIds = new[] { Fixture.Id(2) };
    var handler = new Handler((call, _, _) => Task.FromResult(Fixture.Response(call)));
    await Reject<InvalidDataException>(() => TrajectoryUploader.UploadManyAsync(exported.Paths, f.Endpoint, testHandler: handler,
        confirmedReceipt: _ => malformed));
    Check(handler.Calls == 0, "malformed local acknowledgement started a request");
});

await Test("unconfirmed changed payload is rejected before the first HTTP request", async f =>
{
    var cache = f.Open(); var exported = f.Export(); cache.RememberExport(exported);
    byte[] changed = File.ReadAllBytes(exported.Path); changed[^1] ^= 1; File.WriteAllBytes(exported.Path, changed);
    var handler = new Handler((call, _, _) => Task.FromResult(Fixture.Response(call)));
    await Reject<InvalidDataException>(() => f.Send(cache, exported, handler));
    Check(handler.Calls == 0 && f.Open().CompletedReceipt() == null, "modified unconfirmed payload reached HTTP");
});

await Test("explicit reupload clears only this source endpoint version and reuses prepared export bytes", async f =>
{
    var exported = f.Export(); var cache = f.Open(); cache.RememberExport(exported);
    var receipt = await f.Send(cache, exported, new Handler((call, _, _) => Task.FromResult(Fixture.Response(call)))); cache.MarkCompleted(receipt);
    var otherEndpoint = f.Open(new Uri("https://other.example/api/route-uploads")); otherEndpoint.RememberExport(exported);
    otherEndpoint.RecordConfirmed(exported.Path, Fixture.Receipt(1)); otherEndpoint.MarkCompleted(Fixture.Receipt(1));
    var otherVersion = f.Open(version: "trajectory-v1/export-1"); otherVersion.RememberExport(exported);
    otherVersion.RecordConfirmed(exported.Path, Fixture.Receipt(1)); otherVersion.MarkCompleted(Fixture.Receipt(1));
    string copy = Path.Combine(f.Root, "different-recording.peakrun"); File.Copy(f.Recording, copy);
    var otherSource = TrajectoryUploadCache.Open(f.Output, copy, f.Endpoint, Fixture.Version); otherSource.RememberExport(exported);
    otherSource.RecordConfirmed(exported.Path, Fixture.Receipt(1)); otherSource.MarkCompleted(Fixture.Receipt(1));
    var force = f.Open(); force.ForgetAcknowledgements();
    var retained = force.RestoreExport();
    Check(force.CompletedReceipt() == null && force.ConfirmedParts == 0 && retained != null && retained.Paths.SequenceEqual(exported.Paths), "force lost export or retained acknowledgements");
    Check(f.Open().CompletedReceipt() == null, "forced completion was not cleared on disk");
    Check(f.Open(new Uri("https://other.example/api/route-uploads")).CompletedReceipt() != null
        && f.Open(version: "trajectory-v1/export-1").CompletedReceipt() != null
        && TrajectoryUploadCache.Open(f.Output, copy, f.Endpoint, Fixture.Version).CompletedReceipt() != null, "force affected an unrelated cache key");
    var handler = new Handler((call, _, _) => Task.FromResult(Fixture.Response(call)));
    var retried = await f.Send(force, retained!, handler); force.MarkCompleted(retried);
    Check(handler.Calls == 1 && f.Open().CompletedReceipt() != null, "explicit recovery did not resend or restore completion");
});

await Test("explicit reupload regenerates export only when retained files were removed", async f =>
{
    var cache = f.Open(); var exported = f.Export(); cache.RememberExport(exported);
    var receipt = await f.Send(cache, exported, new Handler((call, _, _) => Task.FromResult(Fixture.Response(call)))); cache.MarkCompleted(receipt);
    File.Delete(exported.Path);
    var force = f.Open(); force.ForgetAcknowledgements();
    Check(force.RestoreExport() == null && force.CompletedReceipt() == null, "missing export reused or completion retained");
    var regenerated = f.Export(); force.RememberExport(regenerated);
    var handler = new Handler((call, _, _) => Task.FromResult(Fixture.Response(call)));
    var retried = await f.Send(force, regenerated, handler); force.MarkCompleted(retried);
    Check(handler.Calls == 1 && regenerated.Path != exported.Path && f.Open().CompletedReceipt() != null, "missing export recovery failed");
});

Console.WriteLine($"{passed} trajectory cache contracts passed; {skipped} platform skips.");

sealed class SkippedException : Exception { public SkippedException(string message) : base(message) { } }

sealed class Handler : HttpMessageHandler
{
    private readonly Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
    public int Calls;
    public Handler(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => this.send = send;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(++Calls, request, token);
}

sealed class Fixture : IDisposable
{
    public const string Version = "trajectory-v2/export-1";
    public readonly string Root = Path.Combine(Path.GetTempPath(), "peak-trajectory-cache-" + Guid.NewGuid().ToString("N"));
    public readonly string Output, Recording;
    public readonly Uri Endpoint = new("https://peak.example/api/route-uploads");
    public Fixture()
    {
        Directory.CreateDirectory(Root); Output = Path.Combine(Root, "exports");
        var header = new ReplayHeader { Schema = 14, Scene = "Level_0", BuildId = 123, GameVersion = "fixture", GameAssembly = new string('a', 32),
            Route = "Shore,Roots", StartedUtc = "2026-10-15T00:00:00Z", SampleHz = 60 };
        var frames = Enumerable.Range(0, 121).Select(i => new ReplayFrame { T = i / 60d,
            Actors = new[] { Actor("first", i, true), Actor("second", i, false) } }).ToArray();
        Recording = LegacyReplayFixture.Save(Root, header, frames);
    }
    private static ActorFrame Actor(string id, int i, bool owner) => new()
    { Id = id, Name = "Synthetic " + id, Position = new[] { 0f, 0f, i / 10f }, RouteState = new ActorRouteState
        { SampleTime = i / 60d, Center = new[] { 0f, 0f, i / 10f }, Alive = true, LocalOwner = owner } };
    public TrajectoryUploadCacheSession Open(Uri? endpoint = null, string version = Version) => TrajectoryUploadCache.Open(Output, Recording, endpoint ?? Endpoint, version);
    public TrajectoryExportResult Export() => ReplayTrajectoryExporter.Export(Recording, Output);
    public TrajectoryExportResult SplitExport()
    {
        var full = Export(); JObject payload;
        using (var file = File.OpenRead(full.Path)) using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        using (var reader = new StreamReader(gzip)) payload = JObject.Parse(reader.ReadToEnd());
        var players = (JArray)payload["players"]!;
        long maximum = 0;
        foreach (var player in players) { var single = (JObject)payload.DeepClone(); single["players"] = new JArray(player.DeepClone()); maximum = Math.Max(maximum, Encoding.UTF8.GetByteCount(single.ToString(Formatting.None))); }
        return ReplayTrajectoryExporter.ExportWithSmallerLimits(Recording, Output, ReplayTrajectoryExporter.MaximumCompressedBytes, maximum + 64);
    }
    public Task<TrajectoryUploadReceipt> Send(TrajectoryUploadCacheSession cache, TrajectoryExportResult export, Handler handler,
        CancellationToken cancellation = default, Action<int, int>? progress = null)
        => TrajectoryUploader.UploadManyAsync(export.Paths, Endpoint, cancellation, progress, handler,
            retryDelay: (_, _) => Task.CompletedTask, confirmedReceipt: path => cache.ConfirmedReceipt(path, cancellation),
            onConfirmed: (path, receipt) => cache.RecordConfirmed(path, receipt, cancellation));
    public static string Id(int number) => number.ToString("x64");
    public static TrajectoryUploadReceipt Receipt(int number) => new()
        { UploadId = Id(number), UploadIds = new[] { Id(number) }, ModerationStatus = "approved", MapCompatibility = "matched" };
    public static HttpResponseMessage Response(int number, HttpStatusCode code = HttpStatusCode.Created) => new(code)
        { Content = new StringContent(JsonConvert.SerializeObject(new { uploadId = Id(number), moderationStatus = "approved", mapCompatibility = "matched", duplicate = false })) };
    public string StatePath() => Directory.GetFiles(Path.Combine(Output, ".upload-cache"), "*.json").Single();
    public void RewriteSignedState(Action<JObject> rewrite)
    {
        string path = StatePath(); var envelope = JObject.Parse(File.ReadAllText(path));
        var payload = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(envelope.Value<string>("Payload")!))); rewrite(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
        using var hmac = new HMACSHA256(File.ReadAllBytes(Path.Combine(Output, ".upload-cache", "authentication.key")));
        envelope["Payload"] = Convert.ToBase64String(bytes); envelope["Mac"] = Convert.ToHexString(hmac.ComputeHash(bytes)).ToLowerInvariant(); File.WriteAllText(path, envelope.ToString());
    }
    public void RewriteIndex(Action<JObject> rewrite)
    {
        byte[] bytes = File.ReadAllBytes(Recording); int length = checked((int)BitConverter.ToInt64(bytes, bytes.Length - 16));
        int offset = bytes.Length - 16 - length; var index = JObject.Parse(Encoding.UTF8.GetString(bytes, offset, length)); rewrite(index);
        byte[] encoded = Encoding.UTF8.GetBytes(index.ToString(Formatting.None));
        using var file = new FileStream(Recording, FileMode.Create); file.Write(bytes, 0, offset); file.Write(encoded);
        file.Write(BitConverter.GetBytes((long)encoded.Length)); file.Write(Encoding.ASCII.GetBytes("PEAKEND1"));
    }
    public void MakeMetadataOnlyBody()
    {
        byte[] bytes = File.ReadAllBytes(Recording); int headerLength = BitConverter.ToInt32(bytes, 8); int headEnd = 12 + headerLength;
        var header = JsonConvert.DeserializeObject<ReplayHeader>(Encoding.UTF8.GetString(bytes, 12, headerLength))!;
        const long bodyLength = 16 * 1024 * 1024;
        var index = new FullReplayIndex { Version = 1, Header = header, Duration = 1, FrameCount = 2,
            ActorIds = new[] { "first", "second" }, Complete = true, Reason = "metadata-only",
            Pages = new[] { new FullReplayPage { Index = 0, Start = 0, End = 1, Offset = headEnd, Length = bodyLength, RawBytes = 1, DecodedBytes = 1, FrameCount = 2 } } };
        byte[] encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(index, ReplayFiles.Json));
        using var file = new FileStream(Recording, FileMode.Create); file.Write(bytes, 0, headEnd); file.SetLength(headEnd + bodyLength);
        file.Position = file.Length; file.Write(encoded); file.Write(BitConverter.GetBytes((long)encoded.Length)); file.Write(Encoding.ASCII.GetBytes("PEAKEND1"));
    }
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
