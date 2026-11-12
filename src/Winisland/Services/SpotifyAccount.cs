using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Winisland.Services;

// A desktop public client: PKCE uses no client secret. Credentials are protected
// by Windows for the current user; playback state remains in memory.
public sealed class SpotifyAccount : IDisposable
{
    public const string RedirectUri = "http://127.0.0.1:43827/spotify/callback/";
    private sealed record Credentials(string ClientId, string RefreshToken);
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Credentials? _credentials;
    private string _accessToken = "";
    private long _expiresAt, _nextRead, _retryAt, _holdUntil, _lastWriteStarted, _generation;
    private PlayerVolumeSnapshot _current = new();
    private bool _disposed;
    private string CredentialPath => Path.Combine(SettingsStore.DirectoryPath, "spotify-account.bin");
    public bool IsConnected => _credentials is not null;
    public string ClientId => _credentials?.ClientId ?? "";
    public string Status { get; private set; } = "Account not connected";
    public event EventHandler? Changed;

    public SpotifyAccount() : this(new HttpClient { Timeout = TimeSpan.FromSeconds(8) }, true) { }
    internal SpotifyAccount(HttpClient http, bool loadCredentials)
    {
        _http = http;
        if (!loadCredentials) return;
        try
        {
            if (File.Exists(CredentialPath))
            {
                var bytes = WindowsCredentialProtection.Protect(File.ReadAllBytes(CredentialPath), false);
                try
                {
                    var credentials = JsonSerializer.Deserialize<Credentials>(bytes);
                    if (credentials is null || credentials.ClientId is not { Length: 32 } || !credentials.ClientId.All(char.IsAsciiHexDigit)
                        || string.IsNullOrWhiteSpace(credentials.RefreshToken)) throw new JsonException("Invalid Spotify credentials.");
                    _credentials = credentials;
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                if (_credentials is not null) Status = "Account connected · 1% volume steps";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException)
        { Status = "Reconnect your Spotify account"; }
    }

    public async Task ConnectAsync(string clientId, CancellationToken cancellationToken = default)
    {
        clientId = clientId.Trim();
        if (clientId.Length != 32 || !clientId.All(char.IsAsciiHexDigit))
            throw new ArgumentException("Enter the 32-character Client ID from your Spotify Developer Dashboard.");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        operation.CancelAfter(TimeSpan.FromMinutes(3));
        var token = operation.Token;
        await _gate.WaitAsync(token);
        var generation = _generation;
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, new Uri(RedirectUri).Port);
            try { listener.Start(); }
            catch (SocketException) { throw new InvalidOperationException("The Spotify sign-in port is busy or unavailable. Close another sign-in attempt and try again."); }
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
            var state = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var url = "https://accounts.spotify.com/authorize?" + Query(new Dictionary<string, string>
            {
                ["client_id"] = clientId, ["response_type"] = "code", ["redirect_uri"] = RedirectUri,
                ["scope"] = "user-read-playback-state user-modify-playback-state", ["state"] = state,
                ["code_challenge_method"] = "S256", ["code_challenge"] = challenge
            });
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            var code = await ReceiveAuthorizationAsync(listener, state, token);
            token.ThrowIfCancellationRequested();
            if (_disposed || generation != _generation) throw new OperationCanceledException(token);
            await FinishSignInAsync(clientId, code, verifier, token, generation);
        }
        finally { _gate.Release(); }
    }

