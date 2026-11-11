using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Winisland.Services;

namespace Winisland;

internal static class PlayerVolumeVerification
{
    internal static async Task RunAsync(Dispatcher dispatcher, Action<bool, string> assert)
    {
        await VerifyProtocolAsync(assert);
        await VerifyHostAsync(assert);
        await VerifyServiceAsync(dispatcher, assert);
        await VerifyBrowserAsync(assert);
        await VerifyUnusedAccessibilityAsync(assert);
        await VerifyAccessibilityAsync(assert);
    }

    private static async Task VerifyProtocolAsync(Action<bool, string> assert)
    {
        using var frames = new MemoryStream();
        await NativeMessagingProtocol.WriteAsync(frames, "{\"type\":\"hello\"}", CancellationToken.None);
        frames.Position = 0;
        assert(await NativeMessagingProtocol.ReadAsync(frames, CancellationToken.None) == "{\"type\":\"hello\"}", "Native messaging round-trips UTF-8 with a four-byte length prefix");
        using var oversized = new MemoryStream(BitConverter.GetBytes(NativeMessagingProtocol.MaximumMessageBytes + 1));
        var refused = false;
        try { await NativeMessagingProtocol.ReadAsync(oversized, CancellationToken.None); } catch (InvalidDataException) { refused = true; }
        assert(refused, "Native messaging refuses oversized frames before allocating their payload");
        using var truncated = new MemoryStream(new byte[] { 4, 0, 0, 0, 123 });
        refused = false;
        try { await NativeMessagingProtocol.ReadAsync(truncated, CancellationToken.None); } catch (EndOfStreamException) { refused = true; }
        assert(refused, "Native messaging rejects truncated payloads");
        using var invalidUtf8 = new MemoryStream(new byte[] { 2, 0, 0, 0, 0xC3, 0x28 });
        refused = false;
        try { await NativeMessagingProtocol.ReadAsync(invalidUtf8, CancellationToken.None); } catch (DecoderFallbackException) { refused = true; }
        assert(refused, "Native messaging rejects malformed UTF-8 instead of accepting replacement characters");
        assert(BrowserNativeHost.IsHostInvocation(new[] { "chrome-extension://" + BrowserIntegration.ExtensionId + "/" }) && !BrowserNativeHost.IsHostInvocation(new[] { "chrome-extension://unknown/" }), "Only the configured browser extension origin selects native-host startup");
    }

