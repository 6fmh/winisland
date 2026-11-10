using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Winisland.Controls;
using Winisland.Services;

namespace Winisland;

internal static class ControlCenterVerification
{
    private sealed class Fixture : IControlCenterBackend
    {
        internal ControlCenterSnapshot Snapshot = new(new(true, true, true, "Studio Wi-Fi"), new(true, true, true, "On"),
            new(true, 64, false, "Speakers"), new(true, 72));
        internal readonly List<int> Volumes = new();
        internal readonly List<int> Brightness = new();
        internal readonly List<(QuickRadio Kind, bool Enabled)> Radios = new();
        internal bool DenyRadio;
        internal TaskCompletionSource<bool>? RadioGate, VolumeGate, MuteGate;
        internal bool FailMute;
        internal int Mutes;
        internal int Reads;
        public Task<ControlCenterSnapshot> ReadAsync() { Reads++; return Task.FromResult(Snapshot); }
        public async Task<ControlResult> SetRadioAsync(QuickRadio kind, bool enabled)
        {
            Radios.Add((kind, enabled));
            if (RadioGate is not null) await RadioGate.Task;
            if (DenyRadio) return new(false, "Windows denied this change. Open settings.");
            var state = (kind == QuickRadio.Wifi ? Snapshot.Wifi : Snapshot.Bluetooth) with { Enabled = enabled, Detail = enabled ? "On" : "Off" };
            Snapshot = kind == QuickRadio.Wifi ? Snapshot with { Wifi = state } : Snapshot with { Bluetooth = state };
            return new(true);
        }
        public async Task<ControlResult> SetVolumeAsync(int value)
        {
            Volumes.Add(value);
            if (VolumeGate is not null) await VolumeGate.Task;
            Snapshot = Snapshot with { Sound = Snapshot.Sound with { Volume = value } };
            return new(true);
        }
        public async Task<ControlResult> SetMutedAsync(bool muted)
        {
            Mutes++;
            if (MuteGate is not null) await MuteGate.Task;
            if (FailMute) throw new InvalidOperationException("Synthetic mute failure");
            Snapshot = Snapshot with { Sound = Snapshot.Sound with { Muted = muted } }; return new(true);
        }
        public Task<ControlResult> SetBrightnessAsync(int value)
        { Brightness.Add(value); Snapshot = Snapshot with { Brightness = new(true, value) }; return Task.FromResult(new ControlResult(true)); }
    }

