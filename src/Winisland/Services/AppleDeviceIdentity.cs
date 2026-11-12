using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Winisland.Services;

public enum AppleDeviceKind { Accessory, AirPods, AirPodsPro, AirPodsMax, Keyboard, Mouse, Trackpad, iPhone, iPad }

public sealed record AppleDeviceConnection(string Id, string Name, AppleDeviceKind Kind, bool Wired = false)
{
    public string? ModelName { get; init; }
    public string ConnectionStatus => ModelName is { } model && !string.Equals(Name, model, StringComparison.OrdinalIgnoreCase)
        ? $"{model} · Connected" : Wired ? "Connected via USB" : "Connected";
    public string Announcement => ModelName is { } model && !string.Equals(Name, model, StringComparison.OrdinalIgnoreCase)
        ? $"{Name}, {model}, connected" : Wired ? $"{Name} connected via USB" : $"{Name} connected";
}

// Names select the illustration only after Windows has supplied an Apple vendor ID.
// A renamed headset and a manufacturer's display string are not identity evidence.
internal static class AppleDeviceIdentity
{
    private static readonly Regex AppleVendor = new(@"(?:^|[^A-Z0-9])(?:VID_05AC(?=[&\\#]|$)|VID&000(?:1004C|205AC)(?=[_\\&]|$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Product = new(@"(?:^|[^A-Z0-9])PID[&_](?<id>[0-9A-F]{4})(?=[_\\&#]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AirPods = new(@"\bAirPods(?:\s+(?<type>Pro|Max))?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ModelNumber = new(@"\bA[0-9]{4}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private sealed record Model(AppleDeviceKind Kind, string Name);

    internal static AppleDeviceConnection? Recognize(string id, string name, IEnumerable<string> hardwareIds, bool wired,
        IEnumerable<string>? modelNumbers = null)
    {
        var appleIds = hardwareIds.Where(value => value is not null && AppleVendor.IsMatch(value)).ToArray();
        if (appleIds.Length == 0) return null;
        // Bluetooth product IDs identify renamed earbuds. USB product IDs belong
        // to a separate namespace and must not be mistaken for Bluetooth models.
        var models = appleIds.Where(value => value.Contains("VID&", StringComparison.OrdinalIgnoreCase))
            .Select(value => Product.Match(value)).Where(match => match.Success)
            .Select(match => FromProduct(match.Groups["id"].Value.ToUpperInvariant()))
            .Concat((modelNumbers ?? Array.Empty<string>()).Where(value => value is not null)
                .SelectMany(value => ModelNumber.Matches(value).Cast<Match>())
                .Select(match => FromModelNumber(match.Value.ToUpperInvariant())))
            .Where(model => model is not null).Cast<Model>().Distinct().ToArray();
        // Conflicting profile metadata cannot justify a specific generation.
        var model = models.Length == 1 ? models[0] : null;
        var kinds = models.Select(value => value.Kind).Distinct().ToArray();
        var kind = kinds.Length == 1 ? kinds[0] : KindFromName(name);
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
        return new(id, displayName, kind, wired) { ModelName = model?.Name };
    }

    // Apple names/A-numbers: support.apple.com/109525. Bluetooth PID evidence:
    // AirPodsDesktop AppleCP.cpp and AirBattery Supports.swift; see research doc.
    private static Model? FromProduct(string product) => product switch
    {
        "2002" => new(AppleDeviceKind.AirPods, "AirPods 1"),
        "200F" => new(AppleDeviceKind.AirPods, "AirPods 2"),
        "2013" => new(AppleDeviceKind.AirPods, "AirPods 3"),
        "2019" => new(AppleDeviceKind.AirPods, "AirPods 4"),
        "201B" => new(AppleDeviceKind.AirPods, "AirPods 4 (ANC)"),
        "2030" or "2036" => new(AppleDeviceKind.AirPods, "AirPods 5"),
        "200E" => new(AppleDeviceKind.AirPodsPro, "AirPods Pro 1"),
        "2014" => new(AppleDeviceKind.AirPodsPro, "AirPods Pro 2 (Lightning)"),
        "2024" => new(AppleDeviceKind.AirPodsPro, "AirPods Pro 2 (USB-C)"),
        "2027" => new(AppleDeviceKind.AirPodsPro, "AirPods Pro 3"),
        "200A" => new(AppleDeviceKind.AirPodsMax, "AirPods Max 1 (Lightning)"),
        "201F" => new(AppleDeviceKind.AirPodsMax, "AirPods Max 1 (USB-C)"),
        _ => null
    };

    private static Model? FromModelNumber(string number) => number switch
    {
        "A1523" or "A1722" => FromProduct("2002"),
        "A2032" or "A2031" => FromProduct("200F"),
        "A2565" or "A2564" => FromProduct("2013"),
        "A3053" or "A3050" or "A3054" => FromProduct("2019"),
        "A3056" or "A3055" or "A3057" => FromProduct("201B"),
        "A3531" or "A3532" or "A3533" => FromProduct("2030"),
        // Both fifth-generation PIDs identify the generation, not the case variant.
        // Normalize to the same label so agreeing model/PID evidence stays specific.
        "A3439" or "A3440" or "A3441" => FromProduct("2030"),
        "A2084" or "A2083" => FromProduct("200E"),
        "A2931" or "A2699" or "A2698" => FromProduct("2014"),
        "A3047" or "A3048" or "A3049" => FromProduct("2024"),
        "A3063" or "A3064" or "A3065" => FromProduct("2027"),
        "A2096" => FromProduct("200A"),
        "A3184" => FromProduct("201F"),
        "A3454" => new(AppleDeviceKind.AirPodsMax, "AirPods Max 2"),
        _ => null
    };

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