    private static async Task VerifyServiceAsync(Dispatcher dispatcher, Action<bool, string> assert)
    {
        using var fixture = new FixturePlayerVolumeBackend();
        using var service = new PlayerVolumeService(dispatcher, new IPlayerVolumeBackend[] { fixture });
        service.SetSource(new MediaSnapshot { HasSession = true, Source = "fixture", Title = "first" });
        await service.WhenIdleAsync();
        assert(service.Current.Available && service.Current.Volume == 60, "Player volume reads the selected player's own backend");
        for (var value = 10.1; value < 83; value += 0.13) service.SetVolume(value);
        service.SetVolume(83.4);
        await service.WhenIdleAsync();
        assert(Math.Abs(fixture.Volume - 83.4) < .001 && fixture.Writes.Count <= 3, "Rapid volume input keeps fractional precision and coalesces to the final player value");
        assert(service.SetMuted(true), "Player mute queues through the player backend");
        await service.WhenIdleAsync();
        assert(fixture.Muted && service.Current.Muted, "Mute state round-trips through the player without changing its saved level");
        service.SetSource(new MediaSnapshot { HasSession = true, Source = "unavailable", Title = "other" });
        await service.WhenIdleAsync();
        assert(!service.Current.Available && !service.SetVolume(40) && !service.SetMuted(false), "Unsupported players disable volume writes instead of falling back to the Windows mixer");
        service.SetSource(new MediaSnapshot { HasSession = true, Source = "fixture", Title = "second" });
        await service.WhenIdleAsync();
        service.SetVolume(45);
        service.SetSource(new MediaSnapshot { HasSession = true, Source = "unavailable", Title = "third" });
        await service.WhenIdleAsync();
        assert(service.Current.Source == "unavailable" && !service.Current.Available, "A media-source change cancels queued volume work and ignores stale readback");
        assert(!service.SetVolume(double.NaN) && !service.SetVolume(double.PositiveInfinity), "Invalid volume values cannot enter the player write queue");
        service.SetSource(new MediaSnapshot { HasSession = true, Source = "fixture", Title = "Shared title", Artist = "First artist" });
        await service.WhenIdleAsync();
        var previousTarget = service.Current.TargetId;
        service.SetVolume(22);
        service.SetSource(new MediaSnapshot { HasSession = true, Source = "fixture", Title = "Shared title", Artist = "Second artist" });
        assert(!service.Current.Available && service.Current.TargetId != previousTarget, "An artist-only media change immediately invalidates the prior player target before refresh");
        await service.WhenIdleAsync();
        assert(service.Current.TargetId == "Shared title|Second artist" && !fixture.Writes.Contains(22), "An artist-only media change cancels queued writes and resolves the new target");

        using var spotify = new FixturePlayerVolumeBackend("Spotify.exe");
        using var spotifyService = new PlayerVolumeService(dispatcher, new IPlayerVolumeBackend[] { spotify });
        var track = new MediaSnapshot { HasSession = true, Source = "Spotify.exe", Title = "First track", IsPlaying = true };
        spotifyService.SetSource(track);
        await spotifyService.WhenIdleAsync();
        var target = spotifyService.Current.TargetId;
        spotifyService.SetVolume(53);
        spotifyService.SetSource(track with { Title = "Next track", Artist = "Another artist", IsPlaying = false });
        assert(spotifyService.Current.Available && spotifyService.Current.Volume == 53 && spotifyService.Current.TargetId == target,
            "Spotify track and pause changes retain the connected volume target and optimistic input");
        await spotifyService.WhenIdleAsync();
        assert(spotify.Volume == 53, "Spotify track changes do not cancel an in-flight volume adjustment");
        spotify.ReportVolume(47);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await WaitUntil(() => spotifyService.Current.Volume == 47, timeout.Token);
        assert(spotifyService.Current.Volume == 47, "External player volume notifications update the service without waiting for polling");
    }

