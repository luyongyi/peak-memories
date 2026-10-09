using System.IO.Compression;
using System.Net;
using System.Text;
using PeakReplayLab;

// Integration probe: only the caller-selected separate trajectory package can
// be submitted, using the exact production HTTP implementation.
if (args.Length == 3 && args[0] == "--probe-server")
{
    var receipt = await TrajectoryUploader.UploadAsync(args[1], TrajectoryUploader.Endpoint(args[2]));
    Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(receipt));
    return;
}

int passed = 0;
void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
async Task Test(string name, Func<Task> test) { await test(); passed++; Console.WriteLine("PASS " + name); }
async Task Reject<T>(Func<Task> action) where T : Exception
{ try { await action(); } catch (T) { return; } throw new Exception("Expected rejection " + typeof(T).Name); }
var directory = Path.Combine(Path.GetTempPath(), "peak-trajectory-upload-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
string path = Path.Combine(directory, "synthetic.trajectory.json.gz");
using (var file = File.Create(path))
using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
    gzip.Write(Encoding.UTF8.GetBytes("{\"format\":\"trajectory-v1\",\"players\":[]}"));
var endpoint = TrajectoryUploader.Endpoint("https://peak.example/api/route-uploads");
string reply = "{\"uploadId\":\"" + new string('a', 64) + "\",\"moderationStatus\":\"pending\",\"mapCompatibility\":\"waiting-map\",\"duplicate\":true}";
HttpResponseMessage Response(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body) };
try
{
    await Test("only HTTPS or loopback and exact upload endpoint", async () =>
    {
        foreach (var value in new[] { "http://peak.example/api/route-uploads", "https://u:p@peak.example/api/route-uploads", "https://peak.example/api/runs", "https://peak.example/api/route-uploads?token=x", "https://peak.example/api/route-uploads#x" })
            await Reject<ArgumentException>(() => Task.FromResult(TrajectoryUploader.Endpoint(value)));
        Check(TrajectoryUploader.Endpoint("http://127.0.0.1:8787/api/route-uploads").IsLoopback, "local test endpoint");
    });
    await Test("POST contains the exact separate gzip package", async () =>
    {
        byte[] expected = File.ReadAllBytes(path);
        var handler = new Handler(async (request, cancellation) =>
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri == endpoint, "correct request target");
            Check(request.Content!.Headers.ContentEncoding.Single() == "gzip" && request.Content.Headers.ContentType!.MediaType == "application/json", "wire headers");
            Check(request.Headers.GetValues("Prefer").Single() == "return=minimal", "receipt must not grow with lobby size");
            byte[] actual = await request.Content.ReadAsByteArrayAsync(cancellation);
            Check(expected.SequenceEqual(actual), "only export bytes sent");
            return Response(HttpStatusCode.Created, reply);
        });
        var receipt = await TrajectoryUploader.UploadAsync(path, endpoint, testHandler: handler);
        Check(receipt.Duplicate && receipt.UploadId == new string('a', 64) && receipt.MapCompatibility == "waiting-map" && handler.Calls == 1, "receipt");
    });
    await Test("raw recordings and partial files never reach HTTP", async () =>
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, reply)));
        foreach (var ext in new[] { ".peakrun", ".peakreplay", ".partial", ".json" })
            await Reject<InvalidDataException>(() => TrajectoryUploader.UploadAsync(path + ext, endpoint, testHandler: handler));
        Check(handler.Calls == 0, "forbidden types sent");
    });
    await Test("empty and oversized exports rejected before HTTP", async () =>
    {
        var oversized = Path.Combine(directory, "size.trajectory.json.gz");
        using (var file = File.Create(oversized)) file.SetLength(TrajectoryUploader.MaximumPackageBytes + 1);
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, reply)));
        await Reject<InvalidDataException>(() => TrajectoryUploader.UploadAsync(oversized, endpoint, testHandler: handler));
        using (File.Create(oversized)) { }
        await Reject<InvalidDataException>(() => TrajectoryUploader.UploadAsync(oversized, endpoint, testHandler: handler));
        Check(handler.Calls == 0, "empty/oversize sent");
    });
    await Test("validation error is not retried", async () =>
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.UnprocessableEntity, "{}")));
        await Reject<InvalidDataException>(() => TrajectoryUploader.UploadAsync(path, endpoint, testHandler: handler));
        Check(handler.Calls == 1 && File.Exists(path), "rejected export preserved");
    });
    await Test("temporary server failure reopens the same immutable payload", async () =>
    {
        var payloads = new List<byte[]>();
        var handler = new Handler(async (request, cancellation) =>
        {
            payloads.Add(await request.Content!.ReadAsByteArrayAsync(cancellation));
            return Response(payloads.Count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, reply);
        });
        await TrajectoryUploader.UploadAsync(path, endpoint, testHandler: handler);
        Check(payloads.Count == 2 && payloads[0].SequenceEqual(payloads[1]), "retry payload changed");
    });
    await Test("redirect response is not considered an upload receipt", async () =>
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.Redirect, reply)));
        await Reject<InvalidDataException>(() => TrajectoryUploader.UploadAsync(path, endpoint, testHandler: handler));
        Check(handler.Calls == 1, "redirect followed");
    });
    await Test("malformed receipt and native identity are rejected", async () =>
    {
        foreach (string invalid in new[] { "not-json", "{}", reply.Replace(new string('a', 64), "76561199000000000"), reply.Replace("\"pending\"", "\"\""), reply.Replace("pending", "unexpected"), reply.Replace("waiting-map", "unexpected") })
        {
            var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, invalid)));
            await Reject<InvalidDataException>(() => TrajectoryUploader.UploadAsync(path, endpoint, testHandler: handler));
        }
    });
    await Test("response allocation is bounded", async () =>
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, new string('x', TrajectoryUploader.MaximumResponseBytes + 1))));
        await Reject<InvalidDataException>(() => TrajectoryUploader.UploadAsync(path, endpoint, testHandler: handler));
    });
    await Test("cancelled export never starts upload", async () =>
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, reply)));
        await Reject<OperationCanceledException>(() => TrajectoryUploader.UploadAsync(path, endpoint, cancellation.Token, handler));
        Check(handler.Calls == 0, "request sent after cancellation");
    });
    await Test("all 70 parts are acknowledged with no fixed teammate or part ceiling", async () =>
    {
        var paths = new List<string>();
        for (int i = 0; i < 70; i++)
        {
            string part = Path.Combine(directory, $"part-{i}.trajectory.json.gz");
            using (var file = File.Create(part))
            using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
                gzip.Write(Encoding.UTF8.GetBytes($"{{\"part\":{i}}}"));
            paths.Add(part);
        }
        int received = 0;
        var handler = new Handler(async (request, cancellation) =>
        {
            Check((await request.Content!.ReadAsByteArrayAsync(cancellation)).SequenceEqual(File.ReadAllBytes(paths[received])), "a teammate's packet was skipped or reordered");
            string id = (++received).ToString("x64");
            return Response(HttpStatusCode.Created, reply.Replace(new string('a', 64), id).Replace("pending", "approved").Replace("waiting-map", "matched"));
        });
        var progress = new List<(int, int)>();
        var receipt = await TrajectoryUploader.UploadManyAsync(paths, endpoint, progress: (done, total) => progress.Add((done, total)), testHandler: handler);
        Check(received == 70 && receipt.UploadIds.Length == 70 && receipt.UploadIds[^1] == 70.ToString("x64"), "last teammate never reached server");
        Check(progress[0] == (0, 70) && progress[^1] == (70, 70), "batch reported success early");
        Check(receipt.ModerationStatus == "approved" && receipt.MapCompatibility == "matched", "aggregate receipt");
    });
    await Test("partial batch failure never reports all teammates uploaded and files remain retryable", async () =>
    {
        string second = Path.Combine(directory, "second.trajectory.json.gz"); File.Copy(path, second, true);
        int received = 0;
        var handler = new Handler((_, _) => Task.FromResult(Response(++received == 1 ? HttpStatusCode.Created : HttpStatusCode.UnprocessableEntity, reply)));
        int confirmed = 0;
        try
        {
            await TrajectoryUploader.UploadManyAsync(new[] { path, second }, endpoint, progress: (done, _) => confirmed = done, testHandler: handler);
            throw new Exception("incomplete batch accepted");
        }
        catch (InvalidDataException error) { Check(error.Message.Contains("1/2"), "missing acknowledged part count"); }
        Check(confirmed == 1 && received == 2 && File.Exists(path) && File.Exists(second), "batch failure lost source exports");
    });
    await Test("batch preflight rejects bad or duplicate paths before sending any part", async () =>
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, reply)));
        await Reject<InvalidDataException>(() => TrajectoryUploader.UploadManyAsync(new[] { path, path }, endpoint, testHandler: handler));
        await Reject<InvalidDataException>(() => TrajectoryUploader.UploadManyAsync(new[] { path, path + ".peakrun" }, endpoint, testHandler: handler));
        await Reject<InvalidDataException>(() => TrajectoryUploader.UploadManyAsync(Array.Empty<string>(), endpoint, testHandler: handler));
        Check(handler.Calls == 0, "invalid batch partially transmitted");
    });
    await Test("large batches respect a server rate window without consuming transient-failure retries", async () =>
    {
        int calls = 0; var waits = new List<TimeSpan>();
        var handler = new Handler((_, _) =>
        {
            var response = Response(++calls <= 4 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, reply);
            if (calls <= 4) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return Task.FromResult(response);
        });
        await TrajectoryUploader.UploadManyAsync(new[] { path }, endpoint, testHandler: handler,
            retryDelay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        Check(calls == 5 && waits.Count == 4 && waits.All(wait => wait == TimeSpan.FromSeconds(60)), "rate window treated as teammate/attempt ceiling");
    });
    await Test("cancel between parts prevents subsequent teammate requests", async () =>
    {
        string second = Path.Combine(directory, "cancel-second.trajectory.json.gz"); File.Copy(path, second, true);
        using var cancellation = new CancellationTokenSource();
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, reply)));
        await Reject<OperationCanceledException>(() => TrajectoryUploader.UploadManyAsync(new[] { path, second }, endpoint,
            cancellation.Token, (done, _) => { if (done == 1) cancellation.Cancel(); }, handler));
        Check(handler.Calls == 1 && File.Exists(second), "cancelled batch sent later members");
    });
}
finally { Directory.Delete(directory, recursive: true); }
Console.WriteLine($"{passed} trajectory upload contracts passed.");

sealed class Handler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
    public int Calls;
    public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => this.send = send;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Calls++; return send(request, cancellationToken); }
}
