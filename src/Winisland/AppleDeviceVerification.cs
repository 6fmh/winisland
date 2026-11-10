using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
        const string proHardware = @"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&201B";
        var pro = AppleDeviceIdentity.Recognize("airpods", "AirPods - Find My", new[] { proHardware }, false)!;
        assert(pro is { Kind: AppleDeviceKind.AirPodsPro, Name: "AirPods Pro" }, "Real Apple Bluetooth vendor and product IDs recognize AirPods Pro even with a Find My driver name");
        assert(AppleDeviceIdentity.Recognize("renamed", "AirPods Pro", new[] { @"BTHENUM\VID&0001054C_PID&201B" }, false) is null,
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
            foreach (var (width, height) in new[] { (185.0, 32.0), (160.0, 24.0), (320.0, 48.0) })
            {
                settings.Update(settings.Current with { NotchWidth = width, NotchHeight = height });
                notch.ShowAppleDeviceForTest(pro);
                await Settle();
                assert(!notch.IsExpanded && !notch.IsActive && banner.IsVisible && banner.Opacity == 1,
                    $"Apple connection appears from the minimized notch without activation at {width:0} by {height:0} (expanded={notch.IsExpanded}, active={notch.IsActive}, visible={banner.IsVisible}, opacity={banner.Opacity})");
                assert(island.ActualWidth == Math.Max(360, width + 64) && island.ActualHeight == height + 76,
                    $"Apple connection preserves the requested camera housing and leaves room for the illustration at {width:0} by {height:0}");
                foreach (var name in new[] { "DeviceIllustration", "DeviceName" })
                {
                    var element = Find<FrameworkElement>(name);
                    var bounds = element.TransformToAncestor(island).TransformBounds(new Rect(element.RenderSize));
                    assert(bounds.Top >= height && bounds.Left >= 20 && bounds.Right < island.ActualWidth - 20 && bounds.Bottom < island.ActualHeight - 14,
                        $"{name} stays below the camera and inside the rounded surface at {width:0} by {height:0}");
                }
                if (width == 185)
                {
                    Save(notch, island, Path.Combine(output, "apple-device-airpods-pro.png"), 1);
                    Save(notch, island, Path.Combine(output, "apple-device-airpods-pro-200.png"), 2);
                }
                notch.DismissAppleDeviceForTest();
                await Settle();
                assert(island.ActualWidth == width && island.ActualHeight == height && !banner.IsVisible,
                    "Dismissing an Apple connection restores the configured compact notch");
            }

            settings.Update(settings.Current with { NotchWidth = 185, NotchHeight = 32 });
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
            notch.DismissAppleDeviceForTest();

            settings.Update(settings.Current with { ReducedMotion = false });
            notch.ShowAppleDeviceForTest(pro);
            await Task.Delay(90);
            var progressWidth = island.ActualWidth;
            settings.Update(settings.Current with { ReducedMotion = true });
            await Settle();
            assert(banner.Opacity == 1 && Find<NotchSurface>("Surface").Expansion == .25 && island.ActualWidth == 360,
                "Enabling reduced motion finishes an active Apple connection spring and content entrance immediately");
            assert(progressWidth >= 185 && progressWidth <= 364, "The Apple connection spring grows smoothly from the compact silhouette");
            // The real timer must dismiss and restore the previous compact state.
            await Task.Delay(4600);
            await Settle();
            assert(!banner.IsVisible && island.ActualWidth == 185 && island.ActualHeight == 32,
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