    // Kept separate from the browser callback so the final account transition
    // can also be verified without opening a browser or a loopback listener.
    internal async Task FinishSignInAsync(string clientId, string code, string verifier, CancellationToken token, long? expectedGeneration = null)
    {
        var generation = expectedGeneration ?? _generation;
        token.ThrowIfCancellationRequested();
        if (_disposed || generation != _generation) throw new OperationCanceledException(token);
        using var response = await _http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = RedirectUri,
            ["client_id"] = clientId, ["code_verifier"] = verifier
        }), token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Spotify could not finish sign-in. Check the app's Client ID, redirect URI, and allowed users.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        token.ThrowIfCancellationRequested();
        if (_disposed || generation != _generation) throw new OperationCanceledException(token);
        var root = json.RootElement;
        ReadToken(root);
        if (!root.TryGetProperty("refresh_token", out var refresh) || refresh.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(refresh.GetString())) throw new JsonException("Invalid Spotify authorization response.");
        var credentials = new Credentials(clientId, refresh.GetString()!);
        Save(credentials);
        _credentials = credentials;
        ++_generation;
        SetAccessToken(root);
        _current = new(); _nextRead = _retryAt = _holdUntil = 0;
        Status = "Account connected · 1% volume steps";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Disconnect()
    {
        try { File.Delete(CredentialPath); }
        catch (DirectoryNotFoundException) { } // A fresh profile has no saved account to delete.
        _credentials = null; _accessToken = ""; _current = new(); _nextRead = _retryAt = _holdUntil = 0;
        ++_generation;
        Status = "Account disconnected";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void ConnectForTest(string clientId, string refreshToken) { _credentials = new(clientId, refreshToken); ++_generation; }

    internal async Task<PlayerVolumeSnapshot> ReadAsync(MediaSnapshot media, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = operation.Token;
        await _gate.WaitAsync(token);
        var generation = _generation;
        var missing = new PlayerVolumeSnapshot { Source = media.Source, Player = "Spotify", Step = 1, ConnectionHint = Status };
        try
        {
            if (!IsConnected || _disposed) return missing;
            var now = Environment.TickCount64;
            if (now < _retryAt || now < _nextRead) return _current with { Source = media.Source };
            _nextRead = now + 1000;
            using var response = await SendAsync(HttpMethod.Get, "https://api.spotify.com/v1/me/player", token);
            if (_disposed || generation != _generation) return missing with { ConnectionHint = Status };
            if (response is null) return _current with { Source = media.Source, ConnectionHint = Status };
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                Status = "Start Spotify playback on this PC to use account volume.";
                return _current = missing with { ConnectionHint = Status };
            }
            if (!response.IsSuccessStatusCode)
            {
                Status = response.StatusCode == HttpStatusCode.Forbidden
                    ? "Spotify API access requires Premium and an account allowed in your app's dashboard."
                    : "Spotify is temporarily unavailable. Try again shortly.";
                return _current = missing with { ConnectionHint = Status };
            }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (_disposed || generation != _generation) return missing with { ConnectionHint = Status };
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("device", out var device) || device.ValueKind != JsonValueKind.Object
                || !IsLocalDevice(device) || !device.TryGetProperty("volume_percent", out var volume) || volume.ValueKind != JsonValueKind.Number
                || !volume.TryGetDouble(out var level) || !double.IsFinite(level) || level is < 0 or > 100)
            {
                Status = "Select " + Environment.MachineName + " in Spotify's device menu. Account volume controls this PC only.";
                return _current = missing with { ConnectionHint = Status };
            }
            var id = "spotify-api:" + device.GetProperty("id").GetString();
            if (now < _holdUntil && _current.TargetId == id) return _current with { Source = media.Source };
            Status = "Account connected · 1% volume steps";
            return _current = missing with { Available = true, Volume = Math.Clamp(level, 0, 100), Muted = level == 0,
                TargetId = id, ConnectionHint = "Spotify account · " + Environment.MachineName };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !token.IsCancellationRequested)
        {
            // A disconnected or replaced account owns its new state even when
            // the old request completes with an error instead of a response.
            if (_disposed || generation != _generation) return missing with { ConnectionHint = Status };
            _nextRead = Environment.TickCount64 + 3000;
            Status = "Spotify is temporarily unavailable. Try again shortly.";
            return _current with { Source = media.Source, ConnectionHint = Status };
        }
        finally { _gate.Release(); }
    }

    internal async Task<bool> WriteAsync(PlayerVolumeSnapshot desired, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = operation.Token;
        await _gate.WaitAsync(token);
        try
        {
            if (_disposed || !IsConnected || !_current.Available || desired.TargetId != _current.TargetId || !double.IsFinite(desired.Volume)) return false;
            var level = desired.Muted ? 0 : (int)Math.Round(Math.Clamp(desired.Volume, 0, 100), MidpointRounding.AwayFromZero);
            if (_current.Volume == level && _current.Muted == (level == 0)) return true;
            var id = desired.TargetId["spotify-api:".Length..];
            var generation = _generation;
            _lastWriteStarted = Environment.TickCount64;
            using var response = await SendAsync(HttpMethod.Put, "https://api.spotify.com/v1/me/player/volume?volume_percent=" + level + "&device_id=" + Uri.EscapeDataString(id), token);
            if (response?.IsSuccessStatusCode != true || _disposed || generation != _generation) return false;
            _current = desired with { Volume = level, Muted = level == 0, Step = 1 };
            _holdUntil = Environment.TickCount64 + 1200;
            _nextRead = Environment.TickCount64 + 200;
            return true;
        }
        catch (HttpRequestException) { return false; }
        finally { _gate.Release(); }
    }

    internal void ObserveLocalVolume(double level, long reportedAt)
    {
        // Notifications originate from Spotify's real slider, not polling a
        // potentially frozen accessibility tree. Ignore delayed echoes of writes.
        if (!_current.Available || reportedAt < _lastWriteStarted || !double.IsFinite(level)) return;
        _current = _current with { Volume = Math.Clamp(level, 0, 100), Muted = level == 0 };
        _nextRead = Environment.TickCount64 + 1000;
    }

    private async Task<HttpResponseMessage?> SendAsync(HttpMethod method, string url, CancellationToken token)
    {
        var generation = _generation;
        if (Environment.TickCount64 < _retryAt || !await EnsureTokenAsync(token)) return null;
        if (_disposed || generation != _generation) return null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            var response = await _http.SendAsync(request, token);
            if (_disposed || generation != _generation) { response.Dispose(); return null; }
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose(); _accessToken = "";
                if (!await EnsureTokenAsync(token)) return null;
                continue;
            }
            if ((int)response.StatusCode == 429)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(5);
                _retryAt = Environment.TickCount64 + (long)Math.Clamp(delay.TotalMilliseconds, 1000, 86400000);
                Status = "Spotify asked Winisland to wait. Volume will reconnect automatically.";
                response.Dispose(); return null;
            }
            return response;
        }
        return null;
    }

    private async Task<bool> EnsureTokenAsync(CancellationToken token)
    {
        if (_accessToken.Length > 0 && Environment.TickCount64 < _expiresAt) return true;
        var credentials = _credentials;
        var generation = _generation;
        if (credentials is null) return false;
        using var response = await _http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = credentials.RefreshToken, ["client_id"] = credentials.ClientId
        }), token);
        if (!response.IsSuccessStatusCode)
        {
            if (_disposed || generation != _generation || _credentials != credentials) return false;
            if (response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _credentials = null; _accessToken = ""; _current = new();
                ++_generation;
                Status = "Spotify authorization expired. Connect your account again.";
                Changed?.Invoke(this, EventArgs.Empty);
            }
            _retryAt = Environment.TickCount64 + 5000;
            return false;
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (_disposed || generation != _generation || _credentials != credentials) return false;
        SetAccessToken(json.RootElement);
        if (json.RootElement.TryGetProperty("refresh_token", out var refreshed) && refreshed.ValueKind == JsonValueKind.String
            && refreshed.GetString() is { Length: > 0 } refresh)
        {
            credentials = credentials with { RefreshToken = refresh };
            Save(credentials); _credentials = credentials;
        }
        return true;
    }

    private void SetAccessToken(JsonElement root)
    {
        var (accessToken, expiresIn) = ReadToken(root);
        _accessToken = accessToken;
        _expiresAt = Environment.TickCount64 + Math.Max(1, expiresIn - 30) * 1000L;
    }

    private static (string AccessToken, int ExpiresIn) ReadToken(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("access_token", out var access)
            || access.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(access.GetString())
            || !root.TryGetProperty("expires_in", out var expires) || expires.ValueKind != JsonValueKind.Number
            || !expires.TryGetInt32(out var seconds) || seconds <= 0)
            throw new JsonException("Invalid Spotify authorization response.");
        return (access.GetString()!, seconds);
    }

    internal static async Task<string> ReceiveAuthorizationAsync(TcpListener listener, string state, CancellationToken token)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await using var stream = client.GetStream();
                var buffer = new byte[8192];
                var length = 0;
                string headers = "";
                while (length < buffer.Length)
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token);
                    if (count == 0) break;
                    length += count;
                    headers = Encoding.ASCII.GetString(buffer, 0, length);
                    if (headers.Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                var parts = headers.Split("\r\n", 2)[0].Split(' ');
                var query = new Dictionary<string, string>(StringComparer.Ordinal);
                var validPath = parts.Length == 3 && parts[0] == "GET" && parts[1].StartsWith("/spotify/callback/?", StringComparison.Ordinal);
                if (validPath)
                {
                    foreach (var pair in parts[1][(parts[1].IndexOf('?') + 1)..].Split('&'))
                    {
                        var keyValue = pair.Split('=', 2);
                        if (keyValue.Length == 2) query[Uri.UnescapeDataString(keyValue[0])] = Uri.UnescapeDataString(keyValue[1]);
                    }
                }
                var valid = validPath && query.TryGetValue("state", out var receivedState) && receivedState == state;
                var code = query.GetValueOrDefault("code", "");
                var accepted = valid && code.Length > 0 && !query.ContainsKey("error");
                var body = Encoding.UTF8.GetBytes("<!doctype html><title>Winisland Spotify</title><p>"
                    + (accepted ? "Spotify sign-in received. Return to Winisland to finish connecting." : "Spotify sign-in was not completed. Return to Winisland and try again.") + "</p>");
                var response = Encoding.ASCII.GetBytes("HTTP/1.1 " + (accepted ? "200 OK" : "400 Bad Request")
                    + "\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: " + body.Length + "\r\n\r\n");
                await stream.WriteAsync(response, timeout.Token);
                await stream.WriteAsync(body, timeout.Token);
                if (!valid) continue;
                if (!accepted) throw new InvalidOperationException("Spotify sign-in was canceled. You can try again whenever you're ready.");
                return code;
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException && !token.IsCancellationRequested) { }
        }
    }

    internal static bool IsLocalDevice(JsonElement device) => device.ValueKind == JsonValueKind.Object
        && device.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
        && !string.IsNullOrEmpty(id.GetString()) && device.TryGetProperty("name", out var name)
        && name.ValueKind == JsonValueKind.String
        && string.Equals(name.GetString(), Environment.MachineName, StringComparison.OrdinalIgnoreCase)
        && device.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && string.Equals(type.GetString(), "computer", StringComparison.OrdinalIgnoreCase)
        && device.TryGetProperty("is_restricted", out var restricted) && restricted.ValueKind == JsonValueKind.False
        && device.TryGetProperty("supports_volume", out var supports) && supports.ValueKind == JsonValueKind.True;

    private void Save(Credentials credentials)
    {
        Directory.CreateDirectory(SettingsStore.DirectoryPath);
        var temporary = CredentialPath + ".tmp";
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try { File.WriteAllBytes(temporary, WindowsCredentialProtection.Protect(plaintext, true)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        File.Move(temporary, CredentialPath, true);
    }

    private static string Query(Dictionary<string, string> values) => string.Join("&", values.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _http.Dispose(); _lifetime.Dispose();
        _accessToken = ""; _credentials = null;
    }
}