    private static async Task VerifyHostAsync(Action<bool, string> assert)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WINISLAND_DATA_DIR")))
            throw new InvalidOperationException("Native-host verification requires an isolated data directory.");
        foreach (var fault in new[] { "disconnect", "invalid-json", "invalid-utf8" })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var pipe = new NamedPipeServerStream(BrowserVolumeBridge.PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(PlayerVolumeVerification).Assembly.GetName().Name + ".dll"));
            start.ArgumentList.Add("--browser-host");
            using var host = Process.Start(start)!;
            try
            {
                await pipe.WaitForConnectionAsync(timeout.Token);
                await NativeMessagingProtocol.WriteAsync(host.StandardInput.BaseStream, "{\"type\":\"hello\",\"browser\":\"chrome\"}", timeout.Token);
                var hello = await NativeMessagingProtocol.ReadAsync(pipe, timeout.Token);
                await NativeMessagingProtocol.WriteAsync(pipe, "{\"type\":\"ready\",\"protocol\":1}", timeout.Token);
                var ready = await NativeMessagingProtocol.ReadAsync(host.StandardOutput.BaseStream, timeout.Token);
                if (fault == "disconnect")
                    assert(hello?.Contains("\"hello\"") == true && ready?.Contains("\"ready\"") == true,
                        "A real native-host process relays a framed browser/app handshake");
                var elapsed = Stopwatch.StartNew();
                if (fault == "disconnect") pipe.Dispose();
                else if (fault == "invalid-json")
                    await NativeMessagingProtocol.WriteAsync(host.StandardInput.BaseStream, "{", timeout.Token);
                else
                {
                    await host.StandardInput.BaseStream.WriteAsync(new byte[] { 2, 0, 0, 0, 0xC3, 0x28 }, timeout.Token);
                    await host.StandardInput.BaseStream.FlushAsync(timeout.Token);
                }
                var exited = true;
                try { await host.WaitForExitAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) { exited = false; }
                var quiet = exited && (await host.StandardError.ReadToEndAsync()).Length == 0
                    && (await host.StandardOutput.ReadToEndAsync()).Length == 0;
                assert(exited && elapsed.ElapsedMilliseconds < 2000 && quiet, fault switch
                {
                    "disconnect" => "The real native host exits quietly after app disconnection while browser stdin remains open",
                    "invalid-json" => "The real native host rejects malformed JSON and exits quietly without waiting for browser stdin",
                    _ => "The real native host rejects malformed UTF-8 and exits quietly without waiting for browser stdin"
                });
            }
            finally
            {
                if (!host.HasExited)
                {
                    // This process was created only for verification; it never
                    // launches or owns the user's browser or media application.
                    host.Kill();
                    await host.WaitForExitAsync();
                }
            }
        }
    }

    private static async Task VerifyBrowserAsync(Action<bool, string> assert)
    {
        using var bridge = new BrowserVolumeBridge();
        using var connection = new NamedPipeClientStream(".", BrowserVolumeBridge.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await connection.ConnectAsync(timeout.Token);
        await NativeMessagingProtocol.WriteAsync(connection, "{\"type\":\"hello\",\"browser\":\"chrome\"}", timeout.Token);
        var ready = await NativeMessagingProtocol.ReadAsync(connection, timeout.Token);
        assert(ready?.Contains("\"ready\"") == true, "The browser bridge confirms a working local handshake");
        await SendState("1:0:1", "Matching title", 61.2);
        await SendState("2:0:1", "A different title", 92);
        await WaitUntil(() => bridge.PlayerCount == 2, timeout.Token);
        var media = new MediaSnapshot { HasSession = true, Source = "Chrome", Title = "Matching title", IsPlaying = true };
        var selected = await bridge.ReadAsync(media, timeout.Token);
        assert(selected.Available && Math.Abs(selected.Volume - 61.2) < .001, "Browser volume selects the exact matching media title among multiple tabs");
        await SendState("unicode:0:1", new string('x', 511) + "\U0001F600", 70);
        await WaitUntil(() => bridge.PlayerCount == 3, timeout.Token);
        assert((await bridge.ReadAsync(media, timeout.Token)).Available,
            "Clipping a long Unicode browser title cannot disable other valid player targets");
        await NativeMessagingProtocol.WriteAsync(connection, "{\"type\":\"remove\",\"id\":\"unicode:0:1\"}", timeout.Token);
        await WaitUntil(() => bridge.PlayerCount == 2, timeout.Token);
        var write = bridge.WriteAsync(selected with { Volume = 38.75, Muted = false }, timeout.Token);
        var command = JsonDocument.Parse((await NativeMessagingProtocol.ReadAsync(connection, timeout.Token))!);
        var request = command.RootElement.GetProperty("requestId").GetString();
        assert(command.RootElement.GetProperty("id").GetString() == "1:0:1" && command.RootElement.GetProperty("volume").GetDouble() == 38.75, "A browser command targets one player and preserves fractional volume");
        await NativeMessagingProtocol.WriteAsync(connection, JsonSerializer.Serialize(new { type = "result", requestId = request, id = "1:0:1", success = true, volume = 38.75, muted = false }), timeout.Token);
        assert(await write, "Browser volume confirms command completion with exact player readback");
        var other = await bridge.ReadAsync(media with { Title = "A different title" }, timeout.Token);
        assert(other.Volume == 92, "Changing one browser player's volume preserves another tab's volume");
        var missing = await bridge.ReadAsync(media with { Title = "A missing title" }, timeout.Token);
        assert(!missing.Available, "A mismatched media title cannot silently control another browser tab");
        await SendState("3:0:1", "Shared title", 55, false);
        await SendState("4:0:1", "Shared title", 85, true);
        await WaitUntil(() => bridge.PlayerCount == 4, timeout.Token);
        var paused = await bridge.ReadAsync(media with { Title = "Shared title", IsPlaying = false }, timeout.Token);
        assert(paused.Available && paused.Volume == 55, "A paused selected title targets the paused browser player rather than another playing duplicate");
        await SendState("5:0:1", "Shared title", 65, true, "Other artist");
        await WaitUntil(() => bridge.PlayerCount == 5, timeout.Token);
        var artistMatch = await bridge.ReadAsync(media with { Title = "Shared title", Artist = "Other artist" }, timeout.Token);
        assert(artistMatch.Available && artistMatch.Volume == 65,
            "Duplicate browser titles select a unique artist before comparing playback state");
        var artistPlayback = await bridge.ReadAsync(media with { Title = "Shared title", Artist = "Fixture" }, timeout.Token);
        assert(artistPlayback.Available && artistPlayback.Volume == 85,
            "Duplicate title and artist metadata select playback state only within the artist matches");
        assert(!(await bridge.ReadAsync(media with { Title = "Shared title", Artist = "Unknown artist" }, timeout.Token)).Available,
            "Ambiguous playing browser tabs remain unavailable when artist metadata cannot disambiguate them");
        await NativeMessagingProtocol.WriteAsync(connection, "{\"type\":\"remove\",\"id\":\"5:0:1\"}", timeout.Token);
        await WaitUntil(() => bridge.PlayerCount == 4, timeout.Token);
        await NativeMessagingProtocol.WriteAsync(connection, "{\"type\":\"remove\",\"id\":\"1:0:1\"}", timeout.Token);
        await WaitUntil(() => bridge.PlayerCount == 3, timeout.Token);
        assert(!(await bridge.ReadAsync(media, timeout.Token)).Available, "Removed browser players immediately stop accepting volume commands");
        connection.Dispose();
        await WaitUntil(() => bridge.PlayerCount == 0, timeout.Token);
        assert(!bridge.IsConnected, "Closing a native browser connection removes its cached players");

        Task SendState(string id, string title, double volume, bool playing = true, string artist = "Fixture") => NativeMessagingProtocol.WriteAsync(connection,
            JsonSerializer.Serialize(new { type = "state", id, title, artist, volume, muted = false, playing, duration = 180, position = 12 }), timeout.Token);
    }

    private static async Task VerifyUnusedAccessibilityAsync(Action<bool, string> assert)
    {
        using var adapter = new SpotifyPlayerVolume();
        assert(!adapter.AutomationStartedForTest && !adapter.AutomationThreadAliveForTest,
            "Constructing the unused Spotify backend creates no dedicated accessibility thread");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await adapter.ReadAsync(new MediaSnapshot { HasSession = true, Source = "Spotify.exe" }, cancelled.Token); }
        catch (OperationCanceledException) { }
        assert(!adapter.AutomationStartedForTest,
            "A cancelled first Spotify read does not start an accessibility worker");
        adapter.Dispose();
        var refused = false;
        try { await adapter.ReadAsync(new MediaSnapshot { HasSession = true, Source = "Spotify.exe" }, CancellationToken.None); }
        catch (ObjectDisposedException) { refused = true; }
        assert(refused && !adapter.AutomationStartedForTest && !adapter.AutomationThreadAliveForTest,
            "Disposing an unused Spotify backend prevents later reads from resurrecting its thread");
    }

    private static async Task VerifyAccessibilityAsync(Action<bool, string> assert)
    {
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = .62, Width = 200, Height = 32 };
        AutomationProperties.SetName(slider, "Volym");
        var window = new Window { Title = "Winisland player volume verification", Width = 260, Height = 100, ShowInTaskbar = false, Content = slider };
        window.Show();
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        try
        {
            var providerThread = 0;
            var providerApartment = ApartmentState.Unknown;
            using var adapter = new SpotifyPlayerVolume(() =>
            {
                providerThread = Environment.CurrentManagedThreadId;
                providerApartment = Thread.CurrentThread.GetApartmentState();
                return AutomationElement.FromHandle(handle);
            });
            var media = new MediaSnapshot { HasSession = true, Source = "Spotify.exe", Title = "Fixture" };
            var snapshot = await adapter.ReadAsync(media, CancellationToken.None);
            assert(adapter.AutomationStartedForTest && adapter.AutomationThreadAliveForTest
                && providerThread != Environment.CurrentManagedThreadId && providerApartment == ApartmentState.STA,
                "First actual Spotify use starts its dedicated STA worker away from the UI thread");
            assert(snapshot.Available && Math.Abs(snapshot.Volume - 62) < .01, "Spotify's adapter recognizes a localized native volume accessibility slider");
            assert(await adapter.WriteAsync(snapshot with { Volume = 36.4 }, CancellationToken.None), "Spotify's adapter writes through the player's RangeValue pattern");
            assert(Math.Abs(slider.Value - .364) < .001, "Native player writes change the actual slider rather than an app audio session");
            var readback = await adapter.ReadAsync(media, CancellationToken.None);
            assert(Math.Abs(readback.Volume - 36.4) < .01, "Native in-player volume provides precise slider readback");
            using var service = new PlayerVolumeService(window.Dispatcher, new IPlayerVolumeBackend[] { adapter });
            service.SetSource(media);
            await service.WhenIdleAsync();
            slider.Value = .517;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await WaitUntil(() => Math.Abs(service.Current.Volume - 51.7) < .01, timeout.Token);
            assert(Math.Abs(service.Current.Volume - 51.7) < .01,
                "Real accessibility volume events reach the notch service without a polling tick");
            service.Dispose();
            adapter.Dispose();
            await WaitUntil(() => !adapter.AutomationThreadAliveForTest, timeout.Token);
            assert(!adapter.AutomationThreadAliveForTest,
                "Disposing a used Spotify backend removes its accessibility handler and ends the dedicated worker");
        }
        finally { window.Close(); }
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition()) await Task.Delay(15, cancellationToken);
    }

    internal sealed class FixturePlayerVolumeBackend : IPlayerVolumeBackend, IPlayerVolumeNotifications, IDisposable
    {
        private readonly string _source;
        internal FixturePlayerVolumeBackend(string source = "fixture") => _source = source;
        public event EventHandler? Changed;
        internal double Volume { get; private set; } = 60;
        internal bool Muted { get; private set; }
        internal List<double> Writes { get; } = new();
        public bool Supports(MediaSnapshot media) => media.HasSession && media.Source == _source;
        public Task<PlayerVolumeSnapshot> ReadAsync(MediaSnapshot media, CancellationToken cancellationToken) => Task.FromResult(new PlayerVolumeSnapshot { Available = true, Volume = Volume, Muted = Muted, Source = media.Source, Player = "Fixture player", TargetId = _source == "Spotify.exe" ? "spotify-fixture" : media.Title + "|" + media.Artist });
        internal void ReportVolume(double volume) { Volume = volume; Changed?.Invoke(this, EventArgs.Empty); }
        public async Task<bool> WriteAsync(PlayerVolumeSnapshot desired, CancellationToken cancellationToken)
        {
            await Task.Delay(25, cancellationToken);
            Volume = desired.Volume; Muted = desired.Muted; Writes.Add(Volume); return true;
        }
        public void Dispose() { }
    }
}
