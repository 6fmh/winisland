using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Winisland.Controls;
using Winisland.Services;

namespace Winisland;

// The same fixture is compiled into baseline and experiment candidates. This
// measures actual frame-update methods, not an elapsed-time model or display FPS.
internal static class RenderingPerformanceVerification
{
    private const int FrameCount = 6000;
    private const int Repetitions = 5;
    private sealed record Measurement(string Workload, int Repetition, int Operations,
        double WallMs, double CpuMs, long ThreadAllocatedBytes, long TotalAllocatedBytes,
        long ManagedBytesBefore, long ManagedBytesAfter, long PrivateBytesBefore,
        long PrivateBytesAfter, long WorkingSetBytesBefore, long WorkingSetBytesAfter);

    internal static async Task<object> RunAsync(string output, Action<bool, string> check)
    {
        BackgroundVerification.RequirePrivateDesktop();
        var app = Application.Current ?? throw new InvalidOperationException("Missing WPF application.");
        if (!app.Dispatcher.CheckAccess()) throw new InvalidOperationException("Frame workload requires the WPF dispatcher.");
        var folder = Path.Combine(Path.GetFullPath(output), "rendering");
        Directory.CreateDirectory(folder);
        var originalData = Environment.GetEnvironmentVariable("WINISLAND_DATA_DIR");
        var data = Path.Combine(folder, "fixture-data");
        Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("WINISLAND_DATA_DIR", data);
        var settings = new SettingsStore();
        settings.Update(new IslandSettings { ReducedMotion = true, HideInFullscreen = false,
            ExpandOnHover = false, ShowLiveActivity = false, ShowNotifications = false });
        using var media = new MediaService(app.Dispatcher);
        using var audio = new AudioService();
        using var focus = new FocusTimer();
        using var volume = new PlayerVolumeService(app.Dispatcher, Array.Empty<IPlayerVolumeBackend>());
        var notch = new NotchWindow(settings, media, audio, focus, new FileShelf(), playerVolume: volume);
        var results = new List<Measurement>();
        var checks = new List<string>();
        void Verify(bool condition, string name) { check(condition, name); checks.Add(name); }
        var update = (Action)(typeof(NotchWindow).GetMethod("UpdateTimeline", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("NotchWindow.UpdateTimeline")).CreateDelegate(typeof(Action), notch);
        var cancel = (Action)(typeof(NotchWindow).GetMethod("CancelSeekAdjustment", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("NotchWindow.CancelSeekAdjustment")).CreateDelegate(typeof(Action), notch);
        var changed = typeof(MediaService).GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("MediaService.Changed");
        EventHandler? handlers = null;
        var snapshots = new MediaSnapshot[FrameCount];
        for (var frame = 0; frame < FrameCount; frame++) snapshots[frame] = new MediaSnapshot
        {
            HasSession = true, IsPlaying = true, CanSeek = true, CanToggle = true,
            Title = "Fixed frame fixture", Artist = "Winisland", Source = "performance-fixture",
            Position = TimeSpan.FromSeconds(42 + frame / 60.0), Duration = TimeSpan.FromSeconds(242)
        };
        try
        {
            notch.ShowActivated = false;
            notch.StopPollingForTest();
            notch.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            notch.StopPollingForTest();
            var player = (DynamicIslandPlayer)notch.FindName("DynamicPlayer");
            var mainSlider = (ScrubSlider)notch.FindName("SeekSlider");
            var elapsed = (TextBlock)notch.FindName("ElapsedLabel");
            var duration = (TextBlock)notch.FindName("DurationLabel");
            var islandSlider = (ScrubSlider)player.FindName("SeekSlider");
            var islandElapsed = (TextBlock)player.FindName("ElapsedLabel");
            var islandRemaining = (TextBlock)player.FindName("RemainingLabel");
            foreach (var useIsland in new[] { false, true })
            {
                settings.Update(settings.Current with { UseDynamicIsland = useIsland });
                media.SetSnapshotForTest(snapshots[0]);
                notch.SelectTabForTest("music");
                notch.Expand(false);
                notch.UpdateLayout();
                cancel();
                Verify(notch.IsExpanded && mainSlider.IsEnabled && mainSlider.Maximum == 242,
                    $"Frame fixture initializes the actual {(useIsland ? "island" : "MacBook")} player without background providers");
                // Providers do not emit metadata every presentation frame. Keep
                // SetSnapshotForTest's exact monotonic-clock reset but omit the
                // metadata Changed fanout during these controlled frame inputs.
                handlers = (EventHandler?)changed.GetValue(media);
                changed.SetValue(media, null);
                try
                {
                    for (var warm = 0; warm < 256; warm++) { media.SetSnapshotForTest(snapshots[warm]); update(); }
                    for (var repetition = 0; repetition < Repetitions; repetition++)
                    {
                        results.Add(Measure(useIsland ? "notch-island-timeline" : "notch-macbook-timeline", repetition, FrameCount, () =>
                        {
                            for (var frame = 0; frame < snapshots.Length; frame++)
                            {
                                media.SetSnapshotForTest(snapshots[frame]);
                                update();
                            }
                        }));
                        Verify(mainSlider.Value >= snapshots[^1].Position.TotalSeconds
                            && mainSlider.Value < snapshots[^1].Position.TotalSeconds + .1,
                            $"Actual {(useIsland ? "island" : "MacBook")} timeline consumes every fixed frame input, repetition {repetition}");
                    }
                }
                finally { changed.SetValue(media, handlers); handlers = null; }

                var boundary = snapshots[0] with { IsPlaying = false, Position = TimeSpan.FromSeconds(59.999), Duration = TimeSpan.FromSeconds(242) };
                media.SetSnapshotForTest(boundary); update();
                Verify(elapsed.Text == "0:59" && duration.Text == "4:02" && Math.Abs(mainSlider.Value - 59.999) < .001,
                    $"{(useIsland ? "Island" : "MacBook")} timeline retains subsecond seek precision before a minute boundary");
                media.SetSnapshotForTest(boundary with { Position = TimeSpan.FromSeconds(60) }); update();
                Verify(elapsed.Text == "1:00" && Math.Abs(mainSlider.Value - 60) < .001,
                    $"{(useIsland ? "Island" : "MacBook")} elapsed label changes on the minute boundary");
                media.SetSnapshotForTest(boundary with { Position = TimeSpan.FromSeconds(3600), Duration = TimeSpan.FromSeconds(3661) }); update();
                Verify(elapsed.Text == "1:00:00" && duration.Text == "1:01:01" && mainSlider.Maximum == 3661,
                    $"{(useIsland ? "Island" : "MacBook")} long-media hours and duration remain correct");
                media.SetSnapshotForTest(boundary with { CanSeek = false }); update();
                Verify(!mainSlider.IsEnabled && mainSlider.ToolTip as string == "Seeking unavailable for this app",
                    $"{(useIsland ? "Island" : "MacBook")} disabled seeking retains the correct explanation");
                media.SetSnapshotForTest(boundary); update();
                mainSlider.Value = 88;
                var preview = mainSlider.Value;
                update();
                Verify(mainSlider.Value == preview && elapsed.Text == "1:28",
                    $"{(useIsland ? "Island" : "MacBook")} pending user seek is preserved through a frame update");
                cancel();

                if (!useIsland) continue;
                player.CancelInteraction();
                player.UpdateTimeline(42, 242, true);
                for (var warm = 0; warm < 256; warm++) player.UpdateTimeline(snapshots[warm].Position.TotalSeconds, 242, true);
                for (var repetition = 0; repetition < Repetitions; repetition++)
                {
                    results.Add(Measure("dynamic-player-timeline", repetition, FrameCount, () =>
                    {
                        for (var frame = 0; frame < snapshots.Length; frame++)
                            player.UpdateTimeline(snapshots[frame].Position.TotalSeconds, 242, true);
                    }));
                    Verify(Math.Abs(islandSlider.Value - snapshots[^1].Position.TotalSeconds) < .001
                        && islandElapsed.Text == "2:21" && islandRemaining.Text == "−1:41",
                        $"Actual Dynamic Island control retains elapsed, rounded remaining time and seek precision, repetition {repetition}");
                }
                player.UpdateTimeline(59.999, 242, true);
                Verify(islandElapsed.Text == "0:59" && islandRemaining.Text == "−3:03", "Island remaining time rounds upward before a minute boundary");
                player.UpdateTimeline(60, 242, true);
                Verify(islandElapsed.Text == "1:00" && islandRemaining.Text == "−3:02", "Island remaining time changes exactly at the minute boundary");
                player.UpdateTimeline(3600, 3661, true);
                Verify(islandElapsed.Text == "1:00:00" && islandRemaining.Text == "−1:01", "Island elapsed hours preserve remaining-minute formatting");
                player.UpdateTimeline(90, 3661, false);
                Verify(!islandSlider.IsEnabled && islandSlider.ToolTip as string == "Seeking unavailable for this app", "Island disabled seeking updates explanation independently of elapsed text");
                player.UpdateTimeline(90, 242, true);
                islandSlider.Value = 110;
                player.UpdateTimeline(120, 242, true);
                Verify(islandSlider.Value == 110 && islandElapsed.Text == "1:50", "Island pending user seek prevents simulated frame overwrite");
                player.CancelInteraction();
                player.UpdateTimeline(double.NaN, double.PositiveInfinity, true);
                Verify(islandSlider.Value == 0 && !islandSlider.IsEnabled && islandElapsed.Text == "0:00", "Island invalid timeline values remain finite and disable seeking");
                player.UpdateTimeline(-1, 242, true);
                Verify(islandSlider.Value == 0, "Island negative position is clamped");
                player.UpdateTimeline(300, 242, true);
                Verify(islandSlider.Value == 242 && islandRemaining.Text == "−0:00", "Island over-duration position is clamped to the end");
            }

            SpectrumVerification.Run(Verify);
            var pcm = new (float Left, float Right)[96000];
            for (var sample = 0; sample < pcm.Length; sample++)
            {
                var tone = (float)(Math.Sin(2 * Math.PI * 1100 * sample / 48000) * .25);
                pcm[sample] = (tone, -tone);
            }
            var analyzer = new SpectrumAnalyzer(48000);
            var bands = new float[SpectrumAnalyzer.BandCount];
            for (var warm = 0; warm < 8192; warm++) analyzer.Push(pcm[warm].Left, pcm[warm].Right);
            for (var repetition = 0; repetition < Repetitions; repetition++)
            {
                results.Add(Measure("spectrum-two-second-stereo-pcm", repetition, pcm.Length, () =>
                {
                    foreach (var sample in pcm) analyzer.Push(sample.Left, sample.Right);
                    analyzer.Read(bands);
                }));
                var strongest = 0;
                for (var band = 1; band < bands.Length; band++) if (bands[band] > bands[strongest]) strongest = band;
                Verify(strongest == 6 && bands[6] > .5 && bands[1] < .05,
                    $"Fixed out-of-phase PCM produces the correct frequency bands, repetition {repetition}");
            }
            var report = new
            {
                desktop = BackgroundVerification.CurrentDesktop, passed = true,
                frameCount = FrameCount, repetitions = Repetitions, sampleRate = 48000,
                measurements = results, checks,
                note = "Controlled actual frame-method computation and 2-second PCM workload; no display-FPS claim. Preallocated snapshots, fixed 60Hz input positions, metadata fanout suppressed only during frame measurement. No forced collection. CPU is whole process; allocations include existing fixture reset costs. Real rendering/layout and interaction behavior are covered by the full private suite."
            };
            File.WriteAllText(Path.Combine(folder, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return report;
        }
        finally
        {
            if (handlers is not null) changed.SetValue(media, handlers);
            notch.CloseForShutdown();
            Environment.SetEnvironmentVariable("WINISLAND_DATA_DIR", originalData);
        }
    }

    private static Measurement Measure(string workload, int repetition, int operations, Action work)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var privateBefore = process.PrivateMemorySize64;
        var workingBefore = process.WorkingSet64;
        var managedBefore = GC.GetTotalMemory(false);
        var cpuBefore = process.TotalProcessorTime;
        var threadBefore = GC.GetAllocatedBytesForCurrentThread();
        var totalBefore = GC.GetTotalAllocatedBytes();
        var started = Stopwatch.GetTimestamp();
        work();
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var threadAllocated = GC.GetAllocatedBytesForCurrentThread() - threadBefore;
        var totalAllocated = GC.GetTotalAllocatedBytes() - totalBefore;
        var cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var managedAfter = GC.GetTotalMemory(false);
        process.Refresh();
        return new Measurement(workload, repetition, operations, elapsed, cpu, threadAllocated, totalAllocated,
            managedBefore, managedAfter, privateBefore, process.PrivateMemorySize64, workingBefore, process.WorkingSet64);
    }
}
