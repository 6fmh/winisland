using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Winisland.Services;

public enum AppleDeviceKind { Accessory, AirPods, AirPodsPro, AirPodsMax, Keyboard, Mouse, Trackpad, iPhone, iPad }

public sealed record AppleDeviceConnection(string Id, string Name, AppleDeviceKind Kind, bool Wired = false);

// Names select the illustration only after Windows has supplied an Apple vendor ID.
// A renamed headset and a manufacturer's display string are not identity evidence.
internal static class AppleDeviceIdentity
{
    private static readonly Regex AppleVendor = new(@"(?:^|[^A-Z0-9])(?:VID_05AC(?=[&\\#]|$)|VID&000(?:1004C|205AC)(?=[_\\&]|$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Product = new(@"(?:^|[^A-Z0-9])PID[&_](?<id>[0-9A-F]{4})(?=[_\\&#]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AirPods = new(@"\bAirPods(?:\s+(?<type>Pro|Max))?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static AppleDeviceConnection? Recognize(string id, string name, IEnumerable<string> hardwareIds, bool wired)
    {
        var appleIds = hardwareIds.Where(value => value is not null && AppleVendor.IsMatch(value)).ToArray();
        if (appleIds.Length == 0) return null;
        // Product IDs also identify AirPods whose owner has given them a custom name.
        var kind = appleIds.Select(value => Product.Match(value)).Where(match => match.Success)
            .Select(match => match.Groups["id"].Value.ToUpperInvariant() switch
        {
            "2002" or "200F" or "2013" => (AppleDeviceKind?)AppleDeviceKind.AirPods,
            "200E" or "2014" or "201B" => AppleDeviceKind.AirPodsPro,
            "200A" => AppleDeviceKind.AirPodsMax,
            _ => null
        }).FirstOrDefault(value => value.HasValue) ?? KindFromName(name);
        // A charging cable does not mean AirPods have connected for Bluetooth audio.
        if (wired && kind is AppleDeviceKind.AirPods or AppleDeviceKind.AirPodsPro or AppleDeviceKind.AirPodsMax) return null;
        var label = kind switch
        {
            AppleDeviceKind.AirPods => "AirPods", AppleDeviceKind.AirPodsPro => "AirPods Pro", AppleDeviceKind.AirPodsMax => "AirPods Max",
            AppleDeviceKind.Keyboard => "Magic Keyboard", AppleDeviceKind.Mouse => "Magic Mouse", AppleDeviceKind.Trackpad => "Magic Trackpad",
            AppleDeviceKind.iPhone => "iPhone", AppleDeviceKind.iPad => "iPad", _ => wired ? "Apple device" : "Apple accessory"
        };
        // Avoid exposing driver names such as "Apple Mobile Device USB Composite Device".
        var displayName = string.IsNullOrWhiteSpace(name) || name.Contains("USB", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Composite", StringComparison.OrdinalIgnoreCase) || name.Contains("Find My", StringComparison.OrdinalIgnoreCase)
            ? label : name.Trim();
        return new(id, displayName, kind, wired);
    }

    private static AppleDeviceKind KindFromName(string name)
    {
        var match = AirPods.Match(name);
        if (match.Success) return match.Groups["type"].Value.ToUpperInvariant() switch
        { "PRO" => AppleDeviceKind.AirPodsPro, "MAX" => AppleDeviceKind.AirPodsMax, _ => AppleDeviceKind.AirPods };
        if (name.Contains("Keyboard", StringComparison.OrdinalIgnoreCase)) return AppleDeviceKind.Keyboard;
        if (name.Contains("Trackpad", StringComparison.OrdinalIgnoreCase)) return AppleDeviceKind.Trackpad;
        if (name.Contains("Mouse", StringComparison.OrdinalIgnoreCase)) return AppleDeviceKind.Mouse;
        if (name.Contains("iPhone", StringComparison.OrdinalIgnoreCase)) return AppleDeviceKind.iPhone;
        if (name.Contains("iPad", StringComparison.OrdinalIgnoreCase)) return AppleDeviceKind.iPad;
        return AppleDeviceKind.Accessory;
    }
}
