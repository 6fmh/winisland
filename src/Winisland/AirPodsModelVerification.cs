using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Winisland.Services;

namespace Winisland;

internal static class AirPodsModelVerification
{
    internal static void Run(string output, Action<bool, string> assert)
    {
        var fixtures = new[]
        {
            ("2002", "AirPods 1", AppleDeviceKind.AirPods, "A1523 A1722"),
            ("200F", "AirPods 2", AppleDeviceKind.AirPods, "A2032 A2031"),
            ("2013", "AirPods 3", AppleDeviceKind.AirPods, "A2565 A2564"),
            ("2019", "AirPods 4", AppleDeviceKind.AirPods, "A3053 A3050 A3054"),
            ("201B", "AirPods 4 (ANC)", AppleDeviceKind.AirPods, "A3056 A3055 A3057"),
            ("2030", "AirPods 5", AppleDeviceKind.AirPods, "A3531 A3532 A3533"),
            ("2036", "AirPods 5", AppleDeviceKind.AirPods, "A3439 A3440 A3441"),
            ("200E", "AirPods Pro 1", AppleDeviceKind.AirPodsPro, "A2084 A2083"),
            ("2014", "AirPods Pro 2 (Lightning)", AppleDeviceKind.AirPodsPro, "A2931 A2699 A2698"),
            ("2024", "AirPods Pro 2 (USB-C)", AppleDeviceKind.AirPodsPro, "A3047 A3048 A3049"),
            ("2027", "AirPods Pro 3", AppleDeviceKind.AirPodsPro, "A3063 A3064 A3065"),
            ("200A", "AirPods Max 1 (Lightning)", AppleDeviceKind.AirPodsMax, "A2096"),
            ("201F", "AirPods Max 1 (USB-C)", AppleDeviceKind.AirPodsMax, "A3184")
        };
        foreach (var (pid, label, kind, numbers) in fixtures)
        {
            var connection = Recognize(pid);
            assert(connection is not null && connection.ModelName == label && connection.Kind == kind && connection.Name == "Personal headphones",
                $"Apple Bluetooth PID {pid} identifies {label} despite a personalized name");
            assert(connection?.ConnectionStatus == $"{label} · Connected" && connection.Announcement.Contains(label, StringComparison.Ordinal),
                $"{label} is present in connection status and accessibility announcement");
            foreach (var number in numbers.Split(' '))
            {
                var numberOnly = Recognize("FFFF", new[] { number });
                var agreeing = Recognize(pid, new[] { number });
                assert(numberOnly?.ModelName == label && numberOnly.Kind == kind && agreeing?.ModelName == label,
                    $"Official model number {number} identifies {label} and agrees with Bluetooth metadata");
            }
        }
        assert(Recognize("FFFF", new[] { "A3454" }) is { Kind: AppleDeviceKind.AirPodsMax, ModelName: "AirPods Max 2" },
            "AirPods Max 2 uses its official model number without inventing a Bluetooth PID");
        assert(Recognize("201B", name: "My AirPods Pro") is { Kind: AppleDeviceKind.AirPods, ModelName: "AirPods 4 (ANC)" },
            "AirPods 4 ANC product metadata overrides a misleading Pro display name");
        assert(Recognize("FFFF", name: "AirPods 5") is { Kind: AppleDeviceKind.AirPods, ModelName: null, ConnectionStatus: "Connected" },
            "A name alone cannot confirm an AirPods generation");
        assert(Recognize("FFFF", new[] { "A9999", "A30560", "serial-A3056suffix", "A3059" }, "AirPods") is { ModelName: null },
            "Unknown numbers, partial numbers, serial fragments and charging-case numbers never invent an earbud model");
        assert(AppleDeviceIdentity.Recognize("fake", "AirPods 4 (ANC)", new[] { @"BTHENUM\VID&0001054C_PID&201B" }, false, new[] { "A3056" }) is null,
            "Neither a model number nor an AirPods name can bypass Apple vendor verification");
        assert(AppleDeviceIdentity.Recognize("usb", "Apple accessory", new[] { @"USB\VID_05AC&PID_201B" }, true) is { Kind: AppleDeviceKind.Accessory, ModelName: null },
            "USB product IDs cannot collide with the Bluetooth AirPods model namespace");
        assert(AppleDeviceIdentity.Recognize("charging", "Personal headphones", new[] { @"USB\VID_05AC&PID_0001" }, true, new[] { "A3056" }) is null,
            "Recognizing AirPods model metadata on USB still does not claim an audio connection");
        assert(AppleDeviceIdentity.Recognize("profiles", "Personal headphones", new[] { Hardware("2019"), Hardware("201B") }, false)
            is { Kind: AppleDeviceKind.AirPods, ModelName: null }, "Conflicting AirPods 4 and ANC profile metadata keeps the generation unspecified");
        assert(Recognize("2019", new[] { "A3056" }) is { Kind: AppleDeviceKind.AirPods, ModelName: null },
            "Conflicting Bluetooth and model-number evidence keeps ANC unspecified");
        assert(AppleDeviceIdentity.Recognize("profiles", "Personal headphones", new[] { Hardware("201B").ToLowerInvariant(), Hardware("201B"), @"USB\VID_05AC&PID_0001" }, false)
            is { ModelName: "AirPods 4 (ANC)" }, "Duplicate, lower-case and unrelated profile IDs preserve a confirmed model");
        assert(Recognize("2019", name: "AirPods 4")?.ConnectionStatus == "Connected", "An exact model name is not repeated on the status line");
        assert(new AppleDeviceConnection("wired", "iPhone", AppleDeviceKind.iPhone, true).ConnectionStatus == "Connected via USB",
            "USB status remains accurate for other Apple devices");

        var metadata = new Dictionary<string, object>
        {
            [AppleDeviceService.BluetoothVendorSourceProperty] = (byte)1,
            [AppleDeviceService.BluetoothVendorProperty] = (ushort)0x004C,
            [AppleDeviceService.BluetoothProductProperty] = (ushort)0x201B
        };
        assert(AppleDeviceService.BluetoothHardwareId(metadata) is { } bluetoothId
            && AppleDeviceIdentity.Recognize("direct", "Personal headphones", new[] { bluetoothId }, false)?.ModelName == "AirPods 4 (ANC)",
            "Native Windows Bluetooth product/vendor/source properties identify AirPods 4 ANC without a PnP string");
        metadata[AppleDeviceService.BluetoothVendorSourceProperty] = (byte)2;
        assert(AppleDeviceService.BluetoothHardwareId(metadata) is null, "Bluetooth SIG vendor IDs cannot be used as USB vendor IDs");
        metadata[AppleDeviceService.BluetoothVendorProperty] = (ushort)0x05AC;
        assert(AppleDeviceService.BluetoothHardwareId(metadata) is { } usbSourceId
            && AppleDeviceIdentity.Recognize("direct", "Headphones", new[] { usbSourceId }, false)?.ModelName == "AirPods 4 (ANC)",
            "Apple Bluetooth metadata with USB-assigned vendor source is supported");
        metadata.Remove(AppleDeviceService.BluetoothVendorSourceProperty);
        assert(AppleDeviceService.BluetoothHardwareId(metadata) is null, "Missing Bluetooth vendor source cannot identify Apple hardware");
        metadata[AppleDeviceService.BluetoothVendorSourceProperty] = (byte)2;
        metadata[AppleDeviceService.BluetoothProductProperty] = "201B";
        assert(AppleDeviceService.BluetoothHardwareId(metadata) is null, "Malformed Windows property types are ignored without throwing");
        File.WriteAllText(Path.Combine(output, "airpods-models.json"), JsonSerializer.Serialize(new
        {
            passed = true, models = fixtures.Select(value => new { bluetoothPid = value.Item1, model = value.Item2, officialModelNumbers = value.Item4 }),
            limits = "Metadata fixtures; no physical reconnection or BLE scan. AirPods 5 case variant is unspecified."
        }, new JsonSerializerOptions { WriteIndented = true }));

        static AppleDeviceConnection? Recognize(string pid, string[]? numbers = null, string name = "Personal headphones") =>
            AppleDeviceIdentity.Recognize("fixture", name, new[] { Hardware(pid) }, false, numbers);
    }