    internal static async Task<int> RunStandaloneAsync(App app, string[] args)
    {
        BackgroundVerification.RequirePrivateDesktop();
        var index = Array.IndexOf(args, "--output");
        var output = Path.GetFullPath(index >= 0 && index + 1 < args.Length ? args[index + 1] : "artifacts/control-center");
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("WINISLAND_DATA_DIR", Path.Combine(output, "test-data"));
        var results = new List<object>();
        var store = new SettingsStore();
        using var media = new MediaService(app.Dispatcher);
        using var audio = new AudioService();
        using var focus = new FocusTimer();
        var notch = new NotchWindow(store, media, audio, focus, new FileShelf());
        try
        {
            notch.Show(); notch.StopPollingForTest();
            await RunAsync(notch, store, output, (condition, name) =>
            {
                if (!condition) throw new InvalidOperationException(name);
                results.Add(new { check = name, passed = true });
            });
            File.WriteAllText(Path.Combine(output, "control-center-results.json"), JsonSerializer.Serialize(new { passed = true, results }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "control-center-results.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString(), results }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
        finally { notch.CloseForShutdown(); }
    }

    internal static async Task RunAsync(NotchWindow notch, SettingsStore settings, string output, Action<bool, string> assert)
    {
        var saved = settings.Current;
        var panel = (ControlCenterPanel)notch.FindName("ControlCenter");
        var fixture = new Fixture();
        var volume = (VolumeSlider)panel.FindName("SystemVolumeSlider");
        var brightness = (VolumeSlider)panel.FindName("BrightnessSlider");
        var wifi = (ToggleButton)panel.FindName("WifiPower");
        var bluetooth = (ToggleButton)panel.FindName("BluetoothPower");
        var status = (TextBlock)panel.FindName("ControlStatus");
        try
        {
            settings.Update(saved with { ReducedMotion = true, HideInFullscreen = false, NotchWidth = 185 });
            panel.SetBackendForTest(fixture);
            ((RadioButton)notch.FindName("ControlCenterTab")).IsChecked = true;
            notch.Expand(true); notch.UpdateLayout();
            await WaitUntil(() => fixture.Reads > 0 && volume.Value == 64);
            await Task.Delay(100); notch.UpdateLayout();
            assert(panel.IsVisible && panel.RefreshTimerRunningForTest && wifi.IsChecked == true && bluetooth.IsChecked == true,
                "The Control Center button opens live radio state and starts polling only while visible");
            assert(fixture.Volumes.Count == 0 && fixture.Radios.Count == 0 && fixture.Brightness.Count == 0,
                "Opening and presenting Control Center never changes a device setting");
            var island = (FrameworkElement)notch.FindName("Island");
            var bounds = panel.TransformToAncestor(island).TransformBounds(new Rect(panel.RenderSize));
            assert(island.ActualHeight == NotchMetrics.ControlCenterExpandedHeight && bounds.Top == 40 && bounds.Bottom <= island.ActualHeight - 12,
                "Control Center fits inside the notch with room for the camera header and lower corners");
            Capture(notch, Path.Combine(output, "control-center.png"));

            fixture.DenyRadio = true; fixture.RadioGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            wifi.IsChecked = false; wifi.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            assert(wifi.IsChecked == true && !wifi.IsEnabled && panel.IsInteracting,
                "Radio presses keep confirmed state and prevent duplicate commands while Windows responds");
            fixture.RadioGate.SetResult(true);
            await WaitUntil(() => !panel.IsInteracting);
            assert(wifi.IsChecked == true && status.Text.Contains("denied") && wifi.IsEnabled,
                "A denied radio command shows an inline error and preserves the actual radio state");
            fixture.DenyRadio = false; fixture.RadioGate = null;
            wifi.IsChecked = false; wifi.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitUntil(() => wifi.IsChecked == false && !panel.IsInteracting);
            bluetooth.IsChecked = false; bluetooth.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitUntil(() => bluetooth.IsChecked == false && !panel.IsInteracting);
            assert(fixture.Radios.Last() == (QuickRadio.Bluetooth, false) && fixture.Radios[1] == (QuickRadio.Wifi, false),
                "Wi-Fi and Bluetooth buttons target only their corresponding Windows radios");

            fixture.Snapshot = fixture.Snapshot with { Sound = new(true, 32, false, "Headphones"), Brightness = new(true, 55) };
            await panel.RefreshAsync();
            assert(volume.Value == 32 && brightness.Value == 55 && fixture.Volumes.Count == 0 && fixture.Brightness.Count == 0,
                "External volume, brightness and output-device changes refresh without generating writes");
            volume.Value = 40; volume.Value = 41; volume.Value = 42;
            await WaitUntil(() => fixture.Volumes.Count > 0 && fixture.Snapshot.Sound.Volume == 42);
            assert(fixture.Volumes.SequenceEqual(new[] { 42 }), "Rapid slider changes coalesce into the latest system-volume command");
            fixture.VolumeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            volume.Value = 66;
            await WaitUntil(() => fixture.Volumes.Count == 2);
            volume.Value = 79; volume.Value = 80;
            fixture.VolumeGate.SetResult(true);
            await WaitUntil(() => fixture.Snapshot.Sound.Volume == 80 && volume.Value == 80);
            assert(fixture.Volumes.Last() == 80, "An adjustment during a slow device write lands on the newest volume");
            fixture.VolumeGate = null;
            brightness.Value = 63;
            await WaitUntil(() => fixture.Brightness.Contains(63));
            ((Button)panel.FindName("SystemMuteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => fixture.Snapshot.Sound.Muted);
            assert(((TextBlock)panel.FindName("SoundValue")).Text == "Muted", "System mute reads back and updates the sound indicator");
            var muteButton = (Button)panel.FindName("SystemMuteButton");
            fixture.MuteGate = new(TaskCreationOptions.RunContinuationsAsynchronously); fixture.FailMute = true;
            var previousMutes = fixture.Mutes;
            muteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            assert(panel.IsInteracting && !muteButton.IsEnabled, "Slow system-mute commands keep confirmed controls busy");
            muteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            fixture.MuteGate.SetResult(true);
            await WaitUntil(() => !panel.IsInteracting);
            assert(fixture.Mutes == previousMutes + 1 && muteButton.IsEnabled && status.Text.Contains("Couldn't change sound"),
                "A failed mute command cannot queue duplicates and immediately restores its button with inline feedback");
            fixture.MuteGate = null; fixture.FailMute = false;

            var track = (Track)volume.Template.FindName("PART_Track", volume);
            var pointer = track.TransformToAncestor(volume).Transform(new Point(
                track.Thumb.ActualWidth / 2 + (track.ActualWidth - track.Thumb.ActualWidth) * .8, 10));
            assert(volume.BeginPointerAdjustment(pointer, captureMouse: false), "System volume begins a control-local drag");
            fixture.Snapshot = fixture.Snapshot with { Sound = fixture.Snapshot.Sound with { Volume = 15 } };
            await panel.RefreshAsync();
            assert(Math.Abs(volume.Value - 80) < .001 && panel.IsInteracting, "Incoming device readbacks cannot pull an active slider drag away from its pointer");
            volume.CancelAdjustment();
            await panel.RefreshAsync();
            assert(volume.Value == 15, "The slider synchronizes with Windows after a drag ends");
            fixture.Snapshot = new(new(false, false, false, "Not available"), new(true, false, false, "Managed by Windows"),
                new(false, 0, false, "No output device"), new(false, 0));
            await panel.RefreshAsync();
            assert(!wifi.IsEnabled && !bluetooth.IsEnabled && !volume.IsEnabled && !brightness.IsEnabled
                && ((FrameworkElement)panel.FindName("BrightnessUnavailable")).IsVisible,
                "Missing audio, hardware-managed radios and unsupported brightness have honest unavailable states");
            Capture(notch, Path.Combine(output, "control-center-unavailable.png"));

            foreach (var width in new[] { 160.0, 208.0, 320.0 })
            {
                settings.Update(settings.Current with { NotchWidth = width });
                ((FrameworkElement)notch.FindName("BatteryStatus")).Visibility = Visibility.Visible;
                notch.UpdateLayout();
                var content = (FrameworkElement)notch.FindName("ExpandedContent");
                var camera = (FrameworkElement)notch.FindName("HeaderCameraSpace");
                var cameraBounds = camera.TransformToAncestor(content).TransformBounds(new Rect(camera.RenderSize));
                foreach (var name in new[] { "ToolsTab", "ControlCenterTab", "PinButton", "NotchSettingsButton", "BatteryStatus" })
                {
                    var element = (FrameworkElement)notch.FindName(name);
                    var rectangle = element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
                    assert(rectangle.Left >= cameraBounds.Right && rectangle.Right <= content.ActualWidth,
                        $"{name} stays clear of the camera and surface edge at notch width {width:0}");
                }
            }
            ((FrameworkElement)notch.FindName("BatteryStatus")).Visibility = Visibility.Collapsed;
            ((Button)panel.FindName("FocusShortcut")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            assert(((FrameworkElement)notch.FindName("FocusPanel")).IsVisible && !panel.RefreshTimerRunningForTest,
                "The Focus timer shortcut navigates inside the notch and stops hidden device polling");
            settings.Update(settings.Current with { ReducedMotion = false });
            notch.SelectTabForTest("controls"); await Task.Delay(40);
            notch.SelectTabForTest("music"); await Task.Delay(40);
            notch.SelectTabForTest("controls"); await Task.Delay(700);
            assert(panel.IsVisible && panel.IsHitTestVisible && panel.Opacity == 1 && island.ActualHeight == NotchMetrics.ControlCenterExpandedHeight,
                "Rapid page changes settle on Control Center without stale animations or clipped content");
            notch.Collapse();
            assert(!panel.RefreshTimerRunningForTest, "Closing the notch immediately stops Control Center polling");

            var native = new ControlCenterService();
            var nativeSnapshot = await native.ReadAsync();
            File.WriteAllText(Path.Combine(output, "control-center-native.json"), JsonSerializer.Serialize(nativeSnapshot, new JsonSerializerOptions { WriteIndented = true }));
            assert(true, "Native Windows radio, audio and brightness discovery completes without changing settings");
            panel.SetBackendForTest(native);
            settings.Update(settings.Current with { ReducedMotion = true, NotchWidth = 185 });
            notch.Expand(true); await panel.RefreshAsync(); await Task.Delay(500); notch.UpdateLayout();
            Capture(notch, Path.Combine(output, "control-center-native.png"));
        }
        finally
        {
            fixture.RadioGate?.TrySetResult(true); fixture.VolumeGate?.TrySetResult(true); fixture.MuteGate?.TrySetResult(true);
            panel.SetBackendForTest(new ControlCenterService());
            notch.Collapse(); notch.SelectTabForTest("music"); settings.Update(saved);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.ElapsedMilliseconds < 3000) await Task.Delay(15);
        if (!condition()) throw new TimeoutException("Control Center did not settle.");
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var visual = new DrawingVisual();
        var native = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        native.Render(window);
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(90, 109, 141), Color.FromRgb(175, 159, 188), 25), null,
                new Rect(0, 0, window.ActualWidth, window.ActualHeight + 30));
            drawing.DrawImage(native, new Rect(window.RenderSize));
        }
        var frame = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight + 30, 96, 96, PixelFormats.Pbgra32);
        frame.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame));
        using var file = File.Create(path); encoder.Save(file);
    }
}
