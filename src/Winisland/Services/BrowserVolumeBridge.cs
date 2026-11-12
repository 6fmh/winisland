using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Winisland.Services;

public sealed class BrowserVolumeBridge : IPlayerVolumeBackend, IPlayerVolumeNotifications, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, BrowserClient> _clients = new();
    private readonly Dictionary<string, BrowserPlayer> _players = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _listener;
    private bool _disposed;
    private static readonly Regex SafeId = new(@"\A[A-Za-z0-9:_-]{1,96}\z", RegexOptions.CultureInvariant);
    private static readonly (string Source, string Browser)[] BrowserSources =
        { ("comet", "comet"), ("perplexity", "comet"), ("chrome", "chrome"), ("msedge", "edge"), ("brave", "brave"), ("vivaldi", "vivaldi"), ("opera", "opera") };
    public static string PipeName
    {
        get
        {
            using var windowsIdentity = WindowsIdentity.GetCurrent();
            var identity = windowsIdentity.User?.Value ?? Environment.UserName;
            var dataPath = Path.GetFullPath(SettingsStore.DirectoryPath).ToUpperInvariant();
            return "Winisland.Player." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + "|" + dataPath)))[..24];
        }
    }
    public bool IsConnected { get { lock (_gate) return _clients.Values.Any(c => c.Browser.Length > 0); } }
    internal int PlayerCount { get { lock (_gate) { Prune(); return _players.Count; } } }
    public event EventHandler? Changed;

    public BrowserVolumeBridge() => _listener = Task.Run(ListenAsync);
    public bool Supports(MediaSnapshot media) => media.HasSession && IsBrowserSource(media.Source);
    public static bool IsBrowserSource(string source) => BrowserNameForSource(source).Length > 0;

    internal static string BrowserNameForSource(string source)
    {
        var value = source.Trim();
        foreach (var entry in BrowserSources)
            if (value.Equals(entry.Source, StringComparison.OrdinalIgnoreCase)
                || value.Length > entry.Source.Length && value[entry.Source.Length] == '.'
                    && value.StartsWith(entry.Source, StringComparison.OrdinalIgnoreCase)) return entry.Browser;
        if (value.StartsWith("Microsoft.MicrosoftEdge_", StringComparison.OrdinalIgnoreCase)) return "edge";
        return "";
    }

    public Task<PlayerVolumeSnapshot> ReadAsync(MediaSnapshot media, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var browserName = BrowserNameForSource(media.Source);
        var missing = new PlayerVolumeSnapshot { Source = media.Source, Player = BrowserDisplayName(browserName),
            ConnectionHint = IsConnected ? "The browser has not reported this media player yet." : "Connect the browser companion to control this player's volume." };
        if (!media.HasSession || browserName.Length == 0 || _disposed) return Task.FromResult(missing);
        lock (_gate)
        {
            Prune();
            // Comet can publish Chromium's generic Chrome SMTC identifier.
            var title = NormalizeTitle(media.Title);
            var artist = NormalizeTitle(media.Artist);
            var candidateCount = 0; var matchingCount = 0; var artistCount = 0;
            var playingCount = 0; var artistPlayingCount = 0;
            BrowserPlayer? candidate = null, matching = null, byArtist = null, playing = null, artistPlaying = null;
            foreach (var entry in _players.Values)
            {
                if (entry.Client.Browser != browserName && !(browserName == "chrome" && entry.Client.Browser == "comet")) continue;
                candidate = entry; candidateCount++;
                if (title.Length == 0 || entry.Title != title) continue;
                matching = entry; matchingCount++;
                if (entry.Playing == media.IsPlaying) { playing = entry; playingCount++; }
                if (artist.Length == 0 || entry.Artist != artist) continue;
                byArtist = entry; artistCount++;
                if (entry.Playing == media.IsPlaying) { artistPlaying = entry; artistPlayingCount++; }
            }
            BrowserPlayer? player = null;
            if (matchingCount == 1) player = matching;
            else if (matchingCount > 1)
            {
                if (artistCount == 1) player = byArtist;
                else if (artistCount > 0 && artistPlayingCount == 1) player = artistPlaying;
                else if (artistCount == 0 && playingCount == 1) player = playing;
            }
            // Unknown metadata is usable only when exactly one player exists.
            // A known mismatched title never controls an arbitrary active tab.
            else if (candidateCount == 1 && title is "" or "untitled media" or "not playing") player = candidate;
            if (player is null) return Task.FromResult(missing);
            return Task.FromResult(new PlayerVolumeSnapshot { Available = true, Source = media.Source, Player = BrowserDisplayName(player.Client.Browser),
                TargetId = player.Key, Volume = player.Volume, Muted = player.Muted, ConnectionHint = "Website player volume" });
        }
    }

    public async Task<bool> WriteAsync(PlayerVolumeSnapshot desired, CancellationToken cancellationToken)
    {
        if (_disposed || !double.IsFinite(desired.Volume)) return false;
        BrowserPlayer? player;
        lock (_gate) { Prune(); _players.TryGetValue(desired.TargetId, out player); }
        if (player is null) return false;
        var requestId = Guid.NewGuid().ToString("N");
        var pending = new PendingWrite(player.Id, Math.Clamp(desired.Volume, 0, 100), desired.Muted);
        if (!player.Client.Pending.TryAdd(requestId, pending)) return false;
        try
        {
            await player.Client.SendAsync(JsonSerializer.Serialize(new { type = "command", id = player.Id, requestId, volume = pending.Volume, muted = desired.Muted }), cancellationToken);
            return await pending.Completion.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException) { return false; }
        finally { player.Client.Pending.TryRemove(requestId, out _); }
    }

    private async Task ListenAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 8192, 8192);
                await pipe.WaitForConnectionAsync(_lifetime.Token);
                lock (_gate)
                {
                    if (_clients.Count >= 8) { pipe.Dispose(); pipe = null; continue; }
                }
                var client = new BrowserClient(pipe);
                pipe = null;
                lock (_gate) _clients.Add(client.Key, client);
                _ = RunClientAsync(client);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                if (!_lifetime.IsCancellationRequested) await Task.Delay(1500, _lifetime.Token).ConfigureAwait(false);
            }
            finally { pipe?.Dispose(); }
        }
    }

    private async Task RunClientAsync(BrowserClient client)
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var text = await NativeMessagingProtocol.ReadAsync(client.Pipe, _lifetime.Token);
                if (text is null) break;
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)) continue;
                var kind = type.GetString();
                if (kind == "hello")
                {
                    var name = GetText(root, "browser", 24).ToLowerInvariant();
                    if (name is not ("chrome" or "comet" or "edge" or "brave" or "vivaldi" or "opera")) continue;
                    lock (_gate) client.Browser = name;
                    await client.SendAsync("{\"type\":\"ready\",\"protocol\":1}", _lifetime.Token);
                    Changed?.Invoke(this, EventArgs.Empty);
                }
                else if (client.Browser.Length > 0 && kind == "state") ReceiveState(client, root);
                else if (client.Browser.Length > 0 && kind == "remove")
                {
                    var id = GetText(root, "id", 96);
                    lock (_gate) _players.Remove(client.Key + "|" + id);
                    Changed?.Invoke(this, EventArgs.Empty);
                }
                else if (kind == "result") ReceiveResult(client, root);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or DecoderFallbackException or OperationCanceledException or InvalidOperationException or ObjectDisposedException) { }
        finally
        {
            lock (_gate)
            {
                _clients.Remove(client.Key);
                foreach (var entry in _players)
                    if (entry.Value.Client == client) _players.Remove(entry.Key);
            }
            foreach (var pending in client.Pending.Values) pending.Completion.TrySetResult(false);
            client.Dispose();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ReceiveState(BrowserClient client, JsonElement root)
    {
        var id = GetText(root, "id", 96);
        if (!SafeId.IsMatch(id) || !TryVolume(root, out var volume)) return;
        var key = client.Key + "|" + id;
        lock (_gate)
        {
            Prune();
            if (!_players.ContainsKey(key) && (_players.Count >= 128 || _players.Values.Count(p => p.Client == client) >= 64)) return;
            // Normalize once when metadata arrives rather than for every
            // candidate on every active volume refresh. Keep only this form.
            var title = NormalizeTitle(GetText(root, "title", 512));
            var artist = NormalizeTitle(GetText(root, "artist", 256));
            var muted = GetBool(root, "muted");
            var playing = GetBool(root, "playing");
            var seen = Environment.TickCount64;
            if (_players.TryGetValue(key, out var existing) && existing.Title == title && existing.Artist == artist
                && existing.Volume == volume && existing.Muted == muted && existing.Playing == playing)
            {
                // Heartbeats keep the target live without waking UI consumers
                // or replacing unchanged player data. Seen is guarded by _gate.
                existing.Seen = seen;
                return;
            }
            _players[key] = new BrowserPlayer(key, id, client, title, artist, volume, muted, playing, seen);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ReceiveResult(BrowserClient client, JsonElement root)
    {
        var requestId = GetText(root, "requestId", 40);
        if (!client.Pending.TryGetValue(requestId, out var pending) || GetText(root, "id", 96) != pending.Id) return;
        var success = GetBool(root, "success") && TryVolume(root, out var volume) && GetBool(root, "muted") == pending.Muted;
        if (success)
        {
            var readback = root.GetProperty("volume").GetDouble();
            // YouTube's API uses integer percentages; HTML5 retains fractions.
            success = pending.Muted || Math.Abs(readback - pending.Volume) <= 1.01;
            lock (_gate)
            {
                var key = client.Key + "|" + pending.Id;
                if (_players.TryGetValue(key, out var player)) _players[key] = player with { Volume = readback, Muted = pending.Muted, Seen = Environment.TickCount64 };
                else success = false;
            }
        }
        pending.Completion.TrySetResult(success);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Prune()
    {
        var now = Environment.TickCount64;
        // Dictionary removal preserves enumeration validity on .NET 8.
        foreach (var entry in _players)
            if (now - entry.Value.Seen > 30000) _players.Remove(entry.Key);
    }
    private static bool TryVolume(JsonElement root, out double volume)
    {
        volume = 0;
        return root.TryGetProperty("volume", out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out volume)
            && double.IsFinite(volume) && volume is >= 0 and <= 100;
    }
    private static string GetText(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return "";
        var text = element.GetString() ?? "";
        if (text.Length <= maximum) return text;
        var length = char.IsHighSurrogate(text[maximum - 1]) ? maximum - 1 : maximum;
        return text[..length];
    }
    private static bool GetBool(JsonElement root, string name) => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.True;
    internal static string NormalizeTitle(string value)
    {
        var text = Regex.Replace(value.Normalize(NormalizationForm.FormKC).Trim(), @"^\(\d+\)\s*", "");
        text = Regex.Replace(text, @"\s*[-|]\s*(YouTube|Spotify|Comet|Perplexity)\s*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(text, @"\s+", " ").ToLowerInvariant();
    }
    private static string BrowserDisplayName(string name) => name switch { "comet" => "Comet", "edge" => "Edge", "brave" => "Brave", "vivaldi" => "Vivaldi", "opera" => "Opera", _ => "Chrome" };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel();
        lock (_gate) foreach (var client in _clients.Values) client.Pipe.Dispose();
        // The listener and client tasks finish on cancellation; no dispatcher
        // thread waits on a browser process during shutdown.
    }

    private sealed record BrowserPlayer(string Key, string Id, BrowserClient Client, string Title, string Artist, double Volume, bool Muted, bool Playing, long Seen)
    {
        public long Seen { get; set; } = Seen;
    }
    private sealed class PendingWrite
    {
        public string Id { get; }
        public double Volume { get; }
        public bool Muted { get; }
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PendingWrite(string id, double volume, bool muted) { Id = id; Volume = volume; Muted = muted; }
    }
    private sealed class BrowserClient : IDisposable
    {
        public string Key { get; } = Guid.NewGuid().ToString("N");
        public string Browser { get; set; } = "";
        public NamedPipeServerStream Pipe { get; }
        public ConcurrentDictionary<string, PendingWrite> Pending { get; } = new();
        private readonly SemaphoreSlim _writer = new(1, 1);
        public BrowserClient(NamedPipeServerStream pipe) => Pipe = pipe;
        public async Task SendAsync(string message, CancellationToken cancellationToken)
        {
            await _writer.WaitAsync(cancellationToken);
            try { await NativeMessagingProtocol.WriteAsync(Pipe, message, cancellationToken); }
            finally { _writer.Release(); }
        }
        public void Dispose() { Pipe.Dispose(); }
    }
}

internal static class NativeMessagingProtocol
{
    internal const int MaximumMessageBytes = 64 * 1024;
    internal static async Task<string?> ReadAsync(Stream input, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var first = await input.ReadAsync(header.AsMemory(0, 4), cancellationToken);
        if (first == 0) return null;
        await ReadRemaining(input, header, first, cancellationToken);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumMessageBytes) throw new InvalidDataException("Invalid native messaging frame length.");
        var bytes = new byte[length];
        await ReadRemaining(input, bytes, 0, cancellationToken);
        return new UTF8Encoding(false, true).GetString(bytes);
    }
    private static async Task ReadRemaining(Stream input, byte[] bytes, int offset, CancellationToken cancellationToken)
    {
        while (offset < bytes.Length)
        {
            var read = await input.ReadAsync(bytes.AsMemory(offset), cancellationToken);
            if (read == 0) throw new EndOfStreamException("Truncated native messaging frame.");
            offset += read;
        }
    }
    internal static async Task WriteAsync(Stream output, string message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        if (bytes.Length <= 0 || bytes.Length > MaximumMessageBytes) throw new InvalidDataException("Invalid native messaging frame length.");
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await output.WriteAsync(header, cancellationToken);
        await output.WriteAsync(bytes, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }
}
