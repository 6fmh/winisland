using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Winisland.Controls;
using Winisland.Services;

namespace Winisland;

// Executed on WPF's real dispatcher. Tests exercise OS integration and render the
// actual windows; all persistent data is isolated from the user's configuration.
internal static class SmokeTests
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }

    internal static async Task<int> RunAsync(App app, string[] args)
    {
        var index = Array.IndexOf(args, "--output");
        var output = Path.GetFullPath(index >= 0 && index + 1 < args.Length ? args[index + 1] : "artifacts/verification");
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("WINISLAND_DATA_DIR", Path.Combine(output, "test-data"));
        BackgroundVerification.RequirePrivateDesktop();
        var results = new List<object>
        {
            new { check = "Verification is isolated from the input desktop", passed = true,
                desktop = BackgroundVerification.CurrentDesktop, mode = "background" }
        };
        NotchWindow? notch = null;
        SettingsWindow? settingsWindow = null;
        using var media = new MediaService(app.Dispatcher);
        using var focus = new FocusTimer();
        using var playerFixture = new PlayerVolumeVerification.FixturePlayerVolumeBackend();
        using var playerVolume = new PlayerVolumeService(app.Dispatcher, new IPlayerVolumeBackend[] { playerFixture });
        void Phase(string name) => File.AppendAllText(Path.Combine(output, "verification-progress.log"), $"{DateTimeOffset.UtcNow:o} {name}\n");
        try
        {
            Phase("Update fixtures and UI");
            await UpdateVerification.RunAsync(app, output, (condition, name) => Assert(condition, name, results));
            if (args.Contains("--updates-only"))
            {
                await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { passed = true, results }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            Phase("Failure reporting");
            VerifyFailureReports(output, (condition, name) => Assert(condition, name, results));
            Phase("Spectrum analysis");
            SpectrumVerification.Run((condition, name) => Assert(condition, name, results));
            Phase("Browser registration");
            BrowserRegistrationVerification.Run(output, (condition, name) => Assert(condition, name, results));
            Phase("Player volume fixtures and native bridge");
            await PlayerVolumeVerification.RunAsync(app.Dispatcher, (condition, name) => Assert(condition, name, results));
            Phase("Spotify fixtures");
            await SpotifyVerification.RunAsync((condition, name) => Assert(condition, name, results));
            if (args.Contains("--spotify-only"))
            {
                await SpotifyVerification.RunNotchAsync(app, (condition, name) => Assert(condition, name, results));
                await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { passed = true, results }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            var store = new SettingsStore();
            store.Update(new IslandSettings { NotchWidth = 900, NotchHeight = 1, FocusMinutes = -5, ReducedMotion = true, HideInFullscreen = false });
            Assert(store.Current.NotchWidth == 320 && store.Current.NotchHeight == 24 && store.Current.FocusMinutes == 1, "Settings constrain invalid persisted sizes and duration", results);
            var reloaded = new SettingsStore();
            Assert(reloaded.Current == store.Current, "Settings survive restart", results);
            store.Update(new IslandSettings { ReducedMotion = true, ExpandOnHover = false, HideInFullscreen = false, HasLaunched = true });

            var shelf = new FileShelf();
            shelf.Clear();
            var file = Path.Combine(SettingsStore.DirectoryPath, "A little test file.txt");
            File.WriteAllText(file, "Shelf operations must preserve this content.");
            Assert(shelf.Add(new[] { file, file, file + ".missing" }) == 1, "Shelf accepts unique existing files", results);
            Assert(new FileShelf().Items.Single().Path == file, "Shelf references survive restart", results);
            shelf.Remove(shelf.Items[0]);
            Assert(File.ReadAllText(file) == "Shelf operations must preserve this content.", "Removing shelf reference preserves original file", results);
            Phase("Upload fixtures");
            await UploadVerification.RunAsync((condition, name) => Assert(condition, name, results));

            focus.Start(1);
            await Task.Delay(100);
            focus.TogglePause();
            var paused = focus.Remaining;
            await Task.Delay(100);
            Assert(!focus.IsRunning && focus.Remaining == paused, "Pausing timer stops elapsed time", results);
            focus.TogglePause();
            await Task.Delay(100);
            Assert(focus.IsRunning && focus.Remaining < paused, "Resuming timer continues elapsed time", results);
            focus.Reset(25);
            Assert(!focus.IsStarted && focus.Remaining == TimeSpan.FromMinutes(25), "Reset restores duration", results);

            var outline = NotchSurface.CreateGeometry(NotchMetrics.DefaultWidth, NotchMetrics.DefaultHeight);
            Assert(outline.FillContains(new Point(NotchMetrics.DefaultWidth / 2, 16)) && !outline.FillContains(new Point(2, 18)) && !outline.FillContains(new Point(10, 31)), "Notch contour has inward shoulders and curved lower corners", results);

            Phase("Windows audio and media availability");
            using var audio = new AudioService();
            var audioStatus = audio.ReadOutputForTest();
            results.Add(new { check = "Windows default audio endpoint", available = audioStatus.Available, volume = audioStatus.Volume, muted = audioStatus.Muted });
            await media.InitializeAsync();
            results.Add(new { check = "Windows media session manager", hasSession = media.Current.HasSession, title = media.Current.Title });
            audio.SetSource(media.Current.Source);
            results.Add(new { check = "Current media app audio session", source = media.Current.Source, snapshot = audio.Read() });
            results.Add(new { check = "Windows power status", status = NativeMethods.GetBattery() });

            Phase("Native notch window");
            notch = new NotchWindow(store, media, audio, focus, shelf, playerVolume: playerVolume);
            notch.Show();
            notch.StopPollingForTest();
            await Settle(app);
            var handle = new WindowInteropHelper(notch).Handle;
            var styles = GetWindowLong(handle, -20);
            results.Add(new { check = "Initial overlay input state", handle = handle.ToInt64(), styles,
                notch.IsExpanded, notch.IsActive, notch.IsVisible,
                desktop = BackgroundVerification.CurrentDesktop });
            Assert((styles & 0x08000000) != 0 && (styles & 0x80) != 0 && !notch.ShowInTaskbar, "Idle overlay is a nonactivating tool window", results);
            Assert(NativeMethods.ConfigureOverlay(handle), "Windows accepts notch exclusion from taskbar and desktop Peek", results);
            var screen = NativeMethods.ResolveScreen(store.Current.MonitorDevice).Bounds;
            GetWindowRect(handle, out var rect);
            Assert(rect.Top == screen.Top && Math.Abs((rect.Left + rect.Right) / 2 - (screen.Left + screen.Right) / 2) <= 1, "Overlay sits at the top center of selected display", results);
            Assert(WindowFromPoint(new NativePoint { X = (rect.Left + rect.Right) / 2, Y = rect.Top + 15 }) == handle
                && WindowFromPoint(new NativePoint { X = rect.Left + 10, Y = rect.Top + 70 }) != handle,
                "Windows hit testing passes through transparent area but targets the visible notch", results);
            var closed = Capture(notch, Path.Combine(output, "notch-closed.png"));
            notch.InitializeDeferredPanelsForTest();
            Assert(GetAlpha(closed, (int)(notch.ActualWidth / 2), 15) == 255 && GetAlpha(closed, 10, 70) == 0, "Idle notch is opaque and surrounding window is transparent", results);

            media.Dispose(); // Keep real player events out of the controlled input fixtures.
            Phase("Physical audio and player controls");
            await AudioVerification.RunAsync(output, notch, media, audio, playerVolume, playerFixture,
                (condition, name) => { Assert(condition, name, results); Phase(name); });
            await VolumeMotionVerification.RunAsync(notch, (condition, name) => Assert(condition, name, results));
            await FocusVerification.RunAsync(notch, store, focus, (condition, name) => Assert(condition, name, results));
            await OrganizerVerification.RunAsync(output, (condition, name) => Assert(condition, name, results));
            await PlaybackVerification.RunAsync(notch, media, store, (condition, name) => Assert(condition, name, results),
                name => Capture(notch, Path.Combine(output, name)));
            await AppleMotionVerification.RunAsync(app, notch, store, media, output,
                (condition, name) => Assert(condition, name, results));
            await NotchVisualVerification.RunAsync(app, notch, store, media, focus, shelf, output,
                (condition, name) => Assert(condition, name, results));
            await ProductivityVisualVerification.RunAsync(notch, store, media, focus, output,
                (condition, name) => Assert(condition, name, results));
            await ControlCenterVerification.RunAsync(notch, store, output,
                (condition, name) => Assert(condition, name, results));
            await NotificationVerification.RunAsync(notch, store, output, (condition, name) => Assert(condition, name, results));
            await NotificationCaptureVerification.RunAsync((condition, name) => Assert(condition, name, results), notch.ShowNotification);
            Assert(notch.ActiveNotificationForTest?.AppName == "Messages", "Native accessibility capture delivers to the actual notch presentation", results);
            notch.DismissNotificationForTest(true);
            await AppleDeviceVerification.RunAsync(notch, store, output,
                (condition, name) => Assert(condition, name, results));

            var island = (FrameworkElement)notch.FindName("Island");
            Phase("Idle overlay recovery");
            var foregroundBeforeRecovery = GetForegroundWindow();
            ShowWindow(handle, 6); // Minimize only the fixture HWND on the private desktop.
            NativeMethods.SetWindowPos(handle, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            Assert(IsIconic(handle) && (GetWindowLong(handle, -20) & 0x0008) == 0,
                "Idle recovery fixture is minimized and has lost native topmost state", results);
            notch.StartOverlayPulseForTest();
            try
            {
                await WaitUntil(() => !IsIconic(handle) && IsWindowVisible(handle) && (GetWindowLong(handle, -20) & 0x0008) != 0, 2600);
                Assert(!IsIconic(handle) && IsWindowVisible(handle) && (GetWindowLong(handle, -20) & 0x0008) != 0,
                    "The real idle pulse restores a minimized displaced notch without user input", results);
                Assert(GetForegroundWindow() == foregroundBeforeRecovery,
                    "Idle overlay recovery preserves the foreground window", results);
                await Task.Delay(2200);
                var idleFrame = Capture(notch, Path.Combine(output, "notch-idle-recovered.png"));
                Assert(island.Visibility == Visibility.Visible && island.Opacity == 1
                    && GetAlpha(idleFrame, (int)(notch.ActualWidth / 2), 15) == 255,
                    "Idle maintenance leaves the camera housing opaque and visible across repeated pulses", results);
            }
            finally { notch.StopPollingForTest(); }

            notch.Hide();
            Assert(!notch.IsVisible, "Resume recovery fixture starts with a hidden notch", results);
            NativeMethods.SetWindowPos(handle, new IntPtr(-2), screen.Left + 80, screen.Top + 80, 0, 0, 0x0001 | 0x0010);
            notch.RecoverOverlayForTest();
            notch.RecoverOverlayForTest(); // Resume/display notifications can arrive together.
            await WaitUntil(() => notch.IsVisible && (GetWindowLong(handle, -20) & 0x0008) != 0, 500);
            GetWindowRect(handle, out var recoveredRect);
            Assert(notch.IsVisible && recoveredRect.Top == screen.Top
                && Math.Abs((recoveredRect.Left + recoveredRect.Right) / 2 - (screen.Left + screen.Right) / 2) <= 1
                && GetForegroundWindow() == foregroundBeforeRecovery,
                "Coalesced resume recovery shows and recenters the notch without stealing focus", results);
            Assert(NativeMethods.IsShellProcess("TextInputHost") && NativeMethods.IsShellProcess("InputApp")
                && NativeMethods.IsShellProcess("shellhost"),
                "Windows Quick Settings and input flyout hosts are excluded from fullscreen hiding regardless of process-name casing", results);
            Assert(!NativeMethods.IsShellProcess("explorer") && !NativeMethods.IsShellProcess("msedge")
                && !NativeMethods.IsShellProcess("Winisland"),
                "Fullscreen-capable desktop apps remain eligible for fullscreen detection", results);
            var fullscreenOffset = (TranslateTransform)notch.FindName("FullscreenOffset");
            var fullscreenWindow = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
            var normalWindow = new Window { Width = 200, Height = 120, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
            try
            {
                fullscreenWindow.Show();
                var fullscreenHandle = new WindowInteropHelper(fullscreenWindow).Handle;
                var workArea = NativeMethods.ResolveScreen(store.Current.MonitorDevice).WorkingArea;
                NativeMethods.SetWindowPos(fullscreenHandle, IntPtr.Zero, workArea.Left + 20, workArea.Top + 20, workArea.Width - 40, workArea.Height - 40, 0x0010);
                notch.SetForegroundWindowForTest(fullscreenHandle);
                await Settle(app);
                Assert(fullscreenWindow.IsVisible, "Fullscreen foreground fixture is visible on the private desktop", results);
                store.Update(store.Current with { HideInFullscreen = true });
                Assert(island.Visibility == Visibility.Visible, "A normal foreground app leaves the notch visible", results);

                var fullscreenResponse = Stopwatch.StartNew();
                NativeMethods.SetWindowPos(fullscreenHandle, IntPtr.Zero, screen.Left, screen.Top, screen.Width, screen.Height, 0x0040);
                await WaitUntil(() => island.Visibility == Visibility.Hidden, 250);
                Assert(island.Visibility == Visibility.Hidden,
                    "Resizing the foreground app to fullscreen hides the notch within 250 ms with polling stopped", results);
                results.Add(new { check = "Fullscreen entry response", milliseconds = fullscreenResponse.ElapsedMilliseconds });
                Assert(NativeMethods.IsFullscreenWindowOn(fullscreenHandle, store.Current.MonitorDevice, handle),
                    "A foreground borderless fullscreen app still triggers fullscreen hiding", results);
                notch.RecoverOverlayForTest();
                notch.StartOverlayPulseForTest();
                try
                {
                    await Task.Delay(2200);
                    Assert(island.Visibility == Visibility.Hidden && island.Opacity == 0,
                        "Resume recovery and repeated idle pulses preserve intentional fullscreen hiding", results);
                }
                finally { notch.StopPollingForTest(); }

                fullscreenWindow.Hide();
                await WaitUntil(() => island.Visibility == Visibility.Visible, 250);
                Assert(!NativeMethods.IsFullscreenWindowOn(fullscreenHandle, store.Current.MonitorDevice, handle)
                    && island.Visibility == Visibility.Visible,
                    "A dismissed window retaining fullscreen bounds does not leave the notch hidden", results);
                fullscreenWindow.Show();
                notch.SetForegroundWindowForTest(fullscreenHandle);
                await WaitUntil(() => island.Visibility == Visibility.Hidden, 250);
                Assert(island.Visibility == Visibility.Hidden, "Showing a real fullscreen app restores fullscreen hiding", results);

                fullscreenWindow.WindowState = WindowState.Minimized;
                await WaitUntil(() => island.Visibility == Visibility.Visible, 250);
                Assert(!NativeMethods.IsFullscreenWindowOn(fullscreenHandle, store.Current.MonitorDevice, handle)
                    && island.Visibility == Visibility.Visible,
                    "Minimizing a fullscreen app restores the notch without polling", results);
                fullscreenWindow.WindowState = WindowState.Normal;
                notch.SetForegroundWindowForTest(fullscreenHandle);
                await WaitUntil(() => island.Visibility == Visibility.Hidden, 250);
                Assert(island.Visibility == Visibility.Hidden, "Restoring a real fullscreen app hides the notch again", results);

                store.Update(store.Current with { HideInFullscreen = false });
                Assert(island.Visibility == Visibility.Visible, "Disabling fullscreen hiding restores the notch immediately", results);
                store.Update(store.Current with { HideInFullscreen = true });
                Assert(island.Visibility == Visibility.Hidden, "Enabling fullscreen hiding applies immediately to an existing fullscreen app", results);

                NativeMethods.SetWindowPos(fullscreenHandle, IntPtr.Zero, workArea.Left + 20, workArea.Top + 20, workArea.Width - 40, workArea.Height - 40, 0x0010);
                await WaitUntil(() => island.Visibility == Visibility.Visible, 250);
                Assert(island.Visibility == Visibility.Visible, "Leaving fullscreen restores the notch within 250 ms with polling stopped", results);
                Assert(!NativeMethods.IsFullscreenWindowOn(fullscreenHandle, store.Current.MonitorDevice, handle), "Returning an app to a normal window stops fullscreen hiding", results);

                NativeMethods.SetWindowPos(fullscreenHandle, IntPtr.Zero, screen.Left, screen.Top, screen.Width, screen.Height, 0x0010);
                await WaitUntil(() => island.Visibility == Visibility.Hidden, 250);
                normalWindow.Show();
                notch.SetForegroundWindowForTest(new WindowInteropHelper(normalWindow).Handle);
                await WaitUntil(() => island.Visibility == Visibility.Visible, 250);
                Assert(island.Visibility == Visibility.Visible, "Switching from fullscreen to a windowed app restores the notch without polling", results);
                notch.Expand(true);
                notch.SetForegroundWindowForTest(fullscreenHandle);
                await WaitUntil(() => island.Visibility == Visibility.Hidden, 250);
                Assert(island.Visibility == Visibility.Hidden && !notch.IsExpanded,
                    "Switching into an already fullscreen app immediately hides and collapses a pinned notch without polling", results);

                store.Update(store.Current with { ReducedMotion = false });
                notch.SetForegroundWindowForTest(new WindowInteropHelper(normalWindow).Handle);
                await WaitUntil(() => island.IsHitTestVisible && island.Opacity > 0.99, 250);
                var fadeFrames = new List<object>();
                var renderedIntermediateFade = false;
                var renderedUpwardMotion = false;
                EventHandler sampleFade = (_, _) =>
                {
                    if (island.IsHitTestVisible) return;
                    fadeFrames.Add(new
                    {
                        milliseconds = fullscreenResponse.ElapsedMilliseconds,
                        opacity = island.Opacity,
                        offsetY = fullscreenOffset.Y,
                        visibility = island.Visibility.ToString()
                    });
                    renderedIntermediateFade |= island.Visibility == Visibility.Visible && island.Opacity is > 0 and < 1;
                    renderedUpwardMotion |= island.Visibility == Visibility.Visible && island.Opacity is > 0 and < 1 && fullscreenOffset.Y < -0.5;
                };
                CompositionTarget.Rendering += sampleFade;
                try
                {
                    fullscreenResponse.Restart();
                    notch.SetForegroundWindowForTest(fullscreenHandle);
                    await WaitUntil(() => !island.IsHitTestVisible, 100);
                    Assert(!island.IsHitTestVisible, "Fullscreen fade yields pointer input within 100 ms with polling stopped", results);
                    results.Add(new { check = "Fullscreen fade start response", milliseconds = fullscreenResponse.ElapsedMilliseconds });
                    // Observe presented WPF frames over the fade instead of
                    // assuming a single delayed continuation lands between them.
                    await WaitUntil(() => island.Visibility == Visibility.Hidden, 250);
                    results.Add(new { check = "Fullscreen fade rendered frames", frames = fadeFrames });
                    if (SystemParameters.ClientAreaAnimation)
                    {
                        Assert(renderedIntermediateFade,
                            "Fullscreen disappearance renders intermediate opacity instead of snapping off", results);
                        Assert(renderedUpwardMotion,
                            "Fullscreen disappearance slides upward on rendered frames while still visible", results);
                    }
                    Assert(island.Visibility == Visibility.Hidden && island.Opacity == 0 && fullscreenResponse.ElapsedMilliseconds < 300,
                        "Fullscreen fade finishes quickly and leaves the notch fully transparent", results);
                }
                finally { CompositionTarget.Rendering -= sampleFade; }

                notch.SetForegroundWindowForTest(new WindowInteropHelper(normalWindow).Handle);
                await WaitUntil(() => island.IsHitTestVisible && island.Opacity > 0.99, 250);
                notch.SetForegroundWindowForTest(fullscreenHandle);
                await WaitUntil(() => !island.IsHitTestVisible, 100);
                await Task.Delay(35);
                notch.SetForegroundWindowForTest(new WindowInteropHelper(normalWindow).Handle);
                await WaitUntil(() => island.IsHitTestVisible, 100);
                await Task.Delay(220);
                Assert(island.Visibility == Visibility.Visible && island.Opacity == 1 && island.IsHitTestVisible && fullscreenOffset.Y == 0,
                    "A quick fullscreen exit reverses the fade without a stale completion hiding the notch", results);

                notch.SetForegroundWindowForTest(fullscreenHandle);
                await WaitUntil(() => !island.IsHitTestVisible, 100);
                await Task.Delay(35);
                notch.Expand(true);
                await Task.Delay(220);
                Assert(notch.IsExpanded && island.Visibility == Visibility.Visible && island.Opacity == 1 && island.IsHitTestVisible && fullscreenOffset.Y == 0,
                    "Explicitly opening the notch cancels an unfinished fullscreen fade", results);

                notch.SetForegroundWindowForTest(new WindowInteropHelper(normalWindow).Handle); notch.Collapse();
                await WaitUntil(() => island.IsHitTestVisible && island.Opacity > 0.99, 250);
                notch.SetForegroundWindowForTest(fullscreenHandle);
                await WaitUntil(() => !island.IsHitTestVisible, 100);
                store.Update(store.Current with { ReducedMotion = true });
                Assert(island.Visibility == Visibility.Hidden && island.Opacity == 0,
                    "Enabling reduced motion finishes an active fullscreen fade immediately", results);
            }
            finally
            {
                notch.SetForegroundWindowForTest(IntPtr.Zero);
                fullscreenWindow.Close(); normalWindow.Close();
                store.Update(store.Current with { HideInFullscreen = false, ReducedMotion = true });
            }

            LeavePointer(island);
            notch.Collapse();
            await Task.Delay(40);
            EnterPointer(island);
            await Task.Delay(310);
            Assert(!notch.IsExpanded, "Pointer entry respects the disabled open-on-hover preference", results);
            LeavePointer(island);
            store.Update(store.Current with { ExpandOnHover = true, ReducedMotion = false });
            var initialWidth = island.Width;
            var initialHeight = island.Height;
            EnterPointer(island);
            Assert(notch.IsExpanded, "Pointer entry starts opening immediately without a hover delay", results);
            var content = (FrameworkElement)notch.FindName("ExpandedContent");
            await app.Dispatcher.InvokeAsync(notch.UpdateLayout, DispatcherPriority.Render);
            await Task.Delay(65);
            Assert(island.Width > initialWidth && island.Height > initialHeight,
                "Hover expansion visibly advances before the former 300 ms delay", results);
            var boundedSpring = true;
            var widthLimit = NotchMetrics.ExpandedWidth + Math.Abs(NotchMetrics.ExpandedWidth - initialWidth) * 0.02;
            var heightLimit = NotchMetrics.ExpandedHeight + Math.Abs(NotchMetrics.ExpandedHeight - initialHeight) * 0.02;
            for (var sample = 0; sample < 44; sample++)
            {
                await Task.Delay(16);
                boundedSpring &= island.Width <= widthLimit + 0.01 && island.Height <= heightLimit + 0.01;
            }
            Assert(boundedSpring && content.Opacity > 0.99
                && Math.Abs(island.Width - NotchMetrics.ExpandedWidth) < 0.01
                && Math.Abs(island.Height - NotchMetrics.ExpandedHeight) < 0.01,
                "Opening keeps its spring bounce within two percent and lands at the reference dimensions", results);

            LeavePointer(island);
            Assert(!notch.IsExpanded, "Pointer exit starts closing immediately without a hover delay", results);
            await app.Dispatcher.InvokeAsync(notch.UpdateLayout, DispatcherPriority.Render);
            await Task.Delay(65);
            Assert(island.Width < NotchMetrics.ExpandedWidth && island.Height < NotchMetrics.ExpandedHeight,
                "Hover collapse visibly advances before the former 240 ms delay", results);
            EnterPointer(island);
            Assert(notch.IsExpanded, "Reentering during collapse starts reopening immediately", results);
            await Task.Delay(700);
            Assert(notch.IsExpanded && content.Visibility == Visibility.Visible && content.Opacity > 0.99,
                "Reentering during collapse reverses motion and keeps reopened content visible", results);

            notch.Expand(true);
            LeavePointer(island);
            await Task.Delay(300);
            Assert(notch.IsExpanded, "Pointer exit preserves a pinned panel", results);
            if (SystemParameters.ClientAreaAnimation)
                await VerifyButtonMotionAsync(notch, store, results);
            notch.Collapse();
            store.Update(store.Current with { ExpandOnHover = false, ReducedMotion = true });
            await Settle(app);

            notch.Expand(true);
            await Settle(app);
            var pinButton = (System.Windows.Controls.Button)notch.FindName("PinButton");
            var pinSurface = (DependencyObject)pinButton.Template.FindName("Surface", pinButton);
            Assert(content.Opacity == 1 && !InteractionMotion.GetEnabled(pinSurface), "Reduced motion disables panel transitions and button motion", results);
            Assert(notch.IsExpanded && (GetWindowLong(handle, -20) & 0x08000000) == 0, "Pinned panel enables keyboard activation", results);
            // Keep screenshots stable while the real player's metadata changes.
            media.Dispose();
            media.SetSnapshotForTest(new MediaSnapshot());
            await Settle(app);
            Assert(((System.Windows.Controls.TextBlock)notch.FindName("ElapsedLabel")).Text == ""
                && ((System.Windows.Controls.TextBlock)notch.FindName("DurationLabel")).Text == ""
                && !((System.Windows.Controls.Button)notch.FindName("PlayButton")).IsEnabled,
                "Idle media hides unavailable timeline text and disables playback", results);
            Capture(notch, Path.Combine(output, "notch-music-empty.png"));
            media.SetSnapshotForTest(new MediaSnapshot
            {
                HasSession = true, IsPlaying = true, CanToggle = true, CanPrevious = true, CanNext = true, CanSeek = true,
                Title = "Midnight City", Artist = "M83", Artwork = NotchVisualVerification.CreateArtwork(),
                Duration = TimeSpan.FromMinutes(4), Position = TimeSpan.FromSeconds(42)
            });
            await Settle(app);
            Capture(notch, Path.Combine(output, "notch-music.png"));
            notch.SelectTabForTest("files");
            Assert(((System.Windows.Controls.RadioButton)notch.FindName("FilesTab")).IsChecked == true
                && ((FrameworkElement)notch.FindName("FilesPanel")).Visibility == Visibility.Visible,
                "Icon navigation exposes the selected activity", results);
            await Settle(app);
            Capture(notch, Path.Combine(output, "notch-shelf-empty.png"));
            shelf.Add(new[] { file });
            await Settle(app);
            Capture(notch, Path.Combine(output, "notch-shelf.png"));
            notch.SelectTabForTest("focus");
            focus.Start(25);
            await Settle(app);
            Capture(notch, Path.Combine(output, "notch-focus.png"));
            notch.Collapse();
            await Settle(app);
            Assert(!notch.IsExpanded && (GetWindowLong(handle, -20) & 0x08000000) != 0, "Collapse restores nonactivating behavior", results);
            Capture(notch, Path.Combine(output, "notch-timer-compact.png"));
            focus.Reset(25);
            media.SetSnapshotForTest(new MediaSnapshot { HasSession = true, IsPlaying = true, Title = "Sample track", Artist = "Verification fixture" });
            focus.Start(25);
            await Settle(app);
            var bars = (AudioBars)notch.FindName("CompactBars");
            Assert(bars.Visibility == Visibility.Visible && ((FrameworkElement)notch.FindName("CompactTimer")).Visibility == Visibility.Visible,
                "Closed notch shows music bars and focus countdown together", results);
            bars.Update(new float[] { 0.85f, 0.7f, 0.4f, 0.3f, 0.55f, 0.6f, 0.25f, 0.2f, 0.4f, 0.65f, 0.45f, 0.2f, 0.15f }, 0.1, false);
            Capture(notch, Path.Combine(output, "notch-music-timer-compact.png"));
            media.SetSnapshotForTest(new MediaSnapshot { HasSession = true, IsPlaying = false });
            Assert(bars.Visibility == Visibility.Collapsed, "Pausing music stops and hides the audio bars", results);
            media.SetSnapshotForTest(new MediaSnapshot()); focus.Reset(25);

            settingsWindow = new SettingsWindow(store, playerVolume: playerVolume);
            settingsWindow.Show();
            await Settle(app);
            await NotchVisualVerification.VerifySettingsAsync(settingsWindow, store, output,
                (condition, name) => Assert(condition, name, results));
            Capture(settingsWindow, Path.Combine(output, "settings-general.png"));
            settingsWindow.SelectPage("appearance");
            await Settle(app);
            Capture(settingsWindow, Path.Combine(output, "settings-appearance.png"));
            settingsWindow.SelectPage("activities");
            await Settle(app);
            Capture(settingsWindow, Path.Combine(output, "settings-activities.png"));
            settingsWindow.SelectPage("players");
            await Settle(app);
            Capture(settingsWindow, Path.Combine(output, "settings-players.png"));
            settingsWindow.SelectPage("uploads");
            await Settle(app);
            Capture(settingsWindow, Path.Combine(output, "settings-uploads.png"));
            settingsWindow.SelectPage("about");
            await Settle(app);
            Capture(settingsWindow, Path.Combine(output, "settings-about.png"));
            results.Add(new { check = "All settings and activity panels load and render", passed = true });
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { passed = true, results }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString(), results }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
        finally
        {
            settingsWindow?.Close(); notch?.CloseForShutdown();
        }
    }

    private static void VerifyFailureReports(string output, Action<bool, string> assert)
    {
        var fixture = Path.Combine(output, "failure-reports", Guid.NewGuid().ToString("N"));
        foreach (var (entrypoint, filename) in new[]
        {
            ("--self-test", "results.json"),
            ("--control-center-test", "control-center-results.json")
        })
        {
            var directory = Path.Combine(fixture, entrypoint.TrimStart('-'));
            BackgroundVerification.WriteFailure(new[] { entrypoint, "--output", directory },
                new InvalidOperationException("Synthetic isolation failure"));
            using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, filename)));
            assert(!report.RootElement.GetProperty("passed").GetBoolean()
                && report.RootElement.GetProperty("error").GetString()!.Contains("Synthetic isolation failure"),
                $"{entrypoint} writes isolation failures to the report consumed by its launcher");
        }
    }

    private static void Assert(bool condition, string name, List<object> results)
    {
        if (!condition) throw new InvalidOperationException(name);
        results.Add(new { check = name, passed = true });
    }
    private static async Task VerifyButtonMotionAsync(NotchWindow notch, SettingsStore store, List<object> results)
    {
        var settingsBefore = store.Current;
        var button = (System.Windows.Controls.Button)notch.FindName("PinButton");
        var surface = (FrameworkElement)button.Template.FindName("Surface", button);
        var scale = (ScaleTransform)surface.RenderTransform;
        try
        {
            EnterPointer(surface);
            await Task.Delay(200);
            // Handled owner events exercise the production motion subscription.
            Press(UIElement.PreviewMouseLeftButtonDownEvent);
            await WaitUntil(() => Math.Abs(scale.ScaleX - 0.97) < 0.001 && !scale.HasAnimatedProperties, 1000);
            Assert(Math.Abs(scale.ScaleX - 0.97) < 0.01 && !scale.HasAnimatedProperties,
                "A control-local button press gives restrained feedback and releases its settled clocks", results);
            Press(UIElement.PreviewMouseLeftButtonUpEvent);
            await WaitUntil(() => scale.ScaleX == 1 && !scale.HasAnimatedProperties, 1000);
            Assert(Math.Abs(scale.ScaleX - 1) < 0.01 && !scale.HasAnimatedProperties,
                "A handled button release restores its capsule without leaving it pressed", results);
            EnterPointer(surface);
            await Task.Delay(180);
            Assert(scale.ScaleX == 1 && scale.ScaleY == 1 && !scale.HasAnimatedProperties,
                "Pointer hover keeps the released button and its label at native size without animation", results);
            LeavePointer(surface);
            await Task.Delay(180);
            Assert(Math.Abs(scale.ScaleX - 1) < 0.01,
                "Leaving a released button returns its capsule to normal size", results);
            PressKey(Keyboard.PreviewKeyDownEvent);
            await WaitUntil(() => Math.Abs(scale.ScaleX - 0.97) < 0.001 && !scale.HasAnimatedProperties, 1000);
            Assert(Math.Abs(scale.ScaleX - 0.97) < 0.01,
                "Space gives keyboard users the same restrained press feedback as pointer input", results);
            PressKey(Keyboard.PreviewKeyUpEvent);
            await WaitUntil(() => scale.ScaleX == 1 && !scale.HasAnimatedProperties, 1000);
            Assert(scale.ScaleX == 1 && scale.ScaleY == 1 && !scale.HasAnimatedProperties,
                "Keyboard release restores native scale and clears both control clocks", results);
            PressKey(Keyboard.PreviewKeyDownEvent);
            await WaitUntil(() => Math.Abs(scale.ScaleX - 0.97) < 0.001 && !scale.HasAnimatedProperties, 1000);
            button.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,
                Environment.TickCount, button, null)
                { RoutedEvent = Keyboard.LostKeyboardFocusEvent, Handled = true });
            await WaitUntil(() => scale.ScaleX == 1 && !scale.HasAnimatedProperties, 1000);
            Assert(scale.ScaleX == 1 && !scale.HasAnimatedProperties,
                "Leaving keyboard focus during a held press cannot leave a control compressed", results);
            Press(UIElement.PreviewMouseLeftButtonDownEvent);
            Press(UIElement.PreviewMouseLeftButtonUpEvent);
            PressKey(Keyboard.PreviewKeyDownEvent);
            PressKey(Keyboard.PreviewKeyUpEvent);
            await WaitUntil(() => scale.ScaleX == 1 && !scale.HasAnimatedProperties, 1000);
            Assert(scale.ScaleX == 1 && scale.ScaleY == 1 && !scale.HasAnimatedProperties,
                "Rapid same-turn pointer and keyboard reversals settle at the latest released state", results);
            Press(UIElement.PreviewMouseLeftButtonDownEvent);
            await Task.Delay(100);
            store.Update(store.Current with { ReducedMotion = true });
            await Task.Delay(35);
            Assert(!InteractionMotion.GetEnabled(surface) && scale.ScaleX == 1 && scale.ScaleY == 1,
                "Enabling reduced motion during a held press resets button motion immediately", results);
        }
        finally
        {
            store.Update(settingsBefore);
        }

        void Press(RoutedEvent routedEvent) => button.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routedEvent, Handled = true });
        void PressKey(RoutedEvent routedEvent) => button.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice, PresentationSource.FromVisual(button), Environment.TickCount, Key.Space)
            { RoutedEvent = routedEvent, Handled = true });
    }
    private static async Task Settle(App app)
    {
        await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(160);
    }
    private static async Task WaitUntil(Func<bool> condition, int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < milliseconds) await Task.Delay(5);
    }
    private static void EnterPointer(FrameworkElement island)
    {
        island.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseEnterEvent });
    }
    private static void LeavePointer(FrameworkElement island) => island.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseLeaveEvent });
    private static RenderTargetBitmap Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
        return bitmap;
    }
    private static byte GetAlpha(BitmapSource source, int x, int y)
    {
        var pixel = new byte[4];
        source.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[3];
    }

}
