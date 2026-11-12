using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Winisland.Services;

namespace Winisland;

internal static class MediaBugVerification
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        foreach (var failure in new[] { DelayedFailure.RefreshHttp, DelayedFailure.RefreshJson, DelayedFailure.PlaybackHttp, DelayedFailure.PlaybackRateLimited })
        {
            using var handler = new DelayedSpotifyHandler(failure);
            using var account = new SpotifyAccount(new HttpClient(handler), loadCredentials: false);
            account.ConnectForTest(new string('a', 32), "fixture-refresh-token");
            var pending = account.ReadAsync(new MediaSnapshot { HasSession = true, Source = "Spotify.exe" }, CancellationToken.None);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            account.Disconnect();
            handler.CompleteFailure();
            var snapshot = await pending.WaitAsync(TimeSpan.FromSeconds(3));
            assert(!account.IsConnected && !snapshot.Available && account.Status == "Account disconnected"
                && snapshot.ConnectionHint == "Account disconnected",
                $"Disconnecting Spotify preserves account state after a delayed {failure} failure");
            assert(!await account.WriteAsync(snapshot with { Volume = 40 }, CancellationToken.None)
                && handler.VolumeWrites == 0,
                $"A delayed {failure} failure cannot restore a writable Spotify target after disconnect");
        }
        await VerifyLateSignInAsync(assert);
    }

    private static async Task VerifyLateSignInAsync(Action<bool, string> assert)
    {
        var previousDirectory = Environment.GetEnvironmentVariable("WINISLAND_DATA_DIR");
        var fixtureDirectory = Path.Combine(SettingsStore.DirectoryPath, "spotify-sign-in-races-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("WINISLAND_DATA_DIR", fixtureDirectory);
            foreach (var invalidation in new[] { "cancel", "disconnect", "dispose" })
            {
                var caseDirectory = Path.Combine(fixtureDirectory, invalidation);
                Environment.SetEnvironmentVariable("WINISLAND_DATA_DIR", caseDirectory);
                using var handler = new DelayedSignInHandler();
                using var account = new SpotifyAccount(new HttpClient(handler), loadCredentials: false);
                using var cancellation = new CancellationTokenSource();
                var notifications = 0;
                account.Changed += (_, _) => ++notifications;
                var pending = account.FinishSignInAsync(new string('a', 32), "fixture-code", "fixture-verifier", cancellation.Token);
                await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (invalidation == "cancel") cancellation.Cancel();
                else if (invalidation == "disconnect") account.Disconnect();
                else account.Dispose();
                handler.CompleteSuccess();
                var canceled = false;
                try { await pending.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (OperationCanceledException) { canceled = true; }
                catch (Exception) { /* A different failure must fail the canceled-state assertion. */ }
                assert(canceled && !account.IsConnected && !File.Exists(Path.Combine(caseDirectory, "spotify-account.bin")),
                    $"Spotify sign-in invalidated by {invalidation} rejects delayed success before persisting credentials");
                assert(notifications == (invalidation == "disconnect" ? 1 : 0)
                    && (invalidation != "disconnect" || account.Status == "Account disconnected"),
                    $"A delayed Spotify sign-in success after {invalidation} does not publish an account connection");
            }

            Environment.SetEnvironmentVariable("WINISLAND_DATA_DIR", fixtureDirectory);
            using var staleHandler = new DelayedSignInHandler();
            using var staleAccount = new SpotifyAccount(new HttpClient(staleHandler), loadCredentials: false);
            staleAccount.Disconnect();
            var rejected = false;
            var staleSignIn = staleAccount.FinishSignInAsync(new string('a', 32), "fixture-code", "fixture-verifier", CancellationToken.None, expectedGeneration: 0);
            if (staleHandler.Started.Task.IsCompleted) staleHandler.CompleteSuccess();
            try { await staleSignIn.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { rejected = true; }
            catch (Exception) { /* A different failure must fail the stale-generation assertion. */ }
            assert(rejected && !staleHandler.Started.Task.IsCompleted && !staleAccount.IsConnected,
                "Spotify rejects the generation captured before the browser callback without exchanging a stale authorization code");

            using var successfulHandler = new DelayedSignInHandler();
            using var successfulAccount = new SpotifyAccount(new HttpClient(successfulHandler), loadCredentials: false);
            var successfulSignIn = successfulAccount.FinishSignInAsync(new string('a', 32), "fixture-code", "fixture-verifier", CancellationToken.None);
            await successfulHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            successfulHandler.CompleteSuccess();
            await successfulSignIn.WaitAsync(TimeSpan.FromSeconds(3));
            using var restoredAccount = new SpotifyAccount(new HttpClient(new DelayedSignInHandler()), loadCredentials: true);
            assert(successfulAccount.IsConnected && restoredAccount.IsConnected && restoredAccount.ClientId == new string('a', 32),
                "A current Spotify sign-in still persists encrypted credentials that reconnect after restart");
            successfulAccount.Disconnect();
        }
        finally { Environment.SetEnvironmentVariable("WINISLAND_DATA_DIR", previousDirectory); }
    }

    private enum DelayedFailure { RefreshHttp, RefreshJson, PlaybackHttp, PlaybackRateLimited }

    private sealed class DelayedSpotifyHandler(DelayedFailure failure) : HttpMessageHandler
    {
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int VolumeWrites { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put)
            {
                ++VolumeWrites;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            if ((failure is DelayedFailure.PlaybackHttp or DelayedFailure.PlaybackRateLimited) && request.RequestUri?.Host == "accounts.spotify.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { access_token = "fixture-access-token", expires_in = 3600 }))
                });
            Started.TrySetResult(true);
            return _completion.Task;
        }

        internal void CompleteFailure()
        {
            if (failure == DelayedFailure.RefreshJson)
                _completion.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{invalid-json") });
            else if (failure == DelayedFailure.PlaybackRateLimited)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                _completion.TrySetResult(response);
            }
            else _completion.TrySetException(new HttpRequestException("Fixture delayed request failure."));
        }
    }

    private sealed class DelayedSignInHandler : HttpMessageHandler
    {
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            return _completion.Task;
        }

        internal void CompleteSuccess() => _completion.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                access_token = "fixture-access-token", refresh_token = "fixture-new-refresh-token", expires_in = 3600
            }))
        });
    }
}
