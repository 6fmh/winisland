using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Winisland.Services;

namespace Winisland;

internal static class UploadVerification
{
    private const string Token = "fixture-upload-token";
    private const string Endpoint = "https://uploads.example.test/api/upload";
    private static UploadConfiguration Configuration => new() { ServerUrl = Endpoint, Token = Token };
    private const string Direct = "https://uploads.example.test/u/test-file.txt";
    private const string Viewer = "https://uploads.example.test/v/test-file";
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var freshDirectory = Path.Combine(SettingsStore.DirectoryPath, "missing-upload-profile-" + Guid.NewGuid().ToString("N"));
        var freshCredentials = new UploadCredentialStore(freshDirectory);
        check(!freshCredentials.IsConnected && freshCredentials.ServerUrl.Length == 0, "A fresh profile has no preconfigured upload server");
        freshCredentials.Disconnect();
        check(!freshCredentials.IsConnected && !Directory.Exists(freshDirectory),
            "Disconnecting an upload account in a fresh profile succeeds without creating data files");
        Directory.CreateDirectory(freshDirectory);
        File.WriteAllBytes(Path.Combine(freshDirectory, "upload-token.bin"), WindowsCredentialProtection.Protect(Encoding.UTF8.GetBytes(Token), true));
        var legacy = new UploadCredentialStore(freshDirectory);
        check(!legacy.IsConnected && legacy.ServerUrl.Length == 0 && legacy.Token.Length == 0,
            "Legacy token-only profiles never infer a server or reuse that token for another destination");
        legacy.Disconnect();
        var credentials = new UploadCredentialStore();
        credentials.Disconnect();
        credentials.Save(Endpoint, Token);
        check(new UploadCredentialStore().Token == Token && !Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(SettingsStore.DirectoryPath, "upload-connection.bin"))).Contains(Token), "Upload credential survives restart encrypted by Windows");
        check(!File.ReadAllText(Path.Combine(SettingsStore.DirectoryPath, "settings.json")).Contains(Token), "Upload tokens never appear in settings JSON");
        var config = JsonSerializer.Serialize(new { RequestURL = Endpoint, RequestMethod = "POST", Body = "MultipartFormData", FileFormName = "file", Headers = new { Authorization = "Bearer " + Token } });
        check(UploadCredentialStore.ImportShareX(config).Headers["Authorization"] == "Bearer " + Token, "Imports the chosen server and authorization from ShareX");
        check(UploadCredentialStore.ImportShareX(config.Replace("uploads.example.test", "my-server.example.test")).ServerUrl == "https://my-server.example.test/api/upload",
            "ShareX import accepts the server explicitly chosen by the user");
        var refused = false;
        try { UploadCredentialStore.ImportShareX(config.Replace("https://", "http://")); } catch (ArgumentException) { refused = true; }
        check(refused, "Rejects insecure HTTP uploader destinations");
        foreach (var malformed in new[] { "[]", "{\"RequestURL\":42}", config.Replace("\"Headers\":{\"Authorization\":\"Bearer " + Token + "\"}", "\"Headers\":42") })
        {
            refused = false;
            try { UploadCredentialStore.ImportShareX(malformed); } catch (ArgumentException) { refused = true; }
            check(refused, "Malformed ShareX value types report a configuration error without crashing");
        }
        check(!UploadClient.IsShareLink("javascript:alert(1)") && !UploadClient.IsShareLink("https://user:password@uploads.example.test/file") && !UploadClient.IsShareLink("http://uploads.example.test/file"), "Clipboard accepts HTTPS share links without embedded credentials");

        var shelf = new FileShelf(); shelf.Clear();
        var file = Path.Combine(SettingsStore.DirectoryPath, "a file with spaces and ü.txt");
        var original = new string('a', 160000) + "\nIsolated upload fixture; preserve the original.";
        File.WriteAllText(file, original);
        var customConfig = JsonSerializer.Serialize(new { RequestURL = "https://my-server.example.test/upload", RequestMethod = "POST", Body = "MultipartFormData",
            FileFormName = "image", URL = "$json:data.url$", Headers = new { X_Api_Key = "custom-key" }, Arguments = new { album = "screenshots" } });
        var custom = UploadCredentialStore.ImportShareX(customConfig);
        var customCorrect = false;
        using (var client = new UploadClient(new HttpClient(new Handler(async (request, cancellation) =>
        {
            var body = await request.Content!.ReadAsStringAsync(cancellation);
            customCorrect = request.RequestUri!.AbsoluteUri == custom.ServerUrl && request.Headers.GetValues("X_Api_Key").Single() == "custom-key"
                && body.Contains("name=image") && body.Contains("screenshots") && body.Contains(original);
            return Response(HttpStatusCode.Created, new { data = new { url = "https://cdn.example.test/files/custom.png" } });
        }))))
        {
            var result = await client.UploadAsync(file, custom, null, CancellationToken.None);
            check(customCorrect && result.ShareUrl == "https://cdn.example.test/files/custom.png", "Custom ShareX fields, headers, form values and nested JSON CDN links work without a site-specific response");
        }
        using (var client = new UploadClient(new HttpClient(new Handler((request, _) =>
        {
            check(request.Headers.Authorization is null, "Public upload servers work without an authorization token");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("https://cdn.example.test/file.txt") });
        }))))
        {
            var result = await client.UploadAsync(file, Configuration with { Token = "" }, null, CancellationToken.None);
            check(result.ShareUrl == "https://cdn.example.test/file.txt", "Plain HTTPS-link upload responses are accepted");
        }
        var otherFile = Path.Combine(SettingsStore.DirectoryPath, "shelf-valid-tail.txt"); File.WriteAllText(otherFile, "Valid shelf tail.");
        File.WriteAllText(Path.Combine(SettingsStore.DirectoryPath, "shelf.json"), JsonSerializer.Serialize(new object?[]
            { null, 42, new { Path = 42 }, new { Path = file, ShareUrl = 42 }, new { Path = file }, new { Path = otherFile } }));
        var recovered = new FileShelf();
        check(recovered.Items.Count == 2 && recovered.Items.Last().Path == otherFile,
            "Malformed shelf rows do not discard later valid references and duplicate paths load once");
        check(shelf.Add(new[] { file, Path.Combine(SettingsStore.DirectoryPath, ".", Path.GetFileName(file)) }) == 1 && shelf.Items.Count == 1,
            "Equivalent file paths share one shelf reference and cannot queue duplicate uploads");
        shelf.Clear();
        var requests = 0;
        var protocolCorrect = false;
        var handler = new Handler(async (request, cancellation) =>
        {
            requests++;
            var body = await request.Content!.ReadAsStringAsync(cancellation);
            protocolCorrect = request.RequestUri!.ToString() == Endpoint
                && request.Headers.Authorization?.ToString() == "Bearer " + Token
                && request.Content.Headers.ContentType?.MediaType == "multipart/form-data"
                && body.Contains("name=file") && body.Contains(original);
            return Response(HttpStatusCode.Created, new { ok = true, file = Direct, viewer = Viewer, url = $"[⠀]({Viewer})" });
        });
        using (var uploads = new ShelfUploadService(shelf, credentials, new UploadClient(new HttpClient(handler))))
        {
            shelf.Add(new[] { file }); var item = shelf.Items[0];
            uploads.Enqueue(item); uploads.Enqueue(item);
            await WaitUntil(() => item.State == UploadState.Uploaded);
            check(protocolCorrect && requests == 1 && item.ShareUrl == Viewer && item.FileUrl == Direct, "Streams one token-authenticated multipart file and unwraps the ShareX Markdown link");
            check(new FileShelf().Items.Single().ShareUrl == Viewer, "Uploaded links survive a shelf restart");
            File.Delete(file);
            check(new FileShelf().Items.Single().ShareUrl == Viewer,
                "Uploaded links survive a restart even after their local original is deleted");
            File.WriteAllText(file, original);
            credentials.Disconnect();
            var waitingFile = Path.Combine(SettingsStore.DirectoryPath, "waiting.txt"); File.WriteAllText(waitingFile, "Waiting upload fixture.");
            shelf.Add(new[] { waitingFile }); var waiting = shelf.Items.Last(); uploads.Enqueue(waiting);
            check(waiting.State == UploadState.NeedsConnection && requests == 1, "Dropped files wait without network requests until a server is configured");
            credentials.Save(Endpoint, Token);
            await WaitUntil(() => waiting.State == UploadState.Uploaded);
            check(requests == 2, "Saving a token uploads only files explicitly waiting for connection");
        }
        shelf.Clear(); shelf.Add(new[] { file });
        using (var uploads = new ShelfUploadService(shelf, credentials, new UploadClient(new HttpClient(new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.UnsupportedMediaType, new { ok = false, error = "Markup files are not accepted" })))))))
        {
            var item = shelf.Items[0]; uploads.Enqueue(item); await WaitUntil(() => item.State == UploadState.Failed);
            check(item.Error == "Markup files are not accepted" && item.CanRetry && !item.HasLink, "Host rejection appears on its file row and allows retry");
        }
        var finishCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var uploads = new ShelfUploadService(shelf, credentials, new UploadClient(new HttpClient(new Handler(async (_, _) =>
        {
            cancelledStarted.SetResult(true); await finishCancelled.Task;
            return Response(HttpStatusCode.Created, new { ok = true, file = Direct });
        })))))
        {
            var item = shelf.Items[0]; uploads.Enqueue(item); await cancelledStarted.Task;
            uploads.Cancel(item); finishCancelled.SetResult(true);
            await WaitUntil(() => item.Error.Length > 0);
            check(item.State == UploadState.Cancelled && !item.HasLink,
                "A response arriving after cancellation cannot restore an uploaded link");
        }
        using (var uploads = new ShelfUploadService(shelf, credentials, new UploadClient(new HttpClient(new Handler(async (_, cancellation) => { await Task.Delay(10000, cancellation); return Response(HttpStatusCode.Created, new { ok = true, file = Direct }); })))))
        {
            var item = shelf.Items[0]; uploads.Enqueue(item); uploads.Cancel(item);
            await WaitUntil(() => !item.IsUploading);
            check(item.State == UploadState.Cancelled && File.ReadAllText(file) == original, "Cancelling an in-flight upload preserves the original file");
        }
        var empty = Path.Combine(SettingsStore.DirectoryPath, "empty.txt"); File.WriteAllText(empty, "");
        using (var client = new UploadClient(new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Empty file reached the network")))))
        {
            refused = false;
            try { await client.UploadAsync(empty, Configuration, null, CancellationToken.None); } catch (InvalidOperationException ex) { refused = ex.Message == "Empty files cannot be uploaded."; }
            check(refused, "Rejects empty files before sending a network request");
        }
        using (var client = new UploadClient(new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            { Content = new StreamContent(new StalledStream()) }))) { Timeout = TimeSpan.FromMilliseconds(100) }))
        {
            refused = false;
            try { await client.UploadAsync(file, Configuration, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { refused = true; }
            check(refused, "The upload deadline also bounds a host that stalls after sending response headers");
        }
        shelf.Clear(); credentials.Disconnect();

        var liveFixture = Environment.GetEnvironmentVariable("WINISLAND_UPLOAD_TEST_URL");
        if (liveFixture is not null)
        {
            var fixtureUri = new Uri(liveFixture);
            if (!fixtureUri.IsLoopback || fixtureUri.Scheme != "http") throw new InvalidOperationException("Upload verification must use an isolated loopback fixture.");
            using var http = new HttpClient();
            using var setup = JsonDocument.Parse(await http.GetStringAsync(new Uri(fixtureUri, "/fixture")));
            var fixtureToken = setup.RootElement.GetProperty("token").GetString()!;
            using var client = new UploadClient(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }), new Uri(fixtureUri, "/api/upload"));
            var result = await client.UploadAsync(file, Configuration with { Token = fixtureToken }, null, CancellationToken.None);
            check(UploadClient.IsShareLink(result.FileUrl) && File.ReadAllText(file) == original, "Native client uploads through the real Fastify route, storage ingestion and token checks in isolation");
            var markup = Path.Combine(SettingsStore.DirectoryPath, "rejected.svg"); File.WriteAllText(markup, "<svg xmlns=\"http://www.w3.org/2000/svg\"><rect/></svg>");
            refused = false;
            try { await client.UploadAsync(markup, Configuration with { Token = fixtureToken }, null, CancellationToken.None); } catch (InvalidOperationException ex) { refused = ex.Message == "Markup files are not accepted"; }
            check(refused, "Real host refuses SVG and returns its rejection to the native client");
            await http.PostAsync(new Uri(fixtureUri, "/fixture/revoke"), null);
            refused = false;
            try { await client.UploadAsync(file, Configuration with { Token = fixtureToken }, null, CancellationToken.None); } catch (InvalidOperationException ex) { refused = ex.Message.Contains("unknown or revoked"); }
            check(refused, "Real host refuses a revoked upload token");
        }
    }
    private static HttpResponseMessage Response(HttpStatusCode status, object body) => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(10);
        if (!condition()) throw new TimeoutException("Upload verification did not finish.");
    }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => _send(request, cancellation);
    }
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default)
        { await Task.Delay(Timeout.Infinite, cancellation); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
