using System;
using System.IO;
using System.Text.Json;
using Winisland.Services;

namespace Winisland;

internal static class UiUpdateBugVerification
{
    internal static void Run(Action<bool, string> check)
    {
        RunCore(check);
        VerifyStartupOwnership(check);
    }

    internal static void RunCore(Action<bool, string> check)
    {
        BackgroundVerification.RequirePrivateDesktop();
        var directory = Path.Combine(SettingsStore.DirectoryPath, "updates", new string('a', 32));
        var applicationDirectory = Path.Combine(SettingsStore.DirectoryPath, "installed-app");
        var package = new UpdatePackage(Path.Combine(directory, $"Winisland-Setup-1.2.3-{GitHubReleaseClient.Runtime}.exe"),
            new string('b', 64), "1.2.3");
        var valid = new UpdatePlan(package, 123, DateTime.UtcNow.Ticks,
            Path.Combine(applicationDirectory, "Winisland.exe"), applicationDirectory);
        UpdateInstaller.ValidatePlan(valid, directory);
        check(true, "Updater accepts a structurally valid plan with matching cache, installer and application paths");

        foreach (var broken in new[]
        {
            valid with { Package = null! },
            valid with { Package = package with { InstallerPath = null! } },
            valid with { Package = package with { Sha256 = null! } },
            valid with { Package = package with { Version = null! } },
            valid with { ApplicationPath = null! },
            valid with { InstallDirectory = null! },
            valid with { ParentId = 0 },
            valid with { ParentStartedUtcTicks = 0 },
            valid with { ApplicationPath = applicationDirectory + "\0/Winisland.exe" },
            valid with { Package = package with { InstallerPath = directory + "\0/installer.exe" } }
        })
        {
            // Exercise the same JSON boundary as the updater helper, including
            // non-nullable record members receiving explicit null from disk.
            var restored = JsonSerializer.Deserialize<UpdatePlan>(JsonSerializer.Serialize(broken))!;
            var refused = false;
            try { UpdateInstaller.ValidatePlan(restored, directory); }
            catch (InvalidOperationException ex) { refused = ex.Message == "The update plan is invalid."; }
            check(refused, "Malformed serialized updater plans fail cleanly before process or installer handoff");
        }
        foreach (var brokenDirectory in new[] { "", null!, directory + "\0" })
        {
            var refused = false;
            try { UpdateInstaller.ValidatePlan(valid, brokenDirectory); }
            catch (InvalidOperationException) { refused = true; }
            check(refused, "Updater rejects empty or invalid plan directories through its normal error contract");
        }
        check(!UpdateInstaller.CanRestartAfterFailure(false, false, false),
            "Failed updater preparation does not start a second app while the original parent is still alive");
        check(UpdateInstaller.CanRestartAfterFailure(true, false, false),
            "An installer launch failure after app shutdown permits recovery of the original app");
        check(!UpdateInstaller.CanRestartAfterFailure(true, true, false),
            "A running or timed-out installer cannot race an app restart while changing its payload");
        check(UpdateInstaller.CanRestartAfterFailure(true, true, true),
            "A completed failed installation permits app recovery after the original parent has exited");
        check(!UpdateInstaller.CanRestartAfterFailure(false, true, true),
            "Even a completed installer cannot duplicate an original app whose exit was not confirmed");
    }

    private static void VerifyStartupOwnership(Action<bool, string> check)
    {
        var installed = Path.Combine(SettingsStore.DirectoryPath, "installed-app", "Winisland.exe");
        var portable = Path.Combine(SettingsStore.DirectoryPath, "portable-app", "Winisland.exe");
        const string sid = "S-1-5-21-fixture";
        var ownedTask = StartupRegistration.BuildTaskXml(installed, sid);
        var foreignTask = StartupRegistration.BuildTaskXml(portable, sid);
        var ownedRun = $"\"{installed}\" --background";
        var foreignRun = $"\"{portable}\" --background";
        var plan = StartupRegistration.PlanOwnedRemoval(ownedTask, ownedRun, installed);
        check(plan.RemoveTask && plan.RemoveRun && plan.ClearPreference,
            "Uninstalling the active startup owner removes its task and legacy command and clears its preference");
        plan = StartupRegistration.PlanOwnedRemoval(foreignTask, foreignRun, installed);
        check(!plan.RemoveTask && !plan.RemoveRun && !plan.ClearPreference,
            "Uninstalling an old installed copy preserves a later portable copy's sign-in registration and preference");
        plan = StartupRegistration.PlanOwnedRemoval(ownedTask, foreignRun, installed);
        check(plan.RemoveTask && !plan.RemoveRun && !plan.ClearPreference,
            "Mixed startup ownership removes only the installed task and preserves a foreign legacy command and preference");
        plan = StartupRegistration.PlanOwnedRemoval(foreignTask, ownedRun, installed);
        check(!plan.RemoveTask && plan.RemoveRun && !plan.ClearPreference,
            "Mixed startup ownership removes only the installed legacy command and preserves a foreign task and preference");
        foreach (var unknown in new[] { "<invalid", ownedTask.Replace("--background", "--settings"),
            ownedTask.Replace("</Actions>", "<ComHandler><ClassId>fixture</ClassId></ComHandler></Actions>") })
        {
            plan = StartupRegistration.PlanOwnedRemoval(unknown, ownedRun, installed);
            check(!plan.RemoveTask && plan.RemoveRun && !plan.ClearPreference,
                "Unknown, malformed or multi-action tasks are preserved instead of deleting another startup registration");
        }
        plan = StartupRegistration.PlanOwnedRemoval(null, null, installed);
        check(!plan.RemoveTask && !plan.RemoveRun && !plan.ClearPreference,
            "An uninstall with no owned startup records leaves the shared preference unchanged");
        plan = StartupRegistration.PlanOwnedRemoval(ownedTask.ToUpperInvariant().Replace("HTTP://SCHEMAS.MICROSOFT.COM/WINDOWS/2004/02/MIT/TASK", "http://schemas.microsoft.com/windows/2004/02/mit/task"), null, installed);
        check(!plan.RemoveTask && !plan.ClearPreference,
            "Unrecognized task XML shapes are preserved through the startup ownership guard");
        plan = StartupRegistration.PlanOwnedRemoval(StartupRegistration.BuildTaskXml(installed.ToUpperInvariant(), sid), null, installed);
        check(plan.RemoveTask && plan.ClearPreference,
            "Startup task ownership recognizes Windows executable path casing without weakening command matching");
    }
}
