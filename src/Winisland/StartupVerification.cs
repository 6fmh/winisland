using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Winisland.Services;

namespace Winisland;

internal static class StartupVerification
{
    internal static async Task<int> RunAsync(App app, string[] args)
    {
        BackgroundVerification.RequirePrivateDesktop();
        var index = Array.IndexOf(args, "--output");
        var output = Path.GetFullPath(index >= 0 && index + 1 < args.Length ? args[index + 1] : "artifacts/startup-verification");
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("WINISLAND_DATA_DIR", Path.Combine(output, "test-data"));
        var clock = Stopwatch.StartNew();
        var settings = new SettingsStore();
        using var media = new MediaService(app.Dispatcher);
        using var audio = new AudioService();
        using var focus = new FocusTimer();
        using var volume = new PlayerVolumeService(app.Dispatcher, deferInitialization: true);
        var shelf = new FileShelf();
        using var uploads = new ShelfUploadService(shelf, new UploadCredentialStore());
        using var updates = new UpdateService(app.Dispatcher);
        var notch = new NotchWindow(settings, media, audio, focus, shelf, uploads, volume, updates);
        var constructionMs = clock.Elapsed.TotalMilliseconds;
        var rendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        double managedToVisibleMs = 0, processToVisibleMs = 0;
        bool deferredAtFirstFrame = false;
        notch.ContentRendered += (_, _) =>
        {
            managedToVisibleMs = Program.StartupClock.Elapsed.TotalMilliseconds;
            using var process = Process.GetCurrentProcess();
            processToVisibleMs = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds;
            deferredAtFirstFrame = !notch.DeferredPanelsCreatedForTest && !volume.StartedForTest;
            rendered.TrySetResult(true);
        };
        try
        {
            notch.Show();
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(notch.ActualWidth), (int)Math.Ceiling(notch.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(notch);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(output, "first-notch.png"))) encoder.Save(stream);
            var quietFirstFrame = notch.IsVisible && !notch.IsExpanded && !notch.IsActive;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var deferredWhileIdle = !notch.DeferredPanelsCreatedForTest;
            settings.Update(settings.Current with { ReducedMotion = true, HideInFullscreen = false });
            notch.SelectTabForTest("controls"); notch.Expand(false); notch.UpdateLayout();
            var controlsUsable = notch.FindName("ControlCenter") is Winisland.Controls.ControlCenterPanel panel && panel.IsVisible;
            notch.SelectTabForTest("calendar"); notch.UpdateLayout();
            var organizerUsable = notch.FindName("Organizer") is Winisland.Controls.OrganizerPanel organizer && organizer.IsVisible;
            var passed = quietFirstFrame && deferredAtFirstFrame && deferredWhileIdle && controlsUsable && organizerUsable;
            await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
            {
                passed, desktop = BackgroundVerification.CurrentDesktop, profile = "fresh", constructionMs,
                managedToVisibleMs, processToVisibleMs, deferredAtFirstFrame, deferredWhileIdle,
                note = "Measured on a private Windows desktop, from worker process creation through the first rendered native notch. Excludes Windows sign-in scheduling.",
                results = new[]
                {
                    new { check = "First notch is rendered without activation on the private desktop", passed = quietFirstFrame },
                    new { check = "Player integrations and secondary panels do not block the first frame", passed = deferredAtFirstFrame },
                    new { check = "Secondary panels remain unconstructed while idle", passed = deferredWhileIdle },
                    new { check = "Deferred Control Center loads on first selection", passed = controlsUsable },
                    new { check = "Deferred calendar loads on first selection", passed = organizerUsable }
                }
            }, new JsonSerializerOptions { WriteIndented = true }));
            return passed ? 0 : 1;
        }
        finally { notch.CloseForShutdown(); }
    }
}
