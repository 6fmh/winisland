using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Winisland.Controls;
using Winisland.Services;

namespace Winisland;

internal static class PlaybackVerification
{
    internal static async Task RunAsync(NotchWindow notch, MediaService media, SettingsStore settings, Action<bool, string> assert, Action<string> capture)
    {
        var savedSettings = settings.Current;
        var commands = new List<double>();
        var pointer = new Point();
        var fixture = new MediaSnapshot
        {
            HasSession = true, CanSeek = true, Title = "Playback verification",
            Duration = TimeSpan.FromMinutes(4), Position = TimeSpan.FromSeconds(40)
        };
        var slider = (ScrubSlider)notch.FindName("SeekSlider");
        var elapsed = (TextBlock)notch.FindName("ElapsedLabel");
        media.SeekHandlerForTest = position => { commands.Add(position); return Task.FromResult(true); };
        try
        {
            settings.Update(savedSettings with { ReducedMotion = false });
            media.SetSnapshotForTest(fixture);
            assert(!await media.SeekAsync(double.NaN) && !await media.SeekAsync(double.PositiveInfinity) && commands.Count == 0,
                "Invalid playback positions are rejected without issuing player commands");
            notch.SelectTabForTest("music");
            notch.Expand(true);
            notch.UpdateLayout();
            ((Button)notch.FindName("PinButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(80);
            notch.UpdateLayout();
            MovePointer(RailX(0.22), 10);
            slider.BeginPointerAdjustment(pointer, captureMouse: false);
            await Task.Delay(60);
            if (!slider.IsAdjusting || !slider.HasPointerCapture || slider.Value is < 45 or > 60)
                throw new InvalidOperationException($"Playback pointer input failed: captured={slider.HasPointerCapture}, adjusting={slider.IsAdjusting}, value={slider.Value}, position={Mouse.GetPosition(slider)}, size={slider.ActualWidth}×{slider.ActualHeight}");
            assert(slider.IsAdjusting && slider.HasPointerCapture && slider.Value is >= 45 and <= 60,
                "Playback slider starts scrubbing anywhere in its larger hit area");
            var preview = slider.Value;
            var previewLabel = elapsed.Text;
            assert(previewLabel != "0:40" && commands.Count == 0,
                "Scrubbing previews the selected timestamp without issuing playback commands");
            media.SetSnapshotForTest(fixture with { Position = TimeSpan.FromSeconds(8) });
            assert(Math.Abs(slider.Value - preview) < 0.01 && elapsed.Text == previewLabel,
                "Player timeline updates do not move the handle during a scrub");
            for (var x = RailX(0.22) + 1; x <= RailX(0.73); x++) { MovePointer(x, 10); await Task.Delay(2); }
            MovePointer(RailX(0.73), 10);
            var pointerResponse = Stopwatch.StartNew();
            while (slider.IsAdjusting && slider.HasPointerCapture && commands.Count == 0
                && (slider.Value is < 170 or > 180) && pointerResponse.ElapsedMilliseconds < 200)
                await Task.Delay(8);
            if (slider.Value is < 170 or > 180 || commands.Count != 0)
            {
                throw new InvalidOperationException($"Slow playback scrubbing follows the pointer continuously: "
                    + $"value={slider.Value}, commands={commands.Count}, adjusting={slider.IsAdjusting}, "
                    + $"captured={slider.HasPointerCapture}, button={Mouse.LeftButton}, "
                    + $"localPointer={pointer.X},{pointer.Y}, "
                    + $"size={slider.ActualWidth}×{slider.ActualHeight}, wait={pointerResponse.ElapsedMilliseconds}ms");
            }
            assert(slider.Value is >= 170 and <= 180 && commands.Count == 0,
                "Slow playback scrubbing follows the pointer continuously");
            capture("notch-playback-scrubbing.png");
            MovePointer(slider.ActualWidth + 80, slider.ActualHeight + 100);
            await Task.Delay(300);
            assert(slider.Value == 240 && slider.IsAdjusting && notch.IsExpanded && commands.Count == 0,
                "Scrubbing outside an unpinned notch keeps it open and clamps to the duration");
            slider.EndPointerAdjustment(pointer);
            await Task.Delay(60);
            assert(!slider.IsAdjusting && !slider.HasPointerCapture && commands.Count == 1 && commands[0] == 240,
                "Releasing the scrub sends exactly one seek to the selected time");
            assert(slider.Value == 240 && elapsed.Text == "4:00",
                "An acknowledged seek holds its selected time until the player catches up");
            notch.Expand(true);
            media.SetSnapshotForTest(fixture with { Position = TimeSpan.FromSeconds(239) });
            assert(slider.Value == 239, "Confirmed seeks resume following the player timeline");

            slider.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.MouseWheelEvent });
            assert(slider.Value == 234 && elapsed.Text == "3:54", "Playback wheel adjustment previews a five-second step");
            media.SetSnapshotForTest(fixture with { Position = TimeSpan.FromSeconds(90) });
            assert(slider.Value == 234, "Pending wheel and keyboard adjustments survive incoming timeline updates");
            await Task.Delay(320);
            assert(commands.Count == 2 && commands[1] == 234, "Wheel adjustment commits after the short input debounce");

            media.SeekHandlerForTest = position => { commands.Add(position); return Task.FromResult(false); };
            slider.Value = 200;
            await Task.Delay(320);
            assert(slider.Value == 90 && elapsed.Text == "1:30", "A rejected seek restores the actual playback position");
            media.SeekHandlerForTest = position => { commands.Add(position); return Task.FromResult(true); };
            media.SetSnapshotForTest(fixture with { Position = TimeSpan.FromSeconds(60) });
            var beforeCancel = commands.Count;
            MovePointer(RailX(0.5), 10);
            await Task.Delay(60);
            slider.BeginPointerAdjustment(pointer, captureMouse: false);
            await Task.Delay(60);
            notch.Collapse();
            assert(!slider.IsAdjusting && !slider.HasPointerCapture && commands.Count == beforeCancel && slider.Value == 60,
                "Closing the panel cancels an uncommitted scrub and releases capture");
            slider.EndPointerAdjustment(pointer);

            notch.Expand(true);
            slider.Value = 180;
            notch.Collapse();
            await Task.Delay(320);
            assert(commands.Count == beforeCancel && slider.Value == 60,
                "Closing the panel cancels a debounced keyboard seek and restores the actual time");

            notch.Expand(true);
            slider.Value = 180;
            notch.SelectTabForTest("files");
            await Task.Delay(320);
            assert(commands.Count == beforeCancel && slider.Value == 60,
                "Leaving music cancels a pending keyboard seek rather than seeking from a hidden page");
            notch.SelectTabForTest("music");
            notch.UpdateLayout();
            slider.BeginPointerAdjustment(new Point(RailX(.5), 10), captureMouse: false);
            notch.SelectTabForTest("focus");
            assert(!slider.IsAdjusting && !slider.HasPointerCapture && slider.Value == 60 && commands.Count == beforeCancel,
                "Leaving music cancels an active playback drag and releases its capture");

            notch.SelectTabForTest("music");
            media.SetSnapshotForTest(fixture with { IsPlaying = true });
            assert(notch.PlaybackRenderingForTest == SystemParameters.ClientAreaAnimation,
                "Visible playing music follows the Windows animation preference");
            notch.SelectTabForTest("tools");
            assert(!notch.PlaybackRenderingForTest,
                "Leaving music stops its playback rendering subscription immediately");
            notch.SelectTabForTest("music");
            assert(notch.PlaybackRenderingForTest == SystemParameters.ClientAreaAnimation,
                "Returning to playing music resumes playback rendering");

            media.SetSnapshotForTest(fixture with { Artist = "Original artist", Position = TimeSpan.FromSeconds(60) });
            slider.Value = 180;
            media.SetSnapshotForTest(fixture with { Artist = "Different artist", Position = TimeSpan.FromSeconds(20) });
            await Task.Delay(320);
            assert(commands.Count == beforeCancel && slider.Value == 20,
                "Tracks with the same title from different artists cancel the prior track's pending seek");

            notch.Collapse();
            notch.Expand(false);
            ((FrameworkElement)notch.FindName("Island")).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,
                Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            assert(notch.IsPinnedForTest,
                "Clicking a notch that already expanded on hover pins it open");

            media.SetSnapshotForTest(fixture with { Position = TimeSpan.FromHours(25), Duration = TimeSpan.FromHours(26) });
            assert(elapsed.Text == "25:00:00" && ((TextBlock)notch.FindName("DurationLabel")).Text == "26:00:00",
                "Playback timestamps retain total hours for media longer than a day");

            media.SetSnapshotForTest(fixture with { CanSeek = false });
            assert(!slider.IsEnabled, "Players without seeking support keep playback adjustments disabled");
        }
        finally
        {
            slider.CancelAdjustment();
            slider.EndPointerAdjustment(pointer);
            media.SeekHandlerForTest = null;
            media.SetSnapshotForTest(new MediaSnapshot());
            notch.Collapse();
            settings.Update(savedSettings);
        }

        void MovePointer(double x, double y)
        {
            pointer = new Point(x, y);
            slider.MovePointerAdjustment(pointer);
        }

        double RailX(double fraction)
        {
            var track = (Track)slider.Template.FindName("PART_Track", slider);
            var halfThumb = track.Thumb.ActualWidth / 2;
            var point = track.TransformToAncestor(slider).Transform(new Point(
                halfThumb + (track.ActualWidth - 2 * halfThumb) * fraction, 0));
            return point.X;
        }
    }

}
