using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace Winisland.Services;

internal sealed record ReleaseAsset(string Name, Uri Url, long Size, string Digest);
internal sealed record AppRelease(Version Version, string Tag, string Notes, ReleaseAsset Installer, ReleaseAsset Checksum, bool NotesTruncated = false);
public sealed record UpdatePackage(string InstallerPath, string Sha256, string Version);

internal sealed class GitHubReleaseClient : IDisposable
{
    internal const string Repository = "6fmh/winisland";
    internal static readonly Uri LatestReleaseUrl = new($"https://api.github.com/repos/{Repository}/releases/latest");
    internal static Version CurrentVersion => Normalize(typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0));
    internal static string Runtime => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
    private readonly HttpClient _http;
    private readonly string _runtime;
    internal GitHubReleaseClient(HttpClient? http = null, string? runtime = null)
    {
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
        _runtime = runtime ?? Runtime;
    }
    internal static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));

    internal async Task<AppRelease?> GetLatestAsync(CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await GetAsync(LatestReleaseUrl, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadBoundedAsync(response, 1024 * 1024, deadline.Token).ConfigureAwait(false));
        return Parse(document.RootElement, _runtime);
    }

    internal static AppRelease? Parse(JsonElement root, string runtime)
    {
        static string Read(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("GitHub returned incomplete update details.");
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True
            || root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True) return null;
        var tag = Read(root, "tag_name");
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$") || !Version.TryParse(tag[1..], out var version))
            throw new InvalidOperationException("This release has an unsupported version. Try again later.");
        var installerName = $"Winisland-Setup-{version.ToString(3)}-{runtime}.exe";
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The release installer is not ready yet. Try again later.");
        ReleaseAsset Find(string name, long limit)
        {
            var matches = assets.EnumerateArray().Where(item => Read(item, "name") == name).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("The release installer for this PC is not ready yet. Try again later.");
            var item = matches[0];
            var expected = new Uri($"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}");
            if (!Uri.TryCreate(Read(item, "browser_download_url"), UriKind.Absolute, out var uri) || uri != expected
                || !item.TryGetProperty("size", out var size) || !size.TryGetInt64(out var length) || length <= 0 || length > limit)
                throw new InvalidOperationException("The release contains invalid installer details.");
            var digest = Read(item, "digest");
            if (digest.Length > 0 && !Regex.IsMatch(digest, @"^sha256:[a-fA-F0-9]{64}$"))
                throw new InvalidOperationException("The release contains an invalid download checksum.");
            return new(name, uri, length, digest);
        }
        var notes = Read(root, "body");
        return new(Normalize(version), tag, notes[..Math.Min(6000, notes.Length)],
            Find(installerName, 512L * 1024 * 1024), Find(installerName + ".sha256", 4096), notes.Length > 6000);
    }

    internal async Task<UpdatePackage> DownloadAsync(AppRelease release, string directory, IProgress<double>? progress, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        cancellation = deadline.Token;
        using var checksumResponse = await GetAsync(release.Checksum.Url, cancellation).ConfigureAwait(false);
        checksumResponse.EnsureSuccessStatusCode();
        var checksumBytes = await ReadBoundedAsync(checksumResponse, 4096, cancellation).ConfigureAwait(false);
        if (checksumBytes.LongLength != release.Checksum.Size) throw new InvalidOperationException("The release checksum download is incomplete.");
        var checksumText = Encoding.UTF8.GetString(checksumBytes).Trim();
        var match = Regex.Match(checksumText, @"^([a-fA-F0-9]{64})[ \t]+\*?" + Regex.Escape(release.Installer.Name) + "$", RegexOptions.CultureInvariant);
        if (!match.Success) throw new InvalidOperationException("The release checksum does not match its installer.");
        var expectedHash = match.Groups[1].Value.ToLowerInvariant();
        if (release.Installer.Digest.Length > 0 && !release.Installer.Digest[7..].Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The release's download checksums disagree. Try again later.");
        if (release.Checksum.Digest.Length > 0 && !Convert.ToHexString(SHA256.HashData(checksumBytes)).Equals(release.Checksum.Digest[7..], StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The release checksum failed verification.");
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, release.Installer.Name);
        var temporary = finalPath + ".partial";
        try
        {
            using var response = await GetAsync(release.Installer.Url, cancellation).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != release.Installer.Size)
                throw new InvalidOperationException("The installer download has an unexpected size.");
            using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536];
                var latestProgress = progress is null ? null : new LatestProgress(progress);
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                {
                    received += count;
                    if (received > release.Installer.Size) throw new InvalidOperationException("The installer download exceeded its expected size.");
                    hash.AppendData(buffer, 0, count);
                    await file.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
                    latestProgress?.Report(100.0 * received / release.Installer.Size);
                }
                if (received != release.Installer.Size) throw new InvalidOperationException("The installer download is incomplete. Try again.");
                await file.FlushAsync(cancellation).ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested();
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(expectedHash)))
                throw new InvalidOperationException("The installer failed verification. Download it again.");
            File.Move(temporary, finalPath);
            return new(finalPath, expectedHash, release.Version.ToString(3));
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellation)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0
                || uri.Host is not ("api.github.com" or "github.com") && !uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("GitHub redirected the download to an unsupported address.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd($"Winisland/{CurrentVersion.ToString(3)}");
            if (uri.Host == "api.github.com")
            {
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            }
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new InvalidOperationException("GitHub returned an incomplete download address.");
            uri = new Uri(uri, location);
        }
        throw new InvalidOperationException("GitHub returned too many download redirects.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken cancellation)
    {
        using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + count > limit) throw new InvalidOperationException("GitHub returned oversized update details.");
            await memory.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
        }
        return memory.ToArray();
    }
    public void Dispose() => _http.Dispose();
}
