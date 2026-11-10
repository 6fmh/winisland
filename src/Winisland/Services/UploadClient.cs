using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Winisland.Services;

public sealed record UploadResult(string ShareUrl, string FileUrl, string ViewerUrl);

public sealed class UploadClient : IDisposable
{
    public const long MaxFileBytes = 90L * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly Uri? _endpoint;
    public UploadClient() : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) }) { }
    internal UploadClient(HttpClient http, Uri? endpoint = null) { _http = http; _endpoint = endpoint; }

    public async Task<UploadResult> UploadAsync(string path, UploadConfiguration connection, IProgress<double>? progress, CancellationToken cancellation)
    {
        connection = UploadCredentialStore.Validate(connection);
        // ResponseHeadersRead completes HttpClient's timeout at the headers. Keep
        // the same deadline alive while reading the bounded response body too.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (_http.Timeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(_http.Timeout);
        cancellation = deadline.Token;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length == 0) throw new InvalidOperationException("Empty files cannot be uploaded.");
        if (file.Length > MaxFileBytes) throw new InvalidOperationException("Winisland supports uploads up to 90 MB.");
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint ?? new Uri(connection.ServerUrl));
        if (connection.Token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
        request.Headers.UserAgent.ParseAdd("Winisland/0.4");
        foreach (var pair in connection.Headers)
        {
            request.Headers.Remove(pair.Key);
            if (!request.Headers.TryAddWithoutValidation(pair.Key, pair.Value)) throw new ArgumentException("The uploader contains an unsupported request header.");
        }
        using var body = new MultipartFormDataContent();
        var content = new ProgressContent(file, progress);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Add(content, connection.FileFormName, Path.GetFileName(path));
        foreach (var pair in connection.Arguments) body.Add(new StringContent(pair.Value), pair.Key);
        request.Content = body;
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellation)) > 0)
        {
            if (memory.Length + count > 65536) throw new InvalidOperationException("The host returned an unexpected response.");
            await memory.WriteAsync(buffer.AsMemory(0, count), cancellation);
        }
        JsonDocument document;
        try { document = JsonDocument.Parse(memory.ToArray()); }
        catch (JsonException)
        {
            var link = Encoding.UTF8.GetString(memory.ToArray()).Trim();
            if (response.IsSuccessStatusCode && IsShareLink(link)) return new(link, link, "");
            throw new InvalidOperationException(response.IsSuccessStatusCode ? "The host returned an unreadable response." : FailureMessage(response.StatusCode, response.Headers.RetryAfter));
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("The host returned an unexpected response.");
            if (!response.IsSuccessStatusCode || root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False
                || root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            {
                var error = root.TryGetProperty("error", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString() : null;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? FailureMessage(response.StatusCode, response.Headers.RetryAfter) : error[..Math.Min(error.Length, 240)]);
            }
            string Read(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            string ReadPath(string path)
            {
                var current = root;
                foreach (var part in path.Split('.'))
                    if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current)) return "";
                return current.ValueKind == JsonValueKind.String ? current.GetString() ?? "" : "";
            }
            var direct = Read("file");
            var viewer = Read("viewer");
            var share = connection.UrlPath.Length > 0 ? ReadPath(connection.UrlPath) : Read("url");
            if (share.Length == 0 && connection.UrlPath.Length == 0)
                share = new[] { "link", "data.url", "data.link", "secure_url", "file" }.Select(ReadPath).FirstOrDefault(IsShareLink) ?? "";
            // The ShareX url is Markdown with an invisible label. Clipboard links must be plain URLs.
            if (share.StartsWith("[", StringComparison.Ordinal) && share.EndsWith(")", StringComparison.Ordinal) && share.IndexOf("](", StringComparison.Ordinal) is var split && split >= 0)
                share = share[(split + 2)..^1];
            if (!IsShareLink(direct)) direct = share;
            if (!IsShareLink(direct)) throw new InvalidOperationException("The host did not return a valid HTTPS file link. Check the uploader's JSON URL field.");
            if (!IsShareLink(viewer)) viewer = "";
            if (!IsShareLink(share)) share = direct;
            return new UploadResult(share, direct, viewer);
        }
    }

    internal static bool IsShareLink(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.UserInfo.Length == 0 && !value!.Any(char.IsControl);

    private static string FailureMessage(HttpStatusCode status, RetryConditionHeaderValue? retry) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Upload access was refused. Update your token in Settings.",
        HttpStatusCode.RequestEntityTooLarge => "The file exceeds the host's current size limit.",
        HttpStatusCode.UnsupportedMediaType => "The host does not accept this file type.",
        HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => $"The host is busy. Retry {(retry?.Delta is { } wait ? $"in {Math.Ceiling(wait.TotalSeconds)} seconds" : "in a moment")}.",
        _ => $"Upload failed (HTTP {(int)status}). Try again."
    };

    public void Dispose() => _http.Dispose();

    private sealed class ProgressContent : HttpContent
    {
        private readonly Stream _file;
        private readonly IProgress<double>? _progress;
        public ProgressContent(Stream file, IProgress<double>? progress) { _file = file; _progress = progress; }
        protected override bool TryComputeLength(out long length) { length = _file.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellation)
        {
            var buffer = new byte[65536];
            long sent = 0;
            int count;
            while ((count = await _file.ReadAsync(buffer, cancellation)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, count), cancellation);
                sent += count;
                _progress?.Report(100.0 * sent / _file.Length);
            }
        }
    }
}
