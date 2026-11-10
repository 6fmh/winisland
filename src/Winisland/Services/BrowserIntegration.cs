using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace Winisland.Services;

public static class BrowserIntegration
{
    public const string HostName = "com.winisland.player";
    public const string ExtensionId = "agpnncgfekilbepnojjaolgpbhefolne";
    private static readonly string[] BrowserRegistryRoots = { @"Software\Google\Chrome", @"Software\Google\Chrome for Testing", @"Software\Chromium", @"Software\Microsoft\Edge",
        @"Software\BraveSoftware\Brave-Browser", @"Software\Perplexity\Comet", @"Software\Comet\Browser", @"Software\Vivaldi", @"Software\Opera Software\Opera Stable" };
    public static string ExtensionDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Winisland", "BrowserExtension");
    public static string HostManifestPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Winisland", "BrowserBridge", HostName + ".json");

    public static void EnsureInstalled(string executablePath)
    {
        if (!OperatingSystem.IsWindows() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WINISLAND_DATA_DIR"))) return;
        executablePath = Path.GetFullPath(executablePath);
        var source = Path.Combine(Path.GetDirectoryName(executablePath)!, "browser-extension");
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(ExtensionDirectory);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.GetFullPath(Path.Combine(ExtensionDirectory, relative));
            if (!destination.StartsWith(Path.GetFullPath(ExtensionDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid browser companion path.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(HostManifestPath)!);
        var manifest = JsonSerializer.Serialize(new
        {
            name = HostName,
            description = "Local Winisland player volume bridge",
            path = executablePath,
            type = "stdio",
            allowed_origins = new[] { "chrome-extension://" + ExtensionId + "/" }
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(HostManifestPath, manifest);
        // Installed Comet 156 explicitly uses Google's native messaging key in
        // chrome.dll. Brand keys cover future builds and other Chromium browsers.
        foreach (var browser in BrowserRegistryRoots)
        {
            using var key = Registry.CurrentUser.CreateSubKey(browser + @"\NativeMessagingHosts\" + HostName);
            key.SetValue("", HostManifestPath);
        }
    }

    public static void Unregister(string executablePath)
    {
        if (!OperatingSystem.IsWindows() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WINISLAND_DATA_DIR"))) return;
        UnregisterOwnedHost(executablePath, HostManifestPath,
            BrowserRegistryRoots.Select(browser => browser + @"\NativeMessagingHosts\" + HostName).ToArray());
    }

    internal static bool UnregisterOwnedHost(string executablePath, string manifestPath, string[] registryPaths)
    {
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 65536) return false;
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (manifest.RootElement.ValueKind != JsonValueKind.Object
            || !manifest.RootElement.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != HostName
            || !manifest.RootElement.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String
            || !SamePath(path.GetString(), executablePath)) return false;

        // A later portable launch can own the shared manifest. Uninstall only
        // registrations still targeting this executable, and keep other values.
        foreach (var registryPath in registryPaths)
        {
            var empty = false;
            using (var key = Registry.CurrentUser.OpenSubKey(registryPath, true))
            {
                if (key is null || !SamePath(key.GetValue("") as string, manifestPath)) continue;
                key.DeleteValue("", false);
                empty = key.ValueCount == 0 && key.SubKeyCount == 0;
            }
            if (empty) Registry.CurrentUser.DeleteSubKey(registryPath, false);
        }
        File.Delete(manifestPath);
        return true;
    }

    private static bool SamePath(string? first, string second)
    {
        if (string.IsNullOrWhiteSpace(first)) return false;
        try { return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException) { return false; }
    }
}
