using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using Winisland.Services;

namespace Winisland;

internal static class ResourceVerification
{
    private sealed record Sample(string Phase, long PrivateBytes, long WorkingSetBytes, long ManagedBytes,
        long AllocatedBytes, double CpuMs, int Handles, int Gen0, int Gen1, int Gen2);

    internal static async Task<int> RunAsync(App app, string[] args)
    {
        BackgroundVerification.RequirePrivateDesktop();
        var index = Array.IndexOf(args, "--output");
        var output = Path.GetFullPath(index >= 0 && index + 1 < args.Length ? args[index + 1] : "artifacts/resource-verification");
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("WINISLAND_DATA_DIR", Path.Combine(output, "test-data"));
        var settings = new SettingsStore();
        using var media = new MediaService(app.Dispatcher);
        using var audio = new AudioService();
        using var focus = new FocusTimer();
        using var volume = new PlayerVolumeService(app.Dispatcher, deferInitialization: true);
        var shelf = new FileShelf();
        using var uploads = new ShelfUploadService(shelf, new UploadCredentialStore());
        using var updates = new UpdateService(app.Dispatcher);
        var notch = new NotchWindow(settings, media, audio, focus, shelf, uploads, volume, updates);
        var rendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        notch.ContentRendered += (_, _) => rendered.TrySetResult(true);
        var samples = new List<Sample>();
        try
        {
            notch.Show();
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            samples.Add(Measure("first-frame"));
            // Let ordinary deferred initialization and dispatcher idle work finish.
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(3000);
            var panelsCreatedAtIdle = notch.DeferredPanelsCreatedForTest;
            for (var i = 0; i < 6; i++)
            {
                samples.Add(Measure("idle"));
                if (i < 5) await Task.Delay(1000);
            }
            settings.Update(settings.Current with { ReducedMotion = true, HideInFullscreen = false });
            notch.SelectTabForTest("calendar"); notch.Expand(false); notch.UpdateLayout();
            var organizerUsable = notch.FindName("Organizer") is Controls.OrganizerPanel panel && panel.IsVisible;
            notch.SelectTabForTest("controls"); notch.UpdateLayout();
            var controlsUsable = notch.FindName("ControlCenter") is Controls.ControlCenterPanel controls && controls.IsVisible;
            notch.Collapse();
            await Task.Delay(1500);
            samples.Add(Measure("after-first-use"));
            // Retained managed data is a diagnostic only. Production never forces
            // collection or trims the working set; idle samples above are natural.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            samples.Add(Measure("retained-after-first-use"));
            var idle = samples.Where(s => s.Phase == "idle").ToArray();
            var passed = organizerUsable && controlsUsable
                && (!args.Contains("--expect-lazy-organizer") || !panelsCreatedAtIdle);
            await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
            {
                passed, desktop = BackgroundVerification.CurrentDesktop, panelsCreatedAtIdle,
                organizerUsable, controlsUsable,
                idleAllocatedBytesPerSecond = (idle[^1].AllocatedBytes - idle[0].AllocatedBytes) / 5.0,
                idleCpuMs = idle[^1].CpuMs - idle[0].CpuMs,
                note = "Fresh file-backed profile, empty media snapshot; real deferred player/device services. No tray, registration, update network request, or personal media session. Compare identical verifier/build settings. Working set varies with system pressure; private bytes and natural managed allocations are also recorded. Forced GC only in the explicitly labelled final diagnostic sample.",
                samples
            }, new JsonSerializerOptions { WriteIndented = true }));
            return passed ? 0 : 1;
        }
        finally { notch.CloseForShutdown(); }
    }

    private static Sample Measure(string phase)
    {
        using var process = Process.GetCurrentProcess();
        return new Sample(phase, process.PrivateMemorySize64, process.WorkingSet64, GC.GetTotalMemory(false),
            GC.GetTotalAllocatedBytes(), process.TotalProcessorTime.TotalMilliseconds, process.HandleCount,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }
}
