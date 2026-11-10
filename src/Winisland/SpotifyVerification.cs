using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Winisland.Controls;
using Winisland.Services;

namespace Winisland;

internal static class SpotifyVerification
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        VerifyFreshProfileDisconnect(assert);
        await VerifyCallbackAsync(assert);
        var media = new MediaSnapshot { HasSession = true, Source = "Spotify.exe", Title = "Fixture track" };
        using var handler = new SpotifyHandler();
        using var account = new SpotifyAccount(new HttpClient(handler), false);
        account.ConnectForTest(new string('a', 32), "fixture-refresh-token");
        var snapshot = await account.ReadAsync(media, CancellationToken.None);
        assert(snapshot.Available && snapshot.Volume == 62 && snapshot.Step == 1 && handler.Refreshes == 1,
            "Spotify account refreshes authorization and reads 1% player volume from this PC");
        assert(await account.WriteAsync(snapshot with { Volume = 53.4 }, CancellationToken.None)
            && handler.Commands.Count == 1 && handler.Commands[0].Contains("volume_percent=53")
            && handler.Commands[0].Contains("device_id=pc%2Fdevice%3F1"),
            "Spotify API volume preserves 1% precision and targets the exact encoded device ID");
        var optimistic = await account.ReadAsync(media, CancellationToken.None);
        assert(optimistic.Volume == 53, "Spotify command feedback survives delayed API playback readback");
        assert(await account.WriteAsync(snapshot with { Volume = 53.45 }, CancellationToken.None) && handler.Commands.Count == 1,
            "Fractional pointer motion inside the same Spotify percentage does not send redundant API commands");
        account.ObserveLocalVolume(47, Environment.TickCount64 + 1);
        assert((await account.ReadAsync(media, CancellationToken.None)).Volume == 47,
            "Local Spotify notifications immediately update account volume during API readback delay");
        assert(!await account.WriteAsync(snapshot with { TargetId = "spotify-api:another-device" }, CancellationToken.None)
            && handler.Commands.Count == 1, "Spotify account rejects commands for an obsolete device target");

        using var remoteHandler = new SpotifyHandler { DeviceName = "Another computer" };
        using var remote = new SpotifyAccount(new HttpClient(remoteHandler), false);
        remote.ConnectForTest(new string('a', 32), "fixture-refresh-token");
        var remoteSnapshot = await remote.ReadAsync(media, CancellationToken.None);
        assert(!remoteSnapshot.Available && !await remote.WriteAsync(remoteSnapshot with { Volume = 40 }, CancellationToken.None)
            && remoteHandler.Commands.Count == 0, "Account connection refuses to change Spotify on another computer");

        using var restrictedHandler = new SpotifyHandler { Restricted = true };
        using var restricted = new SpotifyAccount(new HttpClient(restrictedHandler), false);
        restricted.ConnectForTest(new string('a', 32), "fixture-refresh-token");
        assert(!(await restricted.ReadAsync(media, CancellationToken.None)).Available,
            "Spotify devices that restrict API controls remain disabled");

        using var expiredHandler = new SpotifyHandler { UnauthorizedOnce = true };
        using var expired = new SpotifyAccount(new HttpClient(expiredHandler), false);
        expired.ConnectForTest(new string('a', 32), "fixture-refresh-token");
        assert((await expired.ReadAsync(media, CancellationToken.None)).Available && expiredHandler.Refreshes == 2,
            "Spotify retries a revoked access token once with refreshed authorization");

        using var rateHandler = new SpotifyHandler { RateLimited = true };
        using var limited = new SpotifyAccount(new HttpClient(rateHandler), false);
        limited.ConnectForTest(new string('a', 32), "fixture-refresh-token");
        await limited.ReadAsync(media, CancellationToken.None);
        await Task.Delay(1050);
        await limited.ReadAsync(media, CancellationToken.None);
        assert(rateHandler.PlaybackReads == 1 && limited.Status.Contains("wait", StringComparison.OrdinalIgnoreCase),
            "Spotify Retry-After stops requests across subsequent refreshes");

        using var revokedHandler = new SpotifyHandler { RefreshRejected = true };
        using var revoked = new SpotifyAccount(new HttpClient(revokedHandler), false);
        revoked.ConnectForTest(new string('a', 32), "fixture-refresh-token");
        assert(!(await revoked.ReadAsync(media, CancellationToken.None)).Available && !revoked.IsConnected,
            "Revoked Spotify refresh credentials require reconnection instead of retrying forever");

        foreach (var malformed in new[] { "[]", "{\"device\":null}",
            JsonSerializer.Serialize(new { device = new { id = "pc", name = 42, type = "computer", is_restricted = false, supports_volume = true, volume_percent = 62 } }),
            JsonSerializer.Serialize(new { device = new { id = "pc", name = Environment.MachineName, type = "computer", is_restricted = false, supports_volume = true, volume_percent = (int?)null } }),
            JsonSerializer.Serialize(new { device = new { id = "pc", name = Environment.MachineName, type = "computer", is_restricted = false, supports_volume = true, volume_percent = 101 } }) })
        {
            using var malformedHandler = new SpotifyHandler { PlaybackJson = malformed };
            using var malformedAccount = new SpotifyAccount(new HttpClient(malformedHandler), false);
            malformedAccount.ConnectForTest(new string('a', 32), "fixture-refresh-token");
            var malformedSnapshot = await malformedAccount.ReadAsync(media, CancellationToken.None);
            assert(!malformedSnapshot.Available && !await malformedAccount.WriteAsync(malformedSnapshot with { Volume = 40 }, CancellationToken.None)
                && malformedHandler.Commands.Count == 0, "Malformed Spotify device/volume fields fail closed without sending a volume command");
        }

        using var invalidTokenHandler = new SpotifyHandler { TokenJson = "{\"access_token\":null,\"expires_in\":3600}" };
        using var invalidTokenAccount = new SpotifyAccount(new HttpClient(invalidTokenHandler), false);
        invalidTokenAccount.ConnectForTest(new string('a', 32), "fixture-refresh-token");
        assert(!(await invalidTokenAccount.ReadAsync(media, CancellationToken.None)).Available && invalidTokenHandler.PlaybackReads == 0,
            "Malformed Spotify token responses cannot authorize playback requests");

        using var delayedHandler = new SpotifyHandler { DelayedRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var disconnectedAccount = new SpotifyAccount(new HttpClient(delayedHandler), false);
        disconnectedAccount.ConnectForTest(new string('a', 32), "fixture-refresh-token");
        var pendingRefresh = disconnectedAccount.ReadAsync(media, CancellationToken.None);
        await delayedHandler.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        disconnectedAccount.Disconnect();
        delayedHandler.DelayedRefresh.SetResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        assert(!(await pendingRefresh).Available && !disconnectedAccount.IsConnected && disconnectedAccount.Status == "Account disconnected",
            "An expired in-flight refresh cannot overwrite account state after disconnection");

        using var protectedHandler = new SpotifyHandler { RotateRefreshToken = true };
        using var protectedAccount = new SpotifyAccount(new HttpClient(protectedHandler), false);
        protectedAccount.ConnectForTest(new string('a', 32), "fixture-refresh-token");
        await protectedAccount.ReadAsync(media, CancellationToken.None);
        var path = System.IO.Path.Combine(SettingsStore.DirectoryPath, "spotify-account.bin");
        var stored = System.Text.Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(path));
        using var restored = new SpotifyAccount(new HttpClient(new SpotifyHandler()), true);
        assert(restored.IsConnected && restored.ClientId == new string('a', 32) && !stored.Contains("rotated-fixture-token"),
            "Rotated Spotify credentials persist with Windows encryption and reconnect after restart");
        protectedAccount.Disconnect();
        assert(!protectedAccount.IsConnected && !System.IO.File.Exists(path), "Disconnecting Spotify removes its protected account credential");
    }

    private static void VerifyFreshProfileDisconnect(Action<bool, string> assert)
    {
        var previousDataDirectory = Environment.GetEnvironmentVariable("WINISLAND_DATA_DIR");
        var missingDirectory = System.IO.Path.Combine(SettingsStore.DirectoryPath, "missing-spotify-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("WINISLAND_DATA_DIR", missingDirectory);
            using var account = new SpotifyAccount(new HttpClient(new SpotifyHandler()), false);
            account.ConnectForTest(new string('a', 32), "fixture-refresh-token");
            account.Disconnect();
            assert(!account.IsConnected && account.Status == "Account disconnected" && !System.IO.Directory.Exists(missingDirectory),
                "Disconnecting Spotify on a fresh profile succeeds without creating a credential directory");
        }
        finally { Environment.SetEnvironmentVariable("WINISLAND_DATA_DIR", previousDataDirectory); }
    }

    private static async Task VerifyCallbackAsync(Action<bool, string> assert)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var origin = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var callback = SpotifyAccount.ReceiveAuthorizationAsync(listener, "fixture-state", timeout.Token);
        using var http = new HttpClient();
        using var invalid = await http.GetAsync(origin + "/spotify/callback/?state=wrong-state&code=wrong", timeout.Token);
        assert(invalid.StatusCode == HttpStatusCode.BadRequest && !callback.IsCompleted,
            "Spotify loopback sign-in rejects mismatched OAuth state and waits for the real browser callback");
        using var valid = await http.GetAsync(origin + "/spotify/callback/?state=fixture-state&code=" + Uri.EscapeDataString("code+with/slash?="), timeout.Token);
        assert(valid.IsSuccessStatusCode && await callback == "code+with/slash?=",
            "Spotify loopback callback receives an encoded authorization code without administrator rights");
    }

    internal static async Task RunNotchAsync(App app, Action<bool, string> assert)
    {
        using var media = new MediaService(app.Dispatcher);
        using var audio = new AudioService();
        using var focus = new FocusTimer();
        using var player = new PlayerVolumeService(app.Dispatcher, new IPlayerVolumeBackend[] { new PlayerVolumeVerification.FixturePlayerVolumeBackend() });
        var settings = new SettingsStore();
        settings.Update(new IslandSettings { HasLaunched = true, ReducedMotion = false, ExpandOnHover = false, HideInFullscreen = false });
        var notch = new NotchWindow(settings, media, audio, focus, new FileShelf(), playerVolume: player);
        try
        {
            notch.Show(); notch.StopPollingForTest();
            await VolumeMotionVerification.RunAsync(notch, assert);
            var updatedAt = DateTimeOffset.UtcNow;
            var duration = TimeSpan.FromMinutes(3);
            media.SetSnapshotForTest(new MediaSnapshot { HasSession = true });
            media.ApplyTimeline(true, TimeSpan.FromSeconds(10), duration, TimeSpan.Zero, updatedAt);
            await Task.Delay(120);
            var beforeRefresh = media.EstimatedPosition;
            media.ApplyTimeline(true, TimeSpan.FromSeconds(10), duration, TimeSpan.Zero, updatedAt);
            assert(media.EstimatedPosition >= beforeRefresh,
                "Repeated stale Windows timeline updates never rewind the Spotify playback clock");
            media.ApplyTimeline(false, TimeSpan.FromSeconds(10), duration, TimeSpan.Zero, updatedAt);
            var paused = media.EstimatedPosition;
            await Task.Delay(100);
            assert(media.EstimatedPosition == paused, "Pausing freezes Spotify at its displayed position instead of stale provider time");
            media.ApplyTimeline(true, TimeSpan.FromSeconds(70), duration, TimeSpan.Zero, DateTimeOffset.UtcNow);
            assert(media.EstimatedPosition.TotalSeconds is >= 70 and < 71, "A real seek replaces the interpolated Spotify playback anchor");
            media.SetSnapshotForTest(new MediaSnapshot { HasSession = true, Duration = duration, Position = TimeSpan.FromSeconds(.5) });
            media.ApplyTimeline(false, TimeSpan.Zero, duration, TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow);
            assert(media.EstimatedPosition == TimeSpan.Zero,
                "A changed timeline start resets the playback anchor even within the provider rounding tolerance");

            notch.SelectTabForTest("music"); notch.Expand(true);
            media.SetSnapshotForTest(new MediaSnapshot { HasSession = true, IsPlaying = true, Source = "fixture", CanSeek = true,
                Title = "Smooth playback", Duration = duration, Position = TimeSpan.FromSeconds(10) });
            var slider = (ScrubSlider)notch.FindName("SeekSlider");
            var seeks = new List<double>();
            media.SeekHandlerForTest = value => { seeks.Add(value); return Task.FromResult(true); };
            var values = new List<double>();
            EventHandler observe = (_, _) => values.Add(slider.Value);
            CompositionTarget.Rendering += observe;
            try { await Task.Delay(300); }
            finally { CompositionTarget.Rendering -= observe; }
            assert(!SystemParameters.ClientAreaAnimation || values.Count > 2 && values.Exists(value => value > 10.02 && value < 10.29),
                "Spotify progress renders intermediate positions between polling ticks");
            assert(slider.Value > 10 && slider.Value < 11, "Rendered Spotify progress remains anchored to the actual playback clock");
            media.SetSnapshotForTest(media.Current with { IsPlaying = false, Position = media.EstimatedPosition });
            await Task.Delay(350);
            assert(seeks.Count == 0, "Playback rendering and pausing never issue a seek command from a templated rail update");
        }
        finally { notch.CloseForShutdown(); }
    }

    private sealed class SpotifyHandler : HttpMessageHandler
    {
        internal string DeviceName { get; init; } = Environment.MachineName;
        internal bool Restricted { get; init; }
        internal bool RateLimited { get; init; }
        internal bool RefreshRejected { get; init; }
        internal bool RotateRefreshToken { get; init; }
        internal string? PlaybackJson { get; init; }
        internal string? TokenJson { get; init; }
        internal TaskCompletionSource<HttpResponseMessage>? DelayedRefresh { get; init; }
        internal TaskCompletionSource<bool> RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool UnauthorizedOnce { get; set; }
        internal int Refreshes { get; private set; }
        internal int PlaybackReads { get; private set; }
        internal List<string> Commands { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath == "/api/token")
            {
                Refreshes++;
                RefreshStarted.TrySetResult(true);
                if (DelayedRefresh is not null) return DelayedRefresh.Task.WaitAsync(cancellationToken);
                if (TokenJson is not null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TokenJson) });
                return Task.FromResult(RefreshRejected ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                    : Json(new { access_token = "fixture-access-token", expires_in = 3600,
                        refresh_token = RotateRefreshToken ? "rotated-fixture-token" : (string?)null }));
            }
            if (request.Headers.Authorization?.Scheme != "Bearer") throw new InvalidOperationException("Spotify requests must carry an access token.");
            if (request.Method == HttpMethod.Put)
            {
                Commands.Add(request.RequestUri.PathAndQuery);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            PlaybackReads++;
            if (PlaybackJson is not null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(PlaybackJson) });
            if (UnauthorizedOnce) { UnauthorizedOnce = false; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); }
            if (RateLimited)
            {
                var response = new HttpResponseMessage((HttpStatusCode)429);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                return Task.FromResult(response);
            }
            return Task.FromResult(Json(new { device = new { id = "pc/device?1", name = DeviceName, type = "computer",
                is_active = true, is_restricted = Restricted, supports_volume = true, volume_percent = 62 } }));
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    }
}
