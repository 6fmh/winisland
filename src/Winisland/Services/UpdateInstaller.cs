using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Winisland.Services;

internal sealed record UpdatePlan(UpdatePackage Package, int ParentId, long ParentStartedUtcTicks, string ApplicationPath, string InstallDirectory);

internal static class UpdateInstaller
{
    internal static async Task LaunchAsync(UpdatePackage package)
    {
        if (BackgroundVerification.IsWorker) throw new InvalidOperationException("Installer launches are disabled during background verification.");
        var application = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find Winisland's executable.");
        var directory = Path.GetDirectoryName(package.InstallerPath)!;
        using var parent = Process.GetCurrentProcess();
        var plan = new UpdatePlan(package, parent.Id, parent.StartTime.ToUniversalTime().Ticks, application, AppContext.BaseDirectory);
        ValidatePlan(plan, directory);
        var planPath = Path.Combine(directory, "update-plan.json");
        var helperPath = Path.Combine(directory, "Winisland.Update.exe");
        File.Copy(application, helperPath, false);
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan));
        var start = HiddenStart(helperPath);
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(planPath);
        using var helper = Process.Start(start) ?? throw new InvalidOperationException("Couldn't start the update. Try again.");
        var ready = Path.Combine(directory, "ready");
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (File.Exists(ready)) return;
            if (helper.HasExited) throw new InvalidOperationException("Couldn't prepare the update. Try again.");
            await Task.Delay(40);
        }
        // The helper has its own bounded parent wait. Do not close the app if
        // it failed to confirm that it can install and restart the payload.
        throw new InvalidOperationException("The update took too long to prepare. Try again.");
    }

    internal static void ValidatePlan(UpdatePlan plan, string directory)
    {
        var cache = Path.GetFullPath(Path.Combine(SettingsStore.DirectoryPath, "updates")) + Path.DirectorySeparatorChar;
        directory = Path.GetFullPath(directory);
        if (!directory.StartsWith(cache, StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(Path.GetFileName(directory), "^[a-fA-F0-9]{32}$")
            || !Path.IsPathFullyQualified(plan.Package.InstallerPath) || Path.GetDirectoryName(Path.GetFullPath(plan.Package.InstallerPath)) != directory
            || !Regex.IsMatch(plan.Package.Version, @"^\d+\.\d+\.\d+$")
            || Path.GetFileName(plan.Package.InstallerPath) != $"Winisland-Setup-{plan.Package.Version}-{GitHubReleaseClient.Runtime}.exe"
            || !Regex.IsMatch(plan.Package.Sha256, "^[a-fA-F0-9]{64}$")
            || !Path.IsPathFullyQualified(plan.ApplicationPath) || Path.GetFileName(plan.ApplicationPath) != "Winisland.exe"
            || !Path.IsPathFullyQualified(plan.InstallDirectory)
            || !Path.GetFullPath(Path.GetDirectoryName(plan.ApplicationPath)!).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetFullPath(plan.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The update plan is invalid.");
    }

    internal static ProcessStartInfo InstallerStart(UpdatePlan plan, string logPath)
    {
        var start = HiddenStart(plan.Package.InstallerPath);
        foreach (var argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/UPDATE=1",
            "/DIR=" + plan.InstallDirectory, "/LOG=" + logPath }) start.ArgumentList.Add(argument);
        return start;
    }
    private static ProcessStartInfo HiddenStart(string path) => new(path)
    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };

    internal static async Task<int> RunAsync(string planPath)
    {
        if (BackgroundVerification.IsWorker) return 1;
        UpdatePlan? plan = null;
        var directory = Path.GetDirectoryName(Path.GetFullPath(planPath))!;
        var readyPath = Path.Combine(directory, "ready");
        var success = false;
        var installerFinished = false;
        try
        {
            if (Path.GetFileName(planPath) != "update-plan.json" || new FileInfo(planPath).Length > 16384) throw new InvalidOperationException("Invalid update plan.");
            plan = JsonSerializer.Deserialize<UpdatePlan>(await File.ReadAllTextAsync(planPath)) ?? throw new InvalidOperationException("Missing update plan.");
            ValidatePlan(plan, directory);
            using (var stream = File.OpenRead(plan.Package.InstallerPath))
            {
                if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') throw new InvalidOperationException("The download is not a Windows installer.");
                stream.Position = 0;
                if (!Convert.ToHexString(await SHA256.HashDataAsync(stream)).Equals(plan.Package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The installer changed after download.");
            }
            Process? parent = null;
            try { parent = Process.GetProcessById(plan.ParentId); } catch (ArgumentException) { }
            using (parent)
            {
                if (parent is not null && !parent.HasExited
                    && (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartedUtcTicks
                        || !string.Equals(parent.MainModule?.FileName, plan.ApplicationPath, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("The original Winisland process does not match this update.");
                await File.WriteAllTextAsync(readyPath, "ready");
                if (parent is not null && !parent.HasExited)
                {
                    using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await parent.WaitForExitAsync(wait.Token);
                }
            }
            using var installer = Process.Start(InstallerStart(plan, Path.Combine(directory, "installer.log")))
                ?? throw new InvalidOperationException("Couldn't start the installer.");
            using (var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5))) await installer.WaitForExitAsync(deadline.Token);
            installerFinished = true;
            if (installer.ExitCode != 0) throw new InvalidOperationException($"Setup couldn't finish (code {installer.ExitCode}). Try the update again.");
            using var restarted = Process.Start(RestartStart(plan.ApplicationPath));
            if (restarted is null) throw new InvalidOperationException("The update installed, but Winisland could not restart.");
            success = true;
            await WriteOutcomeAsync(plan.Package.Version, true, "Update installed.", restarted.Id);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or JsonException or OperationCanceledException or CryptographicException)
        {
            Log.Error("Apply Winisland update", ex);
            await WriteOutcomeAsync(plan?.Package.Version ?? "", false,
                ex is InvalidOperationException ? ex.Message : "The update couldn't finish. Please try again.");
            if (installerFinished && plan is not null && File.Exists(plan.ApplicationPath))
                try { using var original = Process.Start(RestartStart(plan.ApplicationPath)); }
                catch (Win32Exception) { } catch (InvalidOperationException) { }
            return 1;
        }
        finally
        {
            if (success)
            {
                try { File.Delete(plan!.Package.InstallerPath); File.Delete(planPath); File.Delete(readyPath); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
    internal static ProcessStartInfo RestartStart(string applicationPath)
    {
        var start = HiddenStart(applicationPath);
        start.ArgumentList.Add("--background");
        return start;
    }
    private static async Task WriteOutcomeAsync(string version, bool success, string message, int? restartedPid = null)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DirectoryPath);
            await File.WriteAllTextAsync(Path.Combine(SettingsStore.DirectoryPath, "update-result.json"),
                JsonSerializer.Serialize(new { version, success, message, restartedPid, at = DateTimeOffset.UtcNow }));
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