    internal static async Task RunUiAsync(NotchWindow notch, SettingsStore settings, string output, Action<bool, string> assert)
    {
        BackgroundVerification.RequirePrivateDesktop();
        var saved = settings.Current;
        try
        {
            settings.Update(saved with { ReducedMotion = true, HideInFullscreen = false, ExpandOnHover = false, ShowLiveActivity = false });
            notch.Collapse();
            foreach (var pid in new[] { "2019", "201B", "2030" })
            {
                var connection = AppleDeviceIdentity.Recognize(pid, "Personal headphones", new[] { Hardware(pid) }, false)!;
                notch.ShowAppleDeviceForTest(connection);
                await Task.Delay(80);
                var status = (TextBlock)notch.FindName("DeviceStatus");
                var banner = (FrameworkElement)notch.FindName("DeviceConnection");
                assert(banner.IsVisible && ((TextBlock)notch.FindName("DeviceName")).Text == connection.Name
                    && status.Text == connection.ConnectionStatus && AutomationProperties.GetName(banner) == connection.Announcement,
                    $"Actual notch shows {connection.ModelName} with the personalized name and accessible connection status");
                var island = (FrameworkElement)notch.FindName("Island");
                var bounds = status.TransformToAncestor(island).TransformBounds(new Rect(status.RenderSize));
                assert(bounds.Left >= 0 && bounds.Right <= island.ActualWidth && bounds.Bottom <= island.ActualHeight,
                    $"{connection.ModelName} connection text fits inside the actual notch");
                var target = new RenderTargetBitmap((int)Math.Ceiling(notch.ActualWidth), (int)Math.Ceiling(notch.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                target.Render(notch);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
                using (var stream = File.Create(Path.Combine(output, $"airpods-model-{pid}.png"))) encoder.Save(stream);
                notch.Expand(true);
                await Task.Delay(80);
                assert(((TextBlock)notch.FindName("DeviceHeaderLabel")).Text == connection.ConnectionStatus,
                    $"Expanded notch retains the identified {connection.ModelName} model");
                notch.DismissAppleDeviceForTest(); notch.Collapse();
            }
        }
        finally { notch.DismissAppleDeviceForTest(); settings.Update(saved); notch.Collapse(); }
    }

    private static string Hardware(string pid) => $@"BTHENUM\{{0000110B-0000-1000-8000-00805F9B34FB}}_VID&0001004C_PID&{pid}";
}
