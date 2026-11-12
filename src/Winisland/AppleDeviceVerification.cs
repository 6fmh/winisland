using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Winisland.Controls;
using Winisland.Services;

namespace Winisland;

internal static class AppleDeviceVerification
{
    internal static async Task RunAsync(NotchWindow notch, SettingsStore settings, string output, Action<bool, string> assert)
    {
        BackgroundVerification.RequirePrivateDesktop();
        const string proHardware = @"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2024";
        var pro = AppleDeviceIdentity.Recognize("airpods", "AirPods - Find My", new[] { proHardware }, false)!;
        assert(pro is { Kind: AppleDeviceKind.AirPodsPro, Name: "AirPods Pro" }, "Real Apple Bluetooth vendor and product IDs recognize AirPods Pro even with a Find My driver name");
        assert(AppleDeviceIdentity.Recognize("renamed", "AirPods Pro", new[] { @"BTHENUM\VID&0001054C_PID&2024" }, false) is null,
            "A non-Apple headset renamed AirPods Pro never triggers a connection notification");
        assert(AppleDeviceIdentity.Recognize("unknown", "AirPods", Array.Empty<string>(), false) is null,
            "An AirPods name without Apple hardware evidence stays silent");
        assert(AppleDeviceIdentity.Recognize("lookalike", "Apple device", new[] { @"USB\VID_05ACA&PID_1234", @"BTHENUM\LOCALMFG&004C" }, true) is null,
            "Vendor lookalikes and a local Bluetooth adapter manufacturer cannot identify a remote Apple device");
        assert(AppleDeviceIdentity.Recognize("phone", "iPhone", new[] { @"USB\VID_05AC&PID_12A8" }, true) is { Kind: AppleDeviceKind.iPhone, Wired: true },
            "Apple USB vendor evidence recognizes a connected iPhone");
        assert(AppleDeviceIdentity.Recognize("charge", "AirPods Pro", new[] { @"USB\VID_05AC&PID_0001" }, true) is null,
            "An AirPods charging cable does not claim a Bluetooth audio connection");
        assert(AppleDeviceIdentity.Recognize("profiles", "Personal headphones", new[] { @"USB\VID_05AC&PID_0001", proHardware }, false)
            is { Kind: AppleDeviceKind.AirPodsPro }, "Known AirPods product metadata is found beyond an unrecognized Apple profile");
        var groups = new AppleConnectionGroups();
        assert(!groups.Connect("baseline", "existing", false) && !groups.Connect("baseline", "extra-profile", true),
            "Additional profiles of an already-connected device retain the silent startup baseline");
        assert(groups.Connect("new-device", "unknown-profile", true) && groups.Connect("new-device", "known-profile", true),
            "A later connected profile can identify a device when its first profile has no identity metadata");
        groups.Disconnect("new-device", "unknown-profile");
        assert(!groups.TryAnnounce("new-device", "unknown-profile") && groups.TryAnnounce("new-device", "known-profile")
            && !groups.TryAnnounce("new-device", "known-profile"),
            "Only a still-connected profile can announce and each physical device announces once");
        groups.Disconnect("new-device", "known-profile");
        assert(groups.Connect("new-device", "known-profile", true) && groups.TryAnnounce("new-device", "known-profile"),
            "Disconnecting the final profile allows a real reconnection to announce again");

        var devices = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true),
            new[] { "System.Devices.Aep.IsConnected", "System.Devices.Aep.ContainerId", "System.Devices.ContainerId" }, DeviceInformationKind.AssociationEndpoint);
        var recognized = new List<string>();
        foreach (var device in devices)
        {
            var identity = await AppleDeviceService.ReadIdentityAsync(device, false);
            if (identity is not null) recognized.Add(identity.Kind.ToString());
        }
        // No connection is forced; enumeration exercises the same Windows metadata path as live events.
        File.WriteAllText(Path.Combine(output, "apple-device-discovery.json"), System.Text.Json.JsonSerializer.Serialize(new { PairedCount = devices.Count, RecognizedAppleKinds = recognized }));
        assert(true, "Windows paired-device discovery and PnP identity lookup complete without changing Bluetooth connections");
        using (var watcher = new AppleDeviceService(notch.Dispatcher))
        {
            var startupNotifications = 0;
            watcher.Connected += (_, _) => startupNotifications++;
            watcher.Start();
            await Task.Delay(750);
            assert(startupNotifications == 0, "Already connected Apple devices establish a silent discovery baseline at startup");
        }

        var saved = settings.Current;
        var island = Find<FrameworkElement>("Island");
        var banner = Find<FrameworkElement>("DeviceConnection");
        var foregroundFixture = new Window { Width = 120, Height = 80, ShowInTaskbar = false, Title = "Apple connection focus fixture" };
        try
        {
            settings.Update(saved with { ReducedMotion = true, HideInFullscreen = false, ExpandOnHover = false, ShowLiveActivity = false });
            notch.Collapse();
            // Establish another app's focus on the private test desktop. A
            // collapsed window can retain focus from earlier editing tests.
            foregroundFixture.Show(); foregroundFixture.Activate();
            await Settle();
            foreach (var (width, height) in new[] { (190.0, 32.0), (160.0, 24.0), (320.0, 48.0) })
            {
                settings.Update(settings.Current with { NotchWidth = width, NotchHeight = height });
                notch.ShowAppleDeviceForTest(pro);
                await Settle();
                assert(!notch.IsExpanded && !notch.IsActive && banner.IsVisible && banner.Opacity == 1,
                    $"Apple connection appears from the minimized notch without activation at {width:0} by {height:0} (expanded={notch.IsExpanded}, active={notch.IsActive}, visible={banner.IsVisible}, opacity={banner.Opacity})");
                assert(island.ActualWidth == Math.Max(NotchWindow.AppleConnectionMinimumWidth, width + 64) && island.ActualHeight == 2 * height + 80,
                    $"Apple connection preserves the requested camera housing and leaves room for the illustration at {width:0} by {height:0}");
                foreach (var name in new[] { "DeviceIllustration", "DeviceName" })
                {
                    var element = Find<FrameworkElement>(name);
                    var bounds = element.TransformToAncestor(island).TransformBounds(new Rect(element.RenderSize));
                    assert(bounds.Top >= height && bounds.Left >= 20 && bounds.Right < island.ActualWidth - 20 && bounds.Bottom < island.ActualHeight - 14,
                        $"{name} stays below the camera and inside the rounded surface at {width:0} by {height:0}");
                }
                if (width == 190)
                {
                    Save(notch, island, Path.Combine(output, "apple-device-airpods-pro.png"), 1);
                    Save(notch, island, Path.Combine(output, "apple-device-airpods-pro-200.png"), 2);
                }
                notch.DismissAppleDeviceForTest();
                await Settle();
                assert(island.ActualWidth == width && island.ActualHeight == height && !banner.IsVisible,
                    "Dismissing an Apple connection restores the configured compact notch");
            }

            settings.Update(settings.Current with { NotchWidth = 190, NotchHeight = 32 });
            foreach (var dynamic in new[] { false, true })
            {
                settings.Update(settings.Current with { UseDynamicIsland = dynamic });
                notch.ShowAppleDeviceForTest(pro); await Settle();
                assert(Find<Button>("DeviceDismissButton").ActualWidth == 44 && Find<Button>("DeviceDismissButton").ActualHeight == 44
                    && Find<TextBlock>("DeviceStatus").Text == pro.ConnectionStatus && Find<TextBlock>("DeviceStatus").ActualHeight <= 36,
                    "Accessory layouts have a 44-DIP dismiss target and show only the confirmed, bounded model status");
                var illustration = Find<FrameworkElement>("DeviceIllustration");
                var top = illustration.TranslatePoint(new Point(), island).Y;
                var bottom = island.ActualHeight - top - illustration.ActualHeight;
                var spacingDismiss = Find<Button>("DeviceDismissButton");
                var dismissCenter = spacingDismiss.TranslatePoint(new Point(0, spacingDismiss.ActualHeight / 2), island).Y;
                assert(Math.Abs(top - bottom) <= 1 && Math.Abs(dismissCenter - top - illustration.ActualHeight / 2) <= 1,
                    "Accessory artwork and its dismiss target center with equal space above and below the black surface");
                File.WriteAllText(Path.Combine(output, $"popup-spacing-device-{(dynamic ? "island" : "macbook")}.json"),
                    System.Text.Json.JsonSerializer.Serialize(new { top, bottom, housingHeight = island.ActualHeight, illustrationHeight = illustration.ActualHeight, dismissCenter }));
                foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                    Save(notch, island, Path.Combine(output, $"popup-device-{(dynamic ? "island" : "macbook")}-{scale * 100:0}.png"), scale);
                notch.DismissAppleDeviceForTest();
            }
            settings.Update(settings.Current with { UseDynamicIsland = true });
            notch.SelectTabForTest("music");
            notch.ShowAppleDeviceForTest(pro);
            notch.Expand(true); await Settle();
            var islandEntry = (Button)((DynamicIslandPlayer)notch.FindName("DynamicPlayer")).FindName("MenuButton");
            var islandDismiss = Find<Button>("DeviceDismissButton");
            assert(islandEntry.Focus() && NotificationVerification.FocusMovesTo(islandDismiss),
                "Tab enters the accessory dismiss action from the actual expanded island player");
            assert(NotificationVerification.FocusMovesTo(islandEntry),
                "The accessory footer and island player share one bounded keyboard cycle");
            assert(islandEntry.Focus() && NotificationVerification.FocusMovesTo(islandDismiss, FocusNavigationDirection.Previous),
                "Reverse Tab reaches the accessory dismiss action from the actual expanded island player");
            notch.ShowAppleDeviceForTest(pro with { Id = "keyboard-next-device", Name = "Queued AirPods" });
            islandDismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            assert(Find<TextBlock>("DeviceName").Text == "Queued AirPods" && NotificationVerification.HasVisibleFocusWithin(island),
                "Advancing a keyboard-dismissed accessory preserves focus on a visible enabled notch control");
            assert(NotificationVerification.FocusMovesTo(islandDismiss),
                "The queued accessory dismiss control remains reachable through native Tab");
            islandDismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            assert(!banner.IsVisible && NotificationVerification.HasVisibleFocusWithin(island),
                "Dismissing the final keyboard-focused accessory restores focus to visible enabled island controls");
            assert(NotificationVerification.FocusMovesTo(islandEntry),
                "Removing an accessory footer keeps keyboard traversal on visible island controls");
            notch.ShowAppleDeviceForTest(pro); await Settle();
            assert(islandEntry.Focus(), "The island player retains focus while a separate accessory is visible");
            islandDismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            assert(islandEntry.IsKeyboardFocused,
                "Dismissing an unfocused accessory does not move focus from the current player control");
            notch.Collapse();
            settings.Update(settings.Current with { UseDynamicIsland = false });
            const string longName = "Alexandra's personalized AirPods Pro for a very long device name";
            notch.ShowAppleDeviceForTest(new("long-name", longName, AppleDeviceKind.AirPodsPro));
            await Settle();
            var nameLabel = Find<TextBlock>("DeviceName");
            var nameBounds = nameLabel.TransformToAncestor(island).TransformBounds(new Rect(nameLabel.RenderSize));
            assert(nameLabel.Text == longName && Equals(nameLabel.ToolTip, longName) && nameLabel.IsHitTestVisible
                && nameLabel.TextTrimming == TextTrimming.CharacterEllipsis && nameBounds.Right <= island.ActualWidth - 30,
                "Personalized Apple device names stay inside the card and expose their complete name through hover help");
            assert(Find<TextBlock>("DeviceStatus").Text == "Connected", "Wireless connection status shows only the confirmed connection without invented battery information");
            Save(notch, island, Path.Combine(output, "apple-device-long-name.png"), 1);
            notch.DismissAppleDeviceForTest();
            notch.ShowAppleDeviceForTest(new("wired-phone", "iPhone", AppleDeviceKind.iPhone, true));
            await Settle();
            assert(Find<TextBlock>("DeviceStatus").Text == "Connected via USB", "A wired Apple device reports its actual USB connection rather than implying an audio connection");
            notch.DismissAppleDeviceForTest();
            foreach (var kind in new[] { AppleDeviceKind.AirPods, AppleDeviceKind.AirPodsMax, AppleDeviceKind.Keyboard, AppleDeviceKind.Mouse, AppleDeviceKind.Trackpad, AppleDeviceKind.iPhone, AppleDeviceKind.iPad })
            {
                notch.ShowAppleDeviceForTest(new(kind.ToString(), kind switch { AppleDeviceKind.Keyboard => "Magic Keyboard", AppleDeviceKind.AirPodsMax => "AirPods Max", _ => kind.ToString() }, kind));
                await Settle();
                Save(notch, island, Path.Combine(output, $"apple-device-{kind.ToString().ToLowerInvariant()}.png"), 1);
                notch.DismissAppleDeviceForTest();
            }

            notch.ShowAppleDeviceForTest(pro);
            notch.Expand(true);
            await Settle();
            assert(!banner.IsVisible && Find<FrameworkElement>("DeviceHeaderStatus").IsVisible && Find<FrameworkElement>("ExpandedContent").IsVisible,
                "Opening the notch moves the connection into a small header status and keeps the selected panel usable");
            Save(notch, island, Path.Combine(output, "apple-device-expanded.png"), 1);
            notch.Collapse();
            await Settle();
            assert(banner.IsVisible, "Closing the panel restores an unexpired Apple connection moment");
            notch.ShowAppleDeviceForTest(new("keyboard", "Magic Keyboard", AppleDeviceKind.Keyboard));
            assert(Find<TextBlock>("DeviceName").Text == pro.Name, "A second Apple connection queues instead of overwriting the first");
            notch.ShowAppleDeviceForTest(new("keyboard", "Studio keyboard", AppleDeviceKind.Keyboard));
            Find<Button>("DeviceDismissButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            assert(Find<TextBlock>("DeviceName").Text == "Studio keyboard", "Dismissing a connection advances to the latest metadata of the queued device");
            notch.DismissAppleDeviceForTest();

            notch.ShowAppleDeviceForTest(pro);
            notch.ShowAppleDeviceForTest(pro with { Name = "Alex's AirPods Pro" }); await Settle();
            assert(Find<TextBlock>("DeviceName").Text == "Alex's AirPods Pro", "A duplicate device event refreshes its name without creating a second popup");
            banner.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
            await Task.Delay(4700);
            assert(banner.IsVisible, "Hover pauses the real accessory timeout while its model is being read");
            banner.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
            notch.Activate();
            var dismiss = Find<Button>("DeviceDismissButton");
            assert(dismiss.Focus() && dismiss.IsKeyboardFocused, "The connection dismiss control accepts keyboard focus");
            await Settle();
            Save(notch, island, Path.Combine(output, "popup-device-keyboard-focus.png"), 1);
            assert(NotificationVerification.HasBlueFocusPixels(dismiss), "The accessory dismiss target renders a visible circular keyboard focus outline");
            await Task.Delay(4700);
            assert(banner.IsVisible, "Keyboard focus pauses the real accessory timeout");
            dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            assert(!banner.IsVisible, "The native connection dismiss button closes the focused moment");
            Keyboard.ClearFocus(); foregroundFixture.Activate();

            notch.ShowAppleDeviceForTest(pro);
            notch.ShowNotification(new("device-interruption", "Messages", "Alex", "Let me know when you're ready.", DateTimeOffset.UtcNow));
            await Task.Delay(4700);
            assert(!banner.IsVisible && notch.ActiveNotificationForTest is not null, "A notification suspends the obscured accessory moment instead of spending its timeout");
            notch.DismissNotificationForTest(true); await Settle();
            assert(banner.IsVisible && Find<TextBlock>("DeviceName").Text == pro.Name, "Dismissing an interrupting notification restores the accessory for a full reading interval");
            notch.DismissAppleDeviceForTest();

            settings.Update(settings.Current with { ReducedMotion = false });
            notch.ShowAppleDeviceForTest(pro);
            notch.Expand(false);
            notch.Collapse();
            await Task.Delay(90);
            var deviceScale = (ScaleTransform)((TransformGroup)banner.RenderTransform).Children[0];
            assert(deviceScale.ScaleX == 1 && deviceScale.ScaleY == 1,
                "Apple connection text remains at native scale throughout the entrance");
            assert(banner.IsVisible && banner.Opacity >= 0 && banner.Opacity <= 1,
                "Opening and immediately closing a connection moment preserves a bounded, visible entrance");
            var progressWidth = island.ActualWidth;
            settings.Update(settings.Current with { ReducedMotion = true });
            await Settle();
            assert(banner.Opacity == 1 && Find<NotchSurface>("Surface").Expansion == .25 && island.ActualWidth == NotchWindow.AppleConnectionMinimumWidth,
                "Enabling reduced motion finishes an active Apple connection spring and content entrance immediately");
            assert(progressWidth >= 190 && progressWidth <= NotchWindow.AppleConnectionMinimumWidth + 4, "The Apple connection spring grows smoothly from the compact silhouette");
            notch.DismissAppleDeviceForTest();
            settings.Update(settings.Current with { ReducedMotion = false });
            notch.ShowAppleDeviceForTest(pro);
            await Task.Delay(380);
            var deviceOffset = (TranslateTransform)((TransformGroup)banner.RenderTransform).Children[1];
            assert(banner.IsVisible && banner.Opacity == 1 && deviceOffset.Y == 0
                && !banner.HasAnimatedProperties && !deviceOffset.HasAnimatedProperties,
                "A visible Apple connection lands precisely and releases its completed fade and translation clocks");
            settings.Update(settings.Current with { ReducedMotion = true });
            // The real timer must dismiss and restore the previous compact state.
            await Task.Delay(4600);
            await Settle();
            assert(!banner.IsVisible && island.ActualWidth == 190 && island.ActualHeight == 32,
                "Apple connection automatically returns to the compact notch after its brief presentation");
        }
        finally { foregroundFixture.Close(); notch.DismissAppleDeviceForTest(); settings.Update(saved); notch.Collapse(); }

        T Find<T>(string name) where T : FrameworkElement => (T)notch.FindName(name);
    }

    private static async Task Settle() => await Task.Delay(45);

    private static void Save(NotchWindow notch, FrameworkElement island, string path, double scale)
    {
        notch.UpdateLayout();
        var visual = new DrawingVisual();
        var height = island.ActualHeight + 30;
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(183, 179, 219), Color.FromRgb(91, 139, 171), 20), null, new Rect(0, 0, 700, height));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(45, 241, 216, 222)), null, new Point(140, 180), 300, 190);
            var frame = new RenderTargetBitmap((int)Math.Ceiling(notch.ActualWidth * scale), (int)Math.Ceiling(notch.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            frame.Render(notch);
            dc.DrawImage(frame, new Rect(0, 0, notch.ActualWidth, notch.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)(700 * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
