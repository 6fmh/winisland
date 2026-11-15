using System;
using System.Collections.Generic;
using System.Linq;
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
        await VerifyRegisteredSignInAsync(assert);
        await VerifyCallbackAsync(assert);
        await VerifyAccessRefusalAsync(assert);
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

    private static Dictionary<string, string> ParseForm(string form) => form.TrimStart('?').Split('&')
        .Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
            pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')), StringComparer.Ordinal);

    private static async Task VerifyAccessRefusalAsync(Action<bool, string> assert)
    {
        using var handler = new SpotifyHandler { PlaybackForbidden = true };
        using var account = new SpotifyAccount(new HttpClient(handler), false);
        account.ConnectForTest(new string('a', 32), "fixture-refresh-token");
        var media = new MediaSnapshot { HasSession = true, Source = "Spotify.exe" };
        var refused = await account.ReadAsync(media, CancellationToken.None);
        assert(account.IsConnected && !refused.Available && refused.ConnectionHint.Contains("User Management", StringComparison.Ordinal)
            && refused.ConnectionAction == "Spotify account needs approval"
            && !await account.WriteAsync(refused with { Volume = 40 }, CancellationToken.None),
            "Spotify's real plain-text unapproved-user 403 preserves sign-in and explains owner approval without issuing commands");
        handler.PlaybackForbidden = false;
        await Task.Delay(1050);
        assert((await account.ReadAsync(media, CancellationToken.None)).Available && handler.Refreshes == 1,
            "Approving the Spotify app user restores playback volume without signing in again");
    }

    private static async Task VerifyRegisteredSignInAsync(Action<bool, string> assert)
    {
        using var packaged = new SpotifyAccount(new HttpClient(new SpotifyHandler()), false);
        assert(packaged.CanConnect == (SpotifyAppRegistration.ClientId.Length == 32),
            "Spotify sign-in uses embedded app registration (" + SpotifyAppRegistration.ClientId + ")");
        var opened = false;
        foreach (var invalid in new[] { "", "not-a-client-id", new string('g', 32) })
        {
            using var unavailable = new SpotifyAccount(new HttpClient(new SpotifyHandler()), false, invalid, _ => opened = true);
            var rejected = false;
            try { await unavailable.ConnectAsync(); }
            catch (InvalidOperationException ex) { rejected = ex.Message == SpotifyAppRegistration.UnavailableMessage; }
            assert(rejected && !unavailable.CanConnect && !opened && !unavailable.IsConnected,
                "Missing or invalid packaged Spotify registration fails before opening a browser or starting sign-in");
        }

        var clientId = new string('b', 32);
        using var handler = new AuthorizationHandler();
        using var callbackHttp = new HttpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Dictionary<string, string>? authorization = null;
        Task<HttpResponseMessage>? callbackRequest = null;
        using var account = new SpotifyAccount(new HttpClient(handler), false, clientId, uri =>
        {
            assert(uri.GetLeftPart(UriPartial.Path) == "https://accounts.spotify.com/authorize",
                "Connect Spotify sends the user directly to Spotify's authorization page");
            authorization = ParseForm(uri.Query);
            callbackRequest = callbackHttp.GetAsync(authorization["redirect_uri"] + "?state="
                + Uri.EscapeDataString(authorization["state"]) + "&code=" + Uri.EscapeDataString("fixture+code/=?"), timeout.Token);
        });
        // A connection from an older user-supplied app must not choose the app
        // used for a new authorization. Refreshes retain their saved client ID.
        account.ConnectForTest(new string('a', 32), "old-app-refresh-token");
        try
        {
            await account.ConnectAsync(timeout.Token);
            using var callbackResponse = await callbackRequest!;
            var tokenRequest = ParseForm(handler.TokenRequest!);
            var verifier = tokenRequest["code_verifier"];
            var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            assert(callbackResponse.IsSuccessStatusCode && account.IsConnected && account.ClientId == clientId
                && authorization!["client_id"] == clientId && tokenRequest["client_id"] == clientId,
                "One-button Spotify sign-in uses the packaged app ID through callback, token exchange and account connection");
            assert(authorization!["response_type"] == "code" && authorization["code_challenge_method"] == "S256"
                && authorization["code_challenge"] == challenge && verifier.Length is >= 43 and <= 128
                && authorization["state"].Length >= 43 && tokenRequest["grant_type"] == "authorization_code"
                && tokenRequest["code"] == "fixture+code/=?" && !tokenRequest.ContainsKey("client_secret"),
                "Packaged Spotify sign-in preserves random state and PKCE without sending a client secret");
            assert(authorization["scope"] == "user-read-playback-state user-modify-playback-state"
                && authorization["redirect_uri"] == SpotifyAccount.RedirectUri && tokenRequest["redirect_uri"] == SpotifyAccount.RedirectUri,
                "Spotify consent requests only playback permissions and exchanges the exact loopback redirect URI");
            var stored = System.Text.Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(System.IO.Path.Combine(SettingsStore.DirectoryPath, "spotify-account.bin")));
            using var restored = new SpotifyAccount(new HttpClient(new SpotifyHandler()), true, new string('c', 32));
            assert(restored.IsConnected && restored.ClientId == clientId && !stored.Contains("authorized-refresh-token"),
                "One-button Spotify sign-in saves encrypted credentials and reloads the original registration after restart");
        }
        finally
        {
            account.Disconnect();
            if (callbackRequest is not null && !callbackRequest.IsCompleted) timeout.Cancel();
        }
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

    private static async Task VerifyConnectedBackendAsync(App app, Action<bool, string> assert)
    {
        var slider = new System.Windows.Controls.Slider { Minimum = 0, Maximum = 1, Value = .62, Width = 180 };
        System.Windows.Automation.AutomationProperties.SetName(slider, "Volym");
        var window = new Window { Title = "Winisland connected Spotify fixture", Content = slider, Width = 240, Height = 100, ShowInTaskbar = false };
        window.Show();
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        try
        {
            using var handler = new SpotifyHandler { PlaybackForbidden = true };
            using var account = new SpotifyAccount(new HttpClient(handler), false);
            account.ConnectForTest(new string('a', 32), "fixture-refresh-token");
            using var adapter = new SpotifyPlayerVolume(() => System.Windows.Automation.AutomationElement.FromHandle(handle), account);
            using var service = new PlayerVolumeService(app.Dispatcher, new IPlayerVolumeBackend[] { adapter });
            service.SetSource(new MediaSnapshot { HasSession = true, Source = "Spotify.exe" });
            await service.WhenIdleAsync();
            assert(service.Current.Available && service.Current.TargetId.StartsWith("spotify:", StringComparison.Ordinal)
                && service.Current.Volume == 62 && account.IsConnected,
                "A signed-in Spotify account refused API access retains the real native player volume control");
            using var media = new MediaService(app.Dispatcher);
            using var audio = new AudioService();
            using var focus = new FocusTimer();
            var settings = new SettingsStore();
            settings.Update(settings.Current with { HasLaunched = true, ShowVolumeHud = true, ExpandOnHover = false, HideInFullscreen = false });
            media.SetSnapshotForTest(new MediaSnapshot { HasSession = true, Source = "Spotify.exe", Title = "Fixture" });
            var notch = new NotchWindow(settings, media, audio, focus, new FileShelf(), playerVolume: service);
            try
            {
                notch.Show(); notch.StopPollingForTest();
                slider.Value = .47;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                var hud = (FrameworkElement)notch.FindName("Hud");
                var value = (System.Windows.Controls.TextBlock)notch.FindName("HudValue");
                while ((service.Current.Volume != 47 || value.Text != "47%") && !timeout.IsCancellationRequested) await Task.Delay(15);
                assert(service.Current.Available && service.Current.Volume == 47,
                    "External Spotify slider notifications still update player volume while account API access is refused");
                assert(hud.Visibility == Visibility.Visible && value.Text == "47%",
                    "External native Spotify volume changes show the actual notch HUD despite account API refusal");
            }
            finally { notch.CloseForShutdown(); }
            assert(await adapter.WriteAsync(service.Current with { Volume = 39 }, CancellationToken.None) && Math.Abs(slider.Value - .39) < .001,
                "Spotify API refusal does not prevent writes to the available native player slider");
        }
        finally { window.Close(); }

        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        using var allowedHandler = new SpotifyHandler();
        using var allowedAccount = new SpotifyAccount(new HttpClient(allowedHandler), false);
        allowedAccount.ConnectForTest(new string('a', 32), "fixture-refresh-token");
        using var blockedLocal = new SpotifyPlayerVolume(() => { started.Set(); release.Wait(TimeSpan.FromSeconds(5)); return null; }, allowedAccount);
        try
        {
            var pending = blockedLocal.ReadAsync(new MediaSnapshot { HasSession = true, Source = "Spotify.exe" }, CancellationToken.None);
            var first = await Task.WhenAny(pending, Task.Delay(900));
            assert(first == pending && (await pending).Available,
                "A blocked Spotify accessibility provider cannot delay working account API volume");
        }
        finally { release.Set(); }

        using var unavailableHandler = new SpotifyHandler { PlaybackForbidden = true };
        using var unavailableAccount = new SpotifyAccount(new HttpClient(unavailableHandler), false);
        unavailableAccount.ConnectForTest(new string('a', 32), "fixture-refresh-token");
        using var brokenLocal = new SpotifyPlayerVolume(() => throw new InvalidOperationException("Fixture provider unavailable"), unavailableAccount);
        using var unavailableService = new PlayerVolumeService(app.Dispatcher, new IPlayerVolumeBackend[] { brokenLocal });
        unavailableService.SetSource(new MediaSnapshot { HasSession = true, Source = "Spotify.exe" });
        await unavailableService.WhenIdleAsync();
        assert(!unavailableService.Current.Available && unavailableService.Current.ConnectionAction == "Spotify account needs approval",
            "A failed local accessibility provider preserves the actionable Spotify API refusal through the player service");
        var accountSettings = new SettingsWindow(new SettingsStore(), playerVolume: unavailableService);
        try
        {
            var button = (System.Windows.Controls.Button)accountSettings.FindName("ConnectSpotifyAccountButton");
            var status = (System.Windows.Controls.TextBlock)accountSettings.FindName("SpotifyAccountStatus");
            assert((string)button.Content == "Switch Spotify account" && status.Text.Contains("User Management", StringComparison.Ordinal),
                "Signed-in Spotify settings offer account switching and explain approval instead of asking to reconnect");
        }
        finally { accountSettings.Close(); }
    }

    internal static async Task RunNotchAsync(App app, Action<bool, string> assert)
    {
        await VerifyConnectedBackendAsync(app, assert);
        foreach (var clientId in new[] { "", SpotifyAppRegistration.ClientId })
        {
            using var settingsAccount = new SpotifyAccount(new HttpClient(new SpotifyHandler()), false, clientId, _ =>
                throw new InvalidOperationException("Settings rendering must not open a browser."));
            using var settingsPlayer = new PlayerVolumeService(app.Dispatcher,
                new IPlayerVolumeBackend[] { new SpotifyPlayerVolume(() => null, settingsAccount) });
            var settingsWindow = new SettingsWindow(new SettingsStore(), playerVolume: settingsPlayer);
            try
            {
                var connect = (System.Windows.Controls.Button)settingsWindow.FindName("ConnectSpotifyAccountButton");
                var status = (System.Windows.Controls.TextBlock)settingsWindow.FindName("SpotifyAccountStatus");
                assert((string)connect.Content == "Connect Spotify" && settingsWindow.FindName("SpotifyClientIdInput") is null
                    && connect.IsEnabled == settingsAccount.CanConnect,
                    "Spotify account settings offer Connect Spotify without developer fields and reflect registration availability");
                if (!settingsAccount.CanConnect) assert(status.Text == SpotifyAppRegistration.UnavailableMessage,
                    "Unconfigured Spotify settings explain availability inline without requiring developer setup");
            }
            finally { settingsWindow.Close(); }
        }
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

    private sealed class AuthorizationHandler : HttpMessageHandler
    {
        internal string? TokenRequest { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri != "https://accounts.spotify.com/api/token" || request.Method != HttpMethod.Post)
                throw new InvalidOperationException("Unexpected authorization request.");
            TokenRequest = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                { access_token = "authorized-access-token", expires_in = 3600, refresh_token = "authorized-refresh-token" })) };
        }
    }

    private sealed class SpotifyHandler : HttpMessageHandler
    {
        internal string DeviceName { get; init; } = Environment.MachineName;
        internal bool Restricted { get; init; }
        internal bool PlaybackForbidden { get; set; }
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
            if (PlaybackForbidden) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                { Content = new StringContent("Check settings on https://developer.spotify.com/dashboard, the user may not be registered.") });
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
