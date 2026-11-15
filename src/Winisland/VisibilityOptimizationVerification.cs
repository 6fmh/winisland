using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Winisland.Services;

namespace Winisland;

internal static class VisibilityOptimizationVerification
{
    private sealed class Backend : IPlayerVolumeBackend, IPlayerVolumeNotifications
    {
        internal int Reads, Writes;
        internal double Volume = 50;
        public event EventHandler? Changed;
        public bool Supports(MediaSnapshot media) => media.HasSession;
        public Task<PlayerVolumeSnapshot> ReadAsync(MediaSnapshot media, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); ++Reads;
            return Task.FromResult(new PlayerVolumeSnapshot { Available = true, Volume = Volume, Source = media.Source, TargetId = "visibility-fixture" });
        }
        public Task<bool> WriteAsync(PlayerVolumeSnapshot desired, CancellationToken token)
        { token.ThrowIfCancellationRequested(); ++Writes; Volume = desired.Volume; return Task.FromResult(true); }
        internal void Notify() => Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static async Task<object> RunAsync(App app, Action<bool, string> check)
    {
        BackgroundVerification.RequirePrivateDesktop();
        var settings = new SettingsStore();
        settings.Update(settings.Current with { ReducedMotion = true, HideInFullscreen = false, ShowLiveActivity = false, ShowVolumeHud = true });
        using var media = new MediaService(app.Dispatcher);
        using var audio = new AudioService();
        using var focus = new FocusTimer();
        var backend = new Backend();
        using var volume = new PlayerVolumeService(app.Dispatcher, new IPlayerVolumeBackend[] { backend });
        var notch = new NotchWindow(settings, media, audio, focus, new FileShelf(), playerVolume: volume);
        var snapshot = new MediaSnapshot { HasSession = true, Source = "visibility-fixture", Title = "Fixture", IsPlaying = true, Duration = TimeSpan.FromMinutes(3), CanSeek = true };
        int closedReads = 0, openReads = 0;
        double prewarmMs = 0;
        try
        {
            notch.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            media.SetSnapshotForTest(snapshot);
            await volume.WhenIdleAsync();
            var before = backend.Reads;
            await Task.Delay(450);
            closedReads = backend.Reads - before;
            check(!notch.PlayerPollingForTest && closedReads == 0, "Closed notch performs zero recurring backend reads with an active media source");
            check(!notch.PlaybackRenderingForTest && !notch.VisualizerRunningForTest, "Closed notch with live activities disabled has no playback or spectrum render loop");
            check(notch.PointerWatcherRegisteredForTest, "Private fixture HWND accepts non-intercepting raw mouse observation");

            var island = (FrameworkElement)notch.FindName("Island");
            var near = island.PointToScreen(new Point(island.ActualWidth / 2, island.ActualHeight + 20));
            var started = Stopwatch.GetTimestamp();
            notch.MovePointerForTest(near);
            check(!notch.IsExpanded && notch.PlayerPollingForTest, "Pointer approach enables player discovery before the notch opens");
            await volume.WhenIdleAsync();
            prewarmMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            check(backend.Reads > before && volume.Current.Available, "Approach immediately requests a fresh backend read without the previous poll deadline");
            notch.MovePointerForTest(new Point(-100000, -100000));
            check(!notch.PlayerPollingForTest, "Leaving the approach area stops recurring player polling");
            before = backend.Reads;
            media.SetSnapshotForTest(snapshot with { Title = "New track while closed" });
            await volume.WhenIdleAsync();
            check(backend.Reads == before, "Closed media identity changes update state without waking view discovery");

            notch.Expand(false);
            await volume.WhenIdleAsync();
            check(volume.Current.Available && notch.PlayerPollingForTest, "Explicit opening wakes player controls immediately");
            before = backend.Reads;
            await Task.Delay(500);
            await volume.WhenIdleAsync();
            openReads = backend.Reads - before;
            check(openReads > 0, "Visible music controls continue refreshing their backend");
            check(volume.SetVolume(63), "Visible player accepts a queued write");
            notch.Collapse();
            await volume.WhenIdleAsync();
            check(backend.Writes == 1 && backend.Volume == 63, "Collapsing preserves the latest pending volume write and its readback");
            before = backend.Reads;
            await Task.Delay(450);
            check(backend.Reads == before, "Polling remains stopped after the final collapsed write readback");
            backend.Volume = 71; backend.Notify();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await volume.WhenIdleAsync();
            check(volume.Current.Volume == 71 && ((FrameworkElement)notch.FindName("Hud")).Visibility == Visibility.Visible,
                "True backend changes still update the closed-notch volume HUD without recurring polling");
            check(!notch.PlayerPollingForTest, "A backend event does not restart the closed view timer");

            notch.Expand(false);
            notch.SelectTabForTest("files");
            await volume.WhenIdleAsync();
            before = backend.Reads;
            await Task.Delay(450);
            check(!notch.PlayerPollingForTest && backend.Reads == before, "Non-music tabs suspend hidden player polling");
            notch.SelectTabForTest("music");
            await volume.WhenIdleAsync();
            notch.Hide();
            before = backend.Reads;
            await Task.Delay(450);
            check(!notch.PlayerPollingForTest && !notch.PlaybackRenderingForTest && !notch.VisualizerRunningForTest && backend.Reads == before,
                "Hidden notch suspends view polling and all render loops");
            notch.Show();
            await volume.WhenIdleAsync();
            check(notch.PlayerPollingForTest, "Showing the music view resumes player discovery");
            if (SystemParameters.ClientAreaAnimation)
            {
                settings.Update(settings.Current with { ReducedMotion = false, UseDynamicIsland = true });
                check(notch.PlaybackRenderingForTest && notch.VisualizerRunningForTest, "Visible playing island starts timeline and spectrum rendering");
                notch.SelectTabForTest("files");
                check(!notch.PlaybackRenderingForTest && !notch.VisualizerRunningForTest, "Switching away from music immediately stops both render loops");
                notch.SelectTabForTest("music");
                check(notch.PlaybackRenderingForTest && notch.VisualizerRunningForTest, "Returning to music immediately restores both render loops");
                notch.Hide();
                check(!notch.PlaybackRenderingForTest && !notch.VisualizerRunningForTest, "Hiding a running animated player detaches both render callbacks");
                notch.Show();
                notch.Collapse();
                settings.Update(settings.Current with { ShowLiveActivity = true });
                check(notch.VisualizerRunningForTest, "Visible compact live activity keeps its spectrum feature running");
                notch.ShowNotification(new Models.NotchNotification { Title = "Visibility fixture", Body = "Compact activity is covered." });
                check(!notch.VisualizerRunningForTest, "A notification covering the compact activity stops hidden spectrum capture");
                notch.DismissNotificationForTest(true);
                check(notch.VisualizerRunningForTest, "Dismissing the notification restores visible live activity rendering");
                settings.Update(settings.Current with { ReducedMotion = true });
            }
            notch.Collapse();
            var reference = VerifySettings(settings, volume, check);
            // WPF releases its most recently presented window during deferred
            // render/input cleanup. Verify eventual collection after that work,
            // with a strict bound; do not confuse a pending frame with a leak.
            for (var attempt = 0; reference.IsAlive && attempt < 20; attempt++)
            {
                await Task.Delay(50);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
            check(!reference.IsAlive, "Closed SettingsWindow is collectible while the shared player service remains alive");
            return new { closedReads, openReads, prewarmMs, settingsCollectible = !reference.IsAlive,
                note = "Deterministic injected backend and local HWND coordinates; no physical cursor moves. Operation counts prove removed work, not real-player latency or overall CPU/RAM percentage. GC is confined to this lifetime assertion." };
        }
        finally { notch.CloseForShutdown(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference VerifySettings(SettingsStore store, PlayerVolumeService volume, Action<bool, string> check)
    {
        var window = new SettingsWindow(store, playerVolume: volume) { ShowActivated = false };
        var subscription = typeof(SettingsWindow).GetField("_playerSubscribed", BindingFlags.NonPublic | BindingFlags.Instance)!;
        window.Show();
        check(subscription.GetValue(window) is false, "General settings page has no player-service subscription");
        window.SelectPage("players");
        check(subscription.GetValue(window) is true, "Visible Players settings subscribes to connection changes");
        window.WindowState = WindowState.Minimized;
        check(subscription.GetValue(window) is false, "Minimized settings releases its player-service subscription");
        window.WindowState = WindowState.Normal;
        window.SelectPage("appearance");
        check(subscription.GetValue(window) is false, "Leaving Players settings releases its connection subscription");
        window.SelectPage("players");
        window.Close();
        check(subscription.GetValue(window) is false, "Closing settings releases connection work");
        return new WeakReference(window);
    }
}
