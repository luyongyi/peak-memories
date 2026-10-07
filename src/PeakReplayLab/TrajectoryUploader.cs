using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PeakReplayLab;

public sealed class TrajectoryUploadReceipt
{
    public string UploadId { get; set; } = "";
    public string ModerationStatus { get; set; } = "";
    public string MapCompatibility { get; set; } = "";
    public bool Duplicate { get; set; }
}

// Only the separately exported, bounded trajectory package reaches HTTP. No
// recording path, native IDs, or raw replay bytes are part of this API.
public static class TrajectoryUploader
{
    public const long MaximumPackageBytes = 12L * 1024 * 1024;
    public const int MaximumResponseBytes = 64 * 1024;

    public static Uri Endpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0 ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            !uri.AbsolutePath.EndsWith("/api/route-uploads", StringComparison.Ordinal))
            throw new ArgumentException("轨迹接口需要 HTTPS /api/route-uploads 地址。");
        return uri;
    }

    public static async Task<TrajectoryUploadReceipt> UploadAsync(string packagePath, Uri endpoint,
        CancellationToken cancellation = default, HttpMessageHandler? testHandler = null)
    {
        Endpoint(endpoint.AbsoluteUri);
        if (!packagePath.EndsWith(".trajectory.json.gz", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("只允许上传独立轨迹包。");
        using var file = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length <= 0 || file.Length > MaximumPackageBytes)
            throw new InvalidDataException("轨迹包为空或超过 12 MiB，请重新导出。");
        using var client = new HttpClient(testHandler ?? new HttpClientHandler { AllowAutoRedirect = false });
        client.Timeout = TimeSpan.FromSeconds(60);
        for (int attempt = 0; ; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            attemptTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            file.Position = 0;
            // A fresh content instance is required for every retry. Keep the one
            // read-only file handle alive so a changed path cannot swap payloads.
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.TryAddWithoutValidation("X-Trajectory-Format", "trajectory-v1");
            request.Content = new StreamContent(new BorrowedStream(file));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content.Headers.ContentEncoding.Add("gzip");
            request.Content.Headers.ContentLength = file.Length;
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptTimeout.Token).ConfigureAwait(false);
                if (Retryable(response.StatusCode) && attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), cancellation).ConfigureAwait(false);
                    continue;
                }
                string body = await ReadBounded(response.Content, attemptTimeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidDataException($"上传接口返回 {(int)response.StatusCode}，轨迹包已保留，可稍后重试。");
                JObject json;
                try
                {
                    using var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(body)) { MaxDepth = 12 };
                    json = JObject.Load(reader);
                }
                catch (Exception e) when (e is Newtonsoft.Json.JsonException || e is ArgumentException)
                { throw new InvalidDataException("服务器响应格式无效，轨迹包已保留。", e); }
                string id = json.Value<string>("uploadId") ?? "";
                string moderation = json.Value<string>("moderationStatus") ?? "";
                string compatibility = json.Value<string>("mapCompatibility") ?? "";
                if (!HexId(id) || (moderation != "pending" && moderation != "approved" && moderation != "hidden" && moderation != "rejected") ||
                    (compatibility != "matched" && compatibility != "waiting-map"))
                    throw new InvalidDataException("服务器没有返回有效投稿回执，轨迹包已保留。");
                return new TrajectoryUploadReceipt
                {
                    UploadId = id, ModerationStatus = moderation, MapCompatibility = compatibility,
                    Duplicate = json.Value<bool?>("duplicate") ?? false
                };
            }
            catch (HttpRequestException) when (attempt < 2)
            { await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && attempt < 2)
            { await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), cancellation).ConfigureAwait(false); }
        }
    }

    private static bool Retryable(HttpStatusCode code) => (int)code == 408 || (int)code == 429 || (int)code >= 500;
    private static bool HexId(string id)
    {
        if (id.Length != 64) return false;
        foreach (char c in id) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
        return true;
    }
    private static async Task<string> ReadBounded(HttpContent content, CancellationToken cancellation)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("服务器响应过大。");
        using var input = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > MaximumResponseBytes) throw new InvalidDataException("服务器响应过大。");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
    private sealed class BorrowedStream : Stream
    {
        private readonly Stream stream;
        public BorrowedStream(Stream stream) => this.stream = stream;
        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => stream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => stream.Length;
        public override long Position { get => stream.Position; set => stream.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => stream.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { /* The upload operation owns the stream. */ }
    }
}
