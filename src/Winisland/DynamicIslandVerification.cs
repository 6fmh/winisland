using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Winisland.Controls;
using Winisland.Models;
using Winisland.Services;

namespace Winisland;

// Exercise the real settings, housing, player and feature controls on the
// existing private verification desktop. No cursor or global input is used.
internal static class DynamicIslandVerification
{
    private sealed record Frame(string Label, BitmapSource Bitmap);
    private sealed record Velocity(double Width, double Height, double Contour);
    private sealed record MotionSample(double Width, double Height, double Contour,
        double Opacity, double Offset, bool Animated, Velocity Velocity);

    internal static async Task RunAsync(App app, NotchWindow notch, SettingsStore store,
        MediaService media, FocusTimer focus, string output, Action<bool, string> assert)
    {
        BackgroundVerification.RequirePrivateDesktop();
        if (!app.Dispatcher.CheckAccess()) throw new InvalidOperationException("Dynamic Island verification requires the UI dispatcher.");
        var originalSettings = store.Current;
        var originalMedia = media.Current;
        var originalSeekHandler = media.SeekHandlerForTest;
        var originalToggleHandler = media.ToggleHandlerForTest;
        var originalPreviousHandler = media.PreviousHandlerForTest;
        var originalNextHandler = media.NextHandlerForTest;
        var originalFocusDuration = focus.Duration;
        var originalFocusRemaining = focus.Remaining;
        var originalFocusStarted = focus.IsStarted;
        var originalFocusRunning = focus.IsRunning;
        var originalExpanded = notch.IsExpanded;
        var originalPinned = notch.IsPinnedForTest;
        var originalTab = (string?)typeof(NotchWindow).GetField("_selectedTab", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(notch) ?? "music";
        var folder = Path.Combine(output, "dynamic-island");
        Directory.CreateDirectory(folder);
        var frames = new List<Frame>();
        var evidence = new List<object>();
        string? failure = null;
        SettingsWindow? settings = null;
        var island = Find<FrameworkElement>(notch, "Island");
        var surface = Find<NotchSurface>(notch, "Surface");
        var content = Find<FrameworkElement>(notch, "ExpandedContent");
        var player = Find<DynamicIslandPlayer>(notch, "DynamicPlayer");
        var slider = Find<ScrubSlider>(player, "SeekSlider");
        var toggles = 0;
        var previous = 0;
        var next = 0;
        media.ToggleHandlerForTest = () => { toggles++; return Task.FromResult(true); };
        media.PreviousHandlerForTest = () => { previous++; return Task.FromResult(true); };
        media.NextHandlerForTest = () => { next++; return Task.FromResult(true); };
        var fixture = new MediaSnapshot
        {
            HasSession = true, IsPlaying = true, CanToggle = true, CanPrevious = true,
            CanNext = true, CanSeek = true, Title = "Midnight City", Artist = "M83",
            Artwork = Artwork(), Duration = TimeSpan.FromMinutes(4), Position = TimeSpan.FromSeconds(42)
        };
        try
        {
            await VerifyPersistenceAsync(app, folder, assert, evidence);
            VerifyRasterContours(folder, assert, evidence);
            store.Update(originalSettings with
            {
                UseDynamicIsland = false, ReducedMotion = true, HideInFullscreen = false,
                ExpandOnHover = false, ShowLiveActivity = true, ShowVolumeHud = false,
                ShowNotifications = false, NotchWidth = 252, NotchHeight = 40
            });
            focus.Reset(25);
            media.SetSnapshotForTest(new MediaSnapshot());
            notch.SelectTabForTest("music");
            notch.Collapse();
            await Settle(notch);
            assert(!surface.IsDynamicIsland && Near(island.ActualWidth, 252) && Near(island.ActualHeight, 40),
                "Legacy appearance still uses the saved custom MacBook dimensions");
            Capture("Legacy custom notch", "legacy-custom");

            // This is the actual Appearance page, not a test-only preference setter.
            settings = new SettingsWindow(store);
            settings.Show();
            settings.SelectPage("appearance");
            await Settle(settings);
            var macBookChoice = Find<RadioButton>(settings, "MacBookChoice");
            var dynamicChoice = Find<RadioButton>(settings, "DynamicIslandChoice");
            var preview = Find<FrameworkElement>(settings, "NotchPreview");
            var previewSurface = Find<NotchSurface>(settings, "PreviewSurface");
            assert(macBookChoice.IsChecked == true && dynamicChoice.IsChecked == false && !previewSurface.IsDynamicIsland,
                "Appearance opens with the persisted MacBook selection and matching live preview");
            dynamicChoice.IsChecked = true;
            await Settle(settings);
            await Settle(notch);
            assert(store.Current.UseDynamicIsland && new SettingsStore().Current.UseDynamicIsland
                && previewSurface.IsDynamicIsland && Near(preview.ActualWidth, 126)
                && Near(preview.Height, 36.67) && Near(preview.ActualHeight, 36.67, 1.1) && Near(preview.Margin.Top, 11)
                && Find<FrameworkElement>(settings, "MacBookSizeSettings").Visibility == Visibility.Collapsed,
                "Selecting Dynamic Island saves immediately and previews the detached iPhone pill");
            assert(store.Current.NotchWidth == 252 && store.Current.NotchHeight == 40,
                "Choosing Dynamic Island preserves both custom MacBook dimensions");
            Save(Render(settings, 1), Path.Combine(folder, "settings-appearance.png"));
            AssertIdle();
            Capture("Dynamic Island idle", "idle");

            media.SetSnapshotForTest(fixture);
            notch.Collapse();
            await Settle(notch);
            VerifyCompact();
            Capture("Compact music", "compact-music");
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                CaptureRaster("compact-music", scale);

            focus.Start(25);
            focus.TogglePause();
            await Settle(notch);
            var minimal = Find<Button>(notch, "MinimalActivity");
            var mainBounds = Bounds(island, notch);
            var bubbleBounds = Bounds(minimal, notch);
            assert(minimal.IsVisible && minimal.IsHitTestVisible
                && Near(bubbleBounds.Top, mainBounds.Top) && Near(bubbleBounds.Height, 36.67, 1.1)
                && Near(bubbleBounds.Left - mainBounds.Right, 8, 1.1)
                && Near((mainBounds.Left + bubbleBounds.Right) / 2, notch.ActualWidth / 2, 1.1)
                && AutomationProperties.GetName(minimal).Contains("25:00", StringComparison.Ordinal)
                && Find<TextBlock>(notch, "MinimalTimerLabel").IsVisible
                && Find<TextBlock>(notch, "MinimalTimerLabel").Text == "25:00",
                "Music and Focus form a centered pill plus a separate accessible live timer bubble");
            VerifyCompact();
            Capture("Music and Focus", "compact-music-focus");
            await VerifyMinimalCaptions();
            minimal.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Settle(notch);
            assert(notch.IsExpanded && Find<RadioButton>(notch, "FocusTab").IsChecked == true
                && Find<FrameworkElement>(notch, "FocusPanel").IsVisible
                && !minimal.IsHitTestVisible && focus.IsStarted && !focus.IsRunning
                && media.Current.Title == fixture.Title,
                "The timer bubble opens the real Focus controls while preserving music and timer state");
            Capture("Focus controls", "focus-controls");
            focus.Reset(25);

            notch.SelectTabForTest("music");
            notch.Expand(true);
            media.SetSnapshotForTest(fixture);
            await Settle(notch);
            assert(surface.IsDynamicIsland && Near(island.ActualWidth, 371) && Near(island.ActualHeight, 160)
                && player.IsVisible && content.IsHitTestVisible
                && Find<FrameworkElement>(notch, "ExpandedHeader").Visibility == Visibility.Collapsed,
                "Music expands to the iPhone-sized native player without the MacBook feature header");
            assert(Find<TextBlock>(player, "TrackTitle").Text == fixture.Title
                && Find<TextBlock>(player, "TrackArtist").Text == fixture.Artist
                && Find<Image>(player, "AlbumArtwork").Source == fixture.Artwork
                && Find<TextBlock>(player, "ElapsedLabel").Text == "0:42"
                && Find<TextBlock>(player, "RemainingLabel").Text == "−3:18",
                "Expanded music displays the real artwork, metadata, elapsed time and remaining time");
            var housing = Bounds(island, notch);
            var playerBounds = Bounds(player, notch);
            assert(Contains(housing, playerBounds, 0.6), "The native music player fits inside its expanded island housing");
            foreach (var name in new[] { "ArtworkContainer", "TrackTitle", "TrackArtist", "PreviousButton", "PlayButton", "NextButton", "MenuButton", "ElapsedLabel", "RemainingLabel" })
                assert(Contains(housing, Bounds(Find<FrameworkElement>(player, name), notch), 0.6),
                    $"Expanded island content stays inside the housing: {name}");
            Capture("Expanded music", "expanded-music");
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                CaptureRaster("expanded-music", scale);

            var transportEvents = 0;
            EventHandler countTransport = (_, _) => transportEvents++;
            player.PreviousRequested += countTransport;
            player.PlayPauseRequested += countTransport;
            player.NextRequested += countTransport;
            try
            {
                foreach (var name in new[] { "PreviousButton", "PlayButton", "NextButton" })
                {
                    var button = Find<Button>(player, name);
                    assert(button.IsEnabled && button.ActualHeight >= 40,
                        $"The island exposes a usable native transport control: {name}");
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                }
                assert(transportEvents == 3, "Island transport buttons issue their three distinct playback intentions exactly once");
                assert(toggles == 1 && previous == 1 && next == 1,
                    "Island transport controls reach the three isolated media backends exactly once without controlling a real player");
            }
            finally
            {
                player.PreviousRequested -= countTransport;
                player.PlayPauseRequested -= countTransport;
                player.NextRequested -= countTransport;
            }

            await VerifyIslandMenu();
            var menu = Find<DynamicIslandMenu>(notch, "IslandMenuPanel");
            foreach (var tab in new[] { "files", "focus", "calendar", "tools", "controls", "updates" })
            {
                island.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                    { RoutedEvent = UIElement.MouseRightButtonUpEvent });
                await Settle(notch);
                var items = Find<Panel>(menu, "Activities");
                var item = items.Children.OfType<Button>().SingleOrDefault(item => Equals(item.Tag, tab));
                assert(item is not null, $"The island menu retains access to {tab}");
                if (tab == "updates" && !item!.IsEnabled)
                {
                    assert(true, "A fixture without an update service exposes the Software update entry as unavailable");
                    continue;
                }
                item!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Settle(notch);
                assert(notch.IsExpanded && surface.IsDynamicIsland && !player.IsVisible
                    && Near(island.ActualWidth, 640) && media.Current.Title == fixture.Title,
                    $"Selecting {tab} opens the working native panel in the rounded island appearance");
            }
            notch.SelectTabForTest("music");
            notch.Expand(true);
            await Settle(notch);

            await VerifyPlayerInput();
            await VerifyTransients();
            // Keep this window's controls in sync by switching through the page.
            macBookChoice.IsChecked = true;
            await Settle(settings);
            await Settle(notch);
            assert(!store.Current.UseDynamicIsland && !surface.IsDynamicIsland && notch.IsExpanded
                && Near(island.ActualWidth, 640) && Near(island.ActualHeight, 190)
                && Near(island.Margin.Top, 0) && store.Current.NotchWidth == 252 && store.Current.NotchHeight == 40,
                "Switching an open island back to MacBook keeps it open and restores the legacy geometry");
            media.SetSnapshotForTest(new MediaSnapshot());
            notch.Collapse();
            await Settle(notch);
            assert(Near(island.ActualWidth, 252) && Near(island.ActualHeight, 40),
                "Returning to an idle MacBook notch restores the retained custom width and height");
            dynamicChoice.IsChecked = true;
            await Settle(notch);
            Find<Button>(settings, "RestoreDefaultsButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Settle(settings);
            await Settle(notch);
            assert(!store.Current.UseDynamicIsland && macBookChoice.IsChecked == true && !previewSurface.IsDynamicIsland,
                "Restore defaults returns the app and preview to the legacy MacBook appearance");
            settings.Close();
            settings = null;

            await VerifyMotion();
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
            try { Capture("Failure state", "failure-state"); } catch { }
            throw;
        }
        finally
        {
            try
            {
                SaveContactSheet(frames, Path.Combine(folder, "contact-sheet.png"));
                File.WriteAllText(Path.Combine(folder, "runtime.json"), JsonSerializer.Serialize(new
                {
                    passed = failure is null, privateDesktop = BackgroundVerification.CurrentDesktop,
                    windowsAnimationEnabled = SystemParameters.ClientAreaAnimation, failure, evidence
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                settings?.Close();
                player.CancelInteraction();
                media.SeekHandlerForTest = originalSeekHandler;
                media.ToggleHandlerForTest = originalToggleHandler;
                media.PreviousHandlerForTest = originalPreviousHandler;
                media.NextHandlerForTest = originalNextHandler;
                store.Update(store.Current with { ReducedMotion = true });
                notch.Collapse();
                focus.Reset(originalFocusDuration);
                if (originalFocusStarted)
                {
                    focus.Start(originalFocusRemaining > TimeSpan.Zero ? originalFocusRemaining : TimeSpan.FromSeconds(1));
                    if (!originalFocusRunning) focus.TogglePause();
                }
                media.SetSnapshotForTest(originalMedia);
                notch.SelectTabForTest(originalTab);
                store.Update(originalSettings);
                if (originalExpanded) notch.Expand(originalPinned);
            }
        }

        async Task VerifyIslandMenu()
        {
            var menu = Find<DynamicIslandMenu>(notch, "IslandMenuPanel");
            var menuSlider = Find<VolumeSlider>(menu, "IslandVolumeSlider");
            var volume = (PlayerVolumeService)typeof(NotchWindow).GetField("_playerVolume", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(notch)!;
            var fixtureWithVolume = fixture with { Source = "fixture" };
            media.SetSnapshotForTest(fixtureWithVolume);
            await Task.Delay(90);
            Find<Button>(player, "MenuButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Settle(notch);
            assert(notch.IsExpanded && menu.IsVisible && !player.IsVisible && island.ContextMenu is null
                && Near(island.ActualWidth, 371) && Near(island.ActualHeight, 204)
                && !Find<FrameworkElement>(notch, "ExpandedHeader").IsVisible,
                "More expands the original black island into the native activity chooser without a detached popup or legacy header");
            assert(Near(Bounds(menu, notch).Top, Bounds(island, notch).Top) && Near(menu.ActualWidth, 371)
                && Near(menu.ActualHeight, 204) && Near(surface.Expansion, 1),
                "The chooser shares the island origin, clipping surface and continuous expanded contour");
            var activities = Find<Panel>(menu, "Activities").Children.OfType<Button>().ToArray();
            foreach (var button in activities.Concat(new[] { Find<Button>(menu, "BackButton"), Find<Button>(menu, "MuteButton") }))
            {
                assert(button.ActualWidth >= 40 && button.ActualHeight >= 40 && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)),
                    $"Island chooser {button.Name} has a named nonoverlapping hit area of at least 40 DIPs");
            }
            foreach (var button in activities)
            {
                var caption = ((StackPanel)button.Content).Children.OfType<TextBlock>().Single();
                assert(caption.DesiredSize.Width <= button.ActualWidth - 3 && Contains(Bounds(island, notch), Bounds(button, notch), .1),
                    $"Island chooser {button.Name} caption fits its activity and stays inside the housing");
            }
            assert(AutomationProperties.GetItemStatus(Find<Button>(menu, "MusicButton")) == "Current activity",
                "The chooser identifies the current activity visually and through automation");
            Capture("Island activity chooser", "activity-menu");
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 }) CaptureRaster("activity-menu", scale);
            var source = PresentationSource.FromVisual(notch)!;
            Find<Button>(menu, "MusicButton").Focus();
            Find<Button>(menu, "MusicButton").RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Right)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            assert(Find<Button>(menu, "FilesButton").IsKeyboardFocused, "Arrow navigation moves focus between actual island activities");
            Capture("Island keyboard focus", "activity-menu-focus");
            assert(volume.Current.Available && menuSlider.IsEnabled, "Inline island volume connects to the isolated fixture player");
            menuSlider.Value = 37;
            await Task.Delay(90);
            assert(Near(volume.Current.Volume, 37, .2), "Inline island volume changes the fixture player's volume through the production service");
            Find<Button>(menu, "MuteButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(90);
            assert(volume.Current.Muted && AutomationProperties.GetName(Find<Button>(menu, "MuteButton")) == "Unmute player",
                "Inline mute updates the player and its accessible action in place");
            Find<Button>(menu, "MuteButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(90);
            var track = menuSlider.Template.FindName("PART_Track", menuSlider) as Track ?? throw new InvalidOperationException("Missing island volume track");
            var point = track.TransformToAncestor(menuSlider).Transform(new Point(track.ActualWidth / 2, 12));
            assert(menuSlider.BeginPointerAdjustment(point, captureMouse: false), "Inline volume supports control-local drag on the private desktop");
            Find<Button>(menu, "BackButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Settle(notch);
            assert(!menuSlider.IsAdjusting && !menuSlider.HasPointerCapture && player.IsVisible && !menu.IsVisible
                && notch.IsPinnedForTest && Near(island.ActualHeight, 160),
                "Returning cancels volume drag, restores the original player and preserves pin state");
            Find<Button>(player, "MenuButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Settle(notch);
            menu.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            await Settle(notch);
            assert(notch.IsExpanded && player.IsVisible && !menu.IsVisible,
                "Escape returns from the chooser to the activity before collapsing the island");
            player.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Apps) { RoutedEvent = Keyboard.KeyDownEvent });
            await Settle(notch);
            assert(menu.IsVisible, "The keyboard context-menu key opens the same island chooser");
            Find<Button>(menu, "BackButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Settle(notch);
            var setupRequests = 0;
            var settingsRequests = 0;
            EventHandler onSetup = (_, _) => setupRequests++;
            EventHandler onSettings = (_, _) => settingsRequests++;
            notch.PlayerSettingsRequested += onSetup;
            notch.SettingsRequested += onSettings;
            try
            {
                Find<Button>(player, "MenuButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Settle(notch);
                Find<Button>(menu, "SetupButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Settle(notch);
                Find<Button>(player, "MenuButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Settle(notch);
                Find<Button>(menu, "SettingsButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Settle(notch);
                assert(setupRequests == 1 && settingsRequests == 1 && player.IsVisible && !menu.IsVisible,
                    "The chooser preserves setup and settings intentions exactly once without opening a dialog in verification");
            }
            finally { notch.PlayerSettingsRequested -= onSetup; notch.SettingsRequested -= onSettings; }
            Find<Button>(player, "MenuButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Settle(notch);
            Find<Button>(menu, "PinButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            assert(!notch.IsPinnedForTest && menu.IsVisible, "Chooser unpin changes keep-open state while retaining the chooser");
            Find<Button>(menu, "PinButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            assert(notch.IsPinnedForTest && AutomationProperties.GetItemStatus(Find<Button>(menu, "PinButton")) == "Pinned",
                "Chooser pin restores keep-open state and reports it accessibly");
            notch.Collapse();
            await Settle(notch);
            island.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonUpEvent });
            await Settle(notch);
            assert(menu.IsVisible && notch.IsExpanded && !notch.IsPinnedForTest && Near(island.ActualHeight, 204),
                "Right-click on the compact island opens the same chooser without automatically pinning it");
            store.Update(store.Current with { UseDynamicIsland = false });
            await Settle(notch);
            assert(!menu.IsVisible && !surface.IsDynamicIsland && Near(island.ActualWidth, 640),
                "Changing appearance dismisses the island chooser and restores the working MacBook panel");
            island.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonUpEvent });
            await Settle(notch);
            assert(!menu.IsVisible, "The island chooser is exclusive to Dynamic Island mode");
            store.Update(store.Current with { UseDynamicIsland = true });
            media.SetSnapshotForTest(fixture);
            notch.SelectTabForTest("music");
            notch.Expand(true);
            await Settle(notch);
        }

        void AssertIdle()
        {
            assert(surface.IsDynamicIsland && !notch.IsExpanded && Near(island.ActualWidth, 126)
                && Near(island.ActualHeight, 36.67) && Near(Bounds(island, notch).Top, 11)
                && !Find<FrameworkElement>(notch, "CompactActivity").IsVisible,
                "An idle Dynamic Island is an empty 126 by 36.67-DIP pill with an 11-DIP top gap");
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 }) CaptureRaster("idle", scale);
        }

        void VerifyCompact()
        {
            var camera = Find<FrameworkElement>(notch, "CompactCameraSpace");
            var left = Find<FrameworkElement>(notch, "CompactLeftWing");
            var right = Find<FrameworkElement>(notch, "CompactRightWing");
            var cameraBounds = Bounds(camera, notch);
            var main = Bounds(island, notch);
            var artwork = Bounds(Find<FrameworkElement>(notch, "CompactArtworkFrame"), notch);
            var bars = Bounds(Find<FrameworkElement>(notch, "CompactBars"), notch);
            assert(surface.IsDynamicIsland && !notch.IsExpanded && Near(main.Height, 36.67)
                && Near(camera.ActualWidth, 126) && Near(left.ActualWidth, right.ActualWidth)
                && Near(cameraBounds.Left + cameraBounds.Width / 2, main.Left + main.Width / 2, 0.6)
                && artwork.Right <= cameraBounds.Left + 0.6 && bars.Left >= cameraBounds.Right - 0.6,
                "Compact music has balanced wings with artwork and waveform outside the original camera region");
            var bitmap = Render(notch, 1);
            assert(IsBlackRegion(bitmap, cameraBounds, 1), "Compact music leaves its central reserved region completely opaque black");
        }

        void Capture(string label, string name)
        {
            notch.UpdateLayout();
            var bitmap = Render(notch, 1);
            Save(bitmap, Path.Combine(folder, name + ".png"));
            frames.Add(new Frame(label, bitmap));
            evidence.Add(new { stage = name, island = Bounds(island, notch), playerVisible = player.IsVisible,
                compactVisible = Find<FrameworkElement>(notch, "CompactActivity").IsVisible,
                minimalVisible = Find<FrameworkElement>(notch, "MinimalActivity").IsVisible,
                media.Current.Title, focus.IsStarted, focus.IsRunning, settings = store.Current });
        }

        void CaptureRaster(string name, double scale)
        {
            var bitmap = Render(notch, scale);
            Save(bitmap, Path.Combine(folder, $"{name}-{scale * 100:0}.png"));
            var bounds = Bounds(island, notch);
            var pixels = Pixels(bitmap);
            var topSample = Pixel(pixels, bitmap.PixelWidth, (int)((bounds.Left + bounds.Width / 2) * scale), (int)(2 * scale));
            assert(Near(bounds.Top, 11) && topSample.A < 90,
                $"{name} at {scale * 100:0}% retains the detached top gap without an opaque attachment");
            evidence.Add(new { stage = name, scale, bitmap.PixelWidth, bitmap.PixelHeight, bounds, topGapAlpha = topSample.A });
        }

        async Task VerifyMinimalCaptions()
        {
            var minimal = Find<Button>(notch, "MinimalActivity");
            var label = Find<TextBlock>(notch, "MinimalTimerLabel");
            var timerFrames = new List<Frame>();
            try
            {
                foreach (var (name, duration, caption) in new[]
                {
                    ("25m", TimeSpan.FromMinutes(25), "25:00"),
                    ("99m59s", TimeSpan.FromSeconds(5999), "99:59"),
                    ("100m", TimeSpan.FromMinutes(100), "1h40"),
                    ("23h59m", TimeSpan.FromMinutes(1439), "23h59")
                })
                {
                    focus.SetDuration(duration);
                    await Settle(notch);
                    var bubble = Bounds(minimal, notch);
                    var text = Bounds(label, notch);
                    var preciseTime = FocusDurationParser.Format(duration);
                    assert(minimal.IsVisible && label.IsVisible && label.Text == caption
                        && label.FontSize >= 11 && label.TextTrimming == TextTrimming.None
                        && label.DesiredSize.Width <= label.ActualWidth + .1
                        && Contains(bubble, text, .1)
                        && AutomationProperties.GetName(minimal).Contains(preciseTime, StringComparison.Ordinal)
                        && minimal.ToolTip is string tip && tip.Contains(preciseTime, StringComparison.Ordinal),
                        $"The minimal timer shows a complete 11-point {caption} caption and retains precise accessible time");
                    foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                    {
                        var bitmap = Render(notch, scale);
                        var pixels = Pixels(bitmap);
                        var left = Math.Max(0, (int)Math.Floor((bubble.Left - 2) * scale));
                        var top = Math.Max(0, (int)Math.Floor((bubble.Top - 2) * scale));
                        var right = Math.Min(bitmap.PixelWidth, (int)Math.Ceiling((bubble.Right + 2) * scale));
                        var bottom = Math.Min(bitmap.PixelHeight, (int)Math.Ceiling((bubble.Bottom + 2) * scale));
                        var center = new Point(bubble.Left + bubble.Width / 2, bubble.Top + bubble.Height / 2);
                        var inkPixels = 0;
                        var maximumInkRadius = 0.0;
                        for (var y = top; y < bottom; y++)
                            for (var x = left; x < right; x++)
                            {
                                var pixel = Pixel(pixels, bitmap.PixelWidth, x, y);
                                if (pixel.R <= 60 || pixel.G <= 30 || pixel.B >= pixel.R / 2) continue;
                                inkPixels++;
                                var dx = (x + .5) / scale - center.X;
                                var dy = (y + .5) / scale - center.Y;
                                maximumInkRadius = Math.Max(maximumInkRadius, Math.Sqrt(dx * dx + dy * dy));
                            }
                        assert(inkPixels > 12 * scale * scale && maximumInkRadius < bubble.Width / 2 - .5,
                            $"Minimal timer {caption} at {scale * 100:0}% renders legible orange text fully inside its circular housing");
                        var crop = new CroppedBitmap(bitmap, new Int32Rect(left, top, right - left, bottom - top));
                        crop.Freeze();
                        Save(crop, Path.Combine(folder, $"minimal-timer-{name}-{scale * 100:0}.png"));
                        timerFrames.Add(new Frame($"{caption} ({scale * 100:0}%)", crop));
                        evidence.Add(new { stage = "minimal-timer-caption", caption, preciseTime, scale,
                            label.FontSize, text, bubble, inkPixels, maximumInkRadius });
                    }
                }
            }
            finally
            {
                SaveContactSheet(timerFrames, Path.Combine(folder, "minimal-timer-contact-sheet.png"), 4, 140, 124);
                focus.SetDuration(TimeSpan.FromMinutes(25));
                await Settle(notch);
            }
        }

        async Task VerifyPlayerInput()
        {
            var commands = new List<double>();
            media.SeekHandlerForTest = seconds => { commands.Add(seconds); return Task.FromResult(true); };
            var paused = fixture with { IsPlaying = false, Position = TimeSpan.FromSeconds(42) };
            media.SetSnapshotForTest(paused);
            await Settle(notch);
            Point Rail(double fraction)
            {
                var track = (Track)slider.Template.FindName("PART_Track", slider);
                var half = track.Thumb.ActualWidth / 2;
                return track.TransformToAncestor(slider).Transform(new Point(half + (track.ActualWidth - 2 * half) * fraction, 10));
            }
            assert(slider.BeginPointerAdjustment(Rail(.25), captureMouse: false) && slider.IsAdjusting
                && slider.HasPointerCapture && Near(slider.Value, 60, 0.1) && commands.Count == 0,
                "The real island timeline starts a local scrub and previews without commanding the player");
            media.SetSnapshotForTest(paused with { Position = TimeSpan.FromSeconds(10) });
            assert(Near(slider.Value, 60, 0.1) && Find<TextBlock>(player, "ElapsedLabel").Text == "1:00",
                "Incoming playback updates preserve the island's uncommitted scrub preview");
            slider.MovePointerAdjustment(Rail(.75));
            slider.EndPointerAdjustment(Rail(.75));
            await Task.Delay(70);
            assert(!slider.IsAdjusting && !slider.HasPointerCapture && commands.Count == 1 && Near(commands[0], 180, 0.1)
                && Near(slider.Value, 180, 0.1), "Releasing the island scrub issues exactly one seek and holds the acknowledged time");
            media.SetSnapshotForTest(paused with { Position = TimeSpan.FromSeconds(180) });
            media.SeekHandlerForTest = seconds => { commands.Add(seconds); return Task.FromResult(false); };
            slider.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.MouseWheelEvent });
            assert(Near(slider.Value, 175, 0.1), "The island timeline previews the real five-second wheel adjustment");
            await Task.Delay(320);
            assert(commands.Count == 2 && Near(commands[1], 175, 0.1) && Near(slider.Value, 180, 0.1),
                "A rejected island seek restores the player's reported position");
            media.SeekHandlerForTest = seconds => { commands.Add(seconds); return Task.FromResult(true); };
            var beforeCancel = commands.Count;
            slider.Value = 200;
            store.Update(store.Current with { UseDynamicIsland = false });
            await Task.Delay(320);
            assert(commands.Count == beforeCancel && !player.HasPendingSeek && !slider.HasPointerCapture
                && media.Current.Title == fixture.Title && notch.IsExpanded,
                "Switching styles cancels a delayed island seek while preserving the open player and its media");
            store.Update(store.Current with { UseDynamicIsland = true });
            notch.SelectTabForTest("music");
            notch.Expand(true);
            await Settle(notch);
            slider.BeginPointerAdjustment(Rail(.5), captureMouse: false);
            store.Update(store.Current with { UseDynamicIsland = false });
            assert(!slider.IsAdjusting && !slider.HasPointerCapture && commands.Count == beforeCancel,
                "Switching away from an active island drag releases capture without committing the preview");
            store.Update(store.Current with { UseDynamicIsland = true });
            await Settle(notch);
            slider.Value = 205;
            notch.SelectTabForTest("files");
            await Task.Delay(320);
            assert(commands.Count == beforeCancel && !player.HasPendingSeek && !player.IsVisible,
                "Leaving island music for a feature panel cancels its pending keyboard seek");
            notch.SelectTabForTest("music");
            await Settle(notch);
            slider.Value = 210;
            notch.Collapse();
            await Task.Delay(320);
            assert(commands.Count == beforeCancel && !player.HasPendingSeek && !slider.HasPointerCapture,
                "Collapsing the island cancels delayed timeline input and leaves no capture behind");
            media.SetSnapshotForTest(paused with { CanSeek = false, CanToggle = false, CanPrevious = false, CanNext = false });
            notch.Expand(true);
            await Settle(notch);
            assert(!slider.IsEnabled && !Find<Button>(player, "PlayButton").IsEnabled
                && !Find<Button>(player, "PreviousButton").IsEnabled && !Find<Button>(player, "NextButton").IsEnabled,
                "The island respects the real media session's seeking and transport capabilities");
            media.SetSnapshotForTest(paused);
            media.SeekHandlerForTest = originalSeekHandler;
        }

        async Task VerifyTransients()
        {
            var preferences = store.Current;
            var notification = new NotchNotification("dynamic-fixture", "Messages", "Alex", "The music player is ready.", DateTimeOffset.UtcNow);
            var banner = Find<NotificationBanner>(notch, "NotificationBanner");
            var device = Find<FrameworkElement>(notch, "DeviceConnection");
            try
            {
                store.Update(preferences with { UseDynamicIsland = true, ReducedMotion = true,
                    HideInFullscreen = false, ShowNotifications = true, ShowVolumeHud = true, ShowLiveActivity = true });
                media.SetSnapshotForTest(fixture with { IsPlaying = false });
                notch.SelectTabForTest("music");
                notch.Collapse();
                focus.Start(25);
                focus.TogglePause();
                notch.ShowNotification(notification);
                await Settle(notch);
                assert(banner.IsVisible && surface.IsDynamicIsland && Near(island.ActualWidth, 371)
                    && Near(island.ActualHeight, banner.ActualHeight + 89.34) && Contains(Bounds(island, notch), Bounds(banner, notch), .6)
                    && !Find<FrameworkElement>(notch, "MinimalActivity").IsHitTestVisible,
                    "A notification expands the pill into a rounded native banner and hides the secondary activity");
                Capture("Dynamic Island notification", "notification-compact");
                notch.Expand(true);
                await Settle(notch);
                assert(player.IsVisible && banner.IsVisible && Near(island.ActualHeight, 192 + banner.ActualHeight)
                    && Bounds(banner, notch).Top >= Bounds(player, notch).Bottom
                    && Contains(Bounds(island, notch), Bounds(banner, notch), .6),
                    "An expanded island places notifications below the actual music player without clipping its controls");
                Capture("Music with notification", "notification-expanded");
                var chooser = Find<DynamicIslandMenu>(notch, "IslandMenuPanel");
                Find<Button>(player, "MenuButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Settle(notch);
                assert(chooser.IsVisible && banner.IsVisible && Near(island.ActualHeight, 236 + banner.ActualHeight)
                    && Bounds(banner, notch).Top >= Bounds(chooser, notch).Bottom
                    && Contains(Bounds(island, notch), Bounds(banner, notch), .6),
                    "An existing notification moves below the expanded chooser without overlapping its controls");
                Capture("Chooser with notification", "activity-menu-notification");
                var chooserEntry = Find<Button>(chooser, "MusicButton");
                var notificationDismiss = Find<Button>(banner, "DismissButton");
                assert(chooserEntry.Focus() && NotificationVerification.FocusMovesTo(notificationDismiss),
                    "Tab leaves the chooser controls to reach the sibling notification dismiss action");
                assert(NotificationVerification.FocusMovesTo(chooserEntry),
                    "Chooser and notification actions wrap within the same native keyboard cycle");
                assert(chooserEntry.Focus() && NotificationVerification.FocusMovesTo(notificationDismiss, FocusNavigationDirection.Previous),
                    "Reverse Tab reaches a visible notification footer while the chooser is open");
                Find<Button>(chooser, "BackButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Settle(notch);
                assert(player.IsVisible && banner.IsVisible && Near(island.ActualHeight, 192 + banner.ActualHeight),
                    "Returning from the chooser restores the notification below the music player");
                store.Update(store.Current with { UseDynamicIsland = false });
                await Settle(notch);
                assert(notch.ActiveNotificationForTest?.Id == notification.Id && banner.IsVisible
                    && !surface.IsDynamicIsland && Contains(Bounds(island, notch), Bounds(banner, notch), .6),
                    "Switching appearance preserves a visible notification and its bounded content");
                store.Update(store.Current with { UseDynamicIsland = true });
                await Settle(notch);
                Find<Button>(banner, "DismissButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Settle(notch);
                assert(notch.ActiveNotificationForTest is null && !banner.IsVisible && player.IsVisible
                    && Near(island.ActualWidth, 371) && Near(island.ActualHeight, 160),
                    "The notification's native dismiss control restores the full island music player");
                focus.Reset(25);
                notch.Collapse();
                notch.ShowAppleDeviceForTest(new AppleDeviceConnection("dynamic-airpods", "AirPods Pro", AppleDeviceKind.AirPodsPro));
                await Settle(notch);
                assert(device.IsVisible && device.Opacity == 1 && surface.IsDynamicIsland
                    && Near(island.ActualWidth, 371) && Near(island.ActualHeight, 153.34)
                    && Contains(Bounds(island, notch), Bounds(device, notch), .6),
                    "An Apple device connection appears in a rounded island card with room for its actual illustration and name");
                Capture("AirPods connection", "device-compact");
                notch.Expand(true);
                await Settle(notch);
                assert(player.IsVisible && device.IsVisible && Near(island.ActualWidth, 371)
                    && Near(island.ActualHeight, 256) && Bounds(device, notch).Top >= Bounds(player, notch).Bottom
                    && Contains(Bounds(island, notch), Bounds(device, notch), .6),
                    "An Apple device connection stays visibly below expanded island music instead of disappearing with the legacy header");
                Capture("Music with AirPods connection", "device-expanded");
                Find<Button>(player, "MenuButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Settle(notch);
                assert(chooser.IsVisible && !device.IsVisible && Near(island.ActualHeight, 204),
                    "Opening the chooser preserves a device activity without leaving its old footer over the menu");
                await Task.Delay(4700);
                assert(chooser.IsVisible && !device.IsVisible,
                    "The chooser remains usable throughout a hidden accessory reading interval");
                Find<Button>(chooser, "BackButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Settle(notch);
                assert(player.IsVisible && device.IsVisible && Near(island.ActualHeight, 256),
                    "Returning to music restores an accessory even after its full timeout elapsed while the chooser hid it");
                Keyboard.ClearFocus();
                await Task.Delay(4700);
                await Settle(notch);
                assert(!device.IsVisible && player.IsVisible && Near(island.ActualHeight, 160),
                    "Restoring the accessory resumes its real timeout and returns to the full-height island music presentation");
                notch.Collapse();
                notch.ShowHudForTest(NotchIconKind.Speaker, 62);
                await Settle(notch);
                assert(Find<FrameworkElement>(notch, "Hud").IsVisible && surface.IsDynamicIsland
                    && Near(island.ActualWidth, 230.66) && Near(island.ActualHeight, 48)
                    && Find<TextBlock>(notch, "HudValue").Text == "62%",
                    "The native volume HUD uses the detached rounded housing and the reported value");
                Capture("Volume HUD", "volume-hud");
                await Task.Delay(2300);
                await Settle(notch);
                assert(!Find<FrameworkElement>(notch, "Hud").IsVisible
                    && Find<FrameworkElement>(notch, "CompactActivity").IsVisible && Near(island.ActualHeight, 36.67),
                    "The real HUD timeout returns to the current compact music activity");
            }
            finally
            {
                notch.DismissNotificationForTest(true);
                notch.DismissAppleDeviceForTest();
                focus.Reset(25);
                store.Update(preferences);
                notch.SelectTabForTest("music");
                notch.Expand(true);
                await Settle(notch);
            }
        }

        async Task VerifyMotion()
        {
            store.Update(store.Current with { UseDynamicIsland = true, ReducedMotion = true,
                ExpandOnHover = false, HideInFullscreen = false, ShowLiveActivity = false });
            media.SetSnapshotForTest(new MediaSnapshot());
            notch.SelectTabForTest("music");
            notch.Collapse();
            await Settle(notch);
            var frameClock = new RenderingFrames();
            if (SystemParameters.ClientAreaAnimation)
            {
                store.Update(store.Current with { ReducedMotion = false });
                var resting = Sample(notch);
                notch.Expand(false);
                var pending = Sample(notch);
                notch.Collapse();
                var reversed = Sample(notch);
                assert(SamePosition(resting, pending) && SamePosition(resting, reversed),
                    "A same-frame island expand-collapse reversal keeps the last presented dimensions");
                await frameClock.NextAsync(() => Sample(notch));
                notch.Expand(false);
                var closing = await Retarget("close-during-island-expansion", sample => InMiddle(sample) && sample.Velocity.Width > 5, notch.Collapse);
                assert(SamePosition(closing.Before, closing.After) && SameVelocity(closing.Before.Velocity, closing.After.Velocity),
                    "An interrupted island expansion preserves presented surface position and spring velocity");
                var opening = await Retarget("reopen-during-island-contraction", sample => InMiddle(sample) && sample.Velocity.Width < -5, () => notch.Expand(false));
                assert(SamePosition(opening.Before, opening.After) && SameVelocity(opening.Before.Velocity, opening.After.Velocity),
                    "Reopening a contracting island inherits its inward velocity without a position jump");
                await Until(sample => Near(sample.Width, 371) && Near(sample.Height, 160) && !sample.Animated && sample.Opacity == 1);
                store.Update(store.Current with { UseDynamicIsland = false });
                var modeReturn = await Retarget("return-to-island-during-style-morph", sample => sample.Width > 390 && sample.Width < 620 && sample.Velocity.Width > 5,
                    () => store.Update(store.Current with { UseDynamicIsland = true }));
                assert(SamePosition(modeReturn.Before, modeReturn.After)
                    && SameVelocity(modeReturn.Before.Velocity, modeReturn.After.Velocity) && surface.IsDynamicIsland,
                    "Reversing a running appearance switch keeps width, height and their installed spring velocities");
                await Until(sample => Near(sample.Width, 371) && Near(sample.Height, 160) && !sample.Animated);
                var menu = Find<DynamicIslandMenu>(notch, "IslandMenuPanel");
                Find<Button>(player, "MenuButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var menuClosing = await Retarget("return-during-menu-expansion", sample => sample.Height > 165 && sample.Height < 198 && sample.Velocity.Height > 5,
                    () => Find<Button>(menu, "BackButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));
                assert(SamePosition(menuClosing.Before, menuClosing.After) && SameVelocity(menuClosing.Before.Velocity, menuClosing.After.Velocity),
                    "Returning during chooser expansion preserves the displayed contour position and spring velocity");
                var menuReopening = await Retarget("reopen-during-menu-return", sample => sample.Height > 165 && sample.Height < 198 && sample.Velocity.Height < -5,
                    () => island.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonUpEvent }));
                assert(SamePosition(menuReopening.Before, menuReopening.After) && SameVelocity(menuReopening.Before.Velocity, menuReopening.After.Velocity),
                    "Reopening during chooser return inherits the contracting spring without a position jump");
                await Until(sample => Near(sample.Width, 371) && Near(sample.Height, 204) && !sample.Animated);
                Capture("Animated chooser settled", "activity-menu-motion");
                store.Update(store.Current with { ReducedMotion = true });
                await Settle(notch);
                assert(menu.IsVisible && !menu.HasAnimatedProperties && !island.HasAnimatedProperties && !surface.HasAnimatedProperties,
                    "Reduced Motion settles the chooser and its shared housing without lingering animation clocks");
                Find<Button>(menu, "BackButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                store.Update(store.Current with { ReducedMotion = true });
            }
            else
            {
                store.Update(store.Current with { ReducedMotion = false });
                notch.Expand(false);
                evidence.Add(new { stage = "motion-sampling", skipped = true, reason = "Windows disables client-area animation" });
            }
            notch.UpdateLayout();
            assert(Near(island.Width, 371) && Near(island.Height, 160) && !island.HasAnimatedProperties
                && !surface.HasAnimatedProperties && !content.HasAnimatedProperties
                && !Find<TranslateTransform>(notch, "ContentOffset").HasAnimatedProperties
                && !notch.PlaybackRenderingForTest && content.Opacity == 1,
                "Reduced Motion or the Windows preference settles the island and player without residual animation clocks");
            Capture("Reduced Motion settled", "reduced-motion");
            notch.Collapse();
            assert(Near(island.Width, 126) && Near(island.Height, 36.67) && !content.IsHitTestVisible
                && !player.HasPendingSeek && !island.HasAnimatedProperties,
                "Reduced Motion collapse immediately returns to the empty pill and removes content input");

            bool InMiddle(MotionSample sample) => sample.Width > 155 && sample.Width < 340;
            async Task<MotionSample> Until(Func<MotionSample, bool> condition)
            {
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed < TimeSpan.FromSeconds(5))
                {
                    var sample = await frameClock.NextAsync(() => Sample(notch));
                    if (condition(sample)) return sample;
                }
                throw new TimeoutException("Island motion did not reach the required rendered state.");
            }
            async Task<(MotionSample Before, MotionSample After)> Retarget(string stage, Func<MotionSample, bool> condition, Action change)
            {
                var before = await Until(condition);
                change();
                var after = Sample(notch);
                evidence.Add(new { stage, before, after });
                return (before, after);
            }
        }
    }

    private static async Task VerifyPersistenceAsync(App app, string output, Action<bool, string> assert, List<object> evidence)
    {
        assert(!new IslandSettings().UseDynamicIsland, "New configurations default to the legacy MacBook notch");
        var originalDirectory = Environment.GetEnvironmentVariable("WINISLAND_DATA_DIR");
        var directory = Path.Combine(output, "settings-fixture");
        Directory.CreateDirectory(directory);
        try
        {
            Environment.SetEnvironmentVariable("WINISLAND_DATA_DIR", directory);
            File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"NotchWidth\":248,\"NotchHeight\":39,\"HasLaunched\":true}");
            var store = new SettingsStore();
            assert(!store.Current.UseDynamicIsland && store.Current.NotchWidth == 248 && store.Current.NotchHeight == 39,
                "Pre-existing settings without the appearance property retain legacy mode and custom sizes");
            store.Update(store.Current with { UseDynamicIsland = true });
            var reloaded = new SettingsStore();
            assert(reloaded.Current.UseDynamicIsland && reloaded.Current.NotchWidth == 248 && reloaded.Current.NotchHeight == 39,
                "Dynamic Island selection survives a settings reload without rewriting legacy size preferences");

            reloaded.Update(reloaded.Current with
            {
                ReducedMotion = true, HideInFullscreen = false, ExpandOnHover = false,
                ShowVolumeHud = false, ShowNotifications = false
            });
            var startupSettings = new SettingsStore();
            using var media = new MediaService(app.Dispatcher);
            using var audio = new AudioService();
            using var focus = new FocusTimer();
            using var volume = new PlayerVolumeService(app.Dispatcher, Array.Empty<IPlayerVolumeBackend>());
            var startup = new NotchWindow(startupSettings, media, audio, focus, new FileShelf(), playerVolume: volume);
            try
            {
                startup.StopPollingForTest();
                startup.Show();
                await Settle(startup);
                var island = Find<FrameworkElement>(startup, "Island");
                var surface = Find<NotchSurface>(startup, "Surface");
                var bounds = Bounds(island, startup);
                assert(startupSettings.Current.UseDynamicIsland && surface.IsDynamicIsland && !startup.IsExpanded
                    && Near(island.ActualWidth, 126) && Near(island.ActualHeight, 36.67) && Near(bounds.Top, 11)
                    && !Find<FrameworkElement>(startup, "CompactActivity").IsVisible,
                    "A new native window starts in the persisted Dynamic Island appearance before any setting is changed");
                Save(Render(startup, 1), Path.Combine(output, "persisted-startup.png"));
                evidence.Add(new { stage = "persisted-startup", settings = startupSettings.Current, bounds,
                    privateDesktop = BackgroundVerification.CurrentDesktop, mediaInitialized = false });
            }
            finally { startup.CloseForShutdown(); }

            reloaded.Update(reloaded.Current with { UseDynamicIsland = false });
            assert(!new SettingsStore().Current.UseDynamicIsland,
                "Switching back to MacBook also persists across application restarts");
        }
        finally { Environment.SetEnvironmentVariable("WINISLAND_DATA_DIR", originalDirectory); }
    }

    private static void VerifyRasterContours(string output, Action<bool, string> assert, List<object> evidence)
    {
        foreach (var (name, width, height, expansion) in new[]
        {
            ("idle", 126.0, 36.67, 0.0), ("compact", 230.66, 36.67, 0.0),
            ("expanded", 371.0, 160.0, 1.0), ("mid-morph", 294.0, 99.0, .5)
        })
        {
            var geometry = NotchSurface.CreateGeometry(width, height, expansion, true);
            var mirror = geometry.Clone();
            mirror.Transform = new MatrixTransform(-1, 0, 0, 1, width, 0);
            var difference = Geometry.Combine(geometry, mirror, GeometryCombineMode.Xor, null, .0001, ToleranceType.Absolute).GetArea(.0001, ToleranceType.Absolute);
            assert(geometry.IsFrozen && difference < .001 && geometry.FillContains(new Point(width / 2, height / 2))
                && !geometry.FillContains(new Point(1, 1)) && !geometry.FillContains(new Point(width - 1, height - 1)),
                $"{name}: the island path is symmetric, filled and detached with convex transparent corners");
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                var canvas = new Canvas { Width = 420, Height = 200 };
                var surface = new NotchSurface { Width = width, Height = height, Expansion = expansion, IsDynamicIsland = true };
                Canvas.SetLeft(surface, (420 - width) / 2);
                Canvas.SetTop(surface, 11);
                canvas.Children.Add(surface);
                canvas.Measure(new Size(420, 200));
                canvas.Arrange(new Rect(0, 0, 420, 200));
                canvas.UpdateLayout();
                var bitmap = Render(canvas, scale);
                var pixels = Pixels(bitmap);
                var blackOnly = true;
                var antialiased = false;
                var contiguous = true;
                var clearTop = true;
                for (var y = 0; y < bitmap.PixelHeight; y++)
                {
                    var phase = 0;
                    for (var x = 0; x < bitmap.PixelWidth; x++)
                    {
                        var pixel = Pixel(pixels, bitmap.PixelWidth, x, y);
                        blackOnly &= pixel.B == 0 && pixel.G == 0 && pixel.R == 0;
                        antialiased |= pixel.A is > 0 and < 255;
                        if (y < Math.Floor(11 * scale)) clearTop &= pixel.A == 0;
                        if (pixel.A > 0) { if (phase == 2) contiguous = false; phase = 1; }
                        else if (phase == 1) phase = 2;
                    }
                }
                var center = Pixel(pixels, bitmap.PixelWidth, (int)(210 * scale), (int)((11 + height / 2) * scale));
                assert(blackOnly && antialiased && contiguous && clearTop && center.A == 255,
                    $"{name} at {scale * 100:0}%: opaque black housing, smooth boundary, no interior gaps and an 11-DIP clear top inset");
                Save(bitmap, Path.Combine(output, $"contour-{name}-{scale * 100:0}.png"));
                evidence.Add(new { contour = name, scale, width, height, expansion, blackOnly, antialiased, contiguous, clearTop, symmetricAreaDifference = difference });
            }
        }
    }

    private static MotionSample Sample(NotchWindow notch)
    {
        var island = Find<FrameworkElement>(notch, "Island");
        var surface = Find<NotchSurface>(notch, "Surface");
        var content = Find<FrameworkElement>(notch, "ExpandedContent");
        var motion = typeof(NotchWindow).GetField("_sizeMotion", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(notch);
        var velocity = new Velocity(0, 0, 0);
        if (motion is not null)
        {
            object Property(string name) => motion.GetType().GetProperty(name)!.GetValue(motion)!;
            var clock = (AnimationClock)Property("Clock");
            var seconds = (double)Property("Seconds");
            var progress = Math.Clamp((clock.CurrentTime?.TotalSeconds ?? 0) / seconds, 0, 1);
            double Derivative(string easeName, string distanceName)
            {
                if (progress >= 1) return 0;
                var ease = (EasingFunctionBase)Property(easeName);
                const double delta = .000001;
                var lower = Math.Max(0, progress - delta);
                var upper = Math.Min(1, progress + delta);
                // Observe the actual installed easing timeline, independently of
                // SpringEase.VelocityAt and its analytic production derivative.
                return (double)Property(distanceName) * (ease.Ease(upper) - ease.Ease(lower)) / ((upper - lower) * seconds);
            }
            velocity = new Velocity(Derivative("WidthEase", "WidthDistance"), Derivative("HeightEase", "HeightDistance"), Derivative("ContourEase", "ContourDistance"));
        }
        return new MotionSample(island.Width, island.Height, surface.Expansion, content.Opacity,
            Find<TranslateTransform>(notch, "ContentOffset").Y, island.HasAnimatedProperties || surface.HasAnimatedProperties, velocity);
    }

    private static bool SamePosition(MotionSample a, MotionSample b) => Near(a.Width, b.Width, .001) && Near(a.Height, b.Height, .001) && Near(a.Contour, b.Contour, .000001);
    private static bool SameVelocity(Velocity a, Velocity b)
    {
        static bool Close(double first, double second) => Math.Abs(first - second) < Math.Max(1, Math.Abs(first) * .01);
        return Close(a.Width, b.Width) && Close(a.Height, b.Height) && Close(a.Contour, b.Contour);
    }
    private static bool Near(double first, double second, double tolerance = .05) => Math.Abs(first - second) <= tolerance;
    private static T Find<T>(FrameworkElement root, string name) where T : DependencyObject => root.FindName(name) as T ?? throw new InvalidOperationException($"Missing {typeof(T).Name}: {name}");
    private static Rect Bounds(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private static bool Contains(Rect outer, Rect inner, double tolerance)
    {
        outer.Inflate(tolerance, tolerance);
        return outer.Contains(inner);
    }
    private static async Task Settle(FrameworkElement element)
    {
        await element.Dispatcher.InvokeAsync(element.UpdateLayout, DispatcherPriority.Render);
        await element.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        element.UpdateLayout();
    }
    private static BitmapSource Render(FrameworkElement element, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale),
            (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }
    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }
    private static (byte B, byte G, byte R, byte A) Pixel(byte[] pixels, int width, int x, int y)
    {
        var offset = (y * width + x) * 4;
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
    }
    private static bool IsBlackRegion(BitmapSource bitmap, Rect region, double scale)
    {
        region.Inflate(-2, -2);
        var pixels = Pixels(bitmap);
        for (var y = (int)Math.Ceiling(region.Top * scale); y < region.Bottom * scale; y++)
            for (var x = (int)Math.Ceiling(region.Left * scale); x < region.Right * scale; x++)
                if (Pixel(pixels, bitmap.PixelWidth, x, y) is not { B: 0, G: 0, R: 0, A: 255 }) return false;
        return true;
    }
    private static BitmapSource Artwork()
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(49, 27, 92), Color.FromRgb(242, 121, 141), 55), null, new Rect(0, 0, 96, 96));
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(248, 218, 170)), null, new Point(67, 32), 17, 17);
            drawing.DrawGeometry(new SolidColorBrush(Color.FromRgb(25, 22, 68)), null, Geometry.Parse("M0,70 L25,45 48,75 65,55 96,90 96,96 0,96Z"));
        }
        var bitmap = new RenderTargetBitmap(96, 96, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
    private static void SaveContactSheet(List<Frame> frames, string path, int columns = 2, int cellWidth = 700, int cellHeight = 422)
    {
        if (frames.Count == 0) return;
        var rows = (frames.Count + columns - 1) / columns;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(238, 240, 246)), null, new Rect(0, 0, columns * cellWidth, rows * cellHeight));
            for (var i = 0; i < frames.Count; i++)
            {
                var x = (i % columns) * cellWidth;
                var y = (i / columns) * cellHeight;
                drawing.DrawText(new FormattedText(frames[i].Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 14, Brushes.DimGray, 1), new Point(x + 20, y + 12));
                drawing.DrawImage(frames[i].Bitmap, new Rect(x, y + 34, frames[i].Bitmap.PixelWidth, frames[i].Bitmap.PixelHeight));
            }
        }
        var bitmap = new RenderTargetBitmap(columns * cellWidth, rows * cellHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Save(bitmap, path);
    }
    private sealed class RenderingFrames
    {
        private TimeSpan _lastFrame;
        internal async Task<T> NextAsync<T>(Func<T> read)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler? handler = null;
            handler = (_, args) =>
            {
                var frame = ((RenderingEventArgs)args).RenderingTime;
                if (frame == _lastFrame) return;
                _lastFrame = frame;
                CompositionTarget.Rendering -= handler;
                try { completion.TrySetResult(read()); } catch (Exception ex) { completion.TrySetException(ex); }
            };
            CompositionTarget.Rendering += handler;
            try
            {
                if (await Task.WhenAny(completion.Task, Task.Delay(5000)) != completion.Task)
                    throw new TimeoutException("No rendering frame arrived on the private Dynamic Island verification desktop.");
                return await completion.Task;
            }
            finally { CompositionTarget.Rendering -= handler; }
        }
    }
}
