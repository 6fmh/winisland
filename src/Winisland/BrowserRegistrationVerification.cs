using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Winisland.Services;

namespace Winisland;

internal static class BrowserRegistrationVerification
{
    internal static void Run(string output, Action<bool, string> assert)
    {
        var testId = Guid.NewGuid().ToString("N");
        var registryRoot = @"Software\Winisland\Verification\" + testId;
        var ownedKey = registryRoot + @"\owned";
        var sharedKey = registryRoot + @"\shared";
        var changedKey = registryRoot + @"\changed";
        var directory = Path.Combine(output, "browser-registration", testId);
        Directory.CreateDirectory(directory);
        var manifestPath = Path.Combine(directory, "host.json");
        var installedExe = Path.Combine(directory, "installed", "Winisland.exe");
        var portableExe = Path.Combine(directory, "portable", "Winisland.exe");
        var otherManifest = Path.Combine(directory, "other-host.json");
        var paths = new[] { ownedKey, sharedKey, changedKey };
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(ownedKey)) key.SetValue("", manifestPath);
            using (var key = Registry.CurrentUser.CreateSubKey(sharedKey))
            { key.SetValue("", manifestPath); key.SetValue("Keep", "unrelated value"); }
            using (var key = Registry.CurrentUser.CreateSubKey(changedKey)) key.SetValue("", otherManifest);

            WriteManifest(portableExe);
            assert(!BrowserIntegration.UnregisterOwnedHost(installedExe, manifestPath, paths)
                && File.Exists(manifestPath) && ReadDefault(ownedKey) == manifestPath,
                "Uninstall preserves browser registration owned by a later portable launch");

            WriteManifest(installedExe);
            assert(BrowserIntegration.UnregisterOwnedHost(installedExe, manifestPath, paths)
                && !File.Exists(manifestPath) && ReadDefault(ownedKey) is null,
                "Uninstall removes the manifest and browser registration belonging to its executable");
            using (var key = Registry.CurrentUser.OpenSubKey(sharedKey))
                assert(key?.GetValue("") is null && key?.GetValue("Keep") as string == "unrelated value",
                    "Browser cleanup preserves unrelated registry values");
            assert(ReadDefault(changedKey) == otherManifest,
                "Browser cleanup preserves a registration that points to another manifest");

            File.WriteAllText(manifestPath, "{ malformed");
            var rejected = false;
            try { BrowserIntegration.UnregisterOwnedHost(installedExe, manifestPath, paths); }
            catch (JsonException) { rejected = true; }
            assert(rejected && File.Exists(manifestPath) && ReadDefault(changedKey) == otherManifest,
                "An unreadable host manifest cannot authorize registration deletion");

            File.WriteAllText(manifestPath, JsonSerializer.Serialize(new { name = "another.host", path = installedExe }));
            assert(!BrowserIntegration.UnregisterOwnedHost(installedExe, manifestPath, paths) && File.Exists(manifestPath),
                "Browser cleanup only accepts a manifest identifying the Winisland host");
        }
        finally
        {
            // This unique fixture key is the only recursive cleanup target.
            Registry.CurrentUser.DeleteSubKeyTree(registryRoot, false);
        }

        void WriteManifest(string executable) => File.WriteAllText(manifestPath,
            JsonSerializer.Serialize(new { name = BrowserIntegration.HostName, path = executable }));
        static string? ReadDefault(string path)
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            return key?.GetValue("") as string;
        }
    }
}
